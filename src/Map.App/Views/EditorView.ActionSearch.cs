using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// DEC-070 step 4: «Найти действие» (Ctrl+K). One list of what the editor can do - the name, the way to it
/// («Объекты → Область → Красота») and, when it needs something first, what. Enter opens the place and lights the
/// control there for a moment; it never edits the map by itself. The one-line descriptions are the tooltip
/// directory's (<see cref="UiTips"/>), so a search result and a tooltip say the same thing.
/// </summary>
public sealed partial class EditorView
{
    private sealed record SearchAction(string Title, string Path, string Words, string? Control, Func<string?> Needs, Action Open);

    private Border _actionSearch = null!;
    private TextBox _actionSearchBox = null!;
    private StackPanel _actionSearchResults = null!;
    private TextBlock _actionSearchHint = null!;
    private List<SearchAction> _searchActions = [];
    private List<SearchAction> _searchShown = [];
    private int _searchIndex;

    private static string? Always() => null;

    private List<SearchAction> BuildSearchActions()
    {
        var list = new List<SearchAction>();
        void Add(string title, string path, string words, string? control, Action open, Func<string?>? needs = null) =>
            list.Add(new(title, path, words, control, needs ?? Always, open));
        void Section(EditorWorkspace workspace, string title, string words) =>
            Add(title, "Раздел «" + title + "»", words, WorkspaceTabName(workspace), () => ShowWorkspace(workspace, openWorldList: true));
        void Tool(EditorTool tool, string control, string words)
        {
            if (!EditorSurfaceLayout.OffersTool(_isHexWorkshop, tool)) return;
            var section = EditorSurfaceLayout.WorkspaceOf(tool);
            Add(EditorLabels.Tool(tool), SectionTitle(section) + " → " + EditorLabels.Tool(tool), words, control, () =>
            {
                ShowWorkspace(section);
                if (tool == EditorTool.Stamp) OpenAssetCatalog(focusSearch: true); else _editor.ActiveTool = tool;
            });
        }
        void World(string control, string title, string words) =>
            Add(title, "Мир → " + title, words, control, () => ShowWorkspace(EditorWorkspace.World, openWorldList: true));

        Section(EditorWorkspace.Draw, "Рисовать", "рисование земля вода");
        if (!_isHexWorkshop)
        {
            Section(EditorWorkspace.Objects, "Объекты", "ассеты расстановка");
            Section(EditorWorkspace.Appearance, "Оформление", "вид стиль свет море");
        }
        Section(EditorWorkspace.World, "Мир", "атлас мир");

        Tool(EditorTool.Land, "LandToolButton", "земля суша остров материк берег обрыв");
        Tool(EditorTool.River, "RiverToolButton", "река вода озеро русло");
        Tool(EditorTool.Road, "RoadToolButton", "дорога путь тропа мост");
        Tool(EditorTool.Line, "LineToolButton", "линия граница");
        Tool(EditorTool.Shape, "ShapeToolButton", "фигура многоугольник градиент");
        Tool(EditorTool.Text, "TextToolButton", "текст подпись надпись название");
        Tool(EditorTool.Brush, "BrushToolButton", "кисть рисовать краска");
        Tool(EditorTool.Mask, "MaskToolButton", "маска скрыть стереть");
        Tool(EditorTool.Measure, "MeasureToolButton", "измерить расстояние линейка масштаб компас легенда картуш");
        Tool(EditorTool.Notes, "NotesToolButton", "заметки сущности персонаж туман игроки переходы портал лестница");
        Tool(EditorTool.Hex, "HexToolButton", "гекс клетка тайл");
        Tool(EditorTool.Stamp, "AssetsLibraryButton", "ассеты каталог горы деревья лес дома объект поставить");

        if (!_isHexWorkshop)
        {
            Add("Красота области", "Объекты → Область «…» → Красота", "красота мягкий край кучки рощи тень подложка вариации плотность",
                "EditableAreaTabsBeauty", () =>
                {
                    ShowWorkspace(EditorWorkspace.Objects);
                    OpenSelectionAreaTab("EditableAreaTabsBeauty");
                },
                () => _editor.SelectionArea is null ? "Сначала выделите горы или лес, поставленные областью, — появится кнопка «Область «…» → Красота»." : null);
            Add("Правила области", "Объекты → Область «…» → Правила", "правила вода дороги суша экология", "EditableAreaTabsRules", () =>
                {
                    ShowWorkspace(EditorWorkspace.Objects);
                    OpenSelectionAreaTab("EditableAreaTabsRules");
                },
                () => _editor.SelectionArea is null ? "Сначала выделите объекты, поставленные областью." : null);
            Add("Паспорт набора и роль", "Объекты → Каталог → значок паспорта на карточке → Роль", "паспорт роль набор дерево ель дом тег",
                null, () => { ShowWorkspace(EditorWorkspace.Objects); OpenAssetCatalog(focusSearch: false); });
            Add("Свет", "Оформление → Свет", "свет время суток вечер ночь день огни", "RibbonLightTab", () => ShowAppearanceTab(RibbonTab.Light));
            Add("Стили", "Оформление → Стили", "стиль тушь гравюра акварель уголь синька лунная ночь печать укиё", "RibbonStyleTab",
                () => ShowAppearanceTab(RibbonTab.Styles));
            Add("Море", "Оформление → Море", "море вода волны рябь изобаты стиль моря", "RibbonSeaTab", () => ShowAppearanceTab(RibbonTab.Sea));
        }

        World("AtlasCheckButton", "Проверка атласа", "атлас подкарта проверка связи");
        World("ScaleSyncButton", "Согласование", "согласование масштаб подкарта реки дороги перенести");
        World("AtlasRefreshButton", "Обновить атлас", "атлас превью подкарт обновить");
        World("TimelineButton", "Время", "время эпоха год история шкала");
        World("PlanetButton", "Планета", "планета глобус шов полюс проекция");
        World("GeographyCheckButton", "Проверка географии", "география проверка реки в гору ошибки");
        World("PointcrawlButton", "Пойнткроул", "пойнткроул места пути игра");
        World("CityButton", "Город", "город улицы кварталы стена");
        World("StarSectorButton", "Сектор", "сектор звёзды космос системы");
        World("IsometricButton", "Изометрия", "изометрия объём 3d вид сбоку");
        World("BookButton", "Книга и язык", "книга атлас художественный язык");

        Add("Экспорт", "Файл → Экспорт…", "экспорт png картинка печать vtt пакет игрока выгрузить", null, OpenExport);
        Add("Сохранить", "Кнопка «Сохранить» вверху (Ctrl+S)", "сохранить запись файл", "SaveButton", () => { });
        Add("Версии и сравнение", "Файл → Версии…", "версии сравнение откат история изменения", "FileMenuButton", () => { });
        Add("Размер и область карты", "Файл → Размер и область…", "размер холст область обрезать расширить", "FileMenuButton", () => { });
        Add("Сочетания клавиш", "Файл → Сочетания клавиш…", "клавиши сочетания горячие", "FileMenuButton", () => { });
        return list;
    }

    private static string SectionTitle(EditorWorkspace workspace) => workspace switch
    {
        EditorWorkspace.Objects => "Объекты",
        EditorWorkspace.World => "Мир",
        EditorWorkspace.Appearance => "Оформление",
        _ => "Рисовать",
    };

    private void ShowAppearanceTab(RibbonTab tab)
    {
        _ribbonTab = tab;
        ShowWorkspace(EditorWorkspace.Appearance);
    }

    // The field-like button of the document bar, as in the chosen layout: «Найти действие   Ctrl+K».
    private Button ActionSearchButton()
    {
        var button = Ui.TextButton("ActionSearchButton", "Найти действие", OpenActionSearch, "search-button");
        button.Content = new DockPanel { LastChildFill = false, Children =
        {
            new TextBlock { Text = "Найти действие…", VerticalAlignment = VerticalAlignment.Center }.Docked(Dock.Left),
            new TextBlock { Text = "Ctrl+K", Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }
                .Classed("faint").Docked(Dock.Right),
        } };
        return button;
    }

    private Border BuildActionSearch()
    {
        _actionSearchBox = new TextBox { Name = "ActionSearchBox", PlaceholderText = "Что сделать? Например: красота, роль, море, туман", FontSize = 17 };
        _actionSearchResults = new StackPanel { Name = "ActionSearchResults", Spacing = 2 };
        _actionSearchHint = new TextBlock { Name = "ActionSearchHint", TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false }.Classed("role-caption", "warn");
        _actionSearchBox.TextChanged += (_, _) => { _searchIndex = 0; FillActionSearch(); };
        _actionSearchBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { HideActionSearch(); e.Handled = true; }
            else if (e.Key == Key.Down) { MoveSearch(1); e.Handled = true; }
            else if (e.Key == Key.Up) { MoveSearch(-1); e.Handled = true; }
            else if (e.Key == Key.Enter) { RunSearch(_searchIndex); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        return new Border
        {
            Name = "ActionSearch", IsVisible = false, Width = 640, Padding = new Thickness(12), ZIndex = 40,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 40, 0, 0),
            Child = new StackPanel { Spacing = 8, Children = { _actionSearchBox, _actionSearchHint, _actionSearchResults,
                new TextBlock { Text = "↑ ↓ — выбрать, Enter — открыть место, Esc — закрыть" }.Classed("role-caption", "faint") } },
        }.Classed("action-search");
    }

    public void OpenActionSearch()
    {
        if (_searchActions.Count == 0) _searchActions = BuildSearchActions();
        _actionSearch.IsVisible = true;
        _actionSearchBox.Text = "";
        _searchIndex = 0;
        FillActionSearch();
        Dispatcher.UIThread.Post(() => _actionSearchBox.Focus(), DispatcherPriority.Input);
    }

    private void HideActionSearch()
    {
        _actionSearch.IsVisible = false;
        _canvas.Focus();
    }

    /// <summary>Every word typed must start a word of the name, the way or the synonyms; names that start with it lead.</summary>
    private static IEnumerable<SearchAction> Match(IEnumerable<SearchAction> actions, string query)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return actions;
        static IEnumerable<string> Words(SearchAction a) =>
            (a.Title + " " + a.Path + " " + a.Words).ToLowerInvariant().Split([' ', '→', '«', '»', '(', ')', ',', '.'], StringSplitOptions.RemoveEmptyEntries);
        return actions.Where(a => words.All(w => Words(a).Any(x => x.StartsWith(w, StringComparison.Ordinal))))
            .OrderBy(a => a.Title.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1);
    }

    private void FillActionSearch()
    {
        _searchShown = Match(_searchActions, _actionSearchBox.Text ?? "").Take(8).ToList();
        _actionSearchResults.Children.Clear();
        for (var i = 0; i < _searchShown.Count; i++)
        {
            var action = _searchShown[i];
            var index = i;
            var line = action.Control is { } name ? UiTips.For(name) : null;
            var body = new StackPanel { Spacing = 1, Children =
            {
                new TextBlock { Text = action.Title, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = action.Path }.Classed("role-caption", "muted"),
            } };
            if (line is not null) body.Children.Add(new TextBlock { Text = line, TextWrapping = Avalonia.Media.TextWrapping.Wrap }.Classed("role-caption", "faint"));
            var button = new Button { Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left }
                .Named("ActionResult" + i).Classed("search-result");
            Ui.SetClass(button, "active", i == _searchIndex);
            button.Click += (_, _) => RunSearch(index);
            _actionSearchResults.Children.Add(button);
        }
        _actionSearchHint.IsVisible = _searchShown.Count == 0;
        _actionSearchHint.Text = "Ничего не нашлось. Попробуйте другое слово: «река», «стиль», «город»…";
    }

    private void MoveSearch(int step)
    {
        if (_searchShown.Count == 0) return;
        _searchIndex = (_searchIndex + step + _searchShown.Count) % _searchShown.Count;
        for (var i = 0; i < _actionSearchResults.Children.Count; i++) Ui.SetClass(_actionSearchResults.Children[i], "active", i == _searchIndex);
    }

    private void RunSearch(int index)
    {
        if (index < 0 || index >= _searchShown.Count) return;
        var action = _searchShown[index];
        if (action.Needs() is { } needs)
        {
            // The place cannot open yet: say what it needs, keep the search open.
            _actionSearchHint.Text = needs;
            _actionSearchHint.IsVisible = true;
            return;
        }
        HideActionSearch();
        action.Open();
        if (action.Control is { } name) Dispatcher.UIThread.Post(() => LightUp(name), DispatcherPriority.Background);
    }

    /// <summary>Lights the found control for a moment where it now stands (main window or the catalog).</summary>
    private void LightUp(string name)
    {
        var roots = new List<Visual> { this };
        if (_assetCatalogWindow is { IsVisible: true } catalog) roots.Add(catalog);
        var control = roots.SelectMany(r => r.GetVisualDescendants()).OfType<Control>()
            .FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);
        if (control is null) return;
        control.BringIntoView();
        Ui.SetClass(control, "search-found", true);
        DispatcherTimer.RunOnce(() => Ui.SetClass(control, "search-found", false), TimeSpan.FromSeconds(2.4));
    }
}
