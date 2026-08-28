using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// Stage 10D: the appearance values the inspector edits actually change pixels — blend modes are
/// distinguishable, flips move an asymmetric image, patterns open gaps, blur spills past the
/// geometry — and the thumbnail/preview path uses exactly the same evaluator as the viewport.
/// </summary>
public sealed class Stage10DAppearanceRenderingTests
{
    private const string Hash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    // ------------------------------------------------------------------------- blend modes

    [Fact]
    public void All_ten_blend_modes_render_and_most_are_pairwise_distinct()
    {
        var samples = new Dictionary<string, SKColor>();
        foreach (var blend in BlendModes.Supported)
        {
            using var source = new QuadrantImageSource(Hash, uniform: new SKColor(0x80, 0xC0, 0x40));
            using var bitmap = Render(
                Document(Stamp(new StampStyle(Hash) { Blend = blend }), "#FF8040C0"), source);
            samples[blend] = bitmap.GetPixel(100, 100);
        }

        var normal = samples["Normal"];
        Assert.True(Brightness(samples["Multiply"]) < Brightness(normal), "Multiply must darken.");
        Assert.True(Brightness(samples["Screen"]) > Brightness(normal), "Screen must lighten.");
        Assert.NotEqual(normal, samples["Difference"]);

        var distinct = samples.Values.Distinct().Count();
        Assert.True(distinct >= 8, $"Only {distinct} of 10 blend modes are distinguishable.");
    }

    // ------------------------------------------------------------------------------- flips

    [Fact]
    public void Horizontal_and_vertical_flips_move_the_asymmetric_quadrant()
    {
        using var source = new QuadrantImageSource(Hash, uniform: null);

        using var plain = Render(Document(Stamp(new StampStyle(Hash))), source);
        using var flippedH = Render(Document(Stamp(new StampStyle(Hash) { FlipHorizontal = true })), source);
        using var flippedV = Render(Document(Stamp(new StampStyle(Hash) { FlipVertical = true })), source);

        // The red quadrant starts top-left (sample 70,70); H-flip moves it top-right (130,70),
        // V-flip moves it bottom-left (70,130).
        Assert.True(IsRed(plain.GetPixel(70, 70)) && !IsRed(plain.GetPixel(130, 70)));
        Assert.True(IsRed(flippedH.GetPixel(130, 70)) && !IsRed(flippedH.GetPixel(70, 70)));
        Assert.True(IsRed(flippedV.GetPixel(70, 130)) && !IsRed(flippedV.GetPixel(70, 70)));
    }

    // ----------------------------------------------------------------------- line patterns

    [Fact]
    public void Dotted_and_dashed_strokes_open_gaps_a_solid_stroke_fills()
    {
        var solid = PaintedAlongPath(StrokePattern.Solid);
        var dotted = PaintedAlongPath(StrokePattern.Dotted);
        var dashed = PaintedAlongPath(StrokePattern.Dashed);

        Assert.InRange(solid, 150, 161);
        Assert.True(dotted < solid - 20, "A dotted stroke must leave gaps.");
        Assert.True(dashed < solid - 20, "A dashed stroke must leave gaps.");
        Assert.NotEqual(dotted, dashed);
    }

    // -------------------------------------------------------------------------- blur spill

    [Fact]
    public void Stroke_blur_spills_ink_past_the_hard_edge_of_the_line()
    {
        using var sharp = Render(Document(Line(new VectorStrokeStyle
        {
            Color = "#FF000000",
            Width = 10,
            EndCap = VectorCap.Butt,
        })));
        using var blurred = Render(Document(Line(new VectorStrokeStyle
        {
            Color = "#FF000000",
            Width = 10,
            EndCap = VectorCap.Butt,
            Blur = 6,
        })));

        // 15 px above the path: far past the 5 px half-width, inside the 3-sigma blur reach.
        Assert.Equal(255, sharp.GetPixel(100, 85).Red);
        Assert.True(blurred.GetPixel(100, 85).Red < 250, "Blur did not spill past the edge.");
        Assert.True(blurred.GetPixel(100, 100).Red > sharp.GetPixel(100, 85).Red - 255,
            "Blurred centre lost all ink.");
    }

    [Fact]
    public void Shape_blur_softens_fill_and_stroke_together()
    {
        var style = new VectorStrokeStyle { Color = "#FF000000", Width = 4 };
        using var sharp = Render(Document(Shape(style)));
        using var blurred = Render(Document(Shape(style with { Blur = 5 })));

        // Outside the rectangle's right edge (x=160) and past the 2 px stroke half-width:
        // background stays white only when sharp.
        Assert.Equal(255, sharp.GetPixel(166, 100).Red);
        Assert.True(blurred.GetPixel(166, 100).Red < 250, "Shape blur did not reach past the edge.");
    }

    // -------------------------------------------------- one evaluator: viewport == preview

    [Fact]
    public void The_thumbnail_preview_is_pixel_identical_to_the_viewport_evaluator()
    {
        var document = Document(
            Stamp(new StampStyle(Hash) { Blend = "Multiply", Blur = 2, ShadowOpacity = 0.4 }),
            "#FF8040C0");

        var png = ThumbnailRenderer.RenderPng(document, 200, 200);
        using var decoded = SKBitmap.Decode(png);

        var info = new SKImageInfo(200, 200, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Clear(SKColors.Transparent);
        var scale = Math.Min(200.0 / document.Canvas.Width, 200.0 / document.Canvas.Height);
        surface.Canvas.Translate(
            (float)((200 - (document.Canvas.Width * scale)) / 2),
            (float)((200 - (document.Canvas.Height * scale)) / 2));
        surface.Canvas.Scale((float)scale);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true });
        var expected = new SKBitmap(info);
        surface.ReadPixels(info, expected.GetPixels(), info.RowBytes, 0, 0);

        for (var y = 0; y < 200; y += 5)
        {
            for (var x = 0; x < 200; x += 5)
            {
                Assert.Equal(expected.GetPixel(x, y), decoded.GetPixel(x, y));
            }
        }

        expected.Dispose();
    }

    // ----------------------------------------------------------------------------- helpers

    private static double Brightness(SKColor color) => color.Red + color.Green + color.Blue;

    private static bool IsRed(SKColor color) => color.Red > 180 && color.Green < 100;

    private static int PaintedAlongPath(StrokePattern pattern)
    {
        using var bitmap = Render(Document(Line(new VectorStrokeStyle
        {
            Color = "#FF000000",
            Width = 8,
            EndCap = VectorCap.Butt,
            NodeCap = VectorCap.Butt,
            Pattern = pattern,
        })));

        var painted = 0;
        for (var x = 20; x <= 180; x++)
        {
            if (bitmap.GetPixel(x, 100).Red < 128)
            {
                painted++;
            }
        }

        return painted;
    }

    private static SceneObject Stamp(StampStyle style) => new(
        ObjectId.New(), SceneObjectKind.Stamp, "stamp", 60, 60, 80, 80, 0,
        "#FFFFFFFF", "#00000000", 0, true, false)
    {
        Stamp = style,
    };

    private static SceneObject Line(VectorStrokeStyle stroke) => VectorGeometry.CreateLine(
        ObjectId.New(), "line", [new VectorPoint(20, 100), new VectorPoint(180, 100)], stroke);

    private static SceneObject Shape(VectorStrokeStyle stroke) => VectorGeometry.CreateShape(
        ObjectId.New(), "shape", ShapePrimitiveKind.Rectangle, new WorldBounds(40, 60, 160, 140),
        new ShapeFillStyle { Color = "#FF3355AA" }, stroke);

    private static MapDocument Document(SceneObject item, string background = "#FFFFFFFF") => new(
        DocumentId.New(), "render", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(200, 200, "px", background),
        [
            new BaseColorLayer(LayerId.New(), "background", true, false, 1, background),
            new ObjectLayer(LayerId.New(), "objects", true, false, 1, [item]),
        ]);

    private static SKBitmap Render(MapDocument document, IStampImageSource? stamps = null)
    {
        var info = new SKImageInfo(200, 200);
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Clear(SKColors.White);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { Stamps = stamps });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    /// <summary>
    /// 8x8 image: red top-left quadrant, green elsewhere — or one uniform colour when asked.
    /// </summary>
    private sealed class QuadrantImageSource : IStampImageSource, IDisposable
    {
        private readonly string _hash;
        private readonly SKImage _image;

        public QuadrantImageSource(string hash, SKColor? uniform)
        {
            _hash = hash;
            using var bitmap = new SKBitmap(new SKImageInfo(8, 8));
            if (uniform is { } color)
            {
                bitmap.Erase(color);
            }
            else
            {
                bitmap.Erase(SKColors.Green);
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        bitmap.SetPixel(x, y, SKColors.Red);
                    }
                }
            }

            _image = SKImage.FromBitmap(bitmap);
        }

        public SKImage? TryGetImage(string sha256) => sha256 == _hash ? _image : null;

        public void Dispose() => _image.Dispose();
    }
}
