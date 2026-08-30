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
