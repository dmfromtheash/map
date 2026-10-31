namespace Map.Core.Documents;

/// <summary>
/// How the map is seen in isometry (R38, plan 2.3): the side it is looked at from and how tall the
/// houses, walls and towers stand. Kept in the map (schema v47), so a reopened map shows the same view.
/// </summary>
public sealed record MapIsometric
{
    public const double MinimumHeight = 0.5;
    public const double MaximumHeight = 2.5;

    private readonly int _turn;
    private readonly double _heightScale = 1;

    /// <summary>Quarter turns before the map is viewed: 0 from the south, 1 west, 2 north, 3 east.</summary>
    public int Turn
    {
        get => _turn;
        init => _turn = value is >= 0 and <= 3 ? value : throw new ArgumentOutOfRangeException(nameof(value), "Сторона изометрии — от 0 до 3.");
    }

    /// <summary>Multiplies every height: 1 is the usual town.</summary>
    public double HeightScale
    {
        get => _heightScale;
        init => _heightScale = double.IsFinite(value) && value >= MinimumHeight && value <= MaximumHeight
            ? value : throw new ArgumentOutOfRangeException(nameof(value), "Высота изометрии — от 0,5 до 2,5.");
    }

    public bool IsDefault => Turn == 0 && HeightScale == 1;
}
