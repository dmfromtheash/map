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
    }

    /// <summary>The whole panel content (taller than the window) as one picture on the panel's own background.</summary>
    private void SavePanel(Control panel, string name)
    {
        var width = Math.Max(1, (int)Math.Ceiling(panel.Bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(panel.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width + 24, height + 24));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(new SolidColorBrush(Color.Parse("#FF2A211A")), new Rect(0, 0, width + 24, height + 24));
            using (context.PushTransform(Matrix.CreateTranslation(12, 12)))
            {
                // Rendered through a visual brush of the panel at its own size.
                var brush = new VisualBrush(panel) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
                context.FillRectangle(brush, new Rect(0, 0, width, height));
            }
        }

        bitmap.Save(Path.ChangeExtension(Options.OutputPath, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    /// <summary>
    /// What the user would see as broken in the panel: a control reaching past the panel's edge,
    /// two neighbours in one row or column overlapping, a one-line text or a button label cut.
    /// </summary>
    private static List<string> AuditPanel(Control panel)
    {
        var issues = new List<string>();
        var width = panel.Bounds.Width;
        string Describe(Control c)
        {
            var text = c switch
            {
                TextBlock t => t.Text,
                ContentControl { Content: string s } => s,
                ContentControl { Content: TextBlock t } => t.Text,
                TextBox t => t.Watermark ?? t.Text,
                _ => null,
            };
            var label = string.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.Name;
            return text is null ? label : $"{label} «{(text.Length > 40 ? text[..40] + "…" : text)}»";
        }

        Rect? InPanel(Control c)
        {
            var origin = c.TranslatePoint(default, panel);
            return origin is { } p ? new Rect(p, c.Bounds.Size) : null;
        }

        foreach (var control in panel.GetVisualDescendants().OfType<Control>())
        {
            if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) continue;
            if (control.FindAncestorOfType<ScrollViewer>() is { } inner && inner != panel.FindAncestorOfType<ScrollViewer>()) continue;
            if (InPanel(control) is not { } rect) continue;
            if (control is TextBlock or Button or TextBox or Slider or ComboBox or Image)
            {
                if (rect.Right > width + 1 || rect.X < -1)
                {
                    issues.Add($"выходит за край плашки на {Math.Max(rect.Right - width, -rect.X):0} px: {Describe(control)}");
                }
            }

            if (control is TextBlock block && block.TextWrapping == TextWrapping.NoWrap && !string.IsNullOrEmpty(block.Text))
            {
                var probe = new TextBlock { Text = block.Text, FontSize = block.FontSize, FontFamily = block.FontFamily, FontWeight = block.FontWeight };
                probe.Measure(Size.Infinity);
                if (probe.DesiredSize.Width > block.Bounds.Width + 1.5)
                {
                    issues.Add($"текст обрезан ({probe.DesiredSize.Width:0} px в {block.Bounds.Width:0} px): {Describe(block)}");
                }
            }
        }

        // Overlapping neighbours: children of one stack, wrap or dock panel.
        foreach (var container in panel.GetVisualDescendants().OfType<Panel>().Where(p => p is StackPanel or WrapPanel or DockPanel))
        {
            if (!container.IsEffectivelyVisible) continue;
            var children = container.Children.Where(c => c.IsVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0).ToList();
            for (var i = 0; i < children.Count; i++)
            {
                for (var j = i + 1; j < children.Count; j++)
                {
                    var overlap = children[i].Bounds.Intersect(children[j].Bounds);
                    if (overlap.Width > 1.5 && overlap.Height > 1.5)
                    {
                        issues.Add($"налезают друг на друга ({overlap.Width:0}×{overlap.Height:0} px): {Describe(children[i])} и {Describe(children[j])}");
                    }
                }
            }
        }

        return issues.Distinct().ToList();
    }
}
