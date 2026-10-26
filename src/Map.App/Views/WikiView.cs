using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace Map.App.Views;

/// <summary>
/// R26: the text of a card read as a page - «#» headings, «-» and «1.» lists,
/// **bold**, *italic* and [[links]] that open the card they name. Only this small, plain part of
/// Markdown: what a game master writes in a note, nothing that needs a browser (angle brackets
/// are not allowed in note text at all).
/// </summary>
public static partial class WikiView
{
    [GeneratedRegex(@"\*\*(?<b>.+?)\*\*|\*(?<i>[^*\s][^*]*?)\*|\[\[(?<l>[^\]]+)\]\]")]
    private static partial Regex InlinePattern();

    [GeneratedRegex(@"^(?<n>\d{1,3})[.)]\s+(?<t>.*)$")]
    private static partial Regex NumberedPattern();

    /// <param name="open">Opens the card a link names; null when nothing should happen.</param>
    public static Control Build(string markdown, Action<string>? open)
    {
        var page = new StackPanel { Name = "WikiPage", Spacing = 4 };
        var lines = (markdown ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { page.Children.Add(new Border { Height = 4 }); continue; }
            if (line.StartsWith('#'))
            {
                var level = Math.Min(3, line.TakeWhileCount('#'));
                var title = Paragraph(line[level..].Trim(), open);
                title.FontWeight = FontWeight.SemiBold;
                title.FontSize = level switch { 1 => 18, 2 => 16, _ => 14 };
                title.Margin = new Thickness(0, 4, 0, 0);
                page.Children.Add(title);
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                page.Children.Add(Item("•", line[2..], open));
            else if (NumberedPattern().Match(line) is { Success: true } numbered)
                page.Children.Add(Item(numbered.Groups["n"].Value + ".", numbered.Groups["t"].Value, open));
            else page.Children.Add(Paragraph(line, open));
        }
        return page;
    }

    private static DockPanel Item(string mark, string text, Action<string>? open)
    {
        var body = Paragraph(text, open);
        var bullet = new TextBlock { Text = mark, MinWidth = 18 };
        DockPanel.SetDock(bullet, Dock.Left);
        return new DockPanel { Margin = new Thickness(6, 0, 0, 0), Children = { bullet, body } };
    }

    private static TextBlock Paragraph(string text, Action<string>? open)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Inlines = [] };
        var at = 0;
        foreach (Match match in InlinePattern().Matches(text))
        {
            if (match.Index > at) block.Inlines!.Add(new Run(text[at..match.Index]));
            if (match.Groups["b"].Success) block.Inlines!.Add(new Run(match.Groups["b"].Value) { FontWeight = FontWeight.Bold });
            else if (match.Groups["i"].Success) block.Inlines!.Add(new Run(match.Groups["i"].Value) { FontStyle = FontStyle.Italic });
            else block.Inlines!.Add(Link(match.Groups["l"].Value.Trim(), open));
            at = match.Index + match.Length;
        }
        if (at < text.Length) block.Inlines!.Add(new Run(text[at..]));
        return block;
    }

    private static InlineUIContainer Link(string name, Action<string>? open)
    {
        var link = new TextBlock
        {
            Text = name, TextDecorations = TextDecorations.Underline, Cursor = new Cursor(StandardCursorType.Hand),
            Foreground = Application.Current?.FindResource("MapAccentBrush") as IBrush,
        };
        Avalonia.Automation.AutomationProperties.SetName(link, "Ссылка: " + name);
        link.PointerPressed += (_, e) => { if (open is not null) { open(name); e.Handled = true; } };
        return new InlineUIContainer(link) { BaselineAlignment = BaselineAlignment.TextBottom };
    }

    private static int TakeWhileCount(this string text, char c)
    {
        var n = 0;
        while (n < text.Length && text[n] == c) n++;
        return n;
    }
}
