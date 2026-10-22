using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// A stationary, seeded hard-core sample. Each ordinal adds a new pass; later passes cannot
/// move or evict earlier ones. The polygon and manual exclusions never affect neighbour decisions.
/// Caches live for one preview/command/read, not in the document or undo history.
/// </summary>
public sealed class PlacementAreaSampler(PlacementArea area, CancellationToken cancellationToken = default)
{
    public const int MaximumCachedCandidates = 16_384;
    public const double MaximumJoinedOverlap = .55;
    private sealed class Candidate(SceneObject item, WorldBounds bounds, WorldBounds visible, ulong priority, bool eligible, double activation, LinkedListNode<PlacementAreaSlot> node)
    {
        public SceneObject Item { get; } = item;
        public WorldBounds Bounds { get; } = bounds;
        public WorldBounds Visible { get; } = visible;
        public ulong Priority { get; } = priority;
        public bool Eligible { get; } = eligible;
        public double Activation { get; } = activation;
        public LinkedListNode<PlacementAreaSlot> Node { get; } = node;
        public bool? ClearEarlier { get; set; }
        public bool? Accepted { get; set; }
        public PlacementAreaSilhouette? Silhouette { get; init; }
        public SceneObject? Finished { get; set; }
    }
    private readonly Dictionary<PlacementAreaSlot, Candidate> _candidates = [];
    private readonly LinkedList<PlacementAreaSlot> _recent = new();
    private readonly double _reach = MaximumReach(area);
    private readonly PlacementAreaLayout? _layout = area.LayoutVersion >= 4 ? new(area) : null;
    // R10 «Красота» and the density brush; null for every area without them (as before).
    private readonly PlacementAreaBeautyField? _beauty = PlacementAreaBeautyField.For(area);
    public int CachedCandidateCount => _candidates.Count;

    public static WorldBounds Footprint(SceneObject item, int layoutVersion = 1)
    {
        var b = SceneGeometry.BoundsOf(item);
        var gap = Math.Max(2, Math.Min(item.Width, item.Height) * (layoutVersion >= 2 ? .02 : .06));
        return new(b.Left - gap, b.Top - gap, b.Right + gap, b.Bottom + gap);
    }
    public static WorldBounds VisibleBounds(PlacementArea area, SceneObject item)
    {
        if (area.LayoutVersion < 3 || item.Stamp is not { } stamp
            || !area.ContentBounds.TryGetValue(stamp.AssetSha256, out var content)) return SceneGeometry.BoundsOf(item);
        var left = stamp.FlipHorizontal ? 1 - content.Right : content.Left;
        var top = stamp.FlipVertical ? 1 - content.Bottom : content.Top;
        var localX = (left + content.Width / 2 - .5) * item.Width;
        var localY = (top + content.Height / 2 - .5) * item.Height;
        var angle = item.RotationDegrees * Math.PI / 180; var cos = Math.Cos(angle); var sin = Math.Sin(angle);
        var centerX = item.X + item.Width / 2 + localX * cos - localY * sin;
        var centerY = item.Y + item.Height / 2 + localX * sin + localY * cos;
        var halfX = (content.Width * item.Width * Math.Abs(cos) + content.Height * item.Height * Math.Abs(sin)) / 2;
        var halfY = (content.Width * item.Width * Math.Abs(sin) + content.Height * item.Height * Math.Abs(cos)) / 2;
        var pad = item.StrokeWidth / 2 + stamp.Blur * 3;
        var shadowX = stamp.ShadowOpacity > 0 ? Math.Abs(stamp.ShadowOffsetX) + stamp.ShadowBlur * 3 : 0;
        var shadowY = stamp.ShadowOpacity > 0 ? Math.Abs(stamp.ShadowOffsetY) + stamp.ShadowBlur * 3 : 0;
        return new(centerX - halfX - pad - shadowX, centerY - halfY - pad - shadowY,
            centerX + halfX + pad + shadowX, centerY + halfY + pad + shadowY);
    }
    public static WorldBounds Footprint(PlacementArea area, SceneObject item)
    {
        if (area.LayoutVersion < 3) return Footprint(item, area.LayoutVersion);
        var b = VisibleBounds(area, item);
        if (area.JoinEdges)
        {
            // Canopies/bases can interlock more vertically than horizontally. Keep the centre
            // clear and independently limit intersection of the smaller visible rectangle.
            var insetX = (b.Right - b.Left) * .18; var insetY = (b.Bottom - b.Top) * .32;
            return new(b.Left + insetX, b.Top + insetY, b.Right - insetX, b.Bottom - insetY);
        }
        var gap = Math.Max(.5, Math.Min(b.Right - b.Left, b.Bottom - b.Top) * .01);
        return new(b.Left - gap, b.Top - gap, b.Right + gap, b.Bottom + gap);
    }
    public static bool Overlaps(WorldBounds a, WorldBounds b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
    public static double OverlapFraction(WorldBounds a, WorldBounds b)
    {
        var intersection = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left))
            * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        return intersection / Math.Min((a.Right - a.Left) * (a.Bottom - a.Top), (b.Right - b.Left) * (b.Bottom - b.Top));
    }
    private static bool CoresConflict(PlacementArea area, WorldBounds a, WorldBounds b)
    {
        if (area.LayoutVersion < 4 || !area.JoinEdges) return Overlaps(a, b);
        // Rounded protected centres let diagonal edges interlock without rectangular holes.
        var dx = (a.Left + a.Right - b.Left - b.Right) / (a.Right - a.Left + b.Right - b.Left);
        var dy = (a.Top + a.Bottom - b.Top - b.Bottom) / (a.Bottom - a.Top + b.Bottom - b.Top);
        return dx * dx + dy * dy < 1;
    }
    public static bool Conflicts(PlacementArea area, SceneObject a, SceneObject b)
    {
        var av = VisibleBounds(area, a); var bv = VisibleBounds(area, b);
        if (PlacementAreaSilhouette.Create(area, a, av) is { } sa && PlacementAreaSilhouette.Create(area, b, bv) is { } sb)
            return Overlaps(av, bv) && PlacementAreaSilhouette.Conflicts(sa, sb);
        return CoresConflict(area, Footprint(area, a), Footprint(area, b)) || area.LayoutVersion >= 3 && area.JoinEdges
            && OverlapFraction(av, bv) > MaximumJoinedOverlap;
    }
    private bool Conflicts(Candidate a, Candidate b) => a.Silhouette is { } sa && b.Silhouette is { } sb
        ? Overlaps(a.Visible, b.Visible) && PlacementAreaSilhouette.Conflicts(sa, sb)
        : CoresConflict(area, a.Bounds, b.Bounds)
            || area.LayoutVersion >= 3 && area.JoinEdges && OverlapFraction(a.Visible, b.Visible) > MaximumJoinedOverlap;

    private static double MaximumReach(PlacementArea p)
    {
        var width = p.Request.BaseWidth * (p.LayoutVersion >= 3 ? PlacementAreaComposition.MaximumScale(p) : 1);
        var height = width * p.Variants.Max(v => (double)v.PixelHeight / v.PixelWidth);
        return Math.Sqrt(width * width + height * height) / 2
            + p.Style.Blur * 3 + (p.Style.ShadowOpacity > 0
                ? Math.Max(Math.Abs(p.Style.ShadowOffsetX), Math.Abs(p.Style.ShadowOffsetY)) + p.Style.ShadowBlur * 3 : 0)
            + Math.Max(2, Math.Min(width, height) * (p.LayoutVersion >= 2 ? .02 : .06));
    }
    public static decimal EstimateBytes(PlacementArea p, IReadOnlyList<PlacementAreaGeometry.CellSpan> spans)
    {
        if (p.LayoutVersion == 0 || spans.Count == 0 || p.Density == 0) return 0;
        // Reserve the bounded working cache, live dependency frames and the <=64 preview results.
        // The old whole dependency halo grew cubically with density (gigabytes for a few mountains).
        // Eviction only recomputes pure slot decisions; it never changes the stored layout rule.
        return (MaximumCachedCandidates + 2 * (decimal)(p.LayoutVersion >= 4 ? 0 : PlacementAreaPlanner.CandidatePasses(p)) + 256) * 1664
            + (p.LayoutVersion >= 4 ? 4 * 1024 * 1024 : 0);
    }
    public SceneObject Item(PlacementAreaSlot slot)
    {
        if (area.LayoutVersion == 0) return PlacementAreaPlanner.Item(area, slot);
        var candidate = CandidateAt(slot);
        // Spacing used the unfinished candidate; the stored object is finished (smaller at the
        // soft edge, colour, contact shadow), so the border and the noise never decide conflicts.
        return _beauty is null ? candidate.Item : candidate.Finished ??= _beauty.Finish(candidate.Item, slot);
    }
    private Candidate CandidateAt(PlacementAreaSlot slot)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_candidates.TryGetValue(slot, out var cached))
        {
            if (_recent.Last != cached.Node) { _recent.Remove(cached.Node); _recent.AddLast(cached.Node); }
            return cached;
        }
        if (_candidates.Count == MaximumCachedCandidates)
        { _candidates.Remove(_recent.First!.Value); _recent.RemoveFirst(); }
        PlacementAreaComposition.Sample? composition = _layout?.At(slot)
            ?? (area.LayoutVersion >= 3 ? PlacementAreaComposition.At(area, slot) : null);
        var item = PlacementAreaPlanner.Item(area, slot, composition, _beauty);
        var visible = VisibleBounds(area, item);
        return _candidates[slot] = new(item, Footprint(area, item), visible,
            composition?.Priority ?? PlacementAreaPlanner.Priority(area, slot), composition?.Eligible ?? true,
            composition?.Activation ?? 0, _recent.AddLast(slot)) { Silhouette = PlacementAreaSilhouette.Create(area, item, visible) };
    }
    public bool Includes(PlacementAreaSlot slot)
    {
        if (!PlacementAreaPlanner.IncludesGeometry(area, slot, _layout?.At(slot).Point)) return false;
        if (area.LayoutVersion >= 3)
        {
            var candidate = CandidateAt(slot);
            var density = area.Density;
            // Soft edge, clumps and the brush scale the density a slot is compared with: a slot
            // only disappears or returns, the ones that stay keep their places.
            if (_beauty is { ChangesDensity: true })
                density *= _beauty.DensityFactor(candidate.Item.X + candidate.Item.Width / 2, candidate.Item.Y + candidate.Item.Height / 2);
            if (candidate.Activation > density) return false;
        }
        return area.LayoutVersion == 0 || Accepted(slot);
    }
    public IEnumerable<PlacementAreaSlot> Slots(int x, int y)
    {
        var passes = area.Density == 0 ? 0 : _layout?.Cell(x, y).Count ?? PlacementAreaPlanner.CandidatePasses(area);
        for (var ordinal = 0; ordinal < passes; ordinal++)
        { var slot = new PlacementAreaSlot(x, y, ordinal); if (Includes(slot)) yield return slot; }
    }
    internal IEnumerable<PlacementAreaSlot> PreviewSlots(int x, int y, int maximum)
    {
        var count = _layout!.Cell(x, y).Count; var take = Math.Min(count, maximum);
        for (var i = 0; i < take; i++)
        {
            var slot = new PlacementAreaSlot(x, y, take == 1 ? 0 : (int)((long)i * (count - 1) / (take - 1)));
            if (Includes(slot)) yield return slot;
        }
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
            foreach (var other in Neighbours(area.LayoutVersion >= 3 ? candidate.Visible : candidate.Bounds, ordinal))
                if (Conflicts(candidate, CandidateAt(other)) && Accepted(other))
                { candidate.ClearEarlier = false; return false; }
        candidate.ClearEarlier = true; return true;
    }
    private bool Accepted(PlacementAreaSlot slot)
    {
        var candidate = CandidateAt(slot);
        if (candidate.Accepted is { } accepted) return accepted;
        if (!candidate.Eligible) { candidate.Accepted = false; return false; }
        if (_layout is not null)
        {
            foreach (var cell in Neighbours(candidate.Visible, 0))
            {
                var count = _layout.Cell(cell.X, cell.Y).Count;
                for (var ordinal = 0; ordinal < count; ordinal++)
                {
                    var other = cell with { Ordinal = ordinal };
                    if (other == slot) continue;
                    var neighbour = CandidateAt(other);
                    var earlier = neighbour.Priority < candidate.Priority || neighbour.Priority == candidate.Priority
                        && (other.X < slot.X || other.X == slot.X && (other.Y < slot.Y
                            || other.Y == slot.Y && other.Ordinal < slot.Ordinal));
                    if (earlier && Conflicts(candidate, neighbour) && Accepted(other))
                    { candidate.Accepted = false; return false; }
                }
            }
            candidate.Accepted = true; return true;
        }
        if (!ClearEarlier(slot)) { candidate.Accepted = false; return false; }
        foreach (var other in Neighbours(area.LayoutVersion >= 3 ? candidate.Visible : candidate.Bounds, slot.Ordinal))
        {
            if (other == slot) continue;
            var neighbour = CandidateAt(other);
            var earlier = neighbour.Priority < candidate.Priority || neighbour.Priority == candidate.Priority
                && (other.X < slot.X || other.X == slot.X && other.Y < slot.Y);
            // V1 also let rejected neighbours block free positions. V2 resolves the earlier
            // neighbour first; decreasing (ordinal, priority, cell) makes dependencies acyclic.
            // Keep V1 unchanged so opening an existing map never repacks its saved objects.
            if (earlier && Conflicts(candidate, neighbour)
                && (area.LayoutVersion >= 2 ? Accepted(other) : ClearEarlier(other)))
            { candidate.Accepted = false; return false; }
        }
        candidate.Accepted = true; return true;
    }
}

/// <summary>Existing visible stamps in the same layer/band are obstacles, never moved by the fill.</summary>
public sealed class PlacementAreaObstacles(MapDocument document, LayerId layerId, Guid areaId, int sublayer, int layoutVersion = 1,
    PlacementArea? area = null)
{
    public bool Blocks(SceneObject item)
    {
        if (document.FindLayer(layerId) is not ObjectLayer layer) return false;
        // Joining is only between this area's members. Foreign and detached objects remain clear.
        var footprint = area is null || area.LayoutVersion < 3 ? PlacementAreaSampler.Footprint(item, layoutVersion)
            : PlacementAreaSampler.VisibleBounds(area, item);
        foreach (var other in ObjectSpatialIndex.For(layer).Query(footprint))
            if (other.Id != item.Id && other.Kind == SceneObjectKind.Stamp && other.Visible && other.Sublayer == sublayer
                && PlacementAreaOwnership.Owner(document, other.Id)?.Id != areaId
                && PlacementAreaSampler.Overlaps(footprint, area is null ? SceneGeometry.BoundsOf(other) : PlacementAreaSampler.VisibleBounds(area, other))) return true;
        return false;
    }
}
