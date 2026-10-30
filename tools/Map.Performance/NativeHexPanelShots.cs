using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Map.App.Views;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;

namespace Map.Performance;

public sealed partial class RegionalApplication
{
    /// <summary>
    /// `native-hex-panel-shots`: the hex tool panel in every mode, for review - the whole panel as
    /// one tall picture and the part a 1440 × 900 window shows, plus a layout audit of the real
    /// controls: what sticks out of the panel, what overlaps its neighbour, which texts are cut.
    /// Writes `&lt;output&gt;.hex-panel-&lt;mode&gt;.png`, `…-view.png` and `&lt;output&gt;.hex-panel-audit.json/.md`.
    /// </summary>
    private async Task HexPanelShots()
    {
        var document = HexMapFactory.Create("Панель гексов · снимки", DateTimeOffset.UtcNow, MapCategories.Hex.Id,
            HexOrientation.Flat, 48, 24, 16, "#FFF2E9DC");
        _editor = new EditorViewModel(document, null, true, _store, _recent, _picker, new LocalPrompts(), new LocalLog(),
            assetCatalog: _catalog, localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { });
        _window.Content = _view;
        await Settle();
        _view.Canvas.FitToCanvas(); await Settle();

        // Some content, so the lists (regions, paths, keys) are not empty.
        HexLayer Layer() => _editor.Session.Document.Layers.OfType<HexLayer>().Single(l => l.Level == 0);
        (double X, double Y) Screen(int column, int row)
        {
            var layout = Layer().Board.Layout;
            var (x, y) = layout.Center(layout.FromOffset(new HexOffset(column, row)));
            var point = _editor.Session.Viewport.WorldToScreen(x, y);
            return (point.X, point.Y);
        }

        PressHex(_view, "HexModeFill"); PressHex(_view, "HexTile_grass"); await Settle();
        _view.Canvas.ClickForSmoke(Screen(10, 8).X, Screen(10, 8).Y); await Settle();
        PressHex(_view, "HexModeRegion"); PressHex(_view, "HexAddRegionButton"); PressHex(_view, "HexAddRegionButton"); await Settle();
        PressHex(_view, "HexModePath"); await Settle();
        _view.Canvas.PressMoveReleaseForSmoke(Screen(2, 2).X, Screen(2, 2).Y, Screen(14, 9).X, Screen(14, 9).Y); await Settle();
        PressHex(_view, "HexModeKey"); await Settle();
        _view.Canvas.ClickForSmoke(Screen(6, 6).X, Screen(6, 6).Y); await Settle();

        var panel = _view.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "HexToolPanel");
        var host = panel.FindAncestorOfType<ScrollViewer>() ?? throw new InvalidOperationException("The hex panel has no scroll viewer.");
        var plate = (Control?)host.GetVisualAncestors().OfType<Border>().FirstOrDefault(c => c.Name == "ContextPanel") ?? host;
        var audit = new List<object>();
        var noTip = new SortedSet<string>(StringComparer.Ordinal);
        var markdown = new StringBuilder("# Аудит раскладки панели гексов\n\n");
        markdown.AppendLine($"Окно {_window.Width}×{_window.Height}, плашка {plate.Bounds.Width:0}×{plate.Bounds.Height:0}, содержимое панели {panel.Bounds.Width:0} px в ширину.\n");

        foreach (var mode in new[] { "Place", "Fill", "Erase", "Pick", "Region", "Path", "Fog", "Key", "Measure", "Select", "World" })
        {
            PressHex(_view, mode == "World" ? "HexTabWorld" : "HexMode" + mode); await Settle();
            if (mode == "World") foreach (var folded in panel.GetVisualDescendants().OfType<Expander>()) folded.IsExpanded = true;
            if (mode == "Key") { _view.Canvas.ClickForSmoke(Screen(6, 6).X, Screen(6, 6).Y); await Settle(); }
            host.Offset = default; await Settle();
            panel.UpdateLayout();
            SavePanel(panel, "hex-panel-" + mode.ToLowerInvariant());
            Capture(plate, "hex-panel-" + mode.ToLowerInvariant() + "-view");

            var issues = AuditPanel(panel);
            foreach (var tip in PanelAudit.MissingTips(_view)) noTip.Add(tip);
            audit.Add(new { mode, height = Math.Round(panel.Bounds.Height), viewport = Math.Round(host.Viewport.Height), issues });
            markdown.AppendLine($"## {mode}: высота {panel.Bounds.Height:0} px при видимых {host.Viewport.Height:0} px ({panel.Bounds.Height / Math.Max(1, host.Viewport.Height):0.0} экрана)\n");
            foreach (var issue in issues) markdown.AppendLine("- " + issue);
            if (issues.Count == 0) markdown.AppendLine("- нарушений раскладки не найдено");
            markdown.AppendLine();
        }

        File.WriteAllText(Path.ChangeExtension(Options.OutputPath, "hex-panel-audit.json"),
            JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.WriteAllText(Path.ChangeExtension(Options.OutputPath, "hex-panel-audit.md"), markdown.ToString());
        Check("the hex panel was shot in every mode", audit.Count == 11);
        // DEC-070 p. 6: every visible control in every hex mode has its line.
        File.WriteAllLines(Path.ChangeExtension(Options.OutputPath, "hex-no-tooltip.txt"), noTip);
        foreach (var name in noTip) Console.WriteLine("no tooltip: " + name);
        Check("every visible hex control has a tooltip", noTip.Count == 0);
    }

    private void SavePanel(Control panel, string name) => PanelAudit.Save(panel, Path.ChangeExtension(Options.OutputPath, name + ".png"));

    private static List<string> AuditPanel(Control panel) => PanelAudit.Audit(panel);
}
