using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class Stage10EGridGeometryTests
{
    [Fact]
    public void Model_exposes_exactly_four_distinct_grid_geometries()
    {
        Assert.Equal(
            ["Square", "IsometricSquares", "HexFlat", "HexPointy"],
            Enum.GetNames<GridGeometry>());
    }

    [Fact]
    public void Isometric_basis_has_two_thirty_degree_families_and_is_not_square_plus_45_degrees()
    {
        var isometric = Grid(GridGeometry.IsometricSquares);
        var square = Grid(GridGeometry.Square) with { RotationDegrees = 45 };
        var basis = GridGeometryContract.LocalBasis(isometric);

        Assert.Equal(Math.Tan(Math.PI / 6), basis.U.Y / basis.U.X, 12);
        Assert.Equal(-Math.Tan(Math.PI / 6), basis.V.Y / basis.V.X, 12);

        var query = new VectorPoint(80, 20);
        var isometricSnap = GridSnapService.Snap(isometric, query);
        var rotatedSquareSnap = GridSnapService.Snap(square, query);
        Assert.NotEqual(isometricSnap, rotatedSquareSnap);
    }

    [Fact]
    public void Isometric_snap_uses_known_intersections_offset_rotation_and_subdivisions()
    {
        var grid = Grid(GridGeometry.IsometricSquares) with
        {
            CellSize = 120,
            Subdivisions = 3,
            OffsetX = 13,
            OffsetY = -7,
            RotationDegrees = 17,
        };
        var intersection = GridGeometryContract.LatticePoint(grid, 2, -3);

        var snapped = GridSnapService.Snap(grid, intersection);

        AssertPoint(intersection, snapped);
        var basis = GridGeometryContract.LocalBasis(grid);
        Assert.Equal(40, Length(basis.U), 10);
        Assert.Equal(40, Length(basis.V), 10);
    }

    [Fact]
    public void Isometric_snap_matches_a_small_brute_force_oracle_for_negative_coordinates()
    {
        var grid = Grid(GridGeometry.IsometricSquares) with { CellSize = 73, Subdivisions = 2 };
        var query = new VectorPoint(-137.25, -84.75);
        var expected = Enumerable.Range(-12, 25)
            .SelectMany(u => Enumerable.Range(-12, 25)
                .Select(v => GridGeometryContract.LatticePoint(grid, u, v)))
            .OrderBy(point => DistanceSquared(point, query))
            .ThenBy(point => point.X)
            .ThenBy(point => point.Y)
            .First();

        AssertPoint(expected, GridSnapService.Snap(grid, query));
    }

    [Fact]
    public void Equidistant_isometric_point_uses_the_lower_local_coordinate_tie_break()
    {
        var grid = Grid(GridGeometry.IsometricSquares);
        var lower = GridGeometryContract.LatticePoint(grid, 0, 0);
        var upper = GridGeometryContract.LatticePoint(grid, 1, 0);
        var middle = new VectorPoint((lower.X + upper.X) / 2, (lower.Y + upper.Y) / 2);

        AssertPoint(lower, GridSnapService.Snap(grid, middle));
    }

    [Fact]
    public void Snap_normalizes_a_very_large_rotation_deterministically()
    {
        var ordinary = Grid(GridGeometry.IsometricSquares) with { RotationDegrees = 17 };
        var large = ordinary with { RotationDegrees = 360_000_000_000d + 17 };
        var query = new VectorPoint(-321.5, 882.25);

        AssertPoint(GridSnapService.Snap(ordinary, query), GridSnapService.Snap(large, query));
    }

    [Fact]
    public void Every_geometry_snaps_the_same_world_point_independently_of_zoom()
    {
        foreach (var geometry in Enum.GetValues<GridGeometry>())
        {
            var grid = Grid(geometry) with
            {
                Subdivisions = 2,
                OffsetX = 13,
                OffsetY = -7,
                RotationDegrees = 17,
            };
            var expected = GridSnapService.Snap(grid, new VectorPoint(431.25, 278.75));
            foreach (var zoom in new[] { 0.005, 1.0, 64.0 })
            {
                var viewport = new Viewport();
                viewport.Resize(1000, 700);
                viewport.Set(-200, 300, zoom);
                var (screenX, screenY) = viewport.WorldToScreen(431.25, 278.75);
                var (worldX, worldY) = viewport.ScreenToWorld(screenX, screenY);
                AssertPoint(expected, GridSnapService.Snap(grid, new VectorPoint(worldX, worldY)));
            }
        }
    }

    [Fact]
    public void Hex_snap_targets_rendered_vertices_and_not_hex_centres()
    {
        var flat = Grid(GridGeometry.HexFlat);
        var pointy = Grid(GridGeometry.HexPointy);

        AssertPoint(new VectorPoint(flat.CellSize, 0),
            GridSnapService.Snap(flat, new VectorPoint(flat.CellSize, 0)));
        AssertPoint(new VectorPoint(0, -pointy.CellSize),
            GridSnapService.Snap(pointy, new VectorPoint(0, -pointy.CellSize)));

        Assert.NotEqual(new VectorPoint(0, 0),
            GridSnapService.Snap(flat, new VectorPoint(0, 0)));
        Assert.NotEqual(new VectorPoint(0, 0),
            GridSnapService.Snap(pointy, new VectorPoint(0, 0)));
    }

    [Fact]
    public void Hex_visible_segments_form_closed_equal_edge_cells_with_the_declared_orientation()
    {
        foreach (var geometry in new[] { GridGeometry.HexFlat, GridGeometry.HexPointy })
        {
            var grid = Grid(geometry);
            var firstCell = GridGeometryContract.VisibleSegments(
                grid, new WorldBounds(-20, -20, 20, 20), 6);

            Assert.Equal(6, firstCell.Count);
            for (var index = 0; index < firstCell.Count; index++)
            {
                AssertPoint(firstCell[index].End, firstCell[(index + 1) % firstCell.Count].Start);
                Assert.Equal(grid.CellSize,
                    Math.Sqrt(DistanceSquared(firstCell[index].Start, firstCell[index].End)), 8);
            }

            var width = firstCell.SelectMany(segment => new[] { segment.Start.X, segment.End.X }).Max()
                - firstCell.SelectMany(segment => new[] { segment.Start.X, segment.End.X }).Min();
            var height = firstCell.SelectMany(segment => new[] { segment.Start.Y, segment.End.Y }).Max()
                - firstCell.SelectMany(segment => new[] { segment.Start.Y, segment.End.Y }).Min();
            Assert.Equal(geometry == GridGeometry.HexFlat, width > height);
        }
    }

    [Fact]
    public void Object_node_and_portal_snap_to_nodes_and_preserve_every_id_for_all_geometries()
    {
        foreach (var geometry in Enum.GetValues<GridGeometry>())
        {
            var grid = Grid(geometry);
            var unrotated = VectorGeometry.CreateLine(ObjectId.New(), "road",
                [new VectorPoint(21, 19), new VectorPoint(74, 181), new VectorPoint(184, 91)]);
            var line = new SceneObject(
                unrotated.Id, unrotated.Kind, unrotated.Name,
                unrotated.X, unrotated.Y, unrotated.Width, unrotated.Height, 23,
                unrotated.FillColor, unrotated.StrokeColor, unrotated.StrokeWidth,
                unrotated.Visible, unrotated.Locked)
            { Line = unrotated.Line };
            line = VectorObjectEditor.AddPortal(line, 0.43, 12);
            var nodeId = line.Line!.Nodes[0].Id;
            var portalId = line.Line.Portals[0].Id;
            var expectedObjectCentre = GridSnapService.Snap(grid,
                new VectorPoint(line.X + line.Width / 2, line.Y + line.Height / 2));
            var expectedNode = GridSnapService.Snap(grid, NodePoint(line, line.Line.Nodes[0]));
            var expectedPortal = GridSnapService.Snap(grid, PortalPoint(line, portalId));

            var objectSnap = GridSnapService.SnapObject(line, grid);
            var nodeSnap = GridSnapService.SnapNode(line, nodeId, grid);
            var portalSnap = GridSnapService.SnapPortal(line, portalId, grid);

            Assert.Equal(line.Id, objectSnap.Id);
            Assert.Equal(line.Id, nodeSnap.Id);
            Assert.Equal(line.Id, portalSnap.Id);
            Assert.Equal(line.Line.Nodes.Select(node => node.Id), nodeSnap.Line!.Nodes.Select(node => node.Id));
            Assert.Equal(line.Line.Portals.Select(portal => portal.Id), portalSnap.Line!.Portals.Select(portal => portal.Id));
            AssertPoint(expectedObjectCentre, new VectorPoint(
                objectSnap.X + objectSnap.Width / 2,
                objectSnap.Y + objectSnap.Height / 2));
            var movedNode = nodeSnap.Line.Nodes.Single(node => node.Id == nodeId);
            AssertPoint(expectedNode, NodePoint(nodeSnap, movedNode));
            AssertPoint(expectedPortal, PortalPoint(portalSnap, portalId));
        }
    }

    [Fact]
    public void Visible_geometry_is_bounded_even_for_tiny_cells_and_huge_views()
    {
        foreach (var geometry in Enum.GetValues<GridGeometry>())
        {
            var grid = Grid(geometry) with { CellSize = 0.000001, Subdivisions = 64 };
            var segments = GridGeometryContract.VisibleSegments(
                grid, new WorldBounds(-1_000_000_000, -1_000_000_000, 1_000_000_000, 1_000_000_000), 96);

            Assert.NotEmpty(segments);
            Assert.InRange(segments.Count, 1, 96);
            Assert.All(segments, segment =>
            {
                Assert.True(double.IsFinite(segment.Start.X));
                Assert.True(double.IsFinite(segment.Start.Y));
                Assert.True(double.IsFinite(segment.End.X));
                Assert.True(double.IsFinite(segment.End.Y));
            });
        }
    }

    [Fact]
    public void Grid_values_reject_non_finite_out_of_range_unknown_and_corrupt_inputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GridDefinition(GridId.New(), GridGeometry.Square, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GridDefinition(GridId.New(), GridGeometry.Square, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GridDefinition(GridId.New(), (GridGeometry)999, 10));

        var valid = Grid(GridGeometry.Square);
        Assert.Throws<ArgumentOutOfRangeException>(() => valid with { Subdivisions = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => valid with { Subdivisions = GridDefinition.MaximumSubdivisions + 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => valid with { LineWidth = double.PositiveInfinity });
        Assert.Throws<ArgumentOutOfRangeException>(() => valid with { LineWidth = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => valid with { Opacity = 1.01 });
        Assert.Throws<ArgumentOutOfRangeException>(() => valid with { OffsetX = double.NegativeInfinity });
        Assert.Throws<ArgumentOutOfRangeException>(() => valid with { RotationDegrees = double.NaN });
        Assert.Throws<ArgumentException>(() => valid with { Color = "" });
        Assert.Throws<ArgumentException>(() => valid with { Color = "#GG000000" });
        Assert.Throws<ArgumentOutOfRangeException>(() => GridGeometryContract.Snap(valid, new VectorPoint(double.NaN, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => GridGeometryContract.Snap(valid, new VectorPoint(0, double.PositiveInfinity)));
        Assert.Throws<ArgumentOutOfRangeException>(() => GridGeometryContract.VisibleSegments(
            valid, new WorldBounds(0, 0, 1, 1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GridGeometryContract.VisibleSegments(
            valid, new WorldBounds(double.NaN, 0, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => GridGeometryContract.VisibleSegments(
            valid, new WorldBounds(2, 0, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => GridGeometryContract.VisibleSegments(
            valid, new WorldBounds(0, 2, 1, 1)));
    }

    private static GridDefinition Grid(GridGeometry geometry) =>
        new(GridId.New(), geometry, 100) { Color = "#FF405060", Opacity = 0.8 };

    private static VectorPoint NodePoint(SceneObject owner, VectorNode node) => RotateOwner(owner,
        new VectorPoint(owner.X + node.X * owner.Width, owner.Y + node.Y * owner.Height));

    private static VectorPoint PortalPoint(SceneObject line, PortalId portalId)
    {
        var points = VectorGeometry.Flatten(line, line.Line!.Nodes, line.Line.Closed);
        var portal = line.Line.Portals.Single(item => item.Id == portalId);
        var segmentLengths = points.Zip(points.Skip(1))
            .Select(pair => Math.Sqrt(DistanceSquared(pair.First, pair.Second))).ToArray();
        var target = portal.Position * segmentLengths.Sum();
        var cursor = 0.0;
        for (var index = 0; index < segmentLengths.Length; index++)
        {
            if (target <= cursor + segmentLengths[index])
            {
                var amount = (target - cursor) / segmentLengths[index];
                return RotateOwner(line, new VectorPoint(
                    points[index].X + (points[index + 1].X - points[index].X) * amount,
                    points[index].Y + (points[index + 1].Y - points[index].Y) * amount));
            }
            cursor += segmentLengths[index];
        }
        return RotateOwner(line, points[^1]);
    }

    private static VectorPoint RotateOwner(SceneObject owner, VectorPoint point)
    {
        var centreX = owner.X + owner.Width / 2;
        var centreY = owner.Y + owner.Height / 2;
        var radians = owner.RotationDegrees * Math.PI / 180;
        var x = point.X - centreX;
        var y = point.Y - centreY;
        return new VectorPoint(
            centreX + x * Math.Cos(radians) - y * Math.Sin(radians),
            centreY + x * Math.Sin(radians) + y * Math.Cos(radians));
    }

    private static double Length(VectorPoint point) => Math.Sqrt(point.X * point.X + point.Y * point.Y);
    private static double DistanceSquared(VectorPoint left, VectorPoint right) =>
        Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2);

    private static void AssertPoint(VectorPoint expected, VectorPoint actual)
    {
        Assert.Equal(expected.X, actual.X, 8);
        Assert.Equal(expected.Y, actual.Y, 8);
    }
}
