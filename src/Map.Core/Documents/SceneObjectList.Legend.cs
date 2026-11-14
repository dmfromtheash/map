using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Map.Core.Documents;

public sealed partial class SceneObjectList
{
    private sealed record LegendOccurrence(string Key, int Offset, string Hash);
    private static readonly IComparer<LegendOccurrence> LegendOrder = Comparer<LegendOccurrence>.Create((a, b) =>
    {
        var key = string.CompareOrdinal(a.Key, b.Key);
        return key != 0 ? key : a.Offset.CompareTo(b.Offset);
    });
    private static readonly ConditionalWeakTable<SceneObject[], Dictionary<string, int>> VisibleChunkAssets = new();
    private readonly LegendIndex _visibleLegend;
    private sealed record NamedLegend(IReadOnlyDictionary<string, string> Names, LegendIndex Index);
    private NamedLegend? _namedLegend;

    internal IEnumerable<string> VisibleStampAssets => _visibleLegend.Hashes;

    // A projection by the asset list's base names groups variants before legend enumeration.
    // Only the most recently requested metadata projection is cached. Metadata belongs to the
    // asset list, never to a document/layer, so these derived indexes retain no revision chain.
    internal IEnumerable<string> VisibleLegendAssets(IReadOnlyDictionary<string, string> names)
    {
        var cached = Volatile.Read(ref _namedLegend);
        if (cached is null || !ReferenceEquals(cached.Names, names))
        {
            cached = new(names, new LegendIndex(this, null, null, null, names));
            Volatile.Write(ref _namedLegend, cached);
        }
        return cached.Index.Hashes;
    }

    private (LegendIndex, NamedLegend?) DeriveLegend(SceneObjectList? source, Rewrite? rewrite)
    {
        var visible = new LegendIndex(this, source, source?._visibleLegend, rewrite, null);
        var named = source is null ? null : Volatile.Read(ref source._namedLegend);
        return (visible, named is null ? null : new(named.Names,
            new LegendIndex(this, source, named.Index, rewrite, named.Names)));
    }

    // One occurrence per asset (or base name) per chunk. A bucket's minimum is its first
    // visible stamp. Edits update dropped/created chunks and only the affected bucket minima.
    private sealed class LegendIndex
    {
        private readonly ImmutableDictionary<string, ImmutableSortedDictionary<string, int>> _buckets;
        private readonly ImmutableSortedSet<LegendOccurrence> _ordered;
        public IEnumerable<string> Hashes => _ordered.Select(entry => entry.Hash);

        public LegendIndex(SceneObjectList list, SceneObjectList? source, LegendIndex? previous, Rewrite? rewrite,
            IReadOnlyDictionary<string, string>? names)
        {
            var comparer = names is null ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var buckets = (previous?._buckets ?? ImmutableDictionary.Create<string, ImmutableSortedDictionary<string, int>>(comparer)).ToBuilder();
            var ordered = (previous?._ordered ?? ImmutableSortedSet.Create(LegendOrder)).ToBuilder();
            var changed = new HashSet<string>(comparer);
            if (previous is null)
            {
                foreach (var chunk in list._chunks) Change(chunk, add: true);
            }
            else if (rewrite is not null)
            {
                foreach (var chunk in rewrite.Dropped) Change(chunk, add: false);
                foreach (var (chunk, _) in rewrite.Created) Change(chunk, add: true);
            }
            // Remove every old minimum before inserting any new one: two assets can exchange
            // positions during one chunk rewrite, and order keys intentionally ignore names.
            foreach (var name in changed)
                if (previous is not null && previous._buckets.TryGetValue(name, out var old))
                {
                    var first = old.First();
                    ordered.Remove(new(first.Key, first.Value, ""));
                }
            foreach (var name in changed)
                if (buckets.TryGetValue(name, out var next))
                {
                    var first = next.First();
                    ordered.Add(new(first.Key, first.Value, list._chunks.ChunkAtKey(first.Key)[first.Value].Stamp!.AssetSha256));
                }
            _buckets = buckets.ToImmutable();
            _ordered = ordered.ToImmutable();

            void Change(SceneObject[] chunk, bool add)
            {
                var key = (add ? list._chunks : source!._chunks).KeyOf(chunk);
                var hashes = VisibleChunkAssets.GetValue(chunk, static items =>
                {
                    var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < items.Length; i++)
                        if (items[i] is { Visible: true, Kind: SceneObjectKind.Stamp, Stamp: { } stamp })
                            result.TryAdd(stamp.AssetSha256, i);
                    return result;
                });
                var groups = hashes;
                if (names is not null)
                {
                    groups = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var (hash, offset) in hashes)
                        if (names.TryGetValue(hash, out var name)
                            && (!groups.TryGetValue(name, out var first) || offset < first)) groups[name] = offset;
                }
                foreach (var (name, offset) in groups)
                {
                    changed.Add(name);
                    var bucket = buckets.GetValueOrDefault(name, ImmutableSortedDictionary.Create<string, int>(StringComparer.Ordinal));
                    bucket = add ? bucket.SetItem(key, offset) : bucket.Remove(key);
                    if (bucket.IsEmpty) buckets.Remove(name);
                    else buckets[name] = bucket;
                }
            }
        }
    }
}
