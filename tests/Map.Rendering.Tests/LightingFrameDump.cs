using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// Look-at frames of the Свет section (Claude's visual check, the owner's showcase): set MAP_LIGHT_FRAMES
/// to an output folder and MAP_LIGHT_ASSETS to a folder of mountain PNGs named by their SHA-256 (a COPY
/// of the owner's originals; the profile itself is never written). Writes every light variation as a
/// 2560×1440 PNG and one sheet of all of them. Skipped otherwise.
/// </summary>
public sealed class LightingFrameDump
{
    [Fact]
    public void Dump_every_light_variation_when_asked()
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
        objects = [.. objects.OrderBy(o => o.Y + o.Height)];
        // A torch by the glacier, a little village between the peaks, a camp fire in the foreground.
        var sources = new[]
        {
            new MapLightSource(Guid.NewGuid(), 860, 930, "#ff9a42", 300, 1.15, true),
            new MapLightSource(Guid.NewGuid(), 1640, 880, "#ffb866", 230, 1.3, false),
            new MapLightSource(Guid.NewGuid(), 1565, 905, "#ffc27a", 150, 0.85, false),
            new MapLightSource(Guid.NewGuid(), 1712, 910, "#ffc888", 130, 0.7, false),
            new MapLightSource(Guid.NewGuid(), 1180, 1180, "#ff8a3c", 380, 1.3, true),
        };
        var lit = new MapLighting { Sources = [.. sources] };
        var off = new LightingEffect(false, 0);
        var variants = new (string Name, MapLighting? Lighting)[]
        {
            ("01 Без света", null),
            ("02 День", lit with { TimeOfDay = 0 }),
            ("03 После полудня", lit with { TimeOfDay = 0.25 }),
            ("04 Вечер", lit with { TimeOfDay = 0.5 }),
            ("05 Сумерки", lit with { TimeOfDay = 0.75 }),
            ("06 Ночь", lit with { TimeOfDay = 1 }),
            ("07 Ночь без огней", lit with { TimeOfDay = 1, Lights = off }),
            ("08 Вечер без объёма гор", lit with { TimeOfDay = 0.5, Relief = off }),
            ("09 Вечер без теней", lit with { TimeOfDay = 0.5, Shadows = off }),
            ("10 Вечер без тумана", lit with { TimeOfDay = 0.5, Fog = off }),
            ("11 Вечер без цвета суток", lit with { TimeOfDay = 0.5, Color = off }),
            ("12 Вечер, объём и тени на максимум", lit with { TimeOfDay = 0.5, Relief = new(true, 1), Shadows = new(true, 1) }),
        };
        var frames = new List<(string Name, SKImage Image)>();
        foreach (var (name, lighting) in variants)
        {
            var document = new MapDocument(DocumentId.New(), "Свет", DateTimeOffset.UnixEpoch,
                new CanvasDefinition(2560, 1440, "px", "#FFB6AE86"),
                [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFB6AE86"),
                 new ObjectLayer(LayerId.New(), "Mountains", true, false, 1, objects)]) { Lighting = lighting };
            using var surface = SKSurface.Create(new SKImageInfo(2560, 1440))!;
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions { Stamps = new Images(images), IsExport = true });
            var shot = surface.Snapshot();
            using (var data = shot.Encode(SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(Path.Combine(output, name + ".png"), data.ToArray());
            frames.Add((name, shot));
        }

        // One sheet of every variation, three across, with its name under each frame.
        const int cellW = 853, cellH = 480, caption = 44, cols = 3;
        var rows = (frames.Count + cols - 1) / cols;
        using (var sheet = SKSurface.Create(new SKImageInfo(cellW * cols, (cellH + caption) * rows))!)
        {
            sheet.Canvas.Clear(new SKColor(0x1d, 0x19, 0x15));
            using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 24);
            using var text = new SKPaint { Color = new SKColor(0xec, 0xe1, 0xc8), IsAntialias = true };
            for (var k = 0; k < frames.Count; k++)
            {
                float x = k % cols * cellW, y = k / cols * (cellH + caption);
                sheet.Canvas.DrawImage(frames[k].Image, SKRect.Create(x, y, cellW, cellH), new SKSamplingOptions(SKCubicResampler.Mitchell));
                sheet.Canvas.DrawText(frames[k].Name[3..], x + 14, y + cellH + 31, font, text);
            }
            using var all = sheet.Snapshot();
            using var encoded = all.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(output, "00 Все варианты.png"), encoded.ToArray());
        }
        foreach (var (_, image) in frames) image.Dispose();
        foreach (var image in images.Values) image.Dispose();
    }

    private sealed class Images(Dictionary<string, SKImage> images) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => images.GetValueOrDefault(sha256);
    }
}
