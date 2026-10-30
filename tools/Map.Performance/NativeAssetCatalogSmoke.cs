using System.Diagnostics;
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
using Map.App.Views;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using Map.Rendering;

namespace Map.Performance;

internal sealed class NativeAssetCatalogReport
{
    public string Observation { get; } = "Own native Avalonia window, ShowActivated=false; the real catalog window, the real stamp panel and the production canvas router. No system input.";
    public Dictionary<string, bool> Checks { get; } = [];
    public List<string> Frames { get; } = [];
    public int MinimumChecks { get; set; } = 10;
    public string? Failure { get; set; }
    public bool GatePassed => Failure is null && Checks.Count >= MinimumChecks && Checks.Values.All(v => v);
}

/// <summary>
/// An empty library first, then explicit imports of the owner's mountains (<see cref="OwnerMountainFixtures"/>) into its own
/// isolated catalog. Exercises the real gallery and stamp tool without reinstalling product art.
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
        _report.MinimumChecks = Options.Profile == "native-asset-catalog" ? 45 : 10;
        _workspace = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, "asset-catalog-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        // Opening a fresh catalog never installs content.
        var catalogRoot = Path.Combine(_workspace, "catalog-" + Guid.NewGuid().ToString("N"));
        _catalog = new AssetCatalog(catalogRoot);
        _window = new Window { Width = 1440, Height = 900, Title = "Map — каталог ассетов", ShowActivated = false,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000) };
        var document = new MapDocument(DocumentId.New(), "Каталог ассетов", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFE9E0C8"),
            [CurrentLook.Page("Фон", "#FFE9E0C8"),
                new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        if (Options.Profile == "native-empty-artwork")
            document = document with { Layers = document.Layers.Append(
                LandLayer.CreateEmpty(LayerId.New(), "Земля") with
                {
                    GeometryTiles = TileMap.Empty(1).With(new TileKey(0, 0),
                        Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray()),
                }).ToArray() };
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
                try
                {
                    _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches();
                    _editor.CancelPlacement();
                    await _editor.PendingEditableAreaCount; await _editor.PendingEditableAreaPreview; await _editor.PendingEditableAreaWork;
                    await _catalog.DisposeAsync(); _window.Close();
                    Directory.Delete(_workspace, recursive: true);
                    Check("isolated workspace deleted", !Directory.Exists(_workspace));
                }
                catch (Exception ex) { _report.Failure ??= ex.ToString(); }
                Completion.TrySetResult(_report); desktop.Shutdown();
            }
        };
        desktop.MainWindow = _window; base.OnFrameworkInitializationCompleted();
    }

    private async Task Execute()
    {
        await Settle();
        Check("fresh library is ready and empty", _catalog.Status == AssetLibraryStatus.Ready
            && _catalog.Records.Count == 0 && _catalog.Sets.Count == 0);
        Check("fresh library has no collections or recent cards", _catalog.Collections.Count == 0 && _catalog.Recent.Count == 0);
        Check("no former artwork is embedded", !typeof(AssetCatalog).Assembly.GetManifestResourceNames()
            .Any(name => name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)));
        _editor.RefreshLibrary(); _view.RefreshLibraryPanel();

        _editor.ActiveTool = EditorTool.Stamp; await Settle();
        _view.OpenAssetCatalog(); await Task.Delay(400);
        var window = _view.AssetCatalogWindowForSmoke!; window.UpdateLayout(); await Task.Delay(300);
        Check("empty catalog window opens without asset cards", window.IsVisible
            && _editor.LibrarySets.Count == 0 && _view.GalleryRealizedCards == 0);
        Capture(window, "empty-gallery");

        if (Options.Profile == "native-empty-artwork")
        {
            Check("empty catalog does not offer the removed regional pack", !window.GetVisualDescendants().OfType<Button>()
                .Any(b => b.Name is "ImportRegionalPackButton" or "EmptyLibraryRegionalPackButton" && b.IsVisible));
            window.Close();
            await CheckEmptyArtwork();
            return;
        }

        // The owner's mountains are imported only into this isolated smoke catalog.
        static string Fixture(int number) => OwnerMountainFixtures.Path(number);
        _catalog.ImportSet([Fixture(1), Fixture(2), Fixture(3)], "Мои горы",
            new AssetImportOptions { Pack = "Проверка вариантов", Tags = ["Тема/Горы"] }, DateTimeOffset.UnixEpoch);
        var sample4 = _catalog.ImportSet([Fixture(4)], OwnerMountainFixtures.Names[3],
            new AssetImportOptions { Pack = "Проверка одиночных", Tags = ["Тема/Образцы"] }, DateTimeOffset.UnixEpoch);
        var sample5 = _catalog.ImportSet([Fixture(5)], OwnerMountainFixtures.Names[4],
            new AssetImportOptions { Pack = "Проверка одиночных", Tags = ["Тема/Образцы"] }, DateTimeOffset.UnixEpoch);
        _editor.RefreshLibrary(); _view.RefreshLibraryPanel(); await Settle();
        Check("explicit owner import creates three sets", _catalog.Records.Count == 5 && _catalog.Sets.Count == 3);
        Check("catalog groups by collection", _editor.LibraryGrouping == AssetGrouping.Collection
            && window.GetVisualDescendants().OfType<Border>().Count(b => b.Name?.StartsWith("LibraryGroupHeader_", StringComparison.Ordinal) == true) >= 2);
        Check("tiles realised and bounded", _view.GalleryRealizedCards is > 0 and < 200);
        await WaitForThumbnails(window);
        Capture(window, "gallery");

        var mine = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(b => b.Name == "Collection_SourceMine");
        mine.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
        Check("explicit imports appear under My source", _editor.LibrarySource == AssetSourceFilter.Mine && _editor.LibrarySets.Count == 3);
        _editor.LibrarySource = AssetSourceFilter.BuiltIn;
        _editor.LibraryGrouping = AssetGrouping.None;
        _view.RefreshLibraryPanel(); await Settle();
        Check("Map source stays empty after explicit import", _editor.LibrarySets.Count == 0);
        _editor.LibrarySource = AssetSourceFilter.Mine;
        _editor.SetLibraryTagSelected("Тема/Образцы", true);
        _view.RefreshLibraryPanel(); await Settle();
        Check("tag filter selects single samples", _editor.LibrarySets.Count == 2);
        _editor.ClearLibraryFilters();
        _editor.LibraryGrouping = AssetGrouping.Collection;
        _editor.LibraryCollectionId = _catalog.Sets.Single(s => s.Name == "Мои горы").CollectionId;
        _view.RefreshLibraryPanel(); await Settle(); await WaitForThumbnails(window);

        // 3. One click puts the asset in the hand: the catalog closes and the ghost is armed
        // (owner, 2026-10-24). While the set is being organised a click only chooses.
        var painted = _catalog.Sets.Single(s => s.Name == "Мои горы");
        Button Tile() => window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "GalleryCover_" + Sanitize(painted.Id));
        Tile().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
        Check("one click arms the ghost and closes the catalog", !window.IsVisible && _editor.SelectedSetId == painted.Id
            && _editor.PendingPlacement is { PointerVisible: true } && _editor.ActiveTool == EditorTool.Stamp);
        _view.OpenAssetCatalog(); await Settle(); await WaitForThumbnails(window);
        FindIn<Expander>(window, "LibraryOrganizationDetails").IsExpanded = true; await Settle();
        Tile().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
        Check("click while organising keeps the catalog", window.IsVisible && _editor.SelectedSetId == painted.Id && _editor.CurrentVariant is not null);
        // The passport of the chosen card, edited in the catalog window: the tool takes it at once.
        var rotationToggle = FindIn<Avalonia.Controls.Primitives.ToggleButton>(window, "PassportRandomRotationToggle");
        rotationToggle.BringIntoView(); await Settle();
        rotationToggle.IsChecked = true;
        rotationToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
        var sizeBox = FindIn<TextBox>(window, "PassportRandomSizeSliderBox");
        sizeBox.Text = "40";
        sizeBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter }); await Settle();
        var passport = _catalog.FindSet(painted.Id)!.Placement;
        Check("passport flags saved from the catalog window", passport is { RandomRotation: true, RandomSizePercent: 40 });
        Check("tool takes passport flags as starting values", _editor.StampRotationJitterDegrees == 180 && _editor.StampSizeJitterPercent == 40);
        await WaitForThumbnails(window);
        Capture(window, "passport");

        // Quick filters above the grid, with the archive button of the chosen card.
        void Press(string name)
        {
            var control = FindIn<Button>(window, name);
            if (control is Avalonia.Controls.Primitives.ToggleButton toggle) toggle.IsChecked = !(toggle.IsChecked == true);
            control.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        Press("SetArchiveButton"); await Settle();
        Check("archive button hides the card", _catalog.FindSet(painted.Id)!.Archived
            && _editor.LibrarySets.All(s => s.SetId != painted.Id) && _catalog.Records.Count == 5);
        Press("QuickFilterArchived"); await Settle(); await WaitForThumbnails(window);
        Check("archived switch shows the card marked", _editor.LibraryShowArchived
            && _editor.LibrarySets.Any(s => s.SetId == painted.Id && s.Archived)
            && window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "в архиве"));
        Capture(window, "archived");
        Press("SetArchiveButton"); await Settle();
        Press("QuickFilterArchived"); await Settle();
        Check("card returns from the archive", !_catalog.FindSet(painted.Id)!.Archived && !_editor.LibraryShowArchived
            && _editor.LibrarySets.Any(s => s.SetId == painted.Id));
        Press("QuickFilterUsedInMap"); await Settle();
        Check("used on this map is empty before placing", _editor.LibraryUsedInMapOnly && _editor.LibrarySets.Count == 0);
        Press("QuickFilterUsedInMap"); await Settle();
        await WaitForThumbnails(window);
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

        // 4. Four stamps on the map, then the same map at 4× zoom.
        var objects = () => _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count);
        _editor.SetStampScalePercent(220, commit: true); await Settle();
        Point(330, 420);
        Check("owner mountain variant placed", objects() == 1);
        _editor.ChooseGalleryVariant(sample4.Id, null); _editor.SetStampScalePercent(140, commit: true); await Settle();
        Point(640, 470); Point(700, 500);
        _editor.ChooseGalleryVariant(sample5.Id, null); _editor.SetStampScalePercent(150, commit: true); await Settle();
        Point(560, 380);
        Check("four stamps placed from three sets", objects() == 4);
        _editor.HandleEscape(); await Settle();
        await Rendered(); Capture(_view.Canvas, "canvas");
        _editor.Session.Viewport.Set(380, 330, 4); await Settle(); await Rendered();
        Capture(_view.Canvas, "canvas-zoom4");
        _editor.Undo(); await Settle();
        Check("undo removes the last stamp", objects() == 3);

        // Alt+wheel over the canvas: through the variants of the set, then on into the next set.
        _editor.ClearLibraryFilters(); _view.RefreshLibraryPanel(); await Settle();
        _editor.ChooseGalleryVariant(painted.Id, painted.VariantIds[0]); await Settle();
        var centre = (X: _view.Canvas.Bounds.Width / 2, Y: _view.Canvas.Bounds.Height / 2);
        var path = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            _view.Canvas.WheelForSmoke(centre.X, centre.Y, 1, KeyModifiers.Alt); await Settle();
            path.Add(_editor.SelectedSet!.SetId + "/" + _editor.CurrentVariant!.Id);
        }
        Check("alt wheel walks the variants then leaves the set",
            path[0] == painted.Id + "/" + painted.VariantIds[1] && path[1] == painted.Id + "/" + painted.VariantIds[2]
            && !path[2].StartsWith(painted.Id, StringComparison.Ordinal) && objects() == 3);
        _view.Canvas.WheelForSmoke(centre.X, centre.Y, -1, KeyModifiers.Alt); await Settle();
        Check("alt wheel back returns to the last variant", _editor.SelectedSet!.SetId == painted.Id
            && _editor.CurrentVariant!.Id == painted.VariantIds[2]);
        var zoomBefore = _editor.Session.Viewport.Scale;
        _view.Canvas.WheelForSmoke(centre.X, centre.Y, 1, KeyModifiers.None); await Settle();
        Check("plain wheel still zooms", Math.Abs(_editor.Session.Viewport.Scale - zoomBefore) > 1e-9
            && _editor.CurrentVariant!.Id == painted.VariantIds[2]);

        await _catalog.FlushMetadataAsync();
        await using (var reopened = new AssetCatalog(_catalog.Root))
            Check("only explicit imports survive catalog reopen", reopened.Records.Count == 5 && reopened.Sets.Count == 3);
        var preferences = new global::Map.App.Services.EditorPreferencesStore(Path.Combine(_workspace, "gallery-preferences.json"));
        preferences.Save(new EditorLocalPreferences(LibraryTileSize: 160, LibraryShowNames: false));
        var loaded = preferences.Load();
        Check("gallery starts at defaults after disk reload", loaded.LibraryTileSize == 112 && loaded.LibraryShowNames);
        await CheckTerrain();
        await CheckRoles(painted.Id);
    }

    private async Task CheckRoles(string setId)
    {
        async Task<ComboBox> OpenPassport(string phase)
        {
            _editor.ActiveTool = EditorTool.Stamp;
            _editor.ClearLibraryFilters(); _view.RefreshLibraryPanel();
            _view.OpenAssetCatalog(); await Settle();
            var window = _view.AssetCatalogWindowForSmoke!;
            // DEC-070: the passport button on the card, with the organisation still folded (no hidden Ctrl+click rule).
            FindIn<Expander>(window, "LibraryOrganizationDetails").IsExpanded = false;
            await Settle(); await WaitForThumbnails(window);
            var tile = FindIn<Button>(window, "GalleryCover_" + Sanitize(setId));
            tile.BringIntoView(); await Settle();
            var passport = FindIn<Button>(window, "GalleryPassport_" + Sanitize(setId));
            passport.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(); await Settle();
            var box = FindIn<ComboBox>(window, "PassportAssetRoleBox");
            Check("role passport opened by the card's passport button " + phase, window.IsVisible && _editor.SelectedSetId == setId
                && box.IsEffectivelyVisible && box.IsEnabled && ToolTip.GetTip(passport) is string tip && tip.Length > 0);
            return box;
        }

        string? DiskRole() => JsonNode.Parse(File.ReadAllText(Path.Combine(_catalog.Root, "catalog.json")))!["sets"]!
            .AsArray().Single(s => s!["id"]!.GetValue<string>() == setId)!["placement"]?["role"]?.GetValue<string>();

        async Task SetRole(ComboBox box, AssetRole role, string diskValue)
        {
            var document = _editor.Session.Document;
            var undo = _editor.Session.UndoDepth; var redo = _editor.Session.RedoDepth; var revision = _editor.Session.Revision;
            box.SelectedIndex = (int)role; await Settle(); await _catalog.FlushMetadataAsync();
            Check("role " + diskValue + " saved explicitly in catalog.json", DiskRole() == diskValue
                && _catalog.FindSet(setId)!.Placement?.Role == role);
            Check("role " + diskValue + " leaves document and history unchanged", ReferenceEquals(document, _editor.Session.Document)
                && undo == _editor.Session.UndoDepth && redo == _editor.Session.RedoDepth && revision == _editor.Session.Revision);
        }

        async Task Reopen()
        {
            var document = _editor.Session.Document; var root = _catalog.Root;
            _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches();
            _editor.CancelPlacement(); await _catalog.DisposeAsync();
            _catalog = new AssetCatalog(root);
            _editor = new EditorViewModel(document, null, false, new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "role-recovery"))),
                new LocalRecent(), new LocalPicker(Path.Combine(_workspace, "roles.mapproj")), new LocalPrompts(), new LocalLog(),
                assetCatalog: _catalog, localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
            _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { });
            _window.Content = _view; await Settle();
        }

        var roleBox = await OpenPassport("initial");
        await SetRole(roleBox, AssetRole.Tree, "tree");
        Capture(_view.AssetCatalogWindowForSmoke!, "passport-role-tree");
        await Reopen(); roleBox = await OpenPassport("tree reopen");
        Check("new catalog and passport reopen as Tree", roleBox.SelectedIndex == (int)AssetRole.Tree
            && Equals(roleBox.SelectedItem, "Дерево"));
        Capture(_view.AssetCatalogWindowForSmoke!, "passport-role-tree-reopened");
        await SetRole(roleBox, AssetRole.None, "none");
        await Reopen(); roleBox = await OpenPassport("none reopen");
        Check("new catalog and passport reopen as explicit None", roleBox.SelectedIndex == (int)AssetRole.None
            && Equals(roleBox.SelectedItem, "Без роли") && DiskRole() == "none");
        Capture(_view.AssetCatalogWindowForSmoke!, "passport-role-none-reopened");
    }

    private async Task CheckEmptyArtwork()
    {
        Check("retired texture pixels remain absent", !typeof(MapTextureLibrary).Assembly.GetManifestResourceNames()
            .Any(name => name.StartsWith("Map.Rendering.Textures.", StringComparison.Ordinal)));
        Check("legacy texture identifiers have no available images", MapFillTextures.All
            .All(t => MapTextureLibrary.Image(t.Id) is null));
        Check("removed regional artwork cannot be reimported", !RegionalArtPack.IsAvailable);
        var availableMaterials=MapFillTextures.All.Count(t=>MapTextureLibrary.Image(t.Id) is not null);
        Check("new water uses plain colour", new RiverStyle().TextureId is null);
        Check("land brush starts with colour and no recent materials", _editor.LandPaintWithColor && _editor.RecentLandMaterials.Count == 0);
        _editor.SetLandPaintAction("Texture");
        Check("retired material cannot be selected", !_editor.ChooseLandMaterial("meadow"));
        _editor.SetLandPaintAction("Color");

        var land = _editor.Session.Document.Layers.OfType<LandLayer>().Single();
        _editor.SelectLayer(land.Id); _editor.ActiveTool = EditorTool.Land;
        _editor.SetLandPaintMode(true); await Settle();
        T Find<T>(string name) where T : Control
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                if (_view.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) is { } control) return control;
                var folded = _view.GetVisualDescendants().OfType<Expander>().Where(e => !e.IsExpanded).ToArray();
                if (folded.Length == 0) break;
                foreach (var expander in folded) expander.IsExpanded = true;
                _view.UpdateLayout();
            }
            throw new InvalidOperationException("Control not found: " + name);
        }
        Check("material brush availability matches installed content", Find<Button>("LandPaintActionTexture").IsEnabled==(availableMaterials>0));
        _editor.BrushSettings = _editor.BrushSettings with { Radius = 16, Softness = 0, Color = "#FFB08040" };
        _editor.Session.Viewport.Set(0, 0, 1); await Settle();
        var p = _editor.Session.Viewport.WorldToScreen(80, 80);
        _view.Canvas.PressForSmoke(p.X, p.Y); _view.Canvas.ReleaseForSmoke(p.X, p.Y);
        await Rendered();
        var painted = (LandLayer)_editor.Session.Document.FindLayer(land.Id)!;
        Check("colour stroke paints without artwork", painted.PaintTiles.TileCount > 0 && ReferenceEquals(land.GeometryTiles, painted.GeometryTiles));
        Check("native canvas renders empty artwork library", _view.Canvas.FramesRendered > 0
            && ReferenceEquals(_view.Canvas.LastRenderedDocument, _editor.Session.Document));
        _editor.Undo();
        Check("colour stroke remains undoable", ((LandLayer)_editor.Session.Document.FindLayer(land.Id)!).PaintTiles.TileCount == 0);
        Capture(_view, "empty-materials-editor");

        _editor.SetLandPaintMode(false); await Settle();
        Find<Button>("LandTexturePickerCatalogButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        var dialog = ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Windows.Single(w => w.Name == "MaterialCatalogWindow");
        dialog.UpdateLayout();
        var expectedGroundCards = MapFillTextures.Of(MapFillTextureKind.Ground)
            .Where(t => MapTextureLibrary.Image(t.Id) is not null).Select(t => "MaterialCatalog_" + t.Id).ToHashSet();
        Check("material catalog shows only available content", dialog.IsVisible
            && expectedGroundCards.SetEquals(dialog.GetVisualDescendants().OfType<WrapPanel>()
                .Single(t => t.Name == "MaterialCatalogTiles").Children.Select(c => c.Name)));
        Capture(dialog, "empty-materials-catalog");
        dialog.Close();

        await _catalog.FlushMetadataAsync();
        await using var reopened = new AssetCatalog(_catalog.Root);
        Check("reopened library remains empty", reopened.Status == AssetLibraryStatus.Ready
            && reopened.Records.Count == 0 && reopened.Sets.Count == 0 && reopened.Collections.Count == 0 && reopened.Recent.Count == 0);
    }

    private async Task CheckTerrain()
    {
        _view.Canvas.DisposeRenderCaches();
        var land = new LandLayer(LayerId.New(), "Верхнее плато", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray()),
            CurrentLook.Land()) { Level = 2 };
        var low = new River { Name = "Нижняя река", Level = 0, Style = CurrentLook.River() with { CutsLand = false, ClipToLand = false },
            Channels = [RiverEditor.NewChannel([new(40, 128), new(470, 128)], 24)] };
        var high = new River { Name = "Верхняя река", Level = 3, Style = low.Style,
            Channels = [RiverEditor.NewChannel([new(128, 40), new(128, 330)], 24)] };
        var owner = new ObjectLayer(LayerId.New(), "Вода с маской", true, false, .6, []) { Rivers = [high, low] };
        var document = new MapDocument(DocumentId.New(), "Вода и уровни", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 384, "px", "#FFE9E0C8"),
            [CurrentLook.Page("Фон", "#FFE9E0C8"), land, owner]);
        _editor = new EditorViewModel(document, null, false, new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "terrain-recovery"))),
            new LocalRecent(), new LocalPicker(Path.Combine(_workspace, "terrain.mapproj")), new LocalPrompts(), new LocalLog(),
            assetCatalog: _catalog, localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { });
        _window.Content = _view; await Settle();
        _editor.Session.Viewport.Set(0, 0, 1.4); _editor.ActiveTool = EditorTool.Select;
        Point(128, 128); await Settle();
        Check("native crossing selects higher water", _editor.SelectedRiver?.Id == high.Id);
        _editor.Session.Execute(new RiverCommand(owner.Id, high.Id, high with { Visible = false }, "Скрыть верхнюю воду"));
        _editor.ClearWaterSelection(); Point(128, 128); await Settle();
        Check("native plateau covers lower water selection", _editor.SelectedLand?.Id == land.Id && _editor.WaterSelectionCount == 0);
        _editor.Session.Execute(new RiverCommand(owner.Id, low.Id, low with { Locked = true }, "Блокировка"));
        _editor.SelectWater(low.Id); var revision = _editor.Session.Revision;
        Check("native locked water refuses reorder", !_editor.ReorderSelectedWater(-1, true) && revision == _editor.Session.Revision);
        _editor.Session.Execute(new RiverCommand(owner.Id, high.Id, high, "Показать верхнюю воду"));
        var masked = owner with { Mask = new LayerMask(TileMap.Empty(1).With(new TileKey(0, 0), new byte[TileMap.TileSize * TileMap.TileSize]), true) };
        _editor.Session.Execute(UpdateMaskCommand.CopyMask(masked, owner));
        _editor.ClearWaterSelection(); Point(128, 128); await Settle(); await Rendered();
        Check("native mask hides upper water from picking", _editor.SelectedLand?.Id == land.Id && _editor.WaterSelectionCount == 0);
        Capture(_view, "terrain-masked");
        _editor.Undo(); _editor.ClearLandSelection(); Point(128, 128); await Settle();
        Check("native undo restores masked water", _editor.SelectedRiver?.Id == high.Id);
        await Rendered(); Capture(_view, "terrain-restored");
    }

    /// <summary>A named control of a window, opening folded sections the way a user would.</summary>
    private static T FindIn<T>(Window window, string name) where T : Control
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) is { } found) return found;
            var folded = window.GetVisualDescendants().OfType<Expander>().Where(e => !e.IsExpanded).ToArray();
            if (folded.Length == 0) break;
            foreach (var expander in folded) expander.IsExpanded = true;
            window.UpdateLayout();
        }
        throw new InvalidOperationException(name + " is not in the catalog window");
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
        var path = Path.ChangeExtension(Options.OutputPath, suffix + ".png");
        image.Save(path, PngBitmapEncoderOptions.Default);
        _report.Frames.Add(path);
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
    }
}
