using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

public static partial class SceneRenderer
{
    /// <summary>
    /// One editor's latest visible layer revisions and a single worker. The queue has at most
    /// 64 requests and 16 raster handoffs. No GPU objects or render caches cross to the worker.
    /// Plans, palettes and variants are immutable snapshots; only their private memo tables change.
    /// </summary>
    public sealed class OverviewPreparation : IDisposable
    {
        private const int QueueLimit = 64;
        private const int HandoffLimit = 16;
        private readonly object _gate = new();
        private readonly Dictionary<LayerId, LayerWork> _layers = [];
        private readonly LinkedList<Work> _queue = [];
        private readonly HashSet<Work> _queued = [];
        internal sealed record Raster(OverviewVariant Variant, SKImage Image);
        private readonly Dictionary<Work, Raster> _ready = [];
        // One below-normal thread of its own (not the thread pool the UI's continuations share). It
        // waits for work and leaves after IdleExitMilliseconds without any; _busy is "work in hand".
        private const int IdleExitMilliseconds = 2000;
        private Thread? _thread;
        private bool _busy;
        private bool _disposed;
        private readonly Func<bool> _interaction;
        private bool _frameMoving;
        private long _openedAt;

        /// <summary>Neighbour prefetch starts no sooner than this after the first layer of a map arrived.</summary>
        public double PrefetchAfterOpenMilliseconds { get; set; } = 1000;
        private long _startedDuringGesture, _prefetchDeferred, _gestureWaits;
        private long _planBytes, _paletteBytes, _variantBytes, _rasterBytes, _prefetchBytes;

        /// <summary>Managed bytes the worker allocated per kind of job (raster pixels are native and not counted).</summary>
        public (long Plan, long Palette, long Variants, long Rasters, long Prefetch) AllocatedBytes
        {
            get { lock (_gate) return (_planBytes, _paletteBytes, _variantBytes, _rasterBytes, _prefetchBytes); }
        }

        // Worker thread, never under _gate: a cold plan waits here while the user pans or zooms.
        private void WaitWhileGesture(CancellationToken token)
        {
            if (!YieldDuringGesture || !_interaction()) return;
            lock (_gate) _gestureWaits++;
            while (YieldDuringGesture && _interaction() && !token.IsCancellationRequested && !_disposed) Thread.Sleep(10);
        }

        /// <param name="interaction">Whether the user is panning, zooming or dragging right now; by default
        /// <see cref="Map.Core.Resources.BackgroundWork.InteractionActive"/>, which the canvas feeds.</param>
        public OverviewPreparation(Func<bool>? interaction = null) =>
            _interaction = interaction ?? (static () => Map.Core.Resources.BackgroundWork.InteractionActive);

        /// <summary>
        /// While a gesture runs, neighbour-bucket prefetch waits (visible chunks are still prepared).
        /// False restores the previous behaviour, for comparisons.
        /// </summary>
        public bool YieldDuringGesture { get; set; } = true;

        // Under _gate.
        private bool GestureActive => YieldDuringGesture && (_frameMoving || _interaction());
        private long _plansBuilt;
        private long _cancelled;
        private long _prefetched;
        private double _planMilliseconds, _paletteMilliseconds, _variantMilliseconds, _rasterMilliseconds;
        private Exception? _lastError;

        private sealed class LayerWork : IDisposable
        {
            public readonly ObjectLayer Layer;
            public readonly IReadOnlyList<MapAssetReference> Assets;
            public readonly IStampImageSource? Source;
            public readonly Action Ready;
            public readonly CancellationTokenSource Stop = new();
            public readonly CancellationToken Token;
            public CancellationTokenSource ViewStop = new();
            public Snapshot? Snapshot;
            public View? View;
            public Timer? Prefetch;
            public (OverviewChunk Chunk, OverviewPalette Palette, int Zoom)[] Neighbours = [];
            public int NeighbourCursor;
            public long FailedAt;

            public LayerWork(ObjectLayer layer, IReadOnlyList<MapAssetReference> assets,
                IStampImageSource? source, Action ready, Snapshot? previous)
            {
                Layer = layer; Assets = assets; Source = source; Ready = ready; Snapshot = previous;
                Token = Stop.Token;
            }

            public void ChangeView()
            {
                ViewStop.Cancel(); ViewStop.Dispose(); ViewStop = new();
                Neighbours = []; NeighbourCursor = 0;
            }

            public void ScheduleNeighbours(TimerCallback callback)
            {
                Prefetch?.Dispose();
                Prefetch = new Timer(callback, null, 150, Timeout.Infinite);
            }

            public void Dispose()
            {
                Stop.Cancel(); ViewStop.Cancel(); Prefetch?.Dispose();
                Neighbours = [];
                Stop.Dispose(); ViewStop.Dispose();
            }
        }

        // Incomplete + a null colour revision: a legacy source may still resolve images; it is
        // re-sampled at most every LegacyRetryMs while frames ask, never in a loop of its own.
        internal sealed record Snapshot(ObjectLayer Layer, ObjectOverviewPlan Plan, OverviewPalette Palette,
            long? ColorRevision, long SourceRevision, long MissingRevision, bool ColorsPending,
            bool Incomplete = false, long PreparedAt = 0);
        private const double LegacyRetryMs = 250;
        private sealed record View(WorldBounds Bounds, int Zoom, StampForegroundSurface? Foreground);
        private sealed record Work(LayerWork Layer, OverviewChunk? Chunk = null, OverviewPalette? Palette = null,
            int Zoom = 0, StampForegroundSurface? Foreground = null, bool Prefetch = false,
            CancellationToken ViewToken = default);

        /// <summary>Call on document open/edit/visibility changes, before requesting the next frame.</summary>
        public void UpdateDocument(MapDocument document, IStampImageSource? source, Action ready)
        {
            lock (_gate)
            {
                if (_disposed) return;
                // Same layers the renderer can draw through an overview (the detail fallback seed
                // does not check sublayers); otherwise a frame and this call would cancel each other.
                var visible = document.Layers.OfType<ObjectLayer>().Where(l => l.Visible && l.Opacity > 0).ToArray();
                var ids = visible.Select(l => l.Id).ToHashSet();
                foreach (var id in _layers.Keys.Where(id => !ids.Contains(id)).ToArray())
                {
                    Cancel(_layers[id]);
                    _layers.Remove(id);
                }
                foreach (var layer in visible) GetLayer(layer, document.Assets, source, ready);
                StartWorker();
            }
        }

        private LayerWork GetLayer(ObjectLayer layer, IReadOnlyList<MapAssetReference> assets,
            IStampImageSource? source, Action ready)
        {
            _layers.TryGetValue(layer.Id, out var state);
            if (state is null || !ReferenceEquals(state.Layer, layer) || !ReferenceEquals(state.Assets, assets)
                || !ReferenceEquals(state.Source, source))
            {
                var previous = state?.Snapshot;
                if (_layers.Count == 0) _openedAt = Stopwatch.GetTimestamp();
                if (state is not null) Cancel(state);
                state = new(layer, assets, source, ready, previous);
                _layers[layer.Id] = state;
                Enqueue(new(state));
            }
            return state;
        }

        internal Snapshot? Request(MapDocument document, ObjectLayer layer, RenderOptions options, WorldBounds bounds, int zoom,
            bool primaryView = true)
        {
            lock (_gate)
            {
                if (_disposed) return null;
                // The latest frame says whether the view moves; a settled frame releases the prefetch.
                if (primaryView) _frameMoving = options.CameraMoving;
                var state = GetLayer(layer, document.Assets, options.Stamps, options.RequestRefinedFrame ?? (() => { }));
                var view = new View(bounds, zoom, document.StampForeground);
                if (state.View != view)
                {
                    // A pan keeps every raster that is still on screen and the chunk in flight;
                    // only a zoom bucket or foreground change makes the queued work useless.
                    if (state.View is not { } old || old.Zoom != zoom || !ReferenceEquals(old.Foreground, view.Foreground))
                    {
                        state.ChangeView();
                        Discard(state, chunksOnly: true);
                    }
                    else Prune(state, bounds);
                    state.View = view;
                    if (primaryView) ArmPrefetch(state);
                }
                var snapshot = state.Snapshot;
                var stamps = options.Stamps;
                var revision = stamps?.OverviewColorRevision;
                var stale = snapshot is null || !ReferenceEquals(snapshot.Layer, layer)
                    || snapshot.ColorRevision != revision
                    || snapshot.SourceRevision != (stamps?.SourceRevision ?? 0)
                    || snapshot.MissingRevision != (stamps?.MissingRevision ?? 0)
                    // A legacy source has no revision to watch: re-sample a gap now and then.
                    || revision is null && snapshot.Incomplete
                        && Stopwatch.GetElapsedTime(snapshot.PreparedAt).TotalMilliseconds >= LegacyRetryMs;
                if (stale && (state.FailedAt == 0 || Stopwatch.GetElapsedTime(state.FailedAt).TotalMilliseconds >= 1000))
                    Enqueue(new(state));
                StartWorker();
                return stale && snapshot is not null ? snapshot with { ColorsPending = true } : snapshot;
            }
        }

        // Visible work for chunks that left the view is dropped; their ready rasters are freed,
        // otherwise a long pan could fill the bounded handoff with pictures nobody takes.
        private void Prune(LayerWork state, WorldBounds bounds)
        {
            var (minX, maxX) = ((int)Math.Floor(bounds.Left / OverviewChunkSize), (int)Math.Floor(bounds.Right / OverviewChunkSize));
            var (minY, maxY) = ((int)Math.Floor(bounds.Top / OverviewChunkSize), (int)Math.Floor(bounds.Bottom / OverviewChunkSize));
            bool Outside(Work work) => work.Chunk is { } chunk
                && (chunk.X < minX || chunk.X > maxX || chunk.Y < minY || chunk.Y > maxY);
            for (var node = _queue.First; node is not null;)
            {
                var next = node.Next;
                if (ReferenceEquals(node.Value.Layer, state) && !node.Value.Prefetch && Outside(node.Value))
                { _queued.Remove(node.Value); _queue.Remove(node); }
                node = next;
            }
            foreach (var key in _ready.Keys.Where(w => ReferenceEquals(w.Layer, state) && Outside(w)).ToArray())
            { _ready[key].Image.Dispose(); _ready.Remove(key); }
        }

        /// <param name="take">False only requests the chunk: a ready raster stays in the handoff for a later frame.</param>
        internal bool TryTakeOrRequest(ObjectLayer layer, OverviewChunk chunk, OverviewPalette palette,
            int zoom, StampForegroundSurface? foreground, out Raster? raster, bool take = true)
        {
            lock (_gate)
            {
                raster = null;
                if (_disposed || !_layers.TryGetValue(layer.Id, out var state) || !ReferenceEquals(state.Layer, layer)
                    || state.Snapshot is not { } snapshot || !ReferenceEquals(snapshot.Layer, layer)
                    || !ReferenceEquals(snapshot.Palette, palette)) return false;
                var work = new Work(state, chunk, palette, zoom, foreground, ViewToken: state.ViewStop.Token);
                if (!take && _ready.ContainsKey(work)) return false;
                if (_ready.Remove(work, out raster)) { StartWorker(); return true; }
                Enqueue(work);
                StartWorker();
                return false;
            }
        }

        private void Enqueue(Work work)
        {
            if (work.Layer.Token.IsCancellationRequested || _queued.Contains(work)) return;
            if (_queue.Count >= QueueLimit)
            {
                if (work.Prefetch) return;
                var expendable = _queue.Last;
                while (expendable is not null && !expendable.Value.Prefetch) expendable = expendable.Previous;
                if (expendable is null) return;
                _queued.Remove(expendable.Value); _queue.Remove(expendable);
            }
            _queued.Add(work);
            if (work.Prefetch) _queue.AddLast(work);
            else if (work.Chunk is null) _queue.AddFirst(work);
            else
            {
                // New visible work precedes neighbour prefetch, but stays centre-first itself.
                var firstPrefetch = _queue.First;
                while (firstPrefetch is not null && !firstPrefetch.Value.Prefetch) firstPrefetch = firstPrefetch.Next;
                if (firstPrefetch is null) _queue.AddLast(work); else _queue.AddBefore(firstPrefetch, work);
            }
        }

        // Under _gate.
        private void StartWorker()
        {
            if (_disposed || _queue.Count == 0) return;
            if (_busy) { Monitor.PulseAll(_gate); return; } // a worker waiting out a gesture re-checks the queue
            _busy = true;
            if (_thread is not null) { Monitor.PulseAll(_gate); return; }
            _thread = new Thread(Run) { IsBackground = true, Name = "Map overview preparation" };
            _thread.Start();
        }

        private void Run()
        {
            Map.Core.Resources.BackgroundWork.EnterOwnedThread();
            while (true)
            {
                lock (_gate)
                {
                    while (!_busy && !_disposed)
                        if (!Monitor.Wait(_gate, IdleExitMilliseconds) && !_busy) { _thread = null; return; }
                    if (_disposed) { _busy = false; _thread = null; return; }
                }
                Pump();
            }
        }

        private void Pump()
        {
            var waiting = false;
            while (true)
            {
                Work work;
                bool gesture;
                lock (_gate)
                {
                    if (_disposed || _queue.First is null) { _busy = false; return; }
                    work = _queue.First.Value;
                    if (work.Chunk is not null && !work.Prefetch && _ready.Count >= HandoffLimit)
                    { _busy = false; return; }
                    gesture = GestureActive;
                    if (work.Prefetch && gesture)
                    {
                        // Visible work always precedes prefetch, so only prefetch is left: it waits for
                        // the gesture to end. A frame flag alone is cleared by the settled frame, which
                        // restarts the worker; the interaction clock runs out by itself.
                        if (!waiting) { _prefetchDeferred++; waiting = true; }
                        if (!_interaction()) { _busy = false; return; }
                        Monitor.Wait(_gate, 15);
                        continue;
                    }
                    waiting = false;
                    _queue.RemoveFirst();
                    if (gesture) _startedDuringGesture++;
                    // Retain the dedup key until publication: repeated frames cannot queue the running work.
                }
                try
                {
                    work.Layer.Token.ThrowIfCancellationRequested();
                    using var slot = Map.Core.Resources.BackgroundWork.EnterSlot(work.Layer.Token);
                    if (work.Chunk is null) PreparePlan(work.Layer);
                    else PrepareChunk(work);
                }
                catch (OperationCanceledException) { Interlocked.Increment(ref _cancelled); }
                catch (Exception error)
                {
                    // A failing plan is retried by later frames, but not by every frame.
                    lock (_gate) { _lastError = error; if (work.Chunk is null) work.Layer.FailedAt = Stopwatch.GetTimestamp(); }
                    SignalReady(work.Layer);
                }
                finally
                {
                    lock (_gate)
                    {
                        _queued.Remove(work);
                        foreach (var state in _layers.Values) QueueNeighbours(state);
                    }
                }
            }
        }

        private void PreparePlan(LayerWork state)
        {
            var token = state.Token;
            var started = Stopwatch.GetTimestamp();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            if (!OverviewPlans.TryGetValue(state.Layer, out var plan))
            {
                // A cold plan of a million objects allocates its whole chunk directory: it waits
                // while a gesture runs, so its collections do not land in the gesture's frames.
                var built = new ObjectOverviewPlan(state.Layer, token, () => WaitWhileGesture(token));
                token.ThrowIfCancellationRequested();
                plan = OverviewPlans.GetValue(state.Layer, _ => built);
                Interlocked.Increment(ref _plansBuilt);
            }
            var planMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var planBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            allocated = GC.GetAllocatedBytesForCurrentThread();
            var revision = state.Source?.OverviewColorRevision;
            var sourceRevision = state.Source?.SourceRevision ?? 0;
            var missingRevision = state.Source?.MissingRevision ?? 0;
            started = Stopwatch.GetTimestamp();
            var stats = new RenderStats();
            using var samples = state.Source?.Lease();
            var palette = plan.GetPalette(state.Assets, new RenderOptions
            {
                Stamps = state.Source,
                Stats = stats,
                RequestRefinedFrame = () => ColorsReady(state),
            }, samples, token);
            var paletteMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(state.Snapshot?.Palette, palette))
                {
                    state.ChangeView();
                    Discard(state, chunksOnly: true);
                }
                state.Snapshot = new(state.Layer, plan, palette, revision, sourceRevision, missingRevision, stats.DetailTilesPending > 0,
                    palette.Colors.Count < palette.UsedAssets.Count, Stopwatch.GetTimestamp());
                _planMilliseconds += planMs; _paletteMilliseconds += paletteMs;
                _planBytes += planBytes; _paletteBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
                ArmPrefetch(state);
            }
            SignalReady(state);
        }

        private void ColorsReady(LayerWork state)
        {
            lock (_gate)
            {
                if (!_disposed && !state.Token.IsCancellationRequested) { Enqueue(new(state)); StartWorker(); }
            }
            // The catalog keeps one ready callback for colours, sources and mips alike. It may be
            // this closure until the next frame re-arms it, so a cancelled revision still forwards.
            try { state.Ready(); }
            catch (Exception error) { lock (_gate) _lastError = error; }
        }

        private void SignalReady(LayerWork state)
        {
            if (state.Token.IsCancellationRequested) return;
            try { state.Ready(); }
            catch (Exception error) { lock (_gate) _lastError = error; }
        }

        [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created",
            Justification = "The ready handoff owns the raster until the render cache adopts it or Discard disposes it.")]
        private void PrepareChunk(Work work)
        {
            var state = work.Layer;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(state.Token, work.ViewToken);
            var token = stop.Token;
            token.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var revision = work.Chunk!.GetTintRevision(work.Palette!);
            var variant = work.Chunk.GetVariant(work.Zoom, Math.Pow(2, work.Zoom / 4d), work.Palette!.Colors, revision, token);
            token.ThrowIfCancellationRequested();
            var variantMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var variantBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (work.Prefetch)
            {
                lock (_gate) { _variantMilliseconds += variantMs; _prefetched++; _prefetchBytes += variantBytes; }
                return;
            }
            started = Stopwatch.GetTimestamp();
            allocated = GC.GetAllocatedBytesForCurrentThread();
            using var lease = state.Source?.Lease();
            var options = new RenderOptions { Stamps = lease, LodTier = RenderLodTier.Overview, ViewScale = Math.Pow(2, work.Zoom / 4d) };
            var image = variant.BuildImage(work.Foreground, options, token);
            var rasterMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var rasterBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            lock (_gate)
            {
                _variantBytes += variantBytes; _rasterBytes += rasterBytes;
                if (_disposed || token.IsCancellationRequested || !ReferenceEquals(state.Snapshot?.Palette, work.Palette))
                { image.Dispose(); return; }
                _ready.Add(work, new(variant, image));
                _variantMilliseconds += variantMs; _rasterMilliseconds += rasterMs;
            }
            SignalReady(state);
        }

        private void ArmPrefetch(LayerWork state)
        {
            if (state.View is not { } view || state.Snapshot is not { } snapshot || !ReferenceEquals(snapshot.Layer, state.Layer)) return;
            var viewToken = state.ViewStop.Token;
            state.ScheduleNeighbours(_ =>
            {
                if (viewToken.IsCancellationRequested || state.Token.IsCancellationRequested) return;
                // Wheel notches and pan steps must not start neighbour buckets: look again a little
                // later, until a settled view (the same view, if nothing changed it meanwhile).
                lock (_gate)
                {
                    if (_disposed) return;
                    // Nor in the first second after open, while the first view and gesture need the core.
                    if (GestureActive || YieldDuringGesture && _openedAt != 0
                        && Stopwatch.GetElapsedTime(_openedAt).TotalMilliseconds < PrefetchAfterOpenMilliseconds)
                    {
                        _prefetchDeferred++;
                        if (!viewToken.IsCancellationRequested && !state.Token.IsCancellationRequested
                            && ReferenceEquals(state.View, view)) ArmPrefetch(state);
                        return;
                    }
                }
                // The plan is immutable. Enumerating a large layer's chunk directory never holds
                // the gate a frame uses to request/take its next raster.
                var neighbours = snapshot.Plan.Chunks.Where(item =>
                    item.Key.X >= Math.Floor(view.Bounds.Left / OverviewChunkSize) && item.Key.X <= Math.Floor(view.Bounds.Right / OverviewChunkSize)
                    && item.Key.Y >= Math.Floor(view.Bounds.Top / OverviewChunkSize) && item.Key.Y <= Math.Floor(view.Bounds.Bottom / OverviewChunkSize))
                    .SelectMany(item => new[] { (item.Value, snapshot.Palette, view.Zoom - 1), (item.Value, snapshot.Palette, view.Zoom + 1) }).ToArray();
                lock (_gate)
                {
                    if (_disposed || state.Token.IsCancellationRequested || viewToken.IsCancellationRequested) return;
                    state.Neighbours = neighbours; state.NeighbourCursor = 0;
                    QueueNeighbours(state);
                    StartWorker();
                }
            });
        }

        private void QueueNeighbours(LayerWork state)
        {
            if (state.View is not { } view || state.Token.IsCancellationRequested) return;
            while (_queue.Count < QueueLimit && state.NeighbourCursor < state.Neighbours.Length)
            {
                var item = state.Neighbours[state.NeighbourCursor++];
                Enqueue(new(state, item.Chunk, item.Palette, item.Zoom, view.Foreground, true, state.ViewStop.Token));
            }
            if (state.NeighbourCursor == state.Neighbours.Length) { state.Neighbours = []; state.NeighbourCursor = 0; }
        }

        [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP007:Don't dispose injected",
            Justification = "Ready rasters are created and owned by this worker, and have not been transferred to a render cache.")]
        private void Discard(LayerWork state, bool chunksOnly = false)
        {
            for (var node = _queue.First; node is not null;)
            {
                var next = node.Next;
                if (ReferenceEquals(node.Value.Layer, state) && (!chunksOnly || node.Value.Chunk is not null))
                { _queued.Remove(node.Value); _queue.Remove(node); }
                node = next;
            }
            foreach (var key in _ready.Keys.Where(w => ReferenceEquals(w.Layer, state)).ToArray())
            { _ready[key].Image.Dispose(); _ready.Remove(key); }
        }

        private void Cancel(LayerWork state)
        {
            state.Dispose();
            Discard(state);
        }

        public (long Plans, long Cancelled, int Queued, int ReadyImages, bool Idle, long Prefetched, double PlanMs,
            double PaletteMs, double VariantMs, double RasterMs, Exception? Error) Statistics
        {
            get
            {
                lock (_gate) return (_plansBuilt, _cancelled, _queue.Count, _ready.Count, !_busy, _prefetched,
                _planMilliseconds, _paletteMilliseconds, _variantMilliseconds, _rasterMilliseconds, _lastError);
            }
        }

        /// <summary>
        /// Live check of the gesture rule: work items started while a gesture ran (visible chunks only,
        /// never prefetch) and the times neighbour prefetch waited for a settled view.
        /// </summary>
        public (long StartedDuringGesture, long PrefetchDeferred, long PlanWaits, bool GestureActive) GestureStatistics
        {
            get { lock (_gate) return (_startedDuringGesture, _prefetchDeferred, _gestureWaits, GestureActive); }
        }

        /// <summary>Cancels a closed editor without waiting on its worker or touching render-owned images.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                foreach (var state in _layers.Values) Cancel(state);
                _layers.Clear();
            }
        }

        public void Dispose()
        {
            lock (_gate) { Reset(); _disposed = true; Monitor.PulseAll(_gate); }
        }
    }
}
