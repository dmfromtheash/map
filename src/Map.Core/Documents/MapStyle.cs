namespace Map.Core.Documents;

/// <summary>
/// The style of a whole map (R09, DEC-059, schema v29): one preset that repaints the finished
/// map in another medium, its overall strength, and the medium's effects - each with its own
/// switch and strength, as in the Свет section. The document stores only these numbers; the
/// renderer paints them. Names stay sharp over the style, and the owner's ink contour of the
/// mountains comes back on top of it.
/// </summary>
public sealed record MapStyle
{
    /// <summary>The whole style on or off. Off draws the map exactly as without a style.</summary>
    public bool Enabled { get; init; } = true;

    public MapStylePreset Preset { get; init; } = MapStylePreset.InkWetBrush;

    /// <summary>How much of the style covers the map: 0 = the map as painted, 1 = the full medium.</summary>
    public double Strength { get; init; } = 1;

    /// <summary>Brush hairs along the forms of the map (flow field and strokes along it).</summary>
    public StyleEffect Strokes { get; init; } = new(true, 0.55);

    /// <summary>Wet ink creeping into the paper fibres.</summary>
    public StyleEffect Bleed { get; init; } = new(true, 1);

    /// <summary>Tide lines: pigment piled up where a wet patch dried.</summary>
    public StyleEffect Edges { get; init; } = new(true, 0.75);

    /// <summary>Dry brush: the paper's tooth breaks the stroke where the brush carried little ink.</summary>
    public StyleEffect DryBrush { get; init; } = new(true, 0.25);

    /// <summary>Mist: bands where the ink thins out to paper.</summary>
    public StyleEffect Mist { get; init; } = new(true, 0.55);

    /// <summary>The pen's own texture: hatching along the forms, or stippling.</summary>
    public StyleEffect Pen { get; init; } = new(false, 1);

    /// <summary>Paper fibres and sizing blotches.</summary>
    public StyleEffect Paper { get; init; } = new(true, 0.7);

    /// <summary>The owner's ink contour of the mountains, brought back over the style.</summary>
    public StyleEffect Contour { get; init; } = new(true, 0.9);

    public StyleEffect Sea { get; init; } = new(true, 1);
    public StyleEffect Coast { get; init; } = new(true, 1);
    public StyleEffect ColourWash { get; init; } = new(true, 1);
    public StyleEffect Aquatint { get; init; } = new(true, 1);
    public StyleEffect Plate { get; init; } = new(true, 1);
    public StyleEffect Border { get; init; } = new(true, 1);

    /// <summary>The preset as it comes, every effect at the strength the preset was tuned with.</summary>
    public static MapStyle For(MapStylePreset preset) => preset switch
    {
        MapStylePreset.OldMapBlaeu or MapStylePreset.EngravingAquatint => new MapStyle
        {
            Preset = preset,
            Strokes = new(false, 0), Bleed = new(false, 0), Edges = new(false, 0), DryBrush = new(false, 0),
            Mist = new(false, 0), Pen = new(true, 1), Paper = new(true, 1), Contour = new(true, 1),
        },
        MapStylePreset.InkPenHatching => new MapStyle
        {
            Preset = preset,
            Strokes = new(false, 0), Bleed = new(false, 0), Edges = new(false, 0), DryBrush = new(false, 0),
            Mist = new(false, 0), Pen = new(true, 1), Paper = new(true, 0.25), Contour = new(true, 1),
        },
        MapStylePreset.InkPenStipple => new MapStyle
        {
            Preset = preset,
            Strokes = new(false, 0), Bleed = new(false, 0), Edges = new(false, 0), DryBrush = new(false, 0),
            Mist = new(false, 0), Pen = new(true, 1), Paper = new(true, 0.3), Contour = new(true, 1),
        },
        _ => new MapStyle { Preset = MapStylePreset.InkWetBrush },
    };

    /// <summary>The effects a preset actually uses: the panel shows only these.</summary>
    public static IReadOnlyList<StyleEffectKind> EffectsOf(MapStylePreset preset) => preset switch
    {
        MapStylePreset.OldMapBlaeu =>
            [StyleEffectKind.Pen, StyleEffectKind.Contour, StyleEffectKind.ColourWash, StyleEffectKind.Sea,
             StyleEffectKind.Coast, StyleEffectKind.Paper, StyleEffectKind.Plate, StyleEffectKind.Border],
        MapStylePreset.EngravingAquatint =>
            [StyleEffectKind.Contour, StyleEffectKind.Pen, StyleEffectKind.Aquatint, StyleEffectKind.Sea,
             StyleEffectKind.Coast, StyleEffectKind.Paper, StyleEffectKind.Plate],
        MapStylePreset.InkPenHatching => [StyleEffectKind.Pen, StyleEffectKind.Paper, StyleEffectKind.Contour],
        MapStylePreset.InkPenStipple => [StyleEffectKind.Pen, StyleEffectKind.Paper, StyleEffectKind.Contour],
        _ =>
        [
            StyleEffectKind.Strokes, StyleEffectKind.Bleed, StyleEffectKind.Edges, StyleEffectKind.DryBrush,
            StyleEffectKind.Mist, StyleEffectKind.Paper, StyleEffectKind.Contour,
        ],
    };

    public StyleEffect Effect(StyleEffectKind kind) => kind switch
    {
        StyleEffectKind.Strokes => Strokes,
        StyleEffectKind.Bleed => Bleed,
        StyleEffectKind.Edges => Edges,
        StyleEffectKind.DryBrush => DryBrush,
        StyleEffectKind.Mist => Mist,
        StyleEffectKind.Pen => Pen,
        StyleEffectKind.Paper => Paper,
        StyleEffectKind.Sea => Sea,
        StyleEffectKind.Coast => Coast,
        StyleEffectKind.ColourWash => ColourWash,
        StyleEffectKind.Aquatint => Aquatint,
        StyleEffectKind.Plate => Plate,
        StyleEffectKind.Border => Border,
        _ => Contour,
    };

    public MapStyle WithEffect(StyleEffectKind kind, StyleEffect effect) => kind switch
    {
        StyleEffectKind.Strokes => this with { Strokes = effect },
        StyleEffectKind.Bleed => this with { Bleed = effect },
        StyleEffectKind.Edges => this with { Edges = effect },
        StyleEffectKind.DryBrush => this with { DryBrush = effect },
        StyleEffectKind.Mist => this with { Mist = effect },
        StyleEffectKind.Pen => this with { Pen = effect },
        StyleEffectKind.Paper => this with { Paper = effect },
        StyleEffectKind.Sea => this with { Sea = effect },
        StyleEffectKind.Coast => this with { Coast = effect },
        StyleEffectKind.ColourWash => this with { ColourWash = effect },
        StyleEffectKind.Aquatint => this with { Aquatint = effect },
        StyleEffectKind.Plate => this with { Plate = effect },
        StyleEffectKind.Border => this with { Border = effect },
        _ => this with { Contour = effect },
    };

    /// <summary>Clamps every number into its range and an unknown preset to the first one (files from outside).</summary>
    public MapStyle Normalized() => this with
    {
        Preset = Enum.IsDefined(Preset) ? Preset : MapStylePreset.InkWetBrush,
        Strength = MapLighting.Clamp01(Strength),
        Strokes = Strokes.Normalized(), Bleed = Bleed.Normalized(), Edges = Edges.Normalized(), DryBrush = DryBrush.Normalized(),
        Mist = Mist.Normalized(), Pen = Pen.Normalized(), Paper = Paper.Normalized(), Contour = Contour.Normalized(),
        Sea = Sea.Normalized(), Coast = Coast.Normalized(), ColourWash = ColourWash.Normalized(),
        Aquatint = Aquatint.Normalized(), Plate = Plate.Normalized(), Border = Border.Normalized(),
    };
}

/// <summary>
/// The styles of the map. The ink family is the owner's choice of 2026-10-24 (variants 1, 3 and 4
/// of five): a Japanese wet brush, a pen hatching along the forms, a pen stippling.
/// </summary>
public enum MapStylePreset
{
    InkWetBrush = 0,
    InkPenHatching = 1,
    InkPenStipple = 2,
    OldMapBlaeu = 3,
    EngravingAquatint = 4,
}

public enum StyleEffectKind { Strokes, Bleed, Edges, DryBrush, Mist, Pen, Paper, Contour, Sea, Coast, ColourWash, Aquatint, Plate, Border }

/// <summary>One effect of a style: its own switch and its own strength (0..1).</summary>
public sealed record StyleEffect(bool Enabled, double Strength)
{
    /// <summary>The strength that actually reaches the picture: 0 when switched off.</summary>
    public double Effective => Enabled ? MapLighting.Clamp01(Strength) : 0;

    public StyleEffect Normalized() => this with { Strength = MapLighting.Clamp01(Strength) };
}
