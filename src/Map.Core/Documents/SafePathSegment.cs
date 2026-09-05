namespace Map.Core.Documents;

/// <summary>
/// Safe Windows filename rules shared by project and catalog relative paths. DOS device
/// aliases keep their special meaning even below a normal directory and even with an extension,
/// so they must be rejected before any path is resolved or probed.
/// </summary>
public static class SafePathSegment
{
    private const string WindowsInvalidCharacters = "<>:\"/\\|?*";

    public static bool IsSafe(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)
            || segment.Any(char.IsControl)
            || segment.Any(WindowsInvalidCharacters.Contains)
            || segment.EndsWith(' ')
            || segment.EndsWith('.'))
        {
            return false;
        }

        var stem = segment.Split('.', 2)[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsNumberedDevice(stem, "COM") && !IsNumberedDevice(stem, "LPT");
    }

    private static bool IsNumberedDevice(string stem, string prefix)
    {
        if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = stem.AsSpan(prefix.Length);
        return suffix.Length == 1
            && (suffix[0] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3');
    }
}
