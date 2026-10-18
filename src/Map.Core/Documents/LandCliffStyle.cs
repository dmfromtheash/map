namespace Map.Core.Documents;

/// <summary>How the foot of a cliff ends on the ground below it.</summary>
public enum CliffFoot
{
    /// <summary>An ink line along the foot, as in a pen drawing.</summary>
    Ink = 0,
    /// <summary>The wash of the wall fades out downwards; no line.</summary>
    Fade = 1,
    /// <summary>The wall simply ends, no line and no fade.</summary>
    Plain = 2,
}

/// <summary>
/// The look of the cliffs of a raised piece of land: the wall of rock below every southern edge
/// that stands higher than the ground or sea in front of it. The wall is drawn the way the owner's
/// tier sheets draw it: it is split into faces where the coast turns, each face has its own flat
/// tone by the way it faces the light, ink lines mark the corners between faces, and the faces are
/// filled with hatching, stippling, a wash and strata in any mix. Every value is appearance only;
/// the geometry of the land never changes. Defaults are the ink-and-sepia look.
/// </summary>
public sealed record LandCliffStyle
{
    /// <summary>Height of one level of difference, in world units; 0 switches cliffs off.</summary>
    public double Height { get; init; } = 34;
    /// <summary>How much the height rises and falls along the edge: peaks and dips, 0..1.</summary>
    public double HeightVariation { get; init; } = 0.35;
    /// <summary>Distance between two peaks of that variation, in world units.</summary>
    public double VariationLength { get; init; } = 70;
    /// <summary>Share of the edge with no wall at all, only the rim line, 0..1.</summary>
    public double Gaps { get; init; } = 0.15;
    /// <summary>Typical length of a wall or of a gap along the edge, in world units.</summary>
    public double GapLength { get; init; } = 140;
    /// <summary>Tilt of the view: the wall drops down and to the right (+) or left (−), −1..1.</summary>
    public double Lean { get; init; }

    /// <summary>How flat each face is: 0 a smooth turn of the light, 1 hard faces with one tone each.</summary>
    public double Facets { get; init; } = 0.75;
    /// <summary>Typical width of one face along a straight edge, in world units.</summary>
    public double FacetWidth { get; init; } = 22;
    /// <summary>Strength of the ink lines on the corners between faces, 0..1.</summary>
    public double FacetLines { get; init; } = 0.9;

    /// <summary>The lit face of the rock.</summary>
    public string Color { get; init; } = "#FFE6D3AE";
    /// <summary>The face turned away from the light, and the deepest wash.</summary>
    public string ShadeColor { get; init; } = "#FF4E3218";
    /// <summary>Contrast between a face turned to the light and one turned away, 0..1.</summary>
    public double Contrast { get; init; } = 0.55;
    /// <summary>
    /// The wash down the wall, −1..1: below zero the wall is darkest under the rim and lightens
    /// downwards (an ink-and-wash drawing), above zero it darkens towards the foot.
    /// </summary>
    public double Wash { get; init; } = -0.85;
    /// <summary>A dark band right under the rim, as a pen shades the overhang, 0..1.</summary>
    public double RimShadow { get; init; } = 0.35;

    /// <summary>Strength of the pen hatching on the faces, 0..1.</summary>
    public double Hatching { get; init; } = 0.85;
    /// <summary>Distance between two hatching strokes, in world units.</summary>
    public double HatchSpacing { get; init; } = 3.6;
    /// <summary>Length of the strokes as a share of the wall, from the rim down, 0..1.</summary>
    public double HatchLength { get; init; } = 0.75;
    /// <summary>Slant of the strokes: 0 vertical, 1 strongly slanted to the right, −1 to the left.</summary>
    public double HatchSlant { get; init; } = 0.3;

    /// <summary>Density of the stippled dots, thickest under the rim, 0..1.</summary>
    public double Stipple { get; init; }
    /// <summary>Size of one stippled dot, in world units.</summary>
    public double StippleSize { get; init; } = 1.1;

    /// <summary>Layers of rock in one step (0 none), drawn as thin bands across the face.</summary>
    public int Strata { get; init; }

    /// <summary>Colour of every pen line of the wall: corners, foot, sides and strokes.</summary>
    public string InkColor { get; init; } = "#FF231A12";
    /// <summary>Width of the pen lines, in world units.</summary>
    public double InkWidth { get; init; } = 1.6;

    /// <summary>How the foot of the wall ends.</summary>
    public CliffFoot Foot { get; init; } = CliffFoot.Fade;
    /// <summary>How ragged the foot is: 0 straight, 1 waves and hanging teeth, 0..1.</summary>
    public double FootRoughness { get; init; } = 0.45;
    /// <summary>Short pen ticks and roots hanging below the foot, 0..1.</summary>
    public double FootTicks { get; init; }

    /// <summary>Opacity of the soft shadow the wall casts on the ground or water at its foot, 0..1.</summary>
    public double BaseShadow { get; init; } = 0.3;
    /// <summary>Length of that shadow as a share of one level's height, 0.1..2.</summary>
    public double BaseShadowLength { get; init; } = 0.5;
    /// <summary>Colour of the shadow at the foot.</summary>
    public string BaseShadowColor { get; init; } = "#FF4A3520";

    public const double MaximumHeight = 256;
    public const int MaximumStrata = 8;

    /// <summary>Number of steps a land at <paramref name="level"/> shows over ground at <paramref name="floorLevel"/>; the open sea is 0.</summary>
    public int Steps(int level, int floorLevel) => Height > 0 ? Math.Max(0, level - floorLevel) : 0;

    /// <summary>How far below an edge anything of the cliff can reach for this many steps, in world units.</summary>
    public double Reach(int steps) => steps <= 0 || Height <= 0 ? 0
        : (Height * steps * (1 + HeightVariation)) + (Height * BaseShadowLength) + (Height * FootTicks * 0.3) + 2;

    /// <summary>This style with every number inside its range; colours are kept as they are.</summary>
    public LandCliffStyle Bounded() => this with
    {
        Height = Clamp(Height, 0, MaximumHeight, 34),
        HeightVariation = Clamp(HeightVariation, 0, 1, .35),
        VariationLength = Clamp(VariationLength, 8, 1024, 70),
        Gaps = Clamp(Gaps, 0, 0.95, .15),
        GapLength = Clamp(GapLength, 8, 2048, 140),
        Lean = Clamp(Lean, -1, 1, 0),
        Facets = Clamp(Facets, 0, 1, .75),
        FacetWidth = Clamp(FacetWidth, 3, 512, 22),
        FacetLines = Clamp(FacetLines, 0, 1, .9),
        Contrast = Clamp(Contrast, 0, 1, .55),
        Wash = Clamp(Wash, -1, 1, -.85),
        RimShadow = Clamp(RimShadow, 0, 1, .35),
        Hatching = Clamp(Hatching, 0, 1, .85),
        HatchSpacing = Clamp(HatchSpacing, 0.8, 64, 3.6),
        HatchLength = Clamp(HatchLength, 0.05, 1, .75),
        HatchSlant = Clamp(HatchSlant, -1, 1, .3),
        Stipple = Clamp(Stipple, 0, 1, 0),
        StippleSize = Clamp(StippleSize, 0.3, 8, 1.1),
        Strata = Math.Clamp(Strata, 0, MaximumStrata),
        InkWidth = Clamp(InkWidth, 0, 8, 1.6),
        Foot = Enum.IsDefined(Foot) ? Foot : CliffFoot.Fade,
        FootRoughness = Clamp(FootRoughness, 0, 1, .45),
        FootTicks = Clamp(FootTicks, 0, 1, 0),
        BaseShadow = Clamp(BaseShadow, 0, 1, .3),
        BaseShadowLength = Clamp(BaseShadowLength, 0.1, 2, .5),
    };

    private static double Clamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    /// <summary>
    /// Ready looks after the owner's sheets and atlases. Each sets the whole cliff; the land's own
    /// fill and rim line are chosen separately (<see cref="LandCliffLook.Rim"/> suggests them).
    /// </summary>
    public static IReadOnlyList<LandCliffLook> Looks { get; } =
    [
        new("ink-sepia", "Тушь и сепия", "Жирные линии на углах, косая штриховка и сепия, густая под кромкой и тающая книзу (лист «ярусы 2»).",
            new LandCliffStyle(), "#FF1E1710", 2.6),
        new("ink-dots", "Тушь и точки", "Светлые грани в точках, тень под кромкой, волнистый низ с волосками (лист «ярусы 1»).",
            new LandCliffStyle
            {
                Height = 24, HeightVariation = .2, VariationLength = 60, Gaps = .3, GapLength = 110,
                Facets = .85, FacetWidth = 24, FacetLines = 1,
                Color = "#FFD9CBAC", ShadeColor = "#FF8A775B", Contrast = .4, Wash = -.4, RimShadow = .55,
                Hatching = 0, Stipple = 1, StippleSize = .9,
                InkColor = "#FF2A2219", InkWidth = 1.4, Foot = CliffFoot.Ink, FootRoughness = .7, FootTicks = .6,
                BaseShadow = .25, BaseShadowLength = .45, BaseShadowColor = "#FF5B4B38",
            }, "#FF2A2219", 2.2),
        new("grey-engraving", "Гравюра", "Ровные серые грани как сложенная лента, тонкий чёткий контур (лист «ярусы 3»).",
            new LandCliffStyle
            {
                Height = 28, HeightVariation = .3, VariationLength = 80, Gaps = .25, GapLength = 160,
                Facets = 1, FacetWidth = 30, FacetLines = .6,
                Color = "#FF9A9DA3", ShadeColor = "#FF3F434B", Contrast = .7, Wash = -.2, RimShadow = .15,
                Hatching = .15, HatchSpacing = 9, HatchLength = 1, HatchSlant = 0,
                InkColor = "#FF26282D", InkWidth = .9, Foot = CliffFoot.Ink, FootRoughness = .2, FootTicks = 0,
                BaseShadow = .2, BaseShadowLength = .35, BaseShadowColor = "#FF3A3A3A",
            }, "#FF2B2C30", 1.4),
        new("atlas", "Атлас", "Раскрашенный песчаник с пластами и мягкой тенью у моря.",
            new LandCliffStyle
            {
                Height = 32, HeightVariation = .2, VariationLength = 90, Gaps = 0,
                Facets = .45, FacetWidth = 18, FacetLines = .25,
                Color = "#FFDCCBA4", ShadeColor = "#FF7A6650", Contrast = .45, Wash = .5, RimShadow = .1,
                Hatching = .35, HatchSpacing = 5, HatchLength = 1, HatchSlant = 0, Strata = 3,
                InkColor = "#FF5A4630", InkWidth = .8, Foot = CliffFoot.Plain, FootRoughness = .3,
                BaseShadow = .35, BaseShadowLength = .5, BaseShadowColor = "#FF2E3A40",
            }, null, null),
    ];
}

/// <summary>A ready cliff look; <see cref="Rim"/> and <see cref="RimWidth"/> suggest the land's rim line to go with it.</summary>
public sealed record LandCliffLook(string Id, string Title, string Description, LandCliffStyle Style, string? Rim, double? RimWidth);
