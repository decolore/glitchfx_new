"""Audio analysis + helpers for audio-reactive effects.

Decodes an audio file to mono PCM via ffmpeg and computes a per-video-frame
loudness envelope (0..1) that the export pipeline uses to drive effect
intensity. No third-party audio libraries are required.
"""
from __future__ import annotations

import math
import shutil
import subprocess
from pathlib import Path
from typing import List, Optional

import numpy as np


def _ffmpeg_bin() -> Optional[str]:
    found = shutil.which("ffmpeg")
    if found:
        return found
    for candidate in (
        "/opt/homebrew/bin/ffmpeg",
        "/usr/local/bin/ffmpeg",
        "/opt/local/bin/ffmpeg",
        "/usr/bin/ffmpeg",
    ):
        if Path(candidate).exists():
            return candidate
    return None


def _decode_mono(audio_path: str, sample_rate: int) -> Optional[np.ndarray]:
    """Decode an audio file to a mono float32 waveform in [-1, 1]."""
    if not audio_path or not Path(audio_path).exists():
        return None
    ffmpeg = _ffmpeg_bin()
    if ffmpeg is None:
        return None
    try:
        proc = subprocess.run(
            [
                ffmpeg, "-v", "error",
                "-i", audio_path,
                "-ac", "1",
                "-ar", str(sample_rate),
                "-f", "s16le",
                "-",
            ],
            capture_output=True, check=False, timeout=120,
        )
    except Exception:
        return None
    if proc.returncode != 0 or not proc.stdout:
        return None
    samples = np.frombuffer(proc.stdout, dtype=np.int16).astype(np.float32) / 32768.0
    if samples.size == 0:
        return None
    return samples


def audio_duration(audio_path: str, sample_rate: int = 22050) -> float:
    """Length of an audio file in seconds (0.0 if it cannot be decoded). Used to
    auto-pick the number of video repeats so the looped video matches the
    master track length."""
    samples = _decode_mono(audio_path, sample_rate)
    if samples is None or samples.size == 0:
        return 0.0
    return float(samples.size) / float(sample_rate)


def compute_audio_envelope(
    audio_path: str,
    fps: float,
    num_frames: int,
    sample_rate: int = 22050,
) -> Optional[np.ndarray]:
    """Return a per-frame loudness envelope in [0, 1] of length num_frames.

    Each entry is the normalized RMS energy of the audio window that lines up
    with that video frame. Returns None if the audio cannot be decoded.
    """
    if not audio_path or not Path(audio_path).exists():
        return None
    if fps <= 0 or num_frames <= 0:
        return None

    samples = _decode_mono(audio_path, sample_rate)
    if samples is None:
        return None

    samples_per_frame = max(1, int(round(sample_rate / fps)))
    env = np.zeros(num_frames, dtype=np.float32)
    for i in range(num_frames):
        start = i * samples_per_frame
        if start >= samples.size:
            break
        window = samples[start:start + samples_per_frame]
        if window.size:
            env[i] = float(np.sqrt(np.mean(window * window)))

    # Reduced normalization: divide by the true peak (not a low percentile) so
    # the natural dynamics are preserved -- quiet passages stay low instead of
    # being boosted toward full, and only genuine peaks reach 1.0.
    peak = float(np.max(env)) if np.any(env) else 0.0
    if peak <= 1e-6:
        return None
    env = np.clip(env / peak, 0.0, 1.0)

    # Very light temporal smoothing -- just enough to kill single-frame jitter
    # while keeping transients sharp.
    if num_frames >= 3:
        kernel = np.array([0.15, 0.70, 0.15], dtype=np.float32)
        env = np.convolve(env, kernel, mode="same").astype(np.float32)

    return env


def compute_band_envelope(
    audio_path: str,
    fps: float,
    num_frames: int,
    low_hz: float = 20.0,
    high_hz: float = 200.0,
    sample_rate: int = 22050,
) -> Optional[np.ndarray]:
    """Per-frame loudness of a single frequency band (default 20-200 Hz bass).

    Lets the effects react to the kick/bass rather than the overall level,
    which reads much more clearly as 'on the beat'. Returns None if the audio
    cannot be decoded.
    """
    if not audio_path or not Path(audio_path).exists():
        return None
    if fps <= 0 or num_frames <= 0:
        return None
    samples = _decode_mono(audio_path, sample_rate)
    if samples is None:
        return None
    spf = max(1, int(round(sample_rate / fps)))
    win_len = max(spf, 1024)  # larger window -> better low-frequency resolution
    window_fn = np.hanning(win_len).astype(np.float32)
    freqs = np.fft.rfftfreq(win_len, d=1.0 / sample_rate)
    band = (freqs >= low_hz) & (freqs <= high_hz)
    if not np.any(band):
        band = freqs <= high_hz
    env = np.zeros(num_frames, dtype=np.float32)
    for i in range(num_frames):
        start = i * spf
        if start >= samples.size:
            break
        seg = samples[start:start + win_len]
        if seg.size < win_len:
            seg = np.pad(seg, (0, win_len - seg.size))
        spec = np.fft.rfft(seg * window_fn)
        power = np.abs(spec) ** 2
        env[i] = float(np.sqrt(np.mean(power[band]))) if np.any(band) else 0.0
    peak = float(np.max(env)) if np.any(env) else 0.0
    if peak <= 1e-6:
        return None
    env = np.clip(env / peak, 0.0, 1.0)
    if num_frames >= 3:
        kernel = np.array([0.15, 0.70, 0.15], dtype=np.float32)
        env = np.convolve(env, kernel, mode="same").astype(np.float32)
    return env


def compute_beat_envelope(
    audio_path: str,
    fps: float,
    num_frames: int,
    bpm: float,
    sample_rate: int = 22050,
) -> Optional[np.ndarray]:
    """A pulse envelope locked to ``bpm`` -- the text-rotation BPM -- so the
    effects punch exactly on the same beat grid the text animation uses.

    Each beat is a sharp attack that decays before the next one. When audio is
    available each beat's strength is scaled by the track's loudness around
    that beat (so silent stretches don't pulse): beat 'detection' snapped to
    the BPM grid. Phase is aligned to t=0, matching the text rotation.
    """
    if fps <= 0 or num_frames <= 0 or bpm <= 0:
        return None
    beat_period = 60.0 / float(bpm)        # seconds per beat
    tau = max(1e-3, beat_period * 0.22)    # decay time constant
    loud = compute_audio_envelope(audio_path, fps, num_frames, sample_rate)
    env = np.zeros(num_frames, dtype=np.float32)
    for i in range(num_frames):
        t = i / fps
        phase = t - math.floor(t / beat_period) * beat_period
        pulse = math.exp(-phase / tau)
        strength = 1.0
        if loud is not None:
            beat_idx = int(round((t - phase) * fps))
            beat_idx = max(0, min(num_frames - 1, beat_idx))
            strength = float(loud[beat_idx])
        env[i] = pulse * strength
    peak = float(np.max(env)) if np.any(env) else 0.0
    if peak <= 1e-6:
        # Loudness gating zeroed everything -> emit a clean metronome pulse.
        for i in range(num_frames):
            t = i / fps
            phase = t - math.floor(t / beat_period) * beat_period
            env[i] = math.exp(-phase / tau)
        peak = float(np.max(env)) or 1.0
    env = np.clip(env / peak, 0.0, 1.0)
    return env


def compute_reaction_envelope(
    audio_path: str,
    fps: float,
    num_frames: int,
    source: str = "loudness",
    bpm: float = 120.0,
    sample_rate: int = 22050,
) -> Optional[np.ndarray]:
    """Per-frame 0..1 envelope that drives the effects, dispatched by source:

      - "loudness": overall RMS level (the original behavior)
      - "bass":     energy in the low/bass band only
      - "beat":     pulse train locked to the text-rotation BPM
    """
    if source == "bass":
        return compute_band_envelope(
            audio_path, fps, num_frames, sample_rate=sample_rate
        )
    if source == "beat":
        return compute_beat_envelope(
            audio_path, fps, num_frames, bpm, sample_rate=sample_rate
        )
    return compute_audio_envelope(audio_path, fps, num_frames, sample_rate)


def compute_graph_data(
    audio_path: str,
    num_points: int = 480,
    sample_rate: int = 22050,
    source: str = "loudness",
    bpm: float = 120.0,
    duration: float = 0.0,
) -> Optional[dict]:
    """Return detailed data for the reactivity graph.

    Over ``num_points`` evenly spaced, contiguous bins across the whole track:
      - "peak": per-bin peak amplitude (max |sample|), normalized so the
        loudest transient reaches 1.0. Unsmoothed, so it shows crisp peaks and
        lows like a real waveform / limiter display.
      - "rms":  per-bin RMS loudness, percentile-normalized and lightly
        smoothed. This mirrors what actually drives the effects, so the
        reaction curve is drawn from it.
    Returns None if the audio cannot be decoded.
    """
    if num_points <= 0:
        return None
    samples = _decode_mono(audio_path, sample_rate)
    if samples is None:
        return None
    total = int(samples.size)
    if total == 0:
        return None
    edges = np.linspace(0, total, num_points + 1).astype(np.int64)
    peak = np.zeros(num_points, dtype=np.float32)
    rms = np.zeros(num_points, dtype=np.float32)
    for i in range(num_points):
        a = int(edges[i])
        b = int(edges[i + 1])
        if b <= a:
            b = min(total, a + 1)
        window = samples[a:b]
        if window.size:
            peak[i] = float(np.max(np.abs(window)))
            rms[i] = float(np.sqrt(np.mean(window * window)))
    pmax = float(np.max(peak)) if np.any(peak) else 0.0
    if pmax <= 1e-6:
        return None
    peak = np.clip(peak / pmax, 0.0, 1.0)
    rpeak = float(np.percentile(rms, 99)) if np.any(rms) else 0.0
    if rpeak > 1e-6:
        rms = np.clip(rms / rpeak, 0.0, 1.0)
        if num_points >= 3:
            kernel = np.array([0.25, 0.5, 0.25], dtype=np.float32)
            rms = np.convolve(rms, kernel, mode="same").astype(np.float32)

    # The reaction curve should reflect what actually drives the effects, so
    # when the clip duration is known recompute it through the active source
    # (loudness / bass / beat) sampled across the whole track.
    curve = rms
    if duration and duration > 0:
        fps_eff = num_points / float(duration)
        src_env = compute_reaction_envelope(
            audio_path, fps_eff, num_points, source, bpm, sample_rate
        )
        if src_env is not None and len(src_env):
            curve = np.asarray(src_env, dtype=np.float32)

    return {
        "peak": [float(x) for x in peak],
        "rms": [float(x) for x in curve],
    }


def envelope_to_gain(value: float, intensity: float = 0.6) -> float:
    """Map an envelope value in [0,1] to a multiplicative effect gain.

    ``intensity`` (0..1) sets how strongly the audio drives the effects. At
    intensity 0 the gain is always 1.0 (no reaction). As intensity rises, the
    quiet-passage floor drops and the loud-hit ceiling rises, so the effects
    recede in quiet moments and pulse harder on the peaks.
    """
    value = max(0.0, min(1.0, float(value)))
    k = max(0.0, min(1.0, float(intensity)))
    floor = 1.0 - 0.85 * k
    ceil = 1.0 + 1.6 * k
    return floor + (ceil - floor) * value


def compute_envelope_samples(
    audio_path: str,
    num_points: int = 240,
    sample_rate: int = 22050,
) -> Optional[List[float]]:
    """Return a loudness envelope in [0,1] sampled at ``num_points`` evenly
    spaced windows across the whole track.

    Used to draw the reactivity graph independently of any particular video
    length. Returns None if the audio cannot be decoded.
    """
    if num_points <= 0:
        return None
    samples = _decode_mono(audio_path, sample_rate)
    if samples is None:
        return None
    total = int(samples.size)
    if total == 0:
        return None
    env = np.zeros(num_points, dtype=np.float32)
    win = max(1, total // num_points)
    for i in range(num_points):
        start = int(i * total / num_points)
        if start >= total:
            break
        end = min(total, start + win)
        window = samples[start:end]
        if window.size:
            env[i] = float(np.sqrt(np.mean(window * window)))
    peak = float(np.percentile(env, 99)) if np.any(env) else 0.0
    if peak <= 1e-6:
        return None
    env = np.clip(env / peak, 0.0, 1.0)
    if num_points >= 3:
        kernel = np.array([0.25, 0.5, 0.25], dtype=np.float32)
        env = np.convolve(env, kernel, mode="same").astype(np.float32)
    return [float(x) for x in env]
