using System.Diagnostics;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Reflection;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using SkiaSharp;

namespace Map.Rendering.Tests;

// test-change: real derived source, empty machine cache, owner pixels, no mocked image arrivals.
[Collection("OverviewExactLayers")]
public sealed class ColdStartPipelineTests
{
    [Fact]
    public async Task Visible_pixel_headers_overtake_a_cold_palette_backlog()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-cold-priority-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var records = SeedLibrary(Path.Combine(root, "library"), 64);
            await using var catalog = new AssetCatalog(Path.Combine(root, "catalog"));
            catalog.SetMounts([Path.Combine(root, "library")]);
            var doc = Document(100, records, records);
            await using var source = new CatalogStampImageSource(catalog, () => doc, () => null,
                derivedRoot: Path.Combine(root, "derived"), startWatcher: _ =>
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                });
            var store = (StampDerivedAssetStore)typeof(CatalogStampImageSource).GetField("_derived",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
            var tasks = records.Select(record => store.Request(record.Sha256)).ToArray();
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(source.PrepareForPixelSizes([(records[^1].Sha256, 32d, 32d)], static () => { }));
                var backlog = Task.WhenAll(tasks.Take(32));
                var first = Task.WhenAny(tasks[^1], backlog);
                release.Set();
                Assert.Same(tasks[^1], await first.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.NotNull(await tasks[^1]);
            }
            finally { release.Set(); }
        }
        finally { release.Set(); Directory.Delete(root, true); }
    }

    internal static async Task Run(int count, bool indexed)
    {
        var root = Path.Combine(Path.GetTempPath(), "map-coldstart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Reproduction runs can prepare the read-only library in a DIFFERENT process, so even
            // fixture encoding/JSON/Skia setup cannot warm the measured program's static state.
            var externalLibrary = Environment.GetEnvironmentVariable("MAP_COLDSTART_LIBRARY_ROOT");
            var library = externalLibrary ?? Path.Combine(root, "library");
            var records = externalLibrary is null ? SeedLibrary(library, 3072, indexed) : [];
            await using var catalog = new AssetCatalog(Path.Combine(root, "catalog"));
            catalog.SetMounts([library]);
            if (externalLibrary is not null)
            {
                records = catalog.Records.ToArray();
                Assert.Equal(3072, records.Length);
                Assert.Equal(indexed, File.Exists(Path.Combine(library, "derived", "v1", "overview-colors.bin")));
            }
            // Keep unused library entries AND unused document references: neither is a reason to read a header.
            var used = records.Take(3000).ToArray();
            var doc = Document(count, used, records);
            var wheelView = new WorldBounds(15616, 15616, 17152, 17152);
            var visibleAssets = doc.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)
                .Where(o => SceneGeometry.BoundsOf(o).Intersects(wheelView)).Select(o => o.Stamp!.AssetSha256).ToHashSet();
            var cache = Path.Combine(root, "empty-derived");
            Assert.False(Directory.Exists(cache));
            var watch = Stopwatch.StartNew();
            await using var source = new CatalogStampImageSource(catalog, () => doc, () => null,
                derivedRoot: cache, startWatcher: _ => { });
            using var preparation = new SceneRenderer.OverviewPreparation(static () => false);
            using var tiles = new ObjectOverviewCache();
            using var atlas = new ObjectOverviewCache();
            using var layers = new LayerImageCache();
            using var surface = SKSurface.Create(new SKImageInfo(768, 768))!;
            var feedback = typeof(CatalogStampImageSource).GetMethod("SetViewRefining");
            preparation.UpdateDocument(doc, source, static () => { });
            var fit = Settle(768d / 32768, "fit");
            var fitMs = watch.Elapsed.TotalMilliseconds;
            var fitHash = Pixels();
            var fitIo = source.DerivedIoDiagnostics;
            var fitHeaders = source.DerivedStatistics.MetadataEntries;
            var fitRequests = HeaderRequests(source);
            Console.WriteLine($"COLDSTART {count} fit preparation={preparation.Statistics} allocations={preparation.AllocatedBytes}");
            watch.Restart();
            var sharp = Settle(.5, "wheel");
            var sharpMs = watch.Elapsed.TotalMilliseconds;
            var sharpHash = Pixels();
            Settle(.5, "wheel");
            Assert.Equal(sharpHash, Pixels());
            var requests = HeaderRequests(source);
            Assert.Empty(requests.Except(used.Select(r => r.Sha256), StringComparer.OrdinalIgnoreCase));
            Console.WriteLine($"COLDSTART {count} visibleAssets={visibleAssets.Count} newWheelHeaders={requests.Except(fitRequests).Count()} newWheelHeadersOutsideVisible={requests.Except(fitRequests).Count(sha => !visibleAssets.Contains(sha))} nonDocumentHeaders=0");
            Console.WriteLine($"COLDSTART objects={count} indexed={indexed} library={records.Length} used={used.Length} fitMs={fitMs:F1} wheelMs={sharpMs:F1} fitHeaders={fitHeaders} headers={source.DerivedStatistics.MetadataEntries} fitIo={fitIo} io={source.DerivedIoDiagnostics} fitExact={fit.OverviewExactLayer} sharpCalls={sharp.DrawCalls} fitPixels={fitHash} sharpPixels={sharpHash}");
            Assert.InRange(source.DerivedStatistics.MetadataEntries, 1, used.Length);
            // Only every fiftieth asset has a >=4px stamp at fit: exactly 3000/gcd(3000,50)=60.
            // The other 2940 need a colour, not a full header. Old builds ignore the optional library palette.
            if (indexed) Assert.InRange(fitHeaders, 1, 60);
            // test-change: 6 s is >3x the slowest fixed cold fit on the reference Mac (~1.9 s),
            // allowing startup/scheduling noise while rejecting cam-stretch's ~9.4 s million-object fit.
            Assert.InRange(fitMs, 0, 6_000);
            Assert.InRange(sharpMs, 0, 10_000);
            Assert.Equal(0, source.DerivedIoDiagnostics.ReadsOnCanvasThread);
            Assert.Equal(0, source.DerivedIoDiagnostics.SourcePreparations);

            string Pixels()
            {
                using var snapshot = surface.Snapshot();
                using var pixels = SKBitmap.FromImage(snapshot);
                return Convert.ToHexStringLower(SHA256.HashData(pixels.Bytes));
            }

            RenderStats Settle(double scale, string phase)
            {
                var timer = Stopwatch.StartNew();
                RenderStats last = new();
                var frames = 0;
                var maxFrameMs = 0d;
                do
                {
                    var frameStarted = Stopwatch.GetTimestamp();
                    var c = surface.Canvas;
                    c.ResetMatrix(); c.Clear(SKColors.White);
                    c.Scale((float)scale);
                    var left = 16384 - 384 / scale;
                    c.Translate((float)-left, (float)-left);
                    tiles.BeginFrame(); atlas.BeginFrame();
                    using (source.BeginFrame())
                    {
                        last = new RenderStats();
                        // Mirror the production canvas's frame feedback. Reflection keeps this exact same
                        // harness runnable on cam-stretch, which has no such scheduling feedback yet.
                        SceneRenderer.Render(c, doc, new RenderOptions
                        {
                            Stamps = source, ObjectCache = tiles, AtlasCache = atlas, LayerImages = layers,
                            OverviewPreparation = preparation, Stats = last, ViewScale = scale,
                            VisibleWorld = new(left, left, left + 768 / scale, left + 768 / scale),
                            LodTier = scale < .125 ? RenderLodTier.Overview : RenderLodTier.Exact,
                            RequestRefinedFrame = static () => { },
                        });
                        feedback?.Invoke(source, [last.DetailTilesPending > 0 || last.UnresolvedImages > 0 || source.ImageRequestsPending]);
                        c.Flush();
                    }
                    tiles.EndFrame(); atlas.EndFrame();
                    maxFrameMs = Math.Max(maxFrameMs, Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds);
                    frames++;
                    Assert.Null(preparation.Statistics.Error);
                    if (Complete()) break;
                    Thread.Sleep(5);
                } while (timer.Elapsed.TotalSeconds < 30);
                Console.WriteLine($"COLDSTART {count} {phase} {timer.Elapsed.TotalMilliseconds:F1}ms frames={frames} maxFrameMs={maxFrameMs:F1} drawn={last.ObjectsDrawn} pending={last.DetailTilesPending} unresolved={last.UnresolvedImages} queues={source.DerivedStatistics}");
                if (!Complete())
                    Console.WriteLine(JsonSerializer.Serialize(new { source.CacheStatistics, source.SampleStatistics,
                        Mips = source.ReadMipDiagnostics().GroupBy(m => new { m.Step, m.Level, m.Pending, m.Resident, m.RequestedLastFrame })
                            .Select(g => new { g.Key, Count = g.Count() }).ToArray() }));
                Assert.True(Complete(), $"{phase} failed to complete");
                return last;

                bool Complete() => phase == "wheel"
                    // test-change: first SHARP VISIBLE view, not completion of its optional coarse seed.
                    // The viewport intersects exactly four ideal detail tiles. Stand-ins or uncovered tiles
                    // never qualify, even when they paint something; each ideal tile has pinned exact mips.
                    ? (last.ObjectTilesDrawn == 4 && last.DetailTilesStandIn == 0 && last.DetailTilesUncovered == 0
                        && last.UnresolvedImages == 0) || last.LayerImagesKept > 0
                    : last.ObjectsDrawn == count && last.DetailTilesPending == 0 && last.UnresolvedImages == 0
                        && !source.ImageRequestsPending;
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static HashSet<string> HeaderRequests(CatalogStampImageSource source)
    {
        var store = (StampDerivedAssetStore)typeof(CatalogStampImageSource).GetField("_derived", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
        return ((ConcurrentDictionary<string, TaskCompletionSource<StampDerivedAssetStore.Header?>>)typeof(StampDerivedAssetStore)
            .GetField("_requests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static MapDocument Document(int count, AssetRecord[] used, AssetRecord[] references)
    {
        var random = new Random(147);
        var objects = new SceneObject[count];
        for (var i = 0; i < count; i++)
        {
            var side = i % 50 == 0 ? 256 : 24;
            objects[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "owner mountain",
                random.NextDouble() * (32768 - side), random.NextDouble() * (32768 - side), side, side,
                0, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(used[i % used.Length].Sha256) };
        }
        return new MapDocument(DocumentId.New(), "cold start", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(32768, 32768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "mountains", true, false, 1, objects)],
            references.Select(r => new MapAssetReference(r.Sha256, r.DisplayName, "image/png", "png",
                ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "owner fixture", "owner", catalogId: r.Sha256)).ToArray());
    }

    // Each PNG has the same owner art and a distinct trailing identity marker, never invented art.
    // Write valid portable files directly: preparing lazy encoded SKImages is a known Mac-only failure.
    internal static AssetRecord[] SeedLibrary(string root, int count, bool indexed = false)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Map.slnx"))) repo = repo.Parent;
        var png = File.ReadAllBytes(Path.Combine(repo!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"));
        using var bitmap = SKBitmap.Decode(png)!;
        using var image = SKImage.FromBitmap(bitmap);
        var color = (uint)SceneRenderer.StampOverviewColor(image);
        var entries = new List<object>();
        using var data = new MemoryStream();
        SKImage current = image;
        try
        {
            for (var level = 0; level <= 9; level++)
            {
                var side = 512 >> level;
                if (level > 0)
                {
                    using var resized = new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
                    using var pixmap = resized.PeekPixels();
                    Assert.True(current.ScalePixels(pixmap, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)));
                    var next = SKImage.FromBitmap(resized);
                    if (!ReferenceEquals(current, image)) current.Dispose();
                    current = next;
                }
                using var raw = new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
                Assert.True(current.ReadPixels(raw.Info, raw.GetPixels(), raw.RowBytes, 0, 0));
                var pixels = raw.Bytes;
                var offset = (int)data.Length;
                using (var compressed = new BrotliStream(data, CompressionLevel.Fastest, leaveOpen: true)) compressed.Write(pixels);
                entries.Add(new { Step = 0, Level = level, Width = side, Height = side, Offset = offset,
                    Length = (int)data.Length - offset, Hash = Convert.ToHexStringLower(SHA256.HashData(pixels)) });
            }
        }
        finally { if (!ReferenceEquals(current, image)) current.Dispose(); }
        var payload = data.ToArray();
        Directory.CreateDirectory(Path.Combine(root, "originals"));
        Directory.CreateDirectory(Path.Combine(root, "derived", "v1"));
        var records = new List<AssetRecord>();
        var colors = new byte[48 + count * 68];
        BinaryPrimitives.WriteUInt32LittleEndian(colors, 0x50434F4C);
        BinaryPrimitives.WriteInt32LittleEndian(colors.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(colors.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(colors.AsSpan(12), count);
        for (var i = 0; i < count; i++)
        {
            var encoded = new byte[png.Length + 4];
            png.CopyTo(encoded, 0); BitConverter.GetBytes(i).CopyTo(encoded, png.Length);
            var sha = Convert.ToHexStringLower(SHA256.HashData(encoded));
            var relative = "originals/" + sha + ".png";
            var original = Path.Combine(root, relative);
            File.WriteAllBytes(original, encoded);
            var stamp = new StampDerivedAssetStore.FileStamp(relative, encoded.Length, File.GetLastWriteTimeUtc(original).Ticks);
            var offset = 16 + i * 68;
            Convert.FromHexString(sha).CopyTo(colors, offset);
            BinaryPrimitives.WriteUInt32LittleEndian(colors.AsSpan(offset + 32), color);
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { stamp, stamp }
                .Select(s => new { s.Path, s.Length }))).CopyTo(colors, offset + 36);
            var header = new StampDerivedAssetStore.Header(1, sha, 512, 512, color, stamp, stamp, 1, 9, new string('0', 64));
            var json = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Header = header, Sources = new[] { stamp },
                Alpha32 = new byte[1024], Entries = entries, SourceHashes = new[] { sha } });
            using (var file = File.Create(Path.Combine(root, "derived", "v1", sha + ".mapderived")))
            using (var writer = new BinaryWriter(file))
            {
                writer.Write("MAPDRV01"u8); writer.Write(json.Length); writer.Write(SHA256.HashData(json));
                writer.Write(json); writer.Write(payload);
            }
            records.Add(new AssetRecord { Sha256 = sha, DisplayName = "owner mountain " + i, OriginalFileName = i + ".png",
                MediaType = "image/png", Extension = "png", Width = 512, Height = 512, HasAlpha = true,
                ByteLength = encoded.Length, ImportedUtc = DateTimeOffset.UnixEpoch, Provenance = new(),
                StoredRelativePath = relative, ThumbnailRelativePath = "thumbnails/" + sha + ".png" });
        }
        AssetLibraryStore.Save(Path.Combine(root, "catalog.json"), new AssetLibraryDocument { Records = records });
        if (indexed)
        {
            SHA256.HashData(colors.AsSpan(0, colors.Length - 32)).CopyTo(colors, colors.Length - 32);
            File.WriteAllBytes(Path.Combine(root, "derived", "v1", "overview-colors.bin"), colors);
        }
        return records.ToArray();
    }
}

// Separate classes let the in-process runner select one case per NEW PROCESS: cold JIT/static state
// as well as an empty machine cache. Ordinary discovery still runs exactly these four cases.
[Collection("OverviewExactLayers")]
public sealed class ColdStart100kIndexedTests
{
    [Fact] public Task Complete_fit_and_first_sharp_view() => ColdStartPipelineTests.Run(100_000, true);
}

[Collection("OverviewExactLayers")]
public sealed class ColdStartMillionIndexedTests
{
    [Fact] public Task Complete_fit_and_first_sharp_view() => ColdStartPipelineTests.Run(1_000_000, true);
}

[Collection("OverviewExactLayers")]
public sealed class ColdStart100kLegacyTests
{
    [Fact] public Task Complete_fit_and_first_sharp_view() => ColdStartPipelineTests.Run(100_000, false);
}

[Collection("OverviewExactLayers")]
public sealed class ColdStartMillionLegacyTests
{
    [Fact] public Task Complete_fit_and_first_sharp_view() => ColdStartPipelineTests.Run(1_000_000, false);
}
