using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class Stage7RenderingTests
{
    [Fact]
    public void Missing_font_uses_declared_fallback_without_changing_request()
    {
        var resolution = TextFontResolver.Resolve("Map Stage 7 Missing Font", "Arial");
        Assert.Equal("Map Stage 7 Missing Font", resolution.RequestedFamily);
        Assert.True(resolution.UsedFallback);
        Assert.False(string.IsNullOrWhiteSpace(resolution.ResolvedFamily));
    }

    [Fact]
    public void Editor_only_grid_is_drawn_in_editor_and_omitted_from_export()
    {
        var grid = new GridLayer(LayerId.New(), "grid", true, false, 1,
            [new GridDefinition(GridId.New(), GridGeometry.Square, 64)
            { Color = "#FF000000", Opacity = 1, LineWidth = 4, IncludeInExport = false }]);
        var document = Document(grid);
        using var editor = Render(document, false); using var export = Render(document, true);

        Assert.True(editor.GetPixel(64, 31).Red < 80);
        Assert.True(export.GetPixel(64, 31).Red > 240);
    }

    [Fact]
    public void Multiline_text_produces_visible_pixels()
    {
        var text = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "label",
            20, 20, 180, 140, 0, "#FF000000", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = "One\nTwo", FontSize = 42, RequestedFontFamily = "Arial" } };
        using var bitmap = Render(Document(new ObjectLayer(LayerId.New(), "objects", true, false, 1, [text])), false);
        Assert.Contains(
            Enumerable.Range(0, 200).SelectMany(x => Enumerable.Range(0, 200).Select(y => bitmap.GetPixel(x, y))),
            color => color.Red < 100);
    }

    private static MapDocument Document(MapLayer layer) => new(DocumentId.New(), "render", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(200, 200, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"), layer]);

    private static SKBitmap Render(MapDocument document, bool export)
    {
        var info = new SKImageInfo(200, 200); using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = export });
        var bitmap = new SKBitmap(info); surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }
}
