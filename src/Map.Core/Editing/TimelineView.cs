using System.Runtime.CompilerServices;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// R31: the map as it stood in a year. Objects, labels and roads outside their era are left out;
/// two shapes sharing a morph key whose eras leave a gap (a border «before» ending in 900, «after»
/// starting in 950) are blended in the gap, so the border moves year by year. In the political
/// mode states are filled in their colour with a firm border and disputed lands get a dashed
/// border. The source document is never changed; views are kept per snapshot and year.
/// </summary>
public static class TimelineView
{
    private const int MorphSamples = 96;
    private static readonly ConditionalWeakTable<MapDocument, Dictionary<(int Year, bool Political), MapDocument>> Cache = new();

    /// <summary>The map as it is shown: the year's view when the timeline is on, else the document itself.</summary>
    public static MapDocument Shown(MapDocument document) =>
        document.Timeline is { Enabled: true } t ? At(document, t.Year, t.PoliticalMode)
        : document.Timeline.PoliticalMode ? At(document, null, true) : document;

    public static MapDocument At(MapDocument document, int? year, bool political)
    {
        ArgumentNullException.ThrowIfNull(document);
        var perMap = Cache.GetOrCreateValue(document);
        lock (perMap)
        {
            var key = (year ?? int.MinValue, political);
            if (perMap.TryGetValue(key, out var cached)) return cached;
            if (perMap.Count > 64) perMap.Clear();
            var view = Build(document, year, political);
            perMap[key] = view;
            return view;
        }
    }

    private static MapDocument Build(MapDocument document, int? year, bool political)
    {
        var changed = false;
        var layers = document.Layers.Select(layer =>
        {
            if (layer is not ObjectLayer objects) return layer;
            var items = new List<SceneObject>(objects.Objects.Count);
            var layerChanged = false;
            if (year is { } y)
            {
                foreach (var group in objects.Objects.Where(o => o.MorphKey is not null && o.Shape is not null).GroupBy(o => o.MorphKey!))
                    if (Morph(group, y) is { } morphed) { items.Add(political ? Political(morphed) : morphed); layerChanged = true; }
            }
            foreach (var item in objects.Objects)
            {
                if (year is { } y2 && item.Era is { } era && !era.Contains(y2)) { layerChanged = true; continue; }
                var shown = political ? Political(item) : item;
                layerChanged |= !ReferenceEquals(shown, item);
                items.Add(shown);
            }
            var roads = year is { } y3 ? objects.Roads.Where(r => r.Era is null || r.Era.Contains(y3)).ToArray() : [.. objects.Roads];
            layerChanged |= roads.Length != objects.Roads.Length;
            if (!layerChanged) return layer;
            changed = true;
            return objects with { Objects = items, Roads = [.. roads] };
        }).ToArray();
        return changed ? document.WithLayers(layers) : document;
    }

    /// <summary>
    /// In the gap between two consecutive shapes of one morph key, the first blended into the
    /// second by the share of the gap passed; null outside every gap.
    /// </summary>
    private static SceneObject? Morph(IEnumerable<SceneObject> group, int year)
    {
        var ordered = group.Where(o => o.Era is { } e && (e.From is not null || e.To is not null))
            .OrderBy(o => o.Era!.From ?? int.MinValue).ToArray();
        for (var i = 0; i + 1 < ordered.Length; i++)
        {
            var (a, b) = (ordered[i], ordered[i + 1]);
            if (a.Era!.To is not { } end || b.Era!.From is not { } start || start - end < 2 || year <= end || year >= start) continue;
            var t = (year - end) / (double)(start - end);
            var from = Outline(a);
            var to = Align(from, Outline(b));
            var blended = from.Zip(to, (p, q) => new VectorPoint(p.X + ((q.X - p.X) * t), p.Y + ((q.Y - p.Y) * t))).ToArray();
            var shape = VectorGeometry.CreateFreeShape(new ObjectId(Guid.NewGuid()), a.Name, blended, a.Shape!.Fill, a.Shape.Stroke);
            return shape with { Political = a.Political, Era = null, MorphKey = null };
        }
        return null;
    }

    private static List<VectorPoint> Outline(SceneObject shape)
    {
        var points = VectorGeometry.Flatten(shape, shape.Shape!.Regions[0].Nodes, closed: true);
        return Resample(points, MorphSamples);
    }

    /// <summary>The second outline turned so its first point is the one nearest the first outline's first point (no twisting).</summary>
    private static List<VectorPoint> Align(List<VectorPoint> reference, List<VectorPoint> outline)
    {
        var best = 0; var bestCost = double.MaxValue;
        for (var shift = 0; shift < outline.Count; shift++)
        {
            var cost = 0.0;
            for (var i = 0; i < reference.Count; i += 8)
            {
                var q = outline[(i + shift) % outline.Count];
                cost += ((reference[i].X - q.X) * (reference[i].X - q.X)) + ((reference[i].Y - q.Y) * (reference[i].Y - q.Y));
            }
            if (cost < bestCost) { bestCost = cost; best = shift; }
        }
        return [.. Enumerable.Range(0, outline.Count).Select(i => outline[(i + best) % outline.Count])];
    }

    private static List<VectorPoint> Resample(IReadOnlyList<VectorPoint> polygon, int count)
    {
        var lengths = new double[polygon.Count];
        var total = 0.0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var (a, b) = (polygon[i], polygon[(i + 1) % polygon.Count]);
            lengths[i] = Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
            total += lengths[i];
        }
        var result = new List<VectorPoint>(count);
        if (total < 1e-9) { for (var k = 0; k < count; k++) result.Add(polygon[0]); return result; }
        var (segment, into) = (0, 0.0);
        for (var k = 0; k < count; k++)
        {
            var target = total * k / count;
            while (segment < polygon.Count - 1 && into + lengths[segment] < target) { into += lengths[segment]; segment++; }
            var t = lengths[segment] < 1e-9 ? 0 : (target - into) / lengths[segment];
            var (a, b) = (polygon[segment], polygon[(segment + 1) % polygon.Count]);
            result.Add(new VectorPoint(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t)));
        }
        return result;
    }

    /// <summary>The political look: a state in its own colour with a firm border, disputed land with a dashed one.</summary>
    private static SceneObject Political(SceneObject item)
    {
        if (item.Shape is not { } shape || item.Political == PoliticalRole.None) return item;
        var fill = shape.Fill with { Opacity = item.Political == PoliticalRole.State ? .42 : .3 };
        var stroke = shape.Stroke with
        {
            Width = Math.Max(3, shape.Stroke.Width),
            Opacity = 1,
            Pattern = item.Political == PoliticalRole.Disputed ? StrokePattern.Dashed : StrokePattern.Solid,
            Color = item.Political == PoliticalRole.Disputed ? "#FF8A2F25" : shape.Stroke.Color,
        };
        return item with { Shape = shape with { Fill = fill, Stroke = stroke } };
    }

    /// <summary>The legend of the political map: the states and disputed lands shown that year.</summary>
    public static IReadOnlyList<(string Name, string Color, PoliticalRole Role)> PoliticalLegend(MapDocument shown) =>
        [.. shown.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)
            .Where(o => o.Political != PoliticalRole.None && o.Shape is not null)
            .GroupBy(o => (o.Name, o.Political)).Select(g => (g.Key.Name, g.First().Shape!.Fill.Color, g.Key.Political))
            .OrderBy(e => e.Political).ThenBy(e => e.Name, StringComparer.CurrentCulture)];
}

/// <summary>The timeline of the map replaced as one undo step (R31).</summary>
public sealed class SetTimelineCommand(MapTimeline before, MapTimeline after, string name) : IEditCommand
{
    public string Name { get; } = name;

    public MapDocument Apply(MapDocument document)
    {
        after.Validate();
        return document with { Timeline = after };
    }

    public MapDocument Revert(MapDocument document) => document with { Timeline = before };
}
