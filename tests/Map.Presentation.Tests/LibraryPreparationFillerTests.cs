using Map.Assets;
using Map.Presentation.Services;

namespace Map.Presentation.Tests;

/// <summary>
/// DEC-110 p.1: an attached drive library gets its shared preparations in the background, so the next machine that
/// attaches it is warm. Until 2026-11-15 nothing in the program wrote them (the folders were empty on every machine).
/// </summary>
public sealed class LibraryPreparationFillerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-library-filler-tests", Guid.NewGuid().ToString("N"));

    private static string Fixture(int number)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        return Path.Combine(root!.FullName, "tests", "fixtures", "assets", $"owner-mountain-{number:00}.png");
    }

    [Fact]
    public async Task An_attached_drive_library_gets_its_shared_preparations_and_the_own_library_is_left_alone()
    {
        var drive = Path.Combine(_root, "drive");
        string[] driveShas;
        await using (var library = new AssetCatalog(drive))
            driveShas = [library.Import(Fixture(1), new(), DateTimeOffset.UnixEpoch).Sha256, library.Import(Fixture(2), new(), DateTimeOffset.UnixEpoch).Sha256];
        var ownRoot = Path.Combine(_root, "own");
        await using var catalog = new AssetCatalog(ownRoot);
        catalog.Import(Fixture(3), new(), DateTimeOffset.UnixEpoch);
        catalog.SetMounts([drive]);

        await using (var filler = new LibraryPreparationFiller(catalog, Path.Combine(_root, "builder"), TimeSpan.Zero, TimeSpan.Zero))
        {
            filler.Start();
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (filler.Built < driveShas.Length && filler.Failed == 0 && DateTime.UtcNow < deadline) await Task.Delay(100);
            Assert.Equal(0, filler.Failed);
            Assert.Equal(driveShas.Length, filler.Built);
        }

        await using var check = new StampDerivedAssetStore(catalog, Path.Combine(_root, "check"));
        Assert.All(driveShas, sha => Assert.True(check.HasCurrentPortable(sha), "No current shared preparation for " + sha));
        Assert.Equal(driveShas.Length, Directory.GetFiles(Path.Combine(drive, "derived"), "*.mapderived", SearchOption.AllDirectories).Length);
        Assert.False(Directory.Exists(Path.Combine(ownRoot, "derived")), "The own library is covered by the machine cache.");
    }

    [Fact]
    public async Task A_library_on_a_nearly_full_drive_is_not_written()
    {
        var drive = Path.Combine(_root, "drive");
        await using (var library = new AssetCatalog(drive)) library.Import(Fixture(1), new(), DateTimeOffset.UnixEpoch);
        await using var catalog = new AssetCatalog(Path.Combine(_root, "own"));
        catalog.SetMounts([drive]);
        await using (var filler = new LibraryPreparationFiller(catalog, Path.Combine(_root, "builder"), TimeSpan.Zero, TimeSpan.Zero,
            freeBytes: _ => LibraryPreparationFiller.MinimumFreeBytes - 1))
        {
            filler.Start();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!filler.Idle && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.True(filler.Idle);
            Assert.Equal(0, filler.Built);
        }
        Assert.False(Directory.Exists(Path.Combine(drive, "derived")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
