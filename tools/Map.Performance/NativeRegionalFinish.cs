using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Map.App.Views;
using Map.Assets;
using Map.Core.Documents;
using Map.Export;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// The finishing pass of the regional map, 2026-09-22, through the real editor: a copy of the
/// delivered «Долина Листвы» gets a denser palette from existing style parameters and two
/// more forests drawn with the regional forest tool, is saved as a new project, exported to a
/// full-size PNG, reopened with an empty external catalogue and edited once more. The source
/// project is opened read-only and its bytes are checked unchanged at the end.
/// </summary>
public sealed partial class RegionalApplication
{
    private const string FinishSea = "#FF638F9C";
    private const string FinishLand = "#FFB2BC80";

    private async Task FinishRegion()
    {
        var source = Environment.GetEnvironmentVariable("MAP_REGIONAL_PROJECT") ?? throw new InvalidOperationException("MAP_REGIONAL_PROJECT required");
        var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        var original = MapProjectStore.Open(source).Document;
        // The forest tool uses the «Тихий атлас» sets: connected explicitly, in the isolated
        // catalogue of this check only, exactly as the catalogue button does.
        RegionalArtPack.Import(_catalog);
        _picker.Path = Path.Combine(_workspace, "Долина Листвы · доработка.mapproj");
        var exportPath = Path.ChangeExtension(_picker.Path, "png");
        _editor = new EditorViewModel(original, null, false, _store, _recent, _picker, new LocalPrompts(), new LocalLog(),
            assetCatalog: _catalog, exportDestinationPicker: new ExportPicker(exportPath), imageExporter: new ImageExportEngine(),
            localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
        await Settle(); _view.Canvas.FitToCanvas(); await SettledTerrain();
        await CanvasShot("finish-00-before");
        var start = _editor.Session.UndoDepth;
        var stampsBefore = Stamps(original);

        // 1. Sea, land and water: the same controls the land, layer and river panels call.
        _editor.SetBaseColor(FinishSea);
        var land = original.Layers.OfType<LandLayer>().Single();
        _editor.SelectLayer(land.Id);
        Check("land style applied", _editor.SetActiveLandStyle(s => s with
        {
            FillColor = FinishLand, FillTextureOpacity = .46, OutlineColor = "#FF4B5C50", InnerShadowOpacity = .14,
        }));
        foreach (var river in _editor.Rivers.ToArray())
        {
            _editor.SelectRiver(river.Id);
            _editor.SetRiverWaterColor(FinishSea); _editor.SetRiverBankColor("#FF465E58");
        }
        _editor.ClearWaterSelection();
        // 2. Roads: a darker trodden surface and a firmer rim, through the road panel's setters.
        foreach (var road in _editor.Roads.ToArray())
        {
            _editor.SelectRoad(road.Id);
            _editor.SetRoadSurfaceColor("#FFD0B889"); _editor.SetRoadToneColor("#FFAE9464"); _editor.SetRoadEdgeColor("#FF514230");
            _editor.SetRoadEdgePercent(78); _editor.SetRoadRutPercent(22); _editor.SetRoadShoulderPercent(30);
        }
        _editor.ClearRoadSelection();
        // 3. A soft ground shadow under the illustrated layers, from the layer effects.
        foreach (var layer in original.Layers.OfType<ObjectLayer>().Where(l => l.Objects.Any(o => o.Stamp is not null)))
        {
            _editor.SelectLayer(layer.Id);
            _editor.SetActiveObjectLayerEffects(layer.Effects with
            {
                OuterShadowOpacity = .42, ShadowColor = "#FF27321F", ShadowBlur = 3, ShadowOffsetX = 1.5, ShadowOffsetY = 2.5,
            });
        }
        var paletteSteps = _editor.Session.UndoDepth - start;
        Check("palette recorded as undoable edits", paletteSteps >= 5);
        await SettledTerrain(); await CanvasShot("finish-01-palette");

        // 4. Two forests in the empty meadow north of the lake and south of the settlement,
        // drawn with the regional forest tool on the canvas.
        _editor.ActiveTool = EditorTool.Stamp; await Settle();
        foreach (var outline in new[]
        {
            new (double X, double Y)[] { (2400, 800), (2650, 775), (2820, 800), (2830, 950), (2650, 985), (2430, 990) },
            new (double X, double Y)[] { (2580, 2240), (2740, 2215), (2840, 2250), (2850, 2390), (2700, 2420), (2600, 2410) },
        })
        {
            ClickVisible(_view, "RegionalForestButton"); await Settle();
            var depth = _editor.Session.UndoDepth;
            foreach (var (x, y) in outline) { var p = _editor.Session.Viewport.WorldToScreen(x, y); _view.Canvas.ClickForSmoke(p.X, p.Y); }
            await Settle();
            ClickVisible(_view, "FinishEditableAreaButton"); await Settle();
            Check("forest drawn as one undoable edit", _editor.Session.UndoDepth == depth + 1 && _editor.SelectedEditableArea is { MemberCount: > 0 });
            _editor.CancelPlacement();
        }
        _editor.ActiveTool = EditorTool.Select; _editor.DeselectObject(); await Settle();
        var finished = _editor.Session.Document;
        var steps = _editor.Session.UndoDepth - start;
        _report.Measurements["finish-undo-steps"] = steps;
        _report.Measurements["finish-stamps-before"] = stampsBefore;
        _report.Measurements["finish-stamps-after"] = Stamps(finished);
        Check("forests added stamps", Stamps(finished) > stampsBefore);

        // Undo everything and redo it: the exact original, then the exact finished map.
        for (var i = 0; i < steps; i++) _editor.Undo();
        var undone = _editor.Session.Document;
        Check("undo returns the delivered map", undone.BaseColor.Color == original.BaseColor.Color
            && undone.Layers.OfType<LandLayer>().Single().EdgeStyle == land.EdgeStyle && Stamps(undone) == stampsBefore
            && undone.Layers.OfType<ObjectLayer>().SelectMany(l => l.Roads).Select(r => r.Style)
                .SequenceEqual(original.Layers.OfType<ObjectLayer>().SelectMany(l => l.Roads).Select(r => r.Style)));
        for (var i = 0; i < steps; i++) _editor.Redo();
        var redone = _editor.Session.Document;
        Check("redo restores the finished map with the same forest members", Stamps(redone) == Stamps(finished)
            && redone.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Select(o => (o.Id, o.X, o.Y, o.Stamp?.AssetSha256))
                .SequenceEqual(finished.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Select(o => (o.Id, o.X, o.Y, o.Stamp?.AssetSha256))));
        await SettledTerrain(); await CanvasShot("finish-02-forests");
        Capture(_view, "finish-02-editor");

        // Save and export through the top bar.
        ClickVisible(_view, "SaveButton");
        await WaitUntil(() => File.Exists(_picker.Path) && !_editor.Session.IsDirty, "finish save");
        ClickVisible(_view, "ExportButton"); await _editor.WaitForExportPreviewAsync(); await Settle();
        Capture(_view.ExportWindowForSmoke!, "finish-03-export");
        Check("export plan ready", _editor.CanStartExport);
        Check("production image export", await _editor.StartExportAsync());
        using (var png = SKBitmap.Decode(exportPath))
            Check("export is the full 4096×3072 canvas", png is { Width: 4096, Height: 3072 });
        _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches();

        // Reopen with an empty external catalogue: every image must come from the project.
        var emptyCatalog = new AssetCatalog(Path.Combine(_workspace, "empty-catalog"));
        var reopened = MapProjectStore.Open(_picker.Path).Document;
        Check("reopened project keeps every object and its image", Stamps(reopened) == Stamps(redone)
            && reopened.Assets.All(a => a.Storage == ProjectAssetStorage.Embedded && a.EmbeddedContent is { Length: > 0 }));
        _editor = new EditorViewModel(reopened, _picker.Path, false, _store, _recent, _picker, new LocalPrompts(), new LocalLog(),
            assetCatalog: emptyCatalog, localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
        await Settle(); _view.Canvas.FitToCanvas(); await SettledTerrain();
        await CanvasShot("finish-04-reopened");
        Check("reopened with an empty catalogue draws every image", emptyCatalog.Records.Count == 0 && _view.Canvas.LastRenderStats.MissingAssets == 0);
        foreach (var (scale, name) in new[] { (1d, "100"), (4d, "400") })
        {
            _editor.Session.Viewport.Set(2560 - _view.Canvas.Bounds.Width / scale / 2, 1880 - _view.Canvas.Bounds.Height / scale / 2, scale);
            _view.Canvas.InvalidateVisual(); await SettledTerrain();
            await CanvasShot("finish-05-detail-" + name);
        }
        // A later local edit on the reopened map: one road's width, one undo, then back.
        var roadAfter = _editor.Roads.First();
        _editor.SelectRoad(roadAfter.Id);
        var before = _editor.Session.UndoDepth;
        _editor.SetRoadWidthScalePercent(130);
        Check("reopened map remains editable", _editor.Session.UndoDepth == before + 1 && _editor.Roads.First().WidthScale > roadAfter.WidthScale);
        _editor.Undo();
        Check("later edit undoes exactly", _editor.Roads.First() == roadAfter);
        await emptyCatalog.DisposeAsync();
        Check("delivered source project unchanged", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))) == sourceHash);
    }

    private static int Stamps(MapDocument document) => document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count(o => o.Stamp is not null));

    private async Task SettledTerrain()
    {
        await Task.Delay(250);
        await WaitUntil(() => !_view!.Canvas.LastFrameFastTerrain && !_view.Canvas.LastTerrainRefinementPending, "settled terrain");
        await Settle();
    }

    private async Task CanvasShot(string name)
    {
        using var bitmap = await _view!.Canvas.CaptureGpuFrameForSmokeAsync();
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.ChangeExtension(Options.OutputPath, name + ".png"), png.ToArray());
    }

    /// <summary>A click on a button the user can actually see and use, not one merely kept in a hidden panel.</summary>
    private static void ClickVisible(Control root, string name)
    {
        var button = root.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
        if (!button.IsEffectivelyVisible || !button.IsEffectivelyEnabled) throw new InvalidOperationException("Button not available: " + name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
