using System.Diagnostics;
using Map.Assets;
using Map.Presentation.Services;
using SkiaSharp;
using Xunit.Abstractions;

namespace Map.Presentation.Tests;

public sealed class StampDerivedAssetStoreLargeSlotsTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-large-slots-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Measurement, not a gate: a dense 400 % view that needs ~300 distinct large results (512–1024 px), adopted
    /// by a simulated 60 Hz frame under the same 2 ms rule as <see cref="CatalogStampImageSource.BeginFrame()"/>.
    /// Runs only with MAP_LARGE_SLOTS_BENCH=1 (owner mountains resized in a disposable catalog).
    /// </summary>
    [Fact]
    public async Task Dense_view_of_large_results_measurement()
    {
        if (Environment.GetEnvironmentVariable("MAP_LARGE_SLOTS_BENCH") != "1") return;
        var count = int.TryParse(Environment.GetEnvironmentVariable("MAP_LARGE_SLOTS_COUNT"), out var n) ? n : 300;
        // Level 1: sources of 1024–2046 px halved once, so every result pays a full decode (the weak laptop's shape).
        var level = Environment.GetEnvironmentVariable("MAP_LARGE_SLOTS_LEVEL") == "1" ? 1 : 0;
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var records = ImportResizedOwners(catalog, count, level);
        var store = new StampDerivedAssetStore(catalog, Path.Combine(_root, "derived"));
        try
        {
            var headers = (await Task.WhenAll(records.Select(r => store.Request(r.Sha256))).WaitAsync(TimeSpan.FromMinutes(5)))
                .Select(h => h!).ToArray();
            var keys = headers.Select(h => new StampDerivedAssetStore.PixelKey(h.Sha.ToLowerInvariant(), 0, level)).ToArray();
            var tasks = new Dictionary<StampDerivedAssetStore.PixelKey, Task<StampDerivedAssetStore.Mip>>();
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < headers.Length; i++)
            {
                Assert.True(store.TryRequestPixels(headers[i], 0, level, out var task));
                tasks[keys[i]] = task;
            }
            var wanted = keys.ToHashSet();
            long peakPending = 0, frames = 0; double frameMax = 0, frameSum = 0;
            var queue = new List<string>();
            var nextSample = 0d;
            while (wanted.Count > 0 && watch.Elapsed < TimeSpan.FromMinutes(5))
            {
                var pending = tasks.Where(t => wanted.Contains(t.Key) && t.Value.IsCompletedSuccessfully)
                    .Sum(t => t.Value.Result.Image is { } image ? (long)image.Width * image.Height * 4 : 0);
                peakPending = Math.Max(peakPending, pending);
                if (watch.Elapsed.TotalSeconds >= nextSample)
                {
                    queue.Add($"{watch.Elapsed.TotalSeconds:0.0}s:{store.PendingMips}");
                    nextSample += 0.5;
                }
                var started = Stopwatch.GetTimestamp();
                foreach (var key in store.ReadyPixels)
                {
                    if (store.TryTakePixels(key, out var mip)) { mip?.Image?.Dispose(); wanted.Remove(key); }
                    if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 2) break;
                }
                var spent = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                frameMax = Math.Max(frameMax, spent); frameSum += spent; frames++;
                store.CancelUnwantedPixels(wanted);
                await Task.Delay(16);
            }
            var total = watch.Elapsed;
            output.WriteLine($"LARGE_SLOTS level={level} count={count} exact={count - wanted.Count} timeToExact={total.TotalSeconds:0.00}s " +
                $"peakPendingMiB={peakPending / 1048576d:0.0} frames={frames} acceptMaxMs={frameMax:0.000} acceptAvgMs={frameSum / Math.Max(1, frames):0.000} " +
                $"cores={Environment.ProcessorCount}");
            output.WriteLine("queue " + string.Join(' ', queue));
            Assert.Empty(wanted);
        }
        finally { store.Dispose(); await store.Completion; }
    }

    // The owner's mountains, resized in the disposable catalog to distinct sides 512–1024 px: every result is a
    // large raster (> 256 px), no detail ladder. The repository fixtures are untouched.
    private AssetRecord[] ImportResizedOwners(AssetCatalog catalog, int count, int level = 0)
    {
        var fixture = new DirectoryInfo(AppContext.BaseDirectory);
        while (fixture is not null && !File.Exists(Path.Combine(fixture.FullName, "Map.slnx"))) fixture = fixture.Parent;
        Assert.NotNull(fixture);
        var owners = Enumerable.Range(1, 6).Select(i => SKBitmap.Decode(Path.Combine(fixture.FullName, "tests", "fixtures", "assets",
            $"owner-mountain-{i:00}.png"))).ToArray();
        try
        {
            var folder = Path.Combine(_root, "sources");
            Directory.CreateDirectory(folder);
            var paths = new string[count];
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, i =>
            {
                var side = (512 + (int)(511L * i / Math.Max(1, count - 1))) << level;
                var other = i % 3 == 0 ? side : side * (6 + i % 4) / 10;
                using var resized = owners[i % owners.Length].Resize(i % 2 == 0 ? new SKImageInfo(side, other) : new SKImageInfo(other, side),
                    new SKSamplingOptions(SKFilterMode.Linear));
                paths[i] = Path.Combine(folder, $"owner-{i:000}.png");
                using var png = resized.Encode(SKEncodedImageFormat.Png, 100);
                using var file = File.Create(paths[i]);
                png.SaveTo(file);
            });
            return paths.Select(path => catalog.Import(path, new(), DateTimeOffset.UnixEpoch)).ToArray();
        }
        finally { foreach (var owner in owners) owner.Dispose(); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
