using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// Since 2026-10-18 every frame that repeats the previous frame's view draws its object layers
/// from kept images, not only the frames of a gesture. Such a frame must still be the frame drawn
/// without the images: the mask overlay of a layer whose mask is being edited, the clip a layer
/// takes from the layer below after that layer changed, and no image at all for an empty layer.
/// </summary>
public sealed class KeptLayerStillFrameTests
{
    [Fact]
    public void A_still_frame_in_mask_edit_mode_keeps_the_mask_overlay()
    {
        using var image = Sprite(new SKColor(40, 110, 50, 230));
        var stamps = new Images(image);
        var trees = new ObjectLayer(LayerId.New(), "Лес", true, false, 1, [.. Enumerable.Range(0, 40).Select(i => Stamp(i, 'c'))])
        {
            Mask = new LayerMask(TileMap.Empty(1).With(new TileKey(0, 0), HalfTile()), enabled: true),
        };
        var document = Scene(trees);
        using var cache = new LayerImageCache();
        using var direct = Draw(document, stamps, trees.Id, null, new());
        using var first = Draw(document, stamps, trees.Id, cache, new());
        var reuse = new RenderStats();
        using var second = Draw(document, stamps, trees.Id, cache, reuse);
        Same(direct, first);
        Same(direct, second);
        Assert.Equal(1, reuse.LayerImagesKept);
    }

    [Fact]
    public void A_clipped_layer_follows_a_change_of_the_layer_below()
    {
        using var image = Sprite(new SKColor(150, 60, 40, 240));
        var stamps = new Images(image);
        var land = new LandLayer(LayerId.New(), "Суша", true, false, 1, TileMap.Empty(1).With(new TileKey(0, 0), HalfTile()),
            LandEdgeStyle.SoftWatercolor());
        var houses = new ObjectLayer(LayerId.New(), "Дома", true, false, 1, [.. Enumerable.Range(0, 40).Select(i => Stamp(i, 'd'))])
        {
            Effects = new LayerEffects { ClippingEnabled = true },
        };
        var document = Scene(land, houses);
        using var cache = new LayerImageCache();

        using var warm = Draw(document, stamps, null, cache, new());
        // The land below grows while the view stands still: the houses over the new land appear.
        var grown = document.WithLayer(land.Id, _ => land with { GeometryTiles = land.GeometryTiles.With(new TileKey(0, 0), FullTile()) });
        using var kept = Draw(grown, stamps, null, cache, new());
        using var direct = Draw(grown, stamps, null, null, new());
        Same(direct, kept);
    }

    [Fact]
    public void An_empty_layer_gets_no_kept_image()
    {
        using var image = Sprite(new SKColor(90, 120, 160, 235));
        var stamps = new Images(image);
        var empty = new ObjectLayer(LayerId.New(), "Пометки", true, false, 1, []);
        var document = Scene(empty);
        using var cache = new LayerImageCache();

        using var first = Draw(document, stamps, null, cache, new());
        using var second = Draw(document, stamps, null, cache, new());
        Assert.Equal(0, cache.Misses);
        Assert.Equal(0, cache.Count);
    }

    private static SKImage Sprite(SKColor color)
    {
        using var sprite = SKSurface.Create(new SKImageInfo(32, 32))!;
        sprite.Canvas.Clear(SKColors.Transparent);
        using var ink = new SKPaint { Color = color, IsAntialias = true };
        sprite.Canvas.DrawCircle(16, 16, 13, ink);
        return sprite.Snapshot();
    }

    private static SceneObject Stamp(int i, char sha) => new(ObjectId.New(), SceneObjectKind.Stamp, "Stamp " + i,
        10 + i % 8 * 27.3, 12 + i / 8 * 23.7, 30, 30, i * 17 % 360, "#FFFFFFFF", "#00000000", 0, true, false)
        { Stamp = new StampStyle(new string(sha, 64)), ObjectOpacity = .9 };

    private static MapDocument Scene(params MapLayer[] layers) =>
        new(DocumentId.New(), "Still", DateTimeOffset.UnixEpoch, new CanvasDefinition(256, 256, "px", "#FFD8D2B0"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFD8D2B0"), .. layers]);

    /// <summary>The left half of the tile covered.</summary>
    private static byte[] HalfTile()
    {
        var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
        for (var y = 0; y < TileMap.TileSize; y++)
            for (var x = 0; x < TileMap.TileSize / 2; x++) bytes[(y * TileMap.TileSize) + x] = 255;
        return bytes;
    }

    private static byte[] FullTile()
    {
        var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
        Array.Fill(bytes, (byte)255);
        return bytes;
    }

    private static SKBitmap Draw(MapDocument scene, IStampImageSource stamps, LayerId? maskEdit, LayerImageCache? layers, RenderStats stats)
    {
        using var surface = SKSurface.Create(new SKImageInfo(200, 180))!;
        surface.Canvas.Clear(SKColors.Black);
        surface.Canvas.Translate(-13.37f, -7.61f);
        surface.Canvas.Scale(.83f);
        SceneRenderer.Render(surface.Canvas, scene, new RenderOptions
        {
            Stamps = stamps, MaskEditLayerId = maskEdit, LayerImages = layers, Stats = stats, ViewScale = .83,
            VisibleWorld = new(16.1, 9.2, 16.1 + 200 / .83, 9.2 + 180 / .83),
        });
        using var snapshot = surface.Snapshot();
        return SKBitmap.FromImage(snapshot);
    }

    private static void Same(SKBitmap a, SKBitmap b) => Assert.All(a.Pixels.Zip(b.Pixels), p =>
    {
        Assert.InRange(Math.Abs(p.First.Red - p.Second.Red), 0, 2);
        Assert.InRange(Math.Abs(p.First.Green - p.Second.Green), 0, 2);
        Assert.InRange(Math.Abs(p.First.Blue - p.Second.Blue), 0, 2);
    });

    private sealed class Images(SKImage image) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => image;
    }
}
