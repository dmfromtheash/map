using System.Runtime.CompilerServices;

namespace Map.Core.Documents;

/// <summary>
/// The document's asset references by hash, built once per asset list. Revisions that do not add or drop an
/// asset share the same list instance, so every edit after the first finds it here instead of hashing the
/// whole list again. Placing one stamp on a 2-million-object map rebuilt three such sets per click
/// (PlaceAssetObjectsCommand, FillTextureAssets, ResolvePlan): 30-80 ms per placement, one 382-431 ms frame
/// in the live place-10 route (PC, 2026-11-15, CPU trace).
/// </summary>
public static class AssetReferenceIndex
{
    private static readonly ConditionalWeakTable<IReadOnlyList<MapAssetReference>, Dictionary<string, MapAssetReference>> Cache = [];

    /// <summary>First reference of each hash (the order <see cref="MapDocument.Assets"/> lists them). Read-only by contract.</summary>
    public static IReadOnlyDictionary<string, MapAssetReference> For(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Cache.GetValue(document.Assets, static assets =>
        {
            Interlocked.Increment(ref _builds);
            var byHash = new Dictionary<string, MapAssetReference>(assets.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets) byHash.TryAdd(asset.Sha256, asset);
            return byHash;
        });
    }

    /// <summary>Indexes built (every reference hashed). Diagnostics and tests only.</summary>
    internal static long Builds => Interlocked.Read(ref _builds);
    private static long _builds;

    public static bool Contains(MapDocument document, string sha256) => For(document).ContainsKey(sha256);
}
