using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Map.Core.Documents;
using Map.Assets;
using Map.Core.Editing;
using Map.Export;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

public sealed partial class NativeEditorScenarioApplication
{
    private static readonly string[] LandMaterialRequired =
    [
        "new-land-watercolor-default", "first-paint-mode-has-material", "paint-clipped-at-coast",
        "catalog-filter-and-choose", "paint-keeps-geometry", "undo-redo-exact-paint",
        "eraser-preserves-coast", "paint-save-open", "paint-export", "resources-disposed",
    ];

    private async Task LandMaterialsOnly()
    {
        // Optional explicitly supplied isolated catalog lets this same short scenario review
        // owner artwork together with the terrain. The live user library is never opened here.
        var reviewCatalogRoot = Environment.GetEnvironmentVariable("MAP_LAND_REVIEW_CATALOG");
        await using var reviewCatalog = string.IsNullOrWhiteSpace(reviewCatalogRoot) ? null : new AssetCatalog(reviewCatalogRoot);
        var document = new MapDocument(DocumentId.New(), "Акварель · подножие гор — земля и кисти", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FF7C9CA4"),
            [new BaseColorLayer(LayerId.New(), "Вода", true, false, 1, "#FF7C9CA4")]);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document, assetCatalog: reviewCatalog);
        _editor.AddLandLayer(); await Settle();
        var land = (LandLayer)_editor.ActiveLayer!;
        await Click("LandToolButton");
        _editor.ActiveLandShape = LandShapeKind.Circle;
        _editor.BrushSettings = _editor.BrushSettings with { Radius = 155, Softness = .04, Opacity = 1, Eraser = false };
        _editor.Session.Viewport.Set(0, 0, 1);
        Drag(285, 280, 650, 235); Drag(230, 375, 700, 410); Drag(380, 515, 590, 500);
        await Frame();
        land = (LandLayer)_editor.Session.Document.FindLayer(land.Id)!;
        _report.Checks["new-land-watercolor-default"] = land.GeometryTiles.TileCount > 0
            && land.EdgeStyle.FillTextureId == MapFillTextures.OwnerSnow;
        SaveMap(_editor.Session.Document, Path.ChangeExtension(Options.OutputPath, ".base.png"));
        await Click("LandToolModePaint");
        _report.Checks["first-paint-mode-has-material"] = !_editor.LandPaintWithColor
            && _editor.LandMaterialId == MapFillTextures.OwnerSnow
            && Find<Border>("LandMaterialPreview").Child is null
            && Find<Button>("LandMaterialCatalogButton").IsEffectivelyVisible;
        await Click("LandMaterialCatalogButton");
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        await Until(() => desktop.Windows.Any(w => w.Name == "MaterialCatalogWindow"));
        var catalog = desktop.Windows.Single(w => w.Name == "MaterialCatalogWindow");
        await Settle();
        var catalogPath = Path.ChangeExtension(Options.OutputPath, ".catalog.png");
        using (var capture = new RenderTargetBitmap(new PixelSize(900, 650)))
        { capture.Render(catalog); using var file = File.Create(catalogPath); capture.Save(file, PngBitmapEncoderOptions.Default); }
        catalog.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "MaterialCatalogGroups").SelectedItem = "Акварель";
        catalog.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "MaterialCatalogSearch").Text = "подножие";
        await Settle();
        var cards = catalog.GetVisualDescendants().OfType<Button>().Where(x => x.Name?.StartsWith("MaterialCatalog_") == true).ToArray();
        // The owner's library grew since 2026-10-09 (ice and snow textures of 2026-10-18 also answer
        // «подножие»); the search must narrow the group to the matching cards and keep the snow one.
        var filtered = cards.Length >= 1 && cards.Length < 6 && cards.Any(c => c.Name == "MaterialCatalog_" + MapFillTextures.OwnerSnow);
        Require(filtered, $"Material search did not filter the category to foothill: {cards.Length} cards: {string.Join(", ", cards.Select(c => c.Name))}");
        cards[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
        _report.Checks["catalog-filter-and-choose"] = _editor.LandMaterialId == MapFillTextures.OwnerSnow && _editor.LandPaintMode;

        Find<Slider>("LandPaintSizeSlider").Value = 64;
        Find<Slider>("LandPaintSoftnessSlider").Value = 80;
        var geometry = ((LandLayer)_editor.Session.Document.FindLayer(land.Id)!).GeometryTiles;
        var before = (LandLayer)_editor.Session.Document.FindLayer(land.Id)!;
        var beforeDepth = _editor.Session.UndoDepth;
        Drag(30, 340, 470, 335); await Settle();
        var painted = (LandLayer)_editor.Session.Document.FindLayer(land.Id)!;
        _report.Checks["paint-keeps-geometry"] = ReferenceEquals(geometry, painted.GeometryTiles) && painted.PaintTiles.TileCount > 0;
        var singleStroke = _editor.Session.UndoDepth == beforeDepth + 1;
        _editor.Undo();
        var undone = (LandLayer)_editor.Session.Document.FindLayer(land.Id)!;
        var undo = _editor.Session.UndoDepth == beforeDepth
            && SamePixels(before.PaintTiles, undone.PaintTiles)
            && SamePixels(geometry, undone.GeometryTiles);
        _editor.Redo();
        var redone = (LandLayer)_editor.Session.Document.FindLayer(land.Id)!;
        _report.Checks["undo-redo-exact-paint"] = singleStroke && undo
            && _editor.Session.UndoDepth == beforeDepth + 1
            && SamePixels(painted.PaintTiles, redone.PaintTiles)
            && SamePixels(geometry, redone.GeometryTiles);
        await Click("LandPaintActionErase");
        Find<Slider>("LandPaintSoftnessSlider").Value = 0;
        Drag(300, 340, 350, 340); await Settle();
        var erased = (LandLayer)_editor.Session.Document.FindLayer(land.Id)!;
        var key = TileMap.KeyAt(330, 340); var offset = (84 * 256 + 74) * 4 + 3;
        _report.Checks["eraser-preserves-coast"] = ReferenceEquals(geometry, erased.GeometryTiles)
            && (erased.PaintTiles.TryGet(key)?[offset] ?? 0) == 0;
        _editor.Undo();
        await Click("LandPaintActionTexture");
        _editor.ChooseLandMaterial(MapFillTextures.OwnerSnow);
        await Click("LandSoftBrushButton");
        Drag(470, 255, 650, 290); Drag(410, 530, 620, 450); await Settle();

        string? stampHash = null;
        if (reviewCatalog is not null)
        {
            _editor.AddObjectLayer(); _editor.RefreshLibrary(); _view.RefreshLibraryPanel(); await Settle();
            _editor.ActiveTool = EditorTool.Stamp; _view.OpenAssetCatalog(); await Settle();
            var gallery = _view.AssetCatalogWindowForSmoke!;
            var set = reviewCatalog.Sets.Single();
            Require(_editor.ChooseGalleryVariant(set.Id, null), "Review asset could not be chosen");
            stampHash = reviewCatalog.Records.Single().Sha256;
            _report.Checks["asset-gallery-and-placement-defaults"] = gallery.IsVisible && _editor.PendingPlacement is not null
                && reviewCatalog.Variants.Single().AnchorY == .5 && reviewCatalog.Variants.Single().DefaultScale == 1;
            gallery.Close(); await Settle();
            _editor.SetStampScalePercent(125, commit: true);
            _editor.SetStampRotationDegrees(12, commit: true);
            Point(495, 475); await Frame();
            var stamps = () => _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).ToArray();
            Require(stamps().Length == 1, "Review mountain was not placed");
            var placed = stamps()[0];
            _report.Checks["asset-scale-rotation-controls"] = Math.Abs(placed.Width - 320) < .01 && placed.RotationDegrees == 12;
            _editor.Undo(); var removed = stamps().Length == 0; _editor.Redo();
            _report.Checks["asset-place-undo-redo"] = removed && stamps().Length == 1 && stamps()[0] == placed;
            _editor.HandleEscape(); await Settle();
        }

        Require(await _editor.SaveAsync(), "Painted map did not save");
        var path = Path.Combine(_workspace, "river.mapproj"); _report.SavedProject = path;
        var reopened = MapProjectStore.Open(path).Document;
        using var reopenedImages = new ExportStampImageSource(reopened, path, null);
        if (stampHash is not null)
            _report.Checks["asset-embedded-save-open"] = reopened.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count) == 1
                && reopened.Assets.Any(a => a.Sha256 == stampHash) && reopenedImages.TryGetImage(stampHash) is not null;
        var savedLand = (LandLayer)reopened.FindLayer(land.Id)!;
        var expectedLand = (LandLayer)_editor.Session.Document.FindLayer(land.Id)!;
        _report.Checks["paint-save-open"] = savedLand.PaintTiles.Keys.All(k =>
            savedLand.PaintTiles.TryGet(k)!.AsSpan().SequenceEqual(expectedLand.PaintTiles.TryGet(k)))
            && savedLand.PaintTiles.TileCount == expectedLand.PaintTiles.TileCount && savedLand.PaintTiles.TileCount > 0;
        var exportPath = Path.ChangeExtension(Options.OutputPath, ".map.png");
        using (var surface = SKSurface.Create(new SKImageInfo(1024, 768)))
        {
            var stats = new RenderStats();
            SceneRenderer.Render(surface.Canvas, reopened, new RenderOptions { IsExport = true, Stamps = reopenedImages, Stats = stats });
            if (stampHash is not null) _report.Checks["asset-export-no-missing-image"] = stats.MissingAssets == 0 && stats.StampInstances == 1;
            using var pixels = new SKBitmap(new SKImageInfo(1024, 768));
            surface.ReadPixels(pixels.Info, pixels.GetPixels(), pixels.RowBytes, 0, 0);
            _report.Checks["paint-clipped-at-coast"] = TerrainOrder.CoverageAt(savedLand, 30, 340) == 0
                && pixels.GetPixel(30, 340) == SKColor.Parse("#7C9CA4")
                && TerrainOrder.CoverageAt(savedLand, 300, 340) > 0;
            using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var output = File.Create(exportPath); data.SaveTo(output);
        }
        _report.Export = exportPath; _report.Checks["paint-export"] = File.Exists(exportPath) && new FileInfo(exportPath).Length > 10000;
        _editor.ClearWaterSelection(); _editor.SelectLayer(land.Id);
        Find<Expander>("LandStyleCategory").IsExpanded = false;
        Find<Expander>("LandPaintAdvanced").IsExpanded = false;
        _editor.Session.Viewport.Set(0, 0, .8);
        await Frame();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".editor.png"); SaveVisual(_report.Screenshot);

        // Commands recreate immutable document/layer wrappers. Pixel content and history depth,
        // rather than wrapper identity, are the undo/redo contract of a material stroke.
        static bool SamePixels(TileMap left, TileMap right) => left.BytesPerPixel == right.BytesPerPixel
            && left.TileCount == right.TileCount
            && left.Keys.All(k => right.TryGet(k) is { } bytes && left.TryGet(k)!.AsSpan().SequenceEqual(bytes));

        static void SaveMap(MapDocument doc, string path)
        {
            using var surface = SKSurface.Create(new SKImageInfo(1024, 768));
            SceneRenderer.Render(surface.Canvas, doc, new RenderOptions { IsExport = true });
            using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var output = File.Create(path); data.SaveTo(output);
        }
    }
}
