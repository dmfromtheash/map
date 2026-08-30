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
            var diagnostics = new DiagnosticsLog(Path.Combine(profileRoot, "Diagnostics"));
            var journal = new RecoveryJournal(
                TestMode.RecoveryRootOverride ?? Path.Combine(profileRoot, "Recovery"));

            desktop.MainWindow = new MainWindow(
                new ProjectStore(journal),
                new Services.PlatformRecentMaps(
                    new RecentMapsStore(Path.Combine(profileRoot, "recent.json"))),
                new Services.DiagnosticsLogAdapter(diagnostics),
                new AssetCatalog(Path.Combine(profileRoot, "Assets")));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
