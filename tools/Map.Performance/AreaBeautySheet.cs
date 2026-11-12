using System.Collections.Immutable;
using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// R10 «Красота» (`--profile area-beauty-sheet`): renders a massif area and a mountain range area to PNG for the
/// before/after sheet, with nothing but the document renderer (no window). Both are the owner's mountains
/// (<see cref="OwnerMountainFixtures"/>; the test conifers once painted here in code are gone, DEC-076): the massif
/// small and in masses, the range large along a ridge. <c>MAP_AREA_BEAUTY=1</c> switches every «Красота» property of
/// both areas on (the values a new area gets); otherwise the areas look as before.
/// <code>dotnet tools/Map.Performance/bin/Release/net10.0/win-x64/Map.Performance.dll --profile area-beauty-sheet --output artifacts/r10-beauty/before.json</code>
/// Writes <c>&lt;output&gt;.massif.png</c>, <c>&lt;output&gt;.massif-zoom.png</c> and <c>&lt;output&gt;.range.png</c>.
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
        var layout5 = Environment.GetEnvironmentVariable("MAP_AREA_LAYOUT") == "5";
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

        var mountains = Enumerable.Range(1, 6)
            .Select(i => Add(OwnerMountainFixtures.Names[i - 1], File.ReadAllBytes(OwnerMountainFixtures.Path(i)))).ToImmutableArray();
        var massifVariants = mountains[..4];
        report.Notes["owner-mountains"] = mountains.Length.ToString();

        var paper = CurrentLook.PageColor;
        var layerId = LayerId.New();
        var document = new MapDocument(DocumentId.New(), "R10 красота", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(2400, 1400, "px", paper),
            [CurrentLook.Page("Море"), CurrentLook.LandEverywhere(2400, 1400),
             new ObjectLayer(layerId, "Горы", true, false, 1, [])], [.. assets]);

        PlacementArea Area(string name, ImmutableArray<StampVariantSource> variants, double width, double density,
            double variation, PlacementAreaPattern pattern, bool join, int seed, params (double X, double Y)[] points)
        {
            var measures = variants.ToDictionary(v => v.Sha256, v => StampContentMeasurer.Read(images[v.Sha256]));
            var area = new PlacementArea
            {
                Name = name, LayoutVersion = layout5 ? PlacementArea.LatestLayoutVersion : 4, Pattern = pattern, JoinEdges = join, Density = density,
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

        // MAP_AREA_LAYOUT=5 (G1, 2026-11-12): the same areas in layout 5 — the range in size classes, the massif as «Горный массив».
        var massif = layout5
            ? Area("Массив владельца", massifVariants, 150, 70, 30, PlacementAreaPattern.Peaks, true, 9127,
                (90, 230), (520, 140), (980, 260), (1190, 560), (1120, 980), (820, 1230), (420, 1190), (150, 900), (60, 560))
            : Area("Массив владельца", massifVariants, 70, 70, 50, PlacementAreaPattern.Masses, true, 9127,
            (90, 230), (520, 140), (980, 260), (1190, 560), (1120, 980), (820, 1230), (420, 1190), (150, 900), (60, 560));
        document = new PlacementAreaCommand(layerId, massif.Id, massif, massif.Name).Apply(document);
        var rangeDensity = double.TryParse(Environment.GetEnvironmentVariable("MAP_AREA_DENSITY"), out var d) ? d : 70;
        var range = Area("Хребет владельца", mountains, 230, rangeDensity, 30, PlacementAreaPattern.Ridge, true, 4411,
            (1330, 420), (1820, 250), (2330, 380), (2350, 720), (1900, 900), (1450, 1100), (1300, 820));
        document = new PlacementAreaCommand(layerId, range.Id, range, range.Name).Apply(document);
        var layer = (ObjectLayer)document.FindLayer(layerId)!;
        report.Notes["objects"] = layer.Objects.Count.ToString();
        // How the arrangement thins: candidates of the layout, those density admits, and those spacing keeps.
        foreach (var area in new[] { massif, range })
        {
            var full = area with { Density = 100 };
            var layout = new PlacementAreaLayout(area);
            int candidates = 0, admitted = 0;
            foreach (var span in PlacementAreaGeometry.Coverage(area.Nodes))
                for (var x = span.MinX; x <= span.MaxX; x++)
                    foreach (var sample in layout.Cell(x, span.Y))
                    { candidates++; if (sample.Activation <= area.Density) admitted++; }
            int kept = 0, keptFull = 0;
            var sampler = new PlacementAreaSampler(area); var samplerFull = new PlacementAreaSampler(full);
            foreach (var span in PlacementAreaGeometry.Coverage(area.Nodes))
                for (var x = span.MinX; x <= span.MaxX; x++)
                { kept += sampler.Slots(x, span.Y).Count(); keptFull += samplerFull.Slots(x, span.Y).Count(); }
            var probe = PlacementAreaPlanner.Item(area, new PlacementAreaSlot(PlacementAreaGeometry.Coverage(area.Nodes)[0].MinX, PlacementAreaGeometry.Coverage(area.Nodes)[0].Y, 0));
            var silhouette = PlacementAreaSilhouette.Create(area, probe, PlacementAreaSampler.VisibleBounds(area, probe)) is not null;
            report.Notes["thinning-" + area.Name] = $"candidates {candidates}, admitted {admitted}, kept {kept}, kept at 100 % {keptFull}, silhouettes {silhouette}";
        }

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
        Save("massif", new WorldBounds(40, 100, 1240, 1280), 1200);
        Save("massif-zoom", new WorldBounds(60, 640, 560, 1240), 1000);
        Save("range", new WorldBounds(1260, 200, 2400, 1160), 1140);
        foreach (var image in images.Values) image.Dispose();
        report.GatePassed = report.Images.Count == 3;
        return report;
    }

    /// <summary>The values a new area gets (R10 «Красота»), switched on for the «after» sheet.</summary>
    internal static PlacementArea BeautyOn(PlacementArea area) =>
        area with { Beauty = PlacementAreaBeauty.ForNewArea(area.Request.BaseWidth, area.Pattern) };
}
