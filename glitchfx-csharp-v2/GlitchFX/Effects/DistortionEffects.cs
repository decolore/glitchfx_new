using System;
using OpenCvSharp;
using GlitchFX.Models;

namespace GlitchFX.Effects
{
    public class EdgeGlow : BaseEffect
    {
        public override string Kind => "edge_glow";
        public EdgeGlow(EffectSettings s) : base(s) { }

        // Mirrors Python's effects.py EdgeGlow.apply: Sobel-magnitude edge
        // detection (not Canny), thresholded, optionally dilated for
        // thickness and Gaussian-blurred for glow spread, then blended back
        // either as an additive glow over the original frame or, in "neon"
        // mode, over a darkened copy of it. The previous C# port used Canny
        // with a completely different, unrelated set of parameters
        // (threshold1/threshold2/glow) that don't correspond to anything in
        // the Mac schema, so the Neon/Darken/Thickness/Pre-Blur controls the
        // user expects from the Mac build didn't exist at all.
        public override Mat Apply(Mat frame, double time)
        {
            int preBlur = AnimatedParamI("pre_blur", time, 7);
            double threshold = AnimatedParam("threshold", time, 55.0);
            int blur = AnimatedParamI("blur", time, 9);
            double intensity = AnimatedParam("intensity", time, 1.0);
            bool neon = ParamB("neon", true);
            double darken = AnimatedParam("darken", time, 0.55);
            int thick = AnimatedParamI("thick", time, 2);
            var color = ParseHex(ParamS("color", "#00ffff"));

            using var gray0 = new Mat();
            Cv2.CvtColor(frame, gray0, ColorConversionCodes.BGR2GRAY);
            Mat gray = gray0;
            Mat? grayBlurred = null;
            if (preBlur > 1)
            {
                int k = preBlur % 2 == 0 ? preBlur + 1 : preBlur;
                grayBlurred = new Mat();
                Cv2.GaussianBlur(gray0, grayBlurred, new Size(k, k), 0);
                gray = grayBlurred;
            }

            using var sobelX = new Mat();
            using var sobelY = new Mat();
            Cv2.Sobel(gray, sobelX, MatType.CV_64FC1, 1, 0, 3);
            Cv2.Sobel(gray, sobelY, MatType.CV_64FC1, 0, 1, 3);
            grayBlurred?.Dispose();

            using var sobelXSq = new Mat();
            using var sobelYSq = new Mat();
            Cv2.Multiply(sobelX, sobelX, sobelXSq);
            Cv2.Multiply(sobelY, sobelY, sobelYSq);
            using var magSq = new Mat();
            Cv2.Add(sobelXSq, sobelYSq, magSq);
            using var mag = new Mat();
            Cv2.Sqrt(magSq, mag);
            Cv2.MinMaxLoc(mag, out _, out double maxVal);
            if (maxVal <= 0) maxVal = 1;
            using var mag8 = new Mat();
            mag.ConvertTo(mag8, MatType.CV_8UC1, 255.0 / maxVal);

            using var edges = new Mat();
            Cv2.Threshold(mag8, edges, threshold, 255, ThresholdTypes.Binary);

            Mat edgesProcessed = edges;
            Mat? dilated = null;
            if (thick > 0)
            {
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(thick * 2 + 1, thick * 2 + 1));
                dilated = new Mat();
                Cv2.Dilate(edges, dilated, kernel, null, 1);
                edgesProcessed = dilated;
            }

            Mat? blurred = null;
            if (blur > 1)
            {
                int k = blur % 2 == 0 ? blur + 1 : blur;
                blurred = new Mat();
                Cv2.GaussianBlur(edgesProcessed, blurred, new Size(k, k), 0);
                edgesProcessed = blurred;
            }

            using var edges3 = new Mat();
            Cv2.CvtColor(edgesProcessed, edges3, ColorConversionCodes.GRAY2BGR);
            dilated?.Dispose();
            blurred?.Dispose();

            using var glowF = new Mat();
            edges3.ConvertTo(glowF, MatType.CV_32FC3, 1.0 / 255.0);
            using var colorLayer8 = new Mat(frame.Size(), frame.Type(), color);
            using var colorLayerF = new Mat();
            colorLayer8.ConvertTo(colorLayerF, MatType.CV_32FC3);
            using var glow = new Mat();
            Cv2.Multiply(glowF, colorLayerF, glow, intensity);

            using var frameF = new Mat();
            frame.ConvertTo(frameF, MatType.CV_32FC3);
            using var baseF = new Mat();
            if (neon) frameF.ConvertTo(baseF, MatType.CV_32FC3, 1.0 - darken);
            else frameF.CopyTo(baseF);

            using var sum = new Mat();
            Cv2.Add(baseF, glow, sum);
            using var clamped = new Mat();
            Cv2.Max(sum, 0.0, clamped);
            Cv2.Min(clamped, 255.0, clamped);
            var outMat = new Mat();
            clamped.ConvertTo(outMat, MatType.CV_8UC3);
            return outMat;
        }

        private static Scalar ParseHex(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length < 6) return Scalar.All(255);
            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            return new Scalar(b, g, r);
        }
    }

    public class ChromaticAberration : BaseEffect
    {
        public override string Kind => "chromatic_aberration";
        public ChromaticAberration(EffectSettings s) : base(s) { }

        // Mirrors Python's effects.py ChromaticAberration.apply, including the
        // "animate" param: when true (the Mac default), the angle keeps
        // rotating on its own (angle += time * 1.2) independent of the
        // effect's own Animate/beat-sync toggle. "shift" is a float in the
        // Mac schema (px, 0-20 step 0.5); the previous C# schema exposed it
        // as an int 0-60, a different range from the Mac slider.
        public override Mat Apply(Mat frame, double time)
        {
            double shift = AnimatedParam("shift", time, 3.0);
            double angle = AnimatedParam("angle", time, 0.0) * Math.PI / 180.0;
            if (ParamB("animate", true)) angle += time * 1.2;
            int dx = (int)Math.Round(Math.Cos(angle) * shift);
            int dy = (int)Math.Round(Math.Sin(angle) * shift);

            Cv2.Split(frame, out Mat[] ch);
            using var bChan = ch[0]; using var gChan = ch[1]; using var rChan = ch[2];
            using var rShifted = ShiftChannel(rChan, dx, dy);
            using var bShifted = ShiftChannel(bChan, -dx, -dy);
            var outMat = new Mat();
            Cv2.Merge(new[] { bShifted, gChan, rShifted }, outMat);
            return outMat;
        }

        private static Mat ShiftChannel(Mat channel, int dx, int dy)
        {
            // Build the affine matrix by setting elements directly rather than
            // via the Array-based Mat constructor, whose 5-arg overload (rows,
            // cols, type, data, step) is not publicly accessible in this
            // OpenCvSharp4 version (CS0122).
            using var m = new Mat(2, 3, MatType.CV_64FC1);
            m.Set(0, 0, 1.0); m.Set(0, 1, 0.0); m.Set(0, 2, (double)dx);
            m.Set(1, 0, 0.0); m.Set(1, 1, 1.0); m.Set(1, 2, (double)dy);
            var result = new Mat();
            Cv2.WarpAffine(channel, result, m, channel.Size(), InterpolationFlags.Linear, BorderTypes.Wrap);
            return result;
        }
    }

    public class Noise : BaseEffect
    {
        public override string Kind => "noise";
        public Noise(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            double amount = AnimatedParam("amount", time, 0.15);
            bool mono = ParamB("mono", false);
            using var noiseF = new Mat(frame.Size(), mono ? MatType.CV_32FC1 : MatType.CV_32FC3);
            Cv2.Randn(noiseF, Scalar.All(0), Scalar.All(amount * 255.0));

            Mat noise3 = noiseF;
            Mat? noise3Owned = null;
            if (mono)
            {
                noise3Owned = new Mat();
                Cv2.CvtColor(noiseF, noise3Owned, ColorConversionCodes.GRAY2BGR);
                noise3 = noise3Owned;
            }

            using var frameF = new Mat();
            frame.ConvertTo(frameF, MatType.CV_32FC3);
            using var sum = new Mat();
            Cv2.Add(frameF, noise3, sum);
            noise3Owned?.Dispose();

            using var clamped = new Mat();
            Cv2.Max(sum, 0.0, clamped);
            Cv2.Min(clamped, 255.0, clamped);
            var outMat = new Mat();
            clamped.ConvertTo(outMat, MatType.CV_8UC3);
            return outMat;
        }
    }

    public class Sharpen : BaseEffect
    {
        public override string Kind => "sharpen";
        public Sharpen(EffectSettings s) : base(s) { }

        // Mirrors Python's effects.py Sharpen.apply: a real convolution
        // kernel (cv2.filter2D) rather than an unsharp-mask (blur-and-subtract)
        // technique. kernel_size < 3 uses a mild cross kernel, otherwise a
        // stronger 8-neighbor kernel, both scaled by "amount".
        public override Mat Apply(Mat frame, double time)
        {
            double amount = AnimatedParam("amount", time, 1.0);
            int kernelSize = AnimatedParamI("kernel_size", time, 3);

            float[] weights = kernelSize < 3
                ? new float[] { 0, -1, 0, -1, 5, -1, 0, -1, 0 }
                : new float[] { -1, -1, -1, -1, 9, -1, -1, -1, -1 };

            using var kernel = new Mat(3, 3, MatType.CV_32FC1);
            for (int i = 0; i < 9; i++) kernel.Set<float>(i / 3, i % 3, (float)(weights[i] * amount));

            var outMat = new Mat();
            Cv2.Filter2D(frame, outMat, frame.Type(), kernel);
            return outMat;
        }
    }

    public class Scanlines : BaseEffect
    {
        public override string Kind => "scanlines";
        public Scanlines(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            double opacity = AnimatedParam("opacity", time, 0.3);
            int spacing = Math.Max(1, AnimatedParamI("spacing", time, 3));
            var outMat = frame.Clone();
            for (int y = 0; y < outMat.Rows; y += spacing)
            {
                using var row = outMat[y, y + 1, 0, outMat.Cols];
                using var darkRow = new Mat();
                row.ConvertTo(darkRow, row.Type(), 1.0 - opacity);
                darkRow.CopyTo(row);
            }
            return outMat;
        }
    }

    public class GlitchBlocks : BaseEffect
    {
        public override string Kind => "glitch_blocks";
        public GlitchBlocks(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            double amount = AnimatedParam("amount", time, 0.3);
            int blockSize = Math.Max(2, AnimatedParamI("block_size", time, 24));
            int maxShift = AnimatedParamI("max_shift", time, 40);
            var outMat = frame.Clone();
            int h = outMat.Rows, w = outMat.Cols;
            for (int y = 0; y < h; y += blockSize)
            {
                if (Rng.NextDouble() > amount) continue;
                int rowH = Math.Min(blockSize, h - y);
                int shift = Rng.Next(-maxShift, maxShift + 1);
                using var band = outMat[y, y + rowH, 0, w];
                using var rolled = RollHorizontal(band, shift);
                rolled.CopyTo(band);
            }
            return outMat;
        }
    }

    public class VHS : BaseEffect
    {
        public override string Kind => "vhs";
        public VHS(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            double noise = AnimatedParam("noise", time, 0.15);
            double colorBleed = AnimatedParam("color_bleed", time, 0.4);
            double trackingJitter = AnimatedParam("tracking_jitter", time, 0.3);
            double lineJitter = AnimatedParam("line_jitter", time, 0.4);

            using var warped = frame.Clone();
            int h = warped.Rows, w = warped.Cols;
            if (trackingJitter > 0.001)
            {
                for (int y = 0; y < h; y++)
                {
                    if (Rng.NextDouble() > 0.05 * trackingJitter) continue;
                    int shift = (int)((Rng.NextDouble() * 2 - 1) * 20 * lineJitter);
                    using var row = warped[y, y + 1, 0, w];
                    using var rolled = RollHorizontal(row, shift);
                    rolled.CopyTo(row);
                }
            }

            // animate explicitly disabled: VHS wants a fixed left/right color
            // bleed direction (angle=0) here, not the chromatic_aberration
            // effect's own auto-rotating-angle behavior.
            var bleedSettings = new EffectSettings("chromatic_aberration", true,
                new System.Collections.Generic.Dictionary<string, object> { ["shift"] = colorBleed * 10.0, ["angle"] = 0.0, ["animate"] = false })
            { Animate = false };
            var bleedEffect = new ChromaticAberration(bleedSettings);
            using var bled = bleedEffect.Apply(warped, time);

            using var noiseF = new Mat(bled.Size(), MatType.CV_32FC3);
            Cv2.Randn(noiseF, Scalar.All(0), Scalar.All(noise * 40.0));
            using var bledF = new Mat();
            bled.ConvertTo(bledF, MatType.CV_32FC3);
            using var sum = new Mat();
            Cv2.Add(bledF, noiseF, sum);
            using var clamped = new Mat();
            Cv2.Max(sum, 0.0, clamped);
            Cv2.Min(clamped, 255.0, clamped);
            var outMat = new Mat();
            clamped.ConvertTo(outMat, MatType.CV_8UC3);
            return outMat;
        }
    }

    public class PixelSort : BaseEffect
    {
        public override string Kind => "pixel_sort";
        public PixelSort(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            double threshold = AnimatedParam("threshold", time, 0.4) * 255.0;
            string direction = ParamS("direction", "horizontal");
            double amount = AnimatedParam("amount", time, 1.0);
            if (amount <= 0.001) return frame.Clone();

            using var work = new Mat();
            if (direction == "vertical") Cv2.Transpose(frame, work); else frame.CopyTo(work);

            using var gray = new Mat();
            Cv2.CvtColor(work, gray, ColorConversionCodes.BGR2GRAY);

            // Scanning for bright-enough runs and sorting each one by
            // brightness can't be expressed as vectorized OpenCV ops, so this
            // walks raw memory through unsafe byte pointers (row stride from
            // each Mat's own Step()) instead of Mat.Get/Set, which carry
            // per-call generic-dispatch and bounds-check overhead across a
            // full 1080x1920+ frame - this was one of the hottest per-frame
            // loops in the whole pipeline.
            unsafe
            {
                int workStep = (int)work.Step();
                int grayStep = (int)gray.Step();
                byte* workBase = (byte*)(void*)work.Data;
                byte* grayBase = (byte*)(void*)gray.Data;
                int rows = work.Rows, cols = work.Cols;
                for (int y = 0; y < rows; y++)
                {
                    byte* workRow = workBase + y * workStep;
                    byte* grayRow = grayBase + y * grayStep;
                    int x = 0;
                    while (x < cols)
                    {
                        if (grayRow[x] < threshold) { x++; continue; }
                        int start = x;
                        while (x < cols && grayRow[x] >= threshold) x++;
                        int len = x - start;
                        if (len > 1) SortSegmentByBrightness(workRow, grayRow, start, len);
                    }
                }
            }

            var outMat = new Mat();
            if (direction == "vertical") Cv2.Transpose(work, outMat); else work.CopyTo(outMat);
            return outMat;
        }

        private static unsafe void SortSegmentByBrightness(byte* workRow, byte* grayRow, int start, int len)
        {
            var pixels = new (byte brightness, byte b, byte g, byte r)[len];
            for (int i = 0; i < len; i++)
            {
                int gi = start + i;
                int wi = gi * 3;
                pixels[i] = (grayRow[gi], workRow[wi], workRow[wi + 1], workRow[wi + 2]);
            }
            Array.Sort(pixels, (a, b) => a.brightness.CompareTo(b.brightness));
            for (int i = 0; i < len; i++)
            {
                int wi = (start + i) * 3;
                workRow[wi] = pixels[i].b; workRow[wi + 1] = pixels[i].g; workRow[wi + 2] = pixels[i].r;
            }
        }
    }
}
