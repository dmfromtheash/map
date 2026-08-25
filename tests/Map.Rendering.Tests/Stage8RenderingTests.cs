using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class Stage8RenderingTests
{
    [Fact]
    public void Idle_and_export_use_the_same_exact_filter_evaluator()
    {
        var document = FilterDocument(masked: false);
        using var idle = Render(document, new RenderOptions());
        using var export = Render(document, new RenderOptions { IsExport = true });
        Assert.Equal(Pixels(idle), Pixels(export));
        Assert.InRange(idle.GetPixel(100, 100).Red, 105, 255);
    }

    [Fact]
    public void Filter_mask_is_local_and_fast_preview_does_not_mutate_document()
    {
        var document = FilterDocument(masked: true); var before = document;
        using var rendered = Render(document, new RenderOptions { FastPreview = true });
        Assert.NotEqual(rendered.GetPixel(40, 100), rendered.GetPixel(170, 100));
        Assert.Same(before, document);
        Assert.Equal(1, document.Layers.OfType<FilterLayer>().Single().Mask!.Tiles.TileCount);
    }

    [Fact]
    public void Explicit_flatten_is_transparent_and_bounded_to_selection_not_canvas()
    {
        var part = new SceneObject(ObjectId.New(), SceneObjectKind.Ellipse, "part",
            1000, 1000, 100, 80, 0, "#8000AA00", "#00000000", 0, true, false);
        var source = new MapDocument(DocumentId.New(), "large", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(65_536, 65_536, "px", "#00000000"),
            [new BaseColorLayer(LayerId.New(), "transparent", true, false, 1, "#00000000"),
             new ObjectLayer(LayerId.New(), "objects", true, false, 1, [part])]);
        var package = CompositionTransfer.Copy(source, [part.Id]);
        var composition = CompositionTransfer.CreateInstance(package, 32_768, 32_768, 512);

        var flattened = CompositionFlattener.Flatten(composition, maxEdge: 256);

        Assert.True(flattened.PixelWidth <= 256 && flattened.PixelHeight <= 256);
        using var data = SKData.CreateCopy(flattened.Png); using var image = SKImage.FromEncodedData(data);
        Assert.NotNull(image);
        using var bitmap = SKBitmap.FromImage(image);
        Assert.Contains(Pixels(bitmap), color => color.Alpha is > 0 and < 255);
    }

    private static MapDocument FilterDocument(bool masked)
    {
        LayerMask? mask = null;
        if (masked)
        {
            var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
            for (var y = 0; y < TileMap.TileSize; y++)
            for (var x = 0; x < 120; x++) bytes[y * TileMap.TileSize + x] = 255;
            mask = new LayerMask(TileMap.Empty(1).With(new TileKey(0, 0), bytes), true);
        }
        return new MapDocument(DocumentId.New(), "filter", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(200, 200, "px", "#FF506070"),
            [new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FF506070"),
             new FilterLayer(LayerId.New(), "filter", true, false, 1,
                 new FilterSettings { Brightness = 0.35, Temperature = 0.3 }, mask)]);
    }

    private static SKBitmap Render(MapDocument document, RenderOptions options)
    {
        var info = new SKImageInfo(200, 200); using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, options); var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0); return bitmap;
    }

    private static SKColor[] Pixels(SKBitmap bitmap)
    {
        var pixels = new SKColor[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++) pixels[y * bitmap.Width + x] = bitmap.GetPixel(x, y);
        return pixels;
    }
}
