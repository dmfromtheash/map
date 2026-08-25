using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>A repeating RGBA (premultiplied) pattern sampled in world space by the brush.</summary>
public sealed record BrushTexture
{
    public BrushTexture(int width, int height, byte[] rgbaPremultiplied)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(rgbaPremultiplied);
        if (rgbaPremultiplied.Length != width * height * 4)
        {
            throw new ArgumentException("Texture buffer size does not match its dimensions.");
        }

        Width = width;
        Height = height;
        Pixels = rgbaPremultiplied;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
}

/// <summary>What the brush paints with. Shared by the brush tool, the mask tool and the land tool.</summary>
public sealed record BrushSettings
{
    /// <summary>Radius in world units. The dab is a circle; softness fades the outer part.</summary>
    public double Radius { get; init; } = 24;

    /// <summary>0 = hard edge, 1 = the whole radius is a gradient.</summary>
    public double Softness { get; init; } = 0.5;

    /// <summary>Stroke opacity 0..1 applied once per stroke, not once per dab.</summary>
    public double Opacity { get; init; } = 1.0;

    /// <summary>Paint colour as "#AARRGGBB" (ignored when a texture is set).</summary>
    public string Color { get; init; } = "#FF000000";

    /// <summary>Optional pattern; sampled in world coordinates so it stays put while painting.</summary>
    public BrushTexture? Texture { get; init; }

    /// <summary>Erase instead of paint: the dab removes coverage from the layer.</summary>
    public bool Eraser { get; init; }

    /// <summary>Distance between dabs as a fraction of the radius.</summary>
    public double Spacing { get; init; } = 0.25;
}

/// <summary>Direction of a mask stroke: hide the layer under the brush, or bring it back.</summary>
public enum MaskPaintMode
{
    Hide = 0,
    Reveal = 1,
}

/// <summary>
/// One in-progress raster gesture over one layer. It accumulates per-tile coverage for the dabs
/// added so far - and nothing else. Only tiles the brush actually touched are ever materialized,
/// so painting a 65 536-unit map costs the brush footprint, not the canvas area (DEC-009).
///
/// The live coverage is what the canvas draws as the stroke preview; <see cref="Commit"/> turns it
/// into the before/after tile pairs of a single reversible command.
/// </summary>
public sealed class RasterStrokeSession
{
    private readonly Dictionary<TileKey, byte[]> _coverage = [];
    private readonly TileMap _baseTiles;
    private double _lastX;
    private double _lastY;
    private bool _started;

    public RasterStrokeSession(TileMap baseTiles, BrushSettings settings)
    {
        ArgumentNullException.ThrowIfNull(baseTiles);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.Radius);

        _baseTiles = baseTiles;
        Settings = settings;
    }

    public BrushSettings Settings { get; }

    /// <summary>Per-tile coverage 0..255 accumulated by this stroke. The canvas draws it as preview.</summary>
    public IReadOnlyDictionary<TileKey, byte[]> Coverage => _coverage;

    public int TouchedTileCount => _coverage.Count;

    public bool IsEmpty => _coverage.Count == 0;

    /// <summary>Adds a dab at the world point, filling in the gap since the previous point.</summary>
    public void AddPoint(double worldX, double worldY)
    {
        if (!_started)
        {
            _started = true;
            Dab(worldX, worldY);
        }
        else
        {
            var dx = worldX - _lastX;
            var dy = worldY - _lastY;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));
            var step = Math.Max(1.0, Settings.Radius * Math.Max(0.05, Settings.Spacing));

            for (var travelled = step; travelled <= distance; travelled += step)
            {
                var t = travelled / distance;
                Dab(_lastX + (dx * t), _lastY + (dy * t));
            }

            Dab(worldX, worldY);
        }

        _lastX = worldX;
        _lastY = worldY;
    }

    private void Dab(double centerX, double centerY)
    {
        var radius = Settings.Radius;
        var inner = radius * (1.0 - Math.Clamp(Settings.Softness, 0, 1));

        var minTile = TileMap.KeyAt(centerX - radius, centerY - radius);
        var maxTile = TileMap.KeyAt(centerX + radius, centerY + radius);

        for (var ty = minTile.Y; ty <= maxTile.Y; ty++)
        {
            for (var tx = minTile.X; tx <= maxTile.X; tx++)
            {
                var key = new TileKey(tx, ty);
                if (!_coverage.TryGetValue(key, out var tile))
                {
                    tile = new byte[TileMap.TileSize * TileMap.TileSize];
                    _coverage[key] = tile;
                }

                var originX = tx * TileMap.TileSize;
                var originY = ty * TileMap.TileSize;

                var x0 = Math.Max(0, (int)Math.Floor(centerX - radius) - originX);
                var x1 = Math.Min(TileMap.TileSize - 1, (int)Math.Ceiling(centerX + radius) - originX);
                var y0 = Math.Max(0, (int)Math.Floor(centerY - radius) - originY);
                var y1 = Math.Min(TileMap.TileSize - 1, (int)Math.Ceiling(centerY + radius) - originY);

                for (var y = y0; y <= y1; y++)
                {
                    var dy = originY + y + 0.5 - centerY;
                    var rowStart = y * TileMap.TileSize;

                    for (var x = x0; x <= x1; x++)
                    {
                        var dx = originX + x + 0.5 - centerX;
                        var distance = Math.Sqrt((dx * dx) + (dy * dy));
                        if (distance > radius)
                        {
                            continue;
                        }

                        var falloff = distance <= inner
                            ? 1.0
                            : radius - inner <= 0 ? 1.0 : 1.0 - ((distance - inner) / (radius - inner));

                        var value = (byte)Math.Clamp(Math.Round(falloff * 255), 0, 255);
                        var index = rowStart + x;

                        // Max, not sum: overlapping dabs of one stroke must not darken the seam.
                        if (value > tile[index])
                        {
                            tile[index] = value;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Composites the accumulated coverage onto the layer's tiles and returns the before/after of
    /// exactly the touched tiles - the payload of one undoable stroke.
    /// </summary>
    public (Dictionary<TileKey, byte[]?> Before, Dictionary<TileKey, byte[]?> After) Commit()
    {
        if (_baseTiles.BytesPerPixel != 4)
        {
            throw new InvalidOperationException("Use CommitMask for single-channel tiles.");
        }

        var before = new Dictionary<TileKey, byte[]?>(_coverage.Count);
        var after = new Dictionary<TileKey, byte[]?>(_coverage.Count);
        var (br, bg, bb, ba) = ParseColor(Settings.Color);
        var strokeAlpha = Math.Clamp(Settings.Opacity, 0, 1) * (ba / 255.0);

        foreach (var (key, coverage) in _coverage)
        {
            var original = _baseTiles.TryGet(key);
            before[key] = original;

            var pixels = original is null
                ? new byte[TileMap.TileSize * TileMap.TileSize * 4]
                : (byte[])original.Clone();

            var originX = key.X * TileMap.TileSize;
            var originY = key.Y * TileMap.TileSize;

            for (var i = 0; i < coverage.Length; i++)
            {
                if (coverage[i] == 0)
                {
                    continue;
                }

                var alpha = coverage[i] / 255.0 * strokeAlpha;
                var offset = i * 4;

                if (Settings.Eraser)
                {
                    var keep = 1.0 - alpha;
                    for (var c = 0; c < 4; c++)
                    {
                        pixels[offset + c] = (byte)Math.Round(pixels[offset + c] * keep);
                    }

                    continue;
                }

                double sr = br, sg = bg, sb = bb;
                if (Settings.Texture is { } texture)
                {
                    var tx = Mod(originX + (i % TileMap.TileSize), texture.Width);
                    var ty = Mod(originY + (i / TileMap.TileSize), texture.Height);
                    var to = ((ty * texture.Width) + tx) * 4;
                    var textureAlpha = texture.Pixels[to + 3] / 255.0;
                    if (textureAlpha <= 0)
                    {
                        continue;
                    }

                    // The texture is premultiplied; recover straight colour for the blend below.
                    sr = texture.Pixels[to] / textureAlpha;
                    sg = texture.Pixels[to + 1] / textureAlpha;
                    sb = texture.Pixels[to + 2] / textureAlpha;
                    alpha *= textureAlpha;
                }

                var inverse = 1.0 - alpha;
                pixels[offset] = (byte)Math.Clamp(Math.Round((sr * alpha) + (pixels[offset] * inverse)), 0, 255);
                pixels[offset + 1] = (byte)Math.Clamp(Math.Round((sg * alpha) + (pixels[offset + 1] * inverse)), 0, 255);
                pixels[offset + 2] = (byte)Math.Clamp(Math.Round((sb * alpha) + (pixels[offset + 2] * inverse)), 0, 255);
                pixels[offset + 3] = (byte)Math.Clamp(Math.Round((255 * alpha) + (pixels[offset + 3] * inverse)), 0, 255);
            }

            after[key] = TileMap.IsEmptyTile(pixels) ? null : pixels;
        }

        return (before, after);
    }

    /// <summary>Same, for a single-channel mask or land coverage surface.</summary>
    public (Dictionary<TileKey, byte[]?> Before, Dictionary<TileKey, byte[]?> After) CommitCoverage(
        byte missingTileValue, bool subtract)
    {
        if (_baseTiles.BytesPerPixel != 1)
        {
            throw new InvalidOperationException("CommitCoverage works on single-channel tiles.");
        }

        var before = new Dictionary<TileKey, byte[]?>(_coverage.Count);
        var after = new Dictionary<TileKey, byte[]?>(_coverage.Count);
        var strokeAlpha = Math.Clamp(Settings.Opacity, 0, 1);

        foreach (var (key, coverage) in _coverage)
        {
            var original = _baseTiles.TryGet(key);
            before[key] = original;

            byte[] pixels;
            if (original is null)
            {
                pixels = new byte[TileMap.TileSize * TileMap.TileSize];
                if (missingTileValue != 0)
                {
                    Array.Fill(pixels, missingTileValue);
                }
            }
            else
            {
                pixels = (byte[])original.Clone();
            }

            for (var i = 0; i < coverage.Length; i++)
            {
                if (coverage[i] == 0)
                {
                    continue;
                }

                var alpha = coverage[i] / 255.0 * strokeAlpha;
                var target = subtract ? 0.0 : 255.0;
                pixels[i] = (byte)Math.Clamp(
                    Math.Round((target * alpha) + (pixels[i] * (1.0 - alpha))), 0, 255);
            }

            // A tile that ended up uniformly equal to the implicit value costs nothing to drop.
            after[key] = IsUniform(pixels, missingTileValue) ? null : pixels;
        }

        return (before, after);
    }

    private static bool IsUniform(byte[] pixels, byte value)
    {
        foreach (var b in pixels)
        {
            if (b != value)
            {
                return false;
            }
        }

        return true;
    }

    private static int Mod(int value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    internal static (byte R, byte G, byte B, byte A) ParseColor(string color)
    {
        var span = color.AsSpan().TrimStart('#');
        var value = uint.Parse(span, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);

        if (span.Length == 6)
        {
            value |= 0xFF000000;
        }

        return ((byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF),
            (byte)((value >> 24) & 0xFF));
    }
}
