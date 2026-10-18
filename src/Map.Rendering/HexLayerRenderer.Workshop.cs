using System.Runtime.CompilerServices;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// The workshop looks of a hex layer: transitions and coasts between neighbouring terrains, living
/// terrain, region tints and frontiers, rivers, roads and bridges, the hex key, the fog of war and
/// the overlays of the hex tools. Everything is drawn from the document; nothing here is stored.
/// </summary>
public static partial class HexLayerRenderer
{
    // ------------------------------------------------------------------ transitions and coasts

    /// <summary>
    /// The two corners of the edge that faces neighbour direction d, for each orientation: the
    /// corners whose screen angle is the direction's angle ∓ 30°.
    /// </summary>
    private static readonly (int A, int B)[][] SideCorners = [BuildSideCorners(HexOrientation.Flat), BuildSideCorners(HexOrientation.Pointy)];

    private static (int A, int B)[] BuildSideCorners(HexOrientation orientation)
    {
        var layout = new HexLayout(orientation, 1, 0, 0);
        var result = new (int A, int B)[6];
        for (var d = 0; d < 6; d++)
        {
            var angle = layout.DirectionAngle(d);
            int Nearest(double target)
            {
                var best = 0;
                var bestDistance = double.MaxValue;
                for (var i = 0; i < 6; i++)
                {
                    var (x, y) = layout.Corner(new HexCoord(0, 0), i);
                    var cornerAngle = Math.Atan2(y, x) * 180 / Math.PI;
                    var distance = Math.Abs(((cornerAngle - target) % 360 + 540) % 360 - 180);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = i;
                    }
                }

                return best;
            }

            // A is the corner on the clockwise-later side of the edge (+30°), B the earlier one.
            result[d] = (Nearest(angle + 30), Nearest(angle - 30));
        }

        return result;
    }

    private static readonly SKColor Sand = new(0xE2, 0xCE, 0x98);

    private static readonly SKColor Foam = new(0xF4, 0xFA, 0xFA);

    /// <summary>
    /// Spills the ground of higher-priority neighbours onto this hex (Wesnoth order: sides that touch
    /// are one band, the longest run first) and draws a sandy shore with surf where land meets water.
    /// Everything is clipped to the hex, so the neighbour's own picture is never covered.
    /// </summary>
    private static void DrawTransitions(SKCanvas canvas, HexBoard board, HexCoord hex, HexCell cell, double detail)
    {
        var self = HexStarterTiles.Find(cell.Terrain);
        if (self is null || !self.Blends) return;
        Span<int> neighbourIndex = stackalloc int[6];
        var spilling = new List<HexTileDefinition>(3);
        var coastMask = 0;
        for (var d = 0; d < 6; d++)
        {
            neighbourIndex[d] = -1;
            var other = board[hex.Neighbor(d)];
            if (other is null || other.Terrain == cell.Terrain) continue;
            var tile = HexStarterTiles.Find(other.Terrain);
            if (tile is null || !tile.Blends) continue;
            if (self.IsWater && !tile.IsWater)
            {
                coastMask |= 1 << d;
                continue;
            }

            if (tile.Priority <= self.Priority) continue;
            var index = spilling.IndexOf(tile);
            if (index < 0)
            {
                index = spilling.Count;
                spilling.Add(tile);
            }

            neighbourIndex[d] = index;
        }

        if (spilling.Count == 0 && coastMask == 0) return;
        var layout = board.Layout;
        canvas.Save();
        using (var clip = HexOutline(layout, hex, 0))
        {
            canvas.ClipPath(clip, antialias: true);
        }

        // A soft edge without a blur (a CPU blur per band made a large export take minutes): the
        // band is laid three times, deep and faint, then shallower and solid.
        var soft = detail >= 14;
        var noiseSeed = (int)HexEditing.Noise(hex, 77);
        ReadOnlySpan<(double Depth, byte Alpha)> steps = soft ? [(0.4, 80), (0.3, 140), (0.2, 255)] : [(0.3, 255)];
        for (var i = 0; i < spilling.Count; i++)
        {
            var mask = 0;
            for (var d = 0; d < 6; d++)
            {
                if (neighbourIndex[d] == i) mask |= 1 << d;
            }

            var ground = HexTileArt.Ground(spilling[i].Id, layout.Orientation);
            if (ground is null) continue;
            var bounds = layout.Bounds(hex);
            var matrix = SKMatrix.CreateScale((float)((bounds.Right - bounds.Left) / ground.Width), (float)((bounds.Bottom - bounds.Top) / ground.Height))
                .PostConcat(SKMatrix.CreateTranslation((float)bounds.Left, (float)bounds.Top));
            using var shader = SKShader.CreateImage(ground, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Sampling, matrix);
            using var paint = new SKPaint { IsAntialias = true, Shader = shader };
            foreach (var (start, length) in HexTransitions.Runs(mask))
            {
                for (var step = 0; step < steps.Length; step++)
                {
                    paint.Color = SKColors.White.WithAlpha(steps[step].Alpha);
                    using var band = Band(layout, hex, start, length, steps[step].Depth, noiseSeed + i * 13 + step * 101, out var bandEdge);
                    canvas.DrawPath(band, paint);
                    bandEdge.Dispose();
                }
            }
        }

        if (coastMask != 0)
        {
            using var wet = new SKPaint { IsAntialias = true, Color = Sand.WithAlpha(110) };
            using var sand = new SKPaint { IsAntialias = true, Color = Sand };
            using var surf = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, Color = Foam.WithAlpha(200), StrokeCap = SKStrokeCap.Round,
                StrokeWidth = (float)(layout.Size * 0.035),
            };
            using var dash = SKPathEffect.CreateDash([(float)(layout.Size * 0.1), (float)(layout.Size * 0.07)], 0);
            using var outer = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, Color = Foam.WithAlpha(90), StrokeCap = SKStrokeCap.Round,
                StrokeWidth = (float)(layout.Size * 0.025), PathEffect = dash,
            };
            foreach (var (start, length) in HexTransitions.Runs(coastMask))
            {
                using var shallow = Band(layout, hex, start, length, 0.27, noiseSeed, out var shallowEdge);
                canvas.DrawPath(shallow, wet);
                using var shore = Band(layout, hex, start, length, 0.18, noiseSeed, out var edge);
                canvas.DrawPath(shore, sand);
                canvas.DrawPath(edge, surf);
                if (soft) canvas.DrawPath(shallowEdge, outer);
                shallowEdge.Dispose();
                edge.Dispose();
            }
        }

        canvas.Restore();
    }

    /// <summary>
    /// A band along a run of sides of a hex: outside the hex edge by a little (so a blurred band is
    /// solid where it meets the neighbour) and inside by <paramref name="depth"/> of the radius with a
    /// wavy inner line that comes back to the corners at the ends of a partial run.
    /// </summary>
    private static SKPath Band(HexLayout layout, HexCoord hex, int start, int length, double depth, int seed, out SKPath innerLine)
    {
        var orientation = layout.Orientation == HexOrientation.Flat ? 0 : 1;
        var (cx, cy) = layout.Center(hex);
        var outer = new List<(double X, double Y)>();
        // Sides of a run follow Directions (counter-clockwise on screen); walk the edge corners in that order.
        for (var k = 0; k < length; k++)
        {
            var side = (start + k) % 6;
            var (a, b) = SideCorners[orientation][side];
            var pa = layout.Corner(hex, a);
            var pb = layout.Corner(hex, b);
            const int Steps = 6;
            for (var s = 0; s < Steps; s++)
            {
                var t = (double)s / Steps;
                outer.Add((pa.X + (pb.X - pa.X) * t, pa.Y + (pb.Y - pa.Y) * t));
            }

            if (k == length - 1) outer.Add(pb);
        }

        var full = length >= 6;
        var path = new SKPath();
        innerLine = new SKPath();
        var count = outer.Count;
        var random = new Random(seed);
        var phase = random.NextDouble() * Math.PI * 2;
        for (var i = 0; i < count; i++)
        {
            var (x, y) = outer[i];
            var push = 1.12;
            var px = cx + (x - cx) * push;
            var py = cy + (y - cy) * push;
            if (i == 0) path.MoveTo((float)px, (float)py);
            else path.LineTo((float)px, (float)py);
        }

        for (var i = count - 1; i >= 0; i--)
        {
            var (x, y) = outer[i];
            var t = count == 1 ? 0.5 : (double)i / (count - 1);
            // A partial run fades to nothing at its two ends; a full ring keeps its depth all round.
            var taper = full ? 1 : Math.Sin(Math.PI * t);
            var wave = 1 + 0.32 * Math.Sin(phase + t * length * 2.3) + 0.12 * (random.NextDouble() - 0.5);
            var inward = depth * Math.Max(0, taper) * wave;
            var ix = x + (cx - x) * inward;
            var iy = y + (cy - y) * inward;
            path.LineTo((float)ix, (float)iy);
            if (i == count - 1) innerLine.MoveTo((float)ix, (float)iy);
            else innerLine.LineTo((float)ix, (float)iy);
        }

        path.Close();
        return path;
    }

    private static void TintHex(SKCanvas canvas, HexLayout layout, HexCoord hex, string color)
    {
        using var path = HexOutline(layout, hex, 0);
        using var paint = new SKPaint { IsAntialias = true, Color = ParseColor(color).WithAlpha(52) };
        canvas.DrawPath(path, paint);
    }

    // ------------------------------------------------------------------ features, living terrain, frontiers

    private readonly record struct Standing(float SortY, SKImage Image, SKRect Rect);

    /// <summary>Everything that stands on the ground of a chunk, sorted back to front, and the region frontiers.</summary>
    private static void DrawChunkFeatures(SKCanvas canvas, HexLayer layer, HexChunkKey key, HexChunk chunk, WorldBounds? clip, double pixelScale)
    {
        var board = layer.Board;
        var layout = board.Layout;
        var items = new List<Standing>();
        foreach (var (column, row) in DrawOrder(board))
        {
            if (chunk[column, row] is not { } cell) continue;
            var hex = board.CoordOf(key, column, row);
            if (clip is { } view && !Intersects(TallBounds(layout, hex), view)) continue;
            if (HexStarterTiles.Find(cell.Terrain) is { Dynamic: not HexDynamicIcon.None } living)
            {
                AddLivingIcons(items, board, hex, cell, living);
            }

            if (cell.Feature is { } feature && HexTileArt.Image(feature, cell.FeatureVariant, layout.Orientation) is { } picture)
            {
                var bounds = layout.Bounds(hex);
                items.Add(new Standing((float)(layout.Center(hex).Y + layout.Size * 0.45), picture,
                    new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom)));
            }
        }

        items.Sort((a, b) => a.SortY.CompareTo(b.SortY));
        foreach (var item in items) canvas.DrawImage(item.Image, item.Rect, Sampling);
        if (!layer.Regions.IsEmpty) DrawFrontiers(canvas, layer, key, chunk, pixelScale);
    }

    /// <summary>
    /// Living terrain (Kohlhofer's scatter): icons sit on a jittered lattice laid over the whole map,
    /// and each point belongs to the hex whose centre is nearest - so a forest flows across its hexes
    /// without repeating the grid. Where many hexes of the same kind touch, mountains grow taller.
    /// </summary>
    private static void AddLivingIcons(List<Standing> items, HexBoard board, HexCoord hex, HexCell cell, HexTileDefinition tile)
    {
        var layout = board.Layout;
        var r = layout.Size;
        var (step, size) = tile.Dynamic switch
        {
            HexDynamicIcon.Trees => (0.44, 0.78),
            HexDynamicIcon.Pines => (0.4, 0.8),
            HexDynamicIcon.Hills => (0.8, 0.86),
            _ => (0.86, 1.0),
        };
        step *= r;
        var same = 0;
        foreach (var neighbour in hex.Neighbors())
        {
            if (board[neighbour]?.Terrain == cell.Terrain) same++;
        }

        var grow = tile.Dynamic switch
        {
            HexDynamicIcon.Mountains => 0.8 + 0.09 * same,
            HexDynamicIcon.Hills => 0.9 + 0.04 * same,
            _ => 1.0,
        };
        var (cx, cy) = layout.Center(hex);
        var rowStep = step * 0.86;
        var x0 = (int)Math.Floor((cx - r - layout.OriginX) / step) - 1;
        var x1 = (int)Math.Ceiling((cx + r - layout.OriginX) / step) + 1;
        var y0 = (int)Math.Floor((cy - r - layout.OriginY) / rowStep) - 1;
        var y1 = (int)Math.Ceiling((cy + r - layout.OriginY) / rowStep) + 1;
        var salt = cell.Variant * 7919 + (int)tile.Dynamic;
        for (var j = y0; j <= y1; j++)
        {
            for (var i = x0; i <= x1; i++)
            {
                var noise = HexEditing.Noise(new HexCoord(i, j), salt);
                var jx = ((noise & 0xFFFF) / 65535.0 - 0.5) * 0.6;
                var jy = ((noise >> 16) / 65535.0 - 0.5) * 0.6;
                var px = layout.OriginX + (i + ((j & 1) == 0 ? 0 : 0.5) + jx) * step;
                var py = layout.OriginY + (j + jy) * rowStep;
                if (layout.HexAt(px, py) != hex) continue;
                var distance = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                if (distance > 0.84 * r) continue;
                // A settlement keeps its middle free of trees and peaks.
                if (cell.Feature is not null && distance < 0.62 * r) continue;
                var variant = (int)(noise % HexTileArt.IconVariants);
                var scale = size * grow * (0.82 + 0.18 * ((noise >> 8) & 0xFF) / 255.0) * r;
                var icon = HexTileArt.Icon(tile.Dynamic, variant);
                var foot = (float)(py + scale * 12 / 256);
                items.Add(new Standing((float)py, icon,
                    new SKRect((float)(px - scale / 2), (float)(foot - scale), (float)(px + scale / 2), foot)));
            }
        }
    }

    /// <summary>Region frontiers: each hex draws its own side of an edge to another region, inset, in its region's colour.</summary>
    private static void DrawFrontiers(SKCanvas canvas, HexLayer layer, HexChunkKey key, HexChunk chunk, double pixelScale)
    {
        var board = layer.Board;
        var layout = board.Layout;
        var orientation = layout.Orientation == HexOrientation.Flat ? 0 : 1;
        var paths = new Dictionary<int, SKPath>();
        for (var row = 0; row < HexBoard.ChunkSize; row++)
        {
            for (var column = 0; column < HexBoard.ChunkSize; column++)
            {
                if (chunk[column, row] is not { Region: not 0 } cell) continue;
                var hex = board.CoordOf(key, column, row);
                var (cx, cy) = layout.Center(hex);
                for (var d = 0; d < 6; d++)
                {
                    var other = board[hex.Neighbor(d)];
                    if ((other?.Region ?? 0) == cell.Region) continue;
                    if (!paths.TryGetValue(cell.Region, out var path)) paths[cell.Region] = path = new SKPath();
                    var (a, b) = SideCorners[orientation][d];
                    var (ax, ay) = layout.Corner(hex, a);
                    var (bx, by) = layout.Corner(hex, b);
                    const double Inset = 0.07;
                    path.MoveTo((float)(ax + (cx - ax) * Inset), (float)(ay + (cy - ay) * Inset));
                    path.LineTo((float)(bx + (cx - bx) * Inset), (float)(by + (cy - by) * Inset));
                }
            }
        }

        var width = (float)Math.Max(layout.Size * 0.075, 1.4 / Math.Max(pixelScale, 1e-6));
        foreach (var (id, path) in paths)
        {
            var color = layer.Region(id) is { } region ? ParseColor(region.Color) : SKColors.Gray;
            using var shadow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width * 1.8f, Color = new SKColor(0, 0, 0, 50), StrokeCap = SKStrokeCap.Round };
            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, Color = color.WithAlpha(230), StrokeCap = SKStrokeCap.Round };
            canvas.DrawPath(path, shadow);
            canvas.DrawPath(path, line);
            path.Dispose();
        }
    }

    // ------------------------------------------------------------------ paths

    /// <summary>The drawable shape of a path for one layout: one piece per hex (rivers widen piece by piece) and its bounds.</summary>
    private sealed class PathShape(HexLayout layout, SKPath[] pieces, SKPath whole, WorldBounds bounds)
    {
        public HexLayout Layout { get; } = layout;
        public SKPath[] Pieces { get; } = pieces;
        public SKPath Whole { get; } = whole;
        public WorldBounds Bounds { get; } = bounds;
    }

    private static readonly ConditionalWeakTable<HexPath, PathShape> PathShapes = new();

    private static PathShape ShapeOf(HexPath path, HexLayout layout)
    {
        if (PathShapes.TryGetValue(path, out var kept) && kept.Layout == layout) return kept;
        var built = BuildShape(path, layout);
        PathShapes.AddOrUpdate(path, built);
        return built;
    }

    private static PathShape BuildShape(HexPath path, HexLayout layout)
    {
        var pieces = new List<SKPath>();
        var whole = new SKPath();
        if (path.Style == HexRouteStyle.Edges)
        {
            for (var i = 0; i < path.Vertices.Length; i++)
            {
                var (x, y) = layout.Position(path.Vertices[i]);
                if (i == 0) whole.MoveTo((float)x, (float)y);
                else whole.LineTo((float)x, (float)y);
            }
        }
        else if (path.Hexes.Length == 1)
        {
            var (x, y) = layout.Center(path.Hexes[0]);
            whole.AddCircle((float)x, (float)y, (float)(layout.Size * 0.12));
        }
        else
        {
            var hexes = path.Hexes;
            for (var i = 0; i < hexes.Length; i++)
            {
                var piece = new SKPath();
                var (cx, cy) = layout.Center(hexes[i]);
                // In through the middle of the edge shared with the previous hex, out through the next one.
                (double X, double Y) entry = i == 0 ? (cx, cy) : Mid(layout, hexes[i - 1], hexes[i]);
                (double X, double Y) exit = i == hexes.Length - 1 ? (cx, cy) : Mid(layout, hexes[i], hexes[i + 1]);
                var tin = i == 0 ? Unit(exit.X - entry.X, exit.Y - entry.Y) : Direction(layout, hexes[i - 1], hexes[i]);
                var tout = i == hexes.Length - 1 ? Unit(exit.X - entry.X, exit.Y - entry.Y) : Direction(layout, hexes[i], hexes[i + 1]);
                piece.MoveTo((float)entry.X, (float)entry.Y);
                if (path.Style == HexRouteStyle.Meander)
                {
                    // Through a middle point pushed sideways by a stable noise of the hex: it winds, and
                    // its direction at both edge middles is fixed, so the pieces join without a kink.
                    var noise = HexEditing.Noise(hexes[i], path.Seed * 31 + 7);
                    var sideways = ((noise & 0xFFFF) / 65535.0 - 0.5) * 0.62 * layout.Size;
                    var across = Unit(exit.X - entry.X, exit.Y - entry.Y);
                    var normal = (X: -across.Y, Y: across.X);
                    var mid = (X: cx + normal.X * sideways, Y: cy + normal.Y * sideways);
                    var k = layout.Size * 0.32;
                    piece.CubicTo((float)(entry.X + tin.X * k), (float)(entry.Y + tin.Y * k),
                        (float)(mid.X - across.X * k), (float)(mid.Y - across.Y * k), (float)mid.X, (float)mid.Y);
                    piece.CubicTo((float)(mid.X + across.X * k), (float)(mid.Y + across.Y * k),
                        (float)(exit.X - tout.X * k), (float)(exit.Y - tout.Y * k), (float)exit.X, (float)exit.Y);
                }
                else
                {
                    var k = layout.Size * 0.42;
                    piece.CubicTo((float)(entry.X + tin.X * k), (float)(entry.Y + tin.Y * k),
                        (float)(exit.X - tout.X * k), (float)(exit.Y - tout.Y * k), (float)exit.X, (float)exit.Y);
                }

                pieces.Add(piece);
                whole.AddPath(piece, SKPathAddMode.Extend);
            }
        }

        var b = whole.Bounds;
        var pad = layout.Size * 0.5;
        return new PathShape(layout, [.. pieces], whole, new WorldBounds(b.Left - pad, b.Top - pad, b.Right + pad, b.Bottom + pad));
    }

    private static (double X, double Y) Mid(HexLayout layout, HexCoord a, HexCoord b)
    {
        var (ax, ay) = layout.Center(a);
        var (bx, by) = layout.Center(b);
        return ((ax + bx) / 2, (ay + by) / 2);
    }

    private static (double X, double Y) Direction(HexLayout layout, HexCoord a, HexCoord b)
    {
        var (ax, ay) = layout.Center(a);
        var (bx, by) = layout.Center(b);
        return Unit(bx - ax, by - ay);
    }

    private static (double X, double Y) Unit(double x, double y)
    {
        var length = Math.Sqrt(x * x + y * y);
        return length < 1e-9 ? (1, 0) : (x / length, y / length);
    }

    /// <summary>The colour a path of this kind is drawn in when it has none of its own.</summary>
    public static SKColor PathColour(HexPathKind kind) => kind switch
    {
        HexPathKind.River => new SKColor(0x3C, 0x7E, 0xBA),
        HexPathKind.Road => new SKColor(0xB4, 0x8E, 0x5A),
        HexPathKind.Trail => new SKColor(0x6E, 0x50, 0x30),
        _ => new SKColor(0xB0, 0x30, 0x34),
    };

    private static readonly ConditionalWeakTable<System.Collections.Immutable.ImmutableList<HexPath>, HashSet<HexCoord>> RiverHexes = new();

    private static void DrawPaths(SKCanvas canvas, HexLayer layer, WorldBounds view, double viewScale, double hexPixels)
    {
        if (layer.Paths.IsEmpty) return;
        var layout = layer.Board.Layout;
        // Water first, then roads and trails over it, borders on top.
        foreach (var kind in (ReadOnlySpan<HexPathKind>)[HexPathKind.River, HexPathKind.Road, HexPathKind.Trail, HexPathKind.Border])
        {
            foreach (var path in layer.Paths)
            {
                if (path.Kind != kind) continue;
                var shape = ShapeOf(path, layout);
                if (!Intersects(shape.Bounds, view)) continue;
                DrawPath(canvas, path, shape, layout, viewScale, alpha: 255);
            }
        }

        if (hexPixels < 14) return;
        var rivers = RiverHexes.GetValue(layer.Paths, static paths =>
            paths.Where(p => p.Kind == HexPathKind.River).SelectMany(p => p.Hexes).ToHashSet());
        if (rivers.Count == 0) return;
        foreach (var path in layer.Paths)
        {
            if (path.Kind is not (HexPathKind.Road or HexPathKind.Trail) || path.Hexes.Length < 2) continue;
            if (!Intersects(ShapeOf(path, layout).Bounds, view)) continue;
            for (var i = 0; i < path.Hexes.Length; i++)
            {
                if (!rivers.Contains(path.Hexes[i])) continue;
                var before = path.Hexes[Math.Max(0, i - 1)];
                var after = path.Hexes[Math.Min(path.Hexes.Length - 1, i + 1)];
                DrawBridge(canvas, layout, path.Hexes[i], Direction(layout, before, after), path.Kind == HexPathKind.Trail, viewScale);
            }
        }
    }

    private static void DrawPath(SKCanvas canvas, HexPath path, PathShape shape, HexLayout layout, double viewScale, byte alpha)
    {
        var r = layout.Size;
        var pixel = 1 / Math.Max(viewScale, 1e-6);
        var color = (path.Color is { } own ? ParseColor(own) : PathColour(path.Kind)).WithAlpha(alpha);
        switch (path.Kind)
        {
            case HexPathKind.River:
            {
                using var bank = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = new SKColor(0x24, 0x40, 0x5C, (byte)(alpha * 0.8)) };
                using var water = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = color };
                var count = Math.Max(1, shape.Pieces.Length - 1);
                // Thin at its source, wider downstream: each hex piece a little wider than the last.
                for (var pass = 0; pass < 2; pass++)
                {
                    for (var i = 0; i < shape.Pieces.Length; i++)
                    {
                        var width = Math.Max(r * path.Width * (0.05 + 0.1 * i / count), 1.2 * pixel);
                        var paint = pass == 0 ? bank : water;
                        paint.StrokeWidth = (float)(pass == 0 ? width + Math.Max(r * 0.035, pixel) : width);
                        canvas.DrawPath(shape.Pieces[i], paint);
                    }
                }

                if (shape.Pieces.Length == 0) canvas.DrawPath(shape.Whole, water);
                break;
            }

            case HexPathKind.Road:
            {
                var width = Math.Max(r * 0.1 * path.Width, 2 * pixel);
                using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, StrokeWidth = (float)(width * 1.6), Color = new SKColor(0x4A, 0x34, 0x20, (byte)(alpha * 0.85)) };
                using var surface = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, StrokeWidth = (float)width, Color = color };
                canvas.DrawPath(shape.Whole, edge);
                canvas.DrawPath(shape.Whole, surface);
                break;
            }

            case HexPathKind.Trail:
            {
                var width = Math.Max(r * 0.055 * path.Width, 1.5 * pixel);
                using var dash = SKPathEffect.CreateDash([(float)(r * 0.12), (float)(r * 0.08)], 0);
                using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = (float)width, Color = color, PathEffect = dash };
                canvas.DrawPath(shape.Whole, paint);
                break;
            }

            default:
            {
                var width = Math.Max(r * 0.055 * path.Width, 1.4 * pixel);
                using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, StrokeWidth = (float)(width * 3), Color = color.WithAlpha((byte)(alpha * 0.28)) };
                using var dash = SKPathEffect.CreateDash([(float)(r * 0.22), (float)(r * 0.1)], 0);
                using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Butt, StrokeJoin = SKStrokeJoin.Round, StrokeWidth = (float)width, Color = color, PathEffect = dash };
                canvas.DrawPath(shape.Whole, halo);
                canvas.DrawPath(shape.Whole, line);
                break;
            }
        }
    }

    /// <summary>A plank bridge where a road crosses a river, across the hex centre along the road.</summary>
    private static void DrawBridge(SKCanvas canvas, HexLayout layout, HexCoord hex, (double X, double Y) along, bool trail, double viewScale)
    {
        var (cx, cy) = layout.Center(hex);
        var r = layout.Size;
        var length = (float)(r * 0.62);
        var width = (float)(r * (trail ? 0.14 : 0.2));
        canvas.Save();
        canvas.Translate((float)cx, (float)cy);
        canvas.RotateDegrees((float)(Math.Atan2(along.Y, along.X) * 180 / Math.PI));
        var deck = new SKRect(-length / 2, -width / 2, length / 2, width / 2);
        using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 70) };
        canvas.DrawRect(deck with { Top = deck.Top + width * 0.25f, Bottom = deck.Bottom + width * 0.25f }, shadow);
        using var wood = new SKPaint { IsAntialias = true, Color = new SKColor(0x9C, 0x74, 0x48) };
        canvas.DrawRect(deck, wood);
        using var plank = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(0x5A, 0x3E, 0x24), StrokeWidth = (float)Math.Max(r * 0.012, 0.6 / viewScale) };
        for (var x = deck.Left + length / 8; x < deck.Right; x += length / 8) canvas.DrawLine(x, deck.Top, x, deck.Bottom, plank);
        using var rail = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(0x3A, 0x28, 0x18), StrokeWidth = (float)Math.Max(r * 0.025, 0.8 / viewScale) };
        canvas.DrawLine(deck.Left, deck.Top, deck.Right, deck.Top, rail);
        canvas.DrawLine(deck.Left, deck.Bottom, deck.Right, deck.Bottom, rail);
        canvas.Restore();
    }

    // ------------------------------------------------------------------ hex key

    /// <summary>The marker colour of a hex key category.</summary>
    public static SKColor NoteColour(HexNoteCategory category) => category switch
    {
        HexNoteCategory.Encounter => new SKColor(0xC0, 0x3A, 0x2E),
        HexNoteCategory.Mystery => new SKColor(0x7A, 0x4C, 0xB0),
        HexNoteCategory.Landmark => new SKColor(0x2E, 0x7C, 0x9C),
        HexNoteCategory.Settlement => new SKColor(0xB8, 0x86, 0x2C),
        HexNoteCategory.Hook => new SKColor(0x3C, 0x8C, 0x48),
        HexNoteCategory.Danger => new SKColor(0x8A, 0x1C, 0x1C),
        HexNoteCategory.Treasure => new SKColor(0xD8, 0xB0, 0x30),
        _ => new SKColor(0x5A, 0x5A, 0x5A),
    };

    /// <summary>The letter on a hex key marker.</summary>
    public static string NoteGlyph(HexNoteCategory category) => category switch
    {
        HexNoteCategory.Encounter => "В",
        HexNoteCategory.Mystery => "?",
        HexNoteCategory.Landmark => "О",
        HexNoteCategory.Settlement => "П",
        HexNoteCategory.Hook => "З",
        HexNoteCategory.Danger => "!",
        HexNoteCategory.Treasure => "$",
        _ => "•",
    };

    /// <summary>True when the players may see this key: it has text for them and, under fog, its hex is revealed.</summary>
    public static bool PlayersSee(HexLayer layer, HexCoord hex, HexNote note) =>
        note.PlayerText.Length > 0 && (!layer.FogEnabled || layer.Board[hex]?.Revealed == true);

    private static void DrawNotes(SKCanvas canvas, HexLayer layer, WorldBounds view, double viewScale, double hexPixels, bool player, HexCoord? focus)
    {
        var layout = layer.Board.Layout;
        if (focus is { } open && layer.Board.Contains(open))
        {
            using var ring = HexOutline(layout, open, -layout.Size * 0.04);
            using var paint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(0xF4, 0xC8, 0x50),
                StrokeWidth = (float)Math.Max(layout.Size * 0.06, 2 / Math.Max(viewScale, 1e-6)),
            };
            canvas.DrawPath(ring, paint);
        }

        if (layer.Notes.IsEmpty || hexPixels < 6) return;
        var pixel = 1 / Math.Max(viewScale, 1e-6);
        var radius = (float)Math.Clamp(layout.Size * 0.16, 5 * pixel, layout.Size * 0.32);
        using var font = new SKFont(SKTypeface.Default, radius * 1.3f) { Embolden = true };
        using var titleFont = new SKFont(SKTypeface.Default, (float)(layout.Size * 0.21));
        using var fill = new SKPaint { IsAntialias = true };
        using var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(0xFF, 0xF8, 0xE8), StrokeWidth = radius * 0.22f };
        using var glyph = new SKPaint { IsAntialias = true, Color = SKColors.White };
        using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(layout.Size * 0.05), Color = new SKColor(0x1A, 0x14, 0x10, 210), StrokeJoin = SKStrokeJoin.Round };
        using var ink = new SKPaint { IsAntialias = true, Color = new SKColor(0xFF, 0xF6, 0xE0) };
        foreach (var (hex, note) in layer.Notes)
        {
            if (player && !PlayersSee(layer, hex, note)) continue;
            if (!Intersects(layout.Bounds(hex), view)) continue;
            var (cx, cy) = layout.Center(hex);
            var x = (float)(cx + layout.Size * 0.42);
            var y = (float)(cy - layout.Size * 0.42);
            fill.Color = NoteColour(note.Category);
            canvas.DrawCircle(x, y, radius, fill);
            canvas.DrawCircle(x, y, radius, rim);
            if (hexPixels >= 36)
            {
                canvas.DrawText(NoteGlyph(note.Category), x, y + radius * 0.45f, SKTextAlign.Center, font, glyph);
            }

            if (note.ChildMap is not null && hexPixels >= 36)
            {
                // A child map: a small second ring.
                using var child = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = fill.Color, StrokeWidth = radius * 0.2f };
                canvas.DrawCircle(x, y, radius * 1.45f, child);
            }

            if (hexPixels >= 70 && note.Title.Length > 0)
            {
                var title = note.Title.Length > 28 ? note.Title[..27] + "…" : note.Title;
                var ty = (float)(cy + layout.Size * 0.66);
                canvas.DrawText(title, (float)cx, ty, SKTextAlign.Center, titleFont, halo);
                canvas.DrawText(title, (float)cx, ty, SKTextAlign.Center, titleFont, ink);
            }
        }
    }

    // ------------------------------------------------------------------ fog of war

    /// <summary>The fog over the hexes the players have not seen: see-through for the game master, opaque for the players.</summary>
    private static void DrawFog(SKCanvas canvas, HexLayer layer, WorldBounds view, HexChunkCache? cache, bool player)
    {
        if (!layer.FogEnabled) return;
        var board = layer.Board;
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = player ? new SKColor(0x22, 0x20, 0x26) : new SKColor(0x10, 0x14, 0x24, 0x78),
        };
        foreach (var key in board.ChunksIn(view))
        {
            var chunk = board.Chunk(key);
            if (chunk is not null && Info(chunk).Revealed == ExtentCount(board, key)) continue;
            if (cache is not null)
            {
                canvas.DrawPath(cache.FogPath(board, key, chunk), paint);
            }
            else
            {
                using var path = BuildFogPath(board, key, chunk);
                canvas.DrawPath(path, paint);
            }
        }

        if (!player || view.Top > BoardRect(board).Top + board.Layout.HexHeight) return;
        // Tall tiles of the first row rise above the board: for the players the fog covers that too.
        using var above = new SKPath();
        foreach (var hex in board.HexesIn(view with { Bottom = BoardRect(board).Top + board.Layout.HexHeight }))
        {
            if (board.Layout.ToOffset(hex).Row != 0 || board[hex]?.Revealed == true) continue;
            var bounds = board.Layout.Bounds(hex);
            above.AddRect(new SKRect((float)bounds.Left, (float)(bounds.Top - (HexStarterTiles.MaxOverhang + 0.3) * board.Layout.HexHeight),
                (float)bounds.Right, (float)(bounds.Top + board.Layout.HexHeight / 2)));
        }

        canvas.DrawPath(above, paint);
    }

    /// <summary>How many board hexes lie in a chunk (edge chunks are cut by the board).</summary>
    private static int ExtentCount(HexBoard board, HexChunkKey key) =>
        Math.Max(0, Math.Min(HexBoard.ChunkSize, board.Columns - key.X * HexBoard.ChunkSize))
        * Math.Max(0, Math.Min(HexBoard.ChunkSize, board.Rows - key.Y * HexBoard.ChunkSize));

    internal static SKPath BuildFogPath(HexBoard board, HexChunkKey key, HexChunk? chunk)
    {
        var path = new SKPath();
        var corners = new SKPoint[6];
        const double grow = 1.03;
        for (var row = 0; row < HexBoard.ChunkSize; row++)
        {
            for (var column = 0; column < HexBoard.ChunkSize; column++)
            {
                var offset = new HexOffset(key.X * HexBoard.ChunkSize + column, key.Y * HexBoard.ChunkSize + row);
                if (!board.Contains(offset)) continue;
                if (chunk?[column, row] is { Revealed: true }) continue;
                var hex = board.Layout.FromOffset(offset);
                var (cx, cy) = board.Layout.Center(hex);
                for (var i = 0; i < 6; i++)
                {
                    var (x, y) = board.Layout.Corner(hex, i);
                    corners[i] = new SKPoint((float)(cx + (x - cx) * grow), (float)(cy + (y - cy) * grow));
                }

                path.AddPoly(corners, close: true);
            }
        }

        // Neighbouring fogged hexes overlap a hair; winding fill keeps the overlap from showing.
        path.FillType = SKPathFillType.Winding;
        return path;
    }

    // ------------------------------------------------------------------ tool overlays

    private static void DrawOverlay(SKCanvas canvas, HexLayer layer, HexOverlay overlay, double viewScale)
    {
        var board = layer.Board;
        var layout = board.Layout;
        if (overlay.Ghost is { Count: > 0 } ghost)
        {
            using var dim = new SKPaint { Color = SKColors.White.WithAlpha(170) };
            canvas.SaveLayer(dim);
            foreach (var (hex, cell) in ghost.OrderBy(pair => layout.Center(pair.Key).Y))
            {
                if (cell is null || !board.Contains(hex)) continue;
                DrawTile(canvas, layout, hex, cell.Terrain, cell.Variant, 0, cell.Rotation);
                if (cell.Feature is { } feature) DrawTile(canvas, layout, hex, feature, cell.FeatureVariant);
            }

            canvas.Restore();
            DrawHexSet(canvas, board, ghost.Keys.Where(board.Contains).ToList(), viewScale, new SKColor(0x60, 0xB0, 0xF0, 30), new SKColor(0x80, 0xC8, 0xFF));
        }

        if (overlay.Selection.Count > 0)
        {
            DrawHexSet(canvas, board, overlay.Selection, viewScale, new SKColor(0x40, 0x90, 0xE0, 50), new SKColor(0x70, 0xB8, 0xFF));
        }

        if (overlay.PathPreview is { } preview)
        {
            var shape = BuildShape(preview, layout);
            DrawPath(canvas, preview, shape, layout, viewScale, alpha: 220);
            using var guide = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(0xFF, 0xFF, 0xFF, 160),
                StrokeWidth = (float)(1 / Math.Max(viewScale, 1e-6)),
                PathEffect = SKPathEffect.CreateDash([(float)(4 / viewScale), (float)(3 / viewScale)], 0),
            };
            canvas.DrawPath(shape.Whole, guide);
            foreach (var piece in shape.Pieces) piece.Dispose();
            shape.Whole.Dispose();
        }

        if (overlay.Route is { Count: > 0 } route)
        {
            using var line = new SKPath();
            for (var i = 0; i < route.Count; i++)
            {
                var (x, y) = layout.Center(route[i]);
                if (i == 0) line.MoveTo((float)x, (float)y);
                else line.LineTo((float)x, (float)y);
            }

            var pixel = (float)(1 / Math.Max(viewScale, 1e-6));
            using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6 * pixel, Color = new SKColor(0, 0, 0, 120), StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            using var gold = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 * pixel, Color = new SKColor(0xF4, 0xC8, 0x50), StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            canvas.DrawPath(line, halo);
            canvas.DrawPath(line, gold);
            using var dot = new SKPaint { IsAntialias = true, Color = new SKColor(0xF4, 0xC8, 0x50) };
            foreach (var hex in route)
            {
                var (x, y) = layout.Center(hex);
                canvas.DrawCircle((float)x, (float)y, 3.5f * pixel, dot);
            }

            if (overlay.RouteLabel is { Length: > 0 } label)
            {
                var (x, y) = layout.Center(route[^1]);
                using var font = new SKFont(SKTypeface.Default, 14 * pixel) { Embolden = true };
                var width = font.MeasureText(label);
                var box = new SKRect((float)x + 10 * pixel, (float)y - 26 * pixel, (float)x + 22 * pixel + width, (float)y - 6 * pixel);
                using var back = new SKPaint { IsAntialias = true, Color = new SKColor(0x20, 0x18, 0x12, 230) };
                canvas.DrawRoundRect(box, 5 * pixel, 5 * pixel, back);
                using var text = new SKPaint { IsAntialias = true, Color = new SKColor(0xFF, 0xF0, 0xC8) };
                canvas.DrawText(label, box.Left + 6 * pixel, box.Bottom - 5 * pixel, SKTextAlign.Left, font, text);
            }
        }
    }
}
