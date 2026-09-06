using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Map.Export;

namespace Map.App.Controls;

/// <summary>Read-only image camera. One tile decode at a time, with a bounded decoded-pixel cache.</summary>
public sealed class PngMapView : Control, IDisposable
{
    public const long CacheLimitBytes = 128L * 1024 * 1024;
    private readonly record struct Key(int Level, int Column, int Row);
    private sealed record Entry(Bitmap Image, long Bytes) { public long Used { get; set; } }
    private readonly Dictionary<Key, Entry> _cache = [];
    private readonly HashSet<Key> _failed = [];
    private List<Key> _wanted = [];
    private PngTilePyramid? _pyramid;
    private long _clock;
    private bool _loading, _disposed, _fit = true;
    private double _scale = 1, _x, _y;
    private Point? _drag;
    public long CachedPixelBytes { get; private set; }
    public int CachedTiles => _cache.Count;
    public int DisplayLevel { get; private set; }
    public double Zoom => _scale;
    public string Error { get; private set; } = "";
    public Task LoadingTask { get; private set; } = Task.CompletedTask;
    public event Action? ViewChanged;

    public PngMapView()
    {
        Name = "PngMapCanvas";
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public void SetPyramid(PngTilePyramid pyramid)
    {
        Clear(); _pyramid = pyramid; _disposed = false; Error = ""; Fit();
    }

    public void Fit()
    {
        _fit = true;
        if (_pyramid is not { } p || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        _scale = Math.Min(Bounds.Width / p.Width, Bounds.Height / p.Height);
        _x = (Bounds.Width - p.Width * _scale) / 2;
        _y = (Bounds.Height - p.Height * _scale) / 2;
        Refresh();
    }

    public void ZoomAt(Point anchor, double scale)
    {
        if (_pyramid is not { } p) return;
        _fit = false;
        var minimum = Math.Min(Bounds.Width / p.Width, Bounds.Height / p.Height) / 2;
        scale = Math.Clamp(scale, Math.Max(.000001, minimum), 16);
        _x = anchor.X - (anchor.X - _x) * scale / _scale;
        _y = anchor.Y - (anchor.Y - _y) * scale / _scale;
        _scale = scale; Refresh();
    }

    public void Pan(double dx, double dy) { _fit = false; _x += dx; _y += dy; Refresh(); }

    /// <summary>Native pixels at a requested image coordinate; also used by the isolated UI smoke.</summary>
    public void ShowAt(double imageX, double imageY, double scale)
    {
        ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), scale);
        _x = Bounds.Width / 2 - imageX * _scale; _y = Bounds.Height / 2 - imageY * _scale;
        Refresh();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    { base.OnSizeChanged(e); if (_fit) Fit(); else Refresh(); }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    { base.OnPointerWheelChanged(e); ZoomAt(e.GetPosition(this), _scale * Math.Pow(1.25, e.Delta.Y)); e.Handled = true; }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _drag = e.GetPosition(this); e.Pointer.Capture(this); Focus(); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } before) return;
        var point = e.GetPosition(this); _drag = point; Pan(point.X - before.X, point.Y - before.Y); e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    { base.OnPointerReleased(e); _drag = null; e.Pointer.Capture(null); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { base.OnPointerCaptureLost(e); _drag = null; }

    private void Refresh()
    {
        if (_pyramid is not { } p || _disposed || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        DisplayLevel = Math.Clamp((int)Math.Floor(Math.Log2(1 / _scale)), 0, p.Levels - 1);
        // Keep unusually large windows bounded too; normal desktop windows use level zero at 1:1.
        while (DisplayLevel < p.Levels - 1
            && (Math.Ceiling(Bounds.Width / (p.TileEdge * _scale * (1 << DisplayLevel))) + 1)
                * (Math.Ceiling(Bounds.Height / (p.TileEdge * _scale * (1 << DisplayLevel))) + 1) > 28)
            DisplayLevel++;
        var size = p.LevelSize(DisplayLevel);
        var edge = p.TileEdge * _scale * (1 << DisplayLevel);
        var left = Math.Clamp((int)Math.Floor(-_x / edge), 0, (size.Width - 1) / p.TileEdge);
        var top = Math.Clamp((int)Math.Floor(-_y / edge), 0, (size.Height - 1) / p.TileEdge);
        var right = Math.Clamp((int)Math.Floor((Bounds.Width - _x) / edge), 0, (size.Width - 1) / p.TileEdge);
        var bottom = Math.Clamp((int)Math.Floor((Bounds.Height - _y) / edge), 0, (size.Height - 1) / p.TileEdge);
        _wanted = [new Key(p.Levels - 1, 0, 0)]; // A bounded overview stays visible while detailed tiles load.
        for (var row = top; row <= bottom; row++)
        for (var column = left; column <= right; column++)
        {
            var key = new Key(DisplayLevel, column, row);
            if (!_wanted.Contains(key)) _wanted.Add(key);
        }
        foreach (var key in _wanted) if (_cache.TryGetValue(key, out var entry)) entry.Used = ++_clock;
        if (!_loading) LoadingTask = LoadVisibleAsync();
        InvalidateVisual(); ViewChanged?.Invoke();
    }

    private async Task LoadVisibleAsync()
    {
        _loading = true;
        try
        {
            while (!_disposed && _pyramid is { } p)
            {
                var missing = _wanted.Where(k => !_cache.ContainsKey(k) && !_failed.Contains(k)).ToArray();
                if (missing.Length == 0) break;
                var key = missing[0];
                Bitmap bitmap;
                try
                {
                    bitmap = await Task.Run(() => new Bitmap(p.CheckTile(key.Level, key.Column, key.Row)));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException
                    or UnauthorizedAccessException or InvalidOperationException or OutOfMemoryException)
                {
                    if (ReferenceEquals(p, _pyramid)) { _failed.Add(key); Error = "Участок не прочитан: " + ex.Message; ViewChanged?.Invoke(); }
                    continue;
                }
                if (_disposed || !ReferenceEquals(p, _pyramid) || !_wanted.Contains(key)) { bitmap.Dispose(); continue; }
                var bytes = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
                while (_cache.Count > 0 && CachedPixelBytes + bytes > CacheLimitBytes)
                {
                    var oldest = _cache.MinBy(pair => pair.Value.Used);
                    _cache.Remove(oldest.Key); CachedPixelBytes -= oldest.Value.Bytes; oldest.Value.Image.Dispose();
                }
                _cache[key] = new Entry(bitmap, bytes) { Used = ++_clock }; CachedPixelBytes += bytes;
                InvalidateVisual(); ViewChanged?.Invoke();
            }
        }
        finally { _loading = false; }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#101B2C")), new Rect(Bounds.Size));
        if (_pyramid is not { } p) return;
        using var clip = context.PushClip(new Rect(Bounds.Size));
        using var imageClip = context.PushClip(new Rect(_x, _y, p.Width * _scale, p.Height * _scale));
        foreach (var key in _wanted)
        {
            if (key.Level != DisplayLevel) continue;
            var factor = _scale * (1 << key.Level);
            var size = p.LevelSize(key.Level);
            var tileWidth = Math.Min(p.TileEdge, size.Width - key.Column * p.TileEdge);
            var tileHeight = Math.Min(p.TileEdge, size.Height - key.Row * p.TileEdge);
            var destination = new Rect(_x + key.Column * p.TileEdge * factor, _y + key.Row * p.TileEdge * factor,
                tileWidth * factor, tileHeight * factor);
            if (_cache.TryGetValue(key, out var entry))
                context.DrawImage(entry.Image, new Rect(entry.Image.Size), destination);
            else if (_cache.TryGetValue(new Key(p.Levels - 1, 0, 0), out var overview))
            {
                // Draw the overview only in missing tiles. Compositing it underneath detailed
                // translucent pixels would change their opacity and leave a blurred ghost.
                var ratio = (double)(1 << key.Level) / (1 << (p.Levels - 1));
                context.DrawImage(overview.Image, new Rect(key.Column * p.TileEdge * ratio,
                    key.Row * p.TileEdge * ratio, tileWidth * ratio, tileHeight * ratio), destination);
            }
        }
    }

    private void Clear()
    {
        _pyramid = null; _wanted.Clear(); _failed.Clear();
        foreach (var entry in _cache.Values) entry.Image.Dispose();
        _cache.Clear(); CachedPixelBytes = 0;
    }
    public void Dispose() { _disposed = true; Clear(); }
}
