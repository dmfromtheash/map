using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

/// <summary>Concrete rows affected by selection changes. Union preserves intermediate changes
/// when several selections are coalesced before the dispatcher runs.</summary>
public sealed record SelectionRefreshDelta(
    ImmutableHashSet<ObjectId> Objects, ImmutableHashSet<string> AssetKeys,
    ImmutableHashSet<Guid> Groups, ImmutableHashSet<Guid> Water,
    ImmutableHashSet<Guid> Roads, ImmutableHashSet<LayerId> Land)
{
    public static SelectionRefreshDelta Empty { get; } = new([], [], [], [], [], []);
    public SelectionRefreshDelta Merge(SelectionRefreshDelta other) => new(
        Objects.Union(other.Objects), AssetKeys.Union(other.AssetKeys), Groups.Union(other.Groups),
        Water.Union(other.Water), Roads.Union(other.Roads), Land.Union(other.Land));

    public bool Affects(ObjectTreeRow row) => row.Kind switch
    {
        ObjectTreeRowKind.AssetKind => row.AssetKey is { } key && AssetKeys.Contains(key),
        ObjectTreeRowKind.Group => row.GroupId is { } group && Groups.Contains(group),
        ObjectTreeRowKind.Water => row.WaterId is { } water && Water.Contains(water),
        ObjectTreeRowKind.Road => row.RoadId is { } road && Roads.Contains(road),
        ObjectTreeRowKind.Land => row.LandId is { } land && Land.Contains(land),
        _ => row.Object is { } item && Objects.Contains(item.Id),
    };

    public static SelectionRefreshDelta Capture(EditorViewModel editor)
    {
        var objects = editor.Session.SelectedObjects;
        return new(editor.Session.SelectedObjectIds.ToImmutableHashSet(),
            ObjectTreeRows.SelectedAssetKeys(editor.Session.Document, editor.Session.SelectedObjectIds).ToImmutableHashSet(),
            objects.Where(item => item.GroupId.HasValue).Select(item => item.GroupId!.Value).ToImmutableHashSet(),
            editor.WaterSelectionIds.ToImmutableHashSet(), editor.RoadSelectionIds.ToImmutableHashSet(),
            editor.SelectedLand is { } land ? [land.Id] : []);
    }
}
