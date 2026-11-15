using System.Diagnostics;
using Map.Core.Documents;
using SkiaSharp;
using Xunit.Abstractions;

namespace Map.Rendering.Tests;

/// <summary>
/// The whole-map view must complete again after an edit and after a wheel-out. Before 2026-11-15 the overview worker
/// rastered chunks whose pictures the frame already kept; the frame never took those rasters, sixteen of them filled
/// the bounded handoff and the worker stopped before the chunks the view still waited for: the fit view stayed
/// pending forever (live: a whole map drawn only in a central patch for minutes).
/// </summary>
[Collection(RealTimeSettleCollection.Name)]
public sealed class OverviewHandoffTests(ITestOutputHelper output)
{
    private const int Width = 1280, Height = 720;
    private const double Canvas = 131_072;

    [Fact]
    public void The_whole_map_view_completes_again_after_an_edit_and_after_a_wheel_out()
    {
        var disk = new DenseDetailTilesTests.MipDisk();
        try
        {
            var random = new Random(13013);
            var shas = Enumerable.Range(0, 1_500).Select(i => Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i + 91_000)))).ToArray();
            var records = shas.Select((sha, i) =>
            {
                var side = 400 + random.Next(1600);
                return new DenseDetailTilesTests.AssetShape(sha, side, (int)(side * (0.6 + random.NextDouble() * 0.8)), i % DenseDetailTilesTests.MipDisk.Fixtures);
            }).ToDictionary(r => r.Sha, StringComparer.OrdinalIgnoreCase);
            var items = new SceneObject[120_000];
            for (var i = 0; i < items.Length; i++)
            {
                var record = records[shas[i % shas.Length]];
                var width = random.NextDouble() < .0078 ? 900 + random.NextDouble() * 1700 : 32 + random.NextDouble() * 96;
                items[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Ассет " + i,
                    random.NextDouble() * (Canvas - width), random.NextDouble() * (Canvas - width),
                    width, width * record.Height / record.Width, random.Next(-180, 181), "#FFFFFFFF", "#00000000", 0, true, false)
                    { Stamp = new StampStyle(record.Sha) };
            }
            var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, items);
            var document = new MapDocument(DocumentId.New(), "handoff", DateTimeOffset.UnixEpoch,
                new CanvasDefinition((int)Canvas, (int)Canvas, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF"), layer])
                .WithAssets(shas.Select(sha => new MapAssetReference(sha, sha, "image/png", "png", ProjectAssetStorage.Linked,
                    DateTimeOffset.UnixEpoch, "owner-fixture", "user-owned", catalogId: sha)).ToArray());

            var moving = false;
            using var tiles = new ObjectOverviewCache(1024, 256L << 20);
            using var atlas = new ObjectOverviewCache();
            using var preparation = new SceneRenderer.OverviewPreparation(() => Volatile.Read(ref moving));
            using var surface = SKSurface.Create(new SKImageInfo(Width, Height))!;
            var source = new DenseDetailTilesTests.BoundedDerivedSource(records, disk, 512L << 20);
            preparation.UpdateDocument(document, source, static () => { });
            var lastFrame = 16.7;
            RenderStats last = new();

            bool Frame(double cx, double cy, double scale, bool camMoving)
            {
                Volatile.Write(ref moving, camMoving);
                var c = surface.Canvas;
                c.ResetMatrix(); c.Clear(SKColors.White);
                var halfW = Width / 2 / scale; var halfH = Height / 2 / scale;
                c.Scale((float)scale); c.Translate((float)-(cx - halfW), (float)-(cy - halfH));
                var stats = new RenderStats();
                var started = Stopwatch.GetTimestamp();
                using (source.BeginFrame(lastFrame))
                {
                    tiles.BeginFrame(); atlas.BeginFrame();
                    try
                    {
                        SceneRenderer.Render(c, document, new RenderOptions
                        {
                            Stamps = source, ObjectCache = tiles, AtlasCache = atlas, Stats = stats, ViewScale = scale,
                            OverviewPreparation = preparation, CameraMoving = camMoving, FastTerrain = camMoving,
                            LodTier = scale < .125 ? RenderLodTier.Overview : RenderLodTier.Exact,
                            VisibleWorld = new(cx - halfW, cy - halfH, cx + halfW, cy + halfH),
                            RequestRefinedFrame = static () => { },
                        });
                        c.Flush();
                    }
                    finally { tiles.EndFrame(); atlas.EndFrame(); }
                }
                Assert.Null(preparation.Statistics.Error);
                lastFrame = Math.Max(16.7, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                last = stats;
                return stats.DetailTilesPending == 0 && stats.UnresolvedImages == 0 && !source.ImageRequestsPending;
            }

            int Settle(double cx, double cy, double scale)
            {
                var timeout = Stopwatch.StartNew();
                for (var frame = 1; timeout.Elapsed < TimeSpan.FromSeconds(60); frame++)
                {
                    if (Frame(cx, cy, scale, false)) return frame;
                    Thread.Sleep(5);
                }
                return -1;
            }

            var centre = Canvas / 2;
            var fit = Height * .9 / Canvas;
            Assert.True(Settle(centre, centre, fit) > 0, "The first whole-map view did not complete.");

            var placed = Enumerable.Range(0, 10).Select(i =>
            {
                var record = records[shas[i]];
                return new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Новый " + i, centre + i * 2500 - 12_000, centre - 4000 + i * 700,
                    1200, 1200.0 * record.Height / record.Width, 15 * i, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(record.Sha) };
            }).ToArray();
            document = new Map.Core.Editing.AddObjectsCommand(layer.Id, placed).Apply(document);
            preparation.UpdateDocument(document, source, static () => { });
            var afterEdit = Settle(centre, centre, fit);
            output.WriteLine($"after edit: {afterEdit} frames; prep queued={preparation.Statistics.Queued} idle={preparation.Statistics.Idle}");
            Assert.True(afterEdit > 0, $"The whole-map view did not complete after an edit: pending={last.DetailTilesPending} queued={preparation.Statistics.Queued} idle={preparation.Statistics.Idle}");

            var scale = fit * 8;
            Settle(centre, centre, scale);
            while (scale > fit * 1.0001)
            {
                scale = Math.Max(fit, scale / Math.Pow(2, .25));
                for (var f = 0; f < 6; f++) { Frame(centre, centre, scale, true); Thread.Sleep(5); }
            }
            var afterWheel = Settle(centre, centre, fit);
            output.WriteLine($"after wheel-out: {afterWheel} frames");
            Assert.True(afterWheel > 0, $"The whole-map view did not complete after a wheel-out: pending={last.DetailTilesPending} queued={preparation.Statistics.Queued} idle={preparation.Statistics.Idle}");
        }
        finally { disk.Dispose(); }
    }
}
