using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// Owner, 2026-11-10: the print and painter styles must show the cliff walls with their scree and the roads (they paint
/// the land from its outline and the mountains from their pictures, and had lost both).
/// </summary>
public sealed class StyleOverlayTests
{
    private const int Size = 512;

    private static LandLayer Land(int level, int left, int top, int right, int bottom)
    {
        var tiles = TileMap.Empty(1);
        for (var ty = 0; ty * TileMap.TileSize < Size; ty++)
            for (var tx = 0; tx * TileMap.TileSize < Size; tx++)
            {
                var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
                var any = false;
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                    {
                        int wx = tx * TileMap.TileSize + x, wy = ty * TileMap.TileSize + y;
                        if (wx < left || wx >= right || wy < top || wy >= bottom) continue;
                        bytes[y * TileMap.TileSize + x] = 255; any = true;
                    }
                if (any) tiles = tiles.With(new TileKey(tx, ty), bytes);
            }
        return new LandLayer(LayerId.New(), "Суша", true, false, 1, tiles, LandEdgeStyle.SoftWatercolor()) { Level = level };
    }

    private static SKBitmap Render(MapStylePreset preset, int plateauLevel, bool road)
    {
        var ground = Land(0, 20, 20, 492, 492);
        var plateau = Land(plateauLevel, 120, 80, 400, 260) with
        {
            EdgeStyle = LandEdgeStyle.SoftWatercolor() with { Cliff = LandCliffStyle.Looks.First(l => l.Id == "atlas").Style with { Scree = CliffScree.TwoBenches } },
        };
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])
        {
            Roads = road ? [RoadEditor.NewRoad([new(60, 420), new(250, 380), new(450, 430)], 12, RoadStyle.Painted(), "Дорога", seed: 3)] : [],
        };
        var document = new MapDocument(DocumentId.New(), "Стиль", DateTimeOffset.UnixEpoch, new CanvasDefinition(Size, Size, "px", "#FF9DB7C2"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF9DB7C2"), ground, plateau, objects])
        { Style = MapStyle.For(preset) with { Enabled = true } };
        var bitmap = new SKBitmap(Size, Size);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        SceneRenderer.Render(canvas, document, new RenderOptions { IsExport = true, VisibleWorld = new WorldBounds(0, 0, Size, Size), ViewScale = 1 });
        return bitmap;
    }

    private static int Differing(SKBitmap a, SKBitmap b, SKRectI area)
    {
        var count = 0;
        for (var y = area.Top; y < area.Bottom; y++)
            for (var x = area.Left; x < area.Right; x++)
            {
                SKColor p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                if (Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue) > 24) count++;
            }
        return count;
    }

    [Theory]
    [InlineData(MapStylePreset.WatercolorBigShapes)]
    [InlineData(MapStylePreset.OldMapBlaeu)]
    [InlineData(MapStylePreset.CharcoalVineStump)]
    public void A_raised_land_shows_its_wall_under_the_style(MapStylePreset preset)
    {
        using var flat = Render(preset, 0, road: false);
        using var raised = Render(preset, 1, road: false);
        // Under the plateau's southern edge, where its wall and scree hang.
        Assert.True(Differing(flat, raised, new SKRectI(130, 262, 390, 320)) > 1500);
    }

    [Fact]
    public void A_painter_style_shows_the_road()
    {
        using var without = Render(MapStylePreset.WatercolorBigShapes, 0, road: false);
        using var with = Render(MapStylePreset.WatercolorBigShapes, 0, road: true);
        Assert.True(Differing(without, with, new SKRectI(60, 370, 450, 440)) > 1500);
    }
}
