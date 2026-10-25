using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Resources;

namespace Map.Core.Editing;

public static class PlacementAreaPlanner
{
    public static ObjectId ObjectIdFor(Guid area, PlacementAreaSlot slot)
    {
        Span<byte> input = stackalloc byte[28]; area.TryWriteBytes(input);
        BinaryPrimitives.WriteInt32LittleEndian(input[16..], slot.X);
        BinaryPrimitives.WriteInt32LittleEndian(input[20..], slot.Y);
        BinaryPrimitives.WriteInt32LittleEndian(input[24..], slot.Ordinal);
        Span<byte> hash = stackalloc byte[32]; SHA256.HashData(input, hash);
        return new(new Guid(hash[..16]));
    }
    internal static ulong Mix(ulong x)
    {
        x += 0x9e3779b97f4a7c15UL; x = (x ^ (x >> 30)) * 0xbf58476d1ce4e5b9UL;
        x = (x ^ (x >> 27)) * 0x94d049bb133111ebUL; return x ^ (x >> 31);
    }
    internal static ulong Random(PlacementArea area, PlacementAreaSlot slot) =>
        Mix(unchecked((ulong)slot.Cell) ^ Mix((uint)slot.Ordinal) ^ Mix((uint)area.Seed));
    internal static double Unit(ulong n) => (n >> 11) * (1d / (1UL << 53));
    public static VectorPoint CompositionOrigin(IReadOnlyList<PlacementPathNode> nodes)
    { PlacementAreaGeometry.Validate(nodes); return PlacementAreaComposition.Origin(nodes); }
    public static PlacementArea InitializeComposition(PlacementArea area)
    {
        var origin = CompositionOrigin(area.Nodes);
        var xx = 0d; var yy = 0d; var xy = 0d;
        foreach (var n in area.Nodes)
        { var x = n.X - origin.X; var y = n.Y - origin.Y; xx += x * x; yy += y * y; xy += x * y; }
        var angle = .5 * Math.Atan2(2 * xy, xx - yy);
        var projections = area.Nodes.Select(n => (n.X - origin.X) * Math.Cos(angle) + (n.Y - origin.Y) * Math.Sin(angle));
        return area with { CompositionOrigin = origin, CompositionAngleDegrees = angle * 180 / Math.PI,
            CompositionLength = projections.Max() - projections.Min() };
    }
    internal static ulong Priority(PlacementArea area, PlacementAreaSlot slot) => Mix(Random(area, slot) ^ 0x7ad614e2UL);
    public static VectorPoint Anchor(PlacementArea area, PlacementAreaSlot slot)
    {
        if (area.LayoutVersion >= 4) return new PlacementAreaLayout(area).At(slot).Point!.Value;
        var bits = Random(area, slot);
        // Follow the editor's whole-world-unit placement convention. Stable integer centres
        // avoid introducing another subpixel phase when an export tile changes its origin.
        static double Offset(ulong value) => Math.Floor(Unit(value) * PlacementAreaGeometry.CellSize);
        return new(slot.X * PlacementAreaGeometry.CellSize + Offset(bits),
            slot.Y * PlacementAreaGeometry.CellSize + Offset(Mix(bits)));
    }
    public static bool Includes(PlacementArea area, PlacementAreaSlot slot) => new PlacementAreaSampler(area).Includes(slot);
    public static int CandidatePasses(PlacementArea area) => area.Density == 0 ? 0
        : area.LayoutVersion >= 4 ? PlacementAreaLayout.Passes(area)
        : area.LayoutVersion >= 3 ? PlacementAreaComposition.Passes(area) : (int)Math.Ceiling(area.Density);
    internal static bool IncludesGeometry(PlacementArea area, PlacementAreaSlot slot, VectorPoint? anchor = null)
    {
        if (slot.Ordinal < 0 || slot.Ordinal >= CandidatePasses(area) || area.Exclusions.Contains(slot)
            || area.OccupiedSlots.Contains(slot)) return false;
        if (area.LayoutVersion < 3 && slot.Ordinal >= Math.Floor(area.Density)
            && Unit(Mix(Random(area, slot) ^ 0x74814UL)) >= area.Density % 1) return false;
        var point = anchor ?? Anchor(area, slot);
        return PlacementAreaGeometry.Contains(area.Nodes, point.X, point.Y);
    }
    public static IEnumerable<PlacementAreaSlot> Slots(PlacementArea area, int x, int y)
    {
        return new PlacementAreaSampler(area).Slots(x, y);
    }
    /// <summary>The finished object of a slot, exactly as the fill stores it.</summary>
    public static SceneObject Item(PlacementArea area, PlacementAreaSlot slot)
    {
        var beauty = PlacementAreaBeautyField.For(area);
        var item = Item(area, slot, area.LayoutVersion >= 4 ? new PlacementAreaLayout(area).At(slot)
            : area.LayoutVersion >= 3 ? PlacementAreaComposition.At(area, slot) : null, beauty);
        return beauty?.Finish(item, slot) ?? item;
    }
    internal static SceneObject Item(PlacementArea area, PlacementAreaSlot slot, PlacementAreaComposition.Sample? composition,
        PlacementAreaBeautyField? beauty = null)
    {
        var point = composition?.Point ?? Anchor(area, slot);
        var request = area.Request with { CenterX = point.X, CenterY = point.Y, AreaRadius = 0,
            BaseWidth = area.Request.BaseWidth * (composition?.Scale ?? 1),
            Density = 0, Seed = unchecked((int)Mix(Random(area, slot) ^ 0x1559UL)) };
        // «Рощи одного вида»: the kind follows patches of the world; every other draw is unchanged.
        var variants = area.Request.RandomizeVariant && beauty?.PatchVariant(point.X, point.Y, slot, area.Variants.Length) is { } kind
            ? [area.Variants[kind]] : (IReadOnlyList<StampVariantSource>)area.Variants;
        var item = StampPlacement.PlanArea(request, variants)[0];
        var style = area.Style with { AssetSha256 = item.Sha256, MaskAssetSha256 = item.MaskSha256,
            ClipMaskEnabled = item.MaskSha256 is not null && area.Style.ClipMaskEnabled,
            ReferenceWidth = item.Width / (area.ScalePercent / 100), ReferenceHeight = item.Height / (area.ScalePercent / 100),
            FlipHorizontal = area.Style.FlipHorizontal ^ item.FlipHorizontal, FlipVertical = area.Style.FlipVertical ^ item.FlipVertical };
        return new SceneObject(ObjectIdFor(area.Id, slot), SceneObjectKind.Stamp,
            $"{area.Name} · {slot.X},{slot.Y}:{slot.Ordinal + 1}", item.X, item.Y, item.Width, item.Height,
            (item.RotationDegrees + area.RotationDegrees) % 360, "#FFFFFFFF", "#00000000", 0, true, false)
        { Stamp = style, Sublayer = area.Sublayer, ObjectOpacity = area.Opacity };
    }
    public static bool SameSettings(PlacementArea a, PlacementArea b) => a.Density == b.Density && a.Seed == b.Seed
        && a.Request == b.Request && a.Style == b.Style && a.RotationDegrees == b.RotationDegrees
        && a.ScalePercent == b.ScalePercent && a.Sublayer == b.Sublayer && a.Opacity == b.Opacity
        && a.Name == b.Name && a.LayoutVersion == b.LayoutVersion && a.CompositionOrigin == b.CompositionOrigin
        && a.SizeVariationPercent == b.SizeVariationPercent && a.Pattern == b.Pattern
        && a.JoinEdges == b.JoinEdges && a.DepthSort == b.DepthSort && Equals(a.Rules, b.Rules)
        && Equals(a.Beauty, b.Beauty) && Equals(a.DensityMask, b.DensityMask)
        && a.CompositionLength == b.CompositionLength && a.CompositionAngleDegrees == b.CompositionAngleDegrees
        && a.ContentBounds.Count == b.ContentBounds.Count && a.ContentBounds.All(kv => b.ContentBounds.TryGetValue(kv.Key, out var value) && value == kv.Value)
        && a.ContentMasks.Count == b.ContentMasks.Count && a.ContentMasks.All(kv => b.ContentMasks.TryGetValue(kv.Key, out var value) && value.Rows.SequenceEqual(kv.Value.Rows))
        && a.Variants.SequenceEqual(b.Variants);

    public static IReadOnlyList<PlacementAreaGeometry.CellSpan> Affected(PlacementArea? before, PlacementArea after)
    {
        if (before is null) return PlacementAreaGeometry.Coverage(after.Nodes);
        if (SameSettings(before, after))
        {
            var changed = PlacementAreaGeometry.ChangedCoverage(before.Nodes, after.Nodes);
            // A soft edge reaches inward from the border: a moved border changes the slots and
            // sizes up to its width away, not only in the strip between the old and new line.
            return after.Beauty is { SoftEdge: true } beauty && after.LayoutVersion >= 3 && changed.Count > 0
                ? PlacementAreaGeometry.Dilate(changed, (int)Math.Ceiling(beauty.EdgeWidth / PlacementAreaGeometry.CellSize) + 1)
                : changed;
        }
        // Only the density brush changed: refill the cells under the stroke, not the whole area.
        if (before.Nodes.SequenceEqual(after.Nodes) && SameSettings(before with { DensityMask = after.DensityMask }, after)
            && DensityMaskChange(before.DensityMask, after.DensityMask) is { } stroke)
            return PlacementAreaGeometry.Clip(PlacementAreaGeometry.Coverage(after.Nodes), stroke);
        return PlacementAreaGeometry.Merge(PlacementAreaGeometry.Coverage(before.Nodes).Concat(PlacementAreaGeometry.Coverage(after.Nodes)));
    }

    /// <summary>
    /// The world rectangle in which two density brushes differ (slightly widened), or null when
    /// they do not differ. Slots are addressed by the cell of their anchor, which is where the
    /// brush is read, so only cells touching this rectangle can change.
    /// </summary>
    internal static WorldBounds? DensityMaskChange(PlacementAreaDensityMask? before, PlacementAreaDensityMask? after)
    {
        if (Equals(before, after)) return null;
        // A first stroke (or «Сбросить»): compare with an untouched raster of the same cells.
        if (before is null || after is null)
            return Painted(before ?? after!);
        if (before.Left != after.Left || before.Top != after.Top
            || before.CellSize != after.CellSize || before.Width != after.Width || before.Height != after.Height)
        {
            var masks = new[] { before, after }.OfType<PlacementAreaDensityMask>().ToArray();
            return new WorldBounds(masks.Min(m => m.Left) - 1, masks.Min(m => m.Top) - 1, masks.Max(m => m.Right) + 1, masks.Max(m => m.Bottom) + 1);
        }
        var a = before.Values; var b = after.Values;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < after.Height; y++)
        {
            var row = y * after.Width;
            for (var x = 0; x < after.Width; x++)
                if (a[row + x] != b[row + x])
                { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; maxY = y; }
        }
        return Cells(after, minX, minY, maxX, maxY);
    }

    private static WorldBounds? Painted(PlacementAreaDensityMask mask)
    {
        var values = mask.Values;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < mask.Height; y++)
            for (var x = 0; x < mask.Width; x++)
                if (values[y * mask.Width + x] != PlacementAreaDensityMask.Neutral)
                { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; maxY = y; }
        return Cells(mask, minX, minY, maxX, maxY);
    }

    private static WorldBounds? Cells(PlacementAreaDensityMask mask, int minX, int minY, int maxX, int maxY) =>
        maxX < 0 ? null
        // Bilinear reading reaches one cell further on each side.
        : new WorldBounds(mask.Left + (minX - 1) * mask.CellSize - 1, mask.Top + (minY - 1) * mask.CellSize - 1,
            mask.Left + (maxX + 2) * mask.CellSize + 1, mask.Top + (maxY + 2) * mask.CellSize + 1);

    /// <summary>
    /// Admission estimate of objects per cell. Version 4 layouts publish only a conservative
    /// upper bound (<see cref="CandidatePasses"/>), often dozens of times the real population, so
    /// a large forest asked for gigabytes and was refused on a machine with 2 GB free. Here the
    /// real positions of up to 64 cells spread over the change are counted, doubled for safety.
    /// </summary>
    public static int EstimatedCellPopulation(PlacementArea area, IReadOnlyList<PlacementAreaGeometry.CellSpan> spans)
    {
        var bound = CandidatePasses(area);
        if (area.LayoutVersion < 4 || bound == 0 || spans.Count == 0) return bound;
        var layout = new PlacementAreaLayout(area);
        var cells = spans.Sum(s => (long)s.MaxX - s.MinX + 1);
        var probes = (int)Math.Min(cells, 64); var most = 0; var si = 0; long start = 0;
        for (var i = 0; i < probes; i++)
        {
            var rank = probes == 1 ? 0 : (long)((decimal)i * (cells - 1) / (probes - 1));
            while (rank >= start + (long)spans[si].MaxX - spans[si].MinX + 1)
                start += (long)spans[si].MaxX - spans[si++].MinX + 1;
            most = Math.Max(most, layout.Cell(spans[si].MinX + (int)(rank - start), spans[si].Y).Count);
        }
        return (int)Math.Min(bound, Math.Max(1L, most * 2L));
    }

    /// <summary>Exact prediction without retaining the full object list. Caller can cancel stale UI work.</summary>
    public static long Count(PlacementArea area, PlacementAreaObstacles? obstacles = null,
        WorkingMemoryBudget? memory = null, CancellationToken cancellationToken = default, PlacementAreaEcology? ecology = null)
    {
        cancellationToken.ThrowIfCancellationRequested(); area.Validate();
        var spans = PlacementAreaGeometry.Coverage(area.Nodes);
        var estimate = PlacementAreaSampler.EstimateBytes(area, spans);
        if (estimate > long.MaxValue) throw new InvalidOperationException("Для подсчёта заполнения недостаточно доступной памяти.");
        if (!(memory ?? WorkingMemoryBudget.Shared).TryReserve((long)estimate, out var reservation, out var reason))
            throw new InvalidOperationException(reason);
        using var held = reservation;
        var sampler = new PlacementAreaSampler(area, cancellationToken); long count = 0;
        foreach (var span in spans)
            for (var x = span.MinX; x <= span.MaxX; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var slot in sampler.Slots(x, span.Y))
                    if ((obstacles is null || !obstacles.Blocks(sampler.Item(slot)))
                        && ecology?.Blocks(slot, sampler.Item(slot)) != true) count++;
            }
        return count;
    }

    /// <summary>At most 64 concrete stamps, sampled across the changed cells, with bounded candidate work.</summary>
    public static IReadOnlyList<SceneObject> Preview(PlacementArea area, PlacementArea? before = null, int maximum = 64,
        PlacementAreaObstacles? obstacles = null, WorkingMemoryBudget? memory = null, CancellationToken cancellationToken = default,
        PlacementAreaEcology? ecology = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        area.Validate();
        var spans = Affected(before, area); var slots = (long)CandidatePasses(area);
        var estimate = PlacementAreaSampler.EstimateBytes(area, spans);
        if (estimate > long.MaxValue) throw new InvalidOperationException("Для предпросмотра такой плотности недостаточно доступной памяти.");
        if (!(memory ?? WorkingMemoryBudget.Shared).TryReserve((long)estimate, out var reservation, out var reason))
            throw new InvalidOperationException(reason);
        using var held = reservation;
        var sampler = new PlacementAreaSampler(area, cancellationToken);
        var candidates = checked(spans.Sum(s => (long)s.MaxX - s.MinX + 1) * slots);
        if (candidates == 0) return [];
        if (area.LayoutVersion >= 4)
        {
            // Structured cells have different populations. Sampling the conservative ordinal
            // bound can miss every real position; sample actual positions across world cells.
            var cells = spans.Sum(s => (long)s.MaxX - s.MinX + 1);
            var cellProbes = (int)Math.Min(cells, maximum * 4L);
            var found = new List<SceneObject>(); var si = 0; long cellStart = 0;
            for (var i = 0; i < cellProbes; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rank = cellProbes == 1 ? 0 : (long)((decimal)i * (cells - 1) / (cellProbes - 1));
                while (rank >= cellStart + (long)spans[si].MaxX - spans[si].MinX + 1)
                    cellStart += (long)spans[si].MaxX - spans[si++].MinX + 1;
                var span = spans[si]; var x = span.MinX + (int)(rank - cellStart);
                foreach (var slot in sampler.PreviewSlots(x, span.Y, 16))
                    if ((before is null || !SameSettings(before, area) || !IncludesGeometry(before, slot))
                        && (obstacles is null || !obstacles.Blocks(sampler.Item(slot)))
                        && ecology?.Blocks(slot, sampler.Item(slot)) != true) found.Add(sampler.Item(slot));
            }
            var count = Math.Min(maximum, found.Count);
            var sample = Enumerable.Range(0, count).Select(i => found[count == 1 ? found.Count / 2
                : (int)((long)i * (found.Count - 1) / (count - 1))]);
            return (PlacementAreaDrawOrder.Uses(area) ? sample.OrderBy(o => PlacementAreaDrawOrder.Key(area, o)) : sample).ToArray();
        }
        var probes = (int)Math.Min(candidates, maximum * 64L);
        var accepted = new List<PlacementAreaSlot>();
        var spanIndex = 0; long start = 0;
        for (var i = 0; i < probes; i++)
        {
            var rank = probes == 1 ? 0 : (long)((decimal)i * (candidates - 1) / (probes - 1));
            while (rank >= start + ((long)spans[spanIndex].MaxX - spans[spanIndex].MinX + 1) * slots)
                start += ((long)spans[spanIndex].MaxX - spans[spanIndex++].MinX + 1) * slots;
            var span = spans[spanIndex]; var local = rank - start;
            var slot = new PlacementAreaSlot(span.MinX + (int)(local / slots), span.Y, (int)(local % slots));
            if (sampler.Includes(slot) && (before is null || !SameSettings(before, area) || !IncludesGeometry(before, slot))
                && (obstacles is null || !obstacles.Blocks(sampler.Item(slot)))
                && ecology?.Blocks(slot, sampler.Item(slot)) != true) accepted.Add(slot);
        }
        var take = Math.Min(maximum, accepted.Count);
        var result = Enumerable.Range(0, take).Select(i => sampler.Item(
            accepted[take == 1 ? accepted.Count / 2 : (int)((long)i * (accepted.Count - 1) / (take - 1))]));
        return (PlacementAreaDrawOrder.Uses(area) ? result.OrderBy(o => PlacementAreaDrawOrder.Key(area, o)) : result).ToArray();
    }
}

/// <summary>One admitted transaction. Persistent cell tables and scene chunks share untouched storage.</summary>
public sealed class PlacementAreaCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly Guid _id;
    private readonly PlacementArea? _requested;
    private readonly IReadOnlyList<MapAssetReference> _assets;
    private readonly ObjectId? _detach;
    private readonly bool _bake;
    private readonly CancellationToken _cancellation;
    private readonly bool? _forestEcology;
    private MapDocument? _before, _after;
    public string Name { get; }
    public int VisitedCells { get; private set; }
    public int ChangedCells { get; private set; }
    public int ChangedObjects { get; private set; }

    public PlacementAreaCommand(LayerId layerId, Guid id, PlacementArea? requested, string name,
        IReadOnlyList<MapAssetReference>? assets = null, ObjectId? detach = null, bool bake = false,
        CancellationToken cancellationToken = default, bool? forestEcology = null)
    {
        _layerId = layerId; _id = id; _requested = requested; Name = name; _assets = assets ?? []; _detach = detach; _bake = bake;
        // Only the first Apply can be cancelled; a completed result is cached and never recomputed.
        _cancellation = cancellationToken;
        _forestEcology = forestEcology;
    }

    public MapDocument Apply(MapDocument document)
    {
        if (_after is not null) return _after;
        var layer = document.FindLayer(_layerId) as ObjectLayer ?? throw new InvalidOperationException("Слой области не найден.");
        var old = layer.PlacementAreas.FirstOrDefault(a => a.Id == _id);
        var current = old ?? _requested ?? throw new InvalidOperationException("Область не найдена.");
        if (!layer.Visible || layer.Locked || layer.StateFor(current.Sublayer) is not { Visible: true, Locked: false }
            || _requested is { } target && layer.StateFor(target.Sublayer) is not { Visible: true, Locked: false })
            throw new InvalidOperationException("Слой или подуровень области скрыт или заблокирован.");
        if (old is null && document.Layers.OfType<ObjectLayer>().Any(l => l.PlacementAreas.Any(a => a.Id == _id)
            || l.PlacementPaths.Any(p => p.Id == _id))) throw new InvalidOperationException("Идентификатор конструкции уже занят.");
        var index = DocumentRevisionIndex.For(document);
        var replacements = new List<SceneObject>(); var additions = new List<SceneObject>(); var removals = new HashSet<ObjectId>();
        PlacementArea? next = _requested;
        var reorder = old is not null && next is not null && (PlacementAreaDrawOrder.Uses(old) || PlacementAreaDrawOrder.Uses(next))
            && !PlacementAreaPlanner.SameSettings(old with { Density = next.Density, DensityMask = next.DensityMask }, next);
        // A soft edge resizes objects near a moved border; in a depth-sorted area such an object
        // moves to its new place in the order instead of keeping a stale one.
        var resort = old is not null && next is not null && PlacementAreaDrawOrder.Uses(old) && PlacementAreaDrawOrder.Uses(next);
        if (_detach is { } detach)
        {
            var member = old?.Members.FirstOrDefault(m => m.ObjectId == detach)
                ?? throw new InvalidOperationException("Объект не принадлежит области.");
            RequireEditable(member.ObjectId);
            var members = old!.Cells[member.Slot.Cell].Remove(member);
            next = old with { Cells = members.IsEmpty ? old.Cells.Remove(member.Slot.Cell) : old.Cells.SetItem(member.Slot.Cell, members),
                MemberCount = old.MemberCount - 1, Exclusions = old.Exclusions.Add(member.Slot) };
        }
        else if (_bake)
        {
            if (old is not null) foreach (var member in old.Members) RequireEditable(member.ObjectId);
            next = null;
        }
        else if (next is null)
        {
            if (old is not null) foreach (var member in old.Members) { RequireEditable(member.ObjectId); removals.Add(member.ObjectId); }
        }
        else
        {
            if (next.Id != _id) throw new InvalidOperationException("Идентификатор области изменён.");
            if (old is not null) next = next with { Exclusions = old.Exclusions };
            next.Validate();
            var spans = PlacementAreaPlanner.Affected(old, next);
            var cellCount = spans.Sum(s => (long)s.MaxX - s.MinX + 1);
            var spacingEstimate = PlacementAreaSampler.EstimateBytes(next, spans);
            if (spacingEstimate > long.MaxValue) throw new InvalidOperationException("Область с такой плотностью не помещается в доступную память.");
            var estimatedBytes = (decimal)cellCount * (128 + (decimal)Math.Max(PlacementAreaPlanner.EstimatedCellPopulation(next, spans),
                old is null ? 0 : PlacementAreaPlanner.EstimatedCellPopulation(old, spans)) * 1664) + spacingEstimate;
            if (estimatedBytes > long.MaxValue) throw new InvalidOperationException("Область с такой плотностью не помещается в доступную память.");
            var estimate = (long)estimatedBytes;
            if (!WorkingMemoryBudget.Shared.TryReserve(estimate, out var reservation, out var reason)) throw new InvalidOperationException(reason);
            using var held = reservation;
            var sampler = new PlacementAreaSampler(next, _cancellation);
            var obstacles = next.LayoutVersion == 0 ? null : new PlacementAreaObstacles(document, layer.Id, _id, next.Sublayer, next.LayoutVersion, next);
            var occupied = next.OccupiedSlots.ToBuilder();
            // R10: rules are a post-filter after spacing, like obstacles, so the slots that stay keep
            // their places. Their verdict is recomputed for every visited cell.
            var rules = next.Rules is { Any: true } activeRules ? new PlacementAreaRuleCheck(document, activeRules) : null;
            var ecology = _forestEcology is { } forest ? new PlacementAreaEcology(document, next, forest) : null;
            var ruleSlots = new HashSet<PlacementAreaSlot>();
            var visited = new HashSet<long>();
            var cells = (old?.Cells ?? ImmutableDictionary<long, ImmutableArray<PlacementAreaMember>>.Empty).ToBuilder();
            var memberCount = old?.MemberCount ?? 0;
            foreach (var span in spans)
                for (var x = span.MinX; x <= span.MaxX; x++)
                {
                    VisitedCells++;
                    var key = new PlacementAreaSlot(x, span.Y, 0).Cell;
                    visited.Add(key);
                    var previous = cells.TryGetValue(key, out var stored) ? stored : [];
                    var previousIds = previous.Select(m => m.ObjectId).ToHashSet();
                    var members = ImmutableArray.CreateBuilder<PlacementAreaMember>(); var used = new HashSet<ObjectId>();
                    var replacedBefore = replacements.Count;
                    foreach (var slot in sampler.Slots(x, span.Y))
                    {
                        var item = sampler.Item(slot); var existing = index.FindObject(item.Id);
                        if (obstacles?.Blocks(item) == true) { occupied.Add(slot); continue; }
                        if (rules?.Blocks(item) == true || ecology?.Blocks(slot, item) == true) { ruleSlots.Add(slot); continue; }
                        members.Add(new(slot, item.Id)); used.Add(item.Id);
                        if (existing is null) additions.Add(item);
                        else if (!previousIds.Contains(item.Id)) throw new InvalidOperationException("Слот области занят посторонним объектом.");
                        else if (reorder || resort && existing != item
                            && PlacementAreaDrawOrder.Key(next, item) != PlacementAreaDrawOrder.Key(old!, existing))
                        { RequireEditable(item.Id); removals.Add(item.Id); additions.Add(item); }
                        else if (existing != item) { RequireEditable(item.Id); replacements.Add(item); }
                    }
                    foreach (var member in previous.Where(m => !used.Contains(m.ObjectId)))
                    { RequireEditable(member.ObjectId); removals.Add(member.ObjectId); }
                    if (!previous.SequenceEqual(members))
                    {
                        memberCount = checked(memberCount + members.Count - previous.Length);
                        if (members.Count == 0) cells.Remove(key); else cells[key] = members.ToImmutable();
                        ChangedCells++;
                    }
                    else if (replacements.Count != replacedBefore) ChangedCells++;
                }
            foreach (var kept in (old ?? next).RuleBlocked)
                if (!visited.Contains(kept.Cell) && (rules is not null || ecology is not null)) ruleSlots.Add(kept);
            next = next with { Cells = cells.ToImmutable(), MemberCount = memberCount, OccupiedSlots = occupied.ToImmutable(),
                RuleSlots = ruleSlots.Count == 0 ? null : [.. ruleSlots] };
        }
        var updated = document;
        var known = document.Assets.Select(a => a.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var introduced = _assets.Where(a => known.Add(a.Sha256)).ToArray();
        if (next is not null && next.Variants.Any(v => !known.Contains(v.Sha256) || v.MaskSha256 is { } mask && !known.Contains(mask)))
            throw new InvalidOperationException("Не найдены сохранённые изображения области.");
        if (introduced.Length > 0) updated = updated.WithAssets([.. document.Assets, .. introduced]);
        if (replacements.Count > 0) updated = DocumentEditor.ReplaceObjects(updated, replacements);
        if (removals.Count > 0) updated = DocumentEditor.RemovePathObjects(updated, _layerId, removals);
        if (PlacementAreaDrawOrder.Uses(next))
        {
            var ordered = old is null || reorder || !PlacementAreaDrawOrder.Uses(old)
                ? ImmutableSortedSet<PlacementAreaDrawOrder.Entry>.Empty : PlacementAreaDrawOrder.For(old);
            if (old is not null && !reorder)
                ordered = ordered.Except(removals.Concat(_detach is { } d ? [d] : []).Select(id => PlacementAreaDrawOrder.Key(old, index.FindObject(id)!)));
            if (additions.Count > 0) updated = PlacementAreaDrawOrder.Insert(updated, _layerId, next!, ordered, additions);
            PlacementAreaDrawOrder.Register(next!, ordered.Union(additions.Select(o => PlacementAreaDrawOrder.Key(next!, o))));
        }
        else if (additions.Count > 0) updated = DocumentEditor.AppendObjects(updated, _layerId, additions);
        var updatedLayer = (ObjectLayer)updated.FindLayer(_layerId)!;
        var areas = updatedLayer.PlacementAreas;
        var position = old is null ? -1 : areas.IndexOf(old);
        areas = next is null ? position < 0 ? areas : areas.RemoveAt(position)
            : position < 0 ? areas.Add(next) : areas.SetItem(position, next);
        var layers = updated.Layers.ToArray(); var li = Array.FindIndex(layers, l => l.Id == _layerId);
        var revisedLayer = updatedLayer with { PlacementAreas = areas }; layers[li] = revisedLayer;
        var result = updated.WithRevisedLayers(layers);
        DocumentRevisionIndex.RegisterReplacements(updated, result, []); ObjectNameIndex.Share(updated, result);
        ObjectLayerChanges.Register(updatedLayer, revisedLayer, [], []);
        ChangedObjects = replacements.Count + additions.Count + removals.Count;
        _before = document; _after = result; return result;

        void RequireEditable(ObjectId id)
        {
            if (!index.TryFind(id, out var location) || location.LayerId != _layerId
                || location.Object is not { Visible: true, Locked: false })
                throw new InvalidOperationException("Объект области отсутствует, скрыт или заблокирован.");
        }
    }
    public MapDocument Revert(MapDocument document) => _before ?? throw new InvalidOperationException("Команда области ещё не выполнена.");
}

public static class PlacementOwnership
{
    public const string DetachFirst = "Объект связан с путём или областью. Сначала нажмите «Отделить выбранный объект» в панели конструкции.";
    public static object? Owner(MapDocument document, ObjectId id) =>
        (object?)PlacementAreaOwnership.Owner(document, id) ?? PlacementPathOwnership.Owner(document, id);
}

public static class PlacementAreaOwnership
{
    public const string DetachFirst = "Объект связан с областью. Сначала нажмите «Отделить выбранный объект» в панели области.";
    public static PlacementArea? Owner(MapDocument document, ObjectId id)
    {
        var index = DocumentRevisionIndex.For(document);
        if (!index.TryFind(id, out var location)) return null;
        var item = location.Object;
        var key = new PlacementAreaSlot(PlacementAreaGeometry.Cell(item.X + item.Width / 2),
            PlacementAreaGeometry.Cell(item.Y + item.Height / 2), 0).Cell;
        return (document.FindLayer(location.LayerId) as ObjectLayer)?.PlacementAreas
            .FirstOrDefault(a => a.Cells.TryGetValue(key, out var members) && members.Any(m => m.ObjectId == id));
    }
    public static bool Allows(MapDocument before, MapDocument after, out string reason)
    {
        reason = DetachFirst;
        foreach (var source in before.Layers.OfType<ObjectLayer>().Where(l => !l.PlacementAreas.IsEmpty))
        {
            if (after.FindLayer(source.Id) is not ObjectLayer target || ReferenceEquals(source.Objects, target.Objects)) continue;
            var index = DocumentRevisionIndex.For(after); var previous = DocumentRevisionIndex.For(before);
            foreach (var area in source.PlacementAreas)
            {
                if (!ReferenceEquals(area, target.PlacementAreas.FirstOrDefault(a => a.Id == area.Id))) continue;
                foreach (var member in area.Members)
                    if (!index.TryFind(member.ObjectId, out var location) || location.LayerId != source.Id
                        || location.Object != previous.FindObject(member.ObjectId)) return false;
            }
        }
        return true;
    }
}
