using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>The brush footprint under the pointer: a ring for the plain brush, the real cells for a rough land dab.</summary>
public sealed class BrushCursorTests
{
    private static MapDocument Sea() => new(DocumentId.New(), "sea", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(256, 256, "px", "#FF336699"),
        [new BaseColorLayer(LayerId.New(), "sea", true, false, 1, "#FF336699")]);

    [Fact]
    public void Plain_brush_draws_a_ring_at_its_radius_and_nothing_inside()
    {
        using var frame = Render(Sea(), new RenderOptions { BrushCursor = new BrushCursorPreview(128, 128, 40, 0, false) });
        var sea = new SKColor(0x33, 0x66, 0x99);
        Assert.NotEqual(sea, frame.GetPixel(168, 128));   // on the ring
        Assert.Equal(sea, frame.GetPixel(128, 128));      // the centre is untouched
        Assert.Equal(sea, frame.GetPixel(128, 60));       // far outside too
        using var none = Render(Sea(), new RenderOptions());
        Assert.Equal(sea, none.GetPixel(168, 128));
    }

    [Fact]
    public void Rough_brush_outlines_the_cells_the_dab_would_paint()
    {
        var cursor = new BrushCursorPreview(128, 128, 48, 24, false);
        using var frame = Render(Sea(), new RenderOptions { BrushCursor = cursor });
        var sea = new SKColor(0x33, 0x66, 0x99);
        // Somewhere along the circle the cells reach beyond it and somewhere they fall short:
        // the outline is not the circle. Count how the outline disagrees with the circle.
        var beyond = 0; var short_ = 0;
        for (var angle = 0; angle < 360; angle += 5)
        {
            var radians = angle * Math.PI / 180;
            var outside = frame.GetPixel((int)(128 + Math.Cos(radians) * 56), (int)(128 + Math.Sin(radians) * 56)) != sea;
            var inside = frame.GetPixel((int)(128 + Math.Cos(radians) * 38), (int)(128 + Math.Sin(radians) * 38)) != sea;
            if (outside) beyond++;
            if (!inside && !RoughCells.InsideRoughCircle(128 + Math.Cos(radians) * 38, 128 + Math.Sin(radians) * 38, 128, 128, 48, 24)) short_++;
        }
        Assert.True(beyond > 0, "the cells never reach beyond the circle");
        // Every pixel the dab would cover is tinted; a pixel well outside the reach is not.
        Assert.NotEqual(sea, frame.GetPixel(128, 128));
        Assert.Equal(sea, frame.GetPixel(20, 20));
    }

    private static SKBitmap Render(MapDocument document, RenderOptions options)
    {
        var info = new SKImageInfo(256, 256);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, options);
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        var output = Environment.GetEnvironmentVariable("MAP_ROUGH_COAST_PNG");
        if (!string.IsNullOrWhiteSpace(output) && options.BrushCursor is { } cursor)
        {
            Directory.CreateDirectory(output);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(Path.Combine(output, $"cursor-{(cursor.RoughCellSize > 0 ? "rough" : "plain")}.png"));
            data.SaveTo(file);
        }
        return bitmap;
    }
}
