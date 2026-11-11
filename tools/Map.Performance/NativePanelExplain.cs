using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Map.Presentation.ViewModels;

namespace Map.Performance;

public sealed partial class NativeEditorScenarioApplication
{
    /// <summary>
    /// `native-panel-explain`: the asset panel in variant 1 «Строки и показ рядом» (owner, 2026-11-11) in the real editor
    /// window (off-screen, no system input), with the owner's mountains from the library. It draws the panel for new
    /// placements and for a selected path, opens the card of every explained row and button, waits for its frames from
    /// the program's renderer, and saves the panel and each card as pictures (`&lt;output&gt;.explain-*.png`). Checks: every
    /// card got its ten frames, the value rows stand on one line, the path's background-calculation buttons are in the
    /// panel, the scope words carry no pill.
    /// </summary>
    private async Task PanelExplainOnly()
    {
        var catalog = new Map.Assets.AssetCatalog(Path.Combine(_workspace, "explain-catalog"));
        catalog.ImportSet([.. Enumerable.Range(1, 6).Select(i => OwnerMountainFixtures.Path(i))], OwnerMountainFixtures.Names[0],
            new Map.Assets.AssetImportOptions(), DateTimeOffset.UnixEpoch);
        OpenEditor(StylePacksSheet.DemoMap(), null, catalog); await Settle();
        _view.ShowWorkspace(EditorWorkspace.Objects); await Settle();
        _editor.ChooseGalleryVariant(catalog.Sets[0].Id, null); await Settle();
        _editor.SetStampScalePercent(120, true); _editor.SetStampRotationDegrees(15, true); await Settle();
        var bad = new List<string>();
        var times = new List<string>();
        var panel = _view.GetVisualDescendants().OfType<Control>().First(c => c.Name == "ContextPanel");

        void Picture(Control control, string key)
        {
            var size = control.Bounds.Size;
            if (size.Width < 1 || size.Height < 1) { bad.Add(key + ": нет размера"); return; }
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)));
            bitmap.Render(control);
            using var file = File.Create(Path.ChangeExtension(Options.OutputPath, $"explain-{key}.png"));
            bitmap.Save(file, PngBitmapEncoderOptions.Default);
        }

        async Task Cards(string stage)
        {
            var seen = new HashSet<PanelDemoKind>();
            foreach (var (target, kind, title) in _view.ExplainedForSmoke.ToArray())
            {
                // A section hidden since the list was read (the deferred panel refresh) has nothing to explain.
                if (!target.IsEffectivelyVisible || !seen.Add(kind)) continue;
                target.BringIntoView(); await Settle();
                var watch = Stopwatch.StartNew();
                _view.ShowExplainForSmoke(target);
                while (!_view.ExplainReadyForSmoke(kind) && watch.Elapsed < TimeSpan.FromSeconds(30)) { await Task.Delay(50); await Settle(); }
                var (shown, frames, open) = _view.ExplainStateForSmoke;
                times.Add($"{stage}/{kind}: {watch.ElapsedMilliseconds} ms, кадров {frames}");
                Console.WriteLine($"explain {stage}/{kind} «{title}»: {watch.ElapsedMilliseconds} ms, frames {frames}, open {open}");
                if (shown != kind || frames != 10 || !open) bad.Add($"{kind}: кадров {frames}, открыта {open}");
                else if (_view.ExplainCardForSmoke is { } card)
                {
                    Picture(card, $"{stage}-{kind}");
                    // MAP_EXPLAIN_FRAMES=1: every frame of the card, for the owner's page of animations.
                    if (Environment.GetEnvironmentVariable("MAP_EXPLAIN_FRAMES") == "1")
                        for (var i = 0; i < frames; i++) { _view.ShowExplainFrameForSmoke(i); await Settle(); Picture(card, $"{stage}-{kind}-f{i}"); }
                }
                _view.HideExplainForSmoke(); await Settle();
            }
        }

        // 1. New placements: «Для новых», «Как ставить», «Новый ассет».
        panel.InvalidateMeasure(); await Settle();
        Picture(panel, "panel-new");
        await Cards("new");
        // One line: the scale's slider and its number stand level.
        var slider = _view.GetVisualDescendants().OfType<Control>().First(c => c.Name == "FutureStampScaleSlider");
        var box = _view.GetVisualDescendants().OfType<Control>().First(c => c.Name == "FutureStampScaleSliderBox");
        var sy = slider.TranslatePoint(new Point(0, slider.Bounds.Height / 2), panel)!.Value.Y;
        var by = box.TranslatePoint(new Point(0, box.Bounds.Height / 2), panel)!.Value.Y;
        _report.Checks["value-row-one-line"] = Math.Abs(sy - by) < 6;
        var chips = _view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("scope-chip") && b.IsEffectivelyVisible).ToArray();
        _report.Checks["scope-words-no-pill"] = chips.All(b => b.Background is null && b.BorderThickness == default);

        // 2. A path of mountains, then the path selected: «Выделенное», the path section and its commands.
        _editor.StampPlacementMode = StampPlacementMode.Path; await Settle();
        foreach (var (x, y) in new[] { (300.0, 520.0), (650.0, 380.0), (1000.0, 560.0), (1350.0, 420.0) }) _editor.AddEditablePathNode(x, y);
        _editor.FinishEditablePath(); await Settle();
        if (_editor.PendingPathCalculation is { } pending) await pending;
        for (var i = 0; i < 100 && _editor.PathCalculationBusy; i++) { await Task.Delay(100); await Settle(); }
        await Task.Delay(400); await Settle();
        _report.Checks["path-made"] = _editor.SelectedEditablePath is { Members.Length: > 0 };
        for (var i = 0; i < 6; i++) { await Task.Delay(300); await Settle(); }
        var spacingBox = _view.GetVisualDescendants().OfType<Control>().First(c => c.Name == "EditablePathSpacingBox");
        Console.WriteLine($"explain: spacing box {spacingBox.Bounds.Width:0} px, busy {_editor.PathCalculationBusy}, summary «{_editor.EditablePathSummary}»");
        _report.Checks["path-numbers-compact"] = spacingBox.Bounds.Width < 80;
        panel.InvalidateMeasure(); await Settle();
        Picture(panel, "panel-path");
        await Cards("path");
        var inPanel = panel.GetVisualDescendants().OfType<Control>().Select(c => c.Name).ToHashSet();
        _report.Checks["path-calculation-buttons-in-panel"] = inPanel.Contains("PathCalculationCancelButton") && inPanel.Contains("PathRecalculateButton");
        // The path's randomness lives in «Как ставить»: it stays open with a selected path (native-stage17).
        _report.Checks["how-kept-for-selected-path"] = _view.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c.Name == "StampModePath") is { IsEffectivelyVisible: true };

        foreach (var line in bad) Console.WriteLine("explain: " + line);
        File.WriteAllLines(Path.ChangeExtension(Options.OutputPath, "explain-timings.txt"), times);
        _report.Checks["explain-cards"] = bad.Count == 0;
    }
}
