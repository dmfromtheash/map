using System.Collections.Immutable;

namespace Map.Core.Documents;

public enum SurfaceKind
{
    Water,
    Road,
}

/// <summary>One drawn surface of an object layer: a river system or a road network, by id.</summary>
public readonly record struct SurfaceEntry(SurfaceKind Kind, Guid Id);

/// <summary>
/// The one draw order of the roads and water of an object layer. Inside one terrain level the
/// layer stack decides between layers; inside one layer this list decides between its own
/// surfaces, and it is what the «Порядок» commands change. Without an explicit order the roads
/// come first and the water lies over them, each in list order - which for a map without roads
/// is exactly the order of the water it always had.
/// </summary>
public static class LayerSurfaceOrder
{
    public static IReadOnlyList<SurfaceEntry> Resolve(ObjectLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var result = new List<SurfaceEntry>(layer.Roads.Length + layer.Rivers.Length);
        var seen = new HashSet<Guid>();
        if (!layer.SurfaceOrder.IsDefaultOrEmpty)
        {
            foreach (var id in layer.SurfaceOrder)
            {
                if (!seen.Add(id)) continue;
                if (layer.Roads.Any(r => r.Id == id)) result.Add(new SurfaceEntry(SurfaceKind.Road, id));
                else if (layer.Rivers.Any(r => r.Id == id)) result.Add(new SurfaceEntry(SurfaceKind.Water, id));
            }
        }
        foreach (var road in layer.Roads)
            if (seen.Add(road.Id)) result.Add(new SurfaceEntry(SurfaceKind.Road, road.Id));
        foreach (var river in layer.Rivers)
            if (seen.Add(river.Id)) result.Add(new SurfaceEntry(SurfaceKind.Water, river.Id));
        return result;
    }

    /// <summary>The default order: roads in list order, then water in list order.</summary>
    public static bool IsDefault(ObjectLayer layer, IReadOnlyList<Guid> order)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(order);
        var expected = layer.Roads.Select(r => r.Id).Concat(layer.Rivers.Select(r => r.Id)).ToList();
        return expected.SequenceEqual(order);
    }

    /// <summary>
    /// The layer with the given full order applied: the road and river lists are rearranged to
    /// follow it, and the explicit list is stored only when roads and water actually interleave
    /// beyond the default. Ids the order does not name keep their place after the named ones.
    /// </summary>
    public static ObjectLayer Ordered(ObjectLayer layer, IReadOnlyList<Guid> order)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(order);
        var full = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var id in order)
            if ((layer.Roads.Any(r => r.Id == id) || layer.Rivers.Any(r => r.Id == id)) && seen.Add(id)) full.Add(id);
        foreach (var entry in Resolve(layer))
            if (seen.Add(entry.Id)) full.Add(entry.Id);
        var roads = full.Where(id => layer.Roads.Any(r => r.Id == id)).Select(id => layer.Roads.First(r => r.Id == id)).ToImmutableArray();
        var rivers = full.Where(id => layer.Rivers.Any(r => r.Id == id)).Select(id => layer.Rivers.First(r => r.Id == id)).ToImmutableArray();
        var rearranged = layer with { Roads = roads, Rivers = rivers, SurfaceOrder = [] };
        return IsDefault(rearranged, full) ? rearranged : rearranged with { SurfaceOrder = [.. full] };
    }
}
