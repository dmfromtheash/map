using System.Runtime.CompilerServices;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// R01: derived render caches must not keep the tiles of document states that nothing else
/// holds any more (a discarded redo branch), and must still hit for the state that is current.
/// </summary>
public sealed class DerivedCacheRetentionTests
{
    private static readonly WorldBounds View = new(0, 0, 256, 256);

    private static LandLayer Land(byte value) => new(LayerId.New(), "Земля", true, false, 1,
        TileMap.Empty(1).With(new TileKey(0, 0), Enumerable.Repeat(value, TileMap.TileSize * TileMap.TileSize).ToArray()),
        new LandEdgeStyle());

    private static SKImage Image(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height))!;
        return surface.Snapshot();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Fill(RenderImageCache cache)
    {
        var land = Land(200);
        Assert.NotNull(cache.Get([land], View, 1, null, Image));
        return new WeakReference(land.GeometryTiles);
    }

    [Fact]
    public void Kept_image_does_not_keep_the_tiles_of_a_land_state_nobody_holds()
    {
        using var cache = new RenderImageCache(4);
        var tiles = Fill(cache);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(tiles.IsAlive);
        // The dead entry is dropped with its image on the next lookup.
        Assert.Null(cache.TryGetExact([Land(10)], View, 1, null));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void A_new_instance_of_the_same_land_still_hits_the_kept_image()
    {
        using var cache = new RenderImageCache(4);
        var land = Land(120);
        var built = cache.Get([land], View, 1, null, Image);
        GC.Collect(); GC.WaitForPendingFinalizers();
        // Same tiles, same style, a different record instance: what a redrawn frame produces.
        var again = land with { Name = land.Name };
        Assert.NotSame(land, again);
        Assert.Same(built, cache.TryGetExact([again], View, 1, null));
        // Another tile map is another state.
        Assert.Null(cache.TryGetExact([Land(120)], View, 1, null));
        GC.KeepAlive(land);
    }

    [Fact]
    public void Tile_image_is_rebuilt_for_a_new_array_and_forgets_a_collected_one()
    {
        using var cache = new TileImageCache(64);
        var layer = LayerId.New();
        var first = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var image = cache.Get(layer, new TileKey(0, 0), TileImageKind.LandCoverage, first);
        Assert.Same(image, cache.Get(layer, new TileKey(0, 0), TileImageKind.LandCoverage, first));
        var second = first.ToArray();
        Assert.NotSame(image, cache.Get(layer, new TileKey(0, 0), TileImageKind.LandCoverage, second));

        DrawTemporaryTile(cache, layer);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        cache.BeginFrame(); cache.EndFrame();
        Assert.Equal(1, cache.Count);
        GC.KeepAlive(second);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DrawTemporaryTile(TileImageCache cache, LayerId layer)
    {
        cache.BeginFrame();
        cache.Get(layer, new TileKey(1, 0), TileImageKind.LandCoverage, new byte[TileMap.TileSize * TileMap.TileSize]);
        cache.EndFrame();
    }
}
