using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Map.Core.Documents;

namespace Map.Performance;

/// <summary>
/// MAP_NEW_ART_LOOK=1 (with MAP_NEW_ART_PERF=1): how «Оформление» feels on the owner's new-art map (owner, 2026-10-31:
/// «оно жесть как лагает… с оптимизацией всё стало очень плохо»). Measured: panning in «Рисовать» (baseline); the
/// press on «Оформление»; panning while the pictures of «Вид» are drawn and after; how long the UI thread is held
/// (a posted no-op's wait); one pointer over a picture - the time until the map shows that look; and a quick sweep of
/// the pointer over twelve pictures and back off, until the map is still again.
/// </summary>
public sealed partial class PaintPerfApplication
{
    private async Task LookRoutes((double X, double Y) centre, double scale)
    {
        Button Find(string name) => _view.GetVisualDescendants().OfType<Button>().First(b => b.Name == name);
        void Press(string name) => Find(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Pan(int _) => _view.Canvas.PanByScreenDeltaForSmoke(-10, -4);
        var canvas = _view.Canvas;

        // How long the UI thread takes to run a posted no-op, sampled every 50 ms for the given time.
        async Task<string> UiWait(TimeSpan span)
        {
            var waits = new List<double>();
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < span)
            {
                var posted = Stopwatch.StartNew();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
                waits.Add(posted.Elapsed.TotalMilliseconds);
                await Task.Delay(50);
            }
            waits.Sort();
            return $"samples={waits.Count} p50={waits[waits.Count / 2]:0} p95={waits[(int)(waits.Count * .95)]:0} max={waits[^1]:0} ms";
        }

        // From a change of the shown map to the finished frame: new frame drawn, no pending refinement.
        async Task<(double First, double Done)> UntilShown()
        {
            var start = canvas.CompletedSceneRevision;
            var watch = Stopwatch.StartNew();
            double first = -1;
            while (watch.Elapsed < TimeSpan.FromSeconds(60))
            {
                canvas.InvalidateVisual();
                await Task.Delay(10);
                if (first < 0 && canvas.CompletedSceneRevision > start) first = watch.Elapsed.TotalMilliseconds;
                if (first >= 0 && !canvas.LastTerrainRefinementPending && !canvas.LastFrameFastTerrain && !canvas.LastDetailTilesPending) break;
            }
            return (first, watch.Elapsed.TotalMilliseconds);
        }

        SetScale(centre, scale); await TimedRefined();
        Press("WorkspaceDrawTab"); await Settle(); await TimedRefined();
        await BurstRoute("look/draw-pan", 40, Pan);
        _report.Environment["look/ui-wait-draw"] = await UiWait(TimeSpan.FromSeconds(2));

        var press = Stopwatch.StartNew();
        Press("WorkspaceAppearanceTab");
        _report.Environment["look/press-appearance-ms"] = press.Elapsed.TotalMilliseconds.ToString("0");
        var pictures = Stopwatch.StartNew();
        _report.Environment["look/ui-wait-while-pictures"] = await UiWait(TimeSpan.FromSeconds(3));
        // The first click on a heavy style 3 s after «Оформление» opened: until the styled map is finished.
        if (System.Environment.GetEnvironmentVariable("MAP_LOOK_FIRST_STYLES") != "0")
        {
            foreach (var preset in new[] { MapStylePreset.WatercolorBigShapes, MapStylePreset.OldMapBlaeu, MapStylePreset.CharcoalVineStump })
            {
                _editor.ApplyStylePreset(preset);
                var (first, done) = await UntilShown();
                _report.Environment["look/first-click/" + preset] = $"first frame {first:0} ms, finished {done:0} ms";
            }
            _editor.SetStyleEnabled(false); await UntilShown();
        }
        await BurstRoute("look/appearance-pan-while-pictures", 40, Pan);
        Button[] Tiles() => [.. _view.GetVisualDescendants().OfType<Button>().Where(b => b.Name?.StartsWith("LookCard_", StringComparison.Ordinal) == true)];
        while (pictures.Elapsed < TimeSpan.FromMinutes(3)
               && Tiles().Any(b => b.GetVisualDescendants().OfType<Border>().FirstOrDefault()?.Background is not ImageBrush))
            await Task.Delay(100);
        _report.Environment["look/pictures-drawn-ms"] = pictures.Elapsed.TotalMilliseconds.ToString("0");
        await Settle(); await TimedRefined();
        await BurstRoute("look/appearance-pan-after", 40, Pan);
        _report.Environment["look/ui-wait-after"] = await UiWait(TimeSpan.FromSeconds(2));

        // The pointer over one picture, as the page does it (PointerEntered → PreviewLook(LookDocument(key))).
        var lookDocument = typeof(Map.App.Views.EditorView).GetMethod("LookDocument", BindingFlags.NonPublic | BindingFlags.Instance)!;
        MapDocument Look(string key) => (MapDocument)lookDocument.Invoke(_view, [key])!;
        // Each look twice: the first time in the session (cold) and again (warm).
        foreach (var key in new[] { "style-InkWetBrush", "style-OldMapBlaeu", "light-evening", "sea-2", "style-InkWetBrush#2", "style-OldMapBlaeu#2" })
        {
            _editor.PreviewLook(Look(key.Split('#')[0]));
            var (first, done) = await UntilShown();
            _report.Environment["look/hover/" + key] = $"first frame {first:0} ms, finished {done:0} ms, longest scene {canvas.LastSceneRenderMilliseconds:0} ms";
            _editor.PreviewLook(null);
            var (back, backDone) = await UntilShown();
            _report.Environment["look/hover-off/" + key] = $"first frame {back:0} ms, finished {backDone:0} ms";
        }

        // A quick sweep: the pointer crosses twelve pictures (80 ms each) and leaves the page - through the page's own
        // pointer handling (HoverLook), as the pointer events call it.
        var hover = typeof(Map.App.Views.EditorView).GetMethod("HoverLook", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var keys = Tiles().Select(b => (string)b.Tag!).Where(k => k is not ("none" or "sea-none")).Take(12).ToArray();
        var sweep = Stopwatch.StartNew();
        var framesBefore = canvas.CompletedSceneRevision;
        foreach (var key in keys) { hover.Invoke(_view, [key]); await Task.Delay(80); hover.Invoke(_view, [null]); }
        // The pointer has left; the page may still owe the map one change. Frames drawn by the sweep tell what it cost.
        var sweepUi = await UiWait(TimeSpan.FromSeconds(1.5));
        _report.Environment["look/sweep-12"] = $"pointer {keys.Length * 80} ms; {sweep.Elapsed.TotalMilliseconds:0} ms with the wait; frames {canvas.CompletedSceneRevision - framesBefore}; ui {sweepUi}";
        // Resting on a light: the map shows it 300 ms later, the time the page waits for the pointer to rest.
        var rest = Stopwatch.StartNew();
        var before = canvas.CompletedSceneRevision;
        hover.Invoke(_view, ["light-night"]);
        while (canvas.CompletedSceneRevision == before && rest.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        _report.Environment["look/rest-on-light"] = $"shown after {rest.Elapsed.TotalMilliseconds:0} ms";
        _report.Checks["look/rest-shows-light"] = _editor.LookPreviewShown;
        hover.Invoke(_view, [null]); await Task.Delay(200);
        _report.Checks["look/leave-restores"] = !_editor.LookPreviewShown;
        // Resting on a style does not restyle the whole map.
        hover.Invoke(_view, ["style-OldMapBlaeu"]); await Task.Delay(500);
        _report.Checks["look/style-not-tried-on-map"] = !_editor.LookPreviewShown;
        hover.Invoke(_view, [null]);

        Press("WorkspaceDrawTab"); await Settle();
        _report.Checks["look/measured"] = true;
    }
}
