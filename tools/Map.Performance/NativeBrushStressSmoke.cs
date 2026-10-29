using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Map.App.Views;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using Map.Rendering;

namespace Map.Performance;

/// <summary>One brush configuration and what the real window did with it.</summary>
internal sealed record BrushStressCase(
    string Name,
    double Radius,
    double OpacityPercent,
    double SoftnessPercent,
    double Zoom,
    bool Eraser,
    int PaintedPixels,
    double MeasuredDiameter,
    double ExpectedDiameter,
    int MinAlphaLive,
    int MinAlphaCommitted,
    int TilesAdded,
    string Error,
    bool Passed);

internal sealed record NativeBrushStressReport(
    string Profile,
    DateTimeOffset RecordedUtc,
    IReadOnlyList<BrushStressCase> Cases,
    IReadOnlyList<string> Notes,
    bool GatePassed);

/// <summary>
/// A stress pass over the brush alone, in a real hidden window.
/// <para>
/// Two questions it answers that a unit test cannot. First, does a dab of a given radius actually
/// cover the area it promises, across the whole slider range and at several zoom levels. Second,
/// and the reason this exists: the owner reported the window going see-through while a stroke is
/// held down, so every case is photographed <em>with the pointer still pressed</em> and every
/// pixel's alpha is checked. A hole in the window is a hole in the rendered frame.
/// </para>
/// </summary>
internal static class NativeBrushStressSmoke
{
    public static NativeBrushStressReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The brush stress profile targets Windows.");
        }

        var completion = new TaskCompletionSource<NativeBrushStressReport>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                NativeBrushStressApplication.Configure(options, completion);
                AppBuilder.Configure<NativeBrushStressApplication>()
                    .UsePlatformDetect()
                    .WithInterFont()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Map brush stress",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            return completion.Task.WaitAsync(TimeSpan.FromSeconds(240)).GetAwaiter().GetResult();
        }
        finally
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}

internal sealed class NativeBrushStressApplication : Application
{
    private const int CanvasWidth = 1_024;
    private const int CanvasHeight = 768;

    private static PerformanceOptions? _options;
    private static TaskCompletionSource<NativeBrushStressReport>? _completion;

    internal static void Configure(
        PerformanceOptions options, TaskCompletionSource<NativeBrushStressReport> completion)
    {
        _options = options;
        _completion = completion;
    }

    public override void Initialize()
    {
        var production = new global::Map.App.App();
        production.Initialize();
        RequestedThemeVariant = production.RequestedThemeVariant;
        foreach (var resource in production.Resources) Resources[resource.Key] = resource.Value;
        var styles = production.Styles.ToArray();
        production.Styles.Clear();
        Styles.AddRange(styles);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || _options is null || _completion is null)
        {
            throw new InvalidOperationException("The brush stress profile was not configured.");
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var workspace = Path.Combine(
            Path.GetTempPath(), "map-brush-stress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(workspace, "profile"));

        var window = new Window
        {
            Title = "Map brush stress",
            Width = 1_416,
            Height = 939,
            CanResize = true,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-32_000, -32_000),
        };

        window.Opened += async (_, _) =>
        {
            EditorView? editorView = null;
            try
            {
                var result = await ExecuteAsync(workspace, window, view => editorView = view);
                _completion.TrySetResult(result);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                if (editorView is not null)
                {
                    editorView.CloseAuxiliaryWindowsForSmoke();
                    editorView.Canvas.DisposeRenderCaches();
                }

                window.Close();
                desktop.Shutdown();
                try { Directory.Delete(workspace, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        };
        desktop.MainWindow = window;
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task<NativeBrushStressReport> ExecuteAsync(
        string workspace, Window window, Action<EditorView> showView)
    {
        var notes = new List<string>();
        var cases = new List<BrushStressCase>();

        var mapPath = Path.Combine(workspace, "brush-stress.mapproj");
        var document = Document();
        MapProjectStore.Save(document, mapPath,
            new SaveMetadata("brush-stress", DateTimeOffset.UnixEpoch),
            ThumbnailRenderer.RenderPng(document, 160, 100));
        var opened = MapProjectStore.Open(mapPath);

        var editor = new EditorViewModel(
            opened.Document, mapPath, false, new StressStore(), new StressRecentMaps(),
            new StressFilePicker(), new StressPrompts(), new StressLog(),
            appVersion: "brush-stress", availableFontFamilies: ["Arial"]);
        var view = new EditorView(
            editor, static () => Task.CompletedTask, static () => { }, static () => { });
        showView(view);
        window.Content = view;
        await SettleAsync();
        view.Canvas.FitToCanvas();
        await SettleAsync();

        var brushLayer = editor.Session.Document.Layers.OfType<BrushLayer>().Single();
        editor.SelectLayer(brushLayer.Id);
        editor.ActiveTool = EditorTool.Brush;
        await SettleAsync();

        // One dab per case on a clean layer, so the measured extent belongs to this radius alone.
        foreach (var probe in Cases())
        {
            cases.Add(await RunCaseAsync(view, editor, brushLayer.Id, probe));
        }

        notes.Add($"canvas {CanvasWidth}x{CanvasHeight}, control "
            + $"{view.Canvas.Bounds.Width:F0}x{view.Canvas.Bounds.Height:F0}");
        notes.Add("live = pointer still pressed, committed = after release");

        var gate = cases.All(item => item.Passed);
        return new NativeBrushStressReport(
            "native-brush-stress", DateTimeOffset.UtcNow, cases, notes, gate);
    }

    private sealed record Probe(
        string Name, double Radius, double OpacityPercent, double SoftnessPercent,
        double Zoom, bool Eraser, double ScatterPercent = 0);

    /// <summary>
    /// The matrix: the whole radius slider first, then the other sliders at one readable radius,
    /// then the same dab at three zoom levels, then the eraser.
    /// </summary>
    private static IEnumerable<Probe> Cases()
    {
        foreach (var radius in new double[] { 1, 4, 16, 48, 128, 256 })
        {
            yield return new Probe($"radius-{radius:F0}", radius, 100, 0, 1, false);
        }

        foreach (var opacity in new double[] { 5, 35, 100 })
        {
            yield return new Probe($"opacity-{opacity:F0}", 48, opacity, 0, 1, false);
        }

        foreach (var softness in new double[] { 0, 50, 100 })
        {
            yield return new Probe($"softness-{softness:F0}", 48, 100, softness, 1, false);
        }

        yield return new Probe("scatter-60", 48, 100, 0, 1, false, ScatterPercent: 60);

        foreach (var zoom in new double[] { 0.2, 1, 4 })
        {
            yield return new Probe($"zoom-{zoom:0.##}", 48, 100, 0, zoom, false);
        }

        yield return new Probe("eraser-48", 48, 100, 0, 1, true);
        yield return new Probe("eraser-192", 192, 100, 0, 1, true);
    }

    private static async Task<BrushStressCase> RunCaseAsync(
        EditorView view, EditorViewModel editor, LayerId layerId, Probe probe)
    {
        var error = "";
        var painted = 0;
        var measured = 0d;
        var minAlphaLive = 255;
        var minAlphaCommitted = 255;
        var tilesAdded = 0;
        var expected = 0d;

        try
        {
            // A clean layer per case, so nothing from the previous radius is measured again.
            while (editor.Session.UndoDepth > 0 && editor.Session.Undo()) { }
            await SettleAsync();

            var viewport = editor.Session.Viewport;
            viewport.Set(
                (CanvasWidth / 2.0) - (view.Canvas.Bounds.Width / 2 / probe.Zoom),
                (CanvasHeight / 2.0) - (view.Canvas.Bounds.Height / 2 / probe.Zoom),
                probe.Zoom);
            editor.SelectLayer(layerId);
            editor.ActiveTool = EditorTool.Brush;

            // An eraser over an empty layer proves nothing about erasing. Lay down paint first,
            // so the case measures a real removal - and so the hole, if there were one, would be
            // punched through pixels that actually exist.
            if (probe.Eraser)
            {
                editor.BrushSettings = editor.BrushSettings with
                {
                    Radius = probe.Radius * 1.5,
                    Opacity = 1,
                    Softness = 0,
                    Scatter = 0,
                    Eraser = false,
                    Color = "#FFFFFFFF",
                };
                await SettleAsync();
                var (paintX, paintY) = editor.Session.Viewport.WorldToScreen(
                    CanvasWidth / 2.0, CanvasHeight / 2.0);
                view.Canvas.PressMoveReleaseForSmoke(paintX, paintY, paintX + 1, paintY + 1);
                await SettleAsync();
            }

            editor.BrushSettings = editor.BrushSettings with
            {
                Radius = probe.Radius,
                Opacity = probe.OpacityPercent / 100.0,
                Softness = probe.SoftnessPercent / 100.0,
                Scatter = probe.ScatterPercent / 100.0,
                Eraser = probe.Eraser,
                Color = "#FFFFFFFF",
            };
            await SettleAsync();

            var tilesBefore = TileCount(editor, layerId);
            var (pressX, pressY) = viewport.WorldToScreen(CanvasWidth / 2.0, CanvasHeight / 2.0);

            var baseline = Snapshot(view.Canvas);
            view.Canvas.PressForSmoke(pressX, pressY);
            view.Canvas.MoveForSmoke(pressX + 1, pressY + 1);
            await SettleAsync();

            // The reported defect happens here, with the button still down.
            var live = Snapshot(view.Canvas);
            minAlphaLive = live.MinAlpha;
            painted = Different(baseline, live);
            measured = live.PaintedDiameter(baseline);

            view.Canvas.ReleaseForSmoke(pressX + 1, pressY + 1);
            await SettleAsync();

            var committed = Snapshot(view.Canvas);
            minAlphaCommitted = committed.MinAlpha;
            if (painted == 0)
            {
                painted = Different(baseline, committed);
                measured = committed.PaintedDiameter(baseline);
            }

            tilesAdded = TileCount(editor, layerId) - tilesBefore;
            expected = 2 * probe.Radius * probe.Zoom;
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name + ": " + exception.Message;
        }

        // A dab has to be opaque in the window, has to appear, and has to be roughly the size the
        // slider promises. The tolerance is loose on purpose: softness and antialiasing blur the
        // edge, and a dab wider than the control is clipped by it.
        var opaque = minAlphaLive == 255 && minAlphaCommitted == 255;
        var appeared = painted > 0;
        var sized = probe.Eraser || expected <= 0
            || measured >= Math.Min(expected * 0.6, view.Canvas.Bounds.Width - 4);
        var passed = error.Length == 0 && opaque && appeared && sized;

        return new BrushStressCase(
            probe.Name, probe.Radius, probe.OpacityPercent, probe.SoftnessPercent, probe.Zoom,
            probe.Eraser, painted, Math.Round(measured, 1), Math.Round(expected, 1),
            minAlphaLive, minAlphaCommitted, tilesAdded, error, passed);
    }

    // ------------------------------------------------------------------ pixels

    private sealed record Frame(byte[] Pixels, int Width, int Height)
    {
        /// <summary>The lowest alpha anywhere in the frame; 255 means the window is solid.</summary>
        public int MinAlpha
        {
            get
            {
                var minimum = 255;
                for (var offset = 3; offset < Pixels.Length; offset += 4)
                {
                    if (Pixels[offset] < minimum) minimum = Pixels[offset];
                }

                return minimum;
            }
        }

        /// <summary>The widest run of changed pixels, i.e. how big the dab actually looks.</summary>
        public double PaintedDiameter(Frame baseline)
        {
            int left = Width, right = -1, top = Height, bottom = -1;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var offset = ((y * Width) + x) * 4;
                    if (!Changed(baseline.Pixels, Pixels, offset)) continue;
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            }

            return right < 0 ? 0 : Math.Max(right - left + 1, bottom - top + 1);
        }
    }

    private static int Different(Frame baseline, Frame frame)
    {
        var count = 0;
        for (var offset = 0; offset < frame.Pixels.Length; offset += 4)
        {
            if (Changed(baseline.Pixels, frame.Pixels, offset)) count++;
        }

        return count;
    }

    private static bool Changed(byte[] baseline, byte[] frame, int offset) =>
        Math.Abs(baseline[offset] - frame[offset]) > 8
        || Math.Abs(baseline[offset + 1] - frame[offset + 1]) > 8
        || Math.Abs(baseline[offset + 2] - frame[offset + 2]) > 8
        || Math.Abs(baseline[offset + 3] - frame[offset + 3]) > 8;

    private static Frame Snapshot(Control control)
    {
        var width = Math.Max(1, (int)Math.Round(control.Bounds.Width));
        var height = Math.Max(1, (int)Math.Round(control.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        bitmap.Render(control);

        var stride = width * 4;
        var buffer = new byte[stride * height];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(
                new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return new Frame(buffer, width, height);
    }

    private static int TileCount(EditorViewModel editor, LayerId layerId) =>
        editor.Session.Document.FindLayer(layerId) is BrushLayer brush ? brush.Tiles.TileCount : 0;

    private static async Task SettleAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        await Task.Delay(20);
    }

    private sealed class StressFilePicker : IFilePicker
    {
        public Task<string?> PickOpenAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickSaveAsync(string suggestedFileName) => Task.FromResult<string?>(null);
    }

    private sealed class StressPrompts : IUserPrompts
    {
        public Task<bool> ConfirmMigrationCopyAsync() => Task.FromResult(false);
        public Task<bool> ConfirmDeleteProjectAsync(string projectTitle, bool fileAlreadyMissing = false) =>
            Task.FromResult(false);
        public Task<CloseChoice> ConfirmCloseDirtyAsync(string documentTitle) =>
            Task.FromResult(CloseChoice.Cancel);
        public Task<bool> ConfirmRestoreCheckpointAsync(string checkpointName) => Task.FromResult(false);
        public Task<bool> ConfirmDeleteCheckpointAsync(string checkpointName) => Task.FromResult(false);
    }

    private sealed class StressLog : IAppLog
    {
        public void Info(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class StressRecentMaps : IRecentMaps
    {
        public IReadOnlyList<RecentMapItem> Load() => [];

        public void Touch(string path, Guid documentId, string title, DateTimeOffset modifiedUtc,
            byte[]? thumbnailPng = null, string mapType = "Локальная карта") { }

        public void Remove(string path) { }
    }

    private sealed class StressStore : IProjectStore
    {
        public ProjectFileInspection Inspect(string path) => MapProjectStore.Inspect(path);
        public ProjectMigrationResult MigrateCopy(string sourcePath, string destinationPath) =>
            MapProjectStore.MigrateCopy(sourcePath, destinationPath);
        public OpenedProject Open(string path) => MapProjectStore.Open(path);
        public void Save(MapDocument document, string path, SaveMetadata metadata, byte[] previewPng) =>
            MapProjectStore.Save(document, path, metadata, previewPng);
        public void SaveAutosave(
            MapDocument document, string? targetPath, SaveMetadata metadata, byte[] previewPng) { }
        public void ClearRecovery(Guid documentId) { }
        public IReadOnlyList<RecoveryCandidate> FindRecoveryCandidates() => [];
        public OpenedProject RestoreRecovery(RecoveryCandidate candidate) =>
            throw new NotSupportedException();
        public void DiscardRecovery(RecoveryCandidate candidate) { }
    }

    private static MapDocument Document() => new(
        DocumentId.New(), "brush stress", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(CanvasWidth, CanvasHeight, "px", "#FF1E2430"),
        [
            new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF1E2430"),
            BrushLayer.CreateEmpty(LayerId.New(), "Кисть"),
            new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []),
        ]);
}
