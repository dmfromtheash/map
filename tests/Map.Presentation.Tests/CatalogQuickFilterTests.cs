using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>Catalog quick filters: used on this map, collection, tags, archived — all together with search.</summary>
public sealed class CatalogQuickFilterTests : IDisposable
{
    private readonly List<AssetCatalog> _catalogs = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-quick-filters-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Archive_hides_a_card_until_archived_is_switched_on_and_deletes_nothing()
    {
        var editor = Editor(out var catalog);
        var oak = catalog.Sets.Single(s => s.Name == "Дуб");
        editor.SelectAssetSet(oak.Id);
        Assert.True(editor.ToggleSelectedSetArchived());
        Assert.True(editor.SelectedSetArchived);
        Assert.DoesNotContain(editor.LibrarySets, s => s.SetId == oak.Id);
        Assert.Equal(4, catalog.Records.Count);

        editor.LibraryShowArchived = true;
        Assert.Contains(editor.LibrarySets, s => s.SetId == oak.Id && s.Archived);
        Assert.True(editor.HasActiveLibraryFilters);
        Assert.Contains("с архивными", editor.ActiveLibraryFilterSummary);

        // Archived and search combine: the archived oak is found by its name only with the switch on.
        editor.LibrarySearchText = "дуб";
        Assert.Equal([oak.Id], editor.LibrarySets.Select(s => s.SetId));
        editor.LibraryShowArchived = false;
        Assert.Empty(editor.LibrarySets);

        editor.ClearLibraryFilters();
        Assert.False(editor.LibraryShowArchived);
        editor.SelectAssetSet(oak.Id);
        Assert.True(editor.ToggleSelectedSetArchived());
        Assert.Contains(editor.LibrarySets, s => s.SetId == oak.Id);
    }

    [Fact]
    public void Used_on_this_map_collection_and_tags_combine_with_search()
    {
        var editor = Editor(out var catalog);
        var pine = catalog.Sets.Single(s => s.Name == "Сосна");
        editor.SelectAssetSet(pine.Id);
        Assert.True(editor.PlaceCurrentVariantAt(300, 300));

        editor.LibraryUsedInMapOnly = true;
        Assert.Equal([pine.Id], editor.LibrarySets.Select(s => s.SetId));
        editor.LibrarySearchText = "дом";
        Assert.Empty(editor.LibrarySets);
        editor.ClearLibraryFilters();

        editor.LibraryCollectionId = catalog.Sets.Single(s => s.Name == "Дом").CollectionId;
        Assert.Equal(["Дом", "Башня"], editor.LibrarySets.Select(s => s.Name).Order().Reverse());
        editor.SetLibraryTagSelected("Стиль/Камень", true);
        Assert.Equal(["Башня"], editor.LibrarySets.Select(s => s.Name));
        editor.LibrarySearchText = "дом";
        Assert.Empty(editor.LibrarySets);
    }

    private EditorViewModel Editor(out AssetCatalog catalog)
    {
        catalog = new AssetCatalog(Path.Combine(_root, "catalog-" + Guid.NewGuid().ToString("N")));
        _catalogs.Add(catalog);
        catalog.ImportSet([Image(SKColors.ForestGreen, "Сосна")], "Сосна", new AssetImportOptions { Pack = "Лес", Tags = ["Стиль/Акварель"] }, DateTimeOffset.UnixEpoch);
        catalog.ImportSet([Image(SKColors.Olive, "Дуб")], "Дуб", new AssetImportOptions { Pack = "Лес", Tags = ["Стиль/Акварель"] }, DateTimeOffset.UnixEpoch.AddSeconds(1));
        catalog.ImportSet([Image(SKColors.Brown, "Дом")], "Дом", new AssetImportOptions { Pack = "Город", Tags = ["Стиль/Дерево"] }, DateTimeOffset.UnixEpoch.AddSeconds(2));
        catalog.ImportSet([Image(SKColors.Gray, "Башня")], "Башня", new AssetImportOptions { Pack = "Город", Tags = ["Стиль/Камень"] }, DateTimeOffset.UnixEpoch.AddSeconds(3));
        var document = new MapDocument(DocumentId.New(), "filters", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FF20242B"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF20242B"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        return new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => DateTimeOffset.UnixEpoch, assetCatalog: catalog);
    }

    private string Image(SKColor color, string name)
    {
        var folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".png");
        using var surface = SKSurface.Create(new SKImageInfo(24, 24))!;
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    public void Dispose()
    {
        foreach (var catalog in _catalogs) catalog.FlushMetadataAsync().GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
