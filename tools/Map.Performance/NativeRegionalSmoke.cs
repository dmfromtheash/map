using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using SkiaSharp;
using Map.App.Views;
using Map.Assets;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using Map.Rendering;

namespace Map.Performance;

internal sealed class RegionalReport
{
    public string Observation { get; } = "Own native Map controls, ShowActivated=false, off-screen, isolated library and profile, no system input.";
    public Dictionary<string, bool> Checks { get; } = [];
    public Dictionary<string,double> Measurements { get; } = [];
    public string? Failure { get; set; }
    public bool GatePassed => Failure is null && Checks.Count > 0 && Checks.Values.All(x => x);
}

internal static class NativeRegionalSmoke
{
    public static RegionalReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Map verification requires Windows.");
        var completion = new TaskCompletionSource<RegionalReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            RegionalApplication.Options = options; RegionalApplication.Completion = completion;
            try { AppBuilder.Configure<RegionalApplication>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(8)).GetAwaiter().GetResult();
    }
}

public sealed partial class RegionalApplication : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<RegionalReport> Completion = null!;
    private readonly RegionalReport _report = new();
    private Window _window = null!;
    private EditorView? _view;
    private EditorViewModel _editor = null!;
    private AssetCatalog _catalog = null!;
    private ProjectStore _store = null!;
    private string _workspace = "";
    private readonly LocalRecent _recent = new();
    private LocalPicker _picker = null!;

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
        _workspace = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, Path.GetFileNameWithoutExtension(Options.OutputPath) + "-workspace");
        // This profile exports folders that deliberately cannot overwrite an existing GM/player
        // package (including renamed-obsidian); every run needs its own complete workspace.
        if (Options.Profile == "native-player-package") _workspace += "-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(_workspace);
        if (Options.Profile == "native-player-package") Console.WriteLine("native-player-package workspace: " + _workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        Environment.SetEnvironmentVariable("MAP_UI_TEST_RECOVERY_ROOT", Path.Combine(_workspace, "recovery"));
        Environment.SetEnvironmentVariable("MAP_UI_TEST_SHORTCUT_SETTINGS_PATH", Path.Combine(_workspace, "profile", "shortcuts.json"));
        Map.App.Services.TestWindows.Guard();
        _catalog = new AssetCatalog(Path.Combine(_workspace, "catalog"));
        _store = new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery")));
        _picker = new LocalPicker(Path.Combine(_workspace, "Тихий атлас.mapproj"));
        _window = Options.Profile == "native-editor-defaults" ? CreateDefaultsWindow(seedLegacy: true) : new Window();
        _window.Width = 1440; _window.Height = 900;
        _window.ShowActivated = false; _window.ShowInTaskbar = false;
        _window.Title = "Map · локальная проверка";
        _window.WindowStartupLocation = WindowStartupLocation.Manual;
        _window.Position = new PixelPoint(-32000, -32000);
        _window.Opened += async (_, _) =>
        {
            try { await Execute(); }
            catch (Exception ex) { _report.Failure = ex.ToString(); if (Options.Profile is not ("native-player-package" or "native-export-wins" or "native-library-wins" or "native-poster-export") && _window.Content is Control control) Capture(control, "failure"); }
            finally
            {
                if (Options.Profile == "native-editor-defaults" && _view is not null)
                    _editor.Session.MarkSaved(_editor.Session.CaptureSaveCheckpoint());
                _view?.CloseAuxiliaryWindowsForSmoke(); _view?.Canvas.DisposeRenderCaches();
                await _catalog.DisposeAsync(); _window.Close(); Completion.TrySetResult(_report); desktop.Shutdown();
            }
        };
        desktop.MainWindow = _window; base.OnFrameworkInitializationCompleted();
    }

    private async Task Execute()
    {
        if(Options.Profile=="native-atlas") { await VerifyAtlas(); return; }
        if(Options.Profile=="native-regional-reopen") { await VerifySavedMap(); return; }
        if(Options.Profile=="native-regional-surfaces") { await VerifySurfaceSizes(); return; }
        if(Options.Profile=="native-regional-finish") { await FinishRegion(); return; }
        if(Options.Profile=="native-area-explore") { await ExploreAreas(); return; }
        if(Options.Profile=="native-asset-opacity") { await VerifyAssetOpacity(); return; }
        if(Options.Profile=="native-editor-defaults") { await VerifyEditorDefaults(); return; }
        if(Options.Profile=="native-map-categories") { await VerifyMapCategories(); return; }
        if(Options.Profile=="native-hex-category") { await VerifyHexCategory(); return; }
        if(Options.Profile=="native-hex-workshop") { await VerifyHexWorkshop(); return; }
        if(Options.Profile=="native-export-wins") { await VerifyExportWins(); return; }
        if(Options.Profile=="native-poster-export") { await VerifyPosterExport(); return; }
        if(Options.Profile=="native-library-wins") { await VerifyLibraryWins(); return; }
        if(Options.Profile=="native-player-package") { await VerifyPlayerPackage(); return; }
        if(Options.Profile=="native-hex-panel-shots") { await HexPanelShots(); return; }
        var dashboard = new DashboardViewModel(_store, _recent, new LocalLog(), _picker);
        _window.Content = new DashboardView(dashboard, () => Task.CompletedTask, () => Task.CompletedTask,
            _ => Task.CompletedTask, _ => Task.CompletedTask, templateRequested: () => Task.CompletedTask,
            backupRequested: () => Task.CompletedTask);
        await Settle(); Capture((Control)_window.Content, "01-dashboard");
        _window.Width=960;_window.Height=640;await Settle();Capture((Control)_window.Content,"01b-dashboard-minimum");
        _window.Width=1440;_window.Height=900;
        var blank = new MapDocument(DocumentId.New(),"Новая карта",DateTimeOffset.UtcNow,new CanvasDefinition(1024,768,"px","#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(),"Фон",true,false,1,"#FFFFFFFF")]);
        _editor = new EditorViewModel(blank,null,false,_store,_recent,_picker,new LocalPrompts(),new LocalLog(),assetCatalog:_catalog,
            localPreferences:new EditorLocalPreferences(PrimaryAutosaveEnabled:false));
        _view = new EditorView(_editor,()=>Task.CompletedTask,()=>{},()=>{});_window.Content=_view;await Settle();
        _view.OpenAssetCatalog();await Settle();Capture(_view.AssetCatalogWindowForSmoke!,"01c-empty-catalog");
        Check("opening an empty library does not import content",_catalog.Records.Count==0);
        Click(_view.AssetCatalogWindowForSmoke!,"EmptyLibraryRegionalPackButton");
        await WaitUntil(()=>_catalog.Records.Count==RegionalArtPack.Load().Assets.Length,"explicit UI pack import");
        await WaitUntil(()=>_view.AssetCatalogWindowForSmoke!.GetVisualDescendants().OfType<Image>().Count(i=>i.Source is not null)>=7,
            "catalogue thumbnails after import");
        await Settle();Capture(_view.AssetCatalogWindowForSmoke!,"01d-imported-catalog");
        _view.CloseAuxiliaryWindowsForSmoke();_view.Canvas.DisposeRenderCaches();
        var pack = RegionalArtPack.Import(_catalog);
        var count = _catalog.Records.Count;
        RegionalArtPack.Import(_catalog);
        Check("explicit pack import is idempotent", _catalog.Records.Count == count && count == pack.Manifest.Assets.Length);
        MapDocument document;
        if(Options.Profile == "native-regional")
        {
            var wizard=new WizardWindow(_catalog) { ShowActivated=false,ShowInTaskbar=false,
                WindowStartupLocation=WindowStartupLocation.Manual,Position=new PixelPoint(-32000,-32000) };
            wizard.Show();await Settle();Capture(wizard,"02-wizard-category");
            Click(wizard,"ChooseCategoryButton");await Settle();
            wizard.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Name=="WizardStartingPoint").SelectedIndex=1;
            await Settle();Capture(wizard,"02a-blank-region-choice");
            wizard.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Name=="WizardStartingPoint").SelectedIndex=2;
            await Settle();Capture(wizard,"02-wizard");
            Click(wizard,"CreateMapButton");
            var deadline=DateTime.UtcNow.AddMinutes(2);
            while(wizard.CreatedDocument is null && DateTime.UtcNow<deadline) await Task.Delay(100);
            document=wizard.CreatedDocument??throw new InvalidOperationException("Wizard did not create the region.");
            wizard.Close();
            Check("native wizard creates editable region",document.Layers.OfType<ObjectLayer>().Sum(l=>l.PlacementAreas.Length)>=5);
        }
        else document = RegionalMapFactory.ArtStudy(pack, _catalog);
        _editor = new EditorViewModel(document, null, false, _store, _recent, _picker,
            new LocalPrompts(), new LocalLog(), assetCatalog: _catalog,
            exportDestinationPicker:new ExportPicker(Path.Combine(_workspace,"Долина Листвы.png")),
            imageExporter:new ImageExportEngine(),
            localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { });
        _window.Content = _view;
        await Settle(); _view.Canvas.FitToCanvas();
        await Settle(); Capture(_view, "02-art-study"); Capture(_view.Canvas, "03-art-canvas");
        _view.OpenAssetCatalog(); await Task.Delay(500); Capture(_view.AssetCatalogWindowForSmoke!, "04-catalog");
        _view.AssetCatalogWindowForSmoke!.Hide();
        if(Options.Profile=="native-regional")
        {
            await VerifyRegionalRoutes();
            document=_editor.Session.Document;
            _picker.Path=Path.Combine(_workspace,"Долина Листвы.mapproj");
            Click(_view,"SaveButton");
            await WaitUntil(()=>File.Exists(_picker.Path) && !_editor.Session.IsDirty,"native save");
            Click(_view,"ExportButton");await _editor.WaitForExportPreviewAsync();await Settle();Capture(_view.ExportWindowForSmoke!,"12-export");
            Check("export plan ready",_editor.CanStartExport);
            Check("production image export",await _editor.StartExportAsync());
            var reopened=MapProjectStore.Open(_picker.Path).Document;
            Check("reopened objects and groups",reopened.Layers.OfType<ObjectLayer>().Sum(l=>l.Objects.Count)==document.Layers.OfType<ObjectLayer>().Sum(l=>l.Objects.Count)
                && reopened.Layers.OfType<ObjectLayer>().Sum(l=>l.PlacementAreas.Length)==document.Layers.OfType<ObjectLayer>().Sum(l=>l.PlacementAreas.Length));
            Check("all images embedded",reopened.Assets.All(a=>a.Storage==ProjectAssetStorage.Embedded && a.EmbeddedContent is {Length:>0}));
            using (var beforeImages = new ExportStampImageSource(document,null,_catalog))
            using (var afterImages = new ExportStampImageSource(reopened,_picker.Path,null))
                Check("reopened render equals saved render",ThumbnailRenderer.RenderPng(document,1120,840,beforeImages)
                    .SequenceEqual(ThumbnailRenderer.RenderPng(reopened,1120,840,afterImages)));
            _view.CloseAuxiliaryWindowsForSmoke();_view.Canvas.DisposeRenderCaches();
            _editor = new EditorViewModel(reopened,_picker.Path,false,_store,_recent,_picker,new LocalPrompts(),new LocalLog(),
                assetCatalog:_catalog,localPreferences:new EditorLocalPreferences(PrimaryAutosaveEnabled:false));
            _view = new EditorView(_editor,()=>Task.CompletedTask,()=>{},()=>{});_window.Content=_view;
            await Settle();_view.Canvas.FitToCanvas();await Settle();Capture(_view,"13-reopened");
            return;
        }
        using var images = new ExportStampImageSource(document, null, _catalog);
        var preview = ThumbnailRenderer.RenderPng(document, 1120, 640, images);
        File.WriteAllBytes(Path.Combine(_workspace, "art-study.png"), preview);
        _store.Save(document, _picker.Path, new SaveMetadata("Map regional art study", DateTimeOffset.UtcNow),
            ThumbnailRenderer.RenderPng(document, stamps: images));
        Check("art study reopens with all embedded images", MapProjectStore.Open(_picker.Path).Document.Assets.Count == count);
    }

    private async Task VerifyRegionalRoutes()
    {
        var view=_view!;
        _editor.ActiveTool=EditorTool.Stamp;await Settle();Capture(view,"05-creation-panel");
        var original=_editor.Session.Document;
        Click(view,"RegionalForestButton");await Settle();
        var settings=_editor.StampToolSettings;var variant=_editor.CurrentVariant!.Id;
        Click(view,"StampNextVariantButton");
        Check("variant switch keeps size and placement mode",variant!=_editor.CurrentVariant!.Id
            && settings.ScalePercent==_editor.StampToolSettings.ScalePercent && settings.Mode==_editor.StampPlacementMode);
        Click(view,"StampFavoriteToggle");Check("compact favorites reachable",view.StampCompactFavoriteCountForSmoke>0);
        _view!.OpenAssetCatalog();await Settle();
        _view.AssetCatalogWindowForSmoke!.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="AssetSearchBox").Text=_editor.SelectedSet!.Name;
        await Settle();Capture(_view.AssetCatalogWindowForSmoke!,"05a-catalog-search");
        Check("native catalogue search finds current family",_editor.LibrarySets.Any(s=>s.SetId==_editor.SelectedSetId));
        _view.AssetCatalogWindowForSmoke!.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="AssetSearchBox").Text="";
        _view.AssetCatalogWindowForSmoke!.Hide();
        view.GetVisualDescendants().OfType<Expander>().Single(e=>e.Name=="AppearanceTransferPanel").IsExpanded=true;await Settle();
        view.GetVisualDescendants().OfType<Expander>().Single(e=>e.Name=="RegionalPresetsPanel").IsExpanded=true;await Settle();
        view.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="RegionalPresetName").Text="Лес долины";
        Click(view,"SavePlacementPresetButton");var preset=_editor.RegionalPresets.Single(p=>p.Placement is not null);
        Click(view,"RegionalRidgeButton");Click(view,"LoadRegionalPresetButton");
        Check("native placement preset reloads its settings",_editor.StampPlacementMode==StampPlacementMode.FillArea
            && _editor.StampToolSettings.ScalePercent==settings.ScalePercent && _editor.SelectedSetId==settings.AssetSetId);
        var nameBox=view.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="RegionalPresetName");nameBox.Text="Лес долины · мой";
        Click(view,"RenameRegionalPresetButton");Check("native preset rename",_editor.RegionalPresets.Single(p=>p.Id==preset.Id).Name==nameBox.Text);
        view.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="LoadRegionalPresetButton").BringIntoView();
        await Settle();Capture(view,"05b-presets");
        view.GetVisualDescendants().OfType<Expander>().Single(e=>e.Name=="AppearanceTransferPanel").IsExpanded=false;
        _editor.CancelPlacement();
        foreach(var mode in new[]{RegionalPlacementMode.Forest,RegionalPlacementMode.Ridge,RegionalPlacementMode.Settlement})
        {
            Click(view,"Regional"+mode+"Button");await Settle();
            var depth=_editor.Session.UndoDepth;
            foreach(var (x,y) in new[]{(2800d,150d),(3180d,180d),(3160d,400d),(2800d,400d)})
            { var p=_editor.Session.Viewport.WorldToScreen(x,y);view.Canvas.ClickForSmoke(p.X,p.Y); }
            await Settle(); // allow the real binding to enable its commit button after the last node
            Click(view,mode==RegionalPlacementMode.Ridge?"FinishEditablePathButton":"FinishEditableAreaButton");
            Check(mode+" finishes",mode==RegionalPlacementMode.Ridge?_editor.SelectedEditablePath is not null:_editor.SelectedEditableArea is not null);
            Check(mode+" contains editable objects", mode==RegionalPlacementMode.Ridge
                ? _editor.SelectedEditablePath!.Members.Length > 0 : _editor.SelectedEditableArea!.MemberCount > 0);
            await Settle();Capture(view,"06-created-"+mode);
            Check(mode+" one undo",_editor.Session.UndoDepth==depth+1);
            Check(mode+" remembered in recent",view.StampCompactRecentCountForSmoke>0);
            _editor.Undo();_editor.CancelPlacement();
        }
        var area=_editor.EditableAreas.First();_editor.SelectEditableArea(area.Id);await Settle();
        var node=area.Nodes[0];var from=_editor.Session.Viewport.WorldToScreen(node.X,node.Y);
        var to=_editor.Session.Viewport.WorldToScreen(node.X-45,node.Y+24);
        var oldObjects=_editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l=>l.Objects).ToDictionary(o=>o.Id);
        view.Canvas.PressMoveReleaseForSmoke(from.X,from.Y,to.X,to.Y);await Settle();Capture(view,"07-late-forest-edit");
        var changed=_editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l=>l.Objects).Count(o=>!oldObjects.TryGetValue(o.Id,out var old)||old!=o);
        Check("local forest edge changes only a fraction",changed>0 && changed<oldObjects.Count/3);
        var edited=_editor.Session.Document;_editor.Undo();_editor.Redo();Check("late edit undo redo exact",ReferenceEquals(edited,_editor.Session.Document));
        var ridge=_editor.EditablePaths.First();_editor.SelectEditablePath(ridge.Id);await Settle();
        var ridgeNode=ridge.Nodes[3];from=_editor.Session.Viewport.WorldToScreen(ridgeNode.X,ridgeNode.Y);
        to=_editor.Session.Viewport.WorldToScreen(ridgeNode.X+28,ridgeNode.Y-18);
        view.Canvas.PressMoveReleaseForSmoke(from.X,from.Y,to.X,to.Y);await Settle();Capture(view,"07b-late-ridge-edit");
        Check("ridge node edit stays local",_editor.LastPathRegeneratedSegments>0 && _editor.LastPathRegeneratedSegments<=2);
        edited=_editor.Session.Document;_editor.Undo();_editor.Redo();Check("ridge edit undo redo exact",ReferenceEquals(edited,_editor.Session.Document));
        _editor.CancelPlacement();var road=_editor.Roads.Single();_editor.SelectRoad(road.Id,road.Branches[1].Id);
        _editor.RoadLevel=RoadSelectionLevel.Node;await Settle();
        var shared=road.Nodes.Single(n=>n.Id==road.Branches[1].Vertices[0].NodeId);
        from=_editor.Session.Viewport.WorldToScreen(shared.X,shared.Y);to=_editor.Session.Viewport.WorldToScreen(shared.X+22,shared.Y-26);
        view.Canvas.PressMoveReleaseForSmoke(from.X,from.Y,to.X,to.Y);await Settle();Capture(view,"07c-late-road-edit");
        var changedRoad=_editor.Roads.Single();
        Check("road junction moves without losing branch links",changedRoad.Nodes.Count(n=>road.Nodes.Single(old=>old.Id==n.Id)!=n)==1
            && changedRoad.Branches.SequenceEqual(road.Branches));
        edited=_editor.Session.Document;_editor.Undo();_editor.Redo();Check("road edit undo redo exact",ReferenceEquals(edited,_editor.Session.Document));
        _editor.CancelPlacement();_editor.ActiveTool=EditorTool.Select;
        view.GetVisualDescendants().OfType<Expander>().Single(e=>e.Name=="AppearanceTransferPanel").IsExpanded=true;
        await Settle();
        var labels=_editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l=>l.Objects).Where(o=>o.Text is not null).ToArray();
        _editor.SelectObject(labels[1].Id);Click(view,"CopyAppearanceButton");_editor.SelectObject(labels[2].Id);
        var before=_editor.Session.Document;Click(view,"PreviewAppearanceButton");await Settle();Capture(view,"08-appearance-preview");
        Check("appearance preview leaves document",ReferenceEquals(before,_editor.Session.Document));
        Click(view,"ApplyAppearanceButton");Check("appearance committed",!ReferenceEquals(before,_editor.Session.Document));
        _editor.Undo();
        view.GetVisualDescendants().OfType<Expander>().Single(e=>e.Name=="AppearanceTransferPanel").IsExpanded=false;
        _editor.DeselectObject();view.Canvas.FitToCanvas();await Settle();Capture(view,"09-region-editor");
        _window.Width=960;_window.Height=640;await Settle();view.Canvas.FitToCanvas();await Settle();Capture(view,"10-editor-minimum");
        _window.Width=1440;_window.Height=900;await Settle();view.Canvas.FitToCanvas();await Settle();
        using var bitmap=await view.Canvas.CaptureGpuFrameForSmokeAsync();using var png=bitmap.Encode(SKEncodedImageFormat.Png,100);
        File.WriteAllBytes(Path.ChangeExtension(Options.OutputPath,"11-native-map.png"),png.ToArray());
        Console.WriteLine("Regional stamps: "+_editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l=>l.Objects.Count(o=>o.Stamp is not null)));
    }

    private static void Click(Control root,string name)
    { var button=root.GetVisualDescendants().OfType<Button>().Single(b=>b.Name==name);
      // Visible as well as enabled: panels shown once stay in the tree hidden, and a check must
      // not press a button the user cannot see.
      if(!button.IsEffectivelyEnabled || !button.IsEffectivelyVisible)throw new InvalidOperationException("Button not available to the user: "+name);
      button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }

    private async Task VerifySurfaceSizes()
    {
        var path=Environment.GetEnvironmentVariable("MAP_REGIONAL_PROJECT")??throw new InvalidOperationException("MAP_REGIONAL_PROJECT required");
        var doc=MapProjectStore.Open(path).Document;
        _editor=new EditorViewModel(doc,path,false,_store,_recent,_picker,new LocalPrompts(),new LocalLog(),assetCatalog:_catalog,
            exportDestinationPicker:new ExportPicker(Path.Combine(_workspace,"unused.png")),imageExporter:new ImageExportEngine(),
            localPreferences:new EditorLocalPreferences(PrimaryAutosaveEnabled:false));
        _view=new EditorView(_editor,()=>Task.CompletedTask,()=>{},()=>{});_window.Content=_view;
        await Settle();await _editor.ImportRegionalPackAsync();_view.RefreshLibraryPanel();
        _view.OpenAssetCatalog();await Settle();
        var catalog=_view.AssetCatalogWindowForSmoke!;
        catalog.Width=catalog.MinWidth;catalog.Height=catalog.MinHeight;await Task.Delay(500);
        Capture(catalog,"catalog-minimum");
        _report.Measurements["catalog-min-width"]=catalog.Width;_report.Measurements["catalog-min-height"]=catalog.Height;
        Check("minimum catalogue contains real variants",_catalog.Records.Count==29 && catalog.IsVisible);
        catalog.Hide();Click(_view,"ExportButton");await _editor.WaitForExportPreviewAsync();await Settle();
        var export=_view.ExportWindowForSmoke!;
        export.Width=export.MinWidth;export.Height=export.MinHeight;await Settle();
        Capture(export,"export-minimum");
        _report.Measurements["export-min-width"]=export.Width;_report.Measurements["export-min-height"]=export.Height;
        Check("minimum export keeps an available start action",export.GetVisualDescendants().OfType<Button>()
            .Any(b=>b.Name=="StartExportButton" && b.IsEffectivelyVisible && b.IsEffectivelyEnabled));
        Check("surface inspection preserves the saved map",ReferenceEquals(doc,_editor.Session.Document));
    }

    private async Task VerifySavedMap()
    {
        var wizard = new WizardWindow(_catalog) { ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000) };
        wizard.Show(); await Settle();
        Click(wizard, "ChooseCategoryButton"); await Settle();
        var startingPoint = wizard.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "WizardStartingPoint");
        startingPoint.SelectedIndex = 2; await Settle();
        Check("wizard shows the regional size limit", wizard.GetVisualDescendants().OfType<TextBlock>()
            .Single(c => c.Name == "WizardSizeHint").Text!.Contains("8192"));
        Check("regional wizard disables unsupported huge preset", !wizard.GetVisualDescendants().OfType<ToggleButton>()
            .Single(c => c.Name == "WizardPresetHuge").IsEnabled);
        Capture(wizard, "00-wizard-final");
        wizard.Width = 860; wizard.Height = 560; await Settle(); Capture(wizard, "00b-wizard-minimum");
        wizard.Close();
        var path=Environment.GetEnvironmentVariable("MAP_REGIONAL_PROJECT")??throw new InvalidOperationException("MAP_REGIONAL_PROJECT required");
        var doc=MapProjectStore.Open(path).Document;
        _editor=new EditorViewModel(doc,path,false,_store,_recent,_picker,new LocalPrompts(),new LocalLog(),assetCatalog:_catalog,
            localPreferences:new EditorLocalPreferences(PrimaryAutosaveEnabled:false));
        _view=new EditorView(_editor,()=>Task.CompletedTask,()=>{},()=>{});_window.Content=_view;
        await Settle();_view.Canvas.FitToCanvas();await Task.Delay(400);Capture(_view,"01-reopened-final");
        Check("embedded map opens with an empty catalogue",_catalog.Records.Count==0 && _view.Canvas.LastRenderStats.MissingAssets==0);
        _view.ShowPanelSection(PanelSection.Other);await Settle();
        _report.Measurements["normal-list-logical"]=_view.ObjectListLogicalCount;
        _report.Measurements["normal-list-realized"]=_view.ObjectListRealizedCount;
        _window.Width=960;_window.Height=640;await Settle();
        var sectionPicker = _view.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "CompactPanelSectionPicker");
        sectionPicker.SelectedIndex = 5; await Settle();
        sectionPicker.SelectedIndex = 4; await Settle();
        Check("compact section picker changes the real object section", sectionPicker.IsEffectivelyVisible
            && _view.CurrentPanelSection == PanelSection.Other && _view.ObjectListLogicalCount == 10);
        var list=_view.GetVisualDescendants().OfType<ListBox>().Single(c=>c.Name=="ObjectList");
        _report.Measurements["minimum-list-height"]=list.Bounds.Height;
        _report.Measurements["minimum-list-logical"]=_view.ObjectListLogicalCount;
        _report.Measurements["minimum-list-realized"]=_view.ObjectListRealizedCount;
        Check("minimum object list remains usable",list.Bounds.Height>=160 && _view.ObjectListRealizedCount>0);
        _view.Canvas.FitToCanvas();await Settle();Capture(_view,"02-minimum-object-list");
        _window.Width=1440;_window.Height=900;_view.ShowPanelSection(PanelSection.Layers);await Settle();
        using var export=SKBitmap.Decode(Path.ChangeExtension(path,"png"))??throw new InvalidDataException("PNG did not decode");
        Check("export keeps full canvas dimensions",export.Width==doc.Canvas.Width && export.Height==doc.Canvas.Height);
        foreach(var scale in new[]{1d,4d,8d,16d})
        {
            var left=Math.Floor(1280-_view.Canvas.Bounds.Width/scale/2);
            var top=Math.Floor(1140-_view.Canvas.Bounds.Height/scale/2);
            _editor.Session.Viewport.Set(left,top,scale);_view.Canvas.InvalidateVisual();await Task.Delay(300);
            await WaitUntil(()=>!_view.Canvas.LastFrameFastTerrain && !_view.Canvas.LastTerrainRefinementPending,"final terrain refinement");
            using var bitmap=await _view.Canvas.CaptureGpuFrameForSmokeAsync();using var png=bitmap.Encode(SKEncodedImageFormat.Png,100);
            File.WriteAllBytes(Path.ChangeExtension(Options.OutputPath,"crop-"+(scale*100)+".png"),png.ToArray());
            if(scale==1)
            {
                double sum=0;var max=0;var n=0;
                for(var y=4;y<bitmap.Height-4;y+=2)for(var x=4;x<bitmap.Width-4;x+=2)
                {
                    var a=bitmap.GetPixel(x,y);var b=export.GetPixel((int)left+x,(int)top+y);
                    var r=Math.Abs(a.Red-b.Red);var g=Math.Abs(a.Green-b.Green);var blue=Math.Abs(a.Blue-b.Blue);
                    sum+=r+g+blue;max=Math.Max(max,Math.Max(r,Math.Max(g,blue)));n++;
                }
                _report.Measurements["native-export-sampled-pixels"]=n;
                _report.Measurements["native-export-mean-channel-difference"]=sum/(n*3);
                _report.Measurements["native-export-max-channel-difference"]=max;
                Check("native map and PNG match in the inspected forest",sum/(n*3)<4);
            }
        }
        _editor.Session.Viewport.Set(Math.Floor(1280-_view.Canvas.Bounds.Width/2),
            Math.Floor(1140-_view.Canvas.Bounds.Height/2),1);
        _view.Canvas.InvalidateVisual();await Task.Delay(300);
        await WaitUntil(()=>!_view.Canvas.LastFrameFastTerrain && !_view.Canvas.LastTerrainRefinementPending,"motion initial frame");
        await CaptureMotion("motion-before");
        _view.Canvas.PanByScreenDeltaForSmoke(-64,-16);
        await CaptureMotion("motion-during");
        Check("motion screenshot captures the moving preview",_view.Canvas.LastFrameFastTerrain);
        await Task.Delay(200);
        await WaitUntil(()=>!_view.Canvas.LastFrameFastTerrain && !_view.Canvas.LastTerrainRefinementPending,"motion settled frame");
        await CaptureMotion("motion-after");
        Check("motion finishes with exact terrain",!_view.Canvas.LastFrameFastTerrain && !_view.Canvas.LastTerrainRefinementPending);
        Check("viewport motion preserves the document",ReferenceEquals(doc,_editor.Session.Document));

        async Task CaptureMotion(string name)
        {
            using var bitmap=await _view.Canvas.CaptureGpuFrameForSmokeAsync();
            using var png=bitmap.Encode(SKEncodedImageFormat.Png,100);
            File.WriteAllBytes(Path.ChangeExtension(Options.OutputPath,name+".png"),png.ToArray());
            _report.Measurements[name+"-fast-terrain"]=_view.Canvas.LastFrameFastTerrain?1:0;
        }
    }
    private static async Task WaitUntil(Func<bool> predicate,string operation)
    { var until=DateTime.UtcNow.AddMinutes(2);while(!predicate() && DateTime.UtcNow<until)await Task.Delay(100);if(!predicate())throw new TimeoutException(operation); }

    private static async Task Settle()
    { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render); await Task.Delay(180); }
    private void Capture(Control control, string name)
    {
        control.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)control.Bounds.Width), Math.Max(1, (int)control.Bounds.Height)));
        bitmap.Render(control); bitmap.Save(Path.ChangeExtension(Options.OutputPath, name + ".png"), PngBitmapEncoderOptions.Default);
    }
    private void Check(string name, bool value)
    { _report.Checks[name] = value; Console.WriteLine((value ? "ok " : "FAIL ") + name); }
    private sealed class LocalPicker(string path) : IFilePicker
    { public string Path = path; public Task<string?> PickOpenAsync() => Task.FromResult<string?>(Path);
      public Task<string?> PickSaveAsync(string suggestedFileName) => Task.FromResult<string?>(Path); }
    private sealed class ExportPicker(string path) : IExportDestinationPicker
    { public Task<string?> PickImageAsync(string name,ExportImageFormat format)=>Task.FromResult<string?>(path);
      public Task<string?> PickTilePackageAsync(string name)=>Task.FromResult<string?>(path+"-tiles"); }
    private sealed class LocalRecent : IRecentMaps
    {
        public List<RecentMapItem> Items = [];
        public IReadOnlyList<RecentMapItem> Load() => Items;
        public void Remove(string path) => Items.RemoveAll(x => x.Path == path);
        public void Touch(string path, Guid id, string title, DateTimeOffset date, byte[]? thumbnailPng = null, string mapType = "Локальная карта")
        { Remove(path); Items.Add(new(path, title, date, true, mapType, 0, thumbnailPng)); }
    }
    private sealed class LocalLog : IAppLog
    { public void Info(string message) { } public void Error(string message, Exception? exception = null) => Console.WriteLine(message + exception); }
    private sealed class LocalPrompts : IUserPrompts
    {
        public Task<CloseChoice> ConfirmCloseDirtyAsync(string title) => Task.FromResult(CloseChoice.Cancel);
        public Task<bool> ConfirmRestoreCheckpointAsync(string name) => Task.FromResult(false);
        public Task<bool> ConfirmDeleteCheckpointAsync(string name) => Task.FromResult(false);
        public Task<bool> ConfirmMigrationCopyAsync() => Task.FromResult(false);
        public Task<bool> ConfirmDeleteProjectAsync(string title, bool fileAlreadyMissing = false) => Task.FromResult(false);
    }
}
