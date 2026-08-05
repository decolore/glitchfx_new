using System;
using System.Collections.Generic;
using OpenCvSharp;
using GlitchFX.Models;

namespace GlitchFX.Effects
{
    public class Datamosh : BaseEffect
    {
        public override string Kind => "datamosh";
        private Mat? _referenceFrame;
        private int _frameIndex;
        public Datamosh(EffectSettings s) : base(s) { }

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
            using var blended = new Mat();
            Cv2.AddWeighted(refF, decay, frameF, 1.0 - decay, 0, blended);
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
        private Mat? _prevFrame;
        private readonly Random _rng = new();
        public MotionGlitch(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            double sensitivity = AnimatedParam("sensitivity", time, 0.3);
            int blockSize = Math.Max(4, AnimatedParamI("block_size", time, 32));
            int maxShift = AnimatedParamI("max_shift", time, 30);

            var outMat = frame.Clone();
            if (_prevFrame == null || _prevFrame.Size() != frame.Size())
            {
                _prevFrame?.Dispose();
                _prevFrame = frame.Clone();
                return outMat;
            }

            using var diff = new Mat();
            Cv2.Absdiff(frame, _prevFrame, diff);
            int h = frame.Rows, w = frame.Cols;
            for (int y = 0; y < h; y += blockSize)
            {
                for (int x = 0; x < w; x += blockSize)
                {
                    int bw = Math.Min(blockSize, w - x);
                    int bh = Math.Min(blockSize, h - y);
                    using var diffBlock = diff[y, y + bh, x, x + bw];
                    double meanDiff = Cv2.Mean(diffBlock).Val0;
                    if (meanDiff / 255.0 <= sensitivity) continue;
                    int shiftX = _rng.Next(-maxShift, maxShift + 1);
                    int srcX = Math.Clamp(x + shiftX, 0, w - bw);
                    using var srcBlock = frame[y, y + bh, srcX, srcX + bw];
                    using var dstBlock = outMat[y, y + bh, x, x + bw];
                    srcBlock.CopyTo(dstBlock);
                }
            }

            _prevFrame.Dispose();
            _prevFrame = frame.Clone();
            return outMat;
        }
    }

    public class MotionTrails : BaseEffect
    {
        public override string Kind => "motion_trails";
        private readonly List<Mat> _history = new();
        public MotionTrails(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            double decay = AnimatedParam("decay", time, 0.85);
            int trailLength = Math.Max(1, AnimatedParamI("trail_length", time, 8));

            _history.Insert(0, frame.Clone());
            while (_history.Count > trailLength)
            {
                _history[^1].Dispose();
                _history.RemoveAt(_history.Count - 1);
            }

            using var accum = new Mat(frame.Size(), MatType.CV_32FC3, Scalar.All(0));
            double totalWeight = 0;
            for (int i = 0; i < _history.Count; i++)
            {
                double weight = Math.Pow(decay, i);
                using var histF = new Mat();
                _history[i].ConvertTo(histF, MatType.CV_32FC3, weight);
                Cv2.Add(accum, histF, accum);
                totalWeight += weight;
            }

            using var normalized = new Mat();
            accum.ConvertTo(normalized, MatType.CV_32FC3, 1.0 / Math.Max(totalWeight, 1e-6));
            var outMat = new Mat();
            normalized.ConvertTo(outMat, MatType.CV_8UC3);
            return outMat;
        }
    }
}
