namespace Map.Core.Documents;

/// <summary>
/// The years a thing of the map exists (R31): from <see cref="From"/> to <see cref="To"/>
/// inclusive; an open end has no limit. A town founded in 812 and burnt in 1040 is (812, 1040).
/// </summary>
public sealed record Era(int? From, int? To)
{
    public bool Contains(int year) => (From is null || year >= From) && (To is null || year <= To);

    public bool IsValid => From is null || To is null || From <= To;

    public override string ToString() => (From, To) switch
    {
        (null, null) => "всегда",
        ({ } f, null) => $"с {f}",
        (null, { } t) => $"до {t}",
        ({ } f, { } t) => $"{f}–{t}",
    };
}

/// <summary>What a shape is on the political map (R31).</summary>
public enum PoliticalRole { None, State, Disputed }

/// <summary>
/// The timeline of a map (R31): the year shown, the span of the slider and the political mode.
/// Off, the map shows everything, as it always did.
/// </summary>
public sealed record MapTimeline
{
    public bool Enabled { get; init; }
    public int Year { get; init; } = 1000;
    public int First { get; init; } = 0;
    public int Last { get; init; } = 2000;
    public bool PoliticalMode { get; init; }

    public bool IsDefault => !Enabled && !PoliticalMode && Year == 1000 && First == 0 && Last == 2000;

    public void Validate()
    {
        if (First > Last || Year < First || Year > Last || First < -1_000_000 || Last > 1_000_000)
            throw new ArgumentException("Годы шкалы: начало не позже конца, выбранный год внутри.");
    }
}
