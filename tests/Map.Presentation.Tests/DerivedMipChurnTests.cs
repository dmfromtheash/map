using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using SkiaSharp;
using Xunit.Abstractions;

namespace Map.Presentation.Tests;

/// <summary>
/// The canvas's frame protocol over the real image source and derived store with the laptop's two large lanes
/// (4 cores): a dense view at 400 % asks for a few hundred distinct large prepared mips. Between its frames come
/// frames that ask for nothing (camera reuse while moving, kept tiles drawn resampled) and frames that ask for only
/// part of the view (3 ms of plan checks, the shelf). A key the view still needs must keep its place in the lanes
/// instead of being cancelled and queued again at the back (live 14.11: 29 750 discarded mips, the view never finished).
/// </summary>
public sealed class DerivedMipChurnTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-mip-churn-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_dense_view_with_gaps_between_its_asks_settles_without_cancelling_the_keys_it_needs()
    {
        var lanes = StampDerivedAssetStore.LargeLaneCount;
        StampDerivedAssetStore.LargeLaneCount = 2;
        try
        {
            await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
            var records = Assets(catalog, 180);
            await using var source = Source(catalog, records);
            // Every stamp at just under half its image: a 320-520 px raster, prepared on a large lane.
            var view = records.Select(r => (r.Sha256, r.Width / 2.0 - 1, r.Height / 2.0 - 1)).ToArray();
            var half = view.Length / 2;
            var ready = false;
            var frames = 0;
            var log = new List<string>();
            var started = DateTime.UtcNow;
            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(90))
            {
                frames++;
                using (source.BeginFrame())
                {
                    // Of every four frames: one asks for the whole view, one for its centre only (the rest was out of
                    // the frame's check budget), two ask for nothing (camera reuse / kept tiles).
                    ready = (frames % 4) switch
                    {
                        0 => source.PrepareForPixelSizes(view, () => { }),
                        1 => source.PrepareForPixelSizes(view[..half], () => { }) && false,
                        _ => false,
                    };
                }
                var derived = source.DerivedStatistics;
                if (frames % 20 == 0)
                    log.Add($"frame {frames}: pending {derived.PendingMips}, discarded {derived.DiscardedMips}, kept wanted {source.LastWantedMipCount}");
                if (ready) break;
                Thread.Sleep(16);
            }
            var final = source.DerivedStatistics;
            var summary = $"ready={ready} after {frames} frames ({(DateTime.UtcNow - started).TotalSeconds:F1}s), discarded {final.DiscardedMips}, "
                + $"byte-limited frames {source.ByteLimitedFrames}, keys {view.Length}";
            foreach (var line in log) output.WriteLine(line);
            output.WriteLine(summary);
            if (Environment.GetEnvironmentVariable("MAP_CHURN_OUT") is { } outFile) File.WriteAllLines(outFile, [.. log, summary]);
            Assert.True(ready, summary);
            // Nothing the view asks for is cancelled while it keeps asking.
            Assert.Equal(0, final.DiscardedMips);
            Assert.Equal(0, source.CancelledStillWanted);
        }
        finally { StampDerivedAssetStore.LargeLaneCount = lanes; }
    }

    [Fact]
    public async Task A_key_the_view_stopped_asking_for_is_still_cancelled()
    {
        var lanes = StampDerivedAssetStore.LargeLaneCount;
        StampDerivedAssetStore.LargeLaneCount = 1;
        try
        {
            await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
            var records = Assets(catalog, 40);
            await using var source = Source(catalog, records);
            var old = records[..20].Select(r => (r.Sha256, r.Width / 2.0 - 1, r.Height / 2.0 - 1)).ToArray();
            var next = records[20..].Select(r => (r.Sha256, r.Width / 2.0 - 1, r.Height / 2.0 - 1)).ToArray();
            using (source.BeginFrame()) source.PrepareForPixelSizes(old, () => { });
            var queued = source.DerivedStatistics.PendingMips;
            // The camera moved on: the new view asks every frame, the old one never again.
            for (var frame = 0; frame < CatalogStampImageSource.UnaskedFramesBeforeCancel + 5; frame++)
                using (source.BeginFrame()) source.PrepareForPixelSizes(next, () => { });
            Assert.True(source.DerivedStatistics.DiscardedMips > 0,
                $"Requests of a view left behind must still give way to the new one (queued {queued}, pending {source.DerivedStatistics.PendingMips}).");
            Assert.Equal(0, source.CancelledStillWanted);
        }
        finally { StampDerivedAssetStore.LargeLaneCount = lanes; }
    }

    /// <summary>The image source over these assets with every prepared header already known (the view's burst follows).</summary>
    private CatalogStampImageSource Source(AssetCatalog catalog, AssetRecord[] records)
    {
        var document = new MapDocument(DocumentId.New(), "Churn", DateTimeOffset.UnixEpoch, new CanvasDefinition(32768, 32768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF")],
            assets: records.Select(r => new MapAssetReference(r.Sha256, r.DisplayName, r.MediaType, r.Extension, ProjectAssetStorage.Linked,
                DateTimeOffset.UnixEpoch, "owner-fixture", "user-owned", catalogId: r.Sha256)).ToArray());
        // Fixtures are immutable throughout this queue scenario. Historical creation events
        // delivered when a watcher starts must not masquerade as a camera-demand cancellation.
        var source = new CatalogStampImageSource(catalog, () => document, () => null, derivedRoot: Path.Combine(_root, "derived"),
            startWatcher: _ => { });
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (!source.PrepareOverview(records.Select(r => r.Sha256), () => { }))
        {
            Assert.True(DateTime.UtcNow < deadline, "The prepared headers did not arrive.");
            Thread.Sleep(50);
        }
        return source;
    }

    /// <summary>Distinct images made from the owner's mountain fixtures: resized, recoloured, one marker pixel each.</summary>
    private AssetRecord[] Assets(AssetCatalog catalog, int count)
    {
        var fixtures = new DirectoryInfo(AppContext.BaseDirectory);
        while (fixtures is not null && !File.Exists(Path.Combine(fixtures.FullName, "Map.slnx"))) fixtures = fixtures.Parent;
        var bases = Enumerable.Range(1, 6).Select(i => SKImage.FromEncodedData(Path.Combine(fixtures!.FullName, "tests", "fixtures", "assets", $"owner-mountain-{i:00}.png"))).ToArray();
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);
        var random = new Random(4242);
        var records = new AssetRecord[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                var side = 640 + random.Next(400);
                using var surface = SKSurface.Create(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul))!;
                surface.Canvas.Clear(SKColors.Transparent);
                var shift = (float)(random.NextDouble() * 0.5 - 0.25);
                using var filter = SKColorFilter.CreateColorMatrix([
                    1 + shift, 0, 0, 0, 0,
                    0, 1 - shift * 0.5f, 0, 0, 0,
                    0, 0, 1 + shift * 0.3f, 0, 0,
                    0, 0, 0, 1, 0]);
                using var paint = new SKPaint { ColorFilter = filter };
                surface.Canvas.DrawImage(bases[i % bases.Length], new SKRect(0, 0, side, side), new SKSamplingOptions(SKFilterMode.Linear), paint);
                using var marker = new SKPaint { Color = new SKColor((byte)(i & 255), (byte)(i >> 8), 77, 255) };
                surface.Canvas.DrawPoint(0, 0, marker);
                using var snapshot = surface.Snapshot();
                var path = Path.Combine(staging, $"a{i:0000}.png");
                using (var file = File.Create(path)) snapshot.Encode(SKEncodedImageFormat.Png, 90).SaveTo(file);
                records[i] = catalog.Import(path, new(), DateTimeOffset.UnixEpoch);
                File.Delete(path);
            }
        }
        finally { foreach (var image in bases) image.Dispose(); }
        return records;
    }
}
