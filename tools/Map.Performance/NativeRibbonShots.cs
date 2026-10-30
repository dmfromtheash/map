using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Map.Core.Documents;

namespace Map.Performance;

/// <summary>
/// MAP_NEW_ART_RIBBON=1 (with MAP_NEW_ART_PERF=1): a short live check of the Свет / Стили ribbon under the map (DEC-064)
/// on the new-art test map - the ribbon closed, the Стили tab with its preview cards, a card applied (one Undo step),
/// the Свет tab with Вечер applied. Each state is saved as a picture of the whole window (Avalonia frame), and the
/// checks go to the report. No measurements.
/// </summary>
public sealed partial class PaintPerfApplication
{
    private async Task RibbonShots()
    {
        T Find<T>(string name) where T : Control => _view.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
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
        async Task WaitPreviews(int cards)
        {
            // The cards are drawn off the UI thread: wait until every visible card has its picture (at most 60 s).
            for (var i = 0; i < 240; i++)
            {
                await Task.Delay(250);
                var pending = _view.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.Classes.Contains("ribbon-card") && b.IsEffectivelyVisible)
                    .Count(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "…"));
                if (pending == 0) break;
            }
            await Settle();
        }

        // DEC-070: the ribbon stands in «Оформление» only.
        Find<Button>("WorkspaceAppearanceTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Shot("ribbon-closed");
        _report.Checks["ribbon/right-panel-without-light-styles"] = !_view.GetVisualDescendants().OfType<Control>()
            .Any(c => c.Name is "PanelSectionLight" or "PanelSectionStyles");

        Find<Button>("RibbonStyleTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitPreviews(0);
        await Shot("ribbon-styles");
        _report.Checks["ribbon/style-cards"] = _view.GetVisualDescendants().OfType<Button>()
            .Count(b => b.Classes.Contains("ribbon-card")) == Enum.GetValues<MapStylePreset>().Length + 1;

        var undoBefore = _editor.CanUndo;
        Find<Button>("StylePreset" + MapStylePreset.OldMapBlaeu).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(1500);
        await Shot("ribbon-styles-blaeu");
        _report.Checks["ribbon/card-applies"] = _editor.Style is { Enabled: true, Preset: MapStylePreset.OldMapBlaeu };
        _editor.Undo(); await Settle();
        _report.Checks["ribbon/card-one-undo"] = _editor.Style is not { Enabled: true, Preset: MapStylePreset.OldMapBlaeu } || !undoBefore;

        // Every preset applied from its card, one picture each (the effects column follows the preset).
        foreach (var preset in Enum.GetValues<MapStylePreset>().Where(p => p > MapStylePreset.EngravingAquatint))
        {
            Find<Button>("StylePreset" + preset).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(2500);
            await Shot("ribbon-styles-" + preset);
            _report.Checks["ribbon/applies-" + preset] = _editor.Style is { Enabled: true } s && s.Preset == preset;
        }
        _editor.SetStyleEnabled(false); await Settle();

        Find<Button>("RibbonLightTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitPreviews(0);
        Find<Button>("LightCardevening").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(1500);
        await Shot("ribbon-light-evening");
        _report.Checks["ribbon/light-evening"] = _editor.ShownLighting is { Enabled: true } l && Math.Abs(l.TimeOfDay - 0.5) < 0.02;
        // DEC-070 p. 6: every visible control of the ribbon (and the window around it) has its line.
        var noTip = PanelAudit.MissingTips(_view);
        File.WriteAllLines(Path.ChangeExtension(Options.OutputPath, "ribbon-no-tooltip.txt"), noTip);
        foreach (var name in noTip) Console.WriteLine("no tooltip: " + name);
        _report.Checks["ribbon/every-visible-control-has-tooltip"] = noTip.Count == 0;
    }
}
