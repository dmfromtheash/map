using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// R12 (`--profile water-variants-sheet`): pictures for the owner's choice pages of water, cliffs and moonlight. One
/// small map — an island with a bay, a raised plateau whose wall drops into the sea and onto the land, a river and the
/// owner's mountains (<see cref="OwnerMountainFixtures"/>) — drawn by the document renderer, whole and close:
/// every sea manner (<c>sea-N</c>), every ready cliff look (<c>cliff-id</c>) and «Лунная ночь» in every scatter of its
/// light (<c>moon-Scatter</c>). A variant under trial is added here the same way and shown before it enters the program.
/// Writes <c>&lt;output&gt;.&lt;key&gt;.jpg</c> and <c>&lt;output&gt;.&lt;key&gt;-zoom.jpg</c>.
/// </summary>
internal static class WaterVariantsSheet
{
    private sealed class Images(Dictionary<string, SKImage> images) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => images.GetValueOrDefault(sha256);
    }

    private const int Width = 1600, Height = 1000;
    private const string Sea = "#FF9DB7C2";

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
                ProjectAssetStorage.Embedded, DateTimeOffset.UnixEpoch, "r12-sheet", "Map", png));
        }

        var plain = Land("Остров", 0,
            [(760, 560, 560, 330, .08, 5), (1385, 235, 95, 62, .12, 4), (1290, 800, 60, 44, .1, 3)],
            [(1010, 850, 200, 120, .1, 4)]);
        var plateau = Land("Плато", 1, [(360, 440, 260, 190, .1, 5)], []);

        var random = new Random(20261109);
        var objects = new List<SceneObject>();
        foreach (var (x, y, w) in new (double, double, double)[]
                 { (520, 300, 230), (700, 330, 190), (860, 290, 210), (620, 420, 170), (790, 450, 200), (960, 420, 160),
                   (1080, 360, 180), (470, 520, 150), (300, 330, 170), (380, 420, 140) })
        {
            var sha = shas[random.Next(shas.Count)]; var image = images[sha];
            var h = w * image.Height / image.Width;
            objects.Add(new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора", x - (w / 2), y - h, w, h, 0,
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(sha) });
        }
        objects.Sort((a, b) => (a.Y + a.Height).CompareTo(b.Y + b.Height));
        var river = new River
        {
            Name = "Река", Style = CurrentLook.River(),
            Channels = [RiverEditor.NewChannel([new(760, 470), new(830, 560), new(900, 610), new(950, 690), new(1000, 760)], 16)],
        };
        var objectLayer = new ObjectLayer(LayerId.New(), "Горы", true, false, 1, [.. objects]) { Rivers = [river] };

        MapDocument Document(SeaStyle sea, LandCliffStyle? cliff = null) => new(DocumentId.New(), "Вода и обрывы", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(Width, Height, "px", Sea),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, Sea) { Sea = sea }, plain,
             cliff is null ? plateau : plateau with { EdgeStyle = plateau.EdgeStyle with { Cliff = cliff } }, objectLayer], [.. assets]);

        var source = new Images(images);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
        void Save(string key, MapDocument document, WorldBounds view, int width)
        {
            var scale = width / (view.Right - view.Left);
            var height = (int)Math.Round((view.Bottom - view.Top) * scale);
            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            surface.Canvas.Clear(SKColor.Parse(Sea));
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
        var coast = new WorldBounds(820, 620, 1420, 995);     // the bay, the river mouth and the islet
        var wall = new WorldBounds(60, 360, 660, 735);        // the plateau wall into the sea and onto the land
        var expected = 0;
        foreach (var (manner, _, _) in SeaStyle.Manners)
        {
            var document = Document(SeaStyle.For(manner));
            Save("sea-" + (int)manner, document, whole, 1200); Save("sea-" + (int)manner + "-zoom", document, coast, 1200);
            expected += 2;
        }
        foreach (var look in LandCliffStyle.Looks)
        {
            var document = Document(SeaStyle.NewMapDefault, look.Style);
            Save("cliff-" + look.Id, document, whole, 1200); Save("cliff-" + look.Id + "-zoom", document, wall, 1200);
            expected += 2;
        }
        var night = Document(SeaStyle.NewMapDefault) with { Style = MapStyle.For(MapStylePreset.MoonlightHasui) };
        foreach (var (scatter, _, _) in MapStyle.MoonScatters)
        {
            var document = night with { Style = night.Style! with { MoonScatter = scatter } };
            Save("moon-" + scatter, document, whole, 1200); Save("moon-" + scatter + "-zoom", document, coast, 1200);
            expected += 2;
        }

        foreach (var image in images.Values) image.Dispose();
        report.Notes["sea-shader"] = SeaShader.Available ? "ok" : "error: " + SeaShader.LastError;
        report.GatePassed = SeaShader.Available && report.Images.Count == expected;
        return report;
    }

    /// <summary>Land from wobbly ellipses joined together, with bays cut out of them.</summary>
    private static LandLayer Land(string name, int level,
        (double Cx, double Cy, double Rx, double Ry, double Wobble, int Lobes)[] shapes,
        (double Cx, double Cy, double Rx, double Ry, double Wobble, int Lobes)[] cuts)
    {
        var tiles = TileMap.Empty(1);
        for (var ty = 0; ty * TileMap.TileSize < Height; ty++)
            for (var tx = 0; tx * TileMap.TileSize < Width; tx++)
            {
                var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
                var any = false;
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                    {
                        double wx = (tx * TileMap.TileSize) + x, wy = (ty * TileMap.TileSize) + y;
                        var value = shapes.Max(s => Shape(s, wx, wy));
                        foreach (var cut in cuts) value = Math.Min(value, 1 - Shape(cut, wx, wy));
                        var b = (byte)Math.Clamp(value * 255, 0, 255);
                        bytes[(y * TileMap.TileSize) + x] = b; any |= b > 0;
                    }
                if (any) tiles = tiles.With(new TileKey(tx, ty), bytes);
            }
        return new LandLayer(LayerId.New(), name, true, false, 1, tiles, CurrentLook.Land()) { Level = level };

        static double Shape((double Cx, double Cy, double Rx, double Ry, double Wobble, int Lobes) s, double x, double y)
        {
            var angle = Math.Atan2(y - s.Cy, x - s.Cx);
            var wobble = 1 + (s.Wobble * Math.Sin(angle * s.Lobes)) + (s.Wobble * .6 * Math.Sin((angle * ((s.Lobes * 2) + 3)) + 1.3));
            var distance = Math.Sqrt(Math.Pow((x - s.Cx) / s.Rx, 2) + Math.Pow((y - s.Cy) / s.Ry, 2)) / wobble;
            return Math.Clamp((1 - distance) * Math.Max(s.Rx, s.Ry) / 2, 0, 1);
        }
    }
}
