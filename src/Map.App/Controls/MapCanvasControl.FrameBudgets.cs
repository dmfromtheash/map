using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Map.Platform.Windows;
using Map.Presentation.Services;

namespace Map.App.Controls;

public sealed partial class MapCanvasControl
{
    private long _displayRefreshProbe = long.MinValue / 2;
    private int _displayRefreshQuery;

    private void UpdateFrameBudgetDisplay()
    {
        if (!OperatingSystem.IsWindows() || _editor?.StampImages is not CatalogStampImageSource images) return;
        var now = Environment.TickCount64;
        if (now - _displayRefreshProbe < 1000) return;
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        if (Interlocked.CompareExchange(ref _displayRefreshQuery, 1, 0) != 0) return;
        _displayRefreshProbe = now;
        // Screen metadata belongs to the UI thread. The image source publishes only an atomic
        // numeric rate to the render thread; no display enumeration happens inside a native frame.
        var deviceName = window.Screens.ScreenFromWindow(window)?.DisplayName;
        _ = Task.Run(() =>
        {
            try { images.DisplayRefreshHz = DisplayRefreshRate.Read(deviceName); }
            finally { Volatile.Write(ref _displayRefreshQuery, 0); }
        });
    }
}
