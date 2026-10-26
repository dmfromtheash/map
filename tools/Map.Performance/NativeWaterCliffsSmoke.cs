using Avalonia.Controls;
using Avalonia.VisualTree;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.ViewModels;

namespace Map.Performance;

public sealed partial class Stage19Application
{
    private static readonly string[] WaterCliffsRequired =
    [
        "land-look-split", "light-angle-relights-wall", "cliff-mask-beach-removes-wall", "cliff-mask-undo",
        "node-feature-waterfall", "delta-preview-and-apply", "sea-tab-applies-manner", "sea-undo",
        "resources-disposed",
    ];

    /// <summary>
    /// R12 through the real panels: the land look in two groups, the light angle of a cliff, the
    /// «Маска обрыва» brush laying a beach under the southern wall (one Undo step), a waterfall on
    /// a river node, the «Дельта» preview applied as one step, and a sea style from the Море tab of
    /// the ribbon. The editor is captured after each part.
    /// </summary>
    /// <summary>How many sampled pixels of a rectangle differ between two renders of the 1024×768 map.</summary>
    private static int Differs(MapDocument a, MapDocument b, int left, int top, int right, int bottom)
    {
        SkiaSharp.SKBitmap Render(MapDocument document)
        {
            using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(1024, 768))!;
            Map.Rendering.SceneRenderer.Render(surface.Canvas, document, new Map.Rendering.RenderOptions { IsExport = true });
            var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(1024, 768));
            surface.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
            return bitmap;
        }
        using var first = Render(a); using var second = Render(b);
        var count = 0;
        for (var y = top; y < bottom; y += 2)
            for (var x = left; x < right; x += 2)
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) count++;
        return count;
    }

    private async Task WaterCliffsOnly()
    {
        var sea = "#FF8FB0BA";
        var island = CliffLand("Остров", 512, 330, 330, 220) with { Level = 1 };
        var river = new River
        {
            Name = "Река", Style = RiverStyle.Natural(), Level = 1,
            Channels = [RiverEditor.NewChannel([new(300, 300), new(420, 340), new(560, 330), new(700, 380), new(900, 470)], 22)],
        };
        var water = new ObjectLayer(LayerId.New(), "Реки", true, false, 1, []) { Rivers = [river] };
        var document = new MapDocument(DocumentId.New(), "Вода и обрывы", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", sea),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, sea), island, water]);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        _editor.Session.Viewport.Set(0, 0, 1);

        // Land: the look in «Заливка» and «Берег».
        await Click("LandToolButton");
        _editor.SelectLayer(island.Id); await Settle();
        var fill = Find<Control>("LandFillColorBox");
        var outline = Find<Control>("LandOutlineWidthSlider");
        _report.Checks["land-look-split"] = fill.GetVisualAncestors().OfType<Expander>().Any(e => e.Name == "LandFillCategory")
            && outline.GetVisualAncestors().OfType<Expander>().Any(e => e.Name == "LandCoastCategory")
            && !outline.GetVisualAncestors().OfType<Expander>().Any(e => e.Name == "LandFillCategory");
        Find<Control>("LandCliffLightAngleSlider").BringIntoView(); await Settle(); await Frame();
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".land-panel.png"));

        // The wall below the whole southern coast, before and after the light turns to the north-east.
        var styleBefore = ((LandLayer)_editor.Session.Document.FindLayer(island.Id)!).EdgeStyle;
        var turned = _editor.SetActiveLandStyle(s => s with { Cliff = s.Cliff with { LightAngle = 45 } }); await Settle();
        var turnedLayer = (LandLayer)_editor.Session.Document.FindLayer(island.Id)!;
        _report.Checks["light-angle-relights-wall"] = turned && turnedLayer.EdgeStyle.Cliff.LightAngle == 45
            && Differs(_editor.Session.Document with { Layers = [.. _editor.Session.Document.Layers.Select(l => l.Id == island.Id ? turnedLayer with { EdgeStyle = styleBefore } : l)] },
                _editor.Session.Document, 300, 520, 720, 600) > 50;
        _editor.Undo(); await Settle();

        // The cliff mask: a beach along the middle of the southern coast.
        await Click("LandToolModeCliffs");
        await Click("LandCliffMaskActionBeach");
        _editor.BrushSettings = _editor.BrushSettings with { Radius = 40, Softness = 0 };
        var depth = _editor.Session.UndoDepth;
        Drag(450, 548, 580, 548); await Settle(); await Frame();
        var beached = (LandLayer)_editor.Session.Document.FindLayer(island.Id)!;
        var shoreFlat = Pixel(_editor.Session.Document with { Layers = [.. _editor.Session.Document.Layers.Select(l => l.Id == island.Id ? beached with { Level = 0 } : l)] }, 512, 565);
        _report.Checks["cliff-mask-beach-removes-wall"] = beached.CliffBeachTiles.TileCount > 0
            && _editor.Session.UndoDepth == depth + 1 && Pixel(_editor.Session.Document, 512, 565) == shoreFlat;
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".cliff-mask.png"));
        _editor.Undo(); await Settle();
        _report.Checks["cliff-mask-undo"] = !((LandLayer)_editor.Session.Document.FindLayer(island.Id)!).HasCliffMask;
        await Click("LandToolModeShape");

        // A river: a waterfall on a node, then a delta at its mouth.
        await Click("RiverToolButton");
        _editor.SelectRiver(river.Id, river.Channels[0].Id);
        _editor.WaterLevel = WaterSelectionLevel.Node;
        _editor.SelectRiverNode(2); await Settle();
        await Click("RiverNodeFeature_Waterfall"); await Frame();
        _report.Checks["node-feature-waterfall"] = _editor.Rivers.Single().Channels[0].Nodes[2].Feature == RiverNodeFeature.Waterfall;
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".waterfall.png"));
        _editor.WaterLevel = WaterSelectionLevel.Channel; await Settle();
        var before = _editor.Session.Document;
        await Click("PreviewRiverDeltaButton");
        var previewKept = ReferenceEquals(before, _editor.Session.Document) && _editor.HasRiverPreview;
        await Click("ApplyRiverPreviewButton"); await Frame();
        _report.Checks["delta-preview-and-apply"] = previewKept && _editor.Rivers.Single().Channels.Length == _editor.RiverDeltaArms;
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".delta.png"));

        // The sea: the Море tab of the ribbon, the isobaths card.
        await Click("RibbonSeaTab");
        await Click("SeaCard2"); await Frame();
        _report.Checks["sea-tab-applies-manner"] = _editor.Sea is { Manner: SeaManner.Isobaths };
        await Task.Delay(800); await Frame();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        SaveVisual(_report.Screenshot);
        _editor.Undo(); await Settle();
        _report.Checks["sea-undo"] = _editor.Sea is null;
        // The card previews render in the background: the ribbon is closed and the last frame
        // settles before the caches are counted, or a late preview frame refills them.
        await Click("RibbonCollapseButton"); await Task.Delay(1500); await Frame(); await Settle();
    }
}
