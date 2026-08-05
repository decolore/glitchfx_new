using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using OpenCvSharp;
using GlitchFX.Effects;
using GlitchFX.Models;

namespace GlitchFX.Export
{
    /// <summary>
    /// Mirrors Python's export.py: renders the base processed cycle to a raw
    /// video via a piped ffmpeg encoder, then assembles the final output with
    /// stream-loop repeats and audio muxing. Simplified to a single worker
    /// (correct output, not yet the multi-threaded reader/worker/writer
    /// pipeline the Python version uses for throughput). See README.
    /// </summary>
    public class ExportService
    {
        public event Action<double>? Progress; // 0..1

        public void ExportVideo(ProjectSettings project, string sourcePath, Action<bool, string> onComplete)
        {
            try
            {
                using var capture = new VideoCapture(sourcePath);
                if (!capture.IsOpened()) { onComplete(false, "Could not open source video"); return; }
                double fps = project.Export.Fps > 0 ? project.Export.Fps : (capture.Fps > 0 ? capture.Fps : 30.0);
                int frameCount = capture.FrameCount;
                double duration = frameCount / Math.Max(capture.Fps, 1.0);

                var (barSeconds, bars, cycleSeconds, drift) = SyncHelpers.ComputeSyncStats(project, duration);

                string tempRaw = Path.Combine(Path.GetTempPath(), $"glitchfx_{Guid.NewGuid():N}.mp4");
                RenderBaseCycle(project, capture, fps, tempRaw);

                int repeats = project.AutoRepeats ? Math.Max(1, (int)Math.Ceiling(60.0 / Math.Max(cycleSeconds, 1.0))) : project.VideoRepeats;
                AssembleOutput(project, tempRaw, sourcePath, repeats, project.Export.OutputPath);

                File.Delete(tempRaw);
                onComplete(true, project.Export.OutputPath);
            }
            catch (Exception ex)
            {
                onComplete(false, ex.Message);
            }
        }

        private void RenderBaseCycle(ProjectSettings project, VideoCapture capture, double fps, string outputPath)
        {
            int w = project.Transform.Width, h = project.Transform.Height;
            var pipeline = Pipeline.BuildPipeline(project);
            foreach (var e in pipeline) e.ResetState();

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -f rawvideo -pix_fmt bgr24 -s {w}x{h} -r {fps.ToString(System.Globalization.CultureInfo.InvariantCulture)} -i - " +
                            $"-c:v {project.Export.Codec} -crf {project.Export.Crf} -preset {project.Export.Preset} " +
                            (string.IsNullOrEmpty(project.Export.MaxBitrate) ? "" : $"-maxrate {project.Export.MaxBitrate} -bufsize {project.Export.MaxBitrate} ") +
                            $"-pix_fmt yuv420p \"{outputPath}\"",
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var ffmpeg = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg");
            int frameCount = capture.FrameCount;
            int idx = 0;
            using var frame = new Mat();
            while (capture.Read(frame) && !frame.Empty())
            {
                double time = idx / fps;
                using var resized = ResizeToTransform(frame, project.Transform);
                using var processed = Pipeline.ApplyPipeline(pipeline, resized, time);
                byte[] bytes = MatToBytes(processed);
                ffmpeg.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                idx++;
                Progress?.Invoke(frameCount > 0 ? idx / (double)frameCount : 0);
            }
            ffmpeg.StandardInput.BaseStream.Flush();
            ffmpeg.StandardInput.Close();
            ffmpeg.WaitForExit();
        }

        private void AssembleOutput(ProjectSettings project, string renderedCyclePath, string originalSourcePath, int repeats, string finalOutputPath)
        {
            string args = $"-y -stream_loop {Math.Max(0, repeats - 1)} -i \"{renderedCyclePath}\" ";
            if (project.Export.AudioCopy)
                args += $"-i \"{originalSourcePath}\" -map 0:v:0 -map 1:a:0? -shortest ";
            args += $"-c:v copy " + (project.Export.AudioCopy ? "-c:a aac " : "") + $"\"{finalOutputPath}\"";

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg");
            process.WaitForExit();
        }

        private static Mat ResizeToTransform(Mat src, Transform transform)
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

        private static byte[] MatToBytes(Mat mat)
        {
            var bytes = new byte[mat.Total() * mat.ElemSize()];
            System.Runtime.InteropServices.Marshal.Copy(mat.Data, bytes, 0, bytes.Length);
            return bytes;
        }
    }
}
