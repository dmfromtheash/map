using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class HexLayerRenderingTests
{
    /// <summary>Writes review pictures when MAP_HEX_PNG names a folder.</summary>
    private static readonly string? ReviewFolder = Environment.GetEnvironmentVariable("MAP_HEX_PNG");

    internal static MapDocument Sample(HexOrientation orientation, int columns = 12, int rows = 9)
    {
        var document = HexMapFactory.Create("Гексы", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"),
            orientation, 64, columns, rows, "#FFF2E9DC");
        var layer = document.Layers.OfType<HexLayer>().Single();
        var terrains = HexStarterTiles.All.Where(tile => tile.Target == HexPaintTarget.Terrain).ToList();
        var features = HexStarterTiles.All.Where(tile => tile.Target == HexPaintTarget.Feature).ToList();
        var cells = new Dictionary<HexCoord, HexCell?>();
        var index = 0;
        foreach (var hex in layer.Board.HexesIn(new WorldBounds(0, 0, layer.Board.PixelWidth, layer.Board.PixelHeight)))
        {
            var offset = layer.Board.Layout.ToOffset(hex);
            if (offset.Column == columns - 1 && offset.Row % 2 == 0) continue;
            var terrain = terrains[(offset.Column / 2 + offset.Row / 3 * 3) % terrains.Count];
            var feature = index % 7 == 3 ? features[index / 7 % features.Count] : null;
            var cell = HexEditing.Place(null, hex, new HexTileChoice(terrain.Id, terrain.VariantCount));
            if (feature is not null) cell = HexEditing.Place(cell, hex, new HexTileChoice(feature.Id, feature.VariantCount, HexPaintTarget.Feature));
            cells[hex] = cell;
            index++;
        }

        var painted = HexPaintCommand.Create("Пример", layer, cells)!;
        return painted.Apply(document);
    }

    private static SKBitmap Render(MapDocument document, double scale, HexChunkCache? cache)
    {
        var width = (int)(document.Canvas.Width * scale);
        var height = (int)(document.Canvas.Height * scale);
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Scale((float)scale);
        SceneRenderer.Render(canvas, document, new RenderOptions { ViewScale = scale, HexChunks = cache });
        return bitmap;
    }

    [Theory]
    [InlineData(HexOrientation.Flat)]
    [InlineData(HexOrientation.Pointy)]
    public void Every_starter_tile_paints_a_hex_shaped_picture(HexOrientation orientation)
    {
        foreach (var tile in HexStarterTiles.All)
        {
            for (var variant = 0; variant < tile.VariantCount; variant++)
            {
                var image = HexTileArt.Image(tile.Id, variant, orientation);
                Assert.NotNull(image);
                using var pixels = SKBitmap.FromImage(image);
                // Inside the hex is painted (features may leave gaps), the corners of the box are empty.
                Assert.Equal(0, pixels.GetPixel(1, 1).Alpha);
                if (tile.Target == HexPaintTarget.Terrain)
                    Assert.True(pixels.GetPixel(pixels.Width / 2, pixels.Height / 2).Alpha > 200, tile.Id);
            }
        }

        Assert.Null(HexTileArt.Image("starter/unknown", 0, orientation));
    }

    [Theory]
    [InlineData(HexOrientation.Flat)]
    [InlineData(HexOrientation.Pointy)]
    public void Kept_chunk_images_draw_the_same_map_as_direct_drawing(HexOrientation orientation)
    {
        var document = Sample(orientation);
        using var direct = Render(document, 0.5, cache: null);
        using var cache = new HexChunkCache();
        // A frame builds one new picture over its budget; a few frames build them all.
        for (var frame = 0; frame < 4; frame++) Render(document, 0.5, cache).Dispose();
        using var kept = Render(document, 0.5, cache);
        Assert.True(cache.Count > 0);
        var differing = 0;
        for (var y = 0; y < direct.Height; y += 3)
        {
            for (var x = 0; x < direct.Width; x += 3)
            {
                var a = direct.GetPixel(x, y);
                var b = kept.GetPixel(x, y);
                if (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) > 60) differing++;
            }
        }

        Assert.True(differing < direct.Width * direct.Height / 9 / 50, $"{differing} pixels differ");

        if (ReviewFolder is { } folder)
        {
            Directory.CreateDirectory(folder);
            using var full = Render(document, 1, cache);
            using var data = full.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(folder, $"hex-{orientation.ToString().ToLowerInvariant()}.png"), data.ToArray());
            using var far = Render(document, 0.06, cache);
            using var farData = far.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(folder, $"hex-{orientation.ToString().ToLowerInvariant()}-far.png"), farData.ToArray());
        }
    }

    /// <summary>
    /// The category card and gallery pictures of the hex constructor are painted from the starter
    /// tiles themselves (MAP_HEX_PNG): map-hex.png (a cluster of seven hexes) and hex-gallery.png.
    /// </summary>
    [Fact]
    public void The_category_artwork_is_painted_from_the_starter_tiles()
    {
        using var card = new SKBitmap(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(card))
        {
            canvas.Clear(SKColors.Transparent);
            var layout = new HexLayout(HexOrientation.Pointy, 50, 128, 128);
            string[] tiles = ["starter/forest", "starter/mountains", "starter/hills", "starter/grass", "starter/sea", "starter/pines", "starter/desert"];
            var index = 0;
            foreach (var hex in HexCoord.Range(new HexCoord(0, 0), 1))
            {
                HexLayerRenderer.DrawTile(canvas, layout, hex, tiles[index++], 0, 0.6f);
            }

            HexLayerRenderer.DrawTile(canvas, layout, new HexCoord(0, 0), "starter/castle", 0);
        }

        Assert.True(card.GetPixel(128, 128).Alpha > 200);
        Assert.Equal(0, card.GetPixel(2, 2).Alpha);
        if (ReviewFolder is not { } folder) return;
        Directory.CreateDirectory(folder);
        using (var data = card.Encode(SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(Path.Combine(folder, "map-hex.png"), data.ToArray());
        var gallery = Sample(HexOrientation.Flat, 16, 10);
        using var full = Render(gallery, 0.5, null);
        using var galleryData = full.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(folder, "hex-gallery.png"), galleryData.ToArray());
    }

    [Fact]
    public void A_changed_hex_rebuilds_only_its_own_chunk_and_the_pictures_that_touch_it()
    {
        var document = Sample(HexOrientation.Flat, 40, 40);
        using var cache = new HexChunkCache();
        // A frame draws as many new chunk images as its budget allows; the rest follow in the next frames.
        void Settle(MapDocument map)
        {
            var last = -1L;
            while (cache.Built != last)
            {
                last = cache.Built;
                using (Render(map, 0.25, cache)) { }
            }
        }

        Settle(document);
        var before = cache.Built;
        Assert.True(before >= 9, $"{before} pictures for 9 chunks");
        var layer = document.Layers.OfType<HexLayer>().Single();
        var hex = layer.Board.Layout.FromOffset(new HexOffset(0, 0));
        var edited = HexPaintCommand.Create("Лес", layer, new Dictionary<HexCoord, HexCell?> { [hex] = new HexCell("starter/snow") })!.Apply(document);
        Settle(edited);
        // The corner chunk and at most its three neighbours (transitions read across chunk edges),
        // each with its ground and what stands on it - never the other five chunks of the 3 × 3 board.
        var rebuilt = cache.Built - before;
        Assert.InRange(rebuilt, 1, 8);
    }

    /// <summary>
    /// The repeating grid picture (many small hexes on screen) and the far-look blocks draw what
    /// direct drawing draws: the same hexes, lines and colours, also at the jagged board edge.
    /// </summary>
    [Theory]
    [InlineData(HexOrientation.Flat, 0.25)]
    [InlineData(HexOrientation.Pointy, 0.25)]
    [InlineData(HexOrientation.Flat, 0.6)]
    [InlineData(HexOrientation.Pointy, 0.6)]
    [InlineData(HexOrientation.Flat, 0.05)]
    [InlineData(HexOrientation.Pointy, 0.05)]
    public void The_grid_picture_and_far_blocks_draw_the_same_map_as_direct_drawing(HexOrientation orientation, double scale)
    {
        var document = Sample(orientation, 40, 32);
        using var direct = Render(document, scale, cache: null);
        using var cache = new HexChunkCache();
        SKBitmap kept = Render(document, scale, cache);
        for (var frame = 0; frame < 64; frame++)
        {
            kept.Dispose();
            kept = Render(document, scale, cache);
        }

        using (kept)
        {
            var differing = 0;
            var samples = 0;
            for (var y = 0; y < direct.Height; y += 2)
            {
                for (var x = 0; x < direct.Width; x += 2)
                {
                    samples++;
                    var a = direct.GetPixel(x, y);
                    var b = kept.GetPixel(x, y);
                    if (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) > 90) differing++;
                }
            }

            if (ReviewFolder is { } folder)
            {
                Directory.CreateDirectory(folder);
                foreach (var (name, bitmap) in new[] { ("direct", direct), ("kept", kept) })
                {
                    using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(Path.Combine(folder, $"equal-{orientation}-{scale:0.00}-{name}.png"), data.ToArray());
                }
            }

            // Far away the kept block is made at the next power-of-two scale and shrinks on screen,
            // which softens the hexes' edges a little: there the edge pixels may differ.
            Assert.True(differing < samples / (scale < 0.1 ? 15 : 40), $"{differing} of {samples} pixels differ");
        }
    }

    [Fact]
    public void Hex_numbers_never_run_together_on_large_boards()
    {
        var small = HexBoard.Create(HexOrientation.Flat, 64, 30, 20);
        var large = HexBoard.Create(HexOrientation.Flat, 64, 512, 300);
        Assert.Equal("0101", HexLayerRenderer.NumberOf(small, new HexOffset(0, 0)));
        Assert.Equal("3020", HexLayerRenderer.NumberOf(small, new HexOffset(29, 19)));
        Assert.Equal("251.009", HexLayerRenderer.NumberOf(large, new HexOffset(250, 8)));
    }
}
