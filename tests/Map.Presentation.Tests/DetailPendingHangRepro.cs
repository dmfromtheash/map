using System.Diagnostics;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;
using Xunit.Abstractions;

namespace Map.Presentation.Tests;

/// <summary>
/// Live 14.11 (8192 px, 250 000 stamps): after a few gestures at 100 % one detail tile stays pending with an empty
/// pixel queue. The real image source and derived store, the canvas's frame protocol (moving frames with the
/// gesture budget and fast terrain, then settled frames) until settled. Runs only with MAP_HANG_REPRO set.
/// </summary>
public sealed class DetailPendingHangRepro(ITestOutputHelper output) : IDisposable
{
    private const int Canvas = 8192, ScreenW = 2048, ScreenH = 1095;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-hang-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [OptInProbe("MAP_HANG_REPRO", "DerivedMipChurnTests.A_dense_view_with_gaps_between_its_asks_settles_without_cancelling_the_keys_it_needs")]
    public async Task Gestures_at_100_percent_settle()
    {
        var outFile = Environment.GetEnvironmentVariable("MAP_HANG_REPRO")!;
        var objects = int.TryParse(Environment.GetEnvironmentVariable("MAP_HANG_OBJECTS"), out var n) ? n : 250_000;
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var records = Assets(catalog, int.TryParse(Environment.GetEnvironmentVariable("MAP_HANG_ASSETS"), out var a) ? a : 300);
        var document = Document(records, objects);
        await using var source = new CatalogStampImageSource(catalog, () => document, () => null, derivedRoot: Path.Combine(_root, "derived"));
        using var tiles = new ObjectOverviewCache();
        using var atlas = new ObjectOverviewCache();
        using var preparation = new SceneRenderer.OverviewPreparation();
        preparation.UpdateDocument(document, source, () => { });
        var log = new List<string>();
        var frame = 0;
        RenderStats Draw(double scale, double cx, double cy, bool moving)
        {
            frame++;
            source.FrameMoving = moving;
            source.SetFrameReady(() => { });
            var stats = new RenderStats();
            var ox = cx - ScreenW / scale / 2; var oy = cy - ScreenH / scale / 2;
            using var stamp = source.BeginFrame();
            using var surface = SKSurface.Create(new SKImageInfo(ScreenW, ScreenH))!;
            surface.Canvas.Clear(SKColors.White);
            surface.Canvas.Scale((float)scale);
            surface.Canvas.Translate((float)-ox, (float)-oy);
            tiles.BeginFrame(); atlas.BeginFrame();
            try
            {
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                {
                    Stamps = source, ObjectCache = tiles, AtlasCache = atlas, OverviewPreparation = preparation, Stats = stats, ViewScale = scale,
                    LodTier = scale < .125 ? RenderLodTier.Overview : RenderLodTier.Exact,
                    VisibleWorld = new(ox, oy, ox + ScreenW / scale, oy + ScreenH / scale),
                    RequestRefinedFrame = () => { }, FastTerrain = moving, CameraMoving = moving,
                });
            }
            finally { tiles.EndFrame(); atlas.EndFrame(); }
            return stats;
        }
        string Line(string phase, RenderStats s) =>
            $"{phase} f{frame} pending={s.DetailTilesPending} waiting={s.DetailRastersWaiting} started={s.DetailRastersStarted} adopted={s.DetailRastersAdopted} "
            + $"onFrame={s.DetailTilesBuiltOnFrame} tiles={s.ObjectTilesDrawn} unresolved={s.UnresolvedImages} late={source.ImageRequestsPending} "
            + $"mips={source.DerivedStatistics.PendingMips} discarded={source.DerivedStatistics.DiscardedMips} skip={s.DenseSkip} reused={s.TilesReused}";
        bool Settle(string phase, double scale, double cx, double cy, TimeSpan limit)
        {
            var watch = Stopwatch.StartNew();
            var quiet = 0;
            RenderStats last = new();
            while (watch.Elapsed < limit)
            {
                last = Draw(scale, cx, cy, moving: false);
                var settled = last.DetailTilesPending == 0 && last.UnresolvedImages == 0 && !source.ImageRequestsPending;
                if (frame % 50 == 0) log.Add(Line(phase, last));
                quiet = settled ? quiet + 1 : 0;
                if (quiet >= 3) { log.Add($"{phase}: settled in {watch.Elapsed.TotalSeconds:F1}s, " + Line(phase, last)); return true; }
                Thread.Sleep(16);
            }
            log.Add($"{phase}: TIMEOUT " + Line(phase, last));
            return false;
        }
        var (x, y) = (Canvas / 2.0, Canvas / 2.0);
        var ok = true;
        var limit = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("MAP_HANG_SECONDS"), out var s) ? s : 90);
        try
        {
            ok &= Settle("start-100", 1, x, y, limit);
            // Wheel in around 100 %: eight notches to 200 % and back.
            for (var i = 1; i <= 16; i++) Draw(Math.Pow(2, i / 8.0), x, y, true);
            for (var i = 15; i >= 0; i--) Draw(Math.Pow(2, i / 8.0), x, y, true);
            ok &= Settle("wheel-in-100", 1, x, y, limit);
            // Pan by a screen and a half.
            for (var i = 1; i <= 30; i++) Draw(1, x + i * 100, y + i * 20, true);
            x += 3000; y += 600;
            ok &= Settle("pan-100", 1, x, y, limit);
            // Wheel out to 50 % and back.
            for (var i = 1; i <= 8; i++) Draw(Math.Pow(2, -i / 8.0), x, y, true);
            for (var i = 7; i >= 0; i--) Draw(Math.Pow(2, -i / 8.0), x, y, true);
            ok &= Settle("wheel-out-100", 1, x, y, limit);
            // The canvas edge.
            for (var i = 1; i <= 20; i++) Draw(1, x + i * 50, y + i * 100, true);
            x = Math.Min(Canvas, x + 1000); y = Math.Min(Canvas, y + 2000);
            ok &= Settle("edge-100", 1, x, y, limit);
        }
        finally
        {
            foreach (var line in log) output.WriteLine(line);
            File.WriteAllLines(outFile, log);
        }
        Assert.True(ok, string.Join(Environment.NewLine, log.Where(l => l.Contains("TIMEOUT", StringComparison.Ordinal))));
    }

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
                var side = 220 + random.Next(420);
                using var surface = SKSurface.Create(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul))!;
                surface.Canvas.Clear(SKColors.Transparent);
                var shift = (float)(random.NextDouble() * 0.5 - 0.25);
                using var filter = SKColorFilter.CreateColorMatrix([1 + shift, 0, 0, 0, 0, 0, 1 - shift * 0.5f, 0, 0, 0, 0, 0, 1 + shift * 0.3f, 0, 0, 0, 0, 0, 1, 0]);
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

    private static MapDocument Document(AssetRecord[] records, int count)
    {
        var random = new Random(20261114);
        var objects = new SceneObject[count];
        for (var i = 0; i < count; i++)
        {
            var w = 20 + random.NextDouble() * 90;
            objects[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Mountain", random.NextDouble() * (Canvas - w),
                random.NextDouble() * (Canvas - w), w, w * (0.6 + random.NextDouble() * 0.6), random.NextDouble() < 0.2 ? random.NextDouble() * 360 : 0,
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(records[random.Next(records.Length)].Sha256), Sublayer = random.Next(4) };
        }
        return new MapDocument(DocumentId.New(), "Hang", DateTimeOffset.UnixEpoch, new CanvasDefinition(Canvas, Canvas, "px", "#FFF3EBDD"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFF3EBDD"), new ObjectLayer(LayerId.New(), "Mountains", true, false, 1, objects)],
            assets: records.Select(r => new MapAssetReference(r.Sha256, r.DisplayName, r.MediaType, r.Extension, ProjectAssetStorage.Linked,
                DateTimeOffset.UnixEpoch, "owner-fixture", "user-owned", catalogId: r.Sha256)).ToArray());
    }
}
