using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>The river tool from the router down: create, widen, attach a tributary, move its mouth, undo/redo.</summary>
public sealed class Stage19RiverWorkflowTests
{
    private static (EditorViewModel Editor, CanvasInputRouter Router) NewEditor()
    {
        var document = new MapDocument(DocumentId.New(), "Rivers", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FFEEE6D2"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEEE6D2"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog());
        editor.Session.Viewport.Set(0, 0, 1);
        return (editor, new CanvasInputRouter(editor));
    }

    private static void Click(CanvasInputRouter router, double x, double y)
    { router.Press(CanvasPointerButton.Left, x, y); router.Release(x, y); }

    private static void Drag(CanvasInputRouter router, double x, double y, double toX, double toY)
    { router.Press(CanvasPointerButton.Left, x, y); router.Move(toX, toY); router.Release(toX, toY); }

    [Fact]
    public void Create_widen_attach_tributary_move_mouth_and_undo_redo_exactly()
    {
        var (editor, router) = NewEditor();
        editor.NewRiverWidth = 10;
        editor.StartRiver();
        Assert.Equal(EditorTool.River, editor.ActiveTool);
        var before = editor.Session.Document;
        Click(router, 100, 100); Click(router, 400, 220); Click(router, 700, 180); Click(router, 1000, 400);
        Assert.Same(before, editor.Session.Document);
        Assert.True(editor.IsCreatingRiver);
        Assert.True(editor.FinishRiver(), editor.StatusDetail);
        var river = Assert.Single(editor.Rivers);
        var main = Assert.Single(river.Channels);
        Assert.Equal(4, main.Nodes.Length);
        Assert.Equal(1, editor.Session.UndoDepth);

        // Width in the last node, then a whole profile: no contour is edited by hand.
        editor.SelectRiverNode(3);
        editor.SetSelectedRiverNodeWidth(30);
        Assert.Equal(30, editor.Rivers[0].Channels[0].Nodes[3].Width);
        editor.SetSelectedChannelWidthProfile(6, 34);
        var profile = editor.Rivers[0].Channels[0].Nodes;
        Assert.Equal(6, profile[0].Width); Assert.Equal(34, profile[3].Width);
        Assert.True(profile[1].Width < profile[2].Width);

        // A tributary: two nodes, then a click on the main channel attaches it there.
        editor.StartRiver();
        Click(router, 500, 40); Click(router, 560, 120);
        var geometry = RiverGeometry.Evaluate(editor.Rivers[0]).Channels[main.Id];
        var target = RiverGeometry.PointAt(geometry, 1, 0.5);
        Click(router, target.X, target.Y);
        Assert.False(editor.IsCreatingRiver);
        river = Assert.Single(editor.Rivers);
        Assert.Equal(2, river.Channels.Length);
        var tributary = river.Channels[1];
        Assert.Equal(main.Id, tributary.Mouth!.ParentChannelId);
        Assert.Equal(main.Nodes[1].Id, tributary.Mouth.ParentNodeId);
        Assert.Equal(tributary.Id, editor.SelectedRiverChannel!.Id);
        var attached = editor.Session.Document;

        // Drag the mouth diamond onto the next segment of the parent.
        var mouth = RiverGeometry.Evaluate(river).Channels[tributary.Id].End;
        var newSpot = RiverGeometry.PointAt(RiverGeometry.Evaluate(river).Channels[main.Id], 2, 0.4);
        Drag(router, mouth.X, mouth.Y, newSpot.X, newSpot.Y);
        var movedMouth = editor.Rivers[0].FindChannel(tributary.Id)!.Mouth!;
        Assert.Equal(main.Nodes[2].Id, movedMouth.ParentNodeId);
        Assert.InRange(movedMouth.Fraction, 0.2, 0.6);
        Assert.Equal(main.Nodes.ToArray(), editor.Rivers[0].FindChannel(main.Id)!.Nodes.Select(n => n with { Width = main.Nodes[0].Width }).Select((n, i) => n with { Width = main.Nodes[i].Width }).ToArray());
        var moved = editor.Session.Document;

        editor.Undo(); Assert.Same(attached, editor.Session.Document);
        editor.Redo(); Assert.Same(moved, editor.Session.Document);

        // Dragging a parent node keeps the tributary attached, and the rest of the parent untouched.
        var node = editor.Rivers[0].FindChannel(main.Id)!.Nodes[2];
        editor.SelectRiverChannel(main.Id);
        Drag(router, node.X, node.Y, node.X + 40, node.Y + 60);
        var after = editor.Rivers[0];
        Assert.Equal(node.X + 40, after.FindChannel(main.Id)!.Nodes[2].X);
        Assert.True(RiverGeometry.Evaluate(after).Channels[tributary.Id].JoinsParent);
        Assert.Equal(movedMouth, after.FindChannel(tributary.Id)!.Mouth);

        // Detach, delete channel, delete river; each is one exact history step.
        editor.SelectRiverChannel(tributary.Id);
        editor.DetachSelectedChannel();
        Assert.Null(editor.Rivers[0].FindChannel(tributary.Id)!.Mouth);
        editor.DeleteSelectedChannel();
        Assert.Single(editor.Rivers[0].Channels);
        var beforeDelete = editor.Session.Document;
        editor.DeleteSelectedRiver();
        Assert.Empty(editor.Rivers);
        editor.Undo(); Assert.Same(beforeDelete, editor.Session.Document);
        Assert.Single(editor.Rivers);
    }

    [Fact]
    public void Freehand_stroke_lake_distributary_split_and_flow_width_work_through_the_router()
    {
        var (editor, router) = NewEditor();
        editor.NewRiverWidth = 10;
        // Freehand: one drag becomes a simplified course.
        editor.RiverDrawMode = RiverDrawMode.Freehand;
        editor.StartRiver();
        router.Press(CanvasPointerButton.Left, 100, 400);
        for (var i = 1; i <= 40; i++) router.Move(100 + i * 20, 400 + Math.Sin(i / 4.0) * 60);
        router.Release(900, 400 + Math.Sin(10) * 60);
        var river = Assert.Single(editor.Rivers);
        var main = Assert.Single(river.Channels);
        Assert.InRange(main.Nodes.Length, 4, 30);
        Assert.False(editor.IsCreatingRiver);

        // A lake from clicks.
        editor.RiverDrawMode = RiverDrawMode.Nodes;
        editor.StartLake();
        Click(router, 300, 100); Click(router, 420, 80); Click(router, 470, 170); Click(router, 380, 250); Click(router, 290, 210);
        Assert.True(editor.FinishRiver(), editor.StatusDetail);
        Assert.Equal(2, editor.Rivers.Count);
        var lakeRiver = editor.Rivers.Single(r => r.Channels.Any(c => c.Closed));
        var lake = lakeRiver.Channels.Single();
        Assert.True(lake.Closed); Assert.Equal(5, lake.Nodes.Length);

        // A distributary: the first click lands on the main channel, the stroke leaves it.
        editor.StartRiver();
        var geometry = RiverGeometry.Evaluate(editor.Rivers.Single(r => r.Id == river.Id)).Channels[main.Id];
        var on = RiverGeometry.PointAt(geometry, 2, 0.5);
        Click(router, on.X, on.Y); Click(router, on.X + 80, on.Y + 160); Click(router, on.X + 120, on.Y + 300);
        Assert.True(editor.FinishRiver(), editor.StatusDetail);
        var withBranch = editor.Rivers.Single(r => r.Id == river.Id);
        Assert.Equal(2, withBranch.Channels.Length);
        var branch = withBranch.Channels[1];
        Assert.Equal(main.Id, branch.Source!.ParentChannelId);
        Assert.Null(branch.Mouth);

        // A tributary that ends on the lake shore merges the lake into the river system.
        editor.StartRiver();
        Click(router, 120, 60); Click(router, 220, 90);
        var lakeGeometry = RiverGeometry.Evaluate(editor.Rivers.Single(r => r.Id == lakeRiver.Id)).Channels[lake.Id];
        var shore = RiverGeometry.PointAt(lakeGeometry, 4, 0.5);
        Click(router, shore.X, shore.Y);
        Assert.False(editor.IsCreatingRiver);
        var lakeSystem = editor.Rivers.Single(r => r.Channels.Any(c => c.Closed));
        Assert.Equal(2, lakeSystem.Channels.Length);
        Assert.Equal(lake.Id, lakeSystem.Channels.Last().Mouth!.ParentChannelId);

        // Split in the middle, detach the lower part as a river, width by flow.
        editor.SelectRiver(river.Id, main.Id);
        editor.SelectRiverNode(2);
        Assert.True(editor.CanSplitSelectedChannel);
        editor.SplitSelectedChannel();
        Assert.Equal(3, editor.Rivers.Single(r => r.Id == river.Id).Channels.Length);
        Assert.NotEqual(main.Id, editor.SelectedRiverChannel!.Id);
        var rivers = editor.Rivers.Count;
        editor.SelectRiverChannel(main.Id);
        editor.SelectRiverNode(1);
        editor.DetachDownstreamAsRiver();
        Assert.Equal(rivers + 1, editor.Rivers.Count);
        Assert.NotEqual(river.Id, editor.SelectedRiver!.Id);
        editor.SelectRiver(river.Id, main.Id);
        editor.RiverFlowSourceWidth = 3; editor.RiverFlowGrowth = 12;
        editor.ApplyWidthByFlow();
        Assert.Equal(3, editor.Rivers.Single(r => r.Id == river.Id).FindChannel(main.Id)!.Nodes[0].Width);
        editor.SetRiverEdgeRoughnessPercent(40); editor.SetRiverCutsLand(false); editor.SetRiverClipToLand(false);
        var style = editor.Rivers.Single(r => r.Id == river.Id).Style;
        Assert.Equal(0.4, style.EdgeRoughness); Assert.False(style.CutsLand); Assert.False(style.ClipToLand);
        editor.Undo(); editor.Undo(); editor.Undo();
        Assert.True(editor.Rivers.Single(r => r.Id == river.Id).Style.CutsLand);
    }

    [Fact]
    public void Escape_cancels_a_draft_without_history_and_select_tool_ignores_rivers()
    {
        var (editor, router) = NewEditor();
        editor.StartRiver();
        Click(router, 100, 100); Click(router, 300, 140);
        var before = editor.Session.Document;
        Assert.True(editor.HandleEscape());
        Assert.False(editor.IsCreatingRiver);
        Assert.Same(before, editor.Session.Document);
        Assert.Equal(0, editor.Session.UndoDepth);

        editor.StartRiver();
        Click(router, 100, 300); Click(router, 500, 320); editor.FinishRiver();
        editor.ActiveTool = EditorTool.Select;
        Assert.False(editor.PressRiver(300, 310));
        editor.ActiveTool = EditorTool.River;
        Assert.True(editor.PressRiver(300, 310));
        Assert.NotNull(editor.SelectedRiver);
    }

    [Fact]
    public void Style_edits_are_single_history_steps_and_hidden_layer_refuses_edits()
    {
        var (editor, router) = NewEditor();
        editor.StartRiver();
        Click(router, 100, 300); Click(router, 500, 320); editor.FinishRiver();
        var depth = editor.Session.UndoDepth;
        editor.SetRiverWaterColor("#2266AA");
        editor.SetRiverBankShadowPercent(50);
        editor.SetRiverWidthScalePercent(150);
        editor.SetRiverMouthFade(40);
        editor.SetRiverName("Ока");
        var river = editor.Rivers[0];
        Assert.Equal("#FF2266AA", river.Style.WaterColor);
        Assert.Equal(0.5, river.Style.BankShadowOpacity);
        Assert.Equal(1.5, river.WidthScale);
        Assert.Equal(40, river.Style.MouthFade);
        Assert.Equal("Ока", river.Name);
        Assert.Equal(depth + 5, editor.Session.UndoDepth);

        var layer = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
        editor.Session.Execute(UpdateLayerCommand.SetLocked(layer, true));
        editor.SetRiverOpacityPercent(30);
        Assert.Equal(1, editor.Rivers[0].Style.Opacity);
        Assert.Contains("заблокирован", editor.StatusDetail);
    }
}
