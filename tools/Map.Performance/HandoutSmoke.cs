using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// R09f smoke (`--profile handouts`): every handout of the new-map dialog end to end. For each sheet the dialog's view
/// model creates a map with it (the document carries the handout and the paper page), then the sample map with the
/// owner's mountains (<see cref="OwnerMountainFixtures"/>) gets that handout and is saved through
/// <see cref="HandoutExporter"/> with the real image export. Writes <c>&lt;output&gt;.handout-&lt;id&gt;.jpg</c>, a corner
/// at full size (<c>…-zoom.jpg</c>) and the dialog card picture (<c>…-card.jpg</c>).
/// </summary>
internal static class HandoutSmoke
{
    private sealed class Images(Dictionary<string, SKImage> images) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => images.GetValueOrDefault(sha256);
    }

    private const int Width = 1600, Height = 1000;
    private const string SeaColor = "#FF9DB7C2";

    public static AreaBeautySheetReport Run(PerformanceOptions options)
    {
        var report = new AreaBeautySheetReport();
        var (document, source, images) = SampleMap();
        var folder = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!;
        Directory.CreateDirectory(folder);
        var engine = new ImageExportEngine();
        var passed = 0;
        foreach (var handout in MapHandouts.All.Where(h => h != MapHandout.None))
        {
            // The dialog: the chosen sheet goes into the new document, the dark default page turns to paper.
            var wizard = new NewMapWizardViewModel { Title = "Хендаут", Handout = handout };
            var created = wizard.Create(DateTimeOffset.UnixEpoch);
            var dialogOk = created is { } c && c.Handout == handout && c.BaseColor.Color == NewMapWizardViewModel.PaperBackground;
            report.Notes["dialog-" + MapHandouts.Id(handout)] = dialogOk ? "ok" : "FAIL";

            var path = Path.ChangeExtension(options.OutputPath, ".handout-" + MapHandouts.Id(handout) + ".png");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = HandoutExporter.WriteAsync(engine, document with { Handout = handout }, handout, path, source).GetAwaiter().GetResult();
            report.Notes["export-" + MapHandouts.Id(handout)] = $"{result.Width}x{result.Height} {watch.ElapsedMilliseconds} ms";
            using (var bitmap = SKBitmap.Decode(path))
            using (var sheet = SKImage.FromBitmap(bitmap))
            {
                Save(report, options, "handout-" + MapHandouts.Id(handout), sheet, 1400, null);
                Save(report, options, "handout-" + MapHandouts.Id(handout) + "-zoom", sheet, 1400, new SKRectI(0, 0, 1400, 875));
                Save(report, options, "handout-" + MapHandouts.Id(handout) + "-card", sheet, 320, null, quality: 86);
            }
            File.Delete(path);
            if (dialogOk && result.Width > 2400) passed++;
        }
        foreach (var image in images.Values) image.Dispose();
        report.GatePassed = passed == MapHandouts.All.Count - 1;
        return report;
    }

    private static void Save(AreaBeautySheetReport report, PerformanceOptions options, string key, SKImage sheet, int width, SKRectI? crop, int quality = 90)
    {
        using var part = crop is { } c ? sheet.Subset(c) : null;
        var image = part ?? sheet;
        var height = (int)Math.Round(image.Height * (double)width / image.Width);
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.DrawImage(image, SKRect.Create(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var output = surface.Snapshot(); using var data = output.Encode(SKEncodedImageFormat.Jpeg, quality);
        var path = Path.ChangeExtension(options.OutputPath, "." + key + ".jpg");
        using (var file = File.Create(path)) data.SaveTo(file);
        report.Images.Add(path);
    }

    /// <summary>The sample map: an island with a plateau, a river, a road, three names and the owner's mountains.</summary>
    private static (MapDocument Document, IStampImageSource Source, Dictionary<string, SKImage> Images) SampleMap()
    {
        var images = new Dictionary<string, SKImage>(StringComparer.OrdinalIgnoreCase);
        var assets = new List<MapAssetReference>();
        var shas = new List<string>();
        for (var i = 1; i <= 6; i++)
        {
            var png = File.ReadAllBytes(OwnerMountainFixtures.Path(i));
            var sha = Convert.ToHexStringLower(SHA256.HashData(png));
            using var bitmap = SKBitmap.Decode(png);
            images[sha] = SKImage.FromBitmap(bitmap); shas.Add(sha);
            assets.Add(new MapAssetReference(sha, OwnerMountainFixtures.Names[i - 1], "image/png", "png",
                ProjectAssetStorage.Embedded, DateTimeOffset.UnixEpoch, "r09f-sheet", "Map", png));
        }
        var plain = WaterVariantsSheet.Land("Остров", 0,
            [(760, 560, 560, 330, .08, 5), (1385, 235, 95, 62, .12, 4), (1290, 800, 60, 44, .1, 3)],
            [(1010, 850, 200, 120, .1, 4)]);
        var plateau = WaterVariantsSheet.Land("Плато", 1, [(360, 440, 260, 190, .1, 5)], []);
        var random = new Random(20261110);
        var objects = new List<SceneObject>();
        foreach (var (x, y, w) in new (double, double, double)[]
                 { (520, 300, 230), (700, 330, 190), (860, 290, 210), (620, 420, 170), (790, 450, 200), (960, 420, 160),
                   (1080, 360, 180), (300, 330, 170), (380, 420, 140) })
        {
            var sha = shas[random.Next(shas.Count)]; var image = images[sha];
            var h = w * image.Height / image.Width;
            objects.Add(new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора", x - (w / 2), y - h, w, h, 0,
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(sha) });
        }
        objects.Sort((a, b) => (a.Y + a.Height).CompareTo(b.Y + b.Height));
        VectorPoint[] riverPath = [new(760, 470), new(830, 560), new(900, 610), new(950, 690), new(1000, 760)];
        VectorPoint[] roadPath = [new(330, 640), new(500, 600), new(680, 640), new(860, 590), new(1060, 620), new(1220, 560)];
        (string Text, double X, double Y, double Size)[] names = [("Северные горы", 560, 120, 46), ("Плато Ветров", 250, 560, 30), ("Ледяная бухта", 940, 930, 30)];
        var river = new River { Name = "Река", Style = CurrentLook.River(), Channels = [RiverEditor.NewChannel(riverPath, 16)] };
        var road = RoadEditor.NewRoad(roadPath, 10, RoadStyle.Painted(), "Дорога", seed: 7);
        var labels = names.Select(n => new SceneObject(ObjectId.New(), SceneObjectKind.Text, n.Text, n.X - n.Text.Length * n.Size * .36, n.Y - n.Size,
            n.Text.Length * n.Size * .72 + n.Size, n.Size * 1.5, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        {
            Text = new TextStyle
            {
                Content = n.Text, FontSize = n.Size, RequestedFontFamily = "Cormorant Garamond", FallbackFontFamily = "Georgia",
                FontWeight = 700, FillColor = "#FF20242B", OutlineColor = "#00000000",
            },
        });
        var objectLayer = new ObjectLayer(LayerId.New(), "Горы", true, false, 1, [.. objects, .. labels]) { Rivers = [river], Roads = [road] };
        var map = new MapDocument(DocumentId.New(), "Хендауты", DateTimeOffset.UnixEpoch, new CanvasDefinition(Width, Height, "px", SeaColor),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, SeaColor) { Sea = SeaStyle.NewMapDefault }, plain, plateau, objectLayer], [.. assets]);
        return (map, new Images(images), images);
    }
}
