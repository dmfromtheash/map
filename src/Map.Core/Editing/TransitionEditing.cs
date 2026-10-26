using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>The transitions of a map replaced as one undo step (R29).</summary>
public sealed class SetTransitionsCommand(ImmutableArray<MapTransition> before, ImmutableArray<MapTransition> after, string name) : IEditCommand
{
    public string Name { get; } = name;

    public MapDocument Apply(MapDocument document)
    {
        foreach (var transition in after) transition.Validate();
        if (after.Length > MapTransitions.MaxCount || after.Select(t => t.Id).Distinct().Count() != after.Length)
            throw new InvalidOperationException("Переходы должны иметь разные идентификаторы.");
        return document with { Transitions = after };
    }

    public MapDocument Revert(MapDocument document) => document with { Transitions = before };
}

/// <summary>R29: the check of the ends of the transitions of a map.</summary>
public static class MapTransitions
{
    public const int MaxCount = 4096;

    /// <summary>
    /// Every end that leads nowhere: the second end not set, outside this map, on a hex level the
    /// map does not have, or on a map the atlas does not hold (or whose size is unknown and the
    /// point lies outside it). <paramref name="mapSizes"/> gives the canvas of the other maps.
    /// </summary>
    public static IReadOnlyList<TransitionIssue> Check(MapDocument document, MapAtlas? atlas,
        IReadOnlyDictionary<Guid, (double Width, double Height)>? mapSizes = null)
    {
        var issues = new List<TransitionIssue>();
        var levels = document.Layers.OfType<HexLayer>().Select(l => l.Level).ToHashSet();
        bool Inside(double x, double y, double w, double h) => x >= 0 && y >= 0 && x <= w && y <= h;
        foreach (var t in document.Transitions)
        {
            void Issue(string problem) => issues.Add(new TransitionIssue(t.Id, t.Name, problem, t.From.X, t.From.Y));
            if (!Inside(t.From.X, t.From.Y, document.Canvas.Width, document.Canvas.Height)) Issue("первый конец за краем карты");
            if (t.From.HexLevel is { } fromLevel && !levels.Contains(fromLevel)) Issue($"на карте нет уровня {fromLevel}");
            if (t.To is not { } to) { Issue("второй конец не задан"); continue; }
            if (to.MapId is null || to.MapId == document.Id.Value)
            {
                if (!Inside(to.X, to.Y, document.Canvas.Width, document.Canvas.Height)) Issue("второй конец за краем карты");
                if (to.HexLevel is { } level && !levels.Contains(level)) Issue($"на карте нет уровня {level}");
                if (to.HexLevel == t.From.HexLevel && Math.Abs(to.X - t.From.X) < 1 && Math.Abs(to.Y - t.From.Y) < 1) Issue("оба конца в одной точке");
                continue;
            }
            if (atlas is null || atlas.Maps.All(m => m.Id != to.MapId)) { Issue("карта второго конца не найдена в атласе"); continue; }
            if (mapSizes is not null && mapSizes.TryGetValue(to.MapId.Value, out var size) && !Inside(to.X, to.Y, size.Width, size.Height))
                Issue("второй конец за краем своей карты");
        }
        return issues;
    }

    /// <summary>The transitions of this map whose second end is on the given map (to show them arriving there).</summary>
    public static IEnumerable<MapTransition> LeadingTo(MapDocument document, Guid mapId) =>
        document.Transitions.Where(t => t.To?.MapId == mapId);
}
