using Avalonia.Controls;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Performance;

public sealed partial class Stage19Application
{
    private static readonly string[] ContinentRequired =
    [
        "heights-preview-not-applied", "heights-applied-one-undo", "climate-paints-land", "rivers-are-editable",
        "towns-and-roads", "rerun-replaces-own-rivers", "undo-steps-one-by-one", "resources-disposed",
    ];

    /// <summary>
    /// R23 through the land panel: a drawn continent gets heights (preview, then «Применить»),
    /// climate, rivers, towns and roads, one step and one undo each; running «Реки» again replaces
    /// only the generated rivers; the steps undo one by one. The editor is captured at the end.
    /// </summary>
    private async Task ContinentOnly()
    {
        var page = "#FF8FB0BA";
        var continent = CliffLand("Материк", 512, 384, 440, 300);
        var water = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        var document = new MapDocument(DocumentId.New(), "Материк", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", page),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, page), continent, water]);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        _editor.Session.Viewport.Set(0, 0, 1);
        await Click("LandToolButton");
        _editor.SelectLayer(continent.Id); await Settle();
        _editor.ContinentSettings = new ContinentSettings { Seed = 17, RiversPerMillion = 12, TownsPerMillion = 14 };

        async Task Step(string button)
        {
            await Click(button);
            await Until(() => !_editor.GenerationBusy);
            await Settle();
        }
        Land Get() => new((LandLayer)_editor.Session.Document.FindLayer(continent.Id)!);
        var depth = _editor.Session.UndoDepth;
        await Step("ContinentStepHeights");
        _report.Checks["heights-preview-not-applied"] = _editor.GenerationHasPreview && Get().Layer.ReliefTiles.TileCount == 0
            && _editor.Session.UndoDepth == depth;
        await Click("ContinentApplyButton");
        _report.Checks["heights-applied-one-undo"] = Get().Layer.ReliefTiles.TileCount > 0 && _editor.Session.UndoDepth == depth + 1;

        await Step("ContinentStepErosion"); await Click("ContinentApplyButton");
        await Step("ContinentStepClimate"); await Click("ContinentApplyButton");
        _report.Checks["climate-paints-land"] = Get().Layer.PaintTiles.TileCount > 0;
        await Step("ContinentStepRivers"); await Click("ContinentApplyButton");
        var rivers = _editor.Rivers.Where(r => r.GenerationReason == ContinentGenerator.RiverReason).ToList();
        _report.Checks["rivers-are-editable"] = rivers.Count > 0 && rivers.All(r => r.Channels.All(c => c.Nodes.Length >= 3));
        await Step("ContinentStepSettlements"); await Click("ContinentApplyButton");
        var objects = _editor.Session.Document.Layers.OfType<ObjectLayer>().First();
        _report.Checks["towns-and-roads"] = objects.Objects.Count(o => o.GenerationReason == ContinentGenerator.TownReason) >= 2 && objects.Roads.Length > 0;
        await Frame(); await Task.Delay(800); await Frame();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        SaveVisual(_report.Screenshot);

        await Step("ContinentStepRivers"); await Click("ContinentApplyButton");
        _report.Checks["rerun-replaces-own-rivers"] = _editor.Rivers.Count(r => r.GenerationReason == ContinentGenerator.RiverReason) == rivers.Count;

        for (var k = 0; k < 6; k++) _editor.Undo();
        await Settle();
        _report.Checks["undo-steps-one-by-one"] = _editor.Session.UndoDepth == depth && Get().Layer.ReliefTiles.TileCount == 0
            && _editor.Rivers.Count == 0;
        // The terrain of the last undo is rebuilt in the background: it settles before the caches are counted.
        await Task.Delay(1500); await Frame(); await Settle();
    }

    private readonly record struct Land(LandLayer Layer);
}
