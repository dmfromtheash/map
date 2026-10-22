using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Map.App.Services;
using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private Action? _refreshHexSets;
    private string _hexSetMessage = "";

    public void ImportHexTileSetForSmoke(string path)
    {
        if (TestMode.Enabled) ImportHexTileSet(path);
    }

    private void ImportHexTileSet(string path)
    {
        try
        {
            var set = HexUserTileSets.Import(path);
            Canvas.RefreshHexTileLibrary();
            _hexSetMessage = $"Импортирован «{set.Manifest.Name}»: {set.Manifest.Tiles.Count} плитки.";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { _hexSetMessage = "Не удалось импортировать комплект: " + e.Message; }
        _refreshHexSets?.Invoke();
    }

    private async Task ChooseHexSetFolder(bool template)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || !top.StorageProvider.CanPickFolder) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = template ? "Куда выдать шаблон гекса" : "Папка комплекта с set.json", AllowMultiple = false });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
        if (!template) { ImportHexTileSet(path); return; }
        try
        {
            var destination = Path.Combine(path, "Шаблон гекса " + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4]);
            HexUserTileSets.ExportTemplate(destination);
            _hexSetMessage = "Шаблон, set.json и описание размеров сохранены: " + destination;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { _hexSetMessage = "Не удалось выдать шаблон: " + e.Message; }
        _refreshHexSets?.Invoke();
    }

    private Control BuildHexUserSets()
    {
        var list = new StackPanel { Name = "HexUserSetsList", Spacing = 8 };
        var message = new TextBlock { Name = "HexUserSetsMessage", TextWrapping = TextWrapping.Wrap };
        void Refresh()
        {
            list.Children.Clear();
            message.Text = _hexSetMessage;
            var missing = _editor.Session.Document.Layers.OfType<HexLayer>().SelectMany(l => l.Board.Cells)
                .SelectMany(p => new[] { p.Value.Terrain }.Concat(p.Value.PointsOfInterest.Select(t => t.Type)))
                .Where(id => HexUserTileSets.IsUserId(id) && HexStarterTiles.Find(id) is null).Distinct().ToArray();
            if (missing.Length > 0) list.Children.Add(new TextBlock { Name = "HexMissingSetsText", Text = "Не найден комплект: " + string.Join(", ", missing)
                + ". На карте показаны заглушки. Импортируйте прежний комплект с тем же Id; ссылки сохранены.", TextWrapping = TextWrapping.Wrap });
            if (HexUserTileSets.LoadWarning.Length > 0) list.Children.Add(new TextBlock { Text = HexUserTileSets.LoadWarning, TextWrapping = TextWrapping.Wrap });
            if (HexUserTileSets.All.Count == 0) list.Children.Add(new TextBlock { Text = "Своих комплектов пока нет. Выдайте шаблон, нарисуйте PNG и импортируйте папку с set.json.", TextWrapping = TextWrapping.Wrap });
            foreach (var set in HexUserTileSets.All)
            {
                var body = new StackPanel { Spacing = 6 };
                foreach (var category in set.Manifest.Tiles.GroupBy(t => t.Category))
                {
                    var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4 };
                    foreach (var tile in category)
                    {
                        var definition = HexStarterTiles.Find(set.TileId(tile))!;
                        if (_hexTileButtons.ContainsKey(definition.Id)) _hexTileButtons.Remove(definition.Id);
                        grid.Children.Add(HexTileButton(definition));
                    }
                    body.Children.Add(Ui.Subgroup(category.Key, grid));
                }
                body.Children.Add(Ui.TextButton("HexCheckSet_" + set.Manifest.Id, "Проверка комплекта", () => _ = ShowHexTileSetCheck(set), "compact"));
                list.Children.Add(Ui.Subgroup(set.Manifest.Name, body));
            }
        }
        _refreshHexSets = Refresh;
        HexSync(Refresh, nameof(Map.Presentation.ViewModels.EditorViewModel.CanUndo));
        Refresh();
        return new StackPanel { Name = "HexUserSetsPanel", Spacing = 8, Children =
        {
            Ui.TextButton("HexImportSetButton", "Импортировать комплект…", () => _ = ChooseHexSetFolder(false), "compact"),
            Ui.TextButton("HexTemplateButton", "Выдать шаблон гекса…", () => _ = ChooseHexSetFolder(true), "compact"),
            message, list,
        } };
    }

    private async Task ShowHexTileSetCheck(HexUserTileSet set)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var window = new Window { Name = "HexTileSetCheckWindow", Title = "Проверка комплекта", Width = 780, Height = 800,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var kinds = new ComboBox { Name = "HexSetCheckKind", ItemsSource = new[] { "Соседства: все пары, грани и повороты", "Повороты: каждая плитка, шесть положений", "Перекрытия: все пары и шесть направлений" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var orientation = new ComboBox { Name = "HexSetCheckOrientation", ItemsSource = new[] { "Грань сверху", "Вершина сверху" }, SelectedIndex = 0 };
        var number = new NumericUpDown { Name = "HexSetCheckNumber", Minimum = 1, Maximum = HexTileSetInspection.Count(set, 0), Value = 1, Increment = 1, FormatString = "0", Width = 130 };
        var summary = new TextBlock { Name = "HexSetCheckSummary", TextWrapping = TextWrapping.Wrap };
        var problems = new TextBlock { Name = "HexSetCheckProblems", TextWrapping = TextWrapping.Wrap };
        var picture = new Image { Name = "HexSetCheckPreview", Height = 300, Stretch = Stretch.Uniform };
        Bitmap? current = null;
        var updating = false;
        void Show()
        {
            if (updating) return;
            updating = true;
            var kind = (HexTileSetCheckKind)Math.Max(0, kinds.SelectedIndex);
            var count = HexTileSetInspection.Count(set, kind);
            number.Maximum = count; number.Value = Math.Clamp(number.Value ?? 1, 1, count);
            var check = HexTileSetInspection.At(set, kind, (int)number.Value.Value - 1, (HexOrientation)Math.Max(0, orientation.SelectedIndex));
            summary.Text = $"{set.Manifest.Name}: {set.Manifest.Tiles.Count} плитки. Случай {number.Value:0} из {count}.\n{check.Description}";
            problems.Text = check.Problems.Count == 0 ? "Метки краёв совместимы; запрета поворота и выхода за ряд нет. Художественный шов проверьте по картинке."
                : string.Join("\n", check.Problems);
            using var preview = HexTileSetInspection.Preview(set, check, (HexOrientation)Math.Max(0, orientation.SelectedIndex));
            using var png = preview.Encode(SKEncodedImageFormat.Png, 100); using var stream = new MemoryStream(png.ToArray());
            var next = new Bitmap(stream); picture.Source = next; current?.Dispose(); current = next;
            updating = false;
        }
        void NextProblem()
        {
            var kind = (HexTileSetCheckKind)kinds.SelectedIndex;
            var count = HexTileSetInspection.Count(set, kind); var start = (int)(number.Value ?? 1) - 1;
            for (var i = 1; i <= count; i++)
            {
                var next = (start + i) % count;
                if (HexTileSetInspection.At(set, kind, next, (HexOrientation)orientation.SelectedIndex).Problems.Count == 0) continue;
                number.Value = next + 1; return;
            }
            problems.Text = "Во всех случаях этой проверки заявленные ограничения соблюдены. Художественные швы оцениваются вручную.";
        }
        kinds.SelectionChanged += (_, _) => { number.Value = 1; Show(); };
        orientation.SelectionChanged += (_, _) => Show(); number.ValueChanged += (_, _) => Show();
        window.Content = new StackPanel { Margin = new Thickness(24), Spacing = 12, Children =
        {
            kinds, orientation,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
            {
                Ui.TextButton("HexSetCheckPrevious", "Назад", () => number.Value = Math.Max(1, (number.Value ?? 1) - 1), "compact"), number,
                Ui.TextButton("HexSetCheckNext", "Далее", () => number.Value = Math.Min(number.Maximum, (number.Value ?? 1) + 1), "compact"),
                Ui.TextButton("HexSetCheckNextProblem", "Следующая проблема", NextProblem, "compact"),
            } }, summary, picture, problems,
            new TextBlock { Text = "Проверяются метки краёв и ограничения комплекта. Стороны 1…6 идут против часовой стрелки от востока (вершина сверху) или юго-востока (грань сверху). Художественный шов проверьте на картинке.", TextWrapping = TextWrapping.Wrap },
            Ui.TextButton("HexSetCheckCloseButton", "Закрыть", window.Close, "compact"),
        } };
        Show();
        try { await window.ShowDialog(owner); }
        finally { picture.Source = null; current?.Dispose(); }
    }
}
