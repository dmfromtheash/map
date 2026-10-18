using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
/// The tool panel of the hex constructor: what a press does (place, fill, erase, pick), the
/// palette of ready painted hexes grouped like the classic hex mappers group them, the size of
/// the footprint, the variants, and the grid of the board.
/// </summary>
public sealed partial class EditorView
{
    private static readonly Dictionary<(string Id, int Variant), Bitmap> HexThumbnails = [];

    private readonly Dictionary<string, ToggleButton> _hexTileButtons = new(StringComparer.Ordinal);
    private readonly List<Action> _hexSyncs = [];
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

    private Control BuildHexPanel()
    {
        var summary = new TextBlock { Name = "HexBoardSummary", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        _hexSyncs.Add(() => summary.Text = _editor.HexBoardSummary);

        var (modes, modeButtons) = Ui.Segmented("HexMode",
        [
            (nameof(HexToolMode.Place), "Поставить", null),
            (nameof(HexToolMode.Fill), "Залить", null),
            (nameof(HexToolMode.Erase), "Стереть", null),
            (nameof(HexToolMode.Pick), "Пипетка", null),
        ], key => _editor.HexMode = Enum.Parse<HexToolMode>(key));
        _hexSyncs.Add(() =>
        {
            foreach (var button in modeButtons.OfType<ToggleButton>())
            {
                var active = button.Name == "HexMode" + _editor.HexMode;
                button.IsChecked = active;
                Ui.SetClass(button, "active", active);
            }
        });
        var modeHint = new TextBlock { Name = "HexModeHint", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        _hexSyncs.Add(() => modeHint.Text = _editor.HexMode switch
        {
            HexToolMode.Place => "Щелчок ставит гекс, протягивание — линию гексов. Площадка больше одного гекса — ползунком ниже.",
            HexToolMode.Fill => "Щелчок заливает всю связную область одной местности (или пустую область) выбранным гексом.",
            HexToolMode.Erase => "Снимает верх гекса: сначала надстройку, затем местность.",
            _ => "Щелчок по гексу берёт его тайл и вариант в палитру.",
        });

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

        var current = new TextBlock { Name = "HexCurrentTile", TextWrapping = TextWrapping.Wrap };
        _hexSyncs.Add(() => current.Text = $"{_editor.HexTile.Name} · "
            + (_editor.HexTile.Target == HexPaintTarget.Feature ? "надстройка, ставится на местность" : "местность")
            + $" · вариантов: {_editor.HexTile.VariantCount}");

        var radius = ValueRow("HexRadiusSlider", "HexRadiusBox", null, "Площадка", 0, HexEditing.MaxRadius, "гекс.",
            () => _editor.HexRadius, (value, _) => _editor.HexRadius = (int)value, 1, 0,
            "Сколько гексов вокруг указателя ставит один щелчок: 0 — один гекс, 1 — семь, 2 — девятнадцать.");

        var random = Ui.PropertyToggle("HexRandomVariantToggle", "Случайный вариант на каждый гекс");
        random.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.HexRandomVariant = random.IsChecked == true; };
        _hexSyncs.Add(() => random.IsChecked = _editor.HexRandomVariant);
        var shuffle = Ui.TextButton("HexShuffleButton", "Перемешать варианты", _editor.ShuffleHexVariants, "compact");
        ToolTip.SetTip(shuffle, "Следующие гексы получат другие случайные картинки; уже поставленные не меняются.");

        var variants = new WrapPanel { Name = "HexVariantStrip", Orientation = Orientation.Horizontal };
        _hexSyncs.Add(() =>
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
        });

        var gridVisible = Ui.PropertyToggle("HexGridVisibleToggle", "Показывать сетку");
        var gridNumbers = Ui.PropertyToggle("HexGridNumbersToggle", "Номера гексов (0101)");
        var gridEmpty = Ui.PropertyToggle("HexGridEmptyToggle", "Контуры пустых гексов");
        gridVisible.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.SetHexGrid(grid => grid with { Visible = gridVisible.IsChecked == true }); };
        gridNumbers.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.SetHexGrid(grid => grid with { Numbers = gridNumbers.IsChecked == true }); };
        gridEmpty.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.SetHexGrid(grid => grid with { ShowEmpty = gridEmpty.IsChecked == true }); };
        _hexSyncs.Add(() =>
        {
            var grid = _editor.HexGrid ?? new HexGridStyle();
            gridVisible.IsChecked = grid.Visible;
            gridNumbers.IsChecked = grid.Numbers;
            gridEmpty.IsChecked = grid.ShowEmpty;
        });

        var panel = new StackPanel
        {
            Name = "HexToolPanel",
            Spacing = 12,
            Children =
            {
                summary,
                Ui.Section("Что делает щелчок", new StackPanel { Spacing = 6, Children = { modes, modeHint } }),
                Ui.Section("Гексы", new StackPanel { Spacing = 6, Children = { current, palette } }),
                Ui.Section("Площадка", radius),
                Ui.Section("Варианты", new StackPanel { Spacing = 6, Children = { random, variants, shuffle } }),
                Ui.Section("Сетка", new StackPanel { Spacing = 4, Children = { gridVisible, gridNumbers, gridEmpty } }),
            },
        };

        ((INotifyPropertyChanged)_editor).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } name && (name.StartsWith("Hex", StringComparison.Ordinal) || name is nameof(EditorViewModel.CanUndo)))
            {
                SyncHexPanel();
            }
        };
        SyncHexPanel();
        return panel;
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
        ToolTip.SetTip(button, tile.Target == HexPaintTarget.Feature
            ? $"{tile.Name} — надстройка: ставится поверх местности"
            : $"{tile.Name} — гекс местности, вариантов: {tile.VariantCount}");
        button.Click += (_, _) =>
        {
            _editor.HexTileId = tile.Id;
            if (_editor.ActiveTool != EditorTool.Hex) _editor.ActiveTool = EditorTool.Hex;
            SyncHexPanel();
        };
        _hexTileButtons[tile.Id] = button;
        return button;
    }

    private void SyncHexPanel()
    {
        if (_syncingHex) return;
        _syncingHex = true;
        try
        {
            foreach (var (id, button) in _hexTileButtons)
            {
                var active = id == _editor.HexTileId;
                button.IsChecked = active;
                Ui.SetClass(button, "active", active);
            }

            foreach (var sync in _hexSyncs) sync();
        }
        finally
        {
            _syncingHex = false;
        }
    }
}
