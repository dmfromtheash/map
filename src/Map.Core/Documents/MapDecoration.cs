using System.Globalization;

namespace Map.Core.Documents;

/// <summary>
/// Real-world size of one canvas pixel (R08): «1 px = 0,25 км». Stored on the canvas; null on
/// maps that never set it, which then measure in pixels only.
/// </summary>
public sealed record MapScale
{
    /// <summary>Units offered in the panel; any non-empty name is accepted from a file.</summary>
    public static readonly IReadOnlyList<string> Units = ["км", "миль", "лиг", "вёрст"];

    public MapScale(double distancePerPixel, string unit)
    {
        if (!double.IsFinite(distancePerPixel) || distancePerPixel <= 0)
            throw new ArgumentOutOfRangeException(nameof(distancePerPixel), "A map scale is a positive finite distance per pixel.");
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        DistancePerPixel = distancePerPixel;
        Unit = unit.Trim();
    }

    public double DistancePerPixel { get; }
    public string Unit { get; }

    /// <summary>Distance covered by <paramref name="pixels"/> canvas pixels.</summary>
    public double Distance(double pixels) => pixels * DistancePerPixel;

    /// <summary>«12,5 км» — the distance rounded for a label.</summary>
    public string Format(double pixels) => FormatDistance(Distance(pixels), Unit);

    public static string FormatDistance(double distance, string unit)
    {
        var format = distance >= 100 ? "0" : distance >= 10 ? "0.#" : "0.##";
        return distance.ToString(format, CultureInfo.GetCultureInfo("ru-RU")) + " " + unit;
    }
}

/// <summary>Which cartographic decoration an object is (R08).</summary>
public enum DecorationKind
{
    /// <summary>«Масштабная линейка»: segments labelled in map units, follows the canvas scale.</summary>
    ScaleBar = 0,

    /// <summary>«Роза ветров»: procedural compass rose in one of several styles.</summary>
    Compass = 1,

    /// <summary>«Картуш»: the map title in an ornamental frame.</summary>
    Cartouche = 2,

    /// <summary>«Легенда»: built from what the map actually uses; refreshed after every edit.</summary>
    Legend = 3,
}

/// <summary>What a legend line stands for.</summary>
public enum LegendEntryKind { Asset = 0, Land = 1, Water = 2, Road = 3, River = 4 }

/// <summary>One line of a live legend: a sample and a caption.</summary>
public sealed record LegendEntry
{
    public LegendEntry(LegendEntryKind kind, string label, string color, string? assetSha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(color);
        Kind = kind;
        Label = label;
        Color = color;
        AssetSha256 = assetSha256?.ToLowerInvariant();
    }

    public LegendEntryKind Kind { get; }
    public string Label { get; }

    /// <summary>Sample colour "#AARRGGBB" (fill for land and water, line colour for roads and rivers).</summary>
    public string Color { get; }

    /// <summary>The placed asset the line shows, for <see cref="LegendEntryKind.Asset"/>.</summary>
    public string? AssetSha256 { get; }
}

/// <summary>
/// Appearance and derived state of a decoration object (kind == Decoration). The user edits
/// kind, style, title and colours; <see cref="Scale"/> and <see cref="Entries"/> are derived from
/// the document by <see cref="Map.Core.Editing.MapDecorations.Refresh"/> after each edit and stored
/// so a saved map renders and exports exactly what the editor showed.
/// </summary>
public sealed record DecorationStyle
{
    public const int CompassStyles = 4;
    public const int CartoucheStyles = 3;
    public const int MaxLegendEntries = 24;

    public DecorationKind Kind { get; init; }

    /// <summary>Style number: compass 0..3, cartouche 0..2, scale bar 0..1; legend ignores it.</summary>
    public int Variant { get; init; }

    /// <summary>Cartouche title or legend heading.</summary>
    public string Title { get; init; } = "";

    /// <summary>Cartouche second line (author, year…).</summary>
    public string Subtitle { get; init; } = "";

    public string InkColor { get; init; } = "#FF3B2A1A";
    public string PaperColor { get; init; } = "#FFF1E3C4";

    /// <summary>Derived: the canvas scale at the last refresh (scale bar). Null = pixels only.</summary>
    public MapScale? Scale { get; init; }

    /// <summary>Derived: the legend lines at the last refresh.</summary>
    public IReadOnlyList<LegendEntry> Entries { get; init; } = [];

    public bool Equals(DecorationStyle? other) =>
        other is not null && Kind == other.Kind && Variant == other.Variant && Title == other.Title
        && Subtitle == other.Subtitle && InkColor == other.InkColor && PaperColor == other.PaperColor
        && Equals(Scale, other.Scale) && Entries.SequenceEqual(other.Entries);

    public override int GetHashCode() => HashCode.Combine(Kind, Variant, Title, Subtitle, InkColor, PaperColor, Scale, Entries.Count);

    public static string DefaultName(DecorationKind kind) => kind switch
    {
        DecorationKind.ScaleBar => "Масштабная линейка",
        DecorationKind.Compass => "Роза ветров",
        DecorationKind.Cartouche => "Картуш",
        _ => "Легенда",
    };
}
