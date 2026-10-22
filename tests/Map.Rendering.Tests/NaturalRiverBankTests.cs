using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// «Живая река» (owner 2026-10-22): sand bars on the inner banks of the bends, a ragged dry-brush
/// edge instead of a pen line, and older rivers keep their even outline.
/// </summary>
public sealed class NaturalRiverBankTests
{
    private static readonly VectorPoint[] Meander =
    [
        new(30, 120), new(180, 90), new(300, 160), new(330, 290), new(450, 360), new(600, 300),
        new(660, 180), new(790, 140), new(880, 230),
    ];

    [Fact]
    public void The_living_river_changes_the_look_and_older_looks_do_not_carry_it()
    {
        using var a = Render(River(RiverStyle.Painted() with { TextureId = null }), 900, 480, 1);
        using var b = Render(River(RiverStyle.Natural() with { TextureId = null }), 900, 480, 1);
        var differing = 0;
        for (var y = 0; y < 480; y += 2)
            for (var x = 0; x < 900; x += 2)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) differing++;
        Assert.True(differing > 800, $"changed pixels {differing}");
        Assert.Equal(0, RiverStyle.Painted().BankManner);
        Assert.Equal(0, RiverStyle.Illustrated().BankManner);
        Assert.Equal(1, RiverStyle.Natural().BankManner);
    }

    [Fact]
    public void Sand_bars_lie_inside_the_bends()
    {
        using var bitmap = Render(River(RiverStyle.Natural() with { TextureId = null }), 900, 480, 1);
        // The bottom of the U (450, 360) turns up: its bar lies above the stream, inside the bend.
        int Sand(int y0, int y1)
        {
            var count = 0;
            for (var y = y0; y < y1; y++)
                for (var x = 380; x < 520; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    if (Math.Abs(c.Red - 0xC9) < 18 && Math.Abs(c.Green - 0xBF) < 18 && Math.Abs(c.Blue - 0xA3) < 18) count++;
                }
            return count;
        }
        var inside = Sand(300, 352);
        var outside = Sand(352, 420);
        Assert.True(inside > 40 && inside > outside * 3, $"sand inside the bend {inside}, outside {outside}");
    }

    [Theory]
    [InlineData(0.04)]
    [InlineData(0.2)]
    public void Far_out_the_living_river_still_draws(double scale)
    {
        var river = River(RiverStyle.Natural() with { TextureId = null });
        var layer = new ObjectLayer(LayerId.New(), "Вода", true, false, 1, []) { Rivers = [river] };
        var document = new MapDocument(DocumentId.New(), "river", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(900, 480, "px", "#FFE9E1CC"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFE9E1CC"), layer]);
        var stats = new RenderStats();
        using var surface = SKSurface.Create(new SKImageInfo(200, 200))!;
        surface.Canvas.Scale((float)scale);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { ViewScale = scale, Stats = stats });
        Assert.True(stats.ObjectsDrawn > 0, $"objects drawn at {scale}: {stats.ObjectsDrawn}");
    }

    /// <summary>MAP_RIVER_VARIANTS=&lt;folder&gt;: the chosen look at 1:1 and close up.</summary>
    [Fact]
    public void Frames_for_review()
    {
        var folder = Environment.GetEnvironmentVariable("MAP_RIVER_VARIANTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var river = River(RiverStyle.Natural());
        using (var one = Render(river, 900, 480, 1)) Save(one, folder, "natural-1x.png");
        using (var two = Render(river, 900, 480, 2.2, 230, 120)) Save(two, folder, "natural-2x.png");
    }

    private static River River(RiverStyle style) => new() { Name = "Река", Style = style, Channels = [RiverEditor.NewChannel(Meander, 30)] };

    private static void Save(SKBitmap bitmap, string folder, string name)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(folder, name), data.ToArray());
    }

    private static SKBitmap Render(River river, int width, int height, double scale, double left = 0, double top = 0)
    {
        const string paper = "#FFE9E1CC";
        var layer = new ObjectLayer(LayerId.New(), "Вода", true, false, 1, []) { Rivers = [river] };
        var document = new MapDocument(DocumentId.New(), "river", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(900, 480, "px", paper),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, paper), layer]);
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
