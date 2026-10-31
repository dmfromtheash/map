using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Map.App.Controls;
using Map.App.Services;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private Window? _atlasCheckWindow;
    private readonly List<Bitmap> _atlasImages = [];

    private Border _worldSection = null!;
    private readonly List<Action> _worldSyncs = [];

    // R25 + DEC-070 step 2: the row keeps only «Мир» and the way through the atlas («Наверх», the crumbs); the world
    // functions moved from a row of bare buttons into the «Мир» list, each with what it does and whether it fits this map.
    private Control BuildAtlasNavigation(Grid canvasHost)
    {
        var row = new StackPanel { Name = "AtlasNavigation", Orientation = Orientation.Horizontal, Spacing = 6 };
        var crumbs = new StackPanel { Name = "AtlasBreadcrumbs", Orientation = Orientation.Horizontal, Spacing = 4 };
        var overlay = new Canvas { Name = "AtlasChildPreviews", ClipToBounds = true };
        canvasHost.Children.Add(overlay);
        var frames = new List<(Control Control, double X, double Y)>();
        var up = Ui.TextButton("AtlasUpButton", "Наверх", () =>
        {
            var trail = _editor.AtlasBreadcrumbs;
            if (trail.Length > 1) _editor.OpenAtlasMap(trail[^2].Id);
        }, "compact");
        _worldSection = BuildWorldSection();
        canvasHost.Children.Add(_worldSection);
        // DEC-070 step 3: the sections lead the row; the way through the atlas follows them.
        row.Children.Add(BuildWorkspaceTabs());
        up.Margin = new Thickness(12, 0, 0, 4); crumbs.Margin = new Thickness(0, 0, 0, 4);
        up.VerticalAlignment = crumbs.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(up); row.Children.Add(crumbs);
        void PositionFrames()
        {
            foreach (var frame in frames)
            {
                var point = _editor.Session.Viewport.WorldToScreen(frame.X, frame.Y);
                Avalonia.Controls.Canvas.SetLeft(frame.Control, point.X); Avalonia.Controls.Canvas.SetTop(frame.Control, point.Y);
            }
        }
        void Refresh()
        {
            crumbs.Children.Clear(); overlay.Children.Clear(); frames.Clear();
            foreach (var bitmap in _atlasImages) bitmap.Dispose(); _atlasImages.Clear();
            var trail = _editor.AtlasBreadcrumbs; up.IsEnabled = trail.Length > 1;
            // A map outside an atlas has no way up and no crumbs worth showing: both stay out of the row.
            up.IsVisible = trail.Length > 1;
            crumbs.IsVisible = trail.Length > 1 || _editor.AtlasChildren.Length > 0;
            if (_worldSection.IsVisible) SyncWorldSection();
            foreach (var map in trail)
            {
                if (crumbs.Children.Count > 0) crumbs.Children.Add(new TextBlock { Text = " / ", VerticalAlignment = VerticalAlignment.Center });
                var button = Ui.TextButton("AtlasCrumb_" + map.Id.ToString("N"), map.Title, () => _editor.OpenAtlasMap(map.Id), "compact");
                button.IsEnabled = map.Id != _editor.Session.Document.Id.Value;
                crumbs.Children.Add(button);
            }
            foreach (var child in _editor.AtlasChildren)
            {
                var body = new StackPanel { Spacing = 2 };
                if (child.Preview is { Length: > 0 } bytes)
                {
                    try
                    {
                        var bitmap = new Bitmap(new MemoryStream(bytes)); _atlasImages.Add(bitmap);
                        body.Children.Add(new Image { Source = bitmap, Width = 112, Height = 72, Stretch = Stretch.Uniform });
                    }
                    catch (ArgumentException) { }
                }
                body.Children.Add(new TextBlock { Text = child.Map.Title, MaxWidth = 150, TextWrapping = TextWrapping.Wrap });
                var button = Ui.TextButton("AtlasChild_" + child.Map.Id.ToString("N"), child.Map.Title, () => _editor.OpenAtlasMap(child.Map.Id), "compact");
                button.Content = body;
                // A plain theme frame with the saved preview, anchored to the linked parent area.
                var frame = new Border { BorderThickness = new Thickness(1), Child = button };
                frame.Classes.Add("panel");
                ToolTip.SetTip(button, "Открыть подкарту: " + child.Map.Title);
                frames.Add((frame, child.Link.Transform.X, child.Link.Transform.Y)); overlay.Children.Add(frame);
            }
            ToolTip.SetTip(row, _editor.AtlasMessage); PositionFrames();
        }
        _editor.PropertyChanged += AtlasChanged;
        void AtlasChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(EditorViewModel.Atlas)) Dispatcher.UIThread.Post(Refresh);
        }
        _canvas.ViewportChanged += ViewChanged;
        void ViewChanged(object? sender, EventArgs e) => PositionFrames();
        DetachedFromVisualTree += (_, _) =>
        {
            _editor.PropertyChanged -= AtlasChanged; _canvas.ViewportChanged -= ViewChanged;
            _atlasCheckWindow?.Close();
            foreach (var image in _atlasImages) image.Dispose(); _atlasImages.Clear();
        };
        Refresh(); UiTasks.Forget(this, _editor.RefreshAtlasAsync);
        return new Border
        {
            Child = new DockPanel
            {
                Children =
                {
                    new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = row },
                },
            },
        }.Classed("workspace-row");
    }

    /// <summary>
    /// DEC-070 step 2: «Мир» - the world functions as a signed list over the top left of the map: the name, one line of
    /// what it does (the same words as its tooltip) and whether it fits this map. A pick closes the list and opens the
    /// function's own window; nothing here edits the map.
    /// </summary>
    // «Мир» (owner's pick 2026-10-31, variant 2 of https://claude.ai/artifact/UA3ciWpYB3UVgFbX56uqNo): a question and
    // four big answers by goal, then the functions of that goal as cards named by what they do (Canva, Earth Studio,
    // Scratch; a «More» tab is the antipattern). The function opens as before; its button keeps its name.
    private static readonly (string Name, string Title, string Glyph, string Colour)[] WorldGoals =
    [
        ("WorldGoalCheck", "Проверить карту", MapIcons.Check, "#7FD08A"),
        ("WorldGoalLook", "Посмотреть иначе", MapIcons.Versions, "#6FB3D4"),
        ("WorldGoalCreate", "Создать что-то большое", MapIcons.Star, "#D4AE62"),
        ("WorldGoalCollect", "Собрать для игроков", MapIcons.File, "#C58BB5"),
    ];

    private StackPanel _worldHome = null!;
    private readonly List<(Control Panel, string Goal)> _worldGoalPanels = [];

    private Border BuildWorldSection()
    {
        var groups = WorldGoals.ToDictionary(g => g.Name, g => new StackPanel { Spacing = 6 });
        var summaries = WorldGoals.ToDictionary(g => g.Name, _ => new List<string>());
        void Item(string goal, string name, string title, string why, Action open, Func<(bool Fits, string Note)> fit)
        {
            // The «Мир» card lists the same entries with the same «подходит» (DEC-080).
            var goalTitle = WorldGoals.First(g => g.Name == goal).Title;
            _worldEntries.Add((goalTitle, name, title, fit));
            summaries[goal].Add(title);
            var note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5 }.Classed("role-caption");
            var body = new StackPanel { Spacing = 1, Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14 },
                new TextBlock { Text = why, TextWrapping = TextWrapping.Wrap, FontSize = 12 }.Classed("muted"),
                note,
            } };
            var button = Ui.TextButton(name, title, () => { ShowWorldSection(false); open(); }, "asset-entry");
            button.Content = body; button.HorizontalAlignment = HorizontalAlignment.Stretch;
            ToolTip.SetTip(button, UiTips.For(name));
            _worldSyncs.Add(() =>
            {
                var (fits, text) = fit();
                note.Text = fits ? "✓ " + text : text;
                Ui.SetClass(note, "success", fits); Ui.SetClass(note, "faint", !fits);
            });
            groups[goal].Children.Add(button);
        }
        var document = () => _editor.Session.Document;
        bool InAtlas() => _editor.AtlasBreadcrumbs.Length > 1 || _editor.AtlasChildren.Length > 0;
        bool HasChildren() => _editor.AtlasChildren.Length > 0;
        (bool, string) Always() => (true, "Подходит к этой карте");

        Item("WorldGoalCheck", "GeographyCheckButton", "Найти ошибки", "Реки в гору, дороги в обрыв, поселения без дороги.",
            () => ShowGeographyCheck(), () => MapCategories.IsHex(document().Category)
                ? (false, "Лучше для обычной карты: на гексах рек и дорог линиями почти нет") : (true, "Подходит к этой карте"));
        Item("WorldGoalCheck", "AtlasCheckButton", "Сверить с подкартами", "Где карта и её подкарты расходятся.",
            () => UiTasks.Forget(this, ShowAtlasCheckAsync),
            () => InAtlas() ? (true, "Подходит: у карты есть связи в атласе") : (false, "Эта карта пока не в атласе — проверять нечего"));
        Item("WorldGoalLook", "TimelineButton", "Мир в другой год", "Что было на карте в выбранном году.", ShowTimeline, Always);
        Item("WorldGoalLook", "PlanetButton", "Карта — целая планета", "Глобус и проекции; левый край сходится с правым.", ShowPlanet,
            () => Math.Abs(document().Canvas.Width - 2 * document().Canvas.Height) < 1
                ? (true, "Подходит: карта 2 : 1, как целая планета") : (false, "Лучше для карты мира 2 : 1"));
        Item("WorldGoalLook", "IsometricButton", "Посмотреть объёмно", "Город в изометрии, с четырёх сторон.", ShowIsometric, Always);
        Item("WorldGoalCreate", "CityButton", "Построить город", "Улицы, дома и стены одной кнопкой.", ShowCity,
            () => MapCategories.IsCity(document().Category)
                ? (true, "Подходит: это карта города") : (true, "Здесь — «Город из точки»: новая подкарта-город"));
        Item("WorldGoalCreate", "PointcrawlButton", "Места и пути для игры", "Точки и дороги между ними для партии.", ShowPointcrawl, Always);
        Item("WorldGoalCreate", "StarSectorButton", "Звёздный сектор", "Звёзды и планеты на гексах.", ShowStarSector,
            () => _editor.SectorLayer is not null ? (true, "Подходит: на карте есть гексы") : (false, "Нужна карта с гексами"));
        Item("WorldGoalCollect", "BookButton", "Книга для игроков", "Атлас и язык мира одной книгой PDF.", ShowBook, Always);
        Item("WorldGoalCollect", "ScaleSyncButton", "Перенести реки и дороги", "Между картой и её подкартами.", ShowScaleSync,
            () => HasChildren() ? (true, "Подходит: у карты есть подкарты") : (false, "Нужны подкарты этой карты"));
        Item("WorldGoalCollect", "AtlasRefreshButton", "Обновить картинки подкарт", "Перерисовать маленькие подкарты на этой карте.",
            () => UiTasks.Forget(this, _editor.RefreshAtlasAsync),
            () => HasChildren() ? (true, "Подходит: у карты есть подкарты") : (false, "Нужны подкарты этой карты"));

        // The first view: the question and four big answers.
        _worldHome = new StackPanel { Spacing = 8, Children =
        {
            new TextBlock { Text = "Что хочешь сделать?", FontSize = 19, Margin = new Thickness(2, 0, 0, 2) }.Classed("panel-title"),
        } };
        foreach (var (goal, title, glyph, colour) in WorldGoals)
        {
            var tint = Color.Parse(colour);
            var mark = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x33, tint.R, tint.G, tint.B)),
                Child = Centered(MapIcons.Stroked(glyph, 20, new SolidColorBrush(tint))),
            };
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15 },
                new TextBlock { Text = string.Join(" · ", summaries[goal]), TextWrapping = TextWrapping.Wrap, FontSize = 11.5 }.Classed("muted"),
            } };
            var answer = Ui.TextButton(goal, title, () => ShowWorldGoal(goal), "asset-entry")
                .WithTip(title + ": " + string.Join(", ", summaries[goal]).ToLowerInvariant() + ". Щелчок — показать эти дела.");
            answer.Content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, Children = { mark, text } };
            Grid.SetColumn(text, 1);
            answer.HorizontalAlignment = HorizontalAlignment.Stretch;
            answer.Padding = new Thickness(10);
            _worldHome.Children.Add(answer);

            // The goal's own view: back to the question, the goal's name, its cards.
            var back = Ui.TextButton(goal + "Back", "← Что хочешь сделать?", () => ShowWorldGoal(null), "compact");
            back.HorizontalAlignment = HorizontalAlignment.Left;
            var panel = new StackPanel
            {
                Spacing = 8, IsVisible = false, Tag = "Reveal:" + goal, Name = goal + "Panel",
                Children =
                {
                    back,
                    new TextBlock { Text = title, FontSize = 18, Foreground = new SolidColorBrush(tint), Margin = new Thickness(2, 0, 0, 0) }.Classed("panel-title"),
                    groups[goal],
                },
            };
            _worldGoalPanels.Add((panel, goal));
        }

        var close = Ui.TextButton("WorldSectionCloseButton", "Закрыть", () => ShowWorldSection(false), "compact");
        var header = new DockPanel { Children =
        {
            close.Docked(Dock.Right),
            new TextBlock { Text = "Мир", VerticalAlignment = VerticalAlignment.Center }.Classed("role-panel"),
        } };
        var pages = new Panel { Children = { _worldHome } };
        foreach (var (panel, _) in _worldGoalPanels) pages.Children.Add(panel);
        return new Border
        {
            Name = "WorldSection", IsVisible = false, Width = 340, Padding = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Ui.RailWidth + 4, Ui.FloatingGap, 0, Ui.FloatingGap), ZIndex = 20,
            Child = new DockPanel { Children =
            {
                header.Docked(Dock.Top),
                new ScrollViewer { Margin = new Thickness(0, 8, 0, 0), Content = pages },
            } },
        }.Classed("elevated");
    }

    /// <summary>One goal's cards, or the question again (null).</summary>
    private void ShowWorldGoal(string? goal)
    {
        _worldHome.IsVisible = goal is null;
        foreach (var (panel, name) in _worldGoalPanels) panel.IsVisible = name == goal;
    }

    private void ShowWorldSection(bool show)
    {
        if (show) { SyncWorldSection(); ShowWorldGoal(null); }
        _worldSection.IsVisible = show;
    }

    private void SyncWorldSection()
    {
        foreach (var sync in _worldSyncs) sync();
    }

    private Window? _geographyWindow;

    /// <summary>
    /// R24: what looks wrong in the geography of the map - each finding with «Показать» (the view
    /// goes there) and «Намеренно» (fantasy on purpose: the check leaves it alone, one undo step).
    /// </summary>
    public void ShowGeographyCheck()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (_geographyWindow is { IsVisible: true }) { _geographyWindow.Activate(); return; }
        var window = new Window { Name = "GeographyCheckWindow", Title = "Проверка географии", Width = 640, Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        if (TestMode.Enabled) PlaceTestWindow(window, owner);
        _geographyWindow = window;
        var summary = new TextBlock { Name = "GeographyCheckSummary", TextWrapping = TextWrapping.Wrap };
        var issues = new StackPanel { Name = "GeographyCheckIssues", Spacing = 10 };
        void Inspect()
        {
            var results = _editor.CheckGeography();
            summary.Text = results.IsEmpty ? "Ничего странного не найдено: реки текут вниз, у озёр есть сток, дороги не упираются в обрывы."
                : $"Найдено: {results.Length}. Это только подсказки — фантастика допустима, отметьте её «намеренно».";
            issues.Children.Clear();
            var index = 0;
            foreach (var issue in results)
            {
                index++;
                var captured = issue;
                var show = Ui.TextButton("GeographyShow" + index, "Показать", () => { _editor.ShowGeographyIssue(captured); Canvas.InvalidateVisual(); }, "compact");
                var intentional = Ui.TextButton("GeographyIntentional" + index, "Намеренно", () => { if (_editor.MarkIntentional(captured)) Inspect(); }, "compact");
                issues.Children.Add(Ui.Subgroup(issue.Message, new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        new TextBlock { Text = "Что сделать: " + issue.Fix, TextWrapping = TextWrapping.Wrap },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { show, intentional } },
                    },
                }));
            }
        }
        window.Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { summary,
            new ScrollViewer { Content = issues, MaxHeight = 330 },
            Ui.TextButton("GeographyCheckAgainButton", "Проверить снова", Inspect, "compact"),
            Ui.TextButton("GeographyCheckCloseButton", "Закрыть", window.Close, "compact") } };
        window.Closed += (_, _) => { if (ReferenceEquals(_geographyWindow, window)) _geographyWindow = null; };
        window.Show(owner); Inspect();
    }

    public async Task ShowAtlasCheckAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (_atlasCheckWindow is { IsVisible: true }) { _atlasCheckWindow.Activate(); return; }
        var window = new Window { Name = "AtlasCheckWindow", Title = "Проверка атласа", Width = 700, Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        if (TestMode.Enabled) PlaceTestWindow(window, owner);
        _atlasCheckWindow = window;
        var summary = new TextBlock { Name = "AtlasCheckSummary", Text = "Проверка карт и ассетов…", TextWrapping = TextWrapping.Wrap };
        var issues = new StackPanel { Name = "AtlasCheckIssues", Spacing = 10 };
        async Task Inspect()
        {
            var results = await _editor.CheckAtlasAsync();
            summary.Text = results.IsEmpty ? "Проблем не найдено. Карты, связи и ассеты доступны." : "Найдено проблем: " + results.Length;
            issues.Children.Clear();
            foreach (var issue in results)
                issues.Children.Add(Ui.Subgroup(issue.Message, new TextBlock { Text = "Что сделать: " + issue.Repair, TextWrapping = TextWrapping.Wrap }));
        }
        var body = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { summary,
            new ScrollViewer { Content = issues, MaxHeight = 330 },
            Ui.TextButton("AtlasCheckAgainButton", "Проверить снова", () => UiTasks.Forget(this, Inspect), "compact"),
            Ui.TextButton("AtlasCheckCloseButton", "Закрыть", window.Close, "compact") } };
        window.Content = body;
        window.Closed += (_, _) => { if (ReferenceEquals(_atlasCheckWindow, window)) _atlasCheckWindow = null; };
        window.Show(owner); await Inspect();
    }
}
