using System.Collections.Immutable;

namespace Map.Core.Documents;

/// <summary>What a transition is (R29).</summary>
public enum TransitionKind { Stairs, CaveEntrance, Shaft, Portal }

/// <summary>Which way a transition may be taken: both ways, only from its first end, only from its second.</summary>
public enum TransitionDirection { Both, Forward, Back }

/// <summary>The level of the world a map of the atlas belongs to (R29).</summary>
public enum WorldLevel { Sky = 1, Surface = 0, Underdark = -1 }

/// <summary>
/// One end of a transition: a point on this map (<see cref="MapId"/> null) or on another map of the
/// atlas, and for a hex map the level of the world it lands on.
/// </summary>
public sealed record TransitionEnd(Guid? MapId, double X, double Y, int? HexLevel = null)
{
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && Math.Abs(X) <= 1e7 && Math.Abs(Y) <= 1e7 && HexLevel is null or (>= -64 and <= 64);
}

/// <summary>
/// A way between levels or maps (R29): stairs, a cave mouth, a shaft or a portal with two ends,
/// a direction and a cost in minutes of travel. The first end is always on the map that keeps
/// the transition; the second may be on this map (another hex level) or on another map of the
/// atlas, or not set yet - the check reports it.
/// </summary>
public sealed record MapTransition
{
    public const int MaxNameLength = 120;

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Переход";
    public TransitionKind Kind { get; init; }
    public TransitionEnd From { get; init; } = new(null, 0, 0);
    public TransitionEnd? To { get; init; }
    public TransitionDirection Direction { get; init; }
    public double CostMinutes { get; init; } = 10;

    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > MaxNameLength || !Enum.IsDefined(Kind) || !Enum.IsDefined(Direction)
            || From is null || From.MapId is not null || !From.IsValid || To is { IsValid: false }
            || !double.IsFinite(CostMinutes) || CostMinutes is < 0 or > 1_000_000)
            throw new ArgumentException("Некорректный переход.");
    }

    public static string KindTitle(TransitionKind kind) => kind switch
    {
        TransitionKind.Stairs => "Лестница",
        TransitionKind.CaveEntrance => "Вход в пещеру",
        TransitionKind.Shaft => "Шахта",
        _ => "Портал",
    };

    public static string LevelTitle(WorldLevel level) => level switch
    {
        WorldLevel.Sky => "Небо",
        WorldLevel.Underdark => "Подземье",
        _ => "Поверхность",
    };
}

/// <summary>A problem with a transition's ends found by the check (R29).</summary>
public sealed record TransitionIssue(Guid TransitionId, string Name, string Problem, double X, double Y);
