using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Map.App.Views;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;

namespace Map.Performance;

internal sealed class NativeAssetCatalogReport
{
    public string Observation { get; } = "Own native Avalonia window, ShowActivated=false; the real catalog window, the real stamp panel and the production canvas router. No system input.";
    public Dictionary<string, bool> Checks { get; } = [];
    public double ArtRenderMs { get; set; }
    public int ArtSetsImported { get; set; }
    public string? Failure { get; set; }
    public bool GatePassed => Failure is null && Checks.Count >= 10 && Checks.Values.All(v => v);
}

/// <summary>
/// The asset catalog as the user meets it: Map's own starter art is painted into a fresh
/// catalog, the catalog window opens grouped by collection, one click chooses, a double click
/// returns to the canvas with the ghost armed, and three stamps land on the map at two zooms.
/// </summary>
internal static class NativeAssetCatalogSmoke
{
    public static NativeAssetCatalogReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<NativeAssetCatalogReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            AssetCatalogApplication.Options = options; AssetCatalogApplication.Completion = completion;
            try { AppBuilder.Configure<AssetCatalogApplication>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(4)).GetAwaiter().GetResult();
    }
}

public sealed class AssetCatalogApplication : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<NativeAssetCatalogReport> Completion = null!;
    private readonly NativeAssetCatalogReport _report = new();
    private string _workspace = "";
    private AssetCatalog _catalog = null!;
    private Window _window = null!;
    private EditorView _view = null!;
    private EditorViewModel _editor = null!;

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
        _workspace = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, "asset-catalog-native");
        Directory.CreateDirectory(_workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        // A fresh catalog every run: the scenario is the first meeting with the starter art.
        var catalogRoot = Path.Combine(_workspace, "catalog");
        if (Directory.Exists(catalogRoot)) Directory.Delete(catalogRoot, recursive: true);
        _catalog = new AssetCatalog(catalogRoot);
        StarterPackBootstrapper.EnsureInstalled(_catalog, DateTimeOffset.UnixEpoch);
        _window = new Window { Width = 1440, Height = 900, Title = "Map — каталог ассетов", ShowActivated = false,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000) };
        var document = new MapDocument(DocumentId.New(), "Каталог ассетов", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFE9E0C8"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFE9E0C8"),
                new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        _editor = new EditorViewModel(document, null, false, new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery"))),
            new LocalRecent(), new LocalPicker(Path.Combine(_workspace, "catalog.mapproj")), new LocalPrompts(), new LocalLog(),
            assetCatalog: _catalog, localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
        _window.Opened += async (_, _) =>
        {
            try { await Execute(); }
            catch (Exception ex)
            {
                _report.Failure = ex.ToString();
                Capture(_view, "failure");
            }
            finally
            {
                _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches();
                await _catalog.FlushMetadataAsync(); _window.Close(); Completion.TrySetResult(_report); desktop.Shutdown();
            }
        };
        desktop.MainWindow = _window; base.OnFrameworkInitializationCompleted();
    }

    private async Task Execute()
    {
        await Settle();
        // 1. The starter art: painted on a worker, imported on the UI thread, like the app does it.
        var missing = StarterPackBootstrapper.MissingArtSets(_catalog);
        Check("art missing from a fresh catalog", missing.Count == StarterArtForge.Sets.Count);
        var watch = Stopwatch.StartNew();
        var root = _catalog.Root;
        var rendered = await Task.Run(() => StarterPackBootstrapper.RenderArtFiles(root, missing));
        _report.ArtRenderMs = watch.Elapsed.TotalMilliseconds;
        _report.ArtSetsImported = StarterPackBootstrapper.ImportArt(_catalog, rendered, DateTimeOffset.UnixEpoch);
        Check("nine art sets imported", _report.ArtSetsImported == 9 && StarterPackBootstrapper.MissingArtSets(_catalog).Count == 0);
        Check("three art collections", _catalog.Collections.Count(c => c.Name.StartsWith("Map · ", StringComparison.Ordinal)) >= 3);
        _editor.RefreshLibrary(); _view.RefreshLibraryPanel();

        // 2. The catalog window, grouped by collection, tiles realised only for what is on screen.
        _editor.ActiveTool = EditorTool.Stamp; await Settle();
        _view.OpenAssetCatalog(); await Task.Delay(400);
        var window = _view.AssetCatalogWindowForSmoke!; window.UpdateLayout(); await Task.Delay(300);
        Check("catalog groups by collection", _editor.LibraryGrouping == AssetGrouping.Collection
            && window.GetVisualDescendants().OfType<Border>().Count(b => b.Name?.StartsWith("LibraryGroupHeader_", StringComparison.Ordinal) == true) >= 3);
        Check("tiles realised and bounded", _view.GalleryRealizedCards is > 0 and < 200);
        await WaitForThumbnails(window);
        Capture(window, "gallery");

        // 3. One click chooses and keeps the window; the side panel already shows the choice.
        var painted = _catalog.Sets.Single(s => s.Name == "Горы · живопись");
        var tile = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "GalleryCover_" + Sanitize(painted.Id));
        tile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
        Check("one click chooses and keeps the catalog", window.IsVisible && _editor.SelectedSetId == painted.Id && _editor.CurrentVariant is not null);
        // The count badge unfolds the variants inline.
        var badge = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "GalleryExpand_" + Sanitize(painted.Id));
        badge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(200);
        Check("badge unfolds three variants", window.GetVisualDescendants().OfType<Button>().Count(b => b.Name?.StartsWith("GalleryVariant_", StringComparison.Ordinal) == true) == 3);
        await WaitForThumbnails(window);
        Capture(window, "gallery-expanded");
        var second = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "GalleryVariant_" + Sanitize(painted.VariantIds[1]));
        second.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!)); await Settle();
        Check("double click arms the ghost and closes", !window.IsVisible && _editor.CurrentVariant?.Id == painted.VariantIds[1]
            && _editor.PendingPlacement is { PointerVisible: true } && _editor.ActiveTool == EditorTool.Stamp);
        var current = _view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "StampToolCurrentText");
        Check("side panel names the chosen set", current.Text == painted.Name);
        Check("variant cells are numbered", _view.GetVisualDescendants().OfType<Button>().Count(b => b.Name?.StartsWith("StampVariant_", StringComparison.Ordinal) == true) == 3);
        await Task.Delay(300);
        Capture(_view, "editor");

        // 4. Three stamps on the map, then the same map at 4× zoom.
        var objects = () => _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count);
        _editor.SetStampScalePercent(220, commit: true); await Settle();
        Point(330, 420);
        Check("mountain placed", objects() == 1);
        var spruce = _catalog.Sets.Single(s => s.Name == "Ель · живопись");
        _editor.ChooseGalleryVariant(spruce.Id, null); _editor.SetStampScalePercent(140, commit: true); await Settle();
        Point(640, 470); Point(700, 500);
        var tower = _catalog.Sets.Single(s => s.Name == "Башня · живопись");
        _editor.ChooseGalleryVariant(tower.Id, null); _editor.SetStampScalePercent(150, commit: true); await Settle();
        Point(560, 380);
        Check("four stamps placed from three sets", objects() == 4);
        _editor.HandleEscape(); await Settle();
        await Rendered(); Capture(_view.Canvas, "canvas");
        _editor.Session.Viewport.Set(380, 330, 4); await Settle(); await Rendered();
        Capture(_view.Canvas, "canvas-zoom4");
        _editor.Undo(); await Settle();
        Check("undo removes the last stamp", objects() == 3);
    }

    private async Task WaitForThumbnails(Window window)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(6))
        {
            await Task.Delay(120);
            var images = window.GetVisualDescendants().OfType<Image>().Where(i => i.Tag is string).ToArray();
            if (images.Length > 0 && images.All(i => i.Source is not null)) return;
        }
    }

    private async Task Rendered()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Frame(object? _, EventArgs __) { if (ReferenceEquals(_view.Canvas.LastRenderedDocument, _editor.Session.Document)) completion.TrySetResult(); }
        _view.Canvas.FrameRendered += Frame; _view.Canvas.InvalidateVisual();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (TimeoutException) { }
        finally { _view.Canvas.FrameRendered -= Frame; }
        await Task.Delay(150);
    }

    private void Point(double x, double y)
    { var p = _editor.Session.Viewport.WorldToScreen(x, y); _view.Canvas.ClickForSmoke(p.X, p.Y); }

    private void Capture(Control root, string suffix)
    {
        root.UpdateLayout();
        var width = Math.Max(1, (int)root.Bounds.Width); var height = Math.Max(1, (int)root.Bounds.Height);
        using var image = new RenderTargetBitmap(new PixelSize(width, height));
        image.Render(root);
        image.Save(Path.ChangeExtension(Options.OutputPath, suffix + ".png"), PngBitmapEncoderOptions.Default);
    }

    private void Check(string key, bool value)
    {
        _report.Checks[key] = value;
        Console.WriteLine((value ? "ok      " : "FAILED  ") + key);
    }

    private static string Sanitize(string value) => new(value.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private static async Task Settle() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render); await Task.Delay(60); }

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
