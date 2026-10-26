using System.Text.RegularExpressions;

namespace Map.Core.Documents;

/// <summary>The travel zone of a star system (R33): green (none), amber (caution), red (forbidden).</summary>
public enum TravelZone { Green, Amber, Red }

/// <summary>
/// A star system in a hex of a sector (R33), in the terms of the open Traveller/Cepheus data:
/// the universal world profile (UWP, e.g. «A788899-C»), bases, trade remarks, travel zone,
/// PBG (population multiplier, belts, gas giants), allegiance and stars.
/// </summary>
public sealed partial record StarSystem
{
    public string Name { get; init; } = "";
    public string Uwp { get; init; } = "X000000-0";
    public string Bases { get; init; } = "";
    public string Remarks { get; init; } = "";
    public TravelZone Zone { get; init; }
    public string Pbg { get; init; } = "";
    public string Allegiance { get; init; } = "";
    public string Stars { get; init; } = "";
    public string Notes { get; init; } = "";

    [GeneratedRegex("^[A-HX][0-9A-HJ-NP-Z]{6}-[0-9A-HJ-NP-Z]$", RegexOptions.CultureInvariant)]
    private static partial Regex UwpPattern();

    public static bool IsValidUwp(string? uwp) => uwp is not null && UwpPattern().IsMatch(uwp);

    public bool IsValid => Name.Length <= 120 && IsValidUwp(Uwp) && Bases.Length <= 8 && Remarks.Length <= 200 && Enum.IsDefined(Zone)
        && Pbg.Length <= 3 && Pbg.All(char.IsDigit) && Allegiance.Length <= 8 && Stars.Length <= 120 && Notes.Length <= 4000;

    /// <summary>The starport class (A best … E poor, X none).</summary>
    public char Starport => Uwp.Length > 0 ? Uwp[0] : 'X';

    /// <summary>The tech level, as a number (0…33, eHex).</summary>
    public int TechLevel => Uwp.Length == 9 ? FromEHex(Uwp[8]) : 0;

    public static int FromEHex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'H' => c - 'A' + 10,
        >= 'J' and <= 'N' => c - 'J' + 18,
        >= 'P' and <= 'Z' => c - 'P' + 23,
        _ => 0,
    };

    /// <summary>The colour of the main star by its spectral class (O blue … M red; white for none).</summary>
    public string StarColor => (Stars.TrimStart().FirstOrDefault()) switch
    {
        'O' => "#FF9BB0FF",
        'B' => "#FFAABFFF",
        'A' => "#FFCAD7FF",
        'F' => "#FFF8F7FF",
        'G' => "#FFFFF4C8",
        'K' => "#FFFFD2A1",
        'M' => "#FFFFB070",
        _ => "#FFF2F2F2",
    };
}

/// <summary>
/// A sector of space (R33): the Traveller sector is 32 columns by 40 rows of flat-topped hexes,
/// numbered «CCRR» from 0101, one parsec each. The field is dark.
/// </summary>
public static class StarSector
{
    public const int Columns = 32, Rows = 40;
    public const string Background = "#FF0A0D18";
    public static readonly HexTravelScale Parsecs = new(1, "пк", 1);

    /// <summary>The four-digit hex number of an offset («0101» for the top-left hex).</summary>
    public static string Number(HexOffset offset) => $"{offset.Column + 1:00}{offset.Row + 1:00}";

    public static HexOffset? Parse(string number) =>
        number.Length == 4 && int.TryParse(number.AsSpan(0, 2), out var c) && int.TryParse(number.AsSpan(2, 2), out var r) && c >= 1 && r >= 1
            ? new HexOffset(c - 1, r - 1) : null;
}
