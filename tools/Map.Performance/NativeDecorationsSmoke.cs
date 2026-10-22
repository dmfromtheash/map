using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Map.App.Services;
using Map.App.Views;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// R08 smoke (`--profile native-decorations`): in its own hidden native window the «Масштаб и
/// украшения» tool sets the map scale from its panel, a drag on the canvas measures in map units,
/// the panel adds a scale bar, compass rose, cartouche and legend (one Undo each), the decoration
/// block changes a style, the legend lists the river, export to PNG carries the decorations and
/// save → reopen keeps everything in schema v17. Writes Avalonia frames and the saved map
/// (`.fixture.mapproj`, for the live pass) next to the report; the work folder is deleted at the end.
/// <code>dotnet tools/Map.Performance/bin/Release/net10.0/win-x64/Map.Performance.dll --profile native-decorations --output artifacts/r08/decorations.json</code>
/// </summary>
internal sealed class NativeDecorationsReport
{
    public string Observation { get; } = "Own native Avalonia window, ShowActivated=false, off-screen; panel buttons drive every step; no system input. Screenshots are Avalonia frames.";
    public Dictionary<string, bool> Checks { get; } = [];
    public Dictionary<string, string> Notes { get; } = [];
    public string[] Screenshots { get; set; } = [];
    public string? Failure { get; set; }
    public bool GatePassed => Failure is null && Checks.Count >= 12 && Checks.Values.All(v => v);
}

internal static class NativeDecorationsSmoke
{
    public static NativeDecorationsReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<NativeDecorationsReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            DecorationsApplication.Options = options; DecorationsApplication.Completion = completion;
            try { AppBuilder.Configure<DecorationsApplication>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(3)).GetAwaiter().GetResult();
    }
}

public sealed class DecorationsApplication : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<NativeDecorationsReport> Completion = null!;
    private readonly NativeDecorationsReport _report = new();
    private string _workspace = "";
    private Window _window = null!;
    private EditorView _view = null!;
    private EditorViewModel _editor = null!;
    private EditorShortcutSettings _shortcuts = null!;
    private readonly List<string> _shots = [];
    private static readonly LayerId Objects = LayerId.New();

    public override void Initialize()
    {
        var production = new global::Map.App.App(); production.Initialize();
        RequestedThemeVariant = production.RequestedThemeVariant;
        foreach (var resource in production.Resources) Resources[resource.Key] = resource.Value;
        var styles = production.Styles.ToArray(); production.Styles.Clear(); Styles.AddRange(styles);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        _shortcuts = new EditorShortcutSettings();
        _workspace = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Options.OutputPath))!, "decorations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        _window = new Window { Width = 1440, Height = 900, Title = "Map — R08", ShowActivated = false,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000) };
        _window.AddHandler(InputElement.KeyDownEvent,
            (object? _, KeyEventArgs args) =>
            { if (EditorKeyRouting.TryHandleKey(_window, _view, _shortcuts, args)) args.Handled = true; },
            RoutingStrategies.Bubble, handledEventsToo: true);
        OpenEditor(Valley());
        _window.Opened += async (_, _) =>
        {
            try { await Execute(); }
            catch (Exception ex) { _report.Failure = ex.ToString(); Shot("failure"); }
            finally
            {
                _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches();
                _report.Screenshots = [.. _shots];
                _window.Close();
                try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                Completion.TrySetResult(_report); desktop.Shutdown();
            }
        };
        desktop.MainWindow = _window; base.OnFrameworkInitializationCompleted();
    }

    private static MapDocument Valley()
    {
        var river = new River
        {
            Name = "Серебрянка", Style = RiverStyle.Illustrated(),
            Channels = [RiverEditor.NewChannel([new VectorPoint(900, 120), new VectorPoint(620, 260), new VectorPoint(360, 300), new VectorPoint(80, 470)], 18)],
        };
        var road = RoadEditor.NewRoad([new VectorPoint(120, 640), new VectorPoint(480, 600), new VectorPoint(900, 660)], 12, RoadStyle.Country(), "Королевский тракт");
        var layer = new ObjectLayer(Objects, "Долина", true, false, 1, []) { Rivers = [river], Roads = [road] };
        return new MapDocument(DocumentId.New(), "Долина Серебрянки", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFE9E1CC"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFE9E1CC"), layer]);
    }

    private void OpenEditor(MapDocument document, string? path = null)
    {
        _editor = new EditorViewModel(document, path, false, new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery"))),
            new LocalRecent(), new LocalPicker(Path.Combine(_workspace, "decor.mapproj")), new LocalPrompts(), new LocalLog(),
            localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
    }

    private IEnumerable<SceneObject> Decorations() =>
        _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Where(o => o.Kind == SceneObjectKind.Decoration);

    private async Task Execute()
    {
        await Settle();
        _editor.Session.Viewport.Set(0, 0, Math.Min(_view.Canvas.Bounds.Width / 1024, _view.Canvas.Bounds.Height / 768));
        _editor.SelectLayer(Objects);

        // 1. The rail button opens the tool; the scale typed into its panel is one Undo step.
        await Click("MeasureToolButton");
        _report.Checks["rail-opens-measure-tool"] = _editor.ActiveTool == EditorTool.Measure;
        var depth = _editor.Session.UndoDepth;
        var box = Find<TextBox>("MapScaleDistanceBox");
        box.Text = "2,5";
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Settle();
        _report.Checks["scale-from-panel"] = _editor.MapScale == new MapScale(0.025, "км") && _editor.Session.UndoDepth == depth + 1;
        _report.Notes["scale-state"] = Find<TextBlock>("MapScaleStateText").Text ?? "";

        // 2. A drag on the canvas measures in kilometres.
        var a = _editor.Session.Viewport.WorldToScreen(100, 100);
        var b = _editor.Session.Viewport.WorldToScreen(500, 400);
        _view.Canvas.PressMoveReleaseForSmoke(a.X, a.Y, b.X, b.Y); await Settle();
        _report.Notes["measure"] = _editor.MeasureText ?? "";
        _report.Checks["drag-measures-in-km"] = _editor.MeasureText?.StartsWith("Расстояние: 12,5 км", StringComparison.Ordinal) == true
            && _editor.Session.UndoDepth == depth + 1;
        await Frame(); Shot("measure");

        // 3. The panel adds each decoration, one Undo step each.
        foreach (var kind in Enum.GetValues<DecorationKind>())
        {
            await Click("MeasureToolButton");
            depth = _editor.Session.UndoDepth;
            await Click("AddDecoration" + kind + "Button");
            _report.Checks["add-" + kind] = _editor.Session.SelectedObject?.Decoration?.Kind == kind && _editor.Session.UndoDepth == depth + 1;
        }

        var placed = Decorations().ToDictionary(o => o.Decoration!.Kind);
        _report.Checks["legend-lists-river-and-road"] = placed[DecorationKind.Legend].Decoration!.Entries is var entries
            && entries.Any(e => e.Kind == LegendEntryKind.River) && entries.Any(e => e.Kind == LegendEntryKind.Road);
        _report.Checks["scale-bar-carries-scale"] = placed[DecorationKind.ScaleBar].Decoration!.Scale == new MapScale(0.025, "км");
        _report.Checks["cartouche-takes-map-title"] = placed[DecorationKind.Cartouche].Decoration!.Title == "Долина Серебрянки";

        // Spread them over the map so the frames and the export show each one.
        Place(placed[DecorationKind.ScaleBar], 60, 690, 360, 60);
        Place(placed[DecorationKind.Compass], 860, 40, 130, 130);
        Place(placed[DecorationKind.Cartouche], 330, 30, 360, 110);
        Place(placed[DecorationKind.Legend], 780, 420, 210, 280);

        // 4. The decoration block changes the compass style (one Undo step).
        _editor.ActiveTool = EditorTool.Select;
        _editor.Session.SelectedObjectId = placed[DecorationKind.Compass].Id; await Settle();
        depth = _editor.Session.UndoDepth;
        _editor.SetDecorationVariant(4); await Settle();
        _report.Checks["compass-style-edit"] = _editor.SelectedDecoration?.Variant == 4 && _editor.Session.UndoDepth == depth + 1;
        _report.Checks["decoration-block-shown"] = Find<TextBlock>("DecorationInfoText").IsEffectivelyVisible;
        await Frame(); Shot("decorations");

        // 5. Export carries the decorations: the cartouche box is inked in the PNG.
        var png = Path.Combine(_workspace, "export.png");
        var engine = new ImageExportEngine();
        var plan = engine.Plan(_editor.Session.Document, new ExportRequest(ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null, 1024, 768,
            ExportOutputMode.SingleImage, TransparentBackground: false, IncludeGrid: false));
        await engine.ExportAsync(_editor.Session.Document, plan, png, null, null);
        using (var bitmap = SKBitmap.Decode(png))
        {
            var cartouche = Decorations().Single(o => o.Decoration!.Kind == DecorationKind.Cartouche);
            var dark = 0;
            for (var y = (int)cartouche.Y; y < cartouche.Y + cartouche.Height; y++)
            for (var x = (int)cartouche.X; x < cartouche.X + cartouche.Width; x++)
                if (bitmap.GetPixel(x, y) is var c && .3 * c.Red + .59 * c.Green + .11 * c.Blue < 110) dark++;
            _report.Notes["export-cartouche-ink-pixels"] = dark.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _report.Checks["export-carries-decorations"] = dark > 500;
        }
        File.Copy(png, Path.ChangeExtension(Options.OutputPath, ".export.png"), overwrite: true);

        // 6. Save → reopen keeps decorations and the scale (schema v17).
        await Click("SaveButton");
        var file = Path.Combine(_workspace, "decor.mapproj");
        await Until(() => File.Exists(file) && !_editor.Session.IsDirty);
        int schema;
        using (var zip = ZipFile.OpenRead(file))
        using (var stream = zip.GetEntry("manifest.json")!.Open())
            schema = JsonNode.Parse(stream)!["schemaVersion"]!.GetValue<int>();
        _report.Notes["schema"] = schema.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var reopened = MapProjectStore.Open(file).Document;
        var again = reopened.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Where(o => o.Kind == SceneObjectKind.Decoration).ToList();
        _report.Checks["save-reopen-v17"] = schema == 17 && again.Count == 4 && reopened.Canvas.Scale == new MapScale(0.025, "км")
            && again.Zip(Decorations()).All(p => Equals(p.First.Decoration, p.Second.Decoration));
        File.Copy(file, Path.ChangeExtension(Options.OutputPath, ".fixture.mapproj"), overwrite: true);
    }

    private void Place(SceneObject item, double x, double y, double width, double height)
    {
        var moved = new SceneObject(item.Id, item.Kind, item.Name, x, y, width, height, item.RotationDegrees,
            item.FillColor, item.StrokeColor, item.StrokeWidth, item.Visible, item.Locked) { Decoration = item.Decoration, Sublayer = item.Sublayer };
        _editor.Session.Execute(new global::Map.Core.Editing.ReplaceObjectsCommand([item], [moved], "Поставить украшение"));
    }

    private void Shot(string name)
    {
        var path = Path.ChangeExtension(Options.OutputPath, "." + name + ".png");
        using var bitmap = new RenderTargetBitmap(new PixelSize(1440, 900)); bitmap.Render(_view);
        using var file = File.Create(path); bitmap.Save(file, PngBitmapEncoderOptions.Default);
        _shots.Add(path);
    }

    private async Task Frame()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Rendered(object? sender, EventArgs args)
        { if (ReferenceEquals(_view.Canvas.LastRenderedDocument, _editor.Session.Document)) completion.TrySetResult(); }
        _view.Canvas.FrameRendered += Rendered; _view.Canvas.InvalidateVisual();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
        finally { _view.Canvas.FrameRendered -= Rendered; }
    }

    private T Find<T>(string name) where T : Control
    {
        Stage19Application.RevealTab(_view, name);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (_view.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) is { } found) return found;
            var folded = _view.GetVisualDescendants().OfType<Expander>().Where(e => !e.IsExpanded).ToArray();
            if (folded.Length == 0) break;
            foreach (var expander in folded) expander.IsExpanded = true;
            _view.UpdateLayout();
        }
        throw new InvalidOperationException($"{name} is not in the panel, even with every section opened");
    }

    private async Task Click(string name)
    {
        var button = Find<Button>(name); button.BringIntoView(); await Settle();
        Require(button.IsEffectivelyVisible && button.IsEnabled, name + " is unavailable");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
    }

    private static async Task Settle() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render); await Task.Delay(50); }
    private static async Task Until(Func<bool> condition)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException(); await Task.Delay(50); } }
    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }

    private sealed class LocalPicker(string path) : IFilePicker
    { public Task<string?> PickOpenAsync() => Task.FromResult<string?>(path); public Task<string?> PickSaveAsync(string suggestedFileName) => Task.FromResult<string?>(path); }
    private sealed class LocalRecent : IRecentMaps
    { public IReadOnlyList<RecentMapItem> Load() => []; public void Remove(string path) { }
        public void Touch(string path, Guid id, string title, DateTimeOffset date, byte[]? thumbnailPng = null, string mapType = "Локальная карта") { } }
    private sealed class LocalLog : IAppLog
    { public void Info(string message) { } public void Error(string message, Exception? exception = null) => Console.WriteLine(message + " " + exception?.Message); }
    private sealed class LocalPrompts : IUserPrompts
    {
        public Task<CloseChoice> ConfirmCloseDirtyAsync(string title) => Task.FromResult(CloseChoice.Cancel);
        public Task<bool> ConfirmRestoreCheckpointAsync(string name) => Task.FromResult(false);
        public Task<bool> ConfirmDeleteCheckpointAsync(string name) => Task.FromResult(false);
        public Task<bool> ConfirmMigrationCopyAsync() => Task.FromResult(false);
        public Task<bool> ConfirmDeleteProjectAsync(string title, bool fileAlreadyMissing = false) => Task.FromResult(false);
        public Task ShowErrorAsync(string title, string message) => throw new InvalidOperationException(title + ": " + message);
    }
}
