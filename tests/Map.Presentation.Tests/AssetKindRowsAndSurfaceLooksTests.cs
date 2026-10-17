using Map.Core.Documents;
using Map.Core.Editing;
using Map.Core.Resources;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// The Assets section lists placed assets by picture ("Зелёный дуб · 3 объекта"), opens a picture
/// into its copies, follows a selection made on the map, and still lists every copy while
/// searching. River and road looks change style and width in one undo entry.
/// </summary>
public sealed class AssetKindRowsAndSurfaceLooksTests
{
    private static readonly string Oak = new('a', 64);
    private static readonly string Bridge = new('b', 64);

    private static SceneObject Stamp(string name, string sha) =>
        new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, name, 40, 40, 30, 30, 0,
            "#FF3E5C76", "#FF1B263B", 0, visible: true, locked: false) { Stamp = new StampStyle(sha) };

    private static (EditorViewModel Editor, SceneObject[] Oaks, SceneObject Bridge, River River, Road Road) NewEditor()
    {
        var oaks = new[] { Stamp("Лес · 1", Oak), Stamp("Лес · 2", Oak), Stamp("Зелёный дуб", Oak) };
        var bridge = Stamp("Каменный мост", Bridge);
        var river = new River { Name = "Река", Channels = [RiverEditor.NewChannel([new(20, 100), new(300, 140), new(500, 300)], 20)] };
        var road = RoadEditor.NewRoad([new(20, 400), new(300, 380), new(500, 420)], 12, RoadStyle.Country(), "Тракт");
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [.. oaks, bridge]) { Rivers = [river], Roads = [road] };
        var document = new MapDocument(DocumentId.New(), "Ассеты", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FFEEE6D2"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEEE6D2"), objects]);
        var memory = new WorkingMemoryBudget(() => new WorkingMemorySnapshot(
            16 * 1024 * WorkingMemoryBudget.MiB, 8 * 1024 * WorkingMemoryBudget.MiB, 128 * WorkingMemoryBudget.MiB));
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), memory: memory);
        return (editor, oaks, bridge, river, road);
    }

    [Fact]
    public void Assets_are_listed_once_per_picture_and_open_into_their_copies()
    {
        var (editor, oaks, bridge, _, _) = NewEditor();
        editor.PanelSection = PanelSection.Assets;

        var kinds = editor.ObjectTree;
        Assert.Equal(2, kinds.Count);
        Assert.All(kinds, row => Assert.Equal(ObjectTreeRowKind.AssetKind, row.Kind));
        var oakRow = kinds.Single(row => row.AssetKey == Oak);
        Assert.Equal(3, oakRow.ChildCount);
        Assert.Equal(oaks.Select(o => o.Id).OrderBy(id => id.Value), oakRow.MemberIds!.OrderBy(id => id.Value));
        // Without a library the title is the topmost copy's name, cut before a " · " suffix.
        Assert.Equal("Зелёный дуб", oakRow.Title);
        Assert.Equal("Каменный мост", kinds.Single(row => row.AssetKey == Bridge).Title);

        editor.OpenAssetKind(Oak);
        var opened = editor.ObjectTree;
        Assert.Equal(ObjectTreeRowKind.AssetKindBreadcrumb, opened[0].Kind);
        Assert.Equal(3, opened.Count - 1);
        Assert.All(opened.Skip(1), row => Assert.Equal(ObjectTreeRowKind.Object, row.Kind));

        editor.CloseAssetKind();
        editor.ObjectSearchText = "мост";
        var found = Assert.Single(editor.ObjectTree);
        Assert.Equal(bridge.Id, found.Object!.Id);
    }

    [Fact]
    public void A_selection_on_the_map_opens_the_picture_it_belongs_to()
    {
        var (editor, _, bridge, _, _) = NewEditor();
        editor.PanelSection = PanelSection.Assets;
        editor.SelectObject(bridge.Id);
        editor.RevealSelectionInAssetKinds();

        Assert.Equal(Bridge, editor.OpenAssetKey);
        var rows = editor.ObjectTree;
        Assert.Contains(rows, row => row.Kind == ObjectTreeRowKind.Object && row.Object!.Id == bridge.Id && row.Selected);

        editor.PanelSection = PanelSection.Layers;
        Assert.Null(editor.OpenAssetKey);
    }

    [Fact]
    public void A_river_look_changes_style_and_width_in_one_undo_entry()
    {
        var (editor, _, _, river, _) = NewEditor();
        editor.SelectRiver(river.Id);
        var depth = editor.Session.UndoDepth;

        editor.ApplyRiverLook(RiverLookPreset.WideRiver);

        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.Equal(1.7, editor.SelectedRiver!.WidthScale, 3);
        Assert.Equal(9, editor.SelectedRiver.Style.ShallowWidth);
        editor.Undo();
        Assert.Equal(river.WidthScale, editor.SelectedRiver!.WidthScale, 3);
    }

    [Fact]
    public void A_road_look_keeps_the_ends_and_changes_drawing_and_width_together()
    {
        var (editor, _, _, _, road) = NewEditor();
        editor.SelectRoad(road.Id);
        var depth = editor.Session.UndoDepth;

        editor.ApplyRoadLook(RoadLookPreset.Highway);

        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.Equal(RoadDrawing.PenRuts, editor.SelectedRoad!.Style.Drawing);
        Assert.Equal(1.5, editor.SelectedRoad.WidthScale, 3);
        Assert.Equal(road.Style.StartCap, editor.SelectedRoad.Style.StartCap);
    }
}
