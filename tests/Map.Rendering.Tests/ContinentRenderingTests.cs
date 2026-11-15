using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>R23: a generated continent draws; MAP_CONTINENT_PNG writes it for review.</summary>
public sealed class ContinentRenderingTests
{
    [Fact]
    public void Small_continent_steps_change_the_map_and_draw_reproducibly()
    {
        var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
        for (var y = 0; y < 256; y++)
        for (var x = 0; x < 256; x++)
            if ((x - 128) * (x - 128) + (y - 128) * (y - 128) < 110 * 110) bytes[y * 256 + x] = 255;
        var land = new LandLayer(LayerId.New(), "land", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), bytes), LandEdgeStyle.SoftWatercolor());
        var original = new MapDocument(DocumentId.New(), "small continent", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(256, 256, "px", "#FF8FB0BA"),
            [new BaseColorLayer(LayerId.New(), "sea", true, false, 1, "#FF8FB0BA"), land,
             new ObjectLayer(LayerId.New(), "objects", true, false, 1, [])]);
        var current = original;
        var settings = new ContinentSettings { Seed = 17, RiversPerMillion = 100, TownsPerMillion = 100 };
        foreach (var step in Enum.GetValues<ContinentStep>())
            current = ContinentGenerator.Run(current, land.Id, step, settings);
        var generated = (LandLayer)current.FindLayer(land.Id)!;
        Assert.True(generated.ReliefTiles.TileCount > 0);
        Assert.True(generated.PaintTiles.TileCount > 0);
        Assert.Same(land.GeometryTiles, generated.GeometryTiles);
        Assert.Equal(0, land.ReliefTiles.TileCount);
        using var a = Draw(current);
        using var b = Draw(current);
        using var before = Draw(original);
        Assert.Equal(a.Bytes, b.Bytes);
        Assert.False(a.GetPixelSpan().SequenceEqual(before.GetPixelSpan()), "generated continent never reached the renderer");

        static SKBitmap Draw(MapDocument document)
        {
            using var surface = SKSurface.Create(new SKImageInfo(256, 256))!;
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true });
            using var image = surface.Snapshot();
            return SKBitmap.FromImage(image);
        }
    }

    [OptInProbe("MAP_CONTINENT_PNG", "ContinentRenderingTests.Small_continent_steps_change_the_map_and_draw_reproducibly")]
    public void Continent_sheet_for_review()
    {
        var folder = Environment.GetEnvironmentVariable("MAP_CONTINENT_PNG")!;
        Directory.CreateDirectory(folder);
        var tiles = TileMap.Empty(1);
        for (var ty = 0; ty < 3; ty++)
            for (var tx = 0; tx < 4; tx++)
            {
                var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
                var any = false;
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                    {
                        var wx = (tx * TileMap.TileSize) + x; var wy = (ty * TileMap.TileSize) + y;
                        var angle = Math.Atan2(wy - 384, wx - 512);
                        var wobble = 1 + (0.07 * Math.Sin(angle * 5)) + (0.04 * Math.Sin((angle * 13) + 1.1));
                        var d = Math.Sqrt(Math.Pow((wx - 512) / 440.0, 2) + Math.Pow((wy - 384) / 300.0, 2)) / wobble;
                        var v = (byte)Math.Clamp((1 - d) * 255 * 40, 0, 255);
                        bytes[(y * TileMap.TileSize) + x] = v; any |= v > 0;
                    }
                if (any) tiles = tiles.With(new TileKey(tx, ty), bytes);
            }
        var land = new LandLayer(LayerId.New(), "Материк", true, false, 1, tiles, LandEdgeStyle.SoftWatercolor());
        var document = new MapDocument(DocumentId.New(), "Материк", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", "#FF8FB0BA"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF8FB0BA"), land, new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        var settings = new ContinentSettings { Seed = 17, RiversPerMillion = 12, TownsPerMillion = 14 };
        foreach (var step in Enum.GetValues<ContinentStep>())
        {
            document = ContinentGenerator.Run(document, land.Id, step, settings);
            var info = new SKImageInfo(1024, 768);
            using var surface = SKSurface.Create(info)!;
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true });
            using var bitmap = new SKBitmap(info);
            surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
            LandCliffRenderingTests.Save(bitmap, Path.Combine(folder, $"continent-{(int)step + 1}-{step.ToString().ToLowerInvariant()}.png"));
        }
    }
}
