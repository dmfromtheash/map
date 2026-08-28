namespace Map.Core.Documents;

/// <summary>
/// The portable path stored in a project for a linked asset. Rooted, escaping,
/// drive-relative, device, UNC and NTFS stream forms are not portable project paths and must
/// never be probed while opening an untrusted project. The final path is contained beneath the
/// project directory; external user links retain catalog identity instead of turning an opened
/// project into a general local-file probe.
/// </summary>
public static class ProjectLinkedAssetPath
{
    public static bool IsSafeRelative(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains('\0')
            || value.Contains(':')
            || Path.IsPathRooted(value))
        {
            return false;
        }

        var segments = value.Replace('\\', '/').Split('/');
        return segments.All(segment =>
            segment.Length > 0
            && (segment is "." or ".." || PortablePathSegment.IsSafe(segment)));
    }

    public static bool TryResolve(string projectPath, string? relativePath, out string resolved)
    {
        resolved = "";
        if (!IsSafeRelative(relativePath))
        {
            return false;
        }

        try
        {
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            if (projectDirectory is null)
            {
                return false;
            }

            var candidate = Path.GetFullPath(Path.Combine(projectDirectory, relativePath!));
            var root = Path.GetPathRoot(candidate);
            if (string.IsNullOrWhiteSpace(root)
                || root.StartsWith(@"\\", StringComparison.Ordinal)
                || candidate.StartsWith(@"\\?\", StringComparison.Ordinal)
                || candidate.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                return false;
            }

            var containedRoot = projectDirectory.EndsWith(Path.DirectorySeparatorChar)
                ? projectDirectory
                : projectDirectory + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(containedRoot, StringComparison.OrdinalIgnoreCase))
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

    public static bool TryMakeRelative(string projectPath, string assetPath, out string relative)
    {
        relative = "";
        try
        {
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            var candidate = Path.GetFullPath(assetPath);
            if (projectDirectory is null)
            {
                return false;
            }

            var containedRoot = projectDirectory.EndsWith(Path.DirectorySeparatorChar)
                ? projectDirectory
                : projectDirectory + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(containedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var value = Path.GetRelativePath(projectDirectory, candidate);
            if (!IsSafeRelative(value))
            {
                return false;
            }

            relative = value.Replace(Path.DirectorySeparatorChar, '/');
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Reject a symlink/junction hop before the renderer opens a contained fallback.</summary>
    public static bool IsExistingFileWithoutReparsePoints(string projectPath, string resolvedPath)
    {
        try
        {
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            if (projectDirectory is null)
            {
                return false;
            }

            var relative = Path.GetRelativePath(projectDirectory, Path.GetFullPath(resolvedPath));
            if (!IsSafeRelative(relative))
            {
                return false;
            }

            var current = projectDirectory;
            foreach (var segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
            }

            return (File.GetAttributes(resolvedPath) & FileAttributes.Directory) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
