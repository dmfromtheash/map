namespace Map.Core.Documents;

/// <summary>How a colour or asset stroke is dashed along its path.</summary>
public enum StrokePattern
{
    Solid = 0,
    Dotted = 1,
    Dashed = 2,
}

/// <summary>
/// Numeric appearance limits shared by persistence and rendering. A stored value outside this
/// envelope is malformed input, not a request for unbounded native filter work.
/// </summary>
public static class AppearanceLimits
{
    public const double MaxBlur = 32;
}

/// <summary>
/// The one canonical list of blend modes an object style may carry. The renderer, the inspector
/// and persistence validation all read this list, so a mode cannot exist in one place only.
/// </summary>
public static class BlendModes
{
    public const string Normal = "Normal";

    public static IReadOnlyList<string> Supported { get; } =
        ["Normal", "Multiply", "Screen", "Overlay", "Darken", "Lighten", "SoftLight", "HardLight",
         "Difference", "Add"];

    /// <summary>Exact, case-sensitive membership - the stored key is a contract, not free text.</summary>
    public static bool IsSupported(string? blend) =>
        blend is not null && Supported.Contains(blend, StringComparer.Ordinal);
}

/// <summary>
/// Mapping between the user-facing percent scale of Saturation/Brightness/Contrast (0..200 %,
/// neutral 100 %) and the domain's stored offsets (-1..+1, neutral 0). The renderer keeps reading
/// offsets; only the inspector speaks percent.
/// </summary>
public static class ColorAdjustment
{
    public const double NeutralPercent = 100;
    public const double MinPercent = 0;
    public const double MaxPercent = 200;

    public static double PercentToOffset(double percent)
    {
        if (!double.IsFinite(percent))
        {
            throw new ArgumentOutOfRangeException(nameof(percent), "The percent value must be finite.");
        }

        return Math.Clamp((percent - NeutralPercent) / 100.0, -1, 1);
    }

    public static double OffsetToPercent(double offset)
    {
        if (!double.IsFinite(offset))
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The stored offset must be finite.");
        }

        return NeutralPercent + (Math.Clamp(offset, -1, 1) * 100.0);
    }
}

/// <summary>
/// The named drop-shadow choices of every inspector: none, the one standard preset, or the
/// custom values the user typed. Preset values live here so reset is byte-stable.
/// </summary>
public static class ShadowPreset
{
    public const double StandardOffsetX = 4;
    public const double StandardOffsetY = 4;
    public const double StandardBlur = 6;
    public const double StandardOpacity = 0.55;

    /// <summary>True when the four values are exactly the standard preset.</summary>
    public static bool IsStandard(double offsetX, double offsetY, double blur, double opacity) =>
        offsetX == StandardOffsetX && offsetY == StandardOffsetY
        && blur == StandardBlur && opacity == StandardOpacity;
}
