using Avalonia.Controls;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Performance;

public sealed partial class NativeEditorScenarioApplication
{
    private static readonly string[] GeographyRequired =
    [
        "auto-road-by-clicks", "road-stays-on-its-tier", "river-to-sea-by-click", "check-finds-uphill-river",
        "intentional-spares-it", "resources-disposed",
    ];

    /// <summary>
    /// R24 through the panels: «Проложить дорогу A→B» with two clicks on the map goes round a raised
    /// plateau; «Река отсюда в море» with one click runs down to the sea; the geography check finds a
    /// river drawn uphill, and «намеренно» spares it. The editor is captured with the new road and river.
    /// </summary>
    private async Task GeographyOnly()
    {
        var page = "#FF8FB0BA";
        var island = CliffLand("Остров", 512, 384, 440, 300);
        var plateau = CliffLand("Плато", 600, 330, 140, 110) with { Level = 1 };
        var uphill = new River { Name = "Вверх", Channels = [RiverEditor.NewChannel([new(300, 330), new(600, 330)], 6)] };
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Rivers = [uphill] };
        var document = new MapDocument(DocumentId.New(), "География", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", page),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, page), island, plateau, objects]);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        _editor.Session.Viewport.Set(0, 0, 1);
        _editor.SelectLayer(objects.Id);

        await Click("RoadToolButton");
        await Click("AutoRoadButton");
        Point(200, 400); await Settle();
        Point(800, 450); await Settle(); await Frame();
        var layer = (ObjectLayer)_editor.Session.Document.FindLayer(objects.Id)!;
        _report.Checks["auto-road-by-clicks"] = layer.Roads.Length == 1 && _editor.GeographyPick == Map.Presentation.ViewModels.GeographyPick.None;
        var probe = new TerrainProbe(_editor.Session.Document);
        _report.Checks["road-stays-on-its-tier"] = layer.Roads.Length == 1 && layer.Roads[0].Nodes.All(n => probe.At(n.X, n.Y).Land?.Level == 0);

        await Click("RiverToolButton");
        await Click("RiverToSeaButton");
        Point(300, 550); await Settle(); await Frame();
        layer = (ObjectLayer)_editor.Session.Document.FindLayer(objects.Id)!;
        var sea = layer.Rivers.FirstOrDefault(r => r.Id != uphill.Id);
        _report.Checks["river-to-sea-by-click"] = sea is not null && probe.At(sea.Channels[0].Nodes[^1].X, sea.Channels[0].Nodes[^1].Y).Land is null;
        await Frame(); await Task.Delay(500); await Frame();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        SaveVisual(_report.Screenshot);

        var issues = _editor.CheckGeography();
        var found = issues.FirstOrDefault(i => i.ObjectId == uphill.Id && i.Kind == GeographyIssueKind.RiverUphill);
        _report.Checks["check-finds-uphill-river"] = found is not null;
        if (found is not null) _editor.MarkIntentional(found);
        _report.Checks["intentional-spares-it"] = found is not null && _editor.CheckGeography().All(i => i.ObjectId != uphill.Id);
        await Task.Delay(1500); await Frame(); await Settle();
    }
}
