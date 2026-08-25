using SkiaSharp;

namespace Map.Spike.App.Canvas;

/// <summary>
/// Records what the renderer actually was during the last frame, observed from inside the Skia
/// lease. A performance number is meaningless without it: the same code on a software fallback and
/// on a GPU backend are two different measurements (docs/08).
/// </summary>
public sealed class RendererProbe
{
    public bool Sampled { get; private set; }

    /// <summary>True when Skia had a live GPU context for the frame.</summary>
    public bool GpuAccelerated { get; private set; }

    public GRBackend? Backend { get; private set; }

    public string SurfaceDescription { get; private set; } = "unknown";

    public string PlatformGraphics { get; set; } = "unknown";

    public void Sample(GRContext? context, SKSurface? surface)
    {
        Sampled = true;
        GpuAccelerated = context is not null;
        Backend = context?.Backend;

        SurfaceDescription = surface is null
            ? "no surface exposed by the lease"
            : $"{surface.Canvas.DeviceClipBounds.Width}x{surface.Canvas.DeviceClipBounds.Height}";
    }

    public string Describe()
    {
        if (!Sampled)
        {
            return "renderer: not sampled yet";
        }

        var mode = GpuAccelerated
            ? $"GPU ({Backend})"
            : "CPU software raster (no GRContext) - FALLBACK";

        return $"renderer: {mode}; platform graphics: {PlatformGraphics}; surface: {SurfaceDescription}";
    }
}
