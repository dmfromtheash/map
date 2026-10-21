using System.IO.Compression;
using System.Text.Json;
using Map.Core.Documents;
using Map.Platform.Windows;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class LibraryCategoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-r03-library-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void Category_comes_from_manifest_and_filters_home_and_library_without_reopening_documents()
    {
        var folder = Path.Combine(_root, "maps");
        Project(folder, "Standard", null); Project(folder, "Hex", "hex"); Project(folder, "Future", "future");
        var dashboard = Dashboard(folder);
        dashboard.Refresh();
        Assert.Equal(3, dashboard.RecentCards.Count);
        Assert.Equal("Стандартный", dashboard.RecentCards.Single(c => c.Title == "Standard").CategoryLabel);
        dashboard.LibraryCategoryFilter = "hex";
        Assert.Equal("Гексовый", Assert.Single(dashboard.RecentCards).CategoryLabel);
        Assert.Equal("Hex", Assert.Single(dashboard.LatestCards).Title);
        dashboard.LibraryCategoryFilter = "standard";
        Assert.Equal("Standard", Assert.Single(dashboard.RecentCards).Title);
        dashboard.LibraryCategoryFilter = "";
        Assert.Equal(3, dashboard.RecentCards.Count);
    }
    [Fact]
    public void Changing_folder_rescans_and_persists_in_profile_without_moving_maps()
    {
        var first = Path.Combine(_root, "one"); var second = Path.Combine(_root, "two");
        Project(first, "Old", null); Project(second, "New", "hex");
        var settings = new AppSettingsStore(Path.Combine(_root, "profile", "settings.json"));
        var dashboard = Dashboard(first, p => settings.Save(settings.Load() with { MapsRoot = p }));
        dashboard.Refresh(); dashboard.CurrentFolder = "stale";
        Assert.True(dashboard.ChangeLibraryFolder(second));
        Assert.Equal("New", Assert.Single(dashboard.RecentCards).Title);
        Assert.Null(dashboard.CurrentFolder);
        Assert.Equal(second, new AppSettingsStore(settings.Path).Load().MapsRoot);
        Assert.True(File.Exists(Path.Combine(first, "Old.mapproj")));
        Assert.False(dashboard.ChangeLibraryFolder(Path.Combine(_root, "missing")));
        Assert.Equal(second, dashboard.Library!.Root);
        Assert.Equal("New", Assert.Single(dashboard.RecentCards).Title);
    }
    [Fact]
    public void Failed_preference_write_keeps_the_old_library()
    {
        var first = Path.Combine(_root, "one"); var second = Path.Combine(_root, "two");
        Project(first, "Old", null); Project(second, "New", "hex");
        var dashboard = Dashboard(first, _ => throw new IOException("disk")); dashboard.Refresh();
        Assert.False(dashboard.ChangeLibraryFolder(second));
        Assert.Equal(first, dashboard.Library!.Root); Assert.Equal("Old", Assert.Single(dashboard.RecentCards).Title);
    }
    private DashboardViewModel Dashboard(string folder, Action<string>? persist = null) => new(new FakeProjectStore(), new FakeRecentMaps(), new FakeLog(),
        library: new MapLibrary(folder, Path.Combine(_root, "favorites.json")), persistLibraryFolder: persist);
    private static void Project(string folder, string title, string? category)
    {
        Directory.CreateDirectory(folder);
        using var archive = ZipFile.Open(Path.Combine(folder, title + ".mapproj"), ZipArchiveMode.Create);
        using var stream = archive.CreateEntry("manifest.json").Open();
        var manifest = new Dictionary<string, object> { ["title"] = title, ["canvas"] = new { width = 200, height = 100 } };
        if (category is not null) manifest["category"] = category;
        JsonSerializer.Serialize(stream, manifest);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
