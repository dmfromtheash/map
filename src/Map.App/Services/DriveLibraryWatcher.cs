using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Map.Assets;

namespace Map.App.Services;

/// <summary>
/// Attaches the asset libraries of external drives while Map runs. Every few seconds the ready
/// drives are checked for <c>MapLibrary\map-library.json</c>; a new drive's catalogs are parsed on a
/// worker and attached on the UI thread, a removed drive's cards disappear. Nothing is copied to
/// the owner's library and no drive letter is remembered.
/// </summary>
internal sealed class DriveLibraryWatcher : IDisposable
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(3);
    private readonly AssetCatalog _catalog;
    private readonly DispatcherTimer _timer;
    private bool _busy;

    public DriveLibraryWatcher(AssetCatalog catalog)
    {
        _catalog = catalog;
        _timer = new DispatcherTimer(Period, DispatcherPriority.Background, (_, _) => Scan());
    }

    public void Start()
    {
        Scan();
        _timer.Start();
    }

    private void Scan()
    {
        if (_busy) return;
        _busy = true;
        var attached = _catalog.Mounts.ToDictionary(mount => mount.Root, StringComparer.OrdinalIgnoreCase);
        Task.Run(() =>
        {
            var roots = AssetDriveLibraries.CatalogRoots(AssetDriveLibraries.Discover());
            if (roots.Count == attached.Count && roots.All(attached.ContainsKey)) return null;
            return roots.Select(root => attached.GetValueOrDefault(root) ?? AssetCatalog.OpenMounted(root))
                .OfType<AssetCatalog>().ToList();
        }).ContinueWith(task =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                if (task.Status == TaskStatus.RanToCompletion && task.Result is { } mounts) _catalog.AttachMounts(mounts);
            });
        }, TaskScheduler.Default);
    }

    public void Dispose() => _timer.Stop();
}
