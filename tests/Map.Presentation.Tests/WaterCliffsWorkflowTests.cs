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

        // A river gets its years the same way and is gone after them.
        var river = new River { Name = "Старица", Channels = [RiverEditor.NewChannel([new(10, 400), new(300, 420)], 8)] };
        var layer = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
        editor.Session.Execute(new RiverCommand(layer.Id, river.Id, river, "Река"));
        editor.Session.SelectedObjectId = null;
        editor.SelectRiver(river.Id);
        Assert.True(editor.HasTimedSelection);
        editor.SetSelectedEra(null, 850);
        Assert.Equal(new Era(null, 850), editor.SelectedEra);
        Assert.Empty(editor.RenderDocument.Layers.OfType<ObjectLayer>().Single().Rivers);
    }

    [Fact]
    public void Pointcrawl_places_and_ways_are_put_by_clicks_and_the_key_is_written()
    {
        var editor = Editor(new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []));
        var router = new CanvasInputRouter(editor);
        void Click(double x, double y) { var (sx, sy) = editor.Session.Viewport.WorldToScreen(x, y); router.Press(CanvasPointerButton.Left, sx, sy); router.Release(sx, sy); }
        editor.StartAddPointcrawlPlace(); Click(100, 100);
        editor.StartAddPointcrawlPlace(); Click(500, 300);
        Assert.Equal(2, editor.Pointcrawl.Nodes.Length);
        editor.StartLinkPointcrawl(); Click(102, 98); Click(498, 303);
        var way = Assert.Single(editor.Pointcrawl.Edges);
        editor.EditPointcrawlEdge(way.Id, "Тракт", 6, 2, hidden: false, oneWay: true);
        Assert.Equal(2, editor.Pointcrawl.Edges.Single().Danger);
        var first = editor.Pointcrawl.Nodes[0];
        editor.EditPointcrawlNode(first.Id, "Деревня", "Таверна «Якорь»", visited: true, null);
        editor.DeletePointcrawlNode(first.Id);
        Assert.Empty(editor.Pointcrawl.Edges);
        editor.Undo();
        Assert.Single(editor.Pointcrawl.Edges);

        var path = Path.Combine(Path.GetTempPath(), "map-crawl-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            var pages = editor.ExportPointcrawlKeyAsync(path, players: true).GetAwaiter().GetResult();
            Assert.True(pages >= 2);
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 4));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Only_a_two_to_one_map_becomes_a_planet_and_a_projection_is_written()
    {
        var editor = Editor();
        editor.SetPlanet(true, true);
        Assert.False(editor.Planet.Enabled);
        Assert.Contains("2:1", editor.StatusDetail);

        var planet = new EditorViewModel(new MapDocument(DocumentId.New(), "Планета", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 512, "px", "#FF8FB0BA"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF8FB0BA")]),
            null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());
        planet.SetPlanet(true, true);
        Assert.True(planet.Planet.Enabled);
        var path = Path.Combine(Path.GetTempPath(), "map-planet-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            Assert.True(planet.ExportProjectionAsync(path, Map.Export.PlanetProjection.Robinson, 600, 0, 0, 12).GetAwaiter().GetResult());
            using var written = SkiaSharp.SKBitmap.Decode(path);
            Assert.Equal(600, written.Width);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        planet.Undo();
        Assert.False(planet.Planet.Enabled);
    }

    [Fact]
    public void A_sector_is_dressed_systems_set_by_number_imported_and_exported()
    {
        var editor = new EditorViewModel(HexMapFactory.Create("Сектор", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"), HexOrientation.Flat, 40, 8, 10, "#FF8FB0BA"),
            null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());
        editor.MakeStarSector();
        Assert.Equal(StarSector.Background, editor.Session.Document.Layers.OfType<BaseColorLayer>().Single().Color);
        Assert.Equal("пк", editor.SectorLayer!.Travel.Unit);
        Assert.False(editor.SetSystem("0101", new StarSystem { Uwp = "bad" }));
        Assert.False(editor.SetSystem("9999", new StarSystem { Uwp = "A788899-C" }));
        Assert.True(editor.SetSystem("0203", new StarSystem { Name = "Регина", Uwp = "A788899-C" }));
        Assert.Equal("Регина", editor.SystemAt("0203")!.Name);
        var text = editor.ExportSectorData();
        Assert.Contains("0203\tРегина\tA788899-C", text);
        Assert.Equal(1, editor.ImportSectorData("Hex\tName\tUWP\n0101\tЭфат\tA646930-D\n9999\tДалеко\tA646930-D\n"));
        Assert.Equal(2, editor.SectorSystems.Count);
        editor.Undo();
        Assert.Single(editor.SectorSystems);
        Assert.True(editor.SetSystem("0203", null));
        Assert.Empty(editor.SectorSystems);
    }

    [Fact]
    public void A_city_from_a_point_is_a_child_map_with_streets_along_the_roads_and_its_name_on_the_parent()
    {
        var store = new FakeProjectStore();
        var road = RoadEditor.NewRoad([new(0, 500), new(1200, 500)], 12, RoadStyle.Country(), "Тракт");
        var parentPath = Path.Combine(Path.GetTempPath(), "map-city-" + Guid.NewGuid().ToString("N"), "world.mapproj");
        var editor = new EditorViewModel(new MapDocument(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(4000, 3000, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Roads = [road] }]),
            parentPath, false, store, new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());
        editor.StartCityFromPoint();
        Assert.True(editor.CityPickArmed);
        var path = editor.CreateCityAtAsync(600, 500).GetAwaiter().GetResult();
        Assert.NotNull(path);
        var child = store.Saved[path!].Document;
        Assert.Equal("city", child.Category.Value);
        Assert.Contains(child.Layers.OfType<ObjectLayer>(), l => l.Name == "Город");
        // The road crossed the area: carried in, and a main street points along it.
        Assert.Contains(child.Layers.OfType<ObjectLayer>().SelectMany(l => l.Roads), r => r.Inherited is not null);
        Assert.Contains(editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects), o => o.Kind == SceneObjectKind.Text && o.Text!.Content == child.Title);
        if (Directory.Exists(Path.GetDirectoryName(parentPath))) Directory.Delete(Path.GetDirectoryName(parentPath)!, recursive: true);
    }

    [Fact]
    public void A_city_map_is_regenerated_as_one_step_keeping_other_layers()
    {
        var city = CityBuilder.NewCityMap("Ильград", 2000, new CitySettings { Seed = 5 }, new MapCategoryId("city"), DateTimeOffset.UnixEpoch);
        var mine = new ObjectLayer(LayerId.New(), "Моё", true, false, 1, []);
        var editor = new EditorViewModel(city.WithLayers([.. city.Layers, mine]), null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());
        Assert.True(editor.IsCityWorkshop);
        var before = editor.Session.Document.Layers.OfType<ObjectLayer>().Single(l => l.Name == "Город").Objects.Count;
        editor.CitySettings = editor.CitySettings with { Seed = 99, Rings = 2 };
        Assert.True(editor.RegenerateCity());
        var after = editor.Session.Document.Layers.OfType<ObjectLayer>().Single(l => l.Name == "Город").Objects.Count;
        Assert.NotEqual(before, after);
        Assert.Contains(editor.Session.Document.Layers, l => l.Id == mine.Id);
        editor.Undo();
        Assert.Equal(before, editor.Session.Document.Layers.OfType<ObjectLayer>().Single(l => l.Name == "Город").Objects.Count);
    }

    [Fact]
    public void Rebuilding_a_city_keeps_an_empty_hand_note_that_looks_like_the_key_and_survives_reopen()
    {
        // R35b: the user's own empty note «1. Храм» is not the generator's key, whatever it is called.
        var root = Path.Combine(Path.GetTempPath(), "map-r35b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var city = CityBuilder.NewCityMap("Ильград", 2000, new CitySettings { Seed = 5 }, new MapCategoryId("city"), DateTimeOffset.UnixEpoch);
            var keys = city.Notes.Count;
            Assert.All(city.Notes, n => Assert.Equal(NoteOrigin.CityKey, n.Origin));
            var store = new Map.Presentation.Services.ProjectStore(new Map.Persistence.Recovery.RecoveryJournal(Path.Combine(root, "recovery")));
            var editor = new EditorViewModel(city, null, false, store, new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());
            var mine = new MapNote(NoteId.New(), "1. Храм", "", NoteAnchorKind.World, 300, 300);
            editor.Session.Execute(new AddNoteCommand(mine));

            editor.CitySettings = editor.CitySettings with { Seed = 99 };
            Assert.True(editor.RegenerateCity());
            var notes = editor.Session.Document.Notes;
            Assert.Contains(notes, n => n.Id == mine.Id);
            Assert.Equal(notes.Count - 1, notes.Count(n => n.Origin == NoteOrigin.CityKey));
            editor.Undo();
            Assert.Equal(keys + 1, editor.Session.Document.Notes.Count);

            // Saved and opened again, the mark stays: the next rebuild still keeps the note and does not double the key.
            var path = Path.Combine(root, "city.mapproj");
            store.Save(editor.Session.Document, path, new("r35b", DateTimeOffset.UtcNow), Map.Rendering.ThumbnailRenderer.RenderPng(editor.Session.Document));
            var reopened = new EditorViewModel(store.Open(path).Document, path, false, store, new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());
            Assert.Equal(keys, reopened.Session.Document.Notes.Count(n => n.Origin == NoteOrigin.CityKey));
            Assert.True(reopened.RegenerateCity());
            Assert.Single(reopened.Session.Document.Notes, n => n.Id == mine.Id && n.Origin == NoteOrigin.Manual);
            Assert.Equal(reopened.Session.Document.Notes.Count - 1, reopened.Session.Document.Notes.Count(n => n.Origin == NoteOrigin.CityKey));

            // A key note the user writes into becomes theirs.
            var key = reopened.Session.Document.Notes.First(n => n.Origin == NoteOrigin.CityKey);
            reopened.EditNote(key.Id, key.Title, "Здесь живёт настоятель.");
            Assert.True(reopened.RegenerateCity());
            Assert.Contains(reopened.Session.Document.Notes, n => n.Id == key.Id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task An_empty_change_package_says_what_changed_but_does_not_travel()
    {
        // R42b: only the fog changed since the version - no file, and the message names the fog.
        var editor = Editor(new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []));
        Assert.True(editor.CreateCheckpoint("Версия 01"));
        editor.SetFogEnabled(true);
        editor.RevealAllFog();
        var path = Path.Combine(Path.GetTempPath(), "map-r42b-" + Guid.NewGuid().ToString("N") + ".mapchanges");
        Assert.Null(await editor.ExportChangesAsync(editor.Session.Document.Checkpoints[0].Id, path));
        Assert.False(File.Exists(path));
        Assert.Contains("Пакет пуст", editor.StatusDetail, StringComparison.Ordinal);
        Assert.Contains("туман", editor.StatusDetail, StringComparison.Ordinal);
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
