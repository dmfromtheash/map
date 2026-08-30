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

    public static string AssetCatalogRoot => Path.Combine(Root, "Assets");
}
