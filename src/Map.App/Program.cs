using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Map.App.Services;
using Map.Export;
using Map.Platform.Windows;

namespace Map.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var profileRoot = TestMode.ProfileRootOverride is { } testProfileRoot
            ? AppPaths.ResolveTestProfileRoot(testProfileRoot)
            : AppPaths.Root;
        var log = new DiagnosticsLog(Path.Combine(profileRoot, "Diagnostics"));
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Error("Unhandled exception", e.ExceptionObject as Exception);

        // An editor frame must not stop for a full blocking collection: a long stroke promotes a
        // few hundred kilobytes per frame, and the resulting gen2 pauses were the 200-250 ms
        // hitches in otherwise 60 FPS painting. Background collection keeps running; only the
        // blocking full collections are avoided unless the machine is genuinely short of memory.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        try
        {
            log.Info("Startup.");
            // Best effort recovery of disposable viewing data left by earlier runs or a crash.
            _ = Task.Run(() => PngViewCache.TrimWhenIdle(Path.Combine(profileRoot, "ImageViewCache")));
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
