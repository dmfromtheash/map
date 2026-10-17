using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// The tile images of the live stroke preview, kept from one frame of a gesture to the next. A
/// pointer move changes the coverage of one to four tiles; the rest of the stroke is exactly what
/// it was, so its images are drawn again instead of being sampled, copied and uploaded once more
/// per frame. An entry is valid while the tile's array and change counter are the ones it was
/// built from and the paint is the same. Entries whose tile the preview no longer has are dropped
/// at the start of a frame, and everything goes when the gesture ends.
/// </summary>
public sealed class StrokePreviewCache : IDisposable
{
    private sealed class Entry
    {
        public required byte[] Source { get; init; }
        public required int Version { get; init; }
        public required BrushSettings? Settings { get; init; }
        public required string Color { get; init; }
        public required double Opacity { get; init; }
        public required bool Eraser { get; init; }
        public required bool IsMask { get; init; }
        public required SKImage Image { get; init; }
    }

    // One stroke is drawn in more than one colour per frame (the land-shape preview draws its
    // coverage once in the fill colour and once in white as the material's mask), so the key
    // carries the paint, and the two images of a tile live side by side.
    private readonly record struct Key(TileKey Tile, string Color, bool Eraser, bool IsMask);
    private readonly Dictionary<Key, Entry> _entries = [];

    public int Count => _entries.Count;
    public long Hits { get; private set; }
    public long Misses { get; private set; }

    /// <summary>Forgets tiles the preview no longer covers; everything when there is no preview.</summary>
    public void Sync(StrokePreview? preview)
    {
        if (_entries.Count == 0) return;
        if (preview is null) { Clear(); return; }
        List<Key>? gone = null;
        foreach (var key in _entries.Keys)
            if (!preview.Coverage.ContainsKey(key.Tile)) (gone ??= []).Add(key);
        if (gone is null) return;
        foreach (var key in gone)
            if (_entries.Remove(key, out var entry)) entry.Image.Dispose();
    }

    /// <summary>The image of this tile, built by <paramref name="build"/> only when the tile or the paint changed.</summary>
    public SKImage Get(StrokePreview preview, TileKey key, byte[] source, int version, Func<SKImage> build)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(build);
        var slot = new Key(key, preview.Color, preview.Eraser, preview.IsMask);
        if (_entries.TryGetValue(slot, out var entry))
        {
            if (ReferenceEquals(entry.Source, source) && entry.Version == version
                && entry.Opacity == preview.Opacity && Equals(entry.Settings, preview.PaintSettings))
            {
                Hits++;
                return entry.Image;
            }
            _entries.Remove(slot);
            entry.Image.Dispose();
        }
        Misses++;
        var image = build();
        _entries[slot] = new Entry
        {
            Source = source, Version = version, Settings = preview.PaintSettings, Color = preview.Color,
            Opacity = preview.Opacity, Eraser = preview.Eraser, IsMask = preview.IsMask, Image = image,
        };
        return image;
    }

    public void Clear()
    {
        foreach (var entry in _entries.Values) entry.Image.Dispose();
        _entries.Clear();
    }

    public void Dispose() => Clear();
}
