using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// The owner's painted road: light from the north-west, so the south half of an east-running road
/// is darker than the north one, with brush strokes over the earth; older looks stay unchanged.
/// </summary>
public sealed class PaintedRoadRenderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_shadow_lies_on_the_south_side_whichever_way_the_road_runs(bool westward)
    {
        VectorPoint[] points = [new(20, 200), new(380, 200)];
        if (westward) Array.Reverse(points);
        using var bitmap = Render(RoadEditor.NewRoad(points, 60, RoadStyle.Painted(), seed: 7));
        double Luma(int y)
        {
            double sum = 0;
            for (var x = 120; x < 280; x++) { var c = bitmap.GetPixel(x, y); sum += .3 * c.Red + .59 * c.Green + .11 * c.Blue; }
            return sum / 160;
        }
        // Inside the earth: 16 px from each edge.
        Assert.True(Luma(214) < Luma(186) - 6, $"south {Luma(214):0} vs north {Luma(186):0}");
    }

    [Fact]
    public void Painted_strokes_change_the_earth_and_older_looks_do_not_carry_them()
    {
        VectorPoint[] points = [new(20, 200), new(380, 200)];
        var painted = RoadEditor.NewRoad(points, 60, RoadStyle.Painted(), seed: 7);
        var flat = painted with { Style = painted.Style with { PaintStrokes = 0 } };
        using var withStrokes = Render(painted);
        using var without = Render(flat);
        var differing = 0;
        for (var y = 172; y < 228; y++)
        for (var x = 40; x < 360; x++)
            if (withStrokes.GetPixel(x, y) != without.GetPixel(x, y)) differing++;
        Assert.True(differing > 2000, $"pixels changed by strokes: {differing}");
        Assert.Equal(0, RoadStyle.Country().PaintStrokes);
        Assert.Equal(0, RoadStyle.PenRuts().PaintStrokes);
        Assert.Equal(0, RoadStyle.Painterly().PaintStrokes);
        Assert.True(painted.NeedsPaintedSchema);
        Assert.False(flat.NeedsPaintedSchema);
    }

    [Theory]
    [InlineData(0.234)]
    [InlineData(0.05)]
    public void A_pen_road_far_out_draws_without_throwing(double scale)
    {
        // The rut width floor (half a pixel) exceeded the pen cap far out and Math.Clamp threw.
        var road = RoadEditor.NewRoad([new(20, 200), new(380, 200)], 60, RoadStyle.Painted(), seed: 7);
        using var bitmap = Render(road, null, 200, 200, scale);
        Assert.NotNull(bitmap);
    }

    /// <summary>Frames for the comparison sheet with the owner's mountains (MAP_ROAD_FRAMES = output folder).</summary>
    [Fact]
    public void Frames_for_review()
    {
        var folder = Environment.GetEnvironmentVariable("MAP_ROAD_FRAMES");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        VectorPoint[] course = [new(10, 60), new(160, 120), new(300, 230), new(460, 250), new(620, 330), new(890, 400)];
        foreach (var (name, style) in new[]
        {
            ("country", RoadStyle.Country()), ("pen", RoadStyle.PenRuts()), ("painterly", RoadStyle.Painterly()), ("painted", RoadStyle.Painted()),
        })
        {
            var road = RoadEditor.NewRoad(course, 22, style, seed: 11);
            // A fork: a narrower path leaving the road towards the south-west.
            road = RoadEditor.AddBranch(road, road.Nodes[2].Id, [new(250, 330), new(120, 440)], null, 13, out _);
            using var one = Render(road, null, 900, 480, 1);
            Save(one, folder, $"{name}-1x.png");
            using var two = Render(road, null, 900, 480, 2, 200, 120);
            Save(two, folder, $"{name}-2x.png");
        }
    }

    private static void Save(SKBitmap bitmap, string folder, string name)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(folder, name), data.ToArray());
    }

    private static SKBitmap Render(Road road) => Render(road, null, 400, 400, 1);

    private static SKBitmap Render(Road road, Road? second, int width, int height, double scale, double left = 0, double top = 0)
    {
        var layer = new ObjectLayer(LayerId.New(), "Дороги", true, false, 1, []) { Roads = second is null ? [road] : [road, second] };
        var document = new MapDocument(DocumentId.New(), "road", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(900, 480, "px", "#FFE9E1CC"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFE9E1CC"), layer]);
        var info = new SKImageInfo(width, height);
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Clear(new SKColor(0xE9, 0xE1, 0xCC));
        surface.Canvas.Scale((float)scale);
        surface.Canvas.Translate((float)-left, (float)-top);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { ViewScale = scale });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }
}
