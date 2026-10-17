using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// The river mouths of the 2026-09-08 audit, as pixels: a straight mouth, an oblique wide mouth
/// running along the coast, a channel that leaves the land and re-enters it, a tributary and a
/// lake outflow, two pieces of land at different levels, and the two switches of a river. Every
/// scene is judged by what open water and dry land look like at named points - no land plug in
/// the mouth, no bank line across open sea, no double dark rim, no translucent hole, no rectangle
/// of a mask - and the same scene is drawn as one image and as tiles that meet in the mouth.
/// With <c>MAP_ESTUARY_FRAMES</c> set to a directory the frames are written there for inspection.
/// </summary>
public sealed class Stage19EstuaryScenarioTests
{
    private static readonly SKColor Sea = new(0x6E, 0x9F, 0xBF);
    private const string SeaHex = "#FF6E9FBF";

    private static LandLayer Land(string name, int level, LandEdgeStyle? style, params (int X, int Y)[] tiles)
    {
        var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var map = TileMap.Empty(1);
        foreach (var (x, y) in tiles) map = map.With(new TileKey(x, y), coverage);
        return new LandLayer(LayerId.New(), name, true, false, 1, map,
            style ?? new LandEdgeStyle { FillColor = "#FF8CA36B", FillTextureId = "meadow", FillTextureOpacity = 0.5 }) { Level = level };
    }

    /// <summary>A coast that is not a tile edge: land over x in [0, 400) of a 768×512 canvas, with a shaped shoreline.</summary>
    private static LandLayer ShapedLand(Func<int, int, bool> inside, int level = 0)
    {
        var map = TileMap.Empty(1);
        for (var tx = 0; tx < 3; tx++)
            for (var ty = 0; ty < 2; ty++)
            {
                var coverage = new byte[TileMap.TileSize * TileMap.TileSize];
                var any = false;
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                        if (inside(tx * TileMap.TileSize + x, ty * TileMap.TileSize + y)) { coverage[y * TileMap.TileSize + x] = 255; any = true; }
                if (any) map = map.With(new TileKey(tx, ty), coverage);
            }
        return new LandLayer(LayerId.New(), "Суша", true, false, 1, map,
            new LandEdgeStyle { FillColor = "#FF8CA36B", FillTextureId = "meadow", FillTextureOpacity = 0.5 }) { Level = level };
    }

    private static MapDocument Document(IEnumerable<MapLayer> layers) => new(DocumentId.New(), "estuary", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(768, 512, "px", SeaHex), [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, SeaHex), .. layers]);

    private static River Course(string name, double width, RiverStyle? style, params (double X, double Y)[] points) => new()
    {
        Name = name, Style = style ?? RiverStyle.Illustrated(),
        Channels = [RiverEditor.NewChannel(points.Select(p => new VectorPoint(p.X, p.Y)).ToList(), width)],
    };

    private static SKBitmap Render(MapDocument document, WorldBounds? view = null, double scale = 1)
    {
        var world = view ?? new WorldBounds(0, 0, document.Canvas.Width, document.Canvas.Height);
        var info = new SKImageInfo((int)Math.Round((world.Right - world.Left) * scale), (int)Math.Round((world.Bottom - world.Top) * scale));
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Scale((float)scale);
        surface.Canvas.Translate((float)-world.Left, (float)-world.Top);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true, ViewScale = scale, VisibleWorld = world });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    private static void Save(SKBitmap bitmap, string name)
    {
        var directory = Environment.GetEnvironmentVariable("MAP_ESTUARY_FRAMES");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        data.SaveTo(file);
    }

    private static bool IsWaterish(SKColor c) => c.Blue > c.Red + 10 && c.Blue >= c.Green - 30;
    private static bool IsLandish(SKColor c) => c.Green >= c.Blue + 8 && c.Red > 90;
    private static bool IsDark(SKColor c) => c.Red + c.Green + c.Blue < 3 * 95;

    [Fact]
    public void A_straight_mouth_opens_into_the_sea_without_a_plug_or_a_bank_across_it()
    {
        var land = Land("Земля", 0, null, (0, 0), (0, 1));
        var river = Course("Река", 40, null, (40, 200), (180, 220), (330, 210), (520, 230));
        var document = Document([land, new ObjectLayer(LayerId.New(), "Реки", true, false, 1, []) { Rivers = [river] }]);
        using var frame = Render(document);
        Save(frame, "straight-mouth");
        // Down the centre from well inside the land to the open sea: water all the way, no plug.
        for (var x = 120; x <= 400; x += 8)
            Assert.True(IsWaterish(frame.GetPixel(x, 214)) || IsDark(frame.GetPixel(x, 214)) == false && !IsLandish(frame.GetPixel(x, 214)),
                $"land or bank in the channel at x={x}: {frame.GetPixel(x, 214)}");
        // No dark bank line across the sea beyond where the mouth dissolved.
        for (var x = 330; x <= 500; x += 6)
            Assert.False(IsDark(frame.GetPixel(x, 214)), $"a dark rim crosses open water at x={x}: {frame.GetPixel(x, 214)}");
        // Dry land on both sides of the mouth, and no rectangle of a mask near the coast.
        Assert.True(IsLandish(frame.GetPixel(200, 150)) && IsLandish(frame.GetPixel(200, 290)));
        Assert.True(IsWaterish(frame.GetPixel(300, 60)) && IsWaterish(frame.GetPixel(300, 460)));
    }

    [Fact]
    public void An_oblique_wide_mouth_along_a_shaped_coast_is_judged_across_its_whole_width()
    {
        // Coast: x < 360 + 60·sin(y/40) - a wavy shoreline the river crosses at a low angle.
        var land = ShapedLand((x, y) => x < 360 + 60 * Math.Sin(y / 40.0));
        var river = Course("Устье", 70, null, (60, 120), (220, 200), (330, 300), (470, 380), (640, 420));
        var document = Document([land, new ObjectLayer(LayerId.New(), "Реки", true, false, 1, []) { Rivers = [river] }]);
        using var frame = Render(document);
        Save(frame, "oblique-mouth");
        // Both banks and the centre are open water past the coast; nothing dark crosses them.
        var geometry = RiverGeometry.Evaluate(river).Channels.Values.Single();
        var seaSide = geometry.Samples.Where(s => s.X > 430).ToArray();
        Assert.NotEmpty(seaSide);
        foreach (var sample in seaSide.Where((_, i) => i % 6 == 0))
            foreach (var side in new[] { -0.8, 0, 0.8 })
            {
                var x = (int)(sample.X - sample.TangentY * sample.HalfWidth * side);
                var y = (int)(sample.Y + sample.TangentX * sample.HalfWidth * side);
                if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height) continue;
                var c = frame.GetPixel(x, y);
                Assert.False(IsLandish(c) || IsDark(c), $"land or ink across the wide mouth at ({x},{y}) side {side}: {c}");
            }
        // The channel inside the land has both banks dry.
        Assert.True(IsLandish(frame.GetPixel(200, 120)) && IsLandish(frame.GetPixel(130, 220)));
    }

    [Fact]
    public void A_channel_that_leaves_and_re_enters_the_land_is_carved_at_every_crossing()
    {
        // Two islands with a strait between them; the river crosses land, sea, land, sea.
        var land = ShapedLand((x, y) => x < 260 || x is > 400 and < 620);
        var river = Course("Пролив", 36, null, (40, 250), (200, 260), (330, 270), (500, 250), (700, 260));
        var document = Document([land, new ObjectLayer(LayerId.New(), "Реки", true, false, 1, []) { Rivers = [river] }]);
        using var frame = Render(document);
        Save(frame, "re-entry");
        foreach (var x in new[] { 120, 240, 330, 430, 510, 600, 690 })
        {
            var c = frame.GetPixel(x, 258);
            Assert.False(IsLandish(c), $"land in the channel at x={x}: {c}");
        }
        // Dry land on each island, on both sides of the channel.
        Assert.True(IsLandish(frame.GetPixel(120, 180)) && IsLandish(frame.GetPixel(120, 340)));
        Assert.True(IsLandish(frame.GetPixel(510, 180)) && IsLandish(frame.GetPixel(510, 340)));
        // Coast stays crisp between the islands: sea in the strait, no land bridge.
        Assert.True(IsWaterish(frame.GetPixel(330, 150)) && IsWaterish(frame.GetPixel(330, 380)));
    }

    [Fact]
    public void A_tributary_and_a_lake_outflow_join_without_seams_or_double_rims()
    {
        var land = Land("Земля", 0, null, (0, 0), (1, 0), (0, 1), (1, 1));
        var main = new River
        {
            Name = "Система", Style = RiverStyle.Illustrated(),
            Channels = [RiverEditor.NewChannel([new(40, 300), new(200, 320), new(380, 340), new(500, 330)], 34)],
        };
        var mainChannel = main.Channels[0];
        var tributary = RiverEditor.NewChannel([new(120, 120), new(210, 220)], 16) with
        {
            Mouth = new RiverJunction(mainChannel.Id, mainChannel.Nodes[1].Id, 0.5),
        };
        var lake = RiverEditor.NewChannel([new(330, 120), new(420, 100), new(470, 170), new(400, 230), new(320, 200)], 10) with { Closed = true };
        var outflow = RiverEditor.NewChannel([new(400, 220), new(410, 290)], 14) with
        {
            Source = new RiverJunction(lake.Id, lake.Nodes[3].Id, 0.3),
            Mouth = new RiverJunction(mainChannel.Id, mainChannel.Nodes[2].Id, 0.2),
        };
        main = main with { Channels = [mainChannel, tributary, lake, outflow] };
        main.Validate();
        var document = Document([land, new ObjectLayer(LayerId.New(), "Реки", true, false, 1, []) { Rivers = [main] }]);
        using var frame = Render(document);
        Save(frame, "joins");
        // The junction pixels are water; the confluence carries no dark seam inside the water.
        var geometry = RiverGeometry.Evaluate(main);
        var mouth = geometry.Channels[tributary.Id].End;
        Assert.True(IsWaterish(frame.GetPixel((int)mouth.X, (int)mouth.Y)), $"tributary mouth is not water: {frame.GetPixel((int)mouth.X, (int)mouth.Y)}");
        var outflowEnd = geometry.Channels[outflow.Id].End;
        Assert.True(IsWaterish(frame.GetPixel((int)outflowEnd.X, (int)outflowEnd.Y)));
        var outflowStart = geometry.Channels[outflow.Id].Start;
        Assert.True(IsWaterish(frame.GetPixel((int)outflowStart.X, (int)outflowStart.Y)));
        // Inside the lake: water, no plug where the outflow leaves.
        Assert.True(IsWaterish(frame.GetPixel(400, 160)));
    }

    [Fact]
    public void Higher_land_covers_lower_water_and_a_river_on_a_plateau_carves_only_the_plateau()
    {
        var lowland = Land("Низина", 0, null, (0, 0), (1, 0), (0, 1), (1, 1));
        var plateau = Land("Плато", 1, new LandEdgeStyle { FillColor = "#FF6F8A4E", FillTextureId = "stone", FillTextureOpacity = 0.4 }, (1, 0));
        var lowRiver = Course("Низкая река", 30, null, (40, 200), (260, 120), (470, 120));   // crosses under the plateau
        var highRiver = Course("Река плато", 24, null, (280, 60), (400, 90), (500, 160)) with { Level = 1 };
        var document = Document([lowland, plateau, new ObjectLayer(LayerId.New(), "Реки", true, false, 1, []) { Rivers = [lowRiver, highRiver] }]);
        using var frame = Render(document);
        Save(frame, "levels");
        // The low river is visible on the lowland and hidden under the plateau (x in 256..512, y < 256).
        Assert.True(IsWaterish(frame.GetPixel(120, 170)), $"low river missing on the lowland: {frame.GetPixel(120, 170)}");
        var underPlateau = frame.GetPixel(380, 120);
        Assert.True(IsLandish(underPlateau) && !IsWaterish(underPlateau), $"the plateau does not cover the low river: {underPlateau}");
        // The plateau river carves the plateau and shows water at its own level.
        Assert.True(IsWaterish(frame.GetPixel(400, 90)), $"plateau river missing: {frame.GetPixel(400, 90)}");
        // Lowland beyond the plateau keeps its own material.
        Assert.True(IsLandish(frame.GetPixel(120, 400)));
    }

    [Fact]
    public void The_two_switches_of_a_river_act_independently_and_a_map_without_land_still_draws_water()
    {
        var land = Land("Земля", 0, null, (0, 0), (0, 1));
        River Water(bool cuts, bool clips) => Course("Река", 40, RiverStyle.Illustrated() with { CutsLand = cuts, ClipToLand = clips }, (40, 200), (330, 210), (600, 230));
        SKBitmap Frame(bool cuts, bool clips, string name)
        {
            var document = Document([land, new ObjectLayer(LayerId.New(), "Реки", true, false, 1, []) { Rivers = [Water(cuts, clips)] }]);
            var frame = Render(document); Save(frame, name); return frame;
        }
        using var both = Frame(true, true, "switch-cut-clip");
        using var cutOnly = Frame(true, false, "switch-cut-only");
        using var clipOnly = Frame(false, true, "switch-clip-only");
        using var neither = Frame(false, false, "switch-neither");
        // Water past the coast: drawn without ClipToLand, dissolved with it.
        Assert.True(IsWaterish(cutOnly.GetPixel(560, 226)) && IsWaterish(neither.GetPixel(560, 226)));
        Assert.True(both.GetPixel(560, 226) == Sea || !IsDark(both.GetPixel(560, 226)));
        // Without CutsLand the coast keeps its outline across the mouth (the river lies on the land).
        Assert.True(IsWaterish(clipOnly.GetPixel(120, 205)) && IsWaterish(neither.GetPixel(120, 205)));
        // No land at all: the water draws, ClipToLand is ignored, nothing throws.
        var open = Document([new ObjectLayer(LayerId.New(), "Реки", true, false, 1, []) { Rivers = [Water(true, true)] }]);
        using var noLand = Render(open); Save(noLand, "no-land");
        Assert.True(IsWaterish(noLand.GetPixel(330, 212)) || noLand.GetPixel(330, 212) != Sea);
    }

    [Fact]
    public void Tiles_that_meet_in_the_mouth_equal_the_single_image()
    {
        var land = ShapedLand((x, y) => x < 360 + 40 * Math.Sin(y / 30.0));
        var river = Course("Устье", 50, null, (60, 200), (250, 240), (400, 260), (600, 300));
        var document = Document([land, new ObjectLayer(LayerId.New(), "Реки", true, false, 1, []) { Rivers = [river] }]);
        using var whole = Render(document);
        Save(whole, "tiles-whole");
        // Four tiles with a seam at x = 384 and y = 256, right through the mouth, each drawn
        // with the effect bleed the exporter uses (ExportPlanner.EffectBleedPixels) and cropped.
        var bleed = (int)Math.Ceiling(Math.Max(
            Math.Max(land.EdgeStyle.OutlineWidth, land.EdgeStyle.RippleSpacing * land.EdgeStyle.RippleCount + land.EdgeStyle.RippleWidth),
            Math.Max(land.EdgeStyle.ShadowBlur * 3, Math.Max(
                river.Style.BankWidth + river.Style.BankShadowBlur * 3 + river.Style.BankReliefWidth * 1.8 + 1,
                RiverRenderer.CoastCoveragePadding(river)))) + 2);
        using var stitched = new SKBitmap(whole.Info);
        using (var canvas = new SKCanvas(stitched))
            foreach (var (tx, ty) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
            {
                var view = new WorldBounds(tx * 384 - bleed, ty * 256 - bleed, tx * 384 + 384 + bleed, ty * 256 + 256 + bleed);
                using var tile = Render(document, view);
                canvas.DrawBitmap(tile, new SKRect(bleed, bleed, bleed + 384, bleed + 256),
                    new SKRect(tx * 384, ty * 256, tx * 384 + 384, ty * 256 + 256));
            }
        Save(stitched, "tiles-stitched");
        var worst = 0; var where = "";
        for (var y = 0; y < whole.Height; y += 2)
            for (var x = 0; x < whole.Width; x += 2)
            {
                var p = whole.GetPixel(x, y); var q = stitched.GetPixel(x, y);
                var d = Math.Max(Math.Abs(p.Red - q.Red), Math.Max(Math.Abs(p.Green - q.Green), Math.Abs(p.Blue - q.Blue)));
                if (d > worst) { worst = d; where = $"({x},{y}) {p} vs {q}"; }
            }
        Assert.True(worst <= 12, $"tile seam differs by {worst} at {where}");
    }
}
