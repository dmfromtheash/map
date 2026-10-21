using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>Crossings of the selected road over rivers (R06): listed for the panel, kind set with Undo.</summary>
public sealed partial class EditorViewModel
{
    /// <summary>Crossings of the selected branch, or of the whole selected road when no branch is chosen.</summary>
    public IReadOnlyList<RoadCrossing> SelectedRoadCrossings
    {
        get
        {
            if (SelectedRoad is not { } road || RoadLayerOf(road.Id) is not { } layer || layer.Rivers.IsEmpty) return [];
            var all = RoadCrossings.Detect([road], layer.Rivers);
            return SelectedRoadBranch is { } branch ? all.Where(c => c.BranchId == branch.Id).ToArray() : all;
        }
    }

    public static string CrossingKindTitle(RoadCrossingKind kind) => kind switch
    {
        RoadCrossingKind.Bridge => "Мост",
        RoadCrossingKind.Ford => "Брод",
        RoadCrossingKind.Ferry => "Паром",
        _ => "Нет",
    };

    /// <summary>Sets the kind of one crossing; the bridge is the default and stores nothing.</summary>
    public bool SetCrossingKind(RoadCrossing crossing, RoadCrossingKind kind)
    {
        if (SelectedRoad is not { } road || road.Id != crossing.RoadId) { StatusDetail = "Выберите дорогу этой переправы."; return false; }
        if (RoadCrossings.KindOf(road, crossing.BranchId, crossing.ChannelId, crossing.Ordinal) == kind) return true;
        var done = CommitRoad(RoadEditor.SetCrossingKind(road, crossing.BranchId, crossing.ChannelId, crossing.Ordinal, kind), "Переправа: " + CrossingKindTitle(kind));
        if (done) StatusDetail = "Переправа: " + CrossingKindTitle(kind) + ".";
        return done;
    }
}
