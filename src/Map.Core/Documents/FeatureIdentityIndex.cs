using System.Collections.Immutable;

namespace Map.Core.Documents;

/// <summary>
/// Document-owned, content-versioned membership of a river/road list. Layer record copies share
/// it; known edits derive it by id delta. There is no global cache keyed by layer/object identity.
/// The lazy cold build belongs to newly loaded/assigned content, never to a derived revision.
/// </summary>
internal sealed class FeatureIdentityIndex
{
    private static long _nextVersion;
    private readonly Lazy<ImmutableHashSet<Guid>> _ids;
    internal long Version { get; } = Interlocked.Increment(ref _nextVersion);
    internal bool IsPrepared => _ids.IsValueCreated;
    internal int Count => _ids.Value.Count;

    private FeatureIdentityIndex(Func<ImmutableHashSet<Guid>> build) => _ids = new(build);

    internal static FeatureIdentityIndex Create<T>(ImmutableArray<T> items, Func<T, Guid> id) =>
        new(() => items.Select(id).ToImmutableHashSet());

    internal void Prepare() => _ = _ids.Value;
    internal bool Contains(Guid id) => _ids.Value.Contains(id);

    internal FeatureIdentityIndex Derive(IReadOnlyList<Guid> removed, IReadOnlyList<Guid> added)
    {
        if (removed.Count == 0 && added.Count == 0) return this;
        // Resolve the predecessor now: derived snapshots never accumulate lazy delta chains or
        // silently rebuild a full index if an intermediate layer has been collected.
        var ids = _ids.Value;
        var retained = added.ToHashSet();
        foreach (var id in removed)
            if (!retained.Contains(id)) ids = ids.Remove(id);
        foreach (var id in added) ids = ids.Add(id);
        if (ReferenceEquals(ids, _ids.Value)) return this;
        var derived = new FeatureIdentityIndex(() => ids);
        derived.Prepare();
        return derived;
    }
}
