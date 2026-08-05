using System;
using OpenCvSharp;
using GlitchFX.Models;

namespace GlitchFX.Effects
{
    public class EdgeGlow : BaseEffect
    {
        public override string Kind => "edge_glow";
        public EdgeGlow(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            double t1 = AnimatedParam("threshold1", time, 50.0);
            double t2 = AnimatedParam("threshold2", time, 150.0);
            double glow = AnimatedParam("glow", time, 0.6);
            var color = ParseHex(ParamS("color", "#A855F7"));

            using var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            using var edges = new Mat();
            Cv2.Canny(gray, edges, t1, t2);
            using var blurred = new Mat();
            Cv2.GaussianBlur(edges, blurred, new Size(9, 9), 0);

            using var colorLayer = new Mat(frame.Size(), frame.Type(), color);
            using var edges3 = new Mat();
            Cv2.CvtColor(blurred, edges3, ColorConversionCodes.GRAY2BGR);
            using var edgesF = new Mat();
            edges3.ConvertTo(edgesF, MatType.CV_32FC3, glow / 255.0);

            using var frameF = new Mat();
            frame.ConvertTo(frameF, MatType.CV_32FC3);
            using var colorF = new Mat();
            colorLayer.ConvertTo(colorF, MatType.CV_32FC3);
            using var glowAdd = edgesF.Mul(colorF);
            using var sum = frameF + glowAdd;
            var outMat = new Mat();
            sum.ConvertTo(outMat, MatType.CV_8UC3);
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

        public override Mat Apply(Mat frame, double time)
        {
            int shift = AnimatedParamI("shift", time, 6);
            double angle = AnimatedParam("angle", time, 0.0) * Math.PI / 180.0;
            int dx = (int)Math.Round(Math.Cos(angle) * shift);
            int dy = (int)Math.Round(Math.Sin(angle) * shift);

            Cv2.Split(frame, out var ch);
            using var bChan = ch[0]; using var gChan = ch[1]; using var rChan = ch[2];
            using var rShifted = ShiftChannel(rChan, dx, dy);
            using var bShifted = ShiftChannel(bChan, -dx, -dy);
            var outMat = new Mat();
            Cv2.Merge(new[] { bShifted, gChan, rShifted }, outMat);
            return outMat;
        }

        private static Mat ShiftChannel(Mat channel, int dx, int dy)
        {
            using var m = new Mat(2, 3, MatType.CV_64FC1, new double[] { 1, 0, dx, 0, 1, dy });
            var result = new Mat();
            Cv2.WarpAffine(channel, result, m, channel.Size(), InterpolationFlags.Linear, BorderTypes.Wrap);
            return result;
        }
    }

    public class Noise : BaseEffect
    {
        public override string Kind => "noise";
        private readonly Random _rng = new();
        public Noise(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            double amount = AnimatedParam("amount", time, 0.15);
            bool mono = ParamB("mono", false);
            using var noiseF = new Mat(frame.Size(), mono ? MatType.CV_32FC1 : MatType.CV_32FC3);
            Cv2.Randn(noiseF, Scalar.All(0), Scalar.All(amount * 255.0));
            using var noise3 = mono ? Gray3(noiseF) : noiseF;
            using var frameF = new Mat();
            frame.ConvertTo(frameF, MatType.CV_32FC3);
            using var sum = frameF + noise3;
            using var clamped = new Mat();
            Cv2.Max(sum, 0.0, clamped);
            Cv2.Min(clamped, 255.0, clamped);
            var outMat = new Mat();
            clamped.ConvertTo(outMat, MatType.CV_8UC3);
            return outMat;
        }

        private static Mat Gray3(Mat gray)
        {
            var result = new Mat();
            Cv2.CvtColor(gray, result, ColorConversionCodes.GRAY2BGR);
            return result;
        }
    }

    public class Sharpen : BaseEffect
    {
        public override string Kind => "sharpen";
        public Sharpen(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            double amount = AnimatedParam("amount", time, 0.5);
            using var blurred = new Mat();
            Cv2.GaussianBlur(frame, blurred, new Size(0, 0), 3);
            using var frameF = new Mat();
            frame.ConvertTo(frameF, MatType.CV_32FC3);
            using var blurredF = new Mat();
            blurred.ConvertTo(blurredF, MatType.CV_32FC3);
            using var detail = frameF - blurredF;
            using var sharpened = frameF + detail * amount;
            var outMat = new Mat();
            sharpened.ConvertTo(outMat, MatType.CV_8UC3);
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
        private readonly Random _rng = new();
        public GlitchBlocks(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            double amount = AnimatedParam("amount", time, 0.3);
            int blockSize = Math.Max(2, AnimatedParamI("block_size", time, 24));
            int maxShift = AnimatedParamI("max_shift", time, 40);
            var outMat = frame.Clone();
            int h = outMat.Rows;
            for (int y = 0; y < h; y += blockSize)
            {
                if (_rng.NextDouble() > amount) continue;
                int rowH = Math.Min(blockSize, h - y);
                int shift = _rng.Next(-maxShift, maxShift + 1);
                using var strip = outMat[y, y + rowH, 0, outMat.Cols];
                using var shifted = RollHorizontal(strip, shift);
                shifted.CopyTo(strip);
            }
            return outMat;
        }
    }

    public class VHS : BaseEffect
    {
        public override string Kind => "vhs";
        private readonly Random _rng = new();
        public VHS(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            double warp = AnimatedParam("warp", time, 0.3);
            double noise = AnimatedParam("noise", time, 0.2);
            double colorBleed = AnimatedParam("color_bleed", time, 0.3);
            double tracking = AnimatedParam("tracking", time, 0.2);

            var warped = frame.Clone();
            int h = warped.Rows;
            for (int y = 0; y < h; y++)
            {
                double wobble = Math.Sin(y * 0.05 + time * 4.0) * warp * 8.0;
                if (_rng.NextDouble() < tracking * 0.05) wobble += _rng.Next(-20, 20);
                using var row = warped[y, y + 1, 0, warped.Cols];
                using var shifted = RollHorizontal(row, (int)Math.Round(wobble));
                shifted.CopyTo(row);
            }

            using var bleedEffect = new ChromaticAberration(new EffectSettings("chromatic_aberration", true,
                new System.Collections.Generic.Dictionary<string, object> { ["shift"] = colorBleed * 10.0, ["angle"] = 0.0 }));
            using var bled = bleedEffect.Apply(warped, time);

            using var noiseF = new Mat(bled.Size(), MatType.CV_32FC3);
            Cv2.Randn(noiseF, Scalar.All(0), Scalar.All(noise * 40.0));
            using var bledF = new Mat();
            bled.ConvertTo(bledF, MatType.CV_32FC3);
            using var sum = bledF + noiseF;
            using var clamped = new Mat();
            Cv2.Max(sum, 0.0, clamped); Cv2.Min(clamped, 255.0, clamped);
            var outMat = new Mat();
            clamped.ConvertTo(outMat, MatType.CV_8UC3);
            warped.Dispose();
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

            using var work = direction == "vertical" ? frame.Clone().Transpose() : frame.Clone();
            using var gray = new Mat();
            Cv2.CvtColor(work, gray, ColorConversionCodes.BGR2GRAY);

            for (int y = 0; y < work.Rows; y++)
            {
                int x = 0;
                while (x < work.Cols)
                {
                    if (gray.Get<byte>(y, x) < threshold) { x++; continue; }
                    int start = x;
                    while (x < work.Cols && gray.Get<byte>(y, x) >= threshold) x++;
                    int len = x - start;
                    if (len > 1)
                    {
                        using var segment = work[y, y + 1, start, start + len];
                        SortSegmentByBrightness(segment, gray, y, start, len);
                    }
                }
            }
            var outMat = direction == "vertical" ? work.Transpose() : work.Clone();
            return outMat;
        }

        private static void SortSegmentByBrightness(Mat segmentRow, Mat gray, int y, int start, int len)
        {
            var pixels = new (byte brightness, Vec3b color)[len];
            for (int i = 0; i < len; i++)
                pixels[i] = (gray.Get<byte>(y, start + i), segmentRow.Get<Vec3b>(0, i));
            Array.Sort(pixels, (a, b) => a.brightness.CompareTo(b.brightness));
            for (int i = 0; i < len; i++) segmentRow.Set(0, i, pixels[i].color);
        }
    }
}
