using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public class Stage14PlacementPreviewRenderingTests
{
    [Fact]
    public void Stage14_ghost_uses_future_geometry_and_opacity_without_entering_thumbnail_or_document()
    {
        var document = Document();
        var ghost = new SceneObject(
            ObjectId.New(), SceneObjectKind.Ellipse, "Будущий эллипс",
            30, 40, 80, 50, 0, "#FFFF0000", "#FFFFFFFF", 2, true, false)
        {
            ObjectOpacity = 0.55,
        };
        var before = ThumbnailRenderer.RenderPng(document, 160, 120);

        using var rendered = Render(document, new RenderOptions
        {
            PlacementPreviewObjects = [ghost],
            ViewScale = 1,
        });
        var inside = rendered.GetPixel(70, 65);
        var outside = rendered.GetPixel(10, 10);
        var after = ThumbnailRenderer.RenderPng(document, 160, 120);

        Assert.True(inside.Red > outside.Red + 80);
        Assert.InRange(inside.Red, 120, 180);
        Assert.Empty(document.Layers.OfType<ObjectLayer>().Single().Objects);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Stage14_stamp_ghost_reuses_real_asset_source_and_export_without_preview_stays_clean()
    {
        var document = Document();
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var ghost = new SceneObject(
            ObjectId.New(), SceneObjectKind.Stamp, "Будущий штамп",
            20, 20, 60, 40, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        {
            ObjectOpacity = 0.55,
            Stamp = new StampStyle(sha) { ReferenceWidth = 60, ReferenceHeight = 40 },
        };
        using var source = new SolidStampSource(sha, SKColors.LimeGreen);
        using var preview = Render(document, new RenderOptions
        {
            PlacementPreviewObjects = [ghost],
            Stamps = source,
            ViewScale = 1,
        });
        using var export = Render(document, new RenderOptions { IsExport = true, Stamps = source });

        Assert.True(preview.GetPixel(50, 40).Green > export.GetPixel(50, 40).Green + 60);
        Assert.Equal(1, source.Requests);
    }

    private static SKBitmap Render(MapDocument document, RenderOptions options)
    {
        var info = new SKImageInfo(160, 120);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, options);
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }

    private static MapDocument Document() => new(
        DocumentId.New(), "Stage 14", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(160, 120, "px", "#FF101010"),
        [
            new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF101010"),
            new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []),
        ]);

    private sealed class SolidStampSource : IStampImageSource, IDisposable
    {
        private readonly string _sha;
        private readonly SKImage _image;

        public SolidStampSource(string sha, SKColor color)
        {
            _sha = sha;
            using var surface = SKSurface.Create(new SKImageInfo(16, 12))!;
            surface.Canvas.Clear(color);
            _image = surface.Snapshot();
        }

        public int Requests { get; private set; }

        public SKImage? TryGetImage(string sha256)
        {
            Requests++;
            return string.Equals(sha256, _sha, StringComparison.OrdinalIgnoreCase) ? _image : null;
        }

        public void Dispose() => _image.Dispose();
    }
}
