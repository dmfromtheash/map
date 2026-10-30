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
                    // DEC-070 «Б»: «Содержимое» and «Слои» at the right end of the row open those pages of the right column.
                    BuildRightColumnButtons().Docked(Dock.Right),
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
    private Border BuildWorldSection()
    {
        var list = new StackPanel { Spacing = 4 };
        var group = "";
        void Group(string title)
        {
            group = title;
            list.Children.Add(new TextBlock { Text = title, Margin = new Thickness(2, list.Children.Count == 0 ? 0 : 8, 0, 2) }.Classed("role-caption", "muted"));
        }
        void Item(string name, string title, Action open, Func<(bool Fits, string Note)> fit)
        {
            // The «Мир» card lists the same entries with the same «подходит» (DEC-080).
            _worldEntries.Add((group, name, title, fit));
            var note = new TextBlock { TextWrapping = TextWrapping.Wrap }.Classed("role-caption");
            var body = new StackPanel { Spacing = 1, Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = UiTips.For(name), TextWrapping = TextWrapping.Wrap }.Classed("role-caption", "muted"),
                note,
            } };
            var button = Ui.TextButton(name, title, () => { ShowWorldSection(false); open(); }, "asset-entry");
            button.Content = body; button.HorizontalAlignment = HorizontalAlignment.Stretch;
            _worldSyncs.Add(() =>
            {
                var (fits, text) = fit();
                note.Text = text;
                Ui.SetClass(note, "success", fits); Ui.SetClass(note, "faint", !fits);
            });
            list.Children.Add(button);
        }
        var document = () => _editor.Session.Document;
        bool InAtlas() => _editor.AtlasBreadcrumbs.Length > 1 || _editor.AtlasChildren.Length > 0;
        bool HasChildren() => _editor.AtlasChildren.Length > 0;
        (bool, string) Always() => (true, "Подходит к этой карте");

        Group("Атлас: карта и её подкарты");
        Item("AtlasCheckButton", "Проверка атласа", () => UiTasks.Forget(this, ShowAtlasCheckAsync),
            () => InAtlas() ? (true, "Подходит: у карты есть связи в атласе") : (false, "Эта карта пока не в атласе — проверять нечего"));
        Item("ScaleSyncButton", "Согласование", ShowScaleSync,
            () => HasChildren() ? (true, "Подходит: у карты есть подкарты") : (false, "Нужны подкарты этой карты"));
        Item("AtlasRefreshButton", "Обновить атлас", () => UiTasks.Forget(this, _editor.RefreshAtlasAsync),
            () => HasChildren() ? (true, "Подходит: у карты есть подкарты") : (false, "Нужны подкарты этой карты"));
        Group("Мир во времени и пространстве");
        Item("TimelineButton", "Время", ShowTimeline, Always);
        Item("PlanetButton", "Планета", ShowPlanet, () => Math.Abs(document().Canvas.Width - 2 * document().Canvas.Height) < 1
            ? (true, "Подходит: карта 2 : 1, как целая планета") : (false, "Лучше для карты мира 2 : 1"));
        Item("GeographyCheckButton", "Проверка географии", () => ShowGeographyCheck(), () => MapCategories.IsHex(document().Category)
            ? (false, "Лучше для обычной карты: на гексах рек и дорог линиями почти нет") : (true, "Подходит к этой карте"));
        Group("Для игры");
        Item("PointcrawlButton", "Пойнткроул", ShowPointcrawl, Always);
        Group("Особые виды карты");
        Item("CityButton", "Город", ShowCity, () => MapCategories.IsCity(document().Category)
            ? (true, "Подходит: это карта города") : (true, "Здесь — «Город из точки»: новая подкарта-город"));
        Item("StarSectorButton", "Сектор", ShowStarSector, () => _editor.SectorLayer is not null
            ? (true, "Подходит: на карте есть гексы") : (false, "Нужна карта с гексами"));
        Item("IsometricButton", "Изометрия", ShowIsometric, Always);
        Item("BookButton", "Книга и язык", ShowBook, Always);

        var close = Ui.TextButton("WorldSectionCloseButton", "Закрыть", () => ShowWorldSection(false), "compact");
        var header = new DockPanel { Children =
        {
            close.Docked(Dock.Right),
            new TextBlock { Text = "Мир", VerticalAlignment = VerticalAlignment.Center }.Classed("role-panel"),
        } };
        return new Border
        {
            Name = "WorldSection", IsVisible = false, Width = 340, Padding = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Ui.RailWidth + 4, Ui.FloatingGap, 0, Ui.FloatingGap), ZIndex = 20,
            Child = new DockPanel { Children =
            {
                header.Docked(Dock.Top),
                new ScrollViewer { Margin = new Thickness(0, 8, 0, 0), Content = list },
            } },
        }.Classed("elevated");
    }

    private void ShowWorldSection(bool show)
    {
        if (show) SyncWorldSection();
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
