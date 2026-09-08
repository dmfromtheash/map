using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>
/// Land as a selectable piece of the map, and the level of water and land among the terrain
/// (<see cref="TerrainOrder"/>). A click on land selects that piece the way a click on a stamp
/// selects the stamp; its level, order, opacity and visibility are then the strip's business,
/// exactly as for an object. Water gets the same level and order commands.
/// </summary>
public sealed partial class EditorViewModel
{
    private LayerId? _selectedLandId;

    /// <summary>The selected piece of land, or null. At most one piece is selected at a time.</summary>
    public LandLayer? SelectedLand =>
        _selectedLandId is { } id ? Session.Document.FindLayer(id) as LandLayer : null;

    public int LandSelectionCount => SelectedLand is null ? 0 : 1;

    /// <summary>The land the strip and the level commands act on: the selected piece, else the active land layer.</summary>
    public LandLayer? LandTarget => SelectedLand ?? ActiveLayer as LandLayer;

    public int? LandTargetLevel => LandTarget?.Level;

    /// <summary>World bounds of the selected land's tiles, for the frame on the canvas.</summary>
    public WorldBounds? LandSelectionFrame => SelectedLand is { } land ? TerrainOrder.Bounds(land) : null;

    /// <summary>Selects the topmost visible piece of land under the point; false over the sea.</summary>
    public bool SelectLandAt(double worldX, double worldY)
    {
        if (TerrainOrder.LandAt(Session.Document, worldX, worldY) is not { } land) return false;
        SelectLand(land.Id);
        return true;
    }

    public void SelectLand(LayerId id)
    {
        if (Session.Document.FindLayer(id) is not LandLayer land) return;
        if (Session.SelectedObjectIds.Count > 0)
        {
            Session.ClearSelection();
            AfterSelectionChange();
        }
        ClearWaterSelection();
        _selectedLandId = id;
        _activeLayerId = id;
        StatusDetail = $"Земля «{land.Name}» · уровень {FormatSublayer(land.Level)}"
            + (land.Locked ? " · заблокирована" : "");
        RaiseLandState();
        Raise(nameof(ActiveLayerId)); Raise(nameof(ActiveLayer)); Raise(nameof(ActiveLayerSummary));
    }

    public void ClearLandSelection()
    {
        if (_selectedLandId is null) return;
        _selectedLandId = null;
        RaiseLandState();
    }

    private void RaiseLandState()
    {
        Raise(nameof(SelectedLand)); Raise(nameof(LandSelectionCount)); Raise(nameof(LandTarget));
        Raise(nameof(LandTargetLevel)); Raise(nameof(LandSelectionFrame));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    /// <summary>Gives the target piece of land one level, as one undoable command.</summary>
    public bool SetLandLevel(int level)
    {
        if (LandTarget is not { } land) return false;
        if (land.Locked) { StatusDetail = "Слой земли заблокирован: снимите блокировку, чтобы менять уровень."; return false; }
        var bounded = TerrainOrder.Clamp(level);
        if (bounded == land.Level) return false;
        Session.Execute(UpdateLayerCommand.SetLandLevel(land, bounded));
        AfterEdit();
        RaiseLandState();
        StatusDetail = $"Земля «{land.Name}» · уровень {FormatSublayer(bounded)}";
        return true;
    }

    public bool ShiftLandLevel(int delta) =>
        LandTarget is { } land && delta != 0 && SetLandLevel(land.Level + Math.Sign(delta));

    // ------------------------------------------------------------ water level and order

    /// <summary>The level every selected water system shares, or null when they disagree or none is selected.</summary>
    public int? WaterSelectionLevelValue
    {
        get
        {
            var waters = SelectedWaters;
            if (waters.Count == 0) return null;
            var first = waters[0].Level;
            return waters.All(river => river.Level == first) ? first : null;
        }
    }

    /// <summary>Gives every selected water system one level, as one history entry.</summary>
    public bool SetSelectedWaterLevel(int level)
    {
        var bounded = TerrainOrder.Clamp(level);
        return EditSelectedWaterLevels(_ => bounded, "Уровень воды");
    }

    /// <summary>Moves every selected water system by the same number of levels, keeping their differences.</summary>
    public bool ShiftSelectedWaterLevel(int delta) =>
        delta != 0 && EditSelectedWaterLevels(river => TerrainOrder.Clamp(river.Level + Math.Sign(delta)), "Уровень воды");

    private bool EditSelectedWaterLevels(Func<River, int> level, string name)
    {
        var waters = SelectedWaters;
        if (waters.Count == 0) return false;
        if (waters.FirstOrDefault(river => river.Locked) is { } locked)
        { StatusDetail = $"«{locked.Name}» заблокирована: снимите блокировку, чтобы менять уровень."; return false; }
        var steps = new List<IEditCommand>();
        foreach (var river in waters)
        {
            if (RiverLayerOf(river.Id) is not { } layer) continue;
            var next = level(river);
            if (next == river.Level) continue;
            steps.Add(new RiverCommand(layer.Id, river.Id, river with { Level = next }, name));
        }
        if (steps.Count == 0) return false;
        var revision = Session.Revision;
        Session.Execute(steps.Count == 1 ? steps[0] : new CompositeCommand(name, steps));
        if (Session.Revision == revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; return false; }
        AfterEdit(geometryOnly: true); RaiseWaterState();
        Raise(nameof(WaterSelectionLevelValue));
        StatusDetail = WaterSelectionLevelValue is { } shared
            ? $"Вода · уровень {FormatSublayer(shared)}"
            : "Уровень воды изменён.";
        return true;
    }

    /// <summary>
    /// Выше / Ниже / Наверх / Вниз for water: the order of the selected systems among the water
    /// of their own layer, which decides what covers what at one level. One history entry.
    /// </summary>
    public bool ReorderSelectedWater(int direction, bool toExtreme = false)
    {
        if (direction == 0) return false;
        var selected = SelectedWaters.Select(river => river.Id).ToHashSet();
        if (selected.Count == 0) return false;
        var steps = new List<IEditCommand>();
        foreach (var layer in Session.Document.Layers.OfType<ObjectLayer>())
        {
            if (!layer.Rivers.Any(river => selected.Contains(river.Id))) continue;
            if (!layer.Visible || layer.Locked) { StatusDetail = "Слой воды скрыт или заблокирован."; return false; }
            var order = layer.Rivers.Select(river => river.Id).ToList();
            var up = direction > 0;
            if (toExtreme)
            {
                var moved = order.Where(selected.Contains).ToList();
                var rest = order.Where(id => !selected.Contains(id)).ToList();
                order = up ? [.. rest, .. moved] : [.. moved, .. rest];
            }
            else if (up)
            {
                for (var index = order.Count - 2; index >= 0; index--)
                    if (selected.Contains(order[index]) && !selected.Contains(order[index + 1]))
                        (order[index], order[index + 1]) = (order[index + 1], order[index]);
            }
            else
            {
                for (var index = 1; index < order.Count; index++)
                    if (selected.Contains(order[index]) && !selected.Contains(order[index - 1]))
                        (order[index], order[index - 1]) = (order[index - 1], order[index]);
            }
            if (!order.SequenceEqual(layer.Rivers.Select(river => river.Id)))
                steps.Add(new ReorderRiversCommand(layer.Id, order, "Порядок воды"));
        }
        if (steps.Count == 0) { StatusDetail = direction > 0 ? "Вода уже сверху." : "Вода уже снизу."; return false; }
        Session.Execute(steps.Count == 1 ? steps[0] : new CompositeCommand("Порядок воды", steps));
        AfterEdit(geometryOnly: true); RaiseWaterState();
        return true;
    }
}
