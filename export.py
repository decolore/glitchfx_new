"""Export the processed video using FFmpeg via subprocess.

The export is a two-stage pipeline:

1. Render the BASE CYCLE once. The source video is time-remapped FIRST (sped up,
   slowed down with frame blending, or trimmed) to fit a whole number of bars at
   the project BPM, then the effects are applied. The result is encoded to a
   temporary ``base.mp4``.
2. The encoded base cycle is looped N times with ``-stream_loop`` (so effects are
   only ever rendered once -- the repeats are a cheap stream copy) and the global
   master audio track is muxed in. The audio plays once, padded with silence, and
   is capped to the full output length -- only the video repeats.
"""
from __future__ import annotations

import os
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
from pathlib import Path
from typing import Callable, List, Optional, Tuple

import cv2
import numpy as np

from audio import (
    audio_duration,
    compute_audio_envelope,
    compute_reaction_envelope,
    envelope_to_gain,
)
from effects import apply_pipeline, apply_audio_reaction, build_pipeline
from settings import (
    ProjectSettings,
    Transform,
    transform_matrix,
    text_rotation_bpm,
    compute_sync_stats,
)


_FFMPEG_BIN: Optional[str] = None
_FFPROBE_BIN: Optional[str] = None


def _find_bin(name: str) -> str:
    """Locate an executable, searching PATH and common macOS Homebrew paths."""
    found = shutil.which(name)
    if found:
        return found
    for candidate in (
        f"/opt/homebrew/bin/{name}",
        f"/usr/local/bin/{name}",
        f"/opt/local/bin/{name}",
        f"/usr/bin/{name}",
    ):
        if Path(candidate).exists():
            return candidate
    raise FileNotFoundError(
        f"'{name}' not found. Install it and ensure it is on PATH "
        "(e.g. /opt/homebrew/bin for Apple Silicon Homebrew)."
    )


def _ffmpeg() -> str:
    global _FFMPEG_BIN
    if _FFMPEG_BIN is None:
        _FFMPEG_BIN = _find_bin("ffmpeg")
    return _FFMPEG_BIN


def _ffprobe() -> str:
    global _FFPROBE_BIN
    if _FFPROBE_BIN is None:
        _FFPROBE_BIN = _find_bin("ffprobe")
    return _FFPROBE_BIN


def _get_audio_stream_count(path: str) -> int:
    try:
        result = subprocess.run(
            [_ffprobe(), "-v", "error", "-select_streams", "a",
             "-show_entries", "stream=index", "-of", "csv=p=0", path],
            capture_output=True, text=True, check=False, timeout=15,
        )
        return len([line for line in result.stdout.strip().split("\n") if line.strip()])
    except Exception:
        return 0


def _compute_output_frame(
    frame: np.ndarray,
    transform: Transform,
    out_w: int,
    out_h: int,
    preview: bool = False,
) -> np.ndarray:
    """Map one source frame to the final output size using the transform matrix.

    Handles cover, contain (with black bars) and stretch via a single affine warp.
    Uses faster interpolation for the preview viewport.
    """
    src_h, src_w = frame.shape[:2]
    if src_w <= 0 or src_h <= 0 or out_w <= 0 or out_h <= 0:
        return np.zeros((out_h, out_w, 3), dtype=np.uint8)

    M = transform_matrix(src_w, src_h, out_w, out_h, transform)
    interp = cv2.INTER_LINEAR if preview else cv2.INTER_LANCZOS4
    return cv2.warpAffine(
        frame,
        M,
        (out_w, out_h),
        flags=interp,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=(0, 0, 0),
    )


def _compute_output_frame_inplace(
    frame: np.ndarray,
    transform: Transform,
    out_w: int,
    out_h: int,
    dst: np.ndarray,
    preview: bool = False,
) -> None:
    """Same as _compute_output_frame but writes into the pre-allocated dst buffer."""
    src_h, src_w = frame.shape[:2]
    if src_w <= 0 or src_h <= 0 or out_w <= 0 or out_h <= 0:
        dst.fill(0)
        return

    M = transform_matrix(src_w, src_h, out_w, out_h, transform)
    interp = cv2.INTER_LINEAR if preview else cv2.INTER_LANCZOS4
    cv2.warpAffine(
        frame,
        M,
        (out_w, out_h),
        dst=dst,
        flags=interp,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=(0, 0, 0),
    )


def _build_remap_positions(
    base_frames: int, total_frames: int, speed_factor: float
) -> List[float]:
    """Source frame position (float) for each output frame of the base cycle.

    ``speed_factor`` > 1 speeds the video up (positions step by >1, dropping
    frames); < 1 slows it down (positions step by <1, so neighbouring output
    frames land between two source frames and get blended). The sequence is
    monotonically non-decreasing, which lets the reader stream the source
    sequentially with a tiny look-ahead cache instead of random seeking.
    """
    max_pos = float(max(0, total_frames - 1))
    positions: List[float] = []
    for j in range(base_frames):
        p = j * speed_factor
        if p > max_pos:
            p = max_pos
        positions.append(p)
    return positions


def _render_base_cycle(
    settings: ProjectSettings,
    cap: "cv2.VideoCapture",
    fps: float,
    out_w: int,
    out_h: int,
    base_frames: int,
    positions: List[float],
    interpolate: bool,
    audio_envelope: Optional[np.ndarray],
    codec: str,
    crf: int,
    preset: str,
    max_bitrate: str,
    base_path: str,
    progress_callback: Optional[Callable[[int, int], bool]],
) -> None:
    """Render the single time-remapped + effected base cycle to ``base_path``
    (video only)."""
    cmd = [
        _ffmpeg(),
        "-y",
        "-f", "rawvideo",
        "-vcodec", "rawvideo",
        "-s", f"{out_w}x{out_h}",
        "-r", str(fps),
        "-pix_fmt", "bgr24",
        "-i", "-",
        "-an",
        "-c:v", codec,
        "-pix_fmt", "yuv420p",
    ]
    if "videotoolbox" in codec:
        quality = max(1, min(100, int((51 - crf) / 51.0 * 100)))
        cmd.extend(["-q:v", str(quality), "-allow_sw", "1"])
        if max_bitrate:
            cmd.extend(["-b:v", max_bitrate])
    else:
        cmd.extend(["-crf", str(crf), "-preset", preset])
        if max_bitrate:
            cmd.extend(["-maxrate", max_bitrate, "-bufsize", max_bitrate])
    cmd.extend(["-movflags", "+faststart", base_path])

    with tempfile.TemporaryFile() as stderr_file:
        proc = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE,
            stdout=subprocess.DEVNULL,
            stderr=stderr_file,
        )

        def _make_pipeline():
            return build_pipeline(
                settings.effects,
                animate_params=settings.animate_params,
                animation_amount=settings.animation_amount,
                master_seed=settings.master_seed,
                bpm=getattr(settings, "bpm", 120.0),
            )

        # Stateful effects (motion trails, datamoshing, optical-flow glitch)
        # must see frames strictly in order, so they run on a single worker.
        probe_pipeline = _make_pipeline()
        has_stateful = any(getattr(e, "stateful", False) for e in probe_pipeline)
        if has_stateful:
            num_workers = 1
        else:
            num_workers = max(1, min(4, os.cpu_count() or 2))

        raw_queue: queue.Queue = queue.Queue(maxsize=max(8, num_workers * 2))
        done_queue: queue.Queue = queue.Queue(maxsize=max(8, num_workers * 2))
        cancel_event = threading.Event()
        error_queue: queue.Queue = queue.Queue(maxsize=1)

        worker_pipelines = [probe_pipeline] + [
            _make_pipeline() for _ in range(num_workers - 1)
        ]
        out_buffers = [
            np.empty((out_h, out_w, 3), dtype=np.uint8)
            for _ in range(num_workers)
        ]

        def reader():
            # Stream the source sequentially, keeping a tiny cache of the most
            # recently decoded frames. ``positions`` is monotonic so we only
            # ever read forward. For each output frame we either blend the two
            # bracketing source frames (slow-down interpolation) or pick the
            # nearest one.
            try:
                cache: dict = {}
                last_read = -1

                def ensure(target: int) -> bool:
                    nonlocal last_read
                    while last_read < target:
                        ok, fr = cap.read()
                        if not ok:
                            return False
                        last_read += 1
                        cache[last_read] = fr
                        drop = last_read - 3
                        if drop in cache:
                            del cache[drop]
                    return True

                for j in range(base_frames):
                    if cancel_event.is_set():
                        break
                    p = positions[j]
                    f0 = int(np.floor(p))
                    frac = float(p - f0)
                    ensure(f0 + 1)
                    a = cache.get(f0)
                    if a is None:
                        a = cache.get(last_read)
                    if a is None:
                        break
                    if interpolate and frac > 1e-3:
                        b = cache.get(f0 + 1)
                        if b is None:
                            b = a
                        frame = cv2.addWeighted(a, 1.0 - frac, b, frac, 0.0)
                    else:
                        nb = int(round(p))
                        nf = cache.get(nb)
                        frame = nf if nf is not None else a
                    raw_queue.put((j, frame), block=True)
            except Exception as e:  # noqa: BLE001
                error_queue.put(e)
            finally:
                cap.release()
                for _ in range(num_workers):
                    raw_queue.put((None, None), block=True)

        def worker(worker_id: int):
            out_buf = out_buffers[worker_id]
            wpipeline = worker_pipelines[worker_id]
            try:
                while True:
                    idx, frame = raw_queue.get(block=True)
                    if idx is None or cancel_event.is_set():
                        raw_queue.task_done()
                        done_queue.put((None, None), block=True)
                        break
                    time = idx / fps if fps > 0 else 0.0
                    reactive = (
                        audio_envelope is not None
                        and 0 <= idx < len(audio_envelope)
                    )
                    mode_r = getattr(settings, "reaction_mode", "opacity")
                    k = float(getattr(settings, "audio_intensity", 60)) / 100.0
                    v = float(audio_envelope[idx]) if reactive else 0.0
                    _compute_output_frame_inplace(
                        frame, settings.transform, out_w, out_h, out_buf, preview=False
                    )
                    if reactive and mode_r != "strength":
                        clean = out_buf.copy()
                        fx = apply_pipeline(out_buf, wpipeline, time, 1.0)
                        out_frame = apply_audio_reaction(clean, fx, v, k, mode_r)
                    else:
                        gain = envelope_to_gain(v, k) if reactive else 1.0
                        out_frame = apply_pipeline(out_buf, wpipeline, time, gain)
                    if out_frame is out_buf:
                        out_frame = out_buf.copy()
                    raw_queue.task_done()
                    done_queue.put((idx, out_frame), block=True)
            except Exception as e:  # noqa: BLE001
                error_queue.put(e)
                cancel_event.set()

        reader_thread = threading.Thread(target=reader, name="export-reader")
        reader_thread.start()
        worker_threads = [
            threading.Thread(target=worker, args=(i,), name=f"export-worker-{i}")
            for i in range(num_workers)
        ]
        for t in worker_threads:
            t.start()

        written = 0
        pending: dict = {}
        workers_finished = 0
        try:
            while written < base_frames:
                try:
                    err = error_queue.get(block=False)
                    raise err
                except queue.Empty:
                    pass

                if cancel_event.is_set():
                    break

                idx, out_frame = done_queue.get(block=True)

                if idx is None:
                    workers_finished += 1
                    if workers_finished >= num_workers:
                        break
                    continue

                pending[idx] = out_frame
                while written in pending:
                    out_frame = pending.pop(written)
                    try:
                        proc.stdin.write(memoryview(out_frame))
                    except BrokenPipeError:
                        cancel_event.set()
                        break
                    written += 1
                    if progress_callback and written % 5 == 0:
                        if not progress_callback(written, base_frames):
                            cancel_event.set()
                            break
                if cancel_event.is_set():
                    break
        finally:
            cancel_event.set()
            reader_thread.join(timeout=2.0)
            for t in worker_threads:
                t.join(timeout=2.0)
            if proc.stdin:
                proc.stdin.close()
            proc.wait()

        if proc.returncode != 0:
            stderr_file.seek(0)
            err = stderr_file.read().decode("utf-8", errors="ignore")[-1000:]
            raise RuntimeError(
                f"FFmpeg base render failed (code {proc.returncode}): {err}"
            )


def _atempo_chain(factor: float) -> List[str]:
    """ffmpeg ``atempo`` filter terms to change audio tempo by ``factor`` so the
    original-video soundtrack follows a video speed-up/slow-down. ``atempo`` is
    only reliable in 0.5..2.0, so factors outside that range are split into a
    chain of stages whose product equals ``factor``."""
    try:
        f = float(factor)
    except (TypeError, ValueError):
        f = 1.0
    if f <= 0:
        f = 1.0
    if abs(f - 1.0) < 1e-3:
        return []
    terms: List[str] = []
    remaining = f
    while remaining > 2.0:
        terms.append("atempo=2.0")
        remaining /= 2.0
    while remaining < 0.5:
        terms.append("atempo=0.5")
        remaining *= 2.0
    terms.append(f"atempo={remaining:.6f}")
    return terms


def _assemble_output(
    base_path: str,
    output_path: str,
    repeats: int,
    cycle_duration: float,
    audio_path: Optional[str],
    og_audio_source: Optional[str] = None,
    og_speed: float = 1.0,
) -> None:
    """Loop the encoded base cycle ``repeats`` times (stream copy -- no
    re-render) and add audio.

    Three cases:
      * ``audio_path`` (custom master track): the video loops, the audio plays
        ONCE, padded with silence, capped at ``repeats * cycle_duration``.
      * ``og_audio_source`` (no custom track but the source video HAS audio):
        the original soundtrack is time-remapped to match the video
        speed/trim, trimmed to one cycle, then looped together WITH the video
        so each repeat carries its own audio.
      * neither: silent output.
    """
    total_dur = max(0.0, cycle_duration * max(1, repeats))

    if audio_path:
        cmd = [_ffmpeg(), "-y"]
        if repeats > 1:
            cmd.extend(["-stream_loop", str(repeats - 1)])
        cmd.extend(["-i", base_path, "-i", audio_path])
        cmd.extend([
            "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "copy",
            "-af", "apad",
            "-c:a", "aac", "-b:a", "192k",
            "-t", f"{total_dur:.6f}",
            "-movflags", "+faststart",
            output_path,
        ])
        result = subprocess.run(cmd, capture_output=True, text=True, check=False)
        if result.returncode != 0:
            raise RuntimeError(
                f"FFmpeg assemble failed (code {result.returncode}): "
                f"{result.stderr[-1000:]}"
            )
        return

    if og_audio_source:
        # Build one cycle of remapped original audio muxed onto the base video,
        # then loop the whole AV cycle so the original sound repeats with it.
        work_dir = os.path.dirname(base_path) or "."
        cycle_av = os.path.join(work_dir, "cycle_av.mp4")
        afilters = _atempo_chain(og_speed)
        afilters.append(f"atrim=0:{max(0.001, cycle_duration):.6f}")
        afilters.append("asetpts=PTS-STARTPTS")
        af = ",".join(afilters)
        cmd_a = [
            _ffmpeg(), "-y",
            "-i", base_path,
            "-i", og_audio_source,
            "-filter:a", af,
            "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "copy",
            "-c:a", "aac", "-b:a", "192k",
            "-t", f"{max(0.001, cycle_duration):.6f}",
            "-movflags", "+faststart",
            cycle_av,
        ]
        result = subprocess.run(cmd_a, capture_output=True, text=True, check=False)
        if result.returncode != 0:
            raise RuntimeError(
                f"FFmpeg original-audio cycle failed (code {result.returncode}): "
                f"{result.stderr[-1000:]}"
            )
        cmd_b = [_ffmpeg(), "-y"]
        if repeats > 1:
            cmd_b.extend(["-stream_loop", str(repeats - 1)])
        cmd_b.extend([
            "-i", cycle_av,
            "-c", "copy",
            "-t", f"{total_dur:.6f}",
            "-movflags", "+faststart",
            output_path,
        ])
        result = subprocess.run(cmd_b, capture_output=True, text=True, check=False)
        if result.returncode != 0:
            raise RuntimeError(
                f"FFmpeg assemble (original audio) failed (code {result.returncode}): "
                f"{result.stderr[-1000:]}"
            )
        return

    cmd = [_ffmpeg(), "-y"]
    if repeats > 1:
        cmd.extend(["-stream_loop", str(repeats - 1)])
    cmd.extend([
        "-i", base_path,
        "-map", "0:v:0", "-an",
        "-c:v", "copy",
        "-t", f"{total_dur:.6f}",
        "-movflags", "+faststart",
        output_path,
    ])
    result = subprocess.run(cmd, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise RuntimeError(
            f"FFmpeg assemble failed (code {result.returncode}): "
            f"{result.stderr[-1000:]}"
        )


def export_video(
    settings: ProjectSettings,
    progress_callback: Optional[Callable[[int, int], bool]] = None,
) -> str:
    """Export project to MP4. Returns output path.

    Pipeline: time-remap (speed/slow/trim to a whole number of bars) -> effects
    -> encode base cycle once -> loop the encoded cycle N times -> mux audio.
    """
    source = settings.source_path
    if not source or not Path(source).exists():
        raise FileNotFoundError("No source video loaded")

    out_w = settings.transform.width
    out_h = settings.transform.height
    codec = settings.export.codec
    crf = settings.export.crf
    preset = settings.export.preset
    max_bitrate = settings.export.max_bitrate.strip()
    output_path = settings.export.output_path
    if not output_path:
        stem = Path(source).stem
        output_path = str(Path.home() / "Desktop" / f"{stem}_fx.mp4")
        settings.export.output_path = output_path

    cap = cv2.VideoCapture(source)
    if not cap.isOpened():
        raise RuntimeError("Cannot open source video")

    fps = cap.get(cv2.CAP_PROP_FPS) or settings.export.fps or 30.0
    total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    if total_frames <= 0:
        total_frames = 1
    src_duration = total_frames / fps if fps > 0 else 0.0

    # Global master audio track: used both for reactivity AND as the soundtrack.
    audio_path = getattr(settings, "audio_path", "") or ""
    have_audio_track = bool(audio_path and Path(audio_path).exists())
    # With no custom track, fall back to the source video's own audio (if any).
    og_available = (not have_audio_track) and (_get_audio_stream_count(source) > 0)
    audio_dur = 0.0
    if have_audio_track:
        try:
            audio_dur = audio_duration(audio_path)
        except Exception:
            audio_dur = 0.0

    # Bar-sync + repeat math (single source of truth, shared with the UI stats).
    stats = compute_sync_stats(settings, src_duration, audio_dur)
    mode = stats["mode"]
    speed_factor = float(stats["speed_factor"])
    cycle_duration = float(stats["cycle_duration"])
    repeats = max(1, int(stats["repeats"]))
    interpolate = bool(stats["interpolate"]) and speed_factor < 0.999

    if cycle_duration <= 0:
        cycle_duration = src_duration if src_duration > 0 else (total_frames / fps)
    base_frames = max(1, int(round(cycle_duration * fps)))
    # Trim / off never need more output frames than the source has.
    if mode != "speed":
        base_frames = min(base_frames, total_frames)

    remap_speed = speed_factor if mode == "speed" else 1.0
    positions = _build_remap_positions(base_frames, total_frames, remap_speed)

    # Audio-reactive envelope computed ONCE over the base cycle only (the
    # repeats reuse the already-rendered, already-reacted frames).
    audio_reactive = bool(getattr(settings, "audio_reactive", False))
    audio_envelope = None
    if audio_reactive and have_audio_track:
        reaction_source = getattr(settings, "reaction_source", "loudness")
        reaction_bpm = text_rotation_bpm(settings)
        audio_envelope = compute_reaction_envelope(
            audio_path, fps, base_frames, reaction_source, reaction_bpm
        )

    # Stage 1: render the base cycle to a temp file.
    tmp_dir = tempfile.mkdtemp(prefix="glitchfx_")
    base_path = os.path.join(tmp_dir, "base.mp4")
    try:
        _render_base_cycle(
            settings, cap, fps, out_w, out_h, base_frames, positions,
            interpolate, audio_envelope, codec, crf, preset, max_bitrate,
            base_path, progress_callback,
        )

        # Stage 2: loop + mux. Cheap stream copy, so effects render only once.
        _assemble_output(
            base_path, output_path, repeats, cycle_duration,
            audio_path if have_audio_track else None,
            og_audio_source=source if og_available else None,
            og_speed=remap_speed,
        )
    finally:
        try:
            cap.release()
        except Exception:
            pass
        shutil.rmtree(tmp_dir, ignore_errors=True)

    return output_path
