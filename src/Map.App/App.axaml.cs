using System;
using System.Threading.Tasks;
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
            _ = InstallStarterArtAsync(assetCatalog, mainWindow, diagnostics);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Map's own mountains, trees and buildings are painted by code the first time this catalog
    /// sees them. Painting takes seconds, so it runs on a worker after the window is up; the
    /// index is written on the UI thread, like any import, and the library then refreshes.
    /// </summary>
    private static async Task InstallStarterArtAsync(AssetCatalog catalog, MainWindow window, DiagnosticsLog diagnostics)
    {
        try
        {
            var missing = StarterPackBootstrapper.MissingArtSets(catalog);
            if (missing.Count == 0) return;
            var root = catalog.Root;
            var rendered = await Task.Run(() => StarterPackBootstrapper.RenderArtFiles(root, missing));
            var imported = StarterPackBootstrapper.ImportArt(catalog, rendered, DateTimeOffset.UtcNow);
            if (imported > 0) window.NotifyAssetCatalogChanged();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException)
        {
            diagnostics.Error("STARTER_ART_INSTALL_FAILED", error);
        }
    }
}
