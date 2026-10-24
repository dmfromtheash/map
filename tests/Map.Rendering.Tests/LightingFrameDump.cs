using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// Look-at frames of the Свет section for Claude's visual check: set MAP_LIGHT_FRAMES to an output
/// folder and MAP_LIGHT_ASSETS to a folder of mountain PNGs named by their SHA-256 (a COPY of the
/// owner's originals; the profile itself is never written). Skipped otherwise.
/// </summary>
public sealed class LightingFrameDump
{
    [Fact]
    public void Dump_day_evening_night_frames_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_LIGHT_FRAMES");
        var assets = Environment.GetEnvironmentVariable("MAP_LIGHT_ASSETS");
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(assets) || !Directory.Exists(assets)) return;
        Directory.CreateDirectory(output);
        var files = Directory.GetFiles(assets, "*.png").Take(3).ToArray();
        var images = files.ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => SKImage.FromEncodedData(f)!);
        var objects = new List<SceneObject>();
        var placements = new[] { (560.0, 880.0, 0.76), (1300.0, 760.0, 0.80), (2040.0, 900.0, 0.74) };
        var i = 0;
        foreach (var (sha, image) in images)
        {
            var (cx, baseY, scale) = placements[i++ % placements.Length];
            double w = 1024 * scale, h = 1024.0 * image.Height / image.Width * scale;
            objects.Add(new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Mountain", cx - w / 2, baseY - h, w, h, 0,
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(sha) });
        }
        // Sort back to front by base.
        objects = [.. objects.OrderBy(o => o.Y + o.Height)];
        var sources = new[]
        {
            new MapLightSource(Guid.NewGuid(), 860, 930, "#ff9a42", 300, 1.15, true),
            new MapLightSource(Guid.NewGuid(), 1640, 880, "#ffb866", 230, 1.3, false),
            new MapLightSource(Guid.NewGuid(), 1565, 905, "#ffc27a", 150, 0.85, false),
            new MapLightSource(Guid.NewGuid(), 1712, 910, "#ffc888", 130, 0.7, false),
        };
        foreach (var (name, t) in new[] { ("day", 0.0), ("evening", 0.5), ("night", 1.0), ("unlit", -1.0) })
        {
            var lighting = t < 0 ? null : new MapLighting { TimeOfDay = t, Sources = [.. sources] };
            var document = new MapDocument(DocumentId.New(), "Light", DateTimeOffset.UnixEpoch,
                new CanvasDefinition(2560, 1440, "px", "#FFB6AE86"),
                [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFB6AE86"),
                 new ObjectLayer(LayerId.New(), "Mountains", true, false, 1, objects)]) { Lighting = lighting };
            var info = new SKImageInfo(2560, 1440);
            using var surface = SKSurface.Create(info)!;
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions { Stamps = new Images(images), IsExport = true });
            using var shot = surface.Snapshot();
            using var data = shot.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(output, name + ".png"), data.ToArray());
        }
        foreach (var image in images.Values) image.Dispose();
    }

    private sealed class Images(Dictionary<string, SKImage> images) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => images.GetValueOrDefault(sha256);
    }
}
