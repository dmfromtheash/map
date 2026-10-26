using System.Collections.Immutable;

namespace Map.Core.Documents;

/// <summary>
/// Light and time of day of a map (DEC-057, schema v28): one time slider and separate effects,
/// each with its own switch and strength, plus the map's own light sources. The document only
/// stores these numbers; the renderer turns them into light, shadows, fog and colour.
/// </summary>
public sealed record MapLighting
{
    public const int MaxSources = 256;

    /// <summary>The whole light section on or off. Off draws the map exactly as without lighting.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>0 = day, 0.5 = evening, 1 = night.</summary>
    public double TimeOfDay { get; init; }

    /// <summary>Volume of the painted mountains under the sun (normal maps), strength 0..1.</summary>
    public LightingEffect Relief { get; init; } = new(true, 0.6);

    /// <summary>Shadows of objects laid from their base along the light, strength 0..1.</summary>
    public LightingEffect Shadows { get; init; } = new(true, 0.8);

    /// <summary>Low-lying haze that keeps off the peaks, strength 0..1.</summary>
    public LightingEffect Fog { get; init; } = new(true, 0.5);

    /// <summary>Torches, windows and fires of the map, strength 0..1 (they light up at evening and night).</summary>
    public LightingEffect Lights { get; init; } = new(true, 0.8);

    /// <summary>Colour of the time of day: warm evening, cool night (white balance, exposure, saturation), 0..1.</summary>
    public LightingEffect Color { get; init; } = new(true, 1);

    public ImmutableArray<MapLightSource> Sources { get; init; } = [];

    /// <summary>Clamps every number into its range and drops invalid sources (files from outside).</summary>
    public MapLighting Normalized() => this with
    {
        TimeOfDay = Clamp01(TimeOfDay),
        Relief = Relief.Normalized(), Shadows = Shadows.Normalized(), Fog = Fog.Normalized(),
        Lights = Lights.Normalized(), Color = Color.Normalized(),
        Sources = Sources.IsDefault ? [] : [.. Sources.Where(s => s is not null && s.IsValid).Take(MaxSources).Select(s => s.Normalized())],
    };

    internal static double Clamp01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}

/// <summary>One effect of the light section: its own switch and its own strength (0..1).</summary>
public sealed record LightingEffect(bool Enabled, double Strength)
{
    /// <summary>The strength that actually reaches the picture: 0 when switched off.</summary>
    public double Effective => Enabled ? MapLighting.Clamp01(Strength) : 0;

    public LightingEffect Normalized() => this with { Strength = MapLighting.Clamp01(Strength) };
}

/// <summary>A light of the map: a torch, a window, a camp fire. Position in map pixels.</summary>
public sealed record MapLightSource(Guid Id, double X, double Y, string Color, double Radius, double Intensity, bool Flicker)
{
    public const double MinRadius = 8;
    public const double MaxRadius = 4096;

    public const double MaxDimZone = 2;

    /// <summary>
    /// The dim zone around the bright circle (R14), as a share of <see cref="Radius"/>, 0..2: the
    /// light reaches out to <see cref="DimRadius"/> at a low level, the way a virtual tabletop has
    /// bright and dim light. Zero - every light before R14 - keeps only the bright circle.
    /// </summary>
    public double DimZone { get; init; }

    /// <summary>How far the light reaches with its dim zone, in map pixels.</summary>
    public double DimRadius => Radius * (1 + Math.Clamp(double.IsFinite(DimZone) ? DimZone : 0, 0, MaxDimZone));

    public bool IsValid => Id != Guid.Empty && double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Radius)
        && double.IsFinite(Intensity) && !string.IsNullOrWhiteSpace(Color);

    public MapLightSource Normalized() => this with
    {
        Radius = Math.Clamp(Radius, MinRadius, MaxRadius),
        Intensity = Math.Clamp(Intensity, 0, 2),
        DimZone = double.IsFinite(DimZone) ? Math.Clamp(DimZone, 0, MaxDimZone) : 0,
    };
}
