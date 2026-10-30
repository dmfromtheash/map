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
        var presets = Enum.GetValues<Map.Core.Documents.MapStylePreset>();
        var style = new ComboBox { Name = "ArtLanguageStyleBox", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, SelectedIndex = 0,
            ItemsSource = new[] { "Стиль не менять" }.Concat(presets.Select(p => "Стиль: " + Map.Presentation.ViewModels.EditorViewModel.PresetTitle(p))).ToArray() };
        ToolTip.SetTip(style, "Стиль, который придёт вместе с языком: одна отмена вернёт и рисунки, и стиль.");
        var apply = Ui.TextButton("ArtLanguageApplyButton", "Перерисовать карту в этом языке", () =>
        {
            if (Chosen() is { } id) _editor.ChangeArtLanguage(id, style.SelectedIndex > 0 ? presets[style.SelectedIndex - 1] : null);
            Preview();
        }, "accent", "compact");
        ToolTip.SetTip(apply, "Места, размеры и повороты остаются; меняются только рисунки. Ctrl+Z вернёт прежний язык.");

        var master = Ui.AsyncButton("AtlasBookMasterButton", "Книга-атлас для ведущего…", async () => await _editor.PickAndExportAtlasBookAsync(players: false), "compact");
        var players = Ui.AsyncButton("AtlasBookPlayersButton", "Книга-атлас для игроков…", async () => await _editor.PickAndExportAtlasBookAsync(players: true), "compact");
        // DEC-077: the cards whose places the players have not seen yet stay out of their book.
        var fogHidden = new TextBlock { Name = "AtlasBookFogHiddenText", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        _bookSyncs.Add(() =>
        {
            var hidden = _editor.CardsHiddenByFog;
            fogHidden.IsVisible = _editor.Fog.Enabled;
            fogHidden.Text = $"Скрыто туманом: {hidden} {Map.Presentation.ViewModels.EditorViewModel.CardsWord(hidden)} «Видно игрокам» этой карты — их места ещё не открыты. Откроете туман — попадут в книгу игроков.";
        });

        var mods = new StackPanel { Name = "ModList", Spacing = 6 };
        void ListMods()
        {
            mods.Children.Clear();
            var found = _editor.ScanMods();
            if (found.Count == 0)
                mods.Children.Add(new TextBlock { Text = "Модов нет. Положите папку мода с mod.json в папку модов.", TextWrapping = TextWrapping.Wrap }.Classed("faint"));
            foreach (var mod in found)
            {
                var captured = mod;
                var title = new TextBlock { Text = $"{mod.Name} {mod.Version}".Trim(), FontWeight = FontWeight.SemiBold };
                var status = new TextBlock { TextWrapping = TextWrapping.Wrap,
                    Text = mod.IsUsable ? $"Ассетов: {mod.Assets.Count} · таблиц встреч: {mod.Encounters.Count}" : "Ошибки: " + string.Join("; ", mod.Problems.Take(4)) }.Classed("faint", "role-caption");
                var assets = Ui.TextButton("ModAssets_" + mod.Id, "Ассеты в каталог", () => { _editor.ImportModAssets(captured); SyncBook(); }, "compact");
                var tables = Ui.TextButton("ModTables_" + mod.Id, "Таблицы встреч на карту", () => _editor.AddModEncounters(captured), "compact");
                assets.IsEnabled = mod.IsUsable && mod.Assets.Count > 0;
                tables.IsEnabled = mod.IsUsable && mod.Encounters.Count > 0;
                mods.Children.Add(new Border { Padding = new Thickness(8), Child = new StackPanel { Spacing = 4, Children = { title, status, new Ui.ChoiceGrid { MaxColumns = 2, Children = { assets, tables } } } } }.Classed("group"));
            }
        }
        var refresh = Ui.TextButton("ModsRefreshButton", "Обновить список", ListMods, "compact");
        var folder = Ui.TextButton("ModsFolderButton", "Открыть папку модов", () =>
        {
            System.IO.Directory.CreateDirectory(_editor.ModsFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = _editor.ModsFolder, UseShellExecute = true });
        }, "compact");
        ListMods();

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
                        languages, style, preview, apply,
                    } }),
                    Ui.Subgroup("Моды данными", new StackPanel { Spacing = 6, Children =
                    {
                        Ui.Hint("Мод — папка с mod.json: ассеты с ролями (в каталог отдельной коллекцией) и таблицы встреч. Только данные, без программ; ошибки показываются до подключения."),
                        new Ui.ChoiceGrid { MaxColumns = 2, Children = { refresh, folder } },
                        mods,
                    } }),
                    Ui.Subgroup("Книга-атлас", new StackPanel { Spacing = 6, Children =
                    {
                        Ui.Hint("Все карты атласа в одном PDF: содержание, лист на карту с отмеченными подкартами («см. с. N»), ключи заметок и карточек, указатель названий. После правок соберите заново."),
                        new Ui.ChoiceGrid { MaxColumns = 2, Children = { master, players } },
                        fogHidden,
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
