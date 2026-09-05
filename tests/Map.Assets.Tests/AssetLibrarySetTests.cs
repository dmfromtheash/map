using SkiaSharp;

namespace Map.Assets.Tests;

/// <summary>
/// Logical sets: a single import is a singleton card, a multi-file import is one ordered card,
/// and detaching a variant is never a delete of the content behind it.
/// </summary>
public sealed class AssetLibrarySetTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "map-library-sets", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Single_import_creates_a_singleton_set_and_a_twelve_file_import_creates_one_ordered_set()
    {
        var catalog = NewCatalog();

        catalog.Import(Source("alone", SKColors.Coral), new AssetImportOptions(), Stamp(0));
        var set = catalog.ImportSet(
            [.. Enumerable.Range(0, 12).Select(index => Source($"v{index:D2}", Shade(index)))],
            "Диагностический набор",
            new AssetImportOptions { Pack = "Диагностика", Tags = ["diagnostic"] },
            Stamp(1));

        Assert.Equal(12, set.VariantIds.Count);
        Assert.Equal(set.VariantIds[0], set.CoverVariantId);
        Assert.Equal("Диагностический набор", set.Name);
        Assert.Equal(2, catalog.Sets.Count);
        Assert.Equal(13, catalog.Records.Count);

        var singleton = catalog.Sets.Single(item => item.VariantIds.Count == 1);
        Assert.Equal("alone", singleton.Name);

        // Order is the import order, not a hash or filename order.
        var labels = set.VariantIds.Select(id => catalog.FindVariant(id)!.Label).ToArray();
        Assert.Equal([.. Enumerable.Range(0, 12).Select(index => $"v{index:D2}")], labels);
    }

    [Fact]
    public void Reimporting_identical_bytes_adds_no_second_content_record()
    {
        var catalog = NewCatalog();
        var first = Source("same", SKColors.SlateBlue);
        var second = Path.Combine(_root, "another-name.png");
        File.Copy(first, second);

        var a = catalog.Import(first, new AssetImportOptions(), Stamp(0));
        var b = catalog.Import(second, new AssetImportOptions(), Stamp(1));

        Assert.Equal(a.Sha256, b.Sha256);
        Assert.Single(catalog.Records);
        Assert.Single(catalog.Sets);
        Assert.Single(Directory.GetFiles(Path.Combine(catalog.Root, "originals")));
    }

    [Fact]
    public void Detaching_a_variant_keeps_the_content_the_original_and_the_variant()
    {
        var catalog = NewCatalog();
        var set = catalog.ImportSet(
            [Source("a", SKColors.Red), Source("b", SKColors.Green), Source("c", SKColors.Blue)],
            "Тройка", new AssetImportOptions(), Stamp(0));
        var detached = set.VariantIds[1];
        var sha = catalog.FindVariant(detached)!.Sha256;
        var original = catalog.Resolve(catalog.Find(sha)!).Path!;

        catalog.RemoveVariantFromSet(set.Id, detached);

        var after = catalog.FindSet(set.Id)!;
        Assert.Equal(2, after.VariantIds.Count);
        Assert.DoesNotContain(detached, after.VariantIds);
        Assert.NotNull(catalog.FindVariant(detached));
        Assert.NotNull(catalog.Find(sha));
        Assert.True(File.Exists(original));
    }

    [Fact]
    public void Detaching_the_cover_promotes_the_next_variant_instead_of_leaving_a_dangling_cover()
    {
        var catalog = NewCatalog();
        var set = catalog.ImportSet(
            [Source("a", SKColors.Red), Source("b", SKColors.Green)], "Пара", new AssetImportOptions(), Stamp(0));

        catalog.RemoveVariantFromSet(set.Id, set.CoverVariantId);

        var after = catalog.FindSet(set.Id)!;
        Assert.Equal(after.VariantIds[0], after.CoverVariantId);
    }

    [Fact]
    public void The_last_variant_of_a_set_cannot_be_detached()
    {
        var catalog = NewCatalog();
        var record = catalog.Import(Source("only", SKColors.Gold), new AssetImportOptions(), Stamp(0));
        var set = catalog.FindSetForContent(record.Sha256)!;

        Assert.Throws<InvalidOperationException>(
            () => catalog.RemoveVariantFromSet(set.Id, set.CoverVariantId));
    }

    [Fact]
    public void Reordering_and_cover_selection_survive_a_catalog_restart()
    {
        var catalog = NewCatalog();
        var set = catalog.ImportSet(
            [Source("a", SKColors.Red), Source("b", SKColors.Green), Source("c", SKColors.Blue)],
            "Тройка", new AssetImportOptions(), Stamp(0));
        var reordered = new[] { set.VariantIds[2], set.VariantIds[0], set.VariantIds[1] };

        catalog.ReorderSetVariants(set.Id, reordered);
        catalog.SetCoverVariant(set.Id, set.VariantIds[1]);
        catalog.RenameSet(set.Id, "Перестроенный набор");

        var reopened = new AssetCatalog(RootPath()).FindSet(set.Id)!;
        Assert.Equal(reordered, reopened.VariantIds);
        Assert.Equal(set.VariantIds[1], reopened.CoverVariantId);
        Assert.Equal("Перестроенный набор", reopened.Name);
    }

    [Fact]
    public async Task Set_favorite_and_recent_survive_a_catalog_restart()
    {
        var catalog = NewCatalog();
        var set = catalog.ImportSet(
            [Source("a", SKColors.Red), Source("b", SKColors.Green)], "Пара", new AssetImportOptions(), Stamp(0));

        catalog.SetSetFavorite(set.Id, true);
        catalog.TouchRecent(set.Id, set.VariantIds[1], Stamp(5));
        await catalog.FlushMetadataAsync();

        var reopened = new AssetCatalog(RootPath());
        Assert.True(reopened.FindSet(set.Id)!.Favorite);
        Assert.Equal(set.VariantIds[1], reopened.CurrentVariantOf(set.Id));
        var recent = Assert.Single(reopened.Recent);
        Assert.Equal(set.Id, recent.SetId);
        Assert.Equal(Stamp(5), recent.UsedUtc);
    }

    [Fact]
    public void Recent_is_bounded_and_keeps_the_newest_selections()
    {
        var catalog = NewCatalog();
        var document = new AssetLibraryDocument();
        for (var index = 0; index < AssetLibraryDocument.MaxRecent + 25; index++)
        {
            var record = SyntheticRecord(index);
            document.Records.Add(record);
            AssetLibraryStore.AddSingleton(document, record);
        }

        catalog.ReplaceLibrary(document);
        for (var index = 0; index < AssetLibraryDocument.MaxRecent + 25; index++)
        {
            var set = catalog.Sets[index];
            catalog.TouchRecent(set.Id, set.CoverVariantId, Stamp(index));
        }

        Assert.Equal(AssetLibraryDocument.MaxRecent, catalog.Recent.Count);
        Assert.Equal(catalog.Sets[^1].Id, catalog.Recent[0].SetId);
        Assert.True(AssetLibraryDocument.MaxRecent >= 100);
    }

    [Fact]
    public void A_placed_stamp_finds_its_containing_set_back_by_content_hash()
    {
        var catalog = NewCatalog();
        var set = catalog.ImportSet(
            [Source("a", SKColors.Red), Source("b", SKColors.Green)], "Пара", new AssetImportOptions(), Stamp(0));
        var sha = catalog.FindVariant(set.VariantIds[1])!.Sha256;

        Assert.Equal(set.Id, catalog.FindSetForContent(sha)!.Id);
        Assert.Null(catalog.FindSetForContent(new string('f', 64)));
    }

    [Fact]
    public void Reimport_overlapping_sets_keeps_independent_membership_and_shared_bytes()
    {
        var catalog = NewCatalog();
        var a = Source("shared-a", SKColors.Red);
        var b = Source("shared-b", SKColors.Green);
        var c = Source("shared-c", SKColors.Blue);
        var singleton = catalog.Import(a, new AssetImportOptions(), Stamp(0));
        var first = catalog.ImportSet([a, b], "Первый", new AssetImportOptions(), Stamp(1));
        var second = catalog.ImportSet([b, c], "Второй", new AssetImportOptions(), Stamp(2));
        var repeated = catalog.ImportSet([a, b], "Первый", new AssetImportOptions(), Stamp(3));
        Assert.Equal(first.Id, repeated.Id);
        var otherLogicalSet = catalog.ImportSet([a, b], "Отдельный набор", new AssetImportOptions(), Stamp(4));
        Assert.NotEqual(first.Id, otherLogicalSet.Id);
        Assert.Empty(first.VariantIds.Intersect(otherLogicalSet.VariantIds));
        Assert.Empty(first.VariantIds.Intersect(second.VariantIds));
        Assert.Equal(3, catalog.Records.Count);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(catalog.Root, "originals")).Length);
        catalog.RemoveVariantFromSet(first.Id, first.VariantIds[0]);
        Assert.NotNull(catalog.Find(singleton.Sha256));
        var reopened = NewCatalog();
        Assert.Equal(4, reopened.Sets.Count);
        Assert.Equal(second.VariantIds, reopened.FindSet(second.Id)!.VariantIds);
    }

    [Fact]
    public async Task Failed_import_and_write_leave_the_last_catalog_usable_without_restart()
    {
        var catalog = NewCatalog();
        var a = Source("existing", SKColors.Red);
        catalog.Import(a, new AssetImportOptions(), Stamp(0));
        var set = Assert.Single(catalog.Sets);
        catalog.SetSetFavorite(set.Id, true);
        catalog.TouchRecent(set.Id, set.CoverVariantId, Stamp(1));
        await catalog.FlushMetadataAsync();
        var index = Path.Combine(catalog.Root, "catalog.json");
        var before = File.ReadAllBytes(index);
        var b = Source("new", SKColors.Blue);
        Assert.ThrowsAny<Exception>(() => catalog.ImportSet([b, Path.Combine(_root, "missing.png")], "Отказ", new AssetImportOptions(), Stamp(2)));
        Assert.Single(catalog.Records);
        Assert.Equal(before, File.ReadAllBytes(index));
        using (var locked = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<Exception>(() => catalog.Import(b, new AssetImportOptions(), Stamp(3)));
        Assert.Single(catalog.Records);
        Assert.True(catalog.FindSet(set.Id)!.Favorite);
        Assert.Single(catalog.Recent);
        Assert.Equal(before, File.ReadAllBytes(index));
        catalog.Import(b, new AssetImportOptions(), Stamp(4));
        Assert.Equal(2, NewCatalog().Records.Count);
        Assert.Equal(2, catalog.Records.Count);
    }

    // ---------------------------------------------------------------- helpers

    private AssetCatalog NewCatalog() => new(RootPath());

    private string RootPath() => Path.Combine(_root, "catalog");

    private static DateTimeOffset Stamp(int seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);

    private static SKColor Shade(int index) => new((byte)(20 + (index * 17)), (byte)(200 - (index * 9)), 128, 255);

    private static AssetRecord SyntheticRecord(int index) => new()
    {
        Sha256 = index.ToString("x64"),
        DisplayName = $"asset-{index:D5}",
        OriginalFileName = $"asset-{index:D5}.png",
        MediaType = "image/png",
        Extension = "png",
        Width = 64,
        Height = 64,
        HasAlpha = true,
        ByteLength = 256,
        ImportedUtc = DateTimeOffset.UnixEpoch.AddSeconds(index),
        Provenance = new AssetProvenance(),
        ThumbnailRelativePath = $"thumbnails/{index:x64}.png",
    };

    private string Source(string name, SKColor color)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name + ".png");
        using var surface = SKSurface.Create(new SKImageInfo(48, 32))!;
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, encoded.ToArray());
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
