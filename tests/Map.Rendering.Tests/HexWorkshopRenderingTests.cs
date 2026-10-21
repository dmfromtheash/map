using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class HexWorkshopRenderingTests
{
    /// <summary>Writes review pictures when MAP_HEX_PNG names a folder.</summary>
    private static readonly string? ReviewFolder = Environment.GetEnvironmentVariable("MAP_HEX_PNG");

    /// <summary>A small world with every workshop construct: coast, tall and living tiles, a river crossed by a road, regions, keys, fog.</summary>
    internal static (MapDocument Document, HexLayer Layer, HexCoord RiverHex, HexCoord FoggedHex) World(HexOrientation orientation)
    {
        var document = HexMapFactory.Create("Мир", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"),
            orientation, 64, 14, 10, "#FFF2E9DC");
        var layer = document.Layers.OfType<HexLayer>().Single();
        var layout = layer.Board.Layout;
        HexCoord At(int c, int r) => layout.FromOffset(new HexOffset(c, r));
        var cells = new Dictionary<HexCoord, HexCell?>();
        for (var c = 0; c < 14; c++)
        {
            for (var r = 0; r < 10; r++)
            {
                var terrain = c < 3 ? "starter/sea" : c == 3 ? "starter/shallows"
                    : r < 3 && c > 9 ? "starter/peaks"
                    : r < 4 && c > 6 ? "starter/live-mountains"
                    : r > 6 && c > 8 ? "starter/live-forest"
                    : c > 6 ? "starter/forest" : "starter/grass";
                cells[At(c, r)] = HexCell.Of(terrain, (c + r) % 3, region: c is > 4 and < 9 ? 1 : c >= 9 ? 2 : 0, revealed: c < 8);
            }
        }

        cells[At(6, 5)] = cells[At(6, 5)]!.WithFeature("starter/village", 0);
        var river = HexPaths.Walk([At(11, 0), At(9, 3), At(5, 5), At(3, 6)]);
        var road = HexPaths.Walk([At(6, 1), At(6, 9)]);
        var crossing = river.Intersect(road).First();
        layer = layer with
        {
            Board = layer.Board.With(cells),
            Paths =
            [
                new HexPath(HexPathId.New(), HexPathKind.River, HexRouteStyle.Meander, [.. river], []),
                new HexPath(HexPathId.New(), HexPathKind.Road, HexRouteStyle.Centers, [.. road], []),
                new HexPath(HexPathId.New(), HexPathKind.Border, HexRouteStyle.Edges, [],
                    [.. HexPaths.EdgeWalk(layout, new HexVertex(At(4, 0), 0), new HexVertex(At(4, 9), 1))]),
            ],
            Regions = [new HexRegion(1, "Долина", "#FFC04030"), new HexRegion(2, "Горы", "#FF3050C0")],
            Notes = ImmutableDictionary<HexCoord, HexNote>.Empty
                .Add(At(6, 5), new HexNote("Ольховка", HexNoteCategory.Settlement, "Староста врёт.", "Деревня у брода."))
                .Add(At(12, 8), new HexNote("Логово", HexNoteCategory.Danger, "Тролль.")),
            FogEnabled = true,
        };
        return (document.WithLayer(layer.Id, _ => layer), layer, crossing, At(12, 5));
    }

    private static SKBitmap Render(MapDocument document, double scale, HexChunkCache? cache, bool player = false)
    {
        var bitmap = new SKBitmap((int)(document.Canvas.Width * scale), (int)(document.Canvas.Height * scale), SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Scale((float)scale);
        SceneRenderer.Render(canvas, document, new RenderOptions { ViewScale = scale, HexChunks = cache, HexPlayerView = player });
        return bitmap;
    }

    private static SKColor At(SKBitmap bitmap, HexLayout layout, HexCoord hex, double scale, double dx = 0, double dy = 0)
    {
        var (x, y) = layout.Center(hex);
        return bitmap.GetPixel((int)((x + dx) * scale), (int)((y + dy) * scale));
    }

    [Theory]
    [InlineData(HexOrientation.Flat)]
    [InlineData(HexOrientation.Pointy)]
    public void The_workshop_world_draws_its_bridge_fog_and_player_view(HexOrientation orientation)
    {
        var (document, layer, crossing, fogged) = World(orientation);
        const double scale = 0.5;
        using var cache = new HexChunkCache();
        SKBitmap gm = Render(document, scale, cache);
        for (var frame = 0; frame < 12; frame++)
        {
            gm.Dispose();
            gm = Render(document, scale, cache);
        }

        using (gm)
        using (var player = Render(document, scale, null, player: true))
        using (var direct = Render(document, scale, null))
        {
            // The bridge deck (brown planks) sits where the road crosses the river.
            var bridge = At(gm, layer.Board.Layout, crossing, scale);
            Assert.True(bridge.Red > bridge.Blue, $"bridge pixel {bridge}");

            // The game master sees through the fog; the players do not (brightness over the hex,
            // since a single pixel may land on a dark tree).
            double Brightness(SKBitmap bitmap)
            {
                var total = 0.0;
                for (var dx = -2; dx <= 2; dx++)
                {
                    for (var dy = -2; dy <= 2; dy++)
                    {
                        var c = At(bitmap, layer.Board.Layout, fogged, scale, dx * layer.Board.Layout.Size * 0.15, dy * layer.Board.Layout.Size * 0.15);
                        total += c.Red + c.Green + c.Blue;
                    }
                }

                return total / 25;
            }

            Assert.True(Brightness(gm) > Brightness(player) + 40, $"{Brightness(gm)} / {Brightness(player)}");
            var hidden = At(player, layer.Board.Layout, fogged, scale, 0, layer.Board.Layout.Size * 0.5);
            Assert.True(Math.Abs(hidden.Red - 0x22) < 6 && Math.Abs(hidden.Blue - 0x26) < 6, $"player fog {hidden}");

            // Kept chunk pictures show the same world as direct drawing.
            var differing = 0;
            var samples = 0;
            for (var y = 0; y < direct.Height; y += 3)
            {
                for (var x = 0; x < direct.Width; x += 3)
                {
                    samples++;
                    var a = direct.GetPixel(x, y);
                    var b = gm.GetPixel(x, y);
                    if (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) > 60) differing++;
                }
            }

            if (ReviewFolder is { } folder)
            {
                Directory.CreateDirectory(folder);
                foreach (var (name, bitmap) in new[] { ("gm", gm), ("player", player) })
                {
                    using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(Path.Combine(folder, $"workshop-{orientation.ToString().ToLowerInvariant()}-{name}.png"), data.ToArray());
                }
            }

            Assert.True(differing < samples / 50, $"{differing} of {samples} pixels differ");
        }
    }

    [Fact]
    public void Tile_brushwork_is_seeded_the_same_in_every_run_of_the_program()
    {
        // string.GetHashCode and HashCode.Combine change with each start of the process, which
        // painted the starter tiles (and every export of a hex map) differently after each launch.
        Assert.Equal(255315222, HexTileArt.StableSeed("starter/grass", 7919));
        Assert.NotEqual(HexTileArt.StableSeed("starter/grass", 0), HexTileArt.StableSeed("starter/grass", 1));
    }

    [Fact]
    public void A_tall_tile_hangs_over_the_row_behind_it()
    {
        var document = HexMapFactory.Create("Горы", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"),
            HexOrientation.Pointy, 64, 3, 3, "#FFF2E9DC");
        var layer = document.Layers.OfType<HexLayer>().Single();
        var layout = layer.Board.Layout;
        var behind = layout.FromOffset(new HexOffset(1, 0));
        var front = layout.FromOffset(new HexOffset(1, 1));
        var cells = new Dictionary<HexCoord, HexCell?> { [behind] = HexCell.Of("starter/snow"), [front] = HexCell.Of("starter/peaks") };
        document = HexPaintCommand.Create("Горы", layer, cells)!.Apply(document);
        using var bitmap = Render(document, 1, null);
        // The upper part of the snow hex behind is covered by the peaks: grey rock, not snow white.
        var (fx, fy) = layout.Center(front);
        var hits = 0;
        for (var dy = 1.0; dy < 1.5; dy += 0.05)
        {
            var pixel = bitmap.GetPixel((int)fx, (int)(fy - layout.Size * dy));
            if (pixel.Red < 200) hits++;
        }

        Assert.True(hits >= 3, $"{hits} rock pixels above the hex");
    }

    [Fact]
    public void A_turned_tile_is_the_same_picture_turned()
    {
        var layout = new HexLayout(HexOrientation.Flat, 64, 100, 100);
        using var straight = new SKBitmap(200, 200);
        using var turned = new SKBitmap(200, 200);
        using (var canvas = new SKCanvas(straight)) HexLayerRenderer.DrawTile(canvas, layout, new HexCoord(0, 0), "starter/canyon", 0);
        using (var canvas = new SKCanvas(turned)) HexLayerRenderer.DrawTile(canvas, layout, new HexCoord(0, 0), "starter/canyon", 0, rotation: 3);
        // Half a turn: the pixel above the centre becomes the pixel below it.
        var a = straight.GetPixel(100, 60);
        var b = turned.GetPixel(100, 140);
        Assert.True(Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) < 40, $"{a} vs {b}");
    }

    [Theory]
    [InlineData(HexOrientation.Flat)]
    [InlineData(HexOrientation.Pointy)]
    public void A_generated_world_draws_seas_land_rivers_and_towns(HexOrientation orientation)
    {
        var document = HexMapFactory.Create("Мир", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"), orientation, 32, 60, 40, "#FFF2E9DC");
        var layer = document.Layers.OfType<HexLayer>().Single();
        var world = HexWorldGenerator.RunAll(layer, new HexGeneratorSettings { Seed = 4, SettlementsPerThousand = 10 });
        document = document.WithLayer(layer.Id, _ => world);
        using var bitmap = Render(document, 0.5, null);
        var colours = new HashSet<(int, int, int)>();
        for (var y = 0; y < bitmap.Height; y += 7)
        {
            for (var x = 0; x < bitmap.Width; x += 7)
            {
                var c = bitmap.GetPixel(x, y);
                colours.Add((c.Red / 32, c.Green / 32, c.Blue / 32));
            }
        }

        Assert.True(colours.Count > 25, $"{colours.Count} colour buckets");
        if (ReviewFolder is not { } folder) return;
        Directory.CreateDirectory(folder);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(folder, $"generated-{orientation.ToString().ToLowerInvariant()}.png"), data.ToArray());
    }

    [Fact]
    public async Task Pictures_drawn_in_the_background_show_the_same_world_once_they_arrive()
    {
        var (document, _, _, _) = World(HexOrientation.Flat);
        const double scale = 0.5;
        using var direct = Render(document, scale, null);
        using var cache = new HexChunkCache { BuildInBackground = true };
        SKBitmap kept = Render(document, scale, cache);
        Assert.True(cache.Queued > 0, "the first frame asks the worker for pictures");
        for (var frame = 0; frame < 200 && (cache.Queued > 0 || frame < 3); frame++)
        {
            await Task.Delay(20);
            kept.Dispose();
            kept = Render(document, scale, cache);
        }

        using (kept)
        {
            Assert.Equal(0, cache.Queued);
            var differing = 0;
            var samples = 0;
            for (var y = 0; y < direct.Height; y += 3)
            {
                for (var x = 0; x < direct.Width; x += 3)
                {
                    samples++;
                    var a = direct.GetPixel(x, y);
                    var b = kept.GetPixel(x, y);
                    if (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) > 60) differing++;
                }
            }

            Assert.True(differing < samples / 50, $"{differing} of {samples} pixels differ");
        }
    }

    private static int Distance(SKColor a, SKColor b) => Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);

    private static async Task<SKBitmap> Settled(MapDocument document, double scale, HexChunkCache cache)
    {
        var bitmap = Render(document, scale, cache);
        for (var frame = 0; frame < 200 && (cache.Queued > 0 || frame < 3 || cache.Animating(HexChunkCache.Now)); frame++)
        {
            await Task.Delay(20);
            bitmap.Dispose();
            bitmap = Render(document, scale, cache);
        }

        return bitmap;
    }

    [Fact]
    public async Task An_edited_chunk_keeps_its_previous_picture_until_the_new_one_arrives()
    {
        var (document, layer, _, _) = World(HexOrientation.Flat);
        const double scale = 0.5;
        using var cache = new HexChunkCache { BuildInBackground = true };
        using var before = await Settled(document, scale, cache);
        var layout = layer.Board.Layout;
        var sea = layout.FromOffset(new HexOffset(1, 5));
        var edited = layer with { Board = layer.Board.With([new(layout.FromOffset(new HexOffset(5, 5)), HexCell.Of("starter/sea", 0))]) };
        var next = document.WithLayer(layer.Id, _ => edited);
        // The first frame after a fill-like edit: no flat patch of the chunk's average colour.
        using var first = Render(next, scale, cache);
        Assert.True(Distance(At(before, layout, sea, scale), At(first, layout, sea, scale)) < 40,
            "the sea keeps its picture while the edited chunk is drawn again");
        using var after = await Settled(next, scale, cache);
        using var direct = Render(next, scale, null);
        Assert.True(Distance(At(direct, layout, layout.FromOffset(new HexOffset(5, 5)), scale),
            At(after, layout, layout.FromOffset(new HexOffset(5, 5)), scale)) < 60, "the new picture arrives");
    }

    [Fact]
    public async Task A_revealed_hex_melts_out_of_the_fog()
    {
        var (document, layer, _, fogged) = World(HexOrientation.Flat);
        const double scale = 0.5;
        using var cache = new HexChunkCache();
        using var before = await Settled(document, scale, cache);
        var revealed = layer with { Board = layer.Board.With([new(fogged, layer.Board[fogged]!.WithRevealed(true))]) };
        var next = document.WithLayer(layer.Id, _ => revealed);
        using var direct = Render(next, scale, null);
        var layout = layer.Board.Layout;
        var foggy = At(before, layout, fogged, scale);
        var clear = At(direct, layout, fogged, scale);
        Assert.True(Distance(foggy, clear) > 20, "the fog shows on the hex");
        using var first = Render(next, scale, cache);
        Assert.True(Distance(At(first, layout, fogged, scale), foggy) < Distance(foggy, clear) / 2, "the first frame still shows most of the fog");
        await Task.Delay((int)HexChunkCache.FogFadeMilliseconds + 80);
        using var last = Render(next, scale, cache);
        Assert.True(Distance(At(last, layout, fogged, scale), clear) < 12, "the fog has melted away");
    }

    [Fact]
    public void A_peak_in_front_hides_a_village_in_the_row_behind()
    {
        static (MapDocument, HexLayer) Board(bool village)
        {
            var document = HexMapFactory.Create("Глубина", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"),
                HexOrientation.Flat, 64, 6, 6, "#FFF2E9DC");
            var layer = document.Layers.OfType<HexLayer>().Single();
            var layout = layer.Board.Layout;
            var cells = new Dictionary<HexCoord, HexCell?>();
            for (var c = 0; c < 6; c++)
            {
                for (var r = 0; r < 6; r++) cells[layout.FromOffset(new HexOffset(c, r))] = HexCell.Of("starter/grass", 0);
            }

            cells[layout.FromOffset(new HexOffset(2, 3))] = HexCell.Of("starter/peaks", 0);
            if (village) cells[layout.FromOffset(new HexOffset(2, 2))] = HexCell.Of("starter/grass", 0).WithFeature("starter/village", 0);
            layer = layer with { Board = layer.Board.With(cells) };
            return (document.WithLayer(layer.Id, _ => layer), layer);
        }

        const double scale = 1;
        var (with, layer) = Board(village: true);
        var (without, _) = Board(village: false);
        using var cache = new HexChunkCache();
        using var a = Render(with, scale, cache);
        using var b = Render(without, scale, null);
        var layout = layer.Board.Layout;
        var peak = layout.FromOffset(new HexOffset(2, 3));
        var art = HexTileArt.Image("starter/peaks", 0, HexOrientation.Flat)!;
        using var pixels = SKBitmap.FromImage(art);
        var bounds = layout.Bounds(peak);
        var (_, cy) = layout.Center(peak);
        var hexPixels = HexTileArt.PixelSize(HexOrientation.Flat).Height;
        var top = bounds.Top - (art.Height - hexPixels) * (bounds.Bottom - bounds.Top) / hexPixels;
        int covered = 0, differing = 0;
        for (var y = top; y < cy; y += 2)
        {
            for (var x = bounds.Left; x < bounds.Right; x += 2)
            {
                var u = (int)((x - bounds.Left) / (bounds.Right - bounds.Left) * art.Width);
                var v = (int)((y - top) / (bounds.Bottom - top) * art.Height);
                if (u < 0 || v < 0 || u >= art.Width || v >= art.Height || pixels.GetPixel(u, v).Alpha < 250) continue;
                covered++;
                if (Distance(a.GetPixel((int)(x * scale), (int)(y * scale)), b.GetPixel((int)(x * scale), (int)(y * scale))) > 24) differing++;
            }
        }

        if (ReviewFolder is { } folder)
        {
            Directory.CreateDirectory(folder);
            using (var fa = File.Create(Path.Combine(folder, "depth-with.png"))) a.Encode(fa, SKEncodedImageFormat.Png, 100);
            using (var fb = File.Create(Path.Combine(folder, "depth-without.png"))) b.Encode(fb, SKEncodedImageFormat.Png, 100);
        }

        Assert.True(covered > 50, "the peak rises over the row behind");
        Assert.True(differing <= covered / 60, $"{differing} of {covered} pixels of the peak show the village behind it");
    }

    [Fact]
    public async Task The_world_moves_while_it_is_looked_at_and_stands_still_otherwise()
    {
        var (document, _, _, _) = World(HexOrientation.Flat);
        const double scale = 0.5;
        using var cache = new HexChunkCache();
        SKBitmap Frame(bool ambient)
        {
            var bitmap = new SKBitmap((int)(document.Canvas.Width * scale), (int)(document.Canvas.Height * scale), SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Scale((float)scale);
            SceneRenderer.Render(canvas, document, new RenderOptions { ViewScale = scale, HexChunks = cache, HexAmbient = ambient });
            return bitmap;
        }

        static int Differing(SKBitmap a, SKBitmap b)
        {
            var n = 0;
            for (var y = 0; y < a.Height; y += 2)
            {
                for (var x = 0; x < a.Width; x += 2)
                {
                    if (Distance(a.GetPixel(x, y), b.GetPixel(x, y)) > 6) n++;
                }
            }

            return n;
        }

        using (Frame(false)) { }
        using var still1 = Frame(false);
        // The living world fades in over about a second.
        async Task WarmUp()
        {
            for (var i = 0; i < 14; i++)
            {
                using (Frame(true)) { }
                await Task.Delay(90);
            }
        }

        await Task.Delay(400);
        using var still2 = Frame(false);
        Assert.Equal(0, Differing(still1, still2));
        await WarmUp();
        using var live1 = Frame(true);
        await Task.Delay(400);
        using var live2 = Frame(true);
        Assert.True(cache.Alive(HexChunkCache.Now), "a moving world asks for its next frame");
        if (ReviewFolder is { } folder)
        {
            Directory.CreateDirectory(folder);
            using var file = File.Create(Path.Combine(folder, "ambient.png"));
            live2.Encode(file, SKEncodedImageFormat.Png, 100);
        }

        Assert.True(Differing(live1, live2) > 20, "water glints, wind and clouds move between two frames");
    }

    [Fact]
    public async Task Trees_sway_and_only_trees()
    {
        var (document, layer, _, _) = World(HexOrientation.Flat);
        const double scale = 1;
        using var cache = new HexChunkCache();
        SKBitmap Frame()
        {
            var bitmap = new SKBitmap((int)(document.Canvas.Width * scale), (int)(document.Canvas.Height * scale), SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Scale((float)scale);
            SceneRenderer.Render(canvas, document, new RenderOptions { ViewScale = scale, HexChunks = cache, HexAmbient = true });
            return bitmap;
        }

        for (var i = 0; i < 14; i++)
        {
            using (Frame()) { }
            await Task.Delay(90);
        }

        using var a = Frame();
        await Task.Delay(700);
        using var b = Frame();
        if (ReviewFolder is { } folder)
        {
            Directory.CreateDirectory(folder);
            using (var fa = File.Create(Path.Combine(folder, "sway-a.png"))) a.Encode(fa, SKEncodedImageFormat.Png, 100);
            using (var fb = File.Create(Path.Combine(folder, "sway-b.png"))) b.Encode(fb, SKEncodedImageFormat.Png, 100);
        }

        var layout = layer.Board.Layout;
        int Moved(int column, int row)
        {
            var bounds = layout.Bounds(layout.FromOffset(new HexOffset(column, row)));
            var n = 0;
            for (var y = (int)bounds.Top + 4; y < bounds.Bottom - 4; y += 2)
            {
                for (var x = (int)bounds.Left + 4; x < bounds.Right - 4; x += 2)
                {
                    if (Distance(a.GetPixel(x, y), b.GetPixel(x, y)) > 30) n++;
                }
            }

            return n;
        }

        // A living forest hex (bottom right) moves; a plain grass hex far from water and woods does not
        // more than the passing cloud shade could.
        Assert.True(Moved(11, 8) > 10, "the trees of the living forest sway");
        Assert.True(Moved(5, 8) <= 2, "plain grass stays where it is");
    }
}
