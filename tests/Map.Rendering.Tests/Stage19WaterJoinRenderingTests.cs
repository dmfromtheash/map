using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// How water meets land and other water: a river leaving the coast, two separate systems that
/// cross, and a creek of one system reaching the lake of another. Set MAP_WATER_FRAMES to a
/// directory to also write the frames as PNG for a visual review.
/// </summary>
public sealed class Stage19WaterJoinRenderingTests
{
    private const int Size = 512;

    internal static MapDocument Scene(bool withLand = true, Func<RiverStyle, RiverStyle>? style = null, bool splitLayers = false)
    {
        var land = TileMap.Empty(1);
        if (withLand)
        {
            for (var ty = 0; ty < 2; ty++)
                for (var tx = 0; tx < 2; tx++)
                {
                    var coverage = new byte[TileMap.TileSize * TileMap.TileSize];
                    for (var y = 0; y < TileMap.TileSize; y++)
                        for (var x = 0; x < TileMap.TileSize; x++)
                        {
                            var wx = tx * TileMap.TileSize + x; var wy = ty * TileMap.TileSize + y;
                            // A blob with a bay on the right, so a river crosses a curved coast.
                            var dx = (wx - 230) / 190.0; var dy = (wy - 240) / 170.0;
                            var bay = Math.Exp(-(((wx - 430) * (wx - 430)) + ((wy - 300) * (wy - 300))) / 2600.0) * 0.55;
                            if (dx * dx + dy * dy + bay < 1) coverage[(y * TileMap.TileSize) + x] = 255;
                        }
                    land = land.With(new TileKey(tx, ty), coverage);
                }
        }
        var landLayer = new LandLayer(LayerId.New(), "Земля", true, false, 1, land,
            new LandEdgeStyle { FillColor = "#FF9C9A6E", FillTextureId = "ground-meadow" });

        var apply = style ?? (s => s);
        var main = new River
        {
            Name = "Река",
            Style = apply(new RiverStyle()),
            Channels = [RiverEditor.NewChannel([new(70, 150), new(180, 190), new(300, 215), new(400, 270), new(500, 330)], 16)],
        };
        main.Validate();
        var crossing = new River
        {
            Name = "Поперечная",
            Style = apply(new RiverStyle()),
            Channels = [RiverEditor.NewChannel([new(330, 90), new(310, 200), new(330, 300), new(300, 420)], 10)],
        };
        crossing.Validate();
        var lake = new River
        {
            Name = "Озеро",
            Style = apply(new RiverStyle()),
            Channels = [RiverEditor.NewLake([new(150, 310), new(210, 300), new(250, 340), new(230, 390), new(170, 400), new(130, 360)])],
        };
        lake.Validate();
        var creek = new River
        {
            Name = "Ручей",
            Style = apply(new RiverStyle()),
            Channels = [RiverEditor.NewChannel([new(60, 440), new(110, 400), new(150, 365)], 7)],
        };
        creek.Validate();

        // Either every river on one layer, or the transverse river and the creek on a second
        // layer above the first: water on two layers must still meet as one body.
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])
            { Rivers = splitLayers ? [main, lake] : [main, crossing, lake, creek] };
        var layers = new List<MapLayer> { new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFE9E1CC"), landLayer, objects };
        if (splitLayers)
            layers.Add(new ObjectLayer(LayerId.New(), "Верхние", true, false, 1, []) { Rivers = [crossing, creek] });
        return new MapDocument(DocumentId.New(), "Вода и земля", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(Size, Size, "px", "#FFE9E1CC"), layers);
    }

    /// <summary>Two pieces of land on two layers, the upper one lying partly over the lower, and a river through both.</summary>
    internal static MapDocument TwoLands(int upperLevel = 0, int riverLevel = 0)
    {
        TileMap Blob(double cx, double cy, double rx, double ry)
        {
            var map = TileMap.Empty(1);
            for (var ty = 0; ty < 2; ty++)
                for (var tx = 0; tx < 2; tx++)
                {
                    var coverage = new byte[TileMap.TileSize * TileMap.TileSize];
                    var any = false;
                    for (var y = 0; y < TileMap.TileSize; y++)
                        for (var x = 0; x < TileMap.TileSize; x++)
                        {
                            var dx = (tx * TileMap.TileSize + x - cx) / rx; var dy = (ty * TileMap.TileSize + y - cy) / ry;
                            if (dx * dx + dy * dy < 1) { coverage[(y * TileMap.TileSize) + x] = 255; any = true; }
                        }
                    if (any) map = map.With(new TileKey(tx, ty), coverage);
                }
            return map;
        }
        var lowerLand = new LandLayer(LayerId.New(), "Земля А", true, false, 1, Blob(180, 250, 150, 130),
            new LandEdgeStyle { FillColor = "#FF9C9A6E" });
        var upperLand = new LandLayer(LayerId.New(), "Земля Б", true, false, 1, Blob(340, 270, 130, 110),
            new LandEdgeStyle { FillColor = "#FF9C9A6E" }) { Level = upperLevel };
        var river = new River
        {
            Name = "Река",
            Level = riverLevel,
            Channels = [RiverEditor.NewChannel([new(40, 200), new(180, 240), new(330, 300), new(480, 330)], 14)],
        };
        river.Validate();
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Rivers = [river] };
        return new MapDocument(DocumentId.New(), "Две земли", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(Size, Size, "px", "#FFE9E1CC"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFE9E1CC"), lowerLand, upperLand, objects]);
    }

    internal static SKBitmap Render(MapDocument document, double scale = 1)
    {
        var info = new SKImageInfo((int)(Size * scale), (int)(Size * scale));
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Scale((float)scale);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { ViewScale = scale });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    internal static SKBitmap RenderRegion(MapDocument document, WorldBounds region, double scale)
    {
        var info = new SKImageInfo((int)Math.Ceiling((region.Right - region.Left) * scale), (int)Math.Ceiling((region.Bottom - region.Top) * scale));
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Scale((float)scale);
        surface.Canvas.Translate((float)-region.Left, (float)-region.Top);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { ViewScale = scale, VisibleWorld = region });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    private static void Save(SKBitmap bitmap, string name)
    {
        var directory = Environment.GetEnvironmentVariable("MAP_WATER_FRAMES");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(Path.Combine(directory, name));
        data.SaveTo(file);
    }

    private static bool InsideLand(MapDocument document, double x, double y)
    {
        var land = document.Layers.OfType<LandLayer>().Single();
        var key = new TileKey((int)Math.Floor(x / TileMap.TileSize), (int)Math.Floor(y / TileMap.TileSize));
        if (land.GeometryTiles.TryGet(key) is not { } tile) return false;
        var px = (int)Math.Clamp(x - (key.X * TileMap.TileSize), 0, TileMap.TileSize - 1);
        var py = (int)Math.Clamp(y - (key.Y * TileMap.TileSize), 0, TileMap.TileSize - 1);
        return tile[(py * TileMap.TileSize) + px] > 127;
    }

    private static bool IsWater(SKColor pixel) => pixel.Blue > pixel.Red + 20 && pixel.Blue > 110;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_systems_that_cross_share_one_outline_without_a_bank_through_the_water(bool splitLayers)
    {
        // Where the narrower transverse river crosses the main one, its own edges run inside the
        // main river's water. Those edges must carry no bank line: the two are one body of water,
        // whether they sit on one object layer or on two.
        var document = Scene(withLand: false, splitLayers: splitLayers);
        var rivers = document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Rivers).ToList();
        var main = RiverGeometry.Evaluate(rivers.Single(r => r.Name == "Река")).Channels.Values.Single().Samples;
        var crossing = RiverGeometry.Evaluate(rivers.Single(r => r.Name == "Поперечная")).Channels.Values.Single().Samples;
        var meeting = crossing.MinBy(c => main.Min(m => (m.X - c.X) * (m.X - c.X) + (m.Y - c.Y) * (m.Y - c.Y)));
        var region = new WorldBounds(280, 180, 350, 260);
        using var frame = RenderRegion(document, region, 4);
        foreach (var side in new[] { 1, -1 })
        {
            var x = meeting.X - meeting.TangentY * meeting.HalfWidth * side;
            var y = meeting.Y + meeting.TangentX * meeting.HalfWidth * side;
            var pixel = frame.GetPixel((int)((x - region.Left) * 4), (int)((y - region.Top) * 4));
            Assert.True(IsWater(pixel), $"the transverse river's edge inside the main water is {pixel}, not water");
        }

        // Away from the crossing the same edge is still a bank line.
        var free = crossing.MinBy(c => Math.Abs(c.Y - 190));
        var bank = frame.GetPixel(
            (int)((free.X - free.TangentY * free.HalfWidth - region.Left) * 4),
            (int)((free.Y + free.TangentX * free.HalfWidth - region.Top) * 4));
        Assert.True(bank.Blue < 110, $"the bank away from the crossing should be dark, got {bank}");
    }

    [Fact]
    public void Water_runs_past_the_coast_and_dissolves_instead_of_stopping_at_it()
    {
        var document = Scene();
        var main = document.Layers.OfType<ObjectLayer>().Single().Rivers[0];
        var samples = RiverGeometry.Evaluate(main).Channels.Values.Single().Samples;
        var exit = -1;
        for (var i = 1; i < samples.Length; i++)
            if (InsideLand(document, samples[i - 1].X, samples[i - 1].Y) && !InsideLand(document, samples[i].X, samples[i].Y)) { exit = i; break; }
        Assert.True(exit > 0, "the main river leaves the land in this scene");
        var coast = samples[exit];

        using var frame = Render(document);
        SKColor At(double along) => frame.GetPixel(
            (int)Math.Round(coast.X + coast.TangentX * along), (int)Math.Round(coast.Y + coast.TangentY * along));

        // Since the coast decision of 2026-09-23 a river meets the sea on the shore line itself with
        // a narrow soft edge (RiverRenderer: no tongue of river water into the sea). So: water just
        // inside the coast, the page a river-width beyond it, and no hard cut on the line - the
        // pixel on the coast is a blend, not the plain page. (R04 rewrote the 2026-09-08 expectation
        // of water running on past the coast.)
        var water = SKColor.Parse(main.Style.WaterColor);
        var page = frame.GetPixel(frame.Width - 4, 4);
        static int Distance(SKColor a, SKColor b) => Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
        Assert.True(IsWater(At(-6)), $"6 units inside the coast must still be river water, got {At(-6)}");
        var far = At(60);
        Assert.True(far.Red > far.Blue, $"60 units past the coast the sea page should show, got {far}");
        Assert.True(Distance(At(0), page) > 6 || Distance(At(1), page) > 6,
            $"the coast line must be a soft edge, not a cut to the page: {At(0)} / {At(1)}, page {page}, water {water}");
    }

    [Fact]
    public void A_piece_of_land_over_another_shows_no_coast_between_them()
    {
        var document = TwoLands();
        var upper = (LandLayer)document.Layers[2];
        using var frame = Render(document);
        var plain = frame.GetPixel(150, 180); // deep inside the lower land, away from every edge
        Assert.True(plain.Green > plain.Blue, "the reference pixel is land");

        // Points on the upper piece's outline that lie over the lower piece: (340-130=210 is its
        // left edge at y=270; x=212 is where the outline used to be drawn).
        foreach (var (x, y) in new[] { (212, 270), (214, 230), (214, 310) })
        {
            var pixel = frame.GetPixel(x, y);
            Assert.True(Math.Abs(pixel.Red - plain.Red) < 28 && Math.Abs(pixel.Green - plain.Green) < 28 && Math.Abs(pixel.Blue - plain.Blue) < 28,
                $"the join between two pieces of land at {x},{y} is {pixel}, the land itself is {plain}");
        }

        // The upper piece's coast against the sea is still drawn: its right edge at (470, 270).
        var coast = frame.GetPixel(471, 270);
        Assert.True(coast.Red < plain.Red - 30 || coast.Blue > plain.Blue + 20, $"the sea coast of the upper piece should carry an outline or surf, got {coast}");
        _ = upper;
    }

    [Fact]
    public void Water_below_a_higher_piece_of_land_passes_under_it_and_still_cuts_the_lower_one()
    {
        // The river is at level 0; the right piece of land is raised to level 1. Over the left
        // piece (level 0) the river is water; under the right piece it is covered by land.
        using var frame = Render(TwoLands(upperLevel: 1, riverLevel: 0));
        var overLower = frame.GetPixel(120, 223);
        Assert.True(IsWater(overLower), $"the river over the lower land is {overLower}, not water");
        var underUpper = frame.GetPixel(420, 318);
        Assert.False(IsWater(underUpper), $"the river under the higher land should be covered, got {underUpper}");
        Assert.True(underUpper.Green > underUpper.Blue, $"land shows where the higher piece covers the river, got {underUpper}");

        // At one level the same river is water on both pieces.
        using var flat = Render(TwoLands());
        Assert.True(IsWater(flat.GetPixel(420, 318)), "at equal levels the river runs over both pieces");
    }

    [Fact]
    public void Frames_for_review()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAP_WATER_FRAMES"))) return;
        using var twoLands = Render(TwoLands(), 2);
        Save(twoLands, "two-lands-2x.png");
        using var plateau = Render(TwoLands(upperLevel: 1, riverLevel: 0), 2);
        Save(plateau, "two-lands-plateau-2x.png");
        using var split = Render(Scene(splitLayers: true), 2);
        Save(split, "split-layers-2x.png");
        using var land = Render(Scene());
        Save(land, "water-land.png");
        using var zoomed = Render(Scene(), 2.5);
        Save(zoomed, "water-land-2x.png");
        using var sea = Render(Scene(withLand: false));
        Save(sea, "water-sea.png");
        var scene = Scene();
        using var mouthRight = RenderRegion(scene, new WorldBounds(380, 220, 512, 360), 4);
        Save(mouthRight, "crop-mouth-right.png");
        using var mouthBottom = RenderRegion(scene, new WorldBounds(240, 370, 360, 470), 4);
        Save(mouthBottom, "crop-mouth-bottom.png");
        using var crossing = RenderRegion(scene, new WorldBounds(260, 160, 370, 270), 4);
        Save(crossing, "crop-crossing.png");
        using var creek = RenderRegion(scene, new WorldBounds(90, 320, 210, 440), 4);
        Save(creek, "crop-creek-lake.png");
    }
}
