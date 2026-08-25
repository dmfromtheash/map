using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Map.Spike.App.Canvas;
using Map.Spike.App.Session;

namespace Map.Spike.App;

public partial class MainWindow : Window
{
    private readonly SpikeSession _session = new();
    private readonly MapCanvasControl _canvas;
    private readonly DispatcherTimer _statusTimer;

    public MainWindow()
    {
        InitializeComponent();

        _canvas = new MapCanvasControl(_session);
        _canvas.StateChanged += (_, _) => RefreshStatus();

        var host = this.FindControl<ContentControl>("CanvasHost")!;
        host.Content = _canvas;

        _canvas.Probe.PlatformGraphics = DescribePlatformGraphics();

        // The status panel reads live counters, so it ticks on its own rather than being pushed
        // from the render path.
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        Opened += (_, _) =>
        {
            _canvas.ZoomToFit();
            _canvas.Focus();
            RefreshStatus();
        };

        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _session.Dispose();
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.D1:
                _session.Mode = ToolMode.Select;
                break;

            case Key.D2:
                _session.Mode = ToolMode.Brush;
                break;

            case Key.D3:
                _session.Mode = ToolMode.MaskErase;
                break;

            case Key.Z when control:
                _session.Undo();
                break;

            case Key.Y when control:
                _session.Redo();
                break;

            case Key.Z:
                _canvas.ZoomToFit();
                break;

            case Key.M:
                _session.MaskEnabled = !_session.MaskEnabled;
                break;

            case Key.O:
                _session.ShowMaskEditOverlay = !_session.ShowMaskEditOverlay;
                break;

            case Key.S when control:
                _session.Save();
                break;

            case Key.R when control:
                _session.Reopen();
                break;

            case Key.E:
                _session.Export4K();
                break;

            case Key.F:
                _canvas.StartBenchmark();
                break;

            case Key.F5:
                _session.RebuildScene(SpikeSession.DefaultObjectCount);
                _canvas.ZoomToFit();
                break;

            default:
                return;
        }

        e.Handled = true;
        _canvas.InvalidateVisual();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        this.FindControl<TextBlock>("ModeText")!.Text =
            $"tool: {_session.Mode} | mask: {(_session.MaskEnabled ? "on" : "off")} | " +
            $"overlay: {(_session.ShowMaskEditOverlay ? "on" : "off")}";

        this.FindControl<TextBlock>("SceneText")!.Text =
            $"objects: {_session.Document.Objects.Count:N0} | " +
            $"brush tiles: {_session.Document.Brush.TileCount} | " +
            $"mask tiles: {_session.Document.Mask.TileCount} | " +
            $"zoom: {_session.Viewport.Scale:F3}x";

        this.FindControl<TextBlock>("DirtyText")!.Text =
            $"revision {_session.Document.Revision} | " +
            $"saved {_session.Document.SavedRevision} | " +
            $"{(_session.Document.IsDirty ? "UNSAVED" : "saved")} | " +
            $"undo {_session.History.UndoDepth}/redo {_session.History.RedoDepth}";

        this.FindControl<TextBlock>("StatusText")!.Text = _session.StatusLine;

        var failure = this.FindControl<TextBlock>("FailureText")!;
        failure.Text = _session.FailureLine ?? string.Empty;
        failure.IsVisible = _session.FailureLine is not null;

        this.FindControl<TextBlock>("RendererText")!.Text = _canvas.Probe.Describe();
        this.FindControl<TextBlock>("FrameText")!.Text = _canvas.LastFrameSummary;
        this.FindControl<TextBlock>("BenchmarkText")!.Text = _canvas.BenchmarkSummary;
        this.FindControl<TextBlock>("HitTestText")!.Text = _session.LastHitTestLine;

        using var process = Process.GetCurrentProcess();
        process.Refresh();

        var document = _session.Document;
        var documentBytes = document.Brush.TotalTileBytes
            + document.Decor.TotalTileBytes
            + document.Mask.TotalTileBytes;

        this.FindControl<TextBlock>("MemoryText")!.Text =
            $"memory: document tiles {Mib(documentBytes)} | " +
            $"render tile cache {Mib(_session.TileCache.ApproximateBytes)} ({_session.TileCache.EntryCount} entries) | " +
            $"geometry cache ~{Mib(_session.GeometryCache.ApproximateVertexBytes)} " +
            $"({_session.GeometryCache.ChunkCount} chunks, {_session.GeometryCache.TriangleCount:N0} triangles) | " +
            $"undo {Mib(_session.History.ApproximateMemoryBytes)} | " +
            $"managed heap {Mib(GC.GetTotalMemory(false))} | " +
            $"private bytes {Mib(process.PrivateMemorySize64)} | " +
            $"working set {Mib(process.WorkingSet64)}";
    }

    private static string Mib(long bytes) => $"{bytes / 1024.0 / 1024.0:F2} MiB";

    /// <summary>
    /// Names the graphics stack from the native modules the process actually loaded.
    ///
    /// Avalonia 12 no longer exposes AvaloniaLocator.Current publicly, and guessing from
    /// configuration would report what we asked for rather than what we got. Loaded modules are
    /// an observation: if libEGL is mapped, ANGLE is really in play.
    /// </summary>
    private static string DescribePlatformGraphics()
    {
        try
        {
            using var process = Process.GetCurrentProcess();

            var names = process.Modules
                .Cast<ProcessModule>()
                .Select(m => m.ModuleName ?? string.Empty)
                .ToArray();

            // Contains, not StartsWith: Avalonia ships ANGLE as av_libGLESv2.dll / av_libEGL.dll.
            bool Loaded(string name) =>
                names.Any(n => n.Contains(name, StringComparison.OrdinalIgnoreCase));

            var found = new[]
                {
                    Loaded("libEGL") || Loaded("libGLESv2") ? "ANGLE (EGL/GLES over Direct3D)" : null,
                    Loaded("vulkan-1") ? "Vulkan loader" : null,
                    Loaded("opengl32") ? "opengl32 (WGL)" : null,
                    Loaded("d3d11") ? "d3d11" : null,
                    Loaded("dcomp") ? "DirectComposition" : null,
                }
                .Where(x => x is not null)
                .ToArray();

            return found.Length == 0
                ? "no GPU native modules loaded (software rendering path)"
                : string.Join(" + ", found);
        }
        catch (InvalidOperationException)
        {
            return "unavailable";
        }
    }
}
