using System.Diagnostics;
using System.Text.Json;
using Map.Assets;
using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

var objects = new List<SceneObject>(50_000);
for (var i = 0; i < 50_000; i++)
{
    var x = (i % 250) * 260.0;
    var y = (i / 250) * 260.0;
    objects.Add(new SceneObject(
        ObjectId.New(), SceneObjectKind.Rectangle, $"object-{i}",
        x, y, 32, 32, 0, "#FF557799", "#FF112233", 1, true, false));
}

var layer = new ObjectLayer(LayerId.New(), "50k objects", true, false, 1, objects);
var document = new MapDocument(
    DocumentId.New(), "benchmark", DateTimeOffset.UnixEpoch,
    new CanvasDefinition(65_536, 65_536, "px", "#FFFFFFFF"),
    [
        new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
        layer,
    ]);
var view = new WorldBounds(10_000, 10_000, 11_920, 11_080);
var candidates = ObjectSpatialIndex.For(layer).Query(view);
var stats = new RenderStats();
var samples = new List<double>();
var info = new SKImageInfo(1920, 1080, SKColorType.Rgba8888, SKAlphaType.Premul);
using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Cannot create benchmark surface.");

for (var iteration = 0; iteration < 35; iteration++)
{
    surface.Canvas.Clear(SKColors.Transparent);
    surface.Canvas.Save();
    surface.Canvas.Translate(-10_000, -10_000);
    var timer = Stopwatch.StartNew();
    SceneRenderer.Render(surface.Canvas, document, new RenderOptions
    {
        VisibleWorld = view,
        ViewScale = 1,
        Stats = stats,
    });
    surface.Canvas.Flush();
    timer.Stop();
    surface.Canvas.Restore();

    if (iteration >= 5)
    {
        samples.Add(timer.Elapsed.TotalMilliseconds);
    }
}

samples.Sort();
var assetRoot = Path.Combine(Path.GetTempPath(), "map-asset-benchmark-" + Guid.NewGuid().ToString("N"));
var catalog = new AssetCatalog(assetRoot);
var assetRecords = Enumerable.Range(0, 10_000).Select(index => new AssetRecord
{
    Sha256 = index.ToString("x64"),
    DisplayName = $"asset-{index:D5}",
    OriginalFileName = $"asset-{index:D5}.png",
    MediaType = "image/png",
    Extension = "png",
    Width = 256,
    Height = 256,
    HasAlpha = true,
    ByteLength = 4_096,
    ImportedUtc = DateTimeOffset.UnixEpoch.AddSeconds(index),
    Pack = index % 2 == 0 ? "even" : "odd",
    Tags = [index % 10 == 0 ? "forest" : "other"],
    Favorite = index % 100 == 0,
    Provenance = new AssetProvenance(),
    ThumbnailRelativePath = $"thumbnails/{index:x64}.png",
}).ToList();
catalog.ReplaceIndex(assetRecords);
var loadTimer = Stopwatch.StartNew();
var loadedCatalog = new AssetCatalog(assetRoot);
loadTimer.Stop();
_ = loadedCatalog.Search("forest", pack: "even");
var searchSamples = new List<double>();
for (var iteration = 0; iteration < 30; iteration++)
{
    var timer = Stopwatch.StartNew();
    _ = loadedCatalog.Search("forest", pack: "even");
    timer.Stop();
    searchSamples.Add(timer.Elapsed.TotalMilliseconds);
}

searchSamples.Sort();
var landTiles = TileMap.Empty(1);
for (var y = 0; y < 2; y++)
{
    for (var x = 0; x < 2; x++)
    {
        var coverage = new byte[TileMap.TileSize * TileMap.TileSize];
        Array.Fill(coverage, (byte)255);
        landTiles = landTiles.With(new TileKey(x, y), coverage);
    }
}

var land = new LandLayer(
    LayerId.New(), "benchmark land", true, false, 1, landTiles,
    new LandEdgeStyle { RippleCount = 3, RippleSpacing = 10, ShadowBlur = 8 });
var landDocument = new MapDocument(
    DocumentId.New(), "land benchmark", DateTimeOffset.UnixEpoch,
    new CanvasDefinition(65_536, 65_536, "px", "#FFFFFFFF"),
    [
        new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
        land,
    ]);
using var landCache = new TileImageCache(32);
var landPreviewSamples = MeasureLand(landDocument, landCache, fast: true, iterations: 20, warmup: 5);
var landExactSamples = MeasureLand(landDocument, landCache, fast: false, iterations: 10, warmup: 3);
var renderMedian = Percentile(samples, 0.50);
var searchP95 = Percentile(searchSamples, 0.95);
var assetLoadMs = Math.Round(loadTimer.Elapsed.TotalMilliseconds, 3);
var gatePassed = renderMedian <= 33.3 && searchP95 <= 100 && assetLoadMs <= 2_000;
var result = new
{
    recordedUtc = DateTimeOffset.UtcNow,
    machine = new
    {
        os = Environment.OSVersion.ToString(),
        processorCount = Environment.ProcessorCount,
        framework = Environment.Version.ToString(),
    },
    render = new
    {
        canvas = "65536x65536",
        viewport = "1920x1080",
        objectCount = objects.Count,
        candidateCount = candidates.Count,
        drawnCount = stats.ObjectsDrawn,
        medianMs = renderMedian,
        p95Ms = Percentile(samples, 0.95),
        gateMedianMs = 33.3,
    },
    assetCatalog = new
    {
        recordCount = loadedCatalog.Records.Count,
        metadataLoadMs = assetLoadMs,
        warmSearchP95Ms = searchP95,
        decodedOriginalCount = 0,
        gateLoadMs = 2_000,
        gateSearchP95Ms = 100,
    },
    land = new
    {
        sourceTileCount = landTiles.TileCount,
        fastPreviewMedianMs = Percentile(landPreviewSamples, 0.50),
        exactIdleMedianMs = Percentile(landExactSamples, 0.50),
        cacheEntries = landCache.Count,
        cacheCapacity = 32,
        geometryBytes = landTiles.TotalBytes,
    },
    gatePassed,
};

var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
Directory.Delete(assetRoot, recursive: true);
return gatePassed ? 0 : 2;

static double Percentile(IReadOnlyList<double> values, double percentile)
{
    var index = (int)Math.Ceiling(values.Count * percentile) - 1;
    return Math.Round(values[Math.Clamp(index, 0, values.Count - 1)], 3);
}

static List<double> MeasureLand(
    MapDocument document, TileImageCache cache, bool fast, int iterations, int warmup)
{
    var result = new List<double>();
    var info = new SKImageInfo(512, 512, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(info)!;
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        surface.Canvas.Clear(SKColors.Transparent);
        var timer = Stopwatch.StartNew();
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions
        {
            VisibleWorld = new WorldBounds(0, 0, 512, 512),
            ViewScale = 1,
            TileCache = cache,
            FastPreview = fast,
        });
        surface.Canvas.Flush();
        timer.Stop();
        if (iteration >= warmup)
        {
            result.Add(timer.Elapsed.TotalMilliseconds);
        }
    }

    result.Sort();
    return result;
}
