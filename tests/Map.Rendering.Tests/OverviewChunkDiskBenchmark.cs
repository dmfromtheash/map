using System.Diagnostics;
using Map.Core.Documents;
using SkiaSharp;
using Xunit.Abstractions;

namespace Map.Rendering.Tests;

/// <summary>
/// Headless measurement (opt-in, MAP_HOT_OVERVIEW_BENCH=1): time from the first frame to a complete whole-map (fit)
/// view - every visible overview chunk adopted, nothing pending - on a first and a second open of the same map,
/// without the disk tier (as before) and with it. 131 072 world units (16 x 16 chunks), the owner's mountains 01-06,
/// every third painted with a shadow. Also: load time of one 512-pixel chunk file (raw) against a PNG decode.
/// </summary>
[Collection(RealTimeSettleCollection.Name)]
public sealed class OverviewChunkDiskBenchmark(ITestOutputHelper output) : IDisposable
{
    private const double Extent = 131_072;
    private const double Scale = 1024 / Extent;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-overview-bench", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [OptInFact("hot overview benchmark", "MAP_HOT_OVERVIEW_BENCH")]
    public void Second_open_of_100k_and_1m_maps()
    {
        ChunkLoad();
        // MAP_HOT_OVERVIEW_COUNTS=100000 limits the sizes (comma-separated).
        var counts = Environment.GetEnvironmentVariable("MAP_HOT_OVERVIEW_COUNTS") is { Length: > 0 } list
            ? list.Split(',').Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray()
            : [100_000, 1_000_000];
        foreach (var count in counts)
        {
            var objects = Objects(count);
            var root = Path.Combine(_root, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Open(objects, null, "warm-up JIT", log: false);
            var before = new List<double>(); var cold = new List<double>(); var hot = new List<double>();
            for (var run = 0; run < 3; run++)
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                before.Add(Open(objects, null, $"{count} before (no disk), run {run}"));
                cold.Add(Open(objects, root, $"{count} first open with disk, run {run}"));
                hot.Add(Open(objects, root, $"{count} second open with disk, run {run}"));
            }
            var files = Directory.GetFiles(Path.Combine(root, "v1"), "*.chunk");
            var bytes = files.Sum(file => new FileInfo(file).Length);
            output.WriteLine($"RESULT {count}: before median={Median(before):F0} ms; first open with disk median={Median(cold):F0} ms; " +
                $"second open median={Median(hot):F0} ms; disk {files.Length} files, {bytes / 1024.0 / 1024:F2} MiB");
        }
    }

    private static double Median(List<double> values) { values.Sort(); return values[values.Count / 2]; }

    private double Open(SceneObject[] objects, string? root, string phase, bool log = true)
    {
        var store = root is null ? null : new OverviewChunkDiskCache(root);
        using var source = new OverviewChunkDiskTests.Mountains(store, identity: true, fixtures: 6);
        var document = new MapDocument(DocumentId.New(), "hot overview bench", DateTimeOffset.UnixEpoch,
            new CanvasDefinition((int)Extent, (int)Extent, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "mountains", true, false, 1, objects)],
            Enumerable.Range(1, 6).Select(i => new MapAssetReference(OverviewChunkDiskTests.Sha(i), "owner mountain", "image/png", "png",
                ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "owner fixture", "owner", catalogId: $"owner-mountain-0{i}")).ToArray());
        using var cache = new ObjectOverviewCache();
        using var preparation = new SceneRenderer.OverviewPreparation(static () => false);
        using var surface = SKSurface.Create(new SKImageInfo(1024, 1024, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        var watch = Stopwatch.StartNew();
        RenderStats stats;
        var frames = 0;
        var chunks = 0;
        do
        {
            Assert.True(watch.Elapsed < TimeSpan.FromMinutes(5), $"{phase}: did not complete");
            if (frames > 0) Thread.Sleep(2);
            var canvas = surface.Canvas;
            canvas.ResetMatrix(); canvas.Clear(SKColors.White); canvas.Scale((float)Scale);
            stats = new RenderStats();
            cache.BeginFrame();
            try
            {
                SceneRenderer.Render(canvas, document, new RenderOptions
                {
                    Stamps = source, ObjectCache = cache, OverviewPreparation = preparation, RequestRefinedFrame = static () => { },
                    Stats = stats, ViewScale = Scale, LodTier = RenderLodTier.Overview, VisibleWorld = new(0, 0, Extent, Extent),
                });
                canvas.Flush();
            }
            finally { cache.EndFrame(); }
            frames++;
            chunks = stats.OverviewChunks;
            Assert.Null(preparation.Statistics.Error);
        }
        while (stats.DetailTilesPending > 0 || chunks < 256 || preparation.VisibleWorkPending);
        var wall = watch.Elapsed.TotalMilliseconds;
        var work = preparation.Statistics;
        // The program closes later; the writer finishes in the background meanwhile (not part of the view's time).
        if (store is not null) Assert.True(store.Flush(TimeSpan.FromSeconds(60)));
        if (log)
            output.WriteLine($"{phase}: complete in {wall:F0} ms, frames={frames}, chunks={chunks}; worker plan={work.PlanMs:F0} palette={work.PaletteMs:F0} " +
                $"variants={work.VariantMs:F0} raster-or-load={work.RasterMs:F0} ms; disk={preparation.DiskStatistics} keys={preparation.DiskKeyMilliseconds:F0} ms");
        return wall;
    }

    /// <summary>Owner mountains at whole-map density, a third painted with a shadow.</summary>
    private static SceneObject[] Objects(int count)
    {
        var random = new Random(20261116);
        return Enumerable.Range(0, count).Select(i =>
        {
            var side = 32 + random.NextDouble() * 96;
            var style = new StampStyle(OverviewChunkDiskTests.Sha(1 + i % 6));
            if (i % 3 == 0) style = style with { ShadowOpacity = .45, ShadowBlur = 4 };
            return new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "owner mountain",
                256 + random.NextDouble() * (Extent - 512), 256 + random.NextDouble() * (Extent - 512),
                side, side * .8, 0, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = style };
        }).ToArray();
    }

    /// <summary>One 512-pixel chunk of the owner's mountain: raw file load against a PNG decode.</summary>
    private void ChunkLoad()
    {
        using var source = new OverviewChunkDiskTests.Mountains(null, identity: true);
        using var surface = SKSurface.Create(new SKImageInfo(512, 512, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        surface.Canvas.Clear(SKColors.Transparent);
        var mountain = source.TryGetImage(OverviewChunkDiskTests.Sha(1))!;
        for (var i = 0; i < 40; i++)
            surface.Canvas.DrawImage(mountain, SKRect.Create(i * 11 % 400, i * 37 % 400, 120, 100));
        using var image = surface.Snapshot();
        var pixels = new byte[512 * 512 * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { Assert.True(image.ReadPixels(image.Info, handle.AddrOfPinnedObject(), 512 * 4, 0, 0)); } finally { handle.Free(); }
        var store = new OverviewChunkDiskCache(Path.Combine(_root, "load"));
        var key = System.Security.Cryptography.SHA256.HashData("chunk"u8);
        store.Save(key, 512, 512, pixels);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var pngBytes = png.ToArray();
        var raw = new List<double>(); var decode = new List<double>();
        for (var i = 0; i < 41; i++)
        {
            var started = Stopwatch.GetTimestamp();
            using (var loaded = store.TryLoad(key, 512, 512)) Assert.NotNull(loaded);
            raw.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            started = Stopwatch.GetTimestamp();
            using (var decoded = SKImage.FromEncodedData(pngBytes)!.ToRasterImage(ensurePixelData: true)) Assert.NotNull(decoded);
            decode.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        output.WriteLine($"CHUNK 512 px: raw file {new FileInfo(store.PathOf(key)).Length / 1024} KiB load median={Median(raw):F3} ms; " +
            $"PNG {pngBytes.Length / 1024} KiB decode median={Median(decode):F3} ms (file read not included)");
    }
}
