using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class Stage13DLargeCanvasRenderingTests
{
    [Fact]
    public void Default_overview_cache_holds_the_test_only_262k_chunk_envelope_under_the_byte_budget()
    {
        using var cache = new ObjectOverviewCache();
        var layer = LayerId.New();

        for (var index = 0; index < 690; index++)
        {
            cache.GetOrCreate(layer, index % 30, index / 30, -34, (ulong)index, Image);
        }

        Assert.Equal(690, cache.Count);
        Assert.Equal(0, cache.Evictions);
        Assert.InRange(cache.Bytes, 1, 64L * 1024 * 1024);
    }

    [Fact]
    public void Far_sparse_land_tile_does_not_enter_the_visible_render_pass()
    {
        var coverage = new byte[TileMap.TileSize * TileMap.TileSize];
        coverage[0] = 255;
        var land = new LandLayer(
            LayerId.New(),
            "far land",
            true,
            false,
            1,
            TileMap.Empty(1).With(new TileKey(511, 511), coverage),
            new LandEdgeStyle
            {
                OutlineWidth = 8,
                InnerShadowOpacity = 0.5,
                OuterShadowOpacity = 0.5,
                ShadowBlur = 12,
                RippleCount = 8,
            });
        var document = new MapDocument(
            DocumentId.New(),
            "large sparse land",
            DateTimeOffset.UnixEpoch,
            new CanvasDefinition(131_072, 131_072, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                land,
            ]);
        var stats = new RenderStats();
        using var surface = SKSurface.Create(new SKImageInfo(320, 200))!;

        SceneRenderer.Render(surface.Canvas, document, new RenderOptions
        {
            VisibleWorld = new WorldBounds(0, 0, 320, 200),
            ViewScale = 1,
            Stats = stats,
        });

        Assert.Equal(0, stats.TilesDrawn);
        Assert.Equal(0, stats.MipmappedTiles);
        using var snapshot = surface.Snapshot();
        using var pixels = snapshot.PeekPixels();
        Assert.Equal(SKColors.White, pixels.GetPixelColor(10, 10));
    }

    private static SKImage Image()
    {
        using var surface = SKSurface.Create(new SKImageInfo(1, 1))!;
        surface.Canvas.Clear(SKColors.CornflowerBlue);
        return surface.Snapshot();
    }
}
