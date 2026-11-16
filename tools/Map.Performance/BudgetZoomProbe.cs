using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>Headless CPU-relative zoom measurement. The same source file can be compiled against
/// the baseline assemblies. Owner pixels only, 100k document objects, no GPU/window or FPS claim.
/// Preparation/import is excluded. A settled frame must have zero unresolved images and exactly
/// match a synchronous reference image, otherwise the measurement fails.</summary>
internal static class BudgetZoomProbe
{
    public static async Task<object> RunAsync(int objectCount, int trials)
    {
        var root = Environment.GetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT")
            ?? throw new InvalidOperationException("Set an isolated MAP_UI_TEST_PROFILE_ROOT.");
        var fixtureRoot = Environment.GetEnvironmentVariable("MAP_BUDGET_PROBE_REPO") ?? Environment.CurrentDirectory;
        var useDerived = Environment.GetEnvironmentVariable("MAP_BUDGET_PROBE_DERIVED") != "0";
        Directory.CreateDirectory(root);
        await using var catalog = new AssetCatalog(Path.Combine(root, "catalog"));
        var owners = Enumerable.Range(1, 6).Select(n => SKBitmap.Decode(OwnerMountainFixtures.Path(n, fixtureRoot))).ToArray();
        AssetRecord[] records;
        const int assetCount = 2000;
        try
        {
            var paths = new string[assetCount];
            for (var i = 0; i < paths.Length; i++)
            {
                // Distinct sizes/SHA without changing the owner's art or adding marker pixels.
                using var resized = owners[i % 6].Resize(new SKImageInfo(192 + i % 64, 160 + i / 64),
                    new SKSamplingOptions(SKFilterMode.Linear));
                paths[i] = Path.Combine(root, $"owner-{i:0000}.png");
                using var encoded = resized.Encode(SKEncodedImageFormat.Png, 100);
                using var file = File.Create(paths[i]);
                encoded.SaveTo(file);
            }
            var set = catalog.ImportSet(paths, "Owner zoom", new(), DateTimeOffset.UnixEpoch);
            records = set.VariantIds.Select(id => catalog.Find(catalog.FindVariant(id)!.Sha256)!).ToArray();
            foreach (var path in paths) File.Delete(path);
        }
        finally { foreach (var owner in owners) owner?.Dispose(); }
        if (records.Length != assetCount) throw new InvalidOperationException("Expected 2000 distinct owner assets.");
        var objects = Enumerable.Range(0, objectCount).Select(i =>
        {
            var record = records[i % records.Length];
            return new SceneObject(new ObjectId(new Guid(i + 1, 0, 0, new byte[8])), SceneObjectKind.Stamp, "Mountain",
                i % 500 * 64, i / 500 * 64, record.Width / 2d, record.Height / 2d, 0,
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(record.Sha256) };
        }).ToArray();
        var document = new MapDocument(DocumentId.New(), "Budget zoom", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(32000, 12800, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Mountains", true, false, 1, objects)]);
        var derived = useDerived ? Path.Combine(root, "derived") : null;
        if (derived is not null)
        {
            await using var preparation = new StampDerivedAssetStore(catalog, derived, startWatcher: _ => { });
            var headers = await Task.WhenAll(records.Select(r => preparation.Request(r.Sha256))).WaitAsync(TimeSpan.FromMinutes(3));
            if (headers.Any(h => h is null))
                throw new InvalidOperationException($"Derived preparation failed: {headers.Count(h => h is null)} null headers; failures={preparation.Failures}.");
        }
        using var surface = SKSurface.Create(new SKImageInfo(1600, 900))!;
        string reference;
        using (var source = new CatalogStampImageSource(catalog, () => document, () => null))
        {
            using var lease = source.Lease();
            Draw(lease, .5f);
            reference = Hash();
        }
        var rows = new List<object>();
        for (var trial = 1; trial <= trials; trial++)
        {
            await using var source = new CatalogStampImageSource(catalog, () => document, () => null,
                derivedRoot: derived, startWatcher: _ => { }) { MeasureTimings = true };
            var timeout = Stopwatch.StartNew();
            while (Draw(source, .25f).UnresolvedImages != 0)
            {
                if (timeout.Elapsed > TimeSpan.FromMinutes(1)) throw new TimeoutException("Old zoom did not settle.");
                await Task.Delay(16);
            }
            // The controller may use the measured warm frames; no fabricated fast-frame input.
            source.FrameMoving = true;
            var watch = Stopwatch.StartNew();
            int frames = 0, unresolved;
            long visibleStamps = 0;
            double maxWorkMs = 0;
            do
            {
                var started = Stopwatch.GetTimestamp();
                var stats = Draw(source, .5f);
                unresolved = stats.UnresolvedImages;
                visibleStamps = stats.StampInstances;
                var spent = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                maxWorkMs = Math.Max(maxWorkMs, spent);
                frames++;
                if (watch.Elapsed > TimeSpan.FromMinutes(1)) throw new TimeoutException("Target zoom did not settle.");
                if (unresolved > 0 && spent < 16.667) await Task.Delay(TimeSpan.FromMilliseconds(16.667 - spent));
            } while (unresolved != 0);
            watch.Stop();
            var hash = Hash();
            if (hash != reference) throw new InvalidOperationException("Settled pixels differ from the synchronous reference.");
            var warmFrames = new List<double>();
            if (Environment.GetEnvironmentVariable("MAP_BUDGET_PROBE_DIAGNOSTICS") == "1")
                for (var i = 0; i < 9; i++)
                {
                    var started = Stopwatch.GetTimestamp();
                    var stable = Draw(source, .5f);
                    if (stable.UnresolvedImages != 0) throw new InvalidOperationException("Sharp view did not remain resident.");
                    warmFrames.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            var timings = source.Timings;
            var cache = source.CacheStatistics;
            var row = new { trial, objects = objectCount, visibleStamps, assets = records.Length, derived = useDerived,
                timeToSharpMs = watch.Elapsed.TotalMilliseconds, frames, maxWorkMs, unresolved,
                source.ByteLimitedFrames, hash, samePixels = true,
                warmFramesMs = warmFrames, source.DerivedIoDiagnostics,
                timings = new { timings.LoadMs, timings.SampleMs, timings.AdoptMs },
                cache = new { cache.Entries, cache.ResidentBytes, cache.Misses, cache.Evictions },
                profile = source.GetType().GetProperty("ResourceProfile")?.GetValue(source),
                limits = source.GetType().GetProperty("ResourceLimits")?.GetValue(source) };
            rows.Add(row);
            Console.WriteLine(JsonSerializer.Serialize(row));
        }
        return new { objects = objectCount, trials, cpuRelativeOnly = true, rows };

        RenderStats Draw(IStampImageSource source, float scale)
        {
            surface.Canvas.ResetMatrix();
            surface.Canvas.Clear(SKColors.White);
            surface.Canvas.Scale(scale);
            var stats = new RenderStats();
            using (source.BeginFrame())
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                {
                    Stamps = source, Stats = stats, ViewScale = scale, LodTier = RenderLodTier.Exact,
                    VisibleWorld = new(0, 0, 1600 / scale, 900 / scale),
                });
            surface.Canvas.Flush();
            return stats;
        }
        string Hash()
        {
            using var snapshot = surface.Snapshot();
            using var pixels = SKBitmap.FromImage(snapshot);
            return Convert.ToHexStringLower(SHA256.HashData(pixels.Bytes));
        }
    }
}
