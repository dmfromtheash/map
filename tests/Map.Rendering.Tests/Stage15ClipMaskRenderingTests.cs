using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class Stage15ClipMaskRenderingTests
{
    private const string VisualSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string MaskSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Stage15ClipMask_composes_base_plus_foreground_times_mask_coverage_0_128_255()
    {
        using var source = new SyntheticSource();
        using var rendered = Render(Document(enabled: true, opacity: 1), source);

        var blackMask = rendered.GetPixel(10, 10);
        var halfMask = rendered.GetPixel(30, 10);
        var whiteMask = rendered.GetPixel(50, 10);
        Assert.True(blackMask.Blue > 240 && blackMask.Red < 15);
        Assert.InRange(halfMask.Red, 115, 140);
        Assert.InRange(halfMask.Blue, 115, 140);
        Assert.True(whiteMask.Red > 240 && whiteMask.Blue < 15);
    }

    [Fact]
    public void Stage15ClipMask_foreground_cannot_paint_outside_original_visual_alpha()
    {
        using var source = new SyntheticSource(transparentVisual: true);
        using var rendered = Render(Document(enabled: true, opacity: 1), source);

        var transparentVisualRegion = rendered.GetPixel(50, 10);
        Assert.True(transparentVisualRegion.Red < 10);
        Assert.True(transparentVisualRegion.Green < 10);
        Assert.True(transparentVisualRegion.Blue < 10);
    }

    [Fact]
    public void Stage15ClipMask_off_is_pixel_identical_to_legacy_and_opacity_is_applied_once()
    {
        using var source = new SyntheticSource();
        using var legacy = Render(Document(enabled: false, includeMaskIdentity: false), source);
        using var off = Render(Document(enabled: false, includeMaskIdentity: true), source);
        Assert.Equal(Bytes(legacy), Bytes(off));

        using var half = Render(Document(enabled: true, opacity: 0.5), source);
        var pixel = half.GetPixel(50, 10);
        Assert.InRange(pixel.Red, 120, 135);
        Assert.True(pixel.Blue < 15);

        var layeredDocument = Document(enabled: true, opacity: 0.5);
        var layer = layeredDocument.Layers.OfType<ObjectLayer>().Single();
        layeredDocument = layeredDocument.WithLayer(
            layer.Id, current => ((ObjectLayer)current) with { Opacity = 0.5 });
        using var layered = Render(layeredDocument, source);
        Assert.InRange(layered.GetPixel(50, 10).Red, 55, 75);
    }

    [Fact]
    public void Stage15ClipMask_preview_and_committed_transform_render_the_same_pixels()
    {
        using var source = new SyntheticSource();
        var sourceStamp = Stamp(enabled: true, opacity: 0.7);
        var stamp = new SceneObject(
            sourceStamp.Id, sourceStamp.Kind, sourceStamp.Name,
            sourceStamp.X, sourceStamp.Y, sourceStamp.Width, sourceStamp.Height, 27,
            sourceStamp.FillColor, sourceStamp.StrokeColor, sourceStamp.StrokeWidth,
            sourceStamp.Visible, sourceStamp.Locked)
        {
            ObjectOpacity = sourceStamp.ObjectOpacity,
            Stamp = sourceStamp.Stamp! with
            {
                FlipHorizontal = true,
                FlipVertical = true,
            },
        };
        var committed = Document(stamp, Foreground());
        var previewDocument = Document([], Foreground());
        using var committedBitmap = Render(committed, source);
        using var previewBitmap = Render(previewDocument, source, new RenderOptions
        {
            Stamps = source,
            ViewScale = 1,
            PlacementPreviewObjects = [stamp],
            PlacementPreviewLayerId = previewDocument.Layers.OfType<ObjectLayer>().Single().Id,
        });

        foreach (var point in new[] { new SKPointI(24, 10), new SKPointI(30, 10), new SKPointI(36, 10) })
        {
            Assert.Equal(
                committedBitmap.GetPixel(point.X, point.Y),
                previewBitmap.GetPixel(point.X, point.Y));
        }
    }

    private static MapDocument Document(
        bool enabled, double opacity = 1, bool includeMaskIdentity = true) =>
        Document(Stamp(enabled, opacity, includeMaskIdentity), Foreground());

    private static MapDocument Document(SceneObject stamp, StampForegroundSurface foreground) =>
        Document([stamp], foreground);

    private static MapDocument Document(
        IReadOnlyList<SceneObject> objects, StampForegroundSurface foreground)
    {
        var objectLayer = new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects);
        return new MapDocument(
            DocumentId.New(), "clip", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(80, 60, "px", "#FF000000"),
            [
                new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FF000000"),
                objectLayer,
            ],
            stampForeground: foreground);
    }

    private static SceneObject Stamp(
        bool enabled, double opacity = 1, bool includeMaskIdentity = true) => new(
        ObjectId.New(), SceneObjectKind.Stamp, "stamp", 0, 0, 60, 20, 0,
        "#FFFFFFFF", "#00000000", 0, true, false)
    {
        ObjectOpacity = opacity,
        Stamp = new StampStyle(VisualSha)
        {
            MaskAssetSha256 = includeMaskIdentity ? MaskSha : null,
            ClipMaskEnabled = enabled,
        },
    };

    private static StampForegroundSurface Foreground()
    {
        var pixels = new byte[TileMap.TileSize * TileMap.TileSize * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 255;
            pixels[offset + 3] = 255;
        }
        return new StampForegroundSurface(
            TileMap.Empty(4).With(new TileKey(0, 0), pixels));
    }

    private static SKBitmap Render(
        MapDocument document, IStampImageSource source, RenderOptions? supplied = null)
    {
        var info = new SKImageInfo(80, 60);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document,
            supplied ?? new RenderOptions { Stamps = source, ViewScale = 1 });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }

    private static byte[] Bytes(SKBitmap bitmap)
    {
        var bytes = new byte[bitmap.ByteCount];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        return bytes;
    }

    private sealed class SyntheticSource : IStampImageSource, IDisposable
    {
        private readonly SKImage _visual;
        private readonly SKImage _mask;

        public SyntheticSource(bool transparentVisual = false)
        {
            if (transparentVisual)
            {
                using var bitmap = new SKBitmap(new SKImageInfo(3, 1));
                bitmap.SetPixel(0, 0, SKColors.Blue);
                bitmap.SetPixel(1, 0, SKColors.Blue);
                bitmap.SetPixel(2, 0, SKColors.Transparent);
                _visual = SKImage.FromBitmap(bitmap);
            }
            else
            {
                using var surface = SKSurface.Create(new SKImageInfo(3, 1))!;
                surface.Canvas.Clear(SKColors.Blue);
                _visual = surface.Snapshot();
            }
            using (var bitmap = new SKBitmap(new SKImageInfo(3, 1)))
            {
                bitmap.SetPixel(0, 0, SKColors.Black);
                bitmap.SetPixel(1, 0, new SKColor(128, 128, 128));
                bitmap.SetPixel(2, 0, SKColors.White);
                _mask = SKImage.FromBitmap(bitmap);
            }
        }

        public SKImage? TryGetImage(string sha256) => sha256 switch
        {
            VisualSha => _visual,
            MaskSha => _mask,
            _ => null,
        };

        public void Dispose()
        {
            _visual.Dispose();
            _mask.Dispose();
        }
    }
}
