using System.Collections.Immutable;
using Map.Core.Editing;

namespace Map.Core.Documents;

/// <summary>A slot belongs to the segment starting at NodeId, independently of other segments.</summary>
public readonly record struct PlacementPathSlot(Guid NodeId, int Ordinal);
public sealed record PlacementPathNode(Guid Id, double X, double Y);
public sealed record PlacementPathMember(PlacementPathSlot Slot, ObjectId ObjectId);

/// <summary>Saved inputs, concrete ownership and permanent manual exclusions. No pixels or UI state.</summary>
public sealed record PlacementPath
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Путь";
    public string? AssetSetId { get; init; }
    public string? VariantId { get; init; }
    public ImmutableArray<PlacementPathNode> Nodes { get; init; } = [];
    public ImmutableArray<StampVariantSource> Variants { get; init; } = [];
    public ImmutableArray<PlacementPathMember> Members { get; init; } = [];
    public ImmutableHashSet<PlacementPathSlot> Exclusions { get; init; } = [];
    public double Spacing { get; init; } = 72;
    public int Seed { get; init; }
    public StampPlacementRequest Request { get; init; } = new();
    public double RotationDegrees { get; init; }
    public double ScalePercent { get; init; } = 100;
    public int Sublayer { get; init; }
    public double Opacity { get; init; } = 1;
    public StampStyle Style { get; init; } = new("path-template");

    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Nodes.IsDefault || Variants.IsDefault
            || Members.IsDefault || Exclusions is null || Request is null || Style is null || Nodes.Length < 2
            || Nodes.Any(n => n is null || n.Id == Guid.Empty || !double.IsFinite(n.X) || !double.IsFinite(n.Y))
            || Nodes.Select(n => n.Id).Distinct().Count() != Nodes.Length
            || !double.IsFinite(Spacing) || Spacing < 1 || !double.IsFinite(Request.BaseWidth) || Request.BaseWidth <= 0
            || !Enum.IsDefined(Request.RandomAngleStep)
            || !double.IsFinite(RotationDegrees) || !double.IsFinite(ScalePercent) || ScalePercent <= 0
            || !double.IsFinite(Opacity) || Opacity is < 0 or > 1 || Sublayer is < -10 or > 10
            || Variants.IsEmpty || Variants.Any(v => v is null || v.Sha256 is null || v.Sha256.Length != 64
                || !v.Sha256.All(Uri.IsHexDigit) || v.PixelWidth <= 0 || v.PixelHeight <= 0
                || v.MaskSha256 is { } mask && (mask.Length != 64 || !mask.All(Uri.IsHexDigit)))
            || Members.Any(m => m is null || m.Slot.Ordinal < 0 || Exclusions.Contains(m.Slot))
            || Members.Select(m => m.Slot).Distinct().Count() != Members.Length
            || Members.Select(m => m.ObjectId).Distinct().Count() != Members.Length
            || Exclusions.Any(s => s.NodeId == Guid.Empty || s.Ordinal < 0)
            || new[] { Style.Blur, Style.ShadowOffsetX, Style.ShadowOffsetY, Style.ShadowBlur,
                Style.ShadowOpacity, Style.Hue, Style.Saturation, Style.Brightness, Style.Contrast }.Any(v => !double.IsFinite(v))
            || Style.Blur < 0 || Style.ShadowBlur < 0 || Style.ShadowOpacity is < 0 or > 1)
            throw new ArgumentException("Некорректная геометрия, параметры или связи пути.");
    }

    /// <summary>Validate a completed row without regenerating images or enumerating the map.</summary>
    public void ValidateMembers()
    {
        Validate();
        var counts = Nodes.Take(Nodes.Length - 1).Select((n, i) => (n.Id, Count: PlacementPathPlanner.SegmentCount(this, i)))
            .ToDictionary(p => p.Id, p => p.Count);
        long expected = counts.Values.Sum(c => (long)c)
            - Exclusions.Count(s => counts.TryGetValue(s.NodeId, out var count) && s.Ordinal < count);
        if (expected != Members.Length || Members.Any(m => !counts.TryGetValue(m.Slot.NodeId, out var count)
            || m.Slot.Ordinal >= count || PlacementPathPlanner.ObjectIdFor(Id, m.Slot) != m.ObjectId))
            throw new ArgumentException("Связи пути не соответствуют его узлам, шагу и исключениям.");
    }
}
