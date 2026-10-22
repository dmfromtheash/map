using Map.Core.Documents;
using Map.Persistence.Container;

namespace Map.Persistence;

public static partial class MapProjectStore
{
    private static bool NeedsHexStructure(HexLayer layer) => layer.Regions.Any(r => r.ParentRegion != 0)
        || layer.Board.Cells.Any(p => p.Value.PointsOfInterest.Count > 1
            || p.Value.PointsOfInterest.Any(point => point.Name.Length != 0 || !point.PlayerVisible));

    private static List<HexPoiCellDto>? WriteHexStructureV18(HexLayer layer) => !NeedsHexStructure(layer) ? null
        : [.. layer.Board.Cells.Where(p => !p.Value.PointsOfInterest.IsEmpty).Select(p =>
        {
            var offset = layer.Board.Layout.ToOffset(p.Key);
            return new HexPoiCellDto { Column = offset.Column, Row = offset.Row,
                Points = [.. p.Value.PointsOfInterest.Select(point => new HexPoiDto
                { Type = point.Type, Name = point.Name, PlayerVisible = point.PlayerVisible, Variant = point.Variant })] };
        })];

    // Isolated v16 → v18 step. A missing collection retains the singleton migrated by HexCell.Of.
    // The integrator can place this step after v17 without changing earlier readers or cell strides.
    private static HexBoard ReadHexStructureV18(HexBoard board, List<HexPoiCellDto>? cells)
    {
        if (cells is null) return board;
        var seen = new HashSet<HexCoord>();
        var edits = new List<KeyValuePair<HexCoord, HexCell?>>();
        foreach (var entry in cells)
        {
            var hex = board.Layout.FromOffset(new HexOffset(entry.Column, entry.Row));
            if (!board.Contains(hex) || !seen.Add(hex) || board[hex] is not { } cell
                || entry.Points is null || entry.Points.Count > 64
                || entry.Points.Any(p => p is null || !HexCell.IsValidTileId(p.Type) || p.Variant < 0 || p.Name is null || p.Name.Length > 120))
                throw new MapProjectCorruptedException("Повреждён список точек интереса гекса.");
            edits.Add(new(hex, cell.WithPointsOfInterest(entry.Points.Select(p => new HexPoi(p.Type, p.Name, p.PlayerVisible, p.Variant)))));
        }
        return board.With(edits);
    }
}
