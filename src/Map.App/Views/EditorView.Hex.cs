using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Map.App.Controls;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.App.Views;

/// <summary>
/// The tool panel of the hex constructor, in four tabs that answer "what am I doing":
/// <b>Рисовать</b> (put tiles down: place, fill, erase, pick - the palette, the tile's variant
/// and turn), <b>Разметка</b> (what the game master lays over the ground: regions, paths, fog,
/// hex keys, the travel ruler), <b>Правка</b> (selection, the clipboard, rows and columns) and
/// <b>Мир</b> (the whole map: board size, levels, grid, generator, export). A tab shows its click
/// modes in one row of icons, then the footprint where it applies, then only the settings of the
/// chosen mode in coloured categories (green makes, blue selects, amber is shape, violet is look).
/// A hidden part names the button that opens it in its Tag (<c>HexReveal:&lt;name&gt;</c>), so
/// automation can reach any control the way a user would.
/// </summary>
public sealed partial class EditorView
{
    private static readonly Dictionary<(string Id, int Variant), Bitmap> HexThumbnails = [];

    private readonly Dictionary<string, ToggleButton> _hexTileButtons = new(StringComparer.Ordinal);

    // Each sync runs when one of its properties changes (or on every document change when it names none).
    private readonly List<(string[] Properties, Action Sync)> _hexSyncs = [];
    private bool _syncingHex;

    /// <summary>The four tabs of the hex panel.</summary>
    private enum HexPage { Draw, Markup, Edit, World, MySets }

    private HexPage _hexPage;
    private HexToolMode? _hexShownMode;
    private Action? _hexShowPage;
    private readonly Dictionary<HexPage, HexToolMode> _hexPageMode = new()
    {
        [HexPage.Draw] = HexToolMode.Place, [HexPage.Markup] = HexToolMode.Region, [HexPage.Edit] = HexToolMode.Select,
    };

    private static HexPage PageOf(HexToolMode mode) => mode switch
    {
        HexToolMode.Place or HexToolMode.Fill or HexToolMode.Erase or HexToolMode.Pick => HexPage.Draw,
        HexToolMode.Select => HexPage.Edit,
        _ => HexPage.Markup,
    };

    private static string HexModeName(HexToolMode mode) => mode switch
    {
        HexToolMode.Place => "Поставить", HexToolMode.Fill => "Залить", HexToolMode.Erase => "Стереть",
        HexToolMode.Pick => "Пипетка", HexToolMode.Region => "Регионы", HexToolMode.Path => "Пути",
        HexToolMode.Party => "Партия", HexToolMode.Fog => "Туман", HexToolMode.Key => "Ключи", HexToolMode.Measure => "Путешествие",
        HexToolMode.Protect => "Защита",
        _ => "Выделение",
    };

    /// <summary>A painted hex tile as a bitmap for the palette; shared by every editor window.</summary>
    private static Bitmap HexThumbnail(string id, int variant)
    {
        if (HexThumbnails.TryGetValue((id, variant), out var cached)) return cached;
        var image = HexTileArt.Image(id, variant, HexOrientation.Pointy);
        using var data = image?.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data?.ToArray() ?? []);
        var bitmap = data is null ? new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96)) : new Bitmap(stream);
        HexThumbnails[(id, variant)] = bitmap;
        return bitmap;
    }

    private void HexSync(Action sync, params string[] properties) => _hexSyncs.Add((properties, sync));

    private static readonly string[] DocumentChanged = [nameof(EditorViewModel.CanUndo), nameof(EditorViewModel.HexLevel)];

    // ------------------------------------------------------------------ small building blocks

    /// <summary>A square icon button with its name as tooltip and accessible name.</summary>
    private static Button HexIcon(string name, string tip, string icon, Action action)
    {
        var button = new Button
        {
            Content = MapIcons.Grid24(icon, 16), Width = 30, Height = 28, MinHeight = 28, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        }.Named(name).Classed("compact");
        AutomationProperties.SetName(button, tip);
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>A square icon toggle (the dice of "random", the brush of a region).</summary>
    private static ToggleButton HexIconToggle(string name, string tip, string icon)
    {
        var toggle = new ToggleButton
        {
            Content = MapIcons.Grid24(icon, 16), Width = 30, Height = 28, MinHeight = 28, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        }.Named(name).Classed("compact", "tile-choice");
        AutomationProperties.SetName(toggle, tip);
        ToolTip.SetTip(toggle, tip);
        return toggle;
    }

    /// <summary>A command with an icon before its caption, filling its cell.</summary>
    private static Button HexCommand(string name, string text, string icon, Action action, string style = "compact")
    {
        var button = Ui.CommandButton(name, text, icon, action, style);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        return button;
    }

    /// <summary>A compact command with its icon over a short caption, so three or four fit in a row.</summary>
    private static Button HexTall(string name, string text, string icon, Action action, string? tip = null)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = MapIcons.Grid24(icon, 17) },
                    new TextBlock { Text = text, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            },
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(2, 4), MinHeight = 44,
        }.Named(name).Classed("compact");
        AutomationProperties.SetName(button, tip ?? text);
        ToolTip.SetTip(button, tip ?? text);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>Equal cells in rows of <paramref name="columns"/>: buttons that belong together.</summary>
    private static Ui.ChoiceGrid HexGrid(int columns, params Control[] children)
    {
        var grid = new Ui.ChoiceGrid { MaxColumns = columns, MinimumItemWidth = 56, Gap = 4 };
        foreach (var child in children) grid.Children.Add(child);
        return grid;
    }

    /// <summary>A short label on the left and its editors on the right, sized for the narrow panel.</summary>
    private static Control HexRow(string label, params Control[] editors)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("72,*"), ColumnSpacing = 8 };
        var caption = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }.Classed("faint");
        var field = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var editor in editors) field.Children.Add(editor);
        Grid.SetColumn(field, 1);
        row.Children.Add(caption);
        row.Children.Add(field);
        return row;
    }

    /// <summary>One line of guidance with an info glyph; the long story lives in tooltips.</summary>
    private static Control HexHint(TextBlock text)
    {
        text.TextWrapping = TextWrapping.Wrap;
        text.Classes.Add("faint");
        text.Classes.Add("role-caption");
        var glyph = new Border { Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Top, Child = MapIcons.Grid24(MapIcons.LineInfo, 13) };
        return new DockPanel { Children = { glyph.Docked(Dock.Left), text } };
    }

    private static Control HexHint(string text) => HexHint(new TextBlock { Text = text });

    /// <summary>Marks the active choices of a segmented strip.</summary>
    private static void HexMark(IEnumerable<Button> buttons, string activeName)
    {
        foreach (var button in buttons.OfType<ToggleButton>())
        {
            var active = button.Name == activeName;
            button.IsChecked = active;
            Ui.SetClass(button, "active", active);
        }
    }

    // ------------------------------------------------------------------ the panel

    private Control BuildHexPanel()
    {
        var summary = new TextBlock { Name = "HexBoardSummary", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        HexSync(() => summary.Text = _editor.HexBoardSummary, [.. DocumentChanged, nameof(EditorViewModel.HexBoardSummary)]);

        var (tabs, tabButtons) = Ui.TallSegmented("HexTab",
        [
            (nameof(HexPage.Draw), "Рисовать", MapIcons.LineBrush),
            (nameof(HexPage.Markup), "Разметка", MapIcons.HexRegion),
            (nameof(HexPage.Edit), "Правка", MapIcons.HexSelectBox),
            (nameof(HexPage.World), "Мир", MapIcons.HexGlobe),
            (nameof(HexPage.MySets), "Мои наборы", MapIcons.HexPlace),
        ], key => ShowHexPage(Enum.Parse<HexPage>(key)));
        ToolTip.SetTip(tabButtons[0], "Рисовать: ставить, заливать, стирать гексы и брать их пипеткой");
        ToolTip.SetTip(tabButtons[1], "Разметка мастера: регионы, пути, туман войны, ключи гексов, линейка");
        ToolTip.SetTip(tabButtons[2], "Правка: выделение, копирование и вставка, ряды и колонки");
        ToolTip.SetTip(tabButtons[3], "Мир: размер поля, уровни, сетка, генератор, экспорт и печать");

        var (drawModes, drawButtons) = Ui.TallSegmented("HexMode",
        [
            (nameof(HexToolMode.Place), "Поставить", MapIcons.HexPlace),
            (nameof(HexToolMode.Fill), "Залить", MapIcons.HexFill),
            (nameof(HexToolMode.Erase), "Стереть", MapIcons.HexErase),
            (nameof(HexToolMode.Pick), "Пипетка", MapIcons.HexPick),
        ], key => _editor.HexMode = Enum.Parse<HexToolMode>(key));
        var (markupModes, markupButtons) = Ui.TallSegmented("HexMode",
        [
            (nameof(HexToolMode.Region), "Регионы", MapIcons.HexRegion),
            (nameof(HexToolMode.Path), "Пути", MapIcons.ModePath),
            (nameof(HexToolMode.Fog), "Туман", MapIcons.HexFog),
            (nameof(HexToolMode.Party), "Партия", MapIcons.HexPlayer),
            (nameof(HexToolMode.Key), "Ключи", MapIcons.HexKey),
            (nameof(HexToolMode.Measure), "Путь", MapIcons.HexRuler),
        ], key => _editor.HexMode = Enum.Parse<HexToolMode>(key));
        // Selection is the only click mode of «Правка»: the tab itself turns it on.
        var editHead = new Border();

        var modeHint = new TextBlock { Name = "HexModeHint" };
        HexSync(() => modeHint.Text = _editor.HexMode switch
        {
            HexToolMode.Place => "Щелчок — гекс, протягивание — линия. Ctrl+щелчок по тайлам — смесь, [ ] — поворот.",
            HexToolMode.Fill => "Щелчок заливает связную область одной местности (или пустоту).",
            HexToolMode.Erase => "Снимает верх гекса: сначала надстройку, затем местность.",
            HexToolMode.Pick => "Щелчок по гексу берёт его тайл, вариант и поворот.",
            HexToolMode.Region => "Протягивание добавляет гексы в выбранный регион; границы рисуются сами.",
            HexToolMode.Path => "Протяните путь по гексам. Дорога через реку даёт мост.",
            HexToolMode.Fog => "Протягивание открывает гексы игрокам или снова скрывает. Мастер видит сквозь туман.",
            HexToolMode.Party => "Щелчок перемещает партию. Маркер можно перетаскивать; каждый шаг раскрывает обзор.",
            HexToolMode.Key => "Щелчок по гексу открывает его ключ.",
            HexToolMode.Measure => "Протяните от гекса к гексу: путь в гексах, милях и днях.",
            _ => "Рамка выделяет гексы, Shift — добавить. Ctrl+C / X / V, Delete, [ ] — поворот вставки, M — отражение.",
        }, nameof(EditorViewModel.HexMode));
        var hint = HexHint(modeHint);
        var worldHint = new TextBlock();
        HexSync(() => worldHint.Text = $"Настройки всей карты. Щелчок по карте сейчас: «{HexModeName(_editor.HexMode)}».",
            nameof(EditorViewModel.HexMode));

        // The footprint: shared by place and erase (Рисовать) and by regions and fog (Разметка).
        var radius = ValueRow("HexRadiusSlider", "HexRadiusBox", null, "Площадка", 0, HexEditing.MaxRadius, "кольца",
            () => _editor.HexRadius, (value, _) => _editor.HexRadius = (int)value, 1, 0);
        var footprint = new TextBlock { Name = "HexFootprintText" }.Classed("faint", "role-caption");
        HexSync(() => footprint.Text = "Один щелчок: " + _editor.HexFootprintText, nameof(EditorViewModel.HexRadius), nameof(EditorViewModel.HexFootprintText));
        var radiusBlock = new StackPanel { Name = "HexFootprintBlock", Spacing = 2, Children = { radius, footprint } };

        var drawBody = BuildHexTerrainSections();
        var region = BuildHexRegionSection();
        var paths = BuildHexPathSection();
        var fog = BuildHexFogSection();
        var party = BuildHexPartySection();
        var key = BuildHexKeySection();
        var measure = BuildHexMeasureSection();
        var editBody = BuildHexSelectSection();
        var worldBody = new StackPanel
        {
            Spacing = 8,
            Children = { BuildHexBoardSection(), BuildHexLevelSection(), BuildHexGridSection(), BuildHexGeneratorSection(), BuildHexExportSection() },
        };

        var heads = new Dictionary<HexPage, Control>
        {
            [HexPage.Draw] = drawModes, [HexPage.Markup] = markupModes, [HexPage.Edit] = editHead, [HexPage.World] = HexHint(worldHint),
            [HexPage.MySets] = new Border(),
        };
        var bodies = new Dictionary<HexPage, Control>
        {
            [HexPage.Draw] = drawBody,
            [HexPage.Markup] = new StackPanel { Spacing = 8, Children = { region, paths, fog, key, measure, party } },
            [HexPage.Edit] = editBody,
            [HexPage.World] = worldBody,
            [HexPage.MySets] = BuildHexUserSets(),
        };
        foreach (var (page, control) in heads.Concat(bodies)) control.Tag = "HexReveal:HexTab" + page;

        void ShowPage()
        {
            var mode = _editor.HexMode;
            HexMark(tabButtons, "HexTab" + _hexPage);
            HexMark(drawButtons.Concat(markupButtons), "HexMode" + mode);
            foreach (var (page, control) in heads) control.IsVisible = page == _hexPage && page != HexPage.Edit;
            foreach (var (page, control) in bodies) control.IsVisible = page == _hexPage;
            hint.IsVisible = _hexPage is not (HexPage.World or HexPage.MySets);
            radiusBlock.IsVisible = _hexPage is not (HexPage.World or HexPage.MySets)
                && mode is HexToolMode.Place or HexToolMode.Erase or HexToolMode.Region or HexToolMode.Fog or HexToolMode.Protect;
            region.IsVisible = mode == HexToolMode.Region;
            paths.IsVisible = mode == HexToolMode.Path;
            fog.IsVisible = mode == HexToolMode.Fog;
            party.IsVisible = mode == HexToolMode.Party;
            key.IsVisible = mode == HexToolMode.Key;
            measure.IsVisible = mode == HexToolMode.Measure;
        }

        _hexShowPage = ShowPage;
        HexSync(() =>
        {
            var mode = _editor.HexMode;
            _hexPageMode[PageOf(mode)] = mode;
            // A new mode (from a tab, a mode button or a key) shows its tab; other refreshes keep «Мир» open.
            if (_hexShownMode != mode) _hexPage = PageOf(mode);
            _hexShownMode = mode;
            ShowPage();
        }, nameof(EditorViewModel.HexMode));

        var panel = new StackPanel
        {
            Name = "HexToolPanel",
            Spacing = 10,
            Children =
            {
                summary,
                BuildHexPresentationChoice(),
                tabs,
                drawModes,
                markupModes,
                editHead,
                heads[HexPage.World],
                hint,
                radiusBlock,
                drawBody,
                bodies[HexPage.Markup],
                editBody,
                worldBody,
                heads[HexPage.MySets],
                bodies[HexPage.MySets],
            },
        };

        ((INotifyPropertyChanged)_editor).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } name && (name.StartsWith("Hex", StringComparison.Ordinal)
                || name is nameof(EditorViewModel.CanUndo) or nameof(EditorViewModel.SelectedHexPath)
                    or nameof(EditorViewModel.ExportHexLevel) or nameof(EditorViewModel.ExportHexPlayers)))
            {
                SyncHexPanel(name);
            }
        };
        SyncHexPanel(null);
        return panel;
    }

    /// <summary>A tab click: the tab's last click mode comes back (Мир keeps the current one).</summary>
    private void ShowHexPage(HexPage page)
    {
        _hexPage = page;
        if (page is not (HexPage.World or HexPage.MySets) && PageOf(_editor.HexMode) != page)
        {
            _editor.HexMode = _hexPageMode[page];
            return;
        }

        _hexShowPage?.Invoke();
    }

    // ------------------------------------------------------------------ Рисовать: tile, variant and turn

    private Control BuildHexTerrainSections()
    {
        // The palette in two halves: ground tiles, and what stands on them.
        Action<bool>? showKind = null;
        var (kinds, kindButtons) = Ui.Segmented("HexPaletteKind",
        [
            ("Terrain", "Местность", null),
            ("Feature", "Надстройки", null),
        ], key => showKind?.Invoke(key == "Feature"));
        var terrainGroups = new StackPanel { Name = "HexPaletteTerrain", Spacing = 6, Tag = "HexReveal:HexPaletteKindTerrain" };
        var featureGroups = new StackPanel { Name = "HexPaletteFeature", Spacing = 6, Tag = "HexReveal:HexPaletteKindFeature" };
        foreach (var group in _editor.HexTiles.GroupBy(tile => tile.Group))
        {
            var grid = new UniformGrid { Columns = 4 };
            foreach (var tile in group) grid.Children.Add(HexTileButton(tile));
            var target = group.First().Target == HexPaintTarget.Feature ? featureGroups : terrainGroups;
            target.Children.Add(Ui.Subgroup(group.Key, grid));
        }

        var showFeatures = false;
        void ShowKind(bool features)
        {
            showFeatures = features;
            terrainGroups.IsVisible = !features;
            featureGroups.IsVisible = features;
            HexMark(kindButtons, features ? "HexPaletteKindFeature" : "HexPaletteKindTerrain");
        }

        showKind = ShowKind;
        ShowKind(false);
        var palette = new StackPanel { Name = "HexPalette", Spacing = 8, Children = { kinds, terrainGroups, featureGroups } };

        HexSync(() =>
        {
            var mixed = _editor.HexMix.Select(entry => entry.TileId).ToHashSet(StringComparer.Ordinal);
            foreach (var (id, button) in _hexTileButtons)
            {
                var active = mixed.Count > 0 ? mixed.Contains(id) : id == _editor.HexTileId;
                button.IsChecked = active;
                Ui.SetClass(button, "active", active);
            }

            // The chosen tile's half of the palette is the one shown.
            var feature = _editor.HexTile.Target == HexPaintTarget.Feature;
            if (feature != showFeatures && mixed.Count == 0) ShowKind(feature);
        }, nameof(EditorViewModel.HexTileId), nameof(EditorViewModel.HexMix));

        var current = new TextBlock { Name = "HexCurrentTile", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        var currentNote = new TextBlock { Name = "HexCurrentTileNote", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        HexSync(() =>
        {
            var tile = _editor.HexTile;
            current.Text = _editor.HexMix.Count > 0 ? $"Смесь из {_editor.HexMix.Count} тайлов" : tile.Name;
            currentNote.Text = (tile.Target == HexPaintTarget.Feature ? "Надстройка — ставится на местность" : "Местность")
                + (tile.Overhang > 0 ? " · высокий, нависает" : "")
                + (tile.Hint is { } hint ? " · " + hint : "");
        }, nameof(EditorViewModel.HexTileId), nameof(EditorViewModel.HexTile), nameof(EditorViewModel.HexMix));

        var mix = new StackPanel { Name = "HexMixList", Spacing = 4 };
        HexSync(() =>
        {
            mix.Children.Clear();
            mix.IsVisible = _editor.HexMix.Count > 0;
            if (_editor.HexMix.Count == 0) return;
            var rows = new StackPanel { Spacing = 2 };
            foreach (var entry in _editor.HexMix)
            {
                var id = entry.TileId;
                var name = HexStarterTiles.Find(id)?.Name ?? id;
                rows.Children.Add(Ui.SliderRow("HexMixWeight_" + id[(id.IndexOf('/') + 1)..], name, 0, 10, entry.Weight, "",
                    value => { if (!_syncingHex) _editor.SetHexMixWeight(id, value); }));
            }

            mix.Children.Add(Ui.Subgroup("Смесь: вес каждого тайла", rows));
            mix.Children.Add(HexCommand("HexMixClearButton", "Убрать смесь", MapIcons.Close, _editor.ClearHexMix));
        }, nameof(EditorViewModel.HexMix));

        // Variant: the dice (a random picture per hex), the pictures themselves, a reshuffle.
        var random = HexIconToggle("HexRandomVariantToggle", "Случайный вариант на каждый гекс", MapIcons.HexDice);
        random.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.HexRandomVariant = random.IsChecked == true; };
        var shuffle = HexIcon("HexShuffleButton", "Перемешать варианты: следующие гексы получат другие случайные картинки (и новый узор живой местности); поставленные не меняются.",
            MapIcons.LineReset, _editor.ShuffleHexVariants);
        var variants = new StackPanel { Name = "HexVariantStrip", Orientation = Orientation.Horizontal, Spacing = 4 };
        HexSync(() =>
        {
            random.IsChecked = _editor.HexRandomVariant;
            Ui.SetClass(random, "active", _editor.HexRandomVariant);
            variants.Children.Clear();
            var tile = _editor.HexTile;
            for (var i = 0; i < tile.VariantCount; i++)
            {
                var variant = i;
                var chosen = !_editor.HexRandomVariant && _editor.HexFixedVariant == variant;
                var button = new ToggleButton
                {
                    Width = 30, Height = 28, MinHeight = 28, Padding = new Thickness(1),
                    Content = new Image { Source = HexThumbnail(tile.Id, variant), Width = 22, Height = 24 },
                    IsChecked = chosen,
                }.Named("HexVariant" + variant).Classed("compact", "tile-choice");
                Ui.SetClass(button, "active", chosen);
                AutomationProperties.SetName(button, $"Вариант {variant + 1}");
                ToolTip.SetTip(button, $"Вариант {variant + 1}: ставить только эту картинку");
                button.Click += (_, _) =>
                {
                    _editor.HexRandomVariant = false;
                    _editor.HexFixedVariant = variant;
                };
                variants.Children.Add(button);
            }
        }, nameof(EditorViewModel.HexTileId), nameof(EditorViewModel.HexTile), nameof(EditorViewModel.HexRandomVariant), nameof(EditorViewModel.HexFixedVariant));

        // Turn: 60° steps either way, the angle between them, the dice for a random turn.
        var rotation = new TextBlock { Name = "HexRotationText", VerticalAlignment = VerticalAlignment.Center, MinWidth = 52, TextAlignment = Avalonia.Media.TextAlignment.Center };
        var left = HexIcon("HexRotateLeftButton", "Повернуть на 60° против часовой ( [ )", MapIcons.HexTurnLeft, () => _editor.RotateHexBrush(-1));
        var right = HexIcon("HexRotateRightButton", "Повернуть на 60° по часовой ( ] )", MapIcons.HexTurnRight, () => _editor.RotateHexBrush(1));
        var randomTurn = HexIconToggle("HexRandomRotationToggle", "Случайный поворот на каждый гекс", MapIcons.HexDice);
        randomTurn.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.HexRandomRotation = randomTurn.IsChecked == true; };
        var turnNote = new TextBlock { Name = "HexTurnNote", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        HexSync(() =>
        {
            var rotatable = _editor.HexTile.Rotatable;
            rotation.Text = !rotatable ? "—" : _editor.HexRandomRotation ? "случайно" : $"{_editor.HexRotation * 60}°";
            randomTurn.IsChecked = _editor.HexRandomRotation;
            Ui.SetClass(randomTurn, "active", _editor.HexRandomRotation);
            left.IsEnabled = right.IsEnabled = randomTurn.IsEnabled = rotatable;
            turnNote.IsVisible = !rotatable;
            turnNote.Text = $"«{_editor.HexTile.Name}» не поворачивается: у тайла есть верх.";
        }, nameof(EditorViewModel.HexTileId), nameof(EditorViewModel.HexTile), nameof(EditorViewModel.HexRotation), nameof(EditorViewModel.HexRandomRotation));

        var look = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                HexRow("Вариант", random, variants, shuffle),
                HexRow("Поворот", left, rotation, right, randomTurn),
                turnNote,
            },
        };

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Category("HexTileCategory", "Тайл", "accent-create", new StackPanel
                {
                    Spacing = 6, Children = { new StackPanel { Spacing = 1, Children = { current, currentNote } }, mix, palette },
                }),
                Ui.Category("HexTileLookCategory", "Вариант и поворот", "accent-look", look),
            },
        };
    }

    private ToggleButton HexTileButton(HexTileDefinition tile)
    {
        var button = new ToggleButton
        {
            Height = 78, Padding = new Thickness(1, 2), Margin = new Thickness(0, 0, 3, 3), HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    new Image { Source = HexThumbnail(tile.Id, 0), Width = 40, Height = 46, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock
                    {
                        // A one-word name never breaks inside the word.
                        Text = tile.Name, TextAlignment = Avalonia.Media.TextAlignment.Center,
                        FontSize = !tile.Name.Contains(' ', StringComparison.Ordinal) && tile.Name.Length >= 10 ? 9 : 10,
                        TextWrapping = tile.Name.Contains(' ', StringComparison.Ordinal) ? TextWrapping.Wrap : TextWrapping.NoWrap,
                        MaxLines = 2, LineHeight = 11, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        }.Named("HexTile_" + tile.Id[(tile.Id.IndexOf('/') + 1)..]).Classed("tile-choice");
        AutomationProperties.SetName(button, tile.Name);
        ToolTip.SetTip(button, (tile.Target == HexPaintTarget.Feature
            ? $"{tile.Name} — надстройка: ставится поверх местности"
            : $"{tile.Name} — гекс местности, вариантов: {tile.VariantCount}")
            + (tile.Hint is { } hint ? "\n" + hint : "")
            + "\nCtrl+щелчок — добавить в смесь или убрать из неё.");
        button.Click += (_, _) =>
        {
            var mix = _hexCtrlHeld;
            if (mix) _editor.ToggleHexMix(tile.Id);
            else
            {
                _editor.ClearHexMix();
                _editor.HexTileId = tile.Id;
            }

            if (_editor.ActiveTool != EditorTool.Hex) _editor.ActiveTool = EditorTool.Hex;
            // Picking a tile from the palette means drawing with it.
            if (PageOf(_editor.HexMode) != HexPage.Draw) _editor.HexMode = _hexPageMode[HexPage.Draw];
            SyncHexPanel(null);
        };
        button.AddHandler(PointerPressedEvent, (_, e) => _hexCtrlHeld = e.KeyModifiers.HasFlag(KeyModifiers.Control),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _hexTileButtons[tile.Id] = button;
        return button;
    }

    private bool _hexCtrlHeld;

    // ------------------------------------------------------------------ Разметка: regions

    private static readonly string[] RegionSwatches =
        ["#FFC0392B", "#FF2E86C1", "#FF27AE60", "#FF8E44AD", "#FFD68910", "#FF16A085", "#FFCB4335", "#FF5D6D7E", "#FF1F2A44", "#FFE5C07B"];

    private Control BuildHexRegionSection()
    {
        var list = new StackPanel { Name = "HexRegionList", Spacing = 4 };
        object? shown = null;
        HexSync(() =>
        {
            var regions = _editor.HexRegions;
            var signature = (regions, _editor.HexRegionId, region: _editor.HexMode == HexToolMode.Region ? _editor.Session.Revision : 0);
            if (Equals(shown, signature)) return;
            shown = signature;
            list.Children.Clear();
            if (regions.Count == 0)
            {
                list.Children.Add(Ui.Hint("Регионов пока нет: создайте первый."));
            }

            foreach (var region in regions)
            {
                var id = region.Id;
                var chosen = id == _editor.HexRegionId;
                // The swatch opens the colours; the row stays one line.
                var colours = new WrapPanel { Width = 5 * 26 };
                var swatch = new Button
                {
                    Width = 22, Height = 22, MinHeight = 22, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Color.Parse(region.Color)),
                    Flyout = new Flyout { Content = colours, Placement = PlacementMode.BottomEdgeAlignedLeft },
                }.Named("HexRegionColor_" + id);
                AutomationProperties.SetName(swatch, "Цвет региона");
                ToolTip.SetTip(swatch, "Цвет региона");
                foreach (var colour in RegionSwatches)
                {
                    var choice = new Button
                    {
                        Width = 22, Height = 22, MinHeight = 22, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4),
                        Background = new SolidColorBrush(Color.Parse(colour)),
                    };
                    ToolTip.SetTip(choice, "Цвет региона");
                    choice.Click += (_, _) => { _editor.SetHexRegionColor(id, colour); swatch.Flyout?.Hide(); };
                    colours.Children.Add(choice);
                }

                var name = new TextBox { Text = region.Name, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center }.Named("HexRegionName_" + id);
                name.LostFocus += (_, _) => _editor.RenameHexRegion(id, name.Text ?? "");
                var pick = HexIconToggle("HexRegionPick_" + id, "Рисовать этим регионом", MapIcons.LineBrush);
                pick.IsChecked = chosen;
                Ui.SetClass(pick, "active", chosen);
                pick.Click += (_, _) => _editor.HexRegionId = id;
                var remove = HexIcon("HexRegionRemove_" + id, "Удалить регион", MapIcons.LineTrash, () => _editor.RemoveHexRegion(id));
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 6 };
                row.Children.Add(swatch);
                Grid.SetColumn(name, 1); row.Children.Add(name);
                Grid.SetColumn(pick, 2); row.Children.Add(pick);
                Grid.SetColumn(remove, 3); row.Children.Add(remove);
                list.Children.Add(new Border
                {
                    Padding = new Thickness(6, 5), CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(chosen ? 2 : 1),
                    BorderBrush = new SolidColorBrush(chosen ? Color.Parse(region.Color) : Color.Parse("#40808080")),
                    Child = new StackPanel
                    {
                        Spacing = 3,
                        Children = { row,
                            new TextBlock { Text = HexRegionHierarchy.Label(regions, region), TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption"),
                            BuildHexParentChoice(region),
                            BuildHexRegionKnowledge(region),
                            new TextBlock { Text = $"Гексов: {_editor.HexRegionSize(id)}" }.Classed("faint", "role-caption") },
                    },
                });
            }
        }, [.. DocumentChanged, nameof(EditorViewModel.HexRegions), nameof(EditorViewModel.HexRegionId), nameof(EditorViewModel.HexMode)]);

        var (brush, brushButtons) = Ui.Segmented("HexRegionBrush",
        [
            ("Add", "Добавляет", null),
            ("Remove", "Убирает", null),
        ], choice => _editor.HexRegionErase = choice == "Remove");
        HexSync(() => HexMark(brushButtons, _editor.HexRegionErase ? "HexRegionBrushRemove" : "HexRegionBrushAdd"), nameof(EditorViewModel.HexRegionErase));
        return Ui.Category("HexRegionCategory", "Регионы", "accent-create", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                HexCommand("HexAddRegionButton", "Новый регион", MapIcons.Plus, () => _editor.AddHexRegion(), "accent"),
                list,
                Ui.Subgroup("Кисть", brush),
            },
        });
    }

    // ------------------------------------------------------------------ Разметка: paths

    private Control BuildHexPathSection()
    {
        var (kinds, kindButtons) = Ui.TallSegmented("HexPathKind",
        [
            (nameof(HexPathKind.River), "Река", MapIcons.LineRiver),
            (nameof(HexPathKind.Road), "Дорога", MapIcons.LineRoad),
            (nameof(HexPathKind.Trail), "Тропа", MapIcons.HexTrail),
            (nameof(HexPathKind.Border), "Граница", MapIcons.HexBorder),
        ], key => _editor.HexPathKind = Enum.Parse<HexPathKind>(key));
        var (styles, styleButtons) = Ui.Segmented("HexRouteStyle",
        [
            (nameof(HexRouteStyle.Centers), "Центры", null),
            (nameof(HexRouteStyle.Meander), "Петли", null),
            (nameof(HexRouteStyle.Edges), "Рёбра", null),
        ], key => _editor.HexRouteStyle = Enum.Parse<HexRouteStyle>(key));
        ToolTip.SetTip(styleButtons[0], "Через центры гексов, прямо");
        ToolTip.SetTip(styleButtons[1], "Через гексы, петляя, как живая река или тропа");
        ToolTip.SetTip(styleButtons[2], "По рёбрам между гексами: границы и стены для виртуальных столов");
        HexSync(() =>
        {
            HexMark(kindButtons, "HexPathKind" + _editor.HexPathKind);
            HexMark(styleButtons, "HexRouteStyle" + _editor.HexRouteStyle);
        }, nameof(EditorViewModel.HexPathKind), nameof(EditorViewModel.HexRouteStyle));

        var auto = Ui.PropertyToggle("HexPathAutoToggle", "По местности: отметьте начало и конец");
        ToolTip.SetTip(auto, "Путь сам прокладывается по местности между двумя отмеченными гексами (как пройти быстрее).");
        auto.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.HexPathAuto = auto.IsChecked == true; };
        HexSync(() => auto.IsChecked = _editor.HexPathAuto, nameof(EditorViewModel.HexPathAuto));
        var erase = Ui.PropertyToggle("HexPathEraseToggle", "Щелчок удаляет путь");
        erase.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.HexPathErase = erase.IsChecked == true; };
        HexSync(() => erase.IsChecked = _editor.HexPathErase, nameof(EditorViewModel.HexPathErase));

        var list = new StackPanel { Name = "HexPathList", Spacing = 2 };
        object? shown = null;
        HexPathId? selected = null;
        HexSync(() =>
        {
            var paths = _editor.HexPaths;
            if (ReferenceEquals(shown, paths) && selected == _editor.SelectedHexPath?.Id) return;
            shown = paths;
            selected = _editor.SelectedHexPath?.Id;
            list.Children.Clear();
            if (paths.Count == 0) list.Children.Add(Ui.Hint("Путей пока нет."));
            foreach (var group in paths.GroupBy(path => path.Kind))
            {
                var index = 0;
                foreach (var path in group)
                {
                    index++;
                    var id = path.Id;
                    var text = $"{EditorViewModel.PathKindName(path.Kind)} {index} · {path.NodeCount} {(path.Style == HexRouteStyle.Edges ? "углов" : "гексов")}";
                    list.Children.Add(new DockPanel
                    {
                        Children =
                        {
                            HexIcon("HexPathRemove_" + index + "_" + path.Kind, "Удалить путь", MapIcons.LineTrash, () => _editor.RemoveHexPath(id)).Docked(Dock.Right),
                            Ui.TextButton("HexPathSelect_" + index + "_" + path.Kind, text, () => _editor.SelectHexPath(id),
                                selected == id ? "active" : "ghost", "compact"),
                        },
                    });
                }
            }
        }, [.. DocumentChanged, nameof(EditorViewModel.HexPaths), nameof(EditorViewModel.HexMode), nameof(EditorViewModel.SelectedHexPath)]);

        return Ui.Category("HexPathCategory", "Пути", "accent-create", new StackPanel
        {
            Spacing = 8,
            Children =
            {
                kinds,
                Ui.Subgroup("Как идёт путь", styles),
                new StackPanel { Spacing = 2, Children = { auto, erase } },
                BuildHexPathBlockingSection(),
                Ui.Subgroup("Пути на карте", list),
            },
        });
    }

    // ------------------------------------------------------------------ Разметка: fog

    private Control BuildHexFogSection()
    {
        var enabled = Ui.PropertyToggle("HexFogToggle", "Туман войны включён");
        enabled.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.HexFogEnabled = enabled.IsChecked == true; };
        HexSync(() => enabled.IsChecked = _editor.HexFogEnabled, [.. DocumentChanged, nameof(EditorViewModel.HexFogEnabled)]);
        var (brush, brushButtons) = Ui.Segmented("HexFogBrush",
        [
            ("Reveal", "Открывает", null),
            ("Hide", "Скрывает", null),
        ], key => _editor.HexFogReveal = key == "Reveal");
        HexSync(() => HexMark(brushButtons, _editor.HexFogReveal ? "HexFogBrushReveal" : "HexFogBrushHide"), nameof(EditorViewModel.HexFogReveal));
        var player = HexCommand("HexPlayerViewButton", "Окно игрока…", MapIcons.HexPlayer, _editor.OpenHexPlayerView, "accent");
        ToolTip.SetTip(player, "Карта так, как её видят игроки: туман непрозрачен, видны только ключи для игроков. Окно можно вынести на второй монитор.");
        return Ui.Category("HexFogCategory", "Туман войны", "accent-create", new StackPanel
        {
            Spacing = 8,
            Children =
            {
                enabled,
                Ui.Subgroup("Кисть", brush),
                HexGrid(2,
                    HexCommand("HexRevealAllButton", "Открыть всё", MapIcons.Eye, () => _editor.RevealAllHexes(true)),
                    HexCommand("HexHideAllButton", "Скрыть всё", MapIcons.EyeOff, () => _editor.RevealAllHexes(false))),
                player,
                Ui.TextButton("HexPartyToolButton", "Партия: разместить или переместить", () => _editor.HexMode = HexToolMode.Party, "compact"),
            },
        });
    }

    // ------------------------------------------------------------------ Разметка: hex key

    private Control BuildHexKeySection()
    {
        var header = new TextBlock { Name = "HexKeyHeader", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        var title = new TextBox { Name = "HexKeyTitleBox", Watermark = "Подпись на карте" };
        var category = new ComboBox { Name = "HexKeyCategoryBox", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var value in Enum.GetValues<HexNoteCategory>()) category.Items.Add(EditorViewModel.HexNoteCategoryName(value));
        var gm = new TextBox { Name = "HexKeyGmBox", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, Watermark = "Видит только мастер" };
        var players = new TextBox { Name = "HexKeyPlayerBox", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, Watermark = "Можно показать игрокам" };
        var child = new TextBlock { Name = "HexKeyChildText", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        var editor = new StackPanel { Spacing = 6 };

        void Commit()
        {
            if (_syncingHex || _editor.HexKeyNote is not { } note) return;
            var chosen = Enum.GetValues<HexNoteCategory>()[Math.Max(0, category.SelectedIndex)];
            _editor.SetHexKey(note with
            {
                Title = (title.Text ?? "").Trim(), Category = chosen, GmText = gm.Text ?? "", PlayerText = players.Text ?? "",
            });
        }

        title.LostFocus += (_, _) => Commit();
        gm.LostFocus += (_, _) => Commit();
        players.LostFocus += (_, _) => Commit();
        category.SelectionChanged += (_, _) => Commit();

        HexSync(() =>
        {
            var note = _editor.HexKeyNote;
            editor.IsVisible = note is not null && _editor.HexPresentation != HexPresentation.PlayerKnowledge;
            if (_editor.HexPresentation == HexPresentation.PlayerKnowledge)
            {
                header.Text = "Ключи ведущего скрыты в представлении знаний игроков.";
                return;
            }
            header.Text = note is null ? "Щёлкните по гексу, чтобы открыть его ключ." : $"Гекс {_editor.HexKeyNumber}";
            if (note is null) return;
            title.Text = note.Title;
            category.SelectedIndex = Array.IndexOf(Enum.GetValues<HexNoteCategory>(), note.Category);
            gm.Text = note.GmText;
            players.Text = note.PlayerText;
            child.Text = note.ChildMap is { } map ? "Подкарта: " + map : "Подкарты пока нет.";
        }, [nameof(EditorViewModel.HexKeyHex), nameof(EditorViewModel.HexKeyNote), nameof(EditorViewModel.HexLevel), nameof(EditorViewModel.HexPresentation)]);

        editor.Children.Add(BuildHexRevealSection());
        editor.Children.Add(BuildHexPoiSection());
        var generationReason = new TextBlock { Name = "HexGenerationReason", TextWrapping = TextWrapping.Wrap };
        HexSync(() => generationReason.Text = _editor.HexKeyHex is { } hex && _editor.ActiveHexLayer is { } layer
            ? (layer.GenerationProtection.Contains(hex) ? "Защищён от генерации. " : "") + layer.Board[hex]?.GenerationReason : "",
            nameof(EditorViewModel.HexKeyHex), nameof(EditorViewModel.CanUndo));
        editor.Children.Add(generationReason);
        editor.Children.Add(title);
        editor.Children.Add(category);
        editor.Children.Add(Ui.Subgroup("Мастеру", gm));
        editor.Children.Add(Ui.Subgroup("Игрокам", players));
        editor.Children.Add(HexGrid(2,
            HexCommand("HexKeySaveButton", "Сохранить", MapIcons.Check, Commit, "accent"),
            HexCommand("HexKeyClearButton", "Удалить", MapIcons.LineTrash, () => _editor.SetHexKey(new HexNote("")))));
        var across = ValueRow("HexChildAcrossSlider", "HexChildAcrossBox", null, "Гексов поперёк", 3, 64, "",
            () => _editor.HexChildAcross, (value, _) => _editor.HexChildAcross = (int)value, 1, 12,
            null);
        editor.Children.Add(Ui.Subgroup("Подкарта гекса", new StackPanel
        {
            Spacing = 4,
            Children = { across, HexCommand("HexChildMapButton", "Открыть подкарту", MapIcons.Open, _editor.OpenHexChildMap), child },
        }));

        var table = new StackPanel { Name = "HexKeyTable", Spacing = 2 };
        object? shown = null;
        HexSync(() =>
        {
            table.IsVisible = _editor.HexPresentation != HexPresentation.PlayerKnowledge;
            var keys = _editor.HexKeys;
            var signature = string.Join("|", keys.Select(row => row.Number + row.Note.Title + row.Note.Category));
            if (Equals(shown, signature)) return;
            shown = signature;
            table.Children.Clear();
            if (keys.Count == 0) table.Children.Add(Ui.Hint("Ключей пока нет."));
            foreach (var row in keys)
            {
                var hex = row.Hex;
                var button = Ui.TextButton("HexKeyRow_" + row.Number.Replace(".", "_", StringComparison.Ordinal),
                    $"{row.Number} · {EditorViewModel.HexNoteCategoryName(row.Note.Category)}"
                    + (row.Note.Title.Length > 0 ? " · " + row.Note.Title : ""),
                    () => _editor.GoToHexKey(hex), "compact");
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                table.Children.Add(button);
            }
        }, [.. DocumentChanged, nameof(EditorViewModel.HexKeys), nameof(EditorViewModel.HexMode), nameof(EditorViewModel.HexPresentation)]);

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Category("HexKeyCategory", "Ключ гекса", "accent-create", new StackPanel { Spacing = 6, Children = { header, editor } }),
                Ui.Category("HexKeyListCategory", "Все ключи", "accent-select", table),
            },
        };
    }

    // ------------------------------------------------------------------ Разметка: ruler

    private Control BuildHexScaleSection()
    {
        static TextBox Box(string name, double width) => new TextBox { Name = name, Width = width, MinWidth = 0 };
        var perHex = Box("HexTravelPerHexBox", 58);
        var unit = Box("HexTravelUnitBox", 64);
        var perDay = Box("HexTravelPerDayBox", 58);
        var dayUnit = new TextBlock { VerticalAlignment = VerticalAlignment.Center }.Classed("faint");
        HexSync(() =>
        {
            var travel = _editor.HexTravel;
            perHex.Text = travel.DistancePerHex.ToString("0.##", CultureInfo.CurrentCulture);
            unit.Text = travel.Unit;
            perDay.Text = travel.DistancePerDay.ToString("0.##", CultureInfo.CurrentCulture);
            dayUnit.Text = travel.Unit;
        }, [.. DocumentChanged, nameof(EditorViewModel.HexTravel)]);

        void Apply()
        {
            if (double.TryParse(perHex.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var hex)
                && double.TryParse(perDay.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var day))
            {
                _editor.SetHexTravel(hex, unit.Text ?? "", day);
            }
        }

        var route = new TextBlock { Name = "HexLegacyRouteText", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        HexSync(() => route.Text = _editor.HexRoute is { } measured
            ? "Путь: " + measured.Label
            : "Протяните от гекса к гексу.", nameof(EditorViewModel.HexRoute), nameof(EditorViewModel.HexMode));
        var apply = HexCommand("HexTravelApplyButton", "Применить масштаб", MapIcons.Check, Apply);
        var scaleHint = HexHint("Дорога улучшает местность на ступень. Профиль задаёт мили за день; масштаб — расстояние между гексами.");
        return Ui.Category("HexMeasureCategory", "Линейка", "accent-create", new StackPanel
        {
            Spacing = 8,
            Children =
            {
                route,
                Ui.Subgroup("Масштаб", new StackPanel
                {
                    Spacing = 4,
                    Children = { HexRow("Один гекс", perHex, unit), HexRow("За день", perDay, dayUnit), apply },
                }),
                scaleHint,
            },
        });
    }

    // ------------------------------------------------------------------ Правка: selection, rows and columns

    private Control BuildHexSelectSection()
    {
        var info = new TextBlock { Name = "HexSelectionText", TextWrapping = TextWrapping.Wrap };
        HexSync(() => info.Text = _editor.HasHexPaste
            ? "Вставка следует за указателем: щёлкните, куда поставить. [ ] — поворот, M — отражение, Esc — отмена."
            : _editor.HexSelectionSummary,
            nameof(EditorViewModel.HexSelection), nameof(EditorViewModel.HexSelectionSummary), nameof(EditorViewModel.HasHexPaste));
        var (tool, toolButtons) = Ui.Segmented("HexSelectTool",
        [
            ("Box", "Рамка", MapIcons.HexSelectBox),
            ("Wand", "Палочка", MapIcons.LineWand),
        ], key => _editor.HexSelectWand = key == "Wand");
        ToolTip.SetTip(toolButtons[1], "Волшебная палочка: щелчок выделяет связную местность");
        HexSync(() => HexMark(toolButtons, _editor.HexSelectWand ? "HexSelectToolWand" : "HexSelectToolBox"), nameof(EditorViewModel.HexSelectWand));

        Button B(string name, string text, string icon, Action action) => HexCommand(name, text, icon, action);
        var selection = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                info,
                tool,
                Ui.Subgroup("Буфер обмена", HexGrid(3,
                    HexTall("HexCopyButton", "Копия", MapIcons.HexCopy, _editor.CopyHexSelection, "Копировать (Ctrl+C)"),
                    HexTall("HexCutButton", "Вырезать", MapIcons.HexCut, _editor.CutHexSelection, "Вырезать (Ctrl+X)"),
                    HexTall("HexPasteButton", "Вставить", MapIcons.HexPaste, _editor.BeginHexPaste, "Вставить (Ctrl+V)"))),
                Ui.Subgroup("Выделенные гексы", HexGrid(3,
                    HexTall("HexMoveButton", "Перенести", MapIcons.Move, _editor.MoveHexSelection, "Перенести выделенное: оно следует за указателем"),
                    HexTall("HexFillSelectionButton", "Залить", MapIcons.HexFill, _editor.FillHexSelection, "Залить выделенное тайлом из палитры"),
                    HexTall("HexDeleteButton", "Очистить", MapIcons.HexErase, _editor.DeleteHexSelection, "Очистить выделенное (Delete)"))),
                Ui.Subgroup("Вставка", HexGrid(3,
                    HexTall("HexTurnLeftButton", "Влево", MapIcons.HexTurnLeft, () => _editor.RotateHexSelection(-1), "Повернуть против часовой ( [ )"),
                    HexTall("HexTurnRightButton", "Вправо", MapIcons.HexTurnRight, () => _editor.RotateHexSelection(1), "Повернуть по часовой ( ] )"),
                    HexTall("HexMirrorButton", "Отразить", MapIcons.HexMirror, _editor.MirrorHexPaste, "Отразить вставку (M)"))),
                HexGrid(2,
                    B("HexSelectAllButton", "Выделить всё", MapIcons.HexSelectBox, _editor.SelectAllHexes),
                    B("HexClearSelectionButton", "Снять", MapIcons.Close, _editor.ClearHexSelection)),
            },
        };

        var rowsHint = HexHint("Вставляются перед первым выделенным гексом (или гексом под указателем). Чётное число колонок сохраняет соседство справа.");
        var rows = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                HexGrid(2,
                    B("HexInsertColumnsButton", "+2 колонки", MapIcons.Plus, () => _editor.InsertHexColumns(2)),
                    B("HexDeleteColumnButton", "−1 колонка", MapIcons.Close, () => _editor.InsertHexColumns(-1)),
                    B("HexInsertRowsButton", "+2 ряда", MapIcons.Plus, () => _editor.InsertHexRows(2)),
                    B("HexDeleteRowButton", "−1 ряд", MapIcons.Close, () => _editor.InsertHexRows(-1))),
                rowsHint,
            },
        };

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Category("HexSelectCategory", "Выделение", "accent-select", selection),
                Ui.Category("HexRowsCategory", "Ряды и колонки", "accent-shape", rows),
            },
        };
    }

    // ------------------------------------------------------------------ Мир: board, levels, grid

    private Control BuildHexBoardSection()
    {
        var columns = new TextBox { Name = "HexBoardColumnsBox", Width = 58, MinWidth = 0 };
        var rows = new TextBox { Name = "HexBoardRowsBox", Width = 58, MinWidth = 0 };
        HexSync(() =>
        {
            if (_editor.ActiveHexLayer is not { } layer) return;
            columns.Text = layer.Board.Columns.ToString(CultureInfo.InvariantCulture);
            rows.Text = layer.Board.Rows.ToString(CultureInfo.InvariantCulture);
        }, [.. DocumentChanged]);
        void Resize()
        {
            if (int.TryParse(columns.Text, out var c) && int.TryParse(rows.Text, out var r)) _editor.ResizeHexBoard(c, r);
        }

        var resize = HexCommand("HexResizeBoardButton", "Изменить размер", MapIcons.ResizeRegion, Resize);
        ToolTip.SetTip(resize, "Колонки × ряды (ширина × высота поля), до 2048 по каждой стороне. Для вставки рядов у выделения — вкладка «Правка».");
        return Ui.Category("HexBoardCategory", "Поле", "accent-shape", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                HexRow("Размер", columns, new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center }, rows),
                resize,
            },
        });
    }

    private Control BuildHexLevelSection()
    {
        var levels = new WrapPanel { Name = "HexLevelList", ItemSpacing = 4, LineSpacing = 4 };
        HexSync(() =>
        {
            levels.Children.Clear();
            foreach (var level in _editor.HexLevels)
            {
                var value = level;
                var active = level == _editor.HexLevel;
                var button = new ToggleButton
                {
                    Content = EditorViewModel.HexLevelName(level), IsChecked = active,
                }.Named("HexLevel_" + (level < 0 ? "m" + -level : level.ToString(CultureInfo.InvariantCulture))).Classed("compact");
                Ui.SetClass(button, "active", active);
                button.Click += (_, _) => _editor.HexLevel = value;
                levels.Children.Add(button);
            }
        }, [.. DocumentChanged, nameof(EditorViewModel.HexLevels)]);
        var ghost = Ui.PropertyToggle("HexGhostSurfaceToggle", "Поверхность видна под подземельем");
        ghost.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.HexGhostSurface = ghost.IsChecked == true; };
        HexSync(() => ghost.IsChecked = _editor.HexGhostSurface, nameof(EditorViewModel.HexGhostSurface));
        return Ui.Category("HexLevelCategory", "Уровни мира", "accent-select", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                levels,
                HexGrid(2,
                    HexCommand("HexAddLevelBelowButton", "Подземелье", MapIcons.ArrowDown, () => _editor.AddHexLevel(below: true)),
                    HexCommand("HexAddLevelAboveButton", "Уровень выше", MapIcons.ArrowUp, () => _editor.AddHexLevel(below: false))),
                ghost,
            },
        });
    }

    private Control BuildHexGridSection()
    {
        var gridVisible = Ui.PropertyToggle("HexGridVisibleToggle", "Показывать сетку");
        var gridNumbers = Ui.PropertyToggle("HexGridNumbersToggle", "Номера гексов (0101)");
        var gridEmpty = Ui.PropertyToggle("HexGridEmptyToggle", "Контуры пустых гексов");
        gridVisible.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.SetHexGrid(grid => grid with { Visible = gridVisible.IsChecked == true }); };
        gridNumbers.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.SetHexGrid(grid => grid with { Numbers = gridNumbers.IsChecked == true }); };
        gridEmpty.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.SetHexGrid(grid => grid with { ShowEmpty = gridEmpty.IsChecked == true }); };
        HexSync(() =>
        {
            var grid = _editor.HexGrid ?? new HexGridStyle();
            gridVisible.IsChecked = grid.Visible;
            gridNumbers.IsChecked = grid.Numbers;
            gridEmpty.IsChecked = grid.ShowEmpty;
            gridNumbers.IsEnabled = gridEmpty.IsEnabled = grid.Visible;
        }, [.. DocumentChanged, nameof(EditorViewModel.HexGrid)]);
        return Ui.Category("HexGridCategory", "Сетка", "accent-look", new StackPanel { Spacing = 2, Children = { gridVisible, gridNumbers, gridEmpty } });
    }

    // ------------------------------------------------------------------ Мир: generator

    private Control BuildHexGeneratorSection()
    {
        var seed = new TextBox { Name = "HexGeneratorSeedBox", Width = 96, MinWidth = 0 };
        HexSync(() => seed.Text = _editor.HexGeneratorSettings.Seed.ToString(CultureInfo.InvariantCulture), nameof(EditorViewModel.HexGeneratorSettings));
        seed.LostFocus += (_, _) =>
        {
            if (int.TryParse(seed.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                _editor.HexGeneratorSettings = _editor.HexGeneratorSettings with { Seed = value };
        };

        Control Slider(string name, string label, double min, double max, Func<HexGeneratorSettings, double> read,
            Func<HexGeneratorSettings, double, HexGeneratorSettings> write, string unit = "")
        {
            return Ui.SliderRow(name, label, min, max, read(_editor.HexGeneratorSettings), unit,
                value => { if (!_syncingHex) _editor.HexGeneratorSettings = write(_editor.HexGeneratorSettings, value); });
        }

        var land = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Slider("HexGenLandSlider", "Суша", 10, 90, s => s.LandShare * 100, (s, v) => s with { LandShare = v / 100 }, "%"),
                Slider("HexGenIslandsSlider", "Острова", 0, 100, s => s.Fragmentation * 100, (s, v) => s with { Fragmentation = v / 100 }, "%"),
                Slider("HexGenReliefSlider", "Горы и холмы", 0, 60, s => s.Relief * 100, (s, v) => s with { Relief = v / 100 }, "%"),
            },
        };
        var climate = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Slider("HexGenMoistureSlider", "Влажность", -100, 100, s => s.Moisture * 100, (s, v) => s with { Moisture = v / 100 }),
                Slider("HexGenTemperatureSlider", "Тепло", -100, 100, s => s.Temperature * 100, (s, v) => s with { Temperature = v / 100 }),
                Slider("HexGenLatitudeSpanSlider", "Размах широт", 0, 100, s => s.LatitudeSpan * 100, (s, v) => s with { LatitudeSpan = v / 100 }, "%"),
                Slider("HexGenLatitudeShiftSlider", "Ближе к полюсу", -100, 100, s => s.LatitudeShift * 100, (s, v) => s with { LatitudeShift = v / 100 }),
            },
        };
        var life = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Slider("HexGenRiversSlider", "Реки", 0, 30, s => s.RiversPerThousand, (s, v) => s with { RiversPerThousand = v }, "/1000"),
                Slider("HexGenTownsSlider", "Поселения", 0, 30, s => s.SettlementsPerThousand, (s, v) => s with { SettlementsPerThousand = v }, "/1000"),
            },
        };

        // R21: the kind of world, an open rim and how rivers are found.
        var (modeRow, modeButtons) = Ui.Segmented("HexGenMode",
            [.. Enum.GetValues<HexWorldMode>().Select(m => (m.ToString(), EditorViewModel.HexWorldModeName(m), (string?)null))],
            key => { if (!_syncingHex) _editor.HexGeneratorSettings = _editor.HexGeneratorSettings with { Mode = Enum.Parse<HexWorldMode>(key) }; });
        ToolTip.SetTip(modeRow, "Материки и моря; архипелаг — много островов; пустыня — сухо и жарко, мало рек; подземье — пещеры в скале, подземные озёра и реки.");
        var openEdges = Ui.PropertyToggle("HexGenOpenEdgesToggle", "Суша до краёв карты");
        ToolTip.SetTip(openEdges, "Без моря по краю: карта — часть большого материка.");
        openEdges.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.HexGeneratorSettings = _editor.HexGeneratorSettings with { OpenEdges = openEdges.IsChecked == true }; };
        var flow = Ui.PropertyToggle("HexGenFlowRiversToggle", "Реки собирают сток и сливаются");
        ToolTip.SetTip(flow, "Вода стекает с каждого гекса вниз; крупнейшие потоки становятся реками, притоки впадают в них. Без галочки — каждая река сама по себе, как раньше.");
        flow.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.HexGeneratorSettings = _editor.HexGeneratorSettings with { RiverModel = flow.IsChecked == true ? HexGeneratorSettings.FlowRivers : HexGeneratorSettings.DownhillRivers }; };
        HexSync(() =>
        {
            var settings = _editor.HexGeneratorSettings;
            foreach (var button in modeButtons)
            {
                var on = button.Name == "HexGenMode" + settings.Mode;
                if (button is ToggleButton toggle) toggle.IsChecked = on;
                Ui.SetClass(button, "active", on);
            }
            openEdges.IsChecked = settings.OpenEdges;
            flow.IsChecked = settings.RiverModel == HexGeneratorSettings.FlowRivers;
        }, nameof(EditorViewModel.HexGeneratorSettings));

        var steps = HexGrid(2);
        foreach (var step in Enum.GetValues<HexGenerationStep>())
        {
            var value = step;
            var button = Ui.TextButton("HexGenStep" + step, $"{(int)step + 1}. {EditorViewModel.HexGenerationStepName(step)}",
                () => _editor.RunHexGeneratorStep(value), "compact");
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            steps.Children.Add(button);
        }

        var reroll = HexCommand("HexGenRerollButton", "Перебросить шаг", MapIcons.LineReset, _editor.RerollHexGeneratorStep);
        ToolTip.SetTip(reroll, "Отменить последний шаг генератора и сделать его заново с другим случаем.");
        HexSync(() => reroll.IsEnabled = _editor.HexCanRerollGenerator, [.. DocumentChanged, nameof(EditorViewModel.HexCanRerollGenerator)]);
        var all = HexCommand("HexGenAllButton", "Создать мир целиком", MapIcons.LineSparkle, _editor.RunHexGeneratorAll, "accent");
        return Ui.Category("HexGeneratorCategory", "Генератор мира", "accent-create", new StackPanel
        {
            Spacing = 8,
            Children =
            {
                HexHint("Расчёт идёт в фоне. Предпросмотр не меняет карту. «Применить» — одна отмена; защита сохраняет гексы и пересекающие её пути."),
                HexRow("Зерно", seed, HexIcon("HexGenSeedButton", "Случайное зерно", MapIcons.HexDice, _editor.NewHexGeneratorSeed)),
                Ui.Subgroup("Вид мира", new StackPanel { Spacing = 4, Children = { modeRow, openEdges } }),
                Ui.Subgroup("Суша и рельеф", land),
                Ui.Subgroup("Климат", climate),
                Ui.Subgroup("Реки и поселения на 1000 гексов", new StackPanel { Spacing = 4, Children = { life, flow } }),
                Ui.Subgroup("Шаги по порядку", steps),
                HexGrid(2, all, reroll),
                BuildGenerationControls(),
            },
        }, expanded: false);
    }

    // ------------------------------------------------------------------ Мир: export and print

    private Control BuildHexExportSection()
    {
        var players = Ui.PropertyToggle("HexExportPlayersToggle", "Как видят игроки (туман, ключи игрокам)");
        players.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.ExportHexPlayers = players.IsChecked == true; };
        HexSync(() => players.IsChecked = _editor.ExportHexPlayers, nameof(EditorViewModel.ExportHexPlayers));

        return Ui.Category("HexExportCategory", "Экспорт и печать", "accent-look", new StackPanel
        {
            Spacing = 8,
            Children =
            {
                players,
                BuildExportPackagePreview("HexExport"),
                HexHint("PNG и .dd2vtt для Foundry и Roll20 — кнопка «Экспорт» вверху; рядом с .dd2vtt пишется макрос сетки для Foundry."),
                HexCommand("HexPrintButton", "PDF для печати…", MapIcons.HexPrint, () => _ = ShowHexPrintAsync(), "accent"),
                Ui.Subgroup("Файлы", HexGrid(4,
                    HexTall("HexJsonExportButton", "JSON", MapIcons.HexExport, () => _ = _editor.ExportHexJsonAsync(), "Сохранить карту в JSON…"),
                    HexTall("HexJsonImportButton", "Из JSON", MapIcons.Import, () => _ = ImportHexJsonAsync(), "Загрузить карту из JSON…"),
                    HexTall("HexKeyExportButton", "Ключ", MapIcons.LineNotes, () => _ = _editor.ExportHexKeyAsync(false), "Ключ гексов для мастера (Markdown)…"),
                    HexTall("HexKeyPlayersExportButton", "Игрокам", MapIcons.HexPlayer, () => _ = _editor.ExportHexKeyAsync(true), "Ключ гексов для игроков (Markdown)…"))),
            },
        }, expanded: false);
    }

    private async Task ImportHexJsonAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "JSON гексовой карты",
            AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("JSON гексовой карты") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0 || Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(files[0]) is not { } path) return;
        try { await ShowHexImportAsync(await File.ReadAllTextAsync(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { await ShowHexImportResultAsync(error.Message); }
    }

    private void SyncHexPanel(string? property)
    {
        if (_syncingHex) return;
        _syncingHex = true;
        try
        {
            foreach (var (properties, sync) in _hexSyncs)
            {
                if (property is null || properties.Contains(property)) sync();
            }
        }
        finally
        {
            _syncingHex = false;
        }
    }
}
