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

    internal sealed record Pack(string Id, string Title, MapStylePreset Style, string CliffLook, CliffScree Scree,
        RiverStyle River, RoadDrawing Road, SeaManner Sea, string NameColor, string NameOutline);

    internal static IReadOnlyList<Pack> Packs { get; } =
    [
        new("ink", "Тушь", MapStylePreset.InkWetBrush, "ink-sepia", CliffScree.TwoBenches, RiverStyle.Illustrated(pen: true),
            RoadDrawing.PenRuts, SeaManner.PenLines, "#FF1E1B18", "#00000000"),
        new("parchment", "Пергамент", MapStylePreset.EngravingAquatint, "grey-engraving", CliffScree.WallAndBench, RiverStyle.Illustrated(pen: true),
            RoadDrawing.Country, SeaManner.Engraved, "#FF5B3A1E", "#00000000"),
        new("watercolor", "Акварель", MapStylePreset.WatercolorBigShapes, "ice", CliffScree.None, RiverStyle.Painted(),
            RoadDrawing.Painterly, SeaManner.WatercolorHalo, "#FF3E5566", "#00000000"),
        new("atlas", "Атлас", MapStylePreset.OldMapBlaeu, "atlas", CliffScree.TwoBenches, RiverStyle.Natural(),
            RoadDrawing.Country, SeaManner.BandsAndLines, "#FF6B2A1A", "#FFFFFFFF"),
    ];

    private const int Width = 1600, Height = 1000;
    private const string SeaColor = "#FF9DB7C2";

    public static AreaBeautySheetReport Run(PerformanceOptions options)
    {
        var report = new AreaBeautySheetReport();
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

        MapDocument Document(Pack? pack)
        {
            var river = new River
            {
                Name = "Река", Style = pack?.River ?? CurrentLook.River(),
                Channels = [RiverEditor.NewChannel(riverPath, 16)],
            };
            var road = RoadEditor.NewRoad(roadPath, 10, pack is null ? RoadStyle.Painted() : RoadStyle.Preset(pack.Road), "Дорога", seed: 7);
            var labels = names.Select(n => new SceneObject(ObjectId.New(), SceneObjectKind.Text, n.Text, n.X - n.Text.Length * n.Size * .36, n.Y - n.Size,
                n.Text.Length * n.Size * .72 + n.Size, n.Size * 1.5, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
            {
                Text = new TextStyle
                {
                    Content = n.Text, FontSize = n.Size, RequestedFontFamily = "Cormorant Garamond", FallbackFontFamily = "Georgia",
                    FontWeight = 700, FillColor = pack?.NameColor ?? "#FF20242B",
                    OutlineColor = pack?.NameOutline ?? "#00000000", OutlineWidth = pack?.NameOutline is "#FFFFFFFF" ? 3 : 0,
                },
            });
            var cliff = pack is null ? null : LandCliffStyle.Looks.First(l => l.Id == pack.CliffLook).Style with { Scree = pack.Scree };
            var objectLayer = new ObjectLayer(LayerId.New(), "Горы", true, false, 1, [.. objects, .. labels]) { Rivers = [river], Roads = [road] };
            var sea = pack is null ? SeaStyle.NewMapDefault : SeaStyle.For(pack.Sea);
            return new MapDocument(DocumentId.New(), "Пакеты стиля", DateTimeOffset.UnixEpoch, new CanvasDefinition(Width, Height, "px", SeaColor),
                [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, SeaColor) { Sea = sea }, plain,
                 cliff is null ? plateau : plateau with { EdgeStyle = plateau.EdgeStyle with { Cliff = cliff } }, objectLayer], [.. assets])
            { Style = pack is null ? null : MapStyle.For(pack.Style) with { Enabled = true } };
        }

        var source = new Images(images);
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
        foreach (var image in images.Values) image.Dispose();
        report.GatePassed = report.Images.Count == 1 + Packs.Count * 2;
        return report;
    }
}
