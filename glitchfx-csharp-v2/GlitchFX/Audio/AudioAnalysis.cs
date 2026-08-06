using System;
using System.Diagnostics;
using System.IO;
using OpenCvSharp;

namespace GlitchFX.Audio
{
    /// <summary>
    /// Mirrors Python's audio.py: decodes audio via ffmpeg into mono PCM and
    /// computes reaction envelopes used for audio-reactive effect params.
    /// </summary>
    public static class AudioAnalysis
    {
        public const int SampleRate = 44100;

        public static float[] DecodeMono(string path)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-v quiet -i \"{path}\" -f f32le -ac 1 -ar {SampleRate} -",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg");
            using var ms = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(ms);
            process.WaitForExit();
            var bytes = ms.ToArray();
            var samples = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
            return samples;
        }

        public static double AudioDuration(string path)
        {
            var samples = DecodeMono(path);
            return samples.Length / (double)SampleRate;
        }

        /// <summary>RMS loudness envelope, mirrors compute_audio_envelope().</summary>
        public static float[] ComputeAudioEnvelope(float[] samples, double windowSeconds = 0.05)
        {
            int windowSize = Math.Max(1, (int)(windowSeconds * SampleRate));
            int numWindows = Math.Max(1, samples.Length / windowSize);
            var envelope = new float[numWindows];
            for (int i = 0; i < numWindows; i++)
            {
                double sum = 0;
                int start = i * windowSize;
                int end = Math.Min(samples.Length, start + windowSize);
                for (int j = start; j < end; j++) sum += samples[j] * samples[j];
                envelope[i] = (float)Math.Sqrt(sum / Math.Max(1, end - start));
            }
            NormalizeInPlace(envelope);
            return envelope;
        }

        /// <summary>
        /// Bass-band energy envelope. For each analysis window, zero-pads to
        /// the next power of two and runs an actual FFT (via OpenCvSharp's
        /// Cv2.Dft, so no extra dependency is needed), then sums the squared
        /// magnitude of only the frequency bins below bassCutoffHz. This
        /// replaces the previous one-pole low-pass filter approximation with
        /// a real frequency-domain bass-band extraction.
        /// </summary>
        public static float[] ComputeBandEnvelope(float[] samples, double windowSeconds = 0.05, double bassCutoffHz = 250.0)
        {
            int windowSize = Math.Max(1, (int)(windowSeconds * SampleRate));
            int numWindows = Math.Max(1, samples.Length / windowSize);
            var envelope = new float[numWindows];

            int fftSize = NextPowerOfTwo(windowSize);
            double binHz = SampleRate / (double)fftSize;
            int cutoffBin = Math.Max(1, (int)(bassCutoffHz / binHz));

            for (int i = 0; i < numWindows; i++)
            {
                int start = i * windowSize;
                int end = Math.Min(samples.Length, start + windowSize);

                using var input = new Mat(1, fftSize, MatType.CV_32FC1, Scalar.All(0));
                for (int j = start; j < end; j++) input.Set<float>(0, j - start, samples[j]);

                using var spectrum = new Mat();
                Cv2.Dft(input, spectrum, DftFlags.ComplexOutput);

                double energy = 0;
                int lastBin = Math.Min(cutoffBin, fftSize / 2 - 1);
                for (int bin = 1; bin <= lastBin; bin++)
                {
                    var c = spectrum.Get<Vec2f>(0, bin);
                    energy += (double)c.Item0 * c.Item0 + (double)c.Item1 * c.Item1;
                }
                envelope[i] = (float)Math.Sqrt(energy / Math.Max(1, lastBin));
            }
            NormalizeInPlace(envelope);
            return envelope;
        }

        private static int NextPowerOfTwo(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return Math.Max(p, 2);
        }

        /// <summary>BPM-locked pulse envelope, mirrors compute_beat_envelope().</summary>
        public static float[] ComputeBeatEnvelope(double durationSeconds, double bpm, double windowSeconds = 0.05)
        {
            int numWindows = Math.Max(1, (int)(durationSeconds / windowSeconds));
            var envelope = new float[numWindows];
            double beatInterval = 60.0 / Math.Max(bpm, 1.0);
            for (int i = 0; i < numWindows; i++)
            {
                double t = i * windowSeconds;
                double phase = (t % beatInterval) / beatInterval;
                envelope[i] = (float)Math.Max(0, 1.0 - phase * 4.0); // sharp decay pulse each beat
            }
            return envelope;
        }

        /// <summary>Dispatches to the right envelope based on ProjectSettings.ReactionSource.</summary>
        public static float[] ComputeReactionEnvelope(string source, float[] samples, double bpm, double windowSeconds = 0.05)
        {
            return source switch
            {
                "bass" => ComputeBandEnvelope(samples, windowSeconds),
                "beat" => ComputeBeatEnvelope(samples.Length / (double)SampleRate, bpm, windowSeconds),
                _ => ComputeAudioEnvelope(samples, windowSeconds),
            };
        }

        public static double EnvelopeToGain(float[] envelope, double timeSeconds, double windowSeconds = 0.05)
        {
            if (envelope.Length == 0) return 0;
            int idx = Math.Clamp((int)(timeSeconds / windowSeconds), 0, envelope.Length - 1);
            return envelope[idx];
        }

        /// <summary>Downsampled envelope for the small waveform/reactivity graph in the effects panel.</summary>
        public static float[] ComputeGraphData(float[] envelope, int targetPoints = 200)
        {
            if (envelope.Length <= targetPoints) return envelope;
            var result = new float[targetPoints];
            double step = envelope.Length / (double)targetPoints;
            for (int i = 0; i < targetPoints; i++)
            {
                int start = (int)(i * step);
                int end = Math.Min(envelope.Length, (int)((i + 1) * step));
                float max = 0;
                for (int j = start; j < end; j++) max = Math.Max(max, envelope[j]);
                result[i] = max;
            }
            return result;
        }

        public static float[] ComputeEnvelopeSamples(float[] envelope, double durationSeconds, double fps)
        {
            int frameCount = Math.Max(1, (int)(durationSeconds * fps));
            var result = new float[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                double t = i / fps;
                result[i] = (float)EnvelopeToGain(envelope, t);
            }
            return result;
        }

        private static void NormalizeInPlace(float[] envelope)
        {
            float max = 0;
            foreach (var v in envelope) max = Math.Max(max, v);
            if (max < 1e-6f) return;
            for (int i = 0; i < envelope.Length; i++) envelope[i] /= max;
        }
    }
}
