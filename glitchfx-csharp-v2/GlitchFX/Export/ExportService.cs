using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using GlitchFX.Effects;
using GlitchFX.Models;

namespace GlitchFX.Export
{
    /// <summary>Structured export progress, richer than a bare 0..1 fraction so the
    /// Output panel can show a frame counter, elapsed time, and an ETA.</summary>
    public class ExportProgressInfo
    {
        public double Fraction { get; init; }
        public int CurrentFrame { get; init; }
        public int TotalFrames { get; init; }
        public TimeSpan Elapsed { get; init; }
        public TimeSpan? EstimatedRemaining { get; init; }
        /// <summary>True while muxing/looping the rendered cycle into the final
        /// output file, a step that has no per-frame progress of its own.</summary>
        public bool IsFinalizing { get; init; }
    }

    /// <summary>
    /// Mirrors Python's export.py: renders the base processed cycle to a raw
    /// video via a piped ffmpeg encoder, then assembles the final output with
    /// stream-loop repeats and audio muxing.
    /// </summary>
    public class ExportService
    {
        public event Action<ExportProgressInfo>? Progress;

        private CancellationTokenSource? _cts;

        /// <summary>
        /// Requests that an in-progress ExportVideo call stop as soon as
        /// possible. Safe to call from any thread (e.g. a Stop button on the
        /// UI thread while export runs on a background Task). Any running
        /// ffmpeg process is killed immediately instead of waiting for it to
        /// drain its input, and ExportVideo's onComplete callback then fires
        /// with success=false, message="Export cancelled".
        /// </summary>
        public void Cancel() => _cts?.Cancel();

        public void ExportVideo(ProjectSettings project, string sourcePath, Action<bool, string> onComplete)
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            string? tempRaw = null;
            try
            {
                using var capture = new VideoCapture(sourcePath);
                if (!capture.IsOpened()) { onComplete(false, "Could not open source video"); return; }
                double fps = project.Export.Fps > 0 ? project.Export.Fps : (capture.Fps > 0 ? capture.Fps : 30.0);
                int frameCount = capture.FrameCount;
                double duration = frameCount / Math.Max(capture.Fps, 1.0);

                var (barSeconds, bars, cycleSeconds, drift) = SyncHelpers.ComputeSyncStats(project, duration);

                tempRaw = Path.Combine(Path.GetTempPath(), $"glitchfx_{Guid.NewGuid():N}.mp4");
                RenderBaseCycle(project, capture, fps, tempRaw, cts.Token);

                Progress?.Invoke(new ExportProgressInfo { Fraction = 1, CurrentFrame = frameCount, TotalFrames = frameCount, IsFinalizing = true });

                int repeats = project.AutoRepeats ? Math.Max(1, (int)Math.Ceiling(60.0 / Math.Max(cycleSeconds, 1.0))) : project.VideoRepeats;
                AssembleOutput(project, tempRaw, sourcePath, repeats, project.Export.OutputPath, cts.Token);

                onComplete(true, project.Export.OutputPath);
            }
            catch (OperationCanceledException)
            {
                onComplete(false, "Export cancelled");
            }
            catch (Exception ex)
            {
                onComplete(false, ex.Message);
            }
            finally
            {
                if (tempRaw != null) { try { File.Delete(tempRaw); } catch { } }
                _cts = null;
            }
        }

        /// <summary>
        /// Builds the ffmpeg "-c:v ..." encoder arguments for the configured
        /// codec. libx264/libx265 are CPU (software) encoders and use the
        /// familiar -crf/-preset knobs. h264_nvenc/hevc_nvenc (NVIDIA),
        /// h264_qsv (Intel Quick Sync) and h264_amf (AMD) are GPU encoders,
        /// typically several times faster than software encoding but each
        /// vendor uses different flag names for quality/speed, so those are
        /// mapped from the same Crf/Preset settings here to keep the UI simple.
        /// A GPU encoder still needs a supported graphics card + driver on the
        /// machine running the export; otherwise ffmpeg will fail to start it.
        /// </summary>
        private static string BuildEncodeArgs(ExportSettings export)
        {
            // The Output panel collects bitrate as a plain kbps number (e.g.
            // "8000" for 8 Mbps) instead of ffmpeg's raw "8M"-style suffix, so
            // any stray non-digit characters (legacy presets, stray spaces)
            // are stripped before appending the "k" ffmpeg expects.
            string maxrate = "";
            if (!string.IsNullOrWhiteSpace(export.MaxBitrate))
            {
                string digits = new string(export.MaxBitrate.Where(char.IsDigit).ToArray());
                if (digits.Length > 0) maxrate = $"-maxrate {digits}k -bufsize {digits}k ";
            }

            switch (export.Codec)
            {
                case "h264_nvenc":
                case "hevc_nvenc":
                {
                    // Constant-quality VBR mode on NVENC uses -cq on roughly the
                    // same 0-51 scale as libx264's -crf (lower = better).
                    string preset = export.Preset switch { "slow" => "slow", "ultrafast" => "fast", _ => "medium" };
                    return $"-c:v {export.Codec} -preset {preset} -rc vbr -cq {export.Crf} -b:v 0 {maxrate}-pix_fmt yuv420p";
                }
                case "h264_qsv":
                {
                    string preset = export.Preset switch { "ultrafast" => "veryfast", "fast" => "faster", "slow" => "slow", _ => "medium" };
                    return $"-c:v h264_qsv -preset {preset} -global_quality {export.Crf} {maxrate}-pix_fmt nv12";
                }
                case "h264_amf":
                {
                    // AMF has no CRF-style knob; map preset to its speed/quality
                    // trade-off and drive quality via constant QP instead.
                    string quality = export.Preset switch { "ultrafast" => "speed", "slow" => "quality", _ => "balanced" };
                    return $"-c:v h264_amf -quality {quality} -rc cqp -qp_i {export.Crf} -qp_p {export.Crf} -qp_b {export.Crf} {maxrate}-pix_fmt yuv420p";
                }
                default: // libx264 / libx265 (CPU/software)
                    return $"-c:v {export.Codec} -crf {export.Crf} -preset {export.Preset} {maxrate}-pix_fmt yuv420p";
            }
        }

        /// <summary>
        /// Renders every source frame through the effect pipeline and pipes
        /// the result into ffmpeg's stdin.
        ///
        /// This runs as a three-stage producer/consumer pipeline (reader -&gt;
        /// effect-processor -&gt; ffmpeg writer) on separate threads connected by
        /// bounded queues, so decoding the next frame and writing the
        /// previous encoded frame to ffmpeg's pipe overlap with the CPU-bound
        /// effect processing instead of happening fully sequentially -
        /// mirroring the throughput intent of the Python version's
        /// multi-threaded reader/worker/writer split.
        ///
        /// Frames are still *applied* to the pipeline strictly in order on a
        /// single processing stage/thread: several effects (Datamosh,
        /// MotionTrails, MotionGlitch, the animated-param noise/beat-sync
        /// state) keep per-instance state that depends on having seen every
        /// prior frame in sequence, so fanning the pipeline stage itself out
        /// across multiple worker threads would corrupt that state or
        /// require reordering frames afterwards. Only the read/process/encode
        /// *stages* run concurrently with each other.
        ///
        /// All three stages share one linked cancellation token
        /// (`cancelStages`), which is essential: if ffmpeg exits early (bad
        /// output path, unsupported codec/GPU encoder, etc.) the write loop
        /// below stops consuming `encodedFrames`, and without a way to signal
        /// that upstream, the process/reader stages would block forever
        /// trying to Add into their now-full, now-unconsumed bounded queues -
        /// this was the cause of exports silently hanging after only a
        /// handful of frames instead of surfacing ffmpeg's real error.
        /// </summary>
        private void RenderBaseCycle(ProjectSettings project, VideoCapture capture, double fps, string outputPath, CancellationToken token)
        {
            int w = project.Transform.Width, h = project.Transform.Height;
            var pipeline = Pipeline.BuildPipeline(project);
            foreach (var e in pipeline) e.ResetState();

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -f rawvideo -pix_fmt bgr24 -s {w}x{h} -r {fps.ToString(System.Globalization.CultureInfo.InvariantCulture)} -i - " +
                            BuildEncodeArgs(project.Export) +
                            $" \"{outputPath}\"",
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var ffmpeg = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg");
            // Stopping mid-export kills ffmpeg immediately instead of waiting
            // for it to drain whatever is left buffered in its stdin pipe.
            using var killOnCancel = token.Register(() => { try { if (!ffmpeg.HasExited) ffmpeg.Kill(true); } catch { } });

            // Capture stderr on a background thread so a failing GPU encoder
            // (missing driver, unsupported codec, etc.) surfaces ffmpeg's real
            // error text instead of a generic "broken pipe" exception once
            // ffmpeg exits early and closes stdin.
            var stderrBuilder = new StringBuilder();
            var stderrTask = Task.Run(() =>
            {
                string? line;
                while ((line = ffmpeg.StandardError.ReadLine()) != null) stderrBuilder.AppendLine(line);
            });

            int frameCount = capture.FrameCount;
            var stopwatch = Stopwatch.StartNew();

            using var rawFrames = new BlockingCollection<(int Idx, Mat Frame)>(boundedCapacity: 4);
            using var encodedFrames = new BlockingCollection<byte[]>(boundedCapacity: 4);
            Exception? failure = null;
            using var cancelStages = CancellationTokenSource.CreateLinkedTokenSource(token);

            var readerTask = Task.Run(() =>
            {
                try
                {
                    int idx = 0;
                    var frame = new Mat();
                    while (!cancelStages.IsCancellationRequested && capture.Read(frame) && !frame.Empty())
                    {
                        rawFrames.Add((idx, frame.Clone()), cancelStages.Token);
                        idx++;
                    }
                    frame.Dispose();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { failure ??= ex; cancelStages.Cancel(); }
                finally { rawFrames.CompleteAdding(); }
            });

            var processTask = Task.Run(() =>
            {
                try
                {
                    foreach (var (idx, frame) in rawFrames.GetConsumingEnumerable(cancelStages.Token))
                    {
                        using (frame)
                        {
                            double time = idx / fps;
                            using var resized = ResizeToTransform(frame, project.Transform);
                            using var processed = Pipeline.ApplyPipeline(pipeline, resized, time);
                            encodedFrames.Add(MatToBytes(processed), cancelStages.Token);
                        }
                        double fraction = frameCount > 0 ? (idx + 1) / (double)frameCount : 0;
                        var elapsed = stopwatch.Elapsed;
                        TimeSpan? eta = fraction > 0.02 ? TimeSpan.FromSeconds(elapsed.TotalSeconds / fraction * (1 - fraction)) : null;
                        Progress?.Invoke(new ExportProgressInfo
                        {
                            Fraction = fraction,
                            CurrentFrame = idx + 1,
                            TotalFrames = frameCount,
                            Elapsed = elapsed,
                            EstimatedRemaining = eta,
                        });
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { failure ??= ex; cancelStages.Cancel(); }
                finally { encodedFrames.CompleteAdding(); }
            });

            try
            {
                foreach (var bytes in encodedFrames.GetConsumingEnumerable(cancelStages.Token))
                {
                    ffmpeg.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException)
            {
                // ffmpeg likely exited early (e.g. GPU encoder unavailable,
                // bad output path, unsupported settings) and closed its
                // stdin pipe; tell the upstream stages to stop instead of
                // leaving them blocked forever on their now-unconsumed queues.
                // The exit-code check below still raises a descriptive error.
                cancelStages.Cancel();
            }

            Task.WaitAll(readerTask, processTask);

            try { if (!ffmpeg.HasExited) ffmpeg.StandardInput.BaseStream.Flush(); } catch { }
            try { ffmpeg.StandardInput.Close(); } catch { }
            ffmpeg.WaitForExit();
            stderrTask.Wait();

            token.ThrowIfCancellationRequested();
            if (failure != null) throw failure;
            if (ffmpeg.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg exited with code {ffmpeg.ExitCode} while encoding with codec '{project.Export.Codec}'. " +
                    (stderrBuilder.Length > 0 ? stderrBuilder.ToString() : "No further output was captured."));
        }

        private void AssembleOutput(ProjectSettings project, string renderedCyclePath, string originalSourcePath, int repeats, string finalOutputPath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
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
            using var killOnCancel = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            string stderrText = process.StandardError.ReadToEnd();
            process.WaitForExit();
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode} while assembling the final output. " +
                    (stderrText.Length > 0 ? stderrText : "No further output was captured."));
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
