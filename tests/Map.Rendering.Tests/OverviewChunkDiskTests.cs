using System.Collections.Immutable;
using System.Diagnostics;
using Map.Core.Documents;
using SkiaSharp;
using Xunit.Abstractions;

namespace Map.Rendering.Tests;

/// <summary>
/// The whole-map view of a map opened again starts from the chunk pictures of the previous run (owner 2026-11-16:
/// «кеш всегда тёплый, прямо горячий»): a second open with new caches and the same disk root rasters none of the
/// unchanged chunks and draws the same pixels; an edit re-rasters only the chunks it touches; a damaged file is
/// never drawn; the frame thread never touches the disk; the key changes with every input of the raster.
/// The pictures are the owner's mountains (fixtures 01 and 02), a third of them painted with a shadow.
/// </summary>
[Collection(RealTimeSettleCollection.Name)]
public sealed class OverviewChunkDiskTests(ITestOutputHelper output) : IDisposable
{
    private const double Extent = 32_768; // 4 x 4 overview chunks of 8 192
    private const double Scale = 512 / Extent;
    private const int Chunks = 16;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-overview-chunks", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_second_open_adopts_every_chunk_from_disk_without_rastering_and_draws_the_same_pixels()
    {
        var objects = Objects(4_000, seed: 5);
        var first = Open(objects, "first");
        Assert.Equal(Chunks, first.Disk.Rastered);
        Assert.Equal(0, first.Disk.Loaded);
        Assert.Equal(Chunks, first.Disk.Saved);
        Assert.Equal(Chunks, Directory.GetFiles(Folder, "*.chunk").Length);

        var second = Open(objects, "second");
        Assert.Equal(0, second.Disk.Rastered);
        Assert.Equal(Chunks, second.Disk.Loaded);
        Assert.Equal(0, second.Disk.Saved);
        Assert.Equal(first.Pixels, second.Pixels);

        // And the same pixels as a fresh raster without any disk tier.
        var fresh = Open(objects, "no disk", disk: false);
        Assert.Equal(fresh.Pixels, second.Pixels);
    }

    [Fact]
    public void An_edit_rasters_only_the_chunks_it_touches_and_the_rest_comes_from_disk()
    {
        var objects = Objects(4_000, seed: 7);
        Open(objects, "before the edit");
        // Move one painted mountain a little and recolour nothing else: one chunk changes.
        var index = Array.FindIndex(objects, o => o.Stamp!.ShadowOpacity > 0 && o.X % 8192 is > 1000 and < 7000);
        var edited = (SceneObject[])objects.Clone();
        edited[index] = Geometry(objects[index], x: objects[index].X + 40, y: objects[index].Y + 25);
        var after = Open(edited, "after the edit");
        Assert.Equal(1, after.Disk.Rastered);
        Assert.Equal(Chunks - 1, after.Disk.Loaded);
        Assert.Equal(Open(edited, "edit, no disk", disk: false).Pixels, after.Pixels);
        // The edit's chunk is now kept too.
        Assert.Equal(0, Open(edited, "edit reopened").Disk.Rastered);
    }

    [Fact]
    public void A_damaged_or_truncated_file_is_never_drawn_and_is_rebuilt()
    {
        var objects = Objects(4_000, seed: 11);
        var first = Open(objects, "first");
        var files = Directory.GetFiles(Folder, "*.chunk").Order(StringComparer.Ordinal).ToArray();
        // One flipped byte in the middle of the pixels, and one file cut short.
        var flipped = File.ReadAllBytes(files[0]);
        flipped[OverviewChunkDiskCache.HeaderBytes + (flipped.Length - OverviewChunkDiskCache.HeaderBytes) / 2] ^= 0x5A;
        File.WriteAllBytes(files[0], flipped);
        var cut = File.ReadAllBytes(files[1]);
        File.WriteAllBytes(files[1], cut[..(cut.Length / 2)]);

        var second = Open(objects, "damaged", inspect: store => Assert.Equal(2, store.Statistics.Damaged));
        Assert.Equal(2, second.Disk.Rastered);
        Assert.Equal(Chunks - 2, second.Disk.Loaded);
        Assert.Equal(2, second.Disk.Saved);
        Assert.Equal(first.Pixels, second.Pixels);
        var third = Open(objects, "repaired");
        Assert.Equal(0, third.Disk.Rastered);
    }

    [Fact]
    public void The_frame_thread_never_touches_the_disk_tier()
    {
        var objects = Objects(2_000, seed: 13);
        var io = new List<(string Operation, int Thread)>();
        void Record(OverviewChunkDiskCache store) => store.BeforeIo = operation =>
        {
            lock (io) io.Add((operation, Environment.CurrentManagedThreadId));
        };
        var frameThread = Environment.CurrentManagedThreadId;
        Open(objects, "write", prepare: Record);
        Open(objects, "read", prepare: Record);
        lock (io)
        {
            output.WriteLine($"disk operations: {string.Join(", ", io.GroupBy(i => i.Operation).Select(g => $"{g.Key}={g.Count()}"))}");
            Assert.Contains(io, i => i.Operation == "save");
            Assert.Contains(io, i => i.Operation == "load");
            Assert.DoesNotContain(io, i => i.Thread == frameThread);
        }
    }

    [Fact]
    public void The_key_changes_with_every_input_of_the_raster()
    {
        var shas = new[] { Sha(1), Sha(2) };
        var source = new Mountains(null, identity: true);
        var plain = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "dot", 1000, 1000, 32, 32, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        { Stamp = new StampStyle(shas[0]) };
        var painted = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "painted", 3000, 2000, 32, 32, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        { Stamp = new StampStyle(shas[1]) { ShadowOpacity = .5 } };
        var colors = ImmutableDictionary<string, SKColor>.Empty.Add(shas[0], new SKColor(90, 110, 70)).Add(shas[1], new SKColor(120, 100, 80));
        const int zoom = -24;

        byte[]? Key(SceneObject[] items, ImmutableDictionary<string, SKColor>? palette = null, int bucket = zoom,
            StampForegroundSurface? foreground = null, IStampImageSource? from = null, string version = "v-test")
        {
            var chunk = new SceneRenderer.OverviewChunk(0, 0, [.. items.Select((item, i) => ((long)i, item))]);
            var p = new SceneRenderer.OverviewPalette(palette ?? colors, shas);
            var variant = chunk.GetVariant(bucket, Math.Pow(2, bucket / 4d), p.Colors, chunk.GetTintRevision(p));
            return SceneRenderer.OverviewChunkDiskKey(variant, bucket, foreground, from ?? source, version);
        }

        var baseline = Key([plain, painted]);
        Assert.NotNull(baseline);
        // Determinism: the same content in new objects (another run) gives the same key.
        Assert.Equal(baseline, Key([plain with { Id = ObjectId.New() }, painted with { Stamp = painted.Stamp! with { } }]));

        var foreground = new StampForegroundSurface(TileMap.Empty(4).With(new TileKey(0, 0), new byte[TileMap.TileSize * TileMap.TileSize * 4]));
        var variants = new (string Input, byte[]? Key)[]
        {
            ("dot position", Key([Geometry(plain, x: 1001), painted])),
            ("dot opacity", Key([plain with { ObjectOpacity = .5 }, painted])),
            ("dot asset", Key([plain with { Stamp = new StampStyle(shas[1]) }, painted])),
            ("palette colour", Key([plain, painted], colors.SetItem(shas[0], new SKColor(91, 110, 70)))),
            ("painted position", Key([plain, Geometry(painted, y: 2001)])),
            ("painted size", Key([plain, Geometry(painted, width: 33)])),
            ("painted rotation", Key([plain, Geometry(painted, rotation: 15)])),
            ("painted opacity", Key([plain, painted with { ObjectOpacity = .75 }])),
            ("painted shadow", Key([plain, painted with { Stamp = painted.Stamp! with { ShadowOpacity = .6 } }])),
            ("painted flip", Key([plain, painted with { Stamp = painted.Stamp! with { FlipHorizontal = true } }])),
            ("painted hue", Key([plain, painted with { Stamp = painted.Stamp! with { Hue = 10 } }])),
            ("painted tint", Key([plain, painted with { Stamp = painted.Stamp! with { Tint = "#FF806040" } }])),
            ("order", Key([painted, plain])),
            ("object added", Key([plain, painted, Geometry(plain, x: 5000) with { Id = ObjectId.New() }])),
            ("zoom bucket", Key([plain, painted], bucket: zoom + 1)),
            ("render version", Key([plain, painted], version: "v-test-2")),
            ("asset pixels", Key([plain, painted], from: new Mountains(null, identity: true, generation: 2))),
        };
        foreach (var (input, key) in variants)
        {
            Assert.True(key is not null, $"{input}: no key");
            Assert.False(key.AsSpan().SequenceEqual(baseline), $"the key did not change with: {input}");
        }
        Assert.Equal(variants.Length, variants.Select(v => Convert.ToHexString(v.Key!)).Append(Convert.ToHexString(baseline!)).Distinct().Count() - 1);

        // Not kept at all: a painted asset whose pixels the source cannot name, a clip mask over a foreground.
        Assert.Null(Key([plain, painted], from: new Mountains(null, identity: false)));
        var masked = painted with { Stamp = painted.Stamp! with { ClipMaskEnabled = true, MaskAssetSha256 = shas[0] } };
        Assert.Null(Key([plain, masked], foreground: foreground));
        Assert.NotNull(Key([plain, masked], foreground: StampForegroundSurface.CreateEmpty()));
        // Dots alone need no pixel identity: their colour is in the key.
        Assert.NotNull(Key([plain], from: new Mountains(null, identity: false)));
    }

    private string Folder => Path.Combine(_root, "v1");

    private sealed record Opened(byte[] Pixels, (long Loaded, long Rastered, long Saved) Disk);

    /// <summary>One program run: a new document instance (new plan), new caches and worker, the same disk root.</summary>
    private Opened Open(SceneObject[] objects, string phase, bool disk = true,
        Action<OverviewChunkDiskCache>? prepare = null, Action<OverviewChunkDiskCache>? inspect = null)
    {
        var store = disk ? new OverviewChunkDiskCache(_root) : null;
        if (store is not null) prepare?.Invoke(store);
        using var source = new Mountains(store, identity: true);
        var document = Document(objects);
        using var cache = new ObjectOverviewCache();
        using var preparation = new SceneRenderer.OverviewPreparation(static () => false);
        using var surface = SKSurface.Create(new SKImageInfo(512, 512, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        var watch = Stopwatch.StartNew();
        RenderStats stats;
        var frames = 0;
        do
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"{phase}: the whole-map view did not complete");
            if (frames > 0) Thread.Sleep(4);
            stats = Frame(surface, document, cache, preparation, source);
            frames++;
            Assert.Null(preparation.Statistics.Error);
        }
        while (stats.DetailTilesPending > 0 || stats.OverviewChunks < Chunks || preparation.VisibleWorkPending);
        var result = new Opened(Pixels(surface), preparation.DiskStatistics);
        // The program closes: the queued pictures reach the disk (CatalogStampImageSource.DisposeAsync).
        if (store is not null) { Assert.True(store.Flush(TimeSpan.FromSeconds(30))); inspect?.Invoke(store); }
        output.WriteLine($"{phase}: frames={frames} wall={watch.Elapsed.TotalMilliseconds:F0} ms disk={result.Disk} store={store?.Statistics}");
        return result;
    }

    private static RenderStats Frame(SKSurface surface, MapDocument document, ObjectOverviewCache cache,
        SceneRenderer.OverviewPreparation preparation, IStampImageSource source)
    {
        var canvas = surface.Canvas;
        canvas.ResetMatrix();
        canvas.Clear(SKColors.White);
        canvas.Scale((float)Scale);
        var stats = new RenderStats();
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
        return stats;
    }

    private static byte[] Pixels(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        return bitmap.Bytes;
    }

    private static SceneObject Geometry(SceneObject o, double? x = null, double? y = null, double? width = null, double? rotation = null) =>
        new SceneObject(o.Id, o.Kind, o.Name, x ?? o.X, y ?? o.Y, width ?? o.Width, o.Height, rotation ?? o.RotationDegrees,
            o.FillColor, o.StrokeColor, o.StrokeWidth, o.Visible, o.Locked) { Stamp = o.Stamp, ObjectOpacity = o.ObjectOpacity };

    internal static string Sha(int fixture) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([(byte)fixture, 77]));

    /// <summary>Small owner mountains over all 16 chunks; every third painted with a shadow (drawn into the raster).</summary>
    internal static SceneObject[] Objects(int count, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count).Select(i =>
        {
            var side = 24 + random.NextDouble() * 40;
            var style = new StampStyle(Sha(1 + i % 2));
            if (i % 3 == 0) style = style with { ShadowOpacity = .45, ShadowBlur = 4 };
            return new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "owner mountain",
                256 + random.NextDouble() * (Extent - 512), 256 + random.NextDouble() * (Extent - 512),
                side, side * .8, 0, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = style };
        }).ToArray();
    }

    internal static MapDocument Document(IReadOnlyList<SceneObject> objects) => new(DocumentId.New(), "hot overview",
        DateTimeOffset.UnixEpoch, new CanvasDefinition((int)Extent, (int)Extent, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"),
         new ObjectLayer(LayerId.New(), "mountains", true, false, 1, objects)],
        new[] { 1, 2 }.Select(i => new MapAssetReference(Sha(i), "owner mountain", "image/png", "png", ProjectAssetStorage.Linked,
            DateTimeOffset.UnixEpoch, "owner fixture", "owner", catalogId: $"owner-mountain-0{i}")).ToArray());

    /// <summary>The owner's mountains 01 and 02 as a ready source, with a disk tier and pixel identities.</summary>
    internal sealed class Mountains : IStampImageSource, IDisposable
    {
        private readonly Dictionary<string, SKImage> _images = new(StringComparer.OrdinalIgnoreCase);
        private readonly IOverviewChunkStore? _store;
        private readonly bool _identity;
        private readonly int _generation;

        public Mountains(IOverviewChunkStore? store, bool identity, int generation = 1, int fixtures = 2)
        {
            _store = store; _identity = identity; _generation = generation;
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
            foreach (var i in Enumerable.Range(1, fixtures))
                _images[Sha(i)] = SKImage.FromEncodedData(Path.Combine(root!.FullName, "tests", "fixtures", "assets", $"owner-mountain-0{i}.png"))!
                    .ToRasterImage(ensurePixelData: true);
        }

        public long? OverviewColorRevision => 1;
        public IOverviewChunkStore? OverviewChunks => _store;
        public string? PixelIdentity(string sha256) => _identity ? $"owner-fixture:{sha256}:{_generation}" : null;
        public SKImage? TryGetImage(string sha256) => _images.GetValueOrDefault(sha256);
        public SKColor? TryGetOverviewColor(string sha256) =>
            _images.TryGetValue(sha256, out var image) ? SceneRenderer.StampOverviewColor(image) : null;
        public void Dispose() { foreach (var image in _images.Values) image.Dispose(); }
    }
}
