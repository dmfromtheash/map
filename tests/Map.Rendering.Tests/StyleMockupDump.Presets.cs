using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed partial class StyleMockupDump
{
    /// <summary>Review artifacts use the real program renderer, not the mockup shaders. Opt-in, no owner data written.</summary>
    [Fact]
    public void Dump_approved_presets_from_program_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_R09_PRESETS");
        var assets = Environment.GetEnvironmentVariable("MAP_LIGHT_ASSETS");
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(assets)) return;
        Directory.CreateDirectory(output);
        var images = Directory.GetFiles(assets, "*.png").Take(3)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f)!, f =>
            {
                // Keep decoded pixels for repeated mountains; lazy PNGs larger than Skia's
                // raster cache otherwise get decoded again for each of the 400 placements.
                using var bitmap = SKBitmap.Decode(f)!;
                return SKImage.FromBitmap(bitmap);
            });
        try
        {
            var (reference, _) = Scene(images);
            var referenceLayer = reference.Layers.OfType<ObjectLayer>().Single(l => l.Name == "Горы");
            // Deliberately separated silhouettes, in addition to the exact mockup scene below.
            var separated = referenceLayer.Objects.Select((o, i) => new SceneObject(o.Id, o.Kind, o.Name,
                360 + i * 720, 430, 480, 480 * images[o.Stamp!.AssetSha256].Height / images[o.Stamp.AssetSha256].Width,
                0, o.FillColor, o.StrokeColor, o.StrokeWidth, true, false) { Stamp = o.Stamp }).ToArray();
            var small = reference.WithLayer(referenceLayer.Id, l => ((ObjectLayer)l) with { Objects = separated });
            var rng = new Random(20261025);
            var shas = images.Keys.ToArray();
            var objects = Enumerable.Range(0, 400).Select(i =>
            {
                var sha = shas[i % shas.Length];
                var width = 140 + rng.NextDouble() * 180;
                return new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора", 200 + rng.NextDouble() * 2000,
                    300 + rng.NextDouble() * 750, width, width * images[sha].Height / images[sha].Width, 0,
                    "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(sha) };
            }).OrderBy(o => o.Y + o.Height).ToArray();
            var layer = small.Layers.OfType<ObjectLayer>().Single(l => l.Name == "Горы");
            var big = small.WithLayer(layer.Id, l => ((ObjectLayer)l) with { Objects = objects });
            foreach (var (tag, document) in new[] { ("small", small), ("big-400", big), ("reference-scene", reference) })
            {
                if (Environment.GetEnvironmentVariable("MAP_R09_PRESETS_SCENE") is { Length: > 0 } only && tag != only) continue;
                foreach (var preset in new[] { MapStylePreset.OldMapBlaeu, MapStylePreset.EngravingAquatint })
                foreach (var scale in new[] { 1f, 0.5f })
                {
                    if (tag == "reference-scene" && scale < 1) continue;
                    using var surface = SKSurface.Create(new SKImageInfo((int)(W * scale), (int)(H * scale)))!;
                    surface.Canvas.Scale(scale);
                    SceneRenderer.Render(surface.Canvas, document with { Style = MapStyle.For(preset) },
                        new RenderOptions { Stamps = new Images(images), IsExport = true, ViewScale = scale });
                    using var image = surface.Snapshot();
                    Save(image, $"{preset}-{tag}-{scale * 100:0}.png");
                }
            }
            if (Environment.GetEnvironmentVariable("MAP_R09_PRESETS_SCENE") is { Length: > 0 }) return;
            // Exact original selected mockups, for a side-by-side review of the small scene.
            var mountainsId = layer.Id;
            var riversId = small.Layers.First(l => l.Name == "Реки").Id;
            var landId = small.Layers.OfType<LandLayer>().First().Id;
            using var mountains = RenderOnly(reference, mountainsId, images);
            using var land = RenderOnly(reference, landId, images);
            using var rivers = RenderOnly(reference, riversId, images);
            using var names = RenderOnly(reference, LabelsId, images);
            using var ground = Render(reference.WithLayer(LabelsId, l => l with { Visible = false })
                .WithLayer(mountainsId, l => l with { Visible = false }), images);
            using var geo = CoastField(land, rivers);
            using var white = OnWhite(mountains);
            using var tensor = Tensor(white);
            using var height = MountainHeight(mountains);
            using var lines = Blurred(mountains, 2);
            using var faces = Blurred(mountains, 8);
            foreach (var atlas in new[] { true, false })
            {
                using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
                using var shader = atlas
                    ? OldMapShader(ground, mountains, geo, tensor, new OldMap(1, "#e6dab9", "#221c17", "#3f8f5e", "#6b7a45", "#d8a93c", "#7c9bb0", 1, 0.5f, 1, 0.6f, 1))
                    : PrintShader(mountains, geo, tensor, height, lines, faces, new Print(5, "#efe7d6", "#4a3324", "#000000", 1));
                using var paint = new SKPaint { Shader = shader };
                surface.Canvas.DrawRect(0, 0, W, H, paint);
                if (atlas) DrawAtlasBorder(surface.Canvas);
                surface.Canvas.DrawImage(names, 0, 0);
                using var image = surface.Snapshot();
                Save(image, atlas ? "reference-Blaeu.png" : "reference-Aquatint.png");
            }
        }
        finally { foreach (var image in images.Values) image.Dispose(); }

        void Save(SKImage image, string name)
        {
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(output, name), data.ToArray());
        }
    }
}
