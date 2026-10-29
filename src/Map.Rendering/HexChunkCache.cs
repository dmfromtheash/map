using System.Runtime.CompilerServices;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// Kept pictures of hex chunks. A chunk is drawn once into an image at a power-of-two scale and
/// redrawn only when that chunk itself changes - or, for the ground and feature looks, one of its
/// eight neighbours or the layer's regions: transitions, coasts and frontiers read the neighbours.
/// Chunks are immutable and shared between snapshots, so the chunk object is the key, and an edit
/// invalidates exactly the chunks it copied and the pictures around them.
/// </summary>
public sealed partial class HexChunkCache : IDisposable
{
    /// <summary>Video memory the kept chunk images may hold.</summary>
    public const long BudgetBytes = 192L * 1024 * 1024;

    /// <summary>
    /// A chunk image larger than this on a side is not kept; the hexes are drawn directly. A chunk
    /// picture carries room for tall tiles above its top row, so a 64-unit hex chunk at level 0 is
    /// just over 2048 pixels high.
    /// </summary>
    public const int MaxImageSide = 2560;

    private readonly Dictionary<(HexChunk Chunk, HexOrientation Orientation, int Level, HexLook Look), Entry> _entries = new(new KeyComparer());
    private long _bytes;
    private long _clock;

    private sealed class Entry(SKImage image, long bytes, HexChunkContext? context)
    {
        public SKImage Image { get; } = image;
        public long Bytes { get; } = bytes;
        public HexChunkContext? Context { get; } = context;
        public long LastUse { get; set; }
    }

    private sealed class KeyComparer : IEqualityComparer<(HexChunk Chunk, HexOrientation Orientation, int Level, HexLook Look)>
    {
        public bool Equals((HexChunk Chunk, HexOrientation Orientation, int Level, HexLook Look) x, (HexChunk Chunk, HexOrientation Orientation, int Level, HexLook Look) y) =>
            ReferenceEquals(x.Chunk, y.Chunk) && x.Orientation == y.Orientation && x.Level == y.Level && x.Look == y.Look;

        public int GetHashCode((HexChunk Chunk, HexOrientation Orientation, int Level, HexLook Look) key) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(key.Chunk), key.Orientation, key.Level, key.Look);
    }

    private (object Key, SKImage Image)? _gridTile;

    /// <summary>Far look of many chunks in one picture: a whole board far away is a few dozen images, not thousands.</summary>
    internal sealed class FlatBlock(HexChunk?[] chunks, HexLayout layout, WorldBounds bounds, SKImage image)
    {
        public HexChunk?[] Chunks { get; } = chunks;
        public HexLayout Layout { get; } = layout;
        public WorldBounds Bounds { get; } = bounds;
        public SKImage Image { get; } = image;
        public long LastUse { get; set; }
    }

    /// <summary>Memory the far-look blocks may hold.</summary>
    public const long BlockBudgetBytes = 64L * 1024 * 1024;

    private readonly Dictionary<(LayerId Layer, HexOrientation Orientation, int X, int Y, int Span, int Level), FlatBlock> _blocks = [];
    private long _blockBytes;

    // Fog outlines of chunks nobody painted (one per layout and position) and of painted chunks.
    private readonly Dictionary<(HexLayout Layout, int Columns, int Rows, HexChunkKey Key), SKPath> _emptyFog = [];
    private readonly ConditionalWeakTable<HexChunk, Tuple<HexLayout, int, int, SKPath>> _fog = new();

    internal FlatBlock? Block((LayerId Layer, HexOrientation Orientation, int X, int Y, int Span, int Level) key)
    {
        if (!_blocks.TryGetValue(key, out var block)) return null;
        block.LastUse = ++_clock;
        return block;
    }

    internal void PutBlock((LayerId Layer, HexOrientation Orientation, int X, int Y, int Span, int Level) key, FlatBlock block)
    {
        if (_blocks.Remove(key, out var old))
        {
            _blockBytes -= (long)old.Image.Width * old.Image.Height * 4;
            old.Image.Dispose();
        }

        block.LastUse = ++_clock;
        _blocks[key] = block;
        _blockBytes += (long)block.Image.Width * block.Image.Height * 4;
        if (_blockBytes <= BlockBudgetBytes) return;
        foreach (var (oldKey, entry) in _blocks.OrderBy(pair => pair.Value.LastUse).ToList())
        {
            if (_blockBytes <= BlockBudgetBytes * 3 / 4 || _blocks.Count <= 1) break;
            _blocks.Remove(oldKey);
            _blockBytes -= (long)entry.Image.Width * entry.Image.Height * 4;
            entry.Image.Dispose();
        }
    }

    public int Count => _entries.Count;

    public long Bytes => _bytes;

    /// <summary>Chunk pictures drawn since the cache was made (kept ones are not counted again).</summary>
    public long Built { get; private set; }

    /// <summary>The one kept tile of the repeating grid pattern (a grid rarely changes style or level).</summary>
    internal SKImage GridTile(object key, Func<SKImage> draw)
    {
        if (_gridTile is { } kept && kept.Key.Equals(key)) return kept.Image;
        _gridTile?.Image.Dispose();
        var image = draw();
        _gridTile = (key, image);
        return image;
    }

    /// <summary>The kept picture drawn for exactly these neighbours and this style (any, for a null context), or null.</summary>
    internal SKImage? TryGet(HexChunk chunk, HexOrientation orientation, int level, HexLook look, HexChunkContext? context)
    {
        if (!_entries.TryGetValue((chunk, orientation, level, look), out var entry)) return null;
        if (context is not null && !context.Matches(entry.Context)) return null;
        entry.LastUse = ++_clock;
        return entry.Image;
    }

    /// <summary>
    /// A kept picture of the chunk to stand in while the right one is drawn: the same level drawn
    /// with older neighbours first, then the nearest other level of the same look.
    /// </summary>
    internal (SKImage Image, int Level)? StandIn(HexChunk chunk, HexOrientation orientation, int level, HexLook look)
    {
        if (TryGet(chunk, orientation, level, look, null) is { } stale) return (stale, level);
        for (var distance = 1; distance <= 8; distance++)
        {
            foreach (var candidate in (ReadOnlySpan<int>)[level - distance, level + distance])
            {
                if (TryGet(chunk, orientation, candidate, look, null) is { } kept) return (kept, candidate);
            }
        }

        return null;
    }

    internal void Add(HexChunk chunk, HexOrientation orientation, int level, HexLook look, SKImage image, HexChunkContext? context)
    {
        var bytes = (long)image.Width * image.Height * 4;
        if (_entries.Remove((chunk, orientation, level, look), out var old))
        {
            _bytes -= old.Bytes;
            old.Image.Dispose();
        }

        _entries[(chunk, orientation, level, look)] = new Entry(image, bytes, context) { LastUse = ++_clock };
        _bytes += bytes;
        Built++;
        if (_bytes <= BudgetBytes) return;

        // Over budget: drop the least recently used quarter at once instead of scanning per image.
        var target = BudgetBytes * 3 / 4;
        foreach (var (key, entry) in _entries.OrderBy(pair => pair.Value.LastUse).ToList())
        {
            if (_bytes <= target || _entries.Count <= 1) break;
            _entries.Remove(key);
            _bytes -= entry.Bytes;
            entry.Image.Dispose();
        }
    }

    /// <summary>Where a chunk picture goes: the layer, the chunk, the look, the level and whether it carries what stands on the ground.</summary>
    internal readonly record struct Place(LayerId Layer, HexChunkKey Key, HexLook Look, int Level, bool Merged);

    private readonly Dictionary<Place, HexChunk> _lastDrawn = [];

    /// <summary>Remembers which version of a chunk the picture last drawn at a place shows.</summary>
    internal void NoteDrawn(Place place, HexChunk chunk)
    {
        if (_lastDrawn.Count > 8192 && !_lastDrawn.ContainsKey(place)) _lastDrawn.Clear();
        _lastDrawn[place] = chunk;
    }

    /// <summary>The version of the chunk whose picture was last drawn at a place, or null.</summary>
    internal HexChunk? LastDrawn(Place place) => _lastDrawn.GetValueOrDefault(place);

    /// <summary>A chunk's CPU picture during a drag and the version of the chunk it shows.</summary>
    internal sealed record LiveChunk(HexChunk Version, SKSurface Surface, SKImage Image);

    private readonly Dictionary<Place, LiveChunk> _live = [];

    internal LiveChunk? Live(Place place) => _live.GetValueOrDefault(place);

    internal void PutLive(Place place, HexChunk version, SKSurface surface, SKImage image)
    {
        if (_live.Remove(place, out var old))
        {
            // A surface not drawn into since its last snapshot hands back that same image.
            if (!ReferenceEquals(old.Image, image)) old.Image.Dispose();
            if (!ReferenceEquals(old.Surface, surface)) old.Surface.Dispose();
        }

        _live[place] = new LiveChunk(version, surface, image);
    }

    /// <summary>
    /// The drag ended: each chunk's last live picture is kept as a stand-in (with no context, so the
    /// exact picture is still drawn) and the drawing surfaces are let go.
    /// </summary>
    internal void EndLive(HexOrientation orientation)
    {
        if (_live.Count == 0) return;
        foreach (var (place, entry) in _live)
        {
            entry.Surface.Dispose();
            if (TryGet(entry.Version, orientation, place.Level, place.Look, null) is null)
            {
                Add(entry.Version, orientation, place.Level, place.Look, entry.Image, null);
            }
            else
            {
                entry.Image.Dispose();
            }
        }

        _live.Clear();
    }

    /// <summary>
    /// The transitions of one hex as drawn while a drag repaints its chunk on every move: the hex,
    /// its own terrain and its six neighbours' terrains, the picture level and whether the edge is soft.
    /// </summary>
    internal readonly record struct TransitionKey(HexLayout Layout, HexCoord Hex, int Level, bool Soft,
        string Self, string? N0, string? N1, string? N2, string? N3, string? N4, string? N5);

    /// <summary>Memory the kept transition pictures may hold.</summary>
    public const long TransitionBudgetBytes = 48L * 1024 * 1024;

    private readonly Dictionary<TransitionKey, (SKImage? Image, long LastUse)> _transitions = [];
    private long _transitionBytes;

    /// <summary>
    /// The kept transition picture of a hex (null when the hex has none), drawn once by
    /// <paramref name="draw"/>: a drag then rasterises only the hexes whose neighbourhood changed.
    /// </summary>
    internal SKImage? Transition(TransitionKey key, Func<SKImage?> draw)
    {
        if (_transitions.TryGetValue(key, out var kept))
        {
            _transitions[key] = (kept.Image, ++_clock);
            return kept.Image;
        }

        var image = draw();
        _transitions[key] = (image, ++_clock);
        if (image is not null) _transitionBytes += (long)image.Width * image.Height * 4;
        if (_transitionBytes <= TransitionBudgetBytes && _transitions.Count <= 65536) return image;

        foreach (var (oldKey, entry) in _transitions.OrderBy(pair => pair.Value.LastUse).ToList())
        {
            if ((_transitionBytes <= TransitionBudgetBytes * 3 / 4 && _transitions.Count <= 49152) || _transitions.Count <= 1) break;
            if (oldKey == key) continue;
            _transitions.Remove(oldKey);
            if (entry.Image is null) continue;
            _transitionBytes -= (long)entry.Image.Width * entry.Image.Height * 4;
            entry.Image.Dispose();
        }

        return image;
    }

    /// <summary>The outline of every hex of a chunk that the players have not seen.</summary>
    internal SKPath FogPath(HexBoard board, HexChunkKey key, HexChunk? chunk)
    {
        if (chunk is null)
        {
            var emptyKey = (board.Layout, board.Columns, board.Rows, key);
            if (_emptyFog.TryGetValue(emptyKey, out var empty)) return empty;
            if (_emptyFog.Count > 4096)
            {
                foreach (var path in _emptyFog.Values) path.Dispose();
                _emptyFog.Clear();
            }

            return _emptyFog[emptyKey] = HexLayerRenderer.BuildFogPath(board, key, null);
        }

        if (_fog.TryGetValue(chunk, out var kept) && kept.Item1 == board.Layout && kept.Item2 == board.Columns && kept.Item3 == board.Rows)
        {
            return kept.Item4;
        }

        var built = HexLayerRenderer.BuildFogPath(board, key, chunk);
        _fog.AddOrUpdate(chunk, Tuple.Create(board.Layout, board.Columns, board.Rows, built));
        return built;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _queued.Clear();
        }

        foreach (var entry in _entries.Values) entry.Image.Dispose();
        _entries.Clear();
        _bytes = 0;
        _gridTile?.Image.Dispose();
        _gridTile = null;
        foreach (var block in _blocks.Values) block.Image.Dispose();
        _blocks.Clear();
        _blockBytes = 0;
        foreach (var path in _emptyFog.Values) path.Dispose();
        _emptyFog.Clear();
        _fog.Clear();
        foreach (var (image, _) in _transitions.Values) image?.Dispose();
        _transitions.Clear();
        _lastDrawn.Clear();
        foreach (var entry in _live.Values)
        {
            entry.Image.Dispose();
            entry.Surface.Dispose();
        }

        _live.Clear();
        _transitionBytes = 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            _queued.Clear();
            foreach (var done in _done) done.Image?.Dispose();
            _done.Clear();
        }

        _signal.Release();
        Clear();
    }

    // ------------------------------------------------------------------ pictures drawn in the background

    /// <summary>
    /// New chunk pictures are drawn on a worker thread (CPU raster) while the frame shows a stand-in;
    /// they join the cache at the start of a later frame. A picture with transitions and living
    /// terrain can take longer than a whole frame, which made zooming stutter when drawn in-frame.
    /// </summary>
    public bool BuildInBackground { get; init; }

    private sealed class BuildRequest(HexChunk chunk, HexOrientation orientation, int level, HexLook look,
        HexChunkContext? context, Func<SKImage?> draw, long generation)
    {
        public HexChunk Chunk { get; } = chunk;
        public HexOrientation Orientation { get; } = orientation;
        public int Level { get; } = level;
        public HexLook Look { get; } = look;
        public HexChunkContext? Context { get; } = context;
        public Func<SKImage?> Draw { get; } = draw;
        public long Generation { get; } = generation;
        public long Stamp { get; set; }
        public SKImage? Image { get; set; }
    }

    private const int MaxQueued = 384;
    private readonly object _gate = new();
    private readonly Dictionary<(HexChunk Chunk, HexOrientation Orientation, int Level, HexLook Look), BuildRequest> _queued = new(new KeyComparer());
    private readonly List<BuildRequest> _done = [];
    private readonly SemaphoreSlim _signal = new(0);
    private Task? _worker;
    private long _stamp;
    private long _generation;
    private bool _closed;

    /// <summary>Pictures asked for and not drawn yet.</summary>
    public int Queued
    {
        get { lock (_gate) return _queued.Count + _done.Count; }
    }

    /// <summary>Asks for a picture to be drawn in the background (again with the newest context if already asked).</summary>
    internal void Enqueue(HexChunk chunk, HexOrientation orientation, int level, HexLook look, HexChunkContext? context, Func<SKImage?> draw)
    {
        lock (_gate)
        {
            if (_closed) return;
            var key = (chunk, orientation, level, look);
            var stamp = ++_stamp;
            if (_queued.TryGetValue(key, out var asked) && (context is null ? asked.Context is null : context.Matches(asked.Context)))
            {
                asked.Stamp = stamp;
                return;
            }

            _queued[key] = new BuildRequest(chunk, orientation, level, look, context, draw, _generation) { Stamp = stamp };
            if (_queued.Count > MaxQueued)
            {
                // The oldest asks are for places the view has left long ago.
                foreach (var old in _queued.OrderBy(pair => pair.Value.Stamp).Take(_queued.Count - MaxQueued).ToList())
                {
                    _queued.Remove(old.Key);
                }
            }

            _worker ??= Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        _signal.Release();
    }

    /// <summary>True when this picture is already asked for.</summary>
    internal bool IsQueued(HexChunk chunk, HexOrientation orientation, int level, HexLook look)
    {
        lock (_gate) return _queued.ContainsKey((chunk, orientation, level, look));
    }

    /// <summary>
    /// New pictures one frame takes from the worker (at least one): each is uploaded to the GPU when
    /// first drawn, and a zoom whose pictures all arrived at once spent its frames uploading.
    /// </summary>
    public const long CollectBytesPerFrame = 6L * 1024 * 1024;

    /// <summary>
    /// Moves pictures the worker finished into the cache, up to <see cref="CollectBytesPerFrame"/>;
    /// called on the render thread. True when more are waiting for the next frame.
    /// </summary>
    internal bool Collect()
    {
        var finished = new List<BuildRequest>();
        bool more;
        lock (_gate)
        {
            if (_done.Count == 0) return false;
            long bytes = 0;
            var taken = 0;
            // The newest first: they are what the view shows now.
            for (var i = _done.Count - 1; i >= 0; i--)
            {
                var request = _done[i];
                var size = request.Image is null ? 0 : (long)request.Image.Width * request.Image.Height * 4;
                if (taken > 0 && bytes + size > CollectBytesPerFrame) continue;
                bytes += size;
                taken++;
                finished.Add(request);
                _done.RemoveAt(i);
            }

            more = _done.Count > 0;
        }

        foreach (var request in finished)
        {
            if (request.Image is null) continue;
            if (request.Generation != _generation)
            {
                request.Image.Dispose();
                continue;
            }

            Add(request.Chunk, request.Orientation, request.Level, request.Look, request.Image, request.Context);
        }

        return more;
    }

    private void Work()
    {
        while (true)
        {
            _signal.Wait();
            BuildRequest? next;
            lock (_gate)
            {
                if (_closed) return;
                next = null;
                foreach (var request in _queued.Values)
                {
                    if (next is null || request.Stamp > next.Stamp) next = request;
                }

                if (next is null) continue;
                _queued.Remove((next.Chunk, next.Orientation, next.Level, next.Look));
            }

            try
            {
                next.Image = next.Draw();
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or OutOfMemoryException)
            {
                next.Image = null;
            }

            lock (_gate)
            {
                if (_closed)
                {
                    next.Image?.Dispose();
                    return;
                }

                _done.Add(next);
                // More asks are waiting: keep going without a new signal.
                if (_queued.Count > 0 && _signal.CurrentCount == 0) _signal.Release();
            }
        }
    }
}

/// <summary>
/// What a kept chunk picture depended on besides the chunk: its eight neighbour chunks (by
/// reference) and the style it was drawn with. A picture is current while both are the same.
/// </summary>
internal sealed class HexChunkContext(HexChunk?[] neighbours, object? style)
{
    private readonly HexChunk?[] _neighbours = neighbours;
    private readonly object? _style = style;

    public bool Matches(HexChunkContext? other)
    {
        if (other is null || !Equals(_style, other._style)) return false;
        for (var i = 0; i < _neighbours.Length; i++)
        {
            if (!ReferenceEquals(_neighbours[i], other._neighbours[i])) return false;
        }

        return true;
    }

    public static HexChunkContext For(HexBoard board, HexChunkKey key, object? style)
    {
        var neighbours = new HexChunk?[8];
        var i = 0;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var x = key.X + dx;
                var y = key.Y + dy;
                neighbours[i++] = x < 0 || y < 0 ? null : board.Chunk(new HexChunkKey(x, y));
            }
        }

        return new HexChunkContext(neighbours, style);
    }
}
