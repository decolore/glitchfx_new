using System;
using OpenCvSharp;
using GlitchFX.Models;

namespace GlitchFX.Effects
{
    public class ColorGrade : BaseEffect
    {
        public override string Kind => "color_grade";
        public ColorGrade(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            double contrast = AnimatedParam("contrast", time, 1.0);
            double saturation = AnimatedParam("saturation", time, 1.0);
            double brightness = AnimatedParam("brightness", time, 0.0);
            double gamma = AnimatedParam("gamma", time, 1.0);
            double hue = AnimatedParam("hue", time, 0.0);

            using var f32 = new Mat();
            frame.ConvertTo(f32, MatType.CV_32FC3, 1.0 / 255.0);

            using var hsv = new Mat();
            Cv2.CvtColor(f32, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.Split(hsv, out Mat[] hsvCh);
            try
            {
                if (Math.Abs(hue) > 0.001)
                {
                    using var shifted = new Mat();
                    hsvCh[0].ConvertTo(shifted, MatType.CV_32FC1, 1.0, hue);
                    using var clampedHue = new Mat();
                    Cv2.Max(shifted, 0.0, clampedHue);
                    Cv2.Min(clampedHue, 360.0, clampedHue);
                    hsvCh[0].Dispose();
                    hsvCh[0] = clampedHue.Clone();
                }

                using var mergedHsv = new Mat();
                Cv2.Merge(hsvCh, mergedHsv);
                using var backToBgr = new Mat();
                Cv2.CvtColor(mergedHsv, backToBgr, ColorConversionCodes.HSV2BGR);

                using var gray = new Mat();
                Cv2.CvtColor(backToBgr, gray, ColorConversionCodes.BGR2GRAY);
                using var gray3 = new Mat();
                Cv2.CvtColor(gray, gray3, ColorConversionCodes.GRAY2BGR);
                using var satMat = new Mat();
                Cv2.AddWeighted(backToBgr, saturation, gray3, 1.0 - saturation, 0, satMat);

                using var contrastMat = new Mat();
                Cv2.AddWeighted(satMat, contrast, satMat, 0, (1 - contrast) * 0.5 + brightness, contrastMat);

                using var clamped = new Mat();
                Cv2.Max(contrastMat, 0.0, clamped);
                Cv2.Min(clamped, 1.0, clamped);

                using var gammaMat = new Mat();
                Cv2.Pow(clamped, 1.0 / Math.Max(gamma, 0.01), gammaMat);

                var outMat = new Mat();
                gammaMat.ConvertTo(outMat, MatType.CV_8UC3, 255.0);
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

        public override Mat Apply(Mat frame, double time)
        {
            int levels = Math.Max(2, AnimatedParamI("levels", time, 6));
            // ConvertTo(dst, type, scale, shift) rounds via saturate_cast, so a
            // scale-down + scale-up round trip through an integer Mat quantizes
            // each channel to `levels` steps without any manual rounding math.
            using var quantized = new Mat();
            frame.ConvertTo(quantized, MatType.CV_8UC3, (levels - 1) / 255.0);
            var outMat = new Mat();
            quantized.ConvertTo(outMat, MatType.CV_8UC3, 255.0 / (levels - 1));
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
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double threshold = (Bayer4x4[y % 4, x % 4] / 16.0 - 0.5) * step;
                    var px = f32.Get<Vec3f>(y, x);
                    float b = QuantizeChannel(px.Item0 + (float)threshold, levels);
                    float g = QuantizeChannel(px.Item1 + (float)threshold, levels);
                    float r = QuantizeChannel(px.Item2 + (float)threshold, levels);
                    dithered.Set(y, x, new Vec3f(b, g, r));
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
