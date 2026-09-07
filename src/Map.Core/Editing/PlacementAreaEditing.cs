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
        var bits = Random(area, slot);
        // Follow the editor's whole-world-unit placement convention. Stable integer centres
        // avoid introducing another subpixel phase when an export tile changes its origin.
        static double Offset(ulong value) => Math.Floor(Unit(value) * PlacementAreaGeometry.CellSize);
        return new(slot.X * PlacementAreaGeometry.CellSize + Offset(bits),
            slot.Y * PlacementAreaGeometry.CellSize + Offset(Mix(bits)));
    }
    public static bool Includes(PlacementArea area, PlacementAreaSlot slot) => new PlacementAreaSampler(area).Includes(slot);
    public static int CandidatePasses(PlacementArea area) => area.Density == 0 ? 0
        : area.LayoutVersion >= 3 ? PlacementAreaComposition.Passes(area) : (int)Math.Ceiling(area.Density);
    internal static bool IncludesGeometry(PlacementArea area, PlacementAreaSlot slot)
    {
        if (slot.Ordinal < 0 || slot.Ordinal >= CandidatePasses(area) || area.Exclusions.Contains(slot)
            || area.OccupiedSlots.Contains(slot)) return false;
        if (area.LayoutVersion < 3 && slot.Ordinal >= Math.Floor(area.Density)
            && Unit(Mix(Random(area, slot) ^ 0x74814UL)) >= area.Density % 1) return false;
        var point = Anchor(area, slot);
        return PlacementAreaGeometry.Contains(area.Nodes, point.X, point.Y);
    }
    public static IEnumerable<PlacementAreaSlot> Slots(PlacementArea area, int x, int y)
    {
        return new PlacementAreaSampler(area).Slots(x, y);
    }
    public static SceneObject Item(PlacementArea area, PlacementAreaSlot slot) => Item(area, slot,
        area.LayoutVersion >= 3 ? PlacementAreaComposition.At(area, slot) : null);
    internal static SceneObject Item(PlacementArea area, PlacementAreaSlot slot, PlacementAreaComposition.Sample? composition)
    {
        var point = Anchor(area, slot);
        var request = area.Request with { CenterX = point.X, CenterY = point.Y, AreaRadius = 0,
            BaseWidth = area.Request.BaseWidth * (composition?.Scale ?? 1),
            Density = 0, Seed = unchecked((int)Mix(Random(area, slot) ^ 0x1559UL)) };
        var item = StampPlacement.PlanArea(request, area.Variants)[0];
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
        && a.CompositionLength == b.CompositionLength && a.CompositionAngleDegrees == b.CompositionAngleDegrees
        && a.ContentBounds.Count == b.ContentBounds.Count && a.ContentBounds.All(kv => b.ContentBounds.TryGetValue(kv.Key, out var value) && value == kv.Value)
        && a.Variants.SequenceEqual(b.Variants);

    public static IReadOnlyList<PlacementAreaGeometry.CellSpan> Affected(PlacementArea? before, PlacementArea after) =>
        before is null ? PlacementAreaGeometry.Coverage(after.Nodes)
        : SameSettings(before, after) ? PlacementAreaGeometry.ChangedCoverage(before.Nodes, after.Nodes)
        : PlacementAreaGeometry.Merge(PlacementAreaGeometry.Coverage(before.Nodes).Concat(PlacementAreaGeometry.Coverage(after.Nodes)));

    /// <summary>Exact prediction without retaining the full object list. Caller can cancel stale UI work.</summary>
    public static long Count(PlacementArea area, PlacementAreaObstacles? obstacles = null,
        WorkingMemoryBudget? memory = null, CancellationToken cancellationToken = default)
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
                    if (obstacles is null || !obstacles.Blocks(sampler.Item(slot))) count++;
            }
        return count;
    }

    /// <summary>At most 64 concrete stamps, sampled across the changed cells, with bounded candidate work.</summary>
    public static IReadOnlyList<SceneObject> Preview(PlacementArea area, PlacementArea? before = null, int maximum = 64,
        PlacementAreaObstacles? obstacles = null, WorkingMemoryBudget? memory = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        area.Validate();
        var spans = Affected(before, area); var slots = (long)CandidatePasses(area);
        var estimate = PlacementAreaSampler.EstimateBytes(area, spans);
        if (estimate > long.MaxValue) throw new InvalidOperationException("Для предпросмотра такой плотности недостаточно доступной памяти.");
        if (!(memory ?? WorkingMemoryBudget.Shared).TryReserve((long)estimate, out var reservation, out var reason))
            throw new InvalidOperationException(reason);
        using var held = reservation;
        var sampler = new PlacementAreaSampler(area);
        var candidates = checked(spans.Sum(s => (long)s.MaxX - s.MinX + 1) * slots);
        if (candidates == 0) return [];
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
                && (obstacles is null || !obstacles.Blocks(sampler.Item(slot)))) accepted.Add(slot);
        }
        var take = Math.Min(maximum, accepted.Count);
        return Enumerable.Range(0, take).Select(i => sampler.Item(
            accepted[take == 1 ? accepted.Count / 2 : (int)((long)i * (accepted.Count - 1) / (take - 1))])).ToArray();
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
    private MapDocument? _before, _after;
    public string Name { get; }
    public int VisitedCells { get; private set; }
    public int ChangedCells { get; private set; }
    public int ChangedObjects { get; private set; }

    public PlacementAreaCommand(LayerId layerId, Guid id, PlacementArea? requested, string name,
        IReadOnlyList<MapAssetReference>? assets = null, ObjectId? detach = null, bool bake = false)
    { _layerId = layerId; _id = id; _requested = requested; Name = name; _assets = assets ?? []; _detach = detach; _bake = bake; }

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
            var estimatedBytes = (decimal)cellCount * (128 + (decimal)Math.Max(PlacementAreaPlanner.CandidatePasses(next),
                old is null ? 0 : PlacementAreaPlanner.CandidatePasses(old)) * 1664) + spacingEstimate;
            if (estimatedBytes > long.MaxValue) throw new InvalidOperationException("Область с такой плотностью не помещается в доступную память.");
            var estimate = (long)estimatedBytes;
            if (!WorkingMemoryBudget.Shared.TryReserve(estimate, out var reservation, out var reason)) throw new InvalidOperationException(reason);
            using var held = reservation;
            var sampler = new PlacementAreaSampler(next);
            var obstacles = next.LayoutVersion == 0 ? null : new PlacementAreaObstacles(document, layer.Id, _id, next.Sublayer, next.LayoutVersion, next);
            var occupied = next.OccupiedSlots.ToBuilder();
            var cells = (old?.Cells ?? ImmutableDictionary<long, ImmutableArray<PlacementAreaMember>>.Empty).ToBuilder();
            var memberCount = old?.MemberCount ?? 0;
            foreach (var span in spans)
                for (var x = span.MinX; x <= span.MaxX; x++)
                {
                    VisitedCells++;
                    var key = new PlacementAreaSlot(x, span.Y, 0).Cell;
                    var previous = cells.TryGetValue(key, out var stored) ? stored : [];
                    var previousIds = previous.Select(m => m.ObjectId).ToHashSet();
                    var members = ImmutableArray.CreateBuilder<PlacementAreaMember>(); var used = new HashSet<ObjectId>();
                    var replacedBefore = replacements.Count;
                    foreach (var slot in sampler.Slots(x, span.Y))
                    {
                        var item = sampler.Item(slot); var existing = index.FindObject(item.Id);
                        if (obstacles?.Blocks(item) == true) { occupied.Add(slot); continue; }
                        members.Add(new(slot, item.Id)); used.Add(item.Id);
                        if (existing is null) additions.Add(item);
                        else if (!previousIds.Contains(item.Id)) throw new InvalidOperationException("Слот области занят посторонним объектом.");
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
            next = next with { Cells = cells.ToImmutable(), MemberCount = memberCount, OccupiedSlots = occupied.ToImmutable() };
        }
        var updated = document;
        var known = document.Assets.Select(a => a.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var introduced = _assets.Where(a => known.Add(a.Sha256)).ToArray();
        if (next is not null && next.Variants.Any(v => !known.Contains(v.Sha256) || v.MaskSha256 is { } mask && !known.Contains(mask)))
            throw new InvalidOperationException("Не найдены сохранённые изображения области.");
        if (introduced.Length > 0) updated = updated.WithAssets([.. document.Assets, .. introduced]);
        if (replacements.Count > 0) updated = DocumentEditor.ReplaceObjects(updated, replacements);
        if (removals.Count > 0) updated = DocumentEditor.RemovePathObjects(updated, _layerId, removals);
        if (additions.Count > 0) updated = DocumentEditor.AppendObjects(updated, _layerId, additions);
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
