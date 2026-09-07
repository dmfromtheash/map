using Map.Core.Documents;
using Map.Core.Editing;
using Map.Core.Resources;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// Rivers and lakes inside the ordinary selection: a click and a rubber band pick them, a drag
/// moves them, Delete removes what the chosen level names, one undo puts it back, and the two
/// switches plus the layer transfer behave like they do for any other object of the map.
/// </summary>
public sealed class Stage19WaterSelectionTests
{
    private static (EditorViewModel Editor, CanvasInputRouter Router, LayerId First, LayerId Second) NewEditor()
    {
        var first = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        var second = new ObjectLayer(LayerId.New(), "Второй слой", true, false, 1, []);
        var document = new MapDocument(DocumentId.New(), "Water", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FFEEE6D2"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEEE6D2"), first, second]);
        var memory = new WorkingMemoryBudget(() => new WorkingMemorySnapshot(
            16 * 1024 * WorkingMemoryBudget.MiB, 8 * 1024 * WorkingMemoryBudget.MiB, 128 * WorkingMemoryBudget.MiB));
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), memory: memory);
        editor.Session.Viewport.Set(0, 0, 1);
        return (editor, new CanvasInputRouter(editor), first.Id, second.Id);
    }

    private static void Click(CanvasInputRouter router, double x, double y, bool additive = false)
    { router.Press(CanvasPointerButton.Left, x, y, additive); router.Release(x, y); }

    private static void Drag(CanvasInputRouter router, double x, double y, double toX, double toY)
    { router.Press(CanvasPointerButton.Left, x, y); router.Move(toX, toY); router.Release(toX, toY); }

    private static River CreateRiver(EditorViewModel editor, CanvasInputRouter router, double width, params (double X, double Y)[] points)
    {
        editor.NewRiverWidth = width;
        editor.StartRiver();
        foreach (var (x, y) in points) Click(router, x, y);
        Assert.True(editor.FinishRiver(), editor.StatusDetail);
        return editor.SelectedRiver!;
    }

    [Fact]
    public void The_select_tool_picks_a_river_by_its_body_and_a_drag_moves_the_whole_system()
    {
        var (editor, router, _, _) = NewEditor();
        var river = CreateRiver(editor, router, 60, (100, 200), (400, 260), (700, 200));
        var channelId = river.Channels[0].Id;

        editor.ActiveTool = EditorTool.Select;
        editor.ClearWaterSelection();
        Assert.Equal(0, editor.WaterSelectionCount);

        // A point 24 units off the centreline is outside any node handle but well inside the
        // 30-unit half width the user actually sees.
        var samples = RiverGeometry.Evaluate(editor.Rivers[0]).Channels[channelId].Samples;
        var sample = samples[samples.Length / 2];
        Assert.True(sample.HalfWidth >= 29);
        var x = sample.X - sample.TangentY * 24;
        var y = sample.Y + sample.TangentX * 24;

        Click(router, x, y);
        Assert.Equal(1, editor.WaterSelectionCount);
        Assert.Equal(river.Id, editor.WaterSelectionIds[0]);
        Assert.NotNull(editor.WaterSelectionFrame);

        var depth = editor.Session.UndoDepth;
        var before = editor.Rivers[0].Channels[0].Nodes[0];
        Drag(router, x, y, x + 90, y - 40);
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        var after = editor.Rivers[0].Channels[0].Nodes[0];
        Assert.Equal(before.X + 90, after.X, 6);
        Assert.Equal(before.Y - 40, after.Y, 6);

        editor.Undo();
        Assert.Equal(before.X, editor.Rivers[0].Channels[0].Nodes[0].X, 6);
    }

    [Fact]
    public void A_rubber_band_takes_several_water_systems_and_one_delete_removes_them_with_one_undo()
    {
        var (editor, router, _, _) = NewEditor();
        var first = CreateRiver(editor, router, 12, (100, 100), (300, 140), (500, 100));
        var second = CreateRiver(editor, router, 12, (100, 300), (300, 340), (500, 300));

        editor.ActiveTool = EditorTool.Select;
        editor.ClearWaterSelection();
        router.Press(CanvasPointerButton.Left, 40, 40);
        router.Move(600, 420);
        router.Release(600, 420);
        Assert.Equal(2, editor.WaterSelectionCount);

        var depth = editor.Session.UndoDepth;
        Assert.True(editor.DeleteSelection());
        Assert.Empty(editor.Rivers);
        Assert.Equal(depth + 1, editor.Session.UndoDepth);

        editor.Undo();
        Assert.Equal(2, editor.Rivers.Count);
        Assert.Contains(editor.Rivers, r => r.Id == first.Id);
        Assert.Contains(editor.Rivers, r => r.Id == second.Id);
    }

    [Fact]
    public void Delete_addresses_the_chosen_level_and_a_tributary_survives_its_parent_node()
    {
        var (editor, router, _, _) = NewEditor();
        var river = CreateRiver(editor, router, 14, (100, 100), (300, 140), (500, 100), (700, 160));
        var main = river.Channels[0];

        // A tributary joining the middle of the main channel.
        editor.StartRiver();
        Click(router, 260, 20); Click(router, 300, 90);
        var geometry = RiverGeometry.Evaluate(editor.Rivers[0]).Channels[main.Id];
        var target = RiverGeometry.PointAt(geometry, 1, 0.5);
        Click(router, target.X, target.Y);
        Assert.Equal(2, editor.Rivers[0].Channels.Length);
        var tributaryId = editor.Rivers[0].Channels[1].Id;

        // Node level: Delete takes exactly one node of the selected channel.
        editor.SelectRiver(river.Id, main.Id);
        editor.WaterLevel = WaterSelectionLevel.Node;
        editor.SelectRiverNode(3);
        var nodesBefore = editor.Rivers[0].FindChannel(main.Id)!.Nodes.Length;
        var depth = editor.Session.UndoDepth;
        Assert.True(editor.DeleteSelection());
        Assert.Equal(nodesBefore - 1, editor.Rivers[0].FindChannel(main.Id)!.Nodes.Length);
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.Equal(2, editor.Rivers[0].Channels.Length);
        Assert.NotNull(editor.Rivers[0].FindChannel(tributaryId)!.Mouth);

        // Channel level: Delete takes the channel, and the parts attached to it stay on the map
        // with a free end instead of disappearing along with it.
        editor.WaterLevel = WaterSelectionLevel.Channel;
        editor.SelectRiverChannel(main.Id);
        depth = editor.Session.UndoDepth;
        Assert.True(editor.DeleteSelection());
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        var remaining = Assert.Single(editor.Rivers);
        var survivor = Assert.Single(remaining.Channels);
        Assert.Equal(tributaryId, survivor.Id);
        Assert.Null(survivor.Mouth);

        editor.Undo();
        Assert.Equal(2, editor.Rivers[0].Channels.Length);
        Assert.NotNull(editor.Rivers[0].FindChannel(tributaryId)!.Mouth);

        // System level: Delete takes the whole construction.
        editor.WaterLevel = WaterSelectionLevel.System;
        Assert.True(editor.DeleteSelection());
        Assert.Empty(editor.Rivers);
    }

    [Fact]
    public void Hidden_water_is_not_picked_and_locked_water_refuses_to_move_or_be_deleted()
    {
        var (editor, router, _, _) = NewEditor();
        var river = CreateRiver(editor, router, 40, (100, 200), (400, 240), (700, 200));

        editor.SelectWater(river.Id);
        editor.SetSelectedWaterLocked(true);
        Assert.True(editor.Rivers[0].Locked);
        var depth = editor.Session.UndoDepth;
        Assert.False(editor.DeleteSelection());
        Assert.Single(editor.Rivers);
        Assert.Equal(depth, editor.Session.UndoDepth);
        Assert.False(editor.BeginWaterDrag(400, 240));

        editor.SetSelectedWaterLocked(false);
        editor.SetSelectedWaterVisible(false);
        Assert.False(editor.Rivers[0].Visible);
        editor.ActiveTool = EditorTool.Select;
        editor.ClearWaterSelection();
        Click(router, 400, 240);
        Assert.Equal(0, editor.WaterSelectionCount);

        // One undo per switch, and the water comes back exactly as it was.
        editor.Undo();
        Assert.True(editor.Rivers[0].Visible);
    }

    [Fact]
    public void Water_moves_between_object_layers_as_one_undoable_step()
    {
        var (editor, router, first, second) = NewEditor();
        var river = CreateRiver(editor, router, 12, (100, 100), (300, 140), (500, 100));
        var source = editor.Session.Document.Layers.OfType<ObjectLayer>().Single(l => l.Rivers.Any(r => r.Id == river.Id)).Id;
        var target = source == first ? second : first;

        editor.SelectWater(river.Id);
        var depth = editor.Session.UndoDepth;
        Assert.True(editor.MoveSelectedWaterToLayer(target));
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.Empty(editor.Session.Document.Layers.OfType<ObjectLayer>().Single(l => l.Id == source).Rivers);
        Assert.Single(editor.Session.Document.Layers.OfType<ObjectLayer>().Single(l => l.Id == target).Rivers);

        editor.Undo();
        Assert.Single(editor.Session.Document.Layers.OfType<ObjectLayer>().Single(l => l.Id == source).Rivers);
        Assert.Empty(editor.Session.Document.Layers.OfType<ObjectLayer>().Single(l => l.Id == target).Rivers);
    }

    [Fact]
    public void The_objects_panel_lists_water_and_marks_the_selected_system()
    {
        var (editor, router, _, _) = NewEditor();
        var river = CreateRiver(editor, router, 12, (100, 100), (300, 140), (500, 100));
        editor.StartLake();
        Click(router, 600, 400); Click(router, 700, 380); Click(router, 660, 480);
        Assert.True(editor.FinishRiver(), editor.StatusDetail);
        var lake = editor.SelectedRiver!;

        editor.SelectWater(river.Id);
        var rows = editor.ObjectTree.Where(row => row.Kind == ObjectTreeRowKind.Water).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.WaterId == river.Id && row.Selected);
        Assert.Contains(rows, row => row.WaterId == lake.Id && !row.Selected);
        Assert.All(rows, row => Assert.True(row.WaterVisible));
    }

    [Fact]
    public void A_lake_drawn_off_the_land_is_created_visible_and_says_why()
    {
        var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var tiles = TileMap.Empty(1).With(new TileKey(0, 0), coverage);
        var land = new LandLayer(LayerId.New(), "Земля", true, false, 1, tiles, new LandEdgeStyle());
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        var document = new MapDocument(DocumentId.New(), "Water", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FFEEE6D2"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEEE6D2"), land, objects]);
        var memory = new WorkingMemoryBudget(() => new WorkingMemorySnapshot(
            16 * 1024 * WorkingMemoryBudget.MiB, 8 * 1024 * WorkingMemoryBudget.MiB, 128 * WorkingMemoryBudget.MiB));
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), memory: memory);
        editor.Session.Viewport.Set(0, 0, 1);
        var router = new CanvasInputRouter(editor);

        editor.StartLake();
        Click(router, 600, 600); Click(router, 720, 580); Click(router, 680, 700);
        Assert.True(editor.FinishRiver(), editor.StatusDetail);
        var lake = Assert.Single(editor.Rivers);
        Assert.True(lake.Channels[0].Closed);
        Assert.False(lake.Style.ClipToLand);
        Assert.Contains("вне суши", editor.StatusDetail);
        Assert.Contains("Озеро создано", editor.StatusDetail);
        Assert.Equal(1, editor.WaterSelectionCount);
        Assert.False(RiverVisibility.IsHiddenByLandClip(editor.Session.Document, editor.Rivers[0]));

        // A lake on the land keeps the clip and reports plain success.
        editor.StartLake();
        Click(router, 40, 40); Click(router, 160, 40); Click(router, 100, 160);
        Assert.True(editor.FinishRiver(), editor.StatusDetail);
        var onLand = editor.Rivers.Last();
        Assert.True(onLand.Style.ClipToLand);
        Assert.DoesNotContain("вне суши", editor.StatusDetail);
    }

    [Fact]
    public void Meanders_are_previewed_before_they_touch_the_map_and_smoothing_never_grows()
    {
        var (editor, router, _, _) = NewEditor();
        var river = CreateRiver(editor, router, 12, (100, 100), (300, 140), (500, 100), (700, 160));
        var channelId = river.Channels[0].Id;

        var committed = editor.Session.Document;
        var depth = editor.Session.UndoDepth;
        editor.RiverMeanderPercent = 25;
        editor.PreviewMeanderOnSelectedChannel();
        Assert.True(editor.HasRiverPreview);
        Assert.Same(committed, editor.Session.Document);
        Assert.Equal(4, editor.Rivers[0].FindChannel(channelId)!.Nodes.Length);
        Assert.True(editor.DisplayedRiver!.FindChannel(channelId)!.Nodes.Length > 4);
        Assert.Contains("Предпросмотр извилин", editor.StatusDetail);

        Assert.True(editor.ApplyRiverPreview());
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        var meandered = editor.Rivers[0].FindChannel(channelId)!.Nodes.Length;
        Assert.True(meandered > 4);

        // Smoothing the result keeps that number; only the positions relax.
        editor.SmoothSelectedChannel();
        Assert.Equal(meandered, editor.Rivers[0].FindChannel(channelId)!.Nodes.Length);
        editor.SmoothSelectedChannel();
        Assert.Equal(meandered, editor.Rivers[0].FindChannel(channelId)!.Nodes.Length);

        // And simplification brings a hand-made course back down with a stated accuracy.
        editor.RiverSimplifyTolerance = 12;
        editor.SimplifySelectedChannel();
        Assert.True(editor.Rivers[0].FindChannel(channelId)!.Nodes.Length < meandered);
    }
}
