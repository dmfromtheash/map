using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Documents;

public sealed class HexWorkshopCoreTests
{
    private static HexLayer Layer(HexOrientation orientation = HexOrientation.Flat, int columns = 12, int rows = 10) =>
        new(LayerId.New(), "Гексы", true, false, 1, HexBoard.Create(orientation, 40, columns, rows));

    [Fact]
    public void Six_turns_bring_every_hex_back_and_one_turn_is_clockwise_on_screen()
    {
        var center = new HexCoord(3, -1);
        var hex = new HexCoord(5, -2);
        Assert.Equal(hex, hex.RotatedAround(center, 6));
        Assert.Equal(hex.RotatedAround(center, -1), hex.RotatedAround(center, 5));
        Assert.Equal(HexCoord.Distance(center, hex), HexCoord.Distance(center, hex.RotatedAround(center, 2)));

        foreach (var orientation in new[] { HexOrientation.Flat, HexOrientation.Pointy })
        {
            var layout = new HexLayout(orientation, 10, 0, 0);
            var origin = new HexCoord(0, 0);
            for (var direction = 0; direction < 6; direction++)
            {
                var before = layout.DirectionAngle(direction);
                var turned = origin.Neighbor(direction).RotatedAround(origin, 1);
                var after = layout.DirectionAngle(origin.DirectionTo(turned));
                // y points down, so clockwise on screen is +60°.
                Assert.Equal(60, ((after - before) % 360 + 360) % 360, 6);
            }
        }
    }

    [Theory]
    [InlineData(HexOrientation.Flat)]
    [InlineData(HexOrientation.Pointy)]
    public void A_mirror_flips_left_and_right_on_screen_and_twice_is_nothing(HexOrientation orientation)
    {
        var layout = new HexLayout(orientation, 10, 0, 0);
        var center = new HexCoord(1, 1);
        foreach (var hex in HexCoord.Range(center, 3))
        {
            var mirrored = hex.MirroredAround(center, orientation);
            Assert.Equal(hex, mirrored.MirroredAround(center, orientation));
            var (cx, cy) = layout.Center(center);
            var (x, y) = layout.Center(hex);
            var (mx, my) = layout.Center(mirrored);
            Assert.Equal(cx - (x - cx), mx, 6);
            Assert.Equal(y, my, 6);
        }
    }

    [Theory]
    [InlineData(HexOrientation.Flat)]
    [InlineData(HexOrientation.Pointy)]
    public void Every_corner_has_one_stored_form_and_three_neighbours_one_edge_away(HexOrientation orientation)
    {
        var layout = new HexLayout(orientation, 30, 100, 100);
        var hex = new HexCoord(2, 3);
        for (var corner = 0; corner < 6; corner++)
        {
            var canonical = layout.Canonical(new HexVertex(hex, corner));
            Assert.Equal(canonical, layout.Canonical(canonical));
            var (x, y) = layout.Position(canonical);
            var (ex, ey) = layout.Corner(hex, corner);
            Assert.Equal(ex, x, 6);
            Assert.Equal(ey, y, 6);
            var neighbours = layout.VertexNeighbors(canonical);
            Assert.Equal(3, neighbours.Count);
            foreach (var next in neighbours)
            {
                var (nx, ny) = layout.Position(next);
                Assert.Equal(layout.Size, Math.Sqrt((nx - x) * (nx - x) + (ny - y) * (ny - y)), 6);
            }
        }

        // Six corners per hex, every corner shared by three hexes: 7 hexes of a flower have 24 corners.
        var corners = HexCoord.Range(hex, 1)
            .SelectMany(h => Enumerable.Range(0, 6).Select(i => layout.Canonical(new HexVertex(h, i))))
            .ToHashSet();
        Assert.Equal(24, corners.Count);
    }

    [Fact]
    public void An_edge_walk_follows_edges_from_corner_to_corner()
    {
        var layout = new HexLayout(HexOrientation.Flat, 20, 0, 0);
        var from = layout.Canonical(new HexVertex(new HexCoord(0, 0), 0));
        var to = layout.Canonical(new HexVertex(new HexCoord(4, -1), 3));
        var walk = HexPaths.EdgeWalk(layout, from, to);
        Assert.Equal(from, walk[0]);
        Assert.Equal(to, walk[^1]);
        for (var i = 1; i < walk.Count; i++) Assert.Contains(walk[i], layout.VertexNeighbors(walk[i - 1]));
    }

    [Fact]
    public void A_stroke_walk_fills_gaps_and_backing_up_shortens_the_path()
    {
        var walk = HexPaths.Walk([new HexCoord(0, 0), new HexCoord(3, 0), new HexCoord(2, 0)]);
        Assert.Equal([new HexCoord(0, 0), new HexCoord(1, 0), new HexCoord(2, 0)], walk);
        for (var i = 1; i < walk.Count; i++) Assert.Equal(1, HexCoord.Distance(walk[i - 1], walk[i]));
    }

    [Fact]
    public void A_route_avoids_hexes_it_cannot_enter_and_prefers_cheap_ones()
    {
        var wall = Enumerable.Range(-3, 6).Select(r => new HexCoord(2, r)).ToHashSet();
        double? Cost(HexCoord _, HexCoord next) => wall.Contains(next) ? null : 1;
        var route = HexRouting.FindRoute(new HexCoord(0, 0), new HexCoord(4, 0), Cost);
        Assert.NotNull(route);
        Assert.DoesNotContain(route!, wall.Contains);
        Assert.Equal(new HexCoord(4, 0), route![^1]);
        for (var i = 1; i < route.Count; i++) Assert.Equal(1, HexCoord.Distance(route[i - 1], route[i]));

        var road = HexCoord.Line(new HexCoord(0, 0), new HexCoord(6, 0)).ToHashSet();
        double? Road(HexCoord _, HexCoord next) => road.Contains(next) ? 0.5 : 2;
        var fast = HexRouting.FindRoute(new HexCoord(0, 0), new HexCoord(6, 0), Road, minStepCost: 0.5)!;
        Assert.All(fast, hex => Assert.Contains(hex, road));
        Assert.Equal(3, HexRouting.CostOf(fast, Road), 6);
    }

    [Fact]
    public void Inserting_columns_moves_hexes_paths_and_keys_and_deleting_cuts_paths()
    {
        var layer = Layer();
        var layout = layer.Board.Layout;
        HexCoord At(int c, int r) => layout.FromOffset(new HexOffset(c, r));
        var row = Enumerable.Range(0, 8).Select(c => At(c, 2)).ToList();
        var path = HexPaths.Walk(row);
        layer = layer with
        {
            Board = layer.Board.With(row.Select(h => new KeyValuePair<HexCoord, HexCell?>(h, HexCell.Of("starter/grass")))),
            Paths = [new HexPath(HexPathId.New(), HexPathKind.Road, HexRouteStyle.Centers, [.. path], [])],
            Notes = ImmutableDictionary<HexCoord, HexNote>.Empty.Add(At(5, 2), new HexNote("Башня")),
        };

        var wider = HexBoardOps.InsertColumns(layer, 3, 2);
        Assert.Equal(14, wider.Board.Columns);
        Assert.Equal(8, wider.Board.CellCount);
        Assert.NotNull(wider.Board[At(9, 2)]);
        Assert.Null(wider.Board[At(3, 2)]);
        Assert.True(wider.Notes.ContainsKey(At(7, 2)));
        // The road keeps crossing the two new columns: one path, still a run of neighbours.
        var road = Assert.Single(wider.Paths);
        Assert.Equal(layer.Paths[0].Id, road.Id);
        for (var i = 1; i < road.Hexes.Length; i++) Assert.Equal(1, HexCoord.Distance(road.Hexes[i - 1], road.Hexes[i]));
        Assert.Equal(At(9, 2), road.Hexes[^1]);

        var narrower = HexBoardOps.InsertColumns(layer, 2, -3);
        Assert.Equal(9, narrower.Board.Columns);
        Assert.Equal(5, narrower.Board.CellCount);
        Assert.True(narrower.Notes.ContainsKey(At(2, 2)));
        Assert.All(narrower.Paths, p => Assert.True(p.Hexes.Length >= 2));
    }

    [Fact]
    public void A_board_shape_command_resizes_every_layer_on_the_grid_and_the_page_and_undoes_exactly()
    {
        var document = HexMapFactory.Create("Карта", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"),
            HexOrientation.Pointy, 32, 10, 8, "#FFFFFFFF");
        var first = document.Layers.OfType<HexLayer>().Single();
        var below = first with { Board = HexBoard.FromCells(first.Board.Layout, 10, 8, []), Level = -1 };
        below = (HexLayer)below.WithNewIds();
        document = document.WithLayers([.. document.Layers, below]);
        var command = HexBoardShapeCommand.Create("Шире", document, first, layer => HexBoardOps.Resize(layer, 20, 8))!;
        var after = command.Apply(document);
        Assert.All(after.Layers.OfType<HexLayer>(), layer => Assert.Equal(20, layer.Board.Columns));
        Assert.True(after.Canvas.Width > document.Canvas.Width);
        var back = command.Revert(after);
        Assert.Equal(document.Canvas, back.Canvas);
        Assert.All(back.Layers.OfType<HexLayer>(), layer => Assert.Equal(10, layer.Board.Columns));
    }

    [Fact]
    public void A_copy_turned_six_times_is_the_same_copy_and_pastes_with_new_path_ids()
    {
        var layer = Layer(HexOrientation.Pointy);
        var anchor = layer.Board.Layout.FromOffset(new HexOffset(4, 4));
        var hexes = HexCoord.Range(anchor, 1).ToList();
        var road = new HexPath(HexPathId.New(), HexPathKind.River, HexRouteStyle.Meander, [hexes[1], anchor, hexes[4]], []);
        layer = layer with
        {
            Board = layer.Board.With(hexes.Select((h, i) => new KeyValuePair<HexCoord, HexCell?>(h, HexCell.Of("starter/forest", i % 3, rotation: i % 6)))),
            Paths = [road],
        };

        var clip = HexClip.Capture(layer, hexes, anchor);
        Assert.Equal(7, clip.Count);
        Assert.Single(clip.Paths);
        var turned = clip.Rotated(6);
        Assert.Equal(clip.Cells.OrderBy(p => p.Key.Q).ThenBy(p => p.Key.R).ToList(),
            turned.Cells.OrderBy(p => p.Key.Q).ThenBy(p => p.Key.R).ToList());
        var once = clip.Rotated(1);
        Assert.Equal((clip.Cells[new HexCoord(0, 0)]!.Rotation + 1) % 6, once.Cells[new HexCoord(0, 0)]!.Rotation);

        var target = layer.Board.Layout.FromOffset(new HexOffset(9, 7));
        var pasted = once.PasteInto(layer, target);
        Assert.Equal(2, pasted.Paths.Count);
        Assert.NotEqual(road.Id, pasted.Paths[1].Id);
        Assert.All(pasted.Paths[1].Hexes, hex => Assert.Equal(1, HexCoord.Distance(hex, target) <= 1 ? 1 : 0));
    }

    [Fact]
    public void A_weighted_mix_gives_every_tile_its_share_and_stays_the_same_on_repaint()
    {
        var choice = new HexTileChoice("starter/forest", 3)
        {
            Mix = [new HexMixEntry("starter/forest", 3, 3), new HexMixEntry("starter/pines", 3, 1)],
        };
        var counts = new Dictionary<string, int>();
        foreach (var hex in HexCoord.Range(new HexCoord(0, 0), 30))
        {
            var cell = HexEditing.Place(null, hex, choice)!;
            counts[cell.Terrain] = counts.GetValueOrDefault(cell.Terrain) + 1;
            Assert.Equal(cell, HexEditing.Place(null, hex, choice));
        }

        var share = counts["starter/forest"] / (double)(counts["starter/forest"] + counts["starter/pines"]);
        Assert.InRange(share, 0.70, 0.80);
    }

    [Fact]
    public void Repainting_terrain_keeps_region_fog_and_feature_and_rotation_follows_the_choice()
    {
        var cell = HexCell.Of("starter/grass", 0, "starter/village", 1, rotation: 2, region: 3, revealed: true);
        var choice = new HexTileChoice("starter/forest", 3, RandomVariant: false) { Rotation = 4 };
        var after = HexEditing.Place(cell, new HexCoord(1, 1), choice)!;
        Assert.Equal("starter/forest", after.Terrain);
        Assert.Equal(4, after.Rotation);
        Assert.Equal(3, after.Region);
        Assert.True(after.Revealed);
        Assert.Equal("starter/village", after.Feature);

        var tall = choice with { Rotatable = false };
        Assert.Equal(0, HexEditing.Place(null, new HexCoord(1, 1), tall)!.Rotation);

        var region = HexEditing.Place(after, new HexCoord(1, 1), new HexTileChoice("", 1, HexPaintTarget.Region) { Region = 7 })!;
        Assert.Equal(7, region.Region);
        Assert.Null(HexEditing.Place(null, new HexCoord(1, 1), new HexTileChoice("", 1, HexPaintTarget.Region) { Region = 7 }));
        Assert.Equal(0, HexEditing.Erase(region, HexPaintTarget.Region)!.Region);
        Assert.False(HexEditing.Erase(region, HexPaintTarget.Fog)!.Revealed);
        Assert.Same(HexCell.Of("starter/grass", rotation: 1), HexCell.Of("starter/grass", rotation: 7));
    }

    [Fact]
    public void Transition_runs_join_touching_sides_across_side_zero_longest_first()
    {
        Assert.Empty(HexTransitions.Runs(0));
        Assert.Equal([(0, 6)], HexTransitions.Runs(0x3F));
        // Sides 5, 0, 1 touch across the wrap, side 3 stands alone.
        Assert.Equal([(5, 3), (3, 1)], HexTransitions.Runs(0b101011));
        var water = new HashSet<HexCoord> { new(1, 0), new(1, -1) };
        Assert.Equal(0b11, HexTransitions.Mask(new HexCoord(0, 0), water.Contains));
    }

    [Fact]
    public void The_workshop_constructs_mark_a_layer_for_the_new_schema()
    {
        var layer = Layer();
        Assert.False(layer.NeedsWorkshopSchema);
        Assert.True((layer with { FogEnabled = true }).NeedsWorkshopSchema);
        Assert.True((layer with { Regions = [new HexRegion(1, "Север", "#FF2040A0")] }).NeedsWorkshopSchema);
        var turned = layer with { Board = layer.Board.With([new(new HexCoord(1, 1), HexCell.Of("starter/grass", rotation: 1))]) };
        Assert.True(turned.NeedsWorkshopSchema);
        Assert.Throws<ArgumentException>(() => new HexPath(HexPathId.New(), HexPathKind.Road, HexRouteStyle.Centers,
            [new HexCoord(0, 0), new HexCoord(3, 0)], []));
    }
}
