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
/// <summary>How a frame's view relates to the previous frame's.</summary>
public enum FrameMotion { Still, Panned, Changed }

public sealed class LayerImageCache : IDisposable
{
    private sealed record Entry(object[] Key, SKImage Image, SKRectI Device, SKSurface Surface) : IDisposable
    {
        public void Dispose() { Image.Dispose(); Surface.Dispose(); }
    }

    // A build owns both native resources until the cache adopts them. Failed draws, failed
    // snapshots and keep callbacks must release the retained surface as well as its image.
    private sealed class RasterWork(SKSurface surface) : IDisposable
    {
        private SKSurface? _surface = surface;
        private SKImage? _image;
        public SKImage? Snapshot() => _image = _surface!.Snapshot();
        public void Transfer() { _surface = null; _image = null; }
        public void Dispose() { _image?.Dispose(); _surface?.Dispose(); }
    }

    private readonly object _gate = new();
    private readonly Dictionary<(LayerId Layer, int Slot), Entry> _entries = [];
    private readonly Action _release;
    private IntPtr _context;

    public LayerImageCache()
    {
        _release = Clear;
        Map.Core.Resources.WorkingMemoryBudget.ReleaseDerivedCaches += _release;
    }

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    /// <summary>Updates that drew only the added objects into the previous layer surface.</summary>
    public long Appended { get; private set; }
    /// <summary>Local edit rasters and the device pixels actually cleared and redrawn.</summary>
    public long Patched { get; private set; }
    public long PatchedPixels { get; private set; }
    public long FullRasterPixels { get; private set; }
    public int Count { get { lock (_gate) return _entries.Count; } }

    internal ObjectLayer? RevisionFor(LayerId layer, bool pan = false)
    {
        lock (_gate)
            return pan ? _pan.GetValueOrDefault(layer)?.Key[0] as ObjectLayer
                : _entries.GetValueOrDefault((layer, 0))?.Key[0] as ObjectLayer;
    }

    /// <summary>
    /// Draws the kept image of <paramref name="layer"/> for this frame, building it with
    /// <paramref name="draw"/> (which draws the layer onto the canvas it is given, in world
    /// coordinates) when nothing matching is held. <paramref name="keep"/> is asked after the
    /// build whether the result may be kept (for example, no asset image was missing).
    /// False when the frame cannot be served this way; the caller then draws the layer itself.
    /// <para>
    /// When the held image was built from <paramref name="previous"/> in place of the first part
    /// (everything else equal), <paramref name="append"/> draws only what the new revision adds
    /// into its surface: a placed stamp costs that stamp, not the whole layer again.
    /// </para>
    /// <para>
    /// <paramref name="slot"/> keeps a second image per layer: slot 1 holds only the objects of a
    /// layer whose mask is being edited, so the mask stroke recomposites them instead of redrawing them.
    /// </para>
    /// <para>With <paramref name="damageAreas"/> (or one <paramref name="damage"/> rectangle),
    /// <paramref name="redraw"/> repairs only disjoint dirty areas in world coordinates. The
    /// caller must include every old and new painted bound and guarantee unchanged pixels
    /// elsewhere. <paramref name="exclude"/> hides device pixels only in the final copy,
    /// without modifying the cached raster; live object previews draw into those holes.</para>
    /// </summary>
    public bool TryDraw(SKCanvas canvas, LayerId layer, object[] parts, GRContext? context,
        Action<SKCanvas> draw, Func<bool> keep, object? previous = null, Action<SKCanvas>? append = null, int slot = 0,
        SKRect? damage = null, Action<SKCanvas, SKRect>? redraw = null, SKRegion? exclude = null,
        IReadOnlyList<SKRect>? damageAreas = null)
    {
        var areas = damageAreas ?? (damage is { } single ? [single] : null);
        var entryKey = (layer, slot);
        var device = canvas.DeviceClipBounds;
        if (device.Width <= 0 || device.Height <= 0 || device.Width > 8192 || device.Height > 8192) return false;
        var matrix = canvas.TotalMatrix;
        var handle = context?.Handle ?? IntPtr.Zero;
        object[] key = [.. parts, matrix.ScaleX, matrix.SkewX, matrix.TransX, matrix.SkewY, matrix.ScaleY, matrix.TransY,
            matrix.Persp0, matrix.Persp1, matrix.Persp2, device, handle];

        SKImage? image = null;
        Entry? basis = null;
        lock (_gate)
        {
            if (_context != handle) { ClearLocked(); _context = handle; }
            if (_entries.TryGetValue(entryKey, out var found))
            {
                if (found.Key.AsSpan().SequenceEqual(key))
                {
                    Hits++;
                    image = found.Image;
                }
                else if (previous is not null && (append is not null || areas is not null && redraw is not null) && Equals(found.Key[0], previous)
                    && found.Key.AsSpan(1).SequenceEqual(key.AsSpan(1)))
                {
                    basis = found;
                    _entries.Remove(entryKey); // transfer ownership before a pressure clear can dispose it
                }
            }
        }

        if (image is null)
        {
            var info = new SKImageInfo(device.Width, device.Height, SKColorType.RgbaF16, SKAlphaType.Premul);
            var surface = basis?.Surface ?? (context is not null ? SKSurface.Create(context, false, info) : null) ?? SKSurface.Create(info);
            if (surface is null) return false;
            using var work = new RasterWork(surface);
            if (basis is not null)
            {
                // Release the snapshot before writing its surface: Skia can then update just the
                // dirty pixels without a copy-on-write copy of the entire viewport.
                basis.Image.Dispose();
            }
            else { surface.Canvas.Clear(SKColors.Transparent); FullRasterPixels += (long)device.Width * device.Height; }
            surface.Canvas.SetMatrix(SKMatrix.CreateTranslation(-device.Left, -device.Top).PreConcat(matrix));
            if (basis is not null && areas is not null && redraw is not null)
                Redraw(surface.Canvas, matrix, device, areas, redraw);
            else if (basis is not null) { append!(surface.Canvas); Appended++; }
            else draw(surface.Canvas);
            surface.Canvas.Flush();
            image = work.Snapshot();
            if (image is null) return false;
            lock (_gate)
            {
                Misses++;
                var complete = keep();
                if (_context == handle && (complete || basis is not null && areas is not null && redraw is not null))
                {
                    if (_entries.Remove(entryKey, out var replaced)) replaced.Dispose();
                    // An incomplete repair keeps its old revision key. The next refinement
                    // clears and redraws the same patch; unchanged pixels stay resident.
                    _entries[entryKey] = new Entry(complete ? key : basis!.Key, image, device, surface);
                    work.Transfer();
                }
                else
                {
                    Blit(canvas, image, device, exclude);
                    return true;
                }
            }
        }

        Blit(canvas, image, device, exclude);
        return true;
    }

    private static void Blit(SKCanvas canvas, SKImage image, SKRectI device, SKRegion? exclude = null)
    {
        canvas.Save();
        canvas.ResetMatrix();
        if (exclude is not null) canvas.ClipRegion(exclude, SKClipOperation.Difference);
        canvas.DrawImage(image, device.Left, device.Top);
        canvas.Restore();
    }

    private void Redraw(SKCanvas target, SKMatrix matrix, SKRectI covered, IReadOnlyList<SKRect> damage, Action<SKCanvas, SKRect> draw)
    {
        // Integer clip edges, including the antialias/filter fringe, leave the old pixels
        // outside the patch untouched. The draw uses the original matrix, never a resample.
        Patched++;
        using var region = new SKRegion();
        foreach (var area in damage)
        {
            var bounds = matrix.MapRect(area);
            var patch = new SKRectI(Math.Max(covered.Left, (int)Math.Floor(bounds.Left) - 2),
                Math.Max(covered.Top, (int)Math.Floor(bounds.Top) - 2),
                Math.Min(covered.Right, (int)Math.Ceiling(bounds.Right) + 2),
                Math.Min(covered.Bottom, (int)Math.Ceiling(bounds.Bottom) + 2));
            if (patch.Width > 0 && patch.Height > 0) region.Op(patch, SKRegionOperation.Union);
        }
        matrix.TryInvert(out var inverse);
        using var rectangles = region.CreateRectIterator();
        while (rectangles.Next(out var patch))
        {
            PatchedPixels += (long)patch.Width * patch.Height;
            target.Save();
            target.ResetMatrix();
            target.ClipRect(new SKRect(patch.Left - covered.Left, patch.Top - covered.Top,
                patch.Right - covered.Left, patch.Bottom - covered.Top));
            target.Clear(SKColors.Transparent);
            target.SetMatrix(SKMatrix.CreateTranslation(-covered.Left, -covered.Top).PreConcat(matrix));
            draw(target, inverse.MapRect(patch));
            target.Restore();
        }
    }

    private SKMatrix _lastMatrix;
    private SKRectI _lastDevice;

    /// <summary>
    /// True when this frame repeats the transform and device clip of the previous one: the view
    /// stands still. A slider, a change of level, a mask stroke on another layer and the frames
    /// that bring in the refined terrain all redraw an unchanged view, and with the owner's 1 500
    /// mountains every such frame redrew every stamp (5-20 ms CPU plus the GPU work; 20-30 FPS on
    /// a cliff slider). A moving view clears the cache instead, so it holds memory only while it serves.
    /// </summary>
    public bool Steady(SKCanvas canvas) => Motion(canvas) == FrameMotion.Still;

    /// <summary>What the last <see cref="Motion"/> call found: the current frame's camera motion.</summary>
    public FrameMotion LastMotion { get; private set; } = FrameMotion.Changed;

    /// <summary>How this frame's transform differs from the previous frame's (see <see cref="Steady"/>).</summary>
    public FrameMotion Motion(SKCanvas canvas)
    {
        var matrix = canvas.TotalMatrix; var device = canvas.DeviceClipBounds;
        lock (_gate)
        {
            // The first frame has nothing to differ from: it is treated as a still view.
            var motion = _lastDevice.IsEmpty || matrix == _lastMatrix && device == _lastDevice ? FrameMotion.Still
                // The device clip is the map's page: it moves with the camera, so only the scale decides.
                : SameLinearPart(matrix, _lastMatrix) ? FrameMotion.Panned
                : FrameMotion.Changed;
            _lastMatrix = matrix; _lastDevice = device;
            LastMotion = motion;
            return motion;
        }
    }

    private static bool SameLinearPart(SKMatrix a, SKMatrix b) =>
        a.ScaleX == b.ScaleX && a.SkewX == b.SkewX && a.SkewY == b.SkewY && a.ScaleY == b.ScaleY
        && a.Persp0 == 0 && a.Persp1 == 0 && b.Persp0 == 0 && b.Persp1 == 0 && a.Persp2 == b.Persp2;

    /// <summary>Pan images not rebuilt because the camera was zooming (diagnostics and tests).</summary>
    public long ZoomRebuildsSkipped { get; private set; }

    private readonly Dictionary<LayerId, float> _lastPanScale = [];

    /// <summary>
    /// The scale changed since this layer's previous pan frame: the camera is zooming. The first frame at a scale
    /// that then holds (a pan after the wheel, or the stop) builds the image as before.
    /// </summary>
    private bool ZoomedAwayFrom(LayerId layer, SKMatrix matrix)
    {
        lock (_gate)
        {
            var zooming = _lastPanScale.TryGetValue(layer, out var last) && Math.Abs(last - matrix.ScaleX) > 1e-6f * Math.Abs(matrix.ScaleX);
            _lastPanScale[layer] = matrix.ScaleX;
            return zooming;
        }
    }

    /// <summary>The kept pan image of a layer: device pixels of a wider area than the view, at one scale.</summary>
    private sealed record PanEntry(object[] Key, SKImage Image, SKMatrix Matrix, SKRectI Covered, SKSurface Surface) : IDisposable
    {
        public void Dispose() { Image.Dispose(); Surface.Dispose(); }
    }

    private readonly Dictionary<LayerId, PanEntry> _pan = [];

    /// <summary>
    /// Draws a layer for a frame of a pan: the camera moved but kept its scale. The layer is drawn
    /// once over the view widened by <paramref name="margin"/> device pixels on every side
    /// (<paramref name="draw"/> receives that wider world area), and the frames of the pan only
    /// shift that image while the view stays inside it - the owner's 1 500 mountains cost one
    /// image copy per frame instead of 1 500 stamps (46-54 FPS while panning a whole-map view).
    /// The image is resampled at sub-pixel offsets; the frame after the camera stops is drawn exactly.
    /// </summary>
    /// <para>
    /// With <paramref name="reuseOnly"/> nothing is built: a held image serves only at a whole
    /// device-pixel offset, where it gives the same pixels as drawing the layer. A frame that
    /// repeats a pan frame's view (the camera paused for one frame) then keeps the pan image
    /// instead of building an exact one and dropping it, which made every other pan frame redraw.
    /// </para>
    public bool TryDrawPanned(SKCanvas canvas, LayerId layer, object[] parts, GRContext? context, int margin,
        Action<SKCanvas, SKRect> draw, Func<bool> keep, bool reuseOnly = false, bool allowScale = false,
        object? previous = null, SKRect? damage = null, SKRegion? exclude = null, IReadOnlyList<SKRect>? damageAreas = null)
    {
        var areas = damageAreas ?? (damage is { } single ? [single] : null);
        var device = canvas.DeviceClipBounds;
        var matrix = canvas.TotalMatrix;
        if (device.Width <= 0 || device.Height <= 0 || device.Width + 2 * margin > 8192 || device.Height + 2 * margin > 8192) return false;
        if (!matrix.TryInvert(out var inverse)) return false;
        var zooming = ZoomedAwayFrom(layer, matrix);
        var handle = context?.Handle ?? IntPtr.Zero;
        object[] key = [.. parts, handle];
        PanEntry? entry = null;
        var patchEntry = false;
        // From the kept image's device space to this frame's: a shift, and during a zoom a scale.
        var relative = SKMatrix.Identity;
        lock (_gate)
        {
            if (_context != handle) { ClearLocked(); _context = handle; }
            if (_pan.TryGetValue(layer, out var found)
                && (found.Key.AsSpan().SequenceEqual(key)
                    || previous is not null && areas is not null && Equals(found.Key[0], previous)
                        && found.Key.AsSpan(1).SequenceEqual(key.AsSpan(1)))
                && found.Matrix.TryInvert(out var back))
            {
                relative = matrix.PreConcat(back);
                var ratio = relative.ScaleX;
                var linear = Math.Abs(relative.SkewX) < 1e-6 && Math.Abs(relative.SkewY) < 1e-6
                    && Math.Abs(relative.ScaleY - ratio) < 1e-4 * Math.Abs(ratio);
                // A zoomed-in stand-in is held to 1.6×, a zoomed-out one to 0.8× of its own scale.
                var scaleOk = Math.Abs(ratio - 1) < 1e-5 || allowScale && ratio is >= .8f and <= 1.6f;
                if (linear && scaleOk && relative.TryInvert(out var toKept))
                {
                    var view = toKept.MapRect(new SKRect(device.Left, device.Top, device.Right, device.Bottom));
                    if (view.Left >= found.Covered.Left - .01f && view.Top >= found.Covered.Top - .01f
                        && view.Right <= found.Covered.Right + .01f && view.Bottom <= found.Covered.Bottom + .01f)
                    {
                        entry = found; Hits++;
                        patchEntry = !found.Key.AsSpan().SequenceEqual(key);
                    }
                }
            }
        }
        // A whole device-pixel shift (inverting and composing the two transforms leaves a few
        // millionths of a pixel) reproduces the layer's own pixels; it is snapped to the pixel.
        var exact = Math.Abs(relative.ScaleX - 1) < 1e-5 && Math.Abs(relative.TransX - MathF.Round(relative.TransX)) < 1e-3f
            && Math.Abs(relative.TransY - MathF.Round(relative.TransY)) < 1e-3f;
        if (exact) relative = SKMatrix.CreateTranslation(MathF.Round(relative.TransX), MathF.Round(relative.TransY));
        if (reuseOnly && (entry is null || !exact)) return false;
        // A pan raster made at another origin can differ by one channel unit from a fresh
        // raster at a fractional scale (Skia's image sampling rounds in device space). Repair
        // only at the original matrix; otherwise retain the established exact-rebuild path.
        if (entry is not null && (patchEntry || exclude is not null) && entry.Matrix != matrix) return false;
        // A scaled pan image is a temporary picture. Only patch at its own scale; the settled
        // frame must still rebuild at the exact scale after a wheel gesture.
        if (patchEntry && Math.Abs(relative.ScaleX - 1) >= 1e-5) return false;
        if (patchEntry && entry is not null)
        {
            lock (_gate)
            {
                if (!_pan.TryGetValue(layer, out var held) || !ReferenceEquals(held, entry)) return false;
                _pan.Remove(layer);
            }
            entry.Image.Dispose();
            using var work = new RasterWork(entry.Surface);
            Redraw(entry.Surface.Canvas, entry.Matrix, entry.Covered, areas!, draw);
            entry.Surface.Canvas.Flush();
            var patched = work.Snapshot();
            if (patched is null) return false;
            var complete = keep();
            if (_context != handle)
            {
                canvas.Save();
                if (exclude is not null) canvas.ClipRegion(exclude, SKClipOperation.Difference);
                canvas.SetMatrix(relative);
                canvas.DrawImage(patched, entry.Covered.Left, entry.Covered.Top); canvas.Restore();
                return true;
            }
            entry = entry with { Key = complete ? key : entry.Key, Image = patched };
            lock (_gate) _pan[layer] = entry;
            work.Transfer();
        }
        // A zoom past the stand-in's limits: building a new image over the view and its margins, then flushing it,
        // cost 7.5 s of draws and 3.6 s of GPU flush in the slow wheel frames at 10 000 objects (PC trace,
        // 2026-11-15). A pan image pays off only for pans; while the scale keeps changing the layer is drawn directly.
        if (entry is null && zooming) { ZoomRebuildsSkipped++; return false; }
        if (entry is null)
        {
            var covered = new SKRectI(device.Left - margin, device.Top - margin, device.Right + margin, device.Bottom + margin);
            var info = new SKImageInfo(covered.Width, covered.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var surface = (context is not null ? SKSurface.Create(context, false, info) : null) ?? SKSurface.Create(info);
            if (surface is null) return false;
            using var work = new RasterWork(surface);
            FullRasterPixels += (long)covered.Width * covered.Height;
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.SetMatrix(SKMatrix.CreateTranslation(-covered.Left, -covered.Top).PreConcat(matrix));
            draw(surface.Canvas, inverse.MapRect(new SKRect(covered.Left, covered.Top, covered.Right, covered.Bottom)));
            surface.Canvas.Flush();
            var image = work.Snapshot();
            if (image is null) return false;
            lock (_gate)
            {
                Misses++;
                if (_context == handle && keep())
                {
                    if (_pan.Remove(layer, out var replaced)) replaced.Dispose();
                    entry = new PanEntry(key, image, matrix, covered, surface);
                    _pan[layer] = entry;
                    work.Transfer();
                }
            }
            if (entry is null)
            {
                Blit(canvas, image, covered, exclude);
                return true;
            }
            relative = SKMatrix.Identity; exact = true;
        }
        canvas.Save();
        if (exclude is not null) canvas.ClipRegion(exclude, SKClipOperation.Difference);
        canvas.SetMatrix(relative);
        canvas.DrawImage(entry.Image, new SKRect(entry.Covered.Left, entry.Covered.Top, entry.Covered.Right, entry.Covered.Bottom),
            new SKSamplingOptions(exact ? SKFilterMode.Nearest : SKFilterMode.Linear, SKMipmapMode.None));
        canvas.Restore();
        return true;
    }

    /// <summary>Drops the kept images of still frames, keeping the pan images (the camera is moving).</summary>
    public void ClearStill()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values) entry.Dispose();
            _entries.Clear();
        }
    }

    /// <summary>Drops the kept pan images (the camera stopped or zoomed).</summary>
    public void ClearPanned()
    {
        lock (_gate)
        {
            foreach (var entry in _pan.Values) entry.Dispose();
            _pan.Clear();
        }
    }

    public void Clear()
    {
        lock (_gate) ClearLocked();
    }

    private void ClearLocked()
    {
        foreach (var entry in _entries.Values) entry.Dispose();
        _entries.Clear();
        foreach (var entry in _pan.Values) entry.Dispose();
        _pan.Clear();
        _lastPanScale.Clear();
    }

    public void Dispose()
    {
        Map.Core.Resources.WorkingMemoryBudget.ReleaseDerivedCaches -= _release;
        Clear();
    }
}
