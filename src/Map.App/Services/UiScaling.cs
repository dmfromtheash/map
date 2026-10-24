using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;

namespace Map.App.Services;

/// <summary>
/// One scale for the whole interface on large screens. Every page and dialog is laid out for
/// about 1920×1080; on a 1440p, ultrawide or 4K monitor the interface grows with the screen
/// (60 % of the extra size, at most ×1.5) and the user's own scale setting multiplies on top.
/// The main window follows its own size (MainWindow.UpdateUiScale); every other window gets the
/// scale of the screen it opens on, its size grown to match; on every screen it is kept inside the working area.
/// </summary>
internal static class UiScaling
{
    /// <summary>Title bar and borders of a decorated window, in device-independent pixels.</summary>
    private const double FrameAllowance = 40;

    /// <summary>The scale chosen in the settings (1 = 100 %).</summary>
    public static double UserScale { get; set; } = 1;

    public static double ForSize(double width, double height)
    {
        if (width <= 0 || height <= 0) return UserScale;
        var ratio = Math.Min(width / 1920, height / 1080);
        return Math.Round(Math.Clamp(1 + (ratio - 1) * .6, 1, 1.5) * UserScale, 3);
    }

    public static void Install() =>
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Apply(window));

    private static void Apply(Window window)
    {
        if (window is MainWindow || window.Content is LayoutTransformControl { Name: "UiScaleHost" }) return;
        FollowOwnerScreen(window);
        // The owner's screen decides: right after the move Windows may still report the dialog on
        // the main screen, and a 2560×1440 area grew the wizard ×1.19 on the 1536×816 laptop.
        if ((OwnerScreen(window) ?? window.Screens.ScreenFromWindow(window)) is not { } screen) return;
        var area = screen.WorkingArea.ToRect(screen.Scaling);
        var scale = ForSize(area.Width, area.Height);
        if (Math.Abs(scale - 1) >= .01 && window.Content is Control content)
        {
            window.Content = null;
            window.Content = new LayoutTransformControl { Name = "UiScaleHost", LayoutTransform = new ScaleTransform(scale, scale), Child = content };
        }
        else scale = 1;
        // Unscaled dialogs are fitted too: on a 1536×816 laptop area (1920×1080 at 125 %) the
        // 1080×820 new-map wizard ran past the top and bottom, its buttons under the taskbar.
        Scale(window, scale, area, screen.Scaling);
    }

    private static Screen? OwnerScreen(Window window) =>
        Owner(window) is { } owner ? window.Screens.ScreenFromWindow(owner) : null;

    /// <summary>
    /// The window a dialog belongs to. ShowDialog assigns Owner only after WindowOpened, so at
    /// that moment it is still null; every dialog of Map is opened from the main window.
    /// </summary>
    private static Window? Owner(Window window)
    {
        var owner = window.Owner as Window
            ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return owner is not null && owner != window && owner.WindowState != WindowState.Minimized ? owner : null;
    }

    /// <summary>
    /// A dialog opens on the screen of the window that owns it. With two monitors of different
    /// scale Windows sometimes put it on the main screen instead; it is moved, centred, to the owner's.
    /// </summary>
    private static void FollowOwnerScreen(Window window)
    {
        if (OwnerScreen(window) is not { } target) return;
        if (window.Screens.ScreenFromWindow(window) is { } current && current.Bounds == target.Bounds) return;
        var area = target.WorkingArea;
        var width = (int)Math.Min(area.Width, (double.IsNaN(window.Width) ? window.Bounds.Width : window.Width) * target.Scaling);
        var height = (int)Math.Min(area.Height, (double.IsNaN(window.Height) ? window.Bounds.Height : window.Height) * target.Scaling);
        window.Position = new PixelPoint(area.X + Math.Max(0, (area.Width - width) / 2), area.Y + Math.Max(0, (area.Height - height) / 2));
    }

    /// <summary>Grows a fixed-size dialog by the same factor (1 on small screens), never past the screen, re-centred.</summary>
    private static void Scale(Window window, double scale, Rect area, double screenScaling)
    {
        if (window.MinWidth > 0) window.MinWidth = Math.Min(window.MinWidth * scale, area.Width);
        if (window.MinHeight > 0) window.MinHeight = Math.Min(window.MinHeight * scale, area.Height);
        if (window.SizeToContent != SizeToContent.Manual || window.WindowState != WindowState.Normal) return;
        // Height is the client area; the title bar and borders come on top of it and must stay
        // above the taskbar as well.
        var width = Math.Min(window.Width * scale, area.Width * .96);
        var height = Math.Min(window.Height * scale, Math.Min(area.Height * .96, area.Height - FrameAllowance));
        if (double.IsNaN(width) || double.IsNaN(height)) return;
        if (window.MinHeight > height) window.MinHeight = height;
        window.Width = width;
        window.Height = height;
        if (window.WindowStartupLocation is WindowStartupLocation.CenterOwner or WindowStartupLocation.CenterScreen)
        {
            var origin = Owner(window) is { } owner && OwnerScreen(window) is { } ownerScreen
                ? new Point(owner.Position.X / ownerScreen.Scaling + owner.Bounds.Width / 2,
                            owner.Position.Y / ownerScreen.Scaling + owner.Bounds.Height / 2)
                : area.Center;
            var x = Math.Clamp(origin.X - width / 2, area.X, area.Right - width);
            var y = Math.Clamp(origin.Y - (height + FrameAllowance) / 2, area.Y, area.Bottom - height - FrameAllowance);
            window.Position = new PixelPoint((int)(x * screenScaling), (int)(y * screenScaling));
        }
    }
}
