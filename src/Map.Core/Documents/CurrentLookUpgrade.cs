namespace Map.Core.Documents;

/// <summary>
/// Owner, 2026-11-13: «удали все старые отображения рек и подобного, но так, чтобы не повредить сами реки и дороги…
/// чтобы было только новое». An older map opens in the look of a new map: every river takes «Живая», every road the
/// painted road, every land the owner's snow in the soft watercolour, a page without a sea the sea of a new map.
/// Only the appearance changes - the channels, nodes, widths, names, levels, junctions, branches, land coverage,
/// cliffs and heights stay exactly as saved; the file changes only when the user saves the map.
/// </summary>
public static class CurrentLookUpgrade
{
    /// <summary>The grounds that have pixels today (the owner's own) and library textures.</summary>
    private static readonly HashSet<string> CurrentGrounds = new(StringComparer.Ordinal)
    {
        MapFillTextures.OwnerSnow, MapFillTextures.OwnerSnowSparkle, MapFillTextures.OwnerSnowSoft, MapFillTextures.OwnerIceWash,
    };

    /// <summary>«Живая» and «Живописная» both lay the owner's ice wash; every older river drawing has no texture or a retired one.</summary>
    public static bool IsCurrent(RiverStyle style) => style.TextureId == MapFillTextures.OwnerIceWash;

    /// <summary>The painted road (<see cref="RoadStyle.Painted"/>) is the only one with the owner's painted pass.</summary>
    public static bool IsCurrent(RoadStyle style) => style.PaintStrokes > 0;

    public static bool IsCurrent(LandEdgeStyle style) =>
        style.FillTextureId is { } id && (CurrentGrounds.Contains(id) || MapFillTextures.IsAssetId(id));

    /// <summary>«Живая» river; how the water meets land and sea and how its ends look are the river's own.</summary>
    public static RiverStyle Upgrade(RiverStyle style) => IsCurrent(style) ? style : RiverStyle.Natural() with
    {
        Opacity = style.Opacity, CutsLand = style.CutsLand, ClipToLand = style.ClipToLand,
        MouthFade = style.MouthFade, TaperSource = style.TaperSource,
    };

    /// <summary>The painted road; its end caps, opacity and whether it is a wall in the export are the road's own.</summary>
    public static RoadStyle Upgrade(RoadStyle style) => IsCurrent(style) ? style : RoadStyle.Painted() with
    {
        Opacity = style.Opacity, StartCap = style.StartCap, EndCap = style.EndCap, TaperLength = style.TaperLength,
        BlocksLight = style.BlocksLight,
    };

    /// <summary>The land of a new map; its cliffs and relief (heights, contours, steep ink) are the land's own.</summary>
    public static LandEdgeStyle Upgrade(LandEdgeStyle style) => IsCurrent(style) ? style : LandEdgeStyle.SoftWatercolor() with
    {
        Cliff = style.Cliff, ReliefShading = style.ReliefShading, ReliefContours = style.ReliefContours,
        ReliefContourStep = style.ReliefContourStep, ReliefSteepInk = style.ReliefSteepInk,
    };

    /// <summary>The same document in the current look; the very same instance when nothing in it is old.</summary>
    public static MapDocument Apply(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var hasLand = document.Layers.OfType<LandLayer>().Any();
        var changed = false;
        var layers = document.Layers.Select(layer =>
        {
            MapLayer next = layer switch
            {
                LandLayer land when !IsCurrent(land.EdgeStyle) => land with { EdgeStyle = Upgrade(land.EdgeStyle) },
                // A page drawn as plain colour water: the sea of a new map, drawn from the shores (a map without land keeps its page).
                BaseColorLayer { Sea: null } page when hasLand => page with { Sea = SeaStyle.NewMapDefault },
                ObjectLayer objects when objects.Rivers.Any(r => !IsCurrent(r.Style))
                    || objects.Roads.Any(r => !IsCurrent(r.Style) || r.Branches.Any(b => b.Style is { } own && !IsCurrent(own))) => objects with
                {
                    Rivers = [.. objects.Rivers.Select(river => KeepCopy(river, river with { Style = Upgrade(river.Style) }))],
                    Roads = [.. objects.Roads.Select(road => KeepCopy(road, road with
                    {
                        Style = Upgrade(road.Style),
                        Branches = [.. road.Branches.Select(branch => branch.Style is { } own ? branch with { Style = Upgrade(own) } : branch)],
                    }))],
                },
                _ => layer,
            };
            changed |= !ReferenceEquals(next, layer);
            return next;
        }).ToArray();
        return changed ? document with { Layers = layers } : document;
    }

    // A copy inherited from a parent map (R28b) that nobody edited keeps being one: its fingerprint covers the look, so
    // the new look is written into it - otherwise the atlas sync would take the new look for a hand edit and stop
    // updating the copy. A copy edited by hand stays protected as before.
    private static Road KeepCopy(Road before, Road after) =>
        before.Inherited is { } source && source.Fingerprint == Editing.ScaleSync.Fingerprint(before)
            ? after with { Inherited = source with { Fingerprint = Editing.ScaleSync.Fingerprint(after) } }
            : after;

    private static River KeepCopy(River before, River after) =>
        before.Inherited is { } source && source.Fingerprint == Editing.ScaleSync.Fingerprint(before)
            ? after with { Inherited = source with { Fingerprint = Editing.ScaleSync.Fingerprint(after) } }
            : after;
}
