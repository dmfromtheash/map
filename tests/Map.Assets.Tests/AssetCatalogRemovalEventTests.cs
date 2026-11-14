namespace Map.Assets.Tests;

/// <summary>
/// DEC-111 p.4: removing content tells listeners (the machine cache drops its preparations), for the owner's library and
/// for a drive library routed through it; tagged content («Тестовое») is listed by tag. Pictures: owner mountain fixtures.
/// </summary>
public sealed class AssetCatalogRemovalEventTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "map-catalog-removal-" + Guid.NewGuid().ToString("N"));

    private static string Fixture(int index)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Map.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "fixtures", "assets", $"owner-mountain-0{index}.png");
    }

    private static readonly DateTimeOffset Stamp = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Removing_own_content_raises_the_event_after_the_record_is_gone()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_temp, "own"));
        var kept = catalog.Import(Fixture(1), new AssetImportOptions(), Stamp);
        var removed = catalog.Import(Fixture(2), new AssetImportOptions(), Stamp);
        var seen = new List<(string Sha, bool StillListed)>();
        catalog.RecordRemoved += sha => seen.Add((sha, catalog.Find(sha) is not null));

        catalog.RemoveRecord(removed.Sha256);

        Assert.Equal([(removed.Sha256, false)], seen);
        Assert.NotNull(catalog.Find(kept.Sha256));
    }

    [Fact]
    public async Task Removing_drive_content_through_the_owner_catalog_raises_the_owner_event()
    {
        var driveRoot = Path.Combine(_temp, "drive", "fantasy");
        var drive = new AssetCatalog(driveRoot);
        var set = drive.ImportSet([Fixture(1), Fixture(2)], "Горы", new AssetImportOptions { Tags = ["Тестовое"] }, Stamp);
        await drive.FlushMetadataAsync();
        await using var own = new AssetCatalog(Path.Combine(_temp, "own"));
        own.SetMounts([driveRoot]);
        var sha = own.FindVariant(set.VariantIds[0])!.Sha256;
        var seen = new List<string>();
        own.RecordRemoved += seen.Add;

        own.RemoveRecord(sha);

        Assert.Equal([sha], seen);
        Assert.Null(own.Find(sha));
    }

    [Fact]
    public async Task Content_with_tag_lists_variants_of_tagged_sets_across_mounted_libraries_only()
    {
        var driveRoot = Path.Combine(_temp, "drive", "fantasy");
        var drive = new AssetCatalog(driveRoot);
        var tagged = drive.ImportSet([Fixture(1), Fixture(2)], "Горы", new AssetImportOptions { Tags = ["Тестовое"] }, Stamp);
        await drive.FlushMetadataAsync();
        await using var own = new AssetCatalog(Path.Combine(_temp, "own"));
        var plain = own.Import(Fixture(3), new AssetImportOptions { Tags = ["горы"] }, Stamp);
        own.SetMounts([driveRoot]);

        var shas = own.ContentWithTag("тестовое");

        var expected = tagged.VariantIds.Select(id => own.FindVariant(id)!.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected.Count, shas.Count);
        Assert.All(shas, sha => Assert.Contains(sha, expected));
        Assert.DoesNotContain(plain.Sha256, shas, StringComparer.OrdinalIgnoreCase);
        own.SetMounts([]);
        Assert.Empty(own.ContentWithTag("Тестовое"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }
}
