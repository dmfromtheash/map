namespace Map.Assets.Tests;

/// <summary>
/// The composite library query over logical cards: text, collection, tag, untagged, favorite,
/// used-in-current-map, media type and imported range, plus sort and group.
/// </summary>
public sealed class AssetLibraryQueryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "map-library-query", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Text_matches_the_set_name_its_tags_and_the_original_file_name_of_a_variant()
    {
        var catalog = Library();

        Assert.Equal(["Ель"], Names(catalog.Query(new AssetQuery { Text = "ел" })));
        Assert.Equal(["Ель"], Names(catalog.Query(new AssetQuery { Text = "хвоя" })));
        Assert.Equal(["Камни"], Names(catalog.Query(new AssetQuery { Text = "rock-02.png" })));
        Assert.Empty(catalog.Query(new AssetQuery { Text = "никогда" }).Sets);
    }

    [Fact]
    public void Collection_tag_untagged_and_favorite_filters_select_logical_cards()
    {
        var catalog = Library();
        var nature = AssetIdentity.CollectionId("Природа");

        Assert.Equal(["Камни", "Ель"], Names(catalog.Query(new AssetQuery { CollectionId = nature })));
        Assert.Equal(["Ель"], Names(catalog.Query(new AssetQuery { Tag = "хвоя" })));
        Assert.Equal(["Башня"], Names(catalog.Query(new AssetQuery { UntaggedOnly = true })));
        Assert.Equal(["Камни"], Names(catalog.Query(new AssetQuery { FavoritesOnly = true })));
    }

    [Fact]
    public void Used_in_current_map_keeps_only_sets_whose_variants_the_document_places()
    {
        var catalog = Library();
        var stones = catalog.Sets.Single(set => set.Name == "Камни");
        var placed = catalog.FindVariant(stones.VariantIds[2])!.Sha256;

        var result = catalog.Query(new AssetQuery { UsedContentHashes = [placed] });

        Assert.Equal(["Камни"], Names(result));
        Assert.Empty(catalog.Query(new AssetQuery { UsedContentHashes = [] }).Sets);
    }

    [Fact]
    public void Media_type_and_imported_range_narrow_the_result()
    {
        var catalog = Library();

        Assert.Equal(3, catalog.Query(new AssetQuery { MediaType = "image/png" }).TotalSets);
        Assert.Empty(catalog.Query(new AssetQuery { MediaType = "image/webp" }).Sets);
        Assert.Equal(
            ["Башня"],
            Names(catalog.Query(new AssetQuery { ImportedFromUtc = Stamp(30) })));
        Assert.Equal(
            ["Камни", "Ель"],
            Names(catalog.Query(new AssetQuery { ImportedToUtc = Stamp(29) })));
    }

    [Fact]
    public void Sort_covers_both_name_directions_and_both_import_directions()
    {
        var catalog = Library();

        Assert.Equal(
            ["Башня", "Ель", "Камни"],
            Names(catalog.Query(new AssetQuery { Sort = AssetSortOrder.NameAscending })));
        Assert.Equal(
            ["Камни", "Ель", "Башня"],
            Names(catalog.Query(new AssetQuery { Sort = AssetSortOrder.NameDescending })));
        Assert.Equal(
            ["Башня", "Камни", "Ель"],
            Names(catalog.Query(new AssetQuery { Sort = AssetSortOrder.ImportedNewest })));
        Assert.Equal(
            ["Ель", "Камни", "Башня"],
            Names(catalog.Query(new AssetQuery { Sort = AssetSortOrder.ImportedOldest })));
    }

    [Fact]
    public void Grouping_by_collection_and_by_tag_partitions_the_same_cards()
    {
        var catalog = Library();

        var byCollection = catalog.Query(new AssetQuery { Group = AssetGrouping.Collection }).Groups;
        Assert.Equal(["Постройки", "Природа"], byCollection.Select(group => group.Title));
        Assert.Equal(2, byCollection.Single(group => group.Title == "Природа").Items.Count);

        var byTag = catalog.Query(new AssetQuery { Group = AssetGrouping.Tag }).Groups;
        Assert.Equal(["Без тегов", "камень", "хвоя"], byTag.Select(group => group.Title));
        Assert.Equal(["Башня"], byTag.Single(group => group.Title == "Без тегов").Items.Select(i => i.Name));
    }

    [Fact]
    public void Recent_only_returns_touched_sets_newest_first_and_ignores_the_sort_order()
    {
        var catalog = Library();
        var spruce = catalog.Sets.Single(set => set.Name == "Ель");
        var tower = catalog.Sets.Single(set => set.Name == "Башня");

        catalog.TouchRecent(spruce.Id, spruce.CoverVariantId, Stamp(100));
        catalog.TouchRecent(tower.Id, tower.CoverVariantId, Stamp(200));

        var result = catalog.Query(new AssetQuery { RecentOnly = true, Sort = AssetSortOrder.NameAscending });
        Assert.Equal(["Башня", "Ель"], Names(result));
    }

    [Fact]
    public void A_summary_carries_the_badge_count_and_the_cover_without_touching_pixels()
    {
        var catalog = Library();

        var stones = catalog.Query(new AssetQuery { Text = "Камни" }).Sets.Single();

        Assert.Equal(4, stones.VariantCount);
        Assert.True(stones.IsSet);
        Assert.Equal(catalog.FindSet(stones.SetId)!.CoverVariantId, stones.CoverVariantId);
        Assert.EndsWith(".png", stones.CoverThumbnailRelativePath);
        Assert.False(catalog.Query(new AssetQuery { Text = "Башня" }).Sets.Single().IsSet);
    }

    [Fact]
    public void The_collection_tree_reports_direct_and_subtree_counts()
    {
        var catalog = Library();

        var tree = catalog.CollectionTree();

        Assert.Equal(2, tree.Count);
        var nature = tree.Single(node => node.Name == "Природа");
        Assert.Equal(2, nature.DirectCount);
        Assert.Equal(2, nature.TotalCount);
        Assert.Empty(nature.Children);
    }

    [Fact]
    public void A_tile_window_hands_out_only_the_slice_that_was_asked_for()
    {
        var sets = Enumerable.Range(0, 10_000).Select(Summary).ToList();

        var window = AssetTileWindow.Slice(sets, 4_000, 40);

        Assert.Equal(40, window.Count);
        Assert.Equal(4_000, window.FirstIndex);
        Assert.Equal("set-04000", window.Tiles[0].SetId);
        Assert.Equal(40, window.Tiles.Count);
        Assert.Equal(0, AssetTileWindow.Slice(sets, 0, 0).Count);
        Assert.Equal(1, AssetTileWindow.Slice(sets, 9_999, 40).Count);
    }

    // ---------------------------------------------------------------- helpers

    private static string[] Names(AssetQueryResult result) => [.. result.Sets.Select(item => item.Name)];

    private static DateTimeOffset Stamp(int seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);

    private static AssetSetSummary Summary(int index) => new()
    {
        SetId = $"set-{index:D5}",
        Name = $"card-{index:D5}",
        VariantCount = 1,
        CoverVariantId = $"var-{index:D5}",
        CoverSha256 = index.ToString("x64"),
        CoverThumbnailRelativePath = $"thumbnails/{index:x64}.png",
        Favorite = false,
        ImportedUtc = DateTimeOffset.UnixEpoch.AddSeconds(index),
    };

    /// <summary>
    /// Three cards: a four-variant favourite set, a two-variant tagged set and an untagged
    /// singleton in a second collection.
    /// </summary>
    private AssetCatalog Library()
    {
        var document = new AssetLibraryDocument();
        AssetLibraryStore.EnsureCollection(document, "Природа");
        AssetLibraryStore.EnsureCollection(document, "Постройки");

        var stones = Card(document, "Камни", "rock", 4, "Природа", ["камень"], Stamp(20), favorite: true);
        _ = Card(document, "Ель", "spruce", 2, "Природа", ["хвоя"], Stamp(10), favorite: false);
        _ = Card(document, "Башня", "tower", 1, "Постройки", [], Stamp(30), favorite: false);
        Assert.Equal(4, stones.VariantIds.Count);

        var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        catalog.ReplaceLibrary(document);
        return catalog;
    }

    private static AssetSet Card(
        AssetLibraryDocument document,
        string name,
        string slug,
        int variants,
        string collection,
        string[] tags,
        DateTimeOffset importedUtc,
        bool favorite)
    {
        var variantIds = new List<string>(variants);
        for (var index = 0; index < variants; index++)
        {
            var sha = Sha(slug, index);
            document.Records.Add(new AssetRecord
            {
                Sha256 = sha,
                DisplayName = $"{slug}-{index:D2}",
                OriginalFileName = $"{slug}-{index:D2}.png",
                MediaType = "image/png",
                Extension = "png",
                Width = 512,
                Height = 512,
                HasAlpha = true,
                ByteLength = 1_024,
                ImportedUtc = importedUtc,
                Pack = collection,
                Provenance = new AssetProvenance(),
                ThumbnailRelativePath = $"thumbnails/{sha}.png",
            });
            var variantId = AssetIdentity.VariantIdForContent(sha);
            document.Variants.Add(new AssetVariant
            {
                Id = variantId,
                Sha256 = sha,
                Label = $"{slug}-{index:D2}",
            });
            variantIds.Add(variantId);
        }

        var set = new AssetSet
        {
            Id = AssetIdentity.SetIdForVariants(variantIds.Select(id =>
                document.Variants.Single(variant => variant.Id == id).Sha256)),
            Name = name,
            VariantIds = variantIds,
            CoverVariantId = variantIds[0],
            CollectionId = AssetIdentity.CollectionId(collection),
            Tags = tags,
            Favorite = favorite,
            ImportedUtc = importedUtc,
        };
        document.Sets.Add(set);
        return set;
    }

    private static string Sha(string slug, int index) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{slug}:{index}")));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
