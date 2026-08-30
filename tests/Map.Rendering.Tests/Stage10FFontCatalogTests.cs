using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class Stage10FFontCatalogTests
{
    [Fact]
    public void Runtime_catalog_is_nonempty_deduplicated_and_stably_sorted()
    {
        var families = SystemFontCatalog.Families;

        Assert.NotEmpty(families);
        Assert.Equal(families.Count, families.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            families,
            families.OrderBy(family => family, StringComparer.OrdinalIgnoreCase)
                .ThenBy(family => family, StringComparer.Ordinal));

        var available = families.First(family => !family.StartsWith('@'));
        var resolution = TextFontResolver.Resolve(available, "Arial");
        Assert.False(resolution.UsedFallback);
        Assert.Equal(available, resolution.ResolvedFamily, ignoreCase: true);
    }

    [Fact]
    public void Export_renders_missing_request_through_fallback_without_mutating_document()
    {
        const string missing = "Map Stage 10F Deliberately Missing Font";
        var text = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "label",
            10, 10, 220, 100, 0, "#FF000000", "#FFFFFFFF", 0, true, false)
        {
            Text = new TextStyle
            {
                Content = "Fallback export",
                RequestedFontFamily = missing,
                FallbackFontFamily = "Arial",
                FontSize = 30,
            },
        };
        var document = new MapDocument(DocumentId.New(), "font export", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(240, 120, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(LayerId.New(), "objects", true, false, 1, [text]),
            ]);
        var info = new SKImageInfo(240, 120);
        using var surface = SKSurface.Create(info)!;

        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true });
        using var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);

        Assert.Equal(missing, text.Text!.RequestedFontFamily);
        Assert.Equal("Arial", text.Text.FallbackFontFamily);
        Assert.Contains(
            Enumerable.Range(0, info.Width)
                .SelectMany(x => Enumerable.Range(0, info.Height).Select(y => bitmap.GetPixel(x, y))),
            color => color.Red < 100);
    }
}
