using System.Runtime.CompilerServices;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// R07b: which labels give way. By scale - a label of importance i is hidden below
/// <see cref="LabelSettings.MinScale"/>; by overlap - of two crossing labels the less important
/// (then the smaller, then the later) one hides. Labels are sized in map units, so overlaps do not
/// depend on the zoom: the result changes only when a threshold is crossed and is cached per map
/// snapshot and set of shown importances.
/// </summary>
public static class LabelPriority
{
    /// <summary>More labels than this skip the overlap pass (it is quadratic in the worst case).</summary>
    public const int MaxOverlapLabels = 4000;

    private static readonly IReadOnlySet<ObjectId> None = new HashSet<ObjectId>();
    private static readonly ConditionalWeakTable<MapDocument, Dictionary<int, IReadOnlySet<ObjectId>>> Cache = new();

    /// <summary>The labels hidden at this scale (screen pixels per map pixel); empty when the map does not ask for it.</summary>
    public static IReadOnlySet<ObjectId> Hidden(MapDocument document, double scale)
    {
        ArgumentNullException.ThrowIfNull(document);
        var settings = document.Labels;
        if (!settings.ByScale && !settings.ResolveOverlaps) return None;
        var shown = 0;
        for (var importance = LabelSettings.MinImportance; importance <= LabelSettings.MaxImportance; importance++)
            if (!settings.ByScale || scale >= settings.MinScale(importance)) shown |= 1 << importance;
        var perMap = Cache.GetOrCreateValue(document);
        lock (perMap)
        {
            if (perMap.TryGetValue(shown, out var cached)) return cached;
            var result = Compute(document, settings, shown);
            perMap[shown] = result;
            return result;
        }
    }

    private static IReadOnlySet<ObjectId> Compute(MapDocument document, LabelSettings settings, int shown)
    {
        var hidden = new HashSet<ObjectId>();
        var candidates = new List<(SceneObject Label, int Importance, int Order)>();
        var order = 0;
        foreach (var layer in document.Layers.OfType<ObjectLayer>())
        {
            if (!layer.Visible) continue;
            foreach (var item in layer.Objects)
            {
                if (item.Kind != SceneObjectKind.Text || item.Text is not { } text || !item.Visible) continue;
                var importance = Math.Clamp(text.Importance, LabelSettings.MinImportance, LabelSettings.MaxImportance);
                if ((shown & (1 << importance)) == 0) { hidden.Add(item.Id); continue; }
                candidates.Add((item, importance, order++));
            }
        }

        if (settings.ResolveOverlaps && candidates.Count <= MaxOverlapLabels)
        {
            var kept = new List<WorldBounds>();
            foreach (var (label, _, _) in candidates.OrderByDescending(c => c.Importance).ThenByDescending(c => c.Label.Text!.FontSize).ThenBy(c => c.Order))
            {
                var bounds = Inset(SceneGeometry.BoundsOf(label));
                if (kept.Any(other => other.Intersects(bounds))) { hidden.Add(label.Id); continue; }
                kept.Add(bounds);
            }
        }

        return hidden.Count == 0 ? None : hidden;
    }

    /// <summary>A label's box less a tenth on each side: letters seldom fill their box to the edge.</summary>
    private static WorldBounds Inset(WorldBounds bounds)
    {
        var (dx, dy) = ((bounds.Right - bounds.Left) * .1, (bounds.Bottom - bounds.Top) * .1);
        return new WorldBounds(bounds.Left + dx, bounds.Top + dy, bounds.Right - dx, bounds.Bottom - dy);
    }

    /// <summary>The boxes of the visible labels of the map, for keeping placement areas clear under them.</summary>
    public static IReadOnlyList<WorldBounds> LabelBounds(MapDocument document) =>
        !document.Labels.ClearUnderLabels ? []
        : [.. document.Layers.OfType<ObjectLayer>().Where(l => l.Visible)
            .SelectMany(l => l.Objects).Where(o => o.Kind == SceneObjectKind.Text && o.Visible && o.Text is not null)
            .Select(SceneGeometry.BoundsOf)];
}
