using System.Text;
using System.Text.Json;
using Map.Platform.Windows;

namespace Map.Presentation.Tests;

public sealed class RecentMapsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "map-recent-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Oversized_recent_index_is_refused_before_read_all()
    {
        var path = Index();
        using (var stream = File.Create(path))
        {
            stream.SetLength(RecentMapsStore.MaxEncodedBytes + 1);
        }

        Assert.Empty(new RecentMapsStore(path).Load());
    }

    [Fact]
    public void More_than_capacity_is_refused_before_paths_are_probed()
    {
        var path = Index();
        var entries = Enumerable.Range(0, RecentMapsStore.Capacity + 1).Select(index => new
        {
            path = Path.Combine(_root, $"missing-{index}.mapproj"),
            documentId = Guid.NewGuid().ToString("D"),
            title = $"Map {index}",
            modifiedUtc = DateTimeOffset.UnixEpoch.ToString("O"),
        });
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(entries));

        Assert.Empty(new RecentMapsStore(path).Load());
    }

    [Fact]
    public void Invalid_required_fields_make_the_small_index_empty()
    {
        var path = Index();
        File.WriteAllText(
            path,
            "[{\"path\":\"missing.mapproj\",\"documentId\":\"bad\",\"title\":\"Map\",\"modifiedUtc\":\"bad\"}]",
            Encoding.UTF8);

        Assert.Empty(new RecentMapsStore(path).Load());
    }

    [Theory]
    [InlineData(@"\\server\share\map.mapproj")]
    [InlineData(@"\\.\NUL")]
    [InlineData(@"C:\maps\CON.mapproj")]
    [InlineData(@"C:\maps\victim:stream.mapproj")]
    public void Non_local_or_device_recent_path_is_never_probed_during_startup(string unsafePath)
    {
        var path = Index();
        var entry = new[]
        {
            new
            {
                path = unsafePath,
                documentId = Guid.NewGuid().ToString("D"),
                title = "Map",
                modifiedUtc = DateTimeOffset.UnixEpoch.ToString("O"),
            },
        };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(entry));

        var loaded = Assert.Single(new RecentMapsStore(path).Load());

        Assert.False(loaded.IsAvailable);
    }

    [Fact]
    public void A_recent_path_beneath_a_reparse_point_is_never_probed_as_available()
    {
        var path = Index();
        var target = Path.Combine(_root, "target");
        var link = Path.Combine(_root, "linked");
        Directory.CreateDirectory(target);
        var mapPath = Path.Combine(target, "map.mapproj");
        File.WriteAllBytes(mapPath, [1]);

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // host policy cannot create the fixture; production still rejects real links
        }

        var entry = new[]
        {
            new
            {
                path = Path.Combine(link, "map.mapproj"),
                documentId = Guid.NewGuid().ToString("D"),
                title = "Map",
                modifiedUtc = DateTimeOffset.UnixEpoch.ToString("O"),
            },
        };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(entry));

        Assert.False(Assert.Single(new RecentMapsStore(path).Load()).IsAvailable);
    }

    [Fact]
    public void A_regular_existing_local_recent_path_remains_available()
    {
        var path = Index();
        var mapPath = Path.Combine(_root, "regular.mapproj");
        File.WriteAllBytes(mapPath, [1]);
        var entry = new[]
        {
            new
            {
                path = mapPath,
                documentId = Guid.NewGuid().ToString("D"),
                title = "Map",
                modifiedUtc = DateTimeOffset.UnixEpoch.ToString("O"),
            },
        };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(entry));

        Assert.True(Assert.Single(new RecentMapsStore(path).Load()).IsAvailable);
    }

    [Fact]
    public void Touch_bounds_card_title_and_publishes_a_reloadable_index()
    {
        var path = Index();
        var store = new RecentMapsStore(path);

        store.Touch(
            Path.Combine(_root, "map.mapproj"),
            Guid.NewGuid(),
            new string('x', RecentMapsStore.MaxTitleRunes + 100),
            DateTimeOffset.UnixEpoch);

        var loaded = Assert.Single(store.Load());
        Assert.Equal(RecentMapsStore.MaxTitleRunes, loaded.Title.Length);
        Assert.False(loaded.IsAvailable);
        Assert.DoesNotContain(Directory.EnumerateFiles(_root), file => file.Contains(".tmp-"));
    }

    private string Index()
    {
        Directory.CreateDirectory(_root);
        return Path.Combine(_root, "recent-maps.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
