using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// Library textures used as fills (land, water, floors, roads, the sea) travel with the map: every
/// <c>asset:</c> id the document names is backed by an embedded asset of the same hash, exactly like
/// the picture of a stamp. The editing session runs <see cref="Embed"/> after each edit, so a texture
/// picked from a drive library is copied into the map in the same undo step that chose it.
/// </summary>
public static class FillTextureAssets
{
    /// <summary>Every fill texture id the layers name (built-in and library), each once.</summary>
    public static IReadOnlySet<string> UsedIds(IEnumerable<MapLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? id) { if (!string.IsNullOrWhiteSpace(id)) ids.Add(id); }
        foreach (var layer in layers)
        {
            switch (layer)
            {
                case BaseColorLayer page: Add(page.TextureId); break;
                case LandLayer land: Add(land.EdgeStyle.FillTextureId); break;
                case ObjectLayer objects:
                    foreach (var river in objects.Rivers) Add(river.Style.TextureId);
                    foreach (var road in objects.Roads)
                    {
                        Add(road.Style.TextureId);
                        foreach (var branch in road.Branches) Add(branch.Style?.TextureId);
                    }
                    break;
            }
        }
        return ids;
    }

    /// <summary>True when some layer names a library texture (schema v55).</summary>
    public static bool UsesLibraryTextures(IEnumerable<MapLayer> layers) =>
        UsedIds(layers).Any(MapFillTextures.IsAssetId);

    /// <summary>Hashes of library textures the document names but does not carry yet.</summary>
    public static IReadOnlyList<string> MissingHashes(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var carried = document.Assets.Select(asset => asset.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. UsedIds(document.Layers).Select(MapFillTextures.AssetSha256).OfType<string>()
            .Where(sha => !carried.Contains(sha)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Adds the missing library textures that <paramref name="source"/> can supply (embedded, with
    /// the hash the id names). A texture the source cannot supply stays unembedded: it draws while
    /// the library is there and falls back to the plain fill without it.
    /// </summary>
    public static MapDocument Embed(MapDocument document, Func<string, MapAssetReference?> source, out IReadOnlyList<MapAssetReference> added)
    {
        ArgumentNullException.ThrowIfNull(source);
        var missing = MissingHashes(document);
        added = [];
        if (missing.Count == 0) return document;
        var found = new List<MapAssetReference>();
        foreach (var sha in missing)
            if (source(sha) is { Storage: ProjectAssetStorage.Embedded, EmbeddedContent: not null } asset
                && string.Equals(asset.Sha256, sha, StringComparison.OrdinalIgnoreCase))
                found.Add(asset);
        if (found.Count == 0) return document;
        added = found;
        return document.WithAssets([.. document.Assets, .. found]);
    }
}

/// <summary>
/// An edit plus the library textures it brought into the map, as one history entry: undo removes
/// both the choice and the copied texture, redo brings both back.
/// </summary>
public sealed class FillTextureEmbedCommand : IEditCommand
{
    private readonly IEditCommand _inner;
    private readonly IReadOnlyList<MapAssetReference> _assets;

    public FillTextureEmbedCommand(IEditCommand inner, IReadOnlyList<MapAssetReference> assets)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(assets);
        _inner = inner;
        _assets = [.. assets];
    }

    public string Name => _inner.Name;
    public long? MeasuredWorkingBytes => _inner.MeasuredWorkingBytes;

    public MapDocument Apply(MapDocument document)
    {
        var edited = _inner.Apply(document);
        var carried = edited.Assets.Select(asset => asset.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var add = _assets.Where(asset => !carried.Contains(asset.Sha256)).ToArray();
        return add.Length == 0 ? edited : edited.WithAssets([.. edited.Assets, .. add]);
    }

    public MapDocument Revert(MapDocument document)
    {
        var drop = _assets.Select(asset => asset.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var without = document.Assets.Any(asset => drop.Contains(asset.Sha256))
            ? document.WithAssets([.. document.Assets.Where(asset => !drop.Contains(asset.Sha256))])
            : document;
        return _inner.Revert(without);
    }
}
