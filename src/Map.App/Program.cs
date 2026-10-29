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
        // Under RenderDoc (tools/ai/live/renderdoc-capture.ps1) its crash handler would end the
        // process on the first hardware access violation the .NET runtime uses for null checks.
        RenderDocCapture.DisableCrashHandler();
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
            Views.UiTasks.Forget(
                () => Task.Run(() => PngViewCache.TrimWhenIdle(Path.Combine(profileRoot, "ImageViewCache"))),
                (operation, error) => log.Error(operation, error));
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
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
        // Diagnostics only (DEC-050): MAP_WIN32_COMPOSITION=LowLatencyDxgiSwapChain|RedirectionSurface
        // swaps the Win32 composition path. RenderDoc kills the process on the default WinUI
        // composition path (Avalonia #15911); tools/ai/live/renderdoc-capture.ps1 sets this.
        if (Enum.TryParse<Avalonia.Win32CompositionMode>(Environment.GetEnvironmentVariable("MAP_WIN32_COMPOSITION"), ignoreCase: true, out var composition))
        {
            builder = builder.With(new Avalonia.Win32PlatformOptions { CompositionMode = [composition] });
        }

        return builder;
    }
}
