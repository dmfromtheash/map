using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>R12 in the editor: the «маска обрыва» brush, river node features and the «Дельта» preview.</summary>
public sealed class WaterCliffsWorkflowTests
{
    private static EditorViewModel Editor(params MapLayer[] layers) => new(
        new MapDocument(DocumentId.New(), "R12", DateTimeOffset.UnixEpoch, new CanvasDefinition(1200, 800, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), .. layers]),
        null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());

    private static LandLayer Get(EditorViewModel editor, LayerId id) => (LandLayer)editor.Session.Document.FindLayer(id)!;

    [Fact]
    public void The_cliff_mask_brush_paints_wall_and_beach_each_clearing_the_other_in_one_undo_step()
    {
        var land = LandLayer.CreateEmpty(LayerId.New(), "Плато") with
        { GeometryTiles = TileMap.Empty(1).With(new(0, 0), Enumerable.Repeat((byte)255, 65536).ToArray()), Level = 1 };
        var editor = Editor(land);
        var router = new CanvasInputRouter(editor);
        editor.SelectLayer(land.Id); editor.ActiveTool = EditorTool.Land;
        editor.SetLandCliffMaskMode(true);
        Assert.True(editor.LandPaintMode); Assert.True(editor.LandCliffMaskMode);
        Assert.Equal(land.Id, editor.CliffMaskLayerId);
        editor.BrushSettings = editor.BrushSettings with { Radius = 16, Softness = 0 };
        editor.Session.Viewport.Set(0, 0, 1);

        editor.SetLandCliffMaskAction(CliffMaskAction.Wall);
        router.Press(CanvasPointerButton.Left, 40, 250); router.Move(120, 250);
        Assert.True(editor.StrokePreview is { IsCliffMask: true, IsLandPaint: false });
        router.Release(120, 250);
        var walled = Get(editor, land.Id);
        Assert.Same(land.GeometryTiles, walled.GeometryTiles);
        Assert.Equal(255, walled.CliffWallTiles.TryGet(new(0, 0))![250 * 256 + 80]);
        Assert.Equal(0, walled.CliffBeachTiles.TileCount);

        editor.SetLandCliffMaskAction(CliffMaskAction.Beach);
        router.Press(CanvasPointerButton.Left, 60, 250); router.Move(100, 250); router.Release(100, 250);
        var beach = Get(editor, land.Id);
        Assert.Equal(255, beach.CliffBeachTiles.TryGet(new(0, 0))![250 * 256 + 80]);
        Assert.Equal(0, beach.CliffWallTiles.TryGet(new(0, 0))![250 * 256 + 80]);
        Assert.Equal(255, beach.CliffWallTiles.TryGet(new(0, 0))![250 * 256 + 30]);

        editor.Undo();
        Assert.Equal(255, Get(editor, land.Id).CliffWallTiles.TryGet(new(0, 0))![250 * 256 + 80]);
        Assert.Equal(0, Get(editor, land.Id).CliffBeachTiles.TileCount);
        editor.Redo();

        editor.SetLandCliffMaskAction(CliffMaskAction.Erase);
        router.Press(CanvasPointerButton.Left, 30, 250); router.Move(130, 250); router.Release(130, 250);
        Assert.False(Get(editor, land.Id).HasCliffMask);

        editor.Undo();
        Assert.True(editor.ClearCliffMask());
        Assert.False(Get(editor, land.Id).HasCliffMask);

        // Back to the shape: the mode is left and strokes edit the coast again.
        editor.SetLandPaintMode(false);
        Assert.False(editor.LandCliffMaskMode);
        Assert.Null(editor.CliffMaskLayerId);
    }

    [Fact]
    public void The_relief_brush_raises_the_heights_in_one_undo_step_and_leaves_the_coast()
    {
        var land = LandLayer.CreateEmpty(LayerId.New(), "Плато") with
        { GeometryTiles = TileMap.Empty(1).With(new(0, 0), Enumerable.Repeat((byte)255, 65536).ToArray()) };
        var editor = Editor(land);
        var router = new CanvasInputRouter(editor);
        editor.SelectLayer(land.Id); editor.ActiveTool = EditorTool.Land;
        editor.SetLandReliefMode(true);
        Assert.True(editor.LandReliefMode); Assert.False(editor.LandCliffMaskMode);
        editor.BrushSettings = editor.BrushSettings with { Radius = 30, Softness = .5, Opacity = 1 };
        editor.Session.Viewport.Set(0, 0, 1);
        var depth = editor.Session.UndoDepth;
        router.Press(CanvasPointerButton.Left, 100, 100); router.Move(160, 100);
        Assert.True(editor.StrokePreview is { IsCliffMask: true });
        router.Release(160, 100);
        var raised = Get(editor, land.Id);
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.Same(land.GeometryTiles, raised.GeometryTiles);
        Assert.True(Map.Core.Editing.ReliefEditor.HeightAt(raised.ReliefTiles, 130, 100) > 20);
        editor.Undo();
        Assert.Equal(0, Get(editor, land.Id).ReliefTiles.TileCount);
        // Switching to the cliff mask leaves the relief mode; the shape leaves both.
        editor.SetLandCliffMaskMode(true);
        Assert.False(editor.LandReliefMode);
        editor.SetLandPaintMode(false);
        Assert.False(editor.LandCliffMaskMode);
    }

    [Fact]
    public void A_region_is_named_from_its_card_inside_the_shape_and_labels_give_way_by_settings()
    {
        var shape = VectorGeometry.CreateFreeShape(ObjectId.New(), "Фигура", [new(100, 100), new(900, 140), new(880, 400), new(120, 380)]);
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [shape]);
        var editor = Editor(objects);
        editor.Session.SelectedObjectId = shape.Id;
        Assert.True(editor.CanLabelRegion);
        var card = editor.CreateEntity(EntityKind.Region);
        editor.EditEntity(card, "Долина Ветров", EntityKind.Region, "", false);
        editor.Session.SelectedObjectId = shape.Id;
        Assert.True(editor.AddRegionLabel());
        var label = editor.Session.SelectedObject!;
        Assert.Equal("Долина Ветров", label.Text!.Content);
        Assert.Equal(4, label.Text.Importance);
        Assert.Equal(card, label.Text.Region!.EntityId);
        Assert.False(editor.AddRegionLabel(), "a second name for the same figure is refused");

        editor.Session.SelectedObjectId = label.Id;
        editor.SetLabelImportance(2);
        Assert.Equal(2, editor.Session.SelectedObject!.Text!.Importance);
        editor.SetLabelSettings(editor.LabelSettings with { ByScale = true, ResolveOverlaps = true });
        Assert.True(editor.LabelSettings.ByScale);
        editor.Undo();
        Assert.False(editor.LabelSettings.ByScale);

        editor.Session.SelectedObjectId = label.Id;
        editor.UnbindRegionLabel();
        Assert.Null(editor.Session.SelectedObject!.Text!.Region);
        editor.Undo();
        Assert.True(editor.AddRegionLabelForEntity(card) is false, "the figure already has its name");
    }

    [Fact]
    public void The_fog_brush_reveals_along_a_drag_as_one_step_and_only_in_the_notes_tool()
    {
        var editor = Editor(new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []));
        editor.ActiveTool = EditorTool.Notes;
        editor.FogBrush = true;
        Assert.False(editor.BeginFogStroke(100, 100), "the brush waits until the fog is on");
        editor.SetFogEnabled(true);
        editor.FogBrushRadius = 40;
        Assert.True(editor.BeginFogStroke(100, 100));
        editor.ContinueFogStroke(400, 100);
        Assert.NotNull(editor.FogStrokePreview);
        Assert.True(editor.EndFogStroke());
        Assert.True(editor.Fog.Sees(250, 100));
        Assert.False(editor.Fog.Sees(250, 300));
        Assert.True(editor.FogRevealedShare > 0);
        editor.Undo();
        Assert.False(editor.Fog.Sees(250, 100));
        editor.ActiveTool = EditorTool.Select;
        Assert.False(editor.BeginFogStroke(100, 100));
        Assert.True(editor.FogVeilToDraw > 0);
    }

    [Fact]
    public void A_version_is_compared_highlighted_shown_before_read_only_and_follows_edits()
    {
        var label = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Порт", 100, 100, 120, 40, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = "Порт" } };
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [label]);
        var editor = Editor(objects);
        Assert.True(editor.CreateCheckpoint("Версия 01"));
        var version = editor.Session.Document.Checkpoints.Single().Id;
        editor.Session.SelectedObjectId = label.Id;
        editor.SetTextContent("Порт Ветров");
        Assert.True(editor.CompareWithCheckpoint(version));
        Assert.Contains(editor.Comparison!.Summary(), line => line.StartsWith("Подписи: изменено 1", StringComparison.Ordinal));
        Assert.Single(editor.CompareMarks!);

        editor.CompareShowsBefore = true;
        Assert.Equal("Порт", editor.RenderDocument.Layers.OfType<ObjectLayer>().Single().Objects.Single().Text!.Content);
        var router = new CanvasInputRouter(editor);
        router.Press(CanvasPointerButton.Left, 10, 10);
        Assert.Contains("только просмотр", editor.StatusDetail);
        editor.CompareShowsBefore = false;
        Assert.Equal("Порт Ветров", editor.RenderDocument.Layers.OfType<ObjectLayer>().Single().Objects.Single().Text!.Content);

        editor.Session.SelectedObjectId = label.Id;
        editor.DeleteSelectedObject();
        Assert.Contains(editor.Comparison!.Summary(), line => line.StartsWith("Подписи: −1", StringComparison.Ordinal));
        editor.CloseComparison();
        Assert.Null(editor.CompareMarks);
    }

    [Fact]
    public void A_transition_is_placed_by_two_clicks_edited_and_undone()
    {
        var editor = Editor(new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []));
        editor.StartNewTransition(TransitionKind.CaveEntrance);
        Assert.True(editor.TransitionPickArmed);
        var router = new CanvasInputRouter(editor);
        var (sx, sy) = editor.Session.Viewport.WorldToScreen(200, 150);
        router.Press(CanvasPointerButton.Left, sx, sy);
        router.Release(sx, sy);
        var placed = Assert.Single(editor.Transitions);
        Assert.Equal(200, placed.From.X, 0);
        Assert.Null(placed.To);
        Assert.Equal(placed.Id, editor.SelectedTransitionId);

        editor.PickTransitionTarget();
        var (tx, ty) = editor.Session.Viewport.WorldToScreen(600, 400);
        router.Press(CanvasPointerButton.Left, tx, ty);
        router.Release(tx, ty);
        Assert.Equal(600, editor.Transitions.Single().To!.X, 0);
        Assert.Contains("на этой карте", editor.TransitionTargetTitle(editor.Transitions.Single()));

        editor.EditTransition(placed.Id, "Пасть дракона", TransitionKind.CaveEntrance, TransitionDirection.Forward, 30);
        Assert.Equal("Пасть дракона", editor.Transitions.Single().Name);
        editor.DeleteTransition(placed.Id);
        Assert.Empty(editor.Transitions);
        editor.Undo();
        Assert.Equal(TransitionDirection.Forward, editor.Transitions.Single().Direction);
    }

    [Fact]
    public void The_year_slider_previews_while_dragging_and_commits_one_step()
    {
        var town = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Ильград", 100, 100, 120, 40, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = "Ильград" } };
        var editor = Editor(new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [town]));
        editor.Session.SelectedObjectId = town.Id;
        editor.SetSelectedEra(812, 1040);
        Assert.Equal(new Era(812, 1040), editor.Session.SelectedObject!.Era);
        editor.SetTimeline(editor.Timeline with { Enabled = true, First = 700, Last = 1200, Year = 900 });
        int Shown() => editor.RenderDocument.Layers.OfType<ObjectLayer>().Single().Objects.Count;
        Assert.Equal(1, Shown());
        var steps = editor.Session.Revision;
        editor.SetTimelineYear(1100, commit: false);
        Assert.Equal(0, Shown());
        Assert.Equal(900, editor.Timeline.Year);
        Assert.Equal(steps, editor.Session.Revision);
        editor.SetTimelineYear(1100, commit: true);
        Assert.Equal(1100, editor.Timeline.Year);
        editor.Undo();
        Assert.Equal(900, editor.Timeline.Year);
        Assert.Equal(1, Shown());
        editor.SetSelectedEra(1300, 1200);
        Assert.Contains("позже", editor.StatusDetail);
    }

    [Fact]
    public void A_card_is_made_from_the_selection_bound_linked_and_undone()
    {
        var label = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Ильград", 100, 100, 120, 40, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = "Ильград" } };
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [label]);
        var editor = Editor(objects);
        editor.Session.SelectedObjectId = label.Id;
        var id = editor.CreateEntity(EntityKind.Place);
        var card = Assert.Single(editor.Entities);
        Assert.Equal("Ильград", card.Name);
        Assert.Equal(EntityTargetKind.Object, card.Bindings.Single().Kind);
        Assert.Equal(label.Id.Value, card.Bindings.Single().TargetId);
        Assert.Single(editor.EntitiesOfSelection());

        editor.Session.SelectedObjectId = null;
        var river = editor.CreateEntity(EntityKind.River);
        editor.EditEntity(river, "Сельва", EntityKind.River, "Течёт мимо [[Ильград]].", false);
        Assert.Single(editor.EntityBacklinks(editor.Entities.First(e => e.Id == id)));
        editor.UnbindEntity(id, 0);
        Assert.Empty(editor.Entities.First(e => e.Id == id).Bindings);
        editor.Undo();
        Assert.Single(editor.Entities.First(e => e.Id == id).Bindings);
        editor.DeleteEntity(river);
        Assert.Single(editor.Entities);

        var note = editor.AddNoteAt(10, 10);
        editor.SetNoteWiki(note, null, NoteCategory.Quest, true);
        Assert.Equal(NoteCategory.Quest, editor.Notes.Single().Category);
        Assert.True(editor.Notes.Single().PlayerVisible);
    }

    [Fact]
    public void Auto_road_and_river_to_sea_take_their_points_from_clicks()
    {
        var island = LandLayer.CreateEmpty(LayerId.New(), "Остров") with
        { GeometryTiles = TileMap.Empty(1).With(new(0, 0), Enumerable.Range(0, 65536).Select(i => (byte)(i % 256 is > 10 and < 245 && i / 256 is > 10 and < 245 ? 255 : 0)).ToArray()) };
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        var editor = Editor(island, objects);
        var router = new CanvasInputRouter(editor);
        editor.SelectLayer(objects.Id); editor.ActiveTool = EditorTool.Road;
        editor.Session.Viewport.Set(0, 0, 1);
        editor.ArmAutoRoad();
        Assert.Equal(GeographyPick.RoadFrom, editor.GeographyPick);
        router.Press(CanvasPointerButton.Left, 40, 40); router.Release(40, 40);
        Assert.Equal(GeographyPick.RoadTo, editor.GeographyPick);
        var depth = editor.Session.UndoDepth;
        router.Press(CanvasPointerButton.Left, 200, 200); router.Release(200, 200);
        Assert.Equal(GeographyPick.None, editor.GeographyPick);
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.Single(((ObjectLayer)editor.Session.Document.FindLayer(objects.Id)!).Roads);

        editor.ActiveTool = EditorTool.River;
        editor.ArmRiverToSea();
        router.Press(CanvasPointerButton.Left, 120, 120); router.Release(120, 120);
        var river = Assert.Single(((ObjectLayer)editor.Session.Document.FindLayer(objects.Id)!).Rivers);
        Assert.True(river.Channels[0].Nodes.Length >= 3);
        var issues = editor.CheckGeography();
        Assert.All(issues, i => Assert.NotEqual(river.Id, i.ObjectId));
    }

    [Fact]
    public void Sea_manner_strength_and_reach_are_undoable_and_keep_each_other()
    {
        var editor = Editor();
        Assert.Null(editor.Sea);
        editor.ApplySeaManner(SeaManner.DepthBands);
        Assert.Equal(SeaStyle.For(SeaManner.DepthBands), editor.Sea);
        editor.SetSeaStrength(40);
        editor.SetSeaScale(200);
        editor.ApplySeaManner(SeaManner.Engraved);
        Assert.Equal(SeaManner.Engraved, editor.Sea!.Manner);
        Assert.Equal(.4, editor.Sea.Strength, 6);
        Assert.Equal(2, editor.Sea.Scale, 6);
        editor.Undo();
        Assert.Equal(SeaManner.DepthBands, editor.Sea!.Manner);
        editor.ClearSea();
        Assert.Null(editor.Sea);
        editor.Undo();
        Assert.NotNull(editor.Sea);
    }

    [Fact]
    public void Node_feature_and_delta_preview_are_undoable_edits_of_the_selected_river()
    {
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        var editor = Editor(objects);
        var river = new River { Name = "Река", Channels = [RiverEditor.NewChannel([new(100, 400), new(400, 400), new(700, 420), new(1000, 400)], 20)] };
        editor.Session.Execute(new RiverCommand(objects.Id, river.Id, river, "create"));
        var main = river.Channels[0];
        editor.SelectRiver(river.Id, main.Id);
        editor.SelectRiverNode(1);
        Assert.Equal(RiverNodeFeature.None, editor.SelectedRiverNodeFeature);

        editor.SetSelectedRiverNodeFeature(RiverNodeFeature.Waterfall);
        Assert.Equal(RiverNodeFeature.Waterfall, editor.SelectedRiverNodeFeature);
        Assert.Equal(RiverNodeFeature.Waterfall, editor.Rivers.Single().Channels[0].Nodes[1].Feature);
        editor.Undo();
        Assert.Equal(RiverNodeFeature.None, editor.Rivers.Single().Channels[0].Nodes[1].Feature);

        Assert.True(editor.CanBuildDelta);
        editor.RiverDeltaArms = 4;
        var before = editor.Session.Document;
        editor.PreviewDeltaOnSelectedChannel();
        Assert.True(editor.HasRiverPreview);
        Assert.Same(before, editor.Session.Document);
        Assert.True(editor.ApplyRiverPreview());
        Assert.Equal(4, editor.Rivers.Single().Channels.Length);
        editor.Undo();
        Assert.Single(editor.Rivers.Single().Channels);
    }
}
