using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class Stage5LandTests
{
    [Theory]
    [InlineData(LandShapeKind.Circle)]
    [InlineData(LandShapeKind.Rectangle)]
    [InlineData(LandShapeKind.Ellipse)]
    [InlineData(LandShapeKind.Polygon)]
    [InlineData(LandShapeKind.GridBlock)]
    [InlineData(LandShapeKind.BoundedFill)]
    public void Every_land_primitive_produces_bounded_tile_deltas(LandShapeKind kind)
    {
        var bounds = new WorldBounds(220, 220, 620, 520);
        var shape = new LandShape
        {
            Kind = kind,
            Bounds = bounds,
            Points =
            [
                new LandPoint(220, 520),
                new LandPoint(420, 220),
                new LandPoint(620, 520),
            ],
            GridSize = 64,
        };

        var (Before, After) = LandRasterizer.ApplyShape(TileMap.Empty(1), shape, subtract: false);

        Assert.NotEmpty(Before);
        Assert.Equal(
            Before.Keys.OrderBy(key => key.Y).ThenBy(key => key.X),
            After.Keys.OrderBy(key => key.Y).ThenBy(key => key.X));
        Assert.InRange(Before.Count, 1, 12);
        Assert.All(After.Values, tile => Assert.NotNull(tile));
    }

    [Fact]
    public void Add_subtract_move_and_style_are_reversible_without_baking_edge_effects()
    {
        var id = LayerId.New();
        var layer = LandLayer.CreateEmpty(id, "land");
        var session = new EditingSession(new MapDocument(
            DocumentId.New(), "land", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(65_536, 65_536, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                layer,
            ]));
        var add = LandRasterizer.ApplyShape(layer.GeometryTiles, new LandShape
        {
            Kind = LandShapeKind.Rectangle,
            Bounds = new WorldBounds(100, 100, 700, 700),
        }, subtract: false);
        session.Execute(new BrushStrokeCommand(id, add.Before, add.After, "add land"));
        var afterAdd = ((LandLayer)session.Document.FindLayer(id)!).GeometryTiles;

        var subtract = LandRasterizer.ApplyShape(afterAdd, new LandShape
        {
            Kind = LandShapeKind.Circle,
            Bounds = new WorldBounds(250, 250, 550, 550),
        }, subtract: true);
        session.Execute(new BrushStrokeCommand(id, subtract.Before, subtract.After, "subtract land"));
        var afterSubtract = ((LandLayer)session.Document.FindLayer(id)!).GeometryTiles;
        var move = LandRasterizer.MoveRegion(
            afterSubtract, new WorldBounds(100, 100, 700, 700), 256, 0);
        session.Execute(new BrushStrokeCommand(id, move.Before, move.After, "move land"));
        var moved = (LandLayer)session.Document.FindLayer(id)!;
        var geometryHash = Hash(moved.GeometryTiles);

        session.Execute(new UpdateLandStyleCommand(moved, moved.EdgeStyle with
        {
            RippleCount = 4,
            RippleSpacing = 14,
            FillColor = "#FFAA7744",
        }));

        Assert.Equal(geometryHash, Hash(((LandLayer)session.Document.FindLayer(id)!).GeometryTiles));
        Assert.Equal(4, ((LandLayer)session.Document.FindLayer(id)!).EdgeStyle.RippleCount);
        Assert.True(session.Undo());
        Assert.Equal(geometryHash, Hash(((LandLayer)session.Document.FindLayer(id)!).GeometryTiles));
        Assert.True(session.Undo());
        Assert.Equal(Hash(afterSubtract), Hash(((LandLayer)session.Document.FindLayer(id)!).GeometryTiles));
    }

    [Fact]
    public void Grid_block_expands_the_requested_bounds_to_grid_lines()
    {
        var result = LandRasterizer.ApplyShape(TileMap.Empty(1), new LandShape
        {
            Kind = LandShapeKind.GridBlock,
            Bounds = new WorldBounds(220, 220, 620, 520),
            GridSize = 64,
        }, subtract: false);

        var firstTile = result.After[new TileKey(0, 0)];
        Assert.NotNull(firstTile);
        Assert.Equal(255, firstTile[(193 * TileMap.TileSize) + 193]);
    }

    private static string Hash(TileMap tiles)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var key in tiles.Keys.OrderBy(key => key.Y).ThenBy(key => key.X))
        {
            hash.AppendData(BitConverter.GetBytes(key.X));
            hash.AppendData(BitConverter.GetBytes(key.Y));
            hash.AppendData(tiles.TryGet(key)!);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
