using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>R08: every decoration draws ink inside its own box and nowhere else; the legend draws one line per entry.</summary>
public sealed class DecorationRenderingTests
{
    private const string Ink = "#FFC00000";

    [Theory]
    [InlineData(DecorationKind.ScaleBar, 0)]
    [InlineData(DecorationKind.ScaleBar, 1)]
    [InlineData(DecorationKind.Compass, 0)]
    [InlineData(DecorationKind.Compass, 1)]
    [InlineData(DecorationKind.Compass, 2)]
    [InlineData(DecorationKind.Compass, 3)]
    [InlineData(DecorationKind.Cartouche, 0)]
    [InlineData(DecorationKind.Cartouche, 1)]
    [InlineData(DecorationKind.Cartouche, 2)]
    [InlineData(DecorationKind.Legend, 0)]
    public void A_decoration_inks_its_own_box(DecorationKind kind, int variant)
    {
        var box = new SKRectI(100, 120, 300, 280);
        var document = MapDecorations.Refresh(Document(kind, variant, box, new MapScale(0.5, "км")));
        using var bitmap = Render(document);
        var inside = 0;
        var outside = 0;
        for (var y = 0; y < 400; y++)
        for (var x = 0; x < 400; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.Red < 150 || c.Green > 90 || c.Blue > 90) continue;
            // Compass letters sit just outside the rose's ring; allow a small margin around the box.
            if (x >= box.Left - 12 && x <= box.Right + 12 && y >= box.Top - 12 && y <= box.Bottom + 12) inside++;
            else outside++;
        }

        Assert.True(inside > 150, $"{kind}/{variant}: ink pixels inside {inside}");
        Assert.Equal(0, outside);
    }

    [Fact]
    public void The_scale_bar_label_carries_the_map_unit_and_a_legend_grows_with_its_entries()
    {
        var scaled = MapDecorations.Refresh(Document(DecorationKind.ScaleBar, 0, new SKRectI(40, 150, 360, 230), new MapScale(0.5, "км")));
        var bar = scaled.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Single().Decoration!;
        Assert.Equal("км", bar.Scale!.Unit);

        using var empty = Render(MapDecorations.Refresh(Document(DecorationKind.Legend, 0, new SKRectI(100, 60, 300, 340), null)));
        var withRiver = Document(DecorationKind.Legend, 0, new SKRectI(100, 60, 300, 340), null);
        var river = new River { Name = "Река", Style = RiverStyle.Illustrated(), Channels = [RiverEditor.NewChannel([new(0, 390), new(20, 395)], 4)] };

        withRiver = withRiver.WithLayer(withRiver.Layers[1].Id, l => ((ObjectLayer)l) with { Rivers = [river] });
        withRiver = MapDecorations.Refresh(withRiver);
        Assert.Contains(withRiver.Layers.OfType<ObjectLayer>().Single().Objects.Single().Decoration!.Entries, e => e.Kind == LegendEntryKind.River);
        using var filled = Render(withRiver);
        Assert.True(Inked(filled, new SKRectI(100, 60, 300, 340)) > Inked(empty, new SKRectI(100, 60, 300, 340)));

    }

    private static int Inked(SKBitmap bitmap, SKRectI box)
    {
        var count = 0;
        for (var y = box.Top; y < box.Bottom; y++)
        for (var x = box.Left; x < box.Right; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.Red < 255 || c.Green < 255 || c.Blue < 255) count++;
        }

        return count;
    }

    private static MapDocument Document(DecorationKind kind, int variant, SKRectI box, MapScale? scale)
    {
        var item = new SceneObject(ObjectId.New(), SceneObjectKind.Decoration, "Украшение", box.Left, box.Top, box.Width, box.Height, 0,
            "#00000000", "#00000000", 0, true, false)
        {
            Decoration = new DecorationStyle { Kind = kind, Variant = variant, InkColor = Ink, PaperColor = "#FFFFFFFF", Title = "Карта" },
        };
        return new MapDocument(DocumentId.New(), "decor", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(400, 400, "px", "#FFFFFFFF") { Scale = scale },
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Оформление", true, false, 1, [item])]);
    }

    private static SKBitmap Render(MapDocument document)
    {
        var info = new SKImageInfo(400, 400);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { ViewScale = 1 });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }
}
