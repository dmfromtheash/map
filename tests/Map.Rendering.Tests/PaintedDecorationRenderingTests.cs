using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// Decorations in the owner's painted manner: they lie on the paper with a soft shadow cast to the
/// south-east by the north-west light, and their paper carries a light-to-shade wash and grain.
/// </summary>
public sealed class PaintedDecorationRenderingTests
{
    private const string Page = "#FFF2E8D2";

    [Fact]
    public void A_cartouche_casts_its_shadow_to_the_south_east_and_its_paper_is_lit_from_the_north_west()
    {
        var box = new SKRectI(100, 140, 300, 240);
        using var bitmap = Render(Document([(DecorationStyle.Ceremonial(DecorationKind.Cartouche, "Север"), box)], 400, 400), 400, 400);
        static double Luma(SKColor c) => .3 * c.Red + .59 * c.Green + .11 * c.Blue;
        double Strip(int x0, int x1, int y0, int y1)
        {
            double sum = 0; var n = 0;
            for (var y = y0; y < y1; y++) for (var x = x0; x < x1; x++) { sum += Luma(bitmap.GetPixel(x, y)); n++; }
            return sum / n;
        }
        // Just outside the frame: the south-east side lies in shadow, the north-west side does not.
        var southEast = Strip(150, 290, box.Bottom + 3, box.Bottom + 8);
        var northWest = Strip(110, 250, box.Top - 8, box.Top - 3);
        Assert.True(southEast < northWest - 4, $"south-east {southEast:0} vs north-west {northWest:0}");
        // Inside the paper: the north-west corner is lighter than the south-east one.
        var lit = Strip(125, 145, 160, 172);
        var shaded = Strip(255, 275, 208, 220);
        Assert.True(lit > shaded + 3, $"lit {lit:0} vs shaded {shaded:0}");
    }

    /// <summary>Frames for the comparison sheet with the owner's mountains (MAP_DECOR_FRAMES = output folder).</summary>
    [Fact]
    public void Frames_for_review()
    {
        var folder = Environment.GetEnvironmentVariable("MAP_DECOR_FRAMES");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var items = new List<(DecorationStyle, SKRectI)>
        {
            (DecorationStyle.Ceremonial(DecorationKind.Compass), new SKRectI(40, 40, 300, 300)),
            (DecorationStyle.Ceremonial(DecorationKind.Cartouche, "Северные земли") with { Subtitle = "карта королевства" }, new SKRectI(340, 70, 860, 250)),
            (DecorationStyle.Ceremonial(DecorationKind.ScaleBar), new SKRectI(340, 300, 760, 360)),
            (DecorationStyle.Ceremonial(DecorationKind.Legend), new SKRectI(40, 340, 300, 600)),
            (DecorationStyle.Ceremonial(DecorationKind.Compass) with { Variant = 4 }, new SKRectI(560, 380, 860, 620)),
        };
        var document = Document(items, 900, 660);
        var layer = document.Layers.OfType<ObjectLayer>().Single();
        var river = new River { Name = "Река", Style = RiverStyle.Painted(), Channels = [RiverEditor.NewChannel([new(0, 650), new(30, 655)], 6)] };
        var road = RoadEditor.NewRoad([new(0, 640), new(30, 645)], 6, RoadStyle.Painted(), "Тракт");
        document = MapDecorations.Refresh(document.WithLayer(layer.Id, l => ((ObjectLayer)l) with { Rivers = [river], Roads = [road] }) with
        {
            Canvas = document.Canvas with { Scale = new MapScale(0.25, "км") },
        });
        using var bitmap = Render(document, 900, 660);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(folder, "decorations.png"), data.ToArray());
    }

    private static MapDocument Document(IReadOnlyList<(DecorationStyle Style, SKRectI Box)> items, int width, int height)
    {
        var objects = items.Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Decoration, "Украшение",
            i.Box.Left, i.Box.Top, i.Box.Width, i.Box.Height, 0, "#00000000", "#00000000", 0, true, false) { Decoration = i.Style }).ToList();
        return new MapDocument(DocumentId.New(), "decor", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(width, height, "px", Page),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, Page),
             new ObjectLayer(LayerId.New(), "Оформление", true, false, 1, [.. objects])]);
    }

    private static SKBitmap Render(MapDocument document, int width, int height)
    {
        var info = new SKImageInfo(width, height);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { ViewScale = 1 });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }
}
