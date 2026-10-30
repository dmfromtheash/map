using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Map.App.Controls;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private Control BuildPosterAndLayersExport()
    {
        var result = new TextBlock { Name = "ExportLayersResultText", TextWrapping = TextWrapping.Wrap };
        var poster = Ui.AsyncButton("ExportPosterButton", "Печать по листам…", ShowHexPrintAsync, "compact");
        poster.IsVisible = !_editor.IsHexWorkshop;
        return new StackPanel { Spacing = 6, Children =
        {
            new WrapPanel { ItemSpacing = 6, LineSpacing = 6, Children =
            {
                poster, Ui.AsyncButton("ExportLayersButton", "Слои отдельными PNG…", async () =>
                {
                    await _editor.ExportLayersAsync();
                    result.Text = _editor.StatusDetail;
                }, "compact"),
            } },
            new TextBlock { Text = "PNG: по файлу на видимый слой, в порядке слоёв. Размер и область — как ниже; фон отдельным файлом.", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption"),
            result,
        } };
    }

    private Control BuildExportRecipient()
    {
        var recipient = new ComboBox
        {
            Name = "ExportRecipientComboBox", ItemsSource = new[] { "Ведущий", "Игрок" },
            MinWidth = 180, SelectedIndex = _editor.ExportHexPlayers ? 1 : 0,
        };
        var syncing = false;
        recipient.SelectionChanged += (_, _) =>
        {
            if (syncing) return;
            _editor.ExportHexPlayers = recipient.SelectedIndex == 1;
            SyncExport();
        };
        HexSync(() =>
        {
            syncing = true;
            recipient.SelectedIndex = _editor.ExportHexPlayers ? 1 : 0;
            syncing = false;
        }, nameof(EditorViewModel.ExportHexPlayers));
        return new StackPanel
        {
            IsVisible = _editor.IsHexWorkshop, Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Получатель пакета" }.Classed("role-caption"), recipient,
                BuildHexExportLevelSelector(), BuildExportPackagePreview(),
                new WrapPanel
                {
                    ItemSpacing = 6, LineSpacing = 6,
                    Children =
                    {
                        Ui.AsyncButton("ExportPackageJsonButton", "JSON гексов…", async () => { await _editor.ExportHexJsonAsync(); }, "compact"),
                        Ui.AsyncButton("ExportPackageMarkdownButton", "Ключ Markdown…", async () => { await _editor.ExportHexKeyAsync(_editor.ExportHexPlayers); }, "compact"),
                        Ui.AsyncButton("ExportObsidianButton", "Папка Obsidian…", async () => { await _editor.ExportObsidianAsync(); }, "compact"),
                        Ui.AsyncButton("ExportPackagePrintButton", "PDF по листам…", async () => { await ShowHexPrintAsync(); }, "compact"),
                    },
                },
            },
        };
    }

    private Control BuildExportPackagePreview(string prefix = "Export")
    {
        TextBlock Line(string suffix) => new TextBlock { Name = prefix + "PackagePreview" + suffix + "Text", TextWrapping = TextWrapping.Wrap }
            .Classed("role-caption");
        var maps = Line("Maps");
        var notes = Line("Notes");
        var layers = Line("Layers");
        var labels = Line("Labels");
        var fog = Line("Fog");
        var panel = new StackPanel
        {
            Name = prefix + "PackagePreviewPanel", Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Что попадёт в пакет" }.Classed("role-panel"),
                maps, notes, layers, labels, fog,
                new TextBlock
                {
                    Text = "Только выбранный уровень. Заметки ведущего и ссылки на подкарты исключены. Подпись видна, если есть текст «Игрокам».",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
            },
        };
        void Sync()
        {
            panel.IsVisible = _editor.ExportHexPlayers;
            if (!panel.IsVisible) return;
            var p = _editor.PlayerPackagePreview;
            maps.Text = $"Карта: {p.MapTitle}\nУровни: {string.Join(", ", p.Levels.Select(EditorViewModel.HexLevelName))}";
            notes.Text = $"Заметки: всего {p.NotesTotal} · в пакете {p.NotesIncluded} · скрыто {p.NotesTotal - p.NotesIncluded}";
            layers.Text = $"Видимых слоёв в пакете: {p.Layers}";
            labels.Text = $"Подписи ключей: всего {p.LabelsTotal} · в пакете {p.LabelsIncluded} · скрыто {p.LabelsTotal - p.LabelsIncluded}";
            var cards = _editor.CardsHiddenByFog;
            fog.Text = $"Скрыто туманом: {p.FogHexes} гексов, {p.FogNotes} заметок, {cards} {EditorViewModel.CardsWord(cards)}";
        }
        HexSync(Sync, [.. DocumentChanged, nameof(EditorViewModel.ExportHexPlayers), nameof(EditorViewModel.ExportHexLevel),
            nameof(EditorViewModel.PlayerPackagePreview)]);
        Sync();
        return panel;
    }
}
