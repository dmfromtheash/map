using Map.Core.Documents;
using Map.Core.Resources;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;

namespace Map.Presentation.Tests;

public sealed class HexWorkshopToolsTests
{
    private static EditorViewModel HexEditor(int columns = 20, int rows = 15)
    {
        var wizard = new NewMapWizardViewModel
        {
            Category = MapCategories.Hex.Id,
            HexColumnsText = columns.ToString(System.Globalization.CultureInfo.InvariantCulture),
            HexRowsText = rows.ToString(System.Globalization.CultureInfo.InvariantCulture),
            HexSizeText = "48",
        };
        var document = wizard.Create(DateTimeOffset.UnixEpoch)!;
        var memory = new WorkingMemoryBudget(() => new WorkingMemorySnapshot(
            16 * 1024 * WorkingMemoryBudget.MiB, 8 * 1024 * WorkingMemoryBudget.MiB, 128 * WorkingMemoryBudget.MiB));
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), memory: memory);
        editor.ActiveTool = EditorTool.Hex;
        return editor;
    }

    private static HexLayer Hexes(EditorViewModel editor, int level = 0) =>
        editor.Session.Document.Layers.OfType<HexLayer>().Single(layer => layer.Level == level);

    private static HexCoord Hex(EditorViewModel editor, int column, int row) =>
        Hexes(editor).Board.Layout.FromOffset(new HexOffset(column, row));

    private static (double X, double Y) At(EditorViewModel editor, int column, int row) =>
        Hexes(editor).Board.Layout.Center(Hex(editor, column, row));

    private static void Drag(EditorViewModel editor, params (int Column, int Row)[] points)
    {
        var (x, y) = At(editor, points[0].Column, points[0].Row);
        if (!editor.BeginHexGesture(x, y)) return;
        foreach (var (column, row) in points.Skip(1))
        {
            var (px, py) = At(editor, column, row);
            editor.ContinueHexGesture(px, py);
        }

        editor.EndHexGesture();
    }

    private static void Fill(EditorViewModel editor, string tile)
    {
        editor.HexTileId = tile;
        editor.HexMode = HexToolMode.Fill;
        var (x, y) = At(editor, 0, 0);
        editor.BeginHexGesture(x, y);
    }

    [Fact]
    public void A_mixed_brush_puts_both_tiles_and_a_turned_brush_turns_the_hexes()
    {
        var editor = HexEditor();
        editor.HexTileId = "starter/forest";
        editor.ToggleHexMix("starter/pines");
        Assert.Equal(2, editor.HexMix.Count);
        editor.HexRadius = 3;
        Drag(editor, (8, 7));
        var terrains = Hexes(editor).Board.Cells.Select(pair => pair.Value.Terrain).Distinct().ToList();
        Assert.Contains("starter/forest", terrains);
        Assert.Contains("starter/pines", terrains);

        editor.ToggleHexMix("starter/pines");
        Assert.Empty(editor.HexMix);
        editor.HexTileId = "starter/canyon";
        editor.HexRadius = 0;
        editor.RotateHexBrush(2);
        Drag(editor, (1, 1));
        Assert.Equal(2, Hexes(editor).Board[Hex(editor, 1, 1)]!.Rotation);
        editor.HexTileId = "starter/peaks";
        Drag(editor, (2, 1));
        Assert.Equal(0, Hexes(editor).Board[Hex(editor, 2, 1)]!.Rotation);
        Assert.Equal("7 гексов", new Func<string>(() => { editor.HexRadius = 1; return editor.HexFootprintText; })());
    }

    [Fact]
    public void Regions_are_painted_and_removed_with_their_hexes_in_one_undo_step()
    {
        var editor = HexEditor();
        Fill(editor, "starter/grass");
        editor.AddHexRegion("Север");
        Assert.Equal(HexToolMode.Region, editor.HexMode);
        editor.HexRadius = 1;
        Drag(editor, (3, 3), (6, 3));
        var region = editor.HexRegions.Single();
        Assert.True(editor.HexRegionSize(region.Id) >= 7);
        var undoBefore = editor.Session.UndoDepth;
        editor.RemoveHexRegion(region.Id);
        Assert.Empty(editor.HexRegions);
        Assert.DoesNotContain(Hexes(editor).Board.Cells, pair => pair.Value.Region != 0);
        Assert.Equal(undoBefore + 1, editor.Session.UndoDepth);
        editor.Undo();
        Assert.Single(editor.HexRegions);
        Assert.True(editor.HexRegionSize(region.Id) >= 7);
    }

    [Fact]
    public void The_fog_hides_what_was_not_revealed_and_the_brush_reveals()
    {
        var editor = HexEditor();
        Fill(editor, "starter/grass");
        editor.HexFogEnabled = true;
        Assert.True(Hexes(editor).FogEnabled);
        editor.HexMode = HexToolMode.Fog;
        editor.HexRadius = 1;
        Drag(editor, (5, 5));
        Assert.Equal(7, Hexes(editor).Board.Cells.Count(pair => pair.Value.Revealed));
        editor.HexFogReveal = false;
        editor.HexRadius = 0;
        Drag(editor, (5, 5));
        Assert.Equal(6, Hexes(editor).Board.Cells.Count(pair => pair.Value.Revealed));
        editor.RevealAllHexes(true);
        Assert.All(Hexes(editor).Board.Cells, pair => Assert.True(pair.Value.Revealed));
    }

    [Fact]
    public void A_river_and_a_crossing_road_are_paths_and_a_click_removes_one()
    {
        var editor = HexEditor();
        Fill(editor, "starter/grass");
        editor.HexMode = HexToolMode.Path;
        editor.HexPathKind = HexPathKind.River;
        Assert.Equal(HexRouteStyle.Meander, editor.HexRouteStyle);
        Drag(editor, (2, 6), (10, 6));
        editor.HexPathKind = HexPathKind.Road;
        Assert.Equal(HexRouteStyle.Centers, editor.HexRouteStyle);
        Drag(editor, (6, 1), (6, 12));
        Assert.Equal(2, Hexes(editor).Paths.Count);
        var river = Hexes(editor).Paths.Single(p => p.Kind == HexPathKind.River);
        var road = Hexes(editor).Paths.Single(p => p.Kind == HexPathKind.Road);
        Assert.NotEmpty(river.Hexes.Intersect(road.Hexes));
        for (var i = 1; i < road.Hexes.Length; i++) Assert.Equal(1, HexCoord.Distance(road.Hexes[i - 1], road.Hexes[i]));

        editor.HexPathKind = HexPathKind.Border;
        Assert.Equal(HexRouteStyle.Edges, editor.HexRouteStyle);
        Drag(editor, (12, 2), (12, 9));
        Assert.True(Hexes(editor).Paths.Single(p => p.Kind == HexPathKind.Border).Vertices.Length >= 4);

        editor.HexPathErase = true;
        var (x, y) = At(editor, 2, 6);
        editor.BeginHexGesture(x, y);
        Assert.DoesNotContain(Hexes(editor).Paths, p => p.Kind == HexPathKind.River);
    }

    [Fact]
    public void An_auto_road_goes_round_the_sea()
    {
        var editor = HexEditor();
        Fill(editor, "starter/grass");
        editor.HexTileId = "starter/sea";
        editor.HexMode = HexToolMode.Place;
        Drag(editor, (6, 0), (6, 11));
        editor.HexMode = HexToolMode.Path;
        editor.HexPathKind = HexPathKind.Road;
        editor.HexPathAuto = true;
        Drag(editor, (3, 5), (9, 5));
        var road = Hexes(editor).Paths.Single();
        Assert.All(road.Hexes, hex => Assert.NotEqual("starter/sea", Hexes(editor).Board[hex]?.Terrain));
        Assert.Equal(Hex(editor, 9, 5), road.Hexes[^1]);
    }

    [Fact]
    public void A_hex_key_keeps_both_texts_lists_in_the_table_and_exports_the_players_part()
    {
        var editor = HexEditor();
        Fill(editor, "starter/grass");
        editor.HexMode = HexToolMode.Key;
        var (x, y) = At(editor, 3, 4);
        editor.BeginHexGesture(x, y);
        Assert.Equal(Hex(editor, 3, 4), editor.HexKeyHex);
        Assert.Equal("0405", editor.HexKeyNumber);
        editor.SetHexKey(new HexNote("Мельница", HexNoteCategory.Landmark, "Мельник — оборотень.", "Скрипит колесо."));
        Assert.Single(editor.HexKeys);
        var gm = editor.HexKeyMarkdown(playersOnly: false);
        var players = editor.HexKeyMarkdown(playersOnly: true);
        Assert.Contains("оборотень", gm);
        Assert.DoesNotContain("оборотень", players);
        Assert.Contains("Скрипит колесо", players);

        editor.HexFogEnabled = true;
        Assert.DoesNotContain("Мельница", editor.HexKeyMarkdown(playersOnly: true));
        editor.SetHexKey(new HexNote(""));
        Assert.Empty(editor.HexKeys);
    }

    [Fact]
    public void The_ruler_measures_hexes_distance_and_days_and_a_road_is_quicker()
    {
        var editor = HexEditor();
        Fill(editor, "starter/forest");
        editor.SetHexTravel(6, "миль", 24);
        editor.HexCampaignDay = 90; // Лето: R18 отделяет цену профиля от сезонной распутицы.
        editor.HexMode = HexToolMode.Measure;
        Drag(editor, (2, 2), (10, 2));
        var forest = editor.HexRoute!;
        Assert.Equal(8, forest.Steps);
        Assert.Equal(48, forest.Distance, 3);
        Assert.Equal(8 * 2 * 6 / 24.0, forest.Days, 3);
        Assert.NotNull(editor.HexOverlay?.Route);

        editor.HexMode = HexToolMode.Path;
        editor.HexPathKind = HexPathKind.Road;
        Drag(editor, (2, 2), (10, 2));
        editor.HexMode = HexToolMode.Measure;
        Drag(editor, (2, 2), (10, 2));
        Assert.Equal(forest.Days / 2, editor.HexRoute!.Days, 6); // Дорога: лес 2 → 1, а не прежние ×0,5 от равнины.
    }

    [Fact]
    public void A_copy_turned_and_pasted_elsewhere_is_one_step_and_a_move_empties_the_old_place()
    {
        var editor = HexEditor();
        editor.HexTileId = "starter/hills";
        editor.HexRadius = 1;
        Drag(editor, (4, 4));
        editor.HexMode = HexToolMode.Select;
        Drag(editor, (3, 3), (5, 5));
        Assert.Equal(9, editor.HexSelection.Count);
        editor.CopyHexSelection();
        editor.BeginHexPaste();
        Assert.True(editor.HasHexPaste);
        editor.RotateHexBrush(1);
        editor.UpdateHexHover(At(editor, 14, 10).X, At(editor, 14, 10).Y);
        Assert.NotNull(editor.HexOverlay?.Ghost);
        var undo = editor.Session.UndoDepth;
        var (x, y) = At(editor, 14, 10);
        editor.BeginHexGesture(x, y);
        Assert.False(editor.HasHexPaste);
        Assert.Equal(undo + 1, editor.Session.UndoDepth);
        Assert.Equal(14, Hexes(editor).Board.CellCount);

        editor.HexMode = HexToolMode.Select;
        Drag(editor, (3, 3), (5, 5));
        editor.MoveHexSelection();
        var (mx, my) = At(editor, 9, 3);
        editor.UpdateHexHover(mx, my);
        editor.BeginHexGesture(mx, my);
        Assert.Null(Hexes(editor).Board[Hex(editor, 4, 4)]);
        Assert.Equal(14, Hexes(editor).Board.CellCount);

        editor.DeleteHexSelection();
        Assert.Equal(7, Hexes(editor).Board.CellCount);
    }

    [Fact]
    public void Inserted_columns_move_the_hexes_and_grow_the_page()
    {
        var editor = HexEditor();
        editor.HexTileId = "starter/snow";
        Drag(editor, (6, 2));
        var width = editor.Session.Document.Canvas.Width;
        editor.HexMode = HexToolMode.Select;
        Drag(editor, (3, 0));
        editor.InsertHexColumns(2);
        Assert.Equal(22, Hexes(editor).Board.Columns);
        Assert.NotNull(Hexes(editor).Board[Hex(editor, 8, 2)]);
        Assert.True(editor.Session.Document.Canvas.Width > width);
        editor.ResizeHexBoard(12, 10);
        Assert.Equal((12, 10), (Hexes(editor).Board.Columns, Hexes(editor).Board.Rows));
        editor.Undo();
        editor.Undo();
        Assert.Equal(20, Hexes(editor).Board.Columns);
    }

    [Fact]
    public void An_underground_level_has_its_own_hexes_on_the_same_grid()
    {
        var editor = HexEditor();
        editor.HexTileId = "starter/grass";
        Drag(editor, (5, 5));
        Assert.Null(editor.HexLevelShown);
        editor.AddHexLevel();
        Assert.Equal(-1, editor.HexLevel);
        Assert.Equal(-1, editor.HexLevelShown);
        Assert.Equal(0, editor.HexGhostLevel);
        editor.HexTileId = "starter/mountains";
        Drag(editor, (5, 5));
        Assert.Equal("starter/mountains", Hexes(editor, -1).Board[Hex(editor, 5, 5)]!.Terrain);
        Assert.Equal("starter/grass", Hexes(editor, 0).Board[Hex(editor, 5, 5)]!.Terrain);
        Assert.Equal(Hexes(editor, 0).Board.Layout, Hexes(editor, -1).Board.Layout);
        editor.HexLevel = 0;
        Assert.Equal(0, editor.ActiveHexLayer!.Level);
    }

    [Fact]
    public void The_wizard_error_goes_away_once_the_fields_are_right_and_boards_reach_1024()
    {
        var wizard = new NewMapWizardViewModel { Category = MapCategories.Hex.Id, HexColumnsText = "0", HexRowsText = "20", HexSizeText = "24" };
        Assert.False(wizard.Validate());
        Assert.NotEmpty(wizard.ValidationError);
        wizard.HexColumnsText = "1024";
        Assert.Empty(wizard.ValidationError);
        wizard.HexRowsText = "1024";
        Assert.True(wizard.Validate());
        var board = wizard.Create(DateTimeOffset.UnixEpoch)!.Layers.OfType<HexLayer>().Single().Board;
        Assert.Equal((1024, 1024), (board.Columns, board.Rows));
    }

    [Fact]
    public void A_new_hex_layer_stays_under_the_labels_and_the_status_names_the_hex_grid()
    {
        var editor = HexEditor();
        Assert.StartsWith("Сетка гексов", editor.GridSummary);
        editor.AddHexLayer();
        var layers = editor.Session.Document.Layers;
        var labels = layers.ToList().FindIndex(layer => layer is ObjectLayer);
        Assert.All(layers.Select((layer, index) => (layer, index)).Where(pair => pair.layer is HexLayer),
            pair => Assert.True(pair.index < labels, "a hex layer above the labels"));
    }

    [Fact]
    public void Generator_steps_are_one_undo_each_and_the_last_one_can_be_dealt_again()
    {
        var editor = HexEditor(60, 40);
        editor.HexGeneratorSettings = new Map.Core.Editing.HexGeneratorSettings { Seed = 9 };
        var depth = editor.Session.UndoDepth;
        editor.RunHexGeneratorStep(Map.Core.Editing.HexGenerationStep.Land);
        editor.RunHexGeneratorStep(Map.Core.Editing.HexGenerationStep.Relief);
        Assert.Equal(depth + 2, editor.Session.UndoDepth);
        Assert.Equal(60 * 40, Hexes(editor).Board.CellCount);
        Assert.True(editor.HexCanRerollGenerator);
        var before = Hexes(editor).Board.Cells.Where(p => p.Value.Terrain == "starter/mountains").Select(p => p.Key).ToHashSet();
        editor.RerollHexGeneratorStep();
        Assert.Equal(depth + 2, editor.Session.UndoDepth);
        Assert.Equal(10, editor.HexGeneratorSettings.Seed);
        var after = Hexes(editor).Board.Cells.Where(p => p.Value.Terrain == "starter/mountains").Select(p => p.Key).ToHashSet();
        Assert.False(before.SetEquals(after));
        editor.RunHexGeneratorAll();
        Assert.Contains(Hexes(editor).Paths, path => path.Kind == HexPathKind.Road);
        editor.Undo();
        Assert.DoesNotContain(Hexes(editor).Paths, path => path.Kind == HexPathKind.Road);
    }
}
