namespace Map.Core.Documents;

/// <summary>
/// The category a map was created in: which workshop of Map owns it.
/// </summary>
/// <remarks>
/// Categories are separate products inside one application. They share the canvas, the file
/// container, rendering and the performance core, but not their tools, placement or generation,
/// so a document never changes its category and two categories never mix in one map. Core knows
/// only the stable lower-case token written to the container; the title, description and the
/// workshop behind a category live in Presentation and the application. The default value of the
/// struct is the standard category, so every document created before categories existed keeps
/// its meaning without a migration.
/// </remarks>
public readonly struct MapCategoryId : IEquatable<MapCategoryId>
{
    /// <summary>The standard map constructor: everything Map could do before categories existed.</summary>
    public const string StandardValue = "standard";

    public const int MaxLength = 64;

    private readonly string? _value;

    public MapCategoryId(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"A map category id is 1–{MaxLength} characters of a–z, 0–9 and '-', not starting or ending with '-'.",
                nameof(value));
        }

        _value = value;
    }

    public static MapCategoryId Standard => new(StandardValue);

    /// <summary>The token; an uninitialised id reads as the standard category.</summary>
    public string Value => _value ?? StandardValue;

    public bool IsStandard => string.Equals(Value, StandardValue, StringComparison.Ordinal);

    public static bool IsValid(string? value)
    {
        if (value is null || value.Length is 0 or > MaxLength || value[0] == '-' || value[^1] == '-')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        return true;
    }

    public static MapCategoryId Parse(string value) => new(value);

    public bool Equals(MapCategoryId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is MapCategoryId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    public static bool operator ==(MapCategoryId left, MapCategoryId right) => left.Equals(right);

    public static bool operator !=(MapCategoryId left, MapCategoryId right) => !left.Equals(right);
}
