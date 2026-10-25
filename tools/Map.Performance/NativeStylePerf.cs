using Map.Core.Documents;
using Map.Rendering.Styles;

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
        _report.Environment["style/stages"] = measure.ToString();
        _report.Environment["style/window"] = $"{_window.Bounds.Width}x{_window.Bounds.Height} DIP at {_window.Position}; scaling={_window.RenderScaling}";
        _report.Environment["style/target-pixels"] = $"{width}x{height}";
        var presets = new[] { MapStylePreset.InkWetBrush, MapStylePreset.InkPenHatching, MapStylePreset.InkPenStipple,
            MapStylePreset.OldMapBlaeu, MapStylePreset.EngravingAquatint };
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
                    await ViewRoutes(name);
                    SetScale(centre, zoom); await TimedRefined();
                    StyleDiagnostics.Samples.Clear();
                    // All 50 previews have nonzero strength, so every step exercises the style.
                    await BurstRoute(name + "/slider", 50, i => _editor.SetStyleStrength((i + 1) / 50.0, commit: false));
                    ReportStyle(name + "/slider", expected: true);
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
        }
        finally
        {
            StyleDiagnostics.MeasureStages = false;
            StyleDiagnostics.Samples.Clear();
        }
    }

    private void ReportStyle(string route, bool expected)
    {
        var allSamples = StyleDiagnostics.Samples.ToArray();
        StyleDiagnostics.Samples.Clear();
        if (!StyleDiagnostics.MeasureStages) return;
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
        static string Stat(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            return $"{Percentile(sorted, .5):0.00}/{Percentile(sorted, .95):0.00}/{sorted[^1]:0.00}";
        }
        var line = $"pass P50/P95/max={Stat(samples.Select(s => s.PassMs))} scene={Stat(samples.Select(s => s.SceneMs))} " +
            $"tensor={Stat(samples.Select(s => s.TensorMs))} soft={Stat(samples.Select(s => s.SoftMs))} " +
            $"wide={Stat(samples.Select(s => s.WideMs))} shader={Stat(samples.Select(s => s.ShaderMs))} " +
            $"composite={Stat(samples.Select(s => s.CompositeMs))} reused={samples.Count(s => s.Reused)}/{samples.Length} " +
            $"pixels={string.Join(',', samples.Select(s => $"{s.Width}x{s.Height}").Distinct())}";
        _report.Environment["style/" + route] = line;
        Console.WriteLine($"    {route} style: {line}");
    }
}
