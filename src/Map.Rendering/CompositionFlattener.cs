using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

public sealed record FlattenedComposition(byte[] Png, int PixelWidth, int PixelHeight, double RasterScale);

public static class CompositionFlattener
{
    public const int MaxRasterEdge = 4096;

    /// <summary>Rasterizes only the composition bounds and caps its surface; never the whole map.</summary>
    public static FlattenedComposition Flatten(
        SceneObject source, IStampImageSource? stamps = null, int maxEdge = MaxRasterEdge)
    {
        if (source.Composition is null) throw new ArgumentException("Only an editable composition can be flattened.", nameof(source));
        if (maxEdge is < 1 or > MaxRasterEdge) throw new ArgumentOutOfRangeException(nameof(maxEdge));
        var scale = Math.Min(1, maxEdge / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Ceiling(source.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(source.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not allocate the bounded composition surface.");
        surface.Canvas.Clear(SKColors.Transparent);
        var local = new SceneObject(source.Id, SceneObjectKind.Composition, source.Name,
            0, 0, width, height, 0, "#00000000", "#00000000", 0, true, false)
        { Composition = source.Composition, ObjectOpacity = source.ObjectOpacity };
        var document = new MapDocument(DocumentId.New(), "flatten", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(width, height, "px", "#00000000"),
            [
                new BaseColorLayer(LayerId.New(), "transparent", true, false, 1, "#00000000"),
                new ObjectLayer(LayerId.New(), "composition", true, false, 1, [local]),
            ]);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions
        {
            IsExport = true, Stamps = stamps, VisibleWorld = new WorldBounds(0, 0, width, height),
        });
        surface.Canvas.Flush(); using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new(data.ToArray(), width, height, scale);
    }
}
