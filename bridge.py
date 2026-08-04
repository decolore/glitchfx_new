"""Bridge between AppKit UI and the Python video backend."""
from __future__ import annotations

import threading
from pathlib import Path
from typing import Callable, Optional

import numpy as np
from Cocoa import NSBitmapImageRep, NSImage, NSColorPanel

from video_reader_native import VideoReader
from settings import ProjectSettings, default_project
from effects import build_pipeline, apply_pipeline
from export import export_video, _compute_output_frame


class Bridge:
    def __init__(self):
        self.settings = default_project()
        self.current_time = 0.0
        self.duration = 0.0
        self.fps = 30.0
        self._last_raw_frame: Optional[np.ndarray] = None
        self._cached_pipeline = None
        self._frame_env = None
        self._frame_env_key = None
        self._preview_callback: Optional[Callable] = None
        self._ui_refresh_callback: Optional[Callable] = None
        self._reader = VideoReader(on_frame=self.__on_reader_frame, on_error=self.__on_reader_error)
        self._reader.start()

        self._history: list = []
        self._history_index = -1
        self._max_history = 10
        self.__push_history()

    def shutdown(self):
        self._reader.stop()

    def set_preview_callback(self, cb: Callable):
        self._preview_callback = cb

    # ------------------------------------------------------------------
    # Video loading
    # ------------------------------------------------------------------
    def load_video(self, path: Optional[str] = None):
        if path is None:
            from Cocoa import NSOpenPanel
            panel = NSOpenPanel.openPanel()
            panel.setAllowedFileTypes_(["mp4", "mov", "avi", "mkv", "webm"])
            panel.setCanChooseFiles_(True)
            panel.setCanChooseDirectories_(False)
            if panel.runModal() != 1:
                return
            path = panel.URL().path()
        self.__open_video(path)

    def __open_video(self, path: str):
        import cv2
        self.settings.source_path = path
        self._reader.set_source(path)
        cap = cv2.VideoCapture(path)
        if not cap.isOpened():
            self.__show_alert("Cannot open video", path)
            return
        self.fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
        total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
        self.duration = total / self.fps if self.fps > 0 else 0.0
        w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
        h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
        cap.release()

        if self.settings.transform.width <= 0:
            self.settings.transform.width = w
            self.settings.transform.height = h

        stem = Path(path).stem
        if not self.settings.export.output_path:
            self.settings.export.output_path = str(Path.home() / "Desktop" / f"{stem}_fx.mp4")

        # The video length just changed: refresh the auto-driven timing fields
        # (closest number of bars + auto repeat count to fill the audio track).
        self.__auto_sync_on_media_change()

        self.current_time = 0.0
        self._reader.request_time(0.0)
        if self._ui_refresh_callback:
            self._ui_refresh_callback()

    def seek_video(self, time: float):
        self.current_time = max(0.0, min(time, self.duration))
        self._reader.request_time(self.current_time)

    # ------------------------------------------------------------------
    # Playback (real-time preview)
    # ------------------------------------------------------------------
    def is_playing(self) -> bool:
        return self._reader.is_playing()

    def play(self):
        if self.duration <= 0:
            return
        start = self.current_time
        if start >= self.duration - 1e-3:
            start = 0.0  # restart from the beginning if parked at the end
        self._reader.play(start)

    def pause(self):
        self._reader.pause()

    def toggle_playback(self) -> bool:
        if self._reader.is_playing():
            self.pause()
        else:
            self.play()
        return self._reader.is_playing()

    def __on_reader_frame(self, frame: np.ndarray, time: float):
        self._last_raw_frame = frame
        self.current_time = time
        self.__process_and_notify()

    def __on_reader_error(self, msg: str):
        print("reader error:", msg)

    def text_overlay_effect(self):
        for es in self.settings.effects:
            if es.kind == "text_overlay":
                return es
        return None

    # ------------------------------------------------------------------
    # Geometry helpers for the interactive preview selection box
    # ------------------------------------------------------------------
    def source_size(self):
        if self._last_raw_frame is None:
            return None
        h, w = self._last_raw_frame.shape[:2]
        return (int(w), int(h))

    def output_size(self):
        return (int(self.settings.transform.width), int(self.settings.transform.height))

    def video_output_rect(self):
        """Bounding rect of the transformed video in output coords (x, y, w, h)."""
        src = self.source_size()
        if src is None:
            return None
        out_w, out_h = self.output_size()
        if out_w <= 0 or out_h <= 0:
            return None
        from settings import transform_matrix
        M = transform_matrix(src[0], src[1], out_w, out_h, self.settings.transform)
        sx = float(M[0][0]); sy = float(M[1][1])
        tx = float(M[0][2]); ty = float(M[1][2])
        return (tx, ty, sx * src[0], sy * src[1])

    def text_overlay_bbox(self):
        """Last-rendered text bounding box in output coords, or None."""
        es = self.text_overlay_effect()
        if es is None or not es.enabled:
            return None
        # NOTE: do NOT gate on es.params["text"] here. The text value is very
        # often only the schema default ("ANTINOMY") and is absent from the
        # params dict, so es.params.get("text", "") returns "" and made the
        # overlay impossible to select unless the field had been edited. The
        # last-rendered bbox already reflects whether real text was drawn
        # (TextOverlay.apply clears it to None when the text is empty).
        if self._cached_pipeline is None:
            return None
        for fx in self._cached_pipeline:
            if getattr(fx, "kind", None) == "text_overlay":
                return getattr(fx, "_last_bbox", None)
        return None

    def get_transform(self):
        t = self.settings.transform
        return {
            "scale_x": float(t.scale_x), "scale_y": float(t.scale_y),
            "offset_x": float(t.offset_x), "offset_y": float(t.offset_y),
        }

    def get_text_params(self):
        es = self.text_overlay_effect()
        if es is None:
            return None
        defaults = {
            "scale_x": 1.0, "scale_y": 1.0,
            "offset_x": 0.0, "offset_y": 0.0,
            "position_x": 0.5, "position_y": 0.5,
        }
        return {k: float(es.params.get(k, d)) for k, d in defaults.items()}

    def set_transform_values(self, values: dict):
        """Set several transform fields at once, then re-render a single time."""
        for k, v in values.items():
            setattr(self.settings.transform, k, v)
        self.__process_and_notify()

    def set_text_params(self, values: dict):
        """Set several text-overlay params at once, then re-render a single time."""
        es = self.text_overlay_effect()
        if es is None:
            return
        es.params.update(values)
        self.__invalidate_pipeline()
        self.__process_and_notify()

    def reset_transform(self):
        """Reset the interactive transform (scale + position) to defaults.

        Output resolution (width/height) and Fit mode are left untouched.
        """
        t = self.settings.transform
        t.scale_x = 1.0
        t.scale_y = 1.0
        t.offset_x = 0.0
        t.offset_y = 0.0
        self.__process_and_notify()
        self.commit_history()

    # ------------------------------------------------------------------
    # Frame processing
    # ------------------------------------------------------------------
    def __build_pipeline(self):
        if self._cached_pipeline is None:
            self._cached_pipeline = build_pipeline(
                self.settings.effects,
                animate_params=self.settings.animate_params,
                animation_amount=self.settings.animation_amount,
                master_seed=self.settings.master_seed,
                bpm=getattr(self.settings, "bpm", 120.0),
            )
        return self._cached_pipeline

    def invalidate_pipeline(self):
        self.__invalidate_pipeline()

    def process_and_notify(self):
        self.__process_and_notify()

    def __invalidate_pipeline(self):
        self._cached_pipeline = None

    def __process_and_notify(self):
        if self._last_raw_frame is None:
            return
        out_w = self.settings.transform.width
        out_h = self.settings.transform.height
        if out_w <= 0 or out_h <= 0:
            return

        # Render the full composition (transform + every effect) at the real
        # output resolution, so the look NEVER changes with the preview
        # setting. Only the final, fully-composited frame is downscaled for
        # display afterwards.
        frame = _compute_output_frame(
            self._last_raw_frame, self.settings.transform, out_w, out_h, preview=True
        )
        pipeline = self.__build_pipeline()
        reactive, v, k, mode = self.__audio_reaction_state()
        if reactive and mode != "strength":
            from effects import apply_audio_reaction
            fx = apply_pipeline(frame.copy(), pipeline, self.current_time, 1.0)
            frame = apply_audio_reaction(frame, fx, v, k, mode)
        else:
            from audio import envelope_to_gain
            gain = envelope_to_gain(v, k) if reactive else 1.0
            frame = apply_pipeline(frame, pipeline, self.current_time, gain)

        # Preview resolution affects only the displayed image, not the
        # composition. "Auto" caps the displayed pixel count (lighter while
        # playing); the explicit factors downscale the composited frame by N.
        import cv2
        quality = getattr(self.settings, "preview_quality", "Auto")
        factors = {"Full": 1.0, "1/2": 2.0, "1/4": 4.0, "1/8": 8.0}
        if quality in factors:
            disp_scale = 1.0 / factors[quality]
        else:  # "Auto"
            max_pixels = (960 * 540) if self._reader.is_playing() else (1280 * 720)
            disp_scale = min(1.0, (max_pixels / max(1, out_w * out_h)) ** 0.5)
        if disp_scale < 0.999:
            disp_w = max(1, int(round(out_w * disp_scale)))
            disp_h = max(1, int(round(out_h * disp_scale)))
            frame = cv2.resize(frame, (disp_w, disp_h), interpolation=cv2.INTER_AREA)

        nsimage = self.__numpy_to_nsimage(frame)
        if self._preview_callback:
            self._preview_callback(nsimage)

    def __numpy_to_nsimage(self, frame: np.ndarray) -> NSImage:
        """Convert a BGR numpy frame to an NSImage."""
        h, w = frame.shape[:2]
        rep = NSBitmapImageRep.alloc().initWithBitmapDataPlanes_pixelsWide_pixelsHigh_bitsPerSample_samplesPerPixel_hasAlpha_isPlanar_colorSpaceName_bitmapFormat_bytesPerRow_bitsPerPixel_(
            None, w, h, 8, 3, False, False, "NSDeviceRGBColorSpace", 0, w * 3, 24
        )
        # Convert BGR -> RGB in place
        rgb = frame[:, :, ::-1]
        rep.bitmapData()[:] = rgb.tobytes()
        image = NSImage.alloc().initWithSize_((w, h))
        image.addRepresentation_(rep)
        return image

    # ------------------------------------------------------------------
    # Audio reaction (live preview)
    # ------------------------------------------------------------------
    def __get_frame_envelope(self):
        """Cached per-frame loudness envelope aligned to the loaded video, used
        to drive the live preview reaction. Decoded once per (audio, fps,
        length) and reused for every previewed frame."""
        path = getattr(self.settings, "audio_path", "") or ""
        if not path:
            self._frame_env = None
            self._frame_env_key = None
            return None
        from settings import text_rotation_bpm
        source = getattr(self.settings, "reaction_source", "loudness")
        bpm = text_rotation_bpm(self.settings)
        if self.duration and self.fps > 0:
            nframes = max(1, int(round(self.duration * self.fps)))
        else:
            nframes = 0
        key = (path, round(self.fps, 3), nframes, source, round(bpm, 3))
        if self._frame_env_key != key:
            if nframes > 0:
                from audio import compute_reaction_envelope
                env = compute_reaction_envelope(path, self.fps, nframes, source, bpm)
                self._frame_env = [float(x) for x in env] if env is not None else None
            else:
                from audio import compute_envelope_samples
                self._frame_env = compute_envelope_samples(path, 600)
            self._frame_env_key = key
        return self._frame_env

    def __current_envelope_value(self):
        env = self.__get_frame_envelope()
        if not env:
            return 0.0
        idx = int(round(self.current_time * self.fps)) if self.fps > 0 else 0
        idx = max(0, min(len(env) - 1, idx))
        return float(env[idx])

    def __audio_reaction_state(self):
        """Return (reactive, value, intensity_0_1, mode) for the current frame."""
        s = self.settings
        reactive = bool(getattr(s, "audio_reactive", False)) and bool(getattr(s, "audio_path", ""))
        mode = getattr(s, "reaction_mode", "opacity")
        k = max(0, min(100, int(getattr(s, "audio_intensity", 60)))) / 100.0
        v = self.__current_envelope_value() if reactive else 0.0
        return reactive, v, k, mode

    # ------------------------------------------------------------------
    # Settings mutations
    # ------------------------------------------------------------------
    def randomize_all(self):
        import random
        self.settings.master_seed = random.randint(1, 999999)
        from effects import randomize_all as fx_randomize_all
        fx_randomize_all(
            self.settings.effects,
            self.settings.master_seed,
            randomize_order=self.settings.randomize_order,
        )
        self.__invalidate_pipeline()
        self.__push_history()
        self.__process_and_notify()

    def randomize_one(self, effect):
        import random
        from effects import randomize_one as fx_randomize_one
        fx_randomize_one(effect, self.settings.master_seed, hash(effect.kind) % 100000, random.randint(0, 999999))
        self.__invalidate_pipeline()
        self.__push_history()
        self.__process_and_notify()

    def toggle_effect(self, effect, enabled: bool):
        effect.enabled = enabled
        self.__invalidate_pipeline()
        self.__process_and_notify()

    def toggle_animate(self, effect, animate: bool):
        effect.animate = animate
        self.__invalidate_pipeline()
        self.__process_and_notify()

    def set_param(self, effect, key: str, value):
        effect.params[key] = value
        self.__invalidate_pipeline()
        self.__process_and_notify()

    def set_effect_beat_sync(self, effect, value: bool):
        """Toggle the optional per-effect beat trigger (pulse on the beat)."""
        effect.beat_sync = bool(value)
        self.__invalidate_pipeline()
        self.__process_and_notify()

    def set_effect_beat_unit(self, effect, value):
        """Set the number of beats between pulses for an effect's beat trigger."""
        try:
            effect.beat_unit = max(0.0625, float(value))
        except (TypeError, ValueError):
            effect.beat_unit = 1.0
        self.__invalidate_pipeline()
        self.__process_and_notify()

    def set_preview_quality(self, quality: str):
        self.settings.preview_quality = quality
        self.__process_and_notify()

    def step_frame(self, frames: int):
        """Pause playback and nudge the playhead by a number of frames."""
        self.pause()
        if self.fps <= 0:
            return
        self.seek_video(self.current_time + frames / self.fps)

    def set_randomize_order(self, value: bool):
        self.settings.randomize_order = bool(value)

    def set_animate_params(self, value: bool):
        """Master switch: enable/disable all per-effect animation."""
        self.settings.animate_params = bool(value)
        self.__invalidate_pipeline()
        self.__process_and_notify()

    def set_animation_amount(self, value: int):
        """Global animation intensity, clamped to 1-100 percent."""
        self.settings.animation_amount = max(1, min(100, int(value)))
        self.__invalidate_pipeline()
        self.__process_and_notify()

    def set_audio_reactive(self, value: bool):
        """Toggle audio-reactive mode (effects pulse with the chosen track)."""
        self.settings.audio_reactive = bool(value)
        self.__process_and_notify()

    def set_audio_path(self, path: str):
        """Set or clear the GLOBAL master audio track. This single track feeds
        BOTH audio-reactive rendering AND the exported soundtrack, and its
        length drives the automatic video-repeat count. The audio itself is
        never looped on export - only the video repeats to fill it."""
        self.settings.audio_path = path or ""
        self._frame_env_key = None
        # A new master track can change the auto repeat count.
        self.__auto_sync_on_media_change()
        self.__process_and_notify()
        if self._ui_refresh_callback:
            self._ui_refresh_callback()

    def set_audio_intensity(self, value):
        """Set the audio reaction strength (1-100). Higher = effects breathe
        and pulse more dramatically with the track."""
        try:
            v = int(round(float(value)))
        except (TypeError, ValueError):
            return
        self.settings.audio_intensity = max(1, min(100, v))
        self.__process_and_notify()

    def set_reaction_mode(self, mode: str):
        """Choose how the audio visibly drives the look: opacity (whole stack
        fades in/out), pulse (zoom punch), shake, flash, rgbsplit, or the
        subtle per-effect strength recede."""
        valid = {"opacity", "pulse", "shake", "flash", "rgbsplit", "strength"}
        self.settings.reaction_mode = mode if mode in valid else "opacity"
        self.__process_and_notify()

    def set_reaction_source(self, source: str):
        """Choose what the effects listen to: overall loudness, the bass band,
        or a beat pulse locked to the text-rotation BPM."""
        valid = {"loudness", "bass", "beat"}
        self.settings.reaction_source = source if source in valid else "loudness"
        self._frame_env_key = None
        self.__process_and_notify()

    # ------------------------------------------------------------------
    # Beat sync / timing (global BPM, time signature, bars, sync mode, repeats)
    # ------------------------------------------------------------------
    def __auto_audio_duration(self) -> float:
        """Length (seconds) of the global master audio track, or 0.0."""
        from audio import audio_duration
        path = getattr(self.settings, "audio_path", "") or ""
        if not path:
            return 0.0
        try:
            return float(audio_duration(path))
        except Exception:
            return 0.0

    def __auto_sync_on_media_change(self):
        """When the video or master audio changes, refresh the auto-driven
        timing fields: the closest number of bars (when bars are on Auto) and
        the number of video repeats (when repeats are on Auto)."""
        from settings import effective_bars, compute_sync_stats
        src = float(self.duration or 0.0)
        if getattr(self.settings, "sync_auto_bars", True) and src > 0:
            try:
                self.settings.sync_bars = int(effective_bars(self.settings, src))
            except Exception:
                pass
        if getattr(self.settings, "auto_repeats", True):
            try:
                stats = compute_sync_stats(self.settings, src, self.__auto_audio_duration())
                self.settings.video_repeats = int(stats.get("repeats", 1))
            except Exception:
                pass

    def get_sync_stats(self) -> dict:
        """Live beat-sync statistics for the stats panel."""
        from settings import compute_sync_stats
        src = float(self.duration or 0.0)
        return compute_sync_stats(self.settings, src, self.__auto_audio_duration())

    def set_bpm(self, value):
        """Set the GLOBAL project BPM (drives bar length, beat-locked effects
        and text rotation)."""
        try:
            v = float(value)
        except (TypeError, ValueError):
            return
        if v <= 0:
            return
        self.settings.bpm = v
        self.__auto_sync_on_media_change()
        self.__invalidate_pipeline()
        self.__process_and_notify()
        if self._ui_refresh_callback:
            self._ui_refresh_callback()

    def set_time_signature(self, num, den):
        try:
            n = int(num); d = int(den)
        except (TypeError, ValueError):
            return
        if n <= 0 or d <= 0:
            return
        self.settings.time_sig_num = n
        self.settings.time_sig_den = d
        self.__auto_sync_on_media_change()
        self.__invalidate_pipeline()
        self.__process_and_notify()
        if self._ui_refresh_callback:
            self._ui_refresh_callback()

    def set_sync_mode(self, mode):
        """off = keep original speed, speed = stretch/compress to the bar grid,
        trim = cut the video to fit the bar grid without changing speed."""
        valid = {"off", "speed", "trim"}
        self.settings.sync_mode = mode if mode in valid else "off"
        self.__auto_sync_on_media_change()
        if self._ui_refresh_callback:
            self._ui_refresh_callback()

    def set_sync_bars(self, bars):
        try:
            b = int(bars)
        except (TypeError, ValueError):
            return
        self.settings.sync_auto_bars = False
        self.settings.sync_bars = max(1, b)
        self.__auto_sync_on_media_change()
        if self._ui_refresh_callback:
            self._ui_refresh_callback()

    def set_sync_auto_bars(self, value):
        self.settings.sync_auto_bars = bool(value)
        self.__auto_sync_on_media_change()
        if self._ui_refresh_callback:
            self._ui_refresh_callback()

    def set_interpolate(self, value):
        """Toggle fast frame-blending (cross-dissolve) for slowed-down video."""
        self.settings.interpolate = bool(value)
        if self._ui_refresh_callback:
            self._ui_refresh_callback()

    def set_video_repeats(self, value):
        try:
            v = int(value)
        except (TypeError, ValueError):
            return
        self.settings.auto_repeats = False
        self.settings.video_repeats = max(1, v)
        if self._ui_refresh_callback:
            self._ui_refresh_callback()

    def set_auto_repeats(self, value):
        self.settings.auto_repeats = bool(value)
        self.__auto_sync_on_media_change()
        if self._ui_refresh_callback:
            self._ui_refresh_callback()

    def get_audio_envelope_graph(self, num_points: int = 240):
        """Return the loudness envelope (list of 0..1 values) for the currently
        selected audio file, for drawing the reactivity graph. Returns an empty
        list when no audio is set or it cannot be decoded."""
        from audio import compute_envelope_samples
        path = getattr(self.settings, "audio_path", "") or ""
        if not path:
            return []
        env = compute_envelope_samples(path, num_points)
        return env or []

    def get_audio_graph_data(self, num_points: int = 480):
        """Return {"peak": [...], "rms": [...]} (each 0..1) for the detailed
        reactivity graph, or empty lists when no audio is set/decodable."""
        from audio import compute_graph_data
        from settings import text_rotation_bpm
        path = getattr(self.settings, "audio_path", "") or ""
        if not path:
            return {"peak": [], "rms": []}
        source = getattr(self.settings, "reaction_source", "loudness")
        bpm = text_rotation_bpm(self.settings)
        data = compute_graph_data(
            path, num_points, source=source, bpm=bpm, duration=self.duration or 0.0
        )
        return data or {"peak": [], "rms": []}

    def set_lock_random(self, effect, locked: bool):
        effect.lock_random = bool(locked)

    def reset_effect(self, effect):
        """Reset one effect's parameters to their schema defaults."""
        from settings import schema_for
        effect.params = {p.name: p.default for p in schema_for(effect.kind)}
        self.__invalidate_pipeline()
        self.__push_history()
        self.__process_and_notify()
        if self._ui_refresh_callback:
            self._ui_refresh_callback()

    def move_effect(self, from_idx: int, to_idx: int):
        """Reorder effects live (no history push; commit_history at drag end)."""
        effects = self.settings.effects
        n = len(effects)
        if from_idx == to_idx or not (0 <= from_idx < n) or not (0 <= to_idx < n):
            return
        es = effects.pop(from_idx)
        effects.insert(to_idx, es)
        self.__invalidate_pipeline()
        self.__process_and_notify()

    def commit_history(self):
        self.__push_history()

    def set_transform_value(self, key: str, value):
        setattr(self.settings.transform, key, value)
        self.__process_and_notify()

    def set_export_value(self, key: str, value):
        setattr(self.settings.export, key, value)

    def set_master_seed(self, seed: int):
        self.settings.master_seed = seed
        from effects import randomize_all as fx_randomize_all
        fx_randomize_all(
            self.settings.effects,
            seed,
            randomize_order=self.settings.randomize_order,
        )
        self.__invalidate_pipeline()
        self.__push_history()
        self.__process_and_notify()

    # ------------------------------------------------------------------
    # Undo / redo
    # ------------------------------------------------------------------
    def __push_history(self):
        from copy import deepcopy
        snapshot = deepcopy(self.settings.to_dict())
        if self._history_index < len(self._history) - 1:
            self._history = self._history[: self._history_index + 1]
        self._history.append(snapshot)
        if len(self._history) > self._max_history:
            self._history.pop(0)
        else:
            self._history_index += 1

    def undo(self):
        if self._history_index <= 0:
            return
        self._history_index -= 1
        from copy import deepcopy
        self.settings = ProjectSettings.from_dict(deepcopy(self._history[self._history_index]))
        self.__invalidate_pipeline()
        self.__process_and_notify()

    def redo(self):
        if self._history_index >= len(self._history) - 1:
            return
        self._history_index += 1
        from copy import deepcopy
        self.settings = ProjectSettings.from_dict(deepcopy(self._history[self._history_index]))
        self.__invalidate_pipeline()
        self.__process_and_notify()

    # ------------------------------------------------------------------
    # Export
    # ------------------------------------------------------------------
    def apply_preset_settings(self, proj):
        """Restore the exact look saved in a preset.

        Presets must reproduce the same result every time they are loaded, so
        we copy the saved effects (and their parameters), the master seed and
        the randomization/animation flags verbatim instead of re-randomizing.
        The loaded video, output resolution and export settings are left
        untouched.
        """
        from copy import deepcopy

        self.settings.master_seed = proj.master_seed
        self.settings.effects = deepcopy(proj.effects)
        self.settings.randomize_order = proj.randomize_order
        self.settings.animate_params = proj.animate_params
        self.settings.animation_amount = proj.animation_amount

        # Interactive framing (fit/scale/offset) is part of the saved look;
        # output width/height stay tied to the current project.
        t = self.settings.transform
        pt = proj.transform
        t.fit = pt.fit
        t.scale_x = pt.scale_x
        t.scale_y = pt.scale_y
        t.offset_x = pt.offset_x
        t.offset_y = pt.offset_y

        self.__invalidate_pipeline()
        self.__push_history()
        self.__process_and_notify()

    # ------------------------------------------------------------------
    def resolve_export_output(self):
        """Validate the project and ask the user where to save the export.

        Returns the chosen output path, or None if export should not proceed
        (no source loaded, or the save dialog was cancelled). The heavy render
        itself is driven separately so a progress popup can be shown.
        """
        if not self.settings.source_path:
            self.__show_alert("Export", "Load a video first.")
            return None
        if not self.settings.export.output_path:
            stem = Path(self.settings.source_path).stem
            self.settings.export.output_path = str(Path.home() / "Desktop" / f"{stem}_fx.mp4")

        from Cocoa import NSSavePanel, NSURL
        panel = NSSavePanel.savePanel()
        panel.setAllowedFileTypes_(["mp4"])
        out_path = self.settings.export.output_path
        if out_path:
            try:
                panel.setDirectoryURL_(NSURL.fileURLWithPath_(str(Path(out_path).parent)))
            except Exception:
                pass
            panel.setNameFieldStringValue_(Path(out_path).name)
        if panel.runModal() != 1:
            return None
        self.settings.export.output_path = panel.URL().path()
        return self.settings.export.output_path

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------
    def __show_alert(self, title: str, msg: str):
        from Cocoa import NSAlert
        alert = NSAlert.alloc().init()
        alert.setMessageText_(title)
        alert.setInformativeText_(msg)
        alert.runModal()

    # ------------------------------------------------------------------
    # Accent color
    # ------------------------------------------------------------------
    def set_accent_from_panel(self):
        """Call after NSColorPanel selection changes."""
        panel = NSColorPanel.sharedColorPanel()
        color = panel.color()
        from ui.theme import THEME
        r = int(color.redComponent() * 255)
        g = int(color.greenComponent() * 255)
        b = int(color.blueComponent() * 255)
        THEME.set_accent_hex(f"#{r:02x}{g:02x}{b:02x}")
        if self._ui_refresh_callback:
            self._ui_refresh_callback()
