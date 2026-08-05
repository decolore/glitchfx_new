using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using GlitchFX.Models;

namespace GlitchFX.Effects
{
    /// <summary>
    /// Mirrors Python's EFFECT_REGISTRY + build_pipeline/apply_pipeline/
    /// randomize_all/randomize_one/apply_audio_reaction in effects.py.
    /// </summary>
    public static class Pipeline
    {
        public static readonly Dictionary<string, Func<EffectSettings, BaseEffect>> Registry = new()
        {
            ["color_grade"] = s => new ColorGrade(s),
            ["posterize"] = s => new Posterize(s),
            ["edge_glow"] = s => new EdgeGlow(s),
            ["chromatic_aberration"] = s => new ChromaticAberration(s),
            ["noise"] = s => new Noise(s),
            ["sharpen"] = s => new Sharpen(s),
            ["glitch_blocks"] = s => new GlitchBlocks(s),
            ["scanlines"] = s => new Scanlines(s),
            ["color_invert"] = s => new ColorInvert(s),
            ["vignette"] = s => new Vignette(s),
            ["color_map"] = s => new ColorMap(s),
            ["datamosh"] = s => new Datamosh(s),
            ["pixel_sort"] = s => new PixelSort(s),
            ["vhs"] = s => new VHS(s),
            ["dither"] = s => new Dither(s),
            ["motion_glitch"] = s => new MotionGlitch(s),
            ["motion_trails"] = s => new MotionTrails(s),
            ["text_overlay"] = s => new TextOverlayEffect(s),
        };

        public static List<BaseEffect> BuildPipeline(ProjectSettings project)
        {
            var list = new List<BaseEffect>();
            foreach (var settings in project.Effects)
            {
                if (!Registry.TryGetValue(settings.Kind, out var factory)) continue;
                var effect = factory(settings);
                effect.AnimateEnabled = project.AnimateParams && settings.Animate;
                effect.AnimateAmount = project.AnimationAmount;
                effect.MasterSeed = project.MasterSeed;
                list.Add(effect);
            }
            return list;
        }

        public static Mat ApplyPipeline(List<BaseEffect> pipeline, Mat frame, double time, double audioGain = 1.0)
        {
            var current = frame;
            bool ownsCurrent = false;
            foreach (var effect in pipeline)
            {
                if (!effect.Enabled) continue;
                effect.AudioGain = audioGain;
                var next = effect.Apply(current, time);
                if (ownsCurrent) current.Dispose();
                current = next;
                ownsCurrent = true;
            }
            return ownsCurrent ? current : frame.Clone();
        }

        public static void RandomizeAll(ProjectSettings project, Random rng)
        {
            foreach (var settings in project.Effects)
            {
                if (settings.LockRandom) continue;
                RandomizeOne(settings, rng);
            }
        }

        public static void RandomizeOne(EffectSettings settings, Random rng)
        {
            foreach (var def in EffectSchemas.SchemaFor(settings.Kind))
            {
                if (def.PType == "float" && def.Min.HasValue && def.Max.HasValue)
                    settings.Params[def.Name] = def.Min.Value + rng.NextDouble() * (def.Max.Value - def.Min.Value);
                else if (def.PType == "int" && def.Min.HasValue && def.Max.HasValue)
                    settings.Params[def.Name] = rng.Next((int)def.Min.Value, (int)def.Max.Value + 1);
                else if (def.PType == "bool")
                    settings.Params[def.Name] = rng.NextDouble() < 0.5;
                else if (def.PType == "choice" && def.Choices is { Length: > 0 })
                    settings.Params[def.Name] = def.Choices[rng.Next(def.Choices.Length)];
            }
        }

        /// <summary>
        /// Mirrors Python's apply_audio_reaction(): post-processes a rendered
        /// frame using the current audio envelope value (0..1) and the
        /// project's reaction mode.
        /// </summary>
        public static Mat ApplyAudioReaction(Mat frame, ProjectSettings project, double envelopeValue, Random rng)
        {
            if (!project.AudioReactive) return frame;
            double intensity = project.AudioIntensity / 100.0;
            double strength = envelopeValue * intensity;

            switch (project.ReactionMode)
            {
                case "opacity":
                {
                    using var dark = new Mat(frame.Size(), frame.Type(), Scalar.All(0));
                    var outMat = new Mat();
                    Cv2.AddWeighted(frame, 1.0 - strength * 0.6, dark, strength * 0.6, 0, outMat);
                    return outMat;
                }
                case "flash":
                {
                    using var white = new Mat(frame.Size(), frame.Type(), Scalar.All(255));
                    var outMat = new Mat();
                    Cv2.AddWeighted(frame, 1.0 - strength, white, strength, 0, outMat);
                    return outMat;
                }
                case "shake":
                {
                    int shift = (int)(strength * 20);
                    int dx = rng.Next(-shift, shift + 1);
                    int dy = rng.Next(-shift, shift + 1);
                    using var m = new Mat(2, 3, MatType.CV_64FC1, new double[] { 1, 0, dx, 0, 1, dy });
                    var outMat = new Mat();
                    Cv2.WarpAffine(frame, outMat, m, frame.Size(), InterpolationFlags.Linear, BorderTypes.Wrap);
                    return outMat;
                }
                case "rgbsplit":
                {
                    int shift = (int)(strength * 15);
                    using var ca = new ChromaticAberration(new EffectSettings("chromatic_aberration", true,
                        new Dictionary<string, object> { ["shift"] = shift, ["angle"] = 0.0 }));
                    return ca.Apply(frame, 0);
                }
                case "pulse":
                {
                    double factor = 1.0 + strength * 0.3;
                    var outMat = new Mat();
                    frame.ConvertTo(outMat, frame.Type(), factor);
                    return outMat;
                }
                default:
                    return frame; // "strength" mode only affects the pipeline's audioGain, handled upstream
            }
        }
    }
}
