using Avalonia.Controls;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Performance;

public sealed partial class NativeEditorScenarioApplication
{
    private static readonly string[] CityRequired = ["city-opened", "city-regenerated", "city-hand-note-kept", "city-coast", "resources-disposed"];

    /// <summary>R34/R35: a generated city map opens in the city workshop; «Другой вариант» in the «Город» window rebuilds it. Whole town and a close-up captured.</summary>
    private async Task CityOnly()
    {
        var document = CityBuilder.NewCityMap("Ильград", 2400, new CitySettings { Name = "Ильград", Seed = 11, Port = true }, new MapCategoryId("city"), DateTimeOffset.UnixEpoch);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        _report.Checks["city-opened"] = _editor.IsCityWorkshop && _editor.Session.Document.Layers.OfType<ObjectLayer>().Any(l => l.Name == "Город" && l.Objects.Count > 100);
        _view.Canvas.FitToCanvas(); await Task.Delay(600); await Settle();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        SaveVisual(_report.Screenshot);

        // R35b: the user's own empty note named like the key survives «Другой вариант».
        var mine = new MapNote(NoteId.New(), "1. Храм", "", NoteAnchorKind.World, 300, 300);
        _editor.Session.Execute(new AddNoteCommand(mine)); await Settle();
        var keys = _editor.Session.Document.Notes.Count(n => n.Origin == NoteOrigin.CityKey);
        _editor.Undo(); _editor.Redo(); await Settle();
        var undoBase = _editor.Session.UndoDepth;

        await Click("WorldSectionButton"); await Click("CityButton"); await Settle();
        var window = _view.GetType().GetField("_cityWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_view) as Window
            ?? throw new InvalidOperationException("Окно «Город» не открылось.");
        var before = _editor.Session.Document.Layers.OfType<ObjectLayer>().Single(l => l.Name == "Город").Objects.Count;
        var reseed = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<Button>().Single(b => b.Name == "CityReseedButton");
        reseed.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Task.Delay(400); await Settle();
        var after = _editor.Session.Document.Layers.OfType<ObjectLayer>().Single(l => l.Name == "Город").Objects.Count;
        _report.Checks["city-regenerated"] = after != before && _editor.Session.UndoDepth == undoBase + 1;
        var notes = _editor.Session.Document.Notes;
        _report.Checks["city-hand-note-kept"] = notes.Any(n => n.Id == mine.Id && n.Origin == NoteOrigin.Manual)
            && notes.Count(n => n.Origin == NoteOrigin.CityKey) == notes.Count - 1 && keys > 0;
        window.Close(); await Settle();
        _editor.Session.Viewport.Set(900, 900, 1.4); await Task.Delay(600); await Settle();
        SaveVisual(Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, Path.GetFileNameWithoutExtension(Options.OutputPath) + "-close.png"));

        // R35: a town on a shore - the region's coast comes in, the town keeps to the land, piers go out.
        var land = LandLayer.CreateEmpty(LayerId.New(), "Суша") with
        {
            GeometryTiles = TileMap.Empty(1).With(new TileKey(0, 0), [.. Enumerable.Range(0, 256 * 256).Select(i => (byte)((i % 256) + (Math.Sin(i / 256 / 20.0) * 18) < 200 ? 255 : 0))]),
        };
        var region = new MapDocument(DocumentId.New(), "Берег", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 1024, "px", "#FF8FB0BA"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF8FB0BA"), land]);
        var coast = ScaleSync.Coast(region, new AtlasTransform(60, 20, 200, 200, 2400, 2400), 2400, 2400);
        var town = CityBuilder.NewCityMap("Пристань", 2400, new CitySettings { Name = "Пристань", Seed = 5, Port = true }, new MapCategoryId("city"), DateTimeOffset.UnixEpoch, coast, "#FF8FB0BA");
        _view.Canvas.DisposeRenderCaches(); OpenEditor(town); await Settle();
        _view.Canvas.FitToCanvas(); await Task.Delay(600); await Settle();
        var townObjects = town.Layers.OfType<ObjectLayer>().Single().Objects;
        _report.Checks["city-coast"] = coast is not null && townObjects.Any(o => o.Name == "Пирс")
            && townObjects.Where(o => o.Name == "Дом").All(h => TerrainOrder.CoverageAt(coast, h.X + (h.Width / 2), h.Y + (h.Height / 2)) >= 128);
        SaveVisual(Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, Path.GetFileNameWithoutExtension(Options.OutputPath) + "-coast.png"));
        await Task.Delay(800); await Settle();
    }
}
