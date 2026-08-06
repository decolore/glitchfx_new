using System;
using OpenCvSharp;
using GlitchFX.Models;

namespace GlitchFX.Effects
{
    public class ColorGrade : BaseEffect
    {
        public override string Kind => "color_grade";
        public ColorGrade(EffectSettings s) : base(s) { }

        // Mirrors Python's effects.py ColorGrade.apply exactly:
        //   out = (frame/255) ** gamma                      (direct exponent, not 1/gamma)
        //   out = (out - 0.5) * contrast + 0.5 + brightness  (contrast/brightness around midpoint)
        //   out = clip(out, 0, 1)
        //   hsv = BGR2HSV(uint8(out*255))                    (8-bit HSV, H in 0..179)
        //   hsv.S *= saturation; hsv.H = (hsv.H + hue/2) % 180
        //   out = HSV2BGR(hsv)
        // The previous C# port applied hue/saturation first (on a 0..360 float
        // HSV range) and gamma last with an inverted 1/gamma exponent - a
        // different operation order *and* a different gamma direction from the
        // Mac build, which is why results didn't match.
        public override Mat Apply(Mat frame, double time)
        {
            double contrast = AnimatedParam("contrast", time, 1.0);
            double saturation = AnimatedParam("saturation", time, 1.0);
            double brightness = AnimatedParam("brightness", time, 0.0);
            double gamma = AnimatedParam("gamma", time, 1.0);
            double hue = AnimatedParam("hue", time, 0.0);

            using var f32 = new Mat();
            frame.ConvertTo(f32, MatType.CV_32FC3, 1.0 / 255.0);

            using var gammaMat = new Mat();
            Cv2.Pow(f32, Math.Max(0.1, gamma), gammaMat);

            using var graded = new Mat();
            Cv2.AddWeighted(gammaMat, contrast, gammaMat, 0, (1 - contrast) * 0.5 + brightness, graded);

            using var clamped = new Mat();
            Cv2.Max(graded, 0.0, clamped);
            Cv2.Min(clamped, 1.0, clamped);

            using var clamped8 = new Mat();
            clamped.ConvertTo(clamped8, MatType.CV_8UC3, 255.0);
            using var hsv = new Mat();
            Cv2.CvtColor(clamped8, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.Split(hsv, out Mat[] hsvCh);
            try
            {
                using var satF = new Mat();
                hsvCh[1].ConvertTo(satF, MatType.CV_32FC1, saturation);
                using var satClamped = new Mat();
                Cv2.Max(satF, 0.0, satClamped);
                Cv2.Min(satClamped, 255.0, satClamped);
                hsvCh[1].Dispose();
                hsvCh[1] = new Mat();
                satClamped.ConvertTo(hsvCh[1], MatType.CV_8UC1);

                if (Math.Abs(hue) > 0.001)
                {
                    using var hueShifted = new Mat();
                    hsvCh[0].ConvertTo(hueShifted, MatType.CV_32FC1, 1.0, hue / 2.0);

                    // Wrap the 8-bit hue channel (0..179) around instead of
                    // clamping - see the unsafe-pointer note on the old hue
                    // wrap loop this mirrors; no elementwise Mat modulo exists
                    // in OpenCvSharp.
                    using var hueWrapped = new Mat(hueShifted.Size(), MatType.CV_32FC1);
                    unsafe
                    {
                        int srcStep = (int)(hueShifted.Step() / sizeof(float));
                        int dstStep = (int)(hueWrapped.Step() / sizeof(float));
                        float* srcBase = (float*)(void*)hueShifted.Data;
                        float* dstBase = (float*)(void*)hueWrapped.Data;
                        int rows = hueShifted.Rows, cols = hueShifted.Cols;
                        for (int y = 0; y < rows; y++)
                        {
                            float* srcRow = srcBase + y * srcStep;
                            float* dstRow = dstBase + y * dstStep;
                            for (int x = 0; x < cols; x++)
                            {
                                float v = srcRow[x];
                                dstRow[x] = ((v % 180f) + 180f) % 180f;
                            }
                        }
                    }
                    hsvCh[0].Dispose();
                    hsvCh[0] = new Mat();
                    hueWrapped.ConvertTo(hsvCh[0], MatType.CV_8UC1);
                }

                using var mergedHsv = new Mat();
                Cv2.Merge(hsvCh, mergedHsv);
                var outMat = new Mat();
                Cv2.CvtColor(mergedHsv, outMat, ColorConversionCodes.HSV2BGR);
                return outMat;
            }
            finally
            {
                foreach (var ch in hsvCh) ch.Dispose();
            }
        }
    }

    public class Posterize : BaseEffect
    {
        public override string Kind => "posterize";
        public Posterize(EffectSettings s) : base(s) { }

        // Mirrors Python's `bits` slider (1-7, levels = 2**bits) rather than a
        // raw "levels" (2-32) slider - the two scales don't line up, so the
        // same slider position previously produced a very different amount of
        // posterization than the Mac build.
        public override Mat Apply(Mat frame, double time)
        {
            int bits = Math.Clamp(AnimatedParamI("bits", time, 5), 1, 8);
            int levels = 1 << bits; // 2 ** bits
            int stepInt = Math.Max(1, 256 / levels);

            using var quantized = new Mat();
            frame.ConvertTo(quantized, MatType.CV_8UC3, 1.0 / stepInt);
            var outMat = new Mat();
            quantized.ConvertTo(outMat, MatType.CV_8UC3, stepInt);
            return outMat;
        }
    }

    public class ColorInvert : BaseEffect
    {
        public override string Kind => "color_invert";
        public ColorInvert(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            double amount = AnimatedParam("amount", time, 1.0);
            using var inverted = new Mat();
            Cv2.BitwiseNot(frame, inverted);
            var outMat = new Mat();
            Cv2.AddWeighted(inverted, amount, frame, 1.0 - amount, 0, outMat);
            return outMat;
        }
    }

    public class Vignette : BaseEffect
    {
        public override string Kind => "vignette";
        private Mat? _cachedMask; private int _cw, _ch; private double _cRadius;
        public Vignette(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            double amount = AnimatedParam("amount", time, 0.5);
            double radius = AnimatedParam("radius", time, 1.0);
            int w = frame.Cols, h = frame.Rows;
            if (_cachedMask == null || _cw != w || _ch != h || Math.Abs(_cRadius - radius) > 1e-6)
            {
                _cachedMask?.Dispose();
                _cachedMask = BuildMask(w, h, radius);
                _cw = w; _ch = h; _cRadius = radius;
            }
            using var f32 = new Mat();
            frame.ConvertTo(f32, MatType.CV_32FC3, 1.0 / 255.0);
            using var mask3 = new Mat();
            Cv2.CvtColor(_cachedMask!, mask3, ColorConversionCodes.GRAY2BGR);

            // weight = mask*amount + (1-amount); computed via AddWeighted to avoid
            // Mat arithmetic-operator/MatExpr pitfalls.
            using var weight = new Mat();
            Cv2.AddWeighted(mask3, amount, mask3, 0, 1.0 - amount, weight);

            using var weighted = new Mat();
            Cv2.Multiply(f32, weight, weighted);
            var outMat = new Mat();
            weighted.ConvertTo(outMat, MatType.CV_8UC3, 255.0);
            return outMat;
        }

        private static Mat BuildMask(int w, int h, double radius)
        {
            var mask = new Mat(h, w, MatType.CV_32FC1);
            double cx = w / 2.0, cy = h / 2.0;
            double maxDist = Math.Sqrt(cx * cx + cy * cy) * radius;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double dx = x - cx, dy = y - cy;
                    double dist = Math.Sqrt(dx * dx + dy * dy) / Math.Max(maxDist, 1e-6);
                    double v = 1.0 - Math.Clamp(dist, 0.0, 1.0);
                    mask.Set<float>(y, x, (float)v);
                }
            }
            return mask;
        }
    }

    public class Dither : BaseEffect
    {
        public override string Kind => "dither";
        private static readonly int[,] Bayer4x4 = {
            { 0, 8, 2, 10 }, { 12, 4, 14, 6 }, { 3, 11, 1, 9 }, { 15, 7, 13, 5 }
        };
        public Dither(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            int levels = Math.Max(2, AnimatedParamI("levels", time, 4));
            double amount = AnimatedParam("amount", time, 1.0);
            int w = frame.Cols, h = frame.Rows;
            using var f32 = new Mat();
            frame.ConvertTo(f32, MatType.CV_32FC3, 1.0 / 255.0);
            using var dithered = new Mat(h, w, MatType.CV_32FC3);
            double step = 1.0 / (levels - 1);

            // Pixel-by-pixel Bayer thresholding can't be expressed as a single
            // vectorized OpenCV op, so this walks raw memory through unsafe
            // float pointers (using each Mat's own Step() for the row stride)
            // instead of Mat.Get/Set, which carry per-call generic-dispatch and
            // bounds-check overhead that adds up fast at 1080x1920+ frame sizes
            // - this is one of the hottest per-frame loops in the whole pipeline.
            unsafe
            {
                int srcStep = (int)(f32.Step() / sizeof(float));
                int dstStep = (int)(dithered.Step() / sizeof(float));
                float* srcBase = (float*)(void*)f32.Data;
                float* dstBase = (float*)(void*)dithered.Data;
                for (int y = 0; y < h; y++)
                {
                    float* srcRow = srcBase + y * srcStep;
                    float* dstRow = dstBase + y * dstStep;
                    int bayerRow = y & 3;
                    for (int x = 0; x < w; x++)
                    {
                        double threshold = (Bayer4x4[bayerRow, x & 3] / 16.0 - 0.5) * step;
                        int idx = x * 3;
                        dstRow[idx] = QuantizeChannel(srcRow[idx] + (float)threshold, levels);
                        dstRow[idx + 1] = QuantizeChannel(srcRow[idx + 1] + (float)threshold, levels);
                        dstRow[idx + 2] = QuantizeChannel(srcRow[idx + 2] + (float)threshold, levels);
                    }
                }
            }

            using var blended = new Mat();
            Cv2.AddWeighted(dithered, amount, f32, 1.0 - amount, 0, blended);
            var outMat = new Mat();
            blended.ConvertTo(outMat, MatType.CV_8UC3, 255.0);
            return outMat;
        }

        private static float QuantizeChannel(float v, int levels)
        {
            v = Math.Clamp(v, 0f, 1f);
            float q = (float)Math.Round(v * (levels - 1)) / (levels - 1);
            return Math.Clamp(q, 0f, 1f);
        }
    }

    public class ColorMap : BaseEffect
    {
        public override string Kind => "color_map";
        public ColorMap(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            string blend = ParamS("blend", "replace");
            var outMat = frame.Clone();
            for (int i = 1; i <= 12; i++)
            {
                if (!ParamB($"active{i}", i == 1)) continue;
                var from = ParseColor(ParamS($"from{i}", "#000000"));
                var to = ParseColor(ParamS($"to{i}", "#000000"));
                double tolerance = ParamD($"tolerance{i}", 0.15) * 255.0;

                using var refMat = new Mat(outMat.Size(), outMat.Type(), from);
                using var diff = new Mat();
                Cv2.Absdiff(outMat, refMat, diff);
                using var diffGray = new Mat();
                Cv2.CvtColor(diff, diffGray, ColorConversionCodes.BGR2GRAY);
                using var mask = new Mat();
                Cv2.Threshold(diffGray, mask, tolerance, 255, ThresholdTypes.BinaryInv);

                using var solidTo = new Mat(outMat.Size(), outMat.Type(), to);
                using var blendResult = new Mat();
                switch (blend)
                {
                    case "multiply":
                        Cv2.Multiply(outMat, solidTo, blendResult, 1.0 / 255.0);
                        break;
                    case "screen":
                        using (var inv1 = new Mat()) using (var inv2 = new Mat()) using (var mul = new Mat())
                        {
                            Cv2.BitwiseNot(outMat, inv1);
                            Cv2.BitwiseNot(solidTo, inv2);
                            Cv2.Multiply(inv1, inv2, mul, 1.0 / 255.0);
                            Cv2.BitwiseNot(mul, blendResult);
                        }
                        break;
                    case "overlay":
                        Cv2.AddWeighted(outMat, 0.5, solidTo, 0.5, 0, blendResult);
                        break;
                    default:
                        solidTo.CopyTo(blendResult);
                        break;
                }
                using var next = new Mat();
                outMat.CopyTo(next);
                blendResult.CopyTo(next, mask);
                outMat.Dispose();
                outMat = next.Clone();
            }
            return outMat;
        }

        private static Scalar ParseColor(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length < 6) return Scalar.All(0);
            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            return new Scalar(b, g, r);
        }
    }
}
