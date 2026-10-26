using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>R23: a generated continent draws; MAP_CONTINENT_PNG writes it for review.</summary>
public sealed class ContinentRenderingTests
{
    [Fact]
    public void Continent_sheet_for_review()
    {
        if (Environment.GetEnvironmentVariable("MAP_CONTINENT_PNG") is not { Length: > 0 } folder) return;
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
