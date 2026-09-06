using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// A stationary, seeded hard-core sample. Each ordinal adds a new pass; later passes cannot
/// move or evict earlier ones. The polygon and manual exclusions never affect neighbour decisions.
/// Caches live for one preview/command/read, not in the document or undo history.
/// </summary>
public sealed class PlacementAreaSampler(PlacementArea area)
{
    public const int MaximumCachedCandidates = 16_384;
    private sealed class Candidate(SceneObject item, WorldBounds bounds, ulong priority, LinkedListNode<PlacementAreaSlot> node)
    {
        public SceneObject Item { get; } = item;
        public WorldBounds Bounds { get; } = bounds;
        public ulong Priority { get; } = priority;
        public LinkedListNode<PlacementAreaSlot> Node { get; } = node;
        public bool? ClearEarlier { get; set; }
        public bool? Accepted { get; set; }
    }
    private readonly Dictionary<PlacementAreaSlot, Candidate> _candidates = [];
    private readonly LinkedList<PlacementAreaSlot> _recent = new();
    private readonly double _reach = MaximumReach(area);
    public int CachedCandidateCount => _candidates.Count;

    public static WorldBounds Footprint(SceneObject item)
    {
        var b = SceneGeometry.BoundsOf(item);
        var gap = Math.Max(2, Math.Min(item.Width, item.Height) * .06);
        return new(b.Left - gap, b.Top - gap, b.Right + gap, b.Bottom + gap);
    }
    public static bool Overlaps(WorldBounds a, WorldBounds b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static double MaximumReach(PlacementArea p)
    {
        var height = p.Request.BaseWidth * p.Variants.Max(v => (double)v.PixelHeight / v.PixelWidth);
        return Math.Sqrt(p.Request.BaseWidth * p.Request.BaseWidth + height * height) / 2
            + p.Style.Blur * 3 + (p.Style.ShadowOpacity > 0
                ? Math.Max(Math.Abs(p.Style.ShadowOffsetX), Math.Abs(p.Style.ShadowOffsetY)) + p.Style.ShadowBlur * 3 : 0)
            + Math.Max(2, Math.Min(p.Request.BaseWidth, height) * .06);
    }
    public static decimal EstimateBytes(PlacementArea p, IReadOnlyList<PlacementAreaGeometry.CellSpan> spans)
    {
        if (p.LayoutVersion == 0 || spans.Count == 0 || p.Density == 0) return 0;
        // Reserve the bounded working cache, live dependency frames and the <=64 preview results.
        // The old whole dependency halo grew cubically with density (gigabytes for a few mountains).
        // Eviction only recomputes pure slot decisions; it never changes the stored layout rule.
        return (MaximumCachedCandidates + 2 * (decimal)Math.Ceiling(p.Density) + 256) * 1664;
    }
    public SceneObject Item(PlacementAreaSlot slot) => area.LayoutVersion == 0
        ? PlacementAreaPlanner.Item(area, slot) : CandidateAt(slot).Item;
    private Candidate CandidateAt(PlacementAreaSlot slot)
    {
        if (_candidates.TryGetValue(slot, out var cached))
        {
            if (_recent.Last != cached.Node) { _recent.Remove(cached.Node); _recent.AddLast(cached.Node); }
            return cached;
        }
        if (_candidates.Count == MaximumCachedCandidates)
        { _candidates.Remove(_recent.First!.Value); _recent.RemoveFirst(); }
        var item = PlacementAreaPlanner.Item(area, slot);
        return _candidates[slot] = new(item, Footprint(item), PlacementAreaPlanner.Priority(area, slot), _recent.AddLast(slot));
    }
    public bool Includes(PlacementAreaSlot slot)
    {
        if (!PlacementAreaPlanner.IncludesGeometry(area, slot)) return false;
        return area.LayoutVersion == 0 || Accepted(slot);
    }
    public IEnumerable<PlacementAreaSlot> Slots(int x, int y)
    {
        for (var ordinal = 0; ordinal < (int)Math.Ceiling(area.Density); ordinal++)
        { var slot = new PlacementAreaSlot(x, y, ordinal); if (Includes(slot)) yield return slot; }
    }
    private IEnumerable<PlacementAreaSlot> Neighbours(WorldBounds b, int ordinal)
    {
        var left = PlacementAreaGeometry.Cell(b.Left - _reach); var right = PlacementAreaGeometry.Cell(b.Right + _reach);
        var top = PlacementAreaGeometry.Cell(b.Top - _reach); var bottom = PlacementAreaGeometry.Cell(b.Bottom + _reach);
        for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++) yield return new(x, y, ordinal);
    }
    private bool ClearEarlier(PlacementAreaSlot slot)
    {
        var candidate = CandidateAt(slot);
        if (candidate.ClearEarlier is { } clear) return clear;
        for (var ordinal = 0; ordinal < slot.Ordinal; ordinal++)
            foreach (var other in Neighbours(candidate.Bounds, ordinal))
                if (Overlaps(candidate.Bounds, CandidateAt(other).Bounds) && Accepted(other))
                { candidate.ClearEarlier = false; return false; }
        candidate.ClearEarlier = true; return true;
    }
    private bool Accepted(PlacementAreaSlot slot)
    {
        var candidate = CandidateAt(slot);
        if (candidate.Accepted is { } accepted) return accepted;
        if (!ClearEarlier(slot)) { candidate.Accepted = false; return false; }
        foreach (var other in Neighbours(candidate.Bounds, slot.Ordinal))
        {
            if (other == slot) continue;
            var neighbour = CandidateAt(other);
            var earlier = neighbour.Priority < candidate.Priority || neighbour.Priority == candidate.Priority
                && (other.X < slot.X || other.X == slot.X && other.Y < slot.Y);
            if (earlier && Overlaps(candidate.Bounds, neighbour.Bounds) && ClearEarlier(other))
            { candidate.Accepted = false; return false; }
        }
        candidate.Accepted = true; return true;
    }
}

/// <summary>Existing visible stamps in the same layer/band are obstacles, never moved by the fill.</summary>
public sealed class PlacementAreaObstacles(MapDocument document, LayerId layerId, Guid areaId, int sublayer)
{
    public bool Blocks(SceneObject item)
    {
        if (document.FindLayer(layerId) is not ObjectLayer layer) return false;
        var footprint = PlacementAreaSampler.Footprint(item);
        foreach (var other in ObjectSpatialIndex.For(layer).Query(footprint))
            if (other.Id != item.Id && other.Kind == SceneObjectKind.Stamp && other.Visible && other.Sublayer == sublayer
                && PlacementAreaOwnership.Owner(document, other.Id)?.Id != areaId
                && PlacementAreaSampler.Overlaps(footprint, SceneGeometry.BoundsOf(other))) return true;
        return false;
    }
}
