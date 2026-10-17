using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// Keeps the finished pixels of whole object layers for the frames of one editor gesture - a
/// stroke on land, material, a mask or a brush layer, or a placement ghost following the
/// pointer. Those frames repeat the same view, and every object layer the gesture does not touch
/// looks the same in each of them; drawing 1 500 stamps one by one cost 5-15 ms of every such
/// frame on a whole-map view (2560×1369 window: 43 FPS while painting land).
/// <para>
/// The image is built in device pixels with the frame's own transform, so drawing it back is a
/// pixel-aligned copy: no resampling, the same pixels as drawing the layer directly. The key is
/// the identity of the layer record (an edit makes a new one), the transform, the device clip and
/// whatever else the caller passes. Only layers whose result does not depend on what lies under
/// them qualify (the caller decides). One entry per layer; recomputable pixels only, dropped
/// between gestures and whenever the working-memory budget asks.
/// </para>
/// </summary>
public sealed class LayerImageCache : IDisposable
{
    private sealed record Entry(object[] Key, SKImage Image, SKRectI Device);

    private readonly object _gate = new();
    private readonly Dictionary<LayerId, Entry> _entries = [];
    private readonly Action _release;
    private IntPtr _context;

    public LayerImageCache()
    {
        _release = Clear;
        Map.Core.Resources.WorkingMemoryBudget.ReleaseDerivedCaches += _release;
    }

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>
    /// Draws the kept image of <paramref name="layer"/> for this frame, building it with
    /// <paramref name="draw"/> (which draws the layer onto the canvas it is given, in world
    /// coordinates) when nothing matching is held. <paramref name="keep"/> is asked after the
    /// build whether the result may be kept (for example, no asset image was missing).
    /// False when the frame cannot be served this way; the caller then draws the layer itself.
    /// </summary>
    public bool TryDraw(SKCanvas canvas, LayerId layer, object[] parts, GRContext? context,
        Action<SKCanvas> draw, Func<bool> keep)
    {
        var device = canvas.DeviceClipBounds;
        if (device.Width <= 0 || device.Height <= 0 || device.Width > 8192 || device.Height > 8192) return false;
        var matrix = canvas.TotalMatrix;
        var handle = context?.Handle ?? IntPtr.Zero;
        object[] key = [.. parts, matrix.ScaleX, matrix.SkewX, matrix.TransX, matrix.SkewY, matrix.ScaleY, matrix.TransY,
            matrix.Persp0, matrix.Persp1, matrix.Persp2, device, handle];

        SKImage? image = null;
        lock (_gate)
        {
            if (_context != handle) { ClearLocked(); _context = handle; }
            if (_entries.TryGetValue(layer, out var found) && found.Key.AsSpan().SequenceEqual(key))
            {
                Hits++;
                image = found.Image;
            }
        }

        if (image is null)
        {
            var info = new SKImageInfo(device.Width, device.Height, SKColorType.RgbaF16, SKAlphaType.Premul);
            using var surface = (context is not null ? SKSurface.Create(context, false, info) : null) ?? SKSurface.Create(info);
            if (surface is null) return false;
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.SetMatrix(SKMatrix.CreateTranslation(-device.Left, -device.Top).PreConcat(matrix));
            draw(surface.Canvas);
            surface.Canvas.Flush();
            image = surface.Snapshot();
            if (image is null) return false;
            lock (_gate)
            {
                Misses++;
                if (_context == handle && keep())
                {
                    if (_entries.Remove(layer, out var replaced)) replaced.Image.Dispose();
                    _entries[layer] = new Entry(key, image, device);
                }
                else
                {
                    Blit(canvas, image, device);
                    image.Dispose();
                    return true;
                }
            }
        }

        Blit(canvas, image, device);
        return true;
    }

    private static void Blit(SKCanvas canvas, SKImage image, SKRectI device)
    {
        canvas.Save();
        canvas.ResetMatrix();
        canvas.DrawImage(image, device.Left, device.Top);
        canvas.Restore();
    }

    public void Clear()
    {
        lock (_gate) ClearLocked();
    }

    private void ClearLocked()
    {
        foreach (var entry in _entries.Values) entry.Image.Dispose();
        _entries.Clear();
    }

    public void Dispose()
    {
        Map.Core.Resources.WorkingMemoryBudget.ReleaseDerivedCaches -= _release;
        Clear();
    }
}
