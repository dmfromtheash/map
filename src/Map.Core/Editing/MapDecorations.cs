using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// Keeps the derived state of cartographic decorations (R08) in step with the map: a scale bar
/// carries the canvas scale, a legend lists what the map actually uses. Runs after every edit in
/// <see cref="EditingSession.Execute"/>, like <see cref="TextOnPath.Follow"/>; maps without
/// decorations pay one scan of each new object list.
/// </summary>
public static class MapDecorations
{
    /// <summary>The document with every decoration refreshed; the same instance when nothing changed.</summary>
    public static MapDocument Refresh(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!HasDecorations(document)) return document;
        IReadOnlyList<LegendEntry>? legend = null;
        List<MapLayer>? layers = null;
        for (var index = 0; index < document.Layers.Count; index++)
        {
            if (document.Layers[index] is not ObjectLayer layer) continue;
            SceneObject[]? rewritten = null;
            for (var i = 0; i < layer.Objects.Count; i++)
            {
                var item = layer.Objects[i];
                if (item.Kind != SceneObjectKind.Decoration || item.Decoration is not { } style) continue;
                var refreshed = style.Kind switch
                {
                    DecorationKind.ScaleBar => style with { Scale = document.Canvas.Scale },
                    DecorationKind.Legend => style with { Entries = legend ??= BuildLegend(document) },
                    _ => style,
                };
                var height = refreshed.Kind == DecorationKind.Legend ? LegendHeight(item.Width, refreshed.Entries.Count) : item.Height;
                if (refreshed.Equals(style) && Math.Abs(height - item.Height) < .5) continue;
                rewritten ??= [.. layer.Objects];
                rewritten[i] = (Math.Abs(height - item.Height) < .5 ? item : SceneObjectClone.At(item, item.X, item.Y, item.Width, height))
                    with { Decoration = refreshed };
            }

            if (rewritten is null) continue;
            layers ??= [.. document.Layers];
            layers[index] = layer with { Objects = rewritten };
        }

        return layers is null ? document : document.WithLayers(layers);
    }

    /// <summary>A legend is as tall as its lines: a heading and one row per entry, no empty tail.</summary>
    public static double LegendHeight(double width, int entries) => Math.Round(width * (.3 + .11 * Math.Max(1, entries)), 2);

    public static bool HasDecorations(MapDocument document) =>
        document.Layers.OfType<ObjectLayer>().Any(layer => HasDecorations(layer.Objects));

    // Every edit asks this. On a map of 50 000 stamps the scan cost 3-4 ms of each cliff-slider
    // step although the objects had not changed (R05, 2026-10-23); an object list is immutable,
    // so its answer is kept for as long as the list itself lives.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<SceneObject>, object> Scanned = [];
    private static readonly object With = new(), Without = new();

    private static bool HasDecorations(IReadOnlyList<SceneObject> objects) => ReferenceEquals(With,
        Scanned.GetValue(objects, static list => list.Any(o => o.Kind == SceneObjectKind.Decoration) ? With : Without));

    /// <summary>
    /// The legend lines of a map, in the order a reader meets them: land and water first, then
    /// rivers and roads, then the placed assets by name («Ель 1» and «Ель 2» are one «Ель»).
    /// Hidden layers do not appear; at most <see cref="DecorationStyle.MaxLegendEntries"/> lines.
    /// </summary>
    public static IReadOnlyList<LegendEntry> BuildLegend(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var entries = new List<LegendEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(LegendEntry entry)
        {
            if (entries.Count < DecorationStyle.MaxLegendEntries && seen.Add(entry.Kind + "|" + entry.Label)) entries.Add(entry);
        }

        foreach (var water in document.Layers.OfType<BaseColorLayer>().Where(l => l.Visible))
            Add(new LegendEntry(LegendEntryKind.Water, MapFillTextures.Find(water.TextureId)?.Title ?? "Вода", water.Color));
        foreach (var land in document.Layers.OfType<LandLayer>().Where(l => l.Visible && l.GeometryTiles.TileCount > 0))
            Add(new LegendEntry(LegendEntryKind.Land,
                MapFillTextures.Find(land.EdgeStyle.FillTextureId)?.Title ?? land.Name, land.EdgeStyle.FillColor));

        var objectLayers = document.Layers.OfType<ObjectLayer>().Where(l => l.Visible).ToArray();
        foreach (var river in objectLayers.SelectMany(l => l.Rivers).Where(r => r.Visible))
            Add(new LegendEntry(LegendEntryKind.River, "Река", river.Style.WaterColor));
        foreach (var road in objectLayers.SelectMany(l => l.Roads).Where(r => r.Visible))
            Add(new LegendEntry(LegendEntryKind.Road, RoadLabel(road.Style.Drawing), road.Style.EdgeColor));

        var names = document.Assets.ToDictionary(a => a.Sha256, a => a.DisplayName, StringComparer.OrdinalIgnoreCase);
        void AddAsset(string sha)
        {
            if (!names.TryGetValue(sha, out var name)) return;
            Add(new LegendEntry(LegendEntryKind.Asset, BaseName(name), "#FF808080", sha));
        }

        foreach (var layer in objectLayers)
        {
            foreach (var item in layer.Objects)
                if (item.Visible && item.Kind == SceneObjectKind.Stamp && item.Stamp is { } stamp) AddAsset(stamp.AssetSha256);
            foreach (var area in layer.PlacementAreas)
            foreach (var variant in area.Variants) AddAsset(variant.Sha256);
            foreach (var path in layer.PlacementPaths)
            foreach (var variant in path.Variants) AddAsset(variant.Sha256);
        }

        return entries;
    }

    public static string RoadLabel(RoadDrawing drawing) => drawing switch
    {
        RoadDrawing.PenRuts => "Дорога с колеёй",
        RoadDrawing.Painterly => "Тракт",
        _ => "Просёлочная дорога",
    };

    /// <summary>«Ель 3» → «Ель», «Дуб (2)» → «Дуб»: variants of one asset share a legend line.</summary>
    public static string BaseName(string name)
    {
        var trimmed = name.Trim();
        var cut = trimmed.TrimEnd(' ', ')', '(', '-', '_', '.', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return cut.Length == 0 ? trimmed : cut;
    }

    /// <summary>
    /// Scale bar layout: a round total length (1; 2; 2,5; 4; 5; 7,5 × 10ⁿ units) that fits into
    /// <paramref name="maxPixels"/>, split into 4 or 5 segments. Without a scale the bar measures pixels.
    /// </summary>
    public static (double TotalPixels, double TotalDistance, int Segments, string Unit) ScaleBarLayout(MapScale? scale, double maxPixels)
    {
        var perPixel = scale?.DistancePerPixel ?? 1;
        var unit = scale?.Unit ?? "px";
        var maxDistance = Math.Max(maxPixels, 1) * perPixel;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(maxDistance)));
        // Round lengths a map reader expects, each with a segment count that keeps the ticks round too.
        (double Factor, int Segments)[] steps = [(7.5, 3), (5, 5), (4, 4), (2.5, 5), (2, 4), (1, 5)];
        var (factor, segments) = steps.First(step => step.Factor * magnitude <= maxDistance + 1e-9);
        var nice = factor * magnitude;
        return (nice / perPixel, nice, segments, unit);
    }
}

/// <summary>A user command plus the decoration refresh it caused, undone and redone as one step.</summary>
public sealed class DecorationRefreshCommand : IEditCommand
{
    private readonly IEditCommand _inner;
    private readonly IReadOnlyList<(LayerId Layer, SceneObject Before, SceneObject After)> _changes;

    public DecorationRefreshCommand(IEditCommand inner, MapDocument edited, MapDocument refreshed)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        var changes = new List<(LayerId, SceneObject, SceneObject)>();
        foreach (var layer in refreshed.Layers.OfType<ObjectLayer>())
        {
            if (edited.FindLayer(layer.Id) is not ObjectLayer previous || ReferenceEquals(previous.Objects, layer.Objects)) continue;
            var before = previous.Objects.Where(o => o.Kind == SceneObjectKind.Decoration).ToDictionary(o => o.Id);
            foreach (var item in layer.Objects)
                if (item.Kind == SceneObjectKind.Decoration && before.TryGetValue(item.Id, out var was) && !ReferenceEquals(was, item))
                    changes.Add((layer.Id, was, item));
        }

        _changes = changes;
    }

    public string Name => _inner.Name;
    public long? MeasuredWorkingBytes => _inner.MeasuredWorkingBytes;
    public MapDocument Apply(MapDocument document) => Replace(_inner.Apply(document), after: true);
    public MapDocument Revert(MapDocument document) => _inner.Revert(Replace(document, after: false));

    private MapDocument Replace(MapDocument document, bool after)
    {
        var result = document;
        foreach (var group in _changes.GroupBy(change => change.Layer))
        {
            var map = group.ToDictionary(change => change.Before.Id, change => after ? change.After : change.Before);
            result = result.WithLayer(group.Key, layer => layer is ObjectLayer objects
                ? objects with { Objects = [.. objects.Objects.Select(item => map.TryGetValue(item.Id, out var swap) ? swap : item)] }
                : layer);
        }

        return result;
    }
}

/// <summary>«Масштаб карты»: sets or clears the real-world size of a pixel (R08).</summary>
public sealed class SetMapScaleCommand : IEditCommand
{
    private readonly MapScale? _before;
    private readonly MapScale? _after;

    public SetMapScaleCommand(MapScale? before, MapScale? after)
    {
        _before = before;
        _after = after;
    }

    public string Name => "Масштаб карты";
    public long? MeasuredWorkingBytes => EditCost.BaseBytes;
    public MapDocument Apply(MapDocument document) => With(document, _after);
    public MapDocument Revert(MapDocument document) => With(document, _before);

    private static MapDocument With(MapDocument document, MapScale? scale) =>
        new(document.Id, document.Title, document.CreatedUtc, document.Canvas with { Scale = scale }, document.Layers,
            document.Assets, document.Notes, document.Checkpoints, document.StampForeground, document.Category);
}
