using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using GlitchFX.Models;

namespace GlitchFX.Effects
{
    /// <summary>
    /// Mirrors Python's TextOverlay effect. The macOS original renders text via
    /// Cocoa with a true 3D-perspective billboard for the rotate/swing/tumble/
    /// float3d/jolt animations; this first C# pass renders text with WPF
    /// (outline, shadow, color, opacity, position all supported) and
    /// approximates the animations as 2D transforms (rotation/scale/offset)
    /// instead of a full perspective warp. See README "Next steps".
    /// </summary>
    public class TextOverlayEffect : BaseEffect
    {
        public override string Kind => "text_overlay";
        public TextOverlayEffect(EffectSettings s) : base(s) { }

        public override Mat Apply(Mat frame, double time)
        {
            string text = ParamS("text", "GLITCH");
            if (string.IsNullOrWhiteSpace(text)) return frame.Clone();

            string fontName = ParamS("font", "Segoe UI");
            double size = AnimatedParam("size", time, 64.0);
            bool bold = ParamB("bold", true);
            bool italic = ParamB("italic", false);
            var color = ParseHex(ParamS("color", "#FFFFFF"));
            double outlineWidth = AnimatedParam("outline_width", time, 0.0);
            var outlineColor = ParseHex(ParamS("outline_color", "#000000"));
            bool shadow = ParamB("shadow", false);
            double opacity = AnimatedParam("opacity", time, 1.0);
            double posX = AnimatedParam("pos_x", time, 0.5);
            double posY = AnimatedParam("pos_y", time, 0.5);
            string animation = ParamS("animation", "none");
            double animSpeed = ParamD("anim_speed", 1.0);

            int w = frame.Cols, h = frame.Rows;
            var (angleDeg, scale, offsetX, offsetY) = ComputeAnimation(animation, time, animSpeed);

            using var textBitmap = RenderText(text, fontName, size, bold, italic, color, outlineWidth, outlineColor, shadow);
            using var rotatedScaled = RotateAndScale(textBitmap, angleDeg, scale);

            int destCx = (int)(w * posX + offsetX);
            int destCy = (int)(h * posY + offsetY);
            var outMat = frame.Clone();
            AlphaComposite(outMat, rotatedScaled, destCx, destCy, opacity);
            return outMat;
        }

        private static (double angle, double scale, double ox, double oy) ComputeAnimation(string animation, double time, double speed)
        {
            double t = time * speed;
            switch (animation)
            {
                case "rotate": return (t * 60.0 % 360.0, 1.0, 0, 0);
                case "swing": return (Math.Sin(t * 2.0) * 25.0, 1.0, 0, 0);
                case "tumble": return (t * 90.0 % 360.0, 0.85 + 0.15 * Math.Cos(t * 2.0), 0, 0);
                case "float3d": return (Math.Sin(t) * 10.0, 1.0 + 0.05 * Math.Sin(t * 1.5), 0, Math.Sin(t * 1.2) * 8.0);
                case "jolt": return (Math.Sin(t * 12.0) * 4.0, 1.0, Math.Sin(t * 20.0) * 3.0, Math.Cos(t * 18.0) * 3.0);
                default: return (0, 1.0, 0, 0);
            }
        }

        private static Mat RenderText(string text, string fontName, double size, bool bold, bool italic,
            Vec3b color, double outlineWidth, Vec3b outlineColor, bool shadow)
        {
            var typeface = new Typeface(new FontFamily(fontName),
                italic ? FontStyles.Italic : FontStyles.Normal,
                bold ? FontWeights.Bold : FontWeights.Normal,
                FontStretches.Normal);
            var brush = new SolidColorBrush(Color.FromRgb(color.Item2, color.Item1, color.Item0));
            var formatted = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, size, brush, 1.0);

            int pad = (int)Math.Ceiling(outlineWidth) + (shadow ? 8 : 0) + 4;
            int bw = (int)Math.Ceiling(formatted.Width) + pad * 2;
            int bh = (int)Math.Ceiling(formatted.Height) + pad * 2;
            bw = Math.Max(bw, 1); bh = Math.Max(bh, 1);

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                if (shadow)
                {
                    var shadowText = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, typeface, size, new SolidColorBrush(Colors.Black) { Opacity = 0.6 }, 1.0);
                    dc.DrawText(shadowText, new System.Windows.Point(pad + 3, pad + 3));
                }
                if (outlineWidth > 0.01)
                {
                    var outlineBrush = new SolidColorBrush(Color.FromRgb(outlineColor.Item2, outlineColor.Item1, outlineColor.Item0));
                    for (double a = 0; a < Math.PI * 2; a += Math.PI / 8)
                    {
                        double ox = Math.Cos(a) * outlineWidth, oy = Math.Sin(a) * outlineWidth;
                        var outlineText = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight, typeface, size, outlineBrush, 1.0);
                        dc.DrawText(outlineText, new System.Windows.Point(pad + ox, pad + oy));
                    }
                }
                dc.DrawText(formatted, new System.Windows.Point(pad, pad));
            }
            var rtb = new RenderTargetBitmap(bw, bh, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            var pixels = new byte[bw * bh * 4];
            rtb.CopyPixels(pixels, bw * 4, 0);

            // RenderTargetBitmap produces premultiplied-alpha BGRA (Pbgra32), but
            // AlphaComposite below treats this buffer as straight (non-premultiplied)
            // color + alpha. Without unpremultiplying here, every anti-aliased edge
            // of the text/outline/shadow keeps its darker premultiplied RGB, which
            // shows up as a dark halo once composited onto the frame. Divide each
            // color channel back out by its own alpha to correct this.
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte a = pixels[i + 3];
                if (a > 0 && a < 255)
                {
                    pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
                    pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
                    pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
                }
            }

            var mat = new Mat(bh, bw, MatType.CV_8UC4);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, mat.Data, pixels.Length);
            return mat;
        }

        private static Mat RotateAndScale(Mat src, double angleDeg, double scale)
        {
            var center = new Point2f(src.Cols / 2f, src.Rows / 2f);
            using var m = Cv2.GetRotationMatrix2D(center, angleDeg, scale);
            var dst = new Mat();
            Cv2.WarpAffine(src, dst, m, src.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
            return dst;
        }

        private static void AlphaComposite(Mat baseFrame, Mat overlayBgra, int centerX, int centerY, double opacity)
        {
            int ow = overlayBgra.Cols, oh = overlayBgra.Rows;
            int x0 = centerX - ow / 2, y0 = centerY - oh / 2;
            int srcX0 = Math.Max(0, -x0), srcY0 = Math.Max(0, -y0);
            int dstX0 = Math.Max(0, x0), dstY0 = Math.Max(0, y0);
            int copyW = Math.Min(ow - srcX0, baseFrame.Cols - dstX0);
            int copyH = Math.Min(oh - srcY0, baseFrame.Rows - dstY0);
            if (copyW <= 0 || copyH <= 0) return;

            using var overlayRegion = overlayBgra[srcY0, srcY0 + copyH, srcX0, srcX0 + copyW];
            using var baseRegion = baseFrame[dstY0, dstY0 + copyH, dstX0, dstX0 + copyW];
            Cv2.Split(overlayRegion, out Mat[] channels);
            using var b = channels[0]; using var g = channels[1]; using var r = channels[2]; using var a = channels[3];
            using var alphaF = new Mat();
            a.ConvertTo(alphaF, MatType.CV_32FC1, opacity / 255.0);
            using var alpha3 = new Mat();
            Cv2.CvtColor(alphaF, alpha3, ColorConversionCodes.GRAY2BGR);

            using var overlayBgr = new Mat();
            Cv2.Merge(new[] { b, g, r }, overlayBgr);
            using var overlayF = new Mat();
            overlayBgr.ConvertTo(overlayF, MatType.CV_32FC3);
            using var baseF = new Mat();
            baseRegion.ConvertTo(baseF, MatType.CV_32FC3);

            // out = overlay*alpha + base*(1-alpha), written with explicit Cv2
            // calls (Subtract/Multiply/Add) so we never rely on Mat operator
            // overloads or MatExpr, which don't expose ConvertTo/instance
            // methods the same way a concrete Mat does.
            using var onesLike = new Mat(alpha3.Size(), alpha3.Type(), Scalar.All(1.0));
            using var invAlpha = new Mat();
            Cv2.Subtract(onesLike, alpha3, invAlpha);
            using var overlayWeighted = new Mat();
            Cv2.Multiply(overlayF, alpha3, overlayWeighted);
            using var baseWeighted = new Mat();
            Cv2.Multiply(baseF, invAlpha, baseWeighted);
            using var blended = new Mat();
            Cv2.Add(overlayWeighted, baseWeighted, blended);

            using var blended8 = new Mat();
            blended.ConvertTo(blended8, MatType.CV_8UC3);
            blended8.CopyTo(baseRegion);
        }

        private static Vec3b ParseHex(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length < 6) return new Vec3b(255, 255, 255);
            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            return new Vec3b((byte)b, (byte)g, (byte)r);
        }
    }
}
