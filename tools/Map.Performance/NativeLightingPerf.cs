using Map.Core.Documents;
using Map.Rendering.Lighting;

namespace Map.Performance;

/// <summary>
/// The Свет section on the owner's mountains (R09, threshold ≤ 4 ms p95 for the light pass):
/// MAP_NEW_ART_PERF=1 with MAP_NEW_ART_LIGHT=1 opens the new-art test map (MAP_NEW_ART_OBJECTS,
/// e.g. 50 000) and, instead of the editing routes, measures still frames and pans with the light
/// off, at day, evening and night (a dozen torches and fires near the centre), and a drag of the
/// time slider. Each stage of the pass waits for the GPU (<see cref="LightingDiagnostics"/>), so
/// the pass time is GPU work, not only command recording. Prints P50/P95/max per route.
/// </summary>
public sealed partial class PaintPerfApplication
{
    private async Task LightingRoutes((double X, double Y) centre, double scale)
    {
        var canvas = _view.Canvas;
        LightingDiagnostics.MeasureStages = System.Environment.GetEnvironmentVariable("MAP_LIGHT_STAGES") != "0";
        var random = new Random(20261024);
        var sources = Enumerable.Range(0, 12).Select(i => new MapLightSource(Guid.NewGuid(),
            centre.X + (random.NextDouble() - .5) * 2000, centre.Y + (random.NextDouble() - .5) * 1400,
            i % 3 == 0 ? "#ff8a3c" : "#ff9a42", i % 3 == 0 ? 1000 : 750, i % 3 == 0 ? 1.3 : 1.1, true)).ToArray();
        _editor.ChangeLighting("Замер", _ => new MapLighting { Enabled = true, Sources = [.. sources] }, true);

        foreach (var zoom in new[] { scale, 1.0 })
        {
            var tag = zoom == scale ? "far" : "100";
            foreach (var (name, time, enabled) in new[] { ("off", 0.0, false), ("day", 0.0, true), ("evening", 0.5, true), ("night", 1.0, true) })
            {
                _editor.ChangeLighting("Замер", l => l with { Enabled = enabled, TimeOfDay = time }, true);
                SetScale(centre, zoom); await TimedRefined();
                LightingDiagnostics.Samples.Clear();
                await BurstRoute($"light-{name}-{tag}/still", 40, _ => canvas.InvalidateVisual());
                ReportLight($"light-{name}-{tag}/still");
                await BurstRoute($"light-{name}-{tag}/pan", 40, _ => canvas.PanByScreenDeltaForSmoke(-10, -4));
                ReportLight($"light-{name}-{tag}/pan");
                if (name == "evening") await Capture($"light-{name}-{tag}");
            }
            // Only the light changes while the time slider is dragged.
            _editor.ChangeLighting("Замер", l => l with { Enabled = true, TimeOfDay = 0 }, true);
            SetScale(centre, zoom); await TimedRefined();
            LightingDiagnostics.Samples.Clear();
            await BurstRoute($"light-slider-{tag}", 50, i => _editor.SetTimeOfDay(i / 49.0, commit: false));
            ReportLight($"light-slider-{tag}");
            _editor.SetTimeOfDay(1, commit: true);
        }
        LightingDiagnostics.MeasureStages = false;
    }

    private void ReportLight(string route)
    {
        var samples = LightingDiagnostics.Samples.ToArray();
        LightingDiagnostics.Samples.Clear();
        if (samples.Length == 0) { Console.WriteLine($"    {route} light: no lit frames"); return; }
        static string Stat(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            return $"{Percentile(sorted, .5):0.00}/{Percentile(sorted, .95):0.00}/{sorted[^1]:0.00}";
        }
        var line = $"pass P50/P95/max={Stat(samples.Select(s => s.PassMs))} buffers={Stat(samples.Select(s => s.BuffersMs))} " +
            $"shader={Stat(samples.Select(s => s.ShaderMs))} scene={Stat(samples.Select(s => s.SceneMs))} " +
            $"shapes={samples.Max(s => s.ShapeDraws)} reused={samples.Count(s => s.BuffersReused)}/{samples.Length}";
        _report.Environment["light/" + route] = line;
        Console.WriteLine($"    {route} light: {line}");
    }
}
