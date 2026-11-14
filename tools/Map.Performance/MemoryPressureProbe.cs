using System.Diagnostics;
using System.Text.Json;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Resources;
using Map.Presentation.Services;
using Map.Export;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// Headless, no Avalonia host or window. --iterations is the route duration in seconds (minimum one).
/// MAP_PERF_MEMORY_POLICY: <c>off</c> - caches do not report to the budget (the code before the change);
/// <c>on</c> (default) - they report and follow the measured pressure of this PC;
/// <c>simulated</c> - as <c>on</c>, with the pressure forced on from the start of the route.
/// The pressure is polled once a second on a background timer, as the program does.
/// </summary>
internal static class MemoryPressureProbe
{
    public static object Run(PerformanceOptions options)
    {
        var policy = (Environment.GetEnvironmentVariable("MAP_PERF_MEMORY_POLICY") ?? "on").Trim().ToLowerInvariant();
        if (policy is not ("off" or "on" or "simulated")) throw new ArgumentException("MAP_PERF_MEMORY_POLICY: off | on | simulated");
        // Measured memory of this PC (not the fixed test machine), polled by our own timer.
        var budget = new WorkingMemoryBudget(ReadMachine);
        var fixture = LargeMapFixtures.Simple(options.ObjectCount, options.Seed);
        using var stamps = new ExportStampImageSource(fixture.Document, null, null);
        using var overview = new ObjectOverviewCache(1024, 128 * WorkingMemoryBudget.MiB, budget,
            memoryAccount: policy == "off" ? null : "object-overview");
        using var assets = new AssetImageCache(4096, 128 * WorkingMemoryBudget.MiB);
        if (policy != "off") assets.TrackMemory(budget, "asset-images", CacheRebuildCost.Moderate);
        using var surface = SKSurface.Create(new SKImageInfo(640, 360))!;
        // Thousands of independently decoded identities, all using the approved owner's mountain pixels.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        using var original = SKImage.FromEncodedData(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"));
        byte[] payload;
        using (var scaled = SKSurface.Create(new SKImageInfo(128, 128))!)
        {
            scaled.Canvas.Clear(SKColors.Transparent);
            scaled.Canvas.DrawImage(original, new SKRect(0, 0, 128, 128));
            using var snapshot = scaled.Snapshot();
            using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            payload = encoded.ToArray();
        }
        for (var i = 0; i < 2048; i++) assets.GetEncoded("asset-" + i, payload);
        if (policy == "simulated") budget.SimulatedCachePressure = true;
        using var poll = policy == "off" ? null
            : new Timer(_ => budget.PollCachePressure(), null, TimeSpan.Zero, WorkingMemoryBudget.CachePressurePollInterval);
        var beforePrivate = PrivateBytes();
        var beforeHeap = GC.GetTotalMemory(false);
        long peakPrivate = beforePrivate, peakHeap = beforeHeap, peakAssets = 0, peakOverview = 0;
        var frames = new List<double>(1 << 18);
        var trimTimes = new List<double>(1 << 18);
        var route = Stopwatch.StartNew();
        var stats = new RenderStats();
        var frame = 0;
        long assetMisses0 = assets.Misses, overviewMisses0 = overview.Misses;
        var samples = new List<object>();
        var nextSample = TimeSpan.Zero;
        do
        {
            var started = Stopwatch.GetTimestamp();
            assets.GetEncoded("asset-" + frame % 2048, payload);
            var zoom = .015625 * Math.Pow(2, frame / 60 % 4);
            var x = frame % 256 * 200d;
            var y = frame / 256 % 256 * 200d;
            var view = new WorldBounds(x, y, x + 640 / zoom, y + 360 / zoom);
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Save(); surface.Canvas.Scale((float)zoom); surface.Canvas.Translate((float)-x, (float)-y);
            overview.BeginFrame();
            try
            {
                SceneRenderer.Render(surface.Canvas, fixture.Document, new RenderOptions
                { VisibleWorld = view, ViewScale = zoom, LodTier = RenderLodTier.Overview, ObjectCache = overview, Stamps = stamps, Stats = stats });
            }
            finally { overview.EndFrame(); surface.Canvas.Restore(); }
            surface.Canvas.Flush();
            // After the frame, as the stamp source does: only does work while the budget reports pressure.
            var trim = Stopwatch.GetTimestamp();
            assets.TrimForMemoryPressure(1);
            trimTimes.Add(Stopwatch.GetElapsedTime(trim).TotalMilliseconds);
            frames.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            frame++;
            peakAssets = Math.Max(peakAssets, assets.ResidentBytes);
            peakOverview = Math.Max(peakOverview, overview.Bytes);
            if (route.Elapsed >= nextSample)
            {
                var memory = PrivateBytes(); var heap = GC.GetTotalMemory(false);
                peakPrivate = Math.Max(peakPrivate, memory); peakHeap = Math.Max(peakHeap, heap);
                samples.Add(new
                {
                    seconds = Math.Round(route.Elapsed.TotalSeconds, 1),
                    privateBytes = memory,
                    managedHeapBytes = heap,
                    assetBytes = assets.ResidentBytes,
                    overviewBytes = overview.Bytes,
                    stage = budget.CachePressureStage
                });
                nextSample = route.Elapsed + TimeSpan.FromSeconds(5);
            }
        } while (route.Elapsed.TotalSeconds < Math.Max(1, options.MeasuredIterations));
        var seconds = route.Elapsed.TotalSeconds;
        frames.Sort(); trimTimes.Sort();
        var tail = Math.Max(1, (int)Math.Ceiling(frames.Count * .01));
        var info = GC.GetGCMemoryInfo();
        var machine = ReadMachine();
        var result = new
        {
            policy,
            objects = options.ObjectCount,
            decodedIdentities = 2048,
            seconds,
            frameCount = frame,
            machine = new
            {
                physicalBytes = machine.PhysicalBytes,
                availableBytesAtEnd = machine.AvailableBytes,
                gcHighMemoryLoadThresholdBytes = info.HighMemoryLoadThresholdBytes,
                gcTotalAvailableBytes = info.TotalAvailableMemoryBytes
            },
            cacheBudgetBytes = budget.CacheBudgetBytes,
            pressureEvents = budget.CachePressureEvents,
            beforePrivateBytes = beforePrivate,
            afterPrivateBytes = PrivateBytes(),
            peakPrivateBytes = peakPrivate,
            beforeManagedHeapBytes = beforeHeap,
            afterManagedHeapBytes = GC.GetTotalMemory(false),
            peakManagedHeapBytes = peakHeap,
            peakAssetBytes = peakAssets,
            peakOverviewBytes = peakOverview,
            assetMisses = assets.Misses - assetMisses0,
            overviewMisses = overview.Misses - overviewMisses0,
            assetPressureTrims = assets.PressureTrims,
            overviewPressureTrims = overview.PressureTrims,
            p50WorkMs = frames[(int)Math.Ceiling(frames.Count * .50) - 1],
            p99WorkMs = frames[(int)Math.Ceiling(frames.Count * .99) - 1],
            maxWorkMs = frames[^1],
            onePercentLowWorkFps = 1000 / frames.TakeLast(tail).Average(),
            over16_7Ms = frames.Count(value => value > 1000 / 60d),
            trimP99Ms = trimTimes[(int)Math.Ceiling(trimTimes.Count * .99) - 1],
            trimMaxMs = trimTimes[^1],
            caches = budget.ReadCacheStatistics(),
            samples,
            limitations = "CPU raster work per frame (incl. the trim), not presentation FPS. One owner mountain repeated as 2048 identities; "
                + "peak private/heap sampled every 5 s; the 1 000 000-object document is created before the route.",
        };
        Console.WriteLine(JsonSerializer.Serialize(result));
        return result;
    }

    private static WorkingMemorySnapshot ReadMachine()
    {
        var status = new MemoryStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status)
            ? new((long)status.TotalPhysical, (long)status.AvailablePhysical, PrivateBytes())
            : new(0, 0, PrivateBytes());
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    private static long PrivateBytes() { using var process = Process.GetCurrentProcess(); return process.PrivateMemorySize64; }
}
