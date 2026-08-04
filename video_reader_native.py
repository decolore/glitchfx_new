"""Video frame reader using plain Python threading (no Qt signals)."""
from __future__ import annotations

import threading
import time
from pathlib import Path
from typing import Callable, Optional

import cv2
import numpy as np


class VideoInfo:
    def __init__(self, path: str):
        self.path = path
        self.cap: Optional[cv2.VideoCapture] = None
        self.width = 0
        self.height = 0
        self.fps = 30.0
        self.frame_count = 0
        self.duration = 0.0
        self.__open()

    def __open(self):
        if not Path(self.path).exists():
            raise FileNotFoundError(f"Video not found: {self.path}")
        self.cap = cv2.VideoCapture(self.path)
        if not self.cap.isOpened():
            raise RuntimeError(f"Cannot open video: {self.path}")
        self.width = int(self.cap.get(cv2.CAP_PROP_FRAME_WIDTH))
        self.height = int(self.cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
        self.fps = self.cap.get(cv2.CAP_PROP_FPS) or 30.0
        self.frame_count = int(self.cap.get(cv2.CAP_PROP_FRAME_COUNT))
        self.duration = self.frame_count / self.fps if self.fps > 0 else 0.0

    def release(self):
        if self.cap:
            self.cap.release()
            self.cap = None

    def read_at_time(self, time: float) -> Optional[np.ndarray]:
        if not self.cap or not self.cap.isOpened():
            self.__open()
        frame_idx = int(round(np.clip(time, 0, self.duration) * self.fps))
        self.cap.set(cv2.CAP_PROP_POS_FRAMES, frame_idx)
        ok, frame = self.cap.read()
        if not ok or frame is None:
            return None
        return frame

    def read_frame(self) -> Optional[np.ndarray]:
        if not self.cap or not self.cap.isOpened():
            self.__open()
        ok, frame = self.cap.read()
        if not ok:
            return None
        return frame


class VideoReader:
    """Threaded reader that calls back with (frame, time) when a frame is ready.

    Callbacks are invoked from the reader thread. If the consumer needs to touch
    AppKit UI, it should re-dispatch to the main thread itself.
    """

    def __init__(
        self,
        on_frame: Optional[Callable[[np.ndarray, float], None]] = None,
        on_error: Optional[Callable[[str], None]] = None,
    ):
        self._path = ""
        self._on_frame = on_frame
        self._on_error = on_error
        self._lock = threading.Lock()
        self._pending_time: Optional[float] = None
        self.__running = False
        self._thread: Optional[threading.Thread] = None
        self._info: Optional[VideoInfo] = None
        self._playing = False
        self._play_idx = 0

    def set_source(self, path: str):
        with self._lock:
            self._path = path
            self._info = None

    def request_time(self, time: float):
        with self._lock:
            self._pending_time = time

    def play(self, from_time: Optional[float] = None):
        with self._lock:
            if from_time is not None:
                self._pending_time = from_time
            self._playing = True

    def pause(self):
        with self._lock:
            self._playing = False

    def is_playing(self) -> bool:
        with self._lock:
            return self._playing

    def info(self) -> Optional[VideoInfo]:
        with self._lock:
            return self._info

    def start(self):
        if self.__running:
            return
        self.__running = True
        self._thread = threading.Thread(target=self.__run, name="video-reader", daemon=True)
        self._thread.start()

    def stop(self):
        self.__running = False
        if self._thread:
            self._thread.join(timeout=1.0)
            self._thread = None

    def __run(self):
        info: Optional[VideoInfo] = None
        next_emit = 0.0
        while self.__running:
            time.sleep(0.003)
            with self._lock:
                path = self._path
                pending = self._pending_time
                self._pending_time = None
                playing = self._playing

            try:
                if info is None or info.path != path:
                    if info:
                        info.release()
                    info = VideoInfo(path) if path else None
                    with self._lock:
                        self._info = info

                if info is None:
                    continue

                fps = info.fps if info.fps > 0 else 30.0
                frame_dt = 1.0 / fps

                # A scrub/seek request takes priority (paused or playing).
                if pending is not None:
                    idx = int(round(np.clip(pending, 0, info.duration) * fps))
                    info.cap.set(cv2.CAP_PROP_POS_FRAMES, idx)
                    ok, frame = info.cap.read()
                    if frame is not None and self._on_frame:
                        self._on_frame(frame, idx / fps)
                    with self._lock:
                        self._play_idx = idx + 1
                    next_emit = time.time() + frame_dt
                    continue

                if not playing:
                    continue

                # Real-time playback: emit sequential frames at the video fps.
                now = time.time()
                if now < next_emit:
                    continue
                ok, frame = info.cap.read()
                if not ok or frame is None:
                    with self._lock:
                        self._playing = False  # reached the end -> stop
                    continue
                with self._lock:
                    t = self._play_idx / fps
                    self._play_idx += 1
                if self._on_frame:
                    self._on_frame(frame, t)
                next_emit = now + frame_dt
            except Exception as e:
                if self._on_error:
                    self._on_error(str(e))
