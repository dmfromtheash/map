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
/// R10 smoke (`--profile native-area-rules`): in its own hidden native window the area panel's
/// «Где ставить» switches remove assets from the road and the river (one Undo each), keep the others
/// in place, «Только на воду» inverts, «Ближние поверх дальних» reorders, and save → reopen keeps
/// the rules in schema v19. Writes Avalonia frames next to the report; the work folder is deleted.
/// <code>dotnet tools/Map.Performance/bin/Release/net10.0/win-x64/Map.Performance.dll --profile native-area-rules --output artifacts/r10/area-rules.json</code>
/// </summary>
internal sealed class NativeAreaRulesReport
{
    public string Observation { get; } = "Own native Avalonia window, ShowActivated=false, off-screen; panel buttons drive every step; no system input. Screenshots are Avalonia frames.";
    public Dictionary<string, bool> Checks { get; } = [];
    public Dictionary<string, string> Notes { get; } = [];
    public string[] Screenshots { get; set; } = [];
    public string? Failure { get; set; }
    public bool GatePassed => Failure is null && Checks.Count >= 16 && Checks.Values.All(v => v);
}

internal static class NativeAreaRulesSmoke
{
    public static NativeAreaRulesReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<NativeAreaRulesReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            AreaRulesApplication.Options = options; AreaRulesApplication.Completion = completion;
            try { AppBuilder.Configure<AreaRulesApplication>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(3)).GetAwaiter().GetResult();
    }
}

public sealed class AreaRulesApplication : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<NativeAreaRulesReport> Completion = null!;
    private readonly NativeAreaRulesReport _report = new();
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
        _workspace = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Options.OutputPath))!, "area-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        _window = new Window { Width = 1440, Height = 900, Title = "Map — R10", ShowActivated = false,
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

    private static readonly Guid AreaId = Guid.NewGuid();

    private static MapDocument Valley()
    {
        using var bitmap = new SKBitmap(32, 32);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = new SKColor(40, 90, 40), IsAntialias = true };
            canvas.DrawCircle(16, 14, 12, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var asset = new MapAssetReference(sha, "Ель", "image/png", "png", ProjectAssetStorage.Embedded, DateTimeOffset.UnixEpoch, "smoke", "Map", bytes);
        var river = new River { Name = "Серебрянка", Style = RiverStyle.Illustrated(), Channels = [RiverEditor.NewChannel([new VectorPoint(0, 300), new VectorPoint(1024, 320)], 40)] };
        var road = RoadEditor.NewRoad([new VectorPoint(0, 560), new VectorPoint(1024, 540)], 14, RoadStyle.Country(), "Тракт");
        var layer = new ObjectLayer(Objects, "Долина", true, false, 1, []) { Rivers = [river], Roads = [road] };
        var area = new PlacementArea
        {
            Id = AreaId, Name = "Лес", LayoutVersion = 1, Density = 6, Seed = 5,
            Nodes = [new(Guid.NewGuid(), 20, 20), new(Guid.NewGuid(), 1000, 20), new(Guid.NewGuid(), 1000, 740), new(Guid.NewGuid(), 20, 740)],
            Variants = [new(sha, 32, 32)], Style = new(sha), Request = new() { BaseWidth = 24 },
        };
        var document = new MapDocument(DocumentId.New(), "Лес у реки", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFE9E1CC"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFE9E1CC"), layer], [asset]);
        var session = new EditingSession(document);
        session.Execute(new PlacementAreaCommand(Objects, AreaId, area, "Лес"));
        return session.Document;
    }

    private void OpenEditor(MapDocument document, string? path = null)
    {
        _editor = new EditorViewModel(document, path, false, new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery"))),
            new LocalRecent(), new LocalPicker(Path.Combine(_workspace, "rules.mapproj")), new LocalPrompts(), new LocalLog(),
            localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
    }

    private PlacementArea Area() => _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementAreas).Single();

    private Dictionary<ObjectId, SceneObject> Stamps() => _editor.Session.Document.Layers.OfType<ObjectLayer>()
        .SelectMany(l => l.Objects).Where(o => o.Kind == SceneObjectKind.Stamp).ToDictionary(o => o.Id);

    private async Task Toggle(string name, bool value)
    {
        var toggle = Find<Avalonia.Controls.Primitives.ToggleButton>(name);
        Require(toggle.IsEffectivelyVisible && toggle.IsEnabled, name + " is unavailable");
        toggle.IsChecked = value; toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(); await Until(() => !_editor.IsEditableAreaBusy); await Settle();
    }

    private static string N(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task Execute()
    {
        await Settle();
        _editor.Session.Viewport.Set(0, 0, Math.Min(_view.Canvas.Bounds.Width / 1024, _view.Canvas.Bounds.Height / 768));
        _editor.SelectLayer(Objects);
        _editor.SelectEditableArea(AreaId); await Settle();
        var plain = Stamps();
        _report.Notes["plain"] = N(plain.Count);
        _report.Checks["rules-block-visible-and-off"] = Find<TextBlock>("EditableAreaRulesState").IsEffectivelyVisible && !_editor.EditableAreaRules.Any;

        var depth = _editor.Session.UndoDepth;
        await Toggle("EditableAreaAvoidRoadsToggle", true);
        var noRoad = Stamps();
        _report.Notes["no-road"] = N(noRoad.Count);
        _report.Checks["avoid-roads-one-undo"] = _editor.EditableAreaRules.AvoidRoads && _editor.Session.UndoDepth == depth + 1 && noRoad.Count < plain.Count;
        _report.Checks["kept-objects-unmoved"] = noRoad.All(p => plain.TryGetValue(p.Key, out var was) && was == p.Value);

        await Toggle("EditableAreaAvoidWaterToggle", true);
        var dry = Stamps();
        _report.Notes["no-road-no-water"] = N(dry.Count);
        _report.Checks["avoid-water-removes-more"] = dry.Count < noRoad.Count && _editor.EditableAreaRuleBlockedCount > 0;
        await Frame(); Shot("rules-on");

        await Toggle("EditableAreaOnlyOnWaterToggle", true);
        var wet = Stamps();
        _report.Notes["only-water"] = N(wet.Count);
        _report.Checks["only-on-water-inverse"] = !_editor.EditableAreaRules.AvoidWater && wet.Count > 0 && wet.Count < dry.Count;
        await Frame(); Shot("only-water");
        await Click("UndoButton"); await Settle();
        _report.Checks["undo-returns-dry-fill"] = Stamps().Count == dry.Count;

        await Toggle("EditableAreaDepthSortToggle", true);
        var ordered = _editor.Session.Document.Layers.OfType<ObjectLayer>().Single(l => l.Id == Objects).Objects;
        _report.Checks["depth-sort-orders-by-foot"] = _editor.EditableAreaDepthSort
            && ordered.Zip(ordered.Skip(1)).All(p => p.First.Y + p.First.Height <= p.Second.Y + p.Second.Height + 1e-6);

        await Click("SaveButton");
        var file = Path.Combine(_workspace, "rules.mapproj");
        await Until(() => File.Exists(file) && !_editor.Session.IsDirty);
        int schema;
        using (var zip = ZipFile.OpenRead(file))
        using (var stream = zip.GetEntry("manifest.json")!.Open())
            schema = JsonNode.Parse(stream)!["schemaVersion"]!.GetValue<int>();
        _report.Notes["schema"] = N(schema);
        var reopened = MapProjectStore.Open(file).Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementAreas).Single();
        _report.Checks["save-reopen-v19"] = schema == 19 && Equals(reopened.Rules, Area().Rules) && reopened.DepthSort && reopened.MemberCount == Area().MemberCount;

        await Toggle("EditableAreaAvoidRoadsToggle", false);
        await Toggle("EditableAreaAvoidWaterToggle", false);
        _report.Checks["all-off-restores-every-asset"] = Area().Rules is null && Stamps().Keys.ToHashSet().SetEquals(plain.Keys);
        await Frame(); Shot("rules-off");
        await Beauty();
    }

    /// <summary>
    /// R10 «Красота» (second pass): the block's switches from the panel, one Undo each, objects that
    /// stay keep their centres; a density brush stroke is one Undo step; «ближние поверх дальних» of
    /// the layer from the layer panel; save → reopen keeps all of it in schema v30.
    /// </summary>
    private async Task Beauty()
    {
        _editor.ArrangeEditableAreaNaturally(); await _editor.PendingGeneration; _editor.ApplyGeneration(); await Settle(); await Until(() => !_editor.IsEditableAreaBusy); await Settle();
        var natural = Stamps();
        _report.Notes["natural"] = N(natural.Count);
        _report.Checks["beauty-block-visible-all-off"] = Area().LayoutVersion == 4
            && Find<TextBlock>("EditableAreaBeautyState").IsEffectivelyVisible && !_editor.EditableAreaBeauty.Any;
        var depth = _editor.Session.UndoDepth;
        await Toggle("EditableAreaSoftEdgeToggle", true);
        var soft = Stamps();
        _report.Notes["soft-edge"] = N(soft.Count);
        static (double, double) Centre(SceneObject o) => (Math.Round(o.X + o.Width / 2, 6), Math.Round(o.Y + o.Height / 2, 6));
        _report.Checks["soft-edge-one-undo-centres-kept"] = _editor.Session.UndoDepth == depth + 1 && soft.Count < natural.Count
            && soft.All(p => natural.TryGetValue(p.Key, out var was) && Centre(was) == Centre(p.Value));
        await Toggle("EditableAreaGroundShadowToggle", true);
        await Toggle("EditableAreaUnderlayToggle", true);
        await Toggle("EditableAreaColorVariationToggle", true);
        _report.Checks["shadow-underlay-tint-on-objects"] = _editor.Session.UndoDepth == depth + 4 && Stamps().Values.All(o =>
            o.Stamp is { Tint: not null, GroundShadow: { Opacity: > 0, Mass: > 0 } });
        await Frame(); Shot("beauty-on");

        await Toggle("EditableAreaDensityBrushToggle", true);
        _report.Checks["density-brush-controls"] = _editor.IsAreaDensityBrushActive
            && Find<Control>("EditableAreaDensityRadiusSlider").IsEffectivelyVisible;
        _editor.SetAreaDensityBrushRadius(160); _editor.SetAreaDensityBrushStrength(100);
        var before = Stamps().Count; depth = _editor.Session.UndoDepth;
        _editor.BeginAreaDensityStroke(150, 420);
        for (var x = 170; x <= 850; x += 25) _editor.ContinueAreaDensityStroke(x, 420 + 60 * Math.Sin(x / 90.0));
        await Frame(); Shot("density-stroke");
        _editor.EndAreaDensityStroke(); await Settle(); await Until(() => !_editor.IsEditableAreaBusy); await Settle();
        _report.Notes["density-stroke"] = N(before) + " -> " + N(Stamps().Count);
        _report.Checks["density-stroke-one-undo"] = _editor.Session.UndoDepth == depth + 1 && Stamps().Count < before && Area().DensityMask is not null;
        await Frame(); Shot("density-brush");
        await Toggle("EditableAreaDensityBrushToggle", false);

        _editor.SelectLayer(Objects); await Settle();
        await Toggle("ObjectLayerDepthSortToggle", true);
        var layer = _editor.Session.Document.Layers.OfType<ObjectLayer>().Single(l => l.Id == Objects);
        _report.Checks["layer-depth-sort"] = layer.Effects.DepthSort;

        await Click("SaveButton");
        var file = Path.Combine(_workspace, "rules.mapproj");
        await Until(() => !_editor.Session.IsDirty);
        int schema;
        using (var zip = ZipFile.OpenRead(file))
        using (var stream = zip.GetEntry("manifest.json")!.Open())
            schema = JsonNode.Parse(stream)!["schemaVersion"]!.GetValue<int>();
        _report.Notes["schema-beauty"] = N(schema);
        var reopened = MapProjectStore.Open(file).Document;
        var reopenedLayer = reopened.Layers.OfType<ObjectLayer>().Single(l => l.Id == Objects);
        var reopenedArea = reopenedLayer.PlacementAreas.Single();
        _report.Checks["save-reopen-v30"] = schema == 30 && Equals(reopenedArea.Beauty, Area().Beauty)
            && Equals(reopenedArea.DensityMask, Area().DensityMask) && reopenedLayer.Effects.DepthSort
            && reopenedArea.MemberCount == Area().MemberCount;
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
