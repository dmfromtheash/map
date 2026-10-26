using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Map.App.Views;

/// <summary>
/// R40: «Книга и язык» - change the art language of the map (assets of the same roles from another
/// collection, one undo step, with a count beforehand) and gather the atlas book as a PDF.
/// </summary>
public sealed partial class EditorView
{
    private Window? _bookWindow;
    private Control? _bookContent;
    private readonly List<Action> _bookSyncs = [];

    private void ShowBook()
    {
        _bookWindow = ShowAuxiliaryWindow(_bookWindow, "Книга и художественный язык", BuildBookContent(), 640, 560, "BookWindow");
        SyncBook();
    }

    private Control BuildBookContent()
    {
        if (_bookContent is not null) return _bookContent;
        var languages = new ComboBox { Name = "ArtLanguageBox", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, PlaceholderText = "Коллекция с ролями" };
        var preview = new TextBlock { Name = "ArtLanguagePreviewText", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        string? Chosen() => languages.SelectedIndex >= 0 && languages.SelectedIndex < _editor.ArtLanguages.Count ? _editor.ArtLanguages[languages.SelectedIndex].Id : null;
        void Preview()
        {
            if (Chosen() is not { } id) { preview.Text = "Выберите язык — коллекцию каталога, где у наборов есть роли (дерево, ель, дом…)."; return; }
            var plan = _editor.PreviewArtLanguage(id);
            preview.Text = $"Будет перерисовано: {plan.Replaced}. Роли языка: {string.Join(", ", plan.Roles)}."
                + (plan.InConstructions > 0 ? $" В путях и областях: {plan.InConstructions} — перестройте их сами." : "")
                + (plan.WithoutCandidate > 0 ? $" Нет такой роли в языке: {plan.WithoutCandidate}." : "")
                + (plan.WithoutRole > 0 ? $" Без роли: {plan.WithoutRole}." : "");
        }
        languages.SelectionChanged += (_, _) => Preview();
        var apply = Ui.TextButton("ArtLanguageApplyButton", "Перерисовать карту в этом языке", () =>
        {
            if (Chosen() is { } id) _editor.ChangeArtLanguage(id);
            Preview();
        }, "accent", "compact");
        ToolTip.SetTip(apply, "Места, размеры и повороты остаются; меняются только рисунки. Ctrl+Z вернёт прежний язык.");

        var master = Ui.AsyncButton("AtlasBookMasterButton", "Книга-атлас для ведущего…", async () => await _editor.PickAndExportAtlasBookAsync(players: false), "compact");
        var players = Ui.AsyncButton("AtlasBookPlayersButton", "Книга-атлас для игроков…", async () => await _editor.PickAndExportAtlasBookAsync(players: true), "compact");

        void Sync()
        {
            var names = _editor.ArtLanguages.Select(c => c.Name).ToArray();
            if (!names.SequenceEqual(languages.ItemsSource?.Cast<string>() ?? [])) languages.ItemsSource = names;
            Preview();
        }
        _bookSyncs.Add(Sync);
        _bookContent = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16, 10, 16, 18), Spacing = 10,
                Children =
                {
                    Ui.Subgroup("Художественный язык", new StackPanel { Spacing = 6, Children =
                    {
                        Ui.Hint("Вся карта перерисовывается ассетами тех же ролей из другой коллекции: структура остаётся, меняется рисунок. Стиль отрисовки — во вкладке «Стили» под картой."),
                        languages, preview, apply,
                    } }),
                    Ui.Subgroup("Книга-атлас", new StackPanel { Spacing = 6, Children =
                    {
                        Ui.Hint("Все карты атласа в одном PDF: содержание, лист на карту с отмеченными подкартами («см. с. N»), ключи заметок и карточек, указатель названий. После правок соберите заново."),
                        new Ui.ChoiceGrid { MaxColumns = 2, Children = { master, players } },
                    } }),
                },
            },
        };
        Sync();
        return _bookContent;
    }

    private void SyncBook()
    {
        foreach (var sync in _bookSyncs) sync();
    }
}
