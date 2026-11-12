using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Map.Assets;

namespace Map.App.Services;

/// <summary>
/// Attaches the asset libraries of external drives while Map runs. Every few seconds the ready
/// drives are checked for <c>MapLibrary\map-library.json</c>; a new drive's catalogs are parsed on a
/// worker and attached on the UI thread, a removed drive's cards disappear, and a drive catalog
/// whose index was changed by the library builder is reopened. Nothing is copied to the owner's
/// library and no drive letter is remembered. A catalog that cannot be opened (damaged, over the
/// budget, locked) is retried only when its file changes, not on every scan.
/// </summary>
internal sealed class DriveLibraryWatcher : IDisposable
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(3);
    private readonly AssetCatalog _catalog;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, (long Length, DateTime WriteUtc)> _failed = new(StringComparer.OrdinalIgnoreCase);
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
        var attached = _catalog.Mounts.ToList();
        Task.Run(() =>
        {
            var roots = AssetDriveLibraries.CatalogRoots(AssetDriveLibraries.Discover());
            var byRoot = attached.ToDictionary(mount => mount.Root, StringComparer.OrdinalIgnoreCase);
            var changed = attached.Any(mount => mount.ChangedOnDisk);
            var missing = roots.Where(root => !byRoot.ContainsKey(root) && !StillFailed(root)).ToList();
            if (!changed && missing.Count == 0 && byRoot.Keys.All(root => roots.Contains(root, StringComparer.OrdinalIgnoreCase)))
                return null;
            var next = new List<AssetCatalog>();
            foreach (var root in roots)
            {
                if (byRoot.TryGetValue(root, out var mount))
                {
                    next.Add(mount.ChangedOnDisk ? AssetCatalog.OpenMounted(root) ?? mount : mount);
                    continue;
                }

                if (StillFailed(root)) continue;
                if (AssetCatalog.OpenMounted(root) is { } opened) { next.Add(opened); lock (_failed) _failed.Remove(root); }
                else if (Stamp(root) is { } stamp) lock (_failed) _failed[root] = stamp;
            }

            return next;
        }).ContinueWith(task =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                if (task.Status == TaskStatus.RanToCompletion && task.Result is { } mounts) _catalog.AttachMounts(mounts);
            });
        }, TaskScheduler.Default);
    }

    private bool StillFailed(string root)
    {
        lock (_failed) return _failed.TryGetValue(root, out var known) && Stamp(root) == known;
    }

    private static (long Length, DateTime WriteUtc)? Stamp(string root)
    {
        try
        {
            var file = new FileInfo(Path.Combine(root, "catalog.json"));
            return file.Exists ? (file.Length, file.LastWriteTimeUtc) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose() => _timer.Stop();
}
