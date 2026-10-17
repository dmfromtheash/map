using System.IO.Compression;
using System.Text;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// The library page lists every map in the library folder and its folders - not only the
/// recently opened ones - with folders, exported pictures and favourites, as Inkarnate's
/// Maps / Folders / Exports / Liked.
/// </summary>
public sealed class MapLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-library-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Library => Path.Combine(_root, "Maps");

    private static void WriteProject(string path, string title)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open(), Encoding.UTF8))
            writer.Write($$$"""{"format":"map-project","title":"{{{title}}}","createdUtc":"2026-10-01T10:00:00+00:00","canvas":{"width":2048,"height":1536}}""");
        using var preview = archive.CreateEntry("preview.png").Open();
        preview.Write([137, 80, 78, 71, 13, 10, 26, 10]);
    }

    [Fact]
    public void Every_map_of_the_folder_and_its_folders_is_listed_with_folders_exports_and_favourites()
    {
        WriteProject(Path.Combine(Library, "Долина.mapproj"), "Долина Листвы");
        WriteProject(Path.Combine(Library, "Кампания", "Остров.mapproj"), "Северный остров");
        Directory.CreateDirectory(Path.Combine(Library, MapLibrary.ExportsFolderName));
        File.WriteAllBytes(Path.Combine(Library, MapLibrary.ExportsFolderName, "Долина.png"), [1, 2, 3]);
        var recent = new FakeRecentMaps();
        var outside = Path.Combine(_root, "elsewhere", "Старая.mapproj");
        WriteProject(outside, "Старая карта");
        recent.Items.Add(new RecentMapItem(outside, "Старая карта", DateTimeOffset.UnixEpoch, true));
        var library = new MapLibrary(Library, Path.Combine(_root, "library.json"));
        var dashboard = new DashboardViewModel(new FakeProjectStore(), recent, new FakeLog(), library: library);

        dashboard.Refresh();

        Assert.Equal(3, dashboard.TotalRecentCount);
        var island = Assert.Single(dashboard.RecentCards, card => card.Title == "Северный остров");
        Assert.Equal("Кампания", island.Folder);
        Assert.Equal("2048×1536", island.CanvasSize);
        Assert.NotNull(island.ThumbnailPng);
        Assert.Equal(1, dashboard.FolderCount);
        Assert.Equal(1, dashboard.ExportCount);

        dashboard.CurrentFolder = "Кампания";
        Assert.Equal(["Северный остров"], dashboard.RecentCards.Select(card => card.Title));
        dashboard.CurrentFolder = null;

        dashboard.SetFavorite(island, true);
        Assert.Equal(1, dashboard.FavoriteCount);
        dashboard.Tab = DashboardTab.Favorites;
        Assert.Equal(["Северный остров"], dashboard.RecentCards.Select(card => card.Title));

        // The favourite survives a new start of the program.
        var reopened = new DashboardViewModel(new FakeProjectStore(), recent, new FakeLog(),
            library: new MapLibrary(Library, Path.Combine(_root, "library.json")));
        reopened.Refresh();
        Assert.Equal(1, reopened.FavoriteCount);
    }

    [Fact]
    public void A_map_moves_into_a_new_folder_and_keeps_its_favourite_mark()
    {
        WriteProject(Path.Combine(Library, "Долина.mapproj"), "Долина Листвы");
        var library = new MapLibrary(Library, Path.Combine(_root, "library.json"));
        var dashboard = new DashboardViewModel(new FakeProjectStore(), new FakeRecentMaps(), new FakeLog(), library: library);
        dashboard.Refresh();
        dashboard.SetFavorite(dashboard.RecentCards.Single(), true);

        var folder = dashboard.CreateFolder("Мир");
        Assert.Equal("Мир", folder);
        Assert.True(dashboard.MoveToFolder(dashboard.RecentCards.Single(), folder!));

        var moved = dashboard.RecentCards.Single();
        Assert.Equal("Мир", moved.Folder);
        Assert.True(moved.IsFavorite);
        Assert.True(File.Exists(Path.Combine(Library, "Мир", "Долина.mapproj")));
        Assert.False(File.Exists(Path.Combine(Library, "Долина.mapproj")));
        Assert.Equal("Мир 2", Path.GetFileName(library.CreateFolder("Мир")));
    }
}
