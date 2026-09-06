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
        if (Cells is null || Exclusions is null || MemberCount < 0 || !double.IsFinite(Density)
            || Density < 0 || Density >= int.MaxValue || Exclusions.Any(s => s.Ordinal < 0))
            throw new ArgumentException("Плотность области должна быть неотрицательной; связи и исключения — корректными.");
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
        var estimated = (decimal)spans.Sum(s => (long)s.MaxX - s.MinX + 1) * (decimal)Math.Ceiling(Density) * 96;
        if (estimated > long.MaxValue) throw new ArgumentException("Наполнение области превышает доступные ресурсы.");
        if (!Map.Core.Resources.WorkingMemoryBudget.Shared.TryReserve((long)estimated, out var reservation, out var reason))
            throw new InvalidOperationException(reason);
        using var held = reservation;
        long expected = 0;
        foreach (var span in spans)
            for (var x = span.MinX; x <= span.MaxX; x++)
                foreach (var slot in PlacementAreaPlanner.Slots(this, x, span.Y))
                {
                    expected++;
                    if (!ids.Contains(PlacementAreaPlanner.ObjectIdFor(Id, slot)))
                        throw new ArgumentException("Наполнение области не соответствует границе, плотности и исключениям.");
                }
        if (expected != MemberCount) throw new ArgumentException("Область содержит лишние управляемые объекты.");
    }
}
