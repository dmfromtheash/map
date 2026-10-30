using System.Collections.Concurrent;
using System.Text.Json;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>
/// Parallel export (owner, 2026-10-29: "faster, the pixels must not change"): the same request
/// drawn by one worker and by several must give the same pixels, and package tiles the same bytes.
/// </summary>
public sealed class ParallelExportTests
{
    public static TheoryData<string, int, int, bool> Cases()
    {
        var data = new TheoryData<string, int, int, bool>();
        foreach (var map in new[] { "stamps", "land" })
        {
            data.Add(map, 320, 160, false);
            data.Add(map, 2000, 1500, false);
            data.Add(map, 2000, 1500, true);
        }
        // The real export source with 300 embedded images (more than its decoder cache holds) drawn at
        // several sizes, and a hex map with rivers and roads across tiles (Astra's review, 2026-10-29).
        data.Add("embedded", 640, 320, false);
        data.Add("embedded", 1920, 960, true);
        data.Add("hex", 2000, 1500, false);
        // An illustrated river and a road: their banks, reeds and shoulders are seeded per channel and branch.
        data.Add("water", 2000, 1500, false);
        // Styled maps keep the serial render (see ExportParallelResources); compression is still parallel.
        // 640×480: at 2000×1500 these three took 7–12 min each and pushed CI past its 40-minute limit;
        // parallel compression of many blocks is covered by the plain 2000×1500 cases above.
        foreach (var map in new[] { "blaeu", "impasto", "moonlight" }) data.Add(map, 640, 480, false);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Parallel_png_and_tile_package_keep_every_pixel_of_the_serial_export(string map, int width, int height, bool transparent)
    {
        using var temp = new Temp();
        var document = Map(map);
        // The style renderer gathers all stamp images before drawing, so only the plain stamp map gets
        // the source that disposes its previous image on every request.
        using IDisposable stampsOwner = map switch
        {
            "stamps" => new EvictingSource(),
            "embedded" => new Map.Presentation.Services.ExportStampImageSource(document, null, null),
            _ => new StableSource(),
        };
        var stamps = (IStampImageSource)stampsOwner;
        var tile = width > 1000 ? 256 : 64;

        var serialPng = Path.Combine(temp.Path, "serial.png");
        var parallelPng = Path.Combine(temp.Path, "parallel.png");
        await Export(document, stamps, serialPng, ExportOutputMode.SingleImage, width, height, transparent, tile, 1, null);
        var threads = new ConcurrentDictionary<int, byte>();
        await Export(document, stamps, parallelPng, ExportOutputMode.SingleImage, width, height, transparent, tile, 4,
            a => { threads.TryAdd(Environment.CurrentManagedThreadId, 0); if (a.Purpose == ExportSurfacePurpose.TileWithBleed) Thread.Sleep(2); });
        if (document.Style is null)
            Assert.True(threads.Count > 1, "Several render workers must have drawn, not the memory fallback.");
        else
            Assert.Single(threads); // a styled map is drawn tile after tile
        Assert.Equal(Pixels(serialPng), Pixels(parallelPng));

        var serialTiles = Path.Combine(temp.Path, "serial-tiles");
        var parallelTiles = Path.Combine(temp.Path, "parallel-tiles");
        await Export(document, stamps, serialTiles, ExportOutputMode.TilePackage, width, height, transparent, tile, 1, null);
        await Export(document, stamps, parallelTiles, ExportOutputMode.TilePackage, width, height, transparent, tile, 4, null);
        var files = Directory.GetFiles(serialTiles).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(files, Directory.GetFiles(parallelTiles).Select(Path.GetFileName).Order().ToArray());
        foreach (var file in files)
            Assert.Equal(File.ReadAllBytes(Path.Combine(serialTiles, file!)), File.ReadAllBytes(Path.Combine(parallelTiles, file!)));
        var manifest = JsonSerializer.Deserialize<TilePackageManifest>(File.ReadAllText(Path.Combine(parallelTiles, "manifest.json")))!;
        Assert.Equal(manifest.Tiles.OrderBy(t => t.Y).ThenBy(t => t.X).Select(t => t.File), manifest.Tiles.Select(t => t.File));
    }

    [Fact]
    public async Task Too_small_a_stamp_reserve_falls_back_to_the_serial_route_with_the_same_pixels()
    {
        using var temp = new Temp();
        var document = Map("stamps");
        using var stamps = new EvictingSource();
        var serial = Path.Combine(temp.Path, "serial.png");
        var fallback = Path.Combine(temp.Path, "fallback.png");
        await Export(document, stamps, serial, ExportOutputMode.SingleImage, 320, 160, false, 64, 1, null);
        await Export(document, stamps, fallback, ExportOutputMode.SingleImage, 320, 160, false, 64, 4, null, stampReserve: 1);
        Assert.Equal(Pixels(serial), Pixels(fallback));
        Assert.Equal(2, Directory.GetFiles(temp.Path).Length);
    }

    private static async Task Export(MapDocument document, IStampImageSource stamps, string path, ExportOutputMode mode,
        int width, int height, bool transparent, int tile, int workers, Action<ExportSurfaceAllocation>? observer,
        long stampReserve = 64L * 1024 * 1024)
    {
        var engine = new ImageExportEngine(new ExportEngineSettings
        {
            TileEdge = tile, TileCacheCapacity = 2, RenderParallelism = workers, CompressionParallelism = workers,
            ConcurrentStampByteCapacity = stampReserve, SurfaceAllocated = observer,
        });
        var plan = engine.Plan(document, new ExportRequest(ExportImageFormat.Png, ExportAreaMode.WholeCanvas,
            null, width, height, mode, transparent, false));
        await engine.ExportAsync(document, plan, path, stamps);
    }

    private static byte[] Pixels(string path)
    {
        using var bitmap = SKBitmap.Decode(path, new SKImageInfo(0, 0, SKColorType.Rgba8888, SKAlphaType.Unpremul))
            ?? SKBitmap.Decode(path);
        Assert.NotNull(bitmap);
        using var rgba = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Assert.True(bitmap.CopyTo(rgba, SKColorType.Rgba8888));
        return rgba.GetPixelSpan().ToArray();
    }

    private static MapDocument Map(string name) => name switch
    {
        "stamps" => Stamps(),
        "land" => Styled(null),
        "embedded" => Embedded(),
        "hex" => Hex(),
        "water" => Water(),
        "blaeu" => Styled(MapStylePreset.OldMapBlaeu),
        "impasto" => Styled(MapStylePreset.OilImpastoRhythm),
        _ => Styled(MapStylePreset.MoonlightHasui),
    };

    private static MapDocument Stamps() => new(DocumentId.New(), "parallel", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(320, 160, "px", "#FF102030"),
        [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FF102030"),
         new ObjectLayer(LayerId.New(), "objects", true, false, .8,
            [.. Enumerable.Range(0, 30).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
                "image", i * 11 - 20, i % 4 * 24 - 10, 80, 80, i % 3 * 13,
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle((i % 3).ToString()) })])]);

    /// <summary>300 different embedded images, each placed twice at different sizes (different detail steps).</summary>
    private static MapDocument Embedded()
    {
        var assets = new List<MapAssetReference>();
        var objects = new List<SceneObject>();
        for (var i = 0; i < 300; i++)
        {
            using var bitmap = new SKBitmap(96 + i % 7, 96 + i % 5);
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                bitmap.SetPixel(x, y, (x + y + i) % 6 < 3 ? new SKColor((byte)(i * 37), (byte)(x * 5), (byte)(y * 3), 255) : SKColors.Transparent);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            var bytes = encoded.ToArray();
            var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            assets.Add(new MapAssetReference(sha, "image " + i, "image/png", ".png", ProjectAssetStorage.Embedded,
                DateTimeOffset.UnixEpoch, "test", "Map tests", bytes));
            foreach (var size in new[] { 14 + i % 9, 30 + i % 13 })
                objects.Add(new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "image", i * 37 % 620, (i * 53 + size * 7) % 300,
                    size, size, i % 4 * 9, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(sha) });
        }
        return new MapDocument(DocumentId.New(), "embedded", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(640, 320, "px", "#FF20384A"),
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FF20384A"),
             new ObjectLayer(LayerId.New(), "objects", true, false, 1, [.. objects])],
            [.. assets]);
    }

    /// <summary>A hex map with a winding river, a road and a trail crossing several export tiles.</summary>
    private static MapDocument Hex()
    {
        var document = HexMapFactory.Create("Гексы", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"),
            HexOrientation.Pointy, 24, 14, 10, "#FFF2E9DC");
        var layer = document.Layers.OfType<HexLayer>().Single();
        var layout = layer.Board.Layout;
        HexCoord At(int c, int r) => layout.FromOffset(new HexOffset(c, r));
        var river = new HexPath(HexPathId.New(), HexPathKind.River, HexRouteStyle.Meander,
            [.. Enumerable.Range(0, 10).Select(r => At(3, r))], []);
        var road = new HexPath(HexPathId.New(), HexPathKind.Road, HexRouteStyle.Centers,
            [.. Enumerable.Range(0, 14).Select(c => At(c, 5))], []);
        var trail = new HexPath(HexPathId.New(), HexPathKind.Trail, HexRouteStyle.Meander,
            [.. Enumerable.Range(0, 8).Select(i => At(9, i))], []);
        var cells = Enumerable.Range(0, 14).SelectMany(c => Enumerable.Range(0, 10).Select(r => (c, r)))
            .Where(p => (p.c * 7 + p.r * 3) % 5 == 0)
            .Select(p => new KeyValuePair<HexCoord, HexCell?>(At(p.c, p.r), HexCell.Of(p.r % 2 == 0 ? "starter/mountains" : "starter/forest")));
        var full = layer with { Paths = [river, road, trail], Board = layer.Board.With([.. cells]) };
        return document.WithLayers([.. document.Layers.Select(l => l.Id == layer.Id ? full : l)]);
    }

    /// <summary>The plain land map with an illustrated river and a country road winding across tiles.</summary>
    private static MapDocument Water()
    {
        var river = new River
        {
            Name = "Река", Style = RiverStyle.Illustrated(),
            Channels = [RiverEditor.NewChannel([new(20, 40), new(150, 120), new(260, 200), new(380, 260), new(500, 300)], 14)],
        };
        var road = RoadEditor.NewRoad([new(10, 330), new(160, 260), new(300, 300), new(500, 150)], 6, RoadStyle.Country(), "Тракт", seed: 7);
        var document = Styled(null);
        var water = new ObjectLayer(LayerId.New(), "Реки и дороги", true, false, 1, []) { Rivers = [river], Roads = [road] };
        return document.WithLayers([.. document.Layers, water]);
    }

    /// <summary>Land with an elliptic coast, shapes and stamps, under a program style or none.</summary>
    private static MapDocument Styled(MapStylePreset? preset)
    {
        var tiles = TileMap.Empty(1);
        for (var tx = 0; tx < 2; tx++)
        for (var ty = 0; ty < 2; ty++)
        {
            var coverage = new byte[256 * 256];
            for (var y = 0; y < 256; y++)
            for (var x = 0; x < 256; x++)
                coverage[y * 256 + x] = Math.Pow((x + tx * 256 - 256) / 200.0, 2)
                    + Math.Pow((y + ty * 256 - 192) / 130.0, 2) <= 1 ? (byte)255 : (byte)0;
            tiles = tiles.With(new TileKey(tx, ty), coverage);
        }
        var objects = new List<SceneObject>
        {
            new(ObjectId.New(), SceneObjectKind.Ellipse, "Форма", 220, 120, 90, 90, 0, "#FF667744", "#FF223322", 2, true, false),
        };
        objects.AddRange(Enumerable.Range(0, 12).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "image", 60 + i * 33, 90 + i % 4 * 40, 48, 48, i * 17, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle((i % 3).ToString()) }));
        return new MapDocument(DocumentId.New(), "Style export", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 384, "px", "#FF7F9FAE"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF7F9FAE"),
             new LandLayer(LayerId.New(), "Земля", true, false, 1, tiles, new LandEdgeStyle()),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [.. objects])])
            { Style = preset is { } p ? MapStyle.For(p) : null };
    }

    private interface IStampImageSourceDisposable : IStampImageSource, IDisposable;

    private static SKImage Picture(string sha256)
    {
        using var bitmap = new SKBitmap(32, 32);
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 32; x++)
            bitmap.SetPixel(x, y, new SKColor((byte)(int.Parse(sha256) * 70), (byte)(x * 7), (byte)(y * 7), (byte)(100 + x * 4)));
        return SKImage.FromBitmap(bitmap);
    }

    /// <summary>One image per hash for the whole export.</summary>
    private sealed class StableSource : IStampImageSourceDisposable
    {
        private readonly ConcurrentDictionary<string, SKImage> _images = new();
        public SKImage? TryGetImage(string sha256) => _images.GetOrAdd(sha256, Picture);
        public void Dispose() { foreach (var image in _images.Values) image.Dispose(); }
    }

    /// <summary>Disposes its previous image on every request, as a small-budget resolver can.</summary>
    private sealed class EvictingSource : IStampImageSourceDisposable
    {
        private readonly object _gate = new();
        private SKImage? _last;

        public SKImage? TryGetImage(string sha256)
        {
            lock (_gate)
            {
                _last?.Dispose();
                return _last = Picture(sha256);
            }
        }

        public void Dispose() => _last?.Dispose();
    }

    private sealed class Temp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "map-parallel-export-" + Guid.NewGuid().ToString("N"));
        public Temp() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
