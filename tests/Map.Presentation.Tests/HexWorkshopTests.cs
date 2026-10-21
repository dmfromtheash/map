using Map.Core.Documents;
using Map.Core.Resources;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;

namespace Map.Presentation.Tests;

public sealed class HexWorkshopTests
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

    private static HexLayer Hexes(EditorViewModel editor) => editor.Session.Document.Layers.OfType<HexLayer>().Single();

    private static (double X, double Y) CenterOf(EditorViewModel editor, int column, int row)
    {
        var layout = Hexes(editor).Board.Layout;
        return layout.Center(layout.FromOffset(new HexOffset(column, row)));
    }

    [Fact]
    public void The_wizard_builds_a_hex_map_whose_canvas_fits_the_board()
    {
        var wizard = new NewMapWizardViewModel { Category = MapCategories.Hex.Id, HexColumnsText = "40", HexRowsText = "25", HexSizeText = "64" };
        Assert.True(wizard.IsHexCategory);
        Assert.True(wizard.Validate());
        var document = wizard.Create(DateTimeOffset.UnixEpoch)!;
        var board = document.Layers.OfType<HexLayer>().Single().Board;
        Assert.Equal((40, 25), (board.Columns, board.Rows));
        Assert.Equal(wizard.HexCanvasSize, (document.Canvas.Width, document.Canvas.Height));

        wizard.HexColumnsText = "0";
        Assert.False(wizard.Validate());
        Assert.Contains("Колонки", wizard.ValidationError);
        wizard.HexColumnsText = "40";
        wizard.HexSizeText = "8";
        Assert.False(wizard.Validate());
    }

    [Fact]
    public void A_hex_map_offers_only_the_hex_rail_and_refuses_standard_tools()
    {
        var editor = HexEditor();
        Assert.True(editor.IsHexWorkshop);
        Assert.Equal(
            [EditorTool.Select, EditorTool.Hex, EditorTool.Text, EditorTool.Notes],
            EditorSurfaceLayout.RailGroupsFor(hexWorkshop: true).SelectMany(group => group).Select(entry => entry.Tool));
        editor.ActiveTool = EditorTool.Land;
        Assert.Equal(EditorTool.Hex, editor.ActiveTool);
        editor.ActiveTool = EditorTool.Text;
        Assert.Equal(EditorTool.Text, editor.ActiveTool);
        Assert.DoesNotContain(EditorSurfaceLayout.RailGroupsFor(hexWorkshop: false).SelectMany(group => group),
            entry => entry.Tool == EditorTool.Hex);
    }

    [Fact]
    public void A_drag_lays_a_line_of_hexes_as_one_undo_step()
    {
        var editor = HexEditor();
        editor.HexTileId = HexStarterTiles.Prefix + "forest";
        var (x0, y0) = CenterOf(editor, 2, 5);
        var (x1, y1) = CenterOf(editor, 10, 5);
        var pending = editor.PendingChanges;
        Assert.True(editor.BeginHexGesture(x0, y0));
        editor.ContinueHexGesture((x0 + x1) / 2, (y0 + y1) / 2);
        editor.ContinueHexGesture(x1, y1);
        editor.EndHexGesture();
        // The status bar counts the drag as one unsaved change, not one per pointer move.
        Assert.Equal(pending + 1, editor.PendingChanges);

        var board = Hexes(editor).Board;
        Assert.True(board.CellCount >= 9);
        Assert.All(board.Cells, pair => Assert.Equal("starter/forest", pair.Value.Terrain));

        Assert.True(editor.CanUndo);
        editor.Undo();
        Assert.Equal(0, Hexes(editor).Board.CellCount);
        Assert.False(editor.CanUndo);
        Assert.Equal(pending, editor.PendingChanges);
    }

    [Fact]
    public void Radius_fill_erase_and_pick_work_on_the_board()
    {
        var editor = HexEditor();
        editor.HexTileId = HexStarterTiles.Prefix + "sea";
        editor.HexRadius = 1;
        var (cx, cy) = CenterOf(editor, 8, 7);
        editor.BeginHexGesture(cx, cy);
        editor.EndHexGesture();
        Assert.Equal(7, Hexes(editor).Board.CellCount);

        // Fill the empty rest of the board with grass: the sea patch stays.
        editor.HexRadius = 0;
        editor.HexTileId = HexStarterTiles.Prefix + "grass";
        editor.HexMode = HexToolMode.Fill;
        var (ex, ey) = CenterOf(editor, 0, 0);
        Assert.False(editor.BeginHexGesture(ex, ey));
        var board = Hexes(editor).Board;
        Assert.Equal(20 * 15, board.CellCount);
        Assert.Equal(7, board.Cells.Count(pair => pair.Value.Terrain == "starter/sea"));

        // A village on the sea centre, then erase: the village goes first, the sea second.
        editor.HexMode = HexToolMode.Place;
        editor.HexTileId = HexStarterTiles.Prefix + "village";
        editor.BeginHexGesture(cx, cy);
        editor.EndHexGesture();
        var centre = board.Layout.FromOffset(new HexOffset(8, 7));
        Assert.Equal("starter/village", Hexes(editor).Board[centre]!.Feature);
        editor.HexMode = HexToolMode.Erase;
        editor.BeginHexGesture(cx, cy);
        editor.EndHexGesture();
        Assert.Equal("starter/sea", Hexes(editor).Board[centre]!.Terrain);
        Assert.Null(Hexes(editor).Board[centre]!.Feature);

        editor.HexMode = HexToolMode.Pick;
        editor.BeginHexGesture(ex, ey);
        Assert.Equal("starter/grass", editor.HexTileId);
        Assert.Equal(HexToolMode.Place, editor.HexMode);
        Assert.False(editor.HexRandomVariant);
    }

    [Fact]
    public void The_cursor_outlines_the_footprint_under_the_pointer()
    {
        var editor = HexEditor();
        editor.HexRadius = 2;
        var (cx, cy) = CenterOf(editor, 8, 7);
        Assert.True(editor.UpdateHexHover(cx, cy));
        Assert.Equal(19, editor.HexCursor!.Hexes.Count);
        editor.UpdateHexHover(null, null);
        Assert.Null(editor.HexCursor);
    }

    [Fact]
    public void A_placed_tile_lands_a_fill_runs_as_a_wave_and_the_pipette_answers_with_a_ring()
    {
        var editor = HexEditor();
        editor.HexTileId = HexStarterTiles.Prefix + "sea";
        var tick = System.Diagnostics.Stopwatch.Frequency / 60;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var landed = 0;
        editor.HexTileLanded += _ => landed++;
        var (cx, cy) = CenterOf(editor, 8, 7);
        var centre = Hexes(editor).Board.Layout.FromOffset(new HexOffset(8, 7));
        editor.BeginHexGesture(cx, cy);
        editor.EndHexGesture();
        Assert.True(editor.StepHexHover(now += tick));
        var land = Assert.Single(editor.HexSettlesShown!.Items);
        Assert.Equal((centre, HexSettleKind.Land), (land.Hex, land.Kind));
        Assert.Null(land.From);
        var frames = 0;
        while (editor.StepHexHover(now += tick)) Assert.True(++frames < 60);
        Assert.Equal(1, landed);
        Assert.Null(editor.HexSettlesShown);

        // A fill: hexes far from the pressed one wait for their turn and still show what was there.
        editor.HexTileId = HexStarterTiles.Prefix + "grass";
        editor.HexMode = HexToolMode.Fill;
        var (ex, ey) = CenterOf(editor, 0, 0);
        editor.BeginHexGesture(ex, ey);
        editor.StepHexHover(now += tick);
        var wave = editor.HexSettlesShown!.Items;
        Assert.Contains(wave, item => item.T < 0);
        Assert.Contains(wave, item => item.T >= 0);
        while (editor.StepHexHover(now += tick)) Assert.True(++frames < 200);

        editor.HexMode = HexToolMode.Pick;
        editor.BeginHexGesture(cx, cy);
        editor.StepHexHover(System.Diagnostics.Stopwatch.GetTimestamp() + tick);
        Assert.Equal(HexSettleKind.Touch, Assert.Single(editor.HexSettlesShown!.Items).Kind);
    }

    [Fact]
    public void The_hover_outline_flows_across_a_hex_border_and_fades_out()
    {
        var editor = HexEditor();
        var (ax, ay) = CenterOf(editor, 8, 7);
        var (bx, by) = CenterOf(editor, 9, 7);
        var tick = System.Diagnostics.Stopwatch.Frequency / 60;
        var now = tick;
        Dictionary<string, double> Shown() => editor.HexCursorShown!.Glows!
            .ToDictionary(g => g.Hexes[0].ToString(), g => g.Strength);

        editor.UpdateHexHover(ax, ay);
        for (var i = 0; i < 60; i++) editor.StepHexHover(now += tick);
        var before = Shown();
        // Walk to the neighbour at 60 frames a second: no hex ever switches at once (a switch is a step of 1).
        for (var i = 1; i <= 40; i++)
        {
            editor.UpdateHexHover(ax + (bx - ax) * i / 40, ay + (by - ay) * i / 40);
            editor.StepHexHover(now += tick);
            var shown = Shown();
            foreach (var key in shown.Keys.Union(before.Keys))
                Assert.True(Math.Abs(shown.GetValueOrDefault(key) - before.GetValueOrDefault(key)) < 0.5, $"{key} jumped at step {i}");
            before = shown;
        }

        editor.UpdateHexHover(null, null);
        var frames = 0;
        while (editor.StepHexHover(now += tick)) Assert.True(++frames < 120);
        Assert.Null(editor.HexCursorShown);
    }

    [Fact]
    public void A_hex_layer_added_later_lines_up_with_the_board()
    {
        var editor = HexEditor();
        editor.AddHexLayer();
        var layers = editor.Session.Document.Layers.OfType<HexLayer>().ToList();
        Assert.Equal(2, layers.Count);
        Assert.Equal(layers[0].Board.Layout, layers[1].Board.Layout);
        Assert.Equal(layers[1].Id, editor.ActiveLayerId);
    }
}
