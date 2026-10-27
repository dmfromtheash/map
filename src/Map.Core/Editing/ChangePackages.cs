using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// R42: the changes made to a map since one of its versions, to carry into another copy of the same
/// map without a network: the things as they were in that version (<see cref="Before"/>) and as
/// they are now (<see cref="After"/>), each a small map holding only the changed objects, rivers,
/// roads, notes and cards. The other copy takes a change cleanly when its thing still looks like
/// <see cref="Before"/>; otherwise it is a conflict for the user to decide.
/// </summary>
public sealed record ChangePackage(Guid SourceMap, string SourceTitle, string BaseName, DateTimeOffset CreatedUtc, MapDocument Before, MapDocument After)
{
    public const int Format = 1;

    /// <summary>The package of what changed between <paramref name="baseVersion"/> and <paramref name="current"/>.</summary>
    public static ChangePackage Create(MapDocument baseVersion, MapDocument current, string baseName, DateTimeOffset now)
    {
        var objects = new HashSet<Guid>();
        var rivers = new HashSet<Guid>();
        var roads = new HashSet<Guid>();
        var notes = new HashSet<Guid>();
        var cards = new HashSet<Guid>();
        foreach (var (a, b) in Pair(Objects(baseVersion), Objects(current), o => o.Id.Value)) if (a is null || b is null || !MapComparison.SameObject(a, b)) objects.Add((a ?? b)!.Id.Value);
        foreach (var (a, b) in Pair(Rivers(baseVersion), Rivers(current), r => r.Id)) if (a is null || b is null || !MapComparison.SameRiver(a, b)) rivers.Add((a ?? b)!.Id);
        foreach (var (a, b) in Pair(Roads(baseVersion), Roads(current), r => r.Id)) if (a is null || b is null || !MapComparison.SameRoad(a, b)) roads.Add((a ?? b)!.Id);
        foreach (var (a, b) in Pair(baseVersion.Notes, current.Notes, n => n.Id.Value)) if (a is null || b is null || !MapComparison.SameNote(a, b)) notes.Add((a ?? b)!.Id.Value);
        foreach (var (a, b) in Pair(baseVersion.Entities, current.Entities, e => e.Id)) if (a is null || b is null || !MapComparison.SameCard(a, b)) cards.Add((a ?? b)!.Id);
        return new ChangePackage(current.Id.Value, current.Title, baseName, now,
            Slice(baseVersion, objects, rivers, roads, notes, cards), Slice(current, objects, rivers, roads, notes, cards));
    }

    public int Count => Kinds().Sum(k => k.Ids.Count);

    /// <summary>What a package carries, in words, for the messages around it.</summary>
    public const string Carries = "объекты, подписи, реки, дороги, заметки и карточки";

    /// <summary>
    /// R42b: what changed between the two versions that a package does not carry - the land, the
    /// hexes, the fog, placement areas and paths - so an empty or partial package can say so.
    /// </summary>
    public static IReadOnlyList<string> NotCarried(MapDocument baseVersion, MapDocument current)
    {
        var result = new List<string>();
        var diff = MapComparison.Compare(baseVersion, current);
        if (diff.LandChanged) result.Add("суша");
        if (HexesDiffer(baseVersion, current)) result.Add("гексы");
        if (diff.FogCellsRevealed + diff.FogCellsHidden > 0 || baseVersion.Fog.Enabled != current.Fog.Enabled) result.Add("туман");
        if (diff.Groups.Any(g => g.Group == MapComparison.Areas) || PathsDiffer(baseVersion, current)) result.Add("области и пути расстановки");
        return result;
    }

    private static bool HexesDiffer(MapDocument a, MapDocument b)
    {
        var left = a.Layers.OfType<HexLayer>().ToDictionary(l => l.Id);
        var right = b.Layers.OfType<HexLayer>().ToList();
        if (left.Count != right.Count) return true;
        foreach (var layer in right)
        {
            if (!left.TryGetValue(layer.Id, out var was)) return true;
            if (ReferenceEquals(was.Board, layer.Board)) continue;
            var cells = was.Board.Cells.ToDictionary(c => c.Key, c => c.Value);
            var count = 0;
            foreach (var (hex, cell) in layer.Board.Cells)
            {
                count++;
                if (!cells.TryGetValue(hex, out var old) || old.Terrain != cell.Terrain || old.Revealed != cell.Revealed) return true;
            }
            if (count != cells.Count) return true;
        }
        return false;
    }

    private static bool PathsDiffer(MapDocument a, MapDocument b)
    {
        var left = a.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementPaths).ToDictionary(p => p.Id);
        var right = b.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementPaths).ToList();
        return left.Count != right.Count || right.Any(p => !left.TryGetValue(p.Id, out var was) || !ReferenceEquals(was, p)
            && (was.AssetSetId != p.AssetSetId || was.VariantId != p.VariantId || !was.Nodes.SequenceEqual(p.Nodes) || !was.Members.SequenceEqual(p.Members)));
    }

    private IEnumerable<(string Kind, HashSet<Guid> Ids)> Kinds()
    {
        yield return ("objects", [.. Objects(Before).Concat(Objects(After)).Select(o => o.Id.Value)]);
        yield return ("rivers", [.. Rivers(Before).Concat(Rivers(After)).Select(r => r.Id)]);
        yield return ("roads", [.. Roads(Before).Concat(Roads(After)).Select(r => r.Id)]);
        yield return ("notes", [.. Before.Notes.Concat(After.Notes).Select(n => n.Id.Value)]);
        yield return ("cards", [.. Before.Entities.Concat(After.Entities).Select(e => e.Id)]);
    }

    /// <summary>A map holding only the given things of <paramref name="source"/>, in layers with the same ids and names.</summary>
    private static MapDocument Slice(MapDocument source, HashSet<Guid> objects, HashSet<Guid> rivers, HashSet<Guid> roads, HashSet<Guid> notes, HashSet<Guid> cards)
    {
        var layers = new List<MapLayer> { new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, source.Canvas.Background) };
        foreach (var layer in source.Layers.OfType<ObjectLayer>())
        {
            var kept = layer with
            {
                Objects = [.. layer.Objects.Where(o => objects.Contains(o.Id.Value))],
                Rivers = [.. layer.Rivers.Where(r => rivers.Contains(r.Id))],
                Roads = [.. layer.Roads.Where(r => roads.Contains(r.Id))],
                PlacementPaths = [], PlacementAreas = [],
            };
            if (kept.Objects.Count + kept.Rivers.Length + kept.Roads.Length > 0) layers.Add(kept);
        }
        return new MapDocument(source.Id, source.Title, source.CreatedUtc, source.Canvas, layers, source.Assets,
            [.. source.Notes.Where(n => notes.Contains(n.Id.Value))])
        { Entities = [.. source.Entities.Where(e => cards.Contains(e.Id))] };
    }

    internal static IEnumerable<SceneObject> Objects(MapDocument d) => d.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects);
    internal static IEnumerable<River> Rivers(MapDocument d) => d.Layers.OfType<ObjectLayer>().SelectMany(l => l.Rivers);
    internal static IEnumerable<Road> Roads(MapDocument d) => d.Layers.OfType<ObjectLayer>().SelectMany(l => l.Roads);

    internal static IEnumerable<(T? A, T? B)> Pair<T>(IEnumerable<T> a, IEnumerable<T> b, Func<T, Guid> id) where T : class
    {
        var left = a.GroupBy(id).ToDictionary(g => g.Key, g => g.First());
        var right = b.GroupBy(id).ToDictionary(g => g.Key, g => g.First());
        foreach (var key in left.Keys.Union(right.Keys)) yield return (left.GetValueOrDefault(key), right.GetValueOrDefault(key));
    }
}

/// <summary>What happens to one change when a package is applied (R42).</summary>
public enum ChangeOutcome { Clean, Same, Conflict }

/// <summary>One change of a package against the target copy: its kind, name, whether it applies cleanly.</summary>
public sealed record PackageChange(string Kind, Guid Id, string Name, string Action, ChangeOutcome Outcome)
{
    /// <summary>How the thing is named in this copy now (empty when it is not there).</summary>
    public string Mine { get; init; } = "";
}

/// <summary>R42: a change package checked against another copy and applied (clean changes always, conflicts as the user decides).</summary>
public static class ChangePackageMerge
{
    public static IReadOnlyList<PackageChange> Check(MapDocument target, ChangePackage package)
    {
        var changes = new List<PackageChange>();
        void Add<T>(string kind, IEnumerable<T> before, IEnumerable<T> after, IEnumerable<T> mine, Func<T, Guid> id, Func<T, string> name, Func<T, T, bool> same) where T : class
        {
            var current = mine.GroupBy(id).ToDictionary(g => g.Key, g => g.First());
            foreach (var (b, a) in ChangePackage.Pair(before, after, id))
            {
                var key = id((a ?? b)!);
                current.TryGetValue(key, out var have);
                var action = b is null ? "добавить" : a is null ? "убрать" : "изменить";
                var title = name((a ?? b)!);
                ChangeOutcome outcome;
                if (a is not null && have is not null && same(have, a)) outcome = ChangeOutcome.Same;
                else if (a is null && have is null) outcome = ChangeOutcome.Same;
                else if (b is null) outcome = have is null ? ChangeOutcome.Clean : ChangeOutcome.Conflict;
                else outcome = have is not null && same(have, b) ? ChangeOutcome.Clean : ChangeOutcome.Conflict;
                changes.Add(new PackageChange(kind, key, title, action, outcome) { Mine = have is null ? "" : name(have) });
            }
        }
        Add("Объект", ChangePackage.Objects(package.Before), ChangePackage.Objects(package.After), ChangePackage.Objects(target), o => o.Id.Value, o => o.Text?.Content ?? o.Name, MapComparison.SameObject);
        Add("Река", ChangePackage.Rivers(package.Before), ChangePackage.Rivers(package.After), ChangePackage.Rivers(target), r => r.Id, r => r.Name, MapComparison.SameRiver);
        Add("Дорога", ChangePackage.Roads(package.Before), ChangePackage.Roads(package.After), ChangePackage.Roads(target), r => r.Id, r => r.Name, MapComparison.SameRoad);
        Add("Заметка", package.Before.Notes, package.After.Notes, target.Notes, n => n.Id.Value, n => n.Title, MapComparison.SameNote);
        Add("Карточка", package.Before.Entities, package.After.Entities, target.Entities, e => e.Id, e => e.Name, MapComparison.SameCard);
        return changes;
    }

    /// <summary>
    /// The target with the package applied: clean changes, and conflicts whose ids are in
    /// <paramref name="takeTheirs"/> (the package wins); other conflicts keep the target's version.
    /// </summary>
    public static MapDocument Apply(MapDocument target, ChangePackage package, IReadOnlySet<Guid> takeTheirs)
    {
        var plan = Check(target, package);
        var apply = plan.Where(c => c.Outcome == ChangeOutcome.Clean || (c.Outcome == ChangeOutcome.Conflict && takeTheirs.Contains(c.Id))).Select(c => c.Id).ToHashSet();
        if (apply.Count == 0) return target;
        var after = package.After;
        var afterObjects = ChangePackage.Objects(after).ToDictionary(o => o.Id.Value);
        var afterRivers = ChangePackage.Rivers(after).ToDictionary(r => r.Id);
        var afterRoads = ChangePackage.Roads(after).ToDictionary(r => r.Id);
        var layers = target.Layers.Select(layer => layer is not ObjectLayer objects ? layer : objects with
        {
            Objects = [.. objects.Objects.Where(o => !apply.Contains(o.Id.Value) || afterObjects.ContainsKey(o.Id.Value)).Select(o => apply.Contains(o.Id.Value) ? afterObjects[o.Id.Value] : o)],
            Rivers = [.. objects.Rivers.Where(r => !apply.Contains(r.Id) || afterRivers.ContainsKey(r.Id)).Select(r => apply.Contains(r.Id) ? afterRivers[r.Id] : r)],
            Roads = [.. objects.Roads.Where(r => !apply.Contains(r.Id) || afterRoads.ContainsKey(r.Id)).Select(r => apply.Contains(r.Id) ? afterRoads[r.Id] : r)],
        }).ToList();
        // New things go to the layer of the same id, else one of the same name, else a layer «Из пакета».
        var present = ChangePackage.Objects(target).Select(o => o.Id.Value).Concat(ChangePackage.Rivers(target).Select(r => r.Id)).Concat(ChangePackage.Roads(target).Select(r => r.Id)).ToHashSet();
        foreach (var source in after.Layers.OfType<ObjectLayer>())
        {
            var fresh = (Objects: source.Objects.Where(o => apply.Contains(o.Id.Value) && !present.Contains(o.Id.Value)).ToList(),
                Rivers: source.Rivers.Where(r => apply.Contains(r.Id) && !present.Contains(r.Id)).ToList(),
                Roads: source.Roads.Where(r => apply.Contains(r.Id) && !present.Contains(r.Id)).ToList());
            if (fresh.Objects.Count + fresh.Rivers.Count + fresh.Roads.Count == 0) continue;
            var index = layers.FindIndex(l => l.Id == source.Id);
            if (index < 0) index = layers.FindIndex(l => l is ObjectLayer && l.Name == source.Name);
            if (index < 0) { layers.Add(new ObjectLayer(source.Id, source.Name, true, false, 1, [])); index = layers.Count - 1; }
            var target2 = (ObjectLayer)layers[index];
            layers[index] = target2 with { Objects = [.. target2.Objects, .. fresh.Objects], Rivers = [.. target2.Rivers, .. fresh.Rivers], Roads = [.. target2.Roads, .. fresh.Roads] };
        }
        var notes = Merge(target.Notes, after.Notes, n => n.Id.Value, apply);
        var cards = Merge(target.Entities, after.Entities, e => e.Id, apply);
        var assets = target.Assets.Concat(after.Assets.Where(a => target.Assets.All(t => !string.Equals(t.Sha256, a.Sha256, StringComparison.OrdinalIgnoreCase)))).ToList();
        return target.WithLayers(layers).WithNotes(notes).WithAssets(assets) with { Entities = [.. cards] };
    }

    private static List<T> Merge<T>(IEnumerable<T> mine, IEnumerable<T> theirs, Func<T, Guid> id, HashSet<Guid> apply)
    {
        var after = theirs.ToDictionary(id);
        var result = mine.Where(m => !apply.Contains(id(m)) || after.ContainsKey(id(m))).Select(m => apply.Contains(id(m)) ? after[id(m)] : m).ToList();
        var present = mine.Select(id).ToHashSet();
        result.AddRange(theirs.Where(t => apply.Contains(id(t)) && !present.Contains(id(t))));
        return result;
    }
}

/// <summary>A change package applied as one undo step (R42).</summary>
public sealed class ApplyChangePackageCommand(MapDocument before, MapDocument after) : IEditCommand
{
    public string Name => "Пакет изменений";
    // Only what a package touches is swapped, so versions or settings made since stay.
    public MapDocument Apply(MapDocument document) => Take(document, after);
    public MapDocument Revert(MapDocument document) => Take(document, before);
    private static MapDocument Take(MapDocument document, MapDocument from) =>
        document.WithLayers(from.Layers).WithNotes(from.Notes).WithAssets(from.Assets) with { Entities = from.Entities };
}
