namespace Map.Core.Documents;

/// <summary>How the open sea is drawn around the land (R12, «Вода глубже»; research 2026-10-26-r12-water-styles).</summary>
public enum SeaManner
{
    /// <summary>Береговые линии пером: broken slate lines along the shore, wider apart and fainter outwards.</summary>
    PenLines = 1,
    // 2 was «Изолинии глубин»: removed by the owner on 2026-11-10 (DEC-091); a map saved with it opens as «Рябь штрихом».
    /// <summary>Рябь штрихом: short level strokes, crowded at the shore, thinning out to clear water.</summary>
    Engraved = 3,
    /// <summary>Глубина цветом: three flat watercolour bands, shallow to deep, with wet edges.</summary>
    DepthBands = 4,
    /// <summary>Глубина цветом и две линии пером.</summary>
    BandsAndLines = 5,
    /// <summary>Штрих-тень у берега: level pen lines of uneven length hugging the coast; open water stays clear (schema v48).</summary>
    ShoreHatch = 6,
    /// <summary>Акварельный ореол: pigment pooled at the shore, running out into light paper (schema v48).</summary>
    WatercolorHalo = 7,
    /// <summary>Пена и прибой: a white rim fraying into lace, two fainter surf lines further out (schema v48).</summary>
    Surf = 8,
    /// <summary>Лёд: fast ice along the shore, cracked, and floes thinning out into dark water (schema v48).</summary>
    Ice = 9,
    /// <summary>Отмели и подводные камни: pale shoals with a dotted edge and dark rocks ringed with surf (schema v48).</summary>
    Shoals = 10,
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
        SeaManner.Engraved => new() { Manner = manner, Ink = "#FF2C4252", Shallow = "#FF9CB7BD", Middle = "#FF86A4B0", Deep = "#FF7393A5" },
        SeaManner.DepthBands => new() { Manner = manner, Ink = "#FF2E4455", Shallow = "#FF93B0B3", Middle = "#FF7F9FAE", Deep = "#FF587891" },
        SeaManner.BandsAndLines => new() { Manner = manner, Ink = "#FF2E4455", Shallow = "#FF95B2B5", Middle = "#FF84A3B0", Deep = "#FF62829A" },
        // Owner's choice of 2026-11-09 (page «Вода и обрывы — 10 вариантов»): the colours of «Рябь штрихом».
        SeaManner.ShoreHatch or SeaManner.WatercolorHalo or SeaManner.Surf or SeaManner.Ice or SeaManner.Shoals =>
            new() { Manner = manner, Ink = "#FF2C4252", Shallow = "#FF9CB7BD", Middle = "#FF86A4B0", Deep = "#FF7393A5" },
        _ => new() { Manner = SeaManner.PenLines },
    };

    /// <summary>A manner of schema v48: a map with it cannot be opened by a Map that knows only the first five.</summary>
    public bool NeedsMannersSchema => Manner >= SeaManner.ShoreHatch;

    public static IReadOnlyList<(SeaManner Manner, string Title, string Description)> Manners { get; } =
    [
        (SeaManner.PenLines, "Линии пером", "Четыре прерывистые линии вдоль берега, как обводка гор: у берега плотнее, дальше реже и бледнее."),
        (SeaManner.Engraved, "Рябь штрихом", "Короткие горизонтальные штрихи, как на гравюре: у берега гуще, к открытому морю редеют."),
        (SeaManner.DepthBands, "Глубина цветом", "Три спокойные полосы цвета — мелководье, середина, глубина — с мягкими краями акварели."),
        (SeaManner.BandsAndLines, "Цвет и две линии", "Полосы глубины, бледнее, и две линии пером у самого берега."),
        (SeaManner.ShoreHatch, "Штрих-тень у берега", "Ровные линии пером разной длины прижаты к берегу, как тень суши на воде; открытое море чистое."),
        (SeaManner.WatercolorHalo, "Акварельный ореол", "Краска густая у берега, растекается и сходит в светлую бумагу; у края — сухой затёк."),
        (SeaManner.Surf, "Пена и прибой", "Белая кромка пены вдоль берега распадается в кружево; дальше — две прерывистые линии прибоя."),
        (SeaManner.Ice, "Лёд: припай и льдины", "У берега сплошной лёд с трещинами, дальше льдины, редеющие к тёмной открытой воде."),
        (SeaManner.Shoals, "Отмели и камни", "Светлые отмели с пунктирным краем и тёмные подводные камни в ободке пены недалеко от берега."),
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
