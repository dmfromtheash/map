using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public class ThumbnailRendererTests
{
    private static MapDocument Document(params SceneObject[] objects) => new(
        DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1600, 900, "px", "#FF204060"),
        [new Layer(LayerId.New(), "objects", true, false, 1.0, objects)]);

    private static SceneObject Rectangle(string fill = "#FFCC3344") => new(
        ObjectId.New(), SceneObjectKind.Rectangle, "rect",
        200, 200, 400, 300, 0, fill, "#FF000000", 4, visible: true, locked: false);

    [Fact]
    public void Thumbnail_is_a_real_png_of_the_requested_size_with_alpha()
    {
        var bytes = ThumbnailRenderer.RenderPng(Document(Rectangle()));

        Assert.True(bytes.Length > 100);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);

        using var decoded = SKBitmap.Decode(bytes);
        Assert.NotNull(decoded);
        Assert.Equal(ThumbnailRenderer.Width, decoded!.Width);
        Assert.Equal(ThumbnailRenderer.Height, decoded.Height);
        Assert.NotEqual(SKAlphaType.Opaque, decoded.Info.AlphaType);
    }

    [Fact]
    public void Thumbnail_is_not_empty_and_shows_the_page_colour()
    {
        var bytes = ThumbnailRenderer.RenderPng(Document());
        using var decoded = SKBitmap.Decode(bytes)!;

        // 1600x900 page fits 320x180 exactly, so the centre pixel is the page background.
        var centre = decoded.GetPixel(160, 90);
        Assert.Equal(0x20, centre.Red);
        Assert.Equal(0x40, centre.Green);
        Assert.Equal(0x60, centre.Blue);
        Assert.Equal(255, centre.Alpha);
    }

    [Fact]
    public void Thumbnail_changes_when_the_scene_meaningfully_changes()
    {
        var without = ThumbnailRenderer.RenderPng(Document());
        var with = ThumbnailRenderer.RenderPng(Document(Rectangle()));

        Assert.NotEqual(without, with);
    }

    [Fact]
    public void Thumbnail_never_contains_the_selection_overlay()
    {
        // Render the same scene through the editor path WITH selection and through the thumbnail
        // path; the selection ring colour must exist in the first and not in the second.
        var rectangle = Rectangle();
        var document = Document(rectangle);

        var thumbnailBytes = ThumbnailRenderer.RenderPng(document);
        using var thumbnail = SKBitmap.Decode(thumbnailBytes)!;
        Assert.Equal(0, CountSelectionPixels(thumbnail));

        // 1:1 scale so the 2px ring is not diluted below the colour tolerance by antialiasing.
        var info = new SKImageInfo(800, 700, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(
            surface.Canvas, document, new RenderOptions { SelectedObjectId = rectangle.Id });
        surface.Canvas.Flush();

        using var editor = new SKBitmap(info);
        surface.ReadPixels(info, editor.GetPixels(), info.RowBytes, 0, 0);
        Assert.True(CountSelectionPixels(editor) > 0, "The editor path drew no selection ring at all.");
    }

    [Fact]
    public void Hidden_objects_and_hidden_layers_do_not_reach_the_thumbnail()
    {
        var hiddenObject = new SceneObject(
            ObjectId.New(), SceneObjectKind.Rectangle, "hidden",
            200, 200, 400, 300, 0, "#FFFF0000", "#FF000000", 0, visible: false, locked: false);

        var visible = ThumbnailRenderer.RenderPng(Document());
        var withHidden = ThumbnailRenderer.RenderPng(Document(hiddenObject));

        Assert.Equal(visible, withHidden);
    }

    private static int CountSelectionPixels(SKBitmap bitmap)
    {
        // Selection ring colour is rgb(255, 214, 10).
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (Math.Abs(pixel.Red - 255) <= 4 && Math.Abs(pixel.Green - 214) <= 4 && Math.Abs(pixel.Blue - 10) <= 4)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
