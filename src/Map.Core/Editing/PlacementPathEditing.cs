using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Resources;

namespace Map.Core.Editing;

public static class PlacementPathPlanner
{
    // Half-open segments: k * spacing < length. The joint is emitted only by its outgoing segment.
    public static int SegmentCount(PlacementPath path, int segment)
    {
        var a = path.Nodes[segment]; var b = path.Nodes[segment + 1];
        var length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        if (!double.IsFinite(length) || length / path.Spacing >= int.MaxValue)
            throw new InvalidOperationException("Путь слишком длинный для выбранного шага.");
        return length < .000001 ? 0 : (int)Math.Ceiling(length / path.Spacing);
    }

    public static ObjectId ObjectIdFor(Guid pathId, PlacementPathSlot slot)
    {
        Span<byte> input = stackalloc byte[36];
        pathId.TryWriteBytes(input); slot.NodeId.TryWriteBytes(input[16..]);
        BinaryPrimitives.WriteInt32LittleEndian(input[32..], slot.Ordinal);
        Span<byte> hash = stackalloc byte[32]; SHA256.HashData(input, hash);
        return new ObjectId(new Guid(hash[..16]));
    }

    public static SceneObject Item(PlacementPath path, int segment, int ordinal)
    {
        var a = path.Nodes[segment]; var b = path.Nodes[segment + 1];
        var dx = b.X - a.X; var dy = b.Y - a.Y; var length = Math.Sqrt(dx * dx + dy * dy);
        var slot = new PlacementPathSlot(a.Id, ordinal);
        var id = ObjectIdFor(path.Id, slot);
        Span<byte> bytes = stackalloc byte[16]; a.Id.TryWriteBytes(bytes);
        var seed = unchecked(path.Seed ^ BinaryPrimitives.ReadInt32LittleEndian(bytes)
            ^ BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]) ^ ordinal * 104729);
        var request = path.Request with { CenterX = a.X + dx * ordinal * path.Spacing / length,
            CenterY = a.Y + dy * ordinal * path.Spacing / length, AreaRadius = 0, Density = 0, Seed = seed };
        var item = StampPlacement.PlanArea(request, path.Variants)[0];
        var style = path.Style with
        {
            AssetSha256 = item.Sha256, MaskAssetSha256 = item.MaskSha256,
            ClipMaskEnabled = item.MaskSha256 is not null && path.Style.ClipMaskEnabled,
            ReferenceWidth = item.Width / (path.ScalePercent / 100),
            ReferenceHeight = item.Height / (path.ScalePercent / 100),
            FlipHorizontal = path.Style.FlipHorizontal ^ item.FlipHorizontal,
            FlipVertical = path.Style.FlipVertical ^ item.FlipVertical,
        };
        return new SceneObject(id, SceneObjectKind.Stamp, $"{path.Name} · {ordinal + 1}",
            item.X, item.Y, item.Width, item.Height, (item.RotationDegrees + path.RotationDegrees) % 360,
            "#FFFFFFFF", "#00000000", 0, true, false)
        { Stamp = style, Sublayer = path.Sublayer, ObjectOpacity = path.Opacity };
    }

    public static IReadOnlyList<SceneObject> Preview(PlacementPath path, int maximum = 64, IReadOnlySet<int>? segments = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        var exclusions = path.Exclusions.ToLookup(slot => slot.NodeId, slot => slot.Ordinal);
        var spans = new List<(int Segment, int Available, int[] Excluded)>();
        long total = 0;
        for (var s = 0; s < path.Nodes.Length - 1; s++)
        {
            if (segments is not null && !segments.Contains(s)) continue;
            var count = SegmentCount(path, s);
            var excluded = exclusions[path.Nodes[s].Id].Where(k => k < count).Order().ToArray();
            var available = count - excluded.Length;
            if (available == 0) continue;
            spans.Add((s, available, excluded)); total += available;
        }
        var take = (int)Math.Min(maximum, total);
        var result = new List<SceneObject>(take);
        var span = 0; long start = 0;
        for (var i = 0; i < take; i++)
        {
            // Sample the entire requested row, including its newest end. A fixed prefix made
            // drawing appear to stop after 64 stamps. Only these bounded samples are created.
            var rank = take == 1 ? total - 1 : (long)((decimal)i * (total - 1) / (take - 1));
            while (rank >= start + spans[span].Available) start += spans[span++].Available;
            var ordinal = (int)(rank - start);
            foreach (var excluded in spans[span].Excluded)
            {
                if (excluded > ordinal) break;
                ordinal++;
            }
            result.Add(Item(path, spans[span].Segment, ordinal));
        }
        return result;
    }

    public static long Count(PlacementPath path)
    {
        var counts = path.Nodes.Take(Math.Max(0, path.Nodes.Length - 1))
            .Select((node, index) => (node.Id, Count: SegmentCount(path, index))).ToDictionary(p => p.Id, p => p.Count);
        return counts.Values.Sum(count => (long)count)
            - path.Exclusions.LongCount(slot => counts.TryGetValue(slot.NodeId, out var count) && slot.Ordinal < count);
    }
}

/// <summary>Atomic immutable path transaction; redo reuses the exact committed snapshot.</summary>
public sealed class PlacementPathCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly Guid _pathId;
    private readonly PlacementPath? _requested;
    private readonly IReadOnlyList<MapAssetReference> _assets;
    private readonly ObjectId? _detach;
    private readonly bool _bake;
    private MapDocument? _before, _after;
    public string Name { get; }
    public int RegeneratedSegments { get; private set; }
    public int ChangedObjects { get; private set; }

    public PlacementPathCommand(LayerId layerId, Guid pathId, PlacementPath? requested,
        string name, IReadOnlyList<MapAssetReference>? assets = null, ObjectId? detach = null, bool bake = false)
    { _layerId = layerId; _pathId = pathId; _requested = requested; Name = name;
        _assets = assets ?? []; _detach = detach; _bake = bake; }

    public MapDocument Apply(MapDocument document)
    {
        if (_after is not null) return _after;
        var layer = document.FindLayer(_layerId) as ObjectLayer
            ?? throw new InvalidOperationException("Слой пути не найден.");
        var old = layer.PlacementPaths.FirstOrDefault(p => p.Id == _pathId);
        var current = old ?? _requested ?? throw new InvalidOperationException("Путь не найден.");
        if (!layer.Visible || layer.Locked || !layer.StateFor(current.Sublayer).Visible
            || layer.StateFor(current.Sublayer).Locked
            || _requested is { } requested && layer.StateFor(requested.Sublayer) is not { Visible: true, Locked: false })
            throw new InvalidOperationException("Слой пути скрыт или заблокирован.");
        var index = DocumentRevisionIndex.For(document);
        if (old?.Members.Any(m => index.FindObject(m.ObjectId) is not { Visible: true, Locked: false }) == true)
            throw new InvalidOperationException("В пути есть скрытый или заблокированный штамп.");
        PlacementPath? next = _requested;
        var replacements = new List<SceneObject>(); var additions = new List<SceneObject>();
        var removals = new HashSet<ObjectId>();
        if (_detach is { } detach)
        {
            var member = old?.Members.FirstOrDefault(m => m.ObjectId == detach)
                ?? throw new InvalidOperationException("Выбранный объект не принадлежит пути.");
            next = old! with { Members = [.. old!.Members.Where(m => m.ObjectId != detach)],
                Exclusions = old.Exclusions.Add(member.Slot) };
        }
        else if (_bake) next = null;
        else if (next is null)
        {
            if (old is not null) removals.UnionWith(old.Members.Select(m => m.ObjectId));
        }
        else
        {
            next.Validate();
            if (next.Id != _pathId) throw new InvalidOperationException("Идентификатор пути изменён.");
            var oldSegments = old?.Nodes.Take(old.Nodes.Length - 1)
                .Select((n, i) => (n.Id, Index: i)).ToDictionary(p => p.Id, p => p.Index) ?? [];
            var oldMembers = old?.Members.GroupBy(m => m.Slot.NodeId)
                .ToDictionary(g => g.Key, g => g.ToArray()) ?? [];
            var members = new List<PlacementPathMember>();
            var sameSettings = old is not null && old.Spacing == next.Spacing && old.Seed == next.Seed
                && old.Request == next.Request && old.Style == next.Style && old.RotationDegrees == next.RotationDegrees
                && old.ScalePercent == next.ScalePercent && old.Sublayer == next.Sublayer && old.Opacity == next.Opacity
                && old.Variants.SequenceEqual(next.Variants) && old.Exclusions.SetEquals(next.Exclusions);
            bool Unchanged(int s) => sameSettings && oldSegments.TryGetValue(next.Nodes[s].Id, out var os)
                && old!.Nodes[os] == next.Nodes[s] && old.Nodes[os + 1] == next.Nodes[s + 1];
            long count = 0, changedCount = 0;
            for (var s = 0; s < next.Nodes.Length - 1; s++)
            {
                var size = PlacementPathPlanner.SegmentCount(next, s); count += size;
                if (!Unchanged(s)) changedCount += size;
            }
            if (count > int.MaxValue) throw new InvalidOperationException("Слишком много объектов пути.");
            // The session already admits the ordinary edit reserve; additionally account for
            // this path's membership table and the concrete objects of changed segments only.
            if (!WorkingMemoryBudget.Shared.TryReserve(checked(count * 96 + changedCount * 1536),
                out var reservation, out var reason)) throw new InvalidOperationException(reason);
            using var held = reservation;
            var activeSegments = new HashSet<Guid>();
            for (var s = 0; s < next.Nodes.Length - 1; s++)
            {
                var node = next.Nodes[s]; activeSegments.Add(node.Id);
                var previous = oldMembers.GetValueOrDefault(node.Id) ?? [];
                var previousIds = previous.Select(m => m.ObjectId).ToHashSet();
                if (Unchanged(s))
                { members.AddRange(previous); continue; }
                RegeneratedSegments++;
                var used = new HashSet<ObjectId>();
                var segmentCount = PlacementPathPlanner.SegmentCount(next, s);
                for (var k = 0; k < segmentCount; k++)
                {
                    var slot = new PlacementPathSlot(node.Id, k);
                    if (next.Exclusions.Contains(slot)) continue;
                    var item = PlacementPathPlanner.Item(next, s, k);
                    members.Add(new(slot, item.Id)); used.Add(item.Id);
                    var existing = index.FindObject(item.Id);
                    if (existing is null) additions.Add(item);
                    else if (!previousIds.Contains(item.Id))
                        throw new InvalidOperationException("Идентификатор пути занят посторонним объектом.");
                    else if (existing != item) replacements.Add(item);
                }
                removals.UnionWith(previous.Where(m => !used.Contains(m.ObjectId)).Select(m => m.ObjectId));
            }
            if (old is not null) removals.UnionWith(old.Members
                .Where(m => !activeSegments.Contains(m.Slot.NodeId)).Select(m => m.ObjectId));
            next = next with { Members = [.. members] };
        }
        next?.ValidateMembers();
        ChangedObjects = replacements.Count + additions.Count + removals.Count;
        var updated = document;
        var known = document.Assets.Select(a => a.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var introduced = _assets.Where(a => known.Add(a.Sha256)).ToArray();
        if (introduced.Length > 0) updated = updated.WithAssets([.. document.Assets, .. introduced]);
        if (replacements.Count > 0) updated = DocumentEditor.ReplaceObjects(updated, replacements);
        if (removals.Count > 0) updated = DocumentEditor.RemovePathObjects(updated, _layerId, removals);
        if (additions.Count > 0) updated = DocumentEditor.AppendObjects(updated, _layerId, additions);
        var updatedLayer = (ObjectLayer)updated.FindLayer(_layerId)!;
        var paths = updatedLayer.PlacementPaths.Where(p => p.Id != _pathId).ToList();
        if (next is not null) paths.Insert(old is null ? paths.Count : Math.Min(paths.Count,
            layer.PlacementPaths.ToList().FindIndex(p => p.Id == _pathId)), next);
        updated = WithPaths(updated, updatedLayer, paths);
        _before = document; _after = updated;
        return updated;
    }

    public MapDocument Revert(MapDocument document) => _before
        ?? throw new InvalidOperationException("Команда пути ещё не выполнена.");

    internal static MapDocument WithPaths(MapDocument document, ObjectLayer layer, IReadOnlyList<PlacementPath> paths)
    {
        var layers = document.Layers.ToArray(); var li = Array.FindIndex(layers, l => l.Id == layer.Id);
        var targetLayer = layer with { PlacementPaths = paths.ToImmutableArray() }; layers[li] = targetLayer;
        var target = document.WithRevisedLayers(layers);
        DocumentRevisionIndex.RegisterReplacements(document, target, []); ObjectNameIndex.Share(document, target);
        ObjectLayerChanges.Register(layer, targetLayer, [], []);
        return target;
    }
}

public static class PlacementPathOwnership
{
    public const string DetachFirst = "Объект связан с путём. Сначала нажмите «Отделить выбранный объект» в панели пути.";
    public static PlacementPath? Owner(MapDocument document, ObjectId id) =>
        DocumentEditor.FindLayerOf(document, id)?.PlacementPaths.FirstOrDefault(p => p.Members.Any(m => m.ObjectId == id));

    /// <summary>Generic commands cannot silently mutate, lose or steal an unchanged path's members.</summary>
    public static bool Allows(MapDocument before, MapDocument after, out string reason)
    {
        reason = DetachFirst;
        foreach (var source in before.Layers.OfType<ObjectLayer>().Where(l => !l.PlacementPaths.IsEmpty))
        {
            if (after.FindLayer(source.Id) is not ObjectLayer target) continue; // deleting the owner layer
            if (ReferenceEquals(source.Objects, target.Objects)) continue;
            var index = DocumentRevisionIndex.For(after);
            foreach (var path in source.PlacementPaths)
            {
                var current = target.PlacementPaths.FirstOrDefault(p => p.Id == path.Id);
                if (!ReferenceEquals(path, current)) continue;
                foreach (var member in path.Members)
                    if (!index.TryFind(member.ObjectId, out var location) || location.LayerId != source.Id
                        || location.Object != DocumentEditor.FindObject(before, member.ObjectId)) return false;
            }
        }
        return true;
    }
}
