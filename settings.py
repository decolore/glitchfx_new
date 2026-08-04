"""Project state, effect parameter schemas, and preset serialization."""
from __future__ import annotations

import json
import os
from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import Any, Dict, List, Optional

import numpy as np


PRESETS_DIR = Path(__file__).parent / "presets"
PRESETS_DIR.mkdir(exist_ok=True)


@dataclass
class ParamDef:
    """Schema for one effect parameter exposed in the UI."""
    name: str
    label: str
    ptype: str  # "float", "int", "bool", "color", "choice"
    default: Any
    min: Optional[float] = None
    max: Optional[float] = None
    step: Optional[float] = None
    choices: Optional[list] = None
    icon: Optional[str] = None


@dataclass
class EffectSettings:
    """One effect with its enable flag and parameter values."""
    kind: str
    enabled: bool = True
    animate: bool = True
    params: Dict[str, Any] = field(default_factory=dict)
    lock_random: bool = False
    # Optional per-effect beat trigger: pulse this effect in on the beat (locked
    # to the global project BPM) and let it recede before the next pulse.
    beat_sync: bool = False
    beat_unit: float = 1.0  # beats between pulses (0.5 = twice/beat, 4 = 1/bar)

    def to_dict(self) -> dict:
        return {
            "kind": self.kind,
            "enabled": self.enabled,
            "animate": self.animate,
            "params": self.params,
            "lock_random": self.lock_random,
            "beat_sync": self.beat_sync,
            "beat_unit": self.beat_unit,
        }

    @classmethod
    def from_dict(cls, d: dict) -> "EffectSettings":
        return cls(
            kind=d["kind"],
            enabled=d.get("enabled", True),
            animate=d.get("animate", True),
            params=d.get("params", {}),
            lock_random=d.get("lock_random", False),
            beat_sync=d.get("beat_sync", False),
            beat_unit=d.get("beat_unit", 1.0),
        )


@dataclass
class Transform:
    """Output framing / transform."""
    width: int = 0
    height: int = 0
    fit: str = "cover"  # cover, contain, stretch
    scale_x: float = 1.0
    scale_y: float = 1.0
    offset_x: float = 0.0
    offset_y: float = 0.0

    def to_dict(self) -> dict:
        return asdict(self)

    @classmethod
    def from_dict(cls, d: dict) -> "Transform":
        return cls(**d)


def transform_matrix(
    src_w: int,
    src_h: int,
    out_w: int,
    out_h: int,
    t: Transform,
) -> np.ndarray:
    """Return a 2x3 affine matrix mapping source coords to output coords.

    Supports cover, contain and stretch fit modes plus per-axis scale and
    normalized offset (in source-width/height units).
    """
    if src_w <= 0 or src_h <= 0 or out_w <= 0 or out_h <= 0:
        return np.eye(2, 3, dtype=np.float32)

    if t.fit == "stretch":
        sx = out_w / src_w
        sy = out_h / src_h
    elif t.fit == "cover":
        s = max(out_w / src_w, out_h / src_h)
        sx = sy = s
    else:  # contain
        s = min(out_w / src_w, out_h / src_h)
        sx = sy = s

    sx *= max(t.scale_x, 0.001)
    sy *= max(t.scale_y, 0.001)

    cx = src_w * 0.5 + t.offset_x * src_w
    cy = src_h * 0.5 + t.offset_y * src_h
    tx = out_w * 0.5 - cx * sx
    ty = out_h * 0.5 - cy * sy
    return np.array([[sx, 0, tx], [0, sy, ty]], dtype=np.float32)


@dataclass
class ExportSettings:
    """Export configuration."""
    codec: str = "libx264"  # libx264, libx265, h264_videotoolbox, hevc_videotoolbox
    crf: int = 18
    preset: str = "medium"  # ultrafast..veryslow for software encoders
    max_bitrate: str = ""   # e.g. "5M" to cap output size; empty = no cap
    output_path: str = ""
    audio_copy: bool = True
    fps: float = 30.0

    def to_dict(self) -> dict:
        return asdict(self)

    @classmethod
    def from_dict(cls, d: dict) -> "ExportSettings":
        return cls(**d)


@dataclass
class ProjectSettings:
    """Full project state."""
    source_path: str = ""
    master_seed: int = 1
    effects: List[EffectSettings] = field(default_factory=list)
    transform: Transform = field(default_factory=Transform)
    export: ExportSettings = field(default_factory=ExportSettings)
    randomize_order: bool = False
    animate_params: bool = True
    animation_amount: int = 20
    audio_reactive: bool = False
    audio_path: str = ""
    audio_intensity: int = 60
    reaction_mode: str = "opacity"
    reaction_source: str = "loudness"
    preview_quality: str = "Auto"
    # --- Global BPM / bar-sync / repeat timing (master, not per-effect) ---
    bpm: float = 120.0
    time_sig_num: int = 4
    time_sig_den: int = 4
    sync_mode: str = "off"      # "off" | "speed" | "trim"
    sync_bars: int = 8          # used when sync_auto_bars is False
    sync_auto_bars: bool = True # pick the closest bar count to the video length
    interpolate: bool = True    # blend frames when slowing the video down
    video_repeats: int = 1
    auto_repeats: bool = True   # derive repeats from the audio length

    def to_dict(self) -> dict:
        return {
            "source_path": self.source_path,
            "master_seed": self.master_seed,
            "effects": [e.to_dict() for e in self.effects],
            "transform": self.transform.to_dict(),
            "export": self.export.to_dict(),
            "randomize_order": self.randomize_order,
            "animate_params": self.animate_params,
            "animation_amount": self.animation_amount,
            "audio_reactive": self.audio_reactive,
            "audio_path": self.audio_path,
            "audio_intensity": self.audio_intensity,
            "reaction_mode": self.reaction_mode,
            "reaction_source": self.reaction_source,
            "preview_quality": self.preview_quality,
            "bpm": self.bpm,
            "time_sig_num": self.time_sig_num,
            "time_sig_den": self.time_sig_den,
            "sync_mode": self.sync_mode,
            "sync_bars": self.sync_bars,
            "sync_auto_bars": self.sync_auto_bars,
            "interpolate": self.interpolate,
            "video_repeats": self.video_repeats,
            "auto_repeats": self.auto_repeats,
        }

    def save(self, path: str):
        with open(path, "w", encoding="utf-8") as f:
            json.dump(self.to_dict(), f, indent=2)

    @classmethod
    def from_dict(cls, d: dict) -> "ProjectSettings":
        return cls(
            source_path=d.get("source_path", ""),
            master_seed=d.get("master_seed", 1),
            effects=[EffectSettings.from_dict(e) for e in d.get("effects", [])],
            transform=Transform.from_dict(d.get("transform", {})),
            export=ExportSettings.from_dict(d.get("export", {})),
            randomize_order=d.get("randomize_order", False),
            animate_params=d.get("animate_params", True),
            animation_amount=d.get("animation_amount", 20),
            audio_reactive=d.get("audio_reactive", False),
            audio_path=d.get("audio_path", ""),
            audio_intensity=d.get("audio_intensity", 60),
            reaction_mode=d.get("reaction_mode", "opacity"),
            reaction_source=d.get("reaction_source", "loudness"),
            preview_quality=d.get("preview_quality", "Auto"),
            bpm=d.get("bpm", 120.0),
            time_sig_num=d.get("time_sig_num", 4),
            time_sig_den=d.get("time_sig_den", 4),
            sync_mode=d.get("sync_mode", "off"),
            sync_bars=d.get("sync_bars", 8),
            sync_auto_bars=d.get("sync_auto_bars", True),
            interpolate=d.get("interpolate", True),
            video_repeats=d.get("video_repeats", 1),
            auto_repeats=d.get("auto_repeats", True),
        )

    @classmethod
    def load(cls, path: str) -> "ProjectSettings":
        with open(path, "r", encoding="utf-8") as f:
            return cls.from_dict(json.load(f))

    def save_preset(self, name: str):
        safe = "".join(c if c.isalnum() or c in " ._-" else "_" for c in name).strip()
        if not safe:
            safe = "preset"
        path = PRESETS_DIR / f"{safe}.json"
        data = self.to_dict()
        data.pop("source_path", None)
        data.pop("audio_path", None)
        with open(path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
        return path

    @classmethod
    def load_preset(cls, name: str) -> "ProjectSettings":
        path = PRESETS_DIR / f"{name}.json"
        return cls.load(str(path))

    @staticmethod
    def list_presets() -> List[str]:
        files = sorted(PRESETS_DIR.glob("*.json"))
        return [f.stem for f in files]


def default_project() -> ProjectSettings:
    """Factory: project with all built-in effects and sensible defaults."""
    effects = [
        EffectSettings("text_overlay", False, params={}),
        EffectSettings("color_grade", True, params={
            "contrast": 1.1,
            "saturation": 1.2,
            "brightness": 0.0,
            "gamma": 1.0,
            "hue": 0.0,
        }),
        EffectSettings("posterize", True, params={"bits": 5}),
        EffectSettings("edge_glow", True, params={
            "pre_blur": 7,
            "threshold": 55,
            "blur": 9,
            "intensity": 1.0,
            "neon": True,
            "darken": 0.55,
            "thick": 2,
            "color": "#00ffff",
        }),
        EffectSettings("chromatic_aberration", True, params={
            "shift": 3.0,
            "angle": 0.0,
            "animate": True,
        }),
        EffectSettings("sharpen", True, params={
            "amount": 1.0,
            "kernel_size": 3,
        }),
        EffectSettings("color_invert", False, params={
            "blend": 0.0,
        }),
        EffectSettings("color_map", False, params=_color_map_defaults()),
        EffectSettings("datamosh", False, params={}),
        EffectSettings("pixel_sort", False, params={}),
        EffectSettings("dither", False, params={}),
        EffectSettings("motion_glitch", False, params={}),
    ]
    proj = ProjectSettings()
    proj.effects = effects
    proj.transform.width = 0
    proj.transform.height = 0
    return proj


def _color_map_defaults() -> dict:
    defaults = {"opacity": 1.0, "blend_mode": "normal", "tolerance": 60.0}
    for i in range(12):
        defaults[f"enabled_{i}"] = (i == 0)
        defaults[f"target_{i}"] = "#4a6fa5" if i == 0 else "#000000"
        defaults[f"replace_{i}"] = "#00ffff" if i == 0 else "#ffffff"
    return defaults


def _color_map_schema() -> List[ParamDef]:
    schema = [
        ParamDef("opacity", "Opacity", "float", 1.0, 0.0, 1.0, 0.05, icon="eye"),
        ParamDef("blend_mode", "Blend", "choice", "normal", choices=[
            "normal", "overlay", "screen", "add", "multiply", "difference"
        ], icon="square.on.square"),
        ParamDef("tolerance", "Tolerance", "float", 60.0, 0.0, 255.0, 1.0, icon="ruler"),
    ]
    for i in range(12):
        schema.append(ParamDef(f"enabled_{i}", f"Pair {i+1}", "bool", i == 0, icon="checkmark.square"))
        schema.append(ParamDef(f"target_{i}", f"Target {i+1}", "color", "#4a6fa5" if i == 0 else "#000000", icon="target"))
        schema.append(ParamDef(f"replace_{i}", f"Replace {i+1}", "color", "#00ffff" if i == 0 else "#ffffff", icon="paintbrush"))
    return schema


# Schemas used by the UI to build sliders.
EFFECT_SCHEMAS: Dict[str, List[ParamDef]] = {
    "color_grade": [
        ParamDef("contrast", "Contrast", "float", 1.0, 0.0, 3.0, 0.05, icon="circle.righthalf.filled"),
        ParamDef("saturation", "Saturation", "float", 1.0, 0.0, 3.0, 0.05, icon="paintpalette"),
        ParamDef("brightness", "Brightness", "float", 0.0, -1.0, 1.0, 0.02, icon="sun.max"),
        ParamDef("gamma", "Gamma", "float", 1.0, 0.2, 3.0, 0.05, icon="camera.aperture"),
        ParamDef("hue", "Hue Shift", "float", 0.0, -180.0, 180.0, 1.0, icon="rainbow"),
    ],
    "posterize": [
        ParamDef("bits", "Bits", "int", 5, 1, 7, 1, icon="square.stack.3d.up"),
    ],
    "edge_glow": [
        ParamDef("pre_blur", "Pre Blur", "int", 7, 0, 15, 2, icon="drop"),
        ParamDef("threshold", "Threshold", "int", 55, 0, 255, 1, icon="slider.horizontal.below.rectangle"),
        ParamDef("blur", "Glow Blur", "int", 9, 1, 31, 2, icon="drop.fill"),
        ParamDef("intensity", "Intensity", "float", 1.0, 0.0, 2.0, 0.05, icon="bolt"),
        ParamDef("neon", "Neon Mode", "bool", True, icon="lightbulb"),
        ParamDef("darken", "Darken BG", "float", 0.55, 0.0, 1.0, 0.05, icon="moon.fill"),
        ParamDef("thick", "Thickness", "int", 2, 0, 5, 1, icon="line.3.horizontal"),
        ParamDef("color", "Glow Color", "color", "#00ffff", icon="paintbrush"),
    ],
    "chromatic_aberration": [
        ParamDef("shift", "Shift", "float", 3.0, 0.0, 20.0, 0.5, icon="arrow.left.and.right"),
        ParamDef("angle", "Angle", "float", 0.0, -180.0, 180.0, 1.0, icon="rotate.right"),
        ParamDef("animate", "Animate", "bool", True, icon="wand.and.stars"),
    ],
    "noise": [
        ParamDef("amount", "Amount", "float", 0.03, 0.0, 0.5, 0.01, icon="waveform"),
        ParamDef("monochrome", "Monochrome", "bool", True, icon="circle.lefthalf.filled"),
    ],
    "sharpen": [
        ParamDef("amount", "Amount", "float", 1.0, 0.0, 3.0, 0.1, icon="triangle"),
        ParamDef("kernel_size", "Kernel", "int", 3, 1, 7, 2, icon="grid"),
    ],
    "glitch_blocks": [
        ParamDef("block_size", "Block Size", "int", 16, 4, 64, 2, icon="square.grid.2x2"),
        ParamDef("shift_max", "Max Shift", "int", 20, 0, 100, 1, icon="arrow.left.and.right"),
        ParamDef("density", "Density", "float", 0.05, 0.0, 1.0, 0.01, icon="uiwindow.split.2x1"),
    ],
    "scanlines": [
        ParamDef("spacing", "Spacing", "int", 2, 1, 8, 1, icon="line.3.horizontal"),
        ParamDef("opacity", "Opacity", "float", 0.2, 0.0, 1.0, 0.05, icon="eye"),
    ],
    "color_invert": [
        ParamDef("blend", "Blend", "float", 0.0, 0.0, 1.0, 0.05, icon="circle.lefthalf.filled"),
    ],
    "vignette": [
        ParamDef("strength", "Strength", "float", 0.4, 0.0, 1.0, 0.05, icon="circle.dashed"),
        ParamDef("color", "Color", "color", "#000000", icon="paintbrush"),
    ],
    "color_map": _color_map_schema(),
    "datamosh": [
        ParamDef("intensity", "Intensity", "float", 0.5, 0.0, 1.0, 0.05, icon="bolt"),
        ParamDef("block_size", "Block Size", "int", 16, 4, 64, 2, icon="square.grid.2x2"),
        ParamDef("threshold", "Threshold", "int", 12, 0, 50, 1, icon="slider.horizontal.below.rectangle"),
    ],
    "pixel_sort": [
        ParamDef("threshold", "Threshold", "int", 120, 0, 255, 1, icon="slider.horizontal.below.rectangle"),
        ParamDef("angle", "Angle", "choice", "0", choices=["0", "90", "180", "270", "360"], icon="rotate.right"),
        ParamDef("mode", "Mode", "choice", "bright", choices=["bright", "dark"], icon="switch.2"),
        ParamDef("max_length", "Max Length", "int", 400, 50, 2000, 10, icon="ruler"),
    ],
    "vhs": [
        ParamDef("tracking", "Tracking", "float", 0.3, 0.0, 1.0, 0.05, icon="arrow.up.forward"),
        ParamDef("chroma_bleed", "Chroma Bleed", "float", 4.0, 0.0, 20.0, 0.5, icon="camera.filters"),
        ParamDef("noise", "Noise", "float", 0.05, 0.0, 0.5, 0.01, icon="waveform"),
        ParamDef("scanlines", "Scanlines", "float", 0.2, 0.0, 1.0, 0.05, icon="line.3.horizontal"),
    ],
    "dither": [
        ParamDef("bits", "Bits", "int", 3, 1, 8, 1, icon="square.stack.3d.up"),
        ParamDef("type", "Type", "choice", "bayer", choices=["bayer", "floyd"], icon="switch.2"),
        ParamDef("palette", "Palette", "choice", "color", choices=["color", "grayscale"], icon="paintpalette"),
    ],
    "motion_glitch": [
        ParamDef("flow_scale", "Flow Scale", "float", 1.5, 0.0, 5.0, 0.1, icon="wind"),
        ParamDef("threshold", "Threshold", "int", 8, 0, 50, 1, icon="slider.horizontal.below.rectangle"),
    ],
    "motion_trails": [
        ParamDef("length", "Length", "int", 6, 2, 30, 1, icon="ruler"),
        ParamDef("decay", "Decay", "float", 0.7, 0.1, 1.0, 0.05, icon="waveform"),
        ParamDef("blend", "Blend", "choice", "screen", choices=["normal", "screen", "add"], icon="square.on.square"),
    ],
    "text_overlay": [
        ParamDef("text", "Text", "string", "ANTINOMY", icon="textformat"),
        ParamDef("font_family", "Font", "font", "Arial", icon="textformat.size"),
        ParamDef("font_size", "Size", "int", 120, 10, 400, 1, icon="textformat.size.larger"),
        ParamDef("bold", "Bold", "bool", True, icon="bold"),
        ParamDef("italic", "Italic", "bool", False, icon="italic"),
        ParamDef("color", "Color", "color", "#ffffff", icon="paintbrush"),
        ParamDef("outline_color", "Outline", "color", "#000000", icon="paintbrush.pointed"),
        ParamDef("outline_thickness", "Outline size", "int", 4, 0, 30, 1, icon="line.3.horizontal"),
        ParamDef("shadow_color", "Shadow", "color", "#000000", icon="paintbrush"),
        ParamDef("shadow_offset_x", "Shadow X", "int", 6, -50, 50, 1, icon="arrow.left.and.right"),
        ParamDef("shadow_offset_y", "Shadow Y", "int", 6, -50, 50, 1, icon="arrow.up.and.down"),
        ParamDef("depth", "3D depth", "int", 0, 0, 60, 1, icon="cube"),
        ParamDef("depth_color", "Depth color", "color", "#1a1a1a", icon="cube.fill"),
        ParamDef("depth_angle", "Depth angle", "float", 45.0, -180.0, 180.0, 1.0, icon="arrow.up.right"),
        ParamDef("position_x", "Pos X", "float", 0.5, 0.0, 1.0, 0.01, icon="arrow.left.and.right"),
        ParamDef("position_y", "Pos Y", "float", 0.5, 0.0, 1.0, 0.01, icon="arrow.up.and.down"),
        ParamDef("anchor_h", "Anchor H", "choice", "center", choices=["left", "center", "right"], icon="align.horizontal.left"),
        ParamDef("anchor_v", "Anchor V", "choice", "center", choices=["top", "center", "bottom"], icon="align.vertical.top"),
        ParamDef("scale_x", "Scale X", "float", 1.0, 0.1, 5.0, 0.05, icon="arrow.left.and.right"),
        ParamDef("scale_y", "Scale Y", "float", 1.0, 0.1, 5.0, 0.05, icon="arrow.up.and.down"),
        ParamDef("offset_x", "Offset X", "float", 0.0, -1.0, 1.0, 0.01, icon="arrow.left.and.right"),
        ParamDef("offset_y", "Offset Y", "float", 0.0, -1.0, 1.0, 0.01, icon="arrow.up.and.down"),
        ParamDef("animation", "Animation", "choice", "rotate", choices=["none", "rotate", "swing", "tumble", "float3d", "jolt"], icon="wand.and.stars"),
        ParamDef("bars", "Animation bars", "choice", "4", choices=["4", "8", "16", "32"], icon="arrow.clockwise"),
        ParamDef("jolt_beats", "Jolt loop (beats)", "int", 4, 1, 32, 1, icon="repeat"),
    ],
}


def schema_for(kind: str) -> List[ParamDef]:
    return EFFECT_SCHEMAS.get(kind, [])


def text_rotation_bpm(settings) -> float:
    """BPM the text rotation/jolt animation is locked to. This is now the single
    global project BPM (moved out of the text effect) so that everything that
    needs the beat -- text rotation, beat-synced reactivity, and the bar-based
    speed/trim sync -- is tied to one value. Defaults to 120."""
    try:
        return float(getattr(settings, "bpm", 120.0))
    except Exception:
        return 120.0


# --- Bar / BPM timing helpers (pure functions, fully unit-testable) ---

SYNC_BAR_CHOICES = (4, 8, 16, 32)


def bar_duration(bpm: float, num_bars: int, time_sig_num: int = 4,
                 time_sig_den: int = 4) -> float:
    """Seconds spanned by ``num_bars`` bars at ``bpm`` in the given time
    signature. One beat = a ``1/time_sig_den`` note; a quarter note is the BPM
    reference, so beat_seconds = (60/bpm) * (4/den). Each bar has ``num`` beats.

    Example: 140 BPM, 8 bars, 4/4 -> 8*4*(4/4)*(60/140) = 13.714s.
    """
    bpm = float(bpm)
    if bpm <= 0:
        return 0.0
    den = max(1, int(time_sig_den))
    beats = float(num_bars) * float(time_sig_num) * (4.0 / den)
    return beats * (60.0 / bpm)


def closest_bars(video_duration: float, bpm: float, time_sig_num: int = 4,
                 time_sig_den: int = 4, choices=SYNC_BAR_CHOICES) -> int:
    """The bar count from ``choices`` whose duration is nearest to
    ``video_duration`` at the given tempo."""
    choices = tuple(choices) or SYNC_BAR_CHOICES
    if video_duration <= 0 or bpm <= 0:
        return choices[0]
    best = choices[0]
    best_diff = None
    for b in choices:
        diff = abs(bar_duration(bpm, b, time_sig_num, time_sig_den) - video_duration)
        if best_diff is None or diff < best_diff:
            best_diff = diff
            best = b
    return int(best)


def effective_bars(settings, video_duration: float) -> int:
    """The bar count actually in effect: the closest one when auto, otherwise
    the user-chosen ``sync_bars``."""
    if getattr(settings, "sync_auto_bars", True):
        return closest_bars(
            video_duration, getattr(settings, "bpm", 120.0),
            getattr(settings, "time_sig_num", 4), getattr(settings, "time_sig_den", 4),
        )
    try:
        return int(getattr(settings, "sync_bars", 8))
    except Exception:
        return 8


def compute_sync_stats(settings, src_duration: float,
                       audio_duration: float = 0.0) -> dict:
    """Everything the stats panel and the exporter need: the bar-synced cycle
    length, the speed factor, the repeat count and the final output length.

    - mode "speed": the single cycle is stretched/compressed to one bar block.
    - mode "trim":  the cycle is the video, capped at the bar block length.
    - mode "off":   the cycle is the untouched video.
    Repeats are derived from the audio length when ``auto_repeats`` is on.
    """
    bpm = float(getattr(settings, "bpm", 120.0))
    num = int(getattr(settings, "time_sig_num", 4))
    den = int(getattr(settings, "time_sig_den", 4))
    mode = getattr(settings, "sync_mode", "off")
    src_duration = max(0.0, float(src_duration))
    audio_duration = max(0.0, float(audio_duration))

    bars = effective_bars(settings, src_duration)
    target = bar_duration(bpm, bars, num, den)

    if mode == "speed" and target > 0 and src_duration > 0:
        cycle = target
        speed = src_duration / target  # >1 => faster, <1 => slower
    elif mode == "trim" and target > 0 and src_duration > 0:
        cycle = min(src_duration, target)
        speed = 1.0
    else:  # "off" or not enough info
        cycle = src_duration
        speed = 1.0

    if getattr(settings, "auto_repeats", True) and audio_duration > 0 and cycle > 0:
        repeats = max(1, int(round(audio_duration / cycle)))
    else:
        try:
            repeats = max(1, int(getattr(settings, "video_repeats", 1)))
        except Exception:
            repeats = 1

    total = cycle * repeats
    if speed > 1.0:
        speed_label = "faster"
    elif speed < 1.0:
        speed_label = "slower"
    else:
        speed_label = "unchanged"

    return {
        "bpm": bpm,
        "time_sig": f"{num}/{den}",
        "time_sig_num": num,
        "time_sig_den": den,
        "mode": mode,
        "bars": bars,
        "auto_bars": bool(getattr(settings, "sync_auto_bars", True)),
        "src_duration": src_duration,
        "target_duration": target,
        "cycle_duration": cycle,
        "speed_factor": speed,
        "speed_label": speed_label,
        "interpolate": bool(getattr(settings, "interpolate", True)),
        "repeats": repeats,
        "auto_repeats": bool(getattr(settings, "auto_repeats", True)),
        "audio_duration": audio_duration,
        "total_duration": total,
    }
