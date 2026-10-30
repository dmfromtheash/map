using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Map.App.Controls;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>One function inside a section, tab or tool: its name, a short line and, where it depends on the map, whether it fits.</summary>
internal sealed record InsideItem(string Title, string Note, string? Fit = null, bool Fits = true, string? Icon = null, string? Key = null);

internal sealed record InsideGroup(string Title, IReadOnlyList<InsideItem> Items);

/// <summary>
/// The card «Внутри: …» (DEC-070 p. 6, the owner's pick 2026-10-30: «Атлас» with tiles, DEC-080): where the control is,
/// what it does, its key, how to use it and what it holds, grouped, each with a line and - for the world list - whether it
/// fits this map. One shape for sections, tabs and tools; no picture previews (they go stale).
/// </summary>
internal sealed record InsideCard(
    string Title, string? Key, IReadOnlyList<string> Path, string What, Color Accent,
    IReadOnlyList<InsideGroup> Groups, IReadOnlyList<string> Steps, string Action, string? Note = null);

public sealed partial class EditorView
{
    // Section colours of the card, the same in the card and on the owner's mockup page.
    private static readonly Color DrawAccent = Color.Parse("#3FB6A8");
    private static readonly Color ObjectsAccent = Color.Parse("#8FC05A");
    private static readonly Color WorldAccent = Color.Parse("#A08CF0");
    private static readonly Color AppearanceAccent = Color.Parse("#F0985A");
    private static readonly Color ColumnAccent = Color.Parse("#C9A15A");
    private static readonly Color CardBase = Color.Parse("#211912");
    private static readonly Color TileBase = Color.Parse("#241A13");

    private readonly List<(string Group, string Name, string Title, Func<(bool Fits, string Note)> Fit)> _worldEntries = [];

    private static Color AccentOf(EditorWorkspace workspace) => workspace switch
    {
        EditorWorkspace.Objects => ObjectsAccent,
        EditorWorkspace.World => WorldAccent,
        EditorWorkspace.Appearance => AppearanceAccent,
        _ => DrawAccent,
    };

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>
    /// Gives a control the card. The card is rebuilt each time it opens, so its numbers and «подходит» follow the map.
    /// </summary>
    private void AttachInsideCard(Control target, Func<InsideCard?> model, PlacementMode placement)
    {
        var tip = new ToolTip().Classed("inside-card");
        ToolTip.SetTip(target, tip);
        ToolTip.SetShowDelay(target, Ui.TipDelay);
        ToolTip.SetPlacement(target, placement);
        target.AddHandler(ToolTip.ToolTipOpeningEvent, (_, e) =>
        {
            if (model() is { } card) tip.Content = BuildInsideCard(card);
            else e.Handled = true;
        });
    }

    // The program's own glyph for each function a card names; a name without one shows its first letter.
    private static readonly Dictionary<string, string> ItemIcons = new(StringComparer.Ordinal)
    {
        ["Проверка атласа"] = MapIcons.Check, ["Согласование"] = MapIcons.Layers, ["Обновить атлас"] = MapIcons.Versions,
        ["Время"] = MapIcons.Versions, ["Планета"] = MapIcons.ShapeCircle, ["Проверка географии"] = MapIcons.Check,
        ["Пойнткроул"] = MapIcons.Line, ["Город"] = MapIcons.Objects, ["Сектор"] = MapIcons.Star,
        ["Изометрия"] = MapIcons.ShapeGridBlock, ["Книга и язык"] = MapIcons.File,
        ["Земля"] = MapIcons.Land, ["Река"] = MapIcons.River, ["Дорога"] = MapIcons.Road, ["Линия"] = MapIcons.Line,
        ["Фигура"] = MapIcons.Shape, ["Текст"] = MapIcons.Text, ["Подпись"] = MapIcons.Text, ["Кисть"] = MapIcons.Brush,
        ["Маска"] = MapIcons.Mask, ["Гексы"] = MapIcons.Hex, ["Заметки"] = MapIcons.Notes,
        ["Готовые стили"] = MapIcons.Brush, ["Вода и берег"] = MapIcons.River, ["Ширина по течению"] = MapIcons.BrushSize,
        ["Озеро"] = MapIcons.ShapeEllipse, ["Подпись вдоль реки"] = MapIcons.Text, ["Ширина и края"] = MapIcons.BrushSize,
        ["Мосты и броды"] = MapIcons.Road, ["Кисть и форма"] = MapIcons.Brush, ["Берег и обрывы"] = MapIcons.Land,
        ["Материал"] = MapIcons.Texture, ["Рельеф"] = MapIcons.Land, ["Как ставить"] = MapIcons.ObjectStamp,
        ["Область и путь"] = MapIcons.StampSpacing, ["Недавние и избранное"] = MapIcons.Star, ["Шрифт и цвет"] = MapIcons.Color,
        ["Вдоль пути"] = MapIcons.Text, ["Суша"] = MapIcons.LayerLand, ["Вода"] = MapIcons.River, ["Дороги"] = MapIcons.Road,
        ["Объекты"] = MapIcons.ObjectStamp, ["Области расстановки"] = MapIcons.StampSpacing, ["Подписи"] = MapIcons.Text,
        ["Всего"] = MapIcons.Layers, ["Скрытые"] = MapIcons.EyeOff, ["Заблокированные"] = MapIcons.Lock, ["Выбранный"] = MapIcons.Check,
        ["Выделено"] = MapIcons.Select, ["Инструмент"] = MapIcons.Gear, ["Слой, уровень, масштаб и поворот"] = MapIcons.LayerOrder,
        ["Положение и размер"] = MapIcons.Move, ["Свет"] = MapIcons.Opacity, ["Стили"] = MapIcons.Brush, ["Море"] = MapIcons.River,
    };

    private static Control Centered(Control control)
    {
        control.HorizontalAlignment = HorizontalAlignment.Center;
        control.VerticalAlignment = VerticalAlignment.Center;
        return control;
    }

    /// <summary>«3 слоя», «1 река», «5 рек»: the Russian plural by the number.</summary>
    private static string Count(int n, string one, string few, string many)
    {
        var tens = n % 100; var units = n % 10;
        var word = tens is >= 11 and <= 14 ? many : units == 1 ? one : units is >= 2 and <= 4 ? few : many;
        return n.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + word;
    }

    internal static Control BuildInsideCard(InsideCard card)
    {
        var c = card.Accent;
        var white = new SolidColorBrush(Colors.White);
        TextBlock Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) => new()
        {
            Text = text, FontSize = size, Foreground = brush, FontWeight = weight, TextWrapping = TextWrapping.Wrap,
        };

        var crumbs = new WrapPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < card.Path.Count; i++)
        {
            if (i > 0) crumbs.Children.Add(new TextBlock { Text = "›", Margin = new Thickness(5, 0), Foreground = new SolidColorBrush(Color.Parse("#CCFFFFFF")), VerticalAlignment = VerticalAlignment.Center });
            crumbs.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#2E000000")), CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1),
                Child = new TextBlock { Text = card.Path[i], FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#E6FFFFFF")) },
            });
        }
        var title = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (card.Key is { Length: > 0 } key)
            title.Children.Add(new Border
            {
                BorderBrush = white, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 0),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                Child = new TextBlock { Text = key, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = white },
            }.Docked(Dock.Right));
        var name = Text(card.Title, 22, white, FontWeight.Bold);
        if (Application.Current?.TryFindResource("MapDisplayFont", out var font) == true && font is FontFamily family) name.FontFamily = family;
        title.Children.Add(name);
        var header = new Border
        {
            Padding = new Thickness(15, 12, 15, 11),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Mix(Color.Parse("#1A130E"), c, .7), 0), new GradientStop(Mix(Color.Parse("#1A130E"), c, .25), 1) },
            },
            Child = new StackPanel { Children = { crumbs, title, Text(card.What, 14, new SolidColorBrush(Color.Parse("#F5FFFFFF"))) } },
        };

        var accentBrush = new SolidColorBrush(Mix(c, Colors.White, .15));
        var muted = new SolidColorBrush(Color.Parse("#B8AA90"));
        var text = new SolidColorBrush(Color.Parse("#F3E3BF"));
        Control GroupTitle(string caption) => new DockPanel
        {
            Children =
            {
                new TextBlock { Text = caption.ToUpperInvariant(), FontSize = 11.5, FontWeight = FontWeight.Bold, Foreground = accentBrush,
                    LetterSpacing = .8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }.Docked(Dock.Left),
                new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Mix(Colors.Transparent, c, .45)) },
            },
        };
        Border Tile(Control child) => new()
        {
            Background = new SolidColorBrush(Mix(TileBase, c, .11)),
            BorderBrush = new SolidColorBrush(Mix(TileBase, c, .32)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 8), Child = child,
        };

        var body = new StackPanel { Spacing = 12, Margin = new Thickness(15, 12) };
        if (card.Steps.Count > 0)
        {
            var steps = new StackPanel { Spacing = 5 };
            for (var i = 0; i < card.Steps.Count; i++)
                steps.Children.Add(new DockPanel
                {
                    Children =
                    {
                        new Border
                        {
                            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(c), Margin = new Thickness(0, 1, 8, 0),
                            VerticalAlignment = VerticalAlignment.Top,
                            Child = new TextBlock { Text = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 11, FontWeight = FontWeight.Bold,
                                Foreground = new SolidColorBrush(Color.Parse("#14100B")), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                        }.Docked(Dock.Left),
                        Text(card.Steps[i], 13.5, text),
                    },
                });
            body.Children.Add(new StackPanel { Spacing = 6, Children = { GroupTitle("Как пользоваться"), Tile(steps) } });
        }
        foreach (var group in card.Groups)
        {
            // A long card (the world list) goes three across so it stays inside a laptop screen.
            var grid = new UniformGrid { Columns = card.Groups.Sum(g => g.Items.Count) > 8 ? 3 : 2 };
            foreach (var item in group.Items)
            {
                var lines = new StackPanel { Spacing = 1, Children = { Text(item.Title, 13, text, FontWeight.Bold), Text(item.Note, 12, muted) } };
                if (item.Fit is { } fit)
                    lines.Children.Add(new Border
                    {
                        HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0), CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 0),
                        Background = new SolidColorBrush(item.Fits ? Color.Parse("#7FD08A") : Color.Parse("#4A4036")),
                        Child = new TextBlock { Text = fit, FontSize = 11.5, FontWeight = FontWeight.Bold,
                            Foreground = new SolidColorBrush(item.Fits ? Color.Parse("#0F2A14") : Color.Parse("#E7DCCB")) },
                    });
                if (item.Key is { } itemKey)
                    lines.Children.Add(new Border
                    {
                        HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 0),
                        BorderBrush = accentBrush, BorderThickness = new Thickness(1),
                        Child = new TextBlock { Text = "клавиша " + itemKey, FontSize = 11.5, FontWeight = FontWeight.Bold, Foreground = accentBrush },
                    });
                var mark = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Top,
                    Background = new SolidColorBrush(Mix(Colors.Transparent, c, .25)),
                    Child = (item.Icon ?? ItemIcons.GetValueOrDefault(item.Title)) is { } icon
                        ? Centered(MapIcons.Stroked(icon, 15, accentBrush))
                        : new TextBlock { Text = item.Title[..1], FontWeight = FontWeight.Bold, Foreground = accentBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                }.Docked(Dock.Left);
                grid.Children.Add(new Border { Margin = new Thickness(0, 0, 7, 7), Child = Tile(new DockPanel { Children = { mark, lines } }) });
            }
            body.Children.Add(new StackPanel { Spacing = 6, Children = { GroupTitle(group.Title), grid } });
        }

        var footer = new Border
        {
            Padding = new Thickness(15, 9), Background = new SolidColorBrush(Color.Parse("#1A130E")),
            BorderBrush = new SolidColorBrush(Color.Parse("#46372A")), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new DockPanel
            {
                Children =
                {
                    new TextBlock { Text = card.Note ?? "", FontSize = 11.5, Foreground = muted, Margin = new Thickness(12, 0, 0, 0) }.Docked(Dock.Right),
                    new TextBlock { Text = card.Action, FontSize = 12.5, Foreground = muted },
                },
            },
        };

        var items = card.Groups.Sum(g => g.Items.Count);
        return new Border
        {
            Name = "InsideCard",
            Width = items > 8 ? 660 : 460,
            Background = new SolidColorBrush(CardBase),
            BorderBrush = new SolidColorBrush(Mix(Colors.Black, c, .55)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), ClipToBounds = true,
            BoxShadow = BoxShadows.Parse("0 14 34 0 #CC000000"),
            Child = new StackPanel { Children = { header, body, footer } },
        };
    }

    // ---------------------------------------------------------------- what the cards say

    private InsideCard WorkspaceCard(EditorWorkspace workspace)
    {
        var title = SectionTitle(workspace);
        if (workspace == EditorWorkspace.World)
        {
            var groups = _worldEntries.GroupBy(e => e.Group).Select(g => new InsideGroup(g.Key, g.Select(e =>
            {
                var (fits, note) = e.Fit();
                // The pill keeps the reason short: «Эта карта пока не в атласе — проверять нечего» → before the dash.
                var reason = note.Split(" — ")[0];
                return new InsideItem(e.Title, UiTips.For(e.Name) ?? "", fits ? "подходит" : reason, fits);
            }).ToList())).ToList();
            return new(title, null, ["Разделы", title], "Всё про мир целиком: атлас подкарт, время, планета и особые виды карты.",
                WorldAccent, groups, [], "Щелчок — открыть список", Count(_worldEntries.Count, "функция", "функции", "функций"));
        }
        if (workspace == EditorWorkspace.Appearance)
            return new(title, null, ["Разделы", title], "Как выглядит вся карта: свет, стиль и море. Лента открывается под картой.",
                AppearanceAccent,
                [new("Лента под картой", [
                    new("Свет", "Время суток, огни и тени"), new("Стили", "Вся карта в другой технике"),
                    new("Море", "Как нарисована вода новых карт")])],
                [], "Щелчок — открыть ленту");
        var tools = EditorSurfaceLayout.RailGroupsFor(_isHexWorkshop).SelectMany(g => g)
            .Where(e => e.Tool != EditorTool.Select && EditorSurfaceLayout.WorkspaceOf(e.Tool) == workspace)
            .Select(e => new InsideItem(EditorLabels.Tool(e.Tool), ToolWhat(e.Tool), Key: ToolShortcut(e.Tool) is { Length: > 0 } k ? k : null))
            .ToList();
        var what = workspace == EditorWorkspace.Objects
            ? "Ваши горы и другие рисунки: каталог и расстановка — по одному, серией, областью или путём."
            : "Земля, вода, дороги, линии, фигуры, подписи, кисть и маска.";
        return new(title, null, ["Разделы", title], what, AccentOf(workspace), [new("Инструменты раздела", tools)], [], "Щелчок — открыть раздел");
    }

    private InsideCard ToolCard(EditorTool tool)
    {
        var section = EditorSurfaceLayout.WorkspaceOf(tool);
        var (steps, inside) = ToolInside(tool);
        return new(tool == EditorTool.Stamp ? "Ассеты" : EditorLabels.Tool(tool), ToolShortcut(tool), [SectionTitle(section), EditorLabels.Tool(tool)],
            ToolWhat(tool), AccentOf(section), inside.Count == 0 ? [] : [new("В панели справа", inside)], steps,
            tool == EditorTool.Stamp ? "Щелчок — открыть каталог" : "Щелчок — взять инструмент", ToolNote(tool));
    }

    private InsideCard RightTabCard(RightColumnTab tab)
    {
        var document = _editor.Session.Document;
        var objects = document.Layers.OfType<ObjectLayer>().ToList();
        string N(int n, string one, string few, string many) => Count(n, one, few, many);
        return tab switch
        {
            RightColumnTab.Content => new("Содержимое", null, ["Правая колонка", "Содержимое"],
                "Всё, что лежит на карте, по разделам. Щелчок по строке выделяет это на карте.", ColumnAccent,
                [new("Что на карте", [
                    new("Суша", N(document.Layers.OfType<LandLayer>().Count(), "слой", "слоя", "слоёв") + " · уровень, порядок, видимость"),
                    new("Вода", N(objects.Sum(l => l.Rivers.Count(r => !r.Channels.All(ch => ch.Closed))), "река", "реки", "рек")
                        + ", " + N(objects.Sum(l => l.Rivers.Sum(r => r.Channels.Count(ch => ch.Closed))), "озеро", "озера", "озёр")),
                    new("Дороги", N(objects.Sum(l => l.Roads.Length), "дорога", "дороги", "дорог")),
                    new("Объекты", N(objects.Sum(l => l.Objects.Count(o => o.Text is null)), "объект", "объекта", "объектов")),
                    new("Области расстановки", N(objects.Sum(l => l.PlacementAreas.Length + l.PlacementPaths.Length), "область или путь", "области или пути", "областей и путей")),
                    new("Подписи", N(objects.Sum(l => l.Objects.Count(o => o.Text is not null)), "подпись", "подписи", "подписей"))])],
                [], "Щелчок — открыть вкладку", "на этой карте"),
            RightColumnTab.Layers => new("Слои", null, ["Правая колонка", "Слои"],
                "Слои карты снизу вверх: порядок, видимость, блокировка и свойства выбранного слоя.", ColumnAccent,
                [new("Слои этой карты", [
                    new("Всего", N(document.Layers.Count, "слой", "слоя", "слоёв")),
                    new("Скрытые", N(document.Layers.Count(l => !l.Visible), "слой", "слоя", "слоёв")),
                    new("Заблокированные", N(document.Layers.Count(l => l.Locked), "слой", "слоя", "слоёв")),
                    new("Выбранный", _editor.ActiveLayer?.Name ?? "—")])],
                [], "Щелчок — открыть вкладку"),
            _ => new("Свойства", null, ["Правая колонка", "Свойства"],
                "Что сейчас изменится: выделенное на карте или следующий инструмент.", ColumnAccent,
                [new("Сейчас здесь", [
                    new("Выделено", _editor.SelectionCount > 0 ? N(_editor.SelectionCount, "объект", "объекта", "объектов") : "ничего"),
                    new("Инструмент", EditorLabels.Tool(_editor.ActiveTool)),
                    new("Слой, уровень, масштаб и поворот", "сверху, когда выделен объект"),
                    new("Положение и размер", "ширина, высота, координаты")])],
                [], "Щелчок — открыть вкладку"),
        };
    }

    private static string ToolWhat(EditorTool tool) => tool switch
    {
        EditorTool.Select => "Выделить, двигать, поворачивать и менять размер того, что на карте.",
        EditorTool.Land => "Рисует сушу кистью или формой; берег, обрывы и материал — в панели.",
        EditorTool.River => "Рисует реку по точкам. Ширина растёт к устью, берега и отмели рисуются сами.",
        EditorTool.Road => "Ставит дорогу по точкам, с развилками; мосты и броды появляются у воды сами.",
        EditorTool.Line => "Линия или граница по точкам: пунктир, толщина, цвет.",
        EditorTool.Shape => "Фигура: многоугольник, круг, заливка и градиент.",
        EditorTool.Text => "Подпись на карте; может идти вдоль реки или дороги.",
        EditorTool.Brush => "Рисует краской поверх карты.",
        EditorTool.Mask => "Скрывает или возвращает часть выбранного слоя.",
        EditorTool.Measure => "Линейка, масштаб, роза ветров, легенда и картуш.",
        EditorTool.Notes => "Заметки, карточки мира, туман и переходы.",
        EditorTool.Hex => "Гексы: местность, дороги и реки по клеткам.",
        EditorTool.Stamp => "Каталог ваших гор и рисунков; выбранный ассет ставится щелчком по карте.",
        _ => EditorLabels.Tool(tool),
    };

    private (IReadOnlyList<string> Steps, IReadOnlyList<InsideItem> Inside) ToolInside(EditorTool tool) => tool switch
    {
        EditorTool.River => (["Щёлкайте по карте от истока к устью", "Enter — закончить, Esc — отменить", "Тяните точку, чтобы поправить русло"],
            [new("Готовые стили", "«Живая» или «Живописная»", Icon: MapIcons.River), new("Вода и берег", "Цвет, материал, блики, откос"),
             new("Ширина по течению", "Растёт по притокам"), new("Озеро", "Обвести по точкам"), new("Подпись вдоль реки", "Название по руслу")]),
        EditorTool.Road => (["Щёлкайте по карте точками", "Щелчок по дороге — развилка", "Enter — закончить, Esc — отменить"],
            [new("Готовые стили", "Тропа, просёлок, тракт"), new("Ширина и края", "Обочины, колеи, камни"), new("Мосты и броды", "Сами у воды")]),
        EditorTool.Land => (["Выберите кисть или форму", "Рисуйте по карте; Alt — стирать"],
            [new("Кисть и форма", "Размер, мягкость, многоугольник"), new("Берег и обрывы", "Ярусы суши по уровням"),
             new("Материал", "Ваш снег, лёд и акварель"), new("Рельеф", "Холмы, штрихи на крутизне")]),
        EditorTool.Stamp => (["Выберите рисунок в каталоге", "Щёлкните по карте — он встанет"],
            [new("Как ставить", "Один, серия, россыпь"), new("Область и путь", "Лес, хребет, поселение"),
             new("Недавние и избранное", "Под рукой без каталога")]),
        EditorTool.Text => (["Щёлкните по карте и пишите", "Тяните, чтобы подвинуть"], [new("Шрифт и цвет", "Размер, обводка, тень"), new("Вдоль пути", "По реке или дороге")]),
        _ => ([], []),
    };

    private string? ToolNote(EditorTool tool)
    {
        var objects = _editor.Session.Document.Layers.OfType<ObjectLayer>().ToList();
        return tool switch
        {
            EditorTool.River => "на этой карте: " + Count(objects.Sum(l => l.Rivers.Length), "река", "реки", "рек"),
            EditorTool.Road => "на этой карте: " + Count(objects.Sum(l => l.Roads.Length), "дорога", "дороги", "дорог"),
            _ => null,
        };
    }
}
