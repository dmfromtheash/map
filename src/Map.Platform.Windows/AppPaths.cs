namespace Map.Platform.Windows;

/// <summary>Where the app keeps its own local state. Everything lives under %LOCALAPPDATA%\Map.</summary>
public static class AppPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Map");

    public static string RecoveryRoot => Path.Combine(Root, "Recovery");

    public static string DiagnosticsDirectory => Path.Combine(Root, "Diagnostics");

    public static string RecentIndexPath => Path.Combine(Root, "recent.json");

    public static string ShortcutSettingsPath => Path.Combine(Root, "shortcuts.json");

    public static string EditorPreferencesPath => Path.Combine(Root, "editor-preferences.json");

    /// <summary>
    /// Validates an explicit, disposable user-state root used by native UI drivers. The real
    /// profile, its descendants, and its ancestors are rejected so a test cannot silently write
    /// into the user's production Map state or an overly broad parent directory.
    /// </summary>
    public static string ResolveTestProfileRoot(string? testOverride)
    {
        if (string.IsNullOrWhiteSpace(testOverride) || !Path.IsPathFullyQualified(testOverride))
        {
            throw new InvalidOperationException(
                "Test mode requires an absolute isolated profile root.");
        }

        var resolved = Path.GetFullPath(testOverride).TrimEnd(Path.DirectorySeparatorChar);
        var pathRoot = Path.GetPathRoot(resolved)?.TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(resolved) ||
            string.Equals(resolved, pathRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Test mode profile root cannot be a drive root.");
        }

        var production = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(resolved, production, StringComparison.OrdinalIgnoreCase) ||
            resolved.StartsWith(production + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            production.StartsWith(resolved + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Test mode profile root must be separate from the real user profile.");
        }

        return resolved;
    }

    /// <summary>
    /// Resolves the settings file for the current process. Test mode is deliberately fail-closed:
    /// a driver must provide a distinct absolute path and can never fall back to the real profile.
    /// </summary>
    public static string ResolveShortcutSettingsPath(bool testMode, string? testOverride)
    {
        if (!testMode)
        {
            return Path.GetFullPath(ShortcutSettingsPath);
        }

        if (string.IsNullOrWhiteSpace(testOverride) || !Path.IsPathFullyQualified(testOverride))
        {
            throw new InvalidOperationException(
                "Test mode requires an absolute isolated shortcut-settings path.");
        }

        var resolved = Path.GetFullPath(testOverride);
        if (string.Equals(resolved, Path.GetFullPath(ShortcutSettingsPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Test mode shortcut settings must not use the real user settings path.");
        }

        return resolved;
    }

    public static string AssetCatalogRoot => Path.Combine(Root, "Assets");
}
