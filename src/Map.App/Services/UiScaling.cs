using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Map.App.Services;

/// <summary>
/// One scale for the whole interface on large screens. Every page and dialog is laid out for
/// about 1920×1080; on a 1440p, ultrawide or 4K monitor the interface grows with the screen
/// (60 % of the extra size, at most ×1.5) and the user's own scale setting multiplies on top.
/// The main window follows its own size (MainWindow.UpdateUiScale); every other window gets the
/// scale of the screen it opens on, its size grown to match and kept inside the working area.
/// </summary>
internal static class UiScaling
{
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
        if (window.Screens.ScreenFromWindow(window) is not { } screen) return;
        var area = screen.WorkingArea.ToRect(screen.Scaling);
        var scale = ForSize(area.Width, area.Height);
        if (Math.Abs(scale - 1) < .01 || window.Content is not Control content) return;

        window.Content = null;
        window.Content = new LayoutTransformControl { Name = "UiScaleHost", LayoutTransform = new ScaleTransform(scale, scale), Child = content };
        Scale(window, scale, area);
    }

    /// <summary>Grows a fixed-size dialog by the same factor, never past the screen, re-centred.</summary>
    private static void Scale(Window window, double scale, Rect area)
    {
        if (window.MinWidth > 0) window.MinWidth = Math.Min(window.MinWidth * scale, area.Width);
        if (window.MinHeight > 0) window.MinHeight = Math.Min(window.MinHeight * scale, area.Height);
        if (window.SizeToContent != SizeToContent.Manual || window.WindowState != WindowState.Normal) return;
        var width = Math.Min(window.Width * scale, area.Width * .96);
        var height = Math.Min(window.Height * scale, area.Height * .96);
        if (double.IsNaN(width) || double.IsNaN(height)) return;
        window.Width = width;
        window.Height = height;
        if (window.WindowStartupLocation is WindowStartupLocation.CenterOwner or WindowStartupLocation.CenterScreen)
        {
            var origin = window.Owner is Window owner && owner.WindowState != WindowState.Minimized
                ? new Point(owner.Position.X / window.Screens.ScreenFromWindow(owner)!.Scaling + owner.Bounds.Width / 2,
                            owner.Position.Y / window.Screens.ScreenFromWindow(owner)!.Scaling + owner.Bounds.Height / 2)
                : area.Center;
            var x = Math.Clamp(origin.X - width / 2, area.X, area.Right - width);
            var y = Math.Clamp(origin.Y - height / 2, area.Y, area.Bottom - height);
            var screenScaling = window.Screens.ScreenFromWindow(window)!.Scaling;
            window.Position = new PixelPoint((int)(x * screenScaling), (int)(y * screenScaling));
        }
    }
}
