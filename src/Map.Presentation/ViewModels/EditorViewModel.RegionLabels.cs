using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R07b: the name of a region set inside its figure, the importance of a label and how the labels
/// of the map give way - by scale, by overlap, and to placement areas. Every change is one undo
/// step; the editing session lays region labels out again after every edit of their figure.
/// </summary>
public sealed partial class EditorViewModel
{
    public LabelSettings LabelSettings => Session.Document.Labels;

    public TextRegionBinding? SelectedTextRegion => Session.SelectedObject?.Text?.Region;

    public int? SelectedLabelImportance => Session.SelectedObject?.Text?.Importance;

    public void SetLabelSettings(LabelSettings after)
    {
        var before = Session.Document.Labels;
        if (after.ByScale == before.ByScale && after.ResolveOverlaps == before.ResolveOverlaps
            && after.ClearUnderLabels == before.ClearUnderLabels && after.MinScales.SequenceEqual(before.MinScales)) return;
        try { Session.Execute(new SetLabelSettingsCommand(before, after)); }
        catch (ArgumentException ex) { StatusDetail = ex.Message; return; }
        Raise(nameof(LabelSettings));
        AfterEdit();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void SetLabelImportance(int importance)
    {
        if (!LabelSettings.IsValidImportance(importance)) return;
        EditText("Важность подписи", commit: true, text => text.Importance == importance ? text : text with { Importance = importance });
        StatusDetail = $"Важность подписи — {importance} из 5."
            + (Session.Document.Labels.ByScale ? "" : " Включите «Подписи по масштабу», чтобы мелкие скрывались при отдалении.");
    }

    /// <summary>The figure a region name can go into: the selected closed shape, else the selected placement area.</summary>
    private (TextRegionTarget Target, Guid Id, string Name, LayerId Layer)? RegionTarget()
    {
        if (Session.SelectedObject is { Kind: SceneObjectKind.Shape, Shape: not null } shape
            && DocumentRevisionIndex.For(Session.Document).FindLayerOf(shape.Id) is { } shapeLayer)
            return (TextRegionTarget.Shape, shape.Id.Value, shape.Name, shapeLayer.Id);
        if (SelectedEditableArea is { } area
            && Session.Document.Layers.OfType<ObjectLayer>().FirstOrDefault(l => l.PlacementAreas.Any(a => a.Id == area.Id)) is { } areaLayer)
            return (TextRegionTarget.PlacementArea, area.Id, area.Name, areaLayer.Id);
        return null;
    }

    public bool CanLabelRegion => RegionTarget() is not null;

    /// <summary>
    /// Names the selected figure: a label in the «Регион» look inside it, along its widest axis.
    /// The text comes from the card bound to the figure (or the given card), else the figure's name.
    /// </summary>
    public bool AddRegionLabel(Guid? entityId = null)
    {
        if (RegionTarget() is not { } target) { StatusDetail = "Выделите фигуру или область расстановки, чтобы подписать её."; return false; }
        return AddRegionLabel(target.Target, target.Id, target.Name, target.Layer, entityId);
    }

    /// <summary>Names the figure a card is bound to on this map (a shape or a placement area).</summary>
    public bool AddRegionLabelForEntity(Guid entityId)
    {
        if (Entities.FirstOrDefault(e => e.Id == entityId) is not { } entity) return false;
        foreach (var binding in entity.Bindings.Where(b => b.MapId is null || b.MapId == Session.Document.Id.Value))
        {
            if (binding.Kind == EntityTargetKind.Object && DocumentRevisionIndex.For(Session.Document).FindObject(new ObjectId(binding.TargetId)) is { Shape: not null } shape
                && DocumentRevisionIndex.For(Session.Document).FindLayerOf(shape.Id) is { } layer)
                return AddRegionLabel(TextRegionTarget.Shape, shape.Id.Value, shape.Name, layer.Id, entity.Id);
            if (binding.Kind == EntityTargetKind.Area
                && Session.Document.Layers.OfType<ObjectLayer>().FirstOrDefault(l => l.PlacementAreas.Any(a => a.Id == binding.TargetId)) is { } areaLayer)
                return AddRegionLabel(TextRegionTarget.PlacementArea, binding.TargetId, entity.Name, areaLayer.Id, entity.Id);
        }

        StatusDetail = "Карточка не привязана к фигуре или области на этой карте. Выделите фигуру и нажмите «Привязать выделенное».";
        return false;
    }

    private bool AddRegionLabel(TextRegionTarget kind, Guid targetId, string fallbackName, LayerId layerId, Guid? entityId)
    {
        var entity = entityId is { } id ? Entities.FirstOrDefault(e => e.Id == id) : MapEntities.BoundTo(Entities, targetId).FirstOrDefault();
        var content = entity?.Name ?? fallbackName;
        if (Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Any(o => o.Text?.Region is { } r && r.TargetId == targetId))
        { StatusDetail = "У этой фигуры уже есть подпись области."; return false; }
        if (Session.Document.FindLayer(layerId) is not ObjectLayer { Locked: false } layer)
        { StatusDetail = "Слой фигуры закрыт — подпись не добавить."; return false; }
        var binding = new TextRegionBinding { Target = kind, TargetId = targetId, EntityId = entity?.Id };
        var style = LabelPresets.Apply(new TextStyle { Content = content, Alignment = TextAlignment.Center }, LabelPreset.Region) with
        { Region = binding, Importance = 4 };
        var draft = new SceneObject(ObjectId.New(), SceneObjectKind.Text, EntityNameGenerator.Text(Session.Document, content),
            0, 0, 1, 1, 0, "#FF20242B", "#FFFFFFFF", 0, true, false) { Text = style };
        var label = RegionLabels.Layout(Session.Document, draft);
        Session.Execute(new AddObjectCommand(layer.Id, label));
        if (Session.Document.FindLayer(layer.Id) is not ObjectLayer added || added.Objects.All(o => o.Id != label.Id)) return false;
        ActiveTool = EditorTool.Select;
        Session.SelectedObjectId = label.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
        StatusDetail = $"Подпись «{content}» стоит внутри области и пойдёт за её контуром. Сдвиньте её — сдвиг запомнится.";
        return true;
    }

    /// <summary>Puts the selected region name back in the middle of its figure.</summary>
    public void ResetRegionLabelShift()
    {
        EditText("Подпись в середину области", commit: true, text => text.Region is { } r && (r.OffsetX != 0 || r.OffsetY != 0)
            ? text with { Region = r with { OffsetX = 0, OffsetY = 0 } } : text);
        StatusDetail = "Подпись снова в середине области.";
    }

    /// <summary>Frees the selected region name; it stays where it is as ordinary text.</summary>
    public void UnbindRegionLabel()
    {
        EditText("Отвязать подпись от области", commit: true, text => text.Region is null ? text : text with { Region = null });
        StatusDetail = "Подпись больше не следует за областью.";
    }
}
