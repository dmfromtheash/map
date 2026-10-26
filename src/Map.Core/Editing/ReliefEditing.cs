using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>What one stroke of the «Рельеф» brush does to the heights under it (R22).</summary>
public enum ReliefAction
{
    /// <summary>Поднять: the ground rises under the brush, most in the middle.</summary>
    Raise,
    /// <summary>Опустить: the ground sinks.</summary>
    Lower,
    /// <summary>Хребет: a sharp crest along the stroke.</summary>
    Ridge,
    /// <summary>Сгладить: heights move towards the mean of their neighbours.</summary>
    Smooth,
    /// <summary>Эрозия: the high ground wears down unevenly, gullies more than spurs.</summary>
    Erode,
}

/// <summary>
/// The heights of a land (R22): one byte per world pixel in <see cref="LandLayer.ReliefTiles"/>,
/// 0 the level of the land itself, 255 the highest ground. Heights only shade and contour the
/// land inside its own tier; the tiers (<see cref="LandLayer.Level"/>) stay the basis of the
/// relief, as the owner asked (2026-10-22).
/// </summary>
public static class ReliefEditor
{
    /// <summary>How many height steps a full-strength stroke adds at its centre.</summary>
    public const double StrokeLift = 48;

    /// <summary>
    /// The tiles one stroke changes: <paramref name="coverage"/> is the stroke's own coverage per
    /// tile (0..255), <paramref name="strength"/> 0..1. Undo restores the returned "before".
    /// </summary>
    public static (Dictionary<TileKey, byte[]?> Before, Dictionary<TileKey, byte[]?> After) Apply(
        TileMap heights, IReadOnlyDictionary<TileKey, byte[]> coverage, ReliefAction action, double strength, int seed)
    {
        ArgumentNullException.ThrowIfNull(heights);
        ArgumentNullException.ThrowIfNull(coverage);
        if (heights.BytesPerPixel != 1) throw new ArgumentException("Heights are one byte per pixel.", nameof(heights));
        strength = double.IsFinite(strength) ? Math.Clamp(strength, 0, 1) : 0;
        var size = TileMap.TileSize;
        var before = new Dictionary<TileKey, byte[]?>();
        var after = new Dictionary<TileKey, byte[]?>();
        foreach (var (key, cover) in coverage)
        {
            var original = heights.TryGet(key);
            var pixels = original is null ? new byte[size * size] : (byte[])original.Clone();
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var i = (y * size) + x;
                    var c = cover[i];
                    if (c == 0) continue;
                    var w = c / 255.0 * strength;
                    var h = (double)pixels[i];
                    var wx = (key.X * size) + x; var wy = (key.Y * size) + y;
                    h = action switch
                    {
                        ReliefAction.Raise => h + (w * StrokeLift),
                        ReliefAction.Lower => h - (w * StrokeLift),
                        // A crest: the lift falls off with the cube of the distance from the stroke's line.
                        ReliefAction.Ridge => h + (Math.Pow(c / 255.0, 3) * strength * StrokeLift * 1.6),
                        ReliefAction.Smooth => h + ((Mean(heights, wx, wy) - h) * w),
                        // Wear: lower where a fractal noise says gully, more on the high ground.
                        _ => h - (w * StrokeLift * 0.5 * (h / 255.0 + 0.15) * Gully(wx, wy, seed)),
                    };
                    pixels[i] = (byte)Math.Clamp(Math.Round(h), 0, 255);
                }
            before[key] = original;
            after[key] = Array.TrueForAll(pixels, v => v == 0) ? null : pixels;
        }
        return (before, after);
    }

    /// <summary>The mean height around a world pixel, over a 5×5 window, from the tiles as they were.</summary>
    private static double Mean(TileMap heights, int x, int y)
    {
        var total = 0.0;
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                total += HeightAt(heights, x + dx, y + dy);
        return total / 25;
    }

    /// <summary>The height of one world pixel; 0 outside the painted tiles.</summary>
    public static byte HeightAt(TileMap heights, int x, int y)
    {
        var size = TileMap.TileSize;
        var key = new TileKey((int)Math.Floor(x / (double)size), (int)Math.Floor(y / (double)size));
        return heights.TryGet(key) is { } tile ? tile[((y - (key.Y * size)) * size) + (x - (key.X * size))] : (byte)0;
    }

    private static double Gully(int x, int y, int seed)
    {
        var n = Map.Core.Editing.HexWorldGenerator.Fractal(x / 23.0, y / 23.0, seed, 4, 0.55);
        return Math.Clamp((n - 0.35) * 2.2, 0, 1.4);
    }

    /// <summary>
    /// A heightmap picture (grey levels, the light high) laid over the whole canvas: every world
    /// pixel takes the grey of the matching point of the picture, scaled to the canvas.
    /// </summary>
    public static (Dictionary<TileKey, byte[]?> Before, Dictionary<TileKey, byte[]?> After) Import(
        TileMap heights, byte[] grey, int width, int height, int canvasWidth, int canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(grey);
        if (width <= 0 || height <= 0 || grey.Length < width * height) throw new ArgumentException("The heightmap is empty.", nameof(grey));
        var size = TileMap.TileSize;
        var before = new Dictionary<TileKey, byte[]?>();
        var after = new Dictionary<TileKey, byte[]?>();
        foreach (var key in heights.Keys) { before[key] = heights.TryGet(key); after[key] = null; }
        for (var ty = 0; ty * size < canvasHeight; ty++)
            for (var tx = 0; tx * size < canvasWidth; tx++)
            {
                var key = new TileKey(tx, ty);
                var pixels = new byte[size * size];
                var any = false;
                for (var y = 0; y < size; y++)
                {
                    var wy = (ty * size) + y;
                    if (wy >= canvasHeight) break;
                    var sy = Math.Min(height - 1, (int)((wy + 0.5) * height / canvasHeight));
                    for (var x = 0; x < size; x++)
                    {
                        var wx = (tx * size) + x;
                        if (wx >= canvasWidth) break;
                        var sx = Math.Min(width - 1, (int)((wx + 0.5) * width / canvasWidth));
                        var v = grey[(sy * width) + sx];
                        pixels[(y * size) + x] = v;
                        any |= v != 0;
                    }
                }
                before.TryAdd(key, heights.TryGet(key));
                after[key] = any ? pixels : null;
            }
        return (before, after);
    }
}

/// <summary>One relief stroke or import: the touched height tiles before and after; nothing else of the land changes.</summary>
public sealed class LandReliefCommand(
    LayerId layerId,
    IReadOnlyDictionary<TileKey, byte[]?> before,
    IReadOnlyDictionary<TileKey, byte[]?> after,
    string name) : IEditCommand
{
    public string Name { get; } = name;
    public long ApproximateMemoryBytes => before.Values.Concat(after.Values).Sum(v => (long)(v?.Length ?? 0));
    public MapDocument Apply(MapDocument document) => Write(document, after);
    public MapDocument Revert(MapDocument document) => Write(document, before);
    private MapDocument Write(MapDocument document, IReadOnlyDictionary<TileKey, byte[]?> tiles) =>
        document.WithLayer(layerId, layer => layer is LandLayer land
            ? land with { ReliefTiles = land.ReliefTiles.WithMany(tiles) }
            : throw new InvalidOperationException("Relief requires a land layer."));
}
