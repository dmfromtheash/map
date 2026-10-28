using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Map.App.Services;

/// <summary>
/// A test run keeps every window off the owner's main screen (owner, 2026-10-28: «ЗАПРЕЩАЮ»). A smoke's window
/// parked at -32000 was pulled by Windows onto the main screen; now any test window that lands on the main screen
/// or on no screen at all - when it opens and whenever it moves - goes to the second display. A smoke calls
/// <see cref="Guard"/> before its first window opens; the editor calls it too.
/// </summary>
public static class TestWindows
{
    private static readonly ConditionalWeakTable<Window, object> Guarded = new();
    private static bool _installed;

    public static void Guard()
    {
        if (!TestMode.Enabled || _installed) return;
        _installed = true;
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Watch(window));
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (var window in desktop.Windows) Watch(window);
    }

    private static void Watch(Window window)
    {
        if (Guarded.TryGetValue(window, out _)) return;
        Guarded.Add(window, new object());
        window.PositionChanged += (_, _) => KeepOffMainScreen(window);
        KeepOffMainScreen(window);
        // The window a test drives (no owner) fills the second display, as the owner works with Map: full screen,
        // never a small window Windows may shift (owner, 2026-10-28). Its own windows stay on that display.
        if (window.Owner is null && window.WindowState == WindowState.Normal
            && window.Screens.All.Any(screen => !screen.IsPrimary))
            window.WindowState = WindowState.FullScreen;
    }

    private static void KeepOffMainScreen(Window window)
    {
        if (window.Screens.All.FirstOrDefault(screen => !screen.IsPrimary) is not { } second) return;
        if (window.Screens.ScreenFromPoint(window.Position) is { IsPrimary: false }) return;
        var target = new PixelPoint(second.WorkingArea.X, second.WorkingArea.Y);
        if (window.Position != target) window.Position = target;
    }
}
