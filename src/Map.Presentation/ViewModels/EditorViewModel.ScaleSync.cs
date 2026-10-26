using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

/// <summary>One child map of the open map and what a sync would do to it (R28).</summary>
public sealed record ChildSync(Guid MapId, string Title, string Path, MapDocument Child, ScaleSyncPlan Plan);

/// <summary>
/// R28: «Согласовать подкарты» - the rivers and roads of the open map carried into its child maps
/// (preview first: what is added, changed, removed and what stays because it was edited by hand),
/// then written into the child files; and in a hex child, «Гексы из рисунка» - the terrain of each
/// hex taken from the painted parent under it.
/// </summary>
public sealed partial class EditorViewModel
{
    public async Task<IReadOnlyList<ChildSync>> PlanChildSyncAsync()
    {
        if (Atlas is not { } atlas || AtlasPath is not { } manifest) { StatusDetail = "У карты нет подкарт в атласе."; return []; }
        var parent = Session.Document;
        var links = atlas.Links.Where(l => l.Parent == parent.Id.Value && l.Kind == "region").ToList();
        var plans = await Task.Run(() =>
        {
            var result = new List<ChildSync>();
            foreach (var link in links)
            {
                if (atlas.Maps.FirstOrDefault(m => m.Id == link.Child) is not { } map) continue;
                try
                {
                    var path = AtlasStore.ResolveMap(manifest, map);
                    var child = _store.Open(path).Document;
                    result.Add(new ChildSync(map.Id, map.Title, path, child, ScaleSync.Plan(parent, child, link.Transform)));
                }
                catch (Exception ex) when (ex is MapProjectException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                { _log.Error("CHILD_SYNC_OPEN_FAILED", ex); }
            }
            return (IReadOnlyList<ChildSync>)result;
        });
        StatusDetail = plans.Count == 0 ? "Подкарт-областей нет." : $"Подкарт: {plans.Count}, с изменениями: {plans.Count(p => p.Plan.Changes > 0)}.";
        return plans;
    }

    /// <summary>Writes the planned changes into the child files (each child's own undo is its file history).</summary>
    public async Task<int> ApplyChildSyncAsync(IReadOnlyList<ChildSync> plans)
    {
        var written = 0;
        foreach (var plan in plans.Where(p => p.Plan.Changes > 0))
        {
            try
            {
                await Task.Run(() =>
                {
                    var updated = ScaleSync.Apply(plan.Child, plan.Plan);
                    var preview = ThumbnailRenderer.RenderPng(updated);
                    _store.Save(updated, plan.Path, new SaveMetadata(_appVersion, _clock(), 0), preview);
                });
                written++;
            }
            catch (Exception ex) when (ex is MapProjectException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                StatusDetail = $"Не удалось записать «{plan.Title}»: {ex.Message}";
                _log.Error("CHILD_SYNC_SAVE_FAILED", ex);
            }
        }
        if (written > 0) StatusDetail = $"Согласовано подкарт: {written}. Правленное вручную в подкартах оставлено как было.";
        return written;
    }

    /// <summary>In a hex child of a painted map: each hex gets the terrain painted under it in the parent (one undo step).</summary>
    public async Task<bool> HexesFromParentArtAsync()
    {
        if (Atlas is not { } atlas || AtlasPath is not { } manifest
            || atlas.Links.FirstOrDefault(l => l.Child == Session.Document.Id.Value) is not { } link
            || atlas.Maps.FirstOrDefault(m => m.Id == link.Parent) is not { } parentMap)
        { StatusDetail = "Эта карта не подкарта в атласе."; return false; }
        if (SectorLayer is not { } hexes) { StatusDetail = "«Гексы из рисунка» — для гекс-подкарты."; return false; }
        MapDocument parent;
        try { parent = await Task.Run(() => _store.Open(AtlasStore.ResolveMap(manifest, parentMap)).Document); }
        catch (Exception ex) when (ex is MapProjectException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { StatusDetail = "Не удалось открыть родительскую карту."; _log.Error("HEX_FROM_ART_OPEN_FAILED", ex); return false; }
        var after = ScaleSync.HexesFromArt(hexes, parent, link.Transform, IsForestArea);
        if (ReferenceEquals(after, hexes)) { StatusDetail = "Гексы уже совпадают с рисунком."; return false; }
        ExecuteHexLayerEdit("Гексы из рисунка", hexes, after);
        StatusDetail = $"Гексы взяты с «{parentMap.Title}»: вода, горы, холмы, лес и равнины. Ctrl+Z вернёт прежние.";
        return true;
    }

    private bool IsForestArea(PlacementArea area) =>
        _assetCatalog?.FindSet(area.AssetSetId ?? "")?.Placement?.Role is AssetRole.Tree or AssetRole.Spruce or AssetRole.Bush
        || area.Name.Contains("лес", StringComparison.OrdinalIgnoreCase) || area.Name.Contains("forest", StringComparison.OrdinalIgnoreCase);
}
