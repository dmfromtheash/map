namespace Map.Assets;

public sealed partial class AssetCatalog
{
    // These dictionaries are built privately, then only read. A metadata-only change can reuse
    // unaffected dictionaries; an own edit never traverses the attached drive's records.
    private sealed class CatalogLayer
    {
        public AssetLibraryDocument Document { get; }
        public Dictionary<string, AssetRecord> BySha { get; }
        public Dictionary<string, AssetVariant> ByVariant { get; }
        public Dictionary<string, AssetSet> BySet { get; }

        public CatalogLayer(AssetLibraryDocument document, CatalogLayer? previous = null)
        {
            Document = document;
            BySha = Index(document.Records, previous?.Document.Records, previous?.BySha,
                record => record.Sha256, StringComparer.OrdinalIgnoreCase);
            ByVariant = Index(document.Variants, previous?.Document.Variants, previous?.ByVariant,
                variant => variant.Id, StringComparer.Ordinal);
            BySet = Index(document.Sets, previous?.Document.Sets, previous?.BySet,
                set => set.Id, StringComparer.Ordinal);
        }

        private static Dictionary<string, T> Index<T>(List<T> items, List<T>? before,
            Dictionary<string, T>? previous, Func<T, string> key, StringComparer comparer) where T : class
        {
            if (before is not null && previous is not null && items.Count == before.Count
                && (ReferenceEquals(items, before) || items.SequenceEqual(before, ReferenceEqualityComparer.Instance)))
                return previous;
            var result = new Dictionary<string, T>(items.Count, comparer);
            foreach (var item in items) result.TryAdd(key(item), item);
            return result;
        }
    }

    private sealed record MountedLayer(AssetCatalog Catalog, CatalogLayer Layer);

    private sealed class LayeredIndex<T>(Dictionary<string, T> own, Dictionary<string, T>[] mounts) where T : class
    {
        public T this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
        public bool ContainsKey(string key) => TryGetValue(key, out _);

        public bool TryGetValue(string key, out T value)
        {
            if (key is not null)
            {
                if (own.TryGetValue(key, out value!)) return true;
                foreach (var mount in mounts)
                    if (mount.TryGetValue(key, out value!)) return true;
            }
            value = null!;
            return false;
        }
    }

    private sealed class CatalogSnapshot
    {
        public static CatalogSnapshot Empty { get; } = new(new CatalogLayer(new()), []);
        public CatalogLayer Own { get; }
        public MountedLayer[] Mounted { get; }
        public IReadOnlyList<AssetCatalog> Mounts { get; }
        public LayeredIndex<AssetRecord> BySha { get; }
        public LayeredIndex<AssetVariant> ByVariant { get; }
        public LayeredIndex<AssetSet> BySet { get; }
        private readonly Lazy<AssetLibraryDocument> _view;
        public AssetLibraryDocument View => _view.Value;

        public CatalogSnapshot(CatalogLayer own, MountedLayer[] mounts)
        {
            Own = own;
            Mounted = mounts;
            Mounts = Array.AsReadOnly(mounts.Select(mount => mount.Catalog).ToArray());
            BySha = new(own.BySha, mounts.Select(mount => mount.Layer.BySha).ToArray());
            ByVariant = new(own.ByVariant, mounts.Select(mount => mount.Layer.ByVariant).ToArray());
            BySet = new(own.BySet, mounts.Select(mount => mount.Layer.BySet).ToArray());
            // The snapshot itself is the cache key (own revision and captured mount layers).
            // QueryAsync captures it in O(1); the first merge runs on its worker, once per snapshot.
            _view = new(() => mounts.Length == 0 ? own.Document
                : Merge(own.Document, mounts.Select(mount => mount.Layer.Document)));
        }

        public AssetCatalog? MountForRecord(string? sha) => MountFor(sha, Own.BySha, layer => layer.BySha);
        public AssetCatalog? MountForVariant(string? id) => MountFor(id, Own.ByVariant, layer => layer.ByVariant);
        public AssetCatalog? MountForSet(string? id) => MountFor(id, Own.BySet, layer => layer.BySet);

        private AssetCatalog? MountFor<T>(string? key, Dictionary<string, T> own,
            Func<CatalogLayer, Dictionary<string, T>> index) where T : class
        {
            if (key is null || own.ContainsKey(key)) return null;
            foreach (var mount in Mounted)
                if (index(mount.Layer).ContainsKey(key)) return mount.Catalog;
            return null;
        }
    }

    private bool RefreshMount(AssetCatalog mount, AssetCatalog? replacement = null)
    {
        var snapshot = _snapshot;
        var next = (MountedLayer[])snapshot.Mounted.Clone();
        var index = Array.FindIndex(next, item => ReferenceEquals(item.Catalog, mount));
        if (index < 0) return false;
        var catalog = replacement ?? mount;
        if (ReferenceEquals(next[index].Catalog, catalog)
            && ReferenceEquals(next[index].Layer, catalog._snapshot.Own)) return false;
        next[index] = new(catalog, catalog._snapshot.Own);
        _snapshot = new(snapshot.Own, next);
        return true;
    }

    // Import helpers only touch dictionaries private to the current transaction. In particular,
    // importing equal content from a drive must create a local copy, not reuse a drive record.
    private AssetRecord? ImportRecord(string sha) => _activeMutation?.Records.GetValueOrDefault(sha)
        ?? _snapshot.Own.BySha.GetValueOrDefault(sha);

    private AssetVariant? ImportVariant(string id) => _activeMutation?.Variants.GetValueOrDefault(id)
        ?? _snapshot.Own.ByVariant.GetValueOrDefault(id);
}
