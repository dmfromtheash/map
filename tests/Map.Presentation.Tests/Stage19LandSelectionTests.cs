using Map.Core.Documents;
using Map.Core.Editing;
using Map.Core.Resources;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// A piece of land is selected by a click like any object, keeps the marquee for a drag, has a
/// level of its own, and water has a level and an order too - all through the one selection path.
/// </summary>
public sealed class Stage19LandSelectionTests
{
    [Fact]
    public void Picking_follows_terrain_levels_and_hidden_water_cannot_be_reordered()
    {
        var (editor, router, land, objects) = NewEditor();
        var low = new River { Name = "low", Level = -1, Channels = [RiverEditor.NewChannel([new(20, 100), new(230, 100)], 30)] };
        var high = low with { Id = Guid.NewGuid(), Name = "high", Level = 3 };
        editor.Session.Execute(new RiverCommand(objects.Id, high.Id, high, "high"));
        editor.Session.Execute(new RiverCommand(objects.Id, low.Id, low, "low"));
        Click(router, 120, 100);
        Assert.Equal(high.Id, editor.SelectedRiver?.Id);
        editor.Session.Execute(new RiverCommand(objects.Id, high.Id, high with { Visible = false }, "hide"));
        editor.ClearWaterSelection();
        Click(router, 120, 100);
        Assert.Equal(land.Id, editor.SelectedLand?.Id);
        Assert.Equal(0, editor.WaterSelectionCount);
        editor.Session.Execute(new RiverCommand(objects.Id, low.Id, low with { Locked = true }, "lock"));
        editor.SelectWater(low.Id);
        var revision = editor.Session.Revision;
        Assert.False(editor.ReorderSelectedWater(-1, true));
        Assert.Equal(revision, editor.Session.Revision);
    }

    [Fact]
    public void A_zero_mask_hides_water_and_land_from_picking()
    {
        var (editor, _, land, objects) = NewEditor();
        var river = new River { Channels = [RiverEditor.NewChannel([new(20, 100), new(230, 100)], 30)] };
        editor.Session.Execute(new RiverCommand(objects.Id, river.Id, river, "river"));
        var mask = new LayerMask(TileMap.Empty(1).With(new TileKey(0, 0), new byte[TileMap.TileSize * TileMap.TileSize]), true);
        var document = editor.Session.Document.WithLayer(objects.Id, layer => layer.WithMask(mask))
            .WithLayer(land.Id, layer => layer.WithMask(mask));
        var masked = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog());
        Assert.False(masked.HitWater(120, 100, 0, out _, out _, out _));
        Assert.Null(TerrainOrder.LandAt(document, 120, 100));
    }

    private static (EditorViewModel Editor, CanvasInputRouter Router, LandLayer Land, ObjectLayer Objects) NewEditor()
    {
        var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var land = new LandLayer(LayerId.New(), "Остров", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), coverage), new LandEdgeStyle());
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        var document = new MapDocument(DocumentId.New(), "Land", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FFEEE6D2"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEEE6D2"), land, objects]);
        var memory = new WorkingMemoryBudget(() => new WorkingMemorySnapshot(
            16 * 1024 * WorkingMemoryBudget.MiB, 8 * 1024 * WorkingMemoryBudget.MiB, 128 * WorkingMemoryBudget.MiB));
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), memory: memory);
        editor.Session.Viewport.Set(0, 0, 1);
        editor.ActiveTool = EditorTool.Select;
        return (editor, new CanvasInputRouter(editor), land, objects);
    }

    private static void Click(CanvasInputRouter router, double x, double y)
    { router.Press(CanvasPointerButton.Left, x, y); router.Release(x, y); }

    [Fact]
    public void A_click_on_land_selects_the_piece_and_a_click_on_the_sea_clears_it()
    {
        var (editor, router, land, _) = NewEditor();
        Click(router, 100, 100);
        Assert.Equal(land.Id, editor.SelectedLand?.Id);
        Assert.Equal(1, editor.LandSelectionCount);
        Assert.Equal(land.Id, editor.ActiveLayerId);
        Assert.NotNull(editor.LandSelectionFrame);

        Click(router, 700, 600);
        Assert.Null(editor.SelectedLand);
    }

    [Fact]
    public void A_drag_that_starts_on_land_is_still_a_marquee()
    {
        var (editor, router, _, _) = NewEditor();
        router.Press(CanvasPointerButton.Left, 20, 20);
        router.Move(180, 180);
        router.Release(180, 180);
        Assert.Null(editor.SelectedLand);
    }

    [Fact]
    public void The_land_level_is_set_shifted_and_undone_as_one_command_each()
    {
        var (editor, router, land, _) = NewEditor();
        Click(router, 100, 100);
        Assert.Equal(0, editor.LandTargetLevel);
        Assert.True(editor.ShiftLandLevel(1));
        Assert.Equal(1, ((LandLayer)editor.Session.Document.FindLayer(land.Id)!).Level);
        Assert.True(editor.SetLandLevel(99));
        Assert.Equal(TerrainOrder.MaximumLevel, editor.LandTargetLevel);
        editor.Undo();
        Assert.Equal(1, editor.LandTargetLevel);
        // Delete does not take a whole layer from under the user; it says where the layer goes.
        Assert.True(editor.DeleteSelection());
        Assert.NotNull(editor.Session.Document.FindLayer(land.Id));
        Assert.Contains("Удалить слой", editor.StatusDetail);
    }

    [Fact]
    public void Water_has_a_level_and_an_order_of_its_own()
    {
        var (editor, _, _, objects) = NewEditor();
        var a = new River { Name = "a", Channels = [RiverEditor.NewChannel([new(300, 300), new(500, 320)], 12)] };
        var b = new River { Name = "b", Channels = [RiverEditor.NewChannel([new(300, 400), new(500, 420)], 12)] };
        editor.Session.Execute(new RiverCommand(objects.Id, a.Id, a, "a"));
        editor.Session.Execute(new RiverCommand(objects.Id, b.Id, b, "b"));

        editor.SelectWater(a.Id);
        Assert.Equal(0, editor.WaterSelectionLevelValue);
        Assert.True(editor.ShiftSelectedWaterLevel(2));
        Assert.Equal(1, editor.WaterSelectionLevelValue);
        Assert.True(editor.SetSelectedWaterLevel(-3));
        Assert.Equal(-3, ((ObjectLayer)editor.Session.Document.FindLayer(objects.Id)!).Rivers.Single(r => r.Id == a.Id).Level);

        // Order among the water of the layer: a is first (below), Наверх puts it last (on top).
        Assert.True(editor.ReorderSelection(1, toExtreme: true));
        Assert.Equal(["b", "a"], ((ObjectLayer)editor.Session.Document.FindLayer(objects.Id)!).Rivers.Select(r => r.Name).ToArray());
        Assert.False(editor.ReorderSelection(1));
        editor.Undo();
        Assert.Equal(["a", "b"], ((ObjectLayer)editor.Session.Document.FindLayer(objects.Id)!).Rivers.Select(r => r.Name).ToArray());

        // The objects panel lists water and land with their levels, highest first.
        var rows = editor.ObjectTree;
        Assert.Contains(rows, row => row.Kind == ObjectTreeRowKind.Land && row.Title == "Остров");
        Assert.Equal("b", rows.First(row => row.Kind == ObjectTreeRowKind.Water).Title);
    }
}
