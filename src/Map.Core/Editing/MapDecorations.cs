using Map.Core.Documents;
using System.Runtime.CompilerServices;

namespace Map.Core.Editing;

/// <summary>
/// Keeps the derived state of cartographic decorations (R08) in step with the map: a scale bar
/// carries the canvas scale, a legend lists what the map actually uses. Runs after every edit in
/// <see cref="EditingSession.Execute"/>, like <see cref="TextOnPath.Follow"/>; maps without
/// decorations share a delta-maintained empty set across edits.
/// </summary>
public static class MapDecorations
{
    private static readonly ConditionalWeakTable<IReadOnlyList<MapAssetReference>, Dictionary<string, string>> AssetNames = new();
    /// <summary>The document with every decoration refreshed; the same instance when nothing changed.</summary>
    public static MapDocument Refresh(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!HasDecorations(document)) return document;
        IReadOnlyList<LegendEntry>? legend = null;
        var replacements = new List<SceneObject>();
        for (var index = 0; index < document.Layers.Count; index++)
        {
            if (document.Layers[index] is not ObjectLayer layer) continue;
            foreach (var item in BoundObjectIndex.For(layer).Decorations.Values)
            {
                if (item.Kind != SceneObjectKind.Decoration || item.Decoration is not { } style) continue;
                var refreshed = style.Kind switch
                {
                    DecorationKind.ScaleBar => style with { Scale = document.Canvas.Scale },
                    DecorationKind.Legend => style with { Entries = legend ??= BuildLegend(document) },
                    _ => style,
                };
                var height = refreshed.Kind == DecorationKind.Legend ? LegendHeight(item.Width, refreshed.Entries.Count) : item.Height;
                if (refreshed.Equals(style) && Math.Abs(height - item.Height) < .5) continue;
                replacements.Add((Math.Abs(height - item.Height) < .5 ? item : SceneObjectClone.At(item, item.X, item.Y, item.Width, height))
                    with { Decoration = refreshed });
            }
        }

        return replacements.Count == 0 ? document : DocumentEditor.ReplaceObjects(document, replacements);
    }

    /// <summary>A legend is as tall as its lines: a heading and one row per entry, no empty tail.</summary>
    public static double LegendHeight(double width, int entries) => Math.Round(width * (.3 + .11 * Math.Max(1, entries)), 2);

    public static bool HasDecorations(MapDocument document) =>
        document.Layers.OfType<ObjectLayer>().Any(layer => BoundObjectIndex.For(layer).Decorations.Count > 0);

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
            Add(new LegendEntry(LegendEntryKind.Water, TextureTitle(document, water.TextureId) ?? "Вода", water.Color));
        foreach (var land in document.Layers.OfType<LandLayer>().Where(l => l.Visible && l.GeometryTiles.TileCount > 0))
            Add(new LegendEntry(LegendEntryKind.Land,
                TextureTitle(document, land.EdgeStyle.FillTextureId) ?? land.Name, land.EdgeStyle.FillColor));

        var objectLayers = document.Layers.OfType<ObjectLayer>().Where(l => l.Visible).ToArray();
        foreach (var river in objectLayers.SelectMany(l => l.Rivers).Where(r => r.Visible))
            Add(new LegendEntry(LegendEntryKind.River, "Река", river.Style.WaterColor));
        foreach (var road in objectLayers.SelectMany(l => l.Roads).Where(r => r.Visible))
            Add(new LegendEntry(LegendEntryKind.Road, RoadLabel(road.Style.Drawing), road.Style.EdgeColor));

        var names = AssetNames.GetValue(document.Assets, static assets =>
            assets.ToDictionary(a => a.Sha256, a => BaseName(a.DisplayName), StringComparer.OrdinalIgnoreCase));
        void AddAsset(string sha)
        {
            if (!names.TryGetValue(sha, out var name)) return;
            Add(new LegendEntry(LegendEntryKind.Asset, name, "#FF808080", sha));
        }

        foreach (var layer in objectLayers)
        {
            foreach (var hash in layer.ObjectStorage.VisibleLegendAssets(names))
            {
                AddAsset(hash);
                if (entries.Count == DecorationStyle.MaxLegendEntries) return entries;
            }
            foreach (var area in layer.PlacementAreas)
            foreach (var variant in area.Variants) AddAsset(variant.Sha256);
            foreach (var path in layer.PlacementPaths)
            foreach (var variant in path.Variants) AddAsset(variant.Sha256);
        }

        return entries;
    }

    /// <summary>A library texture is named after the picture the map carries; a built-in one by its title.</summary>
    private static string? TextureTitle(MapDocument document, string? id) =>
        MapFillTextures.AssetSha256(id) is { } sha
            ? document.Assets.FirstOrDefault(asset => asset.Sha256 == sha)?.DisplayName ?? MapFillTextures.LibraryTitle
            : MapFillTextures.Find(id)?.Title;

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
            var before = BoundObjectIndex.For(previous).Decorations;
            foreach (var item in BoundObjectIndex.ChangedLabels(previous, layer, BoundObjectIndex.For(layer).Decorations))
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
        return _changes.Count == 0 ? document : DocumentEditor.ReplaceObjects(document,
            _changes.Select(change => after ? change.After : change.Before).ToArray());
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

    // Only the canvas changes: the layers stay the same validated list, and the map-wide light,
    // style, cards and the rest are carried over (the former full constructor dropped them).
    private static MapDocument With(MapDocument document, MapScale? scale) =>
        document.WithCanvas(document.Canvas with { Scale = scale });
}
