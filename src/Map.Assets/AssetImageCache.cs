using SkiaSharp;

namespace Map.Assets;

/// <summary>Bounded lazy decode cache shared by catalog thumbnails or full stamp images.</summary>
public sealed class AssetImageCache : IDisposable
{
    private sealed class Entry
    {
        public required SKImage Image { get; init; }
        public long LastUsed { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _capacity;
    private long _clock;

    public AssetImageCache(int capacity = 64)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public int Count => _entries.Count;
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }

    public SKImage? Get(string key, string? path)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            existing.LastUsed = ++_clock;
            Hits++;
            return existing.Image;
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        using var data = SKData.Create(stream);
        var image = SKImage.FromEncodedData(data);
        if (image is null)
        {
            return null;
        }

        Misses++;
        _entries[key] = new Entry { Image = image, LastUsed = ++_clock };
        while (_entries.Count > _capacity)
        {
            var oldest = _entries.MinBy(pair => pair.Value.LastUsed);
            oldest.Value.Image.Dispose();
            _entries.Remove(oldest.Key);
            Evictions++;
        }

        return image;
    }

    public SKImage? GetEncoded(string key, byte[] encoded)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            existing.LastUsed = ++_clock;
            Hits++;
            return existing.Image;
        }

        using var data = SKData.CreateCopy(encoded);
        var image = SKImage.FromEncodedData(data);
        if (image is null)
        {
            return null;
        }

        Misses++;
        _entries[key] = new Entry { Image = image, LastUsed = ++_clock };
        Trim();
        return image;
    }

    public void Remove(string key)
    {
        if (_entries.Remove(key, out var entry))
        {
            entry.Image.Dispose();
        }
    }

    private void Trim()
    {
        while (_entries.Count > _capacity)
        {
            var oldest = _entries.MinBy(pair => pair.Value.LastUsed);
            oldest.Value.Image.Dispose();
            _entries.Remove(oldest.Key);
            Evictions++;
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Image.Dispose();
        }

        _entries.Clear();
    }
}
