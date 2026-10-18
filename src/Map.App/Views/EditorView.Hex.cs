using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
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
/// The tool panel of the hex constructor: what a press does - the terrain modes (place, fill,
/// erase, pick) and the workshop modes (regions, fog, paths, the hex key, the ruler, selection) -
/// and below it only the settings of the chosen mode, then the board, the levels and the grid.
/// </summary>
public sealed partial class EditorView
{
    private static readonly Dictionary<(string Id, int Variant), Bitmap> HexThumbnails = [];

    private readonly Dictionary<string, ToggleButton> _hexTileButtons = new(StringComparer.Ordinal);

    // Each sync runs when one of its properties changes (or on every document change when it names none).
    private readonly List<(string[] Properties, Action Sync)> _hexSyncs = [];
    private bool _syncingHex;

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

    private Control BuildHexPanel()
    {
        var summary = new TextBlock { Name = "HexBoardSummary", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        HexSync(() => summary.Text = _editor.HexBoardSummary, [.. DocumentChanged, nameof(EditorViewModel.HexBoardSummary)]);

        var (terrainModes, terrainButtons) = Ui.Segmented("HexMode",
        [
            (nameof(HexToolMode.Place), "Поставить", null),
            (nameof(HexToolMode.Fill), "Залить", null),
            (nameof(HexToolMode.Erase), "Стереть", null),
            (nameof(HexToolMode.Pick), "Пипетка", null),
        ], key => _editor.HexMode = Enum.Parse<HexToolMode>(key));
        var (workshopModes, workshopButtons) = Ui.Segmented("HexMode",
        [
            (nameof(HexToolMode.Region), "Регионы", null),
            (nameof(HexToolMode.Path), "Пути", null),
            (nameof(HexToolMode.Fog), "Туман", null),
            (nameof(HexToolMode.Key), "Ключ гекса", null),
            (nameof(HexToolMode.Measure), "Линейка", null),
            (nameof(HexToolMode.Select), "Выделение", null),
        ], key => _editor.HexMode = Enum.Parse<HexToolMode>(key));
        HexSync(() =>
        {
            foreach (var button in terrainButtons.Concat(workshopButtons).OfType<ToggleButton>())
            {
                var active = button.Name == "HexMode" + _editor.HexMode;
                button.IsChecked = active;
                Ui.SetClass(button, "active", active);
            }
        }, nameof(EditorViewModel.HexMode));
        var modeHint = new TextBlock { Name = "HexModeHint", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        HexSync(() => modeHint.Text = _editor.HexMode switch
        {
            HexToolMode.Place => "Щелчок ставит гекс, протягивание — линию гексов. Ctrl+щелчок по тайлам палитры собирает смесь. [ и ] — поворот.",
            HexToolMode.Fill => "Щелчок заливает всю связную область одной местности (или пустую область) выбранным гексом или смесью.",
            HexToolMode.Erase => "Снимает верх гекса: сначала надстройку, затем местность.",
            HexToolMode.Pick => "Щелчок по гексу берёт его тайл, вариант и поворот в палитру.",
            HexToolMode.Region => "Протягивание включает гексы в выбранный регион; границы рисуются сами там, где регионы встречаются.",
            HexToolMode.Path => "Протяните реку, дорогу, тропу или границу по гексам. Где дорога пересекает реку, появляется мост.",
            HexToolMode.Fog => "Туман войны: протягивание открывает гексы игрокам (или снова скрывает). Мастер видит сквозь туман.",
            HexToolMode.Key => "Щелчок по гексу открывает его ключ: подпись, текст для мастера и текст для игроков, подкарта.",
            HexToolMode.Measure => "Протяните от гекса к гексу: самый быстрый путь в гексах, милях и днях. Дороги быстрее, болота медленнее.",
            _ => "Рамкой выделите гексы (Shift — добавить). Ctrl+C / Ctrl+X / Ctrl+V, Delete, [ ] — поворот вставки, M — отражение.",
        }, nameof(EditorViewModel.HexMode));

        var terrain = BuildHexTerrainSections();
        var region = BuildHexRegionSection();
        var paths = BuildHexPathSection();
        var fog = BuildHexFogSection();
        var key = BuildHexKeySection();
        var measure = BuildHexMeasureSection();
        var select = BuildHexSelectSection();
        HexSync(() =>
        {
            var mode = _editor.HexMode;
            terrain.IsVisible = mode is HexToolMode.Place or HexToolMode.Fill or HexToolMode.Erase or HexToolMode.Pick;
            region.IsVisible = mode == HexToolMode.Region;
            paths.IsVisible = mode == HexToolMode.Path;
            fog.IsVisible = mode == HexToolMode.Fog;
            key.IsVisible = mode == HexToolMode.Key;
            measure.IsVisible = mode == HexToolMode.Measure;
            select.IsVisible = mode == HexToolMode.Select;
        }, nameof(EditorViewModel.HexMode));

        var radius = ValueRow("HexRadiusSlider", "HexRadiusBox", null, "Площадка", 0, HexEditing.MaxRadius, "кольца",
            () => _editor.HexRadius, (value, _) => _editor.HexRadius = (int)value, 1, 0,
            "Сколько колец вокруг указателя ставит один щелчок: 0 — один гекс, 1 — семь, 2 — девятнадцать.");
        var footprint = new TextBlock { Name = "HexFootprintText" }.Classed("faint", "role-caption");
        HexSync(() => footprint.Text = "Один щелчок: " + _editor.HexFootprintText, nameof(EditorViewModel.HexRadius), nameof(EditorViewModel.HexFootprintText));
        var radiusSection = Ui.Section("Площадка", new StackPanel { Spacing = 4, Children = { radius, footprint } });
        HexSync(() => radiusSection.IsVisible = _editor.HexMode is HexToolMode.Place or HexToolMode.Erase or HexToolMode.Region or HexToolMode.Fog,
            nameof(EditorViewModel.HexMode));

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
        }, [.. DocumentChanged, nameof(EditorViewModel.HexGrid)]);

        var panel = new StackPanel
        {
            Name = "HexToolPanel",
            Spacing = 12,
            Children =
            {
                summary,
                Ui.Section("Что делает щелчок", new StackPanel { Spacing = 6, Children =
                {
                    new TextBlock { Text = "Местность" }.Classed("faint", "role-caption"), terrainModes,
                    new TextBlock { Text = "Мастерская" }.Classed("faint", "role-caption"), workshopModes,
                    modeHint,
                } }),
                terrain,
                region,
                paths,
                fog,
                key,
                measure,
                select,
                radiusSection,
                BuildHexLevelSection(),
                Ui.Section("Сетка", new StackPanel { Spacing = 4, Children = { gridVisible, gridNumbers, gridEmpty } }),
            },
        };

        ((INotifyPropertyChanged)_editor).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } name && (name.StartsWith("Hex", StringComparison.Ordinal) || name is nameof(EditorViewModel.CanUndo)))
            {
                SyncHexPanel(name);
            }
        };
        SyncHexPanel(null);
        return panel;
    }

    // ------------------------------------------------------------------ terrain

    private Control BuildHexTerrainSections()
    {
        var palette = new StackPanel { Name = "HexPalette", Spacing = 8 };
        foreach (var group in _editor.HexTiles.GroupBy(tile => tile.Group))
        {
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var tile in group)
            {
                wrap.Children.Add(HexTileButton(tile));
            }

            palette.Children.Add(new TextBlock { Text = group.Key }.Classed("faint", "role-caption", "subgroup-title"));
            palette.Children.Add(wrap);
        }

        HexSync(() =>
        {
            var mixed = _editor.HexMix.Select(entry => entry.TileId).ToHashSet(StringComparer.Ordinal);
            foreach (var (id, button) in _hexTileButtons)
            {
                var active = mixed.Count > 0 ? mixed.Contains(id) : id == _editor.HexTileId;
                button.IsChecked = active;
                Ui.SetClass(button, "active", active);
            }
        }, nameof(EditorViewModel.HexTileId), nameof(EditorViewModel.HexMix));

        var current = new TextBlock { Name = "HexCurrentTile", TextWrapping = TextWrapping.Wrap };
        HexSync(() => current.Text = $"{_editor.HexTile.Name} · "
            + (_editor.HexTile.Target == HexPaintTarget.Feature ? "надстройка, ставится на местность" : "местность")
            + $" · вариантов: {_editor.HexTile.VariantCount}"
            + (_editor.HexTile.Overhang > 0 ? " · высокий, нависает" : "")
            + (_editor.HexTile.Hint is { } hint ? "\n" + hint : ""),
            nameof(EditorViewModel.HexTileId), nameof(EditorViewModel.HexTile));

        var mix = new StackPanel { Name = "HexMixList", Spacing = 4 };
        HexSync(() =>
        {
            mix.Children.Clear();
            if (_editor.HexMix.Count == 0) return;
            mix.Children.Add(new TextBlock { Text = "Смесь: вес каждого тайла", TextWrapping = TextWrapping.Wrap }.Classed("role-caption"));
            foreach (var entry in _editor.HexMix)
            {
                var id = entry.TileId;
                var name = HexStarterTiles.Find(id)?.Name ?? id;
                mix.Children.Add(Ui.SliderRow("HexMixWeight_" + id[(id.IndexOf('/') + 1)..], name, 0, 10, entry.Weight, "",
                    value => { if (!_syncingHex) _editor.SetHexMixWeight(id, value); }));
            }

            mix.Children.Add(Ui.TextButton("HexMixClearButton", "Убрать смесь", _editor.ClearHexMix, "compact"));
        }, nameof(EditorViewModel.HexMix));

        var random = Ui.PropertyToggle("HexRandomVariantToggle", "Случайный вариант на каждый гекс");
        random.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.HexRandomVariant = random.IsChecked == true; };
        HexSync(() => random.IsChecked = _editor.HexRandomVariant, nameof(EditorViewModel.HexRandomVariant));
        var shuffle = Ui.TextButton("HexShuffleButton", "Перемешать варианты", _editor.ShuffleHexVariants, "compact");
        ToolTip.SetTip(shuffle, "Следующие гексы получат другие случайные картинки (и новый узор живой местности); уже поставленные не меняются.");

        var variants = new WrapPanel { Name = "HexVariantStrip", Orientation = Orientation.Horizontal };
        HexSync(() =>
        {
            variants.Children.Clear();
            var tile = _editor.HexTile;
            for (var i = 0; i < tile.VariantCount; i++)
            {
                var variant = i;
                var button = new ToggleButton
                {
                    Width = 46, Height = 52, Padding = new Thickness(2), Margin = new Thickness(0, 0, 4, 4),
                    Content = new Image { Source = HexThumbnail(tile.Id, variant), Width = 38, Height = 44 },
                    IsChecked = !_editor.HexRandomVariant && _editor.HexFixedVariant == variant,
                }.Named("HexVariant" + variant).Classed("tile-choice");
                AutomationProperties.SetName(button, $"Вариант {variant + 1}");
                ToolTip.SetTip(button, $"Вариант {variant + 1}: ставить только эту картинку");
                button.Click += (_, _) =>
                {
                    _editor.HexRandomVariant = false;
                    _editor.HexFixedVariant = variant;
                };
                variants.Children.Add(button);
            }

            variants.Opacity = _editor.HexRandomVariant ? 0.6 : 1;
        }, nameof(EditorViewModel.HexTileId), nameof(EditorViewModel.HexTile), nameof(EditorViewModel.HexRandomVariant), nameof(EditorViewModel.HexFixedVariant));

        var rotation = new TextBlock { Name = "HexRotationText", VerticalAlignment = VerticalAlignment.Center, MinWidth = 44 };
        HexSync(() => rotation.Text = _editor.HexRandomRotation ? "случайно" : $"{_editor.HexRotation * 60}°",
            nameof(EditorViewModel.HexRotation), nameof(EditorViewModel.HexRandomRotation));
        var randomTurn = Ui.PropertyToggle("HexRandomRotationToggle", "Случайный поворот на каждый гекс");
        randomTurn.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.HexRandomRotation = randomTurn.IsChecked == true; };
        HexSync(() => randomTurn.IsChecked = _editor.HexRandomRotation, nameof(EditorViewModel.HexRandomRotation));
        var turnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                Ui.TextButton("HexRotateLeftButton", "⟲ 60°", () => _editor.RotateHexBrush(-1), "compact"),
                rotation,
                Ui.TextButton("HexRotateRightButton", "⟳ 60°", () => _editor.RotateHexBrush(1), "compact"),
            },
        };
        var turnHint = new TextBlock { TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        HexSync(() => turnHint.Text = _editor.HexTile.Rotatable
            ? "Клавиши [ и ] поворачивают кисть."
            : $"«{_editor.HexTile.Name}» не поворачивается: у тайла есть верх.",
            nameof(EditorViewModel.HexTileId), nameof(EditorViewModel.HexTile));

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Ui.Section("Гексы", new StackPanel { Spacing = 6, Children = { current, palette, mix } }),
                Ui.Section("Варианты", new StackPanel { Spacing = 6, Children = { random, variants, shuffle } }),
                Ui.Section("Поворот", new StackPanel { Spacing = 6, Children = { turnRow, randomTurn, turnHint } }),
            },
        };
    }

    private ToggleButton HexTileButton(HexTileDefinition tile)
    {
        var button = new ToggleButton
        {
            Width = 64, Height = 82, Padding = new Thickness(2), Margin = new Thickness(0, 0, 4, 4),
            Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new Image { Source = HexThumbnail(tile.Id, 0), Width = 50, Height = 56, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock
                    {
                        Text = tile.Name, FontSize = 11, TextAlignment = Avalonia.Media.TextAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center,
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
            SyncHexPanel(null);
        };
        button.AddHandler(PointerPressedEvent, (_, e) => _hexCtrlHeld = e.KeyModifiers.HasFlag(KeyModifiers.Control),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _hexTileButtons[tile.Id] = button;
        return button;
    }

    private bool _hexCtrlHeld;

    // ------------------------------------------------------------------ regions

    private static readonly string[] RegionSwatches =
        ["#FFC0392B", "#FF2E86C1", "#FF27AE60", "#FF8E44AD", "#FFD68910", "#FF16A085", "#FFCB4335", "#FF5D6D7E", "#FF1F2A44", "#FFE5C07B"];

    private Control BuildHexRegionSection()
    {
        var list = new StackPanel { Name = "HexRegionList", Spacing = 6 };
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
                list.Children.Add(new TextBlock { Text = "Регионов пока нет.", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption"));
            }

            foreach (var region in regions)
            {
                var id = region.Id;
                var chosen = id == _editor.HexRegionId;
                var swatch = new Border
                {
                    Width = 18, Height = 18, CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(Color.Parse(region.Color)), VerticalAlignment = VerticalAlignment.Center,
                };
                var name = new TextBox { Text = region.Name, MinWidth = 110 }.Named("HexRegionName_" + id);
                name.LostFocus += (_, _) => _editor.RenameHexRegion(id, name.Text ?? "");
                var pick = new ToggleButton { Content = "Кисть", IsChecked = chosen }.Named("HexRegionPick_" + id).Classed("compact");
                pick.Click += (_, _) => _editor.HexRegionId = id;
                var colours = new WrapPanel();
                foreach (var colour in RegionSwatches)
                {
                    var choice = new Button
                    {
                        Width = 16, Height = 16, Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 3),
                        Background = new SolidColorBrush(Color.Parse(colour)),
                    };
                    ToolTip.SetTip(choice, "Цвет региона");
                    choice.Click += (_, _) => _editor.SetHexRegionColor(id, colour);
                    colours.Children.Add(choice);
                }

                var remove = Ui.TextButton("HexRegionRemove_" + id, "Удалить", () => _editor.RemoveHexRegion(id), "compact");
                list.Children.Add(new Border
                {
                    Padding = new Thickness(6), CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(chosen ? 2 : 1),
                    BorderBrush = new SolidColorBrush(chosen ? Color.Parse(region.Color) : Color.Parse("#40808080")),
                    Child = new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { swatch, name, pick } },
                            new TextBlock { Text = $"Гексов: {_editor.HexRegionSize(id)}" }.Classed("faint", "role-caption"),
                            colours,
                            remove,
                        },
                    },
                });
            }
        }, [.. DocumentChanged, nameof(EditorViewModel.HexRegions), nameof(EditorViewModel.HexRegionId), nameof(EditorViewModel.HexMode)]);

        var erase = Ui.PropertyToggle("HexRegionEraseToggle", "Кисть убирает гексы из регионов");
        erase.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.HexRegionErase = erase.IsChecked == true; };
        HexSync(() => erase.IsChecked = _editor.HexRegionErase, nameof(EditorViewModel.HexRegionErase));
        return Ui.Section("Регионы", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Ui.TextButton("HexAddRegionButton", "Новый регион", () => _editor.AddHexRegion(), "accent"),
                list,
                erase,
            },
        });
    }

    // ------------------------------------------------------------------ paths

    private Control BuildHexPathSection()
    {
        var (kinds, kindButtons) = Ui.Segmented("HexPathKind",
        [
            (nameof(HexPathKind.River), "Река", null),
            (nameof(HexPathKind.Road), "Дорога", null),
            (nameof(HexPathKind.Trail), "Тропа", null),
            (nameof(HexPathKind.Border), "Граница", null),
        ], key => _editor.HexPathKind = Enum.Parse<HexPathKind>(key));
        var (styles, styleButtons) = Ui.Segmented("HexRouteStyle",
        [
            (nameof(HexRouteStyle.Centers), "Через центры", null),
            (nameof(HexRouteStyle.Meander), "Петляя", null),
            (nameof(HexRouteStyle.Edges), "По рёбрам", null),
        ], key => _editor.HexRouteStyle = Enum.Parse<HexRouteStyle>(key));
        HexSync(() =>
        {
            foreach (var button in kindButtons.OfType<ToggleButton>())
            {
                var active = button.Name == "HexPathKind" + _editor.HexPathKind;
                button.IsChecked = active;
                Ui.SetClass(button, "active", active);
            }

            foreach (var button in styleButtons.OfType<ToggleButton>())
            {
                var active = button.Name == "HexRouteStyle" + _editor.HexRouteStyle;
                button.IsChecked = active;
                Ui.SetClass(button, "active", active);
            }
        }, nameof(EditorViewModel.HexPathKind), nameof(EditorViewModel.HexRouteStyle));

        var auto = Ui.PropertyToggle("HexPathAutoToggle", "Прокладывать по местности (отметьте начало и конец)");
        auto.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.HexPathAuto = auto.IsChecked == true; };
        HexSync(() => auto.IsChecked = _editor.HexPathAuto, nameof(EditorViewModel.HexPathAuto));
        var erase = Ui.PropertyToggle("HexPathEraseToggle", "Щелчок удаляет путь");
        erase.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.HexPathErase = erase.IsChecked == true; };
        HexSync(() => erase.IsChecked = _editor.HexPathErase, nameof(EditorViewModel.HexPathErase));

        var list = new StackPanel { Name = "HexPathList", Spacing = 4 };
        object? shown = null;
        HexSync(() =>
        {
            var paths = _editor.HexPaths;
            if (ReferenceEquals(shown, paths)) return;
            shown = paths;
            list.Children.Clear();
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
                            Ui.TextButton("HexPathRemove_" + index + "_" + path.Kind, "×", () => _editor.RemoveHexPath(id), "compact").Docked(Dock.Right),
                            new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
                        },
                    });
                }
            }
        }, [.. DocumentChanged, nameof(EditorViewModel.HexPaths), nameof(EditorViewModel.HexMode)]);

        return Ui.Section("Пути", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                kinds,
                new TextBlock { Text = "Как идёт путь" }.Classed("faint", "role-caption"),
                styles,
                auto,
                erase,
                list,
            },
        });
    }

    // ------------------------------------------------------------------ fog

    private Control BuildHexFogSection()
    {
        var enabled = Ui.PropertyToggle("HexFogToggle", "Туман войны включён");
        enabled.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.HexFogEnabled = enabled.IsChecked == true; };
        HexSync(() => enabled.IsChecked = _editor.HexFogEnabled, [.. DocumentChanged, nameof(EditorViewModel.HexFogEnabled)]);
        var (brush, brushButtons) = Ui.Segmented("HexFogBrush",
        [
            ("Reveal", "Открывать", null),
            ("Hide", "Скрывать", null),
        ], key => _editor.HexFogReveal = key == "Reveal");
        HexSync(() =>
        {
            foreach (var button in brushButtons.OfType<ToggleButton>())
            {
                var active = button.Name == (_editor.HexFogReveal ? "HexFogBrushReveal" : "HexFogBrushHide");
                button.IsChecked = active;
                Ui.SetClass(button, "active", active);
            }
        }, nameof(EditorViewModel.HexFogReveal));
        return Ui.Section("Туман войны", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                enabled,
                brush,
                new WrapPanel
                {
                    ItemSpacing = 6, LineSpacing = 6,
                    Children =
                    {
                        Ui.TextButton("HexRevealAllButton", "Открыть всё", () => _editor.RevealAllHexes(true), "compact"),
                        Ui.TextButton("HexHideAllButton", "Скрыть всё", () => _editor.RevealAllHexes(false), "compact"),
                    },
                },
                Ui.TextButton("HexPlayerViewButton", "Окно игрока…", _editor.OpenHexPlayerView, "accent"),
                new TextBlock
                {
                    Text = "Окно игрока показывает карту так, как её видят игроки: туман непрозрачен, видны только ключи для игроков. Его можно вынести на второй монитор.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
            },
        });
    }

    // ------------------------------------------------------------------ hex key

    private Control BuildHexKeySection()
    {
        var header = new TextBlock { Name = "HexKeyHeader", TextWrapping = TextWrapping.Wrap }.Classed("role-panel");
        var title = new TextBox { Name = "HexKeyTitleBox", Watermark = "Подпись на карте" };
        var category = new ComboBox { Name = "HexKeyCategoryBox", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var value in Enum.GetValues<HexNoteCategory>()) category.Items.Add(EditorViewModel.HexNoteCategoryName(value));
        var gm = new TextBox { Name = "HexKeyGmBox", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, Watermark = "Только для мастера" };
        var players = new TextBox { Name = "HexKeyPlayerBox", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, Watermark = "Можно показать игрокам" };
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
            editor.IsVisible = note is not null;
            header.Text = note is null ? "Щёлкните по гексу, чтобы открыть его ключ." : $"Гекс {_editor.HexKeyNumber}";
            if (note is null) return;
            title.Text = note.Title;
            category.SelectedIndex = Array.IndexOf(Enum.GetValues<HexNoteCategory>(), note.Category);
            gm.Text = note.GmText;
            players.Text = note.PlayerText;
            child.Text = note.ChildMap is { } map ? "Подкарта: " + map : "Подкарты пока нет.";
        }, [nameof(EditorViewModel.HexKeyHex), nameof(EditorViewModel.HexKeyNote), nameof(EditorViewModel.HexLevel)]);

        editor.Children.Add(title);
        editor.Children.Add(category);
        editor.Children.Add(new TextBlock { Text = "Мастеру" }.Classed("faint", "role-caption"));
        editor.Children.Add(gm);
        editor.Children.Add(new TextBlock { Text = "Игрокам" }.Classed("faint", "role-caption"));
        editor.Children.Add(players);
        editor.Children.Add(new WrapPanel
        {
            ItemSpacing = 6, LineSpacing = 6,
            Children =
            {
                Ui.TextButton("HexKeySaveButton", "Сохранить ключ", Commit, "accent"),
                Ui.TextButton("HexKeyClearButton", "Удалить ключ", () => _editor.SetHexKey(new HexNote("")), "compact"),
            },
        });
        var across = ValueRow("HexChildAcrossSlider", "HexChildAcrossBox", null, "Подкарта", 3, 64, "гекс. поперёк",
            () => _editor.HexChildAcross, (value, _) => _editor.HexChildAcross = (int)value, 1, 12,
            "Сколько маленьких гексов поперёк будет в подкарте этого гекса.");
        editor.Children.Add(across);
        editor.Children.Add(Ui.TextButton("HexChildMapButton", "Открыть подкарту гекса", _editor.OpenHexChildMap, "compact"));
        editor.Children.Add(child);

        var table = new StackPanel { Name = "HexKeyTable", Spacing = 2 };
        object? shown = null;
        HexSync(() =>
        {
            var keys = _editor.HexKeys;
            var signature = string.Join("|", keys.Select(row => row.Number + row.Note.Title + row.Note.Category));
            if (Equals(shown, signature)) return;
            shown = signature;
            table.Children.Clear();
            if (keys.Count == 0)
            {
                table.Children.Add(new TextBlock { Text = "Ключей пока нет.", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption"));
            }

            foreach (var row in keys)
            {
                var hex = row.Hex;
                var button = Ui.TextButton("HexKeyRow_" + row.Number.Replace(".", "_", StringComparison.Ordinal),
                    $"{row.Number} · {EditorViewModel.HexNoteCategoryName(row.Note.Category)}"
                    + (row.Note.Title.Length > 0 ? " · " + row.Note.Title : ""),
                    () => _editor.GoToHexKey(hex), "compact");
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                table.Children.Add(button);
            }
        }, [.. DocumentChanged, nameof(EditorViewModel.HexKeys), nameof(EditorViewModel.HexMode)]);

        return Ui.Section("Ключ гекса", new StackPanel
        {
            Spacing = 8,
            Children =
            {
                header,
                editor,
                new TextBlock { Text = "Все ключи" }.Classed("role-panel"),
                table,
            },
        });
    }

    // ------------------------------------------------------------------ ruler

    private Control BuildHexMeasureSection()
    {
        static TextBox Box(string name, string text) => new TextBox { Name = name, Text = text, Width = 70 };
        var perHex = Box("HexTravelPerHexBox", "");
        var unit = new TextBox { Name = "HexTravelUnitBox", Width = 70 };
        var perDay = Box("HexTravelPerDayBox", "");
        HexSync(() =>
        {
            var travel = _editor.HexTravel;
            perHex.Text = travel.DistancePerHex.ToString("0.##", CultureInfo.CurrentCulture);
            unit.Text = travel.Unit;
            perDay.Text = travel.DistancePerDay.ToString("0.##", CultureInfo.CurrentCulture);
        }, [.. DocumentChanged, nameof(EditorViewModel.HexTravel)]);

        void Apply()
        {
            if (double.TryParse(perHex.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var hex)
                && double.TryParse(perDay.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var day))
            {
                _editor.SetHexTravel(hex, unit.Text ?? "", day);
            }
        }

        var route = new TextBlock { Name = "HexRouteText", TextWrapping = TextWrapping.Wrap };
        HexSync(() => route.Text = _editor.HexRoute is { } measured
            ? "Путь: " + measured.Label
            : "Протяните от гекса к гексу.", nameof(EditorViewModel.HexRoute), nameof(EditorViewModel.HexMode));
        return Ui.Section("Линейка и масштаб", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                route,
                Ui.PropertyRow("Один гекс", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { perHex, unit } }),
                Ui.PropertyRow("За день", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { perDay } }),
                Ui.TextButton("HexTravelApplyButton", "Применить масштаб", Apply, "compact"),
                new TextBlock
                {
                    Text = "Время зависит от местности: дорога — вдвое быстрее равнины, лес и холмы — вдвое медленнее, горы и болота — втрое, брод через реку добавляет полгекса. Море пешком не пройти.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
            },
        });
    }

    // ------------------------------------------------------------------ selection and board

    private Control BuildHexSelectSection()
    {
        var info = new TextBlock { Name = "HexSelectionText", TextWrapping = TextWrapping.Wrap };
        HexSync(() => info.Text = _editor.HasHexPaste
            ? "Вставка следует за указателем: щёлкните, куда поставить. [ ] — поворот, M — отражение, Esc — отмена."
            : _editor.HexSelectionSummary,
            nameof(EditorViewModel.HexSelection), nameof(EditorViewModel.HexSelectionSummary), nameof(EditorViewModel.HasHexPaste));
        var wand = Ui.PropertyToggle("HexSelectWandToggle", "Волшебная палочка: щелчок выделяет связную местность");
        wand.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.HexSelectWand = wand.IsChecked == true; };
        HexSync(() => wand.IsChecked = _editor.HexSelectWand, nameof(EditorViewModel.HexSelectWand));

        Button B(string name, string text, Action action) => Ui.TextButton(name, text, action, "compact");
        var actions = new WrapPanel
        {
            ItemSpacing = 6, LineSpacing = 6,
            Children =
            {
                B("HexCopyButton", "Копировать", _editor.CopyHexSelection),
                B("HexCutButton", "Вырезать", _editor.CutHexSelection),
                B("HexPasteButton", "Вставить", _editor.BeginHexPaste),
                B("HexMoveButton", "Перенести", _editor.MoveHexSelection),
                B("HexDeleteButton", "Очистить", _editor.DeleteHexSelection),
                B("HexFillSelectionButton", "Залить тайлом", _editor.FillHexSelection),
                B("HexTurnLeftButton", "Повернуть ⟲", () => _editor.RotateHexSelection(-1)),
                B("HexTurnRightButton", "Повернуть ⟳", () => _editor.RotateHexSelection(1)),
                B("HexMirrorButton", "Отразить вставку", _editor.MirrorHexPaste),
                B("HexSelectAllButton", "Выделить всё", _editor.SelectAllHexes),
                B("HexClearSelectionButton", "Снять выделение", _editor.ClearHexSelection),
            },
        };

        var columns = new TextBox { Name = "HexBoardColumnsBox", Width = 70 };
        var rows = new TextBox { Name = "HexBoardRowsBox", Width = 70 };
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

        var board = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = "Ряды и колонки вставляются перед первым выделенным гексом (или перед гексом под указателем). Чётное число колонок сохраняет соседство гексов справа.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
                new WrapPanel
                {
                    ItemSpacing = 6, LineSpacing = 6,
                    Children =
                    {
                        B("HexInsertColumnsButton", "+2 колонки", () => _editor.InsertHexColumns(2)),
                        B("HexDeleteColumnButton", "−1 колонка", () => _editor.InsertHexColumns(-1)),
                        B("HexInsertRowsButton", "+2 ряда", () => _editor.InsertHexRows(2)),
                        B("HexDeleteRowButton", "−1 ряд", () => _editor.InsertHexRows(-1)),
                    },
                },
                Ui.PropertyRow("Поле", new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { columns, new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center }, rows },
                }),
                B("HexResizeBoardButton", "Изменить размер поля (до 2048)", Resize),
            },
        };

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Ui.Section("Выделение", new StackPanel { Spacing = 6, Children = { info, wand, actions } }),
                Ui.Section("Поле", board),
            },
        };
    }

    // ------------------------------------------------------------------ levels

    private Control BuildHexLevelSection()
    {
        var levels = new WrapPanel { Name = "HexLevelList", ItemSpacing = 6, LineSpacing = 6 };
        HexSync(() =>
        {
            levels.Children.Clear();
            foreach (var level in _editor.HexLevels)
            {
                var value = level;
                var button = new ToggleButton
                {
                    Content = EditorViewModel.HexLevelName(level), IsChecked = level == _editor.HexLevel,
                }.Named("HexLevel_" + (level < 0 ? "m" + -level : level.ToString(CultureInfo.InvariantCulture))).Classed("compact");
                button.Click += (_, _) => _editor.HexLevel = value;
                levels.Children.Add(button);
            }
        }, [.. DocumentChanged, nameof(EditorViewModel.HexLevels)]);
        var ghost = Ui.PropertyToggle("HexGhostSurfaceToggle", "Показывать поверхность под подземельем");
        ghost.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.HexGhostSurface = ghost.IsChecked == true; };
        HexSync(() => ghost.IsChecked = _editor.HexGhostSurface, nameof(EditorViewModel.HexGhostSurface));
        return Ui.Section("Уровни мира", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                levels,
                new WrapPanel
                {
                    ItemSpacing = 6, LineSpacing = 6,
                    Children =
                    {
                        Ui.TextButton("HexAddLevelBelowButton", "+ Подземелье", () => _editor.AddHexLevel(below: true), "compact"),
                        Ui.TextButton("HexAddLevelAboveButton", "+ Уровень выше", () => _editor.AddHexLevel(below: false), "compact"),
                    },
                },
                ghost,
            },
        });
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
