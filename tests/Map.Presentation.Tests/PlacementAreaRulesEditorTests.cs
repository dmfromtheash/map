using Map.Core.Documents;
using Map.Core.Editing;
using Map.Core.Resources;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>R10 in the editor: every rule switch is one Undo step, all off stores no rules, refresh follows a moved road.</summary>
public sealed class PlacementAreaRulesEditorTests
{
    private static readonly string Sha = new('a', 64);

    [Fact]
    public void Rule_switches_are_undoable_steps_and_switching_all_off_stores_no_rules()
    {
        var (editor, area) = NewEditor();
        Assert.False(editor.EditableAreaRules.Any);
        var plain = Count(editor);

        var depth = editor.Session.UndoDepth;
        editor.SetEditableAreaAvoidRoads(true);
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.True(editor.EditableAreaRules.AvoidRoads);
        Assert.True(editor.EditableAreaRuleBlockedCount > 0);
        Assert.True(Count(editor) < plain);

        editor.SetEditableAreaRoadMargin(60);
        Assert.Equal(60, editor.EditableAreaRules.RoadMargin);
        var wider = Count(editor);

        editor.SetEditableAreaOnlyOnWater(true);   // turns «не на воду» and «только на суше» off by itself
        editor.SetEditableAreaAvoidWater(true);    // and back: «только на воду» goes off
        Assert.False(editor.EditableAreaRules.OnlyOnWater);
        Assert.True(editor.EditableAreaRules.AvoidWater);

        editor.Session.Undo(); editor.Session.Undo();
        Assert.Equal(wider, Count(editor));
        editor.SetEditableAreaAvoidRoads(false);
        Assert.Null(editor.SelectedEditableArea!.Rules);
        Assert.Equal(plain, Count(editor));
        _ = area;
    }

    [Fact]
    public void Refresh_fills_the_area_again_after_the_road_moved()
    {
        var (editor, _) = NewEditor();
        editor.SetEditableAreaAvoidRoads(true);
        var layer = editor.Session.Document.Layers.OfType<ObjectLayer>().First(l => !l.PlacementAreas.IsEmpty);
        var moved = RoadEditor.NewRoad([new(0, 300), new(1000, 300)], 16, RoadStyle.Country(), "Дорога") with { Id = layer.Roads[0].Id };
        editor.Session.Execute(UpdateLayerCommand.Replace(layer, layer with { Roads = [moved] }, "move"));
        var before = editor.SelectedEditableArea!.RuleBlocked.ToHashSet();
        var depth = editor.Session.UndoDepth;
        editor.RefreshEditableAreaRules();
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.False(before.SetEquals(editor.SelectedEditableArea!.RuleBlocked));
        editor.SelectedEditableArea!.ValidateMembers();
    }

    private static int Count(EditorViewModel editor) => editor.SelectedEditableArea!.MemberCount;

    private static (EditorViewModel Editor, PlacementArea Area) NewEditor()
    {
        var road = RoadEditor.NewRoad([new(0, 500), new(1000, 500)], 16, RoadStyle.Country(), "Дорога");
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Roads = [road] };
        var asset = new MapAssetReference(Sha, "tree", "image/png", "png", ProjectAssetStorage.Linked,
            DateTimeOffset.UnixEpoch, "test", "test", catalogId: Sha);
        var document = new MapDocument(DocumentId.New(), "Правила", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1000, 1000, "px", "#FFEEE6D2"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEEE6D2"), layer], [asset]);
        var area = new PlacementArea
        {
            Name = "Лес", LayoutVersion = 1, Density = 4, Seed = 7,
            Nodes = [new(Guid.NewGuid(), 20, 20), new(Guid.NewGuid(), 980, 20), new(Guid.NewGuid(), 980, 980), new(Guid.NewGuid(), 20, 980)],
            Variants = [new(Sha, 32, 32)], Style = new(Sha), Request = new() { BaseWidth = 24 },
        };
        var session = new EditingSession(document);
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "create"));
        var memory = new WorkingMemoryBudget(() => new WorkingMemorySnapshot(
            16 * 1024 * WorkingMemoryBudget.MiB, 8 * 1024 * WorkingMemoryBudget.MiB, 128 * WorkingMemoryBudget.MiB));
        var editor = new EditorViewModel(session.Document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), memory: memory);
        editor.SelectLayer(layer.Id);
        editor.SelectEditableArea(area.Id);
        return (editor, area);
    }
}
