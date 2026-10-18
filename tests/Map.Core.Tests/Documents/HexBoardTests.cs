using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Documents;

public sealed class HexBoardTests
{
    [Theory]
    [InlineData(HexOrientation.Flat)]
    [InlineData(HexOrientation.Pointy)]
    public void Every_hex_centre_maps_back_to_its_hex_and_offsets_round_trip(HexOrientation orientation)
    {
        var board = HexBoard.Create(orientation, 40, 12, 9);
        foreach (var hex in board.HexesIn(new WorldBounds(0, 0, board.PixelWidth, board.PixelHeight)))
        {
            var (x, y) = board.Layout.Center(hex);
            Assert.Equal(hex, board.Layout.HexAt(x, y));
            Assert.Equal(hex, board.Layout.FromOffset(board.Layout.ToOffset(hex)));
            Assert.True(board.Contains(hex));
        }

        Assert.Equal(12 * 9, board.HexesIn(new WorldBounds(0, 0, board.PixelWidth, board.PixelHeight)).Count());
    }

    [Fact]
    public void The_first_hex_touches_the_canvas_corner_and_the_board_fits_its_canvas()
    {
        var board = HexBoard.Create(HexOrientation.Flat, 50, 10, 8);
        var first = board.Layout.Bounds(board.Layout.FromOffset(new HexOffset(0, 0)));
        Assert.Equal(0, first.Left, 6);
        Assert.Equal(0, first.Top, 6);
        foreach (var hex in board.HexesIn(new WorldBounds(0, 0, 1e6, 1e6)))
        {
            var bounds = board.Layout.Bounds(hex);
            Assert.True(bounds.Right <= board.PixelWidth + 1e-6 && bounds.Bottom <= board.PixelHeight + 1e-6);
        }
    }

    [Fact]
    public void Ranges_rings_lines_and_distance_follow_hex_arithmetic()
    {
        var center = new HexCoord(3, -1);
        Assert.Equal(19, HexCoord.Range(center, 2).Distinct().Count());
        Assert.All(HexCoord.Ring(center, 3), hex => Assert.Equal(3, HexCoord.Distance(center, hex)));
        Assert.Equal(18, HexCoord.Ring(center, 3).Count());
        var line = HexCoord.Line(new HexCoord(0, 0), new HexCoord(4, -2)).ToList();
        Assert.Equal(5, line.Count);
        for (var i = 1; i < line.Count; i++) Assert.Equal(1, HexCoord.Distance(line[i - 1], line[i]));
    }

    [Fact]
    public void An_edit_copies_only_the_chunks_it_touches()
    {
        var board = HexBoard.Create(HexOrientation.Flat, 32, 64, 64);
        var near = board.Layout.FromOffset(new HexOffset(1, 1));
        var far = board.Layout.FromOffset(new HexOffset(40, 40));
        var painted = board.With([new(near, new HexCell("starter/grass")), new(far, new HexCell("starter/sea"))]);
        Assert.Equal(2, painted.CellCount);
        Assert.Equal(2, painted.ChunkCount);
        var farChunk = painted.Chunk(HexBoard.ChunkOf(new HexOffset(40, 40)));

        var repainted = painted.With([new(near, new HexCell("starter/forest"))]);
        Assert.Same(farChunk, repainted.Chunk(HexBoard.ChunkOf(new HexOffset(40, 40))));
        Assert.Equal("starter/forest", repainted[near]!.Terrain);
        Assert.Equal("starter/grass", painted[near]!.Terrain);

        var cleared = repainted.With([new(near, null), new(far, null)]);
        Assert.Equal(0, cleared.CellCount);
        Assert.Equal(0, cleared.ChunkCount);
    }

    [Fact]
    public void Hexes_outside_the_board_are_ignored()
    {
        var board = HexBoard.Create(HexOrientation.Pointy, 32, 4, 4);
        var outside = board.Layout.FromOffset(new HexOffset(10, 0));
        Assert.Same(board, board.With([new(outside, new HexCell("starter/grass"))]));
        Assert.Null(board[outside]);
    }

    [Fact]
    public void Tile_ids_are_library_names()
    {
        Assert.True(HexCell.IsValidTileId("starter/forest"));
        Assert.True(HexCell.IsValidTileId("owner.pack_1/hill-2"));
        Assert.False(HexCell.IsValidTileId("Starter/Forest"));
        Assert.False(HexCell.IsValidTileId("/forest"));
        Assert.False(HexCell.IsValidTileId(""));
        Assert.Throws<ArgumentException>(() => new HexCell("bad id"));
    }
}

public sealed class HexEditingTests
{
    private static readonly HexTileChoice Forest = new("starter/forest", 3);

    private static (MapDocument Document, HexLayer Layer) NewMap()
    {
        var document = HexMapFactory.Create("Гексы", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"),
            HexOrientation.Flat, 48, 20, 15, "#FFF2E9DC");
        return (document, document.Layers.OfType<HexLayer>().Single());
    }

    [Fact]
    public void The_factory_builds_page_hexes_and_labels_in_a_canvas_that_fits_the_board()
    {
        var (document, layer) = NewMap();
        Assert.Equal("hex", document.Category.Value);
        Assert.Collection(document.Layers,
            first => Assert.IsType<BaseColorLayer>(first),
            second => Assert.IsType<HexLayer>(second),
            third => Assert.IsType<ObjectLayer>(third));
        Assert.True(document.Canvas.Width >= layer.Board.PixelWidth - 1);
        Assert.True(document.Canvas.Height >= layer.Board.PixelHeight - 1);
    }

    [Fact]
    public void A_paint_command_is_undone_exactly_and_skips_unchanged_hexes()
    {
        var (document, layer) = NewMap();
        var session = new EditingSession(document);
        var hexes = HexEditing.Footprint(layer.Board, layer.Board.Layout.FromOffset(new HexOffset(5, 5)), 1).ToList();
        Assert.Equal(7, hexes.Count);
        var after = hexes.ToDictionary(hex => hex, hex => HexEditing.Place(null, hex, Forest));
        session.Execute(HexPaintCommand.Create("Лес", layer, after)!);
        var painted = session.Document.Layers.OfType<HexLayer>().Single();
        Assert.Equal(7, painted.Board.CellCount);

        Assert.Null(HexPaintCommand.Create("Лес", painted, after));

        session.Undo();
        Assert.Equal(0, session.Document.Layers.OfType<HexLayer>().Single().Board.CellCount);
        session.Redo();
        Assert.Equal(7, session.Document.Layers.OfType<HexLayer>().Single().Board.CellCount);
    }

    [Fact]
    public void Random_variants_are_stable_per_hex_and_vary_across_the_board()
    {
        var hexes = Enumerable.Range(0, 60).Select(i => new HexCoord(i % 10, i / 10)).ToList();
        var first = hexes.Select(hex => HexEditing.VariantFor(hex, Forest)).ToList();
        Assert.Equal(first, hexes.Select(hex => HexEditing.VariantFor(hex, Forest)));
        Assert.Equal(3, first.Distinct().Count());
        Assert.All(hexes, hex => Assert.Equal(2, HexEditing.VariantFor(hex, Forest with { RandomVariant = false, FixedVariant = 2 })));
    }

    [Fact]
    public void Features_need_terrain_and_terrain_repaint_keeps_the_feature()
    {
        var hex = new HexCoord(1, 1);
        var village = new HexTileChoice("starter/village", 2, HexPaintTarget.Feature);
        Assert.Null(HexEditing.Place(null, hex, village));
        var grass = HexEditing.Place(null, hex, new HexTileChoice("starter/grass", 3));
        var settled = HexEditing.Place(grass, hex, village)!;
        Assert.Equal("starter/village", settled.Feature);
        var forested = HexEditing.Place(settled, hex, Forest)!;
        Assert.Equal("starter/forest", forested.Terrain);
        Assert.Equal("starter/village", forested.Feature);
        Assert.Null(HexEditing.Erase(forested, HexPaintTarget.Feature)!.Feature);
        Assert.Null(HexEditing.Erase(forested, HexPaintTarget.Terrain));
    }

    [Fact]
    public void Flood_fill_stays_inside_the_region_of_one_terrain()
    {
        var board = HexBoard.Create(HexOrientation.Pointy, 32, 10, 10);
        // A wall of sea down column 4 splits the empty board in two.
        var wall = Enumerable.Range(0, 10).Select(row => board.Layout.FromOffset(new HexOffset(4, row)))
            .Select(hex => new KeyValuePair<HexCoord, HexCell?>(hex, new HexCell("starter/sea")));
        board = board.With(wall);
        var left = HexEditing.FloodRegion(board, board.Layout.FromOffset(new HexOffset(0, 0)));
        Assert.All(left, hex => Assert.True(board.Layout.ToOffset(hex).Column < 4));
        Assert.True(left.Count >= 30);
        var sea = HexEditing.FloodRegion(board, board.Layout.FromOffset(new HexOffset(4, 3)));
        Assert.Equal(10, sea.Count);
    }

    [Fact]
    public void Resizing_the_canvas_moves_the_board_with_its_hexes()
    {
        var (document, layer) = NewMap();
        var hex = layer.Board.Layout.FromOffset(new HexOffset(2, 2));
        var painted = layer with { Board = layer.Board.With([new(hex, new HexCell("starter/grass"))]) };
        var moved = painted.Board.Translated(100, 50);
        Assert.Equal(layer.Board.Layout.Center(hex).X + 100, moved.Layout.Center(hex).X, 6);
        Assert.Equal("starter/grass", moved[hex]!.Terrain);
    }

    [Fact]
    public void A_filled_board_shares_one_cell_object_per_distinct_tile()
    {
        var (_, layer) = NewMap();
        var start = layer.Board.Layout.FromOffset(new HexOffset(0, 0));
        var choice = new HexTileChoice("starter/forest", 3);
        var region = HexEditing.FloodRegion(layer.Board, start);
        var filled = layer.Board.With(region.Select(hex => new KeyValuePair<HexCoord, HexCell?>(hex, HexEditing.Place(null, hex, choice))));
        var distinct = filled.Cells.Select(pair => pair.Value).Distinct(ReferenceEqualityComparer.Instance).Count();
        Assert.Equal(region.Count, filled.CellCount);
        Assert.InRange(distinct, 1, 3);
        Assert.Same(HexCell.Of("starter/forest", 1), HexCell.Of("starter/forest", 1));
        Assert.Equal(new HexCell("starter/forest", 1), HexCell.Of("starter/forest", 1));
    }
}
