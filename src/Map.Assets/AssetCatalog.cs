using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;

namespace Map.Assets;

/// <summary>
/// Local metadata-first catalog. Opening/searching reads JSON only; originals are decoded solely
/// when the user requests a thumbnail or the renderer asks for one image.
/// </summary>
public sealed class AssetCatalog
{
    public const int MaxImageDimension = 32_768;
    public const long MaxEncodedBytes = 512L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _root;
    private readonly string _indexPath;
    private List<AssetRecord> _records;

    public AssetCatalog(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _indexPath = Path.Combine(_root, "catalog.json");
        _records = LoadIndex(_indexPath);
    }

    public IReadOnlyList<AssetRecord> Records => _records;

    public AssetRecord Import(string sourcePath, AssetImportOptions options, DateTimeOffset importedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(options);
        var fullSource = Path.GetFullPath(sourcePath);
        var file = new FileInfo(fullSource);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Asset image was not found.", fullSource);
        }

        if (file.Length is <= 0 or > MaxEncodedBytes)
        {
            throw new InvalidDataException($"Asset size {file.Length} is outside the supported range.");
        }

        byte[] bytes = File.ReadAllBytes(fullSource);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var existing = _records.FirstOrDefault(record => record.Sha256 == sha256);
        if (existing is not null)
        {
            return existing;
        }

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("The file is not a supported PNG, JPEG or WebP image.");
        var (extension, mediaType) = Format(codec.EncodedFormat);
        if (codec.Info.Width is <= 0 or > MaxImageDimension
            || codec.Info.Height is <= 0 or > MaxImageDimension)
        {
            throw new InvalidDataException(
                $"Image dimensions {codec.Info.Width}x{codec.Info.Height} are outside the supported range.");
        }

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "originals"));
        Directory.CreateDirectory(Path.Combine(_root, "thumbnails"));

        string? storedRelative = null;
        if (!options.Linked)
        {
            storedRelative = $"originals/{sha256}.{extension}";
            var storedPath = ResolveCatalogRelative(storedRelative);
            if (!File.Exists(storedPath))
            {
                File.WriteAllBytes(storedPath, bytes);
            }
        }

        var thumbnailRelative = $"thumbnails/{sha256}.png";
        var thumbnailPath = ResolveCatalogRelative(thumbnailRelative);
        if (!File.Exists(thumbnailPath))
        {
            File.WriteAllBytes(thumbnailPath, BuildThumbnail(bytes));
        }

        var record = new AssetRecord
        {
            Sha256 = sha256,
            DisplayName = Path.GetFileNameWithoutExtension(fullSource),
            OriginalFileName = Path.GetFileName(fullSource),
            MediaType = mediaType,
            Extension = extension,
            Width = codec.Info.Width,
            Height = codec.Info.Height,
            HasAlpha = codec.Info.AlphaType != SKAlphaType.Opaque,
            ByteLength = bytes.LongLength,
            ImportedUtc = importedUtc,
            Pack = options.Pack,
            Tags = [.. options.Tags.Distinct(StringComparer.OrdinalIgnoreCase)],
            Favorite = false,
            Provenance = options.Provenance,
            StoredRelativePath = storedRelative,
            LinkedPath = options.Linked ? fullSource : null,
            ThumbnailRelativePath = thumbnailRelative,
        };

        _records.Add(record);
        SaveIndex();
        return record;
    }

    public IReadOnlyList<AssetRecord> Search(
        string? query = null, string? pack = null, string? tag = null, bool favoritesOnly = false)
    {
        IEnumerable<AssetRecord> result = _records;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var text = query.Trim();
            result = result.Where(record =>
                record.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || record.OriginalFileName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || record.Tags.Any(value => value.Contains(text, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(pack))
        {
            result = result.Where(record => string.Equals(record.Pack, pack, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            result = result.Where(record => record.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        }

        if (favoritesOnly)
        {
            result = result.Where(record => record.Favorite);
        }

        return [.. result.OrderByDescending(record => record.ImportedUtc).ThenBy(record => record.DisplayName)];
    }

    public AssetRecord? Find(string sha256) =>
        _records.FirstOrDefault(record => string.Equals(record.Sha256, sha256, StringComparison.OrdinalIgnoreCase));

    public void SetFavorite(string sha256, bool favorite)
    {
        var index = _records.FindIndex(record => record.Sha256 == sha256);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Asset {sha256} is not in the catalog.");
        }

        _records[index] = _records[index] with { Favorite = favorite };
        SaveIndex();
    }

    public AssetResolution Resolve(AssetRecord record)
    {
        var path = record.StoredRelativePath is { } stored
            ? ResolveCatalogRelative(stored)
            : record.LinkedPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new AssetResolution(AssetResolutionStatus.Missing, path);
        }

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        return actual == record.Sha256
            ? new AssetResolution(AssetResolutionStatus.Available, path)
            : new AssetResolution(AssetResolutionStatus.HashMismatch, path);
    }

    public string ThumbnailPath(AssetRecord record) => ResolveCatalogRelative(record.ThumbnailRelativePath);

    public void ReplaceIndex(IReadOnlyList<AssetRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = [.. records.GroupBy(record => record.Sha256).Select(group => group.First())];
        SaveIndex();
    }

    private void SaveIndex()
    {
        Directory.CreateDirectory(_root);
        var temp = _indexPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_records, JsonOptions));
        if (File.Exists(_indexPath))
        {
            File.Replace(temp, _indexPath, _indexPath + ".bak", true);
        }
        else
        {
            File.Move(temp, _indexPath);
        }
    }

    private static List<AssetRecord> LoadIndex(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<AssetRecord>>(File.ReadAllBytes(path), JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The local asset catalog index is damaged.", ex);
        }
    }

    private string ResolveCatalogRelative(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Catalog entry points outside the catalog root.");
        }

        return path;
    }

    private static (string Extension, string MediaType) Format(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Png => ("png", "image/png"),
        SKEncodedImageFormat.Jpeg => ("jpg", "image/jpeg"),
        SKEncodedImageFormat.Webp => ("webp", "image/webp"),
        _ => throw new InvalidDataException($"Image format {format} is not supported."),
    };

    private static byte[] BuildThumbnail(byte[] encoded)
    {
        using var bitmap = SKBitmap.Decode(encoded)
            ?? throw new InvalidDataException("Image pixels cannot be decoded.");
        var info = new SKImageInfo(128, 128, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("Cannot create thumbnail surface.");
        surface.Canvas.Clear(SKColors.Transparent);
        var scale = Math.Min(128.0 / bitmap.Width, 128.0 / bitmap.Height);
        var width = (float)(bitmap.Width * scale);
        var height = (float)(bitmap.Height * scale);
        var rect = new SKRect((128 - width) / 2, (128 - height) / 2, (128 + width) / 2, (128 + height) / 2);
        using var source = SKImage.FromBitmap(bitmap);
        surface.Canvas.DrawImage(
            source, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
