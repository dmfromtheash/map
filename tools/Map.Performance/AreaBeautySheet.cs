using System.Collections.Immutable;
using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// R10 «Красота» (`--profile area-beauty-sheet`): renders a test forest area and a mountain range
/// area to PNG for the before/after sheet, with nothing but the document renderer (no window).
/// The forest uses test conifers painted here in code (contour, light from the north-west, a cold
/// shadow side) - they are not product art. The range uses the owner's mountains from
/// <c>content/Owner.Mountains.2026-10-18/source</c> only when that folder exists; they are read for
/// this sheet and never copied into the product. <c>MAP_AREA_BEAUTY=1</c> switches every «Красота»
/// property of both areas on (the values a new area gets); otherwise the areas look as before.
/// <code>dotnet tools/Map.Performance/bin/Release/net10.0/win-x64/Map.Performance.dll --profile area-beauty-sheet --output artifacts/r10-beauty/before.json</code>
/// Writes <c>&lt;output&gt;.forest.png</c>, <c>&lt;output&gt;.forest-zoom.png</c> and <c>&lt;output&gt;.range.png</c>.
/// </summary>
internal sealed class AreaBeautySheetReport
{
    public Dictionary<string, string> Notes { get; } = [];
    public List<string> Images { get; } = [];
    public bool GatePassed { get; set; }
}

internal static class AreaBeautySheet
{
    private sealed class Images(Dictionary<string, SKImage> images) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => images.GetValueOrDefault(sha256);
    }

    public static AreaBeautySheetReport Run(PerformanceOptions options)
    {
        var report = new AreaBeautySheetReport();
        var beauty = Environment.GetEnvironmentVariable("MAP_AREA_BEAUTY") == "1";
        report.Notes["beauty"] = beauty ? "on" : "off";
        var images = new Dictionary<string, SKImage>(StringComparer.OrdinalIgnoreCase);
        var assets = new List<MapAssetReference>();
        StampVariantSource Add(string name, byte[] png)
        {
            var sha = Convert.ToHexStringLower(SHA256.HashData(png));
            using var bitmap = SKBitmap.Decode(png);
            var image = SKImage.FromBitmap(bitmap);
            images[sha] = image;
            assets.Add(new MapAssetReference(sha, name, "image/png", "png", ProjectAssetStorage.Embedded,
                DateTimeOffset.UnixEpoch, "r10-sheet", "Map", png));
            return new StampVariantSource(sha, image.Width, image.Height);
        }

        var trees = Enumerable.Range(0, 4).Select(i => Add($"Тестовая ель {i + 1}", PaintConifer(i))).ToImmutableArray();
        var owner = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
            "content", "Owner.Mountains.2026-10-18", "source"));
        var mountains = Directory.Exists(owner)
            ? Enumerable.Range(1, 6).Select(i => Path.Combine(owner, $"{i}.png")).Where(File.Exists)
                .Select(p => Add("Гора владельца " + Path.GetFileNameWithoutExtension(p), Downscale(File.ReadAllBytes(p), 640))).ToImmutableArray()
            : [];
        report.Notes["owner-mountains"] = mountains.Length.ToString();

        var paper = "#FFE4DAC0";
        var layerId = LayerId.New();
        var document = new MapDocument(DocumentId.New(), "R10 красота", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(2400, 1400, "px", paper),
            [new BaseColorLayer(LayerId.New(), "Бумага", true, false, 1, paper),
             new ObjectLayer(layerId, "Лес и горы", true, false, 1, [])], [.. assets]);

        PlacementArea Area(string name, ImmutableArray<StampVariantSource> variants, double width, double density,
            double variation, PlacementAreaPattern pattern, bool join, int seed, params (double X, double Y)[] points)
        {
            var measures = variants.ToDictionary(v => v.Sha256, v => StampContentMeasurer.Read(images[v.Sha256]));
            var area = new PlacementArea
            {
                Name = name, LayoutVersion = 4, Pattern = pattern, JoinEdges = join, Density = density,
                SizeVariationPercent = variation, Seed = seed, Variants = variants,
                Nodes = [.. points.Select(p => new PlacementPathNode(Guid.NewGuid(), p.X, p.Y))],
                Request = new() { BaseWidth = width, RandomizeVariant = true },
                Style = new StampStyle(variants[0].Sha256),
                ContentBounds = measures.ToImmutableDictionary(k => k.Key, k => k.Value.Bounds),
                ContentMasks = measures.Where(k => k.Value.Mask is not null).ToImmutableDictionary(k => k.Key, k => k.Value.Mask!),
            };
            area = PlacementAreaPlanner.InitializeComposition(area);
            if (beauty) area = BeautyOn(area);
            return area;
        }

        var forest = Area("Тестовый бор", trees, 44, 70, 50, PlacementAreaPattern.Masses, true, 9127,
            (90, 230), (520, 140), (980, 260), (1190, 560), (1120, 980), (820, 1230), (420, 1190), (150, 900), (60, 560));
        document = new PlacementAreaCommand(layerId, forest.Id, forest, forest.Name).Apply(document);
        if (mountains.Length > 0)
        {
            var range = Area("Хребет владельца", mountains, 230, 70, 30, PlacementAreaPattern.Ridge, true, 4411,
                (1330, 420), (1820, 250), (2330, 380), (2350, 720), (1900, 900), (1450, 1100), (1300, 820));
            document = new PlacementAreaCommand(layerId, range.Id, range, range.Name).Apply(document);
        }
        var layer = (ObjectLayer)document.FindLayer(layerId)!;
        report.Notes["objects"] = layer.Objects.Count.ToString();

        var source = new Images(images);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
        void Save(string suffix, WorldBounds view, int width)
        {
            var scale = width / (view.Right - view.Left);
            var height = (int)Math.Round((view.Bottom - view.Top) * scale);
            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            surface.Canvas.Clear(SKColor.Parse(paper));
            surface.Canvas.Scale((float)scale);
            surface.Canvas.Translate((float)-view.Left, (float)-view.Top);
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions
            { IsExport = true, Stamps = source, VisibleWorld = view, ViewScale = scale });
            var path = Path.ChangeExtension(options.OutputPath, "." + suffix + ".png");
            using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using (var output = File.Create(path)) data.SaveTo(output);
            report.Images.Add(path);
        }
        Save("forest", new WorldBounds(40, 100, 1240, 1280), 1200);
        Save("forest-zoom", new WorldBounds(60, 640, 560, 1240), 1000);
        if (mountains.Length > 0) Save("range", new WorldBounds(1260, 200, 2400, 1160), 1140);
        foreach (var image in images.Values) image.Dispose();
        report.GatePassed = report.Images.Count >= 2;
        return report;
    }

    /// <summary>The values a new area gets (R10 «Красота»), switched on for the «after» sheet.</summary>
    internal static PlacementArea BeautyOn(PlacementArea area) =>
        area with { Beauty = PlacementAreaBeauty.ForNewArea(area.Request.BaseWidth, area.Pattern) };

    private static byte[] Downscale(byte[] png, int size)
    {
        using var source = SKBitmap.Decode(png);
        var scale = (double)size / Math.Max(source.Width, source.Height);
        using var resized = source.Resize(new SKImageInfo((int)Math.Round(source.Width * scale), (int)Math.Round(source.Height * scale),
            SKColorType.Rgba8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var image = SKImage.FromBitmap(resized); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// A test conifer: tiers of drooping needles with an ink contour, lit from the north-west and
    /// shaded to the south-east in a restrained blue-green. For the sheet only.
    /// </summary>
    private static byte[] PaintConifer(int variant)
    {
        const int w = 120, h = 190;
        var random = new Random(31 + variant * 7919);
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            var ink = new SKColor(28, 36, 38);
            using var trunk = new SKPaint { Color = new SKColor(74, 58, 46), IsAntialias = true };
            canvas.DrawRect(w / 2f - 5, h - 34, 10, 26, trunk);
            var tiers = 5 + variant % 2;
            var top = 10f + random.Next(6); var bottom = h - 26f;
            var body = new SKColor[] { new(52, 78, 70), new(46, 72, 66), new(58, 80, 66), new(44, 66, 64) }[variant % 4];
            for (var t = 0; t < tiers; t++)
            {
                var k = (t + 1f) / tiers;
                var tierTop = top + (bottom - top) * t / (tiers + 1f) * .92f;
                var tierBottom = top + (bottom - top) * (t + 1.6f) / (tiers + 1f);
                var half = 10 + 44 * k + random.Next(-3, 4);
                using var path = new SKPath();
                path.MoveTo(w / 2f + random.Next(-2, 3), tierTop);
                var teeth = 5 + t;
                path.LineTo(w / 2f + half, tierBottom - 6);
                for (var i = teeth; i >= 0; i--)
                {
                    var x = w / 2f - half + 2 * half * i / teeth;
                    path.LineTo(x, tierBottom + (i % 2 == 0 ? 2 : -5) + random.Next(-2, 3));
                }
                path.LineTo(w / 2f - half, tierBottom - 6);
                path.Close();
                using var fill = new SKPaint { Color = body, IsAntialias = true };
                canvas.DrawPath(path, fill);
                canvas.Save(); canvas.ClipPath(path, antialias: true);
                using var light = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(
                    new SKPoint(w / 2f - half, tierTop), new SKPoint(w / 2f + half * .3f, tierBottom),
                    [new SKColor(150, 170, 132, 170), new SKColor(150, 170, 132, 0)], SKShaderTileMode.Clamp) };
                canvas.DrawRect(0, 0, w, h, light);
                using var shade = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(
                    new SKPoint(w / 2f + half, tierBottom), new SKPoint(w / 2f, tierTop),
                    [new SKColor(16, 26, 34, 150), new SKColor(16, 26, 34, 0)], SKShaderTileMode.Clamp) };
                canvas.DrawRect(0, 0, w, h, shade);
                using var hatch = new SKPaint { Color = ink.WithAlpha(110), IsAntialias = true, StrokeWidth = 1.1f, StrokeCap = SKStrokeCap.Round };
                for (var i = 0; i < 6 + t; i++)
                {
                    var x = w / 2f + (float)random.NextDouble() * half * .9f;
                    var y = tierTop + (tierBottom - tierTop) * (.35f + .6f * (float)random.NextDouble());
                    canvas.DrawLine(x, y, x - 4, y + 5, hatch);
                }
                canvas.Restore();
                using var contour = new SKPaint { Color = ink, IsAntialias = true, IsStroke = true, StrokeWidth = 2.1f, StrokeJoin = SKStrokeJoin.Round };
                canvas.DrawPath(path, contour);
            }
        }
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
