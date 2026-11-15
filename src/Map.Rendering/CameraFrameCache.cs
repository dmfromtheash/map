using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// Two completed views for a moving camera: the widest view and the most recent detail.
/// Never builds scene data or reads pixels. The caller renders normally once motion ends.
/// Owned by one render context, like the surface from which these images were captured.
/// </summary>
public sealed class CameraFrameCache : IDisposable
{
    private sealed record Frame(SKImage Image, SKRect World, long Bytes) : IDisposable
    {
        public void Dispose() => Image.Dispose();
    }

    private readonly long _budget;
    private Frame? _wide, _detail;
    private object? _document;
    private object?[] _state = [];
    private nint _context;
    public long Bytes => (_wide?.Bytes ?? 0) + (_detail?.Bytes ?? 0);
    public int Count => (_wide is null ? 0 : 1) + (_detail is null ? 0 : 1);
    public long CapturesRejectedByBudget { get; private set; }

    // Two 4K half-float views fit. Smaller windows retain only their actual image bytes.
    public CameraFrameCache(long budget = 128L * 1024 * 1024) => _budget = budget;

    /// <summary>Document reference, non-document view state and GPU context must all match.</summary>
    public void SetIdentity(object document, nint context, params object?[] state)
    {
        if (!ReferenceEquals(_document, document) || _context != context || !_state.AsSpan().SequenceEqual(state))
            Clear();
        _document = document; _context = context; _state = state;
    }

    /// <summary>Captures only the visible part of the page, with the current world-to-device matrix.</summary>
    public bool Capture(SKSurface surface, SKCanvas canvas, SKRect page)
    {
        var matrix = canvas.TotalMatrix;
        if (matrix.SkewX != 0 || matrix.SkewY != 0 || matrix.ScaleX <= 0 || matrix.ScaleY <= 0
            || !matrix.TryInvert(out var inverse)) return false;
        var device = matrix.MapRect(page);
        var clip = canvas.DeviceClipBounds;
        var pixels = new SKRectI(Math.Max(clip.Left, (int)Math.Floor(device.Left)),
            Math.Max(clip.Top, (int)Math.Floor(device.Top)), Math.Min(clip.Right, (int)Math.Ceiling(device.Right)),
            Math.Min(clip.Bottom, (int)Math.Ceiling(device.Bottom)));
        if (pixels.Width <= 0 || pixels.Height <= 0) return false;
        // Reject before allocation using the largest Avalonia canvas format (half-float).
        // Charge the real snapshot format after capture; no pixel readback is needed.
        if ((long)pixels.Width * pixels.Height * 8 > _budget / 2)
        { CapturesRejectedByBudget++; return false; }
        var image = surface.Snapshot(pixels);
        if (image is null) return false;
        var bytes = (long)image.Width * image.Height * image.Info.BytesPerPixel;
        if (bytes > _budget / 2)
        { image.Dispose(); CapturesRejectedByBudget++; return false; }
        var frame = new Frame(image, inverse.MapRect(new SKRect(pixels.Left, pixels.Top, pixels.Right, pixels.Bottom)), bytes);
        if (_wide is null || Contains(frame.World, _wide.World))
        {
            _wide?.Dispose(); _wide = frame;
        }
        else
        {
            _detail?.Dispose(); _detail = frame;
        }
        return true;
    }

    /// <summary>
    /// How far a kept frame may be stretched before the scene is drawn instead. The wheel zoomed into the widest
    /// kept frame stretched many times: a fast frame counter with a blurred picture, and a sharp detail rectangle
    /// over it on the way out (owner and USER, 2026-11-15). Map engines show a kept tile only near its own scale
    /// and draw the new level otherwise (MapLibre, Chromium cc tilings).
    /// </summary>
    public double MaxMagnification { get; set; } =
        double.TryParse(Environment.GetEnvironmentVariable("MAP_CAMERA_FRAME_MAX_ZOOM"), System.Globalization.CultureInfo.InvariantCulture, out var limit) && limit >= 1 ? limit : 1.25;

    /// <summary>Frames refused because they would be stretched beyond <see cref="MaxMagnification"/>.</summary>
    public long RefusedStretched { get; private set; }

    private bool Sharp(Frame frame, SKCanvas canvas) =>
        canvas.TotalMatrix.ScaleX <= frame.Image.Width / Math.Max(1e-6, frame.World.Width) * MaxMagnification;

    /// <summary>False means nothing was drawn; normal rendering must cover this view.</summary>
    public bool TryDraw(SKCanvas canvas, SKRect visible, SKRect page)
    {
        var target = Intersection(visible, page);
        if (target.IsEmpty) return true;
        var cover = _detail is { } detail && Contains(detail.World, target) ? detail : _wide;
        if (cover is null || !Contains(cover.World, target)) return false;
        if (!Sharp(cover, canvas)) { RefusedStretched++; return false; }
        canvas.Save();
        canvas.ClipRect(target);
        // Clip the broad view around the detail. Even translucent pages are composited once.
        if (_detail is { } top && !ReferenceEquals(top, cover) && Sharp(top, canvas))
        {
            canvas.Save();
            canvas.ClipRect(top.World);
            canvas.DrawImage(top.Image, top.World, new SKSamplingOptions(SKFilterMode.Linear));
            canvas.Restore();
            canvas.ClipRect(top.World, SKClipOperation.Difference);
        }
        canvas.DrawImage(cover.Image, cover.World, new SKSamplingOptions(SKFilterMode.Linear));
        canvas.Restore();
        return true;
    }

    private static bool Contains(SKRect outer, SKRect inner) => outer.Left <= inner.Left && outer.Top <= inner.Top
        && outer.Right >= inner.Right && outer.Bottom >= inner.Bottom;
    private static SKRect Intersection(SKRect a, SKRect b) => new(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top),
        Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));

    public void Clear()
    {
        _wide?.Dispose(); _detail?.Dispose(); _wide = null; _detail = null;
    }
    public void Dispose() { Clear(); _document = null; _state = []; }
}
