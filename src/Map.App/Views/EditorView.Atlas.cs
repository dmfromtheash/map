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
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private Window? _atlasCheckWindow;
    private readonly List<Bitmap> _atlasImages = [];

    // R25: functional controls only. The integrator owns the navigation design.
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
        row.Children.Add(up); row.Children.Add(crumbs);
        row.Children.Add(Ui.TextButton("AtlasCheckButton", "Проверка атласа", () => _ = ShowAtlasCheckAsync(), "compact"));
        row.Children.Add(Ui.TextButton("GeographyCheckButton", "Проверка географии", () => ShowGeographyCheck(), "compact"));
        row.Children.Add(Ui.TextButton("TimelineButton", "Время", ShowTimeline, "compact"));
        row.Children.Add(Ui.TextButton("PointcrawlButton", "Пойнткроул", ShowPointcrawl, "compact"));
        row.Children.Add(Ui.TextButton("PlanetButton", "Планета", ShowPlanet, "compact"));
        row.Children.Add(Ui.TextButton("BookButton", "Книга и язык", ShowBook, "compact"));
        row.Children.Add(Ui.TextButton("StarSectorButton", "Сектор", ShowStarSector, "compact"));
        row.Children.Add(Ui.TextButton("ScaleSyncButton", "Согласование", ShowScaleSync, "compact"));
        row.Children.Add(Ui.TextButton("AtlasRefreshButton", "Обновить атлас", () => _ = _editor.RefreshAtlasAsync(), "compact"));
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
        Refresh(); _ = _editor.RefreshAtlasAsync();
        return new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = row };
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
            Ui.TextButton("AtlasCheckAgainButton", "Проверить снова", () => _ = Inspect(), "compact"),
            Ui.TextButton("AtlasCheckCloseButton", "Закрыть", window.Close, "compact") } };
        window.Content = body;
        window.Closed += (_, _) => { if (ReferenceEquals(_atlasCheckWindow, window)) _atlasCheckWindow = null; };
        window.Show(owner); await Inspect();
    }
}
