namespace Map.Assets.Tests;

/// <summary>
/// Libraries on an external drive are attached, not imported: their cards appear while the drive
/// is connected, edits of those cards are written to the drive's own index, files are read from
/// the drive, and the owner's index never names them. Pictures are the owner's mountain fixtures.
/// </summary>
public sealed class DriveLibraryMountTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "map-drive-library", Guid.NewGuid().ToString("N"));

    private static string Fixture(int index)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Map.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "fixtures", "assets", $"owner-mountain-0{index}.png");
    }

    private static DateTimeOffset Stamp(int minute) => new(2026, 11, 12, 10, minute, 0, TimeSpan.Zero);

    /// <summary>A drive folder with the marker and one pack catalog holding a two-variant set.</summary>
    private (string Library, string Catalog, AssetSet Set) MakeDrive()
    {
        var library = Path.Combine(_temp, "drive", AssetDriveLibraries.FolderName);
        var catalogRoot = Path.Combine(library, "fantasy");
        var drive = new AssetCatalog(catalogRoot);
        var set = drive.ImportSet([Fixture(1), Fixture(2)], "Горы тестовые",
            new AssetImportOptions { Pack = "Тестовое · Fantasy", Tags = ["Тестовое", "горы"] }, Stamp(0));
        drive.FlushMetadataAsync().GetAwaiter().GetResult();

        File.WriteAllText(Path.Combine(library, AssetDriveLibraries.MarkerFileName),
            """{"format":"map-drive-library","version":1,"id":"t7-test","name":"Диск T7","catalogs":["fantasy","missing"]}""");
        return (library, catalogRoot, set);
    }

    [Fact]
    public void Marker_lists_only_existing_catalog_folders()
    {
        var (library, catalogRoot, _) = MakeDrive();
        var found = AssetDriveLibraries.TryRead(library);
        Assert.NotNull(found);
        Assert.Equal("t7-test", found!.Id);
        Assert.Equal([catalogRoot], found.CatalogRoots);

        var discovered = AssetDriveLibraries.Discover(name => name == AssetDriveLibraries.RootsVariable ? library : null, () => []);
        Assert.Single(discovered);
        Assert.Empty(AssetDriveLibraries.Discover(name => name == AssetDriveLibraries.DisableVariable ? "1" : null, () => [Path.Combine(_temp, "drive")]));
    }

    [Fact]
    public async Task Attached_cards_are_found_queried_and_read_from_the_drive_then_vanish_when_detached()
    {
        var (_, catalogRoot, driveSet) = MakeDrive();
        await using var own = new AssetCatalog(Path.Combine(_temp, "own"));
        own.Import(Fixture(3), new AssetImportOptions(), Stamp(1));
        var ownIndex = File.ReadAllText(Path.Combine(own.Root, "catalog.json"));
        var revision = own.ContentRevision;
        var raised = 0;
        own.MountsChanged += (_, _) => raised++;

        own.SetMounts([catalogRoot]);

        Assert.Equal(1, raised);
        Assert.True(own.ContentRevision > revision);
        Assert.Equal(2, own.Sets.Count);
        Assert.Same(own.Mounts[0], own.MountOfSet(driveSet.Id));
        Assert.Null(own.MountOfSet(own.Sets.First(s => s.Id != driveSet.Id).Id));
        var cards = own.Query(new AssetQuery { Text = "тестовые" });
        Assert.Single(cards.Sets);

        var record = own.RecordForVariant(driveSet.CoverVariantId)!;
        Assert.Equal(catalogRoot, own.RootFor(record));
        Assert.Equal(AssetResolutionStatus.Available, own.ReadVerified(record).Status);
        Assert.StartsWith(catalogRoot, own.ThumbnailPath(record));
        Assert.True(own.TryThumbnailPath(record, out var thumb) && File.Exists(thumb));

        // Attaching again is free; the owner's own index is untouched by the attach.
        own.SetMounts([catalogRoot]);
        Assert.Equal(1, raised);
        Assert.Equal(ownIndex, File.ReadAllText(Path.Combine(own.Root, "catalog.json")));

        own.SetMounts([]);
        Assert.Equal(2, raised);
        Assert.Single(own.Sets);
        Assert.Null(own.FindSet(driveSet.Id));
    }

    [Fact]
    public async Task Edits_of_a_drive_card_are_written_to_the_drive_index_and_survive_reattach()
    {
        var (_, catalogRoot, driveSet) = MakeDrive();
        await using var own = new AssetCatalog(Path.Combine(_temp, "own"));
        own.Import(Fixture(3), new AssetImportOptions(), Stamp(1));
        own.SetMounts([catalogRoot]);

        own.SetSetFavorite(driveSet.Id, true);
        own.RenameSet(driveSet.Id, "Горы с диска");
        own.SetTags(driveSet.Id, ["Тестовое", "вершины"]);
        own.TouchRecent(driveSet.Id, driveSet.VariantIds[1], Stamp(5));
        await own.FlushMetadataAsync();
        await own.Mounts[0].FlushMetadataAsync();

        Assert.True(own.FindSet(driveSet.Id)!.Favorite);
        Assert.Equal(driveSet.VariantIds[1], own.CurrentVariantOf(driveSet.Id));
        var ownIndex = File.ReadAllText(Path.Combine(own.Root, "catalog.json"));
        Assert.DoesNotContain(driveSet.Id, ownIndex);

        own.SetMounts([]);
        own.SetMounts([catalogRoot]);
        var back = own.FindSet(driveSet.Id)!;
        Assert.Equal("Горы с диска", back.Name);
        Assert.True(back.Favorite);
        Assert.Contains("вершины", back.Tags);
        Assert.Equal(driveSet.Id, own.Recent[0].SetId);
    }

    [Fact]
    public async Task Importing_bytes_that_also_live_on_the_drive_stores_them_in_the_owners_library()
    {
        var (_, catalogRoot, _) = MakeDrive();
        await using var own = new AssetCatalog(Path.Combine(_temp, "own"));
        own.SetMounts([catalogRoot]);

        var record = own.Import(Fixture(1), new AssetImportOptions(), Stamp(2));

        Assert.Equal(own.Root, own.RootFor(record));
        Assert.True(File.Exists(Path.Combine(own.Root, record.StoredRelativePath!.Replace('/', Path.DirectorySeparatorChar))));
        own.SetMounts([]);
        Assert.NotNull(own.Find(record.Sha256));
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
