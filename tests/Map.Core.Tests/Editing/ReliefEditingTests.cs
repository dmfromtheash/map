using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>R22: the «Рельеф» brush and a heightmap import change only the heights, one undoable step each.</summary>
public sealed class ReliefEditingTests
{
    private static Dictionary<TileKey, byte[]> Disc(int cx, int cy, int r)
    {
        var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
        for (var y = 0; y < TileMap.TileSize; y++)
            for (var x = 0; x < TileMap.TileSize; x++)
            {
                var d = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                bytes[(y * TileMap.TileSize) + x] = (byte)Math.Clamp((1 - d / r) * 255, 0, 255);
            }
        return new() { [new TileKey(0, 0)] = bytes };
    }

    private static byte At(TileMap map, int x, int y) => ReliefEditor.HeightAt(map, x, y);

    [Fact]
    public void Raise_lower_ridge_smooth_and_erode_change_the_heights_as_named()
    {
        var empty = TileMap.Empty(1);
        var (_, raisedTiles) = ReliefEditor.Apply(empty, Disc(128, 128, 40), ReliefAction.Raise, 1, 1);
        var raised = empty.WithMany(raisedTiles);
        Assert.True(At(raised, 128, 128) >= 40, $"centre {At(raised, 128, 128)}");
        Assert.True(At(raised, 128, 128) > At(raised, 150, 128));
        Assert.Equal(0, At(raised, 200, 200));

        var (_, ridgeTiles) = ReliefEditor.Apply(empty, Disc(128, 128, 40), ReliefAction.Ridge, 1, 1);
        var ridge = empty.WithMany(ridgeTiles);
        // A crest: high in the middle, falling off faster than a plain raise.
        Assert.True(At(ridge, 128, 128) > At(raised, 128, 128));
        Assert.True(At(ridge, 150, 128) < At(raised, 150, 128));

        var (_, lowerTiles) = ReliefEditor.Apply(raised, Disc(128, 128, 40), ReliefAction.Lower, 1, 1);
        Assert.True(At(raised.WithMany(lowerTiles), 128, 128) < At(raised, 128, 128));

        // A step smooths towards its neighbours.
        var step = new byte[TileMap.TileSize * TileMap.TileSize];
        for (var y = 0; y < TileMap.TileSize; y++) for (var x = 128; x < TileMap.TileSize; x++) step[(y * TileMap.TileSize) + x] = 200;
        var cliff = empty.With(new TileKey(0, 0), step);
        var (_, smoothTiles) = ReliefEditor.Apply(cliff, Disc(128, 128, 30), ReliefAction.Smooth, 1, 1);
        var smooth = cliff.WithMany(smoothTiles);
        Assert.InRange(At(smooth, 127, 128), 20, 180);
        Assert.InRange(At(smooth, 128, 128), 20, 199);

        var (_, erodedTiles) = ReliefEditor.Apply(raised, Disc(128, 128, 40), ReliefAction.Erode, 1, 3);
        var eroded = raised.WithMany(erodedTiles);
        var lowered = 0;
        for (var x = 100; x < 156; x++) if (At(eroded, x, 128) < At(raised, x, 128)) lowered++;
        Assert.True(lowered > 5, $"erosion lowered {lowered} probes");
    }

    [Fact]
    public void A_heightmap_is_stretched_over_the_canvas_and_the_stroke_is_one_undo_step()
    {
        var grey = new byte[] { 0, 64, 128, 255 };
        var (before, after) = ReliefEditor.Import(TileMap.Empty(1), grey, 2, 2, 512, 512);
        var map = TileMap.Empty(1).WithMany(after);
        Assert.Equal(0, At(map, 10, 10));
        Assert.Equal(64, At(map, 400, 10));
        Assert.Equal(128, At(map, 10, 400));
        Assert.Equal(255, At(map, 400, 400));
        Assert.All(before.Values, Assert.Null);

        var land = LandLayer.CreateEmpty(LayerId.New(), "Земля");
        var session = new EditingSession(new MapDocument(DocumentId.New(), "Рельеф", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 512, "px", "#FFFFFFFF"), [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), land]));
        session.Execute(new LandReliefCommand(land.Id, before, after, "Карта высот"));
        // The top-left quarter of the picture is black: that tile stays empty.
        Assert.Equal(3, ((LandLayer)session.Document.FindLayer(land.Id)!).ReliefTiles.TileCount);
        Assert.True(session.Undo());
        Assert.Equal(0, ((LandLayer)session.Document.FindLayer(land.Id)!).ReliefTiles.TileCount);
    }
}
