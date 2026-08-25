using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            var diagnostics = new DiagnosticsLog();
            var journal = new RecoveryJournal(AppPaths.RecoveryRoot);

            desktop.MainWindow = new MainWindow(
                new ProjectStore(journal),
                new Services.PlatformRecentMaps(new RecentMapsStore()),
                new Services.DiagnosticsLogAdapter(diagnostics),
                new AssetCatalog(AppPaths.AssetCatalogRoot));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
