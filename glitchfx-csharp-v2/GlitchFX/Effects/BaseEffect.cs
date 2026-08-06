using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using GlitchFX.Models;

namespace GlitchFX.Effects
{
    /// <summary>
    /// Mirrors Python's BaseEffect in effects.py: holds the live parameter
    /// dictionary for one effect instance in the stack and exposes the
    /// animated_param() noise helper used to drive the "Animate" toggle.
    /// </summary>
    public abstract class BaseEffect
    {
        public abstract string Kind { get; }
        public virtual bool Stateful => false;

        public EffectSettings Settings { get; }
        public Dictionary<string, object> Params { get; }
        public bool Enabled { get; set; }
        public bool AnimateEnabled { get; set; }
        public int AnimateAmount { get; set; } = 20;
        public double AudioGain { get; set; } = 1.0;
        public int MasterSeed { get; set; } = 1;

        /// <summary>
        /// The project-wide Strength control (0..5, 1.0 = neutral/normal). Set
        /// by Pipeline.BuildPipeline from ProjectSettings.GlobalStrength. At
        /// 1.0 every parameter is exactly what the user configured/randomized
        /// (see ApplyStrength() below). At 0.0, Pipeline.BuildPipeline also
        /// forces Enabled to false for every effect, so the whole effect is
        /// skipped entirely rather than merely pulled toward its defaults -
        /// i.e. Strength at 0% truly turns every effect off.
        /// </summary>
        public double GlobalStrength { get; set; } = 1.0;

        private Random? _rng;
        /// <summary>
        /// Deterministic, shared per-effect-instance RNG seeded from
        /// MasterSeed + Kind. Effects that need per-frame randomness
        /// (GlitchBlocks, VHS, MotionGlitch) use this instead of an unseeded
        /// `new Random()` field, so their glitching reproduces identically
        /// across preview and export runs for the same Master Seed -
        /// matching how AnimatedParam already derives its noise from
        /// MasterSeed. Lazily created (not in the constructor) because
        /// MasterSeed is assigned after construction by Pipeline.BuildPipeline.
        /// </summary>
        protected Random Rng => _rng ??= new Random(unchecked((int)Fnv1A($"{MasterSeed}|{Kind}")));

        protected BaseEffect(EffectSettings settings)
        {
            Settings = settings;
            Params = new Dictionary<string, object>(settings.Params);
            Enabled = settings.Enabled;
            AnimateEnabled = settings.Animate;
        }

        /// <summary>Applies the effect to one BGR frame at the given pipeline time (seconds).</summary>
        public abstract Mat Apply(Mat frame, double time);

        /// <summary>Called by randomize_all/randomize_one; override to jitter params.</summary>
        public virtual void Randomize(Random rng) { }

        /// <summary>Reset any internal state held by stateful effects (datamosh, motion trails, etc).</summary>
        public virtual void ResetState() { }

        public object? RawParam(string key, object? def = null) =>
            Params.TryGetValue(key, out var v) ? v : def;

        public double ParamD(string key, double def) =>
            ApplyStrength(key, Convert.ToDouble(RawParam(key, def), System.Globalization.CultureInfo.InvariantCulture));
        public int ParamI(string key, int def) =>
            (int)Math.Round(ApplyStrength(key, Convert.ToInt32(RawParam(key, def))));
        public bool ParamB(string key, bool def) => RawParam(key, def) is bool b ? b : def;
        public string ParamS(string key, string def) => RawParam(key, def)?.ToString() ?? def;

        /// <summary>
        /// Applies the global Strength control to one numeric parameter value.
        /// Rather than fading the whole rendered frame's opacity, Strength
        /// scales how far this parameter's current value sits from its schema
        /// default (the "neutral" setting): scaled = default + (raw - default)
        /// * GlobalStrength. At 1.0 (100%, the neutral/no-op multiplier) this is
        /// exactly the raw value the user configured or randomized - unchanged.
        /// Below 1.0 it pulls the effect back toward its neutral default
        /// (weaker); above 1.0 it pushes further away (stronger, up to 5x at
        /// the slider's max). At exactly 0.0, Pipeline.BuildPipeline separately
        /// disables the whole effect (see GlobalStrength doc above), so this
        /// formula's behavior at 0.0 specifically is moot - it only matters for
        /// the 0-1 range in between. Only numeric (float/int) params are
        /// affected - bool/color/choice/string params are returned unchanged.
        /// The lower bound is still clamped to the param's schema Min (if any)
        /// to avoid producing invalid values, but the upper bound is
        /// intentionally left unclamped so "multiply" can exceed the slider's
        /// normal Max.
        /// </summary>
        private double ApplyStrength(string key, double raw)
        {
            if (Math.Abs(GlobalStrength - 1.0) < 1e-9) return raw;
            var pdef = EffectSchemas.SchemaFor(Kind).FirstOrDefault(p => p.Name == key);
            if (pdef == null || (pdef.PType != "float" && pdef.PType != "int")) return raw;
            double baseline = Convert.ToDouble(pdef.Default, System.Globalization.CultureInfo.InvariantCulture);
            double scaled = baseline + (raw - baseline) * GlobalStrength;
            if (pdef.Min.HasValue) scaled = Math.Max(scaled, pdef.Min.Value);
            return scaled;
        }

        /// <summary>
        /// Mirrors Python's BaseEffect.animated_param(): deterministic
        /// per-parameter pseudo-random noise (seeded from master seed + effect
        /// kind + param name) so "Animate" gently drifts numeric params over time
        /// without any two params moving in lockstep.
        /// </summary>
        public double AnimatedParam(string key, double time, double def)
        {
            double baseVal = ParamD(key, def);
            if (!AnimateEnabled) return baseVal;
            var pdef = EffectSchemas.SchemaFor(Kind).FirstOrDefault(p => p.Name == key);
            if (pdef == null || (pdef.PType != "float" && pdef.PType != "int")) return baseVal;

            uint h = Fnv1A($"{MasterSeed}|{Kind}|{key}");
            double phase1 = (h / 4294967296.0) * 2 * Math.PI;
            uint h2 = unchecked(h * 2654435761u);
            double phase2 = (h2 / 4294967296.0) * 2 * Math.PI;
            double noise = 0.5 * Math.Sin(time * 0.7 + phase1) + 0.5 * Math.Sin(time * 1.3 + phase2);

            double range = (pdef.Min.HasValue && pdef.Max.HasValue) ? (pdef.Max.Value - pdef.Min.Value) : Math.Max(Math.Abs(baseVal), 1.0);
            double magnitude = range * 0.5 * (AnimateAmount / 100.0) * AudioGain;
            double value = baseVal + noise * magnitude;
            if (pdef.Min.HasValue) value = Math.Max(value, pdef.Min.Value);
            if (pdef.Max.HasValue) value = Math.Min(value, pdef.Max.Value);
            if (pdef.PType == "int") value = Math.Round(value);
            return value;
        }

        public int AnimatedParamI(string key, double time, int def) => (int)Math.Round(AnimatedParam(key, time, def));

        private static uint Fnv1A(string s)
        {
            uint h = 2166136261;
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(s))
            {
                h ^= b;
                h *= 16777619;
            }
            return h;
        }

        /// <summary>Mirrors np.roll(arr, shift, axis=1): horizontal wraparound shift.</summary>
        protected static Mat RollHorizontal(Mat src, int shift)
        {
            int w = src.Cols;
            shift = ((shift % w) + w) % w;
            var result = new Mat(src.Rows, src.Cols, src.Type());
            if (shift == 0) { src.CopyTo(result); return result; }
            using (var srcRight = src[0, src.Rows, w - shift, w])
            using (var dstLeft = result[0, src.Rows, 0, shift])
                srcRight.CopyTo(dstLeft);
            using (var srcLeft = src[0, src.Rows, 0, w - shift])
            using (var dstRight = result[0, src.Rows, shift, w])
                srcLeft.CopyTo(dstRight);
            return result;
        }

        /// <summary>Mirrors np.roll(arr, shift, axis=0): vertical wraparound shift.</summary>
        protected static Mat RollVertical(Mat src, int shift)
        {
            int h = src.Rows;
            shift = ((shift % h) + h) % h;
            var result = new Mat(src.Rows, src.Cols, src.Type());
            if (shift == 0) { src.CopyTo(result); return result; }
            using (var srcBottom = src[h - shift, h, 0, src.Cols])
            using (var dstTop = result[0, shift, 0, src.Cols])
                srcBottom.CopyTo(dstTop);
            using (var srcTop = src[0, h - shift, 0, src.Cols])
            using (var dstBottom = result[shift, h, 0, src.Cols])
                srcTop.CopyTo(dstBottom);
            return result;
        }
    }
}
