using SkiaSharp;

namespace Map.Assets;

/// <summary>
/// Map's own starter art: mountains, trees and buildings in three hand-defined styles, painted
/// by code. Nothing here is traced, copied or derived from anyone else's pictures: every stroke
/// is a path built from a seed, so the same seed gives the same file on any machine, and a new
/// seed gives a sibling variant. Drawing happens in a 1000-unit design space and is scaled to
/// the requested pixel size, which is why one asset can be rendered at any resolution.
/// </summary>
public static class StarterArtForge
{
    /// <summary>Long edge of the files written into the catalog. Mip levels serve every zoom below it.</summary>
    public const int RenderEdge = 2048;

    public enum Style { Ink, Painted, Flat }
    public enum Subject { Mountain, Tree, Building }

    public sealed record ArtSet(Subject Subject, Style Style, string Name, string Collection, string[] Tags, int VariantCount);

    public static IReadOnlyList<ArtSet> Sets { get; } =
    [
        new(Subject.Mountain, Style.Ink, "Горы · чернила", "Map · Горы", ["mountain", "ink", "parchment"], 3),
        new(Subject.Mountain, Style.Painted, "Горы · живопись", "Map · Горы", ["mountain", "painted", "snow"], 3),
        new(Subject.Mountain, Style.Flat, "Горы · плоские", "Map · Горы", ["mountain", "flat", "stylized"], 3),
        new(Subject.Tree, Style.Ink, "Дерево · чернила", "Map · Деревья", ["tree", "ink", "parchment"], 3),
        new(Subject.Tree, Style.Painted, "Ель · живопись", "Map · Деревья", ["tree", "painted", "conifer"], 3),
        new(Subject.Tree, Style.Flat, "Дерево · плоское", "Map · Деревья", ["tree", "flat", "stylized"], 3),
        new(Subject.Building, Style.Ink, "Дом · чернила", "Map · Постройки", ["building", "house", "ink"], 3),
        new(Subject.Building, Style.Painted, "Башня · живопись", "Map · Постройки", ["building", "tower", "painted"], 3),
        new(Subject.Building, Style.Flat, "Храм · плоский", "Map · Постройки", ["building", "temple", "flat"], 3),
    ];

    /// <summary>File name of one variant, stable so a re-run finds the file it wrote before.</summary>
    public static string FileName(ArtSet set, int variant) => $"{set.Name} {variant + 1}.png";

    /// <summary>Renders one variant as a PNG with tight alpha bounds; deterministic for a seed.</summary>
    public static byte[] Render(ArtSet set, int variant, int edge = RenderEdge)
    {
        ArgumentNullException.ThrowIfNull(set);
        var seed = (uint)(HashName(set.Name) * 31 + variant * 7919 + 17);
        var rng = new Rng(seed);
        var design = set.Subject switch
        {
            Subject.Mountain => new SKSize(1000, 640),
            Subject.Tree => new SKSize(700, 1000),
            _ => new SKSize(860, 1000),
        };
        var scale = edge / Math.Max(design.Width, design.Height);
        var info = new SKImageInfo((int)Math.Ceiling(design.Width * scale), (int)Math.Ceiling(design.Height * scale),
            SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Cannot create art surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        var art = new Painter(canvas, rng, design, scale, variant);
        switch (set.Subject, set.Style)
        {
            case (Subject.Mountain, Style.Ink): Mountains.Ink(art); break;
            case (Subject.Mountain, Style.Painted): Mountains.Painted(art); break;
            case (Subject.Mountain, Style.Flat): Mountains.Flat(art); break;
            case (Subject.Tree, Style.Ink): Trees.Ink(art); break;
            case (Subject.Tree, Style.Painted): Trees.Painted(art); break;
            case (Subject.Tree, Style.Flat): Trees.Flat(art); break;
            case (Subject.Building, Style.Ink): Buildings.Ink(art); break;
            case (Subject.Building, Style.Painted): Buildings.Painted(art); break;
            default: Buildings.Flat(art); break;
        }
        canvas.Flush();
        using var image = surface.Snapshot();
        using var cropped = CropToContent(image, (int)(edge * 0.015));
        using var png = cropped.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Cannot encode art.");
        return png.ToArray();
    }

    private static int HashName(string name)
    {
        unchecked
        {
            var hash = 2166136261;
            foreach (var c in name) hash = (hash ^ c) * 16777619;
            return (int)(hash & 0x7fffffff);
        }
    }

    private static SKImage CropToContent(SKImage image, int margin)
    {
        using var bitmap = SKBitmap.FromImage(image);
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        var pixels = bitmap.Pixels;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (pixels[y * bitmap.Width + x].Alpha < 3) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }
        if (right < 0) return SKImage.FromBitmap(bitmap);
        left = Math.Max(0, left - margin); top = Math.Max(0, top - margin);
        right = Math.Min(bitmap.Width - 1, right + margin); bottom = Math.Min(bitmap.Height - 1, bottom + margin);
        return image.Subset(new SKRectI(left, top, right + 1, bottom + 1));
    }

    // ------------------------------------------------------------------ toolkit

    /// <summary>SplitMix64: cheap, stable and good enough for silhouettes.</summary>
    public sealed class Rng(uint seed)
    {
        private ulong _state = seed * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL;

        public double Next()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return ((z ^ (z >> 31)) >> 11) * (1.0 / (1UL << 53));
        }

        public float Range(float min, float max) => (float)(min + (max - min) * Next());
        public int Int(int minInclusive, int maxExclusive) => minInclusive + (int)(Next() * (maxExclusive - minInclusive));
        public bool Chance(double p) => Next() < p;
        public uint Seed() => (uint)(Next() * uint.MaxValue);
    }

    /// <summary>Drawing context in design units with the helpers every style shares.</summary>
    public sealed class Painter(SKCanvas canvas, Rng rng, SKSize design, float scale, int variant)
    {
        public SKCanvas Canvas { get; } = canvas;
        public Rng Rng { get; } = rng;
        public float W { get; } = design.Width;
        public float H { get; } = design.Height;
        public float Scale { get; } = scale;
        public int Variant { get; } = variant;

        public SKPaint Fill(SKColor color) => new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };

        public SKPaint Stroke(SKColor color, float width, bool round = true) => new()
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color, StrokeWidth = width,
            StrokeCap = round ? SKStrokeCap.Round : SKStrokeCap.Butt, StrokeJoin = SKStrokeJoin.Round,
        };

        /// <summary>A pen that wobbles like a nib: the ink styles draw everything with it.</summary>
        public SKPaint Nib(SKColor color, float width, float wobble = 1.2f)
        {
            var paint = Stroke(color, width);
            paint.PathEffect = SKPathEffect.CreateDiscrete(6f, wobble, Rng.Seed());
            return paint;
        }

        public SKPaint Gradient(SKPoint from, SKPoint to, params SKColor[] colors) => new()
        {
            IsAntialias = true, Style = SKPaintStyle.Fill,
            Shader = SKShader.CreateLinearGradient(from, to, colors, null, SKShaderTileMode.Clamp),
        };

        public SKPaint Radial(SKPoint centre, float radius, params SKColor[] colors) => new()
        {
            IsAntialias = true, Style = SKPaintStyle.Fill,
            Shader = SKShader.CreateRadialGradient(centre, radius, colors, null, SKShaderTileMode.Clamp),
        };

        /// <summary>Grain: fractal noise multiplied into a shape so flat paint reads as material.</summary>
        public void Grain(SKPath clip, float frequency, byte alpha, SKBlendMode mode = SKBlendMode.Multiply)
        {
            using var paint = new SKPaint
            {
                Shader = SKShader.CreatePerlinNoiseFractalNoise(frequency, frequency, 4, Rng.Seed()),
                BlendMode = mode, Color = new SKColor(255, 255, 255, alpha),
            };
            Canvas.Save();
            Canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
            Canvas.DrawRect(new SKRect(0, 0, W, H), paint);
            Canvas.Restore();
        }

        /// <summary>Parallel hatching inside a shape, denser where <paramref name="spacing"/> is small.</summary>
        public void Hatch(SKPath clip, float angleDegrees, float spacing, SKColor ink, float width, float jitter = 0.35f)
        {
            Canvas.Save();
            Canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
            var bounds = clip.Bounds;
            var diagonal = MathF.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
            var centre = new SKPoint(bounds.MidX, bounds.MidY);
            Canvas.Translate(centre.X, centre.Y);
            Canvas.RotateDegrees(angleDegrees);
            using var pen = Nib(ink, width, 0.8f);
            for (var offset = -diagonal / 2; offset <= diagonal / 2; offset += spacing * Rng.Range(1 - jitter, 1 + jitter))
            {
                var y = offset;
                var x0 = -diagonal / 2 + Rng.Range(0, spacing);
                var x1 = diagonal / 2 - Rng.Range(0, spacing);
                Canvas.DrawLine(x0, y, x1, y, pen);
            }
            Canvas.Restore();
        }

        /// <summary>A soft elliptical contact shadow under an object, drawn first.</summary>
        public void GroundShadow(float cx, float cy, float rx, float ry, byte alpha = 70)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true, Color = new SKColor(20, 18, 30, alpha),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, ry * 0.45f),
            };
            Canvas.DrawOval(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry), paint);
        }

        /// <summary>Closed path through points with soft corners; the workhorse of every silhouette.</summary>
        public static SKPath Smooth(IReadOnlyList<SKPoint> points, float tension = 0.35f, bool close = true)
        {
            var path = new SKPath();
            if (points.Count < 2) return path;
            path.MoveTo(points[0]);
            var count = close ? points.Count : points.Count - 1;
            for (var i = 0; i < count; i++)
            {
                var p0 = points[(i - 1 + points.Count) % points.Count];
                var p1 = points[i];
                var p2 = points[(i + 1) % points.Count];
                var p3 = points[(i + 2) % points.Count];
                if (!close)
                {
                    p0 = points[Math.Max(0, i - 1)];
                    p3 = points[Math.Min(points.Count - 1, i + 2)];
                }
                var c1 = new SKPoint(p1.X + (p2.X - p0.X) * tension, p1.Y + (p2.Y - p0.Y) * tension);
                var c2 = new SKPoint(p2.X - (p3.X - p1.X) * tension, p2.Y - (p3.Y - p1.Y) * tension);
                path.CubicTo(c1, c2, p2);
            }
            if (close) path.Close();
            return path;
        }

        public static SKPath Polygon(params SKPoint[] points)
        {
            var path = new SKPath();
            path.MoveTo(points[0]);
            for (var i = 1; i < points.Length; i++) path.LineTo(points[i]);
            path.Close();
            return path;
        }

        /// <summary>A jagged open polyline between two points: ridges, rock edges, roof lines.</summary>
        public SKPath Jagged(SKPoint from, SKPoint to, int steps, float amplitude)
        {
            var path = new SKPath();
            path.MoveTo(from);
            for (var i = 1; i < steps; i++)
            {
                var t = i / (float)steps;
                var x = from.X + (to.X - from.X) * t;
                var y = from.Y + (to.Y - from.Y) * t;
                var nx = -(to.Y - from.Y); var ny = to.X - from.X;
                var length = MathF.Sqrt(nx * nx + ny * ny);
                var offset = Rng.Range(-amplitude, amplitude);
                path.LineTo(x + nx / length * offset, y + ny / length * offset);
            }
            path.LineTo(to);
            return path;
        }

        public static SKColor Mix(SKColor a, SKColor b, float t) => new(
            (byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t),
            (byte)(a.Blue + (b.Blue - a.Blue) * t), (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));

        public static SKColor Alpha(SKColor c, byte alpha) => c.WithAlpha(alpha);

        /// <summary>A slight, seed-driven hue drift so three variants of one set are siblings, not clones.</summary>
        public SKColor Drift(SKColor color, float amount = 0.06f)
        {
            color.ToHsl(out var h, out var s, out var l);
            var shift = (Variant - 1) * amount * 40f;
            return SKColor.FromHsl((h + shift + 360) % 360, Math.Clamp(s + (Variant - 1) * amount * 20f, 0, 100), l);
        }
    }

    // ------------------------------------------------------------------ mountains

    private static class Mountains
    {
        private static readonly SKColor InkColor = new(0x2E, 0x24, 0x18);

        private sealed record Peak(float X, float Top, float Left, float Right, float Base);

        private static List<Peak> Layout(Painter p, int count)
        {
            var peaks = new List<Peak>();
            var baseY = p.H * 0.9f;
            var spread = p.W * 0.76f;
            var start = (p.W - spread) / 2;
            for (var i = 0; i < count; i++)
            {
                var x = start + spread * (i + 0.5f) / count + p.Rng.Range(-30, 30);
                var height = p.H * p.Rng.Range(0.42f, 0.72f);
                if (i == count / 2) height = p.H * p.Rng.Range(0.7f, 0.82f);
                var halfWidth = height * p.Rng.Range(0.75f, 1.05f);
                peaks.Add(new Peak(x, baseY - height, x - halfWidth, x + halfWidth, baseY));
            }
            return peaks.OrderBy(peak => peak.Top).ToList();
        }

        private static SKPath Silhouette(Painter p, Peak peak, int segments = 7)
        {
            var left = new List<SKPoint> { new(peak.Left, peak.Base) };
            for (var i = 1; i < segments; i++)
            {
                var t = i / (float)segments;
                var x = peak.Left + (peak.X - peak.Left) * t;
                var y = peak.Base - (peak.Base - peak.Top) * MathF.Pow(t, 1.35f) + p.Rng.Range(-14, 14) * (1 - t);
                left.Add(new SKPoint(x, y));
            }
            left.Add(new SKPoint(peak.X, peak.Top));
            for (var i = segments - 1; i >= 1; i--)
            {
                var t = i / (float)segments;
                var x = peak.Right - (peak.Right - peak.X) * t;
                var y = peak.Base - (peak.Base - peak.Top) * MathF.Pow(t, 1.45f) + p.Rng.Range(-14, 14) * (1 - t);
                left.Add(new SKPoint(x, y));
            }
            left.Add(new SKPoint(peak.Right, peak.Base));
            var path = Painter.Smooth(left, 0.16f, close: false);
            path.LineTo(peak.Right, peak.Base);
            path.LineTo(peak.Left, peak.Base);
            path.Close();
            return path;
        }

        /// <summary>The shaded half: from the summit down the right slope, back along a wandering ridge.</summary>
        private static SKPath ShadeFace(Painter p, Peak peak)
        {
            var points = new List<SKPoint> { new(peak.X, peak.Top) };
            var ridgeEnd = new SKPoint(peak.X + (peak.Right - peak.X) * 0.16f, peak.Base);
            var ridge = p.Jagged(new SKPoint(peak.X, peak.Top), ridgeEnd, 6, 18);
            var path = new SKPath();
            path.AddPath(ridge);
            path.LineTo(peak.Right, peak.Base);
            path.LineTo(peak.X, peak.Top);
            path.Close();
            return path;
        }

        public static void Ink(Painter p)
        {
            var peaks = Layout(p, 2 + p.Variant % 2 + (p.Variant == 2 ? 1 : 0));
            using var paper = p.Fill(new SKColor(0xF3, 0xEA, 0xD6));
            using var outline = p.Nib(InkColor, 5.5f, 1.4f);
            using var thin = p.Nib(InkColor, 2.6f, 1f);
            using var faint = p.Nib(Painter.Alpha(InkColor, 150), 2f, 0.8f);
            foreach (var peak in peaks.OrderByDescending(peak => peak.Top))
            {
                using var body = Silhouette(p, peak);
                using var shade = ShadeFace(p, peak);
                p.Canvas.DrawPath(body, paper);
                p.Canvas.Save();
                p.Canvas.ClipPath(body, SKClipOperation.Intersect, true);
                // Shaded side: hatching that follows the right slope, a second pass near the ridge.
                var slope = MathF.Atan2(peak.Base - peak.Top, peak.Right - peak.X) * 180 / MathF.PI;
                p.Hatch(shade, -slope + 8, 12, InkColor, 2.2f);
                using (var dense = new SKPath(shade)) { dense.Transform(SKMatrix.CreateScale(0.5f, 1f, peak.X, peak.Top)); p.Hatch(dense, -slope - 20, 12, InkColor, 1.8f); }
                // Lit side: sparse strokes that thin out towards the summit, and a shoulder line.
                using (var foot = new SKPath())
                {
                    foot.AddRect(new SKRect(peak.Left, peak.Top + (peak.Base - peak.Top) * 0.55f, peak.X, peak.Base));
                    foot.Op(body, SKPathOp.Intersect, foot);
                    using var lit = new SKPath(foot);
                    lit.Op(shade, SKPathOp.Difference, lit);
                    p.Hatch(lit, 180 - slope * 0.8f, 22, Painter.Alpha(InkColor, 150), 1.6f, 0.5f);
                }
                for (var i = 0; i < 3; i++)
                {
                    var t = 0.3f + i * 0.2f;
                    var x0 = peak.X - (peak.X - peak.Left) * t;
                    var y0 = peak.Base - (peak.Base - peak.Top) * MathF.Pow(1 - t, 1.35f);
                    p.Canvas.DrawPath(p.Jagged(new SKPoint(x0, y0), new SKPoint(x0 - 70 * t, y0 + 110 * t), 5, 7), faint);
                }
                p.Canvas.Restore();
                p.Canvas.DrawPath(body, outline);
                p.Canvas.DrawPath(p.Jagged(new SKPoint(peak.X, peak.Top), new SKPoint(peak.X + (peak.Right - peak.X) * 0.16f, peak.Base), 6, 18), thin);
                for (var i = 0; i < 3; i++)
                {
                    var y = peak.Top + 26 + i * 22;
                    var half = (y - peak.Top) * 0.9f;
                    p.Canvas.DrawPath(p.Jagged(new SKPoint(peak.X - half, y), new SKPoint(peak.X + half, y + p.Rng.Range(-6, 6)), 5, 5), thin);
                }
            }
            using var dots = p.Nib(InkColor, 2.4f, 0.6f);
            var baseY = peaks[0].Base;
            for (var i = 0; i < 18; i++)
            {
                var x = p.W * 0.1f + p.Rng.Range(0, p.W * 0.8f);
                var y = baseY + p.Rng.Range(6, 34);
                p.Canvas.DrawLine(x, y, x + p.Rng.Range(10, 34), y - p.Rng.Range(2, 8), dots);
            }
        }

        public static void Painted(Painter p)
        {
            var peaks = Layout(p, 3 + p.Variant % 2);
            var rockLight = p.Drift(new SKColor(0xD9, 0xCB, 0xB2));
            var rockMid = p.Drift(new SKColor(0x9C, 0x8D, 0x78));
            var rockDark = p.Drift(new SKColor(0x55, 0x50, 0x57));
            var shadowCool = p.Drift(new SKColor(0x37, 0x40, 0x5A));
            var shadowLift = p.Drift(new SKColor(0x6B, 0x74, 0x8C));
            var snow = new SKColor(0xF8, 0xFA, 0xFD);
            var snowShade = new SKColor(0xB3, 0xC4, 0xDC);
            var baseY = peaks[0].Base;
            p.GroundShadow(p.W / 2, baseY + 8, p.W * 0.42f, 26, 90);
            foreach (var peak in peaks.OrderByDescending(peak => peak.Top))
            {
                using var body = Silhouette(p, peak, 9);
                using var shade = ShadeFace(p, peak);
                // Lit face: warm light at the summit that cools and darkens towards the foot.
                using var light = p.Gradient(new SKPoint(peak.X, peak.Top), new SKPoint(peak.Left + (peak.X - peak.Left) * 0.3f, peak.Base),
                    rockLight, Painter.Mix(rockLight, rockMid, 0.5f), rockMid, Painter.Mix(rockMid, rockDark, 0.5f));
                p.Canvas.DrawPath(body, light);
                p.Canvas.Save();
                p.Canvas.ClipPath(body, SKClipOperation.Intersect, true);
                // Shade face: deep blue-grey at the ridge, lifting towards the foot with bounced light.
                using (var dark = p.Gradient(new SKPoint(peak.X, peak.Top), new SKPoint(peak.Right, peak.Base), rockDark, shadowCool, shadowLift))
                {
                    p.Canvas.DrawPath(shade, dark);
                }
                // A second, softer facet inside the shade face so it is not one flat wedge.
                using (var facet = Painter.Polygon(new SKPoint(peak.X, peak.Top), new SKPoint(peak.X + (peak.Right - peak.X) * 0.55f, peak.Base),
                    new SKPoint(peak.X + (peak.Right - peak.X) * 0.16f, peak.Base)))
                using (var facetPaint = p.Gradient(new SKPoint(peak.X, peak.Top), new SKPoint(peak.X, peak.Base), Painter.Alpha(shadowCool, 0), Painter.Alpha(shadowLift, 120)))
                {
                    p.Canvas.DrawPath(facet, facetPaint);
                }
                // Strata: soft diagonal bands across the lit face, like layered rock catching the light.
                using (var stratum = p.Stroke(Painter.Alpha(rockDark, 45), 11f))
                using (var glint = p.Stroke(Painter.Alpha(snow, 45), 3f))
                {
                    for (var i = 0; i < 4; i++)
                    {
                        var t = 0.34f + i * 0.16f;
                        var y = peak.Top + (peak.Base - peak.Top) * t;
                        var from = new SKPoint(peak.Left + (peak.X - peak.Left) * (1 - t) * 0.95f + p.Rng.Range(0, 40), y + 14);
                        var to = new SKPoint(peak.X + (peak.Right - peak.X) * p.Rng.Range(0.02f, 0.14f), y - (peak.Base - peak.Top) * 0.1f);
                        var band = p.Jagged(from, to, 8, 9);
                        p.Canvas.DrawPath(band, stratum);
                        p.Canvas.Translate(0, -5);
                        p.Canvas.DrawPath(band, glint);
                        p.Canvas.Translate(0, 5);
                    }
                }
                // Gullies running down from the shoulder on the lit side.
                using (var crease = p.Stroke(Painter.Alpha(rockDark, 110), 3f))
                {
                    for (var i = 0; i < 2; i++)
                    {
                        var t = 0.35f + i * 0.3f;
                        var start = new SKPoint(peak.X - (peak.X - peak.Left) * t * 0.5f, peak.Top + (peak.Base - peak.Top) * t * 0.55f);
                        var end = new SKPoint(peak.Left + (peak.X - peak.Left) * (0.25f + 0.3f * i), peak.Base);
                        p.Canvas.DrawPath(p.Jagged(start, end, 8, 10), crease);
                    }
                }
                p.Grain(body, 0.014f, 60, SKBlendMode.Multiply);
                p.Grain(body, 0.06f, 45, SKBlendMode.Overlay);
                // Snow cap with a torn lower edge, brighter on the lit side.
                var capDepth = (peak.Base - peak.Top) * p.Rng.Range(0.24f, 0.36f);
                var cap = new List<SKPoint> { new(peak.X, peak.Top - 2) };
                var edgeY = peak.Top + capDepth;
                var halfLeft = (peak.X - peak.Left) * (capDepth / (peak.Base - peak.Top)) * 1.12f;
                var halfRight = (peak.Right - peak.X) * (capDepth / (peak.Base - peak.Top)) * 1.12f;
                cap.Add(new SKPoint(peak.X + halfRight, edgeY));
                for (var i = 5; i >= 0; i--)
                {
                    var t = i / 6f;
                    cap.Add(new SKPoint(peak.X - halfLeft + (halfLeft + halfRight) * t, edgeY + (i % 2 == 0 ? p.Rng.Range(14, 40) : p.Rng.Range(-10, 6))));
                }
                cap.Add(new SKPoint(peak.X - halfLeft, edgeY));
                using var capPath = Painter.Polygon(cap.ToArray());
                using var capPaint = p.Gradient(new SKPoint(peak.X - halfLeft, peak.Top), new SKPoint(peak.X + halfRight, edgeY), snow, snow, snowShade);
                p.Canvas.DrawPath(capPath, capPaint);
                using (var capShade = p.Fill(Painter.Alpha(shadowCool, 120)))
                using (var shadePart = new SKPath())
                {
                    shadePart.AddPath(capPath); shadePart.Op(shade, SKPathOp.Intersect, shadePart);
                    p.Canvas.DrawPath(shadePart, capShade);
                }
                p.Canvas.Restore();
                // Sunlit rim along the left silhouette.
                using (var rim = p.Stroke(Painter.Alpha(snow, 110), 3f))
                {
                    p.Canvas.Save();
                    p.Canvas.ClipPath(body, SKClipOperation.Intersect, true);
                    p.Canvas.Translate(3, 3);
                    p.Canvas.DrawPath(body, rim);
                    p.Canvas.Restore();
                }
            }
            using var haze = p.Gradient(new SKPoint(0, baseY - 90), new SKPoint(0, baseY), Painter.Alpha(snow, 0), Painter.Alpha(snow, 120));
            using var footprint = new SKPath();
            foreach (var peak in peaks) { using var body = Silhouette(p, peak, 5); footprint.Op(body, SKPathOp.Union, footprint); }
            p.Canvas.Save();
            p.Canvas.ClipPath(footprint, SKClipOperation.Intersect, true);
            p.Canvas.DrawRect(new SKRect(0, baseY - 90, p.W, baseY), haze);
            p.Canvas.Restore();
        }

        public static void Flat(Painter p)
        {
            var peaks = Layout(p, 2 + p.Variant);
            var light = p.Drift(new SKColor(0x7F, 0x9F, 0xB6));
            var mid = p.Drift(new SKColor(0x4F, 0x6E, 0x88));
            var dark = p.Drift(new SKColor(0x33, 0x48, 0x5F));
            var snow = new SKColor(0xEE, 0xF4, 0xF8);
            var snowShade = new SKColor(0xC5, 0xD5, 0xE3);
            var baseY = peaks[0].Base;
            using (var ground = p.Fill(Painter.Alpha(dark, 60)))
            {
                p.Canvas.DrawOval(new SKRect(p.W * 0.08f, baseY - 6, p.W * 0.92f, baseY + 22), ground);
            }
            foreach (var peak in peaks.OrderByDescending(peak => peak.Top))
            {
                var summit = new SKPoint(peak.X, peak.Top);
                var foot = new SKPoint(peak.X + (peak.Right - peak.X) * 0.18f, peak.Base);
                var shoulder = new SKPoint(peak.X - (peak.X - peak.Left) * 0.42f, peak.Top + (peak.Base - peak.Top) * 0.45f);
                using var lit = Painter.Polygon(new SKPoint(peak.Left, peak.Base), shoulder, summit, foot);
                using var shaded = Painter.Polygon(summit, new SKPoint(peak.Right, peak.Base), foot);
                using var litPaint = p.Fill(light);
                using var midPaint = p.Fill(mid);
                using var darkPaint = p.Fill(dark);
                p.Canvas.DrawPath(lit, litPaint);
                using (var inner = Painter.Polygon(shoulder, summit, foot, new SKPoint(peak.Left + (peak.X - peak.Left) * 0.55f, peak.Base)))
                {
                    p.Canvas.DrawPath(inner, midPaint);
                }
                p.Canvas.DrawPath(shaded, darkPaint);
                var capDepth = (peak.Base - peak.Top) * 0.26f;
                var ratio = capDepth / (peak.Base - peak.Top);
                var capLeft = new SKPoint(peak.X - (peak.X - peak.Left) * ratio, peak.Top + capDepth);
                var capRight = new SKPoint(peak.X + (peak.Right - peak.X) * ratio, peak.Top + capDepth);
                var capMid1 = new SKPoint(peak.X - (peak.X - capLeft.X) * 0.5f, peak.Top + capDepth * 0.72f);
                var capMid2 = new SKPoint(peak.X + (capRight.X - peak.X) * 0.45f, peak.Top + capDepth * 0.78f);
                using var capLit = Painter.Polygon(capLeft, capMid1, summit, new SKPoint(foot.X * ratio + summit.X * (1 - ratio), peak.Top + capDepth * 1.02f));
                using var capShade = Painter.Polygon(summit, capMid2, capRight, new SKPoint(foot.X * ratio + summit.X * (1 - ratio), peak.Top + capDepth * 1.02f));
                using var snowPaint = p.Fill(snow);
                using var snowShadePaint = p.Fill(snowShade);
                p.Canvas.DrawPath(capLit, snowPaint);
                p.Canvas.DrawPath(capShade, snowShadePaint);
            }
        }
    }

    // ------------------------------------------------------------------ trees

    private static class Trees
    {
        private static readonly SKColor InkColor = new(0x2E, 0x24, 0x18);

        private static SKPath Canopy(Painter p, float cx, float cy, float rx, float ry, int lobes)
        {
            var points = new List<SKPoint>();
            for (var i = 0; i < lobes; i++)
            {
                var angle = i / (float)lobes * MathF.Tau;
                var r = 1 + p.Rng.Range(-0.1f, 0.12f);
                points.Add(new SKPoint(cx + MathF.Cos(angle) * rx * r, cy + MathF.Sin(angle) * ry * r));
            }
            return Painter.Smooth(points, 0.3f);
        }

        public static void Ink(Painter p)
        {
            var cx = p.W / 2;
            var baseY = p.H * 0.93f;
            var trunkW = 34 + p.Variant * 6;
            var canopyR = p.W * 0.42f;
            var canopyY = p.H * 0.42f;
            using var paper = p.Fill(new SKColor(0xF3, 0xEA, 0xD6, 190));
            using var outline = p.Nib(InkColor, 5f, 1.4f);
            using var thin = p.Nib(InkColor, 2.4f, 1f);
            // Ground shadow as a hatched ellipse.
            using (var ground = new SKPath())
            {
                ground.AddOval(new SKRect(cx - canopyR * 0.7f, baseY - 12, cx + canopyR * 0.55f, baseY + 26));
                p.Hatch(ground, 12, 9, InkColor, 1.8f);
            }
            // Trunk with roots, drawn before the canopy covers its top.
            using var trunk = Painter.Smooth(
            [
                new SKPoint(cx - trunkW, baseY), new SKPoint(cx - trunkW * 0.55f, baseY - 90), new SKPoint(cx - trunkW * 0.5f, canopyY + canopyR * 0.5f),
                new SKPoint(cx + trunkW * 0.45f, canopyY + canopyR * 0.5f), new SKPoint(cx + trunkW * 0.6f, baseY - 80), new SKPoint(cx + trunkW * 1.2f, baseY),
            ], 0.2f);
            p.Canvas.DrawPath(trunk, paper);
            p.Canvas.DrawPath(trunk, outline);
            for (var i = 0; i < 5; i++)
            {
                var x = cx - trunkW * 0.4f + i * trunkW * 0.22f;
                p.Canvas.DrawLine(x, baseY - 20, x + p.Rng.Range(-6, 6), canopyY + canopyR * 0.7f, thin);
            }
            // Canopy: three overlapping scalloped masses, each with its own hatched underside.
            var masses = new[]
            {
                (X: cx - canopyR * 0.35f, Y: canopyY + canopyR * 0.2f, R: canopyR * 0.62f),
                (X: cx + canopyR * 0.38f, Y: canopyY + canopyR * 0.18f, R: canopyR * 0.6f),
                (X: cx, Y: canopyY - canopyR * 0.18f, R: canopyR * 0.72f),
            };
            foreach (var mass in masses)
            {
                using var path = Scalloped(p, mass.X, mass.Y, mass.R, 14);
                p.Canvas.DrawPath(path, paper);
                using (var lower = new SKPath())
                {
                    lower.AddRect(new SKRect(mass.X - mass.R * 1.2f, mass.Y + mass.R * 0.05f, mass.X + mass.R * 1.2f, mass.Y + mass.R * 1.3f));
                    lower.Op(path, SKPathOp.Intersect, lower);
                    p.Hatch(lower, 28, 12, InkColor, 2f);
                }
                using (var right = new SKPath())
                {
                    right.AddRect(new SKRect(mass.X + mass.R * 0.35f, mass.Y - mass.R * 1.2f, mass.X + mass.R * 1.3f, mass.Y + mass.R * 1.3f));
                    right.Op(path, SKPathOp.Intersect, right);
                    p.Hatch(right, 62, 13, InkColor, 1.8f);
                }
                p.Canvas.DrawPath(path, outline);
                // Leaf scribbles inside the mass.
                for (var i = 0; i < 9; i++)
                {
                    var angle = p.Rng.Range(0, MathF.Tau);
                    var r = p.Rng.Range(0.2f, 0.75f) * mass.R;
                    var x = mass.X + MathF.Cos(angle) * r;
                    var y = mass.Y + MathF.Sin(angle) * r;
                    p.Canvas.DrawPath(p.Jagged(new SKPoint(x, y), new SKPoint(x + p.Rng.Range(-22, 22), y + p.Rng.Range(6, 18)), 3, 5), thin);
                }
            }
        }

        private static SKPath Scalloped(Painter p, float cx, float cy, float r, int lobes)
        {
            var path = new SKPath();
            var start = new SKPoint(cx + r, cy);
            path.MoveTo(start);
            for (var i = 1; i <= lobes; i++)
            {
                var a0 = (i - 1) / (float)lobes * MathF.Tau;
                var a1 = i / (float)lobes * MathF.Tau;
                var mid = (a0 + a1) / 2;
                var bulge = r * p.Rng.Range(1.08f, 1.2f);
                var end = new SKPoint(cx + MathF.Cos(a1) * r * p.Rng.Range(0.94f, 1.02f), cy + MathF.Sin(a1) * r * p.Rng.Range(0.94f, 1.02f));
                path.QuadTo(cx + MathF.Cos(mid) * bulge, cy + MathF.Sin(mid) * bulge, end.X, end.Y);
            }
            path.Close();
            return path;
        }

        public static void Painted(Painter p)
        {
            var cx = p.W / 2;
            var baseY = p.H * 0.94f;
            var top = p.H * 0.05f;
            var dark = p.Drift(new SKColor(0x1B, 0x40, 0x2A));
            var mid = p.Drift(new SKColor(0x38, 0x76, 0x3E));
            var light = p.Drift(new SKColor(0x9E, 0xD2, 0x6E));
            var trunkDark = new SKColor(0x3F, 0x2A, 0x1A);
            var trunkLight = new SKColor(0x7A, 0x55, 0x33);
            p.GroundShadow(cx + 30, baseY, p.W * 0.34f, 30, 100);
            using (var trunk = p.Gradient(new SKPoint(cx - 20, 0), new SKPoint(cx + 24, 0), trunkLight, trunkDark))
            {
                p.Canvas.DrawRect(new SKRect(cx - 22, baseY - 190, cx + 22, baseY), trunk);
            }
            var tiers = 7 + p.Variant;
            var span = baseY - 110 - top;
            using var whole = new SKPath();
            var lean = p.Rng.Range(-0.04f, 0.04f);
            for (var i = tiers - 1; i >= 0; i--)
            {
                var t = i / (float)(tiers - 1);
                var y = top + span * MathF.Pow(t, 1.12f) + 110;
                var width = p.W * (0.12f + 0.38f * t) * p.Rng.Range(0.94f, 1.06f);
                var height = span / tiers * 1.9f * (0.7f + 0.3f * t);
                var axis = cx + lean * (y - top);
                // A bough: from the stem out and slightly up to each side, then a drooping, needled
                // lower edge back to the stem.
                var bough = new SKPath();
                var apex = new SKPoint(axis + p.Rng.Range(-5, 5), y - height);
                bough.MoveTo(apex);
                bough.LineTo(axis + width * 0.5f, y - height * 0.22f);
                var teeth = 14 + i * 2;
                for (var k = teeth; k >= 0; k--)
                {
                    var s = k / (float)teeth;
                    var x = axis - width * 0.5f + width * s;
                    var droop = MathF.Sin(s * MathF.PI) * height * 0.3f;
                    var needle = k % 2 == 0 ? height * p.Rng.Range(0.06f, 0.14f) : -height * 0.03f;
                    bough.LineTo(x + p.Rng.Range(-4, 4), y + droop + needle);
                }
                bough.LineTo(axis - width * 0.5f, y - height * 0.22f);
                bough.Close();
                using var paint = p.Gradient(new SKPoint(axis - width * 0.5f, y - height), new SKPoint(axis + width * 0.45f, y + 10), light, mid, mid, dark);
                p.Canvas.Save();
                p.Canvas.ClipPath(bough, SKClipOperation.Intersect, true);
                p.Canvas.DrawPath(bough, paint);
                // Shadow cast by the tier above: a soft dark band across the top of this bough.
                using (var under = new SKPaint { IsAntialias = true, Color = Painter.Alpha(dark, 150), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12) })
                {
                    p.Canvas.DrawRect(new SKRect(axis - width, y - height - 20, axis + width, y - height * 0.6f), under);
                }
                p.Canvas.Restore();
                whole.Op(bough, SKPathOp.Union, whole);
                p.Grain(bough, 0.07f, 70, SKBlendMode.Overlay);
                // Lit needles: short light strokes along the upper-left edge.
                using (var rim = p.Stroke(Painter.Alpha(light, 170), 3.4f))
                {
                    for (var k = 0; k < 6; k++)
                    {
                        var s = k / 6f;
                        var x = axis - width * 0.5f + (apex.X - (axis - width * 0.5f)) * s;
                        var yy = y - height * 0.22f + (apex.Y - (y - height * 0.22f)) * s;
                        p.Canvas.DrawLine(x, yy, x - 14, yy + 18, rim);
                    }
                }
                bough.Dispose();
            }
            p.Grain(whole, 0.02f, 45, SKBlendMode.Multiply);
        }

        public static void Flat(Painter p)
        {
            var cx = p.W / 2;
            var baseY = p.H * 0.93f;
            var g1 = p.Drift(new SKColor(0x6A, 0xB0, 0x5F));
            var g2 = p.Drift(new SKColor(0x45, 0x8A, 0x4B));
            var g3 = p.Drift(new SKColor(0x2E, 0x62, 0x37));
            var trunk = new SKColor(0x7A, 0x52, 0x30);
            using (var ground = p.Fill(new SKColor(0x2E, 0x62, 0x37, 70)))
            {
                p.Canvas.DrawOval(new SKRect(cx - p.W * 0.3f, baseY - 14, cx + p.W * 0.36f, baseY + 20), ground);
            }
            using var trunkPaint = p.Fill(trunk);
            p.Canvas.DrawRoundRect(new SKRect(cx - 26, baseY - 220, cx + 26, baseY), 12, 12, trunkPaint);
            if (p.Variant == 2)
            {
                // The third sibling is a stylised pine: three stacked triangles in three greens.
                var tiers = new[] { (Y: p.H * 0.75f, W: p.W * 0.78f, C: g3), (Y: p.H * 0.55f, W: p.W * 0.62f, C: g2), (Y: p.H * 0.36f, W: p.W * 0.46f, C: g1) };
                foreach (var tier in tiers)
                {
                    using var tri = Painter.Polygon(new SKPoint(cx - tier.W / 2, tier.Y), new SKPoint(cx, tier.Y - tier.W * 0.55f), new SKPoint(cx + tier.W / 2, tier.Y));
                    using var paint = p.Fill(tier.C);
                    p.Canvas.DrawPath(tri, paint);
                    using var shade = Painter.Polygon(new SKPoint(cx, tier.Y - tier.W * 0.55f), new SKPoint(cx + tier.W / 2, tier.Y), new SKPoint(cx, tier.Y));
                    using var shadePaint = p.Fill(Painter.Alpha(g3, 90));
                    p.Canvas.DrawPath(shade, shadePaint);
                }
                return;
            }
            // Round canopy from three discs, lit from the upper left.
            var r = p.W * 0.3f;
            var discs = new[]
            {
                (X: cx + r * 0.55f, Y: p.H * 0.5f, R: r * 0.95f, C: g3),
                (X: cx - r * 0.5f, Y: p.H * 0.52f, R: r * 0.9f, C: g2),
                (X: cx - r * 0.1f, Y: p.H * 0.33f, R: r * 1.05f, C: g2),
            };
            foreach (var disc in discs)
            {
                using var paint = p.Fill(disc.C);
                p.Canvas.DrawCircle(disc.X, disc.Y, disc.R, paint);
            }
            using (var highlight = p.Fill(g1))
            {
                p.Canvas.DrawCircle(cx - r * 0.35f, p.H * 0.3f, r * 0.62f, highlight);
                p.Canvas.DrawCircle(cx - r * 0.75f, p.H * 0.48f, r * 0.42f, highlight);
            }
            using (var deep = p.Fill(Painter.Alpha(g3, 140)))
            {
                p.Canvas.DrawCircle(cx + r * 0.45f, p.H * 0.62f, r * 0.55f, deep);
            }
        }
    }

    // ------------------------------------------------------------------ buildings

    private static class Buildings
    {
        private static readonly SKColor InkColor = new(0x2E, 0x24, 0x18);

        public static void Ink(Painter p)
        {
            var baseY = p.H * 0.9f;
            var w = p.W * 0.62f;
            var left = (p.W - w) / 2;
            var right = left + w;
            var wallTop = baseY - p.H * 0.34f;
            var ridgeY = wallTop - p.H * 0.3f;
            var depth = p.W * 0.16f;
            using var paper = p.Fill(new SKColor(0xF3, 0xEA, 0xD6, 200));
            using var outline = p.Nib(InkColor, 5f, 1.3f);
            using var thin = p.Nib(InkColor, 2.4f, 0.9f);
            // Ground hatch.
            using (var ground = new SKPath())
            {
                ground.AddOval(new SKRect(left - 40, baseY - 10, right + depth + 20, baseY + 28));
                p.Hatch(ground, 10, 9, InkColor, 1.6f);
            }
            // Front wall, side wall, roof (three-quarter view).
            using var front = Painter.Polygon(new SKPoint(left, baseY), new SKPoint(left, wallTop), new SKPoint(right, wallTop), new SKPoint(right, baseY));
            using var side = Painter.Polygon(new SKPoint(right, baseY), new SKPoint(right, wallTop), new SKPoint(right + depth, wallTop - depth * 0.5f), new SKPoint(right + depth, baseY - depth * 0.5f));
            using var gable = Painter.Polygon(new SKPoint(left, wallTop), new SKPoint(left + w / 2, ridgeY), new SKPoint(right, wallTop));
            using var roof = Painter.Polygon(new SKPoint(left + w / 2, ridgeY), new SKPoint(left + w / 2 + depth, ridgeY - depth * 0.5f), new SKPoint(right + depth + 24, wallTop - depth * 0.5f - 12), new SKPoint(right + 24, wallTop + 12));
            foreach (var part in new[] { front, side, gable, roof }) p.Canvas.DrawPath(part, paper);
            // Timber frame on the front wall.
            for (var i = 1; i < 4; i++) p.Canvas.DrawLine(left + w * i / 4, wallTop, left + w * i / 4, baseY, thin);
            p.Canvas.DrawLine(left, wallTop + (baseY - wallTop) * 0.5f, right, wallTop + (baseY - wallTop) * 0.5f, thin);
            p.Canvas.DrawLine(left + w / 4, wallTop, left + w / 2, wallTop + (baseY - wallTop) * 0.5f, thin);
            p.Canvas.DrawLine(left + w * 3 / 4, wallTop, left + w / 2, wallTop + (baseY - wallTop) * 0.5f, thin);
            // Door and windows.
            var doorW = w * 0.14f;
            p.Canvas.DrawRoundRect(new SKRect(left + w * 0.43f, baseY - p.H * 0.17f, left + w * 0.43f + doorW, baseY), doorW * 0.5f, doorW * 0.5f, outline);
            foreach (var x in new[] { left + w * 0.12f, left + w * 0.72f })
            {
                var window = new SKRect(x, wallTop + (baseY - wallTop) * 0.55f, x + w * 0.14f, wallTop + (baseY - wallTop) * 0.8f);
                p.Canvas.DrawRect(window, outline);
                p.Canvas.DrawLine(window.MidX, window.Top, window.MidX, window.Bottom, thin);
                p.Canvas.DrawLine(window.Left, window.MidY, window.Right, window.MidY, thin);
            }
            // Side wall in shadow; roof shingles as rows of short strokes.
            p.Hatch(side, 60, 11, InkColor, 2f);
            p.Canvas.Save();
            p.Canvas.ClipPath(roof, SKClipOperation.Intersect, true);
            for (var row = 0; row < 8; row++)
            {
                var t = row / 8f;
                var y0 = ridgeY + (wallTop - ridgeY) * t;
                p.Canvas.DrawLine(left + w / 2 + depth * t, y0 - depth * 0.5f * (1 - t) + 0, right + depth + 24, y0 - depth * 0.5f + 12 * t, thin);
                for (var k = 0; k < 6; k++)
                {
                    var x = left + w / 2 + depth * t + k * (w / 2 + 24) / 6 + p.Rng.Range(0, 12);
                    var y = y0 - depth * 0.5f * (1 - t) + (k * 2f);
                    p.Canvas.DrawLine(x, y, x + 2, y + 10, thin);
                }
            }
            p.Canvas.Restore();
            // Gable shading along the eave, chimney, outlines.
            using (var eave = new SKPath())
            {
                eave.AddRect(new SKRect(left, wallTop, right, wallTop + 26));
                eave.Op(front, SKPathOp.Intersect, eave);
                p.Hatch(eave, 62, 8, InkColor, 1.6f);
            }
            var chimneyX = left + w * 0.7f;
            using var chimney = Painter.Polygon(new SKPoint(chimneyX, ridgeY + 40), new SKPoint(chimneyX, ridgeY - 30), new SKPoint(chimneyX + 34, ridgeY - 30), new SKPoint(chimneyX + 34, ridgeY + 20));
            p.Canvas.DrawPath(chimney, paper);
            p.Canvas.DrawPath(chimney, outline);
            foreach (var part in new[] { front, side, gable, roof }) p.Canvas.DrawPath(part, outline);
            p.Canvas.DrawPath(p.Jagged(new SKPoint(chimneyX + 17, ridgeY - 40), new SKPoint(chimneyX + 40, ridgeY - 120), 5, 10), thin);
        }

        public static void Painted(Painter p)
        {
            var cx = p.W / 2;
            var baseY = p.H * 0.92f;
            var towerW = p.W * (0.36f + p.Variant * 0.03f);
            var towerTop = p.H * 0.38f;
            var roofTip = p.H * (0.06f + p.Variant * 0.02f);
            var stoneLight = p.Drift(new SKColor(0xC9, 0xBD, 0xA8));
            var stoneMid = p.Drift(new SKColor(0x8F, 0x85, 0x76));
            var stoneDark = p.Drift(new SKColor(0x4A, 0x45, 0x44));
            var roofLight = p.Drift(new SKColor(0xB4, 0x4E, 0x3E), 0.1f);
            var roofDark = p.Drift(new SKColor(0x5E, 0x27, 0x22), 0.1f);
            var glow = new SKColor(0xFF, 0xD2, 0x7A);
            p.GroundShadow(cx + towerW * 0.3f, baseY + 4, towerW * 0.9f, 34, 110);
            // Cylinder: light on the left, dark on the right, with a bright core.
            using var body = new SKPath();
            body.AddRect(new SKRect(cx - towerW / 2, towerTop, cx + towerW / 2, baseY));
            using var bottom = new SKPath();
            bottom.AddOval(new SKRect(cx - towerW / 2, baseY - towerW * 0.16f, cx + towerW / 2, baseY + towerW * 0.16f));
            body.Op(bottom, SKPathOp.Union, body);
            using (var shade = p.Gradient(new SKPoint(cx - towerW / 2, 0), new SKPoint(cx + towerW / 2, 0), stoneMid, stoneLight, stoneMid, stoneDark))
            {
                p.Canvas.DrawPath(body, shade);
            }
            // Stone courses: staggered blocks, lighter on top and darker below, faded by the shading.
            p.Canvas.Save();
            p.Canvas.ClipPath(body, SKClipOperation.Intersect, true);
            using (var mortar = p.Stroke(Painter.Alpha(stoneDark, 120), 2.2f))
            using (var blockHighlight = p.Stroke(Painter.Alpha(SKColors.White, 60), 2f))
            {
                var courseH = towerW * 0.09f;
                for (var row = 0; ; row++)
                {
                    var y = baseY - row * courseH;
                    if (y < towerTop - courseH) break;
                    p.Canvas.DrawLine(cx - towerW / 2, y, cx + towerW / 2, y, mortar);
                    var offset = row % 2 == 0 ? 0 : courseH * 0.9f;
                    for (var x = cx - towerW / 2 + offset; x < cx + towerW / 2; x += courseH * 1.8f * p.Rng.Range(0.85f, 1.15f))
                    {
                        p.Canvas.DrawLine(x, y, x, y - courseH, mortar);
                        p.Canvas.DrawLine(x + 3, y - courseH + 3, x + courseH * 1.2f, y - courseH + 3, blockHighlight);
                    }
                }
            }
            p.Grain(body, 0.03f, 90, SKBlendMode.Multiply);
            p.Grain(body, 0.09f, 50, SKBlendMode.Overlay);
            // Windows with warm light and a dark reveal.
            foreach (var (wx, wy) in new[] { (cx - towerW * 0.12f, towerTop + (baseY - towerTop) * 0.25f), (cx + towerW * 0.16f, towerTop + (baseY - towerTop) * 0.55f) })
            {
                var rect = new SKRect(wx - towerW * 0.06f, wy - towerW * 0.14f, wx + towerW * 0.06f, wy + towerW * 0.06f);
                using var reveal = p.Fill(stoneDark);
                p.Canvas.DrawRoundRect(rect, towerW * 0.06f, towerW * 0.06f, reveal);
                using var light = new SKPaint { IsAntialias = true, Color = Painter.Alpha(glow, 90), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, rect.Height * 0.35f) };
                p.Canvas.DrawOval(new SKRect(rect.Left - rect.Width * 0.6f, rect.Top - rect.Height * 0.3f, rect.Right + rect.Width * 0.6f, rect.Bottom + rect.Height * 0.3f), light);
                using var inner = p.Fill(Painter.Alpha(glow, 170));
                p.Canvas.DrawRoundRect(new SKRect(rect.Left + 6, rect.Top + 6, rect.Right - 6, rect.Bottom - 6), towerW * 0.045f, towerW * 0.045f, inner);
            }
            p.Canvas.Restore();
            // Battlement ring and the conical roof, lit on the left like the wall.
            var ringTop = towerTop - towerW * 0.06f;
            using (var ring = p.Gradient(new SKPoint(cx - towerW * 0.55f, 0), new SKPoint(cx + towerW * 0.55f, 0), stoneLight, stoneMid, stoneDark))
            {
                p.Canvas.DrawRoundRect(new SKRect(cx - towerW * 0.55f, ringTop, cx + towerW * 0.55f, towerTop + towerW * 0.08f), 8, 8, ring);
            }
            using var roof = Painter.Polygon(new SKPoint(cx - towerW * 0.6f, ringTop), new SKPoint(cx, roofTip), new SKPoint(cx + towerW * 0.6f, ringTop));
            using (var roofPaint = p.Gradient(new SKPoint(cx - towerW * 0.6f, 0), new SKPoint(cx + towerW * 0.6f, 0), roofLight, roofDark))
            {
                p.Canvas.DrawPath(roof, roofPaint);
            }
            p.Canvas.Save();
            p.Canvas.ClipPath(roof, SKClipOperation.Intersect, true);
            using (var tiles = p.Stroke(Painter.Alpha(roofDark, 140), 2.4f))
            {
                for (var y = ringTop; y > roofTip; y -= towerW * 0.07f)
                {
                    var t = (ringTop - y) / (ringTop - roofTip);
                    var half = towerW * 0.6f * (1 - t);
                    p.Canvas.DrawLine(cx - half, y, cx + half, y + 6, tiles);
                }
            }
            p.Grain(roof, 0.05f, 70, SKBlendMode.Multiply);
            p.Canvas.Restore();
            // A pennant at the tip.
            using (var pole = p.Stroke(stoneDark, 4f)) p.Canvas.DrawLine(cx, roofTip, cx, roofTip - towerW * 0.28f, pole);
            using var flag = Painter.Polygon(new SKPoint(cx, roofTip - towerW * 0.28f), new SKPoint(cx + towerW * 0.22f, roofTip - towerW * 0.21f), new SKPoint(cx, roofTip - towerW * 0.13f));
            using var flagPaint = p.Fill(p.Drift(new SKColor(0x2B, 0x5C, 0xA8), 0.2f));
            p.Canvas.DrawPath(flag, flagPaint);
        }

        public static void Flat(Painter p)
        {
            var baseY = p.H * 0.88f;
            var w = p.W * 0.56f;
            var depth = p.W * 0.22f;
            var left = (p.W - w - depth) / 2;
            var right = left + w;
            var wallH = p.H * 0.3f;
            var wallTop = baseY - wallH;
            var ridgeY = wallTop - p.H * 0.2f;
            var sand = p.Drift(new SKColor(0xE6, 0xCF, 0xA3), 0.08f);
            var sandShade = p.Drift(new SKColor(0xC4, 0xA2, 0x6E), 0.08f);
            var roofLight = p.Drift(new SKColor(0xC8, 0x5D, 0x44), 0.1f);
            var roofDark = p.Drift(new SKColor(0x8D, 0x3E, 0x30), 0.1f);
            var accent = new SKColor(0x3C, 0x5A, 0x7A);
            // Long flat shadow to the lower right.
            using (var shadowPaint = p.Fill(new SKColor(0x3A, 0x30, 0x28, 60)))
            using (var shadow = Painter.Polygon(new SKPoint(left + 10, baseY), new SKPoint(right + depth, baseY - depth * 0.45f), new SKPoint(right + depth + 120, baseY + 20), new SKPoint(left + 130, baseY + 64)))
            {
                p.Canvas.DrawPath(shadow, shadowPaint);
            }
            using var front = Painter.Polygon(new SKPoint(left, baseY), new SKPoint(left, wallTop), new SKPoint(right, wallTop), new SKPoint(right, baseY));
            using var side = Painter.Polygon(new SKPoint(right, baseY), new SKPoint(right, wallTop), new SKPoint(right + depth, wallTop - depth * 0.45f), new SKPoint(right + depth, baseY - depth * 0.45f));
            using var gable = Painter.Polygon(new SKPoint(left, wallTop), new SKPoint(left + w / 2, ridgeY), new SKPoint(right, wallTop));
            using var roof = Painter.Polygon(new SKPoint(left + w / 2, ridgeY), new SKPoint(left + w / 2 + depth, ridgeY - depth * 0.45f), new SKPoint(right + depth + 28, wallTop - depth * 0.45f - 14), new SKPoint(right + 28, wallTop + 14));
            using var eaveLeft = Painter.Polygon(new SKPoint(left - 28, wallTop + 14), new SKPoint(left + w / 2, ridgeY), new SKPoint(left + w / 2, ridgeY + 30), new SKPoint(left - 28, wallTop + 44));
            using var frontPaint = p.Fill(sand);
            using var sidePaint = p.Fill(sandShade);
            using var roofPaint = p.Fill(roofLight);
            using var roofDarkPaint = p.Fill(roofDark);
            p.Canvas.DrawPath(front, frontPaint);
            p.Canvas.DrawPath(gable, frontPaint);
            p.Canvas.DrawPath(side, sidePaint);
            p.Canvas.DrawPath(roof, roofPaint);
            p.Canvas.DrawPath(eaveLeft, roofDarkPaint);
            // Columns and a stepped base on the front, the temple's face.
            using var columnPaint = p.Fill(Painter.Mix(sand, SKColors.White, 0.35f));
            using var columnShade = p.Fill(sandShade);
            var columns = 3 + p.Variant;
            for (var i = 0; i < columns; i++)
            {
                var x = left + w * (i + 0.5f) / columns;
                var cw = w * 0.07f;
                p.Canvas.DrawRect(new SKRect(x - cw / 2, wallTop + 18, x + cw / 2, baseY - 24), columnPaint);
                p.Canvas.DrawRect(new SKRect(x + cw * 0.15f, wallTop + 18, x + cw / 2, baseY - 24), columnShade);
                p.Canvas.DrawRect(new SKRect(x - cw * 0.65f, wallTop + 8, x + cw * 0.65f, wallTop + 22), columnPaint);
            }
            using (var steps = p.Fill(sandShade))
            {
                p.Canvas.DrawRect(new SKRect(left - 16, baseY - 24, right + 16, baseY - 12), columnPaint);
                p.Canvas.DrawRect(new SKRect(left - 30, baseY - 12, right + 30, baseY), steps);
            }
            // Door and a pediment emblem.
            using var doorPaint = p.Fill(accent);
            var doorW = w * 0.12f;
            p.Canvas.DrawRoundRect(new SKRect(left + w / 2 - doorW / 2, baseY - wallH * 0.55f, left + w / 2 + doorW / 2, baseY - 24), doorW * 0.5f, doorW * 0.5f, doorPaint);
            p.Canvas.DrawCircle(left + w / 2, wallTop - (wallTop - ridgeY) * 0.35f, w * 0.05f, doorPaint);
        }
    }
}
