using System.Diagnostics;
using System.Security.Cryptography;
using Map.Assets;
using SkiaSharp;

namespace Map.Assets.Tests;

public sealed class AssetCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-assets-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp")]
    public void Import_reads_metadata_hash_provenance_and_builds_thumbnail(
        SKEncodedImageFormat format, string mediaType)
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source." + Extension(format));
        File.WriteAllBytes(source, Image(format, new SKColor(20, 80, 140, 180)));
        var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var provenance = new AssetProvenance
        {
            Source = "user-imported",
            Author = "owner",
            LicenseStatus = AssetLicenseStatus.UserOwned,
            CommercialUseAllowed = true,
        };

        var record = catalog.Import(
            source,
            new AssetImportOptions { Pack = "Test", Tags = ["tree", "green"], Provenance = provenance },
            DateTimeOffset.UnixEpoch);

        Assert.Equal(64, record.Sha256.Length);
        Assert.Equal(96, record.Width);
        Assert.Equal(64, record.Height);
        Assert.Equal(mediaType, record.MediaType);
        Assert.Equal(provenance, record.Provenance);
        Assert.True(File.Exists(catalog.ThumbnailPath(record)));
        using var thumbnail = SKBitmap.Decode(catalog.ThumbnailPath(record));
        Assert.Equal(128, thumbnail!.Width);
        Assert.Equal(128, thumbnail.Height);
    }

    [Fact]
    public void Content_hash_deduplicates_same_bytes_but_same_name_with_other_bytes_stays_distinct()
    {
        Directory.CreateDirectory(_root);
        var first = Path.Combine(_root, "same.png");
        var copy = Path.Combine(_root, "copy.png");
        var bytes = Image(SKEncodedImageFormat.Png, SKColors.Red);
        File.WriteAllBytes(first, bytes);
        File.WriteAllBytes(copy, bytes);
        var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));

        var a = catalog.Import(first, new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        var b = catalog.Import(copy, new AssetImportOptions(), DateTimeOffset.UnixEpoch.AddSeconds(1));
        File.WriteAllBytes(first, Image(SKEncodedImageFormat.Png, SKColors.Blue));
        var c = catalog.Import(first, new AssetImportOptions(), DateTimeOffset.UnixEpoch.AddSeconds(2));

        Assert.Equal(a.Sha256, b.Sha256);
        Assert.NotEqual(a.Sha256, c.Sha256);
        Assert.Equal(2, catalog.Records.Count);
    }

    [Fact]
    public void Linked_asset_reports_missing_and_hash_mismatch_without_substitution()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "linked.png");
        File.WriteAllBytes(source, Image(SKEncodedImageFormat.Png, SKColors.Green));
        var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = catalog.Import(
            source, new AssetImportOptions { Linked = true }, DateTimeOffset.UnixEpoch);

        Assert.Equal(AssetResolutionStatus.Available, catalog.Resolve(record).Status);
        File.WriteAllBytes(source, Image(SKEncodedImageFormat.Png, SKColors.Yellow));
        Assert.Equal(AssetResolutionStatus.HashMismatch, catalog.Resolve(record).Status);
        File.Delete(source);
        Assert.Equal(AssetResolutionStatus.Missing, catalog.Resolve(record).Status);
    }

    [Fact]
    public void Verified_payload_is_the_exact_bounded_content_whose_hash_was_accepted()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "stable-read.png");
        var original = Image(SKEncodedImageFormat.Png, SKColors.Green);
        File.WriteAllBytes(source, original);
        var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = catalog.Import(
            source, new AssetImportOptions { Linked = true }, DateTimeOffset.UnixEpoch);

        var resolution = catalog.ReadVerified(record);
        File.WriteAllBytes(source, Image(SKEncodedImageFormat.Png, SKColors.Yellow));

        Assert.Equal(AssetResolutionStatus.Available, resolution.Status);
        Assert.Equal(record.Sha256, Convert.ToHexStringLower(SHA256.HashData(resolution.Content!)));
        Assert.Equal(original, resolution.Content);
        Assert.Equal(AssetResolutionStatus.HashMismatch, catalog.ReadVerified(record).Status);
    }

    [Fact]
    public void Ten_thousand_metadata_records_load_and_search_without_decoding_originals()
    {
        var catalogRoot = Path.Combine(_root, "catalog");
        var catalog = new AssetCatalog(catalogRoot);
        var records = Enumerable.Range(0, 10_000).Select(index => new AssetRecord
        {
            Sha256 = index.ToString("x64"),
            DisplayName = $"asset-{index:D5}",
            OriginalFileName = $"asset-{index:D5}.png",
            MediaType = "image/png",
            Extension = "png",
            Width = 128,
            Height = 128,
            HasAlpha = true,
            ByteLength = 1_024,
            ImportedUtc = DateTimeOffset.UnixEpoch.AddSeconds(index),
            Pack = index % 2 == 0 ? "even" : "odd",
            Tags = [index % 10 == 0 ? "forest" : "other"],
            Favorite = index % 100 == 0,
            Provenance = new AssetProvenance(),
            ThumbnailRelativePath = $"thumbnails/{index:x64}.png",
        }).ToList();
        catalog.ReplaceIndex(records);

        var loadTimer = Stopwatch.StartNew();
        var reopened = new AssetCatalog(catalogRoot);
        loadTimer.Stop();
        _ = reopened.Search("asset-09999");
        var samples = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            var timer = Stopwatch.StartNew();
            var result = reopened.Search("forest", pack: "even");
            timer.Stop();
            samples.Add(timer.Elapsed.TotalMilliseconds);
            Assert.Equal(1_000, result.Count);
        }

        samples.Sort();
        var p95 = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
        Assert.True(loadTimer.Elapsed < TimeSpan.FromSeconds(2), $"Load: {loadTimer.Elapsed}");
        Assert.True(p95 <= 100, $"Search P95: {p95:F3} ms");
        Assert.False(Directory.Exists(Path.Combine(catalogRoot, "originals")));
    }

    private static byte[] Image(SKEncodedImageFormat format, SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(96, 64))!;
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var encoded = image.Encode(format, 90);
        return encoded.ToArray();
    }

    private static string Extension(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Png => "png",
        SKEncodedImageFormat.Jpeg => "jpg",
        SKEncodedImageFormat.Webp => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
