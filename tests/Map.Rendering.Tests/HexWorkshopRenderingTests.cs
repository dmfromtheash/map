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
}
