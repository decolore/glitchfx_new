using System;
using System.Collections.Generic;
using OpenCvSharp;
using GlitchFX.Models;

namespace GlitchFX.Effects
{
    public class Datamosh : BaseEffect
    {
        public override string Kind => "datamosh";
        public override bool Stateful => true;
        private Mat? _referenceFrame;
        private int _frameIndex;
        public Datamosh(EffectSettings s) : base(s) { }

        public override void ResetState() { _referenceFrame?.Dispose(); _referenceFrame = null; _frameIndex = 0; }

        public override Mat Apply(Mat frame, double time)
        {
            double amount = AnimatedParam("amount", time, 0.5);
            double decay = AnimatedParam("decay", time, 0.9);
            int interval = Math.Max(1, AnimatedParamI("interval", time, 12));

            if (_referenceFrame == null || _frameIndex % interval == 0)
            {
                _referenceFrame?.Dispose();
                _referenceFrame = frame.Clone();
                _frameIndex++;
                return frame.Clone();
            }
            _frameIndex++;

            using var frameF = new Mat();
            frame.ConvertTo(frameF, MatType.CV_32FC3);
            using var refF = new Mat();
            _referenceFrame.ConvertTo(refF, MatType.CV_32FC3);
            using var blended = refF * decay + frameF * (1.0 - decay);
            using var mixed = new Mat();
            Cv2.AddWeighted(blended, amount, frameF, 1.0 - amount, 0, mixed);

            _referenceFrame.Dispose();
            _referenceFrame = new Mat();
            mixed.ConvertTo(_referenceFrame, MatType.CV_8UC3);

            var outMat = new Mat();
            mixed.ConvertTo(outMat, MatType.CV_8UC3);
            return outMat;
        }
    }

    public class MotionGlitch : BaseEffect
    {
        public override string Kind => "motion_glitch";
        public override bool Stateful => true;
        private Mat? _prevGray;
        private readonly Random _rng = new();
        public MotionGlitch(EffectSettings s) : base(s) { }

        public override void ResetState() { _prevGray?.Dispose(); _prevGray = null; }

        public override Mat Apply(Mat frame, double time)
        {
            double amount = AnimatedParam("amount", time, 0.5);
            int blockSize = Math.Max(4, AnimatedParamI("block_size", time, 16));
            double motionThreshold = AnimatedParam("threshold", time, 0.1) * 255.0;

            using var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            var outMat = frame.Clone();

            if (_prevGray != null && _prevGray.Size() == gray.Size())
            {
                using var diff = new Mat();
                Cv2.Absdiff(gray, _prevGray, diff);
                for (int y = 0; y < outMat.Rows; y += blockSize)
                {
                    for (int x = 0; x < outMat.Cols; x += blockSize)
                    {
                        int bh = Math.Min(blockSize, outMat.Rows - y);
                        int bw = Math.Min(blockSize, outMat.Cols - x);
                        using var diffBlock = diff[y, y + bh, x, x + bw];
                        double meanDiff = Cv2.Mean(diffBlock).Val0;
                        if (meanDiff > motionThreshold && _rng.NextDouble() < amount)
                        {
                            int shiftX = _rng.Next(-blockSize, blockSize + 1);
                            int srcX = Math.Clamp(x + shiftX, 0, outMat.Cols - bw);
                            using var srcBlock = frame[y, y + bh, srcX, srcX + bw];
                            using var dstBlock = outMat[y, y + bh, x, x + bw];
                            srcBlock.CopyTo(dstBlock);
                        }
                    }
                }
            }
            _prevGray?.Dispose();
            _prevGray = gray.Clone();
            return outMat;
        }
    }

    public class MotionTrails : BaseEffect
    {
        public override string Kind => "motion_trails";
        public override bool Stateful => true;
        private readonly Queue<Mat> _history = new();
        public MotionTrails(EffectSettings s) : base(s) { }

        public override void ResetState()
        {
            while (_history.Count > 0) _history.Dequeue().Dispose();
        }

        public override Mat Apply(Mat frame, double time)
        {
            int length = Math.Max(1, AnimatedParamI("length", time, 6));
            double decay = AnimatedParam("decay", time, 0.7);

            _history.Enqueue(frame.Clone());
            while (_history.Count > length) _history.Dequeue().Dispose();

            using var accum = new Mat(frame.Size(), MatType.CV_32FC3, Scalar.All(0));
            int i = 0;
            double totalWeight = 0;
            foreach (var hist in _history)
            {
                double weight = Math.Pow(decay, _history.Count - 1 - i);
                using var histF = new Mat();
                hist.ConvertTo(histF, MatType.CV_32FC3, weight);
                Cv2.Add(accum, histF, accum);
                totalWeight += weight;
                i++;
            }
            using var normalized = accum / Math.Max(totalWeight, 1e-6);
            var outMat = new Mat();
            ((Mat)normalized).ConvertTo(outMat, MatType.CV_8UC3);
            return outMat;
        }
    }
}
