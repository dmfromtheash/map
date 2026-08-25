using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class VectorRenderingTests
{
    [Fact]
    public void Portal_is_a_visible_gap_without_changing_source_nodes()
    {
        var line = VectorGeometry.CreateLine(ObjectId.New(), "road",
            [new VectorPoint(20, 100), new VectorPoint(180, 100)],
            new VectorStrokeStyle { Color = "#FF000000", Width = 20, EndCap = VectorCap.Butt });
        line = Map.Core.Editing.VectorObjectEditor.AddPortal(line, 0.5, 40);
        var ids = line.Line!.Nodes.Select(node => node.Id).ToArray();
        using var bitmap = Render(Document(line));

        Assert.True(bitmap.GetPixel(50, 100).Alpha > 200);
        Assert.True(bitmap.GetPixel(100, 100).Red > 240);
        Assert.Equal(ids, line.Line.Nodes.Select(node => node.Id));
    }

    [Fact]
    public void Subtract_region_cuts_a_transparent_hole_before_background_composition()
    {
        var outer = VectorGeometry.CreateShape(ObjectId.New(), "outer", ShapePrimitiveKind.Rectangle,
            new WorldBounds(20, 20, 180, 180), new ShapeFillStyle { Color = "#FF008000" });
        var hole = new ShapeRegion(ShapeRegionId.New(), ShapeCombineMode.Subtract, ShapePrimitiveKind.Ellipse,
            [new VectorNode(VectorNodeId.New(), 0.3, 0.3), new VectorNode(VectorNodeId.New(), 0.7, 0.7)]);
        var shape = outer with { Shape = new ShapeStyle([.. outer.Shape!.Regions, hole], outer.Shape.Fill, outer.Shape.Stroke) };
        using var bitmap = Render(Document(shape));

        Assert.True(bitmap.GetPixel(40, 40).Green > bitmap.GetPixel(40, 40).Red);
        Assert.True(bitmap.GetPixel(100, 100).Red > 240);
    }

    [Fact]
    public void Asset_stroke_offset_caps_and_shadow_use_the_same_vector_source()
    {
        var hash = new string('b', 64);
        var line = VectorGeometry.CreateLine(ObjectId.New(), "asset road",
            [new VectorPoint(30, 60), new VectorPoint(170, 60)],
            new VectorStrokeStyle
            {
                Width = 16, Offset = 12, EndCap = VectorCap.Square,
                AssetSha256 = hash, AssetSpacing = 0.7, ShadowOpacity = 0.5,
            });
        using var source = new SolidImageSource(hash, SKColors.LimeGreen);
        using var bitmap = Render(Document(line), source);

        var sample = bitmap.GetPixel(100, 72);
        Assert.True(sample.Green > sample.Red);
    }

    private static MapDocument Document(SceneObject item) => new(
        DocumentId.New(), "render", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(200, 200, "px", "#FFFFFFFF"),
        [
            new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
            new ObjectLayer(LayerId.New(), "objects", true, false, 1, [item]),
        ]);

    private static SKBitmap Render(MapDocument document, IStampImageSource? stamps = null)
    {
        var info = new SKImageInfo(200, 200);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { Stamps = stamps });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    private sealed class SolidImageSource : IStampImageSource, IDisposable
    {
        private readonly string _hash;
        private readonly SKImage _image;

        public SolidImageSource(string hash, SKColor color)
        {
            _hash = hash;
            using var bitmap = new SKBitmap(new SKImageInfo(8, 8));
            bitmap.Erase(color);
            _image = SKImage.FromBitmap(bitmap);
        }

        public SKImage? TryGetImage(string sha256) => sha256 == _hash ? _image : null;
        public void Dispose() => _image.Dispose();
    }
}
