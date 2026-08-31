namespace Map.Core.Documents;

/// <summary>
/// One dimension policy shared by map creation and later canvas operations. The document model
/// itself remains able to materialize historical snapshots; user-created dimensions are admitted
/// through this policy instead of duplicating the limits in presentation code.
/// </summary>
public static class CanvasDimensionPolicy
{
    public const int Minimum = 16;
    public const int Maximum = 131_072;

    public static bool IsValid(int value) => value is >= Minimum and <= Maximum;
}

/// <summary>
/// Fixed page of the map in document units. The unit is a stored string ("px" for now) so the
/// format does not need a schema change when physical units arrive later (docs/05 manifest).
/// </summary>
public sealed record CanvasDefinition
{
    public CanvasDefinition(int width, int height, string unit, string background)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentException.ThrowIfNullOrWhiteSpace(background);

        Width = width;
        Height = height;
        Unit = unit;
        Background = background;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Document unit name, e.g. "px".</summary>
    public string Unit { get; }

    /// <summary>Page colour as "#AARRGGBB". Stored verbatim; compared exactly.</summary>
    public string Background { get; }
}
