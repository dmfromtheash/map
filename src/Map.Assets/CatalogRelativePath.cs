namespace Map.Assets;

/// <summary>
/// Validates and resolves the two path namespaces owned by the local catalog. Validation is kept
/// independent of the host OS so a catalog written on one platform cannot become unsafe on
/// another one (for example through backslashes or a Windows alternate-data-stream colon).
/// </summary>
internal static class CatalogRelativePath
{
    public static bool IsValid(string? relative, string requiredTopLevel)
    {
        if (string.IsNullOrWhiteSpace(relative)
            || string.IsNullOrWhiteSpace(requiredTopLevel)
            || relative.Contains('\0')
            || relative.Contains('\\', StringComparison.Ordinal)
            || relative.Contains(':', StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return false;
        }

        var segments = relative.Split('/');
        if (segments.Length < 2
            || !string.Equals(segments[0], requiredTopLevel, StringComparison.Ordinal))
        {
            return false;
        }

        return segments.All(segment =>
            segment is not "." and not ".."
            && Map.Core.Documents.PortablePathSegment.IsSafe(segment));
    }

    public static bool TryResolve(
        string root,
        string? relative,
        string requiredTopLevel,
        out string? resolved)
    {
        resolved = null;
        if (!IsValid(relative, requiredTopLevel))
        {
            return false;
        }

        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var candidate = Path.GetFullPath(Path.Combine(
                fullRoot,
                relative!.Replace('/', Path.DirectorySeparatorChar)));
            var rootPrefix = Path.EndsInDirectorySeparator(fullRoot)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!candidate.StartsWith(rootPrefix, comparison))
            {
                return false;
            }

            resolved = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
