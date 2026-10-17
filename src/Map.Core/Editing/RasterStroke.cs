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
    /// <summary>Reflect alternate tiles, matching the renderer and avoiding a colour jump at the tile edge.</summary>
    public bool MirrorRepeat { get; init; }
}

/// <summary>
/// An alpha-only brush tip: the dab takes this silhouette instead of the procedural circle.
/// Colour still comes from the brush; the tip only shapes coverage, which keeps the whole
/// tile/undo pipeline single-channel and unchanged.
/// </summary>
public sealed record BrushTipShape
{
    public BrushTipShape(int width, int height, byte[] alpha)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(alpha);
        if (alpha.Length != width * height)
        {
            throw new ArgumentException("Tip alpha buffer size does not match its dimensions.");
        }

        Width = width;
        Height = height;
        Alpha = alpha;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Alpha { get; }
}

/// <summary>What the brush paints with. Shared by the brush tool, the mask tool and the land tool.</summary>
public sealed record BrushSettings
{
    /// <summary>Radius in world units. The dab is a circle; softness fades the outer part.</summary>
    public double Radius { get; init; } = 24;

    /// <summary>0 = hard edge, 1 = the whole radius is a gradient.</summary>
    public double Softness { get; init; } = 0.5;

    /// <summary>
    /// Size in world units of the coast cells of a rough land dab; 0 keeps the plain round dab.
    /// With it, the dab is not a circle but the union of every cell of a fixed, map-wide cell field
    /// (<see cref="RoughCells"/>) whose centre lies inside the circle: the edge becomes an organic
    /// coastline whose feature size is this value, and consecutive dabs share the same cells, so
    /// a stroke joins seamlessly with itself and with earlier land. Softness is ignored: the cells
    /// are crisp, the renderer smooths the magnified edge. Only the procedural tip honours it.
    /// </summary>
    public double RoughCellSize { get; init; }

    /// <summary>
    /// 0..1: how far the corners of a rough stroke are rounded, as a fraction of half a cell.
    /// The cells keep their irregular layout; only their tips and notches are softened into
    /// arcs, the way a drawn coastline bends around a bay instead of breaking at an angle.
    /// </summary>
    public double RoughSmoothing { get; init; }

    /// <summary>The rounding radius in world units that <see cref="RoughSmoothing"/> asks for.</summary>
    public int RoughSmoothingRadius =>
        RoughCellSize > 0 ? (int)Math.Round(Math.Clamp(RoughSmoothing, 0, 1) * RoughCellSize * 0.5) : 0;

    /// <summary>Stroke opacity 0..1 applied once per stroke, not once per dab.</summary>
    public double Opacity { get; init; } = 1.0;

    /// <summary>Paint colour as "#AARRGGBB" (ignored when a texture is set).</summary>
    public string Color { get; init; } = "#FF000000";

    /// <summary>Optional pattern; sampled in world coordinates so it stays put while painting.</summary>
    public BrushTexture? Texture { get; init; }

    public double TextureScale { get; init; } = 1;
    public double TextureRotationDegrees { get; init; }
    public double TextureOffsetX { get; init; }
    public double TextureOffsetY { get; init; }
    public double TextureOpacity { get; init; } = 1;
    public double HueDegrees { get; init; }
    public double Saturation { get; init; }
    public double Brightness { get; init; }
    public double Contrast { get; init; }

    /// <summary>Erase instead of paint: the dab removes coverage from the layer.</summary>
    public bool Eraser { get; init; }

    /// <summary>Distance between dabs as a fraction of the radius.</summary>
    public double Spacing { get; init; } = 0.25;

    /// <summary>Optional dab silhouette; null keeps the procedural soft circle.</summary>
    public BrushTipShape? Tip { get; init; }

    /// <summary>0..1: each dab may land up to 2×radius×Scatter away from the path.</summary>
    public double Scatter { get; init; }

    /// <summary>Base rotation of the tip silhouette, clockwise degrees.</summary>
    public double AngleDegrees { get; init; }

    /// <summary>Randomly mirror the tip per dab.</summary>
    public bool RandomFlipHorizontal { get; init; }

    public bool RandomFlipVertical { get; init; }

    /// <summary>0..1: per-dab size varies by ±SizeJitter of the radius.</summary>
    public double SizeJitter { get; init; }

    /// <summary>0..1: per-dab coverage may drop by up to this fraction.</summary>
    public double OpacityJitter { get; init; }

    /// <summary>
    /// Seed of the per-stroke randomness. It is fixed BEFORE the stroke command exists, so the
    /// committed tiles never depend on replay: undo/redo restore bytes, they never re-roll.
    /// </summary>
    public int Seed { get; init; }

    /// <summary>Core-only pen dynamics: stylus pressure scales the dab size.</summary>
    public bool PressureAffectsSize { get; init; }

    /// <summary>Core-only pen dynamics: stylus pressure scales the dab coverage.</summary>
    public bool PressureAffectsOpacity { get; init; }

    public bool PressureAffectsSoftness { get; init; }
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
/// so painting a 131 072-unit map costs the brush footprint, not the canvas area (DEC-009).
///
/// The live coverage is what the canvas draws as the stroke preview; <see cref="Commit"/> turns it
/// into the before/after tile pairs of a single reversible command.
/// </summary>
public sealed class RasterStrokeSession
{
    private readonly Dictionary<TileKey, byte[]> _coverage = [];
    private readonly TileMap _baseTiles;
    private readonly bool _randomized;
    private DeterministicRandom _random;
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
        _random = new DeterministicRandom(settings.Seed);
        _randomized = settings.Scatter > 0 || settings.SizeJitter > 0 || settings.OpacityJitter > 0
            || settings.RandomFlipHorizontal || settings.RandomFlipVertical;
    }

    public BrushSettings Settings { get; }

    /// <summary>Per-tile coverage 0..255 accumulated by this stroke, dab by dab.</summary>
    public IReadOnlyDictionary<TileKey, byte[]> Coverage => _coverage;

    private IReadOnlyDictionary<TileKey, byte[]>? _smoothed;
    private int _smoothedDabs;
    private int _dabs;

    /// <summary>
    /// The coverage as it will be committed: the dabs, with the corners of a rough stroke
    /// rounded by its smoothing. The canvas draws this as the preview, so the stroke looks the
    /// same while it is drawn and once it is on the map. Computed once per added dab.
    /// </summary>
    public IReadOnlyDictionary<TileKey, byte[]> CommittedCoverage
    {
        get
        {
            var radius = Settings.RoughSmoothingRadius;
            if (radius <= 0)
            {
                return _coverage;
            }

            if (_smoothed is null || _smoothedDabs != _dabs)
            {
                _smoothed = CoverageSmoothing.Round(_coverage, radius);
                _smoothedDabs = _dabs;
            }

            return _smoothed;
        }
    }

    public int TouchedTileCount => _coverage.Count;

    public bool IsEmpty => _coverage.Count == 0;

    /// <summary>Adds a dab at the world point, filling in the gap since the previous point.</summary>
    public void AddPoint(double worldX, double worldY) => AddPoint(worldX, worldY, pressure: 1.0);

    /// <summary>
    /// Same, with stylus pressure 0..1. Pressure only matters when the settings opted into pen
    /// dynamics; fill-in dabs between two reported points take the newer point's pressure.
    /// </summary>
    public void AddPoint(double worldX, double worldY, double pressure)
    {
        pressure = double.IsFinite(pressure) ? Math.Clamp(pressure, 0, 1) : 1.0;
        if (!_started)
        {
            _started = true;
            Dab(worldX, worldY, pressure);
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
                Dab(_lastX + (dx * t), _lastY + (dy * t), pressure);
            }

            Dab(worldX, worldY, pressure);
        }

        _lastX = worldX;
        _lastY = worldY;
    }

    private void Dab(double centerX, double centerY, double pressure)
    {
        var radius = Settings.Radius;
        var opacityFactor = 1.0;
        var flipHorizontal = false;
        var flipVertical = false;

        // The draw order and count are fixed per dab, so one seed always replays one stroke.
        if (_randomized)
        {
            var scatterAngle = _random.NextDouble() * Math.PI * 2;
            var scatterDistance = _random.NextDouble() * Math.Clamp(Settings.Scatter, 0, 1) * radius * 2;
            var sizeDraw = _random.NextDouble();
            var opacityDraw = _random.NextDouble();
            var flipHorizontalDraw = _random.NextDouble();
            var flipVerticalDraw = _random.NextDouble();

            centerX += Math.Cos(scatterAngle) * scatterDistance;
            centerY += Math.Sin(scatterAngle) * scatterDistance;
            radius *= 1.0 + (Math.Clamp(Settings.SizeJitter, 0, 1) * ((sizeDraw * 2) - 1));
            opacityFactor = 1.0 - (Math.Clamp(Settings.OpacityJitter, 0, 1) * opacityDraw);
            flipHorizontal = Settings.RandomFlipHorizontal && flipHorizontalDraw < 0.5;
            flipVertical = Settings.RandomFlipVertical && flipVerticalDraw < 0.5;
        }

        if (Settings.PressureAffectsSize)
        {
            radius *= pressure;
        }

        if (Settings.PressureAffectsOpacity)
        {
            opacityFactor *= pressure;
        }

        if (radius < 0.25 || opacityFactor <= 0)
        {
            return; // a zero-pressure or fully jittered-out dab paints nothing
        }

        var effectiveSoftness = Math.Clamp(Settings.Softness, 0, 1);
        if (Settings.PressureAffectsSoftness) effectiveSoftness *= pressure;
        var inner = radius * (1.0 - effectiveSoftness);
        var tip = Settings.Tip;
        var angleRadians = -Settings.AngleDegrees * Math.PI / 180.0;
        var angleCos = Math.Cos(angleRadians);
        var angleSin = Math.Sin(angleRadians);

        // A rough dab keeps every cell whose centre is inside the circle, and a cell reaches
        // beyond its centre by up to RoughCells.Reach of the cell size: that is the footprint.
        var cellSize = tip is null && Settings.RoughCellSize > 0 ? Settings.RoughCellSize : 0;
        var reach = cellSize > 0 ? radius + (cellSize * RoughCells.Reach) : radius;
        var radiusSquared = radius * radius;

        var minTile = TileMap.KeyAt(centerX - reach, centerY - reach);
        var maxTile = TileMap.KeyAt(centerX + reach, centerY + reach);
        _dabs++;

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

                var x0 = Math.Max(0, (int)Math.Floor(centerX - reach) - originX);
                var x1 = Math.Min(TileMap.TileSize - 1, (int)Math.Ceiling(centerX + reach) - originX);
                var y0 = Math.Max(0, (int)Math.Floor(centerY - reach) - originY);
                var y1 = Math.Min(TileMap.TileSize - 1, (int)Math.Ceiling(centerY + reach) - originY);

                for (var y = y0; y <= y1; y++)
                {
                    var dy = originY + y + 0.5 - centerY;
                    var rowStart = y * TileMap.TileSize;

                    for (var x = x0; x <= x1; x++)
                    {
                        var dx = originX + x + 0.5 - centerX;

                        double alpha;
                        if (cellSize > 0)
                        {
                            // The pixel belongs to the dab when the centre of its cell does.
                            var (siteX, siteY) = RoughCells.NearestSite(originX + x + 0.5, originY + y + 0.5, cellSize);
                            var sx = siteX - centerX;
                            var sy = siteY - centerY;
                            if ((sx * sx) + (sy * sy) > radiusSquared)
                            {
                                continue;
                            }

                            alpha = 1.0;
                        }
                        else if (tip is null)
                        {
                            var distance = Math.Sqrt((dx * dx) + (dy * dy));
                            if (distance > radius)
                            {
                                continue;
                            }

                            // The soft part of the dab falls off as a Gaussian bell, not a straight
                            // ramp: dense, full near the solid core and fading to a feathered tail,
                            // which reads as a natural brush edge instead of a cone of alpha.
                            if (distance <= inner || radius - inner <= 0)
                            {
                                alpha = 1.0;
                            }
                            else
                            {
                                var t = (distance - inner) / (radius - inner);
                                alpha = Math.Exp(-t * t * 6.0);
                            }
                        }
                        else
                        {
                            // Rotate into the tip's frame, mirror per dab, then sample nearest.
                            var localX = (dx * angleCos) - (dy * angleSin);
                            var localY = (dx * angleSin) + (dy * angleCos);
                            if (flipHorizontal)
                            {
                                localX = -localX;
                            }

                            if (flipVertical)
                            {
                                localY = -localY;
                            }

                            var u = ((localX / radius) + 1) * 0.5 * tip.Width;
                            var v = ((localY / radius) + 1) * 0.5 * tip.Height;
                            if (u < 0 || u >= tip.Width || v < 0 || v >= tip.Height)
                            {
                                continue;
                            }

                            alpha = tip.Alpha[((int)v * tip.Width) + (int)u] / 255.0;
                        }

                        var value = Math.Clamp(alpha * opacityFactor, 0, 1);
                        var index = rowStart + x;

                        // Dabs accumulate "over" one another towards full coverage - never past
                        // it, so a seam cannot darken, and never below what is there - so the soft
                        // rims of neighbouring dabs build a dense, even stroke instead of leaving
                        // the scalloped edge that keeping only the strongest dab produces.
                        var existing = tile[index];
                        var combined = (byte)Math.Clamp(Math.Round(existing + (value * (255 - existing))), 0, 255);
                        if (combined > existing)
                        {
                            tile[index] = combined;
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
        var paintSettings = Settings.Eraser ? Settings with { Texture = null } : Settings;
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

                var worldX = originX + (i % TileMap.TileSize);
                var worldY = originY + (i / TileMap.TileSize);
                var (sr, sg, sb, sourceAlpha) = SamplePaint(paintSettings, worldX, worldY);
                var alpha = coverage[i] / 255.0 * sourceAlpha;
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

                if (alpha <= 0) continue;

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

    /// <summary>Premultiplied preview source evaluated by the exact same sampler as Commit.</summary>
    public static byte[] RenderPreviewTile(BrushSettings settings, TileKey key, byte[] coverage)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(coverage);
        if (coverage.Length != TileMap.TileSize * TileMap.TileSize)
            throw new ArgumentException("Coverage tile has an invalid size.", nameof(coverage));
        var pixels = new byte[TileMap.TileSize * TileMap.TileSize * 4];
        var originX = key.X * TileMap.TileSize; var originY = key.Y * TileMap.TileSize;
        for (var i = 0; i < coverage.Length; i++)
        {
            if (coverage[i] == 0) continue;
            var (r, g, b, sourceAlpha) = SamplePaint(settings,
                originX + (i % TileMap.TileSize), originY + (i / TileMap.TileSize));
            var alpha = coverage[i] / 255.0 * sourceAlpha;
            var offset = i * 4;
            pixels[offset] = (byte)Math.Clamp(Math.Round(r * alpha), 0, 255);
            pixels[offset + 1] = (byte)Math.Clamp(Math.Round(g * alpha), 0, 255);
            pixels[offset + 2] = (byte)Math.Clamp(Math.Round(b * alpha), 0, 255);
            pixels[offset + 3] = (byte)Math.Clamp(Math.Round(255 * alpha), 0, 255);
        }
        return pixels;
    }

    private static (double R, double G, double B, double Alpha) SamplePaint(
        BrushSettings settings, double worldX, double worldY)
    {
        var (r, g, b, a) = ParseColor(settings.Color);
        var alpha = Math.Clamp(settings.Opacity, 0, 1) * (a / 255.0);
        double sr = r, sg = g, sb = b;
        if (settings.Texture is { } texture)
        {
            worldX -= settings.TextureOffsetX; worldY -= settings.TextureOffsetY;
            var scale = Math.Clamp(settings.TextureScale, 1.0 / 4096, 64);
            var radians = -settings.TextureRotationDegrees * Math.PI / 180;
            var cos = Math.Cos(radians); var sin = Math.Sin(radians);
            var textureX = ((worldX * cos) - (worldY * sin)) / scale;
            var textureY = ((worldX * sin) + (worldY * cos)) / scale;
            var tx = TextureIndex(textureX, texture.Width, texture.MirrorRepeat);
            var ty = TextureIndex(textureY, texture.Height, texture.MirrorRepeat);
            var to = ((ty * texture.Width) + tx) * 4;
            var textureAlpha = texture.Pixels[to + 3] / 255.0;
            if (textureAlpha <= 0) return (0, 0, 0, 0);
            sr = texture.Pixels[to] / textureAlpha;
            sg = texture.Pixels[to + 1] / textureAlpha;
            sb = texture.Pixels[to + 2] / textureAlpha;
            alpha *= textureAlpha * Math.Clamp(settings.TextureOpacity, 0, 1);
        }
        (sr, sg, sb) = AdjustColor(sr, sg, sb, settings);
        return (sr, sg, sb, alpha);
    }

    private static int TextureIndex(double position, int size, bool mirror)
    {
        var index = Mod((int)Math.Floor(position), mirror ? size * 2 : size);
        return index < size ? index : size * 2 - 1 - index;
    }

    /// <summary>Same, for a single-channel mask or land coverage surface.</summary>
    public (Dictionary<TileKey, byte[]?> Before, Dictionary<TileKey, byte[]?> After) CommitCoverage(
        byte missingTileValue, bool subtract)
    {
        if (_baseTiles.BytesPerPixel != 1)
        {
            throw new InvalidOperationException("CommitCoverage works on single-channel tiles.");
        }

        var committed = CommittedCoverage;
        var before = new Dictionary<TileKey, byte[]?>(committed.Count);
        var after = new Dictionary<TileKey, byte[]?>(committed.Count);
        var strokeAlpha = Math.Clamp(Settings.Opacity, 0, 1);

        foreach (var (key, coverage) in committed)
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

    private static (double R, double G, double B) AdjustColor(
        double red, double green, double blue, BrushSettings settings)
    {
        var r = red / 255.0; var g = green / 255.0; var b = blue / 255.0;
        var luminance = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        var saturation = Math.Clamp(1 + settings.Saturation, 0, 2);
        r = luminance + ((r - luminance) * saturation);
        g = luminance + ((g - luminance) * saturation);
        b = luminance + ((b - luminance) * saturation);
        var angle = Math.Clamp(settings.HueDegrees, -180, 180) * Math.PI / 180;
        var cos = Math.Cos(angle); var sin = Math.Sin(angle);
        var nr = (.213 + cos * .787 - sin * .213) * r
                 + (.715 - cos * .715 - sin * .715) * g
                 + (.072 - cos * .072 + sin * .928) * b;
        var ng = (.213 - cos * .213 + sin * .143) * r
                 + (.715 + cos * .285 + sin * .140) * g
                 + (.072 - cos * .072 - sin * .283) * b;
        var nb = (.213 - cos * .213 - sin * .787) * r
                 + (.715 - cos * .715 + sin * .715) * g
                 + (.072 + cos * .928 + sin * .072) * b;
        var contrast = Math.Clamp(1 + settings.Contrast, 0, 2);
        var brightness = Math.Clamp(settings.Brightness, -1, 1);
        return (
            Math.Clamp(((nr - .5) * contrast + .5 + brightness) * 255, 0, 255),
            Math.Clamp(((ng - .5) * contrast + .5 + brightness) * 255, 0, 255),
            Math.Clamp(((nb - .5) * contrast + .5 + brightness) * 255, 0, 255));
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
