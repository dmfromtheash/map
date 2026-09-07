using System.Text.Json;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class Stage19RiverRenderingTests
{
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "map-river-render-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static MapDocument Fixture(out River river, out RiverChannel main, out RiverChannel tributary, double opacity = 1)
    {
        main = new RiverChannel
        {
            Name = "Главное",
            Nodes = [new(Guid.NewGuid(), 30, 60, 6), new(Guid.NewGuid(), 200, 150, 16), new(Guid.NewGuid(), 330, 110, 24), new(Guid.NewGuid(), 490, 200, 34)],
        };
        tributary = new RiverChannel
        {
            Name = "Приток",
            Nodes = [new(Guid.NewGuid(), 240, 20, 4), new(Guid.NewGuid(), 270, 70, 9)],
            // Straddles the 256-px tile boundary of the export below.
            Mouth = new RiverJunction(main.Id, main.Nodes[1].Id, 0.45),
        };
        river = new River { Name = "Река", Channels = [main, tributary], Style = new RiverStyle { Opacity = opacity } };
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Rivers = [river] };
        return new MapDocument(DocumentId.New(), "River render", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 256, "px", "#00000000"),
            [new BaseColorLayer(LayerId.New(), "Transparent", true, false, 0, "#00000000"), layer]);
    }

    private static SKBitmap Render(MapDocument doc, double scale, bool export = false, IReadOnlyList<River>? overrides = null)
    {
        using var surface = SKSurface.Create(new SKImageInfo(512, 256, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.Scale((float)scale);
        using var cache = new ObjectOverviewCache();
        SceneRenderer.Render(surface.Canvas, doc, new RenderOptions
        {
            ViewScale = scale, VisibleWorld = new(0, 0, 512 / scale, 256 / scale),
            LodTier = scale < .03 ? RenderLodTier.Overview : RenderLodTier.Exact,
            ObjectCache = cache, IsExport = export, IncludeBaseColor = false, IncludeExportGrids = false,
            RiverOverrides = overrides,
        });
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return SKBitmap.Decode(data);
    }

    [Fact]
    public void Junction_has_no_gap_and_no_double_opacity_and_far_zoom_keeps_a_thread()
    {
        var document = Fixture(out var river, out var main, out var tributary, opacity: 0.6);
        using var bitmap = Render(document, 1);
        SaveForReview(bitmap, "junction.png");
        var set = RiverGeometry.Evaluate(river);
        var parent = set.Channels[main.Id]; var child = set.Channels[tributary.Id];
        var interior = parent.Samples[parent.NodeSampleIndex[2]];
        var plain = bitmap.GetPixel((int)interior.X, (int)interior.Y);
        Assert.True(plain.Alpha is > 100 and < 200, $"60 % water reads as alpha {plain.Alpha}");
        var mouth = child.End;
        var junction = bitmap.GetPixel((int)mouth.X, (int)mouth.Y);
        Assert.InRange(Math.Abs(junction.Alpha - plain.Alpha), 0, 8);
        // Walk the tributary centreline into the parent: every pixel is water, none is background.
        for (var i = Math.Max(0, child.Samples.Length - 12); i < child.Samples.Length; i++)
        {
            var s = child.Samples[i];
            Assert.True(bitmap.GetPixel((int)s.X, (int)s.Y).Alpha > 60, $"gap at tributary sample {i}");
        }
        using var far = Render(document, 0.02);
        Assert.Contains(far.Pixels, p => p.Alpha > 0);
        Assert.True(RiverGeometry.Contains(river, mouth.X, mouth.Y));
        Assert.False(RiverGeometry.Contains(river, 20, 240));
    }

    [Fact]
    public async Task Export_tiles_match_the_single_image_across_the_junction_boundary()
    {
        using var temp = new TempDirectory();
        var document = Fixture(out _, out _, out _);
        using var stamps = new ExportStampImageSource(document, null, null);
        var engine = new ImageExportEngine(new ExportEngineSettings { TileEdge = 256 });
        ExportRequest Request(ExportOutputMode mode) => new(ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null, 512, 256, mode, true, false);
        var png = Path.Combine(temp.Path, "river.png"); var tiles = Path.Combine(temp.Path, "tiles");
        await engine.ExportAsync(document, engine.Plan(document, Request(ExportOutputMode.SingleImage)), png, stamps);
        await engine.ExportAsync(document, engine.Plan(document, Request(ExportOutputMode.TilePackage)), tiles, stamps);
        using var actual = SKBitmap.Decode(png);
        using var viewport = Render(document, 1);
        SaveForReview(viewport, "export-viewport.png"); SaveForReview(actual, "export-single.png");
        AssertNearlyEqual(viewport, actual, "viewport vs single image", exact: false);
        Assert.Contains(Enumerable.Range(100, 120), y => actual.GetPixel(255, y).Alpha > 0 && actual.GetPixel(256, y).Alpha > 0);
        var manifest = JsonSerializer.Deserialize<TilePackageManifest>(File.ReadAllText(Path.Combine(tiles, "manifest.json")))!;
        foreach (var tile in manifest.Tiles)
        {
            using var tileBitmap = SKBitmap.Decode(Path.Combine(tiles, tile.File));
            using var expected = new SKBitmap(tile.Width, tile.Height);
            for (var y = 0; y < tile.Height; y++)
            for (var x = 0; x < tile.Width; x++)
                expected.SetPixel(x, y, actual.GetPixel(tile.X + x, tile.Y + y));
            AssertNearlyEqual(expected, tileBitmap, tile.File, exact: true);
        }
    }

    [Fact]
    public void Override_and_draft_preview_replace_or_add_a_river_without_touching_the_document()
    {
        var document = Fixture(out var river, out var main, out _);
        var layer = document.Layers.OfType<ObjectLayer>().Single();
        var wider = river with { WidthScale = 2 };
        using var plain = Render(document, 1);
        using var overridden = Render(document, 1, overrides: [wider]);
        Assert.NotEqual(plain.Pixels, overridden.Pixels);
        Assert.Same(river, layer.Rivers[0]);
        var draft = new River { Channels = [RiverEditor.NewChannel([new(20, 230), new(200, 235)], 10)] };
        using var surface = SKSurface.Create(new SKImageInfo(512, 256, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        surface.Canvas.Clear(SKColors.Transparent);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions
        {
            ViewScale = 1, VisibleWorld = new(0, 0, 512, 256), IncludeBaseColor = false,
            RiverOverrides = [draft], RiverPreviewLayerId = layer.Id,
        });
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var withDraft = SKBitmap.Decode(data);
        Assert.True(withDraft.GetPixel(100, 232).Alpha > 0);
        Assert.Equal(0, plain.GetPixel(100, 232).Alpha);
    }

    /// <summary>
    /// Export tiles are rasterized on their own surfaces, where the same world coordinate lands on
    /// a different device coordinate; Skia's float-to-fixed edge setup then rounds antialiased
    /// coverage slightly differently. Only pixels that are not interior (a neighbour differs, or the
    /// pixel is translucent) may differ, by at most 64 premultiplied units, on no more than 0.5 %
    /// of the image; interior water and bank pixels match exactly. Tiles and the assembled single
    /// image share one rasterization and must match exactly.
    /// </summary>
    private static MapDocument LandFixture(out River river, RiverStyle style)
    {
        var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var land = new LandLayer(LayerId.New(), "Земля", true, false, 1, TileMap.Empty(1).With(new TileKey(0, 0), coverage),
            new LandEdgeStyle { OutlineWidth = 4, OutlineColor = "#FF102030", FillColor = "#FF8CA36B", InnerShadowOpacity = 0, OuterShadowOpacity = 0, RippleCount = 0 });
        var main = new RiverChannel { Nodes = [new(Guid.NewGuid(), 40, 128, 20), new(Guid.NewGuid(), 200, 128, 20), new(Guid.NewGuid(), 400, 128, 20)] };
        river = new River { Channels = [main], Style = style with { TaperSource = false, BankWidth = 0, BankShadowOpacity = 0, HighlightOpacity = 0 } };
        var layer = new ObjectLayer(LayerId.New(), "Реки", true, false, 1, []) { Rivers = [river] };
        return new MapDocument(DocumentId.New(), "River land", DateTimeOffset.UnixEpoch, new CanvasDefinition(512, 256, "px", "#00000000"),
            [new BaseColorLayer(LayerId.New(), "Transparent", true, false, 0, "#00000000"), land, layer]);
    }

    [Fact]
    public void River_carves_the_land_so_the_coast_outline_wraps_its_banks_and_can_stop_at_the_shore()
    {
        var outline = new SKColor(0x10, 0x20, 0x30); var fill = new SKColor(0x8C, 0xA3, 0x6B);
        // An almost invisible river still carves: under it the land is gone and the coast outline
        // now runs along the banks (inside the notch, where the water normally covers it).
        using var carved = Render(LandFixture(out _, new RiverStyle { CutsLand = true, ClipToLand = false, Opacity = 0.02 }), 1);
        SaveForReview(carved, "land-cut.png");
        using var plain = Render(LandFixture(out _, new RiverStyle { CutsLand = false, ClipToLand = false, Opacity = 0.02 }), 1);
        Assert.True(carved.GetPixel(120, 128).Alpha < 16, "land under the river must be carved away");
        var under = plain.GetPixel(120, 128);
        Assert.True(under.Alpha == 255 && Math.Abs(under.Red - fill.Red) < 6 && Math.Abs(under.Green - fill.Green) < 6 && Math.Abs(under.Blue - fill.Blue) < 6,
            $"land stays under a river that does not carve, got {under}");
        var ring = carved.GetPixel(120, 128 - 8);
        Assert.True(Math.Abs(ring.Red - outline.Red) < 24 && Math.Abs(ring.Green - outline.Green) < 24 && Math.Abs(ring.Blue - outline.Blue) < 24,
            $"coast outline should hug the bank, got {ring}");
        var beside = plain.GetPixel(120, 128 - 8);
        Assert.True(Math.Abs(beside.Red - fill.Red) < 6 && Math.Abs(beside.Green - fill.Green) < 6 && Math.Abs(beside.Blue - fill.Blue) < 6, $"no ring without carving, got {beside}");
        using var visible = Render(LandFixture(out _, new RiverStyle { CutsLand = true, ClipToLand = false }), 1);
        Assert.Equal(new SKColor(0x4E, 0x86, 0xA3), visible.GetPixel(120, 128));
        Assert.True(visible.GetPixel(350, 128).Alpha > 0, "without clipping the river keeps flowing over the sea");
        using var clipped = Render(LandFixture(out _, new RiverStyle { CutsLand = true, ClipToLand = true }), 1);
        SaveForReview(clipped, "land-clip.png");
        Assert.Equal(0, clipped.GetPixel(350, 128).Alpha);
        Assert.True(clipped.GetPixel(120, 128).Alpha == 255);
    }

    [Fact]
    public void Rough_banks_stay_deterministic_and_lakes_join_the_same_body()
    {
        var lake = RiverEditor.NewLake([new(300, 60), new(420, 40), new(470, 130), new(380, 210), new(290, 170)]);
        var inflow = RiverEditor.NewChannel([new(60, 40), new(200, 90)], 10) with { Mouth = new RiverJunction(lake.Id, lake.Nodes[4].Id, 0.5) };
        var river = new River { Channels = [lake, inflow], Style = new RiverStyle { EdgeRoughness = 0.6, Opacity = 0.6 } };
        var layer = new ObjectLayer(LayerId.New(), "Реки", true, false, 1, []) { Rivers = [river] };
        var document = new MapDocument(DocumentId.New(), "Lake", DateTimeOffset.UnixEpoch, new CanvasDefinition(512, 256, "px", "#00000000"),
            [new BaseColorLayer(LayerId.New(), "Transparent", true, false, 0, "#00000000"), layer]);
        using var first = Render(document, 1); using var second = Render(document, 1);
        SaveForReview(first, "lake.png");
        Assert.Equal(first.Pixels, second.Pixels);
        var inside = first.GetPixel(380, 120); var inflowGeometry = RiverGeometry.Evaluate(river).Channels[inflow.Id];
        var upstream = inflowGeometry.Samples[^6];
        Assert.True(inside.Alpha is > 100 and < 200);
        // Water just before the shore and water inside the lake carry the same single opacity: no seam.
        Assert.InRange(Math.Abs(first.GetPixel((int)upstream.X, (int)upstream.Y).Alpha - inside.Alpha), 0, 8);
    }

    private static void AssertNearlyEqual(SKBitmap expected, SKBitmap actual, string what, bool exact)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        var soft = 0; var hard = new List<string>();
        static (int R, int G, int B, int A) Premultiplied(SKColor c) =>
            ((c.Red * c.Alpha + 127) / 255, (c.Green * c.Alpha + 127) / 255, (c.Blue * c.Alpha + 127) / 255, c.Alpha);
        static int Delta(SKColor a, SKColor b)
        {
            var (ar, ag, ab, aa) = Premultiplied(a); var (br, bg, bb, ba) = Premultiplied(b);
            return Math.Max(Math.Max(Math.Abs(ar - br), Math.Abs(ag - bg)), Math.Max(Math.Abs(ab - bb), Math.Abs(aa - ba)));
        }
        bool Interior(int x, int y)
        {
            var centre = expected.GetPixel(x, y);
            if (centre.Alpha < 255) return false;
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var nx = x + dx; var ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= expected.Width || ny >= expected.Height) return false;
                if (expected.GetPixel(nx, ny) != centre) return false;
            }
            return true;
        }
        for (var y = 0; y < expected.Height; y++)
        for (var x = 0; x < expected.Width; x++)
        {
            var a = expected.GetPixel(x, y); var b = actual.GetPixel(x, y);
            if (a == b) continue;
            if (!exact && Delta(a, b) <= 64 && !Interior(x, y)) soft++;
            else hard.Add($"({x},{y}):{a}/{b}");
        }
        Assert.True(hard.Count == 0, $"{what}: {hard.Count} pixels differ beyond the edge tolerance, first {string.Join(" ", hard.Take(6))}");
        Assert.True(soft <= expected.Width * expected.Height / 200, $"{what}: {soft} edge pixels differ, more than 0.5 %");
    }

    private static void SaveForReview(SKBitmap bitmap, string name)
    {
        var directory = Environment.GetEnvironmentVariable("MAP_STAGE19_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(Path.Combine(directory, name)); data.SaveTo(file);
    }
}
