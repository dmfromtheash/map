using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// A deliberate manual selection edit owns both detachment and the edit in one history entry.
/// Only selected members leave their areas; their slots stay excluded after future regeneration.
/// The ordinary ownership guards still reject unrelated commands and edits to path members.
/// </summary>
public sealed class ManualAreaSelectionCommand(IEditCommand edit, IReadOnlyCollection<ObjectId> ids) : IEditCommand
{
    private readonly ObjectId[] _ids = ids.Distinct().ToArray();
    private MapDocument? _before, _after;
    public string Name => edit.Name;

    public long? MeasuredWorkingBytes => edit.MeasuredWorkingBytes;
    public int DetachedCount { get; private set; }

    public MapDocument Apply(MapDocument document)
    {
        if (_after is not null) return _after;
        var index = DocumentRevisionIndex.For(document);
        var selected = new Dictionary<LayerId, List<SceneObject>>();
        foreach (var id in _ids)
        {
            if (!index.TryFind(id, out var location)
                || location.Object is not { Visible: true, Locked: false } item
                || document.FindLayer(location.LayerId) is not ObjectLayer { Visible: true, Locked: false } layer
                || layer.StateFor(item.Sublayer) is not { Visible: true, Locked: false })
                throw new InvalidOperationException("В выделении есть скрытые или заблокированные объекты либо слои — изменение отклонено целиком.");
            if (!selected.TryGetValue(layer.Id, out var objects)) selected[layer.Id] = objects = [];
            objects.Add(item);
        }

        var layers = document.Layers.ToArray();
        var changedLayers = new List<(ObjectLayer Before, ObjectLayer After)>();
        var detachedCount = 0;
        for (var li = 0; li < layers.Length; li++)
        {
            if (layers[li] is not ObjectLayer layer || layer.PlacementAreas.IsEmpty
                || !selected.TryGetValue(layer.Id, out var objects)) continue;
            var selectedIds = objects.Select(o => o.Id).ToHashSet();
            var keys = objects.Select(o => new PlacementAreaSlot(PlacementAreaGeometry.Cell(o.X + o.Width / 2),
                PlacementAreaGeometry.Cell(o.Y + o.Height / 2), 0).Cell).ToHashSet();
            var areas = layer.PlacementAreas.ToBuilder();
            var changed = false;
            for (var ai = 0; ai < areas.Count; ai++)
            {
                var area = areas[ai];
                var removed = keys.Where(area.Cells.ContainsKey).SelectMany(k => area.Cells[k])
                    .Where(m => selectedIds.Contains(m.ObjectId)).ToArray();
                if (removed.Length == 0) continue;
                var cells = area.Cells.ToBuilder();
                foreach (var group in removed.GroupBy(m => m.Slot.Cell))
                {
                    var remaining = cells[group.Key].Where(m => !selectedIds.Contains(m.ObjectId)).ToImmutableArray();
                    if (remaining.IsEmpty) cells.Remove(group.Key); else cells[group.Key] = remaining;
                }
                areas[ai] = area with { Cells = cells.ToImmutable(), MemberCount = area.MemberCount - removed.Length,
                    Exclusions = area.Exclusions.Union(removed.Select(m => m.Slot)) };
                PlacementAreaDrawOrder.Detach(area, areas[ai], removed.Select(m => index.FindObject(m.ObjectId)!));
                detachedCount += removed.Length;
                changed = true;
            }
            if (!changed) continue;
            var revised = layer with { PlacementAreas = areas.ToImmutable() };
            layers[li] = revised;
            changedLayers.Add((layer, revised));
        }

        var detached = document;
        if (changedLayers.Count > 0)
        {
            detached = document.WithRevisedLayers(layers);
            DocumentRevisionIndex.RegisterReplacements(document, detached, []);
            ObjectNameIndex.Share(document, detached);
            foreach (var pair in changedLayers) ObjectLayerChanges.Register(pair.Before, pair.After, [], []);
        }
        var result = edit.Apply(detached);
        _before = document;
        _after = result;
        DetachedCount = detachedCount;
        return result;
    }

    public MapDocument Revert(MapDocument document) => _before
        ?? throw new InvalidOperationException("Ручное изменение выделения ещё не выполнено.");
}
