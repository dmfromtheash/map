using System.Globalization;
using System.Runtime.CompilerServices;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>The hexes the hex tool would touch, outlined under the pointer.</summary>
/// <param name="LayerId">The hex layer the tool works on.</param>
/// <param name="Hexes">The footprint.</param>
/// <param name="Erase">Outlined as an eraser instead of a placement.</param>
/// <param name="Glows">
/// When set, what is drawn instead of <paramref name="Hexes"/>: the footprints round the pointer,
/// each at its own strength, so the outline flows from hex to hex instead of jumping.
/// </param>
/// <param name="Ghost">The tile a click would put down, drawn faint under the outline.</param>
public sealed record HexCursorPreview(LayerId LayerId, IReadOnlyList<HexCoord> Hexes, bool Erase = false,
    IReadOnlyList<HexCursorGlow>? Glows = null, HexGhostTile? Ghost = null);

/// <summary>One footprint of the hover outline and how strongly it shows (0..1).</summary>
public sealed record HexCursorGlow(IReadOnlyList<HexCoord> Hexes, double Strength);

/// <summary>Which picture of a chunk is kept: the far look, the ground with its transitions, or what stands on it.</summary>
public enum HexLook
{
    /// <summary>Flat average colours, far away.</summary>
    Flat,

    /// <summary>Terrain tiles, transitions, coasts and region tints.</summary>
    Terrain,

    /// <summary>Features, living-terrain icons and region frontiers, drawn over the paths.</summary>
    Features,

    /// <summary>
    /// Not drawn itself: where the trees of the living terrain are and how high each pixel sits in
    /// its tree, kept at half the picture's scale; <see cref="HexTreeSway"/> sways the trees by it.
    /// </summary>
    Sway,
}

/// <summary>
/// What the hex workshop shows over a hex layer while a tool works: the selection, a paste that
/// follows the pointer, a path being drawn, a measured route, the hex whose key is open.
/// </summary>
public sealed record HexOverlay(LayerId LayerId)
{
    public IReadOnlyCollection<HexCoord> Selection { get; init; } = [];

    /// <summary>Cells a paste would write, at their board positions (null cells are holes).</summary>
    public IReadOnlyDictionary<HexCoord, HexCell?>? Ghost { get; init; }

    public HexPath? PathPreview { get; init; }

    public IReadOnlyList<HexCoord>? Route { get; init; }

    public string? RouteLabel { get; init; }
    public IReadOnlyList<JourneyRoute> Journeys { get; init; } = [];
    public int SelectedJourney { get; init; }
    public TravelMode TravelMode { get; init; }
    public TravelReach? Reach { get; init; }

    public HexCoord? Focus { get; init; }
}

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

/// <summary>
/// Draws a hex layer. Order: the board's backdrop, the ground of every hex (terrain tiles with
/// their transitions, coasts and region tints), rivers, roads and borders, then everything that
/// stands on the ground (features, living-terrain icons, region frontiers), the grid, the hex key,
/// the fog of war and the tool overlays. Rows are drawn top to bottom so a tall tile or a feature
/// never hides under the row in front of it. Far away a hex is its tile's average colour. With a
/// <see cref="HexChunkCache"/> both chunk looks are kept, and a frame draws only as many new chunk
/// images as its budget allows: the others stand in and follow in the next frames.
/// </summary>
public static partial class HexLayerRenderer
{
    /// <summary>How strongly the living world shows in the frame being drawn (it fades in and out).</summary>
    [ThreadStatic]
    private static double _swayStrength;

    /// <summary>From this many screen pixels per hex the trees of the living terrain sway (with the living world on).</summary>
    public const double SwayFromPixels = 34;

    /// <summary>Below this many screen pixels per hex the tile is drawn as its average colour.</summary>
    public const double FlatColourBelowPixels = 10;

    /// <summary>Below this many screen pixels per hex the grid lines are left out.</summary>
    public const double GridBelowPixels = 7;

    /// <summary>From this many screen pixels per hex the grid shows fully; between it fades in with the zoom.</summary>
    public const double GridFullPixels = 10;

    /// <summary>An export draws flat colours below this many pixels per hex.</summary>
    public const double ExportFlatBelowPixels = 4;

    /// <summary>An export leaves the grid out below this many pixels per hex.</summary>
    public const double ExportGridBelowPixels = 3;

    /// <summary>Below this many screen pixels per hex the numbers are left out.</summary>
    public const double NumbersBelowPixels = 44;

    /// <summary>The repeating grid picture is used while its tile (two hexes across) stays this small.</summary>
    private const int MaxGridTilePixels = 1024;

    /// <summary>Time one frame may spend drawing chunk images that are not kept yet.</summary>
    public const double BuildBudgetMilliseconds = 8;

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    // Kept chunk images are made at the level at or just above the view scale, so they never shrink
    // by more than half: no mipmaps, which a texture made from a GPU surface does not have (asking
    // for them would copy the texture on every draw).
    private static readonly SKSamplingOptions ChunkSampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private static readonly ConditionalWeakTable<HexChunk, StrongBox<SKColor>> ChunkAverages = new();

    /// <summary>Draws a hex layer with the editor's or the export's options.</summary>
    public static void Draw(SKCanvas canvas, HexLayer layer, WorldBounds view, RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var player = options.HexPlayerView || options.HexPresentation == HexPresentation.PlayerKnowledge;
        if (player) layer = PlayerLayers.GetValue(layer, ExportPlayerVisibility.Sanitize);
        Draw(canvas, layer, view, options.ViewScale, options.HexChunks, options.GpuContext,
            player ? null : options.HexCursor, options.IsExport, options.RequestRefinedFrame,
            options.Stats, options.HexGestureLive, player,
            player ? null : options.HexOverlay, options.IsExport || player ? null : options.HexSettles,
            options.HexAmbient && !options.IsExport, options.RequestAmbientFrame);
        DrawPresentation(canvas, layer, view, options.HexPresentation, options.ViewScale, player);
    }

    /// <param name="requestFrame">Asks for another frame when some chunk images are still to be drawn.</param>
    /// <param name="stats">Counts the chunks that stood in this frame.</param>
    /// <param name="player">The players' view: fog is opaque and only what the players may read is shown.</param>
    public static void Draw(SKCanvas canvas, HexLayer layer, WorldBounds view, double viewScale,
        HexChunkCache? cache, GRContext? gpu, HexCursorPreview? cursor, bool isExport,
        Action? requestFrame = null, RenderStats? stats = null, bool live = false, bool player = false,
        HexOverlay? overlay = null, HexSettles? settles = null, bool ambient = false, Action? requestAmbientFrame = null)
    {
        if (player) layer = PlayerLayers.GetValue(layer, ExportPlayerVisibility.Sanitize);
        var board = layer.Board;
        var hexPixels = board.Layout.HexWidth * viewScale;
        // An export far smaller than the board (a library thumbnail of a 512 × 512 board) draws the
        // far look too: hundreds of thousands of tiles a pixel wide only average to grey.
        var flat = hexPixels < (isExport ? ExportFlatBelowPixels : FlatColourBelowPixels);
        var budget = new BuildBudget(System.Diagnostics.Stopwatch.GetTimestamp());
        // Pictures left for the next frame ask for it, like chunks still being drawn.
        if (cache is { BuildInBackground: true } && !isExport && cache.Collect()) requestFrame?.Invoke();
        if (cache is not null && !live && !isExport) cache.EndLive(board.Orientation);
        if (!isExport) DrawBackdrop(canvas, layer, viewScale, hexPixels);

        // A chunk below the view can still hang into it: tall tiles rise over the rows behind.
        var reach = new WorldBounds(view.Left, view.Top, view.Right,
            view.Bottom + (HexStarterTiles.MaxOverhang + 0.2) * board.Layout.HexHeight);
        // Trees sway while the world is alive and close enough to see them; it fades in and out.
        var alive = cache is not null && !isExport ? cache.AliveLevel(ambient && !flat && hexPixels >= AmbientFromPixels, HexChunkCache.Now) : 0;
        _swayStrength = alive;
        var sway = alive > 0 && hexPixels >= SwayFromPixels && HexTreeSway.Available
            ? System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency
            : -1;
        void Ground(bool asFlat, ref BuildBudget budget)
        {
            if (asFlat && cache is not null)
            {
                DrawFlatBlocks(canvas, layer, view, viewScale, cache, ref budget);
            }
            else
            {
                DrawPass(canvas, layer, asFlat ? HexLook.Flat : HexLook.Terrain, asFlat ? view : reach, viewScale, cache, gpu, isExport, live, ref budget,
                    asFlat ? -1 : sway);
            }
        }

        // When the zoom crosses between the far and the near look, the new one fades in over the old.
        var now = HexChunkCache.Now;
        var change = cache is not null && !isExport ? cache.LookChange(layer.Id, flat, now) : null;
        var layerRect = new SKRect((float)view.Left, (float)reach.Top, (float)view.Right, (float)reach.Bottom);
        if (change is { } handOver)
        {
            Ground(handOver.FromFlat, ref budget);
            using var fade = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(255 * HexChunkCache.Ease(handOver.T))) };
            canvas.SaveLayer(layerRect, fade);
            Ground(flat, ref budget);
            canvas.Restore();
        }
        else
        {
            Ground(flat, ref budget);
        }

        DrawPaths(canvas, layer, view, viewScale, hexPixels);
        var featuresAlpha = change is { } c ? (flat ? 1 - HexChunkCache.Ease(c.T) : c.FromFlat ? HexChunkCache.Ease(c.T) : 1) : 1;
        if (!flat || (change is { FromFlat: false } && featuresAlpha > 0.01))
        {
            if (featuresAlpha < 0.995)
            {
                using var fade = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(255 * featuresAlpha)) };
                canvas.SaveLayer(layerRect, fade);
                DrawPass(canvas, layer, HexLook.Features, reach, viewScale, cache, gpu, isExport, live, ref budget);
                canvas.Restore();
            }
            else
            {
                DrawPass(canvas, layer, HexLook.Features, reach, viewScale, cache, gpu, isExport, live, ref budget, sway);
            }
        }

        if (budget.Pending > 0)
        {
            if (stats is not null) stats.HexChunksPending += budget.Pending;
            requestFrame?.Invoke();
        }
        else if (cache is not null && !isExport && cache.Animating(HexChunkCache.Now))
        {
            requestFrame?.Invoke();
        }
        else if (cache is not null && !isExport && cache.Alive(HexChunkCache.Now))
        {
            (requestAmbientFrame ?? requestFrame)?.Invoke();
        }

        // Hexes the tool just changed ease in over the kept pictures.
        if (settles is not null && settles.LayerId == layer.Id && settles.Items.Count > 0 && !flat)
        {
            DrawSettles(canvas, layer, settles.Items, viewScale);
        }

        // The world alive while it is looked at: water glints, wind over forests, cloud shadows.
        if (alive > 0 && !flat && cache is not null)
        {
            DrawAmbient(canvas, layer, view, viewScale, now / (double)System.Diagnostics.Stopwatch.Frequency, alive);
            cache.KeepAlive(now);
        }

        // Grid lines on hexes a few pixels wide would cover them: the board would be all grid.
        if (layer.Grid.Visible && (hexPixels >= GridBelowPixels || (isExport && hexPixels >= ExportGridBelowPixels)))
        {
            // The grid fades in with the zoom instead of appearing at one notch.
            var gridAlpha = isExport ? 1 : Math.Clamp((hexPixels - GridBelowPixels) / (GridFullPixels - GridBelowPixels), 0, 1);
            if (gridAlpha < 0.995)
            {
                using var fade = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(255 * gridAlpha)) };
                canvas.SaveLayer(new SKRect((float)view.Left, (float)view.Top, (float)view.Right, (float)view.Bottom), fade);
            }

            if (cache is null || isExport || !TryDrawGridPattern(canvas, layer, view, viewScale, hexPixels, cache))
            {
                DrawGrid(canvas, layer, view, viewScale, hexPixels, outside: null);
            }

            if (gridAlpha < 0.995) canvas.Restore();
        }

        DrawNotes(canvas, layer, view, viewScale, hexPixels, player, overlay?.Focus);
        DrawFog(canvas, layer, view, cache, player);
        DrawParty(canvas, layer, Math.Max(viewScale, 1e-6));

        if (overlay is not null && overlay.LayerId == layer.Id)
        {
            DrawOverlay(canvas, layer, overlay, viewScale);
        }

        if (cursor is not null && cursor.LayerId == layer.Id)
        {
            if (cursor.Glows is { } glows)
            {
                if (cursor.Ghost is { } ghost && hexPixels >= 12)
                {
                    foreach (var glow in glows) DrawGhostTile(canvas, board, glow, ghost);
                }

                foreach (var glow in glows) DrawHoverGlow(canvas, board, glow, cursor.Erase, viewScale);
            }
            else if (cursor.Hexes.Count > 0)
            {
                DrawHoverGlow(canvas, board, new HexCursorGlow(cursor.Hexes, 1), cursor.Erase, viewScale);
            }
        }
    }

    /// <summary>How many chunk images a frame built, how many it left for later, and when it started.</summary>
    private struct BuildBudget(long started)
    {
        public long Started = started;
        public int Built;
        public int Pending;

        /// <summary>The first picture of a frame is always drawn; more only while the frame's budget lasts.</summary>
        public readonly bool MayBuild => Built == 0
            || System.Diagnostics.Stopwatch.GetElapsedTime(Started).TotalMilliseconds < BuildBudgetMilliseconds;
    }

    /// <summary>
    /// The board's extent, faint, with its frame: an empty board, or one seen from too far for
    /// its grid, is still visible as the area hexes go on.
    /// </summary>
    private static void DrawBackdrop(SKCanvas canvas, HexLayer layer, double viewScale, double hexPixels)
    {
        if (layer.Grid.Visible && hexPixels >= GridBelowPixels && layer.Grid.ShowEmpty) return;
        var rect = BoardRect(layer.Board);
        var color = ParseColor(layer.Grid.Color);
        using var fill = new SKPaint { Color = color.WithAlpha((byte)Math.Min(40, color.Alpha / 3 + 8)) };
        canvas.DrawRect(rect, fill);
        using var frame = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color.WithAlpha((byte)Math.Min(255, color.Alpha * 2)),
            StrokeWidth = (float)(1.2 / Math.Max(viewScale, 1e-6)),
        };
        canvas.DrawRect(rect, frame);
    }

    /// <summary>The bounding box of the whole board.</summary>
    public static SKRect BoardRect(HexBoard board)
    {
        var left = board.Layout.OriginX - board.Layout.HexWidth / 2;
        var top = board.Layout.OriginY - board.Layout.HexHeight / 2;
        return new SKRect((float)left, (float)top, (float)(left + board.PixelWidth), (float)(top + board.PixelHeight));
    }

    private static void DrawPass(SKCanvas canvas, HexLayer layer, HexLook look, WorldBounds view, double viewScale,
        HexChunkCache? cache, GRContext? gpu, bool isExport, bool live, ref BuildBudget budget, double sway = -1)
    {
        var board = layer.Board;
        var crossed = look == HexLook.Flat ? null : PathChunks(layer);
        foreach (var key in board.ChunksIn(view))
        {
            var chunk = board.Chunk(key);
            if (chunk is null) continue;
            // No path runs through the chunk: what stands on the ground goes into the ground's
            // picture, one picture instead of two.
            var merged = crossed is not null && !crossed.Contains(key);
            if (look == HexLook.Features && (merged || !Info(chunk).NeedsFeatures)) continue;
            if (cache is not null && !isExport)
            {
                var context = look == HexLook.Flat ? null : HexChunkContext.For(board, key, new LookStyle(layer.Regions, merged));
                var level = LevelFor(viewScale);
                var place = new HexChunkCache.Place(layer.Id, key, look, level, merged);
                // During a drag a chunk changes on every move. Drawn hex by hex on the GPU it cost a
                // few hundred draw calls a frame (29 FPS with footprint 3); instead the chunk keeps a
                // CPU picture for the drag, only the hexes changed since are drawn into it again, and
                // the frame draws one image. A neighbour of the edit keeps its picture (only its edge
                // transitions are a step late).
                if (live && look != HexLook.Flat && cache.TryGet(chunk, board.Orientation, level, look, context) is null)
                {
                    if (cache.TryGet(chunk, board.Orientation, level, look, null) is { } stale)
                    {
                        DrawPicture(canvas, board, key, look, stale, level);
                        cache.NoteDrawn(place, chunk);
                    }
                    else if (LivePicture(layer, key, chunk, look, level, viewScale, cache, place, merged) is { } picture)
                    {
                        DrawPicture(canvas, board, key, look, picture, level);
                    }
                    else
                    {
                        DrawChunk(canvas, layer, key, chunk, look, view, viewScale, merged, cache);
                    }

                    continue;
                }

                switch (TryDrawCached(canvas, layer, key, chunk, look, viewScale, cache, gpu, context, budget.MayBuild && !live, merged, place, sway))
                {
                    case CachedDraw.Kept:
                        continue;
                    case CachedDraw.Built:
                        budget.Built++;
                        continue;
                    case CachedDraw.Deferred:
                        budget.Pending++;
                        continue;
                }
            }

            DrawChunk(canvas, layer, key, chunk, look, isExport ? null : view, viewScale, merged);
        }
    }

    /// <summary>The style a ground picture was drawn with: the regions, and whether it carries what stands on the ground.</summary>
    private readonly record struct LookStyle(System.Collections.Immutable.ImmutableList<HexRegion> Regions, bool WithFeatures);

    private static readonly ConditionalWeakTable<System.Collections.Immutable.ImmutableList<HexPath>, Tuple<HexLayout, HashSet<HexChunkKey>>> CrossedChunks = new();

    /// <summary>The chunks some path of the layer runs through, kept per path list.</summary>
    private static HashSet<HexChunkKey> PathChunks(HexLayer layer)
    {
        var layout = layer.Board.Layout;
        if (CrossedChunks.TryGetValue(layer.Paths, out var kept) && kept.Item1 == layout) return kept.Item2;
        var keys = new HashSet<HexChunkKey>();
        foreach (var path in layer.Paths)
        {
            var hexes = path.Style == HexRouteStyle.Edges ? path.Vertices.Select(v => v.Hex) : path.Hexes.AsEnumerable();
            foreach (var hex in hexes)
            {
                // A path piece reaches into the neighbours' halves of the edges it crosses.
                foreach (var near in HexCoord.Range(hex, 1))
                {
                    if (layer.Board.Contains(near)) keys.Add(HexBoard.ChunkOf(layout.ToOffset(near)));
                }
            }
        }

        CrossedChunks.AddOrUpdate(layer.Paths, Tuple.Create(layout, keys));
        return keys;
    }

    /// <param name="live">Kept transition pictures for a chunk drawn directly during a drag (render thread only).</param>
    /// <param name="only">Draws only these hexes of the chunk (all when null).</param>
    private static void DrawChunk(SKCanvas canvas, HexLayer layer, HexChunkKey key, HexChunk chunk, HexLook look, WorldBounds? clip,
        double pixelScale, bool merged = false, HexChunkCache? live = null, IReadOnlySet<HexCoord>? only = null)
    {
        switch (look)
        {
            case HexLook.Flat:
                DrawChunkFlat(canvas, layer.Board, key, chunk);
                break;
            case HexLook.Terrain:
                DrawChunkGround(canvas, layer, key, chunk, clip, pixelScale, live, only);
                if (merged && Info(chunk).NeedsFeatures) DrawChunkFeatures(canvas, layer, key, chunk, clip, pixelScale, only);
                break;
            case HexLook.Sway:
                DrawSwayMask(canvas, layer, key, chunk, clip, pixelScale);
                break;
            default:
                DrawChunkFeatures(canvas, layer, key, chunk, clip, pixelScale, only);
                break;
        }
    }

    /// <summary>A kept chunk picture at its place on the canvas.</summary>
    private static void DrawPicture(SKCanvas canvas, HexBoard board, HexChunkKey key, HexLook look, SKImage image, int level, SKPaint? paint = null)
    {
        var scale = Math.Pow(2, level);
        var bounds = Snapped(PictureBounds(board, key, look), scale);
        canvas.DrawImage(image, new SKRect((float)bounds.Left, (float)bounds.Top,
            (float)(bounds.Left + image.Width / scale), (float)(bounds.Top + image.Height / scale)), ChunkSampling, paint);
    }

    /// <summary>Draws one tile picture into a hex; used by the canvas and by the palette thumbnails.</summary>
    /// <param name="rotation">The picture turned by this many 60° steps clockwise.</param>
    public static void DrawTile(SKCanvas canvas, HexLayout layout, HexCoord hex, string tileId, int variant, float bleed = 0, int rotation = 0,
        SKPaint? paint = null)
    {
        var image = HexTileArt.Image(tileId, variant, layout.Orientation);
        if (image is null)
        {
            DrawMissing(canvas, layout, hex);
            return;
        }

        var rect = TileRect(layout, hex, image, bleed);
        var above = layout.Bounds(hex).Top - bleed - rect.Top;
        if (rotation % 6 == 0 || above > 0.01)
        {
            DrawScaled(canvas, image, rect, paint);
            return;
        }

        var (cx, cy) = layout.Center(hex);
        canvas.Save();
        canvas.RotateDegrees(60 * (rotation % 6), (float)cx, (float)cy);
        DrawScaled(canvas, image, rect, paint);
        canvas.Restore();
    }

    /// <summary>Where a tile picture goes: a tall picture is the hex picture plus room above it, the hex at its bottom.</summary>
    internal static SKRect TileRect(HexLayout layout, HexCoord hex, SKImage image, float bleed)
    {
        var bounds = layout.Bounds(hex);
        var hexPixels = HexTileArt.PixelSize(layout.Orientation).Height;
        var above = (image.Height - hexPixels) * (bounds.Bottom - bounds.Top) / hexPixels;
        return new SKRect((float)bounds.Left - bleed, (float)(bounds.Top - above) - bleed, (float)bounds.Right + bleed, (float)bounds.Bottom + bleed);
    }

    /// <summary>The hair terrain tiles overlap their neighbours by, at least one pixel of the target.</summary>
    internal static float TileBleed(HexLayout layout, double pixelScale) =>
        (float)Math.Min(layout.Size * 0.1, Math.Max(layout.Size * 0.012, 2.0 / Math.Max(pixelScale, 1e-6)));

    /// <summary>
    /// A tile or icon picture into a rectangle. On a CPU canvas a picture shown at under half its
    /// size is taken from a copy halved once or more (kept), sampled linearly: sampling the full
    /// 256-pixel picture through mipmaps cost a chunk drawn on the CPU most of its time.
    /// </summary>
    internal static void DrawScaled(SKCanvas canvas, SKImage image, SKRect rect, SKPaint? paint = null)
    {
        if (canvas.Context is null)
        {
            var pixels = rect.Width * Math.Abs(canvas.TotalMatrix.ScaleX);
            if (pixels > 0 && pixels * 2 <= image.Width)
            {
                canvas.DrawImage(HexTileArt.Reduced(image, (int)Math.Floor(Math.Log2(image.Width / pixels))), rect, ChunkSampling, paint);
                return;
            }
        }

        canvas.DrawImage(image, rect, Sampling, paint);
    }

    private static void DrawMissing(SKCanvas canvas, HexLayout layout, HexCoord hex)
    {
        using var path = HexOutline(layout, hex, 0);
        using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(0xD0, 0x40, 0xA0, 160) };
        canvas.DrawPath(path, fill);
    }

    /// <summary>The order hexes are drawn in: row by row, and on flat boards the higher (even) columns of a row first.</summary>
    private static IEnumerable<(int Column, int Row)> DrawOrder(HexBoard board)
    {
        var flat = board.Orientation == HexOrientation.Flat;
        for (var row = 0; row < HexBoard.ChunkSize; row++)
        {
            if (!flat)
            {
                for (var column = 0; column < HexBoard.ChunkSize; column++) yield return (column, row);
                continue;
            }

            for (var parity = 0; parity < 2; parity++)
            {
                for (var column = parity; column < HexBoard.ChunkSize; column += 2) yield return (column, row);
            }
        }
    }

    private static void DrawChunkGround(SKCanvas canvas, HexLayer layer, HexChunkKey key, HexChunk chunk, WorldBounds? clip, double pixelScale,
        HexChunkCache? live = null, IReadOnlySet<HexCoord>? only = null)
    {
        var board = layer.Board;
        // Terrain overlaps its neighbours by a hair, at least one pixel of the target, so antialiased
        // edges never show the page between hexes - also where two kept chunk images meet.
        var bleed = TileBleed(board.Layout, pixelScale);
        var detail = board.Layout.Size * pixelScale;
        List<HexCoord>? tall = null;
        foreach (var (column, row) in DrawOrder(board))
        {
            if (chunk[column, row] is not { } cell) continue;
            var hex = board.CoordOf(key, column, row);
            if (clip is { } view && !Intersects(TallBounds(board.Layout, hex), view)) continue;
            if (only is not null && !only.Contains(hex)) continue;
            DrawTile(canvas, board.Layout, hex, cell.Terrain, cell.Variant, bleed, cell.Rotation);
            if (detail >= 4 && live is not null) DrawTransitionsKept(canvas, board, hex, cell, pixelScale, live);
            else if (detail >= 4) DrawTransitions(canvas, board, hex, cell, detail);
            if (cell.Region != 0 && layer.Region(cell.Region) is { } region) TintHex(canvas, board.Layout, hex, region.Color);
            if (detail >= 8 && HexStarterTiles.Find(cell.Terrain) is { Overhang: > 0 }) (tall ??= []).Add(hex);
        }

        // A tall tile throws a soft shadow down and to the right onto the low ground round it.
        if (tall is not null && only is null)
        {
            foreach (var hex in tall) DrawBaseShadow(canvas, board, hex, bleed);
        }
    }

    /// <summary>The soft shadow at the foot of a tall tile, kept off the tile itself.</summary>
    private static void DrawBaseShadow(SKCanvas canvas, HexBoard board, HexCoord hex, float bleed)
    {
        var layout = board.Layout;
        if (board[hex] is not { } cell || HexTileArt.Image(cell.Terrain, cell.Variant, layout.Orientation) is not { } image) return;
        var r = (float)layout.Size;
        using var shape = HexOutline(layout, hex, layout.Size * 0.14);
        using var paint = new SKPaint
        {
            IsAntialias = true, Color = new SKColor(0x10, 0x0C, 0x08, 58),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, r * 0.12f),
        };
        canvas.Save();
        using (var own = HexOutline(layout, hex, 0)) canvas.ClipPath(own, SKClipOperation.Difference, antialias: true);
        canvas.ClipRect(TileRect(layout, hex, image, bleed) with { Bottom = (float)layout.Bounds(hex).Top + bleed }, SKClipOperation.Difference);
        canvas.Translate(r * 0.16f, r * 0.11f);
        canvas.DrawPath(shape, paint);
        canvas.Restore();
    }

    private static WorldBounds TallBounds(HexLayout layout, HexCoord hex)
    {
        var bounds = layout.Bounds(hex);
        return bounds with { Top = bounds.Top - HexStarterTiles.MaxOverhang * layout.HexHeight };
    }

    private static void DrawChunkFlat(SKCanvas canvas, HexBoard board, HexChunkKey key, HexChunk chunk)
    {
        // One path per colour: a chunk holds a few tile colours, and many hexes go in one draw call.
        var paths = new Dictionary<SKColor, SKPath>();
        var corners = new SKPoint[6];
        const double grow = 1.04;
        SKPath? marks = null;
        var markColors = new Dictionary<SKColor, SKPath>();
        for (var row = 0; row < HexBoard.ChunkSize; row++)
        {
            for (var column = 0; column < HexBoard.ChunkSize; column++)
            {
                if (chunk[column, row] is not { } cell) continue;
                var hex = board.CoordOf(key, column, row);
                var color = AverageOf(cell.Terrain);
                if (!paths.TryGetValue(color, out var path)) paths[color] = path = new SKPath();
                var (cx, cy) = board.Layout.Center(hex);
                for (var i = 0; i < 6; i++)
                {
                    var (x, y) = board.Layout.Corner(hex, i);
                    corners[i] = new SKPoint((float)(cx + (x - cx) * grow), (float)(cy + (y - cy) * grow));
                }

                path.AddPoly(corners, close: true);
                if (cell.Feature is { } feature)
                {
                    // Far away a settlement stays visible: a dark-rimmed dot of its colour on the hex.
                    var markColor = AverageOf(feature);
                    if (!markColors.TryGetValue(markColor, out var mark)) markColors[markColor] = mark = new SKPath();
                    mark.AddCircle((float)cx, (float)cy, (float)(board.Layout.Size * 0.42));
                    marks ??= new SKPath();
                    marks.AddCircle((float)cx, (float)cy, (float)(board.Layout.Size * 0.56));
                }
            }
        }

        using var paint = new SKPaint { IsAntialias = false };
        foreach (var (color, path) in paths)
        {
            paint.Color = color;
            canvas.DrawPath(path, paint);
            path.Dispose();
        }

        if (marks is null) return;
        using var rim = new SKPaint { IsAntialias = true, Color = new SKColor(0x20, 0x18, 0x10, 230) };
        canvas.DrawPath(marks, rim);
        marks.Dispose();
        using var dot = new SKPaint { IsAntialias = true };
        foreach (var (color, path) in markColors)
        {
            dot.Color = Lighten(color);
            canvas.DrawPath(path, dot);
            path.Dispose();
        }
    }

    private static SKColor Lighten(SKColor color) => new(
        (byte)Math.Min(255, color.Red + 50), (byte)Math.Min(255, color.Green + 40), (byte)Math.Min(255, color.Blue + 30));

    private static SKColor AverageOf(string tileId) => HexStarterTiles.Find(tileId)?.Average ?? new SKColor(0xD0, 0x40, 0xA0);

    /// <summary>The colour of a whole chunk seen from very far: the mean of its hexes' colours.</summary>
    private static SKColor ChunkAverage(HexChunk chunk) => ChunkAverages.GetValue(chunk, static c =>
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (var row = 0; row < HexBoard.ChunkSize; row++)
        {
            for (var column = 0; column < HexBoard.ChunkSize; column++)
            {
                if (c[column, row] is not { } cell) continue;
                var color = AverageOf(cell.Terrain);
                r += color.Red; g += color.Green; b += color.Blue; n++;
            }
        }

        return new StrongBox<SKColor>(n == 0 ? SKColors.Transparent : new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n)));
    }).Value;

    /// <summary>What a chunk holds besides terrain, counted once per chunk object.</summary>
    internal sealed record ChunkInfo(bool NeedsFeatures, int Revealed, bool Trees = false);

    private static readonly ConditionalWeakTable<HexChunk, ChunkInfo> ChunkInfos = new();

    internal static ChunkInfo Info(HexChunk chunk) => ChunkInfos.GetValue(chunk, static c =>
    {
        var needs = false;
        var trees = false;
        var revealed = 0;
        for (var row = 0; row < HexBoard.ChunkSize; row++)
        {
            for (var column = 0; column < HexBoard.ChunkSize; column++)
            {
                if (c[column, row] is not { } cell) continue;
                if (cell.Revealed) revealed++;
                needs |= cell.Feature is not null || cell.Region != 0
                    || HexStarterTiles.Find(cell.Terrain)?.Dynamic is { } kind && kind != HexDynamicIcon.None;
                trees |= HexStarterTiles.Find(cell.Terrain)?.Dynamic is HexDynamicIcon.Trees or HexDynamicIcon.Pines;
            }
        }

        return new ChunkInfo(needs, revealed, trees);
    });

    private enum CachedDraw { Direct, Kept, Built, Deferred }

    private static int LevelFor(double viewScale) => (int)Math.Ceiling(Math.Log2(Math.Max(viewScale, 1.0 / 1024)));

    /// <summary>
    /// The far look in blocks of span × span chunks (span grows until a block is about 128 px), drawn
    /// on the CPU: a GPU surface per small chunk cost the GPU 100-250 ms when a 512 × 512 board came
    /// into view. A block whose chunks changed keeps showing its old picture until it is redrawn.
    /// </summary>
    private static void DrawFlatBlocks(SKCanvas canvas, HexLayer layer, WorldBounds view, double viewScale,
        HexChunkCache cache, ref BuildBudget budget)
    {
        var board = layer.Board;
        var level = LevelFor(viewScale);
        var scale = Math.Pow(2, level);
        var first = board.ChunkBounds(new HexChunkKey(0, 0));
        var span = 1;
        while (span < 16 && span * (first.Right - first.Left) * scale < 128) span *= 2;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        foreach (var key in board.ChunksIn(view))
        {
            minX = Math.Min(minX, key.X); minY = Math.Min(minY, key.Y);
            maxX = Math.Max(maxX, key.X); maxY = Math.Max(maxY, key.Y);
        }

        if (maxX < 0) return;
        var lastChunkX = (board.Columns - 1) / HexBoard.ChunkSize;
        var lastChunkY = (board.Rows - 1) / HexBoard.ChunkSize;
        for (var by = minY / span; by <= maxY / span; by++)
        {
            for (var bx = minX / span; bx <= maxX / span; bx++)
            {
                // The block's chunks and the ring of chunks around it: the ring is drawn into the
                // block's picture too, so where two blocks meet each picture is solid across the seam
                // (shrunk on screen, a picture's edge pixels would otherwise let the page show through).
                var side = span + 2;
                var chunks = new HexChunk?[side * side];
                var any = false;
                for (var j = -1; j <= span; j++)
                {
                    for (var i = -1; i <= span; i++)
                    {
                        var x = bx * span + i;
                        var y = by * span + j;
                        var chunk = x < 0 || y < 0 ? null : board.Chunk(new HexChunkKey(x, y));
                        chunks[(j + 1) * side + i + 1] = chunk;
                        if (i >= 0 && j >= 0 && i < span && j < span) any |= chunk is not null;
                    }
                }

                if (!any) continue;
                var blockKey = (layer.Id, board.Orientation, bx, by, span, level);
                var kept = cache.Block(blockKey);
                if (kept is not null && kept.Layout != board.Layout) kept = null;
                var current = kept is not null && kept.Chunks.AsSpan().SequenceEqual(chunks, ReferenceComparer.Instance);
                if (!current && budget.MayBuild)
                {
                    var topLeft = board.ChunkBounds(new HexChunkKey(bx * span, by * span));
                    var bottomRight = board.ChunkBounds(new HexChunkKey(Math.Min(bx * span + span - 1, lastChunkX), Math.Min(by * span + span - 1, lastChunkY)));
                    var bounds = new WorldBounds(topLeft.Left, topLeft.Top, bottomRight.Right, bottomRight.Bottom);
                    var width = Math.Max(1, (int)Math.Ceiling((bounds.Right - bounds.Left) * scale));
                    var height = Math.Max(1, (int)Math.Ceiling((bounds.Bottom - bounds.Top) * scale));
                    using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
                    if (surface is not null)
                    {
                        var target = surface.Canvas;
                        target.Clear(SKColors.Transparent);
                        target.Scale((float)scale);
                        target.Translate((float)-bounds.Left, (float)-bounds.Top);
                        for (var j = -1; j <= span; j++)
                        {
                            for (var i = -1; i <= span; i++)
                            {
                                if (chunks[(j + 1) * side + i + 1] is { } chunk)
                                {
                                    DrawChunkFlat(target, board, new HexChunkKey(bx * span + i, by * span + j), chunk);
                                }
                            }
                        }

                        kept = new HexChunkCache.FlatBlock(chunks, board.Layout, bounds, surface.Snapshot());
                        cache.PutBlock(blockKey, kept);
                        budget.Built++;
                        current = true;
                    }
                }

                if (kept is not null)
                {
                    var b = kept.Bounds;
                    canvas.DrawImage(kept.Image, new SKRect((float)b.Left, (float)b.Top,
                        (float)(b.Left + kept.Image.Width / scale), (float)(b.Top + kept.Image.Height / scale)), ChunkSampling);
                }
                else
                {
                    for (var j = 0; j < span; j++)
                    {
                        for (var i = 0; i < span; i++)
                        {
                            if (chunks[(j + 1) * side + i + 1] is not { } chunk) continue;
                            var bounds = board.ChunkBounds(new HexChunkKey(bx * span + i, by * span + j));
                            using var paint = new SKPaint { Color = ChunkAverage(chunk) };
                            canvas.DrawRect(new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom), paint);
                        }
                    }
                }

                if (!current) budget.Pending++;
            }
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<HexChunk?>
    {
        public static readonly ReferenceComparer Instance = new();

        public bool Equals(HexChunk? x, HexChunk? y) => ReferenceEquals(x, y);

        public int GetHashCode(HexChunk? obj) => RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>
    /// The canvas area a chunk picture covers: the chunk's hexes, the room tall tiles need above
    /// them and a margin for icons and transitions that reach a little past a hex.
    /// </summary>
    private static WorldBounds PictureBounds(HexBoard board, HexChunkKey key, HexLook look)
    {
        var bounds = board.ChunkBounds(key);
        if (look == HexLook.Flat) return bounds;
        var layout = board.Layout;
        return new WorldBounds(
            bounds.Left - layout.HexWidth * 0.3,
            bounds.Top - layout.HexHeight * (HexStarterTiles.MaxOverhang + 0.25),
            bounds.Right + layout.HexWidth * 0.3,
            bounds.Bottom + layout.HexHeight * 0.15);
    }

    /// <summary>
    /// The picture's corner moved onto a whole pixel of its own scale: drawn at that scale its
    /// pixels then sit on the screen's pixels instead of being resampled half a pixel off.
    /// </summary>
    private static WorldBounds Snapped(WorldBounds bounds, double scale) =>
        bounds with { Left = Math.Floor(bounds.Left * scale) / scale, Top = Math.Floor(bounds.Top * scale) / scale };

    /// <summary>Asks the cache's worker for a chunk picture at a level.</summary>
    private static void EnqueueBuild(HexLayer layer, HexChunkKey key, HexChunk chunk, HexLook look, int level, HexChunkCache cache,
        HexChunkContext? context, bool merged)
    {
        var board = layer.Board;
        var scale = Math.Pow(2, level);
        var bounds = Snapped(PictureBounds(board, key, look), scale);
        var width = (int)Math.Ceiling((bounds.Right - bounds.Left) * scale);
        var height = (int)Math.Ceiling((bounds.Bottom - bounds.Top) * scale);
        if (width <= 0 || height <= 0 || width > HexChunkCache.MaxImageSide || height > HexChunkCache.MaxImageSide) return;
        cache.Enqueue(chunk, board.Orientation, level, look, context, () =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null) return null;
            var target = surface.Canvas;
            target.Clear(SKColors.Transparent);
            target.Scale((float)scale);
            target.Translate((float)-bounds.Left, (float)-bounds.Top);
            DrawChunk(target, layer, key, chunk, look, clip: null, scale, merged);
            return surface.Snapshot();
        });
    }

    private static CachedDraw TryDrawCached(SKCanvas canvas, HexLayer layer, HexChunkKey key, HexChunk chunk, HexLook look,
        double viewScale, HexChunkCache cache, GRContext? gpu, HexChunkContext? context, bool mayBuild, bool merged,
        HexChunkCache.Place place, double sway = -1)
    {
        var board = layer.Board;
        // Level n keeps the chunk at scale 2^n: the nearest level at or above the view scale.
        var level = LevelFor(viewScale);
        var scale = Math.Pow(2, level);
        var bounds = Snapped(PictureBounds(board, key, look), scale);
        var width = (int)Math.Ceiling((bounds.Right - bounds.Left) * scale);
        var height = (int)Math.Ceiling((bounds.Bottom - bounds.Top) * scale);
        if (width <= 0 || height <= 0 || width > HexChunkCache.MaxImageSide || height > HexChunkCache.MaxImageSide)
        {
            return CachedDraw.Direct;
        }

        var image = cache.TryGet(chunk, board.Orientation, level, look, context);
        var result = CachedDraw.Kept;
        if (image is null && cache.BuildInBackground)
        {
            // Drawn on the worker from the immutable layer; this frame shows a stand-in.
            EnqueueBuild(layer, key, chunk, look, level, cache, context, merged);
            mayBuild = false;
        }

        if (image is null && !mayBuild)
        {
            // A fill, an undo, a paste or the generator made a new version of the chunk that has no
            // picture yet: the picture of the version shown here before stands in, instead of a flat
            // patch of the chunk's average colour.
            var standIn = cache.StandIn(chunk, board.Orientation, level, look)
                ?? (cache.LastDrawn(place) is { } before && !ReferenceEquals(before, chunk)
                    ? cache.StandIn(before, board.Orientation, level, look)
                    : null);
            if (standIn is { } other)
            {
                // Every picture of a look covers the same canvas area, its corner snapped to its own pixels.
                DrawPicture(canvas, board, key, look, other.Image, other.Level);
                // The same hexes at another level only sharpen when the right picture comes (a zoom):
                // that is not faded, it would cost every chunk on screen a second picture per frame.
                if (other.Level == level || cache.StandIn(chunk, board.Orientation, level, look) is null)
                {
                    cache.NoteStandIn(place, other.Image, other.Level);
                }
            }
            else if (look == HexLook.Terrain)
            {
                if (chunk.Count < HexBoard.ChunkSize * HexBoard.ChunkSize * 3 / 4)
                {
                    // A sparse chunk is cheap to draw flat, and a rectangle would paint its empty hexes.
                    DrawChunkFlat(canvas, board, key, chunk);
                }
                else
                {
                    var flatBounds = board.ChunkBounds(key);
                    using var paint = new SKPaint { Color = ChunkAverage(chunk) };
                    canvas.DrawRect(new SKRect((float)flatBounds.Left, (float)flatBounds.Top, (float)flatBounds.Right, (float)flatBounds.Bottom), paint);
                }
            }

            return CachedDraw.Deferred;
        }

        if (image is null)
        {
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = (gpu is not null ? SKSurface.Create(gpu, false, info) : null) ?? SKSurface.Create(info);
            if (surface is null) return CachedDraw.Direct;
            var target = surface.Canvas;
            target.Clear(SKColors.Transparent);
            target.Scale((float)scale);
            target.Translate((float)-bounds.Left, (float)-bounds.Top);
            DrawChunk(target, layer, key, chunk, look, clip: null, scale, merged);
            image = surface.Snapshot();
            if (image is null) return CachedDraw.Direct;
            cache.Add(chunk, board.Orientation, level, look, image, context);
            result = CachedDraw.Built;
        }

        // The right picture fades in over the one that stood in for it instead of replacing it at once.
        if (cache.Swap(place, image, HexChunkCache.Now) is { } swap)
        {
            DrawPicture(canvas, board, key, look, swap.From, swap.FromLevel);
            using var fade = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(255 * HexChunkCache.Ease(swap.T))) };
            DrawPicture(canvas, board, key, look, image, level, fade);
        }
        else if (sway < 0 || !DrawSwaying(canvas, layer, key, chunk, look, image, level, bounds, cache, context, merged, sway, _swayStrength))
        {
            canvas.DrawImage(image,
                new SKRect((float)bounds.Left, (float)bounds.Top,
                    (float)(bounds.Left + image.Width / scale), (float)(bounds.Top + image.Height / scale)),
                ChunkSampling);
        }

        cache.NoteDrawn(place, chunk);
        return result;
    }

    /// <summary>
    /// The chunk's picture during a drag, kept on the CPU: made once from the chunk's last drawn
    /// picture (or drawn whole), then on every change only the areas of the hexes changed since are
    /// cleared and drawn again - everything that can reach such an area (its own tile and what
    /// stands on it, the transitions and living icons of its neighbours, tall tiles from the rows in
    /// front) is drawn clipped to it. Null when the picture would be too large.
    /// </summary>
    private static SKImage? LivePicture(HexLayer layer, HexChunkKey key, HexChunk chunk, HexLook look, int level,
        double viewScale, HexChunkCache cache, HexChunkCache.Place place, bool merged)
    {
        var board = layer.Board;
        var scale = Math.Pow(2, level);
        var bounds = Snapped(PictureBounds(board, key, look), scale);
        var width = (int)Math.Ceiling((bounds.Right - bounds.Left) * scale);
        var height = (int)Math.Ceiling((bounds.Bottom - bounds.Top) * scale);
        if (width <= 0 || height <= 0 || width > HexChunkCache.MaxImageSide || height > HexChunkCache.MaxImageSide) return null;

        var kept = cache.Live(place);
        if (kept is { } same && ReferenceEquals(same.Version, chunk)) return same.Image;
        HexChunk? before = kept?.Version;
        SKSurface? surface = kept?.Surface;
        if (surface is null)
        {
            surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null) return null;
            surface.Canvas.Clear(SKColors.Transparent);
            // The picture the drag started from, when it is a CPU one (a GPU picture would have to
            // be read back): copied, and only the changes are drawn into it.
            before = cache.LastDrawn(place);
            if (before is not null && cache.TryGet(before, board.Orientation, level, look, null) is { IsTextureBacked: false } start
                && start.Width == width && start.Height == height)
            {
                surface.Canvas.DrawImage(start, 0, 0);
            }
            else
            {
                before = null;
            }
        }

        var target = surface.Canvas;
        target.Save();
        target.Scale((float)scale);
        target.Translate((float)-bounds.Left, (float)-bounds.Top);
        if (before is null)
        {
            DrawChunk(target, layer, key, chunk, look, null, scale, merged, cache);
        }
        else
        {
            DrawChangedHexes(target, layer, key, before, chunk, look, scale, merged, cache);
        }

        target.Restore();
        var image = surface.Snapshot();
        cache.PutLive(place, chunk, surface, image);
        return image;
    }

    /// <summary>Clears the areas the hexes changed between two versions of a chunk can reach and draws them again.</summary>
    private static void DrawChangedHexes(SKCanvas canvas, HexLayer layer, HexChunkKey key, HexChunk before, HexChunk chunk,
        HexLook look, double scale, bool merged, HexChunkCache cache)
    {
        var board = layer.Board;
        var layout = board.Layout;
        // Reach of one hex's drawing: two hex sizes round it (bleed, transitions and living icons of
        // the neighbours) and the tallest tile above it.
        var reach = layout.Size * 2.2;
        var rise = (HexStarterTiles.MaxOverhang + 0.2) * layout.HexHeight;
        var areas = new List<SKRect>();
        for (var row = 0; row < HexBoard.ChunkSize; row++)
        {
            for (var column = 0; column < HexBoard.ChunkSize; column++)
            {
                if (ReferenceEquals(before[column, row], chunk[column, row])) continue;
                areas.Add(Reach(layout.Bounds(board.CoordOf(key, column, row)), reach, rise));
            }
        }

        if (areas.Count == 0) return;

        // Every hex whose own drawing can fall into a changed area is drawn again.
        var again = new HashSet<HexCoord>();
        for (var row = 0; row < HexBoard.ChunkSize; row++)
        {
            for (var column = 0; column < HexBoard.ChunkSize; column++)
            {
                if (chunk[column, row] is null) continue;
                var hex = board.CoordOf(key, column, row);
                var own = Reach(layout.Bounds(hex), reach, rise);
                foreach (var area in areas)
                {
                    if (!own.IntersectsWith(area)) continue;
                    again.Add(hex);
                    break;
                }
            }
        }

        using var path = new SKPath();
        foreach (var area in areas) path.AddRect(area);
        canvas.Save();
        canvas.ClipPath(path, SKClipOperation.Intersect, antialias: false);
        canvas.DrawColor(SKColors.Transparent, SKBlendMode.Src);
        DrawChunk(canvas, layer, key, chunk, look, null, scale, merged, cache, again);
        canvas.Restore();
    }

    private static SKRect Reach(WorldBounds bounds, double reach, double rise) =>
        new((float)(bounds.Left - reach), (float)(bounds.Top - rise - reach), (float)(bounds.Right + reach), (float)(bounds.Bottom + reach));

    /// <summary>
    /// Many small hexes on screen: the grid is one repeating picture instead of a path through every
    /// hex (a path of 15 000 hexes cost the GPU 10-16 ms a frame). The picture covers the inner part
    /// of the board, where every hex exists; the outer two rings, where the board's edge is jagged,
    /// are drawn as lines around the picture.
    /// </summary>
    private static bool TryDrawGridPattern(SKCanvas canvas, HexLayer layer, WorldBounds view, double viewScale,
        double hexPixels, HexChunkCache cache)
    {
        var board = layer.Board;
        if (!layer.Grid.ShowEmpty || board.Columns < 5 || board.Rows < 5) return false;
        var layout = board.Layout;
        (double X, double Y) At(int column, int row) => layout.Center(layout.FromOffset(new HexOffset(column, row)));
        var inner = new SKRect(
            (float)Math.Max(At(1, 1).X, At(1, 2).X), (float)Math.Max(At(1, 1).Y, At(2, 1).Y),
            (float)Math.Min(At(board.Columns - 2, 1).X, At(board.Columns - 2, 2).X),
            (float)Math.Min(At(1, board.Rows - 2).Y, At(2, board.Rows - 2).Y));
        if (inner.Width <= 0 || inner.Height <= 0) return false;

        var level = (int)Math.Ceiling(Math.Log2(Math.Max(viewScale, 1.0 / 1024)));
        var scale = Math.Pow(2, level);
        var width = Math.Max(layer.Grid.Width, 0.5 / scale);
        var flatTop = layout.Orientation == HexOrientation.Flat;
        var periodX = flatTop ? 3 * layout.Size : HexLayout.Sqrt3 * layout.Size;
        var periodY = flatTop ? HexLayout.Sqrt3 * layout.Size : 3 * layout.Size;
        var pixelsX = Math.Max(1, (int)Math.Ceiling(periodX * scale));
        var pixelsY = Math.Max(1, (int)Math.Ceiling(periodY * scale));
        if (pixelsX > MaxGridTilePixels || pixelsY > MaxGridTilePixels) return false;
        var color = ParseColor(layer.Grid.Color);
        var tile = cache.GridTile((layout.Orientation, layout.Size, color, width, level), () =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(pixelsX, pixelsY, SKColorType.Rgba8888, SKAlphaType.Premul));
            var target = surface.Canvas;
            target.Clear(SKColors.Transparent);
            target.Scale((float)(pixelsX / periodX), (float)(pixelsY / periodY));
            // Hex (0, 0) has its centre at the tile's corner; every hex touching the tile is outlined.
            var local = new HexLayout(layout.Orientation, layout.Size, 0, 0);
            using var path = new SKPath();
            for (var q = -3; q <= 4; q++)
            {
                for (var r = -4; r <= 4; r++)
                {
                    var hex = new HexCoord(q, r);
                    for (var i = 0; i < 6; i++)
                    {
                        var (x, y) = local.Corner(hex, i);
                        if (i == 0) path.MoveTo((float)x, (float)y);
                        else path.LineTo((float)x, (float)y);
                    }

                    path.Close();
                }
            }

            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color, StrokeWidth = (float)width };
            target.DrawPath(path, line);
            return surface.Snapshot();
        });

        var matrix = SKMatrix.CreateScale((float)(periodX / pixelsX), (float)(periodY / pixelsY))
            .PostConcat(SKMatrix.CreateTranslation((float)layout.OriginX, (float)layout.OriginY));
        using (var shader = SKShader.CreateImage(tile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, ChunkSampling, matrix))
        using (var paint = new SKPaint { Shader = shader, IsAntialias = false })
        {
            canvas.DrawRect(inner, paint);
        }

        // The outer rings' lines and the numbers (text, never part of the picture) go on top.
        if ((layer.Grid.Numbers && hexPixels >= NumbersBelowPixels)
            || view.Left < inner.Left || view.Top < inner.Top || view.Right > inner.Right || view.Bottom > inner.Bottom)
        {
            DrawGrid(canvas, layer, view, viewScale, hexPixels, outside: inner);
        }

        return true;
    }

    /// <param name="outside">When set, only the lines outside this rectangle are drawn (the pattern covers it).</param>
    private static void DrawGrid(SKCanvas canvas, HexLayer layer, WorldBounds view, double viewScale, double hexPixels, SKRect? outside)
    {
        var board = layer.Board;
        var color = ParseColor(layer.Grid.Color);
        using var line = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color,
            // The grid stays at least half a screen pixel wide however far the view is zoomed out.
            StrokeWidth = (float)Math.Max(layer.Grid.Width, 0.5 / Math.Max(viewScale, 1e-6)),
        };
        using var path = new SKPath();
        var numbers = layer.Grid.Numbers && hexPixels >= NumbersBelowPixels;
        using var font = numbers ? new SKFont(SKTypeface.Default, (float)(board.Layout.Size * 0.2)) : null;
        using var text = numbers ? new SKPaint { IsAntialias = true, Color = color.WithAlpha((byte)Math.Min(255, color.Alpha * 2)) } : null;
        foreach (var hex in board.HexesIn(view))
        {
            if (!layer.Grid.ShowEmpty && board[hex] is null) continue;
            var offset = board.Layout.ToOffset(hex);
            // Under the repeating picture only the two outer rings need lines of their own.
            var lines = outside is null || offset.Column <= 1 || offset.Row <= 1
                || offset.Column >= board.Columns - 2 || offset.Row >= board.Rows - 2;
            if (lines)
            {
                for (var i = 0; i < 6; i++)
                {
                    var (x, y) = board.Layout.Corner(hex, i);
                    if (i == 0) path.MoveTo((float)x, (float)y);
                    else path.LineTo((float)x, (float)y);
                }

                path.Close();
            }

            if (numbers)
            {
                var (cx, cy) = board.Layout.Center(hex);
                canvas.DrawText(NumberOf(board, offset), (float)cx, (float)(cy - board.Layout.HexHeight * 0.32), SKTextAlign.Center, font!, text!);
            }
        }

        if (outside is { } inner)
        {
            canvas.Save();
            canvas.ClipRect(inner, SKClipOperation.Difference);
            canvas.DrawPath(path, line);
            canvas.Restore();
        }
        else
        {
            canvas.DrawPath(path, line);
        }
    }

    /// <summary>
    /// The hex's number, column then row from 1: «0101» while both fit in two digits; on larger
    /// boards the parts are as wide as the largest and split by a dot («251.251»), so they never run together.
    /// </summary>
    public static string NumberOf(HexBoard board, HexOffset offset)
    {
        var digits = Math.Max(2, Math.Max(board.Columns, board.Rows).ToString(CultureInfo.InvariantCulture).Length);
        var format = new string('0', digits);
        var column = (offset.Column + 1).ToString(format, CultureInfo.InvariantCulture);
        var row = (offset.Row + 1).ToString(format, CultureInfo.InvariantCulture);
        return digits == 2 ? column + row : column + "." + row;
    }

    /// <summary>A set of hexes filled and ringed round its outside (cursor, selection).</summary>
    private static void DrawHexSet(SKCanvas canvas, HexBoard board, IReadOnlyCollection<HexCoord> hexes, double viewScale, SKColor fillColor, SKColor ringColor)
    {
        using var path = new SKPath();
        foreach (var hex in hexes)
        {
            using var outline = HexOutline(board.Layout, hex, 0);
            path.AddPath(outline);
        }

        // The ring goes round the set only: the edges between two hexes of it are left out
        // (a footprint of 217 hexes has 1 302 edges, its outline 96), which keeps the stroked
        // outline cheap for the GPU on every pointer move.
        var inside = hexes as IReadOnlySet<HexCoord> ?? hexes.ToHashSet();
        using var border = new SKPath();
        foreach (var hex in hexes)
        {
            var (cx, cy) = board.Layout.Center(hex);
            for (var i = 0; i < 6; i++)
            {
                var (ax, ay) = board.Layout.Corner(hex, i);
                var (bx, by) = board.Layout.Corner(hex, (i + 1) % 6);
                var across = board.Layout.HexAt(ax + bx - cx, ay + by - cy);
                if (inside.Contains(across)) continue;
                border.MoveTo((float)ax, (float)ay);
                border.LineTo((float)bx, (float)by);
            }
        }

        var screen = (float)(1 / Math.Max(viewScale, 1e-6));
        // The fill of a large footprint as one antialiased path of overlapping hexes cost the GPU
        // more than the whole drag (217 hexes); its outer edge lies under the ring anyway.
        using var fill = new SKPaint { IsAntialias = false, Color = fillColor };
        using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = 4 * screen, Color = new SKColor(0, 0, 0, 120) };
        using var ring = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = 2 * screen, Color = ringColor,
        };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(border, halo);
        canvas.DrawPath(border, ring);
    }

    /// <summary>
    /// The hover outline ("Шёлк", owner 2026-10-21): a faint even fill, a 1.25 px line on a soft
    /// dark seat, all scaled by the footprint's strength - no hard black halo.
    /// </summary>
    private static void DrawHoverGlow(SKCanvas canvas, HexBoard board, HexCursorGlow glow, bool erase, double viewScale)
    {
        var strength = Math.Clamp(glow.Strength, 0, 1);
        if (strength <= 0.003 || glow.Hexes.Count == 0) return;
        var alpha = (byte)Math.Round(255 * strength);
        var screen = (float)(1 / Math.Max(viewScale, 1e-6));
        var (fill, line) = erase
            ? (new SKColor(0xC8, 0x30, 0x30), new SKColor(0xF0, 0x70, 0x60))
            : (new SKColor(0xF4, 0xD0, 0x70), new SKColor(0xF4, 0xD0, 0x70));
        using var area = new SKPath();
        foreach (var hex in glow.Hexes)
        {
            using var outline = HexOutline(board.Layout, hex, 0);
            area.AddPath(outline);
        }

        var inside = glow.Hexes as IReadOnlySet<HexCoord> ?? glow.Hexes.ToHashSet();
        using var border = new SKPath();
        foreach (var hex in glow.Hexes)
        {
            var (cx, cy) = board.Layout.Center(hex);
            for (var i = 0; i < 6; i++)
            {
                var (ax, ay) = board.Layout.Corner(hex, i);
                var (bx, by) = board.Layout.Corner(hex, (i + 1) % 6);
                if (inside.Contains(board.Layout.HexAt(ax + bx - cx, ay + by - cy))) continue;
                border.MoveTo((float)ax, (float)ay);
                border.LineTo((float)bx, (float)by);
            }
        }

        // A small footprint is filled smoothly; a large one stays aliased (its edge lies under the line).
        using var fillPaint = new SKPaint { IsAntialias = glow.Hexes.Count <= 7, Color = fill.WithAlpha((byte)Math.Round(15 * strength)) };
        using var seat = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
            StrokeWidth = 2.75f * screen, Color = new SKColor(0x14, 0x0C, 0x04, (byte)Math.Round(82 * strength)),
        };
        using var ring = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
            StrokeWidth = 1.25f * screen, Color = line.WithAlpha(alpha),
        };
        canvas.DrawPath(area, fillPaint);
        canvas.DrawPath(border, seat);
        canvas.DrawPath(border, ring);
    }

    internal static SKPath HexOutline(HexLayout layout, HexCoord hex, double grow)
    {
        var path = new SKPath();
        var (cx, cy) = layout.Center(hex);
        var factor = (layout.Size + grow) / layout.Size;
        for (var i = 0; i < 6; i++)
        {
            var (x, y) = layout.Corner(hex, i);
            var px = (float)(cx + (x - cx) * factor);
            var py = (float)(cy + (y - cy) * factor);
            if (i == 0) path.MoveTo(px, py);
            else path.LineTo(px, py);
        }

        path.Close();
        return path;
    }

    private static bool Intersects(WorldBounds a, WorldBounds b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    internal static SKColor ParseColor(string value) =>
        SKColor.TryParse(value, out var color) ? color : new SKColor(0x20, 0x18, 0x10, 0x66);
}
