using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// The owner's painted river: light from the north-west, so the south bank of an east-flowing
/// river is darker than the north one, with brush strokes inside the water and no glossy highlights.
/// </summary>
public sealed class PaintedRiverRenderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_shadow_lies_on_the_south_bank_whichever_way_the_river_flows(bool westward)
    {
        VectorPoint[] points = [new(20, 200), new(380, 200)];
        if (westward) Array.Reverse(points);
        var river = new River { Name = "Р", Style = RiverStyle.Painted() with { TextureId = null }, Channels = [RiverEditor.NewChannel(points, 80)] };
        using var bitmap = Render(river);
        double Luma(int y)
        {
            double sum = 0;
            for (var x = 120; x < 280; x++) { var c = bitmap.GetPixel(x, y); sum += .3 * c.Red + .59 * c.Green + .11 * c.Blue; }
            return sum / 160;
        }
        // Inside the water: 25 px from each bank.
        Assert.True(Luma(225) < Luma(175) - 6, $"south {Luma(225):0} vs north {Luma(175):0}");
    }

    [Fact]
    public void Painted_water_has_visible_strokes_and_plain_illustrated_water_does_not_change()
    {
        VectorPoint[] points = [new(20, 200), new(380, 200)];
        var painted = new River { Name = "Р", Style = RiverStyle.Painted() with { TextureId = null }, Channels = [RiverEditor.NewChannel(points, 80)] };
        var flat = painted with { Style = painted.Style with { PaintStrokes = 0 } };
        using var withStrokes = Render(painted);
        using var without = Render(flat);
        var differing = 0;
        for (var y = 170; y < 230; y++)
        for (var x = 40; x < 360; x++)
            if (withStrokes.GetPixel(x, y) != without.GetPixel(x, y)) differing++;
        Assert.True(differing > 2000, $"pixels changed by strokes: {differing}");
        Assert.Equal(0, RiverStyle.Illustrated().PaintStrokes);
        Assert.Equal(0, RiverStyle.Painted().HighlightOpacity);
    }

    [Fact]
    public void Where_the_shadow_changes_bank_no_ink_line_crosses_the_water()
    {
        // East, then turning south: the shadow bank flips on the way. The contour used to jump
        // across the channel there - a dark bar over the water.
        VectorPoint[] points = [new(20, 100), new(200, 100), new(300, 200), new(300, 380)];
        var river = new River { Name = "Р", Style = RiverStyle.Painted() with { TextureId = null }, Channels = [RiverEditor.NewChannel(points, 44)] };
        using var bitmap = Render(river);
        var darkest = 255.0;
        for (var i = 0; i < points.Length - 1; i++)
            for (var t = 0.0; t <= 1; t += 0.01)
            {
                var x = (int)(points[i].X + (points[i + 1].X - points[i].X) * t);
                var y = (int)(points[i].Y + (points[i + 1].Y - points[i].Y) * t);
                if (x < 40 || y > 360) continue;
                var c = bitmap.GetPixel(x, y);
                darkest = Math.Min(darkest, .3 * c.Red + .59 * c.Green + .11 * c.Blue);
            }
        Assert.True(darkest > 70, $"darkest pixel on the channel line: {darkest:0}");
    }

    private static SKBitmap Render(River river)
    {
        var layer = new ObjectLayer(LayerId.New(), "Вода", true, false, 1, []) { Rivers = [river] };
        var document = new MapDocument(DocumentId.New(), "river", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(400, 400, "px", "#FFE9E1CC"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFE9E1CC"), layer]);
        var info = new SKImageInfo(400, 400);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { ViewScale = 1 });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }
}
