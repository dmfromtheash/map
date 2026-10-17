using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class LandMaterialTests
{
    [Fact]
    public void Paint_undo_does_not_change_coverage_and_resize_moves_both_surfaces()
    {
        var land = LandLayer.CreateEmpty(LayerId.New(), "Земля") with
        { GeometryTiles = TileMap.Empty(1).With(new TileKey(0, 0), Enumerable.Repeat((byte)255, 65536).ToArray()) };
        var doc = new MapDocument(DocumentId.New(), "Paint", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(256, 256, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), land]);
        var stroke = new RasterStrokeSession(land.PaintTiles, new BrushSettings { Radius = 12, Softness = 0, Color = "#FFC08040" });
        stroke.AddPoint(40, 40); var (before, after) = stroke.Commit();
        var command = new LandPaintCommand(land.Id, before, after, "Paint");
        var painted = command.Apply(doc); var result = (LandLayer)painted.FindLayer(land.Id)!;
        Assert.Same(land.GeometryTiles, result.GeometryTiles);
        Assert.Equal(255, result.PaintTiles.TryGet(new(0, 0))![(40 * 256 + 40) * 4 + 3]);
        Assert.Equal(0, ((LandLayer)command.Revert(painted).FindLayer(land.Id)!).PaintTiles.TileCount);
        var plan = CanvasTransformService.PlanResize(painted, 512, 512, CanvasAnchor.BottomRight);
        var resized = CanvasTransformService.ApplyResize(painted, plan);
        var moved = (LandLayer)resized.FindLayer(land.Id)!;
        Assert.NotNull(moved.PaintTiles.TryGet(new(1, 1))); Assert.NotNull(moved.GeometryTiles.TryGet(new(1, 1)));
    }

    [Fact]
    public void Moving_a_painted_region_keeps_RGBA_and_clears_its_source_even_when_regions_overlap()
    {
        var pixels = new byte[256 * 256 * 4];
        for (var x = 30; x < 60; x++)
        { var offset = (40 * 256 + x) * 4; pixels[offset] = 90; pixels[offset + 1] = 60; pixels[offset + 2] = 30; pixels[offset + 3] = 128; }
        var source = TileMap.Empty(4).With(new(0, 0), pixels);
        var (before, after) = LandRasterizer.MoveRegion(source, new(20, 30, 70, 50), 10, 0);
        var moved = source.WithMany(after).TryGet(new(0, 0))!;
        Assert.Equal(0, moved[(40 * 256 + 35) * 4 + 3]);
        Assert.Equal(new byte[] { 90, 60, 30, 128 }, moved.AsSpan((40 * 256 + 65) * 4, 4).ToArray());
        Assert.Same(pixels, before[new(0, 0)]);
    }
}
