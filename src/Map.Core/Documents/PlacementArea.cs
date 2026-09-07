using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Map.Core.Editing;

namespace Map.Core.Documents;

/// <summary>A permanent world-grid address; neither the polygon bounds nor density renumber it.</summary>
public readonly record struct PlacementAreaSlot(int X, int Y, int Ordinal)
{
    [JsonIgnore] public long Cell => ((long)X << 32) | (uint)Y;
}
public sealed record PlacementAreaMember(PlacementAreaSlot Slot, ObjectId ObjectId);

public enum PlacementAreaPattern { Masses, Ridge, Groups, Clearings }

/// <summary>A small shared silhouette, frozen per source image, not per placed object.</summary>
public sealed record StampContentMask(ImmutableArray<uint> Rows)
{
    public const int Edge = 32;
    public void Validate()
    {
        if (Rows.IsDefault || Rows.Length != Edge || !Rows.Any(r => r != 0))
            throw new ArgumentException("Некорректный силуэт изображения области.");
    }
}

/// <summary>Normalized alpha bounds, measured once from the shared source and frozen with the area.</summary>
public readonly record struct StampContentBounds(double Left, double Top, double Right, double Bottom)
{
    public static StampContentBounds Full => new(0, 0, 1, 1);
    [JsonIgnore] public double Width => Right - Left;
    [JsonIgnore] public double Height => Bottom - Top;
    public void Validate()
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top) || !double.IsFinite(Right) || !double.IsFinite(Bottom)
            || Left < 0 || Top < 0 || Right > 1 || Bottom > 1 || Width <= 0 || Height <= 0)
            throw new ArgumentException("Некорректные границы видимой части изображения области.");
    }
}

/// <summary>Immutable fill inputs and a persistent cell ownership map. Pixels remain shared assets.</summary>
public sealed record PlacementArea
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Область";
    public string? AssetSetId { get; init; }
    public string? VariantId { get; init; }
    public ImmutableArray<PlacementPathNode> Nodes { get; init; } = [];
    public ImmutableArray<StampVariantSource> Variants { get; init; } = [];
    public ImmutableDictionary<long, ImmutableArray<PlacementAreaMember>> Cells { get; init; } = ImmutableDictionary<long, ImmutableArray<PlacementAreaMember>>.Empty;
    public ImmutableHashSet<PlacementAreaSlot> Exclusions { get; init; } = [];
    // Missing in early Stage 18 files: preserve their materialized scatter until explicit repack.
    public int LayoutVersion { get; init; }
    public VectorPoint CompositionOrigin { get; init; }
    public double CompositionLength { get; init; }
    public double CompositionAngleDegrees { get; init; }
    public PlacementAreaPattern Pattern { get; init; }
    public bool JoinEdges { get; init; }
    public ImmutableDictionary<string, StampContentBounds> ContentBounds { get; init; } = ImmutableDictionary<string, StampContentBounds>.Empty;
    public ImmutableDictionary<string, StampContentMask> ContentMasks { get; init; } = ImmutableDictionary<string, StampContentMask>.Empty;
    public double SizeVariationPercent { get; init; } = 30;
    public ImmutableHashSet<PlacementAreaSlot> OccupiedSlots { get; init; } = [];
    public int MemberCount { get; init; }
    public double Density { get; init; } = 5;
    public int Seed { get; init; }
    public StampPlacementRequest Request { get; init; } = new();
    public double RotationDegrees { get; init; }
    public double ScalePercent { get; init; } = 100;
    public int Sublayer { get; init; }
    public double Opacity { get; init; } = 1;
    public StampStyle Style { get; init; } = new("area-template");
    [JsonIgnore] public IEnumerable<PlacementAreaMember> Members => Cells.Values.SelectMany(c => c);

    public void Validate()
    {
        // Share Stage 17's frozen stamp-input validation, without changing the path format.
        new PlacementPath { Id = Id, Name = Name, Nodes = Nodes, Variants = Variants, Request = Request,
            RotationDegrees = RotationDegrees, ScalePercent = ScalePercent, Sublayer = Sublayer,
            Opacity = Opacity, Style = Style }.Validate();
        if (Cells is null || Exclusions is null || OccupiedSlots is null || ContentBounds is null || ContentMasks is null || LayoutVersion is < 0 or > 4
            || !double.IsFinite(CompositionOrigin.X) || !double.IsFinite(CompositionOrigin.Y)
            || !double.IsFinite(CompositionLength) || CompositionLength < 0 || CompositionLength > CanvasDimensionPolicy.Maximum * 2d
            || !double.IsFinite(CompositionAngleDegrees) || CompositionAngleDegrees is < -180 or > 180 || !Enum.IsDefined(Pattern)
            || !double.IsFinite(SizeVariationPercent) || SizeVariationPercent is < 0 or > 50
            || Math.Abs(CompositionOrigin.X) > CanvasDimensionPolicy.Maximum || Math.Abs(CompositionOrigin.Y) > CanvasDimensionPolicy.Maximum
            || OccupiedSlots.Any(s => s.Ordinal < 0) || MemberCount < 0 || !double.IsFinite(Density)
            || Density < 0 || Density >= int.MaxValue || Exclusions.Any(s => s.Ordinal < 0))
            throw new ArgumentException("Плотность области должна быть неотрицательной; связи и исключения — корректными.");
        if (LayoutVersion >= 3 && Density > 100) throw new ArgumentException("Плотность естественной расстановки — от 0 до 100%.");
        foreach (var (sha, bounds) in ContentBounds)
        {
            if (!Variants.Any(v => v.Sha256 == sha)) throw new ArgumentException("Границы принадлежат неизвестному изображению области.");
            bounds.Validate();
        }
        foreach (var (sha, mask) in ContentMasks)
        {
            if (!Variants.Any(v => v.Sha256 == sha) || mask is null)
                throw new ArgumentException("Силуэт принадлежит неизвестному изображению области.");
            mask.Validate();
        }
        PlacementAreaGeometry.Validate(Nodes);
    }

    /// <summary>Full read/write validation only. Local edits validate their own delta.</summary>
    public void ValidateMembers()
    {
        Validate();
        var ids = new HashSet<ObjectId>();
        var count = 0;
        foreach (var (key, members) in Cells)
        {
            if (members.IsDefaultOrEmpty || members.Any(m => m is null || m.Slot.Cell != key
                || m.Slot.Ordinal < 0 || Exclusions.Contains(m.Slot)
                || PlacementAreaPlanner.ObjectIdFor(Id, m.Slot) != m.ObjectId || !ids.Add(m.ObjectId)))
                throw new ArgumentException("Некорректные или повторные связи области.");
            count = checked(count + members.Length);
        }
        if (count != MemberCount) throw new ArgumentException("Число объектов области не совпадает со связями.");
        var spans = PlacementAreaGeometry.Coverage(Nodes);
        var spacingEstimate = PlacementAreaSampler.EstimateBytes(this, spans);
        if (spacingEstimate > long.MaxValue) throw new ArgumentException("Наполнение области превышает доступные ресурсы.");
        var estimated = (decimal)spans.Sum(s => (long)s.MaxX - s.MinX + 1) * PlacementAreaPlanner.CandidatePasses(this) * 96 + spacingEstimate;
        if (estimated > long.MaxValue) throw new ArgumentException("Наполнение области превышает доступные ресурсы.");
        if (!Map.Core.Resources.WorkingMemoryBudget.Shared.TryReserve((long)estimated, out var reservation, out var reason))
            throw new InvalidOperationException(reason);
        using var held = reservation;
        var sampler = new PlacementAreaSampler(this);
        long expected = 0;
        foreach (var span in spans)
            for (var x = span.MinX; x <= span.MaxX; x++)
                foreach (var slot in sampler.Slots(x, span.Y))
                {
                    expected++;
                    if (!ids.Contains(PlacementAreaPlanner.ObjectIdFor(Id, slot)))
                        throw new ArgumentException("Наполнение области не соответствует границе, плотности и исключениям.");
                }
        if (expected != MemberCount) throw new ArgumentException("Область содержит лишние управляемые объекты.");
    }
}
