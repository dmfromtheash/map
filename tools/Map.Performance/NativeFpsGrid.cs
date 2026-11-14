using System.Globalization;
using System.Text.Json;
using Avalonia.Input;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.Performance;

public sealed partial class ZoomAssetsApplication
{
    private async Task ExecuteFpsGrid(ZoomScenarioReport scenario, (double X, double Y) anchor, double fit)
    {
        const int steps = 300;
        var canvas = _view.Canvas;
        var layer = _editor.Session.Document.Layers.OfType<ObjectLayer>().Last();
        var selectedId = layer.Objects[^1].Id;
        _report.Environment["grid-steps"] = steps.ToString(CultureInfo.InvariantCulture);
        _report.Environment["grid-style"] = _editor.Session.Document.Style?.Preset.ToString() ?? "off";
        _report.Environment["grid-fps-definition"] = "median of equal ~1s windows including empty windows; Fps also retains whole-route throughput";
        _report.Environment["grid-wheel"] = "300 fractional wheel deltas per zoom segment through production HandleWheel; not OS hardware input";
        _report.Environment["grid-cache"] = "shared warm derived cache; process/render caches start empty";
        _report.Environment["grid-side-product-limit"] = CanvasDimensionPolicy.Maximum.ToString(CultureInfo.InvariantCulture);
        _report.Environment["grid-selection"] = "public select-object/drag commands on original centered stamp; not alpha hit-testing";
        await Pan("fit");
        foreach (var scale in new[] { 1d, 4d, 16d })
        {
            await Wheel(scale, "in-" + Label(scale));
            await Pan(Label(scale));
            await MillionCapture("grid-" + Label(scale));
            SaveProgress();
        }
        foreach (var scale in new[] { 4d, 1d, fit })
            await Wheel(scale, "out-" + (scale == fit ? "fit" : Label(scale)));

        await Event("edge/setup", () => SetScale((canvas.Bounds.Width / 2, anchor.Y), 1));
        var distance = Math.Max(0, scenario.CanvasWidth - canvas.Bounds.Width);
        await BurstRoute(scenario, "pan/edge-to-edge-100", steps,
            _ => canvas.PanByScreenDeltaForSmoke(-distance / steps, 0));
        SaveProgress();
        await Event("edit/setup", () => { _editor.ActiveTool = EditorTool.Select; SetScale(anchor, 1); });
        await Route(scenario, "edit/select", 10, _ =>
        {
            _editor.DeselectObject();
            _editor.SelectObject(selectedId);
        });
        Require("selected", _editor.Session.SelectedObjectId == selectedId);
        await Settled(scenario.Routes[^1]);
        var initial = _editor.Session.SelectedObject!;
        await Route(scenario, "edit/drag", 10, _ =>
        {
            var obj = _editor.Session.SelectedObject!;
            var x = obj.X + obj.Width / 2;
            var y = obj.Y + obj.Height / 2;
            Require("drag-started", _editor.BeginDrag(x, y));
            _editor.UpdateDragTo(x + 3, y + 2);
            _editor.EndDrag();
        });
        Require("dragged", _editor.Session.SelectedObject is { } moved && moved.X != initial.X);
        await Settled(scenario.Routes[^1]);
        await Route(scenario, "edit/undo-drag", 10, _ => _editor.Undo());
        Require("undo-drag", FindSelected() == initial);
        await Route(scenario, "edit/redo-drag", 10, _ => _editor.Redo());
        Require("redo-drag", FindSelected().X != initial.X);

        var beforePlacement = ObjectCount();
        var set = _catalog.Sets.First(s => !s.Archived);
        Require("placement-armed", _editor.ChooseGalleryVariant(set.Id, null));
        await Route(scenario, "edit/place-10", 10, i =>
        {
            var p = _editor.Session.Viewport.WorldToScreen(anchor.X - 160 + i * 32, anchor.Y + 120);
            canvas.MovePlacementPointerForSmoke(p.X, p.Y);
            canvas.CommitPlacementForSmoke();
        });
        _editor.CancelPlacement();
        Require("placed-10", ObjectCount() == beforePlacement + 10);
        await Settled(scenario.Routes[^1]);
        await Route(scenario, "edit/undo-place", 10, _ => _editor.Undo());
        Require("undo-place", ObjectCount() == beforePlacement);
        await Route(scenario, "edit/redo-place", 10, _ => _editor.Redo());
        Require("redo-place", ObjectCount() == beforePlacement + 10);
        await Settled(scenario.Routes[^1]);
        await MillionCapture("grid-edits");
        SaveProgress();
        _report.Checks["grid/gpu"] = scenario.Routes.All(route => route.Gpu);
        _report.Checks["grid/routes-complete"] = scenario.Routes.Count(route => route.Name.StartsWith("wheel/", StringComparison.Ordinal)) == 6;
        _report.Checks["grid/fps-60"] = scenario.Routes.Where(route => route.MedianWindowFps is not null)
            .All(route => route.MedianWindowFps >= 60 && route.OnePercentLowFps >= 60 && route.FrameWorkMax <= 16.7);
        await Finish(scenario);

        async Task Pan(string name)
        {
            await BurstRoute(scenario, "pan/" + name, steps,
                i => canvas.PanByScreenDeltaForSmoke(i < steps / 2 ? -3 : 3, 0));
            SaveProgress();
        }
        async Task Wheel(double target, string name)
        {
            var from = _editor.Session.Viewport.Scale;
            var delta = Math.Log(target / from) / Math.Log(1.15) / steps;
            await BurstRoute(scenario, "wheel/" + name, steps, _ =>
            {
                var p = _editor.Session.Viewport.WorldToScreen(anchor.X, anchor.Y);
                canvas.WheelForSmoke(p.X, p.Y, delta, KeyModifiers.None);
            });
            Require("scale-" + name, Math.Abs(_editor.Session.Viewport.Scale - target) < Math.Max(1e-6, target * 1e-5));
            SaveProgress();
        }
        async Task Event(string name, Action action)
        {
            await Route(scenario, name, 1, _ => action());
            await Settled(scenario.Routes[^1]);
            SaveProgress();
        }
        void Require(string name, bool result)
        {
            _report.Checks["grid/" + name] = result;
            if (!result) throw new InvalidOperationException("FPS grid failed: " + name + "; " + _editor.StatusDetail);
        }
        int ObjectCount() => _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(item => item.Objects.Count);
        SceneObject FindSelected() => _editor.Session.Document.Layers.OfType<ObjectLayer>()
            .Single(item => item.Id == layer.Id).Objects.First(item => item.Id == selectedId);
        static string Label(double scale) => (scale * 100).ToString("0", CultureInfo.InvariantCulture);
        void SaveProgress() => File.WriteAllText(Path.ChangeExtension(Options.OutputPath, ".progress.json"),
            JsonSerializer.Serialize(_report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
