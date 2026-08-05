using System;
using System.Threading;
using OpenCvSharp;

namespace GlitchFX.Video
{
    public class VideoInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
        public int FrameCount { get; set; }
        public double Duration { get; set; }
    }

    /// <summary>
    /// Threaded video reader mirroring Python's video_reader_native.py:
    /// supports play/pause and seek, and pushes decoded frames to a callback
    /// on a background thread so the UI stays responsive.
    /// </summary>
    public class VideoReader : IDisposable
    {
        private VideoCapture? _capture;
        private Thread? _thread;
        private volatile bool _running;
        private volatile bool _playing;
        private volatile double _seekRequestSeconds = -1;
        private readonly object _lock = new();

        public VideoInfo? Info { get; private set; }
        public double CurrentTime { get; private set; }
        public event Action<Mat, double>? FrameReady;
        public event Action? PlaybackEnded;

        public bool Load(string path)
        {
            Stop();
            _capture = new VideoCapture(path);
            if (!_capture.IsOpened()) return false;
            double fps = _capture.Fps > 0 ? _capture.Fps : 30.0;
            int frameCount = _capture.FrameCount;
            Info = new VideoInfo
            {
                Width = _capture.FrameWidth,
                Height = _capture.FrameHeight,
                Fps = fps,
                FrameCount = frameCount,
                Duration = frameCount / fps,
            };
            CurrentTime = 0;
            _running = true;
            _thread = new Thread(RunLoop) { IsBackground = true, Name = "GlitchFX-VideoReader" };
            _thread.Start();
            return true;
        }

        public void Play() => _playing = true;
        public void Pause() => _playing = false;
        public void Seek(double seconds) => _seekRequestSeconds = Math.Max(0, seconds);

        private void RunLoop()
        {
            if (_capture == null || Info == null) return;
            double frameInterval = 1.0 / Info.Fps;
            while (_running)
            {
                double seekTo;
                lock (_lock) { seekTo = _seekRequestSeconds; _seekRequestSeconds = -1; }
                if (seekTo >= 0)
                {
                    _capture.Set(VideoCaptureProperties.PosMsec, seekTo * 1000.0);
                    CurrentTime = seekTo;
                    using var frame = new Mat();
                    if (_capture.Read(frame) && !frame.Empty())
                        FrameReady?.Invoke(frame.Clone(), CurrentTime);
                    if (!_playing) { Thread.Sleep(15); continue; }
                }

                if (!_playing) { Thread.Sleep(15); continue; }

                using var next = new Mat();
                if (!_capture.Read(next) || next.Empty())
                {
                    _playing = false;
                    PlaybackEnded?.Invoke();
                    continue;
                }
                CurrentTime = _capture.Get(VideoCaptureProperties.PosMsec) / 1000.0;
                FrameReady?.Invoke(next.Clone(), CurrentTime);
                Thread.Sleep((int)Math.Max(1, frameInterval * 1000));
            }
        }

        public void Stop()
        {
            _running = false;
            _thread?.Join(500);
            _thread = null;
            _capture?.Dispose();
            _capture = null;
        }

        public void Dispose() => Stop();
    }
}
