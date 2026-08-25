using Map.Spike.Core.Documents;
using Map.Spike.Core.Geometry;

namespace Map.Spike.Core.Commands;

/// <summary>Live mask gesture. Lowers coverage (hides the target raster) under the cursor.</summary>
public sealed class MaskStrokeSession(MaskLayer mask, BrushSettings settings)
{
    private readonly MaskLayer _mask = mask;
    private readonly BrushSettings _settings = settings;
    private readonly Dictionary<TileKey, byte[]?> _before = [];
    private readonly HashSet<TileKey> _touched = [];

    private Vec2? _last;
    private bool _completed;

    public IReadOnlyCollection<TileKey> TouchedTiles => _touched;

    public Rect2 DirtyWorldBounds { get; private set; } = Rect2.Empty;

    public void AddPoint(Vec2 world)
    {
        if (_completed) throw new InvalidOperationException("Mask stroke already completed.");

        if (_last is null)
        {
            Stamp(world);
        }
        else
        {
            var from = _last.Value;
            var delta = world - from;
            var steps = (int)Math.Max(1, Math.Ceiling(delta.Length / _settings.Spacing));
            for (var i = 1; i <= steps; i++)
            {
                var t = (double)i / steps;
                Stamp(new Vec2(from.X + (delta.X * t), from.Y + (delta.Y * t)));
            }
        }

        _last = world;
    }

    public MaskStrokeCommand? Complete(string name = "Mask stroke")
    {
        if (_completed) throw new InvalidOperationException("Mask stroke already completed.");
        _completed = true;

        if (_touched.Count == 0) return null;

        var after = new Dictionary<TileKey, byte[]?>(_touched.Count);
        foreach (var key in _touched)
        {
            var current = _mask.TryGetTile(key);
            after[key] = current is null ? null : (byte[])current.Clone();
        }

        return new MaskStrokeCommand(_mask.Id, _before, after, name);
    }

    private void Stamp(Vec2 center)
    {
        var radius = _settings.Radius;
        var tileSize = _mask.TileSize;

        var minX = (int)Math.Floor(center.X - radius);
        var maxX = (int)Math.Ceiling(center.X + radius);
        var minY = (int)Math.Floor(center.Y - radius);
        var maxY = (int)Math.Ceiling(center.Y + radius);

        var inner = radius * Math.Clamp(_settings.Hardness, 0, 1);

        for (var ty = (int)Math.Floor(minY / (double)tileSize); ty <= (int)Math.Floor(maxY / (double)tileSize); ty++)
        {
            for (var tx = (int)Math.Floor(minX / (double)tileSize); tx <= (int)Math.Floor(maxX / (double)tileSize); tx++)
            {
                var key = new TileKey(tx, ty);
                if (_touched.Add(key))
                {
                    var existing = _mask.TryGetTile(key);
                    _before[key] = existing is null ? null : (byte[])existing.Clone();
                }

                var pixels = _mask.GetOrCreateTile(key);
                var originX = tx * tileSize;
                var originY = ty * tileSize;

                var localMinX = Math.Max(0, minX - originX);
                var localMaxX = Math.Min(tileSize - 1, maxX - originX);
                var localMinY = Math.Max(0, minY - originY);
                var localMaxY = Math.Min(tileSize - 1, maxY - originY);

                for (var py = localMinY; py <= localMaxY; py++)
                {
                    var dy = originY + py + 0.5 - center.Y;
                    var rowOffset = py * tileSize;

                    for (var px = localMinX; px <= localMaxX; px++)
                    {
                        var dx = originX + px + 0.5 - center.X;
                        var distance = Math.Sqrt((dx * dx) + (dy * dy));
                        if (distance > radius) continue;

                        var falloff = distance <= inner
                            ? 1.0
                            : 1.0 - ((distance - inner) / Math.Max(1e-9, radius - inner));

                        var erase = _settings.Flow * Math.Clamp(falloff, 0, 1);
                        var offset = rowOffset + px;
                        var current = pixels[offset] / 255.0;
                        pixels[offset] = (byte)Math.Clamp(Math.Round(current * (1.0 - erase) * 255.0), 0, 255);
                    }
                }

                _mask.BumpRevision(key);
            }
        }

        DirtyWorldBounds = DirtyWorldBounds.Union(Rect2.FromCenter(center, radius * 2, radius * 2));
    }
}
