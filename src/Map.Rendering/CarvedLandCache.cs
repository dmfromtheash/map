using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// Keeps the land coverage that rivers have already been carved out of, so a frame that changed
/// nothing about the land or the rivers does not rasterize the same offscreen image again.
/// <para>
/// The key is the identity of everything the image is built from: the land layer, its coverage
/// tiles, the exact set of carving rivers (all immutable, so reference identity is content
/// identity), the view rectangle and the device scale. Any edit produces new instances and the
/// entry is simply not found - the cache can never show a stale coast. It holds recomputable
/// pixels only and drops them all when the working-memory budget asks for relief.
/// </para>
/// </summary>
public sealed class CarvedLandCache : IDisposable
{
    private sealed record Key(LayerId Layer, object Tiles, long Left, long Top, long Right, long Bottom, long Scale, River[] Cutters)
    {
        public bool Equals(Key? other) =>
            other is not null && Layer == other.Layer && ReferenceEquals(Tiles, other.Tiles)
            && Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom
            && Scale == other.Scale && Cutters.Length == other.Cutters.Length
            && !Cutters.Where((river, i) => !ReferenceEquals(river, other.Cutters[i])).Any();

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Layer); hash.Add(Left); hash.Add(Top); hash.Add(Right); hash.Add(Bottom); hash.Add(Scale);
            hash.Add(Cutters.Length);
            foreach (var river in Cutters) hash.Add(river.Id);
            return hash.ToHashCode();
        }
    }

    private sealed record Entry(SKImage Image, int Width, int Height);

    private readonly object _gate = new();
    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly List<Key> _order = [];
    private readonly int _capacity;
    private readonly Action _release;

    public CarvedLandCache(int capacity = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _release = Clear;
        Map.Core.Resources.WorkingMemoryBudget.ReleaseDerivedCaches += _release;
    }

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>
    /// The carved coverage for this land layer, view and river set, built by <paramref name="build"/>
    /// only when nothing usable is held. The returned image belongs to the cache; the caller draws
    /// it and must not dispose it.
    /// </summary>
    public SKImage? Get(LayerId layer, TileMap tiles, WorldBounds view, double scale, IReadOnlyList<River> cutters,
        Func<int, int, SKImage?> build, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(cutters);
        var key = new Key(layer, tiles, Quantize(view.Left), Quantize(view.Top), Quantize(view.Right),
            Quantize(view.Bottom), Quantize(scale), [.. cutters]);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var found))
            {
                Hits++;
                _order.Remove(key); _order.Add(key);
                width = found.Width; height = found.Height;
                return found.Image;
            }
        }

        var pixelWidth = (int)Math.Clamp(Math.Ceiling((view.Right - view.Left) * scale) + 2, 1, 16384);
        var pixelHeight = (int)Math.Clamp(Math.Ceiling((view.Bottom - view.Top) * scale) + 2, 1, 16384);
        var image = build(pixelWidth, pixelHeight);
        width = pixelWidth; height = pixelHeight;
        if (image is null) return null;
        lock (_gate)
        {
            Misses++;
            if (_entries.TryAdd(key, new Entry(image, pixelWidth, pixelHeight)))
            {
                _order.Add(key);
                while (_order.Count > _capacity)
                {
                    var oldest = _order[0]; _order.RemoveAt(0);
                    if (_entries.Remove(oldest, out var evicted)) evicted.Image.Dispose();
                }
            }
            else image.Dispose();
            return _entries[key].Image;
        }
    }

    private static long Quantize(double value) => (long)Math.Round(value * 64);

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values) entry.Image.Dispose();
            _entries.Clear(); _order.Clear();
        }
    }

    public void Dispose()
    {
        Map.Core.Resources.WorkingMemoryBudget.ReleaseDerivedCaches -= _release;
        Clear();
    }
}
