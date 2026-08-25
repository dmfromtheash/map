using System.Diagnostics;
using Map.Spike.Core.Documents;
using Map.Spike.Core.Geometry;
using Map.Spike.Rendering;
using SkiaSharp;

namespace Map.Spike.Export;

public sealed record ExportResult(
    string Path,
    int Width,
    int Height,
    long FileBytes,
    TimeSpan Duration,
    RenderStats Stats);

/// <summary>
/// Offscreen render to a transparent PNG. Runs on a CPU raster surface on purpose: an export must
/// not depend on a window, a swap chain or a live GPU context, and must produce identical bytes on
/// a machine with no usable GPU.
/// </summary>
public sealed class OffscreenPngExporter
{
    public static ExportResult Export(SpikeDocument document, string path, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Export size must be positive.");
        }

        var startedAt = Stopwatch.GetTimestamp();

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException($"Could not create a {width}x{height} raster surface.");

        var viewport = new Viewport();
        viewport.Resize(width, height);

        // Fit the whole canvas into the target, so export scale is derived, not guessed.
        var scale = Math.Min(
            width / (double)document.CanvasWidth,
            height / (double)document.CanvasHeight);
        viewport.Set(Vec2.Zero, scale);

        var settings = new RenderSettings
        {
            // Editor chrome is excluded here, and RenderSettings.IncludeOverlay is the only switch
            // that can draw it, so no selection ring or mask tint can reach an exported file.
            IncludeOverlay = false,
            ShowMaskEditOverlay = false,
            MaskEnabled = true,
            BackgroundColor = null,
        };

        using var tileCache = new TileImageCache();
        using var geometryCache = ObjectGeometryCache.Build(document.Objects);

        var stats = default(RenderStats);
        SceneRenderer.Render(
            surface.Canvas, document, viewport, settings, tileCache, geometryCache, ref stats);

        surface.Canvas.Flush();

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (var image = surface.Snapshot())
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var stream = File.Create(path))
        {
            data.SaveTo(stream);
            stream.Flush(flushToDisk: true);
        }

        var info2 = new FileInfo(path);
        return new ExportResult(
            Path.GetFullPath(path),
            width,
            height,
            info2.Length,
            Stopwatch.GetElapsedTime(startedAt),
            stats);
    }
}
