using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Map.Core.Documents;

namespace Map.Performance;

/// <summary>
/// MAP_NEW_ART_RIBBON=1 (with MAP_NEW_ART_PERF=1): a short live check of Свет / Стиль of «Вид» in «Оформление» (the
/// ribbon under the map that repeated the same cards was removed 2026-10-31) on the new-art test map - the page with
/// its pictures, a style applied (one Undo step), every style applied from its picture, the style's settings opened
/// under it, Вечер applied. Each state is saved as a picture of the whole window (Avalonia frame), and the checks go to
/// the report. No measurements.
/// </summary>
public sealed partial class PaintPerfApplication
{
    private async Task RibbonShots()
    {
        T Find<T>(string name) where T : Control => _view.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
        void Press(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Shot(string name)
        {
            await Settle();
            var size = new PixelSize((int)Math.Ceiling(_window.Bounds.Width), (int)Math.Ceiling(_window.Bounds.Height));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            bitmap.Render(_window);
            var path = Path.ChangeExtension(Options.OutputPath, "." + name + ".png");
            bitmap.Save(path);
            _report.Frames.Add(path);
        }
        Button[] Cards() => [.. _view.GetVisualDescendants().OfType<Button>().Where(b => b.Name?.StartsWith("LookCard_", StringComparison.Ordinal) == true)];
        async Task WaitPreviews()
        {
            // The pictures are drawn off the UI thread: wait until every picture has its image (at most 60 s).
            for (var i = 0; i < 240; i++)
            {
                await Task.Delay(250);
                var pending = Cards().Count(b => b.GetVisualDescendants().OfType<Border>().FirstOrDefault()?.Background is not ImageBrush);
                if (pending == 0) break;
            }
            await Settle();
        }

        Press("WorkspaceAppearanceTab");
        await WaitPreviews();
        await Shot("look-page");
        // One place for light, style and sea: no ribbon under the map, no section buttons for them in the panel.
        _report.Checks["ribbon/no-second-copy"] = !_view.GetVisualDescendants().OfType<Control>()
            .Any(c => c.Name is "AppearanceRibbon" or "PanelSectionLight" or "PanelSectionStyles" or "RowLookButton" or "RightTabLook");
        _report.Checks["ribbon/style-cards"] = Cards().Count(b => b.Name!.StartsWith("LookCard_style-", StringComparison.Ordinal))
            == Enum.GetValues<MapStylePreset>().Length;

        var undoBefore = _editor.CanUndo;
        Press("LookCard_style-" + MapStylePreset.OldMapBlaeu);
        await Task.Delay(1500);
        await Shot("look-style-blaeu");
        _report.Checks["ribbon/card-applies"] = _editor.Style is { Enabled: true, Preset: MapStylePreset.OldMapBlaeu };
        _editor.Undo(); await Settle();
        _report.Checks["ribbon/card-one-undo"] = _editor.Style is not { Enabled: true, Preset: MapStylePreset.OldMapBlaeu } || !undoBefore;

        // Every preset applied from its picture, one shot each, with the style's settings open under the pictures.
        Press("LookFine_style"); await Settle();
        _report.Checks["ribbon/style-settings-open"] = Find<Control>("LookFineBody_style").IsEffectivelyVisible;
        foreach (var preset in Enum.GetValues<MapStylePreset>().Where(p => p > MapStylePreset.EngravingAquatint))
        {
            Press("LookCard_style-" + preset);
            await Task.Delay(2500);
            await Shot("look-style-" + preset);
            _report.Checks["ribbon/applies-" + preset] = _editor.Style is { Enabled: true } s && s.Preset == preset;
        }
        _editor.SetStyleEnabled(false); await Settle();

        Press("LookCard_light-evening");
        await Task.Delay(1500);
        await Shot("look-light-evening");
        _report.Checks["ribbon/light-evening"] = _editor.ShownLighting is { Enabled: true } l && Math.Abs(l.TimeOfDay - 0.5) < 0.02;
        // DEC-070 p. 6: every visible control of the page (and the window around it) has its line.
        var noTip = PanelAudit.MissingTips(_view);
        File.WriteAllLines(Path.ChangeExtension(Options.OutputPath, "ribbon-no-tooltip.txt"), noTip);
        foreach (var name in noTip) Console.WriteLine("no tooltip: " + name);
        _report.Checks["ribbon/every-visible-control-has-tooltip"] = noTip.Count == 0;
    }
}
