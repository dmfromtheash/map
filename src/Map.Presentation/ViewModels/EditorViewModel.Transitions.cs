using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R29: stairs, cave mouths, shafts and portals. «Новый переход» arms a click for the first end;
/// «Второй конец здесь» arms a click on this map (another hex level shown, too), or the second end
/// goes to another map of the atlas. Every change is one undo step; the check lists ends that lead
/// nowhere. The maps of the atlas carry their level of the world: sky, surface, underdark.
/// </summary>
public sealed partial class EditorViewModel
{
    private enum TransitionPickMode { None, First, Second }

    private TransitionPickMode _transitionPick;
    private TransitionKind _transitionPickKind;

    public ImmutableArray<MapTransition> Transitions => Session.Document.Transitions;

    public Guid? SelectedTransitionId { get; set; }

    public MapTransition? SelectedTransition => Transitions.FirstOrDefault(t => t.Id == SelectedTransitionId);

    public bool TransitionPickArmed => _transitionPick != TransitionPickMode.None;

    private void SetTransitions(ImmutableArray<MapTransition> after, string name)
    {
        try { Session.Execute(new SetTransitionsCommand(Transitions, after, name)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { StatusDetail = ex.Message; return; }
        Raise(nameof(Transitions));
        AfterEdit();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    /// <summary>The next click on the canvas places the first end of a new transition of this kind.</summary>
    public void StartNewTransition(TransitionKind kind)
    {
        _transitionPick = TransitionPickMode.First;
        _transitionPickKind = kind;
        StatusDetail = $"Щёлкните на карте, где {MapTransition.KindTitle(kind).ToLowerInvariant()}: первый конец перехода. Esc — отмена.";
        Raise(nameof(TransitionPickArmed));
    }

    /// <summary>The next click on the canvas places the second end of the selected transition on this map (at the hex level shown).</summary>
    public void PickTransitionTarget()
    {
        if (SelectedTransition is null) return;
        _transitionPick = TransitionPickMode.Second;
        StatusDetail = "Щёлкните на карте второй конец перехода" + (HexLevelShown is { } level ? $" (уровень {level})." : ".") + " Esc — отмена.";
        Raise(nameof(TransitionPickArmed));
    }

    public void CancelTransitionPick()
    {
        if (_transitionPick == TransitionPickMode.None) return;
        _transitionPick = TransitionPickMode.None;
        StatusDetail = "";
        Raise(nameof(TransitionPickArmed));
    }

    /// <summary>A click while a transition end is armed: the end goes there.</summary>
    public bool PressTransitionPick(double x, double y)
    {
        if (_transitionPick == TransitionPickMode.None) return false;
        var mode = _transitionPick;
        _transitionPick = TransitionPickMode.None;
        Raise(nameof(TransitionPickArmed));
        var level = Session.Document.Layers.OfType<HexLayer>().Any() ? HexLevelShown : null;
        if (mode == TransitionPickMode.First)
        {
            var transition = new MapTransition
            {
                Name = $"{MapTransition.KindTitle(_transitionPickKind)} {Transitions.Length + 1}",
                Kind = _transitionPickKind,
                From = new TransitionEnd(null, Math.Round(x, 1), Math.Round(y, 1), level),
            };
            SetTransitions(Transitions.Add(transition), "Новый переход");
            SelectedTransitionId = transition.Id;
            Raise(nameof(SelectedTransitionId));
            StatusDetail = $"«{transition.Name}» поставлен. Укажите второй конец: на этой карте или на другой карте атласа.";
            return true;
        }

        if (SelectedTransition is not { } selected) return true;
        SetTransitions(Transitions.Replace(selected, selected with { To = new TransitionEnd(null, Math.Round(x, 1), Math.Round(y, 1), level) }), "Второй конец перехода");
        StatusDetail = $"«{selected.Name}» ведёт сюда" + (level is { } l ? $", уровень {l}." : ".");
        return true;
    }

    /// <summary>The second end on another map of the atlas, at the given point of that map.</summary>
    public void SetTransitionTargetMap(Guid id, Guid mapId, double x, double y)
    {
        if (Transitions.FirstOrDefault(t => t.Id == id) is not { } before) return;
        SetTransitions(Transitions.Replace(before, before with { To = new TransitionEnd(mapId == Session.Document.Id.Value ? null : mapId, x, y) }), "Второй конец на другой карте");
    }

    public void EditTransition(Guid id, string name, TransitionKind kind, TransitionDirection direction, double costMinutes)
    {
        if (Transitions.FirstOrDefault(t => t.Id == id) is not { } before || string.IsNullOrWhiteSpace(name) || !double.IsFinite(costMinutes)) return;
        var after = before with
        {
            Name = name.Trim().Length > MapTransition.MaxNameLength ? name.Trim()[..MapTransition.MaxNameLength] : name.Trim(),
            Kind = kind, Direction = direction, CostMinutes = Math.Clamp(Math.Round(costMinutes, 1), 0, 1_000_000),
        };
        if (after == before) return;
        SetTransitions(Transitions.Replace(before, after), "Изменить переход");
    }

    public void DeleteTransition(Guid id)
    {
        if (Transitions.FirstOrDefault(t => t.Id == id) is not { } before) return;
        SetTransitions(Transitions.Remove(before), "Удалить переход");
        if (SelectedTransitionId == id) { SelectedTransitionId = null; Raise(nameof(SelectedTransitionId)); }
    }

    /// <summary>Shows an end: on this map the view goes there (and to its hex level); another map of the atlas opens.</summary>
    public void GoToTransitionEnd(Guid id, bool second)
    {
        if (Transitions.FirstOrDefault(t => t.Id == id) is not { } transition) return;
        var end = second ? transition.To : transition.From;
        if (end is null) { StatusDetail = "Второй конец не задан."; return; }
        if (end.MapId is { } map && map != Session.Document.Id.Value) { OpenAtlasMap(map); return; }
        if (end.HexLevel is { } level && Session.Document.Layers.OfType<HexLayer>().Any(l => l.Level == level)) HexLevel = level;
        var viewport = Session.Viewport;
        viewport.Set(end.X - (viewport.ViewWidth / 2 / viewport.Scale), end.Y - (viewport.ViewHeight / 2 / viewport.Scale), viewport.Scale);
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    /// <summary>Where the second end leads, in words.</summary>
    public string TransitionTargetTitle(MapTransition transition) => transition.To switch
    {
        null => "второй конец не задан",
        { MapId: { } map } when map != Session.Document.Id.Value =>
            "на карту «" + (Atlas?.Maps.FirstOrDefault(m => m.Id == map)?.Title ?? "из атласа") + "»"
            + (Atlas?.Maps.FirstOrDefault(m => m.Id == map) is { } m ? " · " + MapTransition.LevelTitle(m.Level) : ""),
        { HexLevel: { } level } => $"на этой карте, уровень {level}",
        var to => $"на этой карте ({to.X:0}, {to.Y:0})",
    };

    /// <summary>The check of the ends: the canvases of the other maps are read in the background.</summary>
    public async Task<IReadOnlyList<TransitionIssue>> CheckTransitionsAsync()
    {
        var document = Session.Document;
        var atlas = Atlas;
        var manifest = AtlasPath;
        var wanted = document.Transitions.Select(t => t.To?.MapId).OfType<Guid>().Where(id => id != document.Id.Value).Distinct().ToArray();
        var sizes = await Task.Run(() =>
        {
            var result = new Dictionary<Guid, (double, double)>();
            if (atlas is null || manifest is null) return result;
            foreach (var id in wanted)
            {
                if (atlas.Maps.FirstOrDefault(m => m.Id == id) is not { } map) continue;
                try
                {
                    var canvas = MapProjectStore.Open(AtlasStore.ResolveMap(manifest, map)).Document.Canvas;
                    result[id] = (canvas.Width, canvas.Height);
                }
                catch (Exception ex) when (ex is MapProjectException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                { _log.Error("TRANSITION_TARGET_OPEN_FAILED", ex); }
            }
            return result;
        });
        var issues = MapTransitions.Check(document, atlas, sizes);
        StatusDetail = issues.Count == 0 ? "Все концы переходов на месте." : $"Переходов с ошибками: {issues.Select(i => i.TransitionId).Distinct().Count()}.";
        return issues;
    }

    /// <summary>The level of the world of a map of the atlas (sky, surface, underdark), saved in the atlas file.</summary>
    public async Task<bool> SetAtlasMapLevelAsync(Guid mapId, WorldLevel level)
    {
        if (AtlasPath is not { } path || Atlas is not { } atlas || atlas.Maps.FirstOrDefault(m => m.Id == mapId) is not { } map || map.Level == level) return false;
        try
        {
            var updated = atlas with { Maps = atlas.Maps.Replace(map, map with { Level = level }) };
            await Task.Run(() => AtlasStore.Save(updated, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            StatusDetail = "Не удалось сохранить уровень в атласе.";
            _log.Error("ATLAS_LEVEL_SAVE_FAILED", ex);
            return false;
        }
        await RefreshAtlasAsync();
        StatusDetail = $"«{map.Title}» — {MapTransition.LevelTitle(level).ToLowerInvariant()}.";
        return true;
    }
}
