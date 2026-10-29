using Map.Rendering;
using SkiaSharp;

namespace Map.Export;

/// <summary>
/// Serializes access to an export's stamp source and owns immutable pixel copies while several
/// workers draw. Eviction in the source must never dispose an image on another worker's canvas.
/// The copies are bounded; a map that needs more falls back to the serial route.
/// </summary>
internal sealed class ExportStampSnapshot(IStampImageSource source, long capacity) : IStampImageSource, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Hash, double Width, double Height), SKImage?> _requests = [];
    private readonly Dictionary<(string Hash, int Width, int Height), SKImage> _images = [];
    private long _bytes;

    public SKImage? TryGetImage(string sha256) => Resolve(sha256, 0, 0);

    public SKImage? TryGetImageForPixelSize(string sha256, double width, double height) => Resolve(sha256, width, height);

    public bool PrepareForPixelSizes(IEnumerable<(string Sha256, double Width, double Height)> stamps, Action ready)
    {
        lock (_gate) return source.PrepareForPixelSizes(stamps, ready);
    }

    private SKImage? Resolve(string hash, double width, double height)
    {
        lock (_gate)
        {
            var request = (hash, width, height);
            if (_requests.TryGetValue(request, out var held)) return held;
            var image = width == 0 ? source.TryGetImage(hash) : source.TryGetImageForPixelSize(hash, width, height);
            if (image is null) { _requests[request] = null; return null; }
            var key = (hash, image.Width, image.Height);
            if (!_images.TryGetValue(key, out var copy))
            {
                // The copy keeps the source's own pixel format and colour space, so it is drawn exactly as the source.
                var info = image.Info.ColorType == SKColorType.Unknown
                    ? new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul, image.ColorSpace)
                    : image.Info;
                var bytes = info.BytesSize64;
                if (bytes > capacity - _bytes) throw new SnapshotCapacityException();
                using var bitmap = new SKBitmap(info);
                if (!image.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
                    throw new InvalidDataException("Не удалось подготовить изображение для параллельной отрисовки.");
                bitmap.SetImmutable();
                copy = SKImage.FromBitmap(bitmap);
                _images.Add(key, copy);
                _bytes += bytes;
            }
            _requests.Add(request, copy);
            return copy;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var image in _images.Values) image.Dispose();
            _images.Clear();
            _requests.Clear();
        }
    }

    internal sealed class SnapshotCapacityException() : Exception("Изображений больше, чем помещается в общий запас.");
}
