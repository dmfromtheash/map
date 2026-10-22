using System;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Map.App.Services;
using Map.Assets;
using Map.Persistence.Recovery;
using Map.Platform.Windows;
using Map.Presentation.Services;

namespace Map.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composition root: local app-data services, the production journal, the real store.
            var profileRoot = TestMode.ProfileRootOverride is { } testProfileRoot
                ? AppPaths.ResolveTestProfileRoot(testProfileRoot)
                : AppPaths.Root;
            var appSettingsStore = new AppSettingsStore(Path.Combine(profileRoot, "settings.json"));
            var appSettings = appSettingsStore.Load();
            Map.Rendering.HexUserTileSets.Initialize(Path.Combine(profileRoot, "HexTileSets", "user"));
            var diagnostics = new DiagnosticsLog(Path.Combine(
                appSettings.CacheRoot ?? profileRoot, "Diagnostics"));
            var journal = new RecoveryJournal(
                TestMode.RecoveryRootOverride ?? Path.Combine(profileRoot, "Recovery"),
                appSettings.RecoveryItemBudget);

            var assetCatalog = new AssetCatalog(
                appSettings.LibraryRoot ?? Path.Combine(profileRoot, "Assets"));
            // The owner starts the artwork library from scratch. Opening Map never seeds assets;
            // new content enters the existing catalog through an explicit import.

            var mainWindow = new MainWindow(
                new ProjectStore(journal),
                new Services.PlatformRecentMaps(
                    new RecentMapsStore(Path.Combine(profileRoot, "recent.json"))),
                new Services.DiagnosticsLogAdapter(diagnostics),
                assetCatalog,
                appSettingsStore: appSettingsStore,
                appSettings: appSettings);
            desktop.MainWindow = mainWindow;
            // A map file on the command line (a shortcut, a double-click, a script such as
            // tools/ai/live/renderdoc-capture.ps1) opens in the workshop as soon as the window is up.
            foreach (var argument in desktop.Args ?? [])
            {
                if (argument.EndsWith(".mapproj", StringComparison.OrdinalIgnoreCase) && File.Exists(argument))
                {
                    var mapPath = Path.GetFullPath(argument);
                    mainWindow.Opened += (_, _) => _ = mainWindow.OpenPathAsync(mapPath);
                    break;
                }

                // An exported picture opens straight in the PNG viewer (e.g. the giant map download).
                if (argument.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(argument))
                {
                    var pngPath = Path.GetFullPath(argument);
                    mainWindow.Opened += (_, _) => Views.PngMapWindow.ShowFor(mainWindow, pngPath);
                    break;
                }
            }
            // Sound on every button, switch, slider, list and menu - one switch silences it all.
            UiSoundHooks.Install();
            // Dialogs grow with large screens like the main window does.
            UiScaling.Install();
        }

        base.OnFrameworkInitializationCompleted();
    }

}
