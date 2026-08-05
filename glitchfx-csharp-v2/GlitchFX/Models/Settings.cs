using System;
using System.Collections.Generic;
using System.Linq;

namespace GlitchFX.Models
{
    /// <summary>
    /// Mirrors Python's ParamDef in settings.py: describes one tweakable
    /// parameter of an effect (used to drive the auto-generated ParamPanel UI).
    /// </summary>
    public class ParamDef
    {
        public string Name { get; }
        public string Label { get; }
        /// <summary>"float" | "int" | "bool" | "color" | "choice" | "string" | "font"</summary>
        public string PType { get; }
        public object Default { get; }
        public double? Min { get; }
        public double? Max { get; }
        public double? Step { get; }
        public string[]? Choices { get; }
        public string? Icon { get; }

        public ParamDef(string name, string label, string ptype, object def,
            double? min = null, double? max = null, double? step = null,
            string[]? choices = null, string? icon = null)
        {
            Name = name; Label = label; PType = ptype; Default = def;
            Min = min; Max = max; Step = step; Choices = choices; Icon = icon;
        }
    }

    /// <summary>Mirrors Python's EffectSettings dataclass.</summary>
    public class EffectSettings
    {
        public string Kind { get; set; }
        public bool Enabled { get; set; } = true;
        public bool Animate { get; set; } = true;
        public Dictionary<string, object> Params { get; set; } = new();
        public bool LockRandom { get; set; } = false;
        public bool BeatSync { get; set; } = false;
        public double BeatUnit { get; set; } = 1.0;

        public EffectSettings(string kind, bool enabled = true, Dictionary<string, object>? parameters = null)
        {
            Kind = kind;
            Enabled = enabled;
            Params = parameters ?? new Dictionary<string, object>();
            foreach (var def in EffectSchemas.SchemaFor(kind))
            {
                if (!Params.ContainsKey(def.Name)) Params[def.Name] = def.Default;
            }
        }

        public EffectSettings Clone()
        {
            return new EffectSettings(Kind, Enabled, new Dictionary<string, object>(Params))
            {
                Animate = Animate,
                LockRandom = LockRandom,
                BeatSync = BeatSync,
                BeatUnit = BeatUnit,
            };
        }
    }

    /// <summary>Mirrors Python's Transform dataclass (output framing).</summary>
    public class Transform
    {
        public int Width { get; set; } = 1080;
        public int Height { get; set; } = 1920;
        /// <summary>"cover" | "contain" | "stretch"</summary>
        public string Fit { get; set; } = "cover";
        public double ScaleX { get; set; } = 1.0;
        public double ScaleY { get; set; } = 1.0;
        public double OffsetX { get; set; } = 0.0;
        public double OffsetY { get; set; } = 0.0;

        /// <summary>Mirrors Python's transform_matrix(): builds the affine matrix
        /// used to map source video pixels into the output canvas.</summary>
        public double[,] TransformMatrix(int srcW, int srcH)
        {
            double baseScale;
            if (Fit == "stretch")
            {
                // handled separately by resize; identity here
                return new double[,] { { 1, 0, 0 }, { 0, 1, 0 } };
            }
            double scaleCover = Math.Max((double)Width / srcW, (double)Height / srcH);
            double scaleContain = Math.Min((double)Width / srcW, (double)Height / srcH);
            baseScale = Fit == "contain" ? scaleContain : scaleCover;

            double sx = baseScale * ScaleX;
            double sy = baseScale * ScaleY;
            double dstCx = Width / 2.0 + OffsetX;
            double dstCy = Height / 2.0 + OffsetY;
            double srcCx = srcW / 2.0;
            double srcCy = srcH / 2.0;

            // dst = R*S*(src - srcC) + dstC  (no rotation for frame transform)
            double tx = dstCx - sx * srcCx;
            double ty = dstCy - sy * srcCy;
            return new double[,] { { sx, 0, tx }, { 0, sy, ty } };
        }
    }

    /// <summary>Mirrors Python's ExportSettings dataclass.</summary>
    public class ExportSettings
    {
        public string Codec { get; set; } = "libx264";
        public int Crf { get; set; } = 18;
        public string Preset { get; set; } = "medium";
        public string MaxBitrate { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public bool AudioCopy { get; set; } = true;
        public double Fps { get; set; } = 30.0;
    }

    /// <summary>Mirrors Python's ProjectSettings dataclass: the full project state.</summary>
    public class ProjectSettings
    {
        public string SourcePath { get; set; } = "";
        public int MasterSeed { get; set; } = 1;
        public List<EffectSettings> Effects { get; set; } = new();
        public Transform Transform { get; set; } = new();
        public ExportSettings Export { get; set; } = new();
        public bool RandomizeOrder { get; set; } = false;
        public bool AnimateParams { get; set; } = true;
        public int AnimationAmount { get; set; } = 20;
        public bool AudioReactive { get; set; } = false;
        public string AudioPath { get; set; } = "";
        public int AudioIntensity { get; set; } = 60;
        /// <summary>"opacity" | "pulse" | "shake" | "flash" | "rgbsplit" | "strength"</summary>
        public string ReactionMode { get; set; } = "opacity";
        /// <summary>"loudness" | "bass" | "beat"</summary>
        public string ReactionSource { get; set; } = "loudness";
        public string PreviewQuality { get; set; } = "Auto";
        public double Bpm { get; set; } = 120.0;
        public int TimeSigNum { get; set; } = 4;
        public int TimeSigDen { get; set; } = 4;
        /// <summary>"off" | "bars" | "auto"</summary>
        public string SyncMode { get; set; } = "off";
        public int SyncBars { get; set; } = 8;
        public bool SyncAutoBars { get; set; } = true;
        public bool Interpolate { get; set; } = true;
        public int VideoRepeats { get; set; } = 1;
        public bool AutoRepeats { get; set; } = true;

        public ProjectSettings Clone()
        {
            var clone = (ProjectSettings)MemberwiseClone();
            clone.Effects = Effects.Select(e => e.Clone()).ToList();
            clone.Transform = new Transform
            {
                Width = Transform.Width, Height = Transform.Height, Fit = Transform.Fit,
                ScaleX = Transform.ScaleX, ScaleY = Transform.ScaleY,
                OffsetX = Transform.OffsetX, OffsetY = Transform.OffsetY,
            };
            clone.Export = new ExportSettings
            {
                Codec = Export.Codec, Crf = Export.Crf, Preset = Export.Preset,
                MaxBitrate = Export.MaxBitrate, OutputPath = Export.OutputPath,
                AudioCopy = Export.AudioCopy, Fps = Export.Fps,
            };
            return clone;
        }
    }

    /// <summary>Mirrors Python's EFFECT_SCHEMAS dict: one ParamDef list per effect kind.</summary>
    public static class EffectSchemas
    {
        public static readonly Dictionary<string, List<ParamDef>> Schemas = new();

        static EffectSchemas()
        {
            Schemas["color_grade"] = new List<ParamDef> {
                new("contrast", "Contrast", "float", 1.0, 0.0, 3.0, 0.05, icon: "circle.righthalf.filled"),
                new("saturation", "Saturation", "float", 1.0, 0.0, 3.0, 0.05, icon: "paintpalette"),
                new("brightness", "Brightness", "float", 0.0, -1.0, 1.0, 0.02, icon: "sun.max"),
                new("gamma", "Gamma", "float", 1.0, 0.2, 3.0, 0.05, icon: "camera.aperture"),
                new("hue", "Hue Shift", "float", 0.0, -180.0, 180.0, 1.0, icon: "rainbow"),
            };
            Schemas["posterize"] = new List<ParamDef> {
                new("levels", "Levels", "int", 6, 2, 32, 1, icon: "square.stack.3d.up"),
            };
            Schemas["edge_glow"] = new List<ParamDef> {
                new("threshold1", "Threshold Low", "float", 50.0, 0.0, 255.0, 1.0),
                new("threshold2", "Threshold High", "float", 150.0, 0.0, 255.0, 1.0),
                new("glow", "Glow", "float", 0.6, 0.0, 1.0, 0.02),
                new("color", "Glow Color", "color", "#A855F7"),
            };
            Schemas["chromatic_aberration"] = new List<ParamDef> {
                new("shift", "Shift (px)", "int", 6, 0, 60, 1),
                new("angle", "Angle", "float", 0.0, -180.0, 180.0, 1.0),
            };
            Schemas["noise"] = new List<ParamDef> {
                new("amount", "Amount", "float", 0.15, 0.0, 1.0, 0.01),
                new("mono", "Monochrome", "bool", false),
            };
            Schemas["sharpen"] = new List<ParamDef> {
                new("amount", "Amount", "float", 0.5, 0.0, 3.0, 0.05),
            };
            Schemas["glitch_blocks"] = new List<ParamDef> {
                new("amount", "Amount", "float", 0.3, 0.0, 1.0, 0.02),
                new("block_size", "Block Size", "int", 24, 4, 200, 2),
                new("max_shift", "Max Shift", "int", 40, 0, 400, 2),
            };
            Schemas["scanlines"] = new List<ParamDef> {
                new("opacity", "Opacity", "float", 0.3, 0.0, 1.0, 0.02),
                new("spacing", "Spacing (px)", "int", 3, 1, 20, 1),
            };
            Schemas["color_invert"] = new List<ParamDef> {
                new("amount", "Amount", "float", 1.0, 0.0, 1.0, 0.02),
            };
            Schemas["vignette"] = new List<ParamDef> {
                new("amount", "Amount", "float", 0.5, 0.0, 1.0, 0.02),
                new("radius", "Radius", "float", 1.0, 0.3, 2.0, 0.02),
            };

            var colorMapSchema = new List<ParamDef> {
                new("blend", "Blend Mode", "choice", "replace", choices: new[] { "replace", "multiply", "screen", "overlay" }),
            };
            for (int i = 1; i <= 12; i++)
            {
                colorMapSchema.Add(new ParamDef($"from{i}", $"From {i}", "color", "#000000"));
                colorMapSchema.Add(new ParamDef($"to{i}", $"To {i}", "color", "#000000"));
                colorMapSchema.Add(new ParamDef($"tolerance{i}", $"Tolerance {i}", "float", 0.15, 0.0, 1.0, 0.01));
                colorMapSchema.Add(new ParamDef($"active{i}", $"Active {i}", "bool", i == 1));
            }
            Schemas["color_map"] = colorMapSchema;

            Schemas["datamosh"] = new List<ParamDef> {
                new("amount", "Amount", "float", 0.5, 0.0, 1.0, 0.02),
                new("decay", "Decay", "float", 0.9, 0.5, 0.999, 0.005),
                new("interval", "Interval (frames)", "int", 12, 1, 120, 1),
            };
            Schemas["pixel_sort"] = new List<ParamDef> {
                new("threshold", "Threshold", "float", 0.4, 0.0, 1.0, 0.02),
                new("direction", "Direction", "choice", "horizontal", choices: new[] { "horizontal", "vertical" }),
                new("amount", "Amount", "float", 1.0, 0.0, 1.0, 0.02),
            };
            Schemas["vhs"] = new List<ParamDef> {
                new("warp", "Warp", "float", 0.3, 0.0, 1.0, 0.02),
                new("noise", "Noise", "float", 0.2, 0.0, 1.0, 0.02),
                new("color_bleed", "Color Bleed", "float", 0.3, 0.0, 1.0, 0.02),
                new("tracking", "Tracking Jitter", "float", 0.2, 0.0, 1.0, 0.02),
            };
            Schemas["dither"] = new List<ParamDef> {
                new("levels", "Levels", "int", 4, 2, 16, 1),
                new("amount", "Amount", "float", 1.0, 0.0, 1.0, 0.02),
            };
            Schemas["motion_glitch"] = new List<ParamDef> {
                new("amount", "Amount", "float", 0.5, 0.0, 1.0, 0.02),
                new("block_size", "Block Size", "int", 16, 4, 64, 2),
                new("threshold", "Motion Threshold", "float", 0.1, 0.0, 1.0, 0.01),
            };
            Schemas["motion_trails"] = new List<ParamDef> {
                new("length", "Trail Length (frames)", "int", 6, 1, 30, 1),
                new("decay", "Decay", "float", 0.7, 0.1, 0.99, 0.02),
            };
            Schemas["text_overlay"] = new List<ParamDef> {
                new("text", "Text", "string", "GLITCH"),
                new("font", "Font", "font", "Segoe UI"),
                new("size", "Size", "float", 64.0, 8.0, 400.0, 1.0),
                new("bold", "Bold", "bool", true),
                new("italic", "Italic", "bool", false),
                new("color", "Color", "color", "#FFFFFF"),
                new("outline_width", "Outline Width", "float", 0.0, 0.0, 20.0, 0.5),
                new("outline_color", "Outline Color", "color", "#000000"),
                new("shadow", "Shadow", "bool", false),
                new("opacity", "Opacity", "float", 1.0, 0.0, 1.0, 0.02),
                new("pos_x", "Position X", "float", 0.5, 0.0, 1.0, 0.01),
                new("pos_y", "Position Y", "float", 0.5, 0.0, 1.0, 0.01),
                new("animation", "Animation", "choice", "none", choices: new[] { "none", "rotate", "swing", "tumble", "float3d", "jolt" }),
                new("anim_speed", "Animation Speed", "float", 1.0, 0.1, 4.0, 0.1),
            };
        }

        public static List<ParamDef> SchemaFor(string kind) =>
            Schemas.TryGetValue(kind, out var s) ? s : new List<ParamDef>();
    }

    /// <summary>Mirrors Python's default_project(): the initial effect stack shown on first launch.</summary>
    public static class ProjectFactory
    {
        public static readonly string[] DefaultOrder = {
            "text_overlay", "color_grade", "posterize", "edge_glow",
            "chromatic_aberration", "sharpen", "color_invert", "color_map",
            "datamosh", "pixel_sort", "dither", "motion_glitch",
        };
        public static readonly HashSet<string> DefaultEnabled = new() {
            "color_grade", "posterize", "edge_glow", "chromatic_aberration", "sharpen",
        };

        public static ProjectSettings DefaultProject()
        {
            var project = new ProjectSettings();
            foreach (var kind in DefaultOrder)
            {
                project.Effects.Add(new EffectSettings(kind, DefaultEnabled.Contains(kind)));
            }
            return project;
        }
    }

    /// <summary>Mirrors Python's bar_duration / closest_bars / effective_bars / compute_sync_stats.</summary>
    public static class SyncHelpers
    {
        public static double BarDuration(double bpm, int timeSigNum) =>
            bpm <= 0 ? 0 : (60.0 / bpm) * timeSigNum;

        public static int ClosestBars(double seconds, double bpm, int timeSigNum)
        {
            double bar = BarDuration(bpm, timeSigNum);
            if (bar <= 0) return 1;
            return Math.Max(1, (int)Math.Round(seconds / bar));
        }

        public static int EffectiveBars(ProjectSettings project, double videoDuration)
        {
            if (project.SyncMode == "off") return project.SyncBars;
            if (project.SyncAutoBars) return ClosestBars(videoDuration, project.Bpm, project.TimeSigNum);
            return project.SyncBars;
        }

        public static (double barSeconds, int bars, double cycleSeconds, double drift) ComputeSyncStats(
            ProjectSettings project, double videoDuration)
        {
            double bar = BarDuration(project.Bpm, project.TimeSigNum);
            int bars = EffectiveBars(project, videoDuration);
            double cycle = bar * bars;
            double drift = cycle > 0 ? (videoDuration - cycle) / cycle : 0;
            return (bar, bars, cycle, drift);
        }
    }
}
