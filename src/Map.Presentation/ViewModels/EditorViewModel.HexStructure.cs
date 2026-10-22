using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    private HexPresentation _hexPresentation;
    public HexPresentation HexPresentation
    {
        get => _hexPresentation;
        set
        {
            if (!Enum.IsDefined(value) || value == _hexPresentation) return;
            _hexPresentation = value;
            Raise(nameof(HexPresentation));
        }
    }

    public IReadOnlyList<HexPoi> HexPoints => HexKeyHex is { } hex ? ActiveHexLayer?.Board[hex]?.PointsOfInterest ?? [] : [];

    public void AddHexPoi(string type = "starter/village") => EditHexPoints(points => points.Count < 64
        ? points.Add(new HexPoi(type, "Новая точка")) : points, "Добавить точку интереса");

    public void SetHexPoi(int index, HexPoi point) => EditHexPoints(points => index >= 0 && index < points.Count
        ? points.SetItem(index, point) : points, "Изменить точку интереса");

    public void RemoveHexPoi(int index) => EditHexPoints(points => index >= 0 && index < points.Count
        ? points.RemoveAt(index) : points, "Удалить точку интереса");

    public void MoveHexPoi(int index, int delta) => EditHexPoints(points =>
    {
        var target = index + delta;
        return index < 0 || index >= points.Count || target < 0 || target >= points.Count ? points
            : points.RemoveAt(index).Insert(target, points[index]);
    }, "Порядок точек интереса");

    private void EditHexPoints(Func<ImmutableList<HexPoi>, ImmutableList<HexPoi>> edit, string name)
    {
        if (ActiveHexLayer is not { } layer || HexKeyHex is not { } hex) return;
        var cell = layer.Board[hex] ?? HexCell.Of("starter/grass");
        var after = cell.WithPointsOfInterest(edit(cell.PointsOfInterest));
        if (ReferenceEquals(after, cell)) return;
        ExecuteHexLayerEdit(name, layer, layer with { Board = layer.Board.With([new(hex, after)]) });
        Raise(nameof(HexPoints));
    }

    public bool SetHexRegionParent(int id, int parent)
    {
        if (ActiveHexLayer is not { } layer || layer.Region(id) is not { } region) return false;
        if (!HexRegionHierarchy.CanParent(layer.Regions, id, parent))
        {
            StatusDetail = "Нельзя назначить родителя: получился бы цикл или регион не существует.";
            return false;
        }
        if (region.ParentRegion != parent)
            ExecuteHexLayerEdit("Родитель региона", layer, layer with
            { Regions = layer.Regions.Replace(region, region with { ParentRegion = parent }) });
        Raise(nameof(HexRegions));
        return true;
    }
}
