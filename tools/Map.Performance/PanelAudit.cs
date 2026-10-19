using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace Map.Performance;

/// <summary>
/// Review shots of a tool panel and its layout audit, shared by `native-hex-panel-shots` and
/// `native-standard-panel-shots`: the whole panel as one tall picture, and what the user would see
/// as broken - a control past the panel's edge, neighbours overlapping, a one-line text cut.
/// </summary>
internal static class PanelAudit
{
    /// <summary>The whole panel content (taller than the window) as one picture on the panel's own background.</summary>
    public static void Save(Control panel, string path)
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

        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }

    /// <summary>
    /// What the user would see as broken in the panel: a control reaching past the panel's edge,
    /// two neighbours in one row or column overlapping, a one-line text or a button label cut.
    /// </summary>
    public static List<string> Audit(Control panel)
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
