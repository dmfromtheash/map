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

        async Task Cards(string stage, Func<PanelDemoKind, bool>? only = null)
        {
            var seen = new HashSet<PanelDemoKind>();
            foreach (var (target, kind, title) in _view.ExplainedForSmoke.ToArray())
            {
                // A section hidden since the list was read (the deferred panel refresh) has nothing to explain.
                if (!target.IsEffectivelyVisible || only?.Invoke(kind) == false || !seen.Add(kind)) continue;
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

        // 3. One object selected with the asset tool: the switch «Выделенное / Для новых» shows, and its two halves
        //    have their cards (the switch is hidden with nothing selected, so stages 1-2 never reach them).
        var someObject = _editor.Session.Document.Layers.OfType<Map.Core.Documents.ObjectLayer>().SelectMany(l => l.Objects).First();
        _editor.SelectObject(someObject.Id); await Settle();
        if (_editor.ActiveTool != Map.Presentation.ViewModels.EditorTool.Stamp) { _editor.ActiveTool = Map.Presentation.ViewModels.EditorTool.Stamp; await Settle(); }
        await Task.Delay(300); await Settle();
        panel.InvalidateMeasure(); await Settle();
        Picture(panel, "panel-selected");
        var scopeKinds = _view.ExplainedForSmoke.Where(e => e.Kind is PanelDemoKind.ScopeSelected or PanelDemoKind.ScopeNew).Select(e => e.Kind).ToHashSet();
        Console.WriteLine($"explain: selection {_editor.SelectionCount}, tool {_editor.ActiveTool}, scope cards {string.Join(", ", scopeKinds)}");
        _report.Checks["scope-cards-with-selection"] = scopeKinds.SetEquals([PanelDemoKind.ScopeSelected, PanelDemoKind.ScopeNew]);
        await Cards("selected", k => k is PanelDemoKind.ScopeSelected or PanelDemoKind.ScopeNew);

        // 4. A narrow window (right column 280 px, «narrow-window»): no row runs into another, and a value row whose
        //    slider does not fit beside its name puts the slider under it.
        _editor.Session.ClearSelection(); await Settle();
        _window.Width = 1000; await Task.Delay(300); await Settle();
        panel.InvalidateMeasure(); await Settle();
        Console.WriteLine($"explain: narrow {_view.Classes.Contains("narrow-window")}, panel {panel.Bounds.Width:0} px");
        Picture(panel, "panel-narrow");
        var overlaps = new List<string>();
        var under = 0;
        foreach (var row in panel.GetVisualDescendants().OfType<Map.App.Views.Ui.ValueLinePanel>().Where(r => r.IsEffectivelyVisible))
        {
            var parts = row.Children.Where(c => c.IsVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0).ToArray();
            for (var i = 0; i < parts.Length; i++)
                for (var j = i + 1; j < parts.Length; j++)
                    if (parts[i].Bounds.Intersect(parts[j].Bounds) is { Width: > 1, Height: > 1 }) overlaps.Add($"строка {row.Children[0]}: части наезжают");
            if (row.Children.Count == 3 && row.Children[1].Bounds.Top >= row.Children[0].Bounds.Bottom - 1) under++;
        }
        // Only what is in view: text scrolled under the panel's header is clipped there, not overlapping it.
        Rect InView(Control c)
        {
            var rect = new Rect(c.TranslatePoint(new Point(0, 0), panel)!.Value, c.Bounds.Size);
            foreach (var sv in c.GetVisualAncestors().OfType<ScrollViewer>())
                rect = rect.Intersect(new Rect(sv.TranslatePoint(new Point(0, 0), panel)!.Value, sv.Bounds.Size));
            return rect;
        }
        var texts = panel.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && t.Bounds.Width > 1 && t.Bounds.Height > 1 && !string.IsNullOrEmpty(t.Text))
            .Select(t => (t.Text, Rect: InView(t), Block: t)).Where(t => t.Rect.Width > 1 && t.Rect.Height > 1).ToArray();
        for (var i = 0; i < texts.Length; i++)
            for (var j = i + 1; j < texts.Length; j++)
                if (texts[i].Rect.Intersect(texts[j].Rect) is { Width: > 2, Height: > 2 }) overlaps.Add($"«{texts[i].Text}» и «{texts[j].Text}»");
        // A caption cut by its box («Новый пут») is as bad as one running into another.
        foreach (var (text, _, block) in texts)
            if (block.TextLayout.TextLines.Any(l => l.HasCollapsed) || block.TextLayout.Width > block.Bounds.Width + 1)
                overlaps.Add($"«{text}» обрезано ({block.TextLayout.Width:0} > {block.Bounds.Width:0} px)");
        foreach (var line in overlaps.Distinct().Take(20)) Console.WriteLine("explain narrow: " + line);
        Console.WriteLine($"explain narrow: {texts.Length} строк текста, ползунков под названием {under}, наездов {overlaps.Count}");
        _report.Checks["narrow-window-280"] = _view.Classes.Contains("narrow-window") && panel.Bounds.Width <= 282;
        _report.Checks["narrow-no-overlap"] = overlaps.Count == 0;
        _report.Checks["narrow-slider-under-name"] = under > 0;

        foreach (var line in bad) Console.WriteLine("explain: " + line);
        File.WriteAllLines(Path.ChangeExtension(Options.OutputPath, "explain-timings.txt"), times);
        _report.Checks["explain-cards"] = bad.Count == 0;
    }
}
