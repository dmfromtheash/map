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
    /// <summary>
    /// The four packs the owner approved on 2026-11-10 (DEC-094). Since 2026-11-13 (owner: «только новое и в пакетах»)
    /// every pack keeps the current river («Живая»; «Акварель» - «Живописная») and the painted road: the style of the
    /// drawing repaints them in its medium, the older road and river drawings are gone.
    /// </summary>
    public static IReadOnlyList<MapStylePack> Presets { get; } =
    [
        Make("ink", "Тушь", MapStylePreset.InkWetBrush, "ink-sepia", CliffScree.TwoBenches, RiverStyle.Natural(),
            SeaManner.PenLines, "#FF1E1B18", "#00000000", 0),
        Make("parchment", "Пергамент", MapStylePreset.EngravingAquatint, "grey-engraving", CliffScree.WallAndBench, RiverStyle.Natural(),
            SeaManner.Engraved, "#FF5B3A1E", "#00000000", 0),
        Make("watercolor", "Акварель", MapStylePreset.WatercolorBigShapes, "ice", CliffScree.None, RiverStyle.Painted(),
            SeaManner.WatercolorHalo, "#FF3E5566", "#00000000", 0),
        Make("atlas", "Атлас", MapStylePreset.OldMapBlaeu, "atlas", CliffScree.TwoBenches, RiverStyle.Natural(),
            SeaManner.BandsAndLines, "#FF6B2A1A", "#FFFFFFFF", 3),
    ];

    private static MapStylePack Make(string id, string title, MapStylePreset style, string cliffLook, CliffScree scree, RiverStyle river,
        SeaManner sea, string nameColor, string nameOutline, double outlineWidth) =>
        new(id, title, MapStyle.For(style) with { Enabled = true },
            LandCliffStyle.Looks.First(look => look.Id == cliffLook).Style with { Scree = scree },
            river, RoadStyle.Painted(), SeaStyle.For(sea), nameColor, nameOutline, outlineWidth);

    public static MapStylePack? Find(string id) => Presets.FirstOrDefault(pack => pack.Id == id);

    /// <summary>The id of the owner's own pack (saved in the profile).</summary>
    public const string OwnId = "own";

    /// <summary>
    /// The look of a map as a pack: its style, the cliff of its highest land, its first river, road and named label, and
    /// the sea of its page; what the map has none of comes from the look of a new map.
    /// </summary>
    public static MapStylePack From(MapDocument document, string id, string title)
    {
        ArgumentNullException.ThrowIfNull(document);
        var lands = document.Layers.OfType<LandLayer>().ToList();
        var cliff = lands.OrderByDescending(l => l.Level).FirstOrDefault(l => l.Level > (lands.Count > 0 ? lands.Min(x => x.Level) : 0))?.EdgeStyle.Cliff
            ?? LandCliffStyle.OwnerMountains;
        var objects = document.Layers.OfType<ObjectLayer>().ToList();
        // An own pack saved before 2026-11-13 may hold an older drawing: it comes back in the current one.
        var river = CurrentLookUpgrade.Upgrade(objects.SelectMany(l => l.Rivers).FirstOrDefault()?.Style ?? RiverStyle.Natural());
        var road = CurrentLookUpgrade.Upgrade(objects.SelectMany(l => l.Roads).FirstOrDefault()?.Style ?? RoadStyle.Painted());
        var name = objects.SelectMany(l => l.Objects).FirstOrDefault(o => o.Text is not null)?.Text;
        var sea = document.Layers.OfType<BaseColorLayer>().FirstOrDefault(p => p.Sea is not null)?.Sea ?? SeaStyle.NewMapDefault;
        return new MapStylePack(id, title, document.Style ?? new MapStyle(), cliff, river, road, sea,
            name?.FillColor ?? "#FF20242B", name?.OutlineColor ?? "#00000000", name?.OutlineWidth ?? 0);
    }

    /// <summary>A small map that holds every part of this pack, for a file that <see cref="From"/> reads back.</summary>
    public MapDocument ToTemplate()
    {
        var full = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var tiles = TileMap.Empty(1).With(new TileKey(0, 0), full);
        var ground = new LandLayer(LayerId.New(), "Суша", true, false, 1, tiles, LandEdgeStyle.SoftWatercolor());
        var raised = new LandLayer(LayerId.New(), "Ярус", true, false, 1, tiles, LandEdgeStyle.SoftWatercolor() with { Cliff = Cliff }) { Level = 1 };
        var river = new River
        {
            Name = "Река", Style = River,
            Channels = [Editing.RiverEditor.NewChannel([new VectorPoint(10, 10), new VectorPoint(200, 200)], 12)],
        };
        var road = Editing.RoadEditor.NewRoad([new VectorPoint(20, 240), new VectorPoint(240, 250)], 8, Road, "Дорога", seed: 1);
        var label = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Подпись", 10, 10, 120, 30, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = "Подпись", FillColor = NameColor, OutlineColor = NameOutline, OutlineWidth = NameOutlineWidth } };
        return new MapDocument(DocumentId.New(), Title, DateTimeOffset.UnixEpoch, new CanvasDefinition(256, 256, "px", "#FF9DB7C2"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF9DB7C2") { Sea = Sea }, ground, raised,
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [label]) { Rivers = [river], Roads = [road] }])
        { Style = Style };
    }

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
