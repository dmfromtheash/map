namespace Map.Core.Documents;

/// <summary>
/// A style pack (R09d, DEC-094): one choice that sets the whole look of a map at once - the style of the drawing, the
/// cliffs with their scree, the rivers, the roads, the open sea and the colour of the names. The owner's ground materials,
/// the map's shapes, widths and heights stay as they are.
/// </summary>
public sealed record MapStylePack(
    string Id, string Title, MapStyle Style, LandCliffStyle Cliff, RiverStyle River, RoadStyle Road, SeaStyle Sea,
    string NameColor, string NameOutline, double NameOutlineWidth)
{
    /// <summary>The four packs the owner approved on 2026-11-10 (DEC-094).</summary>
    public static IReadOnlyList<MapStylePack> Presets { get; } =
    [
        Make("ink", "Тушь", MapStylePreset.InkWetBrush, "ink-sepia", CliffScree.TwoBenches, RiverStyle.Illustrated(pen: true),
            RoadDrawing.PenRuts, SeaManner.PenLines, "#FF1E1B18", "#00000000", 0),
        Make("parchment", "Пергамент", MapStylePreset.EngravingAquatint, "grey-engraving", CliffScree.WallAndBench, RiverStyle.Illustrated(pen: true),
            RoadDrawing.Country, SeaManner.Engraved, "#FF5B3A1E", "#00000000", 0),
        Make("watercolor", "Акварель", MapStylePreset.WatercolorBigShapes, "ice", CliffScree.None, RiverStyle.Painted(),
            RoadDrawing.Painterly, SeaManner.WatercolorHalo, "#FF3E5566", "#00000000", 0),
        Make("atlas", "Атлас", MapStylePreset.OldMapBlaeu, "atlas", CliffScree.TwoBenches, RiverStyle.Natural(),
            RoadDrawing.Country, SeaManner.BandsAndLines, "#FF6B2A1A", "#FFFFFFFF", 3),
    ];

    private static MapStylePack Make(string id, string title, MapStylePreset style, string cliffLook, CliffScree scree, RiverStyle river,
        RoadDrawing road, SeaManner sea, string nameColor, string nameOutline, double outlineWidth) =>
        new(id, title, MapStyle.For(style) with { Enabled = true },
            LandCliffStyle.Looks.First(look => look.Id == cliffLook).Style with { Scree = scree },
            river, RoadStyle.Preset(road), SeaStyle.For(sea), nameColor, nameOutline, outlineWidth);

    public static MapStylePack? Find(string id) => Presets.FirstOrDefault(pack => pack.Id == id);

    /// <summary>
    /// The map in this pack. Each land keeps its cliff height and lean (its geometry) and takes the pack's cliff look;
    /// every river and road takes the pack's drawing (a road keeps its end caps and whether it blocks light; its
    /// sections lose their own looks); a page with an open sea takes the pack's sea; every name takes its colours.
    /// </summary>
    public MapDocument ApplyTo(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var layers = document.Layers.Select(layer => layer switch
        {
            LandLayer land => land with
            {
                EdgeStyle = land.EdgeStyle with
                {
                    Cliff = Cliff with { Height = land.EdgeStyle.Cliff.Height, Lean = land.EdgeStyle.Cliff.Lean },
                },
            },
            BaseColorLayer { Sea: not null } page => page with { Sea = Sea },
            ObjectLayer objects => objects with
            {
                Rivers = [.. objects.Rivers.Select(river => river with { Style = River })],
                Roads = [.. objects.Roads.Select(road => road with
                {
                    Style = Road with
                    {
                        StartCap = road.Style.StartCap, EndCap = road.Style.EndCap, TaperLength = road.Style.TaperLength,
                        BlocksLight = road.Style.BlocksLight,
                    },
                    Branches = [.. road.Branches.Select(branch => branch with { Style = null })],
                })],
                Objects = [.. objects.Objects.Select(item => item.Text is { } text
                    ? item with { Text = text with { FillColor = NameColor, OutlineColor = NameOutline, OutlineWidth = NameOutlineWidth } }
                    : item)],
            },
            _ => layer,
        }).ToArray();
        return document with { Layers = layers, Style = Style.Normalized() };
    }
}
