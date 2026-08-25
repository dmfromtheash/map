using System.Diagnostics;
using System.Text.Json;
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
var result = new
{
    recordedUtc = DateTimeOffset.UtcNow,
    machine = new
    {
        os = Environment.OSVersion.ToString(),
        processorCount = Environment.ProcessorCount,
        framework = Environment.Version.ToString(),
    },
    canvas = "65536x65536",
    viewport = "1920x1080",
    objectCount = objects.Count,
    candidateCount = candidates.Count,
    drawnCount = stats.ObjectsDrawn,
    medianMs = Percentile(samples, 0.50),
    p95Ms = Percentile(samples, 0.95),
    gateMedianMs = 33.3,
    gatePassed = Percentile(samples, 0.50) <= 33.3,
};

var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
return result.gatePassed ? 0 : 2;

static double Percentile(IReadOnlyList<double> values, double percentile)
{
    var index = (int)Math.Ceiling(values.Count * percentile) - 1;
    return Math.Round(values[Math.Clamp(index, 0, values.Count - 1)], 3);
}
