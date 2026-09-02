using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// A gradient that is stored but not drawn is not a feature. These check the pixels: the ramp runs
/// in the direction the angle names, a radial ramp is bright at its centre, the object's own
/// opacity still applies, and an object without a gradient renders exactly as it did before.
/// </summary>
public sealed class GradientRenderingTests
{
    [Fact]
    public void A_linear_gradient_runs_in_the_direction_its_angle_names()
    {
        // 90 degrees is top to bottom: black at the top edge, white at the bottom.
        using var bitmap = Render(Document(ShapeWith(new GradientFill(
            GradientKind.Linear,
            [new GradientStopStyle(0, "#FF000000"), new GradientStopStyle(1, "#FFFFFFFF")],
            angleDegrees: 90))));

        var top = Brightness(bitmap.GetPixel(100, 55));
        var bottom = Brightness(bitmap.GetPixel(100, 145));
        Assert.True(bottom > top + 100, $"Expected a top-to-bottom ramp, got {top} then {bottom}.");

        // Across the ramp instead of along it the colour barely moves.
        Assert.True(
            Math.Abs(Brightness(bitmap.GetPixel(60, 100)) - Brightness(bitmap.GetPixel(140, 100))) < 12);
    }

    [Fact]
    public void A_rotated_angle_moves_the_ramp_with_it()
    {
        using var vertical = Render(Document(ShapeWith(Ramp(90))));
        using var horizontal = Render(Document(ShapeWith(Ramp(0))));

        Assert.True(Brightness(vertical.GetPixel(100, 145)) > Brightness(vertical.GetPixel(100, 55)));
        Assert.True(Brightness(horizontal.GetPixel(145, 100)) > Brightness(horizontal.GetPixel(55, 100)));

        static GradientFill Ramp(double angle) => new(
            GradientKind.Linear,
            [new GradientStopStyle(0, "#FF000000"), new GradientStopStyle(1, "#FFFFFFFF")],
            angleDegrees: angle);
    }

    [Fact]
    public void A_radial_gradient_is_brightest_at_the_centre_it_names()
    {
        using var bitmap = Render(Document(ShapeWith(new GradientFill(
            GradientKind.Radial,
            [new GradientStopStyle(0, "#FFFFFFFF"), new GradientStopStyle(1, "#FF000000")],
            centerX: 0.5, centerY: 0.5, radius: 0.5))));

        Assert.True(Brightness(bitmap.GetPixel(100, 100)) > Brightness(bitmap.GetPixel(55, 55)));
    }

    [Fact]
    public void The_object_opacity_still_applies_to_a_gradient()
    {
        // The paint colour is ignored once a shader is set, so the alpha has to be folded into the
        // stops. If it were not, a half-transparent gradient object would render fully opaque.
        var opaque = ShapeWith(Solid("#FF000000"));
        using var full = Render(Document(opaque));
        using var faded = Render(Document(opaque with { ObjectOpacity = 0.25 }));

        Assert.True(Brightness(faded.GetPixel(100, 100)) > Brightness(full.GetPixel(100, 100)) + 60);
    }

    [Fact]
    public void An_object_without_a_gradient_renders_exactly_as_before()
    {
        var shape = Shape();
        using var flat = Render(Document(shape));

        Assert.Equal(new SKColor(0x20, 0x40, 0x60), Opaque(flat.GetPixel(100, 100)));
    }

    [Fact]
    public void A_plain_rectangle_object_takes_a_gradient_too()
    {
        var rectangle = new SceneObject(
            ObjectId.New(), SceneObjectKind.Rectangle, "rect",
            50, 50, 100, 100, 0, "#FF204060", "#00000000", 0, true, false)
        {
            FillGradient = new GradientFill(
                GradientKind.Linear,
                [new GradientStopStyle(0, "#FF000000"), new GradientStopStyle(1, "#FFFFFFFF")],
                angleDegrees: 90),
        };

        using var bitmap = Render(Document(rectangle));

        Assert.True(Brightness(bitmap.GetPixel(100, 145)) > Brightness(bitmap.GetPixel(100, 55)) + 100);
    }

    // ------------------------------------------------------------------------ fixtures

    private static GradientFill Solid(string color) => new(
        GradientKind.Linear,
        [new GradientStopStyle(0, color), new GradientStopStyle(1, color)]);

    private static SceneObject Shape() => VectorGeometry.CreateShape(
        ObjectId.New(), "shape", ShapePrimitiveKind.Rectangle,
        new WorldBounds(50, 50, 150, 150),
        new ShapeFillStyle { Color = "#FF204060" },
        new VectorStrokeStyle { Width = 0, Color = "#00000000" });

    private static SceneObject ShapeWith(GradientFill gradient)
    {
        var shape = Shape();
        return shape with
        {
            Shape = new ShapeStyle(
                shape.Shape!.Regions,
                shape.Shape.Fill with { Gradient = gradient },
                shape.Shape.Stroke,
                shape.Shape.Portals),
        };
    }

    private static MapDocument Document(SceneObject item) => new(
        DocumentId.New(), "gradient", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(200, 200, "px", "#FFFFFFFF"),
        [
            new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
            new ObjectLayer(LayerId.New(), "objects", true, false, 1, [item]),
        ]);

    private static SKBitmap Render(MapDocument document)
    {
        var info = new SKImageInfo(200, 200);
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Clear(SKColors.White);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions());
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    private static double Brightness(SKColor color) => (color.Red + color.Green + color.Blue) / 3.0;

    private static SKColor Opaque(SKColor color) => new(color.Red, color.Green, color.Blue);
}
