using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// The kept object layers of a gesture frame: a frame drawn from them is the frame drawn without
/// them, at any transform; an edited layer is never shown from its old image; a layer whose
/// pixels depend on what lies beneath (Multiply) is always drawn directly.
/// </summary>
public sealed class LayerImageCacheTests
{
    [Fact]
    public void Kept_layers_match_the_direct_frame_and_follow_edits()
    {
        using var sprite = SKSurface.Create(new SKImageInfo(32, 32))!;
        sprite.Canvas.Clear(SKColors.Transparent);
        using (var ink = new SKPaint { Color = new SKColor(40, 110, 50, 220), IsAntialias = true })
            sprite.Canvas.DrawCircle(16, 16, 13, ink);
        using var image = sprite.Snapshot();
        SceneObject Tree(int i) => new(ObjectId.New(), SceneObjectKind.Stamp, "Tree " + i,
            10 + i % 9 * 27.3, 12 + i / 9 * 23.7, 30, 30, i * 17 % 360, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(new string('c', 64)), ObjectOpacity = .8 };
        var forest = new ObjectLayer(LayerId.New(), "Лес", true, false, .7, [.. Enumerable.Range(0, 60).Select(Tree)]);
        var multiplied = new ObjectLayer(LayerId.New(), "Тень", true, false, 1, [.. Enumerable.Range(60, 20).Select(Tree)]) { Blend = "Multiply" };
        var stroked = new ObjectLayer(LayerId.New(), "Мазок", true, false, 1, []);
        var document = new MapDocument(DocumentId.New(), "Kept", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(256, 256, "px", "#FFD8D2B0"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFD8D2B0"), forest, multiplied, stroked]);
        var coverage = new byte[TileMap.TileSize * TileMap.TileSize];
        for (var y = 40; y < 90; y++) for (var x = 30; x < 200; x++) coverage[y * TileMap.TileSize + x] = 255;
        var preview = new StrokePreview { LayerId = stroked.Id, Coverage = new Dictionary<TileKey, byte[]> { [new(0, 0)] = coverage }, Color = "#FF3060A0" };
        using var cache = new LayerImageCache();
        var stamps = new Images(image);

        SKBitmap Draw(MapDocument scene, LayerImageCache? layers, RenderStats stats)
        {
            using var surface = SKSurface.Create(new SKImageInfo(200, 180))!;
            surface.Canvas.Clear(SKColors.Black);
            // A fractional scale and offset: the kept image must land on the very same pixels.
            surface.Canvas.Translate(-13.37f, -7.61f);
            surface.Canvas.Scale(.83f);
            SceneRenderer.Render(surface.Canvas, scene, new RenderOptions
            {
                Stamps = stamps, Preview = preview, LayerImages = layers, Stats = stats,
                ViewScale = .83, VisibleWorld = new(16.1, 9.2, 16.1 + 200 / .83, 9.2 + 180 / .83),
            });
            using var snapshot = surface.Snapshot();
            return SKBitmap.FromImage(snapshot);
        }
        // The kept image is composited once more than the direct frame (a translucent group over
        // a half-float layer): rounding moves a few pixels by up to two channel units. A stale
        // or misplaced image differs by far more.
        void Same(SKBitmap a, SKBitmap b) => Assert.All(a.Pixels.Zip(b.Pixels), p =>
        {
            Assert.InRange(Math.Abs(p.First.Red - p.Second.Red), 0, 2);
            Assert.InRange(Math.Abs(p.First.Green - p.Second.Green), 0, 2);
            Assert.InRange(Math.Abs(p.First.Blue - p.Second.Blue), 0, 2);
        });

        using var direct = Draw(document, null, new());
        var build = new RenderStats();
        using var first = Draw(document, cache, build);
        var reuse = new RenderStats();
        using var second = Draw(document, cache, reuse);
        Same(direct, first);
        Same(direct, second);
        Assert.Equal(0, build.LayerImagesKept);
        // Only the forest is kept: the multiplied layer reads the frame, the stroked one is the gesture.
        Assert.Equal(1, reuse.LayerImagesKept);
        Assert.Equal(1, cache.Count);

        // An edited forest is a new record: drawn anew, never from the old image.
        var edited = document.WithLayer(forest.Id, _ => forest with { Objects = [.. forest.Objects.Skip(5)] });
        using var editedDirect = Draw(edited, null, new());
        var afterEdit = new RenderStats();
        using var editedKept = Draw(edited, cache, afterEdit);
        Assert.Equal(0, afterEdit.LayerImagesKept);
        Same(editedDirect, editedKept);

        // Without a gesture nothing is kept and the images are released.
        using var surface = SKSurface.Create(new SKImageInfo(64, 64))!;
        SceneRenderer.Render(surface.Canvas, edited, new RenderOptions { Stamps = stamps, LayerImages = cache, Stats = new() });
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void A_placed_stamp_is_drawn_over_the_kept_image_and_matches_the_direct_frame()
    {
        using var sprite = SKSurface.Create(new SKImageInfo(32, 32))!;
        sprite.Canvas.Clear(SKColors.Transparent);
        using (var ink = new SKPaint { Color = new SKColor(150, 60, 40, 230), IsAntialias = true })
            sprite.Canvas.DrawRoundRect(3, 5, 26, 22, 6, 6, ink);
        using var image = sprite.Snapshot();
        var stamps = new Images(image);
        SceneObject House(int i) => new(ObjectId.New(), SceneObjectKind.Stamp, "House " + i,
            12 + i % 8 * 26.4, 14 + i / 8 * 25.1, 28, 28, i * 23 % 360, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(new string('d', 64)), ObjectOpacity = .9 };
        var houses = new ObjectLayer(LayerId.New(), "Дома", true, false, 1, [.. Enumerable.Range(0, 40).Select(House)]);
        var marks = new ObjectLayer(LayerId.New(), "Пометки", true, false, 1, []);
        var document = new MapDocument(DocumentId.New(), "Append", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(256, 256, "px", "#FFE0D8B8"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFE0D8B8"), houses, marks]);
        // The ghost of the next placement over the houses: the frames of an armed placement.
        IReadOnlyList<SceneObject> ghost = [House(99) with { Name = "Ghost" }];
        using var cache = new LayerImageCache();

        SKBitmap Draw(MapDocument scene, LayerImageCache? layers)
        {
            using var surface = SKSurface.Create(new SKImageInfo(220, 200))!;
            surface.Canvas.Clear(SKColors.Black);
            surface.Canvas.Translate(-5.25f, -3.5f);
            surface.Canvas.Scale(.91f);
            SceneRenderer.Render(surface.Canvas, scene, new RenderOptions
            {
                Stamps = stamps, PlacementPreviewObjects = ghost, PlacementPreviewLayerId = marks.Id,
                LayerImages = layers, Stats = new(), ViewScale = .91,
                VisibleWorld = new(5.77, 3.85, 5.77 + 220 / .91, 3.85 + 200 / .91),
            });
            using var snapshot = surface.Snapshot();
            return SKBitmap.FromImage(snapshot);
        }

        using var warm = Draw(document, cache);
        var placed = new AddObjectCommand(houses.Id, House(40) with { Name = "Placed" }).Apply(document);
        Assert.True(ObjectLayerChanges.TryGet((ObjectLayer)placed.Layers[1], out _));
        using var appended = Draw(placed, cache);
        using var direct = Draw(placed, null);
        Assert.Equal(1, cache.Appended);
        Assert.All(appended.Pixels.Zip(direct.Pixels), p =>
        {
            Assert.InRange(Math.Abs(p.First.Red - p.Second.Red), 0, 2);
            Assert.InRange(Math.Abs(p.First.Green - p.Second.Green), 0, 2);
            Assert.InRange(Math.Abs(p.First.Blue - p.Second.Blue), 0, 2);
        });
    }

    [Fact]
    public void A_pan_shifts_the_kept_layer_and_matches_the_direct_frame()
    {
        using var sprite = SKSurface.Create(new SKImageInfo(32, 32))!;
        sprite.Canvas.Clear(SKColors.Transparent);
        using (var ink = new SKPaint { Color = new SKColor(90, 120, 160, 235), IsAntialias = true })
            sprite.Canvas.DrawCircle(16, 16, 14, ink);
        using var image = sprite.Snapshot();
        var stamps = new Images(image);
        SceneObject Peak(int i) => new(ObjectId.New(), SceneObjectKind.Stamp, "Peak " + i,
            8 + i % 10 * 24.6, 6 + i / 10 * 22.9, 26, 26, i * 13 % 360, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(new string('e', 64)), ObjectOpacity = .85 };
        var peaks = new ObjectLayer(LayerId.New(), "Горы", true, false, 1, [.. Enumerable.Range(0, 90).Select(Peak)]);
        var document = new MapDocument(DocumentId.New(), "Pan", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(256, 256, "px", "#FFE6E2D0"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFE6E2D0"), peaks]);
        using var cache = new LayerImageCache();

        SKBitmap Draw(float shiftX, LayerImageCache? layers, RenderStats stats)
        {
            using var surface = SKSurface.Create(new SKImageInfo(180, 160))!;
            surface.Canvas.Clear(SKColors.Black);
            surface.Canvas.Translate(-11f + shiftX, -6f);
            surface.Canvas.Scale(.8f);
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions
            {
                Stamps = stamps, LayerImages = layers, Stats = stats, ViewScale = .8,
                VisibleWorld = new((11 - shiftX) / .8, 6 / .8, (11 - shiftX + 180) / .8, (6 + 160) / .8),
            });
            using var snapshot = surface.Snapshot();
            return SKBitmap.FromImage(snapshot);
        }

        using var still = Draw(0, cache, new());
        // Whole-pixel steps of a pan: the first builds the wider image, the next ones only shift it.
        var built = new RenderStats();
        using var first = Draw(-10, cache, built);
        var shifted = new RenderStats();
        using var second = Draw(-20, cache, shifted);
        using var direct = Draw(-20, null, new());
        Assert.Equal(0, built.LayerImagesKept);
        Assert.Equal(1, shifted.LayerImagesKept);
        Assert.Equal(0, shifted.ObjectsDrawn);
        Assert.All(second.Pixels.Zip(direct.Pixels), p =>
        {
            Assert.InRange(Math.Abs(p.First.Red - p.Second.Red), 0, 2);
            Assert.InRange(Math.Abs(p.First.Green - p.Second.Green), 0, 2);
            Assert.InRange(Math.Abs(p.First.Blue - p.Second.Blue), 0, 2);
        });
        // The frame after the pan stops repeats the view: the shifted image serves it as it is.
        var paused = new RenderStats();
        using var third = Draw(-20, cache, paused);
        Assert.Equal(1, paused.LayerImagesKept);
    }

    private sealed class Images(SKImage image) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => image;
    }
}
