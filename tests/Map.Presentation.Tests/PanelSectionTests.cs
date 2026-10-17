using Map.Core.Documents;
using Map.Core.Editing;
using Map.Core.Resources;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// The working sections of the right panel: each lists one kind of map content, Ctrl+A stays
/// inside the open section, the section follows what is selected on the canvas, and switching
/// sections is a view change only - never a document, layer or history change.
/// </summary>
public sealed class PanelSectionTests
{
    private static (EditorViewModel Editor, SceneObject Stamp, SceneObject Text, River River, River Lake, Road Road, LandLayer Land) NewEditor()
    {
        var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var land = new LandLayer(LayerId.New(), "Материк", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), coverage).With(new TileKey(1, 0), coverage), new LandEdgeStyle());
        var stamp = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Дерево", 40, 40, 30, 30, 0,
            "#FF3E5C76", "#FF1B263B", 0, visible: true, locked: false);
        var text = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Подпись", 200, 40, 80, 20, 0,
            "#FF3E5C76", "#FF1B263B", 0, visible: true, locked: false);
        var river = new River { Name = "Река", Channels = [RiverEditor.NewChannel([new(20, 100), new(300, 140), new(500, 300)], 20)] };
        var lake = new River { Name = "Озеро", Channels = [RiverEditor.NewChannel([new(320, 40), new(400, 30), new(440, 90), new(360, 120)], 10) with { Closed = true }] };
        var road = RoadEditor.NewRoad([new(20, 400), new(300, 380), new(500, 420)], 12, RoadStyle.Country(), "Тракт");
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [stamp, text]) { Rivers = [river, lake], Roads = [road] };
        var document = new MapDocument(DocumentId.New(), "Разделы", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FFEEE6D2"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEEE6D2"), land, objects]);
        var memory = new WorkingMemoryBudget(() => new WorkingMemorySnapshot(
            16 * 1024 * WorkingMemoryBudget.MiB, 8 * 1024 * WorkingMemoryBudget.MiB, 128 * WorkingMemoryBudget.MiB));
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), memory: memory);
        editor.Session.Viewport.Set(0, 0, 1);
        return (editor, stamp, text, river, lake, road, land);
    }

    [Fact]
    public void Each_section_lists_only_its_own_kind_and_changes_nothing_on_the_map()
    {
        var (editor, stamp, text, river, lake, road, land) = NewEditor();
        var document = editor.Session.Document;
        var depth = editor.Session.UndoDepth;

        editor.PanelSection = PanelSection.Land;
        var landRows = editor.ObjectTree;
        Assert.Single(landRows);
        Assert.Equal(ObjectTreeRowKind.Land, landRows[0].Kind);
        Assert.Equal(land.Id, landRows[0].LandId);

        editor.PanelSection = PanelSection.Water;
        var waterRows = editor.ObjectTree;
        Assert.Equal(2, waterRows.Count);
        Assert.All(waterRows, row => Assert.Equal(ObjectTreeRowKind.Water, row.Kind));
        Assert.Equal(new[] { lake.Id, river.Id }.OrderBy(id => id), waterRows.Select(row => row.WaterId!.Value).OrderBy(id => id));

        editor.PanelSection = PanelSection.Assets;
        var assetRows = editor.ObjectTree;
        Assert.Single(assetRows);
        Assert.Equal(stamp.Id, assetRows[0].Object!.Id);

        editor.PanelSection = PanelSection.Roads;
        var roadRows = editor.ObjectTree;
        Assert.Single(roadRows);
        Assert.Equal(road.Id, roadRows[0].RoadId);

        editor.PanelSection = PanelSection.Other;
        var otherRows = editor.ObjectTree;
        Assert.Single(otherRows);
        Assert.Equal(text.Id, otherRows[0].Object!.Id);

        Assert.Same(document, editor.Session.Document);
        Assert.Equal(depth, editor.Session.UndoDepth);
    }

    [Fact]
    public void Select_all_stays_inside_the_open_section()
    {
        var (editor, stamp, text, _, _, _, _) = NewEditor();

        editor.PanelSection = PanelSection.Water;
        editor.SelectAllByFilter();
        Assert.Equal(2, editor.WaterSelectionCount);
        Assert.Equal(0, editor.SelectionCount);
        Assert.Equal(0, editor.RoadSelectionCount);

        editor.PanelSection = PanelSection.Assets;
        editor.SelectAllByFilter();
        Assert.Equal(new[] { stamp.Id }, editor.Session.SelectedObjectIds);
        Assert.Equal(0, editor.WaterSelectionCount);

        editor.PanelSection = PanelSection.Roads;
        editor.SelectAllByFilter();
        Assert.Equal(1, editor.RoadSelectionCount);
        Assert.Equal(0, editor.SelectionCount);

        editor.PanelSection = PanelSection.Other;
        editor.SelectAllByFilter();
        Assert.Equal(new[] { text.Id }, editor.Session.SelectedObjectIds);

        editor.PanelSection = PanelSection.Land;
        editor.SelectAllByFilter();
        Assert.NotNull(editor.SelectedLand);
        Assert.Equal(0, editor.SelectionCount);
    }

    [Fact]
    public void The_section_follows_what_is_selected()
    {
        var (editor, stamp, text, river, _, road, land) = NewEditor();
        Assert.Null(editor.SectionForSelection());

        editor.SelectWater(river.Id);
        Assert.Equal(PanelSection.Water, editor.SectionForSelection());
        editor.SelectObject(stamp.Id);
        Assert.Equal(PanelSection.Assets, editor.SectionForSelection());
        editor.SelectObject(text.Id);
        Assert.Equal(PanelSection.Other, editor.SectionForSelection());
        editor.SelectRoadOnly(road.Id);
        Assert.Equal(PanelSection.Roads, editor.SectionForSelection());
        editor.SelectLand(land.Id);
        Assert.Equal(PanelSection.Land, editor.SectionForSelection());
    }

    [Theory]
    [InlineData(PanelSection.Water)]
    [InlineData(PanelSection.Roads)]
    [InlineData(PanelSection.Land)]
    public void Select_all_with_no_matches_clears_every_previous_kind_and_delete_preserves_the_map(PanelSection section)
    {
        var (editor, stamp, _, river, _, road, land) = NewEditor();
        Action[] selectPrevious = [() => editor.SelectObject(stamp.Id), () => editor.SelectWater(river.Id),
            () => editor.SelectRoadOnly(road.Id), () => editor.SelectLand(land.Id)];
        foreach (var select in selectPrevious)
        {
            select();
            editor.PanelSection = section;
            editor.ObjectSearchText = "no-match-xyz";
            Assert.Empty(editor.ObjectTree);
            var document = editor.Session.Document;
            var depth = editor.Session.UndoDepth;

            editor.SelectAllByFilter();
            Assert.Empty(editor.Session.SelectedObjectIds);
            Assert.Equal(0, editor.WaterSelectionCount);
            Assert.Equal(0, editor.RoadSelectionCount);
            Assert.Null(editor.SelectedLand);
            editor.DeleteSelection();
            Assert.Same(document, editor.Session.Document);
            Assert.Equal(depth, editor.Session.UndoDepth);
        }
    }

    [Fact]
    public void Switching_drops_a_foreign_search_and_remembers_the_other_sections_sub_filter()
    {
        var (editor, _, _, _, _, _, _) = NewEditor();
        editor.PanelSection = PanelSection.Other;
        editor.ObjectTypeFilter = ObjectTypeFilter.Text;
        editor.ObjectSearchText = "Подпись";
        Assert.Single(editor.ObjectTree);

        editor.PanelSection = PanelSection.Water;
        Assert.Equal("", editor.ObjectSearchText);
        Assert.Equal(ObjectTypeFilter.Water, editor.ObjectTypeFilter);
        Assert.Equal(2, editor.ObjectTree.Count);

        editor.PanelSection = PanelSection.Other;
        Assert.Equal(ObjectTypeFilter.Text, editor.ObjectTypeFilter);
    }
}
