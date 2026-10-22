using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// R09 🎨 review: the owner's showcase island drawn plain and under the colour looks the filter layer
/// already has, the base for the «кнопки преображения» mock-up sheet (MAP_STYLE_VARIANTS = folder).
/// </summary>
public sealed class StyleVariantsReviewTests
{
    [Fact]
    public void Variants_for_review()
    {
        var folder = Environment.GetEnvironmentVariable("MAP_STYLE_VARIANTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var document = Showcase();
        Save(Render(document), folder, "base.png");
        foreach (var id in new[] { "night", "winter", "parchment", "sepia", "bw", "red-sky", "faded", "overcast" })
        {
            var preset = FilterColorEffects.Preset(id)!;
            var filter = new FilterLayer(LayerId.New(), "Стиль", true, false, 1, FilterColorEffects.WithPreset(new FilterSettings(), preset));
            using var bitmap = Render(document with { Layers = [.. document.Layers, filter] });
            Save(bitmap, folder, $"filter-{id}.png");
        }
    }

    private static MapDocument Showcase()
    {
        const int width = 1600, height = 1000;
        var tiles = TileMap.Empty(1);
        for (var ty = 0; ty * TileMap.TileSize < height; ty++)
            for (var tx = 0; tx * TileMap.TileSize < width; tx++)
            {
                var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
                var any = false;
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                    {
                        var value = Island(tx * TileMap.TileSize + x, ty * TileMap.TileSize + y);
                        bytes[y * TileMap.TileSize + x] = value;
                        any |= value > 0;
                    }
                if (any) tiles = tiles.With(new TileKey(tx, ty), bytes);
            }
        var land = new LandLayer(LayerId.New(), "Остров", true, false, 1, tiles, LandEdgeStyle.SoftWatercolor()) { Level = 1 };
        var river = new River
        {
            Name = "Серебрянка", Style = RiverStyle.Natural(), Level = 1,
            Channels = [RiverEditor.NewChannel([new(520, 300), new(640, 380), new(600, 480), new(720, 560), new(860, 520), new(940, 640), new(1060, 700)], 26)],
        };
        var road = RoadEditor.NewRoad([new(420, 660), new(620, 620), new(820, 640), new(1040, 560), new(1220, 470)], 18, RoadStyle.Painted(), "Тракт", seed: 5) with { Level = 1 };
        SceneObject Decoration(DecorationStyle style, int x, int y, int w, int h) =>
            new(ObjectId.New(), SceneObjectKind.Decoration, DecorationStyle.DefaultName(style.Kind), x, y, w, h, 0,
                "#00000000", "#00000000", 0, true, false) { Decoration = style };
        var decorations = new ObjectLayer(LayerId.New(), "Оформление", true, false, 1,
        [
            Decoration(DecorationStyle.Ceremonial(DecorationKind.Cartouche, "Северные земли") with { Subtitle = "карта королевства" }, 520, 40, 560, 170),
            Decoration(DecorationStyle.Ceremonial(DecorationKind.Compass), 1320, 60, 230, 230),
            Decoration(DecorationStyle.Ceremonial(DecorationKind.Legend), 40, 680, 260, 250),
            Decoration(DecorationStyle.Ceremonial(DecorationKind.ScaleBar), 1150, 900, 400, 60),
        ]);
        var water = new ObjectLayer(LayerId.New(), "Реки и дороги", true, false, 1, []) { Rivers = [river], Roads = [road] };
        var document = new MapDocument(DocumentId.New(), "Живописный стиль", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(width, height, "px", "#FFB9CBD2") { Scale = new MapScale(0.25, "км") },
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FFB9CBD2"), land, water, decorations]);
        return MapDecorations.Refresh(document);
    }

    private static byte Island(int x, int y)
    {
        const double cx = 800, cy = 560, rx = 560, ry = 330;
        var angle = Math.Atan2(y - cy, x - cx);
        var wobble = 1 + 0.09 * Math.Sin(angle * 5 + 1) + 0.05 * Math.Sin(angle * 11 + 2.3) + 0.03 * Math.Sin(angle * 23);
        var distance = Math.Sqrt(Math.Pow((x - cx) / rx, 2) + Math.Pow((y - cy) / ry, 2)) / wobble;
        return (byte)Math.Clamp((1 - distance) * 255 * 40, 0, 255);
    }

    private static SKBitmap Render(MapDocument document)
    {
        var info = new SKImageInfo((int)document.Canvas.Width, (int)document.Canvas.Height);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { ViewScale = 1, IsExport = true });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }

    private static void Save(SKBitmap bitmap, string folder, string name)
    {
        using (bitmap)
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(Path.Combine(folder, name), data.ToArray());
    }
}
