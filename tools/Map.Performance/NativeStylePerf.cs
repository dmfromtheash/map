using Map.Core.Documents;
using Map.Rendering.Styles;
using Map.Presentation.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

public sealed partial class PaintPerfApplication
{
    private async Task StyleRoutes((double X, double Y) centre, double scale)
    {
        // Generation/save has already finished; exports may themselves acquire HeavyGate.
        // Hold the existing cross-process slot for every warm-up and measured style route.
        using var styleGate = await Map.Core.Resources.WorkingMemoryBudget.Shared.EnterHeavyAsync(reserveBytes:
            Map.Core.Resources.WorkingMemoryBudget.SmallMeasuredBypassBytes + 1);
        _report.Environment["heavy-gate"] = "held throughout style routes";
        // W/H still size the window in DIP. Pin this tool's canvas separately to the threshold's
        // device pixels: Windows scaling and editor panels must not change the tested workload.
        var width = (int)EnvNumber("MAP_PAINT_W", 2048);
        var height = (int)EnvNumber("MAP_PAINT_H", 1095);
        _view.Canvas.Width = width / _window.RenderScaling;
        _view.Canvas.Height = height / _window.RenderScaling;
        _view.Canvas.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        _view.Canvas.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        await Settle();
        var measure = System.Environment.GetEnvironmentVariable("MAP_STYLE_STAGES") != "0";
        StyleDiagnostics.MeasureStages = measure;
        StyleDiagnostics.CollectFrames = true;
        _report.Environment["style/stages"] = measure.ToString();
        _report.Environment["style/window"] = $"{_window.Bounds.Width}x{_window.Bounds.Height} DIP at {_window.Position}; scaling={_window.RenderScaling}";
        _report.Environment["style/target-pixels"] = $"{width}x{height}";
        var presets = new[] { MapStylePreset.InkWetBrush, MapStylePreset.InkPenHatching, MapStylePreset.InkPenStipple,
            MapStylePreset.OldMapBlaeu, MapStylePreset.EngravingAquatint };
        // MAP_NEW_ART_STYLE_PRESETS = comma-separated preset names to measure instead (e.g. the painter, oil and charcoal ones).
        if (System.Environment.GetEnvironmentVariable("MAP_NEW_ART_STYLE_PRESETS") is { Length: > 0 } chosen)
            presets = [.. chosen.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<MapStylePreset>)];
        try
        {
            _editor.ChangeLighting("Замер", l => l with { Enabled = false }, true);
            foreach (var (tag, zoom) in new[] { ("far", scale), ("100", 1.0) })
            {
                _editor.SetStyleEnabled(false);
                await ViewRoutes("style-off-" + tag);
                foreach (var preset in presets)
                {
                    _editor.ApplyStylePreset(preset);
                    _editor.SetStyleStrength(1, commit: true);
                    var name = $"style-{preset}-{tag}";
                    // R09b: from the preset to the finished styled frame (detail tiles in, frame kept).
                    SetScale(centre, zoom);
                    var settle = await StyledSettle();
                    _report.Environment["style-settle/" + name] = settle;
                    Console.WriteLine($"    {name} settle: {settle}");
                    try
                    {
                        await ViewRoutes(name);
                        SetScale(centre, zoom); await TimedRefined();
                        StyleDiagnostics.Samples.Clear();
                        // All 50 previews have nonzero strength, so every step exercises the style.
                        await BurstRoute(name + "/slider", 50, i => _editor.SetStyleStrength((i + 1) / 50.0, commit: false));
                        ReportStyle(name + "/slider", expected: true);
                    }
                    catch (TimeoutException)
                    {
                        // A frame slower than the route's minute: say so and go on to the next scale.
                        _report.Checks["style-routes-in-time/" + name] = false;
                        Console.WriteLine($"    {name}: routes timed out");
                        StyleDiagnostics.Samples.Clear();
                    }
                    _editor.SetStyleStrength(1, commit: true);
                }
                _editor.ApplyStylePreset(MapStylePreset.InkWetBrush);
                _editor.ChangeLighting("Замер", _ => new MapLighting { Enabled = true, TimeOfDay = .5 }, true);
                await ViewRoutes("style-InkWetBrush-evening-" + tag);
                _editor.ChangeLighting("Замер", l => l with { Enabled = false }, true);

                async Task ViewRoutes(string name)
                {
                    SetScale(centre, zoom); await TimedRefined();
                    StyleDiagnostics.Samples.Clear();
                    await BurstRoute(name + "/still", 40, _ => _view.Canvas.InvalidateVisual());
                    ReportStyle(name + "/still", _editor.Style.Enabled);
                    await BurstRoute(name + "/pan", 40, _ => _view.Canvas.PanByScreenDeltaForSmoke(-10, -4));
                    ReportStyle(name + "/pan", _editor.Style.Enabled);
                }
            }
            // A focused native scenario after measurement (not a UI tour). Only the small map
            // needs it; measured routes keep exactly the same panel/workload as the baseline.
            if (EnvNumber("MAP_NEW_ART_OBJECTS", 1500) == 40) await VerifyStylePanel();
        }
        finally
        {
            StyleDiagnostics.MeasureStages = false;
            StyleDiagnostics.CollectFrames = false;
            StyleDiagnostics.Samples.Clear();
        }
    }

    /// <summary>
    /// Frames until the styled view is finished: no pending terrain or detail tiles and a style frame taken from the
    /// kept buffers. Reports the time, the frames on the way and the longest of them (capped at 5 minutes).
    /// </summary>
    private async Task<string> StyledSettle()
    {
        var canvas = _view.Canvas;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var frames = 0; double longest = 0; var done = false; var stages = "";
        void OnFrame(object? sender, EventArgs e) { frames++; longest = Math.Max(longest, canvas.LastSceneRenderMilliseconds); }
        canvas.FrameRendered += OnFrame;
        try
        {
            StyleDiagnostics.Samples.Clear();
            while (watch.Elapsed < TimeSpan.FromMinutes(5))
            {
                canvas.InvalidateVisual();
                await Task.Delay(15);
                if (!canvas.LastTerrainRefinementPending && !canvas.LastFrameFastTerrain && !canvas.LastDetailTilesPending
                    && StyleDiagnostics.Samples.Any(s => s.Reused)) { done = true; break; }
            }
            // The built (not reused) frames on the way, each as scene/painter-or-print/shader ms.
            stages = string.Join(' ', StyleDiagnostics.Samples.Where(s => !s.Reused && s.Width > 0)
                .Select(s => $"{s.SceneMs:0}/{s.PrintMs:0}/{s.ShaderMs:0}"));
        }
        finally { canvas.FrameRendered -= OnFrame; StyleDiagnostics.Samples.Clear(); }
        _report.Checks["style-settle/" + _editor.Style.Preset] = done;
        // MAP_STYLE_SETTLE_PNG=1: the finished frame as the GPU drew it, for a pixel A/B of two builds.
        if (done && System.Environment.GetEnvironmentVariable("MAP_STYLE_SETTLE_PNG") == "1")
        {
            using var frame = await canvas.CaptureGpuFrameForSmokeAsync();
            using var image = SKImage.FromBitmap(frame);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var path = Path.ChangeExtension(Options.OutputPath, $".settle-{_editor.Style.Preset}-{_editor.Session.Viewport.Scale:0.###}.png");
            File.WriteAllBytes(path, data.ToArray());
            _report.Frames.Add(path);
        }
        return $"{(done ? "" : "NOT DONE ")}{watch.Elapsed.TotalMilliseconds:0} ms; frames={frames}; longest scene={longest:0} ms; " +
            $"built scene/painter/shader ms: {stages}";
    }

    private async Task VerifyStylePanel()
    {
        _view.ShowPanelSection(PanelSection.Styles);
        await Settle();
        T Find<T>(string name) where T : Control => _view.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        foreach (var preset in new[] { MapStylePreset.OldMapBlaeu, MapStylePreset.EngravingAquatint })
        {
            // The preset is a card of the ribbon under the map (DEC-064), marked "selected" when applied.
            var button = Find<Button>("StylePreset" + preset);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            var correct = _editor.Style.Preset == preset && button.Classes.Contains("selected");
            foreach (var kind in Enum.GetValues<StyleEffectKind>())
                correct &= Find<Grid>("Style" + kind + "Row").IsVisible == MapStyle.EffectsOf(preset).Contains(kind);
            var toggle = Find<CheckBox>("StyleCoastToggle");
            toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            correct &= !_editor.Style.Coast.Enabled;
            _editor.Undo(); await Settle();
            correct &= _editor.Style.Coast.Enabled && toggle.IsChecked == true;
            _report.Checks["style-panel/" + preset] = correct;
            await CompareStyleScreenAndExport(preset);
        }
    }

    private async Task CompareStyleScreenAndExport(MapStylePreset preset)
    {
        await TimedRefined();
        using var gpu = await _view.Canvas.CaptureGpuFrameForSmokeAsync();
        var viewport = _editor.Session.Viewport;
        var deviceScale = gpu.Width / _view.Canvas.Bounds.Width;
        var scale = viewport.Scale * deviceScale;
        using var cpu = SKSurface.Create(new SKImageInfo(gpu.Width, gpu.Height))!;
        cpu.Canvas.Scale((float)scale);
        cpu.Canvas.Translate((float)-viewport.OffsetX, (float)-viewport.OffsetY);
        SceneRenderer.Render(cpu.Canvas, _editor.Session.Document, new RenderOptions
        {
            IsExport = true, ViewScale = scale, Stamps = _editor.StampImages,
            VisibleWorld = new WorldBounds(viewport.OffsetX, viewport.OffsetY,
                viewport.OffsetX + gpu.Width / scale, viewport.OffsetY + gpu.Height / scale),
        });
        using var image = cpu.Snapshot();
        var a = gpu;
        using var b = SKBitmap.FromImage(image);
        long sum = 0; var worst = 0; var samples = 0; var off = 0;
        for (var y = 0; y < a.Height; y += 2)
        for (var x = 0; x < a.Width; x += 2)
        {
            var p = a.GetPixel(x, y); var q = b.GetPixel(x, y);
            var dr = Math.Abs(p.Red - q.Red); var dg = Math.Abs(p.Green - q.Green); var db = Math.Abs(p.Blue - q.Blue);
            var d = Math.Max(dr, Math.Max(dg, db));
            sum += dr + dg + db; worst = Math.Max(worst, d); samples++; if (d > 8) off++;
        }
        _report.Environment["style-screen-export/" + preset] =
            $"{gpu.Width}x{gpu.Height}; scale={scale:0.###}; mean RGB delta={sum / (3.0 * samples):0.###}; max={worst}; delta>8={off}/{samples}";
        using var screenImage = SKImage.FromBitmap(gpu);
        foreach (var (tag, frame) in new[] { ("screen", screenImage), ("export", image) })
        {
            var path = Path.ChangeExtension(Options.OutputPath, $".{preset}-{tag}.png");
            using var data = frame.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
            _report.Frames.Add(path);
        }
    }

    private void ReportStyle(string route, bool expected)
    {
        var allSamples = StyleDiagnostics.Samples.ToArray();
        StyleDiagnostics.Samples.Clear();
        // Asset/UI previews also use StyleRenderer. Only the benchmark canvas belongs here.
        var width = (int)EnvNumber("MAP_PAINT_W", 2048);
        var height = (int)EnvNumber("MAP_PAINT_H", 1095);
        var samples = allSamples.Where(s => s.Width == width && s.Height == height).ToArray();
        _report.Checks["style-samples/" + route] = !expected || samples.Length > 0;
        if (samples.Length == 0)
        {
            Console.WriteLine($"    {route} style: no {width}x{height} styled frames; observed=" +
                string.Join(',', allSamples.Select(s => $"{s.Width}x{s.Height}").Distinct()));
            return;
        }
        var reuse = $"reused={samples.Count(s => s.Reused)}/{samples.Length}; followed={samples.Count(s => s.Followed)}/{samples.Length}; " +
            $"pending terrain/details/hex/missing={samples.Max(s => s.PendingTerrain)}/{samples.Max(s => s.PendingDetails)}/{samples.Max(s => s.PendingHex)}/{samples.Max(s => s.MissingAssets)}";
        _report.Environment["style-reuse/" + route] = reuse;
        if (!StyleDiagnostics.MeasureStages) { Console.WriteLine($"    {route} style: {reuse}"); return; }
        static string Stat(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            return $"{Percentile(sorted, .5):0.00}/{Percentile(sorted, .95):0.00}/{sorted[^1]:0.00}";
        }
        var line = $"pass P50/P95/max={Stat(samples.Select(s => s.PassMs))} scene={Stat(samples.Select(s => s.SceneMs))} " +
            $"tensor={Stat(samples.Select(s => s.TensorMs))} soft={Stat(samples.Select(s => s.SoftMs))} " +
            $"wide={Stat(samples.Select(s => s.WideMs))} print={Stat(samples.Select(s => s.PrintMs))} shader={Stat(samples.Select(s => s.ShaderMs))} " +
            $"composite={Stat(samples.Select(s => s.CompositeMs))} reused={samples.Count(s => s.Reused)}/{samples.Length} " +
            $"pixels={string.Join(',', samples.Select(s => $"{s.Width}x{s.Height}").Distinct())}";
        _report.Environment["style/" + route] = line;
        Console.WriteLine($"    {route} style: {line}");
    }
}
