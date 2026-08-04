"""Image effect pipeline built on NumPy + OpenCV."""
from __future__ import annotations

import math
import random
from collections import deque
from typing import List

import cv2
import numpy as np

from PyQt6.QtCore import Qt
from PyQt6.QtGui import (
    QColor, QFont, QFontDatabase, QFontMetrics, QImage, QPainter,
    QPainterPath, QPen,
)
from PyQt6.QtWidgets import QApplication

from settings import EffectSettings, schema_for


def _hex_to_rgb(hexstr):
    """Convert a #rrggbb / #rgb string to an (r, g, b) float tuple in 0..1."""
    s = str(hexstr).lstrip("#")
    if len(s) == 3:
        s = "".join(c * 2 for c in s)
    if len(s) != 6:
        s = "ffffff"
    try:
        return (int(s[0:2], 16) / 255.0, int(s[2:4], 16) / 255.0, int(s[4:6], 16) / 255.0)
    except ValueError:
        return (1.0, 1.0, 1.0)


class BaseEffect:
    """Base class for an effect."""

    kind: str = ""
    stateful: bool = False

    def __init__(self, settings: EffectSettings):
        self.settings = settings
        self.params = dict(settings.params)
        self.enabled = settings.enabled
        self.animate_enabled = False
        self.animate_amount = 0
        self.audio_gain = 1.0
        self.master_seed = 1
        self.project_bpm = None
        self.beat_sync = False
        self.beat_unit = 1.0

    def randomize(self, rng: random.Random):
        """Override to randomize params from a seeded RNG."""

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        """Override to process a frame."""
        return frame

    def param(self, key: str, default=None):
        return self.params.get(key, default)

    def animated_param(self, key: str, time: float, default=None):
        """Return a parameter value, optionally animated over time."""
        base = self.param(key, default)
        if not self.animate_enabled:
            return base

        pdef = None
        for p in schema_for(self.settings.kind):
            if p.name == key:
                pdef = p
                break
        if pdef is None or pdef.ptype not in ("float", "int"):
            return base

        # Fall back to schema default when the value is missing.
        if base is None:
            base = pdef.default
        if base is None:
            return self.param(key, default)

        # Stable 32-bit FNV-1a hash; Python hash() is randomized per process.
        h = 2166136261
        for b in repr((self.master_seed, self.settings.kind, key)).encode("utf-8"):
            h ^= b
            h = (h * 16777619) & 0xFFFFFFFF

        phase1 = (h / (2**32)) * 2 * math.pi
        phase2 = ((h * 2654435761) & 0xFFFFFFFF) / (2**32) * 2 * math.pi
        noise = 0.5 * math.sin(time * 0.7 + phase1) + 0.5 * math.sin(time * 1.3 + phase2)

        if pdef.min is not None and pdef.max is not None:
            param_range = pdef.max - pdef.min
        else:
            try:
                param_range = abs(base)
            except TypeError:
                param_range = 0.0

        magnitude = param_range * 0.5 * (self.animate_amount / 100.0) * self.audio_gain
        value = base + noise * magnitude

        if pdef.min is not None:
            value = max(value, pdef.min)
        if pdef.max is not None:
            value = min(value, pdef.max)
        if pdef.ptype == "int":
            value = int(round(value))
        return value


def _hex_to_bgr(hex_color: str) -> np.ndarray:
    hex_color = hex_color.lstrip("#")
    if len(hex_color) == 3:
        hex_color = "".join(c * 2 for c in hex_color)
    r = int(hex_color[0:2], 16)
    g = int(hex_color[2:4], 16)
    b = int(hex_color[4:6], 16)
    return np.array([b, g, r], dtype=np.float32)


class ColorGrade(BaseEffect):
    kind = "color_grade"

    def randomize(self, rng: random.Random):
        self.params["contrast"] = rng.uniform(0.9, 2.0)
        self.params["saturation"] = rng.uniform(0.6, 2.2)
        self.params["brightness"] = rng.uniform(-0.15, 0.25)
        self.params["gamma"] = rng.uniform(0.7, 1.4)
        self.params["hue"] = rng.uniform(-50.0, 50.0)

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        out = frame.astype(np.float32) / 255.0
        out = np.power(out, max(0.1, self.animated_param("gamma", time, 1.0)))
        out = (
            out - 0.5
        ) * self.animated_param("contrast", time, 1.0) + 0.5 + self.animated_param(
            "brightness", time, 0.0
        )
        out = np.clip(out, 0, 1)

        hsv = cv2.cvtColor((out * 255).astype(np.uint8), cv2.COLOR_BGR2HSV).astype(np.float32)
        hsv[:, :, 1] *= self.animated_param("saturation", time, 1.0)
        hsv[:, :, 1] = np.clip(hsv[:, :, 1], 0, 255)
        hsv[:, :, 0] = (hsv[:, :, 0] + self.animated_param("hue", time, 0.0) / 2.0) % 180
        out = cv2.cvtColor(hsv.astype(np.uint8), cv2.COLOR_HSV2BGR).astype(np.float32) / 255.0
        return (out * 255).astype(np.uint8)


class Posterize(BaseEffect):
    kind = "posterize"

    def randomize(self, rng: random.Random):
        self.params["bits"] = rng.randint(2, 6)

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        bits = int(self.animated_param("bits", time, 5))
        levels = 2 ** bits
        quant = (frame.astype(np.float32) / (256.0 / levels)).astype(np.uint8) * int(256 / levels)
        return np.clip(quant, 0, 255).astype(np.uint8)


class EdgeGlow(BaseEffect):
    kind = "edge_glow"

    def randomize(self, rng: random.Random):
        self.params["pre_blur"] = rng.choice([0, 3, 5, 7])
        self.params["threshold"] = rng.randint(15, 80)
        self.params["blur"] = rng.choice([3, 5, 7, 11, 15])
        self.params["intensity"] = rng.uniform(0.5, 1.5)
        self.params["neon"] = rng.choice([True, False])
        self.params["darken"] = rng.uniform(0.3, 0.85)
        self.params["thick"] = rng.choice([0, 1, 2, 3])
        self.params["color"] = "#" + "".join(f"{rng.randint(0,255):02x}" for _ in range(3))

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)

        # Smooth away fine texture before edge detection
        pre_blur = int(self.animated_param("pre_blur", time, 5))
        if pre_blur > 1:
            if pre_blur % 2 == 0:
                pre_blur += 1
            gray = cv2.GaussianBlur(gray, (pre_blur, pre_blur), 0)

        # Sobel magnitude edges (smoother than Canny for this look)
        sobelx = cv2.Sobel(gray, cv2.CV_64F, 1, 0, ksize=3)
        sobely = cv2.Sobel(gray, cv2.CV_64F, 0, 1, ksize=3)
        mag = np.sqrt(sobelx ** 2 + sobely ** 2)
        mx = mag.max() if mag.max() > 0 else 1
        mag = (mag / mx * 255).astype(np.uint8)
        _, edges = cv2.threshold(
            mag, self.animated_param("threshold", time, 40), 255, cv2.THRESH_BINARY
        )

        # Thicken edges
        thick = int(self.animated_param("thick", time, 1))
        if thick > 0:
            kernel = cv2.getStructuringElement(
                cv2.MORPH_ELLIPSE, (thick * 2 + 1, thick * 2 + 1)
            )
            edges = cv2.dilate(edges, kernel, iterations=1)

        # Glow blur
        blur = int(self.animated_param("blur", time, 7))
        if blur > 1:
            if blur % 2 == 0:
                blur += 1
            edges = cv2.GaussianBlur(edges, (blur, blur), 0)

        intensity = self.animated_param("intensity", time, 0.8)
        color = _hex_to_bgr(self.animated_param("color", time, "#00ffff"))
        glow = cv2.cvtColor(edges, cv2.COLOR_GRAY2BGR).astype(np.float32) / 255.0
        glow = glow * color * intensity

        if self.animated_param("neon", time, False):
            darken = self.animated_param("darken", time, 0.6)
            dark = frame.astype(np.float32) * (1.0 - darken)
            result = dark + glow
        else:
            result = frame.astype(np.float32) + glow
        return np.clip(result, 0, 255).astype(np.uint8)


class ChromaticAberration(BaseEffect):
    kind = "chromatic_aberration"

    def randomize(self, rng: random.Random):
        self.params["shift"] = rng.uniform(1.0, 10.0)
        self.params["angle"] = rng.uniform(-180.0, 180.0)
        self.params["animate"] = rng.choice([True, False])

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        shift = self.animated_param("shift", time, 3.0)
        angle = math.radians(self.animated_param("angle", time, 0.0))
        if self.animated_param("animate", time, True):
            angle += time * 1.2
        dx = int(round(shift * math.cos(angle)))
        dy = int(round(shift * math.sin(angle)))
        b, g, r = cv2.split(frame)
        M_b = np.float32([[1, 0, -dx], [0, 1, -dy]])
        M_r = np.float32([[1, 0, dx], [0, 1, dy]])
        b = cv2.warpAffine(
            b, M_b, (frame.shape[1], frame.shape[0]), borderMode=cv2.BORDER_WRAP
        )
        r = cv2.warpAffine(
            r, M_r, (frame.shape[1], frame.shape[0]), borderMode=cv2.BORDER_WRAP
        )
        return cv2.merge([b, g, r])


class Noise(BaseEffect):
    kind = "noise"

    def randomize(self, rng: random.Random):
        self.params["amount"] = rng.uniform(0.01, 0.12)
        self.params["monochrome"] = rng.choice([True, False])

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        amount = self.animated_param("amount", time, 0.03)
        if amount <= 0:
            return frame
        if self.animated_param("monochrome", time, True):
            noise = np.random.normal(0, amount * 255, frame.shape[:2]).astype(np.float32)
            noise = np.stack([noise] * 3, axis=2)
        else:
            noise = np.random.normal(0, amount * 255, frame.shape).astype(np.float32)
        result = frame.astype(np.float32) + noise
        return np.clip(result, 0, 255).astype(np.uint8)


class Sharpen(BaseEffect):
    kind = "sharpen"

    def randomize(self, rng: random.Random):
        self.params["amount"] = rng.uniform(0.5, 2.2)
        self.params["kernel_size"] = rng.choice([1, 3, 5])

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        amount = self.animated_param("amount", time, 1.0)
        k = int(self.animated_param("kernel_size", time, 3))
        if k < 3:
            kernel = (
                np.array([[0, -1, 0], [-1, 5, -1], [0, -1, 0]], dtype=np.float32) * amount
            )
        else:
            kernel = (
                np.array([[-1, -1, -1], [-1, 9, -1], [-1, -1, -1]], dtype=np.float32)
                * amount
            )
        sharpened = cv2.filter2D(frame, -1, kernel)
        return np.clip(sharpened, 0, 255).astype(np.uint8)


class GlitchBlocks(BaseEffect):
    kind = "glitch_blocks"

    def randomize(self, rng: random.Random):
        self.params["block_size"] = rng.randint(8, 48)
        self.params["shift_max"] = rng.randint(10, 80)
        self.params["density"] = rng.uniform(0.02, 0.2)

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        h, w = frame.shape[:2]
        block = max(4, int(self.animated_param("block_size", time, 16)))
        shift_max = int(self.animated_param("shift_max", time, 20))
        density = self.animated_param("density", time, 0.05)
        if density <= 0 or shift_max <= 0:
            return frame
        out = frame.copy()
        rng = random.Random(self.settings.kind + str(int(time * 1000)))
        for y in range(0, h, block):
            if rng.random() < density:
                shift = rng.randint(-shift_max, shift_max)
                strip = out[y:y + block, :].copy()
                out[y:y + block, :] = np.roll(strip, shift, axis=1)
                if rng.random() < 0.3:
                    ch = rng.randint(0, 2)
                    out[y:y + block, :, ch] = np.roll(
                        out[y:y + block, :, ch], shift // 2, axis=1
                    )
        return out


class Scanlines(BaseEffect):
    kind = "scanlines"

    def randomize(self, rng: random.Random):
        self.params["spacing"] = rng.randint(1, 5)
        self.params["opacity"] = rng.uniform(0.1, 0.5)

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        spacing = max(1, int(self.animated_param("spacing", time, 2)))
        opacity = self.animated_param("opacity", time, 0.2)
        mask = np.ones_like(frame, dtype=np.float32)
        mask[::spacing, :] *= (1.0 - opacity)
        return np.clip(frame.astype(np.float32) * mask, 0, 255).astype(np.uint8)


class ColorInvert(BaseEffect):
    kind = "color_invert"

    def randomize(self, rng: random.Random):
        self.params["blend"] = rng.uniform(0.0, 0.5)

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        blend = self.animated_param("blend", time, 0.0)
        if blend <= 0:
            return frame
        inv = 255 - frame
        return cv2.addWeighted(frame, 1.0 - blend, inv, blend, 0)


class Vignette(BaseEffect):
    kind = "vignette"

    def randomize(self, rng: random.Random):
        self.params["strength"] = rng.uniform(0.1, 0.7)
        self.params["color"] = "#" + "".join(f"{rng.randint(0,255):02x}" for _ in range(3))

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        h, w = frame.shape[:2]
        strength = self.animated_param("strength", time, 0.4)
        color = _hex_to_bgr(self.animated_param("color", time, "#000000"))
        Y, X = np.ogrid[:h, :w]
        cx, cy = w / 2.0, h / 2.0
        dist = np.sqrt((X - cx) ** 2 + (Y - cy) ** 2)
        max_dist = np.sqrt(cx ** 2 + cy ** 2)
        mask = 1 - (dist / max_dist) * strength
        mask = np.clip(mask, 0, 1)
        mask_3 = np.stack([mask] * 3, axis=2)
        result = frame.astype(np.float32) * mask_3 + color * (1.0 - mask_3)
        return np.clip(result, 0, 255).astype(np.uint8)


class ColorMap(BaseEffect):
    """Replace up to 12 target colors with replacement colors."""
    kind = "color_map"

    def randomize(self, rng: random.Random):
        self.params["opacity"] = rng.uniform(0.4, 1.0)
        self.params["blend_mode"] = rng.choice(
            ["normal", "overlay", "screen", "add", "multiply", "difference"]
        )
        self.params["tolerance"] = rng.uniform(30.0, 120.0)
        # Randomize 2-5 active pairs
        active_count = rng.randint(2, 5)
        for i in range(12):
            self.params[f"enabled_{i}"] = i < active_count
            self.params[f"target_{i}"] = "#" + "".join(
                f"{rng.randint(0,255):02x}" for _ in range(3)
            )
            self.params[f"replace_{i}"] = "#" + "".join(
                f"{rng.randint(0,255):02x}" for _ in range(3)
            )

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        opacity = self.animated_param("opacity", time, 1.0)
        if opacity <= 0:
            return frame

        base = frame.astype(np.float32)
        mapped = base.copy()
        tolerance = self.animated_param("tolerance", time, 60.0)
        tolerance_sq = tolerance * tolerance

        for i in range(12):
            if not self.animated_param(f"enabled_{i}", time, False):
                continue
            target = _hex_to_bgr(self.animated_param(f"target_{i}", time, "#000000"))
            replace = _hex_to_bgr(self.animated_param(f"replace_{i}", time, "#ffffff"))
            diff = mapped - target
            dist_sq = np.sum(diff * diff, axis=2)
            mask = (dist_sq <= tolerance_sq).astype(np.float32)
            mask_3 = np.stack([mask] * 3, axis=2)
            mapped = mapped * (1.0 - mask_3) + replace * mask_3

        mode = self.animated_param("blend_mode", time, "normal")
        if mode == "normal":
            blended = base * (1 - opacity) + mapped * opacity
        elif mode == "overlay":
            overlay = _blend_overlay(base, mapped)
            blended = base * (1 - opacity) + overlay * opacity
        elif mode == "screen":
            screen = 255 - (255 - base) * (255 - mapped) / 255.0
            blended = base * (1 - opacity) + screen * opacity
        elif mode == "add":
            add = np.clip(base + mapped, 0, 255)
            blended = base * (1 - opacity) + add * opacity
        elif mode == "multiply":
            mult = base * mapped / 255.0
            blended = base * (1 - opacity) + mult * opacity
        elif mode == "difference":
            diff = np.abs(base - mapped)
            blended = base * (1 - opacity) + diff * opacity
        else:
            blended = base * (1 - opacity) + mapped * opacity

        return np.clip(blended, 0, 255).astype(np.uint8)


def _blend_overlay(base: np.ndarray, blend: np.ndarray) -> np.ndarray:
    """Photoshop-style Overlay blend."""
    result = np.zeros_like(base)
    mask = blend <= 128
    result[mask] = 2 * base[mask] * blend[mask] / 255.0
    result[~mask] = 255 - 2 * (255 - base[~mask]) * (255 - blend[~mask]) / 255.0
    return result


class Datamosh(BaseEffect):
    """Copy low-motion blocks from the previous frame."""

    kind = "datamosh"
    stateful = True

    def __init__(self, settings: EffectSettings):
        super().__init__(settings)
        self._prev = None

    def randomize(self, rng: random.Random):
        self.params["intensity"] = rng.uniform(0.1, 0.8)
        self.params["block_size"] = rng.randint(8, 32)
        self.params["threshold"] = rng.randint(5, 30)

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        if self._prev is None or self._prev.shape != frame.shape:
            self._prev = frame.copy()
            return frame

        intensity = self.animated_param("intensity", time)
        block_size = max(4, int(self.animated_param("block_size", time)))
        threshold = int(self.animated_param("threshold", time))

        gray_cur = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        gray_prev = cv2.cvtColor(self._prev, cv2.COLOR_BGR2GRAY)
        diff = cv2.absdiff(gray_cur, gray_prev)

        out = frame.copy()
        h, w = frame.shape[:2]
        for y in range(0, h, block_size):
            for x in range(0, w, block_size):
                block = diff[y : y + block_size, x : x + block_size]
                if block.size == 0:
                    continue
                if block.mean() < threshold and np.random.rand() < intensity:
                    out[y : y + block_size, x : x + block_size] = self._prev[
                        y : y + block_size, x : x + block_size
                    ]

        self._prev = frame.copy()
        return out


class PixelSort(BaseEffect):
    """Sort pixels along rows based on brightness thresholds."""

    kind = "pixel_sort"

    def randomize(self, rng: random.Random):
        self.params["threshold"] = rng.randint(60, 180)
        self.params["angle"] = rng.choice(["0", "90", "180", "270", "360"])
        self.params["mode"] = rng.choice(["bright", "dark"])
        self.params["max_length"] = rng.randint(200, 800)

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        threshold = int(self.animated_param("threshold", time))
        angle = int(self.animated_param("angle", time))
        mode = self.animated_param("mode", time)
        max_length = int(self.animated_param("max_length", time))

        h, w = frame.shape[:2]
        working = frame.copy()
        center = (w / 2, h / 2)

        if angle != 0:
            M = cv2.getRotationMatrix2D(center, angle, 1.0)
            working = cv2.warpAffine(
                working, M, (w, h), borderMode=cv2.BORDER_CONSTANT, borderValue=(0, 0, 0)
            )

        gray = cv2.cvtColor(working, cv2.COLOR_BGR2GRAY)
        for y in range(gray.shape[0]):
            row = working[y]
            g_row = gray[y]
            x = 0
            while x < len(g_row):
                in_run = (g_row[x] >= threshold) if mode == "bright" else (g_row[x] <= threshold)
                if not in_run:
                    x += 1
                    continue
                start = x
                while x < len(g_row) and (
                    (g_row[x] >= threshold) if mode == "bright" else (g_row[x] <= threshold)
                ):
                    x += 1
                end = min(start + max_length, x)
                if end > start:
                    order = np.argsort(g_row[start:end])
                    if mode == "bright":
                        order = order[::-1]
                    row[start:end] = row[start:end][order]

        if angle != 0:
            M_back = cv2.getRotationMatrix2D(center, -angle, 1.0)
            working = cv2.warpAffine(
                working, M_back, (w, h), borderMode=cv2.BORDER_CONSTANT, borderValue=(0, 0, 0)
            )

        return working


class VHS(BaseEffect):
    """VHS tape degradation look."""

    kind = "vhs"

    def randomize(self, rng: random.Random):
        self.params["tracking"] = rng.uniform(0.1, 0.6)
        self.params["chroma_bleed"] = rng.uniform(1.0, 10.0)
        self.params["noise"] = rng.uniform(0.02, 0.2)
        self.params["scanlines"] = rng.uniform(0.1, 0.5)

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        tracking = self.animated_param("tracking", time)
        chroma_bleed = self.animated_param("chroma_bleed", time)
        noise_amount = self.animated_param("noise", time)
        scanlines = self.animated_param("scanlines", time)

        b, g, r = cv2.split(frame)
        shift = max(0, int(round(chroma_bleed)))
        if shift:
            M_b = np.float32([[1, 0, -shift], [0, 1, 0]])
            M_r = np.float32([[1, 0, shift], [0, 1, 0]])
            b = cv2.warpAffine(
                b, M_b, (frame.shape[1], frame.shape[0]), borderMode=cv2.BORDER_WRAP
            )
            r = cv2.warpAffine(
                r, M_r, (frame.shape[1], frame.shape[0]), borderMode=cv2.BORDER_WRAP
            )

        out = cv2.merge([b, g, r]).astype(np.float32)

        if scanlines > 0:
            out[1::2] *= 1.0 - scanlines

        if noise_amount > 0:
            noise = np.random.normal(0, noise_amount * 255, frame.shape).astype(np.float32)
            out += noise

        h = frame.shape[0]
        if tracking > 0 and np.random.rand() < tracking:
            rng = random.Random(int(time * 1000) + self.master_seed)
            band_h = rng.randint(2, 8)
            y = rng.randint(0, max(1, h - band_h))
            offset = rng.randint(-20, 20)
            out[y : y + band_h] = np.roll(out[y : y + band_h], offset, axis=1)

        return np.clip(out, 0, 255).astype(np.uint8)


class Dither(BaseEffect):
    """Ordered Bayer dither with optional grayscale palette."""

    kind = "dither"

    BAYER_4X4 = np.array(
        [[0, 8, 2, 10], [12, 4, 14, 6], [3, 11, 1, 9], [15, 7, 13, 5]],
        dtype=np.float32,
    ) / 16.0

    def randomize(self, rng: random.Random):
        self.params["bits"] = rng.randint(2, 5)
        self.params["type"] = rng.choice(["bayer", "floyd"])
        self.params["palette"] = rng.choice(["color", "grayscale"])

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        bits = int(self.animated_param("bits", time))
        palette = self.animated_param("palette", time)

        working = frame.copy()
        if palette == "grayscale":
            gray = cv2.cvtColor(working, cv2.COLOR_BGR2GRAY)
            working = np.stack([gray, gray, gray], axis=-1)

        levels = max(1, 2 ** bits)
        step = 256 // levels
        if step <= 0:
            step = 1

        h, w = working.shape[:2]
        bayer = np.tile(self.BAYER_4X4, ((h // 4) + 1, (w // 4) + 1))[:h, :w] * step
        bayer = bayer[..., np.newaxis]
        dithered = np.floor((working.astype(np.float32) + bayer) / step) * step
        return np.clip(dithered, 0, 255).astype(np.uint8)


class MotionGlitch(BaseEffect):
    """Distort moving areas using optical flow."""

    kind = "motion_glitch"
    stateful = True

    def __init__(self, settings: EffectSettings):
        super().__init__(settings)
        self._prev = None

    def randomize(self, rng: random.Random):
        self.params["flow_scale"] = rng.uniform(0.5, 3.0)
        self.params["threshold"] = rng.randint(5, 25)

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        if self._prev is None or self._prev.shape != frame.shape:
            self._prev = frame.copy()
            return frame

        prev_gray = cv2.cvtColor(self._prev, cv2.COLOR_BGR2GRAY)
        cur_gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        flow = cv2.calcOpticalFlowFarneback(
            prev_gray, cur_gray, None, 0.5, 3, 15, 3, 5, 1.2, 0
        )

        fx = flow[:, :, 0]
        fy = flow[:, :, 1]
        magnitude = np.sqrt(fx ** 2 + fy ** 2)
        mask = magnitude > int(self.animated_param("threshold", time))

        flow_scale = self.animated_param("flow_scale", time)
        h, w = frame.shape[:2]
        map_x, map_y = np.meshgrid(
            np.arange(w, dtype=np.float32), np.arange(h, dtype=np.float32)
        )
        map_x = map_x + fx * flow_scale
        map_y = map_y + fy * flow_scale

        warped = cv2.remap(frame, map_x, map_y, cv2.INTER_LINEAR)
        out = frame.copy()
        out[mask] = warped[mask]

        self._prev = frame.copy()
        return out


class MotionTrails(BaseEffect):
    """Frame-history trail composite."""

    kind = "motion_trails"
    stateful = True

    def __init__(self, settings: EffectSettings):
        super().__init__(settings)
        self._buffer = deque(maxlen=2)

    def randomize(self, rng: random.Random):
        self.params["length"] = rng.randint(4, 16)
        self.params["decay"] = rng.uniform(0.5, 0.9)
        self.params["blend"] = rng.choice(["normal", "screen", "add"])

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        length = max(2, int(self.animated_param("length", time)))
        decay = self.animated_param("decay", time)
        blend_mode = self.animated_param("blend", time)

        if self._buffer.maxlen != length:
            self._buffer = deque(self._buffer, maxlen=length)
        self._buffer.append(frame.copy())

        frames = list(self._buffer)
        if blend_mode == "normal":
            weights = [decay ** i for i in range(len(frames))]
            total = sum(weights)
            acc = np.zeros_like(frame, dtype=np.float32)
            for i, f in enumerate(frames):
                acc += f.astype(np.float32) * weights[i]
            out = acc / total if total else acc
        elif blend_mode == "screen":
            out = frames[0].astype(np.float32)
            for i in range(1, len(frames)):
                weight = decay ** i
                layer = frames[i].astype(np.float32) * weight
                out = 255.0 - (255.0 - out) * (255.0 - layer) / 255.0
        else:  # add
            acc = np.zeros_like(frame, dtype=np.float32)
            for i, f in enumerate(frames):
                acc += f.astype(np.float32) * (decay ** i)
            out = acc

        return np.clip(out, 0, 255).astype(np.uint8)


class TextOverlay(BaseEffect):
    """Render styled text over the frame with optional kinetic animation.

    The text glyph layer is rendered once and cached; only the transform,
    position and animation are recomputed each frame.
    """

    kind = "text_overlay"

    def __init__(self, settings):
        super().__init__(settings)
        self._cached_layer: Optional[np.ndarray] = None
        self._cached_meta: Optional[tuple] = None
        self._cache_key: Optional[tuple] = None
        self._last_bbox = None  # (x, y, w, h) of the last rendered text, output coords

    def randomize(self, rng: random.Random):
        pass

    def apply(self, frame: np.ndarray, time: float) -> np.ndarray:
        text = str(self.param("text", "ANTINOMY"))
        if not text.strip():
            self._last_bbox = None
            return frame

        font_family = str(self.param("font_family", "Arial"))
        font_size = int(self.param("font_size", 120))
        bold = bool(self.param("bold", True))
        italic = bool(self.param("italic", False))
        color = str(self.param("color", "#ffffff"))
        outline_color = str(self.param("outline_color", "#000000"))
        outline_thickness = int(self.param("outline_thickness", 4))
        shadow_color = str(self.param("shadow_color", "#000000"))
        shadow_offset_x = int(self.param("shadow_offset_x", 6))
        shadow_offset_y = int(self.param("shadow_offset_y", 6))
        position_x = float(self.param("position_x", 0.5))
        position_y = float(self.param("position_y", 0.5))
        offset_x = float(self.param("offset_x", 0.0))
        offset_y = float(self.param("offset_y", 0.0))
        scale_x = float(self.param("scale_x", 1.0))
        scale_y = float(self.param("scale_y", 1.0))
        anchor_h = str(self.param("anchor_h", "center"))
        anchor_v = str(self.param("anchor_v", "center"))
        animation = str(self.param("animation", "rotate"))
        depth = int(self.param("depth", 0))
        depth_color = str(self.param("depth_color", "#1a1a1a"))
        depth_angle = float(self.param("depth_angle", 45.0))
        # BPM is the global project tempo injected by build_pipeline. Fall back
        # to the legacy per-effect param only if it was not provided.
        project_bpm = getattr(self, "project_bpm", None)
        if project_bpm is not None:
            bpm = float(project_bpm)
        else:
            bpm = float(self.param("bpm", 120.0))
        bars = int(float(self.param("bars", 4)))
        jolt_beats = int(float(self.param("jolt_beats", 4)))

        h, w = frame.shape[:2]

        # Render (or reuse) the styled text layer via native Cocoa text drawing.
        cache_key = (
            text, font_family, font_size, bold, italic,
            color, outline_color, outline_thickness,
            shadow_color, shadow_offset_x, shadow_offset_y,
            depth, depth_color, round(depth_angle, 2),
        )
        if self._cache_key != cache_key or self._cached_layer is None:
            base_layer, base_meta = self._render_layer_cocoa(
                text, font_family, font_size, bold, italic,
                color, outline_color, outline_thickness,
                shadow_color, shadow_offset_x, shadow_offset_y,
            )
            if depth > 0:
                base_layer, base_meta = self._apply_extrude(
                    base_layer, base_meta, depth, depth_color, depth_angle
                )
            self._cached_layer, self._cached_meta = base_layer, base_meta
            self._cache_key = cache_key

        layer = self._cached_layer
        text_w, text_h, pad_l, pad_r, pad_t, pad_b = self._cached_meta
        layer_h, layer_w = layer.shape[:2]

        # Base anchor position with user offset.
        base_x = w * position_x + offset_x * w
        base_y = h * position_y + offset_y * h
        if anchor_h == "center":
            base_x -= text_w / 2.0
        elif anchor_h == "right":
            base_x -= text_w
        if anchor_v == "center":
            base_y -= text_h / 2.0
        elif anchor_v == "bottom":
            base_y -= text_h

        # Whole-text animation. The global animation toggle (animate_enabled)
        # masters every per-effect animation. The intensity slider applies only
        # to the visual effects (per-effect parameter jitter), NOT to the text
        # animation, so the text Jolt/Rotate always runs at full strength.
        if self.animate_enabled:
            anim = self._whole_text_animation(animation, time, bpm, bars, jolt_beats, 1.0)
        else:
            anim = {"tx": 0.0, "ty": 0.0, "scale_x": 1.0, "scale_y": 1.0, "rot": 0.0, "anchor_bottom": False}
        extra_scale_x = anim["scale_x"]
        extra_scale_y = anim["scale_y"]
        extra_tx = anim["tx"]
        extra_ty = anim["ty"]
        anchor_bottom = anim["anchor_bottom"]
        extra_rot = anim.get("rot", 0.0)
        extra_rot_y = anim.get("rot_y", 0.0)
        extra_rot_x = anim.get("rot_x", 0.0)

        final_scale_x = scale_x * extra_scale_x
        final_scale_y = scale_y * extra_scale_y

        # Anchor points inside the layer (content box).
        content_cx = pad_l + text_w / 2.0
        content_cy = pad_t + text_h / 2.0
        content_bx = content_cx
        content_by = pad_t + text_h

        # Target anchor point on the output frame.
        target_cx = base_x + text_w / 2.0 + extra_tx
        target_cy = base_y + text_h / 2.0 + extra_ty
        target_bx = target_cx
        target_by = base_y + text_h + extra_ty

        if anchor_bottom:
            origin_layer = (content_bx, content_by)
            origin_target = (target_bx, target_by)
        else:
            origin_layer = (content_cx, content_cy)
            origin_target = (target_cx, target_cy)

        # Negative scale means a horizontal flip (rotate/3D flip).
        flip_x = final_scale_x < 0
        eff_scale_x = abs(final_scale_x)
        eff_scale_y = max(0.001, abs(final_scale_y))

        dst_w = max(1, int(round(layer_w * eff_scale_x)))
        dst_h = max(1, int(round(layer_h * eff_scale_y)))

        if flip_x:
            scaled = cv2.resize(layer, (dst_w, dst_h), interpolation=cv2.INTER_LINEAR)
            scaled = cv2.flip(scaled, 1)
        else:
            scaled = cv2.resize(layer, (dst_w, dst_h), interpolation=cv2.INTER_LINEAR)

        # Center of the (scaled, unrotated) layer in output coordinates.
        center_x = origin_target[0] + (dst_w / 2.0 - origin_layer[0] * eff_scale_x)
        center_y = origin_target[1] + (dst_h / 2.0 - origin_layer[1] * eff_scale_y)

        # Optional whole-text rotation about the center. Expand the canvas so
        # the rotated corners are never clipped.
        if abs(extra_rot) > 0.01:
            sh0, sw0 = scaled.shape[:2]
            M = cv2.getRotationMatrix2D((sw0 / 2.0, sh0 / 2.0), extra_rot, 1.0)
            cos_a = abs(M[0, 0])
            sin_a = abs(M[0, 1])
            rot_w = int(round(sw0 * cos_a + sh0 * sin_a))
            rot_h = int(round(sw0 * sin_a + sh0 * cos_a))
            M[0, 2] += rot_w / 2.0 - sw0 / 2.0
            M[1, 2] += rot_h / 2.0 - sh0 / 2.0
            scaled = cv2.warpAffine(
                scaled, M, (rot_w, rot_h),
                flags=cv2.INTER_LINEAR,
                borderMode=cv2.BORDER_CONSTANT,
                borderValue=(0, 0, 0, 0),
            )
            out_w, out_h = rot_w, rot_h
        else:
            out_w, out_h = dst_w, dst_h

        # True 3D rotation about the text's own vertical axis (the "rotate"
        # animation). A perspective warp makes the text turn like a billboard
        # instead of spinning flat in the 2D image plane.
        if abs(extra_rot_y) > 0.01:
            scaled, out_w, out_h = self._perspective_rotate_y(scaled, extra_rot_y)

        # True 3D rotation about the text's own horizontal axis (tumble / float).
        if abs(extra_rot_x) > 0.01:
            scaled, out_w, out_h = self._perspective_rotate_x(scaled, extra_rot_x)

        # Top-left corner so the layer center matches the target center.
        dst_x = int(round(center_x - out_w / 2.0))
        dst_y = int(round(center_y - out_h / 2.0))

        # Remember the rendered bounding box (output coords) so the preview can
        # draw interactive selection handles around the text.
        self._last_bbox = (int(dst_x), int(dst_y), int(out_w), int(out_h))

        # Fast alpha composite with bounds clipping.
        return self._composite(frame, scaled, dst_x, dst_y)

    def _render_layer_cocoa(
        self,
        text: str,
        font_family: str,
        font_size: int,
        bold: bool,
        italic: bool,
        color: str,
        outline_color: str,
        outline_thickness: int,
        shadow_color: str,
        shadow_offset_x: int,
        shadow_offset_y: int,
    ):
        """Render the styled text into a straight-alpha BGRA layer using Cocoa.

        Returns (layer_bgra, (text_w, text_h, pad_l, pad_r, pad_t, pad_b)).
        """
        from Cocoa import (
            NSFont,
            NSFontManager,
            NSColor,
            NSAttributedString,
            NSBitmapImageRep,
            NSGraphicsContext,
            NSMakePoint,
            NSFontAttributeName,
            NSForegroundColorAttributeName,
            NSStrokeColorAttributeName,
            NSStrokeWidthAttributeName,
        )

        def _color(hexstr):
            r, g, b = _hex_to_rgb(hexstr)
            return NSColor.colorWithSRGBRed_green_blue_alpha_(r, g, b, 1.0)

        # Build the font with the requested traits.
        base = NSFont.fontWithName_size_(font_family, float(max(1, font_size)))
        if base is None:
            base = NSFont.systemFontOfSize_(float(max(1, font_size)))
        fm = NSFontManager.sharedFontManager()
        font = base
        if bold:
            font = fm.convertFont_toHaveTrait_(font, 2)   # NSBoldFontMask
        if italic:
            font = fm.convertFont_toHaveTrait_(font, 1)   # NSItalicFontMask

        fill = _color(color)

        # Measure the text.
        measure_attrs = {NSFontAttributeName: font, NSForegroundColorAttributeName: fill}
        astr = NSAttributedString.alloc().initWithString_attributes_(text, measure_attrs)
        size = astr.size()
        text_w = max(1, int(math.ceil(size.width)))
        text_h = max(1, int(math.ceil(size.height)))

        pad_l = max(outline_thickness, -shadow_offset_x, 0) + 4
        pad_r = max(outline_thickness, shadow_offset_x, 0) + 4
        pad_t = max(outline_thickness, -shadow_offset_y, 0) + 4
        pad_b = max(outline_thickness, shadow_offset_y, 0) + 4
        layer_w = pad_l + text_w + pad_r
        layer_h = pad_t + text_h + pad_b

        # Core Graphics bitmap contexts only support premultiplied alpha. A
        # non-premultiplied rep makes graphicsContextWithBitmapImageRep_ return
        # nil, so nothing draws (this was why the text never appeared). Render
        # premultiplied, then un-premultiply on readback below.
        rep = NSBitmapImageRep.alloc().initWithBitmapDataPlanes_pixelsWide_pixelsHigh_bitsPerSample_samplesPerPixel_hasAlpha_isPlanar_colorSpaceName_bitmapFormat_bytesPerRow_bitsPerPixel_(
            None, layer_w, layer_h, 8, 4, True, False,
            "NSCalibratedRGBColorSpace", 0, layer_w * 4, 32,
        )  # bitmapFormat 0 = premultiplied alpha (required for CG drawing)

        ctx = NSGraphicsContext.graphicsContextWithBitmapImageRep_(rep)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.setCurrentContext_(ctx)

        # Cocoa bitmap contexts use a bottom-left origin. Drawing the text at
        # (pad_l, pad_b) leaves symmetric padding; a positive shadow offset Y
        # means "down" on screen, i.e. toward smaller drawing-space y.
        draw_x = pad_l
        draw_y = pad_b

        if shadow_offset_x or shadow_offset_y:
            shadow_attrs = {
                NSFontAttributeName: font,
                NSForegroundColorAttributeName: _color(shadow_color),
            }
            sh = NSAttributedString.alloc().initWithString_attributes_(text, shadow_attrs)
            sh.drawAtPoint_(NSMakePoint(draw_x + shadow_offset_x, draw_y - shadow_offset_y))

        if outline_thickness > 0:
            stroke_w = -abs(outline_thickness) / float(max(1, font_size)) * 100.0
            outline_attrs = {
                NSFontAttributeName: font,
                NSForegroundColorAttributeName: fill,
                NSStrokeColorAttributeName: _color(outline_color),
                NSStrokeWidthAttributeName: stroke_w,
            }
            ostr = NSAttributedString.alloc().initWithString_attributes_(text, outline_attrs)
            ostr.drawAtPoint_(NSMakePoint(draw_x, draw_y))
        else:
            astr.drawAtPoint_(NSMakePoint(draw_x, draw_y))

        NSGraphicsContext.restoreGraphicsState()

        # Pull the pixels back out of the bitmap representation (premultiplied RGBA).
        bpr = rep.bytesPerRow()
        raw = np.frombuffer(rep.bitmapData(), dtype=np.uint8, count=layer_h * bpr)
        raw = raw.reshape(layer_h, bpr)[:, : layer_w * 4].reshape(layer_h, layer_w, 4)

        # Un-premultiply to straight alpha so the compositor blends correctly.
        rgba = raw.astype(np.float32)
        alpha = rgba[:, :, 3:4] / 255.0
        with np.errstate(divide="ignore", invalid="ignore"):
            straight = np.where(alpha > 0, rgba[:, :, :3] / alpha, 0.0)
        straight = np.clip(straight, 0, 255)

        # Cocoa gives RGBA; the compositor expects BGRA to match BGR frames.
        bgra = np.empty((layer_h, layer_w, 4), dtype=np.uint8)
        bgra[:, :, 0] = straight[:, :, 2].astype(np.uint8)
        bgra[:, :, 1] = straight[:, :, 1].astype(np.uint8)
        bgra[:, :, 2] = straight[:, :, 0].astype(np.uint8)
        bgra[:, :, 3] = raw[:, :, 3]
        return bgra, (text_w, text_h, pad_l, pad_r, pad_t, pad_b)

    def _composite(self, frame: np.ndarray, layer: np.ndarray, x: int, y: int) -> np.ndarray:
        fh, fw = frame.shape[:2]
        lh, lw = layer.shape[:2]

        x1, y1 = max(0, x), max(0, y)
        x2, y2 = min(fw, x + lw), min(fh, y + lh)
        if x1 >= x2 or y1 >= y2:
            return frame

        src_x = x1 - x
        src_y = y1 - y
        roi_frame = frame[y1:y2, x1:x2]
        roi_layer = layer[src_y:src_y + (y2 - y1), src_x:src_x + (x2 - x1)]

        alpha = roi_layer[:, :, 3:4].astype(np.float32, copy=False) * (1.0 / 255.0)
        text_bgr = roi_layer[:, :, :3].astype(np.float32, copy=False)

        # In-place blend into a float buffer to avoid another allocation.
        blended = roi_frame.astype(np.float32, copy=False)
        blended *= (1.0 - alpha)
        blended += text_bgr * alpha
        np.clip(blended, 0, 255, out=blended)
        frame[y1:y2, x1:x2] = blended.astype(np.uint8, copy=False)
        return frame

    def _smoothstep(self, t: float) -> float:
        t = max(0.0, min(1.0, t))
        return t * t * (3.0 - 2.0 * t)

    def _perspective_rotate_y(self, layer, angle_deg):
        """Rotate a BGRA layer about its own vertical axis in 3D.

        Uses a perspective warp so the text turns like a billboard (with real
        foreshortening) instead of spinning flat in the image plane. The text's
        centre is kept at the centre of the returned canvas so the existing
        centre-based compositing keeps it positioned correctly.
        """
        h, w = layer.shape[:2]
        theta = math.radians(angle_deg)
        cos_t = math.cos(theta)
        sin_t = math.sin(theta)
        if abs(cos_t) < 0.02:
            # Edge-on: the text is essentially invisible. Return a tiny
            # transparent layer to avoid a degenerate perspective transform.
            empty = np.zeros((1, 1, layer.shape[2]), dtype=layer.dtype)
            return empty, 1, 1
        # Focal length controls how strong the perspective is (larger = flatter).
        f = max(w, h) * 1.6
        half_w = w / 2.0
        half_h = h / 2.0
        local = [(-half_w, -half_h), (half_w, -half_h), (half_w, half_h), (-half_w, half_h)]
        proj = []
        for (x, y) in local:
            depth = x * sin_t
            denom = f + depth
            if denom < 1e-3:
                denom = 1e-3
            s = f / denom
            proj.append(((x * cos_t) * s, y * s))
        half_out_w = max(abs(p[0]) for p in proj)
        half_out_h = max(abs(p[1]) for p in proj)
        out_w = max(1, int(math.ceil(2.0 * half_out_w)))
        out_h = max(1, int(math.ceil(2.0 * half_out_h)))
        cx = out_w / 2.0
        cy = out_h / 2.0
        src = np.float32([[0.0, 0.0], [w, 0.0], [w, h], [0.0, h]])
        dst = np.float32([[p[0] + cx, p[1] + cy] for p in proj])
        M = cv2.getPerspectiveTransform(src, dst)
        warped = cv2.warpPerspective(
            layer, M, (out_w, out_h),
            flags=cv2.INTER_LINEAR,
            borderMode=cv2.BORDER_CONSTANT,
            borderValue=(0, 0, 0, 0),
        )
        return warped, out_w, out_h

    def _perspective_rotate_x(self, layer, angle_deg):
        """Rotate a BGRA layer about its own horizontal axis in 3D.

        The mirror of :meth:`_perspective_rotate_y`: the top and bottom edges
        recede with real foreshortening so the text tumbles forward/back like a
        flap instead of spinning flat. The text centre stays at the centre of
        the returned canvas so centre-based compositing keeps it positioned.
        """
        h, w = layer.shape[:2]
        theta = math.radians(angle_deg)
        cos_t = math.cos(theta)
        sin_t = math.sin(theta)
        if abs(cos_t) < 0.02:
            # Edge-on: essentially invisible. Avoid a degenerate transform.
            empty = np.zeros((1, 1, layer.shape[2]), dtype=layer.dtype)
            return empty, 1, 1
        f = max(w, h) * 1.6
        half_w = w / 2.0
        half_h = h / 2.0
        local = [(-half_w, -half_h), (half_w, -half_h), (half_w, half_h), (-half_w, half_h)]
        proj = []
        for (x, y) in local:
            depth = y * sin_t
            denom = f + depth
            if denom < 1e-3:
                denom = 1e-3
            s = f / denom
            proj.append((x * s, (y * cos_t) * s))
        half_out_w = max(abs(p[0]) for p in proj)
        half_out_h = max(abs(p[1]) for p in proj)
        out_w = max(1, int(math.ceil(2.0 * half_out_w)))
        out_h = max(1, int(math.ceil(2.0 * half_out_h)))
        cx = out_w / 2.0
        cy = out_h / 2.0
        src = np.float32([[0.0, 0.0], [w, 0.0], [w, h], [0.0, h]])
        dst = np.float32([[p[0] + cx, p[1] + cy] for p in proj])
        M = cv2.getPerspectiveTransform(src, dst)
        warped = cv2.warpPerspective(
            layer, M, (out_w, out_h),
            flags=cv2.INTER_LINEAR,
            borderMode=cv2.BORDER_CONSTANT,
            borderValue=(0, 0, 0, 0),
        )
        return warped, out_w, out_h

    def _apply_extrude(self, layer, meta, depth, depth_color_hex, angle_deg):
        """Bake a solid 3D extrusion behind the glyphs.

        Stacks ``depth`` darkening silhouettes of the text, each stepped one
        pixel along ``angle_deg`` (0 deg = right, 90 deg = down), then composites
        the styled text on top. Returns ``(layer_bgra, meta)`` with the padding
        in ``meta`` widened so the existing anchor maths still point at the real
        text centre.
        """
        text_w, text_h, pad_l, pad_r, pad_t, pad_b = meta
        lh, lw = layer.shape[:2]
        rad = math.radians(angle_deg)
        dx = math.cos(rad)
        dy = math.sin(rad)
        total_ox = depth * dx
        total_oy = depth * dy
        left = max(0, int(math.ceil(-total_ox)))
        right = max(0, int(math.ceil(total_ox)))
        top = max(0, int(math.ceil(-total_oy)))
        bottom = max(0, int(math.ceil(total_oy)))
        new_w = lw + left + right
        new_h = lh + top + bottom
        main_x = left
        main_y = top

        # Straight-alpha float canvas: rgb in 0..255, alpha in 0..1.
        canvas = np.zeros((new_h, new_w, 4), dtype=np.float32)
        r, g, b = _hex_to_rgb(depth_color_hex)
        base_bgr = np.array([b * 255.0, g * 255.0, r * 255.0], dtype=np.float32)
        src_alpha = layer[:, :, 3].astype(np.float32) / 255.0

        def _over(x, y, a_full, rgb_src):
            sh, sw = a_full.shape
            x0 = max(0, x)
            y0 = max(0, y)
            x1 = min(new_w, x + sw)
            y1 = min(new_h, y + sh)
            if x1 <= x0 or y1 <= y0:
                return
            sx0 = x0 - x
            sy0 = y0 - y
            a = a_full[sy0:sy0 + (y1 - y0), sx0:sx0 + (x1 - x0), None]
            if isinstance(rgb_src, np.ndarray) and rgb_src.ndim == 3:
                s_rgb = rgb_src[sy0:sy0 + (y1 - y0), sx0:sx0 + (x1 - x0), :]
            else:
                s_rgb = rgb_src.reshape(1, 1, 3)
            reg = canvas[y0:y1, x0:x1, :]
            d_rgb = reg[:, :, :3]
            d_a = reg[:, :, 3:4]
            out_a = a + d_a * (1.0 - a)
            safe = np.where(out_a > 1e-6, out_a, 1.0)
            reg[:, :, :3] = (s_rgb * a + d_rgb * d_a * (1.0 - a)) / safe
            reg[:, :, 3:4] = out_a

        # Back-to-front: farthest copy first (darkest), nearest last (brightest).
        for i in range(depth, 0, -1):
            t = 0.0 if depth <= 1 else (i - 1) / float(depth - 1)
            bright = 1.0 - 0.55 * t
            ox = int(round(main_x + i * dx))
            oy = int(round(main_y + i * dy))
            _over(ox, oy, src_alpha, base_bgr * bright)

        # The styled text sits on top of the extrusion.
        _over(main_x, main_y, src_alpha, layer[:, :, :3].astype(np.float32))

        out = np.empty((new_h, new_w, 4), dtype=np.uint8)
        out[:, :, :3] = np.clip(canvas[:, :, :3], 0, 255).astype(np.uint8)
        out[:, :, 3] = np.clip(canvas[:, :, 3] * 255.0, 0, 255).astype(np.uint8)
        new_meta = (text_w, text_h, pad_l + main_x, pad_r + right, pad_t + main_y, pad_b + bottom)
        return out, new_meta

    def _whole_text_animation(
        self,
        animation: str,
        time: float,
        bpm: float,
        bars: int,
        jolt_beats: int,
        intensity: float = 1.0,
    ) -> dict:
        result = {"tx": 0.0, "ty": 0.0, "scale_x": 1.0, "scale_y": 1.0, "rot": 0.0, "anchor_bottom": False}

        # Everything is locked to musical time, never to the clip length, so the
        # animation speed is fully determined by the BPM the user dials in.
        bps = max(0.01, bpm / 60.0)  # beats per second

        if animation == "rotate":
            # One full 360 deg turn spread evenly across `bars` musical bars.
            # A bar is 4 beats, so its real length depends on the BPM
            # (seconds per bar = 4 * 60 / bpm = 240 / bpm). A faster BPM means
            # shorter bars and a faster spin, exactly as dialed in. The angle is
            # reported as a 3D rotation about the text's own vertical axis, and
            # since 360 deg == 0 deg the spin loops seamlessly.
            n_bars = max(1, int(bars))
            seconds_per_bar = 240.0 / max(0.01, bpm)
            period = n_bars * seconds_per_bar
            result["rot_y"] = 360.0 * (time / period)
            return result

        if animation == "swing":
            # Pendulum turn about the vertical axis: the text rocks left/right
            # like a sign on a hinge. One full back-and-forth per `bars` span.
            n_bars = max(1, int(bars))
            seconds_per_bar = 240.0 / max(0.01, bpm)
            period = n_bars * seconds_per_bar
            result["rot_y"] = 55.0 * math.sin(2.0 * math.pi * (time / period))
            return result

        if animation == "tumble":
            # Continuous forward flip about the horizontal axis (cards turning).
            n_bars = max(1, int(bars))
            seconds_per_bar = 240.0 / max(0.01, bpm)
            period = n_bars * seconds_per_bar
            result["rot_x"] = 360.0 * (time / period)
            return result

        if animation == "float3d":
            # Gentle 3D drift: combined pitch + yaw wobble with a vertical bob,
            # all sharing one musical period so the loop is seamless.
            n_bars = max(1, int(bars))
            seconds_per_bar = 240.0 / max(0.01, bpm)
            period = n_bars * seconds_per_bar
            ph = 2.0 * math.pi * (time / period)
            result["rot_y"] = 18.0 * math.sin(ph)
            result["rot_x"] = 11.0 * math.sin(2.0 * ph + 1.0)
            result["ty"] = 10.0 * math.sin(ph)
            return result

        if animation == "jolt":
            # Jerky, but built so it returns to the identity transform at every
            # beat boundary -> a clip spanning a whole number of beats loops with
            # no visible jump. `jolt_beats` sets the seamless loop unit and adds
            # a slow swell across that span.
            beats = max(1, int(jolt_beats))
            bt = time * bps                          # beats elapsed (float)
            beat_frac = bt - math.floor(bt)          # 0..1 within the current beat
            btri = 1.0 - abs(2.0 * beat_frac - 1.0)  # 0 -> 1 -> 0 each beat
            loop_pos = (bt / beats) - math.floor(bt / beats)
            ltri = 1.0 - abs(2.0 * loop_pos - 1.0)   # slow swell across the loop
            ph = 2.0 * math.pi * bt                  # whole 2pi per beat -> seamless

            amp = max(0.0, intensity) * (0.6 + 0.4 * ltri)
            wstep = math.floor(btri * 6.0) / 6.0
            rstep = math.floor(btri * 4.0) / 4.0
            result["scale_x"] = 1.0 + amp * (0.5 * wstep)
            result["scale_y"] = 1.0 + amp * (0.12 * math.sin(ph * 2.0))
            result["rot"] = amp * (26.0 * rstep)
            result["tx"] = amp * (24.0 * math.sin(ph))
            return result

        return result

EFFECT_REGISTRY = {
    cls.kind: cls
    for cls in [
        ColorGrade,
        Posterize,
        EdgeGlow,
        ChromaticAberration,
        Noise,
        Sharpen,
        GlitchBlocks,
        Scanlines,
        ColorInvert,
        Vignette,
        ColorMap,
        Datamosh,
        PixelSort,
        VHS,
        Dither,
        MotionGlitch,
        MotionTrails,
        TextOverlay,
    ]
}


def build_pipeline(
    effect_settings: List[EffectSettings],
    animate_params: bool = False,
    animation_amount: int = 0,
    master_seed: int = 1,
    bpm: Optional[float] = None,
) -> List[BaseEffect]:
    pipeline = []
    for es in effect_settings:
        if not es.enabled:
            continue
        cls = EFFECT_REGISTRY.get(es.kind)
        if cls:
            effect = cls(es)
            effect.animate_enabled = animate_params and getattr(es, "animate", True)
            effect.animate_amount = animation_amount
            effect.master_seed = master_seed
            # The single global project BPM drives the text rotation/jolt AND
            # the optional per-effect beat trigger, so every effect receives it.
            if bpm is not None:
                effect.project_bpm = float(bpm)
            effect.beat_sync = bool(getattr(es, "beat_sync", False))
            try:
                effect.beat_unit = float(getattr(es, "beat_unit", 1.0))
            except (TypeError, ValueError):
                effect.beat_unit = 1.0
            pipeline.append(effect)
    return pipeline


def _is_text_overlay(es: EffectSettings) -> bool:
    return es.kind == "text_overlay"


def _is_locked(es: EffectSettings) -> bool:
    """Effects excluded from shuffle: text overlay and user-locked effects."""
    return _is_text_overlay(es) or getattr(es, "lock_random", False)


def randomize_all(
    effect_settings: List[EffectSettings],
    master_seed: int,
    randomize_order: bool = False,
):
    rng = random.Random(master_seed)
    if randomize_order:
        # Locked effects (and text overlay) keep their slot; the rest get shuffled.
        fixed_indices = {i for i, es in enumerate(effect_settings) if _is_locked(es)}
        others = [es for i, es in enumerate(effect_settings) if i not in fixed_indices]
        rng.shuffle(others)
        new_list = []
        it = iter(others)
        for i in range(len(effect_settings)):
            if i in fixed_indices:
                new_list.append(effect_settings[i])
            else:
                new_list.append(next(it))
        effect_settings[:] = new_list

    for es in effect_settings:
        if _is_locked(es):
            continue
        cls = EFFECT_REGISTRY.get(es.kind)
        if cls:
            effect = cls(es)
            effect.randomize(rng)
            es.params = effect.params
            for p in schema_for(es.kind):
                if p.name not in es.params:
                    es.params[p.name] = p.default


def randomize_one(es: EffectSettings, master_seed: int, offset: int, counter: int = 0):
    rng = random.Random(master_seed + offset + counter * 7919)
    cls = EFFECT_REGISTRY.get(es.kind)
    if cls:
        effect = cls(es)
        effect.randomize(rng)
        es.params = effect.params
        for p in schema_for(es.kind):
            if p.name not in es.params:
                es.params[p.name] = p.default


def _beat_gate_value(time: float, bpm, beat_unit: float) -> float:
    """0..1 envelope that snaps to 1 on each beat division and decays to ~0
    before the next, locked to the global project BPM. ``beat_unit`` is the
    number of beats between pulses (0.5 = twice per beat, 4 = once per bar in
    4/4). Used by the optional per-effect beat trigger."""
    try:
        b = float(bpm) if bpm else 120.0
    except (TypeError, ValueError):
        b = 120.0
    if b <= 0:
        b = 120.0
    try:
        unit = float(beat_unit)
    except (TypeError, ValueError):
        unit = 1.0
    unit = max(0.0625, unit)
    period = (60.0 / b) * unit
    if period <= 0:
        return 1.0
    p = (float(time) % period) / period  # 0 at the hit -> 1 just before next
    gate = (1.0 - p) ** 2                 # sharp attack, smooth decay
    return max(0.0, min(1.0, gate))


def apply_pipeline(
    frame: np.ndarray,
    pipeline: List[BaseEffect],
    time: float,
    audio_gain: float = 1.0,
) -> np.ndarray:
    for effect in pipeline:
        # Text overlay is intentionally exempt from global intensity and
        # audio-reactive scaling so the typography stays stable.
        is_text = effect.kind == "text_overlay"
        gain = 1.0 if is_text else audio_gain
        effect.audio_gain = gain
        out = effect.apply(frame, time)
        # When the audio is quiet (gain < 1) blend the effect back toward the
        # untouched frame so it visibly recedes; loud passages (gain >= 1) keep
        # the full effect while the boosted gain also drives stronger per-param
        # animation. This makes the reaction obvious even when per-effect
        # animation is turned off.
        if not is_text and gain < 0.999 and out is not frame:
            wet = max(0.0, min(1.0, gain))
            if out.shape == frame.shape and out.dtype == frame.dtype:
                out = cv2.addWeighted(out, wet, frame, 1.0 - wet, 0.0)
        # Optional per-effect beat trigger: pulse the effect in on each beat
        # division and let it recede before the next, locked to the global BPM.
        if getattr(effect, "beat_sync", False) and out is not frame:
            g = _beat_gate_value(
                time,
                getattr(effect, "project_bpm", None),
                getattr(effect, "beat_unit", 1.0),
            )
            if g < 0.999 and out.shape == frame.shape and out.dtype == frame.dtype:
                out = cv2.addWeighted(out, g, frame, 1.0 - g, 0.0)
        frame = out
    return frame


REACTION_MODES = ("opacity", "pulse", "shake", "flash", "rgbsplit", "strength")


def apply_audio_reaction(
    clean: np.ndarray,
    fx: np.ndarray,
    value: float,
    intensity: float,
    mode: str,
) -> np.ndarray:
    """Modulate the composited frame by the instantaneous audio loudness using a
    chosen, visually distinct reaction mode.

    ``clean`` is the transformed, effect-free frame; ``fx`` is the fully
    processed frame; ``value`` is the 0..1 loudness at this instant and
    ``intensity`` (0..1) controls how dramatic the reaction is. The aim is a
    reaction that an onlooker can obviously see pulsing with the music, unlike
    the subtle per-effect 'strength' recede.
    """
    v = max(0.0, min(1.0, float(value)))
    k = max(0.0, min(1.0, float(intensity)))
    # 'strength' reacts inside apply_pipeline; nothing to post-process here.
    if k <= 1e-6 or mode == "strength":
        return fx
    h, w = fx.shape[:2]

    if mode == "opacity":
        # The whole effect stack fades toward the raw video in quiet moments
        # and snaps to full strength on peaks. Very obvious on a loud track.
        wet = 1.0 - k * (1.0 - v)
        wet = max(0.0, min(1.0, wet))
        if wet >= 0.999:
            return fx
        return cv2.addWeighted(fx, wet, clean, 1.0 - wet, 0.0)

    if mode == "pulse":
        # Zoom punch on the beat.
        z = 1.0 + 0.35 * k * v
        if z <= 1.0001:
            return fx
        M = cv2.getRotationMatrix2D((w / 2.0, h / 2.0), 0.0, z)
        return cv2.warpAffine(
            fx, M, (w, h), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REFLECT
        )

    if mode == "shake":
        # Camera-shake jolt whose size tracks the loudness; direction wanders
        # with the envelope so sustained loudness still jitters.
        amp = 0.08 * k * v * min(h, w)
        if amp < 0.5:
            return fx
        import math
        ang = (v * 53.0 + k * 17.0) * 6.28318
        dx = int(round(math.cos(ang) * amp))
        dy = int(round(math.sin(ang) * amp))
        M = np.float32([[1.0, 0.0, dx], [0.0, 1.0, dy]])
        return cv2.warpAffine(
            fx, M, (w, h), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REFLECT
        )

    if mode == "flash":
        # Exposure pump: the image brightens hard on peaks.
        factor = 1.0 + 1.2 * k * v
        if factor <= 1.0001:
            return fx
        return cv2.convertScaleAbs(fx, alpha=factor, beta=0.0)

    if mode == "rgbsplit":
        # Chromatic split that widens with loudness (music-video look).
        shift = int(round(0.03 * k * v * w))
        if shift <= 0:
            return fx
        b, g, r = cv2.split(fx)
        r = np.roll(r, shift, axis=1)
        b = np.roll(b, -shift, axis=1)
        return cv2.merge([b, g, r])

    return fx
