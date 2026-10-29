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
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;

namespace Map.Performance;

/// <summary>
/// R07 smoke (`--profile native-text-on-path`): in its own hidden native window, the river and
/// road panels add a label along the line, the label follows a moved river and returns on Undo,
/// the text panel applies a preset and a generated name (each one Undo), binds a free text to the
/// nearest road, flips it, and save → reopen keeps the binding in schema v16. Writes two Avalonia
/// frames and the saved map (`.fixture.mapproj`, for the live pass) next to the report; the work folder (map, profile, recovery) is deleted at the end.
/// <code>dotnet tools/Map.Performance/bin/Release/net10.0/win-x64/Map.Performance.dll --profile native-text-on-path --output artifacts/r07/text-on-path.json</code>
/// </summary>
internal sealed class NativeTextOnPathReport
{
    public string Observation { get; } = "Own native Avalonia window, ShowActivated=false, off-screen; panel buttons drive every step; no system input. Screenshots are Avalonia frames.";
    public Dictionary<string, bool> Checks { get; } = [];
    public Dictionary<string, string> Notes { get; } = [];
    public string[] Screenshots { get; set; } = [];
    public string? Failure { get; set; }
    public bool GatePassed => Failure is null && Checks.Count >= 14 && Checks.Values.All(v => v);
}

internal static class NativeTextOnPathSmoke
{
    public static NativeTextOnPathReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<NativeTextOnPathReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            TextOnPathApplication.Options = options; TextOnPathApplication.Completion = completion;
            try { AppBuilder.Configure<TextOnPathApplication>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(3)).GetAwaiter().GetResult();
    }
}

public sealed class TextOnPathApplication : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<NativeTextOnPathReport> Completion = null!;
    private readonly NativeTextOnPathReport _report = new();
    private string _workspace = "";
    private Window _window = null!;
    private EditorView _view = null!;
    private EditorViewModel _editor = null!;
    private EditorShortcutSettings _shortcuts = null!;
    private readonly List<string> _shots = [];

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
        _workspace = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Options.OutputPath))!, "text-on-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        _window = new Window { Width = 1440, Height = 900, Title = "Map — R07", ShowActivated = false,
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
                // The owner's rule: no test leftovers — only the report and its frames stay.
                try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                Completion.TrySetResult(_report); desktop.Shutdown();
            }
        };
        desktop.MainWindow = _window; base.OnFrameworkInitializationCompleted();
    }

    private static readonly LayerId Objects = LayerId.New();

    private static MapDocument Valley()
    {
        var river = new River
        {
            Name = "Серебрянка", Style = RiverStyle.Illustrated(),
            Channels = [RiverEditor.NewChannel([new VectorPoint(900, 120), new VectorPoint(620, 260), new VectorPoint(360, 300), new VectorPoint(80, 470)], 18)],
        };
        var road = RoadEditor.NewRoad([new VectorPoint(120, 640), new VectorPoint(480, 600), new VectorPoint(900, 660)], 12, RoadStyle.Country(), "Королевский тракт");
        var house = new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle, "Дом", 150, 150, 60, 40, 0, "#FF8E6B4E", "#FF3A2A1B", 1.5, true, false);
        var layer = new ObjectLayer(Objects, "Долина", true, false, 1, [house]) { Rivers = [river], Roads = [road] };
        return new MapDocument(DocumentId.New(), "Подписи долины", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFE9E1CC"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFE9E1CC"), layer]);
    }

    private void OpenEditor(MapDocument document, string? path = null)
    {
        _editor = new EditorViewModel(document, path, false, new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery"))),
            new LocalRecent(), new LocalPicker(Path.Combine(_workspace, "labels.mapproj")), new LocalPrompts(), new LocalLog(),
            localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
    }

    private SceneObject Label(ObjectId id) =>
        _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Single(o => o.Id == id);

    private async Task Execute()
    {
        await Settle();
        _editor.Session.Viewport.Set(0, 0, Math.Min(_view.Canvas.Bounds.Width / 1024, _view.Canvas.Bounds.Height / 768));
        _editor.SelectLayer(Objects);
        var river = _editor.Rivers.Single();
        var road = _editor.Roads.Single();

        // 1. River panel: «Подпись вдоль реки».
        _editor.SelectRiver(river.Id); await Settle();
        var depth = _editor.Session.UndoDepth;
        await Click("RiverAddLabelButton");
        var label = _editor.Session.SelectedObject;
        _report.Checks["river-label-added-from-panel"] = label?.Text?.Path is { Target: TextPathTarget.River } path
            && path.TargetId == river.Id && label.Text.Content == "Серебрянка" && _editor.Session.UndoDepth == depth + 1;
        Require(label is not null, "No label after RiverAddLabelButton: " + _editor.StatusDetail);
        var start = label!.Text!.Path!.Baseline[0]; var end = label.Text.Path.Baseline[^1];
        _report.Checks["river-label-reads-left-to-right"] = end.X > start.X;
        _report.Notes["river-label-bounds"] = $"{label.X:0},{label.Y:0} {label.Width:0}×{label.Height:0}";
        await Frame(); Shot("river-label");

        // 1b. Dragging on the canvas keeps the dragged object's row in the object list — a plain
        // rectangle (control) and the bound label.
        _report.Checks["list-keeps-dragged-rectangle"] = await DragKeepsRow(_editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Single(o => o.Name == "Дом"));
        _report.Checks["list-keeps-dragged-label"] = await DragKeepsRow(Label(label.Id));
        label = Label(label.Id);

        // 2. The label follows a moved river and comes back with Undo.
        var moved = river with { Channels = [river.Channels[0] with { Nodes = [.. river.Channels[0].Nodes.Select(n => n with { Y = n.Y + 60 })] }] };
        _editor.Session.Execute(new RiverCommand(Objects, river.Id, moved, "Сдвинуть реку"));
        var followed = Label(label.Id);
        _report.Checks["label-follows-moved-river"] = Math.Abs(followed.Y - label.Y - 60) < 1.5;
        await Click("UndoButton");
        _report.Checks["undo-returns-label-with-river"] = Math.Abs(Label(label.Id).Y - label.Y) < 0.01;

        // 3. Text panel: preset and name, one Undo each.
        _report.Checks["select-tool-after-adding"] = _editor.ActiveTool == EditorTool.Select && _editor.Session.SelectedObjectId == label.Id;
        _editor.Session.SelectedObjectId = label.Id; await Settle();
        depth = _editor.Session.UndoDepth;
        await Click("LabelPresetCountryButton");
        _report.Checks["preset-from-panel"] = _editor.Session.SelectedObject?.Text?.FontSize == 64 && _editor.Session.UndoDepth == depth + 1;
        await Click("UndoButton");
        _report.Checks["preset-undo"] = _editor.Session.SelectedObject?.Text?.FontSize == label.Text.FontSize;
        await Click("LabelPresetRiverButton");
        await Click("TextGenerateNameButton");
        var named = _editor.Session.SelectedObject!;
        _report.Checks["name-button-sets-a-name"] = named.Text!.Content != "Серебрянка" && named.Text.Content.Length >= 3 && named.Name == named.Text.Content;
        _report.Notes["generated-name"] = named.Text.Content;

        // 4. A free text bound to the nearest road from the panel, then flipped and moved below.
        _editor.AddText();
        var free = _editor.Session.SelectedObject!;
        _editor.Session.Execute(new ReplaceObjectsCommand([free], [SceneObjectClone.At(free, 420 - free.Width / 2, 640 - free.Height / 2)], "Поставить у дороги"));
        await Settle();
        await Click("TextBindPathButton");
        _report.Checks["bind-to-nearest-road"] = _editor.SelectedTextPath is { Target: TextPathTarget.Road } bound && bound.TargetId == road.Id;
        await Click("TextPathFlipButton");
        _report.Checks["flip-turns-reading-direction"] = _editor.SelectedTextPath is { KeepUpright: false };
        _editor.SetTextPathSide(TextPathSide.Below);
        await Frame(); Shot("road-label");

        // 5. Save → reopen keeps both bindings (schema v16).
        await Click("SaveButton");
        await Until(() => File.Exists(Path.Combine(_workspace, "labels.mapproj")) && !_editor.Session.IsDirty);
        var file = Path.Combine(_workspace, "labels.mapproj");
        int schema;
        using (var zip = ZipFile.OpenRead(file))
        using (var stream = zip.GetEntry("manifest.json")!.Open())
            schema = JsonNode.Parse(stream)!["schemaVersion"]!.GetValue<int>();
        _report.Notes["schema"] = schema.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var reopened = MapProjectStore.Open(file).Document;
        var labels = reopened.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Where(o => o.Text?.Path is not null).ToList();
        _report.Checks["save-reopen-keeps-bindings-v16"] = schema == 16 && labels.Count == 2
            && labels.Any(l => l.Text!.Path!.Target == TextPathTarget.River) && labels.Any(l => l.Text!.Path!.Target == TextPathTarget.Road);
        _report.Checks["reopened-label-already-laid-out"] = labels.All(l => ReferenceEquals(TextOnPath.Layout(reopened, l), l));
        // One small map stays for the live pass in the real window (Map.App with this file).
        File.Copy(file, Path.ChangeExtension(Options.OutputPath, ".fixture.mapproj"), overwrite: true);
    }

    private async Task<bool> DragKeepsRow(SceneObject item)
    {
        _editor.ActiveTool = EditorTool.Select;
        _editor.PanelSection = PanelSection.Other;
        var centre = item.Text?.Path is not null ? TextOnPath.BaselineCentre(item) : new VectorPoint(item.X + item.Width / 2, item.Y + item.Height / 2);
        var a = _editor.Session.Viewport.WorldToScreen(centre.X, centre.Y);
        _view.Canvas.PressMoveReleaseForSmoke(a.X, a.Y, a.X, a.Y); await Settle();
        var b = _editor.Session.Viewport.WorldToScreen(centre.X + 40, centre.Y - 30);
        _view.Canvas.PressMoveReleaseForSmoke(a.X, a.Y, b.X, b.Y); await Settle(); await Settle();
        var list = _view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ObjectList");
        list.UpdateLayout();
        var rows = list.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(c => c.DataContext is ObjectTreeRow { Object: { } o } && o.Id == item.Id && c.IsEffectivelyVisible && c.Bounds.Height > 1).ToList();
        var model = _editor.ObjectTree.Count(r => r.Object?.Id == item.Id);
        var moved = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Single(o => o.Id == item.Id);
        _report.Notes["drag-" + item.Name] = $"model rows={model}, visible rows={rows.Count}, moved={(moved.X != item.X || moved.Y != item.Y)}, items={list.ItemCount}";
        return rows.Count == 1;
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
        NativeEditorScenarioApplication.RevealTab(_view, name);
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
        Require(button.IsEffectivelyVisible && button.IsEnabled, name + " is unavailable (" + (button.IsEffectivelyVisible ? "visible" : "hidden") + ", " + (button.IsEnabled ? "enabled" : "disabled") + ")");
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
    }
}
