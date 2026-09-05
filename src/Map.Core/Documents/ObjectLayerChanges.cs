using System.Runtime.CompilerServices;

namespace Map.Core.Documents;

/// <summary>Transient delta handoff between immutable storage and lazily built scene derivatives.</summary>
public sealed record ObjectLayerChange(ObjectLayer Source, IReadOnlyList<int> Removed, IReadOnlyList<int> Added);

public static class ObjectLayerChanges
{
    private sealed record Link(WeakReference<ObjectLayer> Source, ObjectLayer? RetainedSource,
        int Depth, IReadOnlyList<int> Removed, IReadOnlyList<int> Added);
    private static readonly ConditionalWeakTable<ObjectLayer, Link> Changes = [];

    internal static void Register(ObjectLayer source, ObjectLayer target,
        IReadOnlyList<int> removed, IReadOnlyList<int> added)
    {
        var depth = Changes.TryGetValue(source, out var previous) ? previous.Depth + 1 : 1;
        // Keep a bounded bridge across coalesced frames: otherwise GC could erase the immediate
        // predecessor before rendering and force another million-object cold build on every click.
        var retain = depth <= 64;
        Changes.Add(target, new Link(new(source), retain ? source : null, retain ? depth : 0, removed, added));
    }

    public static bool TryGet(ObjectLayer layer, out ObjectLayerChange change)
    {
        if (Changes.TryGetValue(layer, out var link) && (link.RetainedSource ??
            (link.Source.TryGetTarget(out var weakSource) ? weakSource : null)) is { } source)
        {
            change = new(source, link.Removed, link.Added);
            return true;
        }
        change = null!;
        return false;
    }
}
