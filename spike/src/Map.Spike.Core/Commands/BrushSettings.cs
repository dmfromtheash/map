namespace Map.Spike.Core.Commands;

/// <param name="Radius">World-unit radius of the dab.</param>
/// <param name="Hardness">0..1. Fraction of the radius that stays at full strength.</param>
/// <param name="Flow">0..1 alpha contributed by a single dab.</param>
/// <param name="ColorArgb">Non-premultiplied 0xAARRGGBB source colour.</param>
public readonly record struct BrushSettings(double Radius, double Hardness, double Flow, uint ColorArgb)
{
    public static BrushSettings Default => new(24, 0.5, 0.85, 0xFF2E7D32);

    /// <summary>Distance between consecutive dabs along a segment.</summary>
    public double Spacing => Math.Max(0.5, Radius * 0.25);
}
