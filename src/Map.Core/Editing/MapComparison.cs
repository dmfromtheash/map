using System.Collections.Immutable;
using System.Numerics;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>What happened to one thing between two versions.</summary>
public enum MapChangeKind { Added, Removed, Changed }

/// <summary>One changed thing on the map: its kind of change, its group («Поселения», «Реки»…) and where it is.</summary>
public sealed record MapChange(MapChangeKind Kind, string Group, string Name, WorldBounds Bounds);

/// <summary>A group's tally: «+4 поселения», «изменены 2 дороги».</summary>
public sealed record MapChangeGroup(string Group, int Added, int Removed, int Changed);

/// <summary>
/// R30: two versions of a map compared by meaning, not by bytes - what was added, removed or
/// changed among settlements, labels, objects, rivers, roads, notes and cards, and how many hexes
/// and fog cells were revealed. Things are matched by id; «changed» means moved, resized, turned,
/// renamed or redrawn. Each change keeps its place, for the highlight on the canvas. R30b: also what
/// the players see, a note's link and category, river node features, and fog moved cell by cell.
/// </summary>
public sealed class MapComparison
{
    public IReadOnlyList<MapChange> Changes { get; }
    public IReadOnlyList<MapChangeGroup> Groups { get; }
    public int HexesRevealed { get; }
    public int HexesHidden { get; }
    public long FogCellsRevealed { get; }
    public long FogCellsHidden { get; }
    public bool LandChanged { get; }

    public bool IsEmpty => Changes.Count == 0 && HexesRevealed == 0 && HexesHidden == 0 && FogCellsRevealed == 0 && FogCellsHidden == 0 && !LandChanged;

    private MapComparison(List<MapChange> changes, int hexesRevealed, int hexesHidden, long fogCells, long fogHidden, bool land)
    {
        FogCellsHidden = fogHidden;
        Changes = changes;
        Groups = [.. changes.GroupBy(c => c.Group).Select(g => new MapChangeGroup(g.Key,
            g.Count(c => c.Kind == MapChangeKind.Added), g.Count(c => c.Kind == MapChangeKind.Removed), g.Count(c => c.Kind == MapChangeKind.Changed)))
            .OrderBy(g => Array.IndexOf(GroupOrder, g.Group) is var i && i < 0 ? int.MaxValue : i)];
        HexesRevealed = hexesRevealed;
        HexesHidden = hexesHidden;
        FogCellsRevealed = fogCells;
        LandChanged = land;
    }

    public const string Settlements = "Поселения", Labels = "Подписи", Objects = "Объекты", Figures = "Фигуры",
        Rivers = "Реки", Roads = "Дороги", Areas = "Области", Notes = "Заметки", Cards = "Карточки";

    private static readonly string[] GroupOrder = [Settlements, Labels, Objects, Figures, Rivers, Roads, Areas, Notes, Cards];

    /// <param name="settlement">True for an object that is a settlement (the catalog knows the role of its asset).</param>
    public static MapComparison Compare(MapDocument before, MapDocument after, Func<SceneObject, bool>? settlement = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<MapChange>();
        string GroupOf(SceneObject o) => o.Kind switch
        {
            SceneObjectKind.Text => Labels,
            SceneObjectKind.Stamp when settlement?.Invoke(o) == true => Settlements,
            SceneObjectKind.Line or SceneObjectKind.Shape or SceneObjectKind.Rectangle or SceneObjectKind.Ellipse => Figures,
            _ => Objects,
        };
        Match(AllObjects(before), AllObjects(after), o => o.Id.Value, GroupOf, o => o.Text?.Content ?? o.Name, SceneGeometry.BoundsOf, SameObject, changes);
        Match(Layers<River>(before, l => l.Rivers), Layers<River>(after, l => l.Rivers), r => r.Id, _ => Rivers, r => r.Name, RiverBounds, SameRiver, changes);
        Match(Layers<Road>(before, l => l.Roads), Layers<Road>(after, l => l.Roads), r => r.Id, _ => Roads, r => r.Name, RoadBounds, SameRoad, changes);
        Match(Layers<PlacementArea>(before, l => l.PlacementAreas), Layers<PlacementArea>(after, l => l.PlacementAreas), a => a.Id, _ => Areas, a => a.Name,
            a => Bounds(a.Nodes.Select(n => (n.X, n.Y))), (x, y) => x.Nodes.Select(n => (n.X, n.Y)).SequenceEqual(y.Nodes.Select(n => (n.X, n.Y))) && x.Density == y.Density, changes);
        Match(before.Notes, after.Notes, n => n.Id.Value, _ => Notes, n => n.Title, NoteBounds, SameNote, changes);
        Match(before.Entities, after.Entities, e => e.Id, _ => Cards, e => e.Name, e => EntityBounds(after, e), SameCard, changes);

        var (revealed, hidden) = HexReveals(before, after);
        var (fogOpened, fogClosed) = FogDifference(before.Fog, after.Fog);
        return new MapComparison(changes, revealed, hidden, fogOpened, fogClosed, LandDiffers(before, after));
    }

    /// <summary>The same note by what it says and does: text, place, anchor, link, category, whether the players see it (R30b).</summary>
    public static bool SameNote(MapNote x, MapNote y) =>
        x.Title == y.Title && x.Markdown == y.Markdown && x.WorldX == y.WorldX && x.WorldY == y.WorldY
        && x.AnchorKind == y.AnchorKind && x.ObjectId == y.ObjectId
        && x.LinkedMap == y.LinkedMap && x.Category == y.Category && x.PlayerVisible == y.PlayerVisible;

    /// <summary>The same card: name, kind, text, places and whether the players see it (R30b).</summary>
    public static bool SameCard(MapEntity x, MapEntity y) =>
        x.Name == y.Name && x.Kind == y.Kind && x.Markdown == y.Markdown && x.PlayerVisible == y.PlayerVisible && x.Bindings.SequenceEqual(y.Bindings);

    /// <summary>
    /// Fog cells opened and closed between two versions, cell by cell (R30b): moving the opened area
    /// keeps the count the same but is still a change.
    /// </summary>
    public static (long Opened, long Closed) FogDifference(MapFog before, MapFog after)
    {
        long opened = 0, closed = 0;
        foreach (var key in before.Revealed.Keys.Union(after.Revealed.Keys))
        {
            var was = before.Revealed.TryGetValue(key, out var a) ? a : (ImmutableArray<ulong>?)null;
            var now = after.Revealed.TryGetValue(key, out var b) ? b : (ImmutableArray<ulong>?)null;
            for (var row = 0; row < MapFog.TileCells; row++)
            {
                var (x, y) = (Row(was, row), Row(now, row));
                opened += BitOperations.PopCount(y & ~x);
                closed += BitOperations.PopCount(x & ~y);
            }
        }
        return (opened, closed);

        // A tile stored as an empty array is wholly open; a missing tile is closed.
        static ulong Row(ImmutableArray<ulong>? rows, int row) => rows is not { } r ? 0 : r.Length == 0 ? ulong.MaxValue : r[row];
    }

    /// <summary>The summary in words: «+4 поселения · изменены 2 дороги · раскрыто 12 гексов».</summary>
    public IReadOnlyList<string> Summary()
    {
        var lines = new List<string>();
        foreach (var group in Groups)
        {
            var parts = new List<string>();
            if (group.Added > 0) parts.Add("+" + group.Added);
            if (group.Removed > 0) parts.Add("−" + group.Removed);
            if (group.Changed > 0) parts.Add("изменено " + group.Changed);
            lines.Add(group.Group + ": " + string.Join(", ", parts));
        }
        if (HexesRevealed > 0) lines.Add($"Раскрыто гексов: {HexesRevealed}");
        if (HexesHidden > 0) lines.Add($"Скрыто гексов: {HexesHidden}");
        if (FogCellsRevealed > 0) lines.Add($"Открыто тумана: {FogCellsRevealed * MapFog.Cell * MapFog.Cell / 1_000_000.0:0.##} Мпкс");
        if (FogCellsHidden > 0) lines.Add($"Закрыто туманом: {FogCellsHidden * MapFog.Cell * MapFog.Cell / 1_000_000.0:0.##} Мпкс");
        if (LandChanged) lines.Add("Суша перерисована");
        if (lines.Count == 0) lines.Add("Различий нет.");
        return lines;
    }

    private static void Match<T>(IEnumerable<T> before, IEnumerable<T> after, Func<T, Guid> id, Func<T, string> group, Func<T, string> name,
        Func<T, WorldBounds> bounds, Func<T, T, bool> same, List<MapChange> changes)
    {
        var old = new Dictionary<Guid, T>();
        foreach (var item in before) old.TryAdd(id(item), item);
        var seen = new HashSet<Guid>();
        foreach (var item in after)
        {
            var key = id(item);
            if (!seen.Add(key)) continue;
            if (!old.TryGetValue(key, out var was)) changes.Add(new MapChange(MapChangeKind.Added, group(item), name(item), bounds(item)));
            else if (!ReferenceEquals(was, item) && !same(was, item)) changes.Add(new MapChange(MapChangeKind.Changed, group(item), name(item), Union(bounds(was), bounds(item))));
        }
        foreach (var (key, was) in old)
            if (!seen.Contains(key)) changes.Add(new MapChange(MapChangeKind.Removed, group(was), name(was), bounds(was)));
    }

    private static IEnumerable<SceneObject> AllObjects(MapDocument document) => document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects);

    private static IEnumerable<T> Layers<T>(MapDocument document, Func<ObjectLayer, IEnumerable<T>> pick) => document.Layers.OfType<ObjectLayer>().SelectMany(pick);

    /// <summary>The same object by what it shows: place, size, turn, name, text and its look, asset, the nodes of a line or shape.</summary>
    public static bool SameObject(SceneObject x, SceneObject y) =>
        x.Kind == y.Kind && Near(x.X, y.X) && Near(x.Y, y.Y) && Near(x.Width, y.Width) && Near(x.Height, y.Height) && Near(x.RotationDegrees, y.RotationDegrees)
        && x.Name == y.Name && x.Visible == y.Visible && x.Locked == y.Locked && Equals(x.Text, y.Text) && x.Stamp?.AssetSha256 == y.Stamp?.AssetSha256
        && x.FillColor == y.FillColor && x.StrokeColor == y.StrokeColor && x.Era == y.Era && x.Political == y.Political && x.MorphKey == y.MorphKey
        && SameNodes(x.Line?.Nodes, y.Line?.Nodes)
        && (x.Shape is null) == (y.Shape is null)
        && (x.Shape is null || (x.Shape.Regions.Count == y.Shape!.Regions.Count && x.Shape.Regions.Zip(y.Shape.Regions).All(p => SameNodes(p.First.Nodes, p.Second.Nodes))
            && x.Shape.Fill.Color == y.Shape.Fill.Color && Near(x.Shape.Fill.Opacity, y.Shape.Fill.Opacity) && x.Shape.Stroke.Color == y.Shape.Stroke.Color && Near(x.Shape.Stroke.Width, y.Shape.Stroke.Width)));

    private static bool SameNodes(IReadOnlyList<VectorNode>? a, IReadOnlyList<VectorNode>? b) =>
        (a is null && b is null) || (a is not null && b is not null && a.Count == b.Count && a.Zip(b).All(p => Near(p.First.X, p.Second.X) && Near(p.First.Y, p.Second.Y)));

    /// <summary>The same river by its course, widths and the waterfalls, rapids and fords on its nodes (R30b), name, years and visibility.</summary>
    public static bool SameRiver(River x, River y) => x.Name == y.Name && x.Era == y.Era && x.Visible == y.Visible && Near(x.WidthScale, y.WidthScale)
        && x.Channels.Length == y.Channels.Length
        && x.Channels.Zip(y.Channels).All(p => p.First.Nodes.Select(n => (n.X, n.Y, n.Width, n.Sharp, n.Tension, n.Feature))
            .SequenceEqual(p.Second.Nodes.Select(n => (n.X, n.Y, n.Width, n.Sharp, n.Tension, n.Feature))));

    public static bool SameRoad(Road x, Road y) => x.Name == y.Name && x.Era == y.Era && x.Visible == y.Visible && x.Style == y.Style && Near(x.WidthScale, y.WidthScale)
        && x.Nodes.Select(n => (n.X, n.Y)).SequenceEqual(y.Nodes.Select(n => (n.X, n.Y)))
        && x.Branches.SelectMany(b => b.Vertices.Select(v => v.Width)).SequenceEqual(y.Branches.SelectMany(b => b.Vertices.Select(v => v.Width)));

    private static WorldBounds RiverBounds(River river) => Bounds(river.Channels.SelectMany(c => c.Nodes).Select(n => (n.X, n.Y)));
    private static WorldBounds RoadBounds(Road road) => Bounds(road.Nodes.Select(n => (n.X, n.Y)));
    private static WorldBounds NoteBounds(MapNote note) => new(note.WorldX - 24, note.WorldY - 24, note.WorldX + 24, note.WorldY + 24);

    private static WorldBounds EntityBounds(MapDocument document, MapEntity entity) =>
        entity.Bindings.FirstOrDefault(b => b.MapId is null || b.MapId == document.Id.Value) is { } b
            ? new WorldBounds(b.X - 24, b.Y - 24, b.X + 24, b.Y + 24) : new WorldBounds(0, 0, 0, 0);

    private static WorldBounds Bounds(IEnumerable<(double X, double Y)> points)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var (x, y) in points) { l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y); }
        return l > r ? new WorldBounds(0, 0, 0, 0) : new WorldBounds(l - 8, t - 8, r + 8, b + 8);
    }

    private static WorldBounds Union(WorldBounds a, WorldBounds b) =>
        new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    private static (int Revealed, int Hidden) HexReveals(MapDocument before, MapDocument after)
    {
        var (revealed, hidden) = (0, 0);
        foreach (var layer in after.Layers.OfType<HexLayer>())
        {
            if (before.FindLayer(layer.Id) is not HexLayer was) continue;
            foreach (var (hex, cell) in layer.Board.Cells)
            {
                var old = was.Board[hex]?.Revealed == true;
                if (cell.Revealed && !old) revealed++;
                else if (!cell.Revealed && old) hidden++;
            }
            foreach (var (hex, cell) in was.Board.Cells)
                if (cell.Revealed && layer.Board[hex] is null) hidden++;
        }
        return (revealed, hidden);
    }

    private static bool LandDiffers(MapDocument before, MapDocument after)
    {
        var a = before.Layers.OfType<LandLayer>().ToDictionary(l => l.Id);
        var b = after.Layers.OfType<LandLayer>().ToList();
        if (a.Count != b.Count) return true;
        return b.Any(l => !a.TryGetValue(l.Id, out var was) || !SameTiles(was.GeometryTiles, l.GeometryTiles));
    }

    /// <summary>Tile by tile: a shared tile is the same; a different one is compared byte for byte.</summary>
    private static bool SameTiles(TileMap x, TileMap y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x.TileCount != y.TileCount) return false;
        foreach (var key in x.Keys)
        {
            var (p, q) = (x.TryGet(key), y.TryGet(key));
            if (ReferenceEquals(p, q)) continue;
            if (p is null || q is null || !p.AsSpan().SequenceEqual(q)) return false;
        }
        return true;
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < .01;
}
