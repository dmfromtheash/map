namespace Map.Assets;

public enum AssetSourceFilter { All, Mine, BuiltIn }

public enum AssetSortOrder
{
    ImportedNewest = 0,
    ImportedOldest = 1,
    NameAscending = 2,
    NameDescending = 3,
}

public enum AssetGrouping
{
    None = 0,
    Collection = 1,
    Tag = 2,
}

/// <summary>
/// One composite library request. Every member is metadata only: answering a query never opens an
/// original image, so filtering ten thousand cards costs no decode.
/// </summary>
public sealed record AssetQuery
{
    public string? Text { get; init; }

    /// <summary>Collection id; a set inherits the collection of the pack it was imported into.</summary>
    public string? CollectionId { get; init; }

    public string? Tag { get; init; }

    /// <summary>Any selected tag within a branch, all selected branches (Style, Subject, etc.).</summary>
    public IReadOnlyCollection<string> Tags { get; init; } = [];
    public AssetSourceFilter Source { get; init; }

    public bool UntaggedOnly { get; init; }

    public bool FavoritesOnly { get; init; }

    /// <summary>«Архивные»: archived cards are shown too (they are hidden otherwise).</summary>
    public bool IncludeArchived { get; init; }

    /// <summary>Only sets present in the bounded recent list, most recently used first.</summary>
    public bool RecentOnly { get; init; }

    /// <summary>Content hashes the open map actually places; null disables the filter.</summary>
    public IReadOnlyCollection<string>? UsedContentHashes { get; init; }

    /// <summary>Media type of the cover content, for example <c>image/png</c>.</summary>
    public string? MediaType { get; init; }

    public DateTimeOffset? ImportedFromUtc { get; init; }

    public DateTimeOffset? ImportedToUtc { get; init; }

    public AssetSortOrder Sort { get; init; } = AssetSortOrder.ImportedNewest;

    public AssetGrouping Group { get; init; } = AssetGrouping.None;
}

/// <summary>
/// The logical card the grid shows. It carries the badge count and the cover metadata but no
/// pixels: the thumbnail is decoded later, only for a tile the user can actually see.
/// </summary>
public sealed record AssetSetSummary
{
    public required string SetId { get; init; }
    public required string Name { get; init; }
    public required int VariantCount { get; init; }
    public required string CoverVariantId { get; init; }
    public required string CoverSha256 { get; init; }
    public required string CoverThumbnailRelativePath { get; init; }
    public required bool Favorite { get; init; }
    public string? CollectionId { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required DateTimeOffset ImportedUtc { get; init; }
    public int CoverWidth { get; init; }
    public int CoverHeight { get; init; }
    public string MediaType { get; init; } = "";
    public bool Archived { get; init; }

    /// <summary>A card with more than one variant is what the UI badges.</summary>
    public bool IsSet => VariantCount > 1;
}

public sealed record AssetSetGroup(string Key, string Title, IReadOnlyList<AssetSetSummary> Items);

public sealed record AssetQueryResult(
    IReadOnlyList<AssetSetSummary> Sets,
    IReadOnlyList<AssetSetGroup> Groups,
    int TotalSets);

/// <summary>One collection tree node with the number of logical cards under it.</summary>
public sealed record AssetCollectionNode(
    string Id,
    string Name,
    int DirectCount,
    int TotalCount,
    IReadOnlyList<AssetCollectionNode> Children);

/// <summary>
/// The slice of a result the grid is currently allowed to realise. Virtualization is a contract,
/// not a rendering detail: a caller that asks for a window of forty tiles must never be handed
/// ten thousand, so the bound lives here where it can be tested without a window.
/// </summary>
public sealed record AssetTileWindow(int FirstIndex, int Count, IReadOnlyList<AssetSetSummary> Tiles)
{
    public static AssetTileWindow Empty { get; } = new(0, 0, []);

    public static AssetTileWindow Slice(IReadOnlyList<AssetSetSummary> sets, int firstIndex, int count)
    {
        ArgumentNullException.ThrowIfNull(sets);
        if (sets.Count == 0 || count <= 0)
        {
            return Empty;
        }

        var first = Math.Clamp(firstIndex, 0, Math.Max(0, sets.Count - 1));
        var take = Math.Clamp(count, 0, sets.Count - first);
        var tiles = new AssetSetSummary[take];
        for (var index = 0; index < take; index++)
        {
            tiles[index] = sets[first + index];
        }

        return new AssetTileWindow(first, take, tiles);
    }
}
