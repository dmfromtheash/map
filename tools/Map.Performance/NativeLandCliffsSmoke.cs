using Avalonia.Controls;
using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

public sealed partial class NativeEditorScenarioApplication
{
    private static readonly string[] LandCliffRequired =
    [
        "no-tier-shows-raise", "raise-creates-tier", "cliff-drawn-below-edge", "look-applied-and-undone",
        "island-and-plateau-tiers", "resources-disposed",
    ];

    /// <summary>
    /// Tiers of land through the land panel: a plateau on an island has no cliff while both are at
    /// level 0, the panel offers to raise it; one click raises it a level, the cliff settings take
    /// the place of the hint and the wall appears below the plateau's southern edge. A ready look
    /// changes the rock and is one Undo step. Then the island is raised over the sea and the
    /// plateau over the island, and the editor is captured with both walls.
    /// </summary>
    private async Task LandCliffsOnly()
    {
        var sea = "#FF8FB0BA";
        var island = CliffLand("Остров", 512, 360, 360, 250);
        var plateau = CliffLand("Плато", 470, 320, 160, 105);
        var document = new MapDocument(DocumentId.New(), "Ярусы земли", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", sea),
            [CurrentLook.Page("Море", sea), island, plateau]);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        _editor.Session.Viewport.Set(0, 0, 1);
        await Click("LandToolButton");
        _editor.SelectLayer(plateau.Id); await Settle();

        var raise = Find<Button>("LandCliffRaiseButton");
        raise.BringIntoView(); await Settle();
        _report.Checks["no-tier-shows-raise"] = raise.IsEffectivelyVisible && !Find<Control>("LandCliffHeightSlider").IsEffectivelyVisible;

        var flat = Pixel(_editor.Session.Document, 470, 438);
        await Click("LandCliffRaiseButton"); await Frame();
        var raised = (LandLayer)_editor.Session.Document.FindLayer(plateau.Id)!;
        var height = Find<Control>("LandCliffHeightSlider");
        height.BringIntoView(); await Settle();
        _report.Checks["raise-creates-tier"] = raised.Level == 1 && height.IsEffectivelyVisible
            && !Find<Button>("LandCliffRaiseButton").IsEffectivelyVisible;
        var wall = Pixel(_editor.Session.Document, 470, 438);
        // New land takes the cliff of the owner's mountains (2026-10-22): slate-blue rock.
        _report.Checks["cliff-drawn-below-edge"] = wall != flat && wall.Blue > wall.Red;

        var depth = _editor.Session.UndoDepth;
        var before = raised.EdgeStyle;
        await Click("LandCliffLook_ink-dots");
        var dots = ((LandLayer)_editor.Session.Document.FindLayer(plateau.Id)!).EdgeStyle;
        var applied = dots.Cliff == LandCliffStyle.Looks.Single(l => l.Id == "ink-dots").Style && _editor.Session.UndoDepth == depth + 1;
        _editor.Undo(); await Settle();
        _report.Checks["look-applied-and-undone"] = applied
            && ((LandLayer)_editor.Session.Document.FindLayer(plateau.Id)!).EdgeStyle == before;

        // The island over the sea, the plateau one level above the island.
        _editor.SelectLayer(island.Id); await Settle();
        await Click("LandCliffRaiseButton");
        _editor.SelectLayer(plateau.Id); await Settle();
        _editor.ShiftLandLevel(1); await Settle(); await Frame();
        var levels = (((LandLayer)_editor.Session.Document.FindLayer(island.Id)!).Level,
            ((LandLayer)_editor.Session.Document.FindLayer(plateau.Id)!).Level);
        var shore = Pixel(_editor.Session.Document, 512, 625);
        _report.Checks["island-and-plateau-tiers"] = levels == (1, 2) && shore.Blue > shore.Red;
        Find<Control>("LandCliffHeightSlider").BringIntoView(); await Settle(); await Frame();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        SaveVisual(_report.Screenshot);
        // Every group of the cliff settings, opened the way a user opens them.
        Find<Control>("LandCliffHatchingSlider").BringIntoView(); await Settle();
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".panel.png"));
        await Click("LandCliffLook_ink-dots"); await Frame();
        _editor.SelectLayer(island.Id); await Settle();
        await Click("LandCliffLook_ink-dots"); await Frame(); await Task.Delay(1500); await Frame();
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".dots.png"));
    }

    private static SKColor Pixel(MapDocument document, int x, int y)
    {
        using var surface = SKSurface.Create(new SKImageInfo(1024, 768));
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true });
        using var bitmap = new SKBitmap(new SKImageInfo(1024, 768));
        surface.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap.GetPixel(x, y);
    }

    private static LandLayer CliffLand(string name, double cx, double cy, double rx, double ry)
    {
        var tiles = TileMap.Empty(1);
        for (var ty = 0; ty < 3; ty++)
            for (var tx = 0; tx < 4; tx++)
            {
                var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
                var any = false;
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                    {
                        var wx = (tx * TileMap.TileSize) + x; var wy = (ty * TileMap.TileSize) + y;
                        var angle = Math.Atan2(wy - cy, wx - cx);
                        var wobble = 1 + (0.07 * Math.Sin(angle * 5)) + (0.04 * Math.Sin((angle * 13) + 1.1));
                        var distance = Math.Sqrt(Math.Pow((wx - cx) / rx, 2) + Math.Pow((wy - cy) / ry, 2)) / wobble;
                        var value = (byte)Math.Clamp((1 - distance) * 255 * 40, 0, 255);
                        bytes[(y * TileMap.TileSize) + x] = value; any |= value > 0;
                    }
                if (any) tiles = tiles.With(new TileKey(tx, ty), bytes);
            }
        return new LandLayer(LayerId.New(), name, true, false, 1, tiles, CurrentLook.Land());
    }
}
