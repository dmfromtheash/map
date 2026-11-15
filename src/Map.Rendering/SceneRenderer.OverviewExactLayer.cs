using System.Runtime.CompilerServices;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

public static partial class SceneRenderer
{
    /// <summary>
    /// The overview's exact objects (stamps of four screen pixels and more, <see cref="OverviewExactScreenSize"/>) as one
    /// screen-aligned layer rastered off the frame thread. Drawn one by one on the frame, the ~15 000 large stamps of a
    /// fit view of two million objects took 250-640 ms of every refinement frame. Now the frame copies the finished
    /// layer 1:1 (the same evaluator, matrix and image keys as the direct draw, so the settled pixels are the same),
    /// and while a layer for the current view is being rastered it draws the previous one transformed - as Chromium's
    /// compositor draws tiles of another raster scale stretched while the tiles at the ideal scale are rastered on
    /// worker threads (cc PictureLayerTiling / TilingSetCoverageIterator). The layer reaches a quarter of the view
    /// past each edge, so a pan or a wheel notch is covered until the next raster lands.
    /// False turns it off (the direct draw), for comparisons.
    /// </summary>
    internal static bool OverviewExactLayers = true;

    /// <summary>Below this many exact objects the direct draw is cheaper than a layer.</summary>
    private const int ExactLayerMinimumObjects = 128;
    private const long ExactLayerMaximumPixels = 20L * 1024 * 1024;
    /// <summary>A stand-in layer from resident images (cold library) is redrawn at most this often while images arrive.</summary>
    private const double ExactLayerPartialIntervalMilliseconds = 250;

    /// <summary>Objects the layer can carry: their pixels do not depend on what lies under them or on a working image.</summary>
    private static bool ExactLayerNeutral(SceneObject o) => o.Visible && o.Kind == SceneObjectKind.Stamp
        && o.Stamp is { Blur: 0, ShadowOpacity: 0, ClipMaskEnabled: false, Blend: "Normal" };

    private readonly record struct ExactLayerKey(ulong Content, SKMatrix Matrix, SKRectI Extent,
        StampForegroundSurface? Foreground, long SourceRevision, long MissingRevision);

    /// <summary>
    /// What a layer was made of, chunk by chunk: the exact set's hash and its largest object, and the selection's hash
    /// and bounds. After an edit the frame compares them with the current ones to find the few changed places.
    /// </summary>
    private sealed record ExactLayerChunks(Dictionary<(int X, int Y), (ulong Hash, double MaxSide)> Map, ulong ExtrasHash, WorldBounds? ExtrasBounds);

    private sealed record ExactLayerImage(ExactLayerKey Key, SKImage Image, bool Complete, ExactLayerChunks Chunks);

    /// <summary>The objects of one key and the preparation plan of the images they sample (built off the frame).</summary>
    private sealed class ExactLayerWork(ExactLayerKey key, IStampPreparationPlan plan, ExactLayerChunks chunks)
    {
        public ExactLayerChunks Chunks { get; } = chunks;
        public ExactLayerKey Key { get; } = key;
        public IStampPreparationPlan Plan { get; } = plan;
        public SceneObject[]? Objects;
        public volatile bool Added;
        public volatile bool Failed;
        /// <summary>A final raster of this key started (or a stand-in for an older key): the next change replans.</summary>
        public bool Rastered;
    }

    private sealed class ExactLayerJob
    {
        public required ExactLayerKey Key { get; init; }
        public required ExactLayerChunks Chunks { get; init; }
        public required SceneObject[] Objects { get; init; }
        public required RenderOptions Options { get; init; }
        public required IStampImageLease Images { get; init; }
        public required bool Final { get; init; }
        public required Action Ready { get; init; }
        public required StampForegroundSurface? Foreground { get; init; }
        public bool Cancelled, Done, Complete;
        public SKImage? Image;
    }

    private sealed class ExactLayerState
    {
        public ExactLayerImage? Ready;
        public ExactLayerJob? Job;
        public ExactLayerWork? Work;
        public ExactLayerKey? FrameOnly;
        public long Touched;
        public long PartialStarted;
    }

    private sealed class ExactLayerContext
    {
        public readonly Dictionary<LayerId, ExactLayerState> Layers = [];
        public long Sweeps;
    }

    private static readonly ConditionalWeakTable<ObjectOverviewCache, ExactLayerContext> ExactLayerContexts = [];

    /// <summary>
    /// Draws the frame's overview exact objects from their layer, or returns false: the caller then draws them
    /// directly as before (a sparse view, an edit preview, objects the layer cannot carry, no layer yet).
    /// </summary>
    private static bool TryDrawOverviewExactLayer(SKCanvas canvas, MapDocument document, ObjectLayer layer,
        ObjectOverviewPlan plan, int zoomBucket, double bucketScale, IReadOnlyList<SceneObject> extras, RenderOptions options)
    {
        if (!OverviewExactLayers || options.IsExport || options.RequestRefinedFrame is not { } refine
            || options.Stamps is not { } stamps || options.ObjectCache is not { } cache
            || options.ObjectOverrides is { Count: > 0 }) return false;
        var matrix = canvas.TotalMatrix;
        if (matrix.Persp0 != 0 || matrix.Persp1 != 0 || matrix.Persp2 != 1 || !matrix.TryInvert(out var inverse)) return false;
        var clip = canvas.DeviceClipBounds;
        if (clip.Width <= 0 || clip.Height <= 0) return false;
        // A quarter of the view past each edge; an eighth on a very large view, to bound the layer's memory.
        var extent = SKRectI.Inflate(clip, clip.Width / 4, clip.Height / 4);
        if ((long)extent.Width * extent.Height > ExactLayerMaximumPixels / 2) extent = SKRectI.Inflate(clip, clip.Width / 8, clip.Height / 8);
        if ((long)extent.Width * extent.Height > ExactLayerMaximumPixels) return false;
        var world = inverse.MapRect(extent);
        var area = new WorldBounds(world.Left, world.Top, world.Right, world.Bottom);

        // The content: the chunks' exact sets (made once per chunk and bucket off the frame) and the selection.
        var content = 14_695_981_039_346_656_037UL;
        var count = 0;
        var pendingSets = false;
        var chunkMap = new Dictionary<(int X, int Y), (ulong Hash, double MaxSide)>();
        var minX = (int)Math.Floor(area.Left / OverviewChunkSize); var maxX = (int)Math.Floor(area.Right / OverviewChunkSize);
        var minY = (int)Math.Floor(area.Top / OverviewChunkSize); var maxY = (int)Math.Floor(area.Bottom / OverviewChunkSize);
        if ((long)(maxX - minX + 1) * (maxY - minY + 1) > 16_384) return false;
        for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                if (!plan.TryGetChunk(x, y, out var chunk)) continue;
                if (chunk.TryGetExact(zoomBucket) is not { } set)
                {
                    pendingSets = true;
                    if (chunk.BeginExact(zoomBucket))
                        QueueDetailWork(() =>
                        {
                            try { chunk.BuildExact(zoomBucket, bucketScale); }
                            finally { refine(); }
                        });
                    continue;
                }
                if (set.Objects.Length == 0) continue;
                if (!set.Neutral) return false;
                count += set.Objects.Length;
                content = (content ^ set.Hash) * 1_099_511_628_211UL;
                content = (content ^ (uint)x ^ ((ulong)(uint)y << 32)) * 1_099_511_628_211UL;
                chunkMap[(x, y)] = (set.Hash, set.MaxSide);
            }
        var extrasHash = 14_695_981_039_346_656_037UL;
        WorldBounds? extrasBounds = null;
        foreach (var extra in extras)
        {
            if (!ExactLayerNeutral(extra)) return false;
            content = HashObject(content, extra);
            extrasHash = HashObject(extrasHash, extra);
            extrasBounds = Union(extrasBounds, SceneGeometry.BoundsOf(extra));
        }
        var chunks = new ExactLayerChunks(chunkMap, extrasHash, extrasBounds);
        var context = ExactLayerContexts.GetValue(cache, static _ => new ExactLayerContext());
        SweepExactLayers(context, cache.FrameNumber);
        context.Layers.TryGetValue(layer.Id, out var state);
        if (pendingSets)
        {
            // A new bucket or edited chunk: its sets arrive in a frame or two. Keep the previous layer even
            // while settled; returning false here drew the entire exact set on the snapshot handoff frame.
            if (options.Stats is { } waiting) waiting.DetailTilesPending++;
            refine();
            return state is not null && DrawExactStandIn(canvas, state, matrix, clip, null, options);
        }
        if (count + extras.Count < ExactLayerMinimumObjects) return false;
        if (state is null) context.Layers[layer.Id] = state = new ExactLayerState();
        state.Touched = cache.FrameNumber;
        var key = new ExactLayerKey(content, matrix, extent, document.StampForeground, stamps.SourceRevision, stamps.MissingRevision);

        // A finished raster replaces the layer shown so far.
        if (state.Job is { } finished)
        {
            SKImage? image = null;
            var done = false;
            lock (finished)
            {
                if (finished.Done) { done = true; image = finished.Image; finished.Image = null; }
            }
            if (done)
            {
                state.Job = null;
                if (image is not null)
                {
                    state.Ready?.Image.Dispose();
                    state.Ready = new ExactLayerImage(finished.Key, image, finished.Complete, finished.Chunks);
                }
                // A final raster that failed or still missed an image (a key the plan did not name): the frame draws that view.
                if (finished.Final && finished.Key == key && (image is null || !finished.Complete)) state.FrameOnly = key;
            }
        }
        if (state.Ready is { } exact && exact.Key == key && exact.Complete)
        {
            BlitExactLayer(canvas, exact);
            RecordExactLayer(options, 1);
            return true;
        }
        if (state.FrameOnly == key) return false;
        StartExactLayerWork(state, key, chunks, plan, zoomBucket, area, extras, stamps, refine, options);
        if (options.Stats is { } pending) pending.DetailTilesPending++;
        refine();
        // The previous layer stands in: always while the camera moves, and for the same objects (new images or a
        // pan) when settled.
        if (DrawExactStandIn(canvas, state, matrix, clip, options.CameraMoving ? null : content, options)) return true;
        // After an edit: the previous layer everywhere except the few changed places, which are drawn directly until
        // the new layer lands (Chromium re-rasters only a tile's invalidated rect). Drawing every exact object
        // directly here cost 378 ms p95 per placement on 2 million objects (pack-5 A/B, 2026-11-15).
        return !options.CameraMoving && DrawExactPatched(canvas, document, state, key, chunks, plan, zoomBucket, extras, options);
    }

    /// <summary>At most this many changed chunks are patched; more is a different view, drawn directly as before.</summary>
    private const int ExactPatchMaximumChunks = 16;

    private static bool DrawExactPatched(SKCanvas canvas, MapDocument document, ExactLayerState state, ExactLayerKey key,
        ExactLayerChunks chunks, ObjectOverviewPlan plan, int zoomBucket, IReadOnlyList<SceneObject> extras, RenderOptions options)
    {
        if (state.Ready is not { } shown || !shown.Key.Matrix.Equals(key.Matrix) || shown.Key.Extent != key.Extent
            || !ReferenceEquals(shown.Key.Foreground, key.Foreground)) return false;
        var dirty = new List<WorldBounds>();
        var changed = 0;
        foreach (var (cell, now) in chunks.Map)
        {
            if (shown.Chunks.Map.TryGetValue(cell, out var was) && was.Hash == now.Hash) continue;
            if (++changed > ExactPatchMaximumChunks) return false;
            dirty.Add(Expand(ChunkBounds(cell), Math.Max(now.MaxSide, was.MaxSide)));
        }
        foreach (var (cell, was) in shown.Chunks.Map)
        {
            if (chunks.Map.ContainsKey(cell)) continue;
            if (++changed > ExactPatchMaximumChunks) return false;
            dirty.Add(Expand(ChunkBounds(cell), was.MaxSide));
        }
        if (shown.Chunks.ExtrasHash != chunks.ExtrasHash)
        {
            if (shown.Chunks.ExtrasBounds is { } oldExtras) dirty.Add(oldExtras);
            if (chunks.ExtrasBounds is { } newExtras) dirty.Add(newExtras);
        }
        if (dirty.Count == 0) return false;
        // Separate old/new places for a move: a bounding rectangle would redraw everything between them.
        // Include filtering beyond object bounds and gather against the rounded device clip, not its smaller
        // world rectangle. A large unchanged stamp in a neighbouring chunk may cross the patch as well.
        using var patch = new SKRegion();
        var areas = new List<WorldBounds>();
        key.Matrix.TryInvert(out var inverse);
        foreach (var area in dirty)
        {
            var device = key.Matrix.MapRect(ToRect(area));
            var rect = new SKRectI((int)Math.Floor(device.Left) - 2, (int)Math.Floor(device.Top) - 2,
                (int)Math.Ceiling(device.Right) + 2, (int)Math.Ceiling(device.Bottom) + 2);
            patch.Op(rect, SKRegionOperation.Union);
            var world = inverse.MapRect(rect);
            areas.Add(new(world.Left, world.Top, world.Right, world.Bottom));
        }
        // The previous layer outside the patch, pixel for pixel.
        canvas.Save();
        canvas.ResetMatrix();
        canvas.ClipRegion(patch, SKClipOperation.Difference);
        canvas.DrawImage(shown.Image, shown.Key.Extent.Left, shown.Key.Extent.Top);
        canvas.Restore();
        // Inside the patch: every exact object that touches it, in the layer's order, drawn as the layer draws them.
        canvas.Save();
        canvas.ResetMatrix();
        canvas.ClipRegion(patch);
        canvas.SetMatrix(key.Matrix);
        var exact = new Dictionary<ObjectId, SceneObject>();
        var reach = 2 / Math.Max(.000001, Math.Abs(key.Matrix.ScaleX));
        foreach (var (cell, set) in chunks.Map)
        {
            if (!areas.Any(area => Expand(ChunkBounds(cell), set.MaxSide + reach).Intersects(area))
                || !plan.TryGetChunk(cell.X, cell.Y, out var chunk) || chunk.TryGetExact(zoomBucket) is not { } current) continue;
            foreach (var item in current.Objects)
                if (areas.Any(area => Expand(SceneGeometry.BoundsOf(item), reach).Intersects(area))) exact.TryAdd(item.Id, item);
        }
        foreach (var extra in extras)
            if (areas.Any(area => Expand(SceneGeometry.BoundsOf(extra), reach).Intersects(area))) exact[extra.Id] = extra;
        var objects = exact.Values.OrderBy(item => item.Sublayer).ToArray();
        foreach (var sceneObject in objects) DrawObject(canvas, sceneObject, document.StampForeground, options);
        canvas.Restore();
        if (options.Stats is { } stats) { stats.OverviewExactPatched += objects.Length; stats.DrawCalls += objects.Length + 1; }
        RecordExactLayer(options, 3);
        return true;
    }

    private static WorldBounds ChunkBounds((int X, int Y) cell) => new(cell.X * OverviewChunkSize, cell.Y * OverviewChunkSize,
        (cell.X + 1) * OverviewChunkSize, (cell.Y + 1) * OverviewChunkSize);

    private static WorldBounds? Union(WorldBounds? a, WorldBounds? b) => a is not { } x ? b : b is not { } y ? x
        : new WorldBounds(Math.Min(x.Left, y.Left), Math.Min(x.Top, y.Top), Math.Max(x.Right, y.Right), Math.Max(x.Bottom, y.Bottom));

    private static void StartExactLayerWork(ExactLayerState state, ExactLayerKey key, ExactLayerChunks chunks, ObjectOverviewPlan overview,
        int zoomBucket, WorldBounds area, IReadOnlyList<SceneObject> extras, IStampImageSource stamps, Action refine,
        RenderOptions options)
    {
        if (state.Job is not null) return;
        // One plan at a time. A moving camera changes the key every frame: it rasters the last planned view
        // (a frame or two old, inside the layer's margin) instead of replanning forever.
        if (state.Work is { Added: false, Failed: false }) return;
        if (state.Work is { Failed: true } failed && failed.Key == key) { state.FrameOnly = key; return; }
        if (state.Work is not { } work || work.Failed || work.Key != key && (!options.CameraMoving || work.Rastered))
        {
            if (stamps.CreatePreparationPlan() is not { } created) { state.FrameOnly = key; return; }
            work = state.Work = new ExactLayerWork(key, created, chunks);
            var selected = extras.ToArray();
            var foreground = key.Foreground;
            var recordOptions = options.ForBackgroundTile(new ExactRequestRecorder(), new RenderStats());
            QueueDetailWork(() =>
            {
                try
                {
                    var objects = GatherExactObjects(overview, zoomBucket, area, selected);
                    // The image keys are those the raster will ask for: the same draw calls with the same matrix,
                    // on a canvas that draws nothing, answered by a source that only records the requests.
                    var recorder = (ExactRequestRecorder)recordOptions.Stamps!;
                    using var dry = new SKNoDrawCanvas(Math.Max(1, key.Extent.Width), Math.Max(1, key.Extent.Height));
                    dry.SetMatrix(LayerMatrix(key));
                    foreach (var sceneObject in objects) DrawObject(dry, sceneObject, foreground, recordOptions, prepareGpuEffect: false);
                    work.Plan.Add(recorder.Requests);
                    Volatile.Write(ref work.Objects, objects);
                    work.Added = true;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { work.Failed = true; }
                finally { refine(); }
            });
            return;
        }
        if (Volatile.Read(ref work.Objects) is not { } list) return;
        var ready = work.Plan.Prepare(refine);
        IStampImageLease? images;
        if (ready)
        {
            images = work.Plan.Pin();
            if (images is null) { state.FrameOnly = work.Key; return; }
            work.Rastered = true;
        }
        else
        {
            // Images still arriving (a cold library): a stand-in from what is resident, now and then.
            if (state.Ready is { } shown && shown.Key == work.Key
                && System.Diagnostics.Stopwatch.GetElapsedTime(state.PartialStarted).TotalMilliseconds < ExactLayerPartialIntervalMilliseconds) return;
            images = work.Plan.PinResident();
            if (images is null) return;
            state.PartialStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            if (work.Key != key) work.Rastered = true;
        }
        var job = new ExactLayerJob
        {
            Key = work.Key, Chunks = work.Chunks, Objects = list, Images = images, Final = ready, Ready = refine, Foreground = work.Key.Foreground,
            Options = options.ForBackgroundTile(images, new RenderStats()),
        };
        state.Job = job;
        QueueDetailWork(() => RunExactLayer(job));
        if (options.Stats is { } stats) stats.OverviewExactLayerStarted++;
    }

    /// <summary>The overview's exact objects of an area in the direct draw's order: chunk rows, chunk order, then the selection, by sublayer.</summary>
    private static SceneObject[] GatherExactObjects(ObjectOverviewPlan overview, int zoomBucket, WorldBounds area, SceneObject[] extras)
    {
        var exact = new Dictionary<ObjectId, SceneObject>();
        for (var y = (int)Math.Floor(area.Top / OverviewChunkSize); y <= (int)Math.Floor(area.Bottom / OverviewChunkSize); y++)
            for (var x = (int)Math.Floor(area.Left / OverviewChunkSize); x <= (int)Math.Floor(area.Right / OverviewChunkSize); x++)
            {
                if (!overview.TryGetChunk(x, y, out var chunk) || chunk.TryGetExact(zoomBucket) is not { } set) continue;
                foreach (var sceneObject in set.Objects)
                    if (SceneGeometry.BoundsOf(sceneObject).Intersects(area)) exact.TryAdd(sceneObject.Id, sceneObject);
            }
        foreach (var extra in extras) exact[extra.Id] = extra;
        return [.. exact.Values.OrderBy(item => item.Sublayer)];
    }

    /// <summary>The frame's matrix moved to the layer's own pixels (its top-left device pixel at 0,0).</summary>
    private static SKMatrix LayerMatrix(ExactLayerKey key)
    {
        var matrix = key.Matrix;
        matrix.TransX -= key.Extent.Left;
        matrix.TransY -= key.Extent.Top;
        return matrix;
    }

    private static void RunExactLayer(ExactLayerJob job)
    {
        SKImage? image = null;
        var complete = false;
        try
        {
            bool cancelled;
            lock (job) cancelled = job.Cancelled;
            if (!cancelled)
            {
                // The platform pixel layout of the frame's CPU surface, as the detail tiles use.
                using var surface = SKSurface.Create(new SKImageInfo(job.Key.Extent.Width, job.Key.Extent.Height))
                    ?? throw new InvalidOperationException("Could not allocate an overview exact layer.");
                var target = surface.Canvas;
                target.Clear(SKColors.Transparent);
                target.SetMatrix(LayerMatrix(job.Key));
                foreach (var sceneObject in job.Objects) DrawObject(target, sceneObject, job.Foreground, job.Options, prepareGpuEffect: false);
                target.Flush();
                image = surface.Snapshot();
                complete = job.Final && job.Images.UnresolvedRequestVersion == 0 && job.Options.Stats!.UnresolvedImages == 0;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { image?.Dispose(); image = null; }
        job.Images.Dispose();
        lock (job)
        {
            if (job.Cancelled) { image?.Dispose(); image = null; }
            job.Image = image;
            job.Complete = complete;
            job.Done = true;
        }
        job.Ready();
    }

    /// <summary>The layer copied pixel for pixel: its matrix is the frame's, so nothing is resampled.</summary>
    private static void BlitExactLayer(SKCanvas canvas, ExactLayerImage layer)
    {
        canvas.Save();
        canvas.ResetMatrix();
        canvas.DrawImage(layer.Image, layer.Key.Extent.Left, layer.Key.Extent.Top);
        canvas.Restore();
    }

    /// <summary>The previous layer under the current matrix, when it covers the whole view; null content: any objects.</summary>
    private static bool DrawExactStandIn(SKCanvas canvas, ExactLayerState state, SKMatrix matrix, SKRectI clip,
        ulong? content, RenderOptions options)
    {
        if (state.Ready is not { } shown || content is { } same && shown.Key.Content != same) return false;
        if (!shown.Key.Matrix.TryInvert(out var previous)) return false;
        if (shown.Key.Matrix.Equals(matrix))
        {
            BlitExactLayer(canvas, shown);
            RecordExactLayer(options, 2);
            return true;
        }
        var device = SKMatrix.Concat(matrix, previous);
        var covered = device.MapRect(new SKRect(shown.Key.Extent.Left, shown.Key.Extent.Top, shown.Key.Extent.Right, shown.Key.Extent.Bottom));
        if (covered.Left > clip.Left + .5f || covered.Top > clip.Top + .5f
            || covered.Right < clip.Right - .5f || covered.Bottom < clip.Bottom - .5f)
        {
            // A moving camera that ran ahead of the rasters keeps the part it has (the chunks' dots show the rest)
            // when that is most of the view: drawing the rest object by object is the 0.2-0.5 s frame this replaces.
            var inside = SKRect.Intersect(covered, new SKRect(clip.Left, clip.Top, clip.Right, clip.Bottom));
            if (!options.CameraMoving || inside.IsEmpty
                || (double)inside.Width * inside.Height < .5 * clip.Width * clip.Height) return false;
        }
        canvas.Save();
        canvas.SetMatrix(device);
        canvas.DrawImage(shown.Image, new SKRect(0, 0, shown.Image.Width, shown.Image.Height),
            new SKRect(shown.Key.Extent.Left, shown.Key.Extent.Top, shown.Key.Extent.Right, shown.Key.Extent.Bottom),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        canvas.Restore();
        RecordExactLayer(options, 2);
        return true;
    }

    private static void RecordExactLayer(RenderOptions options, int kind)
    {
        if (options.Stats is not { } stats) return;
        stats.OverviewExactLayer = Math.Max(stats.OverviewExactLayer, kind);
        stats.DrawCalls++;
    }

    /// <summary>Layers no frame drew for a while (another document, a closed tab) give their pixels back.</summary>
    private static void SweepExactLayers(ExactLayerContext context, long frame)
    {
        if (++context.Sweeps % 64 != 0 || context.Layers.Count == 0) return;
        foreach (var (id, state) in context.Layers.Where(pair => pair.Value.Touched < frame - 240).ToArray())
        {
            context.Layers.Remove(id);
            state.Ready?.Image.Dispose();
            state.Ready = null;
            if (state.Job is { } job) lock (job) { job.Cancelled = true; job.Image?.Dispose(); job.Image = null; }
        }
    }

    /// <summary>A source for the dry run: records each pixel-size request and answers it as pending.</summary>
    private sealed class ExactRequestRecorder : IStampImageSource
    {
        public readonly List<(string Sha256, double Width, double Height)> Requests = [];
        public bool LastImageRequestPending => true;
        public SKImage? TryGetImage(string sha256) => null;
        public SKImage? TryGetImageForOcclusion(string sha256) => null;
        public (int Width, int Height)? TryGetImageSize(string sha256) => null;
        public SKColor? TryGetOverviewColor(string sha256) => null;
        public SKImage? TryGetReadyImageForPixelSize(string sha256, double width, double height) => null;
        public SKImage? TryGetImageForPixelSize(string sha256, double width, double height)
        {
            Requests.Add((sha256, width, height));
            return null;
        }
    }
}

/// <summary>A chunk's exact objects at one zoom bucket, their content hash, and whether the exact layer can carry them all.</summary>
internal sealed record OverviewExactSet(SceneObject[] Objects, ulong Hash, bool Neutral)
{
    /// <summary>The longest side of its largest object: how far a change in this chunk can reach past its border.</summary>
    public double MaxSide { get; } = Objects.Length == 0 ? 0 : Objects.Max(o => Math.Max(o.Width, o.Height));
}
