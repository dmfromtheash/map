using System.Collections.Immutable;

namespace Map.Core.Documents;

/// <summary>What closed figure a region label sits inside (R07b).</summary>
public enum TextRegionTarget { Shape = 0, PlacementArea = 1 }

/// <summary>
/// A label that names a region (R07b): it sits inside a closed shape or a placement area along the
/// figure's widest axis and follows every edit of the outline. <see cref="EntityId"/> takes the
/// text from a card of the world (R26), so renaming the card renames the label. The user's own
/// shift of the label is kept as <see cref="OffsetX"/>/<see cref="OffsetY"/> and survives reopen.
/// </summary>
public sealed record TextRegionBinding
{
    public TextRegionTarget Target { get; init; }
    public Guid TargetId { get; init; }
    public Guid? EntityId { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }

    /// <summary>Derived: true when the figure no longer exists; the label stays where it was.</summary>
    public bool Detached { get; init; }

    public bool IsValid => Enum.IsDefined(Target) && TargetId != Guid.Empty
        && double.IsFinite(OffsetX) && double.IsFinite(OffsetY) && Math.Abs(OffsetX) <= 1e7 && Math.Abs(OffsetY) <= 1e7;
}

/// <summary>
/// How the labels of a map give way to each other (R07b): by scale - a label of importance i shows
/// only from the scale <see cref="MinScales"/>[i - 1] up, so far away only the important names
/// stay; by overlap - of two crossing labels the less important one hides; and as a constraint on
/// placement areas, which then leave the ground under labels empty. All off for older maps.
/// </summary>
public sealed record LabelSettings
{
    public const int MinImportance = 1;
    public const int MaxImportance = 5;
    public const int DefaultImportance = 3;

    /// <summary>The scale (screen pixels per map pixel) from which each importance 1…5 is shown.</summary>
    public static readonly ImmutableArray<double> DefaultMinScales = [0.6, 0.35, 0.18, 0.08, 0];

    public bool ByScale { get; init; }
    public ImmutableArray<double> MinScales { get; init; } = DefaultMinScales;
    public bool ResolveOverlaps { get; init; }
    public bool ClearUnderLabels { get; init; }

    public bool IsDefault => !ByScale && !ResolveOverlaps && !ClearUnderLabels && MinScales.SequenceEqual(DefaultMinScales);

    /// <summary>The smallest scale at which a label of this importance is shown.</summary>
    public double MinScale(int importance) =>
        MinScales.IsDefaultOrEmpty || MinScales.Length != MaxImportance ? 0 : MinScales[Math.Clamp(importance, MinImportance, MaxImportance) - 1];

    public void Validate()
    {
        if (MinScales.IsDefault || MinScales.Length != MaxImportance || MinScales.Any(s => !double.IsFinite(s) || s < 0 || s > 64))
            throw new ArgumentException("Пороги подписей: пять чисел от 0 до 64.");
    }

    public static bool IsValidImportance(int importance) => importance is >= MinImportance and <= MaxImportance;
}
