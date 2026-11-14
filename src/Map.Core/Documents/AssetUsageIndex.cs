using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Map.Core.Documents;

/// <summary>
/// References owned by immutable object storage. Roots are prepared at document construction;
/// edits update only removed/added objects, recursively including compositions and masks.
/// Values contain counts only, so cannot retain an old document or its payloads.
/// </summary>
public sealed class AssetUsageIndex
{
    private static readonly ConditionalWeakTable<IReadOnlyList<SceneObject>, AssetUsageIndex> Cache = [];
    private readonly ImmutableDictionary<string, long> _counts;
    private AssetUsageIndex(ImmutableDictionary<string, long> counts, int stampCount)
    { _counts = counts; StampCount = stampCount; }
    public int StampCount { get; }
    public long this[string hash] => _counts.GetValueOrDefault(hash);

    public static AssetUsageIndex For(ObjectLayer layer) => Cache.GetValue(layer.Objects, static objects =>
    {
        // Count in a plain dictionary (an immutable builder allocated tree nodes per object), and
        // make the counting delegate once instead of once per object.
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        Action<string> add = hash => counts[hash] = counts.GetValueOrDefault(hash) + 1;
        var stamps = 0;
        foreach (var item in objects)
        { Visit(item, add); if (item.Kind == SceneObjectKind.Stamp) stamps++; }
        return new(counts.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase), stamps);
    });

    internal static void Register(ObjectLayer source, ObjectLayer target,
        IReadOnlyList<int> removed, IReadOnlyList<int> added)
    {
        if (ReferenceEquals(source.Objects, target.Objects)) return;
        var previous = For(source);
        Cache.GetValue(target.Objects, _ =>
        {
            var counts = previous._counts.ToBuilder();
            void Change(string hash, int delta)
            {
                var count = counts.GetValueOrDefault(hash) + delta;
                if (count == 0) counts.Remove(hash);
                else if (count > 0) counts[hash] = count;
                else throw new InvalidOperationException("Negative asset usage count.");
            }
            var stamps = previous.StampCount;
            foreach (var i in removed)
            { Visit(source.Objects[i], hash => Change(hash, -1)); if (source.Objects[i].Kind == SceneObjectKind.Stamp) stamps--; }
            foreach (var i in added)
            { Visit(target.Objects[i], hash => Change(hash, 1)); if (target.Objects[i].Kind == SceneObjectKind.Stamp) stamps++; }
            return new(counts.ToImmutable(), stamps);
        });
    }

    /// <summary>Includes saved variants even when they have no placed copies.</summary>
    public static long Count(MapDocument document, string hash)
    {
        long count = 0;
        void Add(string? candidate)
        {
            if (string.Equals(hash, candidate, StringComparison.OrdinalIgnoreCase)) count++;
        }
        foreach (var layer in document.Layers)
        {
            if (layer is ObjectLayer objects)
            {
                count += For(objects)[hash];
                foreach (var path in objects.PlacementPaths)
                    foreach (var variant in path.Variants) { Add(variant.Sha256); Add(variant.MaskSha256); }
                foreach (var area in objects.PlacementAreas)
                    foreach (var variant in area.Variants) { Add(variant.Sha256); Add(variant.MaskSha256); }
                foreach (var river in objects.Rivers) Add(MapFillTextures.AssetSha256(river.Style.TextureId));
                foreach (var road in objects.Roads)
                {
                    Add(MapFillTextures.AssetSha256(road.Style.TextureId));
                    foreach (var branch in road.Branches) Add(MapFillTextures.AssetSha256(branch.Style?.TextureId));
                }
            }
            if (layer is FilterLayer filter) Add(filter.Settings.TextureAssetSha256);
            if (layer is BaseColorLayer background) Add(MapFillTextures.AssetSha256(background.TextureId));
            if (layer is LandLayer land) Add(MapFillTextures.AssetSha256(land.EdgeStyle.FillTextureId));
        }
        return count;
    }

    internal static void Visit(SceneObject item, Action<string> add)
    {
        static void Add(Action<string> add, string? hash) { if (!string.IsNullOrWhiteSpace(hash)) add(hash); }
        Add(add, item.Stamp?.AssetSha256);
        Add(add, item.Stamp?.MaskAssetSha256);
        Add(add, item.Line?.Stroke.AssetSha256);
        Add(add, item.Shape?.Stroke.AssetSha256);
        Add(add, item.Shape?.Fill.TextureAssetSha256);
        Add(add, item.Room?.CapAssetSha256);
        if (item.Decoration is { } decoration)
            foreach (var entry in decoration.Entries) Add(add, entry.AssetSha256);
        if (item.Composition is not { } composition) return;
        foreach (var child in composition.Items) Visit(child, add);
        foreach (var filter in composition.Filters) Add(add, filter.TextureAssetSha256);
    }
}
