using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Map.App.Views;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using Map.Rendering;

namespace Map.Performance;

internal sealed class MountainFoundationReport
{
    public string Observation { get; } = "Original mountain pack, editable templates; isolated catalog and own native window without system input.";
    public string Workspace { get; set; } = "";
    public Dictionary<string, bool> Checks { get; } = [];
    public string? Failure { get; set; }
    internal int RequiredCount { get; set; } = 18;
    public bool GatePassed => Failure is null && Checks.Count >= RequiredCount && Checks.Values.All(v => v);
}

internal static class NativeMountainFoundationSmoke
{
    public static MountainFoundationReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<MountainFoundationReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MountainFoundationApplication.Options = options; MountainFoundationApplication.Completion = completion;
            try { AppBuilder.Configure<MountainFoundationApplication>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(4)).GetAwaiter().GetResult();
    }
}

public sealed partial class MountainFoundationApplication : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<MountainFoundationReport> Completion = null!;
    private readonly MountainFoundationReport _report = new();
    private AssetCatalog _catalog = null!;
    private ProjectStore _store = null!;
    private Window _window = null!;
    private EditorView _view = null!;
    private EditorViewModel _editor = null!;
    private MountainManifest _pack = null!;
    private Dictionary<string, AssetRecord> _records = null!;
    private string Workspace => _report.Workspace;

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
        _report.Workspace = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, "mountains-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(Workspace, "profile"));
        _catalog = new AssetCatalog(Path.Combine(Workspace, "catalog"));
        _store = new ProjectStore(new RecoveryJournal(Path.Combine(Workspace, "recovery")));
        _pack = MountainFoundationPack.Load(); _records = MountainFoundationPack.Import(_catalog, _pack);
        if (Options.Profile == "native-stamp-family") ImportOtherMountainFamilies();
        var document = MountainFoundationPack.Template(_pack, _pack.Styles[0], _records, _catalog);
        _editor = new EditorViewModel(document, null, false, _store, new LocalRecent(),
            new LocalPicker(Path.Combine(Workspace, "edited.mapproj")), new LocalPrompts(), new LocalLog(),
            assetCatalog: _catalog, localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { });
        _window = new Window { Width = 1440, Height = 900, Title = "Map — пробные горные стили", ShowActivated = false,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000), Content = _view };
        _window.Opened += async (_, _) =>
        {
            try
            {
                if (Options.Profile == "native-stamp-family") await ExecuteStampFamily();
                else await Execute();
            }
            catch (Exception ex) { _report.Failure = ex.ToString(); Capture(_view, "failure"); }
            finally
            {
                _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches();
                await _catalog.DisposeAsync(); _window.Close(); Completion.TrySetResult(_report); desktop.Shutdown();
            }
        };
        desktop.MainWindow = _window; base.OnFrameworkInitializationCompleted();
    }

    private async Task Execute()
    {
        await Settle();
        var assetCount = _pack.Assets.Length;
        var collectionCount = _pack.Styles.Length + 1;
        Check("all distinct real alpha images", _records.Count == assetCount && _records.Values.All(r => r.HasAlpha));
        Check("all styles under one mountain collection", _catalog.Collections.Count == collectionCount
            && _catalog.Collections.Count(c => c.ParentId is not null) == _pack.Styles.Length);
        MountainFoundationPack.Import(_catalog, _pack);
        Check("repeated explicit import is idempotent", _catalog.Records.Count == assetCount && _catalog.Sets.Count == assetCount && _catalog.Collections.Count == collectionCount);
        Check("art names survive grouping", _catalog.Sets.Select(s => s.Name).Order().SequenceEqual(_pack.Assets.Select(a => a.Name).Order()));
        _editor.RefreshLibrary(); _view.RefreshLibraryPanel();
        _view.OpenAssetCatalog(); await Task.Delay(500); await Settle();
        Check("native catalog shows all pack cards", _view.AssetCatalogWindowForSmoke!.IsVisible
            && _editor.LibrarySets.Count == assetCount && _view.GalleryRealizedCards == assetCount);
        Capture(_view.AssetCatalogWindowForSmoke!, "catalog");
        _view.CloseAuxiliaryWindowsForSmoke();

        var original = _editor.Session.Document;
        var owner = original.Layers.OfType<ObjectLayer>().Single(l => l.PlacementPaths.Length == 1);
        var path = owner.PlacementPaths[0];
        _editor.SelectEditablePath(path.Id);
        Check("mountain chain is editable in native panel", _editor.SelectedEditablePath?.Id == path.Id);
        var movedNode = path.Nodes[1] with { Y = path.Nodes[1].Y + 70 };
        _editor.Session.Execute(new PlacementPathCommand(owner.Id, path.Id,
            path with { Nodes = path.Nodes.SetItem(1, movedNode) }, "Передвинуть горный перевал"));
        var edited = _editor.Session.Document;
        Check("node edit regenerates mountain chain", !ReferenceEquals(original, edited)
            && edited.Layers.OfType<ObjectLayer>().Single(l => l.Id == owner.Id).PlacementPaths[0].Nodes[1].Y == movedNode.Y);
        _editor.Undo(); Check("path undo restores exact document", ReferenceEquals(original, _editor.Session.Document));
        _editor.Redo(); Check("path redo restores exact document", ReferenceEquals(edited, _editor.Session.Document));
        _editor.Undo();

        var first = _records[_pack.Assets[0].Id];
        var count = _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count);
        _editor.StampPlacementMode = StampPlacementMode.One;
        _editor.ChooseGalleryVariant(_catalog.FindSetForContent(first.Sha256)!.Id, null);
        _editor.SetStampScalePercent(150, commit: true); await Settle();
        var point = _editor.Session.Viewport.WorldToScreen(950, 690); _view.Canvas.ClickForSmoke(point.X, point.Y);
        Check("native canvas places selected mountain", _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count) == count + 1);
        _editor.HandleEscape(); _editor.Undo();
        Check("native stamp undo preserves template", _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count) == count);
        await Rendered(); Capture(_view, "editor");

        var templates = Path.Combine(Workspace, "templates"); Directory.CreateDirectory(templates);
        var previews = Path.Combine(Workspace, "previews"); Directory.CreateDirectory(previews);
        foreach (var style in _pack.Styles)
        {
            var document = MountainFoundationPack.Template(_pack, style, _records, _catalog);
            var filename = Path.Combine(templates, style.Id + ".mapproj");
            await SaveAndExport(document, filename, Path.Combine(previews, style.Id + ".png"));
            var inspection = _store.Inspect(filename);
            Check(style.Id + " template saves and reopens with embedded art and editable chain",
                inspection.Status == ProjectFileStatus.Ready && inspection.Project!.Document.Assets.Count == 2
                && inspection.Project.Document.Layers.OfType<ObjectLayer>().Sum(l => l.PlacementPaths.Length) == 1);
        }
        var overview = MountainFoundationPack.Overview(_pack, _records, _catalog);
        await SaveAndExport(overview, Path.Combine(Workspace, "overview.mapproj"), Path.Combine(previews, "overview.png"));
        var dark = new MapDocument(DocumentId.New(), "Проверка края на тёмном", DateTimeOffset.UtcNow,
            new CanvasDefinition(overview.Canvas.Width, overview.Canvas.Height, "px", "#FF303B3D"),
            [new BaseColorLayer(LayerId.New(), "Контрастная подложка", true, false, 1, "#FF303B3D"),
                .. overview.Layers.OfType<ObjectLayer>()], overview.Assets);
        using (var darkImages = new ExportStampImageSource(dark, null, _catalog))
            File.WriteAllBytes(Path.Combine(Workspace, "alpha-dark.png"),
                ThumbnailRenderer.RenderPng(dark, dark.Canvas.Width, dark.Canvas.Height, darkImages));
        Check("overview exports all pack mountains", overview.Assets.Count == assetCount && File.Exists(Path.Combine(previews, "overview.png")));

        // The production dashboard command, with an isolated destination picker.
        var source = Path.Combine(templates, _pack.Styles[0].Id + ".mapproj");
        var beforeHash = SHA256.HashData(File.ReadAllBytes(source));
        var target = Path.Combine(Workspace, "from-template.mapproj");
        var dashboard = new DashboardViewModel(_store, new LocalRecent(), new LocalLog(), new LocalPicker(target), new LocalPrompts());
        var cloned = await dashboard.CloneAsync(new RecentCard(source, "Горы", DateTimeOffset.UtcNow, true, null), asTemplate: true);
        var copy = _store.Inspect(target).Project?.Document;
        Check("From template creates an independent map", cloned == target && copy is not null && copy.Id != _store.Inspect(source).Project!.Document.Id);
        Check("template source remains unchanged", SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(beforeHash));
        await _catalog.FlushMetadataAsync();
        await using var reopened = new AssetCatalog(_catalog.Root);
        Check("catalog hierarchy and images survive reopening", reopened.Sets.Count == assetCount && reopened.Collections.Count == collectionCount && reopened.Records.All(r => reopened.Resolve(r).Path is not null));
    }

    private async Task SaveAndExport(MapDocument document, string projectPath, string pngPath)
    {
        using var images = new ExportStampImageSource(document, projectPath, _catalog);
        var thumbnail = ThumbnailRenderer.RenderPng(document, stamps: images);
        _store.Save(document, projectPath, new SaveMetadata("Map mountain foundation", DateTimeOffset.UtcNow), thumbnail);
        var settings = new ExportEngineSettings { TileEdge = 256 };
        var request = new ExportRequest(ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null,
            document.Canvas.Width, document.Canvas.Height, ExportOutputMode.SingleImage, false, false, 100);
        var plan = ExportPlanner.Create(document, request, settings);
        var exported = await new ImageExportEngine(settings).ExportAsync(document, plan, pngPath, images);
        if (exported.FileCount != 1 || !File.Exists(pngPath)) throw new IOException("Missing mountain export");
    }

    private async Task Rendered()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Frame(object? _, EventArgs __) { if (ReferenceEquals(_view.Canvas.LastRenderedDocument, _editor.Session.Document)) completion.TrySetResult(); }
        _view.Canvas.FrameRendered += Frame; _view.Canvas.InvalidateVisual();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
        finally { _view.Canvas.FrameRendered -= Frame; }
    }

    private void Capture(Control control, string name)
    {
        control.UpdateLayout();
        using var image = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)control.Bounds.Width), Math.Max(1, (int)control.Bounds.Height)));
        image.Render(control); image.Save(Path.Combine(Workspace, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private void Check(string name, bool passed) { _report.Checks[name] = passed; Console.WriteLine((passed ? "ok " : "FAILED ") + name); }
    private static async Task Settle() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render); await Task.Delay(80); }

    private sealed class LocalPicker(string path) : IFilePicker
    { public Task<string?> PickOpenAsync() => Task.FromResult<string?>(path); public Task<string?> PickSaveAsync(string name) => Task.FromResult<string?>(path); }
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
