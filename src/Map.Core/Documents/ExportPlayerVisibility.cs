using System.Collections.Immutable;

namespace Map.Core.Documents;

/// <summary>One public-data rule for the player window, export metadata and printable keys.</summary>
public static class ExportPlayerVisibility
{
    public static bool HexVisible(HexLayer layer, HexCoord hex) =>
        layer.Board.Contains(hex) && (!layer.FogEnabled || layer.Board[hex]?.Revealed == true);

    public static bool NoteVisible(HexLayer layer, HexCoord hex, HexNote note) =>
        (!string.IsNullOrWhiteSpace(note.PlayerText) || !string.IsNullOrWhiteSpace(note.Rumor)
            || !string.IsNullOrWhiteSpace(note.ApproximateLabel))
        && HexVisible(layer, hex) && HexGameplay.IsRevealed(layer, hex, note);

    public static HexLayer Sanitize(HexLayer layer)
    {
        var board = layer.Board;
        var cells = layer.FogEnabled
            ? HexBoard.FromCells(board.Layout, board.Columns, board.Rows, board.Cells.Where(p => p.Value.Revealed))
            : board;
        cells = cells.With(cells.Cells.Where(p => !p.Value.PointsOfInterest.IsEmpty)
            .Select(p => new KeyValuePair<HexCoord, HexCell?>(p.Key,
                p.Value.WithPointsOfInterest(p.Value.PointsOfInterest.Where(point => point.PlayerVisible).Select(point => point with
                { Name = HexGameplay.PlayerLabel(point.Name, point.ApproximateLabel, point.Rumor, "Неизвестное место"),
                    ApproximateLabel = HexGameplay.PlayerLabel(point.Name, point.ApproximateLabel, point.Rumor, "Неизвестное место") })))));
        var regions = cells.Cells.Select(p => p.Value.Region).Where(id => id != 0).ToHashSet();
        foreach (var id in regions.ToArray())
        {
            var parent = layer.Region(id)?.ParentRegion ?? 0;
            while (parent != 0 && regions.Add(parent)) parent = layer.Region(parent)?.ParentRegion ?? 0;
        }
        return layer with
        {
            Board = cells,
            // Таблицы встреч и план ведущего никогда не передаются игрокам.
            Journey = new HexTravelSettings(),
            Notes = layer.Notes.Where(p => NoteVisible(layer, p.Key, p.Value))
                .ToImmutableDictionary(p => p.Key, p => p.Value with { GmText = "", ChildMap = null,
                    Title = HexGameplay.PlayerLabel(p.Value.Title, p.Value.ApproximateLabel, p.Value.Rumor, "Слух"),
                    PlayerText = string.IsNullOrWhiteSpace(p.Value.Rumor) ? p.Value.PlayerText : p.Value.Rumor,
                    Rumor = "", RevealCondition = RevealCondition.Always,
                    ManuallyRevealed = false, RevealDay = 0 }),
            Regions = [.. layer.Regions.Where(r => !layer.FogEnabled || regions.Contains(r.Id)).Select(r => r with
            { Name = HexGameplay.PlayerLabel(r.Name, r.ApproximateLabel, r.Rumor, "Неизвестный край"),
                ApproximateLabel = HexGameplay.PlayerLabel(r.Name, r.ApproximateLabel, r.Rumor, "Неизвестный край") })],
            Party = new HexParty { Position = layer.Party.Position is { } position && HexVisible(layer, position) ? position : null,
                History = [] },
            Paths = layer.FogEnabled ? [.. layer.Paths.SelectMany(path => VisibleRuns(layer, path))] : layer.Paths,
        };
    }

    // A path crossing fog is split, never joined across a hidden run. At the boundary an edge
    // belongs to both adjacent cells: expose it only when both in-board cells are revealed.
    private static IEnumerable<HexPath> VisibleRuns(HexLayer layer, HexPath path)
    {
        var edges = path.Style == HexRouteStyle.Edges;
        var start = -1;
        for (var i = 0; i < path.NodeCount; i++)
        {
            var visible = i + 1 < path.NodeCount && (edges
                ? EdgeVisible(layer, path.Vertices[i], path.Vertices[i + 1])
                : HexVisible(layer, path.Hexes[i]) && HexVisible(layer, path.Hexes[i + 1]));
            if (visible) { if (start < 0) start = i; }
            else if (start >= 0)
            {
                yield return edges ? path with { Vertices = path.Vertices[start..(i + 1)] }
                    : path with { Hexes = path.Hexes[start..(i + 1)] };
                start = -1;
            }
        }
    }

    private static bool EdgeVisible(HexLayer layer, HexVertex a, HexVertex b)
    {
        var layout = layer.Board.Layout;
        var p = layout.Position(a);
        var q = layout.Position(b);
        var dx = q.X - p.X;
        var dy = q.Y - p.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-9) return false;
        var mx = (p.X + q.X) / 2;
        var my = (p.Y + q.Y) / 2;
        var epsilon = layout.Size * 0.01;
        var left = layout.HexAt(mx - dy / length * epsilon, my + dx / length * epsilon);
        var right = layout.HexAt(mx + dy / length * epsilon, my - dx / length * epsilon);
        return (layer.Board.Contains(left) || layer.Board.Contains(right))
            && (!layer.Board.Contains(left) || HexVisible(layer, left))
            && (!layer.Board.Contains(right) || HexVisible(layer, right));
    }
}

/// <summary>Counts from the same source and recipient as the outgoing scene; no secret text.</summary>
public sealed record ExportPackagePreview(string MapTitle, ImmutableArray<int> Levels, int Layers,
    int NotesTotal, int NotesIncluded, int LabelsTotal, int LabelsIncluded, int FogHexes, int FogNotes)
{
    internal static ExportPackagePreview Create(string title, IReadOnlyList<MapLayer> layers, bool players)
    {
        var hexes = layers.OfType<HexLayer>().ToList();
        var notes = hexes.Sum(l => l.Notes.Count);
        var labels = hexes.Sum(l => l.Notes.Count(p => p.Value.Title.Length > 0));
        return new(title, [.. hexes.Select(l => l.Level).Distinct().OrderDescending()], layers.Count,
            notes, players ? hexes.Sum(l => l.Notes.Count(p => ExportPlayerVisibility.NoteVisible(l, p.Key, p.Value))) : notes,
            labels, players ? hexes.Sum(l => l.Notes.Count(p => p.Value.Title.Length > 0
                && ExportPlayerVisibility.NoteVisible(l, p.Key, p.Value))) : labels,
            hexes.Where(l => l.FogEnabled).Sum(l => l.Board.Columns * l.Board.Rows - l.Board.Cells.Count(p => p.Value.Revealed)),
            hexes.Where(l => l.FogEnabled).Sum(l => l.Notes.Count(p => !ExportPlayerVisibility.HexVisible(l, p.Key))));
    }
}
