using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class LandRoundTripTests
{
    [Fact]
    public void Independent_land_layers_styles_and_masks_survive_reopen_without_geometry_change()
    {
        using var workspace = new TempWorkspace();
        var firstId = LayerId.New();
        var secondId = LayerId.New();
        var firstCoverage = new byte[TileMap.TileSize * TileMap.TileSize];
        Array.Fill(firstCoverage, (byte)255);
        var secondCoverage = new byte[TileMap.TileSize * TileMap.TileSize];
        secondCoverage[123] = 255;
        var mask = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        mask[0] = 0;
        var first = new LandLayer(
            firstId, "continent", true, false, 1,
            TileMap.Empty(1).With(new TileKey(2, 3), firstCoverage),
            new LandEdgeStyle { FillColor = "#FF557733", RippleCount = 3, RippleSpacing = 14 },
            new LayerMask(TileMap.Empty(1).With(new TileKey(2, 3), mask), true));
        var second = new LandLayer(
            secondId, "island", true, false, 0.8,
            TileMap.Empty(1).With(new TileKey(7, 8), secondCoverage),
            new LandEdgeStyle { FillColor = "#FFAA7733", RippleCount = 0 });
        var document = new MapDocument(
            DocumentId.New(), "land", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(65_536, 65_536, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                first,
                second,
            ]);
        var firstHash = GeometryHash(first.GeometryTiles);
        var path = workspace.File("land.mapproj");

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path).Document;

        var firstAgain = Assert.IsType<LandLayer>(reopened.FindLayer(firstId));
        var secondAgain = Assert.IsType<LandLayer>(reopened.FindLayer(secondId));
        Assert.Equal(firstHash, GeometryHash(firstAgain.GeometryTiles));
        Assert.Equal(3, firstAgain.EdgeStyle.RippleCount);
        Assert.Equal("#FF557733", firstAgain.EdgeStyle.FillColor);
        Assert.NotNull(firstAgain.Mask);
        Assert.Null(secondAgain.Mask);
        Assert.Equal("#FFAA7733", secondAgain.EdgeStyle.FillColor);
        Assert.Null(secondAgain.GeometryTiles.TryGet(new TileKey(2, 3)));
    }

    private static string GeometryHash(TileMap tiles)
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
