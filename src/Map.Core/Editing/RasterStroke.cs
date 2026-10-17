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
    // Change counters per tile: a dab that writes a pixel of the tile bumps its counter, so a
    // consumer holding an image of the tile knows whether the bytes behind it moved.
    private readonly Dictionary<TileKey, int> _versions = [];
    private readonly HashSet<TileKey> _dirty = [];
    private int _stamp;
    private Dictionary<TileKey, RoughTile>? _roughTiles;
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

    /// <summary>Change counter of every tile in <see cref="Coverage"/>; it grows whenever a dab changes a pixel of the tile.</summary>
    public IReadOnlyDictionary<TileKey, int> Versions => _versions;

    private Dictionary<TileKey, byte[]>? _smoothed;
    private readonly Dictionary<TileKey, int> _smoothedVersions = [];

    /// <summary>
    /// The coverage as it will be committed: the dabs, with the corners of a rough stroke
    /// rounded by its smoothing. The canvas draws this as the preview, so the stroke looks the
    /// same while it is drawn and once it is on the map. Recomputed only around the tiles the
    /// dabs since the last request touched (CoverageSmoothing.RoundIncremental), so a long
    /// stroke costs the same per dab at its end as at its start.
    /// </summary>
    public IReadOnlyDictionary<TileKey, byte[]> CommittedCoverage
    {
        get
        {
            if (Settings.RoughSmoothingRadius <= 0)
            {
                return _coverage;
            }

            EnsureSmoothed();
            return _smoothed!;
        }
    }

    /// <summary>Change counters of the tiles of <see cref="CommittedCoverage"/>: a tile whose bytes did not change keeps its counter and its array.</summary>
    public IReadOnlyDictionary<TileKey, int> CommittedVersions
    {
        get
        {
            if (Settings.RoughSmoothingRadius <= 0)
            {
                return _versions;
            }

            EnsureSmoothed();
            return _smoothedVersions;
        }
    }

    private void EnsureSmoothed()
    {
        var radius = Settings.RoughSmoothingRadius;
        if (_smoothed is not null && _dirty.Count == 0)
        {
            return;
        }

        if (_smoothed is null)
        {
            _smoothed = new Dictionary<TileKey, byte[]>(CoverageSmoothing.Round(_coverage, radius));
            _smoothedVersions.Clear();
            foreach (var key in _smoothed.Keys)
            {
                _smoothedVersions[key] = ++_stamp;
            }
        }
        else
        {
            foreach (var (key, tile) in CoverageSmoothing.RoundIncremental(_coverage, radius, _dirty, _smoothed))
            {
                if (tile is null)
                {
                    if (_smoothed.Remove(key))
                    {
                        _smoothedVersions.Remove(key);
                    }

                    continue;
                }

                if (_smoothed.TryGetValue(key, out var previous) && ReferenceEquals(previous, tile))
                {
                    continue;
                }

                _smoothed[key] = tile;
                _smoothedVersions[key] = ++_stamp;
            }
        }

        _dirty.Clear();
    }

    /// <summary>
    /// The cells of <see cref="RoughCells"/> over one tile: for every pixel the index of the
    /// cell it belongs to, with the sites of the cells of the tile's window. The partition is
    /// fixed for a cell size, so it is computed once per tile and stroke and every rough dab
    /// that crosses the tile is a lookup per pixel instead of nine hashed sites per pixel.
    /// </summary>
    private sealed class RoughTile
    {
        // The index fits a byte for cells of 20 units and up (the usual coast); a 64 KiB byte
        // array stays out of the large-object heap, where a 128 KiB one per touched tile made
        // every long stroke pay a full collection.
        private readonly byte[]? _small;
        private readonly ushort[]? _wide;
        public required double[] SiteX { get; init; }
        public required double[] SiteY { get; init; }
        public required int Window { get; init; }
        public RoughTile(int cellCount) { if (cellCount <= 256) _small = new byte[TileMap.TileSize * TileMap.TileSize]; else _wide = new ushort[TileMap.TileSize * TileMap.TileSize]; }
        public int Cell(int index) => _small is not null ? _small[index] : _wide![index];
        public void Set(int index, int cell) { if (_small is not null) _small[index] = (byte)cell; else _wide![index] = (ushort)cell; }
    }

    /// <summary>Tiles of rough cells kept per stroke; 128 KiB each, released with the stroke.</summary>
    private const int MaxRoughTiles = 512;

    private RoughTile? RoughTileFor(TileKey key, double cellSize)
    {
        if (cellSize < 4)
        {
            return null; // the window would not fit the index; the per-pixel search still works
        }

        _roughTiles ??= [];
        if (_roughTiles.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_roughTiles.Count >= MaxRoughTiles)
        {
            return null;
        }

        var originX = key.X * TileMap.TileSize;
        var originY = key.Y * TileMap.TileSize;
        var baseCellX = (int)Math.Floor(originX / cellSize) - 1;
        var baseCellY = (int)Math.Floor(originY / cellSize) - 1;
        var lastCellX = (int)Math.Floor((originX + TileMap.TileSize - 0.5) / cellSize) + 1;
        var lastCellY = (int)Math.Floor((originY + TileMap.TileSize - 0.5) / cellSize) + 1;
        var window = Math.Max(lastCellX - baseCellX, lastCellY - baseCellY) + 1;
        if (window > 255)
        {
            return null;
        }

        var siteX = new double[window * window];
        var siteY = new double[window * window];
        for (var j = 0; j < window; j++)
        {
            for (var i = 0; i < window; i++)
            {
                (siteX[(j * window) + i], siteY[(j * window) + i]) = RoughCells.Site(baseCellX + i, baseCellY + j, cellSize);
            }
        }

        var tile = new RoughTile(window * window) { SiteX = siteX, SiteY = siteY, Window = window };
        for (var y = 0; y < TileMap.TileSize; y++)
        {
            var worldY = originY + y + 0.5;
            var cellY = (int)Math.Floor(worldY / cellSize);
            for (var x = 0; x < TileMap.TileSize; x++)
            {
                var worldX = originX + x + 0.5;
                var cellX = (int)Math.Floor(worldX / cellSize);
                // The same search as RoughCells.NearestSite, in the same order, over the same sites.
                var bestDistance = double.MaxValue;
                var best = 0;
                for (var j = cellY - 1; j <= cellY + 1; j++)
                {
                    for (var i = cellX - 1; i <= cellX + 1; i++)
                    {
                        var index = ((j - baseCellY) * window) + (i - baseCellX);
                        var dx = siteX[index] - worldX;
                        var dy = siteY[index] - worldY;
                        var distance = (dx * dx) + (dy * dy);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = index;
                        }
                    }
                }

                tile.Set((y * TileMap.TileSize) + x, best);
            }
        }

        _roughTiles[key] = tile;
        return tile;
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

        for (var ty = minTile.Y; ty <= maxTile.Y; ty++)
        {
            for (var tx = minTile.X; tx <= maxTile.X; tx++)
            {
                var key = new TileKey(tx, ty);
                // A tile is materialized by its first written pixel, not by the footprint
                // touching it: a rough dab's reach is wider than its cells.
                _coverage.TryGetValue(key, out var tile);

                var originX = tx * TileMap.TileSize;
                var originY = ty * TileMap.TileSize;

                var x0 = Math.Max(0, (int)Math.Floor(centerX - reach) - originX);
                var x1 = Math.Min(TileMap.TileSize - 1, (int)Math.Ceiling(centerX + reach) - originX);
                var y0 = Math.Max(0, (int)Math.Floor(centerY - reach) - originY);
                var y1 = Math.Min(TileMap.TileSize - 1, (int)Math.Ceiling(centerY + reach) - originY);

                // Rough dab: which cells of this tile's window have their site inside the circle.
                RoughTile? rough = null;
                bool[]? insideCells = null;
                if (cellSize > 0 && RoughTileFor(key, cellSize) is { } roughTile)
                {
                    rough = roughTile;
                    insideCells = new bool[roughTile.SiteX.Length];
                    for (var c = 0; c < insideCells.Length; c++)
                    {
                        var sx = roughTile.SiteX[c] - centerX;
                        var sy = roughTile.SiteY[c] - centerY;
                        insideCells[c] = (sx * sx) + (sy * sy) <= radiusSquared;
                    }
                }

                var changed = false;
                for (var y = y0; y <= y1; y++)
                {
                    var dy = originY + y + 0.5 - centerY;
                    var rowStart = y * TileMap.TileSize;

                    for (var x = x0; x <= x1; x++)
                    {
                        var index = rowStart + x;
                        var existing = tile is null ? (byte)0 : tile[index];
                        if (existing == 255)
                        {
                            continue; // full already; no dab can raise it further
                        }

                        var dx = originX + x + 0.5 - centerX;

                        double alpha;
                        if (cellSize > 0)
                        {
                            // The pixel belongs to the dab when the centre of its cell does.
                            if (rough is not null)
                            {
                                if (!insideCells![rough.Cell(index)])
                                {
                                    continue;
                                }
                            }
                            else
                            {
                                var (siteX, siteY) = RoughCells.NearestSite(originX + x + 0.5, originY + y + 0.5, cellSize);
                                var sx = siteX - centerX;
                                var sy = siteY - centerY;
                                if ((sx * sx) + (sy * sy) > radiusSquared)
                                {
                                    continue;
                                }
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

                        // Dabs accumulate "over" one another towards full coverage - never past
                        // it, so a seam cannot darken, and never below what is there - so the soft
                        // rims of neighbouring dabs build a dense, even stroke instead of leaving
                        // the scalloped edge that keeping only the strongest dab produces.
                        var combined = (byte)Math.Clamp(Math.Round(existing + (value * (255 - existing))), 0, 255);
                        if (combined > existing)
                        {
                            if (tile is null)
                            {
                                tile = new byte[TileMap.TileSize * TileMap.TileSize];
                                _coverage[key] = tile;
                            }

                            tile[index] = combined;
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    _versions[key] = ++_stamp;
                    _dirty.Add(key);
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
        var sampler = new PaintSampler(paintSettings);
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
                var (sr, sg, sb, sourceAlpha) = sampler.Sample(worldX, worldY);
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
        var pixels = new byte[TileMap.TileSize * TileMap.TileSize * 4];
        RenderPreviewTile(settings, key, coverage, pixels);
        return pixels;
    }

    /// <summary>The same, into a caller-owned buffer (cleared here), so a frame can reuse one buffer per tile.</summary>
    public static void RenderPreviewTile(BrushSettings settings, TileKey key, byte[] coverage, Span<byte> pixels)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(coverage);
        if (coverage.Length != TileMap.TileSize * TileMap.TileSize)
            throw new ArgumentException("Coverage tile has an invalid size.", nameof(coverage));
        if (pixels.Length < TileMap.TileSize * TileMap.TileSize * 4)
            throw new ArgumentException("Pixel buffer is too small.", nameof(pixels));
        pixels = pixels[..(TileMap.TileSize * TileMap.TileSize * 4)];
        pixels.Clear();
        var sampler = new PaintSampler(settings);
        if (settings.Texture is null)
        {
            // One colour for the whole tile: the four channels depend on the coverage byte alone.
            var (r, g, b, sourceAlpha) = sampler.Sample(0, 0);
            Span<byte> lutR = stackalloc byte[256], lutG = stackalloc byte[256], lutB = stackalloc byte[256], lutA = stackalloc byte[256];
            for (var c = 1; c < 256; c++)
            {
                var alpha = c / 255.0 * sourceAlpha;
                lutR[c] = (byte)Math.Clamp(Math.Round(r * alpha), 0, 255);
                lutG[c] = (byte)Math.Clamp(Math.Round(g * alpha), 0, 255);
                lutB[c] = (byte)Math.Clamp(Math.Round(b * alpha), 0, 255);
                lutA[c] = (byte)Math.Clamp(Math.Round(255 * alpha), 0, 255);
            }
            for (var i = 0; i < coverage.Length; i++)
            {
                var c = coverage[i];
                if (c == 0) continue;
                var offset = i * 4;
                pixels[offset] = lutR[c]; pixels[offset + 1] = lutG[c]; pixels[offset + 2] = lutB[c]; pixels[offset + 3] = lutA[c];
            }
            return;
        }
        var originX = key.X * TileMap.TileSize; var originY = key.Y * TileMap.TileSize;
        for (var i = 0; i < coverage.Length; i++)
        {
            if (coverage[i] == 0) continue;
            var (r, g, b, sourceAlpha) = sampler.Sample(
                originX + (i % TileMap.TileSize), originY + (i / TileMap.TileSize));
            var alpha = coverage[i] / 255.0 * sourceAlpha;
            var offset = i * 4;
            pixels[offset] = (byte)Math.Clamp(Math.Round(r * alpha), 0, 255);
            pixels[offset + 1] = (byte)Math.Clamp(Math.Round(g * alpha), 0, 255);
            pixels[offset + 2] = (byte)Math.Clamp(Math.Round(b * alpha), 0, 255);
            pixels[offset + 3] = (byte)Math.Clamp(Math.Round(255 * alpha), 0, 255);
        }
    }

    /// <summary>
    /// The paint of a brush at a world point: colour, texture sample and colour adjustments.
    /// Everything that does not depend on the point (the parsed colour, the texture's rotation,
    /// the hue rotation) is computed once here instead of once per pixel; the arithmetic per
    /// pixel is exactly the former per-pixel sampler's, so preview and commit still agree byte
    /// for byte with each other and with earlier maps.
    /// </summary>
    private readonly struct PaintSampler
    {
        private readonly double _alpha;
        private readonly BrushTexture? _texture;
        private readonly double _offsetX, _offsetY, _scale, _cos, _sin, _textureOpacity;
        private readonly double _saturation, _hueCos, _hueSin, _contrast, _brightness;
        private readonly double _constantR, _constantG, _constantB;

        public PaintSampler(BrushSettings settings)
        {
            var (r, g, b, a) = ParseColor(settings.Color);
            _alpha = Math.Clamp(settings.Opacity, 0, 1) * (a / 255.0);
            _texture = settings.Texture;
            if (_texture is not null)
            {
                _offsetX = settings.TextureOffsetX; _offsetY = settings.TextureOffsetY;
                _scale = Math.Clamp(settings.TextureScale, 1.0 / 4096, 64);
                var radians = -settings.TextureRotationDegrees * Math.PI / 180;
                _cos = Math.Cos(radians); _sin = Math.Sin(radians);
                _textureOpacity = Math.Clamp(settings.TextureOpacity, 0, 1);
            }
            _saturation = Math.Clamp(1 + settings.Saturation, 0, 2);
            var angle = Math.Clamp(settings.HueDegrees, -180, 180) * Math.PI / 180;
            _hueCos = Math.Cos(angle); _hueSin = Math.Sin(angle);
            _contrast = Math.Clamp(1 + settings.Contrast, 0, 2);
            _brightness = Math.Clamp(settings.Brightness, -1, 1);
            (_constantR, _constantG, _constantB) = Adjust(r, g, b);
        }

        public (double R, double G, double B, double Alpha) Sample(double worldX, double worldY)
        {
            if (_texture is not { } texture)
            {
                return (_constantR, _constantG, _constantB, _alpha);
            }

            worldX -= _offsetX; worldY -= _offsetY;
            var textureX = ((worldX * _cos) - (worldY * _sin)) / _scale;
            var textureY = ((worldX * _sin) + (worldY * _cos)) / _scale;
            var tx = TextureIndex(textureX, texture.Width, texture.MirrorRepeat);
            var ty = TextureIndex(textureY, texture.Height, texture.MirrorRepeat);
            var to = ((ty * texture.Width) + tx) * 4;
            var textureAlpha = texture.Pixels[to + 3] / 255.0;
            if (textureAlpha <= 0) return (0, 0, 0, 0);
            var sr = texture.Pixels[to] / textureAlpha;
            var sg = texture.Pixels[to + 1] / textureAlpha;
            var sb = texture.Pixels[to + 2] / textureAlpha;
            var alpha = _alpha;
            alpha *= textureAlpha * _textureOpacity;
            var (ar, ag, ab) = Adjust(sr, sg, sb);
            return (ar, ag, ab, alpha);
        }

        private (double R, double G, double B) Adjust(double red, double green, double blue)
        {
            var r = red / 255.0; var g = green / 255.0; var b = blue / 255.0;
            var luminance = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
            var saturation = _saturation;
            r = luminance + ((r - luminance) * saturation);
            g = luminance + ((g - luminance) * saturation);
            b = luminance + ((b - luminance) * saturation);
            var cos = _hueCos; var sin = _hueSin;
            var nr = (.213 + cos * .787 - sin * .213) * r
                     + (.715 - cos * .715 - sin * .715) * g
                     + (.072 - cos * .072 + sin * .928) * b;
            var ng = (.213 - cos * .213 + sin * .143) * r
                     + (.715 + cos * .285 + sin * .140) * g
                     + (.072 - cos * .072 - sin * .283) * b;
            var nb = (.213 - cos * .213 - sin * .787) * r
                     + (.715 - cos * .715 + sin * .715) * g
                     + (.072 + cos * .928 + sin * .072) * b;
            var contrast = _contrast;
            var brightness = _brightness;
            return (
                Math.Clamp(((nr - .5) * contrast + .5 + brightness) * 255, 0, 255),
                Math.Clamp(((ng - .5) * contrast + .5 + brightness) * 255, 0, 255),
                Math.Clamp(((nb - .5) * contrast + .5 + brightness) * 255, 0, 255));
        }
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
