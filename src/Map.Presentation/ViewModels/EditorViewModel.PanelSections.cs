using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

/// <summary>
/// The working sections of the right panel: land, rivers and lakes, placed assets, roads, the
/// other objects, and the layer stack. A section is a view over the same map - it decides which
/// rows the panel lists and which kind Ctrl+A addresses. Switching sections never touches the
/// document, the history, the layers or what the canvas draws.
/// </summary>
public sealed partial class EditorViewModel
{
    // The layer stack opens first: it is the view the former first tab showed, and the one an
    // opened map is read from before any content is chosen.
    private PanelSection _panelSection = PanelSection.Layers;
    private ObjectTypeFilter _otherFilter = ObjectTypeFilter.NotStamps;

    public PanelSection PanelSection
    {
        get => _panelSection;
        set => ShowPanelSection(value);
    }

    /// <summary>The sub-filter the "Прочее" section last used, kept while other sections are open.</summary>
    public ObjectTypeFilter OtherSectionFilter => _otherFilter;

    /// <summary>
    /// Opens a section: the row filter follows it, a search typed for another section is
    /// dropped (it would otherwise hide this section's rows without a visible reason), and an
    /// open group is closed, since its members belong to the objects sections only.
    /// </summary>
    public void ShowPanelSection(PanelSection section)
    {
        var changed = _panelSection != section;
        _panelSection = section;
        if (changed) _openAssetKey = null;
        if (changed && _objectSearchText.Length > 0) ObjectSearchText = "";
        if (changed && _openGroupId is not null && section is not (PanelSection.Assets or PanelSection.Other))
        {
            _openGroupId = null;
            Raise(nameof(ObjectTree));
        }
        var filter = ObjectTreeRows.FilterOf(section, _otherFilter);
        if (_objectTypeFilter != filter) { _objectTypeFilter = filter; Raise(nameof(ObjectTypeFilter)); }
        if (changed) Raise(nameof(PanelSection));
    }

    /// <summary>The section that lists what is currently selected, or null when nothing is.</summary>
    public PanelSection? SectionForSelection()
    {
        if (Session.SelectedObjectIds.Count > 0)
        {
            var objects = Session.SelectedObjects;
            return objects.Count > 0 && objects.All(o => o.Kind == SceneObjectKind.Stamp)
                ? PanelSection.Assets
                : objects.Count > 0 && objects.All(o => o.Kind != SceneObjectKind.Stamp)
                    ? PanelSection.Other
                    : _panelSection is PanelSection.Assets or PanelSection.Other ? _panelSection : PanelSection.Assets;
        }
        if (_waterSelection.Count > 0) return PanelSection.Water;
        if (_roadSelection.Count > 0) return PanelSection.Roads;
        if (_selectedLandId is not null) return PanelSection.Land;
        return null;
    }

    /// <summary>
    /// Ctrl+A in a terrain section: every visible, unlocked river/lake, road or piece of land the
    /// section lists (search included) - never the objects, water or roads of another section.
    /// Land holds one selected piece at a time, so the topmost listed piece is chosen.
    /// </summary>
    private bool SelectAllTerrainByFilter()
    {
        var rows = ObjectTree;
        switch (_objectTypeFilter)
        {
            case ObjectTypeFilter.Water:
            {
                var ids = rows.Where(r => r.Kind == ObjectTreeRowKind.Water && r.WaterVisible && !r.WaterLocked && r.WaterId is not null)
                    .Select(r => r.WaterId!.Value).ToList();
                ClearWaterSelection(); ClearRoadSelection(); ClearLandSelection();
                if (Session.SelectedObjectIds.Count > 0) Session.ClearSelection();
                for (var i = 0; i < ids.Count; i++) SelectWater(ids[i], additive: i > 0);
                StatusDetail = ids.Count == 0 ? "В этом разделе нет видимой незаблокированной воды." : $"Выбрано водных систем: {ids.Count}.";
                return true;
            }
            case ObjectTypeFilter.Roads:
            {
                var ids = rows.Where(r => r.Kind == ObjectTreeRowKind.Road && r.WaterVisible && !r.WaterLocked && r.RoadId is not null)
                    .Select(r => r.RoadId!.Value).ToList();
                ClearRoadSelection(); ClearWaterSelection(); ClearLandSelection();
                if (Session.SelectedObjectIds.Count > 0) Session.ClearSelection();
                for (var i = 0; i < ids.Count; i++) SelectRoadOnly(ids[i], additive: i > 0);
                StatusDetail = ids.Count == 0 ? "В этом разделе нет видимых незаблокированных дорог." : $"Выбрано дорог: {ids.Count}.";
                return true;
            }
            case ObjectTypeFilter.Land:
            {
                var first = rows.FirstOrDefault(r => r.Kind == ObjectTreeRowKind.Land && r.WaterVisible && !r.WaterLocked && r.LandId is not null);
                ClearWaterSelection(); ClearRoadSelection(); ClearLandSelection();
                if (Session.SelectedObjectIds.Count > 0) Session.ClearSelection();
                if (first?.LandId is { } landId) SelectLand(landId);
                else StatusDetail = "В этом разделе нет видимой незаблокированной земли.";
                return true;
            }
            default:
                return false;
        }
    }
}
