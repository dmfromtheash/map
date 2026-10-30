using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.Performance;

public sealed partial class NativeEditorScenarioApplication
{
    /// <summary>
    /// `native-standard-panel-shots`: the tool panel of the standard constructor for every rail tool
    /// (land, land paint, river and road with a drawn one selected, line, shape, text, notes, brush,
    /// mask, select with the river picked), as it opens and with every category unfolded, plus the
    /// layout audit of the unfolded panel. Writes `&lt;output&gt;.std-panel-&lt;tool&gt;.png`,
    /// `…-open.png` (unfolded), `…-view.png` (what the window shows) and `std-panel-audit.md/.json`.
    /// </summary>
    private async Task StandardPanelShotsOnly()
    {
        var audit = new List<object>();
        var markdown = new StringBuilder("# Аудит раскладки панелей стандартного конструктора\n\n");
        var noTip = new SortedSet<string>(StringComparer.Ordinal);

        async Task Shoot(string tool)
        {
            await Settle(); await Settle();
            var plate = Find<Border>("ContextPanel");
            var host = plate.GetVisualDescendants().OfType<ScrollViewer>().First();
            host.Offset = default; await Settle();
            var panel = (Control)host.Content!;
            panel.UpdateLayout();
            var opened = panel.Bounds.Height;
            PanelAudit.Save(panel, Path.ChangeExtension(Options.OutputPath, $"std-panel-{tool}.png"));
            PanelAudit.Save(plate, Path.ChangeExtension(Options.OutputPath, $"std-panel-{tool}-view.png"));
            var folded = panel.GetVisualDescendants().OfType<Expander>().Where(e => !e.IsExpanded).ToList();
            foreach (var expander in folded) expander.IsExpanded = true;
            await Settle(); panel.UpdateLayout();
            PanelAudit.Save(panel, Path.ChangeExtension(Options.OutputPath, $"std-panel-{tool}-open.png"));
            var issues = PanelAudit.Audit(panel);
            foreach (var tip in PanelAudit.MissingTips(_view)) noTip.Add(tip);
            audit.Add(new { tool, height = Math.Round(opened), unfolded = Math.Round(panel.Bounds.Height), viewport = Math.Round(host.Viewport.Height), folded = folded.Count, issues });
            markdown.AppendLine($"## {tool}: {opened:0} px как открывается, {panel.Bounds.Height:0} px раскрыто ({folded.Count} свёрнутых), видно {host.Viewport.Height:0} px\n");
            foreach (var issue in issues) markdown.AppendLine("- " + issue);
            if (issues.Count == 0) markdown.AppendLine("- нарушений раскладки не найдено");
            markdown.AppendLine();
            foreach (var expander in folded) expander.IsExpanded = false;
            await Settle();

            // Every other tab of the panel, unfolded, with its own audit.
            var strip = panel.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.UniformGrid>().FirstOrDefault(g => g.Name?.EndsWith("TabsStrip", StringComparison.Ordinal) == true && g.IsEffectivelyVisible);
            if (strip is null) return;
            var tabs = strip.Children.OfType<Avalonia.Controls.Primitives.ToggleButton>().ToList();
            foreach (var tab in tabs.Skip(1))
            {
                tab.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle();
                var inner = panel.GetVisualDescendants().OfType<Expander>().Where(e => !e.IsExpanded && e.IsEffectivelyVisible).ToList();
                foreach (var expander in inner) expander.IsExpanded = true;
                await Settle(); panel.UpdateLayout();
                var key = tab.Name![(strip.Name!.Length - "Strip".Length)..];
                PanelAudit.Save(panel, Path.ChangeExtension(Options.OutputPath, $"std-panel-{tool}-tab-{key}.png"));
                var tabIssues = PanelAudit.Audit(panel);
                foreach (var tip in PanelAudit.MissingTips(_view)) noTip.Add(tip);
                audit.Add(new { tool = tool + "/" + key, height = Math.Round(panel.Bounds.Height), unfolded = Math.Round(panel.Bounds.Height), viewport = Math.Round(host.Viewport.Height), folded = inner.Count, issues = tabIssues });
                markdown.AppendLine($"### {tool} · вкладка {key}: {panel.Bounds.Height:0} px раскрыто\n");
                foreach (var issue in tabIssues) markdown.AppendLine("- " + issue);
                if (tabIssues.Count == 0) markdown.AppendLine("- нарушений раскладки не найдено");
                markdown.AppendLine();
                foreach (var expander in inner) expander.IsExpanded = false;
            }

            tabs[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle();
        }

        var land = _editor.Session.Document.Layers.OfType<LandLayer>().First();
        _editor.SelectLayer(land.Id);
        await Click("LandToolButton"); await Shoot("land");
        _editor.SetLandPaintMode(true); await Shoot("land-paint");
        _editor.SetLandPaintMode(false); await Settle();

        await Click("RiverToolButton"); await Shoot("river-new");
        await Click("StartRiverButton");
        Point(120, 220); Point(380, 320); Point(640, 280); Point(900, 600);
        await Click("FinishRiverButton");
        Point(380, 320); await Settle();
        await Shoot("river");

        await Click("RoadToolButton"); await Shoot("road-new");
        await Click("StartRoadButton");
        Point(60, 430); Point(300, 380); Point(520, 470); Point(960, 380);
        await Click("FinishRoadButton");
        await Shoot("road");

        await Click("SelectToolButton"); Point(380, 320); await Shoot("select-river");
        _editor.DeselectObject(); await Shoot("select-empty");
        foreach (var (button, tool) in new[] { ("LineToolButton", "line"), ("ShapeToolButton", "shape"), ("TextToolButton", "text"), ("NotesToolButton", "notes"), ("BrushToolButton", "brush") })
        {
            await Click(button); await Shoot(tool);
        }

        var objects = _editor.Session.Document.Layers.OfType<ObjectLayer>().First();
        _editor.SelectLayer(objects.Id); await Settle();
        await Click("MaskToolButton");
        if (_editor.ActiveLayer?.Mask is null) { _editor.ToggleActiveMask(); await Settle(); }
        await Shoot("mask");

        File.WriteAllText(Path.ChangeExtension(Options.OutputPath, "std-panel-audit.json"),
            JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.WriteAllText(Path.ChangeExtension(Options.OutputPath, "std-panel-audit.md"), markdown.ToString());
        // DEC-070: every visible control of the whole window (bars, rail, panels) in all the states above has a tooltip.
        File.WriteAllLines(Path.ChangeExtension(Options.OutputPath, "std-panel-no-tooltip.txt"), noTip);
        _report.Checks["every-visible-control-has-tooltip"] = noTip.Count == 0;
        Console.WriteLine($"controls without tooltip: {noTip.Count}");
        _report.Checks["standard-panel-shots-saved"] = audit.Count >= 14;

        // DEC-080: the «Внутри» card opens on every section tab, the column tabs and the main rail tools, and is drawn.
        var cards = 0; var badCards = new List<string>();
        foreach (var name in new[] { "WorkspaceDrawTab", "WorkspaceObjectsTab", "WorldSectionButton", "WorkspaceAppearanceTab",
                     "RightTabProperties", "RightTabContent", "RightTabLayers",
                     "SelectToolButton", "LandToolButton", "RiverToolButton", "RoadToolButton", "AssetsLibraryButton" })
        {
            var target = Find<Control>(name);
            if (ToolTip.GetTip(target) is not ToolTip tip || !tip.Classes.Contains("inside-card")) { badCards.Add(name + ": нет карточки"); continue; }
            ToolTip.SetIsOpen(target, true);
            for (var i = 0; i < 20 && tip.Content is not Control { Bounds.Width: > 0 }; i++) await Settle();
            // Drawn, and no taller than the owner's laptop screen (1080 px at 125 % = 864 DIP, minus the bars).
            if (tip.Content is Border { Name: "InsideCard" } card && card.Bounds is { Width: > 300, Height: > 120 and <= 820 })
            {
                PanelAudit.Save(card, Path.ChangeExtension(Options.OutputPath, $"inside-card-{name}.png"));
                cards++;
            }
            else badCards.Add(name + ": карточка не построена");
            ToolTip.SetIsOpen(target, false); await Settle();
        }
        foreach (var bad in badCards) Console.WriteLine("inside card: " + bad);
        Console.WriteLine($"inside cards drawn: {cards}/12");
        _report.Checks["inside-card-on-tabs-and-tools"] = badCards.Count == 0;
    }
}
