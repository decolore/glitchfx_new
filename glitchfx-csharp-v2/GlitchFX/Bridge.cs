using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using GlitchFX.Audio;
using GlitchFX.Effects;
using GlitchFX.Models;
using GlitchFX.Video;

namespace GlitchFX
{
    /// <summary>
    /// Mirrors Python's bridge.py: central app state connecting the video
    /// reader, the effect pipeline, and the UI. Renders preview frames as
    /// WPF BitmapSource (the analogue of the macOS NSImage conversion),
    /// tracks undo/redo history, and applies settings mutations coming from
    /// the effects/output panels.
    /// </summary>
    public class Bridge
    {
        public ProjectSettings Project { get; private set; } = ProjectFactory.DefaultProject();
        public readonly VideoReader Reader = new();

        private List<BaseEffect> _pipeline = new();
        private readonly List<ProjectSettings> _undoStack = new();
        private readonly List<ProjectSettings> _redoStack = new();
        private float[]? _audioEnvelope;
        private readonly Random _rng = new();

        public event Action<BitmapSource, double>? PreviewFrameReady;

        public Bridge()
        {
            Reader.FrameReady += OnFrameReady;
            RebuildPipeline();
        }

        public bool LoadVideo(string path)
        {
            bool ok = Reader.Load(path);
            if (ok)
            {
                Project.SourcePath = path;
                foreach (var e in _pipeline) e.ResetState();
            }
            return ok;
        }

        public bool LoadAudio(string path)
        {
            try
            {
                var samples = AudioAnalysis.DecodeMono(path);
                _audioEnvelope = AudioAnalysis.ComputeReactionEnvelope(Project.ReactionSource, samples, Project.Bpm);
                Project.AudioPath = path;
                return true;
            }
            catch { return false; }
        }

        public void Play() => Reader.Play();
        public void Pause() => Reader.Pause();
        public void Seek(double seconds) => Reader.Seek(seconds);

        public void RebuildPipeline()
        {
            _pipeline = Pipeline.BuildPipeline(Project);
        }

        private void OnFrameReady(Mat frame, double time)
        {
            // The reader hands us a cloned frame we own; make sure it (and
            // every intermediate Mat we create from it) gets disposed exactly
            // once, even though `ApplyAudioReaction` sometimes returns the
            // same Mat instance it was given instead of a fresh clone.
            using (frame)
            {
                using var resized = ResizeForPreview(frame, Project.Transform);
                using var processed = Pipeline.ApplyPipeline(_pipeline, resized, time, CurrentAudioGain(time));

                Mat final = processed;
                Mat? reactedOwned = null;
                if (Project.AudioReactive)
                {
                    reactedOwned = Pipeline.ApplyAudioReaction(processed, Project, CurrentAudioGain(time), _rng);
                    final = reactedOwned;
                }

                var bitmap = MatToBitmapSource(final);
                if (reactedOwned != null && !ReferenceEquals(reactedOwned, processed)) reactedOwned.Dispose();

                PreviewFrameReady?.Invoke(bitmap, time);
            }
        }

        private double CurrentAudioGain(double time)
        {
            if (!Project.AudioReactive || _audioEnvelope == null) return 1.0;
            return AudioAnalysis.EnvelopeToGain(_audioEnvelope, time);
        }

        private static Mat ResizeForPreview(Mat src, Transform transform)
        {
            var canvas = new Mat(transform.Height, transform.Width, src.Type(), Scalar.All(0));
            if (transform.Fit == "stretch")
            {
                Cv2.Resize(src, canvas, canvas.Size());
                return canvas;
            }
            var m2x3 = transform.TransformMatrix(src.Cols, src.Rows);
            using var m = new Mat(2, 3, MatType.CV_64FC1);
            for (int r = 0; r < 2; r++) for (int c = 0; c < 3; c++) m.Set(r, c, m2x3[r, c]);
            Cv2.WarpAffine(src, canvas, m, canvas.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
            return canvas;
        }

        private static BitmapSource MatToBitmapSource(Mat mat)
        {
            using var rgb = new Mat();
            Cv2.CvtColor(mat, rgb, ColorConversionCodes.BGR2BGRA);
            // BitmapSource.Create copies the pixel buffer into its own backing
            // store synchronously, so it's safe to dispose `rgb` (and the Mats
            // that fed into `mat`) as soon as this call returns.
            var bitmap = BitmapSource.Create(rgb.Width, rgb.Height, 96, 96, PixelFormats.Bgra32, null,
                rgb.Data, (int)(rgb.Total() * rgb.ElemSize()), (int)rgb.Step());
            bitmap.Freeze();
            return bitmap;
        }

        // ---- Settings mutations (mirrors bridge.py's set_param/toggle/etc) ----

        public void PushUndo()
        {
            _undoStack.Add(Project.Clone());
            _redoStack.Clear();
        }

        public void Undo()
        {
            if (_undoStack.Count == 0) return;
            _redoStack.Add(Project.Clone());
            Project = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            RebuildPipeline();
        }

        public void Redo()
        {
            if (_redoStack.Count == 0) return;
            _undoStack.Add(Project.Clone());
            Project = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            RebuildPipeline();
        }

        public void SetParam(string effectKind, string paramName, object value)
        {
            var settings = Project.Effects.FirstOrDefault(e => e.Kind == effectKind);
            if (settings == null) return;
            settings.Params[paramName] = value;
            RebuildPipeline();
        }

        public void ToggleEnabled(string effectKind, bool enabled)
        {
            var settings = Project.Effects.FirstOrDefault(e => e.Kind == effectKind);
            if (settings == null) return;
            settings.Enabled = enabled;
            RebuildPipeline();
        }

        public void RandomizeAll(int? seed = null)
        {
            Project.MasterSeed = seed ?? _rng.Next(1, int.MaxValue);
            Pipeline.RandomizeAll(Project, _rng);
            RebuildPipeline();
        }

        public void RandomizeOne(string effectKind)
        {
            var settings = Project.Effects.FirstOrDefault(e => e.Kind == effectKind);
            if (settings == null) return;
            Pipeline.RandomizeOne(settings, _rng);
            RebuildPipeline();
        }

        public void SetBeatSync(double bpm, int timeSigNum, int timeSigDen)
        {
            Project.Bpm = bpm;
            Project.TimeSigNum = timeSigNum;
            Project.TimeSigDen = timeSigDen;
        }

        // ---- Presets (JSON, analogous to the Python presets/*.json files) ----

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public void SavePreset(string filePath)
        {
            File.WriteAllText(filePath, JsonSerializer.Serialize(Project, JsonOptions));
        }

        public bool LoadPreset(string filePath)
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<ProjectSettings>(File.ReadAllText(filePath));
                if (loaded == null) return false;
                Project = loaded;
                RebuildPipeline();
                return true;
            }
            catch { return false; }
        }
    }
}
