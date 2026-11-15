using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Resources;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>Opt-in CPU benchmark, also runnable unchanged against the original renderer.</summary>
public sealed class CoverageCacheBenchmark
{
    internal static MapDocument Fixture(bool huge)
    {
        var coverage = new byte[256 * 256];
        var relief = new byte[coverage.Length];
        for (var y = 0; y < 256; y++)
        for (var x = 0; x < 256; x++)
        {
            var radius = Math.Sqrt((x - 128) * (x - 128) + (y - 128) * (y - 128));
            coverage[y * 256 + x] = (byte)Math.Clamp((110 - radius) * 32 + 128, 0, 255);
            relief[y * 256 + x] = (byte)Math.Clamp(180 - radius, 0, 255);
        }
        var layers = new List<MapLayer>
        {
            new BaseColorLayer(LayerId.New(), "sea", true, false, 1, "#FF9DBBC4")
                { Sea = SeaStyle.For(SeaManner.DepthBands) },
        };
        for (var i = 0; i < 3; i++)
        {
            var keys = new HashSet<TileKey>();
            if (huge)
            {
                // One painted tile in every coarse block: all 1024 blocks/layer are exercised
                // without allocating tens of GiB of document pixels on the shared Mac.
                for (var by = 0; by < 32; by++)
                for (var bx = 0; bx < 32; bx++) keys.Add(new TileKey(bx * 16 + i, by * 16 + i));
                for (var y = 247; y < 263; y++)
                for (var x = 247; x < 267; x++) keys.Add(new TileKey(x, y));
            }
            else
            {
                keys.Add(new TileKey(i % 2, i / 2));
                keys.Add(new TileKey(1, 1));
            }
            var changes = keys.Select(key => new KeyValuePair<TileKey, byte[]?>(key, coverage));
            var heights = keys.Select(key => new KeyValuePair<TileKey, byte[]?>(key, relief));
            layers.Add(new LandLayer(LayerId.New(), "land", true, false, i == 1 ? .7 : 1,
                TileMap.Empty(1).WithMany(changes), new LandEdgeStyle
                {
                    FillColor = i == 1 ? "#FFCBD4A1" : "#FFD6CFA8", FillTextureId = null,
                    OutlineWidth = 2, RippleCount = 2, ShadowBlur = 4,
                    OuterShadowOpacity = .2, InnerShadowOpacity = .15,
                    ReliefShading = .7, ReliefContours = .5, ReliefSteepInk = .2,
                }) { ReliefTiles = TileMap.Empty(1).WithMany(heights) });
        }
        var size = huge ? 131072 : 512;
        return new MapDocument(DocumentId.New(), "coverage fixture", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(size, size, "px", "#FF9DBBC4"), layers);
    }

    internal static byte[] Render(MapDocument document, WorldBounds view, double scale, TileImageCache cache,
        RenderLodTier tier = RenderLodTier.Exact)
    {
        var info = new SKImageInfo(640, 360, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale((float)scale);
        surface.Canvas.Translate((float)-view.Left, (float)-view.Top);
        cache.BeginFrame();
        try
        {
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                { VisibleWorld = view, ViewScale = scale, TileCache = cache, LodTier = tier });
            surface.Canvas.Flush();
        }
        finally { cache.EndFrame(); }
        using var bitmap = new SKBitmap(info);
        Assert.True(surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0));
        return bitmap.GetPixelSpan().ToArray();
    }

    [OptInProbe("MAP_COVERAGE_BENCH", "CoverageCacheTests.Cached_sea_relief_and_coast_are_byte_identical_to_uncached_renders")]
    public void Huge_canvas_fit_and_quarter_scale_pan_cpu()
    {
        var output = Environment.GetEnvironmentVariable("MAP_COVERAGE_BENCH")!;
        var document = Fixture(huge: true);
        using var tiles = new TileImageCache(8192, 512L * WorkingMemoryBudget.MiB);
        var fit = 360.0 / 131072;
        var route = new List<(string Name, WorldBounds View, double Scale, RenderLodTier Tier)>();
        var whole = new WorldBounds(0, 0, 131072, 131072);
        for (var i = 0; i < 4; i++) route.Add(("fit-" + i, whole, fit, RenderLodTier.Exact));
        for (var lap = 0; lap < 2; lap++)
        for (var step = 0; step < 5; step++)
        {
            var left = 64000 + step * 256;
            route.Add(($"pan-{lap}-{step}", new WorldBounds(left, 64000, left + 2560, 65440), .25, RenderLodTier.Exact));
        }
        for (var i = 0; i < 3; i++) route.Add(("overview-" + i, whole, fit, RenderLodTier.Overview));
        var lines = new List<string> { "131072x131072; 3 land layers, sea + relief; CPU 640x360; sparse global blocks + dense pan region" };
        foreach (var (name, view, scale, tier) in route)
        {
            var allocated = GC.GetTotalAllocatedBytes(true);
            var started = Stopwatch.GetTimestamp();
            var pixels = Render(document, view, scale, tiles, tier);
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            lines.Add(FormattableString.Invariant($"{name} ms={ms:F3} allocated={GC.GetTotalAllocatedBytes(true) - allocated} sha256={Convert.ToHexString(SHA256.HashData(pixels))}"));
        }
        foreach (var stats in WorkingMemoryBudget.Shared.ReadCacheStatistics()) lines.Add(stats.ToString());
        File.WriteAllLines(output, lines);
    }

    [OptInProbe("MAP_COVERAGE_PIXELS", "CoverageCacheTests.Cached_sea_relief_and_coast_are_byte_identical_to_uncached_renders")]
    public void Pixel_fixtures_against_original_renderer()
    {
        var output = Environment.GetEnvironmentVariable("MAP_COVERAGE_PIXELS")!;
        var document = Fixture(huge: false);
        using var tiles = new TileImageCache();
        var lines = new List<string>();
        for (var variant = 0; variant < 6; variant++)
        {
            var lands = document.Layers.OfType<LandLayer>().ToArray();
            var changed = lands[0];
            if (variant == 1) changed = changed with { GeometryTiles = changed.GeometryTiles.With(new TileKey(0, 0), new byte[65536]) };
            if (variant == 2) changed = changed with { ReliefTiles = changed.ReliefTiles.With(new TileKey(0, 0), new byte[65536]) };
            if (variant == 3) changed = changed with { Opacity = .35 };
            if (variant == 4) changed = changed with { Mask = new LayerMask(changed.GeometryTiles, true) };
            var layers = document.Layers.Select(layer => layer.Id == changed.Id ? changed : layer).ToArray();
            if (variant == 5) layers[0] = ((BaseColorLayer)layers[0]) with { Sea = SeaStyle.For(SeaManner.Surf) with { Scale = 1.7 } };
            var scene = document with { Layers = layers };
            foreach (var scale in new[] { .0625, .25, .251, 1.0, 1.75 })
            {
                var view = new WorldBounds(17.25, 23.5, 512, 512);
                var first = Render(scene, view, scale, tiles);
                var warm = Render(scene, view, scale, tiles);
                Assert.Equal(first, warm);
                lines.Add($"{variant}/{scale.ToString(CultureInfo.InvariantCulture)} {Convert.ToHexString(SHA256.HashData(first))}");
            }
        }
        File.WriteAllLines(output, lines);
    }
}
