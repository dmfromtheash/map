using Map.Core.Documents;
using SkiaSharp;

namespace Map.Export;

/// <summary>How the isometric view is turned and how tall things stand (R38).</summary>
public sealed record IsometricSettings
{
    /// <summary>Quarter turns of the map before it is viewed (0–3): the four sides to look from.</summary>
    public int Turn { get; init; }

    /// <summary>Multiplies every height: 1 is the usual town, 2 a town of tall houses.</summary>
    public double HeightScale { get; init; } = 1;

    public int Width { get; init; } = 2000;
}

/// <summary>
/// R38: a map seen in isometry (2:1, the light from the upper left). The ground - paper, squares,
/// water, roads - lies flat; houses, landmarks, walls and towers stand up as blocks with roofs,
/// cast shadows and are drawn back to front. Until the owner's isometric set exists the buildings
/// are drawn procedurally: plaster walls, the roof colour of the plan, ink outlines.
/// </summary>
public static class IsometricRenderer
{
    private const string Ink = "#FF2E241C";
    private static readonly double Cos30 = Math.Cos(Math.PI / 6), Sin30 = .5;

    // The light comes from the left of the picture: the south-west sides are lit, shadows fall right.
    private const double LightX = -.35, LightY = .94;

    private sealed record Flat(SKPoint[] Points, SKColor Fill, SKColor? Stroke, float StrokeWidth);
    private sealed record Path(SKPoint[] Points, SKColor Color, float Width);
    private sealed record Solid(SKPoint[] Footprint, double Height, double RoofHeight, string Walls, string Roof, SolidRoof Kind, double Depth);
    private enum SolidRoof { Gable, Pyramid, Cone, Flat }

    public static SKBitmap Render(MapDocument document, IsometricSettings settings)
    {
        ArgumentNullException.ThrowIfNull(document);
        var (cx, cy) = (document.Canvas.Width / 2.0, document.Canvas.Height / 2.0);
        var turn = ((settings.Turn % 4) + 4) % 4;
        SKPoint Turn(double x, double y)
        {
            var (dx, dy) = (x - cx, y - cy);
            for (var i = 0; i < turn; i++) (dx, dy) = (-dy, dx);
            return new SKPoint((float)(cx + dx), (float)(cy + dy));
        }

        var flats = new List<Flat>();
        var paths = new List<Path>();
        var solids = new List<Solid>();
        var labels = new List<(SKPoint At, string Text, int Importance)>();
        foreach (var layer in document.Layers.OfType<ObjectLayer>().Where(l => l.Visible))
        {
            foreach (var road in layer.Roads.Where(r => r.Visible))
            {
                var nodes = road.Nodes.ToDictionary(n => n.Id);
                foreach (var branch in road.Branches)
                {
                    var points = branch.Vertices.Where(v => nodes.ContainsKey(v.NodeId)).Select(v => Turn(nodes[v.NodeId].X, nodes[v.NodeId].Y)).ToArray();
                    if (points.Length < 2) continue;
                    var width = branch.Vertices.Average(v => v.Width) * road.WidthScale;
                    paths.Add(new Path(points, Color(road.Style.SurfaceColor), (float)width));
                }
            }

            foreach (var item in layer.Objects.Where(o => o.Visible))
            {
                if (item.Kind == SceneObjectKind.Text && item.Text is { } text)
                {
                    labels.Add((Turn(item.X + (item.Width / 2), item.Y + (item.Height / 2)), text.Content, text.Importance));
                    continue;
                }
                if (item.Line is { } line)
                {
                    var points = VectorGeometry.Flatten(item, line.Nodes, closed: false).Select(p => Turn(p.X, p.Y)).ToArray();
                    if (item.Name == "Стена")
                    {
                        // A wall stands up segment by segment, so each part sorts on its own.
                        var thick = Math.Max(4, line.Stroke.Width);
                        for (var i = 0; i + 1 < points.Length; i++)
                            solids.Add(Box(Segment(points[i], points[i + 1], thick), 22 * settings.HeightScale, 0, "#FFB8AE9C", "#FF8E8476", SolidRoof.Flat));
                    }
                    else paths.Add(new Path(points, Color(line.Stroke.Color), (float)Math.Max(1, line.Stroke.Width)));
                    continue;
                }
                if (item.Shape is not { } shape || shape.Regions.Count == 0) continue;
                var outline = VectorGeometry.RegionOutline(item, shape.Regions[0]).Select(p => Turn(p.X, p.Y)).ToArray();
                if (outline.Length > 3 && outline[0] == outline[^1]) outline = outline[..^1];
                var fill = Color(shape.Fill.Color).WithAlpha((byte)Math.Clamp(shape.Fill.Opacity * 255, 0, 255));
                var seed = (item.Id.Value.GetHashCode() & 0xFFFF) / 65535.0;
                var area = Math.Abs(Area(outline));
                switch (item.Name)
                {
                    case "Дом":
                        solids.Add(Box(outline, (9 + (seed * 7) + Math.Min(6, area / 120)) * settings.HeightScale, Math.Sqrt(area) * .35, Plaster(seed), shape.Fill.Color, SolidRoof.Gable));
                        break;
                    case "Храм":
                        solids.Add(Box(outline, 26 * settings.HeightScale, Math.Sqrt(area) * .45, "#FFF1EAD8", "#FF7D6A5A", SolidRoof.Gable));
                        solids.Add(Box(Square(Centroid(outline), Math.Sqrt(area) * .22), 52 * settings.HeightScale, 30 * settings.HeightScale, "#FFF1EAD8", "#FF6E5C4E", SolidRoof.Pyramid));
                        break;
                    case "Ратуша":
                        solids.Add(Box(outline, 20 * settings.HeightScale, Math.Sqrt(area) * .3, "#FFE3D2AE", "#FF9A4E3A", SolidRoof.Gable));
                        solids.Add(Box(Square(Centroid(outline), Math.Sqrt(area) * .18), 40 * settings.HeightScale, 14 * settings.HeightScale, "#FFE3D2AE", "#FF5A4E46", SolidRoof.Pyramid));
                        break;
                    case "Гильдия":
                        solids.Add(Box(outline, 18 * settings.HeightScale, Math.Sqrt(area) * .3, "#FFE6D6B8", shape.Fill.Color, SolidRoof.Gable));
                        break;
                    case "Башня":
                        solids.Add(Box(outline, 34 * settings.HeightScale, 16 * settings.HeightScale, "#FFB8AE9C", "#FF6A4A3C", SolidRoof.Cone));
                        break;
                    default:
                        flats.Add(new Flat(outline, fill, shape.Stroke.Width > 0 ? Color(shape.Stroke.Color) : null, (float)shape.Stroke.Width));
                        break;
                }
            }
        }

        // The ground is the part of the map the town takes, with a margin, inside the canvas.
        var turnedCanvas = new[] { Turn(0, 0), Turn(document.Canvas.Width, document.Canvas.Height) };
        var (gl, gt, gr, gb) = (turnedCanvas.Min(p => p.X), turnedCanvas.Min(p => p.Y), turnedCanvas.Max(p => p.X), turnedCanvas.Max(p => p.Y));
        if (solids.Count > 0)
        {
            var b = solids.SelectMany(x => x.Footprint).ToList();
            var margin = Math.Max(b.Max(p => p.X) - b.Min(p => p.X), b.Max(p => p.Y) - b.Min(p => p.Y)) * .08f;
            (gl, gt, gr, gb) = (Math.Max(gl, b.Min(p => p.X) - margin), Math.Max(gt, b.Min(p => p.Y) - margin), Math.Min(gr, b.Max(p => p.X) + margin), Math.Min(gb, b.Max(p => p.Y) + margin));
        }
        var corners = Clockwise([new SKPoint(gl, gt), new SKPoint(gr, gt), new SKPoint(gr, gb), new SKPoint(gl, gb)]);
        var projected = corners.Select(p => Project(p, 0)).ToArray();
        var minX = projected.Min(p => p.X); var maxX = projected.Max(p => p.X);
        var minY = projected.Min(p => p.Y) - 80; var maxY = projected.Max(p => p.Y) + 30;
        var scale = (float)((settings.Width - 40) / (maxX - minX));
        var height = (int)Math.Ceiling(((maxY - minY) * scale) + 40);
        SKPoint P(SKPoint p, double z) { var q = Project(p, z); return new SKPoint(((q.X - minX) * scale) + 20, ((q.Y - minY) * scale) + 20); }

        var bitmap = new SKBitmap(settings.Width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        var paper = document.Layers.OfType<BaseColorLayer>().FirstOrDefault() is { } baseLayer ? Color(baseLayer.Color) : Color(document.Canvas.Background);
        canvas.Clear(Shade(paper, .93f));
        using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var inkPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = Color(Ink), StrokeWidth = Math.Max(.8f, scale * .9f), StrokeJoin = SKStrokeJoin.Round };

        // The ground: a slab of the canvas with its earth edge.
        var slab = 10 / scale;
        foreach (var (a, b) in Edges(corners))
            if (Faces(a, b)) Polygon(canvas, [P(a, 0), P(b, 0), P(b, -slab * 4), P(a, -slab * 4)], Shade(paper, .62f), fillPaint, inkPaint);
        using var ground = Outline([.. corners.Select(c => P(c, 0))]);
        fillPaint.Color = paper;
        canvas.DrawPath(ground, fillPaint);
        canvas.Save();
        canvas.ClipPath(ground, antialias: true);

        foreach (var flat in flats)
        {
            fillPaint.Color = flat.Fill;
            using var path = Outline([.. flat.Points.Select(p => P(p, 0))]);
            canvas.DrawPath(path, fillPaint);
            if (flat.Stroke is { } stroke) { inkPaint.Color = stroke; canvas.DrawPath(path, inkPaint); inkPaint.Color = Color(Ink); }
        }
        using (var roadPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round })
        {
            foreach (var road in paths)
            {
                using var line = new SKPath();
                line.MoveTo(P(road.Points[0], 0));
                foreach (var point in road.Points.Skip(1)) line.LineTo(P(point, 0));
                roadPaint.Color = Shade(road.Color, .7f); roadPaint.StrokeWidth = (road.Width * scale * .82f) + 1.5f;
                canvas.DrawPath(line, roadPaint);
                roadPaint.Color = road.Color; roadPaint.StrokeWidth = road.Width * scale * .82f;
                canvas.DrawPath(line, roadPaint);
            }
        }

        // Shadows on the ground, one layer so overlaps do not darken twice.
        using (var shadowLayer = new SKPaint { Color = new SKColor(0, 0, 0, 70) })
        {
            canvas.SaveLayer(shadowLayer);
            using var shadowPaint = new SKPaint { IsAntialias = true, Color = SKColors.Black };
            foreach (var solid in solids)
            {
                var top = solid.Height + (solid.RoofHeight * .6);
                var cast = solid.Footprint.Select(p => new SKPoint(p.X - (float)(top * LightX * .9), p.Y - (float)(top * LightY * .9))).ToArray();
                using var hull = Outline([.. Hull([.. solid.Footprint, .. cast]).Select(p => P(p, 0))]);
                canvas.DrawPath(hull, shadowPaint);
            }
            canvas.Restore();
        }

        canvas.Restore();
        canvas.DrawPath(ground, inkPaint);

        foreach (var solid in solids.OrderBy(s => s.Depth))
            DrawSolid(canvas, solid, P, fillPaint, inkPaint);

        // Names stand upright over their places, plain with a paper halo.
        using var typeface = SKTypeface.FromFamilyName("Georgia", SKFontStyle.Bold) ?? SKTypeface.Default;
        foreach (var (at, text, importance) in labels.Where(l => l.Importance >= 3).OrderBy(l => l.Importance))
        {
            using var font = new SKFont(typeface, importance >= 5 ? 30 : 18);
            var p = P(at, 60);
            using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5, Color = paper.WithAlpha(230), StrokeJoin = SKStrokeJoin.Round };
            using var ink = new SKPaint { IsAntialias = true, Color = Color(Ink) };
            canvas.DrawText(text, p.X, p.Y, SKTextAlign.Center, font, halo);
            canvas.DrawText(text, p.X, p.Y, SKTextAlign.Center, font, ink);
        }
        return bitmap;
    }

    public static byte[] RenderPng(MapDocument document, IsometricSettings settings)
    {
        using var bitmap = Render(document, settings);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void DrawSolid(SKCanvas canvas, Solid solid, Func<SKPoint, double, SKPoint> P, SKPaint fill, SKPaint ink)
    {
        var foot = Clockwise(solid.Footprint);
        // Walls facing the viewer, lit by how they face the light.
        foreach (var (a, b) in Edges(foot))
        {
            if (!Faces(a, b)) continue;
            var (nx, ny) = Normal(a, b);
            var light = Math.Clamp(.66 + (.36 * ((nx * LightX) + (ny * LightY))), .45, 1);
            Polygon(canvas, [P(a, 0), P(b, 0), P(b, solid.Height), P(a, solid.Height)], Shade(solid.Walls, (float)light), fill, ink);
        }

        var roof = Color(solid.Roof);
        var top = solid.Height;
        switch (solid.Kind)
        {
            case SolidRoof.Flat:
                Polygon(canvas, [.. foot.Select(p => P(p, top))], Shade(solid.Walls, .95f), fill, ink);
                break;
            case SolidRoof.Gable when foot.Length == 4:
            {
                // The ridge runs along the longer side, from the middle of one short side to the other.
                var longFirst = Distance(foot[0], foot[1]) >= Distance(foot[1], foot[2]);
                var (s0, s1, s2, s3) = longFirst ? (foot[0], foot[1], foot[2], foot[3]) : (foot[1], foot[2], foot[3], foot[0]);
                var r0 = Mid(s3, s0); var r1 = Mid(s1, s2);
                var ridge = top + solid.RoofHeight;
                var faces = new (SKPoint[] Base, SKPoint[] Ridge)[]
                {
                    ([s0, s1], [r1, r0]), ([s2, s3], [r0, r1]), ([s1, s2], [r1]), ([s3, s0], [r0]),
                };
                foreach (var (bases, ridges) in faces.OrderBy(f => f.Base.Average(p => p.X + p.Y)))
                {
                    var (nx, ny) = Normal(bases[0], bases[1]);
                    var light = Math.Clamp(.78 + (.3 * ((nx * LightX) + (ny * LightY))), .5, 1.1);
                    var points = bases.Select(p => P(p, top)).Concat(ridges.Select(p => P(p, ridge))).ToArray();
                    Polygon(canvas, points, Shade(roof, (float)light), fill, ink);
                }
                break;
            }
            default:
            {
                var apex = Centroid(foot);
                var height = top + solid.RoofHeight;
                foreach (var (a, b) in Edges(foot).OrderBy(e => e.A.X + e.A.Y + e.B.X + e.B.Y))
                {
                    var (nx, ny) = Normal(a, b);
                    var light = Math.Clamp(.78 + (.3 * ((nx * LightX) + (ny * LightY))), .5, 1.1);
                    var face = new[] { P(a, top), P(b, top), P(apex, height) };
                    if (solid.Kind == SolidRoof.Cone) { fill.Color = Shade(roof, (float)light); using var path = Outline(face); canvas.DrawPath(path, fill); }
                    else Polygon(canvas, face, Shade(roof, (float)light), fill, ink);
                }
                if (solid.Kind == SolidRoof.Cone)
                {
                    // A cone keeps only its silhouette inked.
                    var rim = foot.Select(p => P(p, top)).ToList();
                    var tip = P(apex, height);
                    var left = rim.MinBy(p => p.X); var right = rim.MaxBy(p => p.X);
                    canvas.DrawLine(left, tip, ink); canvas.DrawLine(right, tip, ink);
                }
                break;
            }
        }
    }

    private static Solid Box(SKPoint[] footprint, double height, double roof, string walls, string roofColor, SolidRoof kind) =>
        new(footprint, height, roof, walls, roofColor, kind, footprint.Max(p => p.X + p.Y) + (footprint.Average(p => p.X + p.Y) * .001));

    private static SKColor Color(string hex) => SKColor.TryParse(hex, out var color) ? color : SKColors.Magenta;

    private static SKColor Shade(SKColor color, float factor) =>
        new((byte)Math.Clamp(color.Red * factor, 0, 255), (byte)Math.Clamp(color.Green * factor, 0, 255), (byte)Math.Clamp(color.Blue * factor, 0, 255), color.Alpha);

    private static SKColor Shade(string color, float factor) => Shade(Color(color), factor);

    private static string Plaster(double seed) => seed switch
    {
        < .3 => "#FFEDE2C8",
        < .55 => "#FFE4D5B4",
        < .8 => "#FFD9C9A6",
        _ => "#FFCFC0A2",
    };

    private static SKPoint Project(SKPoint p, double z) => new((float)((p.X - p.Y) * Cos30), (float)(((p.X + p.Y) * Sin30) - z));

    /// <summary>A side is seen when its outward normal points towards the viewer (larger x + y).</summary>
    private static bool Faces(SKPoint a, SKPoint b) { var (nx, ny) = Normal(a, b); return nx + ny > 1e-6; }

    // Outward normal of an edge of a clockwise polygon (y down).
    private static (double X, double Y) Normal(SKPoint a, SKPoint b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        return length < 1e-9 ? (0, 0) : (dy / length, -dx / length);
    }

    private static SKPoint[] Clockwise(SKPoint[] points) => Area(points) > 0 ? points : [.. points.Reverse()];

    // Signed area with y down: positive for clockwise on screen.
    private static double Area(SKPoint[] points)
    {
        double sum = 0;
        for (var i = 0; i < points.Length; i++) { var (a, b) = (points[i], points[(i + 1) % points.Length]); sum += (a.X * b.Y) - (b.X * a.Y); }
        return sum / 2;
    }

    private static IEnumerable<(SKPoint A, SKPoint B)> Edges(SKPoint[] points)
    {
        for (var i = 0; i < points.Length; i++) yield return (points[i], points[(i + 1) % points.Length]);
    }

    private static SKPoint Centroid(SKPoint[] points) => new(points.Average(p => p.X), points.Average(p => p.Y));
    private static SKPoint Mid(SKPoint a, SKPoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    private static double Distance(SKPoint a, SKPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static SKPoint[] Square(SKPoint c, double side)
    {
        var h = (float)(side / 2);
        return [new(c.X - h, c.Y - h), new(c.X + h, c.Y - h), new(c.X + h, c.Y + h), new(c.X - h, c.Y + h)];
    }

    private static SKPoint[] Segment(SKPoint a, SKPoint b, double thick)
    {
        var (nx, ny) = Normal(a, b);
        var (ox, oy) = ((float)(nx * thick / 2), (float)(ny * thick / 2));
        return [new(a.X - ox, a.Y - oy), new(b.X - ox, b.Y - oy), new(b.X + ox, b.Y + oy), new(a.X + ox, a.Y + oy)];
    }

    private static SKPath Outline(SKPoint[] points)
    {
        var path = new SKPath();
        path.AddPoly(points, close: true);
        return path;
    }

    private static void Polygon(SKCanvas canvas, SKPoint[] points, SKColor color, SKPaint fill, SKPaint ink)
    {
        using var path = Outline(points);
        fill.Color = color;
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, ink);
    }

    // Monotone chain convex hull.
    private static SKPoint[] Hull(SKPoint[] points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        if (sorted.Length < 3) return sorted;
        static double Cross(SKPoint o, SKPoint a, SKPoint b) => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));
        var hull = new List<SKPoint>();
        foreach (var pass in new[] { sorted, sorted.Reverse().ToArray() })
        {
            var start = hull.Count;
            foreach (var p in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return [.. hull];
    }
}
