using Map.Spike.Core.Documents;
using Map.Spike.Core.Geometry;

namespace Map.Spike.Core.Commands;

/// <summary>
/// Live brush gesture. It mutates the raster layer immediately so the canvas can show the
/// stroke as it is drawn, while recording enough state to collapse the whole gesture into a
/// single undoable command at <see cref="Complete"/>.
/// </summary>
public sealed class BrushStrokeSession(RasterLayer layer, BrushSettings settings)
{
    private readonly RasterLayer _layer = layer;
    private readonly BrushSettings _settings = settings;
    private readonly TileStrokeRecorder _recorder = new();
    private readonly HashSet<TileKey> _touched = [];

    private Vec2? _last;
    private bool _completed;

    public IReadOnlyCollection<TileKey> TouchedTiles => _touched;

    /// <summary>World bounds actually painted so far, for targeted invalidation.</summary>
    public Rect2 DirtyWorldBounds { get; private set; } = Rect2.Empty;

    public void AddPoint(Vec2 world)
    {
        if (_completed) throw new InvalidOperationException("Stroke already completed.");

        if (_last is null)
        {
            Stamp(world);
        }
        else
        {
            var from = _last.Value;
            var delta = world - from;
            var distance = delta.Length;
            var steps = (int)Math.Max(1, Math.Ceiling(distance / _settings.Spacing));
            for (var i = 1; i <= steps; i++)
            {
                var t = (double)i / steps;
                Stamp(new Vec2(from.X + (delta.X * t), from.Y + (delta.Y * t)));
            }
        }

        _last = world;
    }

    /// <summary>Returns null when the gesture painted nothing, so no empty history entry appears.</summary>
    public BrushStrokeCommand? Complete(string name = "Brush stroke")
    {
        if (_completed) throw new InvalidOperationException("Stroke already completed.");
        _completed = true;

        if (_touched.Count == 0) return null;

        var after = new Dictionary<TileKey, byte[]?>(_touched.Count);
        foreach (var key in _touched)
        {
            var current = _layer.TryGetTile(key);
            after[key] = current is null ? null : (byte[])current.Clone();
        }

        return new BrushStrokeCommand(_layer.Id, _recorder.Before, after, name);
    }

    private void Stamp(Vec2 center)
    {
        var radius = _settings.Radius;
        var minX = (int)Math.Floor(center.X - radius);
        var maxX = (int)Math.Ceiling(center.X + radius);
        var minY = (int)Math.Floor(center.Y - radius);
        var maxY = (int)Math.Ceiling(center.Y + radius);

        var tileSize = _layer.TileSize;
        var tileMinX = (int)Math.Floor(minX / (double)tileSize);
        var tileMaxX = (int)Math.Floor(maxX / (double)tileSize);
        var tileMinY = (int)Math.Floor(minY / (double)tileSize);
        var tileMaxY = (int)Math.Floor(maxY / (double)tileSize);

        var srcA = (byte)((_settings.ColorArgb >> 24) & 0xFF);
        var srcR = (byte)((_settings.ColorArgb >> 16) & 0xFF);
        var srcG = (byte)((_settings.ColorArgb >> 8) & 0xFF);
        var srcB = (byte)(_settings.ColorArgb & 0xFF);

        var inner = radius * Math.Clamp(_settings.Hardness, 0, 1);

        for (var ty = tileMinY; ty <= tileMaxY; ty++)
        {
            for (var tx = tileMinX; tx <= tileMaxX; tx++)
            {
                var key = new TileKey(tx, ty);
                if (_touched.Add(key)) _recorder.NoteBeforeFirstWrite(key, _layer.TryGetTile(key));

                var pixels = _layer.GetOrCreateTile(key);
                var originX = tx * tileSize;
                var originY = ty * tileSize;

                var localMinX = Math.Max(0, minX - originX);
                var localMaxX = Math.Min(tileSize - 1, maxX - originX);
                var localMinY = Math.Max(0, minY - originY);
                var localMaxY = Math.Min(tileSize - 1, maxY - originY);

                for (var py = localMinY; py <= localMaxY; py++)
                {
                    var worldY = originY + py + 0.5;
                    var dy = worldY - center.Y;
                    var rowOffset = py * tileSize * RasterLayer.BytesPerPixel;

                    for (var px = localMinX; px <= localMaxX; px++)
                    {
                        var worldX = originX + px + 0.5;
                        var dx = worldX - center.X;
                        var distance = Math.Sqrt((dx * dx) + (dy * dy));
                        if (distance > radius) continue;

                        var falloff = distance <= inner
                            ? 1.0
                            : 1.0 - ((distance - inner) / Math.Max(1e-9, radius - inner));

                        var coverage = _settings.Flow * (srcA / 255.0) * Math.Clamp(falloff, 0, 1);
                        if (coverage <= 0) continue;

                        var offset = rowOffset + (px * RasterLayer.BytesPerPixel);
                        BlendPremultipliedOver(pixels, offset, srcR, srcG, srcB, coverage);
                    }
                }

                _layer.BumpRevision(key);
            }
        }

        DirtyWorldBounds = DirtyWorldBounds.Union(
            Rect2.FromCenter(center, radius * 2, radius * 2));
    }

    /// <summary>dst = src + dst*(1-srcAlpha), with src already scaled by coverage (premultiplied).</summary>
    private static void BlendPremultipliedOver(byte[] dst, int offset, byte r, byte g, byte b, double alpha)
    {
        var inv = 1.0 - alpha;
        dst[offset + 0] = (byte)Math.Clamp((r * alpha) + (dst[offset + 0] * inv), 0, 255);
        dst[offset + 1] = (byte)Math.Clamp((g * alpha) + (dst[offset + 1] * inv), 0, 255);
        dst[offset + 2] = (byte)Math.Clamp((b * alpha) + (dst[offset + 2] * inv), 0, 255);
        dst[offset + 3] = (byte)Math.Clamp((255 * alpha) + (dst[offset + 3] * inv), 0, 255);
    }
}
