using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// One edit of a hex layer as the whole layer before and after. Hex layers are immutable and share
/// every untouched chunk, path and note with the previous snapshot, so keeping both costs only what
/// changed. Used for the edits that touch more than hexes: paths, regions, notes, fog, levels.
/// </summary>
public sealed class HexLayerEditCommand : IEditCommand
{
    private readonly HexLayer _before;
    private readonly HexLayer _after;

    public HexLayerEditCommand(string name, HexLayer before, HexLayer after, long? workingBytes = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Id != after.Id) throw new ArgumentException("Both snapshots must be of the same layer.");
        Name = string.IsNullOrWhiteSpace(name) ? "Hex layer" : name;
        _before = before;
        _after = after;
        MeasuredWorkingBytes = workingBytes ?? EditCost.BaseBytes;
    }

    public string Name { get; }

    public LayerId LayerId => _after.Id;

    public HexLayer After => _after;

    public long? MeasuredWorkingBytes { get; }

    public MapDocument Apply(MapDocument document) => Write(document, _after);

    public MapDocument Revert(MapDocument document) => Write(document, _before);

    private static MapDocument Write(MapDocument document, HexLayer layer)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.FindLayer(layer.Id) is not HexLayer current)
        {
            throw new InvalidOperationException($"Hex layer {layer.Id} is not in the document.");
        }

        // Name, visibility, lock and opacity belong to the layer list: other commands own them.
        return document.WithLayer(layer.Id, _ => layer with
        {
            Name = current.Name, Visible = current.Visible, Locked = current.Locked, Opacity = current.Opacity,
        });
    }
}

/// <summary>
/// An edit of the whole hex board of a map - rows or columns inserted, the board resized - that
/// changes every hex layer on the same grid and the page size with them. Before and after are kept
/// as the canvas plus the changed layers only.
/// </summary>
public sealed class HexBoardShapeCommand : IEditCommand
{
    private readonly CanvasDefinition _beforeCanvas;
    private readonly CanvasDefinition _afterCanvas;
    private readonly IReadOnlyList<HexLayer> _before;
    private readonly IReadOnlyList<HexLayer> _after;

    private HexBoardShapeCommand(string name, CanvasDefinition beforeCanvas, CanvasDefinition afterCanvas,
        IReadOnlyList<HexLayer> before, IReadOnlyList<HexLayer> after)
    {
        Name = name;
        _beforeCanvas = beforeCanvas;
        _afterCanvas = afterCanvas;
        _before = before;
        _after = after;
    }

    /// <summary>
    /// Applies <paramref name="reshape"/> to every hex layer laid out like <paramref name="reference"/>
    /// and fits the page to the new board. Null when nothing changes.
    /// </summary>
    public static HexBoardShapeCommand? Create(string name, MapDocument document, HexLayer reference, Func<HexLayer, HexLayer> reshape)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(reshape);
        var before = new List<HexLayer>();
        var after = new List<HexLayer>();
        foreach (var layer in document.Layers.OfType<HexLayer>())
        {
            if (layer.Board.Layout != reference.Board.Layout || layer.Board.Columns != reference.Board.Columns
                || layer.Board.Rows != reference.Board.Rows) continue;
            var changed = reshape(layer);
            if (ReferenceEquals(changed, layer)) continue;
            before.Add(layer);
            after.Add(changed);
        }

        if (after.Count == 0) return null;
        var board = after[0].Board;
        var width = Math.Clamp((int)Math.Ceiling(board.PixelWidth), CanvasDimensionPolicy.Minimum, CanvasDimensionPolicy.Maximum);
        var height = Math.Clamp((int)Math.Ceiling(board.PixelHeight), CanvasDimensionPolicy.Minimum, CanvasDimensionPolicy.Maximum);
        var canvas = document.Canvas.Resized(width, height);
        return new HexBoardShapeCommand(name, document.Canvas, canvas, before, after);
    }

    public string Name { get; }

    public long? MeasuredWorkingBytes => EditCost.BaseBytes + _after.Sum(layer => (long)layer.Board.CellCount * 48);

    public MapDocument Apply(MapDocument document) => Write(document, _afterCanvas, _after);

    public MapDocument Revert(MapDocument document) => Write(document, _beforeCanvas, _before);

    private static MapDocument Write(MapDocument document, CanvasDefinition canvas, IReadOnlyList<HexLayer> layers)
    {
        var byId = layers.ToDictionary(layer => layer.Id);
        var rewritten = document.Layers.Select(layer => byId.TryGetValue(layer.Id, out var hex) && layer is HexLayer current
            ? hex with { Name = current.Name, Visible = current.Visible, Locked = current.Locked, Opacity = current.Opacity }
            : layer).ToList();
        return new MapDocument(document.Id, document.Title, document.CreatedUtc, canvas, rewritten,
            document.Assets, document.Notes, document.Checkpoints, document.StampForeground, document.Category)
        { Atlas = document.Atlas, Lighting = document.Lighting, Style = document.Style, Entities = document.Entities, Labels = document.Labels, Fog = document.Fog, Transitions = document.Transitions, Timeline = document.Timeline, Pointcrawl = document.Pointcrawl };
    }
}

/// <summary>Row or column operations on a hex board (Hextml, Hexmap World Creator): insert, delete, resize.</summary>
public static class HexBoardOps
{
    /// <summary>
    /// Inserts <paramref name="count"/> empty columns before column <paramref name="at"/> (a negative
    /// count deletes that many columns from <paramref name="at"/>). Hexes keep their offset position,
    /// so an even count keeps every shifted hex on the same side of its neighbours.
    /// </summary>
    public static HexLayer InsertColumns(HexLayer layer, int at, int count)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var board = layer.Board;
        if (count == 0) return layer;
        at = Math.Clamp(at, 0, board.Columns);
        if (count < 0) count = -Math.Min(-count, board.Columns - at);
        var columns = board.Columns + count;
        if (columns < 1 || columns > HexBoard.MaxDimension || count == 0) return layer;
        return Remap(layer, columns, board.Rows, offset =>
            offset.Column < at ? offset
            : count < 0 && offset.Column < at - count ? null
            : offset with { Column = offset.Column + count }, fillGaps: count > 0);
    }

    /// <summary>Inserts (or with a negative count deletes) rows, like <see cref="InsertColumns"/>.</summary>
    public static HexLayer InsertRows(HexLayer layer, int at, int count)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var board = layer.Board;
        if (count == 0) return layer;
        at = Math.Clamp(at, 0, board.Rows);
        if (count < 0) count = -Math.Min(-count, board.Rows - at);
        var rows = board.Rows + count;
        if (rows < 1 || rows > HexBoard.MaxDimension || count == 0) return layer;
        return Remap(layer, board.Columns, rows, offset =>
            offset.Row < at ? offset
            : count < 0 && offset.Row < at - count ? null
            : offset with { Row = offset.Row + count }, fillGaps: count > 0);
    }

    /// <summary>The board grown or cut at its right and bottom edges; hexes keep their place.</summary>
    public static HexLayer Resize(HexLayer layer, int columns, int rows)
    {
        ArgumentNullException.ThrowIfNull(layer);
        columns = Math.Clamp(columns, 1, HexBoard.MaxDimension);
        rows = Math.Clamp(rows, 1, HexBoard.MaxDimension);
        if (columns == layer.Board.Columns && rows == layer.Board.Rows) return layer;
        return Remap(layer, columns, rows, offset => offset.Column < columns && offset.Row < rows ? offset : null);
    }

    /// <summary>Moves every hex, path node and note through an offset map; unmapped ones are removed.</summary>
    private static HexLayer Remap(HexLayer layer, int columns, int rows, Func<HexOffset, HexOffset?> map, bool fillGaps = false)
    {
        var layout = layer.Board.Layout;
        HexCoord? Move(HexCoord hex) => map(layout.ToOffset(hex)) is { } moved ? layout.FromOffset(moved) : null;
        var cells = new List<KeyValuePair<HexCoord, HexCell>>(layer.Board.CellCount);
        foreach (var (hex, cell) in layer.Board.Cells)
        {
            if (Move(hex) is { } moved) cells.Add(new(moved, cell));
        }

        var board = HexBoard.FromCells(layout, columns, rows, cells);
        var notes = ImmutableDictionary.CreateBuilder<HexCoord, HexNote>();
        foreach (var (hex, note) in layer.Notes)
        {
            if (Move(hex) is { } moved && board.Contains(moved)) notes[moved] = note;
        }

        return layer with
        {
            Board = board,
            GenerationProtection = layer.GenerationProtection.Select(Move).Where(h => h is not null && board.Contains(h.Value)).Select(h => h!.Value).ToImmutableHashSet(),
            GenerationRecipe = layer.GenerationRecipe is not { } recipe ? null : recipe with
            { Selection = recipe.Selection.Select(Move).Where(h => h is not null && board.Contains(h.Value)).Select(h => h!.Value).ToImmutableHashSet() },
            Notes = notes.ToImmutable(),
            Journey = layer.Journey with
            {
                From = layer.Journey.From is { } from && Move(from) is { } newFrom && board.Contains(newFrom) ? newFrom : null,
                To = layer.Journey.To is { } to && Move(to) is { } newTo && board.Contains(newTo) ? newTo : null,
            },
            Party = layer.Party with
            {
                Position = layer.Party.Position is { } position && Move(position) is { } movedPosition && board.Contains(movedPosition) ? movedPosition : null,
                History = layer.Party.History.Select(Move).Where(h => h.HasValue && board.Contains(h.Value)).Select(h => h!.Value).ToImmutableList(),
                Visited = layer.Party.Visited.Select(Move).Where(h => h.HasValue && board.Contains(h.Value)).Select(h => h!.Value).ToImmutableHashSet(),
            },
            Paths = HexPaths.Remap(layer.Paths, layout, hex => Move(hex) is { } moved && board.Contains(moved) ? moved : null, fillGaps),
        };
    }
}

/// <summary>Path rules shared by the path tool, the board operations and the generator.</summary>
public static class HexPaths
{
    /// <summary>
    /// Moves paths node by node. Where a node has no new place, or two nodes stop being neighbours,
    /// the path is cut in two; pieces shorter than two nodes are dropped. The first piece keeps the id.
    /// </summary>
    /// <param name="fillGaps">
    /// When two moved nodes stop being neighbours, join them with the straight run between them
    /// instead of cutting (inserted rows or columns: a road keeps crossing them).
    /// </param>
    public static ImmutableList<HexPath> Remap(ImmutableList<HexPath> paths, HexLayout layout, Func<HexCoord, HexCoord?> map, bool fillGaps = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var result = ImmutableList.CreateBuilder<HexPath>();
        foreach (var path in paths)
        {
            var first = true;
            if (path.Style == HexRouteStyle.Edges)
            {
                var piece = new List<HexVertex>();
                void FlushVertices()
                {
                    if (piece.Count >= 2)
                    {
                        result.Add(new HexPath(first ? path.Id : HexPathId.New(), path.Kind, path.Style, [], [.. piece],
                            path.Width, path.Color, path.Name, path.Seed)
                        { BlocksSight = path.BlocksSight, BlocksMove = path.BlocksMove, BlocksLight = path.BlocksLight, GenerationReason = path.GenerationReason });
                        first = false;
                    }

                    piece.Clear();
                }

                foreach (var vertex in path.Vertices)
                {
                    if (map(vertex.Hex) is not { } moved)
                    {
                        FlushVertices();
                        continue;
                    }

                    var next = layout.Canonical(vertex with { Hex = moved });
                    if (piece.Count > 0 && next != piece[^1] && !layout.VertexNeighbors(piece[^1]).Contains(next))
                    {
                        if (fillGaps) piece.AddRange(EdgeWalk(layout, piece[^1], next).Skip(1).SkipLast(1));
                        else FlushVertices();
                    }

                    piece.Add(next);
                }

                FlushVertices();
                continue;
            }

            var hexes = new List<HexCoord>();
            void Flush()
            {
                if (hexes.Count >= 2)
                {
                    result.Add(new HexPath(first ? path.Id : HexPathId.New(), path.Kind, path.Style, [.. hexes], [],
                        path.Width, path.Color, path.Name, path.Seed)
                        { BlocksSight = path.BlocksSight, BlocksMove = path.BlocksMove, BlocksLight = path.BlocksLight, GenerationReason = path.GenerationReason });
                    first = false;
                }

                hexes.Clear();
            }

            foreach (var hex in path.Hexes)
            {
                if (map(hex) is not { } moved)
                {
                    Flush();
                    continue;
                }

                if (hexes.Count > 0 && HexCoord.Distance(hexes[^1], moved) != 1)
                {
                    if (fillGaps && moved != hexes[^1]) hexes.AddRange(HexCoord.Line(hexes[^1], moved).Skip(1).SkipLast(1));
                    else Flush();
                }

                if (hexes.Count == 0 || hexes[^1] != moved) hexes.Add(moved);
            }

            Flush();
        }

        return result.ToImmutable();
    }

    /// <summary>
    /// The hexes of a stroke turned into a path run: consecutive duplicates dropped, gaps between
    /// non-neighbours filled with the straight hex line, and a step back onto the previous hex
    /// taken as undoing the last step (dragging back shortens the path).
    /// </summary>
    public static List<HexCoord> Walk(IEnumerable<HexCoord> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var result = new List<HexCoord>();
        foreach (var point in points)
        {
            if (result.Count == 0)
            {
                result.Add(point);
                continue;
            }

            foreach (var hex in HexCoord.Line(result[^1], point).Skip(1))
            {
                if (result.Count >= 2 && result[^2] == hex) result.RemoveAt(result.Count - 1);
                else if (result[^1] != hex) result.Add(hex);
            }
        }

        return result;
    }

    /// <summary>A corner walk along edges between two corners (breadth-first over the corner graph).</summary>
    public static List<HexVertex> EdgeWalk(HexLayout layout, HexVertex from, HexVertex to, int limit = 4096)
    {
        ArgumentNullException.ThrowIfNull(layout);
        from = layout.Canonical(from);
        to = layout.Canonical(to);
        if (from == to) return [from];
        var (tx, ty) = layout.Position(to);
        var previous = new Dictionary<HexVertex, HexVertex> { [from] = from };
        var queue = new PriorityQueue<HexVertex, double>();
        var cost = new Dictionary<HexVertex, double> { [from] = 0 };
        queue.Enqueue(from, 0);
        var expanded = 0;
        while (queue.TryDequeue(out var current, out _) && expanded++ < limit)
        {
            if (current == to) break;
            foreach (var next in layout.VertexNeighbors(current))
            {
                var step = cost[current] + 1;
                if (cost.TryGetValue(next, out var known) && known <= step) continue;
                cost[next] = step;
                previous[next] = current;
                var (x, y) = layout.Position(next);
                // Straight-line distance in edge lengths keeps the search heading for the target.
                queue.Enqueue(next, step + Math.Sqrt((x - tx) * (x - tx) + (y - ty) * (y - ty)) / layout.Size);
            }
        }

        if (!previous.ContainsKey(to)) return [from];
        var walk = new List<HexVertex>();
        for (var vertex = to; vertex != from; vertex = previous[vertex]) walk.Add(vertex);
        walk.Add(from);
        walk.Reverse();
        return walk;
    }
}

/// <summary>Cheapest ways between hexes (A*): travel times, the river and road generator, auto-routed paths.</summary>
public static class HexRouting
{
    /// <summary>
    /// The cheapest run of neighbouring hexes from <paramref name="from"/> to <paramref name="to"/>.
    /// <paramref name="stepCost"/> gives the cost of entering a hex from a neighbour, or null when it
    /// cannot be entered. Null when no way exists within <paramref name="limit"/> expanded hexes.
    /// </summary>
    public static List<HexCoord>? FindRoute(HexCoord from, HexCoord to, Func<HexCoord, HexCoord, double?> stepCost,
        double minStepCost = 1, int limit = 2_000_000)
    {
        ArgumentNullException.ThrowIfNull(stepCost);
        if (from == to) return [from];
        var previous = new Dictionary<HexCoord, HexCoord>();
        var cost = new Dictionary<HexCoord, double> { [from] = 0 };
        var queue = new PriorityQueue<HexCoord, double>();
        queue.Enqueue(from, 0);
        var closed = new HashSet<HexCoord>();
        while (queue.TryDequeue(out var current, out _))
        {
            if (current == to) break;
            if (!closed.Add(current)) continue;
            if (closed.Count > limit) return null;
            var here = cost[current];
            foreach (var next in current.Neighbors())
            {
                if (closed.Contains(next) || stepCost(current, next) is not { } step) continue;
                var total = here + step;
                if (cost.TryGetValue(next, out var known) && known <= total) continue;
                cost[next] = total;
                previous[next] = current;
                queue.Enqueue(next, total + HexCoord.Distance(next, to) * minStepCost);
            }
        }

        if (!previous.ContainsKey(to)) return null;
        var route = new List<HexCoord>();
        for (var hex = to; hex != from; hex = previous[hex]) route.Add(hex);
        route.Add(from);
        route.Reverse();
        return route;
    }

    /// <summary>The total cost of walking a route (the first hex is the start and costs nothing).</summary>
    public static double CostOf(IReadOnlyList<HexCoord> route, Func<HexCoord, HexCoord, double?> stepCost)
    {
        ArgumentNullException.ThrowIfNull(route);
        var total = 0.0;
        for (var i = 1; i < route.Count; i++) total += stepCost(route[i - 1], route[i]) ?? 0;
        return total;
    }
}

/// <summary>Selections of hexes: rectangles, same-terrain regions, outlines.</summary>
public static class HexSelection
{
    /// <summary>Every board hex whose offset lies in the rectangle spanned by two hexes.</summary>
    public static IEnumerable<HexCoord> Rectangle(HexBoard board, HexCoord a, HexCoord b)
    {
        ArgumentNullException.ThrowIfNull(board);
        var oa = board.Layout.ToOffset(a);
        var ob = board.Layout.ToOffset(b);
        var c0 = Math.Clamp(Math.Min(oa.Column, ob.Column), 0, board.Columns - 1);
        var c1 = Math.Clamp(Math.Max(oa.Column, ob.Column), 0, board.Columns - 1);
        var r0 = Math.Clamp(Math.Min(oa.Row, ob.Row), 0, board.Rows - 1);
        var r1 = Math.Clamp(Math.Max(oa.Row, ob.Row), 0, board.Rows - 1);
        for (var row = r0; row <= r1; row++)
        {
            for (var column = c0; column <= c1; column++)
            {
                yield return board.Layout.FromOffset(new HexOffset(column, row));
            }
        }
    }

    /// <summary>The magic wand: the connected hexes with the same terrain (and feature when <paramref name="matchFeature"/>).</summary>
    public static IReadOnlyList<HexCoord> SameTerrain(HexBoard board, HexCoord start, bool matchFeature = false, int limit = HexEditing.MaxFillHexes)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (!board.Contains(start)) return [];
        var seed = board[start];
        bool Same(HexCell? cell) => cell?.Terrain == seed?.Terrain && (!matchFeature || cell?.Feature == seed?.Feature);
        var region = new List<HexCoord>();
        var seen = new HashSet<HexCoord> { start };
        var queue = new Queue<HexCoord>();
        queue.Enqueue(start);
        while (queue.Count > 0 && region.Count < limit)
        {
            var hex = queue.Dequeue();
            region.Add(hex);
            foreach (var next in hex.Neighbors())
            {
                if (seen.Add(next) && board.Contains(next) && Same(board[next])) queue.Enqueue(next);
            }
        }

        return region;
    }
}

/// <summary>
/// Copied hexes, kept relative to an anchor hex so they can be pasted anywhere, turned in 60° steps
/// and mirrored: the cells, the paths that lie wholly inside the copy, and the hex keys.
/// </summary>
public sealed record HexClip(
    HexOrientation Orientation,
    IReadOnlyDictionary<HexCoord, HexCell?> Cells,
    IReadOnlyList<HexPath> Paths,
    IReadOnlyDictionary<HexCoord, HexNote> Notes)
{
    public int Count => Cells.Count;

    /// <summary>Copies the selected hexes of a layer relative to <paramref name="anchor"/>; empty hexes are copied as holes.</summary>
    public static HexClip Capture(HexLayer layer, IReadOnlyCollection<HexCoord> hexes, HexCoord anchor)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(hexes);
        var inside = hexes.Where(layer.Board.Contains).ToHashSet();
        var cells = inside.ToDictionary(hex => hex - anchor, hex => layer.Board[hex]);
        var paths = layer.Paths
            .Where(path => path.Style == HexRouteStyle.Edges
                ? path.Vertices.All(v => inside.Contains(v.Hex))
                : path.Hexes.All(inside.Contains))
            .Select(path => path.Moved(hex => hex - anchor))
            .ToList();
        var notes = layer.Notes.Where(pair => inside.Contains(pair.Key)).ToDictionary(pair => pair.Key - anchor, pair => pair.Value);
        return new HexClip(layer.Board.Orientation, cells, paths, notes);
    }

    /// <summary>The copy turned clockwise by <paramref name="steps"/> × 60° around the anchor; terrain pictures turn with it.</summary>
    public HexClip Rotated(int steps)
    {
        var origin = new HexCoord(0, 0);
        HexCoord Turn(HexCoord hex) => hex.RotatedAround(origin, steps);
        return this with
        {
            Cells = Cells.ToDictionary(pair => Turn(pair.Key), pair => pair.Value?.WithRotation(pair.Value.Rotation + steps)),
            Paths = [.. Paths.Select(path => TurnPath(path, Turn, steps))],
            Notes = Notes.ToDictionary(pair => Turn(pair.Key), pair => pair.Value),
        };
    }

    /// <summary>The copy mirrored left-right around the anchor.</summary>
    public HexClip Mirrored()
    {
        var origin = new HexCoord(0, 0);
        HexCoord Flip(HexCoord hex) => hex.MirroredAround(origin, Orientation);
        return this with
        {
            Cells = Cells.ToDictionary(pair => Flip(pair.Key), pair => pair.Value?.WithRotation(6 - pair.Value.Rotation)),
            Paths = [.. Paths.Select(path => path.Style == HexRouteStyle.Edges
                ? path with { Vertices = [.. path.Vertices.Select(v => MirrorVertex(v, Flip))] }
                : path.Moved(Flip))],
            Notes = Notes.ToDictionary(pair => Flip(pair.Key), pair => pair.Value),
        };
    }

    private HexPath TurnPath(HexPath path, Func<HexCoord, HexCoord> turn, int steps) => path.Style == HexRouteStyle.Edges
        ? path with { Vertices = [.. path.Vertices.Select(v => TurnVertex(v, turn, steps))] }
        : path.Moved(turn);

    private HexVertex TurnVertex(HexVertex vertex, Func<HexCoord, HexCoord> turn, int steps)
    {
        // Corners are numbered clockwise on screen, like the turn: corner i of a hex becomes corner i + steps of the turned hex.
        var layout = new HexLayout(Orientation, 1, 0, 0);
        return layout.Canonical(new HexVertex(turn(vertex.Hex), (((vertex.Corner + steps) % 6) + 6) % 6));
    }

    private HexVertex MirrorVertex(HexVertex vertex, Func<HexCoord, HexCoord> flip)
    {
        // A left-right mirror sends the corner at angle a to the one at 180° − a.
        var layout = new HexLayout(Orientation, 1, 0, 0);
        var (x, y) = layout.Position(vertex);
        var (cx, _) = layout.Center(vertex.Hex);
        var mirroredHex = flip(vertex.Hex);
        var (mx, _) = layout.Center(mirroredHex);
        var target = (X: mx - (x - cx), Y: y);
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < 6; i++)
        {
            var (px, py) = layout.Corner(mirroredHex, i);
            var distance = (px - target.X) * (px - target.X) + (py - target.Y) * (py - target.Y);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return layout.Canonical(new HexVertex(mirroredHex, best));
    }

    /// <summary>
    /// What pasting the copy with its anchor on <paramref name="at"/> does to a layer: hexes outside
    /// the board are dropped, paths get new ids and are cut where they leave the board.
    /// </summary>
    public HexLayer PasteInto(HexLayer layer, HexCoord at, bool includeEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var changes = Cells
            .Where(pair => includeEmpty || pair.Value is not null)
            .Select(pair => new KeyValuePair<HexCoord, HexCell?>(pair.Key + at, pair.Value))
            .Where(pair => layer.Board.Contains(pair.Key))
            .ToList();
        var board = layer.Board.With(changes);
        var moved = Paths.Select(path => new HexPath(HexPathId.New(), path.Kind, path.Style, path.Hexes, path.Vertices,
            path.Width, path.Color, path.Name, path.Seed)
            { BlocksSight = path.BlocksSight, BlocksMove = path.BlocksMove, BlocksLight = path.BlocksLight }
            .Moved(hex => hex + at)).ToImmutableList();
        var paths = layer.Paths.AddRange(HexPaths.Remap(moved, layer.Board.Layout, hex => board.Contains(hex) ? hex : null));
        var notes = layer.Notes.ToBuilder();
        foreach (var (hex, note) in Notes)
        {
            if (board.Contains(hex + at)) notes[hex + at] = note;
        }

        return layer with { Board = board, Paths = paths, Notes = notes.ToImmutable() };
    }
}

/// <summary>
/// Neighbour rules of the terrain transitions and coasts (the Wesnoth terrain builder): which sides
/// of a hex face a neighbour that draws over it, and how those sides group into runs.
/// </summary>
public static class HexTransitions
{
    /// <summary>
    /// The 6-bit mask of the sides of <paramref name="hex"/> whose neighbour satisfies
    /// <paramref name="test"/>; bit i is the side towards <see cref="HexCoord.Directions"/>[i].
    /// </summary>
    public static int Mask(HexCoord hex, Func<HexCoord, bool> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        var mask = 0;
        for (var i = 0; i < 6; i++)
        {
            if (test(hex.Neighbor(i))) mask |= 1 << i;
        }

        return mask;
    }

    /// <summary>
    /// The sides of a mask as runs of neighbouring sides (start side, length), longest first - the
    /// greedy order of Wesnoth's transitions: one picture for three touching sides instead of three.
    /// A full mask is one run of six starting at side 0.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> Runs(int mask)
    {
        mask &= 0x3F;
        if (mask == 0) return [];
        if (mask == 0x3F) return [(0, 6)];
        var runs = new List<(int Start, int Length)>();
        // Start right after an unset side so no run is split across side 5 → 0.
        var begin = 0;
        while ((mask & (1 << begin)) != 0) begin++;
        for (var k = 1; k <= 6; k++)
        {
            var side = (begin + k) % 6;
            if ((mask & (1 << side)) == 0) continue;
            if (runs.Count > 0 && (runs[^1].Start + runs[^1].Length) % 6 == side)
            {
                runs[^1] = (runs[^1].Start, runs[^1].Length + 1);
            }
            else
            {
                runs.Add((side, 1));
            }
        }

        return [.. runs.OrderByDescending(run => run.Length).ThenBy(run => run.Start)];
    }
}

/// <summary>
/// Travel over a hex layer (the hex-crawl ruler): the cost of a step is the entered tile's travel
/// cost, halved along a road or trail, with a little extra for wading a river where no road crosses.
/// </summary>
public sealed class HexTravelPlanner
{
    private readonly HexLayer _layer;
    private readonly Func<string, double?> _tileCost;
    private readonly HashSet<(HexCoord, HexCoord)> _roadSteps = [];
    private readonly HashSet<HexCoord> _river = [];

    /// <param name="tileCost">Travel cost of a terrain tile id relative to plains (null: cannot be entered).</param>
    public HexTravelPlanner(HexLayer layer, Func<string, double?> tileCost)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(tileCost);
        _layer = layer;
        _tileCost = tileCost;
        foreach (var path in layer.Paths)
        {
            if (path.Kind == HexPathKind.River) _river.UnionWith(path.Hexes);
            if (path.Kind is not (HexPathKind.Road or HexPathKind.Trail)) continue;
            for (var i = 1; i < path.Hexes.Length; i++)
            {
                _roadSteps.Add((path.Hexes[i - 1], path.Hexes[i]));
                _roadSteps.Add((path.Hexes[i], path.Hexes[i - 1]));
            }
        }
    }

    /// <summary>The cheapest step is half a plains hex (a road).</summary>
    public const double RoadFactor = 0.5;

    public double? StepCost(HexCoord from, HexCoord to)
    {
        if (!_layer.Board.Contains(to)) return null;
        if (_roadSteps.Contains((from, to))) return RoadFactor;
        // An unpainted hex is unknown country: priced like plains.
        var cost = _layer.Board[to] is { } cell ? _tileCost(cell.Terrain) : 1;
        if (cost is null) return null;
        return cost.Value + (_river.Contains(to) ? 0.5 : 0);
    }

    /// <summary>The quickest way between two hexes, or null when none exists.</summary>
    public HexRoute? Route(HexCoord from, HexCoord to)
    {
        var hexes = HexRouting.FindRoute(from, to, StepCost, RoadFactor, limit: 400_000);
        if (hexes is null) return null;
        var cost = HexRouting.CostOf(hexes, StepCost);
        var scale = _layer.Travel;
        return new HexRoute(hexes, hexes.Count - 1, (hexes.Count - 1) * scale.DistancePerHex,
            cost * scale.DistancePerHex / scale.DistancePerDay, scale.Unit);
    }
}

/// <summary>A measured way: its hexes, its length in hexes and distance units, and the days it takes.</summary>
public sealed record HexRoute(IReadOnlyList<HexCoord> Hexes, int Steps, double Distance, double Days, string Unit)
{
    public string Label => string.Create(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"),
        $"{Steps} гекс. · {Distance:0.#} {Unit} · {Days:0.#} дн.");
}
