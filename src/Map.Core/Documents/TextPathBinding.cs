using System.Collections.Immutable;

namespace Map.Core.Documents;

/// <summary>What kind of line a label follows (R07, S2/H15).</summary>
public enum TextPathTarget
{
    River = 0,
    Road = 1,
    HexPath = 2,
}

/// <summary>Where the label sits relative to the line, as the reader sees it.</summary>
public enum TextPathSide
{
    Above = 0,
    On = 1,
    Below = 2,
}

/// <summary>
/// A text label bound to a river channel, a road branch or a hex path. The binding (target, part,
/// position, side) is what the user chose; <see cref="Baseline"/> is derived from the target's
/// geometry by <c>TextOnPath</c> after every edit and saved with the label, so the renderer, the
/// tiles and every export draw the label without looking the target up. Baseline points are
/// relative to the label object's top-left corner: moving the object moves the drawn text with it.
/// </summary>
public sealed record TextPathBinding
{
    public const int MaxBaselinePoints = 512;

    public TextPathTarget Target { get; init; }

    /// <summary>River, road or hex path id.</summary>
    public Guid TargetId { get; init; }

    /// <summary>River channel or road branch id; null for a hex path.</summary>
    public Guid? PartId { get; init; }

    /// <summary>Where the label's middle sits along the line, 0 = start, 1 = end.</summary>
    public double Position { get; init; } = 0.5;

    public TextPathSide Side { get; init; } = TextPathSide.Above;

    /// <summary>Extra distance between the line's edge and the text, world units.</summary>
    public double Gap { get; init; } = 6;

    /// <summary>
    /// True (default) turns the text so it never reads upside down; false follows the line's own
    /// direction, which <see cref="Reverse"/> can flip.
    /// </summary>
    public bool KeepUpright { get; init; } = true;

    /// <summary>Read the line from its end to its start (only when <see cref="KeepUpright"/> is off).</summary>
    public bool Reverse { get; init; }

    /// <summary>Derived: the line the glyphs sit on, in reading order, relative to the object's top-left.</summary>
    public ImmutableArray<VectorPoint> Baseline { get; init; } = [];

    /// <summary>Derived: true when the target no longer exists; the label keeps its last baseline.</summary>
    public bool Detached { get; init; }

    public bool Equals(TextPathBinding? other) =>
        other is not null
        && Target == other.Target && TargetId == other.TargetId && PartId == other.PartId
        && Position.Equals(other.Position) && Side == other.Side && Gap.Equals(other.Gap)
        && KeepUpright == other.KeepUpright && Reverse == other.Reverse && Detached == other.Detached
        && Baseline.AsSpan().SequenceEqual(other.Baseline.AsSpan());

    public override int GetHashCode() =>
        HashCode.Combine(Target, TargetId, PartId, Position, Side, Gap, Baseline.Length);
}
