using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.VisualTree;
using Map.Core.Editing;
using Map.Platform.Windows;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.App.Controls;

/// <summary>
/// The editor canvas: hands its drawing straight to Skia through a custom draw operation - no
/// WebView, no intermediate bitmap. Input: left click selects, left drag moves the selected
/// object (one history entry per drag), middle drag pans, wheel zooms around the cursor.
/// </summary>
public sealed partial class MapCanvasControl : Control
{
    private EditorViewModel? _editor;
    private CanvasInputRouter? _input;
    private Point? _lastPointer;
    private long _sceneRevision;
    private long _completedSceneRevision;

    /// <summary>A diagnostic frame request tied to the input that just changed this canvas.</summary>
    public long RequestFrameForSmoke()
    {
        QueueVisualInvalidation();
        return System.Threading.Interlocked.Read(ref _sceneRevision);
    }

    public long CompletedSceneRevision => System.Threading.Interlocked.Read(ref _completedSceneRevision);
    private sealed record GpuCapture(long Revision, System.Threading.Tasks.TaskCompletionSource<SKBitmap> Completion);
    private GpuCapture? _gpuCapture;

    /// <summary>Reads this canvas from its own native GPU frame; no desktop capture or activation.</summary>
    public async System.Threading.Tasks.Task<SKBitmap> CaptureGpuFrameForSmokeAsync()
    {
        var request = new GpuCapture(System.Threading.Interlocked.Read(ref _sceneRevision) + 1,
            new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously));
        if (System.Threading.Interlocked.CompareExchange(ref _gpuCapture, request, null) is not null)
            throw new InvalidOperationException("A canvas capture is already pending.");
        QueueVisualInvalidation();
        try { return await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally
        {
            System.Threading.Interlocked.CompareExchange(ref _gpuCapture, null, request);
            request.Completion.TrySetCanceled();
        }
    }

    private void CompleteGpuCapture(SKSurface surface, SKCanvas canvas, long revision)
    {
        var request = System.Threading.Volatile.Read(ref _gpuCapture);
        if (request is null || revision < request.Revision) return;
        if (!ReferenceEquals(System.Threading.Interlocked.CompareExchange(ref _gpuCapture, null, request), request)) return;
        var area = canvas.TotalMatrix.MapRect(new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));
        var bitmap = new SKBitmap(new SKImageInfo((int)Math.Ceiling(area.Width), (int)Math.Ceiling(area.Height),
            SKColorType.Rgba8888, SKAlphaType.Premul));
        if (!surface.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, (int)Math.Floor(area.Left), (int)Math.Floor(area.Top)))
        {
            bitmap.Dispose(); request.Completion.TrySetException(new InvalidOperationException("Native canvas readback failed."));
        }
        else if (!request.Completion.TrySetResult(bitmap)) bitmap.Dispose();
    }
    private readonly CanvasRenderResources _renderResources = new();
    private readonly RenderStats _renderStats = new();

    // Native composition and RenderTargetBitmap may use different render threads. Images and
    // frame pins belong to that render context; a snapshot must not poison the window's cache.
    private sealed class CanvasRenderResources
    {
        public object Gate { get; } = new();
        private readonly System.Collections.Generic.Dictionary<int, HexChunkCache> _hexChunks = [];

        /// <summary>Kept hex chunk pictures of the current render thread.</summary>
        public HexChunkCache Hex()
        {
            var thread = Environment.CurrentManagedThreadId;
            if (!_hexChunks.TryGetValue(thread, out var cache)) _hexChunks[thread] = cache = new HexChunkCache();
            return cache;
        }
        private readonly System.Collections.Generic.Dictionary<int, (TileImageCache Tiles, ObjectOverviewCache Objects, CarvedLandCache Land, RenderImageCache Styled, RenderImageCache Terrain, ObjectOverviewCache Atlases, TerrainRefinement Refinement, StrokePreviewCache Preview, LayerImageCache Layers)> _contexts = [];
        public (TileImageCache Tiles, ObjectOverviewCache Objects, CarvedLandCache Land, RenderImageCache Styled, RenderImageCache Terrain, ObjectOverviewCache Atlases, TerrainRefinement Refinement, StrokePreviewCache Preview, LayerImageCache Layers) Current()
        {
            var thread = Environment.CurrentManagedThreadId;
            if (!_contexts.TryGetValue(thread, out var context))
                // Eight finished land pieces and six whole terrains, each a view-sized half-float
                // image (about 10 MiB at 1440×900): enough that returning to a recent view, the
                // settled frame after a zoom, or placing an object does not rebuild the terrain.
                // Six stamp atlases of dense tiles (one per zoom bucket, up to 64 MiB each).
                // Dense object tiles are GPU textures of about 1 MiB: 256 MiB of video memory hold the
                // buckets of a wheel zoom across a whole-map view at 2560×1369 (24-42 tiles each), so
                // the frames of the zoom find a complete kept bucket instead of rebuilding tiles of
                // 50 000 objects in the frame (64 MiB held about one and a half buckets). Under memory
                // pressure the cache still drops to 16 MiB.
                _contexts[thread] = context = (new(4096, 256L * 1024 * 1024), new(1024, 256L * 1024 * 1024), new(), new(8), new(6), new(6, 192L * 1024 * 1024), new(), new(), new());
            return context;
        }
        public (int Tiles, int Objects, long Bytes, long Disposals) Counts()
        {
            lock (Gate)
            {
                int tiles = 0, objects = 0; long bytes = 0, disposals = 0;
                foreach (var context in _contexts.Values)
                { tiles += context.Tiles.Count; objects += context.Objects.Count; bytes += context.Objects.Bytes; disposals += context.Objects.Disposals; }
                return (tiles, objects, bytes, disposals);
            }
        }
        public void Dispose()
        {
            lock (Gate)
            {
                foreach (var hex in _hexChunks.Values) hex.Dispose();
                _hexChunks.Clear();
                foreach (var context in _contexts.Values) { context.Refinement.Dispose(); context.Tiles.Dispose(); context.Objects.Dispose(); context.Land.Dispose(); context.Styled.Dispose(); context.Terrain.Dispose(); context.Atlases.Dispose(); context.Preview.Dispose(); context.Layers.Dispose(); }
                _contexts.Clear();
            }
        }
    }

    public bool IsSpacePanHeld
    {
        get => _input?.SpacePanHeld == true;
        set
        {
            if (_input is not null)
            {
                _input.SpacePanHeld = value;
            }
        }
    }

    public CanvasPerformanceMode PerformanceMode { get; set; } = CanvasPerformanceMode.Balanced;

    /// <summary>
    /// Budget of the GPU texture cache shared by the windows. The library's originals are about
    /// 1.5 K × 0.9 K pixels; at close zoom a screen of different assets needs their full mips,
    /// some 120 MiB for seventeen of them. The former 64 MiB re-uploaded every texture on every
    /// frame there (49 ms per frame measured, 0.1 ms with room); 256 MiB is a small share of any
    /// GPU this program runs on.
    /// </summary>
    private const long GpuResourceCacheBytes = 256L * 1024 * 1024;

    /// <summary>
    /// How long after the last wheel notch or pan step the camera counts as moving. While it
    /// moves, the terrain is drawn from its last finished image, resampled; this timer then asks
    /// for the frame that rebuilds the exact terrain for the settled view.
    /// </summary>
    private static readonly TimeSpan CameraSettle = TimeSpan.FromMilliseconds(120);
    private long _lastCameraMotion = long.MinValue;
    private Avalonia.Threading.DispatcherTimer? _settleTimer;

    /// <summary>The camera moved through the wheel or a pan: mark the motion and schedule the settled frame.</summary>
    private void NoteCameraMotion()
    {
        _lastCameraMotion = Stopwatch.GetTimestamp();
        _settleTimer ??= new Avalonia.Threading.DispatcherTimer(CameraSettle + TimeSpan.FromMilliseconds(15),
            Avalonia.Threading.DispatcherPriority.Render, (_, _) => { _settleTimer!.Stop(); QueueVisualInvalidation(); });
        _settleTimer.Stop(); _settleTimer.Start();
    }

    private bool CameraMoving => _lastCameraMotion != long.MinValue && Stopwatch.GetElapsedTime(_lastCameraMotion) < CameraSettle;

    /// <summary>Diagnostics: bytes held by the shared GPU context's resource cache at the last frame.</summary>
    public static long LastGpuResourceCacheBytes { get; private set; }
    private static int _purgeGpuResources;

    /// <summary>Diagnostics: the next frame frees every GPU resource the context does not need right now.</summary>
    public static void PurgeGpuResourcesOnNextFrame() => System.Threading.Interlocked.Exchange(ref _purgeGpuResources, 1);

    /// <summary>Diagnostic: when MAP_MEASURE_GPU_FLUSH=1 the frame waits for its GPU work and records the wait.</summary>
    private static readonly bool MeasureGpuFlush = Environment.GetEnvironmentVariable("MAP_MEASURE_GPU_FLUSH") == "1";
    public static double LastFrameGpuFlushMilliseconds { get; private set; }

    public double LastPressureSample => _input?.LastPressureSample ?? 1;

    /// <summary>The routing state machine shared by input, rendering and the native smokes.</summary>
    internal CanvasInputRouter? Input => _input;

    /// <summary>Own-process smoke hook: the very router a real pointer event goes through.</summary>
    public CanvasInputRouter InputForSmoke => _input
        ?? throw new InvalidOperationException("The canvas has no editor attached yet.");

    public MapCanvasControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Name = "CanvasControl";
        LayoutUpdated += (_, _) => AnchorTopEdge();
        AutomationProperties.SetAutomationId(this, "MapCanvas");
        AutomationProperties.SetName(this, "Холст карты");
        DetachedFromVisualTree += (_, _) =>
        {
            _input?.Cancel();
            _editor?.CancelPlacement();
            DisposeRenderCaches();
        };
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new MapCanvasAutomationPeer(this);

    private sealed class MapCanvasAutomationPeer(MapCanvasControl owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetClassNameCore() => nameof(MapCanvasControl);
    }

    public event EventHandler? ViewportChanged;

    /// <summary>Raised after a stamp gesture so the library panel can re-read the tool state.</summary>
    public event EventHandler? StampPlaced;

    /// <summary>Raised when Alt+wheel moved the current variant of the selected set.</summary>
    public event EventHandler? VariantCycled;

    /// <summary>
    /// A secondary press the router left alone: it cancelled nothing, so the shell may answer it
    /// with a context menu. The position is read from the event by the subscriber.
    /// </summary>
    public event EventHandler<PointerPressedEventArgs>? SecondaryPressed;

    /// <summary>
    /// Own-process instrumentation hook raised after the Skia scene pass finishes for an Avalonia
    /// frame. It is used by the hidden Stage 13 native profile, never persisted or telemetered.
    /// </summary>
    public event EventHandler? FrameRendered;

    public RenderLodTier LastLodTier { get; private set; } = RenderLodTier.Exact;
    public double LastSceneRenderMilliseconds { get; private set; }

    /// <summary>Own-process smoke hooks: whether the last frame simplified the terrain, and how the finished-land cache fared in it.</summary>
    public bool LastFrameFastTerrain { get; private set; }
    public bool LastTerrainRefinementPending { get; private set; }
    public long LastFrameStyledLandHits { get; private set; }
    public long LastFrameStyledLandMisses { get; private set; }
    public long LastFrameTerrainHits { get; private set; }
    public long LastFrameTerrainMisses { get; private set; }
    public bool LastFrameHadGpuContext { get; private set; }
    public string LastStyledLandMiss { get { lock (_renderResources.Gate) { var current = _renderResources.Current(); return "terrain: " + current.Terrain.LastMiss + "; land: " + current.Styled.LastMiss; } } }
    public long FramesRendered => System.Threading.Interlocked.Read(ref _framesRendered);
    private long _framesRendered;
    public Map.Core.Documents.MapDocument? LastRenderedDocument { get; private set; }
    public int LastFrameObjectsDrawn { get; private set; }
    public int ObjectCacheCount => _renderResources.Counts().Objects;
    public long ObjectCacheBytes => _renderResources.Counts().Bytes;
    public long ObjectCacheDisposals => _renderResources.Counts().Disposals;
    public int TileCacheCount => _renderResources.Counts().Tiles;
    public RenderStats LastRenderStats => _renderStats;

    /// <summary>Own-process native-smoke hook; normal lifetime calls the same path on detach.</summary>
    public void DisposeRenderCaches()
    {
        lock (_renderResources.Gate)
        {
            _renderResources.Dispose();
            // Stamp images belong to this editor, and must outlive its last pinned frame.
            (_editor?.StampImages as IDisposable)?.Dispose();
        }
    }

    /// <summary>Own-process native-smoke pan through the same viewport/event/invalidation path.</summary>
    public void PanByScreenDeltaForSmoke(double dx, double dy)
    {
        if (_editor is null)
        {
            return;
        }

        _editor.Session.Viewport.PanByScreenDelta(dx, dy);
        NoteCameraMotion();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        QueueVisualInvalidation();
    }

    /// <summary>
    /// Own-process smoke zoom through the same viewport/event/invalidation path the wheel takes:
    /// one wheel notch is a factor of 1.15 around the pointer.
    /// </summary>
    public void ZoomAtForSmoke(double screenX, double screenY, double factor)
    {
        if (_editor is null)
        {
            return;
        }

        _editor.Session.Viewport.ZoomAt(screenX, screenY, factor);
        NoteCameraMotion();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        QueueVisualInvalidation();
    }

    /// <summary>Own-process smoke hook through the same snapped preview state as pointer move.</summary>
    public void ShowPlacementPreviewAtCenter()
    {
        if (_input is not null) Apply(_input.Move(Bounds.Width / 2, Bounds.Height / 2));
    }

    /// <summary>Own-process smoke hook through the same snapped preview state as pointer move.</summary>
    public void MovePlacementPointerForSmoke(double screenX, double screenY)
    {
        if (_input is null)
        {
            return;
        }

        Apply(_input.Move(screenX, screenY));
    }

    /// <summary>Own-process smoke hook through the same commit path as a left canvas press.</summary>
    public bool CommitPlacementForSmoke()
    {
        var committed = _editor?.CommitPendingPlacement() == true;
        QueueVisualInvalidation();
        return committed;
    }

    /// <summary>Own-process smoke hook through the same secondary-pointer cancel path as input.</summary>
    public bool CancelPlacementWithSecondaryPointerForSmoke()
    {
        if (_input is null)
        {
            return false;
        }

        var result = _input.Press(CanvasPointerButton.Right, _lastPointer?.X ?? 0, _lastPointer?.Y ?? 0);
        Apply(result);
        return result.Handled;
    }

    /// <summary>
    /// Own-process smoke hook: one complete left press-move-release through the production router,
    /// so a smoke proves the same routing order a real pointer would take.
    /// </summary>
    public void PressMoveReleaseForSmoke(
        double pressScreenX, double pressScreenY,
        double releaseScreenX, double releaseScreenY,
        bool additive = false)
    {
        if (_input is null)
        {
            return;
        }

        Apply(_input.Press(CanvasPointerButton.Left, pressScreenX, pressScreenY, additive));
        Apply(_input.Move(releaseScreenX, releaseScreenY));
        Apply(_input.Release(releaseScreenX, releaseScreenY));
        _lastPointer = new Point(releaseScreenX, releaseScreenY);
    }

    /// <summary>Own-process smoke hook: a click that never moves, i.e. the selection gesture.</summary>
    public void ClickForSmoke(double screenX, double screenY, bool additive = false) =>
        PressMoveReleaseForSmoke(screenX, screenY, screenX, screenY, additive);

    /// <summary>
    /// The three halves of a gesture, separately, so an own-process check can look at the screen
    /// while the pointer is still down. The composite helper above cannot see a live preview at
    /// all - it presses and releases before anything is ever painted.
    /// </summary>
    public void PressForSmoke(double screenX, double screenY, bool additive = false)
    {
        if (_input is null) return;
        Apply(_input.Press(CanvasPointerButton.Left, screenX, screenY, additive));
    }

    public void MoveForSmoke(double screenX, double screenY)
    {
        if (_input is null) return;
        Apply(_input.Move(screenX, screenY));
    }

    public void ReleaseForSmoke(double screenX, double screenY)
    {
        if (_input is null) return;
        Apply(_input.Release(screenX, screenY));
    }

    public void CancelGestureForSmoke()
    {
        _input?.Cancel();
        QueueVisualInvalidation();
    }

    public void PointerLeftForSmoke()
    {
        if (_input is not null) Apply(_input.PointerLeft());
    }

    /// <summary>Applies one routed result to the Avalonia-facing parts of the control.</summary>
    private void Apply(CanvasInputResult result, PointerEventArgs? source = null)
    {
        if (result.CapturePointer)
        {
            source?.Pointer.Capture(this);
        }

        if (result.ReleasePointer)
        {
            source?.Pointer.Capture(null);
        }

        if (result.ViewportChanged)
        {
            NoteCameraMotion();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }

        if (result.StampPlaced)
        {
            StampPlaced?.Invoke(this, EventArgs.Empty);
        }

        if (result.Invalidate)
        {
            QueueVisualInvalidation();
        }

        if (source is not null && result.Handled)
        {
            source.Handled = true;
        }
    }

    public void Attach(EditorViewModel editor)
    {
        var panHeld = IsSpacePanHeld;
        if (_editor is not null)
        {
            _editor.SceneChanged -= OnSceneChanged;
            _editor.SceneInvalidated -= OnSceneInvalidated;
            _editor.CanvasGestureCancelled -= OnCanvasGestureCancelled;
        }

        _editor = editor;
        _input = new CanvasInputRouter(editor) { SpacePanHeld = panHeld };
        _editor.SceneChanged += OnSceneChanged;
        _editor.SceneInvalidated += OnSceneInvalidated;
        _editor.CanvasGestureCancelled += OnCanvasGestureCancelled;
        QueueVisualInvalidation();
    }

    /// <summary>
    /// The editor dropped an unfinished gesture (a tool switch today). The control drops its own
    /// pointer state in the same step, so no stale flag can commit on a later release.
    /// </summary>
    private void OnCanvasGestureCancelled(object? sender, EventArgs e)
    {
        _input?.Cancel();
        Cursor = Cursor.Default;
        QueueVisualInvalidation();
    }

    private void OnSceneChanged(object? sender, EventArgs e) =>
        QueueVisualInvalidation();

    private void OnSceneInvalidated(object? sender, EditorSceneInvalidatedEventArgs e)
    {
        if (e.Has(EditorChangeReason.CanvasInvalidation))
        {
            QueueVisualInvalidation();
        }
    }

    /// <summary>At most one Avalonia invalidation is queued for the next render tick.</summary>
    private void QueueVisualInvalidation()
    {
        System.Threading.Interlocked.Increment(ref _sceneRevision);
        System.Threading.Interlocked.Increment(ref _requestedFrame);
        // Avalonia coalesces dirty visuals. A separate queued flag could swallow an input
        // after Render captured its revision; an extra dispatcher hop added a frame of
        // latency. Register every state change directly, including the last one in a gesture.
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>
    /// The viewport learns its size during layout, not during rendering. It used to be resized
    /// inside <see cref="Render"/>, so a resize could emit editor state from the middle of a
    /// render pass and make the side panel rebuild its rows mid-frame - which showed up as a
    /// briefly empty layer list right after the window changed size.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        if (_editor is not null
            && (Math.Abs(_editor.Session.Viewport.ViewWidth - arranged.Width) > 0.01
                || Math.Abs(_editor.Session.Viewport.ViewHeight - arranged.Height) > 0.01))
        {
            var viewport = _editor.Session.Viewport;
            // Reflowing tool panels must keep the same world point at the centre of the view.
            if (viewport.ViewWidth > 0 && viewport.ViewHeight > 0)
                viewport.PanByScreenDelta((arranged.Width - viewport.ViewWidth) / 2,
                    (arranged.Height - viewport.ViewHeight) / 2);
            viewport.Resize(arranged.Width, arranged.Height);
            _arrangeHeightDelta += arranged.Height - _lastArrangedHeight;
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            QueueVisualInvalidation();
        }
        _lastArrangedHeight = arranged.Height;

        return arranged;
    }

    private double _lastArrangedHeight;
    private double _arrangeHeightDelta;
    private double? _lastScreenTop;

    /// <summary>
    /// When the top edge of the canvas moves (the quick property strip appears, grows to two rows
    /// or hides), the map stays where it is on the screen instead of jumping by half the strip:
    /// the strip covers or uncovers a band at the top. The centre rule above still decides for
    /// every other resize (window size, side panels).
    /// </summary>
    private void AnchorTopEdge()
    {
        if (_editor is null || TopLevel.GetTopLevel(this) is not { } top) return;
        var screenTop = this.TranslatePoint(new Point(0, 0), top)?.Y;
        if (screenTop is not { } y) return;
        var moved = _lastScreenTop is { } previous ? y - previous : 0;
        _lastScreenTop = y;
        var heightDelta = _arrangeHeightDelta;
        _arrangeHeightDelta = 0;
        if (Math.Abs(moved) < 0.5) return;
        // The centre rule already moved the content by heightDelta / 2; complete the shift so
        // the world point under every screen pixel stays put.
        var extra = -moved - heightDelta / 2;
        if (Math.Abs(extra) < 0.01) return;
        _editor.Session.Viewport.PanByScreenDelta(0, extra);
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        QueueVisualInvalidation();
    }

    public override void Render(DrawingContext context)
    {
        if (_editor is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var requestedFrame = System.Threading.Interlocked.Read(ref _requestedFrame);
        var sceneRevision = System.Threading.Interlocked.Read(ref _sceneRevision);
        context.Custom(new SceneDrawOperation(
            new Rect(0, 0, Bounds.Width, Bounds.Height), _editor, _renderResources,
            _renderStats, PerformanceMode,
            _input is { ActiveGesture: not CanvasInputGesture.None and not CanvasInputGesture.Marquee },
            // The terrain is drawn from its kept image only while the view itself moves (a pan,
            // or wheel notches within the last CameraSettle) or the geometry of water or a road
            // is being dragged. A stroke, an armed asset, a selection or an object drag leaves
            // the land and the water exactly as the idle frame draws them.
            _input is { ActiveGesture: CanvasInputGesture.Pan } || CameraMoving
                || _editor.IsDraggingRiver || _editor.IsDraggingRoad,
            () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (TopLevel.GetTopLevel(this) is not null) QueueVisualInvalidation();
            }, Avalonia.Threading.DispatcherPriority.Render),
            (surface, canvas) => CompleteGpuCapture(surface, canvas, sceneRevision),
            ShowFpsBadge ? DrawFpsBadge : null,
            (tier, elapsedMilliseconds, document, fastTerrain, styledHits, styledMisses, terrainHits, terrainMisses, gpu) =>
            {
                LastLodTier = tier;
                LastFrameFastTerrain = fastTerrain;
                LastTerrainRefinementPending = _renderStats.TerrainRefinementsPending > 0;
                LastFrameHadGpuContext = gpu;
                System.Threading.Interlocked.Increment(ref _framesRendered);
                LastFrameStyledLandHits = styledHits;
                LastFrameStyledLandMisses = styledMisses;
                LastFrameTerrainHits = terrainHits;
                LastFrameTerrainMisses = terrainMisses;
                LastSceneRenderMilliseconds = elapsedMilliseconds;
                LastFrameObjectsDrawn = _renderStats.ObjectsDrawn;
                LastRenderedDocument = document;
                System.Threading.Interlocked.Exchange(ref _completedSceneRevision, sceneRevision);
                RecordStatisticsFrame(requestedFrame, elapsedMilliseconds);
                TraceFrame(elapsedMilliseconds, tier, fastTerrain, _renderStats, System.Threading.Interlocked.Read(ref _requestedFrame), requestedFrame);
                FrameRendered?.Invoke(this, EventArgs.Empty);
            },
            BrushCursorPreview()));
    }

    private Point? _hoverPointer;

    /// <summary>The brush footprint under the pointer, when a painting tool is active and the pointer is over the canvas.</summary>
    private Map.Rendering.BrushCursorPreview? BrushCursorPreview()
    {
        if (_editor is null || _hoverPointer is not { } pointer) return null;
        var world = _editor.Session.Viewport.ScreenToWorld(pointer.X, pointer.Y);
        return _editor.BrushCursorAt(world.X, world.Y);
    }

    /// <summary>Remembers the pointer for the brush outline and redraws when it is shown.</summary>
    private void TrackHover(Point? position)
    {
        var shown = BrushCursorPreview() is not null;
        _hoverPointer = position;
        var hexChanged = false;
        if (_editor is not null)
        {
            if (position is { } pointer)
            {
                var world = _editor.Session.Viewport.ScreenToWorld(pointer.X, pointer.Y);
                hexChanged = _editor.UpdateHexHover(world.X, world.Y);
            }
            else hexChanged = _editor.UpdateHexHover(null, null);
        }
        if (shown || hexChanged || BrushCursorPreview() is not null) QueueVisualInvalidation();
    }

    private sealed class SceneDrawOperation(
        Rect bounds, EditorViewModel editor, CanvasRenderResources resources, RenderStats renderStats,
        CanvasPerformanceMode performanceMode, bool fastPreview, bool fastTerrain, Action requestRefinedFrame, Action<SKSurface, SKCanvas> captureFrame,
        Action<SKCanvas, float, float>? drawBadge,
        Action<RenderLodTier, double, Map.Core.Documents.MapDocument, bool, long, long, long, long, bool> frameRendered,
        Map.Rendering.BrushCursorPreview? brushCursor) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;

        public bool HitTest(Point p) => Bounds.Contains(p);

        // Every frame carries fresh viewport/document state; never reuse a previous frame.
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null)
            {
                return;
            }

            lock (resources.Gate) RenderFrame(feature);
        }

        private void RenderFrame(ISkiaSharpApiLeaseFeature feature)
        {
            var (tileCache, objectCache, carvedLandCache, styledLandCache, terrainCache, atlasCache, refinement, previewCache, layerImages) = resources.Current();
            tileCache.BeginFrame();
            var overviewFrame = false;
            try
            {
                objectCache.BeginFrame(); atlasCache.BeginFrame();
                overviewFrame = true;
                using var lease = feature.Lease();
                // One Avalonia GPU context is shared by the windows. Bound its reusable texture
                // cache; active frame resources remain owned by the borrowed lease/driver.
                if (lease.GrContext is { } gpu && gpu.GetResourceCacheLimit() != GpuResourceCacheBytes)
                    gpu.SetResourceCacheLimit(GpuResourceCacheBytes);
                if (lease.GrContext is { } diagnosed)
                {
                    diagnosed.GetResourceCacheUsage(out _, out var gpuBytes);
                    LastGpuResourceCacheBytes = gpuBytes;
                    if (System.Threading.Interlocked.Exchange(ref _purgeGpuResources, 0) == 1) diagnosed.PurgeResources();
                }
                var canvas = lease.SkCanvas;
                var viewport = editor.Session.Viewport;
                var (X, Y) = viewport.ScreenToWorld(0, 0);
                var bottomRight = viewport.ScreenToWorld(Bounds.Width, Bounds.Height);

            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));
            // The parent paints the approved watercolor surround. Clearing this leased surface
            // would cover that real bitmap with the editor's former near-black background.

            canvas.Scale((float)viewport.Scale);
            canvas.Translate((float)-viewport.OffsetX, (float)-viewport.OffsetY);

            // A drag stays a bounded render overlay over the immutable session revision. Canvas
            // resize preview is a different workflow and still supplies its explicit document.
            var renderDocument = editor.DragPreview is null
                ? editor.RenderDocument
                : editor.Session.Document;
            var hasLiveObjectPreview = editor.DragPreview is not null || editor.HasPendingPlacement || editor.HasPathGesture
                || editor.HasRiverGesture || editor.HasRoadGesture || editor.IsDraggingRoads;
            var tier = performanceMode == CanvasPerformanceMode.Performance && viewport.Scale < 0.25
                ? RenderLodTier.Overview
                : viewport.Scale < 0.125
                ? RenderLodTier.Overview
                : performanceMode != CanvasPerformanceMode.Quality
                    && (fastPreview || hasLiveObjectPreview || editor.StrokePreview is not null)
                    ? RenderLodTier.Interactive
                    : RenderLodTier.Exact;
            var sceneStopwatch = Stopwatch.StartNew();
            var styledHitsBefore = styledLandCache.Hits;
            var styledMissesBefore = styledLandCache.Misses;
            var terrainHitsBefore = terrainCache.Hits;
            var terrainMissesBefore = terrainCache.Misses;
            SceneRenderer.Render(canvas, renderDocument, new Map.Rendering.RenderOptions
            {
                GpuContext = lease.GrContext,
                SelectedObjectId = editor.CanvasWorkflowOpen ? null : editor.Session.SelectedObjectId,
                CoSelectedObjectIds = !editor.CanvasWorkflowOpen && editor.Session.SelectedObjectIds.Count > 1
                    ? editor.Session.SelectedObjectIds
                    : null,
                SelectionFrame = editor.CanvasWorkflowOpen ? null : editor.SelectionFrame,
                // The land region drag borrows the marquee outline: it is the same idea on
                // screen - "this is the area the next action covers".
                Marquee = editor.CanvasWorkflowOpen
                    ? null
                    : editor.MarqueeWorld ?? editor.LandShapePreview,
                BrushCursor = brushCursor,
                HexChunks = resources.Hex(),
                HexCursor = editor.CanvasWorkflowOpen ? null : editor.HexCursor,
                ViewScale = viewport.Scale,
                VisibleWorld = new Map.Core.Documents.WorldBounds(
                    X, Y, bottomRight.X, bottomRight.Y),
                ObjectOverrides = editor.EditableAreaPreviewOverrides ?? editor.EditablePathPreviewOverrides ?? editor.DragPreview?.Proposed,
                RiverOverrides = editor.RiverRenderOverrides,
                RiverPreviewLayerId = editor.RiverPreviewLayerId,
                RoadOverrides = editor.RoadRenderOverrides,
                RoadPreviewLayerId = editor.RoadPreviewLayerId,
                PlacementPreviewObjects = editor.PlacementPreviewObjects,
                PlacementPreviewLayerId = editor.PlacementPreviewLayerId,
                PlacementArea = editor.PendingPlacement is
                    { PointerVisible: true, Target: PlacementTargetKind.Stamp,
                      Mode: StampPlacementMode.Area } placement
                    ? new Map.Core.Documents.WorldBounds(
                        placement.WorldX - editor.StampAreaRadius,
                        placement.WorldY - editor.StampAreaRadius,
                        placement.WorldX + editor.StampAreaRadius,
                        placement.WorldY + editor.StampAreaRadius)
                    : null,
                PlacementInvalid = editor.PendingPlacement?.InvalidReason is not null,
                TileCache = tileCache,
                ObjectCache = objectCache,
                AtlasCache = atlasCache,
                CarvedLand = carvedLandCache,
                StyledLand = styledLandCache,
                Terrain = terrainCache,
                LayerImages = layerImages,
                TerrainRefinement = lease.GrContext is not null ? refinement : null,
                RequestRefinedFrame = requestRefinedFrame,
                LodTier = tier,
                Preview = editor.StrokePreview,
                PreviewCache = previewCache,
                MaskEditLayerId = editor.MaskEditLayerId,
                Stamps = editor.StampImages,
                FastPreview = editor.StrokePreview is not { IsLandPaint: true }
                    && (fastPreview || hasLiveObjectPreview || editor.StrokePreview is not null || editor.HasPendingPlacement),
                FastTerrain = fastTerrain,
                Stats = renderStats,
                BoundaryOverlay = editor.CanvasBoundaryOverlay,
            });
            sceneStopwatch.Stop();
            if (MeasureGpuFlush && lease.GrContext is { } flushContext)
            {
                // Diagnostics only: complete this frame's GPU work here so its cost is visible per frame.
                var flushStarted = Stopwatch.GetTimestamp();
                flushContext.Flush(); flushContext.Submit(synchronous: true);
                LastFrameGpuFlushMilliseconds = Stopwatch.GetElapsedTime(flushStarted).TotalMilliseconds;
            }
            if (editor.ActiveTool == EditorTool.Stamp && editor.DisplayedEditableArea is { } editableArea)
            {
                var scale = (float)Math.Max(.001, viewport.Scale);
                using var pen = new SKPaint { Color = new SKColor(80, 216, 167), IsAntialias = true,
                    Style = SKPaintStyle.Stroke, StrokeWidth = 2 / scale };
                using var fill = new SKPaint { Color = new SKColor(16, 55, 47), IsAntialias = true };
                for (var n = 0; n < editableArea.Nodes.Length; n++)
                {
                    var node = editableArea.Nodes[n];
                    if (n > 0 || editableArea.Nodes.Length >= 3)
                    {
                        var previous = editableArea.Nodes[(n + editableArea.Nodes.Length - 1) % editableArea.Nodes.Length];
                        canvas.DrawLine((float)previous.X, (float)previous.Y, (float)node.X, (float)node.Y, pen);
                    }
                    var radius = (n == editor.SelectedAreaNodeIndex ? 7 : 5) / scale;
                    canvas.DrawCircle((float)node.X, (float)node.Y, radius, fill);
                    canvas.DrawCircle((float)node.X, (float)node.Y, radius, pen);
                }
            }
            if (editor.LandSelectionFrame is { } landFrame)
            {
                var scale = (float)Math.Max(.001, viewport.Scale);
                using var landPen = new SKPaint { Color = new SKColor(160, 200, 90, 200), IsAntialias = true,
                    Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f / scale,
                    PathEffect = SKPathEffect.CreateDash([8 / scale, 4 / scale], 0) };
                canvas.DrawRect(new SKRect((float)landFrame.Left, (float)landFrame.Top,
                    (float)landFrame.Right, (float)landFrame.Bottom), landPen);
            }
            if (editor.WaterSelectionFrame is { } waterFrame && !editor.IsCreatingRiver)
            {
                var scale = (float)Math.Max(.001, viewport.Scale);
                using var framePen = new SKPaint { Color = new SKColor(90, 200, 255, 190), IsAntialias = true,
                    Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f / scale,
                    PathEffect = SKPathEffect.CreateDash([6 / scale, 4 / scale], 0) };
                canvas.DrawRect(new SKRect((float)waterFrame.Left, (float)waterFrame.Top,
                    (float)waterFrame.Right, (float)waterFrame.Bottom), framePen);
            }
            if (editor.DisplayedRiver is { } river
                && (editor.ActiveTool == EditorTool.River || editor.IsWaterNodeMode))
            {
                DrawRiverHandles(canvas, editor, river, (float)Math.Max(.001, viewport.Scale));
            }
            if (editor.RoadSelectionFrame is { } roadFrame && !editor.IsCreatingRoad)
            {
                var scale = (float)Math.Max(.001, viewport.Scale);
                using var framePen = new SKPaint { Color = new SKColor(236, 178, 84, 200), IsAntialias = true,
                    Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f / scale,
                    PathEffect = SKPathEffect.CreateDash([6 / scale, 4 / scale], 0) };
                canvas.DrawRect(new SKRect((float)roadFrame.Left, (float)roadFrame.Top,
                    (float)roadFrame.Right, (float)roadFrame.Bottom), framePen);
            }
            if (editor.DisplayedRoad is { } road
                && (editor.ActiveTool == EditorTool.Road || editor.IsRoadNodeMode))
            {
                DrawRoadHandles(canvas, editor, road, (float)Math.Max(.001, viewport.Scale));
            }
            if (editor.ActiveTool == EditorTool.Stamp && editor.DisplayedEditablePath is { } editablePath)
            {
                var scale = (float)Math.Max(.001, viewport.Scale);
                using var pathPen = new SKPaint { Color = new SKColor(90, 200, 255), IsAntialias = true,
                    Style = SKPaintStyle.Stroke, StrokeWidth = 2 / scale };
                using var nodePaint = new SKPaint { Color = new SKColor(22, 55, 78), IsAntialias = true };
                for (var n = 0; n < editablePath.Nodes.Length; n++)
                {
                    var node = editablePath.Nodes[n];
                    if (n > 0)
                    {
                        var previous = editablePath.Nodes[n - 1];
                        canvas.DrawLine((float)previous.X, (float)previous.Y, (float)node.X, (float)node.Y, pathPen);
                    }
                    var radius = (n == editor.SelectedPathNodeIndex ? 7 : 5) / scale;
                    canvas.DrawCircle((float)node.X, (float)node.Y, radius, nodePaint);
                    canvas.DrawCircle((float)node.X, (float)node.Y, radius, pathPen);
                }
            }

            canvas.Restore();
            if (lease.GrContext is not null && lease.SkSurface is { } nativeSurface) captureFrame(nativeSurface, canvas);
            // After the capture: the smokes compare map pixels, and the readout is not the map.
            drawBadge?.Invoke(canvas, (float)Bounds.Width, (float)Bounds.Height);
            frameRendered(tier, sceneStopwatch.Elapsed.TotalMilliseconds, renderDocument, fastTerrain,
                    styledLandCache.Hits + terrainCache.Hits - styledHitsBefore - terrainHitsBefore,
                    styledLandCache.Misses + terrainCache.Misses - styledMissesBefore - terrainMissesBefore,
                    terrainCache.Hits - terrainHitsBefore, terrainCache.Misses - terrainMissesBefore, lease.GrContext is not null);
            }
            finally
            {
                tileCache.EndFrame();
                if (overviewFrame) { objectCache.EndFrame(); atlasCache.EndFrame(); }
            }
        }

        /// <summary>
        /// Editor chrome of the river tool: faint centrelines, one circle per node, a larger ring
        /// on the selected channel and a diamond on the mouth of a tributary. Drawn after the
        /// scene, never by the exporter.
        /// </summary>
        private static void DrawRiverHandles(SKCanvas canvas, EditorViewModel editor, Map.Core.Documents.River river, float scale)
        {
            var geometry = Map.Core.Editing.RiverGeometry.Evaluate(river);
            using var centre = new SKPaint { Color = new SKColor(255, 255, 255, 150), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 / scale };
            using var pen = new SKPaint { Color = new SKColor(90, 200, 255), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 / scale };
            using var faint = new SKPaint { Color = new SKColor(90, 200, 255, 140), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f / scale };
            using var nodePaint = new SKPaint { Color = new SKColor(22, 55, 78), IsAntialias = true };
            using var mouthPaint = new SKPaint { Color = new SKColor(255, 196, 64), IsAntialias = true };
            using var sourcePaint = new SKPaint { Color = new SKColor(120, 230, 150), IsAntialias = true };
            var selectedChannel = editor.DisplayedRiverChannelId;
            void Diamond(Map.Core.Documents.VectorPoint at, bool active, SKPaint fill)
            {
                var size = (active ? 8 : 6) / scale;
                using var diamond = new SKPath();
                diamond.MoveTo((float)at.X, (float)(at.Y - size)); diamond.LineTo((float)(at.X + size), (float)at.Y);
                diamond.LineTo((float)at.X, (float)(at.Y + size)); diamond.LineTo((float)(at.X - size), (float)at.Y); diamond.Close();
                canvas.DrawPath(diamond, fill);
                canvas.DrawPath(diamond, active ? pen : faint);
            }
            foreach (var channel in river.Channels)
            {
                if (!geometry.Channels.TryGetValue(channel.Id, out var g)) continue;
                using var path = new SKPath();
                for (var i = 0; i < g.Samples.Length; i++)
                {
                    var s = g.Samples[i];
                    if (i == 0) path.MoveTo((float)s.X, (float)s.Y); else path.LineTo((float)s.X, (float)s.Y);
                }
                canvas.DrawPath(path, centre);
                var active = channel.Id == selectedChannel;
                for (var n = 0; n < channel.Nodes.Length; n++)
                {
                    var node = channel.Nodes[n];
                    var radius = (active && n == editor.SelectedRiverNodeIndex ? 7 : active ? 5 : 4) / scale;
                    if (channel.Closed)
                    {
                        var half = radius;
                        canvas.DrawRect((float)(node.X - half), (float)(node.Y - half), (float)(half * 2), (float)(half * 2), nodePaint);
                        canvas.DrawRect((float)(node.X - half), (float)(node.Y - half), (float)(half * 2), (float)(half * 2), active ? pen : faint);
                        continue;
                    }
                    canvas.DrawCircle((float)node.X, (float)node.Y, radius, nodePaint);
                    canvas.DrawCircle((float)node.X, (float)node.Y, radius, active ? pen : faint);
                }
                if (channel.Mouth is not null) Diamond(g.End, active, mouthPaint);
                if (channel.Source is not null) Diamond(g.Start, active, sourcePaint);
            }
        }

        /// <summary>
        /// Editor chrome of the road tool: faint axes, a circle per vertex, a larger ring on the
        /// selected branch and a warm square on every shared node - the place where branches meet.
        /// Drawn after the scene, never by the exporter.
        /// </summary>
        private static void DrawRoadHandles(SKCanvas canvas, EditorViewModel editor, Map.Core.Documents.Road road, float scale)
        {
            var geometry = Map.Core.Editing.RoadGeometry.Evaluate(road);
            using var centre = new SKPaint { Color = new SKColor(255, 255, 255, 150), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 / scale };
            using var pen = new SKPaint { Color = new SKColor(236, 178, 84), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 / scale };
            using var faint = new SKPaint { Color = new SKColor(236, 178, 84, 140), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f / scale };
            using var nodePaint = new SKPaint { Color = new SKColor(78, 52, 22), IsAntialias = true };
            using var sharedPaint = new SKPaint { Color = new SKColor(255, 224, 150), IsAntialias = true };
            var selectedBranch = editor.DisplayedRoadBranchId;
            foreach (var branch in road.Branches)
            {
                if (!geometry.Branches.TryGetValue(branch.Id, out var g)) continue;
                using var path = new SKPath();
                for (var i = 0; i < g.Samples.Length; i++)
                {
                    var s = g.Samples[i];
                    if (i == 0) path.MoveTo((float)s.X, (float)s.Y); else path.LineTo((float)s.X, (float)s.Y);
                }
                canvas.DrawPath(path, centre);
            }
            foreach (var branch in road.Branches)
            {
                var active = branch.Id == selectedBranch;
                for (var n = 0; n < branch.Vertices.Length; n++)
                {
                    var node = road.FindNode(branch.Vertices[n].NodeId);
                    if (node is null) continue;
                    var radius = (active && n == editor.SelectedRoadVertexIndex ? 7 : active ? 5 : 4) / scale;
                    if (road.IsShared(node.Id))
                    {
                        var half = radius * 1.05f;
                        canvas.DrawRect((float)(node.X - half), (float)(node.Y - half), half * 2, half * 2, sharedPaint);
                        canvas.DrawRect((float)(node.X - half), (float)(node.Y - half), half * 2, half * 2, active ? pen : faint);
                        continue;
                    }
                    canvas.DrawCircle((float)node.X, (float)node.Y, radius, nodePaint);
                    canvas.DrawCircle((float)node.X, (float)node.Y, radius, active ? pen : faint);
                }
            }
        }

        public void Dispose()
        {
        }
    }

    // ---------------------------------------------------------------- input

    // The control no longer decides what a press means. It translates Avalonia input into router
    // calls and applies the routed result: capture, cursor, invalidation and events. The routing
    // order itself lives in CanvasInputRouter so it can be regression-tested without a window.

    private static CanvasPointerButton ButtonOf(PointerPoint point)
    {
        if (point.Properties.IsMiddleButtonPressed) return CanvasPointerButton.Middle;
        if (point.Properties.IsRightButtonPressed) return CanvasPointerButton.Right;
        return point.Properties.IsLeftButtonPressed ? CanvasPointerButton.Left : CanvasPointerButton.None;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_input is null)
        {
            return;
        }

        Focus();
        var position = e.GetPosition(this);
        var point = e.GetCurrentPoint(this);
        _lastPointer = position;
        ApplyDrawModifiers(e.KeyModifiers);

        var button = ButtonOf(point);
        Apply(
            _input.Press(
                button,
                position.X,
                position.Y,
                e.KeyModifiers.HasFlag(KeyModifiers.Control),
                point.Properties.Pressure),
            e);
        if (button == CanvasPointerButton.Right && !e.Handled)
        {
            SecondaryPressed?.Invoke(this, e);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_editor is null || _input is null)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var position = e.GetPosition(this);
        ApplyDrawModifiers(e.KeyModifiers);
        Apply(_input.Move(position.X, position.Y, e.GetCurrentPoint(this).Properties.Pressure), e);
        _lastPointer = position;
        SyncCursor(position);
        TrackHover(position);
        if (_input.ActiveGesture != CanvasInputGesture.None || _editor.HasPendingPlacement) TraceInput("move", started);
    }

    /// <summary>
    /// The two drawing modifiers, read on every pointer event rather than tracked as key state:
    /// what matters is what is held at the moment the shape is being sized, and a missed KeyUp
    /// must never leave the tool stuck constraining every later gesture.
    /// </summary>
    private void ApplyDrawModifiers(KeyModifiers modifiers)
    {
        if (_editor is null)
        {
            return;
        }

        _editor.VectorConstrainProportions = modifiers.HasFlag(KeyModifiers.Shift);
        _editor.VectorDrawFromCenter = modifiers.HasFlag(KeyModifiers.Alt);
    }

    /// <summary>Pointer feedback: the gizmo cursors win, then the gesture cursor, then default.</summary>
    private void SyncCursor(Point position)
    {
        if (_editor is null || _input is null)
        {
            return;
        }

        if (_input.IsStroking || _editor.CanvasWorkflowOpen)
        {
            return;
        }

        switch (_input.ActiveGesture)
        {
            case CanvasInputGesture.VectorDraw:
            case CanvasInputGesture.TextPlacement:
                Cursor = new Cursor(StandardCursorType.Cross);
                return;
            case CanvasInputGesture.VectorElementDrag:
                Cursor = new Cursor(StandardCursorType.Hand);
                return;
        }

        if (_editor.HasPendingPlacement && _input.ActiveGesture == CanvasInputGesture.None)
        {
            Cursor = new Cursor(StandardCursorType.Cross);
            return;
        }

        var (worldX, worldY) = _editor.Session.Viewport.ScreenToWorld(position.X, position.Y);
        Cursor = _editor.HitHandle(worldX, worldY, _editor.Session.Viewport.Scale) switch
        {
            HandleKind.Rotate => new Cursor(StandardCursorType.Hand),
            HandleKind.TopLeft => new Cursor(StandardCursorType.TopLeftCorner),
            HandleKind.TopRight => new Cursor(StandardCursorType.TopRightCorner),
            HandleKind.BottomLeft => new Cursor(StandardCursorType.BottomLeftCorner),
            HandleKind.BottomRight => new Cursor(StandardCursorType.BottomRightCorner),
            HandleKind.Top => new Cursor(StandardCursorType.TopSide),
            HandleKind.Bottom => new Cursor(StandardCursorType.BottomSide),
            HandleKind.Left => new Cursor(StandardCursorType.LeftSide),
            HandleKind.Right => new Cursor(StandardCursorType.RightSide),
            _ => Cursor.Default,
        };
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_input is not null)
        {
            Apply(_input.PointerLeft());
        }
        TrackHover(null);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_input is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        Apply(_input.Release(position.X, position.Y), e);
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
        _lastPointer = position;
    }

    /// <summary>
    /// Capture can be revoked without a release - a modal dialog, an Alt+Tab, a device change.
    /// Without this the control kept believing a drag was running and committed it later.
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_input?.Cancel() == true)
        {
            Cursor = Cursor.Default;
            QueueVisualInvalidation();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var started = Stopwatch.GetTimestamp();
        if (HandleWheel(e.GetPosition(this), e.Delta.Y, e.KeyModifiers)) e.Handled = true;
        TraceInput("wheel", started);
    }

    /// <summary>Own-process smoke hook: the same wheel path a real mouse takes, with its modifiers.</summary>
    public bool WheelForSmoke(double screenX, double screenY, double deltaY, KeyModifiers modifiers) =>
        HandleWheel(new Point(screenX, screenY), deltaY, modifiers);

    private bool HandleWheel(Point position, double deltaY, KeyModifiers modifiers)
    {
        if (_editor is null)
        {
            return false;
        }

        var direction = Math.Sign(deltaY);

        if (_editor.CanvasWorkflowOpen)
        {
            _editor.Session.Viewport.ZoomAt(position.X, position.Y, Math.Pow(1.15, deltaY));
            NoteCameraMotion();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            QueueVisualInvalidation();
            return true;
        }

        // Alt+wheel walks through the variants of the current set and on into the next set of the
        // library, but only inside the stamp tool with a set chosen; everywhere else the wheel
        // keeps its existing meaning.
        if (modifiers.HasFlag(KeyModifiers.Alt)
            && !modifiers.HasFlag(KeyModifiers.Control)
            && !modifiers.HasFlag(KeyModifiers.Shift)
            && _editor.ActiveTool == EditorTool.Stamp
            && _editor.SelectedSet is not null)
        {
            if (direction != 0) _editor.StepAssetSet(direction);

            VariantCycled?.Invoke(this, EventArgs.Empty);
            QueueVisualInvalidation();
            return true;
        }

        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            _editor.RotateSelectedObject(direction,
                fineStep: modifiers.HasFlag(KeyModifiers.Alt));
        }
        else if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            _editor.ResizeSelectedObject(direction, largerStep: true);
        }
        else
        {
            _editor.Session.Viewport.ZoomAt(position.X, position.Y, Math.Pow(1.15, deltaY));
            NoteCameraMotion();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }

        QueueVisualInvalidation();
        return true;
    }

    /// <summary>Width covered by floating chrome at the canvas's left edge, in screen pixels.</summary>
    public Func<double>? FitInsetLeft { get; set; }

    public void FitToCanvas()
    {
        if (_editor is null)
        {
            return;
        }

        // Floating cards (rail and tool panel) stand over the left of the canvas: the map is fitted
        // into the free part beside them, so «Вписать» never tucks its left edge under a panel.
        var inset = Math.Clamp(FitInsetLeft?.Invoke() ?? 0, 0, Bounds.Width * 0.5);
        _editor.Session.Viewport.Resize(Bounds.Width - inset, Bounds.Height);
        _editor.Session.Viewport.FitTo(
            _editor.Session.Document.Canvas.Width,
            _editor.Session.Document.Canvas.Height);
        _editor.Session.Viewport.Resize(Bounds.Width, Bounds.Height);
        _editor.Session.Viewport.PanByScreenDelta(inset, 0);
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        QueueVisualInvalidation();
    }
}
