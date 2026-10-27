namespace Map.Core.Documents;

/// <summary>How the open sea is drawn around the land (R12, «Вода глубже»; research 2026-10-26-r12-water-styles).</summary>
public enum SeaManner
{
    /// <summary>Береговые линии пером: broken slate lines along the shore, wider apart and fainter outwards.</summary>
    PenLines = 1,
    /// <summary>Изолинии глубин: contours at equal steps, every fourth heavier, as on a sea chart.</summary>
    Isobaths = 2,
    /// <summary>Рябь штрихом: short level strokes, crowded at the shore, thinning out to clear water.</summary>
    Engraved = 3,
    /// <summary>Глубина цветом: three flat watercolour bands, shallow to deep, with wet edges.</summary>
    DepthBands = 4,
    /// <summary>Глубина цветом и две линии пером.</summary>
    BandsAndLines = 5,
}

/// <summary>
/// The style of the open sea, kept on the base colour layer - the page that is the sea (schema v32).
/// Depth is read as the distance from the nearest shore of any land; the renderer draws the bands
/// and lines over the page colour, under the land and its own coast. Null on the layer keeps the
/// plain sea every earlier map has.
/// </summary>
public sealed record SeaStyle
{
    public SeaManner Manner { get; init; } = SeaManner.PenLines;

    /// <summary>How strongly the style covers the plain page colour, 0..1.</summary>
    public double Strength { get; init; } = 1;

    /// <summary>
    /// How far from the shore the bands and lines reach, as a multiple of the drawn sizes (0.25..4):
    /// a small island map wants them close, a continent far out.
    /// </summary>
    public double Scale { get; init; } = 1;

    public string Ink { get; init; } = "#FF2E4455";
    public string Shallow { get; init; } = "#FF93B0B8";
    public string Middle { get; init; } = "#FF7F9FAE";
    public string Deep { get; init; } = "#FF6F90A2";

    public const double MinimumScale = 0.25, MaximumScale = 4;

    /// <summary>How far from the shore anything of the sea style reaches at scale 1, in world units.</summary>
    public const double BaseReach = 260;

    public double Reach => BaseReach * Math.Clamp(double.IsFinite(Scale) ? Scale : 1, MinimumScale, MaximumScale);

    /// <summary>
    /// The sea of a new map (DEC-069): «Рябь штрихом». Maps that already exist keep the sea saved in them.
    /// </summary>
    public static SeaStyle NewMapDefault => For(SeaManner.Engraved);

    /// <summary>The ready look of each manner: the colours chosen with the owner's land (mockups of 2026-10-26).</summary>
    public static SeaStyle For(SeaManner manner) => manner switch
    {
        SeaManner.Isobaths => new() { Manner = manner, Ink = "#FF34505F", Shallow = "#FF9AB6BB", Middle = "#FF86A5B1", Deep = "#FF6B8CA0" },
        SeaManner.Engraved => new() { Manner = manner, Ink = "#FF2C4252", Shallow = "#FF9CB7BD", Middle = "#FF86A4B0", Deep = "#FF7393A5" },
        SeaManner.DepthBands => new() { Manner = manner, Ink = "#FF2E4455", Shallow = "#FF93B0B3", Middle = "#FF7F9FAE", Deep = "#FF587891" },
        SeaManner.BandsAndLines => new() { Manner = manner, Ink = "#FF2E4455", Shallow = "#FF95B2B5", Middle = "#FF84A3B0", Deep = "#FF62829A" },
        _ => new() { Manner = SeaManner.PenLines },
    };

    public static IReadOnlyList<(SeaManner Manner, string Title, string Description)> Manners { get; } =
    [
        (SeaManner.PenLines, "Линии пером", "Четыре прерывистые линии вдоль берега, как обводка гор: у берега плотнее, дальше реже и бледнее."),
        (SeaManner.Isobaths, "Изолинии", "Линии равной глубины, каждая четвёртая жирнее; дальше от берега вода темнее. Как на морской карте."),
        (SeaManner.Engraved, "Рябь штрихом", "Короткие горизонтальные штрихи, как на гравюре: у берега гуще, к открытому морю редеют."),
        (SeaManner.DepthBands, "Глубина цветом", "Три спокойные полосы цвета — мелководье, середина, глубина — с мягкими краями акварели."),
        (SeaManner.BandsAndLines, "Цвет и две линии", "Полосы глубины, бледнее, и две линии пером у самого берега."),
    ];

    /// <summary>This style with every number inside its range and every colour readable.</summary>
    public SeaStyle Bounded()
    {
        var fallback = For(Enum.IsDefined(Manner) ? Manner : SeaManner.PenLines);
        return this with
        {
            Manner = fallback.Manner,
            Strength = double.IsFinite(Strength) ? Math.Clamp(Strength, 0, 1) : 1,
            Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, MinimumScale, MaximumScale) : 1,
            Ink = AppearanceColor.Normalize(Ink) ?? fallback.Ink,
            Shallow = AppearanceColor.Normalize(Shallow) ?? fallback.Shallow,
            Middle = AppearanceColor.Normalize(Middle) ?? fallback.Middle,
            Deep = AppearanceColor.Normalize(Deep) ?? fallback.Deep,
        };
    }
}
