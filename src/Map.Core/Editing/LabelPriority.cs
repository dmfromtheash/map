using System.Runtime.CompilerServices;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// R07b: which labels give way. By scale - a label of importance i is hidden below
/// <see cref="LabelSettings.MinScale"/>; by overlap - of two crossing labels the less important
/// (then the smaller, then the later) one hides. Labels are sized in map units, so overlaps do not
/// depend on the zoom: the result changes only when a threshold is crossed and is cached per map
/// snapshot and set of shown importances. Membership resolves queried labels and their overlap
/// dependencies lazily; enumerating/counting the set resolves all labels.
/// </summary>
public static partial class LabelPriority
{
    /// <summary>Compatibility limit: larger candidate sets keep the original scale-only behavior.</summary>
    public const int MaxOverlapLabels = 4000;

    private static readonly IReadOnlySet<ObjectId> None = new HashSet<ObjectId>();
    private static readonly ConditionalWeakTable<MapDocument, Dictionary<int, IReadOnlySet<ObjectId>>> Cache = new();

    /// <summary>Prepares label-only lookups off the frame; independent of visibility/settings.</summary>
    public static void Prepare(MapDocument document, CancellationToken cancellationToken = default)
    {
        foreach (var layer in document.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer is ObjectLayer objects) _ = LabelIndex.For(objects);
        }
    }

    public static bool IsPrepared(MapDocument document)
    {
        foreach (var layer in document.Layers)
            if (layer is ObjectLayer objects && !LabelIndex.TryFor(objects)) return false;
        return true;
    }

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
            var layers = document.Layers.OfType<ObjectLayer>().Where(l => l.Visible)
                .Select(l => new LabelLayer(l, LabelIndex.For(l))).ToArray();
            IReadOnlySet<ObjectId> result = layers.All(l => l.Index.Labels.IsEmpty)
                ? None : new HiddenSet(layers, settings.ResolveOverlaps, shown);
            perMap[shown] = result;
            return result;
        }
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
