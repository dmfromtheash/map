using Map.Core.Documents;

namespace Map.Core.Editing;

public enum LandShapeKind
{
    Circle,
    Rectangle,
    Ellipse,
    Polygon,
    GridBlock,
    BoundedFill,
}

public readonly record struct LandPoint(double X, double Y);

public sealed record LandShape
{
    public required LandShapeKind Kind { get; init; }
    public required WorldBounds Bounds { get; init; }
    public IReadOnlyList<LandPoint> Points { get; init; } = [];
    public double GridSize { get; init; } = 64;
}

/// <summary>Pure bounded rasterization for land primitives; returns only touched tile deltas.</summary>
public static class LandRasterizer
{
    public static (Dictionary<TileKey, byte[]?> Before, Dictionary<TileKey, byte[]?> After) ApplyShape(
        TileMap source, LandShape shape, bool subtract)
    {
        if (source.BytesPerPixel != 1)
        {
            throw new ArgumentException("Land source must be single-channel coverage.", nameof(source));
        }

        var bounds = Normalize(shape.Bounds);
        if (shape.Kind == LandShapeKind.GridBlock)
        {
            bounds = SnapToGrid(bounds, shape.GridSize);
        }
        if (bounds.Right - bounds.Left > 16_384 || bounds.Bottom - bounds.Top > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(shape), "One land operation is bounded to 16384x16384 units.");
        }

        var min = TileMap.KeyAt(bounds.Left, bounds.Top);
        var max = TileMap.KeyAt(bounds.Right, bounds.Bottom);
        var before = new Dictionary<TileKey, byte[]?>();
        var after = new Dictionary<TileKey, byte[]?>();
        for (var ty = min.Y; ty <= max.Y; ty++)
        {
            for (var tx = min.X; tx <= max.X; tx++)
            {
                var key = new TileKey(tx, ty);
                var original = source.TryGet(key);
                var pixels = original is null
                    ? new byte[TileMap.TileSize * TileMap.TileSize]
                    : (byte[])original.Clone();
                var changed = false;
                for (var y = 0; y < TileMap.TileSize; y++)
                {
                    var worldY = (ty * TileMap.TileSize) + y + 0.5;
                    if (worldY < bounds.Top || worldY > bounds.Bottom)
                    {
                        continue;
                    }

                    for (var x = 0; x < TileMap.TileSize; x++)
                    {
                        var worldX = (tx * TileMap.TileSize) + x + 0.5;
                        if (worldX < bounds.Left || worldX > bounds.Right
                            || !Contains(shape, bounds, worldX, worldY))
                        {
                            continue;
                        }

                        var index = (y * TileMap.TileSize) + x;
                        var value = subtract ? (byte)0 : (byte)255;
                        if (pixels[index] != value)
                        {
                            pixels[index] = value;
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    before[key] = original;
                    after[key] = TileMap.IsEmptyTile(pixels) ? null : pixels;
                }
            }
        }

        return (before, after);
    }

    public static (Dictionary<TileKey, byte[]?> Before, Dictionary<TileKey, byte[]?> After) MoveRegion(
        TileMap source, WorldBounds region, int deltaX, int deltaY)
    {
        if (source.BytesPerPixel != 1)
        {
            throw new ArgumentException("Land source must be single-channel coverage.", nameof(source));
        }

        var bounds = Normalize(region);
        var width = (int)Math.Ceiling(bounds.Right) - (int)Math.Floor(bounds.Left);
        var height = (int)Math.Ceiling(bounds.Bottom) - (int)Math.Floor(bounds.Top);
        if (width is <= 0 or > 4_096 || height is <= 0 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "A moved land selection is bounded to 4096x4096.");
        }

        var x0 = (int)Math.Floor(bounds.Left);
        var y0 = (int)Math.Floor(bounds.Top);
        var captured = new List<(int X, int Y, byte Value)>();
        for (var y = y0; y < y0 + height; y++)
        {
            for (var x = x0; x < x0 + width; x++)
            {
                var value = Read(source, x, y);
                if (value != 0)
                {
                    captured.Add((x, y, value));
                }
            }
        }

        if (captured.Count == 0 || (deltaX == 0 && deltaY == 0))
        {
            return ([], []);
        }

        var working = new Dictionary<TileKey, byte[]>();
        for (var y = y0; y < y0 + height; y++)
        {
            for (var x = x0; x < x0 + width; x++)
            {
                Write(x, y, 0);
            }
        }

        foreach (var pixel in captured)
        {
            Write(pixel.X + deltaX, pixel.Y + deltaY, pixel.Value);
        }

        var before = new Dictionary<TileKey, byte[]?>();
        var after = new Dictionary<TileKey, byte[]?>();
        foreach (var (key, pixels) in working)
        {
            var original = source.TryGet(key);
            if (original is not null && original.AsSpan().SequenceEqual(pixels))
            {
                continue;
            }

            before[key] = original;
            after[key] = TileMap.IsEmptyTile(pixels) ? null : pixels;
        }

        return (before, after);

        void Write(int worldX, int worldY, byte value)
        {
            var key = TileMap.KeyAt(worldX, worldY);
            if (!working.TryGetValue(key, out var tile))
            {
                tile = source.TryGet(key) is { } original
                    ? (byte[])original.Clone()
                    : new byte[TileMap.TileSize * TileMap.TileSize];
                working[key] = tile;
            }

            var localX = Mod(worldX, TileMap.TileSize);
            var localY = Mod(worldY, TileMap.TileSize);
            tile[(localY * TileMap.TileSize) + localX] = value;
        }
    }

    private static byte Read(TileMap source, int worldX, int worldY)
    {
        var tile = source.TryGet(TileMap.KeyAt(worldX, worldY));
        return tile is null
            ? (byte)0
            : tile[(Mod(worldY, TileMap.TileSize) * TileMap.TileSize) + Mod(worldX, TileMap.TileSize)];
    }

    private static int Mod(int value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static bool Contains(LandShape shape, WorldBounds bounds, double x, double y) =>
        shape.Kind switch
        {
            LandShapeKind.Circle => InEllipse(bounds, x, y, forceCircle: true),
            LandShapeKind.Ellipse => InEllipse(bounds, x, y, forceCircle: false),
            LandShapeKind.Polygon => InPolygon(shape.Points, x, y),
            LandShapeKind.GridBlock => InGridBlock(bounds, shape.GridSize, x, y),
            _ => true,
        };

    private static bool InEllipse(WorldBounds bounds, double x, double y, bool forceCircle)
    {
        var cx = (bounds.Left + bounds.Right) / 2;
        var cy = (bounds.Top + bounds.Bottom) / 2;
        var rx = (bounds.Right - bounds.Left) / 2;
        var ry = (bounds.Bottom - bounds.Top) / 2;
        if (forceCircle)
        {
            rx = ry = Math.Min(rx, ry);
        }

        return rx > 0 && ry > 0
            && (((x - cx) * (x - cx)) / (rx * rx)) + (((y - cy) * (y - cy)) / (ry * ry)) <= 1;
    }

    private static bool InPolygon(IReadOnlyList<LandPoint> points, double x, double y)
    {
        if (points.Count < 3)
        {
            return false;
        }

        var inside = false;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            var a = points[i];
            var b = points[j];
            if ((a.Y > y) != (b.Y > y)
                && x < ((b.X - a.X) * (y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool InGridBlock(WorldBounds bounds, double gridSize, double x, double y)
    {
        if (gridSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gridSize));
        }

        var snappedLeft = Math.Floor(bounds.Left / gridSize) * gridSize;
        var snappedTop = Math.Floor(bounds.Top / gridSize) * gridSize;
        var snappedRight = Math.Ceiling(bounds.Right / gridSize) * gridSize;
        var snappedBottom = Math.Ceiling(bounds.Bottom / gridSize) * gridSize;
        return x >= snappedLeft && x <= snappedRight && y >= snappedTop && y <= snappedBottom;
    }

    private static WorldBounds SnapToGrid(WorldBounds bounds, double gridSize)
    {
        if (gridSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gridSize));
        }

        return new WorldBounds(
            Math.Floor(bounds.Left / gridSize) * gridSize,
            Math.Floor(bounds.Top / gridSize) * gridSize,
            Math.Ceiling(bounds.Right / gridSize) * gridSize,
            Math.Ceiling(bounds.Bottom / gridSize) * gridSize);
    }

    private static WorldBounds Normalize(WorldBounds bounds) => new(
        Math.Min(bounds.Left, bounds.Right), Math.Min(bounds.Top, bounds.Bottom),
        Math.Max(bounds.Left, bounds.Right), Math.Max(bounds.Top, bounds.Bottom));
}
