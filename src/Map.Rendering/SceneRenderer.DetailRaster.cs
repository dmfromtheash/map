using System.Runtime.CompilerServices;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

public static partial class SceneRenderer
{
    /// <summary>
    /// Dense detail tiles are rastered off the frame thread. A tile whose images are ready is drawn by a
    /// background worker from a pinned snapshot of those images (<see cref="IStampPreparationPlan.Pin"/>) with the
    /// same CPU evaluator as the synchronous path; the frame only adopts finished tiles, one per frame,
    /// and keeps the seed or the overview for the others. One tile of a dense map
    /// holds tens of thousands of objects: rastered on the frame it took seconds (Chromium rasters tiles on
    /// worker threads for the same reason, the compositor only uploads them).
    /// </summary>
    private enum BackgroundTile { FrameThread, Waiting, Adopted }

    private readonly record struct DetailRasterKey(LayerId Layer, int X, int Y, int Zoom, ulong Revision);

    private sealed class DetailRasterJob
    {
        public required DetailRasterKey Key { get; init; }
        public required MapDocument Document { get; init; }
        public required ObjectLayer Layer { get; init; }
        public required WorldBounds Bounds { get; init; }
        public required double Scale { get; init; }
        public required int Bucket { get; init; }
        public required RenderOptions Options { get; init; }
        public IStampImageLease? Images;
        public IStampPreparationPlan? Plan;
        public IDisposable? Demand;
        public readonly StampImageRequestPriority Priority = new();
        public bool Preparing;
        public required Action? Ready { get; init; }
        /// <summary>A coarse fallback level (see <see cref="DrawDetailedStampTiles"/>), not an exact tile.</summary>
        public required bool Seed { get; init; }
        public long Touched;
        public bool Cancelled, Done, Complete;
        public SKImage? Image;
        public int Objects, Culled;
        public PreparedDetailTile? Tile;
        public SKImage?[]? Parts;
        public int Side, Remaining, Failures;
    }

    /// <summary>A tile's objects in draw order, after occlusion culling: what every part of its raster draws.</summary>
    private sealed record PreparedDetailTile(IReadOnlyList<SceneObject> DrawOrder, IReadOnlyList<SceneObject> Ordered, int Objects, int Culled);

    /// <summary>The background tiles of one render context (one detail cache), touched by its frame thread only.</summary>
    private sealed class DetailRasterJobs
    {
        public readonly Dictionary<DetailRasterKey, DetailRasterJob> Jobs = [];
        /// <summary>Tiles whose background raster came back incomplete: they are built on the frame as before.</summary>
        public readonly HashSet<DetailRasterKey> FrameOnly = [];
        public int Running => Jobs.Values.Count(job => !job.Done);
        public WorldBounds? View;
        public long ViewFrame = -1;
        public double AheadX, AheadY;
        public bool BackgroundPreparation;
    }

    private static readonly ConditionalWeakTable<ObjectOverviewCache, DetailRasterJobs> DetailRasterContexts = [];

    /// <summary>
    /// Workers for every render context: ProcessorCount − 2 (at most 8), below normal priority. Visible tile
    /// work is FIFO (centre first); optional overview layers wait until visible work has drained.
    /// </summary>
    private static readonly int DetailRasterWorkers = Math.Clamp(Environment.ProcessorCount - 2, 1, 8);
    internal static DetailWorkQueue DetailRasterQueue = new(DetailRasterWorkers);

    private static void QueueDetailWork(Action work, bool overviewLayer = false) => DetailRasterQueue.Add(work, overviewLayer);

    private static void QueueDetailWorkFromFrame(Action work, RenderStats? stats, Action? rejected = null)
    {
        var queue = DetailRasterQueue;
        if (queue.TryAdd(work)) return;
        if (stats is not null) stats.DetailQueueDeferrals++;
        ThreadPool.QueueUserWorkItem(static item =>
        {
            try { item.Queue.Add(item.Work); }
            catch (ObjectDisposedException)
            {
                // A deferred descriptor can lose a race with render-context shutdown.
                try { item.Rejected?.Invoke(); }
                catch (Exception error) when (error is not OutOfMemoryException)
                { System.Diagnostics.Trace.TraceError("Deferred detail cleanup: {0}", error); }
            }
        }, (Queue: queue, Work: work, Rejected: rejected), preferLocal: false);
    }

    /// <summary>Two FIFO priorities. Idle workers leave the CPUs free while a visible raster is still running.</summary>
    internal sealed class DetailWorkQueue : IDisposable
    {
        private readonly object _gate = new();
        private readonly Queue<Action> _visible = [], _layers = [];
        private readonly Thread[] _threads;
        private int _visibleRunning;
        private bool _stopped;

        internal DetailWorkQueue(int workers)
        {
            _threads = new Thread[workers];
            for (var i = 0; i < workers; i++)
            {
                _threads[i] = new Thread(Run)
                {
                    IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Map detail tile raster " + i,
                };
                _threads[i].Start();
            }
        }

        internal void Add(Action work, bool overviewLayer = false)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopped, this);
                (overviewLayer ? _layers : _visible).Enqueue(work);
                Monitor.PulseAll(_gate);
            }
        }

        internal bool TryAdd(Action work)
        {
            if (!Monitor.TryEnter(_gate)) return false;
            try
            {
                ObjectDisposedException.ThrowIf(_stopped, this);
                _visible.Enqueue(work);
                Monitor.PulseAll(_gate);
                return true;
            }
            finally { Monitor.Exit(_gate); }
        }

        private void Run()
        {
            while (true)
            {
                Action work;
                bool visible;
                lock (_gate)
                {
                    while (_visible.Count == 0 && (_layers.Count == 0 || _visibleRunning > 0))
                    {
                        if (_stopped && _layers.Count == 0) return;
                        Monitor.Wait(_gate);
                    }
                    visible = _visible.Count > 0;
                    work = (visible ? _visible : _layers).Dequeue();
                    if (visible) _visibleRunning++;
                }
                try { work(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
                finally
                {
                    lock (_gate)
                    {
                        if (visible) _visibleRunning--;
                        Monitor.PulseAll(_gate);
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_gate) { _stopped = true; Monitor.PulseAll(_gate); }
            foreach (var thread in _threads) thread.Join();
        }
    }

    /// <summary>
    /// The tile's draw order is made once. A seed tile (a whole page of objects) is then split into up to 4×4 parts
    /// drawn in parallel, each the whole tile under a clip of its part (runs entirely outside it are skipped as a
    /// whole, so the same stamps go together); an exact tile is one raster.
    /// </summary>


    private static void RunDetailRaster(DetailRasterJob job)
    {
        try
        {
            lock (job) if (job.Cancelled) { FinishDetailRaster(job, null, false); return; }
            if (job.Plan is { } plan)
            {
                job.Demand ??= plan.KeepRequests();
                if (!plan.Prepare(job.Ready!) || (job.Images = plan.Pin()) is null)
                {
                    lock (job)
                    {
                        if (job.Cancelled) { FinishDetailRaster(job, null, false); return; }
                        job.Preparing = false;
                    }
                    job.Ready?.Invoke();
                    return;
                }
            }
            var tile = PrepareDetailTile(job.Layer, job.Bounds, job.Scale, null, default,
                job.Options.ForBackgroundTile(job.Images!, new RenderStats()));
            (job.Objects, job.Culled) = (tile.Objects, tile.Culled);
            job.Tile = tile;
            // Only a seed is split: a clip moves Skia's edge coverage by up to 3/255 at the parts' seams, harmless in
            // a stand-in that is never the final picture. An exact tile is one raster, byte for byte the frame's.
            var side = !job.Seed ? 1 : tile.Ordered.Count > 24_000 ? 4 : tile.Ordered.Count > 6_000 ? 2 : 1;
            job.Parts = new SKImage?[side * side];
            job.Side = side;
            job.Remaining = side * side;
            for (var part = 1; part < side * side; part++)
            {
                var index = part;
                QueueDetailWork(() => RunDetailRasterPart(job, index));
            }
            RunDetailRasterPart(job, 0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { System.Diagnostics.Trace.TraceError("Detail tile: {0}", ex); FinishDetailRaster(job, null, false); }
    }

    private static void RunDetailRasterPart(DetailRasterJob job, int part)
    {
        SKImage? image = null;
        var unresolved = 1;
        try
        {
            bool cancelled;
            lock (job) cancelled = job.Cancelled;
            if (!cancelled)
            {
                var stats = new RenderStats();
                var options = job.Options.ForBackgroundTile(job.Images!, stats);
                using var scratch = new StampBatchScratch();
                // The same platform pixel layout as the frame-thread CPU path.
                // The same platform pixel layout as the frame-thread CPU path.
                using var surface = SKSurface.Create(new SKImageInfo(514, 514))!;
                surface.Canvas.Clear(SKColors.Transparent);
                SKRectI? device = null; WorldBounds? world = null;
                if (job.Side > 1)
                {
                    int Edge(int k) => k * 514 / job.Side;
                    var (column, row) = (part % job.Side, part / job.Side);
                    device = new SKRectI(Edge(column), Edge(row), Edge(column + 1), Edge(row + 1));
                    double World(int pixel, double origin) => origin + (pixel - 1) / job.Scale;
                    world = new WorldBounds(World(device.Value.Left, job.Bounds.Left), World(device.Value.Top, job.Bounds.Top),
                        World(device.Value.Right, job.Bounds.Left), World(device.Value.Bottom, job.Bounds.Top));
                }
                DrawDetailTilePiece(surface.Canvas, job.Document, job.Layer, job.Bounds, job.Scale, job.Bucket, job.Tile!,
                    null, options, scratch, device, world);
                image = surface.Snapshot();
                unresolved = stats.UnresolvedImages;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { System.Diagnostics.Trace.TraceError("Detail tile: {0}", ex); image?.Dispose(); image = null; }
        job.Parts![part] = image;
        if (image is null || unresolved > 0) Interlocked.Increment(ref job.Failures);
        if (Interlocked.Decrement(ref job.Remaining) > 0) return;
        // The last part composes the tile: each part's pixels copied unchanged into their rectangle.
        SKImage? composed = null;
        try
        {
            if (job.Failures == 0 && job.Side == 1) { composed = job.Parts[0]; job.Parts[0] = null; }
            else if (job.Failures == 0)
            {
                using var surface = SKSurface.Create(new SKImageInfo(514, 514))!;
                surface.Canvas.Clear(SKColors.Transparent);
                using var copy = new SKPaint { BlendMode = SKBlendMode.Src };
                for (var index = 0; index < job.Parts.Length; index++)
                {
                    int Edge(int k) => k * 514 / job.Side;
                    var (column, row) = (index % job.Side, index / job.Side);
                    var rect = new SKRect(Edge(column), Edge(row), Edge(column + 1), Edge(row + 1));
                    surface.Canvas.DrawImage(job.Parts[index], rect, rect, new SKSamplingOptions(SKFilterMode.Nearest), copy);
                }
                composed = surface.Snapshot();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { composed?.Dispose(); composed = null; }
        foreach (var piece in job.Parts) piece?.Dispose();
        FinishDetailRaster(job, composed, composed is not null && job.Images!.UnresolvedRequestVersion == 0);
    }

    private static void FinishDetailRaster(DetailRasterJob job, SKImage? image, bool complete)
    {
        job.Images?.Dispose();
        job.Demand?.Dispose();
        job.Demand = null;
        lock (job)
        {
            if (job.Cancelled) { image?.Dispose(); image = null; }
            job.Image = image;
            job.Complete = complete;
            job.Done = true;
        }
        job.Ready?.Invoke();
    }

    /// <summary>Cancels background tiles that no frame asked for since the previous one (the view or revision moved on).</summary>
    internal static void SweepDetailRasters(ObjectOverviewCache cache, bool endFrame = false)
    {
        if (!DetailRasterContexts.TryGetValue(cache, out var context) || context.Jobs.Count == 0) return;
        foreach (var (key, job) in context.Jobs.Where(pair => pair.Value.Touched < cache.FrameNumber - (endFrame ? 0 : 1)).ToArray())
        {
            context.Jobs.Remove(key);
            CancelDetailRaster(job);
        }
        if (context.FrameOnly.Count > 256) context.FrameOnly.Clear();
    }

    internal static void ReleaseDetailRasters(ObjectOverviewCache cache)
    {
        if (!DetailRasterContexts.TryGetValue(cache, out var context)) return;
        foreach (var job in context.Jobs.Values)
            CancelDetailRaster(job);
        context.Jobs.Clear();
        DetailRasterContexts.Remove(cache);
    }

    // Relinquish ownership on the frame; dropping image interests or waiting for a job's
    // publication lock belongs to a worker, just like acquiring/pinning its images.
    private static void CancelDetailRaster(DetailRasterJob job)
    {
        Volatile.Write(ref job.Cancelled, true);
        job.Priority.Value = double.PositiveInfinity;
        ThreadPool.QueueUserWorkItem(static cancelled =>
        {
            SKImage? image;
            IDisposable? demand = null;
            lock (cancelled)
            {
                image = cancelled.Image; cancelled.Image = null;
                if (!cancelled.Preparing && !cancelled.Done)
                { demand = cancelled.Demand; cancelled.Demand = null; }
            }
            image?.Dispose(); demand?.Dispose();
        }, job, preferLocal: false);
    }

    // Finished job images are CPU rasters; releasing an abandoned one need not enter Skia's
    // native allocation/handle bookkeeping on the compositor thread either.
    private static void RetireDetailImage(SKImage? image)
    {
        if (image is not null) ThreadPool.QueueUserWorkItem(static held => held.Dispose(), image, preferLocal: false);
    }

    private static BackgroundTile TryBackgroundTile(MapDocument document, ObjectLayer layer, DetailTileRevisions revisions,
        int x, int y, int bucket, WorldBounds bounds, double scale, ulong revision, ObjectOverviewCache cache, RenderOptions options,
        DetailPreparation frame, bool seed, out ObjectOverviewCacheResult adopted, bool prefetch = false)
    {
        adopted = default;
        if (options.Stamps is not { } stamps || options.RequestRefinedFrame is not { } ready) return BackgroundTile.FrameThread;
        var context = DetailRasterContexts.GetValue(cache, static _ => new DetailRasterJobs());
        var key = new DetailRasterKey(layer.Id, x, y, DetailZoomKey(layer, bucket), revision);
        if (context.FrameOnly.Contains(key)) return BackgroundTile.FrameThread;
        if (context.Jobs.TryGetValue(key, out var job))
        {
            job.Touched = cache.FrameNumber;
            job.Priority.Value = DetailPriority(context, bounds, seed || prefetch);
            SKImage? image;
            if (!Monitor.TryEnter(job))
            {
                if (options.Stats is { } busy) busy.DetailJobLocksBusy++;
                return BackgroundTile.Waiting;
            }
            try
            {
                if (!job.Done)
                {
                    if (job.Plan is not null && !job.Preparing && frame.StartedJobs < 2)
                    {
                        job.Preparing = true;
                        frame.StartedJobs++;
                        QueueDetailWorkFromFrame(() => RunDetailRaster(job), options.Stats,
                            () => FinishDetailRaster(job, null, false));
                    }
                    if (options.Stats is { } waiting) waiting.DetailRastersWaiting++;
                    return BackgroundTile.Waiting;
                }
                // Each adopted tile is a texture upload on its first draw (on the CPU a 0.26 Mpx copy): one per frame.
                if (frame.Adopted >= 1) return BackgroundTile.Waiting;
                image = job.Image;
                job.Image = null;
            }
            finally { Monitor.Exit(job); }
            context.Jobs.Remove(key);
            if (image is null || !job.Complete)
            {
                RetireDetailImage(image);
                if (job.Plan is not null) return BackgroundTile.Waiting;
                context.FrameOnly.Add(key);
                return BackgroundTile.FrameThread;
            }
            frame.Adopted++;
            if (options.Stats is { } taken) taken.DetailRastersAdopted++;
            adopted = cache.GetOrCreate(layer.Id, x, y, DetailZoomKey(layer, bucket), revision, () => image,
                () => (job.Objects, job.Culled), fallback: seed);
            return BackgroundTile.Adopted;
        }
        var area = Expand(bounds, 1 / scale);
        // A tile with a kept base gets only its appended objects drawn over that base, on the frame as before.
        if (!layer.Effects.DepthSort && revisions.TryAppendBase(cache, layer.Id, x, y, bucket, area, stamps.MissingRevision, out _, out _))
            return BackgroundTile.FrameThread;
        if (revisions.Plan(x, y, bucket, area, stamps) is not { Complete: true } tile)
            return context.BackgroundPreparation ? BackgroundTile.Waiting : BackgroundTile.FrameThread;
        if (context.Running >= DetailRasterWorkers * 2 || frame.StartedJobs >= 2) return BackgroundTile.Waiting;
        var backgroundPreparation = tile.Plan.BackgroundPreparation;
        context.BackgroundPreparation |= backgroundPreparation;
        if (backgroundPreparation)
        {
            var heldBytes = context.Jobs.Values.Where(j => !j.Done && j.Plan is not null).Sum(j => j.Plan!.Bytes);
            if (heldBytes > 0 && heldBytes + tile.Plan.Bytes > stamps.PreparationBytes) return BackgroundTile.Waiting;
        }
        var images = backgroundPreparation ? null : tile.Plan.Pin();
        if (!backgroundPreparation && images is null)
            return tile.Plan.Prepare(ready) ? BackgroundTile.FrameThread : BackgroundTile.Waiting;
        var started = new DetailRasterJob
        {
            Key = key, Document = document, Layer = layer, Bounds = bounds, Scale = scale, Bucket = bucket,
            Options = options.ForBackgroundTile(stamps, new RenderStats()), Images = images, Ready = ready, Seed = seed,
            Plan = backgroundPreparation ? tile.Plan : null, Preparing = true,
            Touched = cache.FrameNumber,
        };
        started.Priority.Value = DetailPriority(context, bounds, seed || prefetch);
        if (backgroundPreparation) tile.Plan.Priority = started.Priority;
        context.Jobs.Add(key, started);
        frame.StartedJobs++;
        QueueDetailWorkFromFrame(() => RunDetailRaster(started), options.Stats,
            () => FinishDetailRaster(started, null, false));
        if (options.Stats is { } stats) stats.DetailRastersStarted++;
        return BackgroundTile.Waiting;
    }

    // Visible tiles first, biased by up to half a viewport in the current direction of travel.
    // Prefetch uses the same distance but is always behind visible detail.
    private static double DetailPriority(DetailRasterJobs context, WorldBounds tile, bool seed)
    {
        if (context.View is not { } view) return 0;
        var x = (tile.Left + tile.Right - view.Left - view.Right) / 2 - context.AheadX;
        var y = (tile.Top + tile.Bottom - view.Top - view.Bottom) / 2 - context.AheadY;
        return x * x + y * y + (seed ? 1e18 : 0);
    }

    /// <summary>
    /// The objects of one detail tile onto its 514 px raster (one pixel of bleed): the same evaluator on the frame
    /// thread and on a background worker. With <paramref name="appended"/> only those objects go over the copied base.
    /// </summary>
    private static (int Objects, int Culled) DrawDetailTileObjects(SKCanvas target, MapDocument document, ObjectLayer layer,
        WorldBounds bounds, double scale, int bucket, IReadOnlyList<SceneObject>? appended, ObjectOverviewCacheResult basis,
        ObjectOverviewCache? cache, RenderOptions options, StampBatchScratch scratch)
    {
        var tile = PrepareDetailTile(layer, bounds, scale, appended, basis, options);
        DrawDetailTilePiece(target, document, layer, bounds, scale, bucket, tile, cache, options, scratch, null, null);
        return (tile.Objects, tile.Culled);
    }

    private static PreparedDetailTile PrepareDetailTile(ObjectLayer layer, WorldBounds bounds, double scale,
        IReadOnlyList<SceneObject>? appended, ObjectOverviewCacheResult basis, RenderOptions options)
    {
        var prepareStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var incremental = appended is not null;
        var candidates = appended ?? ObjectSpatialIndex.For(layer).Query(Expand(bounds, 1 / scale));
        if (layer.SublayerStates.Any(s => !s.Visible)) candidates = candidates.Where(o => layer.StateFor(o.Sublayer).Visible).ToArray();
        var drawOrder = SceneGeometry.InDrawOrder(layer, candidates);
        var ordered = StampOcclusion.Cull(drawOrder, Expand(bounds, 1 / scale), scale, options.Stamps);
        var objects = (incremental ? basis.ObjectCount : 0) + ordered.Count(o => o.Visible);
        var culled = (incremental ? basis.CulledCount : 0) + candidates.Count - ordered.Count;
        if (options.Stats is { } preparation)
            preparation.DetailPrepareMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(prepareStarted).TotalMilliseconds;
        return new(drawOrder, ordered, objects, culled);
    }

    /// <summary>
    /// Draws a prepared tile, or with <paramref name="device"/> one part of it: the same draw calls in the same order
    /// under a pixel-exact clip, skipping whole runs (a batch or a single stamp) whose bounds miss the part.
    /// </summary>
    private static void DrawDetailTilePiece(SKCanvas target, MapDocument document, ObjectLayer layer, WorldBounds bounds,
        double scale, int bucket, PreparedDetailTile tile, ObjectOverviewCache? cache, RenderOptions options,
        StampBatchScratch scratch, SKRectI? device, WorldBounds? part)
    {
        if (device is { } clip) target.ClipRect(clip, SKClipOperation.Intersect, false);
        target.Translate(1, 1);
        target.Scale((float)scale);
        target.Translate((float)-bounds.Left, (float)-bounds.Top);
        var rasterStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var ordered = tile.Ordered;
        AreaBeautyRenderer.DrawMasses(target, tile.DrawOrder, options);
        if (cache is null || !TryDrawDenseAtlas(target, ordered, layer.Id, bucket, cache, options, scratch))
        {
            // Two device pixels of filtering reach around every stamp.
            var reach = 2 / scale;
            for (var i = 0; i < ordered.Count; i++)
            {
                if (!ordered[i].Visible) continue;
                if (part is { } area)
                {
                    var run = Math.Max(1, StampBatchLength(target.TotalMatrix, ordered, i, options, out _));
                    var touches = false;
                    for (var k = i; k < i + run && !touches; k++)
                        // A contact shadow or tint reaches past the object's own rectangle: never skipped.
                        touches = ordered[k].Stamp is not { GroundShadow: null, Tint: null }
                            || Expand(SceneGeometry.BoundsOf(ordered[k]), reach).Intersects(area);
                    if (!touches) { i += run - 1; continue; }
                }
                var batch = TryDrawStampBatch(target, ordered, i, options, scratch);
                if (batch > 0) i += batch - 1;
                else DrawObject(target, ordered[i], document.StampForeground, options);
            }
        }
        if (options.Stats is { } raster)
            raster.DetailRasterMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(rasterStarted).TotalMilliseconds;
    }
}
