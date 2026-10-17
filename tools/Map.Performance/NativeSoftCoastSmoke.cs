using Avalonia;
using Avalonia.Media.Imaging;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Performance;

public sealed partial class Stage19Application
{
    private static readonly string[] SoftCoastRequired =
    [
        "soft-land-created-by-stroke", "river-created-by-clicks", "both-ends-leave-land",
        "width-changed-from-panel", "width-undo-restores-river", "zoom-preserves-document",
        "save-open-preserves-soft-coast", "resources-disposed",
    ];

    private async Task SoftCoastOnly()
    {
        var land = LandLayer.CreateEmpty(LayerId.New(), "Мягкая земля") with
        {
            EdgeStyle = new LandEdgeStyle
            {
                FillColor = "#FF8CA36B", FillTextureId = "meadow", FillTextureOpacity = .7,
                FillTextureScale = 128, OutlineWidth = 0, RippleCount = 0,
                InnerShadowOpacity = 0, OuterShadowOpacity = 0,
            },
        };
        var document = new MapDocument(DocumentId.New(), "Проверка мягкого берега", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(768, 640, "px", "#FFF2E9DB"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFF2E9DB"), land,
                new ObjectLayer(LayerId.New(), "Река", true, false, 1, [])]);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document);
        await Settle();
        _editor.SelectLayer(land.Id);
        await Click("LandToolButton");
        _editor.SetLandPaintMode(false); _editor.ActiveLandShape = LandShapeKind.Circle;
        _editor.BrushSettings = _editor.BrushSettings with { Radius = 190, Softness = .72, Opacity = 1, Eraser = false };
        _editor.Session.Viewport.Set(0, 0, 1); await Frame();
        Drag(280, 430, 450, 240); await Frame();
        var ground = (LandLayer)_editor.Session.Document.FindLayer(land.Id)!;
        _report.Checks["soft-land-created-by-stroke"] = ground.GeometryTiles.TileCount > 0 && _editor.Session.UndoDepth == 1;

        await Click("RiverToolButton"); await Click("StartRiverButton");
        Point(465, 30); Point(450, 170); Point(425, 290); Point(360, 390); Point(315, 460); Point(395, 590);
        await Click("FinishRiverButton"); await Click("RiverIllustratedStyleButton");
        await Number("RiverSourceWidthBox", "26"); await Number("RiverMouthWidthBox", "30");
        var river = _editor.Rivers.Single();
        _report.MainNodes = river.Channels[0].Nodes.Length;
        _report.Checks["river-created-by-clicks"] = _report.MainNodes == 6;
        _report.Checks["both-ends-leave-land"] = TerrainOrder.CoverageAt(ground, 465, 30) == 0
            && TerrainOrder.CoverageAt(ground, 395, 590) == 0;

        await Click("SelectToolButton"); Point(40, 40); await Settle();
        await SaveSoftCoast("map", 0, 0, 1);
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".editor.png"); SaveVisual(_report.Screenshot);
        await SaveSoftCoast("entry", 280, 0, 2);
        await SaveSoftCoast("exit", 170, 390, 2);
        var beforeZoom = _editor.Session.Document;
        await SaveSoftCoast("overview", 0, 0, .65);
        _report.Checks["zoom-preserves-document"] = ReferenceEquals(beforeZoom, _editor.Session.Document);

        _editor.Session.Viewport.Set(0, 0, 1); await Frame();
        _editor.SelectWater(river.Id); await Click("RiverToolButton");
        await Number("RiverMouthWidthBox", "54");
        _report.Checks["width-changed-from-panel"] = _editor.Rivers.Single().Channels[0].Nodes[^1].Width == 54;
        await Click("SelectToolButton"); Point(40, 40); await SaveSoftCoast("wide-exit", 170, 390, 2);
        _editor.Undo();
        _report.Checks["width-undo-restores-river"] = _editor.Rivers.Single() == river;
        await SaveSoftCoast("undone", 0, 0, 1);

        await Click("SaveButton");
        _report.SavedProject = Path.Combine(_workspace, "river.mapproj");
        await Until(() => File.Exists(_report.SavedProject) && !_editor.Session.IsDirty);
        var opened = MapProjectStore.Open(_report.SavedProject).Document;
        var reopenedLand = opened.Layers.OfType<LandLayer>().Single();
        var reopenedRiver = opened.Layers.OfType<ObjectLayer>().SelectMany(l => l.Rivers).Single();
        _report.Checks["save-open-preserves-soft-coast"] = reopenedRiver.Channels[0].Nodes.SequenceEqual(river.Channels[0].Nodes)
            && TerrainOrder.CoverageAt(reopenedLand, 465, 100) == TerrainOrder.CoverageAt(ground, 465, 100);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(opened, _report.SavedProject); await Settle();
        await SaveSoftCoast("reopened", 0, 0, 1);

        // Optional comparison against the exact saved document from an earlier run: channel
        // ids seed the illustrated bank noise, so recreating the same clicks is not identical.
        var comparison = Environment.GetEnvironmentVariable("MAP_SOFT_COAST_COMPARE_PROJECT");
        if (!string.IsNullOrWhiteSpace(comparison))
        {
            var original = MapProjectStore.Open(comparison).Document;
            _view.Canvas.DisposeRenderCaches(); OpenEditor(original); await Settle();
            await SaveSoftCoast("compare-map", 0, 0, 1);
            await SaveSoftCoast("compare-entry", 280, 0, 2);
            await SaveSoftCoast("compare-exit", 170, 390, 2);
        }
    }

    private async Task SaveSoftCoast(string name, double left, double top, double scale)
    {
        // Render the actual canvas after its layout and GPU frame settle. A snapshot is labelled
        // with the exact world view; no reading outside a smaller-than-expected canvas is allowed.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            _view.UpdateLayout(); _editor.Session.Viewport.Set(left, top, scale); await Frame();
            // Capture the document portion of the real control, omitting empty space beyond
            // its bottom/right edges when a close-up reaches the end of the map.
            var size = _editor.Session.Document.Canvas;
            var width = Math.Min(_view.Canvas.Bounds.Width, (size.Width - left) * scale);
            var height = Math.Min(_view.Canvas.Bounds.Height, (size.Height - top) * scale);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(width), (int)Math.Ceiling(height)));
            bitmap.Render(_view.Canvas);
            var view = _editor.Session.Viewport;
            if (view.OffsetX != left || view.OffsetY != top || view.Scale != scale) continue;
            using var file = File.Create(Path.ChangeExtension(Options.OutputPath, "." + name + ".png"));
            bitmap.Save(file, PngBitmapEncoderOptions.Default);
            return;
        }
        throw new InvalidOperationException("The soft-coast capture did not settle at the requested view.");
    }
}
