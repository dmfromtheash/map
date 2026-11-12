using System.Text.Json;
using Map.App.Services;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// R20 (`--profile generation-cards-sheet`): the animations of the generation panels' explanation cards for the page of
/// panel variants - the same frames the program shows (<see cref="GenerationDemos"/>, drawn by
/// <see cref="GenerationDemoPainter"/>), on the default look; the area cards with the owner's mountains.
/// Writes <c>&lt;output&gt;.card-&lt;panel&gt;-&lt;key&gt;.webp</c> (the ten frames side by side) and <c>&lt;output&gt;.cards.json</c>.
/// </summary>
internal static class GenerationCardsSheet
{
    private const int PictureWidth = 600, PictureHeight = 260;

    private sealed record Card(string Panel, string Key, string File);

    private sealed class Images(Dictionary<string, SKImage> images) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => images.GetValueOrDefault(sha256);
    }

    public static AreaBeautySheetReport Run(PerformanceOptions options)
    {
        var report = new AreaBeautySheetReport();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
        var cards = new List<Card>();
        string Key(PanelDemoKind kind) => kind.ToString()["Gen".Length..];

        var hexKinds = Enum.GetValues<PanelDemoKind>().Where(k => k.IsGeneration() && !k.IsContinent() && !k.IsArea());
        foreach (var kind in hexKinds)
            cards.Add(Save(options, "hex", Key(kind), GenerationDemos.Hex(kind, CurrentLook.PageColor, Map.Presentation.Services.MapCategories.Hex.Id), null));

        var land = WaterVariantsSheet.Land("Материк", 0, [(800, 500, 640, 290, .12, 5), (330, 330, 150, 90, .14, 4)], [(1010, 700, 120, 70, .1, 3)]);
        var continent = new MapDocument(DocumentId.New(), "Генератор материка", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(GenerationDemos.ContinentWidth, GenerationDemos.ContinentHeight, "px", CurrentLook.PageColor), [CurrentLook.Page(), land]);
        foreach (var kind in Enum.GetValues<PanelDemoKind>().Where(k => k.IsContinent()))
            cards.Add(Save(options, "continent", Key(kind), GenerationDemos.Continent(kind, continent, land.Id, "sheet"), null));

        var images = new Dictionary<string, SKImage>(StringComparer.OrdinalIgnoreCase);
        var assets = new List<MapAssetReference>(); var variants = new List<StampVariantSource>();
        for (var n = 1; n <= 6; n++)
        {
            var asset = OwnerMountainFixtures.Embedded(n);
            using var bitmap = SKBitmap.Decode(asset.EmbeddedContent!);
            images[asset.Sha256] = SKImage.FromBitmap(bitmap);
            assets.Add(asset); variants.Add(new StampVariantSource(asset.Sha256, bitmap.Width, bitmap.Height));
        }
        var layer = new ObjectLayer(LayerId.New(), "Области", true, false, 1, []);
        var area = GenerationDemos.AreaStart(variants);
        var start = new MapDocument(DocumentId.New(), "Область", DateTimeOffset.UnixEpoch, new(GenerationDemos.AreaWidth, GenerationDemos.AreaHeight, "px", CurrentLook.PageColor),
            [CurrentLook.Page("Фон"), CurrentLook.LandEverywhere(GenerationDemos.AreaWidth, GenerationDemos.AreaHeight), layer], [.. assets]);
        start = new PlacementAreaCommand(layer.Id, area.Id, area, "Fixture").Apply(start);
        var stamps = new Images(images);
        foreach (var kind in Enum.GetValues<PanelDemoKind>().Where(k => k.IsArea()))
            cards.Add(Save(options, "area", Key(kind), GenerationDemos.Area(kind, start, layer.Id), stamps));
        foreach (var image in images.Values) image.Dispose();

        File.WriteAllText(Path.ChangeExtension(options.OutputPath, ".cards.json"),
            JsonSerializer.Serialize(cards, new JsonSerializerOptions { WriteIndented = true }));
        report.Images.AddRange(cards.Select(c => c.File));
        report.Notes["cards"] = cards.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        report.GatePassed = cards.Count == 29 && cards.All(c => File.Exists(c.File));
        return report;
    }

    private static Card Save(PerformanceOptions options, string panel, string key, IReadOnlyList<GenerationDemoFrame> frames, IStampImageSource? stamps)
    {
        using var strip = SKSurface.Create(new SKImageInfo(PictureWidth * frames.Count, PictureHeight));
        for (var i = 0; i < frames.Count; i++)
        {
            strip.Canvas.Save();
            strip.Canvas.Translate(i * PictureWidth, 0);
            GenerationDemoPainter.Paint(strip.Canvas, frames[i], PictureWidth, PictureHeight, stamps);
            strip.Canvas.Restore();
        }
        var path = Path.ChangeExtension(options.OutputPath, $".card-{panel}-{key}.webp");
        using var image = strip.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Webp, 82);
        using (var file = File.Create(path)) data.SaveTo(file);
        Console.WriteLine($"  card {panel}/{key}: {new FileInfo(path).Length / 1024} KB");
        return new Card(panel, key, path);
    }
}
