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
            var diagnostics = new DiagnosticsLog(Path.Combine(
                appSettings.CacheRoot ?? profileRoot, "Diagnostics"));
            var journal = new RecoveryJournal(
                TestMode.RecoveryRootOverride ?? Path.Combine(profileRoot, "Recovery"),
                appSettings.RecoveryItemBudget);

            var assetCatalog = new AssetCatalog(
                appSettings.LibraryRoot ?? Path.Combine(profileRoot, "Assets"));
            try
            {
                StarterPackBootstrapper.EnsureInstalled(assetCatalog, DateTimeOffset.UtcNow);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or InvalidDataException or InvalidOperationException)
            {
                diagnostics.Error("STARTER_PACK_INSTALL_FAILED", error);
            }

            var mainWindow = new MainWindow(
                new ProjectStore(journal),
                new Services.PlatformRecentMaps(
                    new RecentMapsStore(Path.Combine(profileRoot, "recent.json"))),
                new Services.DiagnosticsLogAdapter(diagnostics),
                assetCatalog,
                appSettingsStore: appSettingsStore,
                appSettings: appSettings);
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

}
