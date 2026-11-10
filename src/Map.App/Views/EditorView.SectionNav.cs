using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Map.App.Controls;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>One function under a section item: its name and what a click on it does in the program.</summary>
internal sealed record NavFunction(string Title, Action Go);

/// <summary>
/// One entry of a section's navigation: its name, group, icon, key, the dry text («**…**» is bold), its main functions,
/// what opening it does, whether it fits this map and the show of the program's renderer (null: no show).
/// </summary>
internal sealed record NavItem(string Title, string Group, string? Glyph, string? Key, string Text,
    IReadOnlyList<NavFunction> Functions, Action Open, Func<(bool Fits, string Note)>? Fit, ShowKind? Show);

/// <summary>
/// The navigation of a section tab (owner's pick 2026-11-10, variant 7 «Со схемой», DEC-093): pointing at «Рисовать»,
/// «Объекты», «Мир» or «Оформление» opens a compact panel under the tab instead of the read-only card — a search
/// field, the list of the section's functions and, for the one pointed at, a short show drawn by the program's renderer
/// (<see cref="EditorViewModel.ShowFrame"/>), the dry text, its main functions as buttons and «Открыть». The panel stays
/// open while the pointer is over the tab or the panel, so every entry can be pointed at and clicked.
/// </summary>
public sealed partial class EditorView
{
    private Popup? _navPopup;
    private EditorWorkspace? _navOpen;
    private DispatcherTimer? _navOpenTimer, _navCloseTimer, _navShowTimer, _navHoverTimer;
    private readonly Dictionary<ShowKind, (string Signature, Bitmap[] Frames)> _navFrames = [];
    private readonly HashSet<ShowKind> _navFramesPending = [];
    private bool _navOverPanel, _navOverTab;
    private readonly Dictionary<EditorWorkspace, Button> _navTabs = [];

    /// <summary>For the smokes: opens a section's navigation as pointing at its tab does; the panel, or null.</summary>
    public Control? OpenSectionNavForSmoke(EditorWorkspace workspace)
    {
        if (!_navTabs.TryGetValue(workspace, out var tab)) return null;
        OpenSectionNav(tab, workspace);
        _navOverPanel = true;
        return _navPopup?.Child;
    }

    public void CloseSectionNavForSmoke() { _navOverPanel = false; CloseSectionNav(); }

    /// <summary>The panel a tab opens: built once, filled each time it opens so its «подходит» follows the map.</summary>
    private void AttachSectionNav(Button tab, EditorWorkspace workspace, Panel host)
    {
        if (_navPopup is null)
        {
            _navPopup = new Popup
            {
                Name = "SectionNavPopup", Placement = PlacementMode.BottomEdgeAlignedLeft, IsLightDismissEnabled = false,
                OverlayDismissEventPassThrough = true,
            };
            host.Children.Add(_navPopup);
            _navPopup.Closed += (_, _) => { _navOpen = null; _navShowTimer?.Stop(); };
        }
        // The panel is the tab's explanation: it carries no tooltip of its own (the audit knows the class).
        Ui.SetClass(tab, "section-nav", true);
        _navTabs[workspace] = tab;
        tab.PointerEntered += (_, _) =>
        {
            _navOverTab = true;
            _navCloseTimer?.Stop();
            if (_navOpen == workspace) return;
            // Moving from one open panel to the next tab switches at once; the first opening waits like every tip.
            Schedule(ref _navOpenTimer, TimeSpan.FromMilliseconds(_navOpen is null ? Ui.TipDelay : 60), () => OpenSectionNav(tab, workspace));
        };
        tab.PointerExited += (_, _) => { _navOverTab = false; _navOpenTimer?.Stop(); ScheduleNavClose(); };
        tab.AddHandler(PointerPressedEvent, (_, _) => { _navOpenTimer?.Stop(); CloseSectionNav(); }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private static void Schedule(ref DispatcherTimer? timer, TimeSpan delay, Action action)
    {
        timer ??= new DispatcherTimer(DispatcherPriority.Input);
        timer.Stop();
        timer.Interval = delay;
        var run = timer;
        void Tick(object? s, EventArgs e) { run.Stop(); run.Tick -= Tick; action(); }
        timer.Tick += Tick;
        timer.Start();
    }

    private void ScheduleNavClose() => Schedule(ref _navCloseTimer, TimeSpan.FromMilliseconds(300), () =>
    {
        if (!_navOverPanel && !_navOverTab) CloseSectionNav();
    });

    private void CloseSectionNav()
    {
        _navShowTimer?.Stop();
        if (_navPopup is { IsOpen: true }) _navPopup.IsOpen = false;
        _navOpen = null;
    }

    private void OpenSectionNav(Button tab, EditorWorkspace workspace)
    {
        if (_navPopup is null) return;
        var items = SectionNavItems(workspace);
        if (items.Count == 0) { CloseSectionNav(); return; }
        _navShowTimer?.Stop();
        _navPopup.IsOpen = false;
        _navPopup.PlacementTarget = tab;
        _navPopup.Child = BuildSectionNav(workspace, items);
        _navOpen = workspace;
        _navOverPanel = false;
        _navPopup.IsOpen = true;
    }

    // ------------------------------------------------------------------ the panel

    private Control BuildSectionNav(EditorWorkspace workspace, IReadOnlyList<NavItem> items)
    {
        var accent = AccentOf(workspace);
        var accentLight = Mix(accent, Colors.White, .35);
        var text = new SolidColorBrush(Color.Parse("#F3E3BF"));
        var muted = new SolidColorBrush(Color.Parse("#B8AA90"));
        var line = new SolidColorBrush(Color.Parse("#46372A"));
        var hotBrush = new SolidColorBrush(Mix(Color.Parse("#211912"), accent, .22));

        var search = new TextBox
        {
            Name = "SectionNavSearch", PlaceholderText = $"Найти в «{SectionTitle(workspace)}»…", MinHeight = 30, FontSize = 13.5,
            Margin = new Thickness(8, 8, 8, 4),
        };
        AutomationProperties.SetName(search, "Найти в разделе");
        var list = new StackPanel { Name = "SectionNavList", Spacing = 0, Margin = new Thickness(0, 2, 0, 6) };
        var detail = new StackPanel { Name = "SectionNavDetail", Spacing = 7, Margin = new Thickness(14, 10, 14, 12) };
        var picture = new Image { Stretch = Stretch.UniformToFill };
        var pictureBox = new Border
        {
            Name = "SectionNavShow", Height = 178, CornerRadius = new CornerRadius(7), ClipToBounds = true,
            BorderBrush = line, BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.Parse("#1A140E")), Child = picture,
        };

        var shown = items.ToList();
        var hot = 0;
        void Pick(int index, bool redraw)
        {
            hot = Math.Clamp(index, 0, Math.Max(0, shown.Count - 1));
            if (redraw) DrawList(); else foreach (var row in list.Children.OfType<Button>()) row.Background = (int?)row.Tag == hot ? hotBrush : Brushes.Transparent;
            FillDetail();
        }
        void DrawList()
        {
            list.Children.Clear();
            var query = search.Text?.Trim() ?? "";
            string? group = null;
            for (var i = 0; i < shown.Count; i++)
            {
                var item = shown[i];
                if (item.Group != group && items.Select(x => x.Group).Distinct().Count() > 1)
                {
                    group = item.Group;
                    list.Children.Add(new TextBlock
                    {
                        Text = group.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeight.Bold, LetterSpacing = .8,
                        Foreground = new SolidColorBrush(accentLight), Margin = new Thickness(12, 7, 8, 3),
                    });
                }
                var index = i;
                var fits = item.Fit?.Invoke().Fits ?? true;
                var name = new TextBlock { Text = item.Title, FontSize = 13.5, Foreground = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var row = new DockPanel { LastChildFill = true };
                row.Children.Add(Glyph(item.Glyph, item.Title, accentLight, 16).Docked(Dock.Left));
                if (item.Key is { Length: > 0 } key) row.Children.Add(KeyPill(key, accent, accentLight).Docked(Dock.Right));
                row.Children.Add(name);
                var button = new Button
                {
                    Name = "SectionNavItem_" + index, Tag = index, Content = row, Height = 29, Padding = new Thickness(12, 0, 10, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = index == hot ? hotBrush : Brushes.Transparent, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
                    Opacity = fits ? 1 : .55,
                };
                AutomationProperties.SetName(button, item.Title);
                // The entry follows the pointer only once it rests on a row: a pointer crossing the list on its way to
                // the functions of the entry shown must not switch it to every row it passes (live check 2026-11-10).
                button.PointerEntered += (_, _) =>
                {
                    if (hot == index) return;
                    Schedule(ref _navHoverTimer, TimeSpan.FromMilliseconds(150), () => { if (button.IsPointerOver) Pick(index, redraw: false); });
                };
                button.PointerExited += (_, _) => _navHoverTimer?.Stop();
                button.Click += (_, _) => Go(item.Open);
                list.Children.Add(button);
            }
            if (shown.Count == 0)
                list.Children.Add(new TextBlock { Text = $"«{query}» не найдено", Foreground = muted, Margin = new Thickness(12, 8), FontSize = 13 });
        }
        void FillDetail()
        {
            detail.Children.Clear();
            _navShowTimer?.Stop();
            if (shown.Count == 0) return;
            var item = shown[hot];
            if (item.Show is { } kind) { detail.Children.Add(pictureBox); PlayShow(kind, picture); }
            var title = new TextBlock { Text = item.Title, FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
            if (Application.Current?.TryFindResource("MapDisplayFont", out var font) == true && font is FontFamily family) title.FontFamily = family;
            detail.Children.Add(title);
            detail.Children.Add(RichText(item.Text, muted, text));
            var fit = item.Fit?.Invoke();
            if (fit is { Fits: false } no) detail.Children.Add(RichText("Недоступно: **" + no.Note.Split(" — ")[0] + "**.", muted, text));
            if (item.Functions.Count > 0)
            {
                detail.Children.Add(new TextBlock { Text = "ФУНКЦИИ", FontSize = 10, FontWeight = FontWeight.Bold, LetterSpacing = .7, Foreground = muted });
                var chips = new WrapPanel { Name = "SectionNavFunctions" };
                foreach (var function in item.Functions)
                {
                    var chip = new Button
                    {
                        Content = function.Title, FontSize = 12, Padding = new Thickness(7, 2), Margin = new Thickness(0, 0, 4, 4), MinHeight = 0,
                        Foreground = text, Background = new SolidColorBrush(Mix(TileBase, accent, .14)),
                        BorderBrush = new SolidColorBrush(Mix(TileBase, accent, .35)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                    }.Named("SectionNavFunction_" + function.Title);
                    AutomationProperties.SetName(chip, item.Title + ": " + function.Title);
                    chip.Click += (_, _) => Go(function.Go);
                    chips.Children.Add(chip);
                }
                detail.Children.Add(chips);
            }
            var foot = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 2, 0, 0) };
            var open = new Button
            {
                Name = "SectionNavOpen", Content = "Открыть", FontWeight = FontWeight.SemiBold, Padding = new Thickness(14, 5),
                Background = new SolidColorBrush(accent), Foreground = new SolidColorBrush(Color.Parse("#14100B")), CornerRadius = new CornerRadius(7),
            };
            open.Click += (_, _) => Go(item.Open);
            foot.Children.Add(open.Docked(Dock.Right));
            var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (item.Key is { Length: > 0 } k) { meta.Children.Add(KeyPill(k, accent, accentLight)); meta.Children.Add(new TextBlock { Text = "клавиша", FontSize = 12, Foreground = muted, VerticalAlignment = VerticalAlignment.Center }); }
            if (fit is { } f)
                meta.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(f.Fits ? Color.Parse("#7FD08A") : Color.Parse("#4A4036")),
                    Child = new TextBlock { Text = f.Fits ? "подходит" : f.Note.Split(" — ")[0], FontSize = 11, FontWeight = FontWeight.SemiBold,
                        Foreground = new SolidColorBrush(f.Fits ? Color.Parse("#0F2A14") : Color.Parse("#E7DCCB")) },
                });
            foot.Children.Add(meta.Docked(Dock.Left));
            detail.Children.Add(foot);
        }
        void Filter()
        {
            var query = (search.Text ?? "").Trim();
            shown = query.Length == 0 ? [.. items]
                : [.. items.Where(i => (i.Title + " " + i.Text.Replace("**", "") + " " + i.Group + " " + string.Join(' ', i.Functions.Select(f => f.Title)))
                    .Contains(query, StringComparison.OrdinalIgnoreCase))];
            Pick(0, redraw: true);
        }
        search.TextChanged += (_, _) => Filter();
        search.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down: Pick(hot + 1, redraw: false); e.Handled = true; break;
                case Key.Up: Pick(hot - 1, redraw: false); e.Handled = true; break;
                case Key.Enter when shown.Count > 0: Go(shown[hot].Open); e.Handled = true; break;
                case Key.Escape: CloseSectionNav(); e.Handled = true; break;
            }
        };
        Filter();

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("224,*") };
        var listScroll = new ScrollViewer
        {
            Content = list, MaxHeight = 420, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var listBorder = new Border { BorderBrush = line, BorderThickness = new Thickness(0, 0, 1, 0), Child = listScroll };
        Grid.SetColumn(detail, 1);
        body.Children.Add(listBorder);
        body.Children.Add(detail);
        var tip = new TextBlock
        {
            Text = "Печать — поиск · ↑↓ — выбор · Enter — открыть · Esc — закрыть", FontSize = 11.5, Foreground = muted, Margin = new Thickness(12, 5),
        };
        var panel = new Border
        {
            Name = "SectionNav", Width = 620, Background = new SolidColorBrush(CardBase),
            BorderBrush = new SolidColorBrush(Mix(Colors.Black, accent, .55)), BorderThickness = new Thickness(1, 2, 1, 1),
            CornerRadius = new CornerRadius(0, 10, 10, 10), BoxShadow = BoxShadows.Parse("0 12 30 0 #CC000000"), ClipToBounds = true,
            Child = new StackPanel { Children = { search, body, new Border { BorderBrush = line, BorderThickness = new Thickness(0, 1, 0, 0), Child = tip } } },
        };
        panel.PointerEntered += (_, _) => { _navOverPanel = true; _navCloseTimer?.Stop(); };
        panel.PointerExited += (_, _) => { _navOverPanel = false; if (!search.IsFocused || string.IsNullOrEmpty(search.Text)) ScheduleNavClose(); };
        return panel;

        void Go(Action action) { CloseSectionNav(); action(); }
    }

    private static Control Glyph(string? glyph, string title, Color colour, double size) => new Border
    {
        Width = size + 4, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = (glyph ?? ItemIcons.GetValueOrDefault(title)) is { } icon
            ? Centered(MapIcons.Stroked(icon, size, new SolidColorBrush(colour)))
            : new TextBlock { Text = title[..1], FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(colour), HorizontalAlignment = HorizontalAlignment.Center },
    };

    private static Control KeyPill(string key, Color accent, Color accentLight) => new Border
    {
        BorderBrush = new SolidColorBrush(Mix(Colors.Transparent, accent, .6)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 0), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
        Child = new TextBlock { Text = key, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(accentLight) },
    };

    /// <summary>Plain text with «**…**» parts in bold, the bold parts in the light text colour.</summary>
    private static TextBlock RichText(string source, IBrush normal, IBrush strong)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 19, Foreground = normal };
        var parts = source.Split("**");
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0)
                block.Inlines!.Add(i % 2 == 1 ? new Run(parts[i]) { FontWeight = FontWeight.SemiBold, Foreground = strong } : new Run(parts[i]));
        return block;
    }

    // ------------------------------------------------------------------ the show

    /// <summary>
    /// Plays the show of <paramref name="kind"/> in <paramref name="image"/>: eight frames drawn by the program's renderer
    /// in the background (once, and again only when what they are drawn with changes), then a frame every 220 ms with the
    /// finished map held for four more. Only the open panel plays.
    /// </summary>
    private void PlayShow(ShowKind kind, Image image)
    {
        var signature = _editor.ShowSignature;
        if (_navFrames.TryGetValue(kind, out var ready) && ready.Signature == signature) { Animate(ready.Frames); return; }
        image.Source = null;
        if (!_navFramesPending.Add(kind)) return;
        var documents = Enumerable.Range(0, EditorViewModel.ShowFrames).Select(i => _editor.ShowFrame(kind, i)).ToArray();
        var stamps = _editor.StampImages;
        var area = new WorldBounds(0, 0, EditorViewModel.ShowWidth, EditorViewModel.ShowHeight);
        UiTasks.Forget(this, () => System.Threading.Tasks.Task.Run(() =>
        {
            var pixels = documents.Select(d => RenderPreviewPixels(d, area, EditorViewModel.ShowWidth, EditorViewModel.ShowHeight, 1, stamps)).ToArray();
            Dispatcher.UIThread.Post(() =>
            {
                _navFramesPending.Remove(kind);
                var frames = pixels.Select(p => ToBitmap(p, EditorViewModel.ShowWidth, EditorViewModel.ShowHeight)).ToArray();
                if (_navFrames.Remove(kind, out var old)) foreach (var bitmap in old.Frames) bitmap.Dispose();
                _navFrames[kind] = (signature, frames);
                // Still the same entry on screen: start it.
                if (image.IsEffectivelyVisible && _navPopup is { IsOpen: true }) Animate(frames);
            });
        }));

        void Animate(Bitmap[] frames)
        {
            var step = 0;
            image.Source = frames[0];
            _navShowTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(220), DispatcherPriority.Background, (_, _) => { });
            _navShowTimer.Stop();
            _navShowTimer.Tick -= _navShowTick;
            _navShowTick = (_, _) =>
            {
                step++;
                image.Source = frames[Math.Min(step % (frames.Length + 4), frames.Length - 1)];
            };
            _navShowTimer.Tick += _navShowTick;
            if (Map.App.Services.UiFeel.EffectsEnabled) _navShowTimer.Start();
            else image.Source = frames[^1];
        }
    }

    private EventHandler _navShowTick = (_, _) => { };

    // ------------------------------------------------------------------ what each section holds

    private IReadOnlyList<NavItem> SectionNavItems(EditorWorkspace workspace) => workspace switch
    {
        EditorWorkspace.Objects => ObjectsNavItems(),
        EditorWorkspace.World => WorldNavItems(),
        EditorWorkspace.Appearance => AppearanceNavItems(),
        _ => DrawNavItems(),
    };

    private void Take(EditorTool tool) => _editor.ActiveTool = tool;

    private IReadOnlyList<NavItem> DrawNavItems()
    {
        var tools = EditorSurfaceLayout.RailGroupsFor(_isHexWorkshop).SelectMany(g => g)
            .Where(e => e.Tool != EditorTool.Select && EditorSurfaceLayout.WorkspaceOf(e.Tool) == EditorWorkspace.Draw)
            .Select(e => e.Tool).Distinct();
        return [.. tools.Select(tool =>
        {
            NavFunction F(string title, Action? extra = null) => new(title, () => { Take(tool); extra?.Invoke(); });
            NavFunction[] functions = tool switch
            {
                EditorTool.Land => [F("Кисть", () => _editor.ApplyBrushPreset(BrushPresetKind.RoundSoft)),
                    F("Прямоугольник", () => _editor.ApplyBrushPreset(BrushPresetKind.Rectangle)),
                    F("Круг", () => _editor.ApplyBrushPreset(BrushPresetKind.Ellipse)),
                    F("Многоугольник", () => _editor.ApplyBrushPreset(BrushPresetKind.Polygon)),
                    F("Стирание (Alt)"), F("Размер и мягкость"), F("Берег"), F("Обрывы ярусов"), F("Осыпь у подножия"), F("Материал"), F("Рельеф")],
                EditorTool.River => [F("Рукой", () => { _editor.RiverDrawMode = RiverDrawMode.Freehand; _editor.StartRiver(); }),
                    F("По точкам", () => { _editor.RiverDrawMode = RiverDrawMode.Nodes; _editor.StartRiver(); }),
                    F("Озеро", _editor.StartLake), F("Рукав"), F("Приток"), F("Готовые стили"), F("Вода и берег"), F("Ширина по течению"), F("Подпись вдоль реки")],
                EditorTool.Road => [F("Рукой", () => { _editor.RoadDrawMode = RoadDrawMode.Freehand; _editor.StartRoad(); }),
                    F("По точкам", () => { _editor.RoadDrawMode = RoadDrawMode.Nodes; _editor.StartRoad(); }),
                    F("Ветвь"), F("Соединение"), F("Замкнуть маршрут"), F("Готовые стили"), F("Ширина и края"), F("Мосты и броды")],
                EditorTool.Line => [F("Рукой", () => _editor.ActiveLineDrawMode = LineDrawMode.FreeDraw),
                    F("Пером", () => _editor.ActiveLineDrawMode = LineDrawMode.Pen), F("Пунктир"), F("Толщина"), F("Цвет")],
                EditorTool.Shape => [F("Рукой", () => _editor.ActiveShapeDrawMode = ShapeDrawMode.FreeDraw),
                    F("Пером", () => _editor.ActiveShapeDrawMode = ShapeDrawMode.Pen), F("Кистью", () => _editor.ActiveShapeDrawMode = ShapeDrawMode.Brush),
                    F("Прямоугольник", () => _editor.ActiveShapeDrawMode = ShapeDrawMode.Rectangle), F("Круг", () => _editor.ActiveShapeDrawMode = ShapeDrawMode.Ellipse),
                    F("Многоугольник", () => _editor.ActiveShapeDrawMode = ShapeDrawMode.Polygon), F("Заливка"), F("Градиент"), F("Обводка")],
                EditorTool.Text => [F("Подпись"), F("Вдоль пути"), F("Шрифт"), F("Размер"), F("Обводка"), F("Тень")],
                EditorTool.Measure => [F("Линейка масштаба"), F("Роза ветров"), F("Легенда"), F("Картуш"), F("Измерить расстояние")],
                EditorTool.Brush => [F("Размер"), F("Мягкость"), F("Непрозрачность"), F("Цвет"), F("Текстура"), F("Ластик")],
                EditorTool.Mask => [F("Скрыть"), F("Вернуть (X)"), F("Размер кисти")],
                _ => [],
            };
            var show = tool == EditorTool.Measure ? null : EditorViewModel.ShowOf(tool);
            return new NavItem(EditorLabels.Tool(tool), "Инструменты", tool == EditorTool.Measure ? MapIcons.ScaleObject : null, ToolShortcut(tool) is { Length: > 0 } k ? k : null,
                NavText(EditorLabels.Tool(tool)) ?? ToolWhat(tool), functions, () => Take(tool), null, show);
        })];
    }

    private IReadOnlyList<NavItem> ObjectsNavItems()
    {
        void Assets(bool search)
        {
            _editor.ActiveTool = EditorTool.Stamp;
            OpenAssetCatalog(focusSearch: search);
            if (_editor.HasChosenAsset) _editor.ArmStampPlacement();
        }
        NavFunction F(string title, bool search = false) => new(title, () => Assets(search));
        var key = ToolShortcut(EditorTool.Stamp) is { Length: > 0 } k ? k : null;
        return
        [
            new("Ассеты", "Каталог", MapIcons.ObjectStamp, key, NavText("Ассеты")!,
                [F("Установка щелчком"), F("Размер W/S"), F("Поворот A/D"), F("Каталог"), F("Роли наборов")], () => Assets(false), null, ShowKind.Assets),
            new("Поиск в каталоге", "Каталог", MapIcons.Select, "F", NavText("Поиск в каталоге")!, [F("Имя", true), F("Тег", true)], () => Assets(true), null, ShowKind.Assets),
            new("Недавние и избранное", "Каталог", MapIcons.Star, null, NavText("Недавние и избранное")!, [F("Недавние"), F("Избранное")], () => Assets(false), null, ShowKind.Assets),
            new("Как ставить", "Расстановка", MapIcons.ObjectStamp, null, NavText("Как ставить")!, [F("По одному"), F("Серия"), F("Россыпь")], () => Assets(false), null, ShowKind.Assets),
            new("Область и путь", "Расстановка", MapIcons.StampSpacing, null, NavText("Область и путь")!, [F("Область"), F("Путь")], () => Assets(false), null, ShowKind.Assets),
        ];
    }

    private IReadOnlyList<NavItem> WorldNavItems()
    {
        var items = _worldEntries.Select(e => new NavItem(e.Title, e.Group, WorldGlyph(e.Name), null,
            NavText(e.Title) ?? UiTips.For(e.Name) ?? "", [.. (NavFunctions.GetValueOrDefault(e.Title) ?? []).Select(f => new NavFunction(f, e.Open))],
            () => { ShowWorldSection(false); e.Open(); }, e.Fit, null)).ToList();
        if (EditorSurfaceLayout.RailGroupsFor(_isHexWorkshop).SelectMany(g => g).Any(x => x.Tool == EditorTool.Notes))
            items.Add(new NavItem("Заметки", "Собрать для игроков", MapIcons.Notes, ToolShortcut(EditorTool.Notes) is { Length: > 0 } k ? k : null,
                NavText("Заметки")!, [.. NavFunctions["Заметки"].Select(f => new NavFunction(f, () => Take(EditorTool.Notes)))], () => Take(EditorTool.Notes), null, null));
        return items;
    }

    private static string? WorldGlyph(string name) => name switch
    {
        "GeographyCheckButton" => MapIcons.Check, "AtlasCheckButton" => MapIcons.Layers, "TimelineButton" => MapIcons.Versions,
        "PlanetButton" => MapIcons.ShapeCircle, "IsometricButton" => MapIcons.ShapeGridBlock, "CityButton" => MapIcons.Objects,
        "PointcrawlButton" => MapIcons.Line, "StarSectorButton" => MapIcons.Star, "BookButton" => MapIcons.File,
        "ScaleSyncButton" => MapIcons.River, "AtlasRefreshButton" => MapIcons.Versions, _ => null,
    };

    private IReadOnlyList<NavItem> AppearanceNavItems()
    {
        NavFunction Card(string title, string key) => new(title, () => { ShowLookPart(PartOf(key.StartsWith("sea-", StringComparison.Ordinal) ? "sea"
            : key.StartsWith("light-", StringComparison.Ordinal) ? "light" : "style"), fine: false); ApplyLook(key); });
        NavFunction Fine(string title) => new(title, () => ShowLookPart(LookPart.Light, fine: true));
        return
        [
            new("Свет", "Вид всей карты", MapIcons.Opacity, null, NavText("Свет")!,
                [.. LightCards.Select(c => Card(c.Title, c.Key)), Fine("Время суток"), Fine("Объём гор"), Fine("Тени"), Fine("Туман в низинах"), Fine("Огни"), Fine("Цвет суток")],
                () => ShowLookPart(LookPart.Light, fine: true), null, ShowKind.SectionAppearance),
            new("Стили", "Вид всей карты", MapIcons.Brush, null, NavText("Стили")!,
                [.. StyleCards.Select(c => Card(c.Title, c.Key))], () => ShowLookPart(LookPart.Styles, fine: false), null, null),
            new("Море", "Вид всей карты", MapIcons.River, null, NavText("Море")!,
                [.. SeaCards.Select(c => Card(c.Manner is null ? "Без моря" : c.Title, c.Key))], () => ShowLookPart(LookPart.Sea, fine: false), null, null),
            new("Сравнить", "Вид всей карты", MapIcons.Layers, null, NavText("Сравнить")!, [],
                () => ShowLookPart(LookPart.Light, fine: false), null, null),
        ];
    }

    // The dry texts of the entries (owner 2026-11-10: facts about the function, no addressing, no analogies, bold key).
    private static string? NavText(string title) => title switch
    {
        "Земля" => "Суша. Режимы: **кисть**, прямоугольник, круг, многоугольник. Alt — стирание. Справа: берег, обрывы, материал.",
        "Река" => "Русло от истока к устью: **рукой** или по точкам. Начало на другом русле — **рукав**, конец на русле или озере — **приток**. Ширина к устью и берега строятся автоматически. Озёра — здесь же.",
        "Дорога" => "Маршрут **рукой** или по точкам. Начало на дороге — **ветвь**, конец на дороге — **соединение**. Мосты и броды у воды — автоматически.",
        "Линия" => "Граница, тропа, маршрут. Режимы: **рукой**, пером по точкам. Справа: пунктир, толщина, цвет.",
        "Фигура" => "Участок с заливкой. Режимы: **рукой**, пером, кистью, прямоугольник, круг, многоугольник. Справа: заливка, градиент, обводка.",
        "Текст" => "Подпись на карте. Может идти **вдоль реки или дороги**. Справа: шрифт, размер, обводка, тень.",
        "Масштаб и украшения" => "**Линейка масштаба, роза ветров, легенда, картуш**; измерение расстояния.",
        "Кисть" => "Краска **отдельным слоем поверх карты**. Справа: размер, мягкость, текстура.",
        "Маска" => "**Скрывает часть слоя без удаления.** X — переключение «скрыть / вернуть».",
        "Ассеты" => "Каталог гор и рисунков. Выбор картинки, **щелчок по карте — установка**. W/S — размер, A/D — поворот.",
        "Поиск в каталоге" => "Поиск ассета по **имени или тегу**.",
        "Недавние и избранное" => "**Последние поставленные** ассеты и отмеченные звёздочкой.",
        "Как ставить" => "Режим установки: **по одному**, **серией**, **россыпью** по ходу мыши.",
        "Область и путь" => "**Область расстановки**: контур заполняется объектами по ролям наборов. **Путь**: объекты вдоль линии.",
        "Найти ошибки" => "Проверка географии: **реки в гору, дороги в обрыв, поселения без дороги**. Переход к каждому месту.",
        "Сверить с подкартами" => "**Расхождения** между этой картой и её подкартами. Только для карты в атласе.",
        "Мир в другой год" => "Эпохи: **объекты, видимые в выбранном году**.",
        "Карта — целая планета" => "Карта как **планета**: шов, полюса, глобус. Для карты 2 : 1.",
        "Посмотреть объёмно" => "Карта **в изометрии**, с четырёх сторон.",
        "Построить город" => "**Генерация или перестройка города**: улицы, дома, стены.",
        "Места и пути для игры" => "Пойнткроул: **места и пути между ними**.",
        "Звёздный сектор" => "**Звёзды и планеты на гексах.** Нужна карта с гексами.",
        "Книга для игроков" => "**Книга-атлас PDF** из карты и подкарт; язык мира.",
        "Перенести реки и дороги" => "**Перенос рек и дорог** между картой и подкартами.",
        "Обновить картинки подкарт" => "**Перерисовка превью подкарт** на этой карте.",
        "Заметки" => "Заметки, **туман**, переходы на другие карты.",
        "Свет" => "**Время суток**: день, вечер, ночь. Огни, тени, туман в низинах.",
        "Стили" => "**Стиль всей карты**. «Без стиля» — исходный вид. Щелчок по стилю — применить, Ctrl+Z — вернуть.",
        "Море" => "**Рисунок открытого моря.** Наведение — проба на всей карте, щелчок — применить.",
        "Сравнить" => "Кнопка «Сравнить» на странице «Вид»: **пока зажата — карта без стиля и света**.",
        _ => null,
    };

    private static readonly Dictionary<string, string[]> NavFunctions = new(StringComparer.Ordinal)
    {
        ["Найти ошибки"] = ["Реки в гору", "Дороги в обрыв", "Поселения без дороги"],
        ["Сверить с подкартами"] = ["Расхождения"],
        ["Мир в другой год"] = ["Ползунок года"],
        ["Карта — целая планета"] = ["Шов", "Полюса", "Глобус", "Проекции"],
        ["Посмотреть объёмно"] = ["Изометрия", "Четыре стороны"],
        ["Построить город"] = ["Генерация", "Перестройка"],
        ["Места и пути для игры"] = ["Места", "Пути"],
        ["Звёздный сектор"] = ["Системы", "Гексы"],
        ["Книга для игроков"] = ["Книга-атлас PDF", "Язык мира"],
        ["Перенести реки и дороги"] = ["Реки", "Дороги"],
        ["Заметки"] = ["Заметка", "Туман", "Переход на карту"],
    };
}
