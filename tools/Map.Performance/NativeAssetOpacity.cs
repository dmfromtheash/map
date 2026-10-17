using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Map.App.Views;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Performance;

// One isolated native session. Both versions have identical geometry, backgrounds, camera,
// neutral effects and 100% object/layer opacity. No owner map is opened or rewritten.
public sealed partial class RegionalApplication
{
    private async Task VerifyAssetOpacity()
    {
        var beforeRoot = Environment.GetEnvironmentVariable("MAP_OPACITY_BEFORE")
            ?? throw new InvalidOperationException("MAP_OPACITY_BEFORE is required.");
        var afterRoot = Environment.GetEnvironmentVariable("MAP_OPACITY_AFTER") ?? RegionalArtPack.Root;
        var beforePack = RegionalArtPack.Import(_catalog, beforeRoot);
        var beforeDoc = OpacityScene(beforePack, _catalog);
        var beforePath = Path.Combine(_workspace, "До исправления.mapproj");
        await ShowOpacityDocument(beforeDoc, beforePath);
        await OpacityShots("before");
        await SaveOpacityDocument(beforePath);
        var oldMapHash = SHA256.HashData(File.ReadAllBytes(beforePath));

        _view!.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches();
        await _catalog.DisposeAsync();
        _catalog = new AssetCatalog(Path.Combine(_workspace, "after-catalog"));
        var afterPack = RegionalArtPack.Import(_catalog, afterRoot);
        RegionalArtPack.Import(_catalog, afterRoot);
        Check("all 28 corrected subjects imported without duplicate variants", _catalog.Records.Count == 28
            && _catalog.Variants.Count == 28 && _catalog.Sets.Count == 7);
        Check("all variant placement settings unchanged in the pack", beforePack.Manifest.Assets.All(old =>
            afterPack.Manifest.Assets.Single(a => a.Id == old.Id) is var next
            && old.Width == next.Width && old.AnchorY == next.AnchorY));
        var afterDoc = OpacityScene(afterPack, _catalog);
        var afterPath = Path.Combine(_workspace, "Проверка прозрачности.mapproj");
        await ShowOpacityDocument(afterDoc, afterPath);
        _view!.OpenAssetCatalog();
        await WaitUntil(() => _view.AssetCatalogWindowForSmoke!.GetVisualDescendants().OfType<Image>()
            .Count(i => i.Source is not null) >= 7, "corrected catalogue thumbnails");
        Capture(_view.AssetCatalogWindowForSmoke!, "after-catalog");
        Check("all corrected thumbnail files exist", _catalog.Variants.All(v =>
            _catalog.ThumbnailPathForVariant(v.Id) is { } path && File.Exists(path)));
        _view.CloseAuxiliaryWindowsForSmoke();

        _editor.SelectLayer(afterDoc.Layers.OfType<ObjectLayer>().Last().Id);
        var set = afterPack.Sets["mountains"];
        var variant = set.VariantIds.Select(_catalog.FindVariant).First(v => v!.Sha256 == afterPack.Records["waterfall-peak"].Sha256)!;
        Check("corrected variant arms through the gallery", _editor.ChooseGalleryVariant(set.Id, variant.Id));
        await Settle();
        var initialCount = Stamps(_editor.Session.Document);
        var p = _editor.Session.Viewport.WorldToScreen(940, 1100);
        _view.Canvas.ClickForSmoke(p.X, p.Y); await Settle();
        Check("new placement uses the corrected PNG", Stamps(_editor.Session.Document) == initialCount + 1
            && _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)
                .Last(o => o.Stamp is not null).Stamp!.AssetSha256 == variant.Sha256);
        _editor.CancelPlacement(); _editor.ActiveTool = EditorTool.Select;
        _editor.Undo(); _editor.DeselectObject();
        Check("placement undo restores the diagnostic scene", Stamps(_editor.Session.Document) == initialCount);
        await OpacityShots("after");
        await SaveOpacityDocument(afterPath);

        // The old embedded image must remain old even when the library has the new pixels.
        var oldReopened = MapProjectStore.Open(beforePath).Document;
        Check("old embedded copies are not silently replaced", oldReopened.Assets.All(a =>
            beforeDoc.Assets.Single(b => b.Sha256 == a.Sha256).EmbeddedContent!.SequenceEqual(a.EmbeddedContent!)));
        using (var oldImages = new ExportStampImageSource(oldReopened, beforePath, _catalog))
        using (var original = SKBitmap.Decode(Path.Combine(beforeRoot, beforePack.Manifest.Assets.Single(a => a.Id == "waterfall-peak").File)))
        using (var decoded = SKBitmap.FromImage(oldImages.TryGetImage(beforePack.Records["waterfall-peak"].Sha256)!))
            Check("embedded resolver keeps the original source alpha", original.GetPixel(68, 611).Alpha == decoded.GetPixel(68, 611).Alpha);
        Check("old diagnostic map bytes unchanged", oldMapHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(beforePath))));

        var reopened = MapProjectStore.Open(afterPath).Document;
        Check("reopened map embeds every corrected source byte", reopened.Assets.Count == 28 && reopened.Assets.All(a =>
            a.Storage == ProjectAssetStorage.Embedded && a.EmbeddedContent is { } bytes
            && Convert.ToHexStringLower(SHA256.HashData(bytes)) == a.Sha256
            && afterDoc.Assets.Single(b => b.Sha256 == a.Sha256).EmbeddedContent!.SequenceEqual(bytes)));
        _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches();
        await _catalog.DisposeAsync(); _catalog = new AssetCatalog(Path.Combine(_workspace, "empty-catalog"));
        await ShowOpacityDocument(reopened, Path.Combine(_workspace, "Повторно открытая проверка.mapproj"));
        await OpacityShots("reopened");
        Check("reopened native scene resolves all images without an external library", _catalog.Records.Count == 0 && _view.Canvas.LastRenderStats.MissingAssets == 0);
        await SaveOpacityDocument(_picker.Path);
        using var saved = SKBitmap.Decode(Path.ChangeExtension(afterPath, "png"));
        using var again = SKBitmap.Decode(Path.ChangeExtension(_picker.Path, "png"));
        Check("save and reopen preserve the exact exported pixels", saved.Bytes.SequenceEqual(again.Bytes));
        using var oldExport = SKBitmap.Decode(Path.ChangeExtension(beforePath, "png"));
        var beforeLeak = BackgroundDifference(oldExport);
        var afterLeak = BackgroundDifference(saved);
        _report.Measurements["before-white-black-body-channel-difference"] = beforeLeak;
        _report.Measurements["after-white-black-body-channel-difference"] = afterLeak;
        Check("source defect is visible in the native export", beforeLeak > 15);
        Check("opaque mountain body rejects both white and black backgrounds", afterLeak <= 1);
    }

    private async Task ShowOpacityDocument(MapDocument document, string path)
    {
        _picker.Path = path;
        _editor = new EditorViewModel(document, null, false, _store, _recent, _picker,
            new LocalPrompts(), new LocalLog(), assetCatalog: _catalog,
            exportDestinationPicker: new ExportPicker(Path.ChangeExtension(path, "png")), imageExporter: new ImageExportEngine(),
            localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { });
        _window.Content = _view; await Settle(); _view.Canvas.FitToCanvas(); await SettledTerrain();
        Check("diagnostic objects and layers have neutral appearance", document.Layers.OfType<ObjectLayer>()
            .All(l => l.Opacity == 1 && l.Blend == "Normal" && l.Effects.OuterShadowOpacity == 0
                && l.Objects.All(o => o.ObjectOpacity == 1 && (o.Stamp is null || o.Stamp is
                    { Blend: "Normal", ShadowOpacity: 0, Blur: 0, Hue: 0, Saturation: 0, Brightness: 0, Contrast: 0 }))));
    }

    private async Task OpacityShots(string prefix)
    {
        _view!.Canvas.FitToCanvas(); await SettledTerrain();
        Capture(_view, prefix + "-editor"); await CanvasShot(prefix + "-normal");
        // The same striped solo mountain at 1:1 and 4:1, including its lower silhouette.
        foreach (var scale in new[] { 1d, 4d })
        {
            _editor.Session.Viewport.Set(1760 - _view.Canvas.Bounds.Width / scale / 2,
                260 - _view.Canvas.Bounds.Height / scale / 2, scale);
            _view.Canvas.InvalidateVisual(); await SettledTerrain();
            await CanvasShot(prefix + "-edge-" + scale);
        }
        _view.Canvas.FitToCanvas(); await SettledTerrain();
    }

    private async Task SaveOpacityDocument(string path)
    {
        ClickVisible(_view!, "SaveButton");
        await WaitUntil(() => File.Exists(path) && !_editor.Session.IsDirty, "opacity map save");
        ClickVisible(_view!, "ExportButton"); await _editor.WaitForExportPreviewAsync(); await Settle();
        Check("diagnostic map exports through the production dialog " + Path.GetFileName(path), await _editor.StartExportAsync());
        _view!.CloseAuxiliaryWindowsForSmoke();
    }

    private static double BackgroundDifference(SKBitmap image)
    {
        // Dense green ground inside waterfall-peak, well clear of the antialiased contour.
        // Both objects have exactly the same coordinates relative to their 500px panels.
        var x = 20 + (int)Math.Round(68d / 1124 * 460);
        var y = (int)Math.Round(370 - 805d / 1124 * 460 + 611d / 1124 * 460);
        var a = image.GetPixel(x, y); var b = image.GetPixel(x + 500, y);
        return Math.Max(Math.Abs(a.Red-b.Red), Math.Max(Math.Abs(a.Green-b.Green), Math.Abs(a.Blue-b.Blue)));
    }

    private static MapDocument OpacityScene(RegionalArtImport pack, AssetCatalog catalog)
    {
        const int width = 2000, height = 1400;
        var land = LandLayer.CreateEmpty(LayerId.New(), "Обычная земля Map") with
        { EdgeStyle = RegionalMapFactory.LandStyle with { OutlineWidth = 0, InnerShadowOpacity = 0, OuterShadowOpacity = 0, RippleCount = 0 } };
        var geometry = LandRasterizer.ApplyShape(land.GeometryTiles, new LandShape
        { Kind = LandShapeKind.Polygon, Bounds = new(0, 0, width, height),
            Points = [new(-10,-10),new(width+10,-10),new(width+10,height+10),new(-10,height+10)] }, false);
        land = land with { GeometryTiles = land.GeometryTiles.WithMany(geometry.After) };
        var panels = new List<SceneObject>(); var subjects = new List<SceneObject>();
        SceneObject Rect(double x, double y, double w, double h, string color) => new(ObjectId.New(),
            SceneObjectKind.Rectangle, "Диагностический фон", x, y, w, h, 0, color, "#00000000", 0, true, false);
        for (var column = 0; column < 4; column++)
        {
            panels.Add(Rect(column*500,0,500,400,new[]{"#FFFFFFFF","#FF000000","#FF962EB3","#FF128DAE"}[column]));
            if (column == 3) for (var x = 1500; x < 2000; x += 40) panels.Add(Rect(x,0,20,400,"#FFCD3AA4"));
            subjects.Add(RegionalMapFactory.Stamp(pack.Records["waterfall-peak"], "Одиночная гора", column*500+250,370,460));
        }
        subjects.Add(RegionalMapFactory.Stamp(pack.Records["waterfall-peak"], "Задняя гора", 340,720,460));
        subjects.Add(RegionalMapFactory.Stamp(pack.Records["waterfall-peak"], "Передняя гора", 255,765,410));
        for(var i=0;i<4;i++) subjects.Add(RegionalMapFactory.Stamp(pack.Records[i%2==0?"waterfall-peak":"forested-mesa"], "Цепь",720+i*150,680+i*22,430));
        subjects.Add(RegionalMapFactory.Stamp(pack.Records["lava-volcano"], "Вулкан и сохранённый дым",1660,765,530));
        var index=0;
        foreach(var item in pack.Manifest.Assets)
        {
            var record=pack.Records[item.Id];var w=Math.Min(225,160d*record.Width/record.Height);
            subjects.Add(RegionalMapFactory.Stamp(record,item.Name,125+index%8*250,970+index/8*130,w));
            index++;
        }
        return new MapDocument(DocumentId.New(), "Проверка прозрачности ассетов", DateTimeOffset.UtcNow,
            new CanvasDefinition(width,height,"px","#FF8FB04E"),
            [new BaseColorLayer(LayerId.New(),"Фон",true,false,1,"#FF8FB04E"),land,
             new ObjectLayer(LayerId.New(),"Контрастные подложки",true,false,1,panels),
             new ObjectLayer(LayerId.New(),"Предметы · непрозрачность 100%",true,false,1,subjects)],
            pack.Records.Values.Select(r=>RegionalMapFactory.Reference(r,catalog)).ToArray());
    }
}
