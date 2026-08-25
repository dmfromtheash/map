using System;
using Avalonia;
using Map.Platform.Windows;

namespace Map.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var log = new DiagnosticsLog();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Error("Unhandled exception", e.ExceptionObject as Exception);

        try
        {
            log.Info("Startup.");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            log.Info("Clean shutdown.");
        }
        catch (Exception ex)
        {
            log.Error("Fatal exception", ex);
            throw;
        }
    }

    /// <summary>
    /// Plain desktop configuration: no browser host, no WebView, no local server, and nothing
    /// here opens a socket (DEC-001).
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
