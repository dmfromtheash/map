using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// Keeps a finished raster of one part of the scene for the current view, so a frame that
/// changed nothing about that part draws one image instead of every pass again. Two instances
/// serve the editor: the finished look of one piece of land (coast shadow, surf rings, outline,
/// fill, ground material, painting, inner shading) and the whole terrain of a level run (land and
/// water together). While the user holds a stroke, carries a placement ghost or drags a selection
/// the view does not move and the terrain does not change, so those frames look exactly like the
/// idle frame - which is what used to fail when the material and the coast were skipped to stay
/// responsive.
/// <para>
/// The key is the identity of everything the image is built from: the immutable records and
/// tile maps of the layers, rivers and roads involved (an edit produces new instances, so the
/// entry is simply not found), the view rectangle and the device scale. The image is built on
/// the frame's GPU context when there is one, so it lives in video memory; a context that
/// changes drops every entry, since its images belong to it. Recomputable pixels only: the
/// working-memory budget can drop them all at any time.
/// </para>
/// </summary>
public sealed class RenderImageCache : IDisposable
{
    private sealed record Key(long Left, long Top, long Right, long Bottom, long Scale, object[] Parts, IntPtr Context)
    {
        public bool Equals(Key? other) =>
            ReferenceEquals(this, other) || other is not null
            && Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom
            && Scale == other.Scale && Context == other.Context && Parts.Length == other.Parts.Length
            && !Parts.Where((part, i) => !PartEquals(part, other.Parts[i])).Any();

        /// <summary>
        /// The key an entry is stored under: tile maps - alone or inside a land layer - are held
        /// weakly. A tile map is compared by reference and every edit or undo makes a new one, so
        /// once nothing else holds it no future frame can ask for that entry again. A strong key
        /// kept the tiles of states already dropped from the history alive (49 MiB after a
        /// discarded redo branch on a 4096×3072 map). Everything else compares as before.
        /// </summary>
        public Key Stored() => this with { Parts = [.. Parts.Select(part => part switch
        {
            TileMap tiles => new WeakTiles(tiles),
            LandLayer land => new WeakLand(land),
            _ => part,
        })] };

        public bool IsDead => Parts.Any(part => part is WeakTiles { Alive: false } or WeakLand { Alive: false });

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Left); hash.Add(Top); hash.Add(Right); hash.Add(Bottom); hash.Add(Scale);
            hash.Add(Parts.Length); hash.Add(Context);
            return hash.ToHashCode();
        }
    }

    private static readonly object DeadPart = new();
    private static readonly TileMap NoCoverage = TileMap.Empty(1);
    private static readonly TileMap NoPaint = TileMap.Empty(4);

    private sealed class WeakTiles(TileMap tiles)
    {
        private readonly WeakReference<TileMap> _tiles = new(tiles);
        public bool Alive => _tiles.TryGetTarget(out _);
        public object Resolve() => _tiles.TryGetTarget(out var tiles) ? tiles : DeadPart;
    }

    /// <summary>A land layer with its light fields kept and its coverage, paint and mask tiles held weakly.</summary>
    private sealed class WeakLand
    {
        private readonly LandLayer _shape;
        private readonly WeakReference<TileMap> _geometry, _paint;
        private readonly WeakReference<TileMap>? _mask;
        private readonly bool _maskEnabled;

        public WeakLand(LandLayer land)
        {
            _shape = land with { GeometryTiles = NoCoverage, PaintTiles = NoPaint, Mask = null };
            _geometry = new(land.GeometryTiles); _paint = new(land.PaintTiles);
            if (land.Mask is { } mask) { _mask = new(mask.Tiles); _maskEnabled = mask.Enabled; }
        }

        public bool Alive => _geometry.TryGetTarget(out _) && _paint.TryGetTarget(out _) && (_mask is null || _mask.TryGetTarget(out _));

        public object Resolve()
        {
            if (!_geometry.TryGetTarget(out var geometry) || !_paint.TryGetTarget(out var paint)) return DeadPart;
            TileMap? maskTiles = null;
            if (_mask is not null && !_mask.TryGetTarget(out maskTiles)) return DeadPart;
            return _shape with
            {
                GeometryTiles = geometry, PaintTiles = paint,
                Mask = maskTiles is null ? null : new LayerMask(maskTiles, _maskEnabled),
            };
        }
    }

    private static object Resolve(object part) => part switch
    {
        WeakTiles tiles => tiles.Resolve(),
        WeakLand land => land.Resolve(),
        _ => part,
    };

    /// <summary>Part equality through weakly held parts; a collected part equals nothing.</summary>
    private static bool PartEquals(object a, object b)
    {
        var left = Resolve(a); var right = Resolve(b);
        return !ReferenceEquals(left, DeadPart) && !ReferenceEquals(right, DeadPart) && Equals(left, right);
    }

    /// <summary>Entries whose weakly held inputs are gone can never be asked for again; their images go now.</summary>
    private void PurgeDead()
    {
        for (var i = _order.Count - 1; i >= 0; i--)
        {
            var key = _order[i];
            if (!key.IsDead) continue;
            _order.RemoveAt(i);
            if (_entries.Remove(key, out var dead)) dead.Image.Dispose();
        }
    }

    /// <summary>Moves the stored (weakly holding) key equal to <paramref name="key"/> to the newest place.</summary>
    private void Touch(Key key)
    {
        var index = _order.FindIndex(stored => stored.Equals(key));
        if (index < 0) return;
        var stored = _order[index];
        _order.RemoveAt(index); _order.Add(stored);
    }

    private sealed record Entry(SKImage Image, WorldBounds View, double Scale);

    private readonly object _gate = new();
    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly List<Key> _order = [];
    private readonly int _capacity;
    private readonly Action _release;
    private IntPtr _context;

    public RenderImageCache(int capacity = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _release = Clear;
        Map.Core.Resources.WorkingMemoryBudget.ReleaseDerivedCaches += _release;
    }

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>Diagnostics: which part of the key differed from the newest entry on the last miss.</summary>
    public string LastMiss { get; private set; } = "";

    /// <summary>Looks up an exact finished view without doing work on a miss.</summary>
    public SKImage? TryGetExact(IReadOnlyList<object> parts, WorldBounds view, double scale, GRContext? context)
    {
        var handle = context?.Handle ?? IntPtr.Zero;
        var key = new Key(Quantize(view.Left), Quantize(view.Top), Quantize(view.Right), Quantize(view.Bottom),
            BitConverter.DoubleToInt64Bits(scale), [.. parts], handle);
        lock (_gate)
        {
            PurgeDead();
            if (_context != handle || !_entries.TryGetValue(key, out var found)) return null;
            Hits++; Touch(key);
            return found.Image;
        }
    }

    private string Describe(Key key)
    {
        if (_order.Count == 0) return "empty";
        var previous = _order[^1];
        var parts = new List<string>();
        if (previous.Left != key.Left || previous.Top != key.Top || previous.Right != key.Right || previous.Bottom != key.Bottom)
            parts.Add($"view {previous.Left / 64.0:0.##},{previous.Top / 64.0:0.##},{previous.Right / 64.0:0.##},{previous.Bottom / 64.0:0.##} -> {key.Left / 64.0:0.##},{key.Top / 64.0:0.##},{key.Right / 64.0:0.##},{key.Bottom / 64.0:0.##}");
        if (previous.Scale != key.Scale) parts.Add("scale");
        if (previous.Context != key.Context) parts.Add("context");
        if (previous.Parts.Length != key.Parts.Length) parts.Add("parts-count");
        else for (var i = 0; i < key.Parts.Length; i++)
            if (!PartEquals(previous.Parts[i], key.Parts[i])) parts.Add("part" + i + ":" + key.Parts[i].GetType().Name);
        return parts.Count == 0 ? "identical (evicted?)" : string.Join(",", parts);
    }

    /// <summary>
    /// The image for this view, scale and set of inputs, built by <paramref name="build"/> only
    /// when nothing usable is held. Parts are compared by equality: immutable records compare by
    /// content, tile maps and other classes by reference. The image belongs to the cache; the
    /// caller draws it and must not dispose it. Null when the build produced nothing.
    /// </summary>
    public SKImage? Get(IReadOnlyList<object> parts, WorldBounds view, double scale, GRContext? context,
        Func<int, int, SKImage?> build)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(build);
        var handle = context?.Handle ?? IntPtr.Zero;
        var key = new Key(Quantize(view.Left), Quantize(view.Top), Quantize(view.Right), Quantize(view.Bottom),
            BitConverter.DoubleToInt64Bits(scale), [.. parts], handle);
        lock (_gate)
        {
            if (_context != handle)
            {
                // Images of a previous GPU context cannot be drawn on this one.
                foreach (var stale in _entries.Values) stale.Image.Dispose();
                _entries.Clear(); _order.Clear();
                _context = handle;
            }
            PurgeDead();
            if (_entries.TryGetValue(key, out var found))
            {
                Hits++;
                Touch(key);
                return found.Image;
            }
            LastMiss = Describe(key);
        }

        var (width, height) = SizeOf(view, scale);
        var image = build(width, height);
        if (image is null) return null;
        lock (_gate)
        {
            Misses++;
            if (_context != handle) { image.Dispose(); return null; }
            var stored = key.Stored();
            if (_entries.TryAdd(stored, new Entry(image, view, scale)))
            {
                _order.Add(stored);
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

    /// <summary>
    /// The image built from exactly these inputs on this context that serves the wanted view
    /// best, whatever view and scale it was built for: while the camera moves, the frame draws
    /// it resampled into the new view instead of rebuilding the terrain for every notch of the
    /// wheel. An image covering the whole wanted view wins; among those the one whose scale is
    /// nearest the wanted scale, preferring finer; otherwise the largest covered share, newest
    /// first. Null when nothing built from these inputs is held. The image belongs to the cache.
    /// </summary>
    public SKImage? TryGetBest(IReadOnlyList<object> parts, WorldBounds wanted, double wantedScale, GRContext? context,
        out WorldBounds view, out double scale)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var handle = context?.Handle ?? IntPtr.Zero;
        lock (_gate)
        {
            view = default; scale = 0;
            if (_context != handle) return null;
            PurgeDead();
            Entry? best = null; var bestRank = double.NegativeInfinity; var bestCovers = false;
            var wantedArea = Math.Max(1e-9, (wanted.Right - wanted.Left) * (wanted.Bottom - wanted.Top));
            for (var i = _order.Count - 1; i >= 0; i--)
            {
                var key = _order[i];
                if (key.Parts.Length != parts.Count) continue;
                var same = true;
                for (var p = 0; p < parts.Count && same; p++) same = PartEquals(key.Parts[p], parts[p]);
                if (!same) continue;
                var entry = _entries[key];
                var placement = Placement(entry.Image, entry.View, entry.Scale);
                var left = Math.Max(placement.Left, wanted.Left); var top = Math.Max(placement.Top, wanted.Top);
                var right = Math.Min(placement.Right, wanted.Right); var bottom = Math.Min(placement.Bottom, wanted.Bottom);
                var share = right > left && bottom > top ? (right - left) * (bottom - top) / wantedArea : 0;
                var covers = share >= 1 - 1e-6;
                // Full coverage ranks above any partial one; within it, the scale nearest the
                // wanted one, a finer image before a coarser one at the same distance.
                var ratio = Math.Log2(Math.Max(1e-9, entry.Scale / Math.Max(1e-9, wantedScale)));
                var rank = covers ? -Math.Abs(ratio) + (ratio >= 0 ? .01 : 0) : share;
                if (best is null || (covers && !bestCovers) || covers == bestCovers && rank > bestRank)
                { bestRank = rank; best = entry; bestCovers = covers; }
            }
            if (best is null) return null;
            view = best.View; scale = best.Scale;
            return best.Image;
        }
    }

    /// <summary>Pixel size the cache expects an image for this view and scale to have.</summary>
    public static (int Width, int Height) SizeOf(WorldBounds view, double scale) => (
        (int)Math.Clamp(Math.Ceiling((view.Right - view.Left) * scale) + 2, 1, 16384),
        (int)Math.Clamp(Math.Ceiling((view.Bottom - view.Top) * scale) + 2, 1, 16384));

    /// <summary>
    /// An offscreen surface for one image of the cache: on the GPU context when there is one,
    /// in memory otherwise, with the world transform of the view applied.
    /// </summary>
    public static SKSurface? CreateSurface(GRContext? context, int width, int height, WorldBounds view, double scale)
    {
        // Half-float pixels: a translucent bank or halo stored premultiplied in 8 bits and composed
        // later would drift by several levels from the frame drawn in one go; in 16 bits it does not.
        var info = new SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Premul);
        var surface = (context is not null ? SKSurface.Create(context, false, info) : null) ?? SKSurface.Create(info);
        if (surface is null)
        {
            info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            surface = (context is not null ? SKSurface.Create(context, false, info) : null) ?? SKSurface.Create(info);
            if (surface is null) return null;
        }
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale((float)scale);
        surface.Canvas.Translate((float)-view.Left, (float)-view.Top);
        return surface;
    }

    /// <summary>The world rectangle an image built for this view covers, for drawing it back.</summary>
    public static SKRect Placement(SKImage image, WorldBounds view, double scale) => new(
        (float)view.Left, (float)view.Top,
        (float)(view.Left + image.Width / scale), (float)(view.Top + image.Height / scale));

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
