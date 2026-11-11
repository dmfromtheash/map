using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// R09d (`--profile style-packs-sheet`): the four style packs proposed to the owner before any code of the packs
/// exists - «Тушь», «Пергамент», «Акварель», «Атлас» - each on the same small map (an island with a raised plateau, a
/// river, a road, three names and the owner's mountains, <see cref="OwnerMountainFixtures"/>), drawn by the document
/// renderer. A pack is made only of looks the program already has: the style of the drawing, a ready cliff look with its
/// scree, the river and road drawings, the sea and the colour of the names; the owner's ground materials stay as they
/// are. Writes <c>&lt;output&gt;.pack-&lt;id&gt;.jpg</c>, <c>…-zoom.jpg</c> and <c>…-none.jpg</c> (the map before any pack).
/// </summary>
internal static class StylePacksSheet
{
    private sealed class Images(Dictionary<string, SKImage> images) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => images.GetValueOrDefault(sha256);
    }

    // The packs themselves live in Core (MapStylePack.Presets, DEC-094); this sheet only draws them.
    private static IReadOnlyList<MapStylePack> Packs => MapStylePack.Presets;

    private const int Width = 1600, Height = 1000;
    private const string SeaColor = "#FF9DB7C2";

    /// <summary>The same small map with the owner's mountains, for the live pass of R09 (`native-r09-live`).</summary>
    internal static MapDocument DemoMap() => Build().Document(null);

    public static AreaBeautySheetReport Run(PerformanceOptions options)
    {
        var report = new AreaBeautySheetReport();
        var (images, Document) = Build();
        var source = new Images(images);
        RunWith(options, report, source, Document);
        foreach (var image in images.Values) image.Dispose();
        return report;
    }

    private static (Dictionary<string, SKImage> Images, Func<MapStylePack?, MapDocument> Document) Build()
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
                ProjectAssetStorage.Embedded, DateTimeOffset.UnixEpoch, "r09d-sheet", "Map", png));
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

        MapDocument Document(MapStylePack? pack)
        {
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
            var map = new MapDocument(DocumentId.New(), "Пакеты стиля", DateTimeOffset.UnixEpoch, new CanvasDefinition(Width, Height, "px", SeaColor),
                [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, SeaColor) { Sea = SeaStyle.NewMapDefault }, plain, plateau, objectLayer], [.. assets]);
            return pack?.ApplyTo(map) ?? map;
        }
        return (images, Document);
    }

    private static void RunWith(PerformanceOptions options, AreaBeautySheetReport report, Images source, Func<MapStylePack?, MapDocument> Document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
        void Save(string key, MapDocument document, WorldBounds view, int width)
        {
            var scale = width / (view.Right - view.Left);
            var height = (int)Math.Round((view.Bottom - view.Top) * scale);
            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            surface.Canvas.Clear(SKColor.Parse(SeaColor));
            surface.Canvas.Scale((float)scale);
            surface.Canvas.Translate((float)-view.Left, (float)-view.Top);
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions
            { IsExport = true, Stamps = source, VisibleWorld = view, ViewScale = scale });
            var path = Path.ChangeExtension(options.OutputPath, "." + key + ".jpg");
            using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            using (var output = File.Create(path)) data.SaveTo(output);
            report.Images.Add(path);
        }

        var whole = new WorldBounds(0, 0, Width, Height);
        var close = new WorldBounds(80, 300, 980, 860);   // the plateau wall, the road, the river and a name
        Save("pack-none", Document(null), whole, 1200);
        foreach (var pack in Packs)
        {
            var document = Document(pack);
            Save("pack-" + pack.Id, document, whole, 1200);
            Save("pack-" + pack.Id + "-zoom", document, close, 1200);
        }
        report.GatePassed = report.Images.Count == 1 + Packs.Count * 2;
    }
}
