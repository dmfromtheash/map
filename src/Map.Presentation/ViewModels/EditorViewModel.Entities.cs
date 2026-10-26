using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;

namespace Map.Presentation.ViewModels;

/// <summary>A card found on another map of the atlas.</summary>
public sealed record AtlasEntityHit(Guid MapId, string MapTitle, MapEntity Entity);

/// <summary>
/// R26: the cards of the world - a place, a river, a character, a faction - with their text,
/// [[links]], backlinks and places on this and other maps of the atlas; and the link, category and
/// visibility of a note. Every change is one undo step.
/// </summary>
public sealed partial class EditorViewModel
{
    public ImmutableArray<MapEntity> Entities => Session.Document.Entities;

    public IReadOnlyList<MapEntity> SearchEntities(string? query) => [.. MapEntities.Search(Entities, query)];

    public IReadOnlyList<MapEntity> EntityBacklinks(MapEntity entity) => [.. MapEntities.Backlinks(Entities, entity)];

    /// <summary>The cards bound to the selected object, river or road.</summary>
    public IReadOnlyList<MapEntity> EntitiesOfSelection() => SelectionTarget() is { } target ? [.. MapEntities.BoundTo(Entities, target.Id)] : [];

    private void SetEntities(ImmutableArray<MapEntity> after, string name)
    {
        try { Session.Execute(new SetEntitiesCommand(Entities, after, name)); }
        catch (ArgumentException ex) { StatusDetail = ex.Message; return; }
        Raise(nameof(Entities));
        AfterEdit();
    }

    /// <summary>What a new card is bound to: the selected object, river, road or placement area, else nothing.</summary>
    private (EntityTargetKind Kind, Guid Id, string Name, double X, double Y)? SelectionTarget()
    {
        if (Session.SelectedObject is { } item)
            return (EntityTargetKind.Object, item.Id.Value, item.Text?.Content ?? item.Name, item.X + (item.Width / 2), item.Y + (item.Height / 2));
        if (SelectedRiver is { } river && river.Channels.FirstOrDefault()?.Nodes.FirstOrDefault() is { } node)
            return (EntityTargetKind.River, river.Id, river.Name, node.X, node.Y);
        if (SelectedRoad is { } road && road.Nodes.FirstOrDefault() is { } first)
            return (EntityTargetKind.Road, road.Id, road.Name, first.X, first.Y);
        if (SelectedEditableArea is { Nodes.Length: > 0 } area)
            return (EntityTargetKind.Area, area.Id, area.Name, area.Nodes.Average(n => n.X), area.Nodes.Average(n => n.Y));
        return null;
    }

    /// <summary>A new card of this kind, named and bound after the selection when there is one, else at the middle of the view.</summary>
    public Guid CreateEntity(EntityKind kind)
    {
        var target = SelectionTarget();
        var name = target?.Name is { Length: > 0 } n ? n.Split('\n')[0].Trim() : $"{MapEntities.KindTitle(kind)} {Entities.Length + 1}";
        if (name.Length > MapEntity.MaxNameLength) name = name[..MapEntity.MaxNameLength];
        var centre = ViewCentre();
        var binding = target is { } t ? new EntityBinding(null, t.Kind, t.Id, t.X, t.Y)
            : new EntityBinding(null, EntityTargetKind.Point, Guid.Empty, centre.X, centre.Y);
        var entity = new MapEntity { Name = name, Kind = kind, Bindings = [binding] };
        SetEntities(Entities.Add(entity), "Новая карточка");
        StatusDetail = $"Карточка «{name}» создана" + (target is null ? " в центре вида." : " и привязана к выделенному.");
        return entity.Id;
    }

    public void EditEntity(Guid id, string name, EntityKind kind, string markdown, bool playerVisible)
    {
        if (Entities.FirstOrDefault(e => e.Id == id) is not { } before || string.IsNullOrWhiteSpace(name)) return;
        var after = before with { Name = name.Trim(), Kind = kind, Markdown = markdown, PlayerVisible = playerVisible };
        if (after == before) return;
        SetEntities(Entities.Replace(before, after), "Изменить карточку");
    }

    /// <summary>Binds the selection to a card (one more place of the same entity).</summary>
    public bool BindSelectionToEntity(Guid id)
    {
        if (Entities.FirstOrDefault(e => e.Id == id) is not { } before) return false;
        if (SelectionTarget() is not { } t) { StatusDetail = "Выделите объект, реку или дорогу, чтобы привязать к карточке."; return false; }
        if (before.Bindings.Any(b => b.MapId is null && b.TargetId == t.Id)) { StatusDetail = "Уже привязано."; return false; }
        SetEntities(Entities.Replace(before, before with { Bindings = before.Bindings.Add(new EntityBinding(null, t.Kind, t.Id, t.X, t.Y)) }), "Привязать к карточке");
        return true;
    }

    /// <summary>Binds a card to a place on another map of the atlas (the same entity on the painted and the hex map).</summary>
    public bool BindEntityToMap(Guid id, Guid mapId, double x, double y)
    {
        if (Entities.FirstOrDefault(e => e.Id == id) is not { } before) return false;
        SetEntities(Entities.Replace(before, before with { Bindings = before.Bindings.Add(new EntityBinding(mapId, EntityTargetKind.Point, Guid.Empty, x, y)) }), "Привязать к другой карте");
        return true;
    }

    public void UnbindEntity(Guid id, int index)
    {
        if (Entities.FirstOrDefault(e => e.Id == id) is not { } before || index < 0 || index >= before.Bindings.Length) return;
        SetEntities(Entities.Replace(before, before with { Bindings = before.Bindings.RemoveAt(index) }), "Отвязать от карточки");
    }

    public void DeleteEntity(Guid id)
    {
        if (Entities.FirstOrDefault(e => e.Id == id) is not { } before) return;
        SetEntities(Entities.Remove(before), "Удалить карточку");
    }

    /// <summary>Shows a place of a card: on this map the view goes there; another map of the atlas opens.</summary>
    public void GoToBinding(EntityBinding binding)
    {
        if (binding.MapId is { } map && map != Session.Document.Id.Value) { OpenAtlasMap(map); return; }
        var viewport = Session.Viewport;
        viewport.Set(binding.X - (viewport.ViewWidth / 2 / viewport.Scale), binding.Y - (viewport.ViewHeight / 2 / viewport.Scale), viewport.Scale);
        if (binding.Kind == EntityTargetKind.Object) Session.SelectedObjectId = new ObjectId(binding.TargetId);
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    /// <summary>A short description of where a card is bound, for the list in the card.</summary>
    public string BindingTitle(EntityBinding binding)
    {
        var where = binding.MapId is { } map && map != Session.Document.Id.Value
            ? "на карте «" + (Atlas?.Maps.FirstOrDefault(m => m.Id == map)?.Title ?? "из атласа") + "»" : "на этой карте";
        var what = binding.Kind switch
        {
            EntityTargetKind.Object => "объект",
            EntityTargetKind.River => "река",
            EntityTargetKind.Road => "дорога",
            EntityTargetKind.Note => "заметка",
            EntityTargetKind.Hex => "гекс",
            EntityTargetKind.Area => "область",
            _ => "точка",
        };
        return $"{what} {where} ({binding.X:0}, {binding.Y:0})";
    }

    /// <summary>The cards of the other maps of the atlas whose name or text contain the query (read in the background).</summary>
    public async Task<IReadOnlyList<AtlasEntityHit>> SearchAtlasEntitiesAsync(string? query)
    {
        if (AtlasPath is not { } manifest || Atlas is not { } atlas) return [];
        var current = Session.Document.Id.Value;
        return await Task.Run(() =>
        {
            var hits = new List<AtlasEntityHit>();
            foreach (var map in atlas.Maps.Where(m => m.Id != current))
            {
                try
                {
                    var opened = MapProjectStore.Open(AtlasStore.ResolveMap(manifest, map)).Document;
                    hits.AddRange(MapEntities.Search(opened.Entities, query).Select(e => new AtlasEntityHit(map.Id, map.Title, e)));
                }
                catch (Exception ex) when (ex is MapProjectException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    _log.Error("ATLAS_ENTITY_SEARCH_FAILED", ex);
                }
            }
            return (IReadOnlyList<AtlasEntityHit>)hits;
        });
    }

    /// <summary>The link of a note to a map of the atlas, its category and whether the players see it (one undo step).</summary>
    public void SetNoteWiki(NoteId id, Guid? linkedMap, NoteCategory category, bool playerVisible)
    {
        var before = Notes.FirstOrDefault(note => note.Id == id);
        if (before is null) return;
        var after = before with { LinkedMap = linkedMap, Category = category, PlayerVisible = playerVisible };
        if (after == before) return;
        Session.Execute(new ReplaceNoteCommand(before, after));
        AfterEdit();
    }
}
