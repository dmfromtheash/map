using System.Text.Json;
using SkiaSharp;

namespace Map.Assets.Tests;

/// <summary>
/// Schema v2 migration. The old catalog was a bare JSON array of records; the new document is a
/// versioned object that additionally owns collections, variants and logical sets. Migration must
/// keep every old field, must be atomic, and must never destroy the only surviving old copy.
/// </summary>
public sealed class AssetLibraryMigrationTests : IDisposable
{
    private static readonly JsonSerializerOptions LegacyJson = new() { WriteIndented = true };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "map-library-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Flat_v1_index_migrates_into_singleton_sets_without_losing_any_field()
    {
        var catalogRoot = Path.Combine(_root, "catalog");
        Directory.CreateDirectory(catalogRoot);
        var legacy = new AssetRecord
        {
            Sha256 = new string('a', 64),
            DisplayName = "старый ассет",
            OriginalFileName = "старый ассет.png",
            MediaType = "image/png",
            Extension = "png",
            Width = 512,
            Height = 384,
            HasAlpha = true,
            ByteLength = 4242,
            ImportedUtc = DateTimeOffset.UnixEpoch.AddDays(3),
            Pack = "Мои ассеты",
            Tags = ["лес", "дерево"],
            Favorite = true,
            Provenance = new AssetProvenance
            {
                Source = "user-imported",
                Author = "owner",
                LicenseId = "CC0-1.0",
                LicenseUrl = "https://example.invalid/cc0",
                LicenseStatus = AssetLicenseStatus.UserOwned,
                CommercialUseAllowed = true,
            },
            StoredRelativePath = null,
            LinkedPath = Path.Combine(catalogRoot, "linked.png"),
            ThumbnailRelativePath = "thumbnails/aaa.png",
        };
        File.WriteAllBytes(
            Path.Combine(catalogRoot, "catalog.json"),
            JsonSerializer.SerializeToUtf8Bytes(new[] { legacy }, LegacyJson));

        var catalog = new AssetCatalog(catalogRoot);

        Assert.Equal(AssetLibraryStatus.Migrated, catalog.Status);
        var record = Assert.Single(catalog.Records);

        // Field by field on purpose: record equality would compare the tag list by reference and
        // would therefore pass even if migration silently dropped every tag.
        Assert.Equal(legacy.Sha256, record.Sha256);
        Assert.Equal(legacy.DisplayName, record.DisplayName);
        Assert.Equal(legacy.OriginalFileName, record.OriginalFileName);
        Assert.Equal(legacy.MediaType, record.MediaType);
        Assert.Equal(legacy.Extension, record.Extension);
        Assert.Equal(legacy.Width, record.Width);
        Assert.Equal(legacy.Height, record.Height);
        Assert.Equal(legacy.HasAlpha, record.HasAlpha);
        Assert.Equal(legacy.ByteLength, record.ByteLength);
        Assert.Equal(legacy.ImportedUtc, record.ImportedUtc);
        Assert.Equal(legacy.Pack, record.Pack);
        Assert.Equal(legacy.Tags, record.Tags);
        Assert.Equal(legacy.Favorite, record.Favorite);
        Assert.Equal(legacy.Provenance, record.Provenance);
        Assert.Equal(legacy.StoredRelativePath, record.StoredRelativePath);
        Assert.Equal(legacy.LinkedPath, record.LinkedPath);
        Assert.Equal(legacy.ThumbnailRelativePath, record.ThumbnailRelativePath);

        var set = Assert.Single(catalog.Sets);
        Assert.Equal("старый ассет", set.Name);
        Assert.True(set.Favorite);
        Assert.Equal(["лес", "дерево"], set.Tags);
        var variantId = Assert.Single(set.VariantIds);
        Assert.Equal(set.CoverVariantId, variantId);
        Assert.Equal(legacy.Sha256, catalog.FindVariant(variantId)!.Sha256);
    }

    [Fact]
    public void Migration_keeps_a_readable_backup_and_reopening_is_idempotent()
    {
        var catalogRoot = Path.Combine(_root, "catalog");
        Directory.CreateDirectory(catalogRoot);
        var indexPath = Path.Combine(catalogRoot, "catalog.json");
        var original = JsonSerializer.SerializeToUtf8Bytes(new[] { Record(0) });
        File.WriteAllBytes(indexPath, original);

        var first = new AssetCatalog(catalogRoot);
        var firstSetId = Assert.Single(first.Sets).Id;
        var backup = Directory.GetFiles(catalogRoot, "catalog.v1.*.json").Single();
        Assert.Equal(original, File.ReadAllBytes(backup));

        var second = new AssetCatalog(catalogRoot);
        Assert.Equal(AssetLibraryStatus.Ready, second.Status);
        Assert.Equal(firstSetId, Assert.Single(second.Sets).Id);
        Assert.Single(Directory.GetFiles(catalogRoot, "catalog.v1.*.json"));
    }

    [Fact]
    public void Damaged_index_is_reported_and_never_replaced_by_an_empty_catalog()
    {
        var catalogRoot = Path.Combine(_root, "catalog");
        Directory.CreateDirectory(catalogRoot);
        var indexPath = Path.Combine(catalogRoot, "catalog.json");
        var damaged = "{ \"schemaVersion\": 2, \"records\": [ {\"Sha256\""u8.ToArray();
        File.WriteAllBytes(indexPath, damaged);

        var catalog = new AssetCatalog(catalogRoot);

        Assert.Equal(AssetLibraryStatus.Damaged, catalog.Status);
        Assert.Empty(catalog.Records);
        Assert.Empty(catalog.Sets);
        Assert.Throws<InvalidOperationException>(() => catalog.SetSetFavorite("whatever", true));
        Assert.Equal(damaged, File.ReadAllBytes(indexPath));
    }

    [Fact]
    public void Singleton_set_and_variant_ids_are_derived_from_the_content_hash()
    {
        var catalogRoot = Path.Combine(_root, "catalog");
        var source = Path.Combine(_root, "one.png");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(source, Png(SKColors.Teal));

        var record = new AssetCatalog(catalogRoot)
            .Import(source, new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        var reopened = new AssetCatalog(catalogRoot);
        var set = Assert.Single(reopened.Sets);

        Assert.Equal(AssetIdentity.SetIdForContent(record.Sha256), set.Id);
        Assert.Equal(AssetIdentity.VariantIdForContent(record.Sha256), set.CoverVariantId);
    }

    private static AssetRecord Record(int index) => new()
    {
        Sha256 = index.ToString("x64"),
        DisplayName = $"asset-{index}",
        OriginalFileName = $"asset-{index}.png",
        MediaType = "image/png",
        Extension = "png",
        Width = 64,
        Height = 64,
        HasAlpha = true,
        ByteLength = 128,
        ImportedUtc = DateTimeOffset.UnixEpoch.AddSeconds(index),
        Provenance = new AssetProvenance(),
        ThumbnailRelativePath = $"thumbnails/{index:x64}.png",
    };

    private static byte[] Png(SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 24))!;
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
