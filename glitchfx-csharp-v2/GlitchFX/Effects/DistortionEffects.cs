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
            using var glowAdd = new Mat();
            Cv2.Multiply(edgesF, colorF, glowAdd);
            using var sum = new Mat();
            Cv2.Add(frameF, glowAdd, sum);
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
            using var m = new Mat(2, 3, MatType.CV_64FC1, new double[] { 1, 0, dx, 0, 1, dy });
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

        public override Mat Apply(Mat frame, double time)
        {
            double amount = AnimatedParam("amount", time, 0.5);
            using var blurred = new Mat();
            Cv2.GaussianBlur(frame, blurred, new Size(0, 0), 3);
            using var frameF = new Mat();
            frame.ConvertTo(frameF, MatType.CV_32FC3);
            using var blurredF = new Mat();
            blurred.ConvertTo(blurredF, MatType.CV_32FC3);
            using var detail = new Mat();
            Cv2.Subtract(frameF, blurredF, detail);
            using var detailScaled = new Mat();
            detail.ConvertTo(detailScaled, MatType.CV_32FC3, amount);
            using var sharpened = new Mat();
            Cv2.Add(frameF, detailScaled, sharpened);
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
            for (int y = 0; y < h; y += block