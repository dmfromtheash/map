using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>Stage 19: the river tool panel — creation, node, channel, flow and appearance of one water system.</summary>
public sealed partial class EditorView
{
    private ComboBox? _riverPicker, _riverChannelPicker;
    private TextBlock? _riverSummary;
    private Button? _riverFinish, _riverDetach, _riverDetachSource, _riverDeleteChannel, _riverSplit, _riverSplitOff, _riverTrimBefore, _riverTrimAfter, _riverReverse, _riverMeander;
    private Button? _riverApplyPreview, _riverHide, _riverLock, _riverDeleteSelection;
    private Control? _riverNodeBlock, _riverChannelBlock, _riverStyleBlock, _riverNodeWidth, _riverProfileBlock, _riverFlowBlock;
    private Control? _riverSelectionBlock, _riverNodeTension;
    private ComboBox? _riverLayerPicker;
    private ToggleButton? _riverSharp, _riverTaper, _riverCutsLand, _riverClipToLand;
    private TextBox? _riverName;
    private TextBlock? _riverSelectionSummary;
    private System.Collections.Generic.IReadOnlyList<Button>? _riverModeButtons, _riverLevelButtons;
    private bool _syncingRivers;

    private Control BuildRiverPanel()
    {
        _riverPicker = new ComboBox { Name = "RiverPicker", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, PlaceholderText = "Реки и озёра этой карты" };
        _riverPicker.SelectionChanged += (_, _) =>
        {
            if (!_syncingRivers && _riverPicker.SelectedItem is RiverChoice item) _editor.SelectRiver(item.Id);
        };
        _riverChannelPicker = new ComboBox { Name = "RiverChannelPicker", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, PlaceholderText = "Русла реки" };
        _riverChannelPicker.SelectionChanged += (_, _) =>
        {
            if (!_syncingRivers && _riverChannelPicker.SelectedItem is RiverChoice item) _editor.SelectRiverChannel(item.Id);
        };
        _riverSummary = new TextBlock { Name = "RiverSummary", TextWrapping = TextWrapping.Wrap };
        _riverFinish = Ui.TextButton("FinishRiverButton", "Завершить (Enter)", () => _editor.FinishRiver(), "compact");
        _riverName = new TextBox { Name = "RiverNameBox", PlaceholderText = "Название реки или озера" };
        _riverName.LostFocus += (_, _) => { if (!_syncingRivers) _editor.SetRiverName(_riverName.Text ?? ""); };
        _riverName.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { _editor.SetRiverName(_riverName.Text ?? ""); e.Handled = true; } };

        var (modes, modeButtons) = Ui.Segmented("RiverDrawMode",
            [("Nodes", "Узлы", null), ("Freehand", "От руки", null)],
            key => _editor.RiverDrawMode = key == "Freehand" ? RiverDrawMode.Freehand : RiverDrawMode.Nodes);
        _riverModeButtons = modeButtons;

        var (levels, levelButtons) = Ui.Segmented("WaterSelectionLevel",
            [("System", "Вся система", null), ("Channel", "Русло / озеро", null), ("Node", "Узлы", null)],
            key => _editor.WaterLevel = key switch
            {
                "Node" => WaterSelectionLevel.Node,
                "Channel" => WaterSelectionLevel.Channel,
                _ => WaterSelectionLevel.System,
            });
        _riverLevelButtons = levelButtons;
        _riverSelectionSummary = new TextBlock { Name = "WaterSelectionSummary", TextWrapping = TextWrapping.Wrap };
        _riverHide = Ui.TextButton("ToggleWaterVisibleButton", "Скрыть воду", _editor.ToggleSelectedWaterVisible, "compact");
        _riverLock = Ui.TextButton("ToggleWaterLockButton", "Заблокировать воду", _editor.ToggleSelectedWaterLock, "compact");
        _riverDeleteSelection = Ui.TextButton("DeleteWaterSelectionButton", "Удалить выбранное (Delete)",
            () => _editor.DeleteSelection(), "compact", "destructive");
        Avalonia.Automation.AutomationProperties.SetName(_riverDeleteSelection, "Удалить выбранное");
        _riverLayerPicker = new ComboBox { Name = "WaterLayerPicker", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            PlaceholderText = "Перенести воду на слой" };
        _riverLayerPicker.SelectionChanged += (_, _) =>
        {
            if (!_syncingRivers && _riverLayerPicker.SelectedItem is LayerChoice choice) _editor.MoveSelectedWaterToLayer(choice.Id);
        };
        _riverDeleteChannel = Ui.TextButton("DeleteRiverChannelButton", "Удалить русло", _editor.DeleteSelectedChannel, "compact", "destructive");
        _riverSelectionBlock = new StackPanel
        {
            Spacing = 4, Children =
            {
                _riverPicker,
                _riverChannelPicker,
                levels,
                Ui.Hint("Уровень решает, к чему относятся перетаскивание и Delete."),
                _riverSelectionSummary,
                _riverSummary,
                _riverHide, _riverLock, _riverDeleteSelection,
                Ui.Advanced("RiverSelectionAdvanced", new StackPanel
                {
                    Spacing = 4, Children = { _riverLayerPicker, _riverDeleteChannel },
                }),
            },
        };

        _riverSharp = AppearanceToggle("RiverNodeSharpToggle", "Острый узел", () => _editor.SelectedRiverNodeSharp, _editor.SetSelectedRiverNodeSharp);
        _riverSplit = Ui.TextButton("SplitRiverChannelButton", "Разделить русло в узле", _editor.SplitSelectedChannel, "compact");
        _riverSplitOff = Ui.TextButton("DetachDownstreamRiverButton", "Отделить нижнее течение в отдельную реку", _editor.DetachDownstreamAsRiver, "compact");
        _riverTrimBefore = Ui.TextButton("TrimRiverBeforeButton", "Обрезать всё выше узла", _editor.TrimSelectedChannelBeforeNode, "compact");
        _riverTrimAfter = Ui.TextButton("TrimRiverAfterButton", "Обрезать всё ниже узла", _editor.TrimSelectedChannelAfterNode, "compact");
        _riverNodeWidth = AppearanceNumberBox("RiverNodeWidthBox", "Ширина в узле", "ед.", () => _editor.SelectedRiverNodeWidth, _editor.SetSelectedRiverNodeWidth);
        _riverNodeTension = AppearanceNumberBox("RiverNodeTensionBox", "Кривизна узла", "%",
            () => _editor.SelectedRiverNodeTensionPercent, _editor.SetSelectedRiverNodeTensionPercent);
        _riverNodeBlock = Ui.Subgroup("Узел", new StackPanel
        {
            Spacing = 4, Children =
            {
                _riverNodeWidth,
                _riverNodeTension,
                Ui.Hint("0 % — угол, 100 % — обычная кривая, больше — шире размах."),
                _riverSharp,
                Ui.TextButton("InsertRiverNodeButton", "Добавить узел на русле", _editor.InsertRiverNode, "compact"),
                Ui.TextButton("DeleteRiverNodeButton", "Удалить выбранный узел (Delete)", () => _editor.DeleteSelectedRiverNode(), "compact"),
                Ui.Advanced("RiverNodeAdvanced", new StackPanel
                {
                    Spacing = 4, Children = { _riverSplit, _riverSplitOff, _riverTrimBefore, _riverTrimAfter },
                }),
            },
        });

        _riverDetach = Ui.TextButton("DetachRiverChannelButton", "Отделить устье (свободный конец)", _editor.DetachSelectedChannel, "compact");
        _riverDetachSource = Ui.TextButton("DetachRiverSourceButton", "Отделить исток (свободный исток)", _editor.DetachSelectedChannelSource, "compact");
        _riverReverse = Ui.TextButton("ReverseRiverChannelButton", "Развернуть течение", _editor.ReverseSelectedChannel, "compact");
        _riverMeander = Ui.TextButton("MeanderRiverChannelButton", "Показать извилины", _editor.PreviewMeanderOnSelectedChannel, "compact");
        _riverApplyPreview = Ui.TextButton("ApplyRiverPreviewButton", "Применить извилины", () => _editor.ApplyRiverPreview(), "compact");
        _riverProfileBlock = new StackPanel
        {
            Spacing = 4, Children =
            {
                AppearanceNumberBox("RiverSourceWidthBox", "Ширина истока", "ед.", () => _editor.SelectedChannelSourceWidth, _editor.SetSelectedChannelSourceWidth),
                AppearanceNumberBox("RiverMouthWidthBox", "Ширина устья", "ед.", () => _editor.SelectedChannelMouthWidth, _editor.SetSelectedChannelMouthWidth),
                new TextBlock { Text = "Профиль от истока к устью распределяется по всем узлам русла. Ромбы истока и устья перетаскиваются вдоль других русел и берегов озёр.", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption"),
            },
        };
        _riverChannelBlock = Ui.Subgroup("Русло", new StackPanel
        {
            Spacing = 4, Children =
            {
                _riverProfileBlock,
                AppearanceNumberBox("RiverChannelTensionBox", "Плавность русла", "%", () => _editor.SelectedChannelTensionPercent, _editor.SetSelectedChannelTensionPercent),
                Ui.TextButton("SmoothRiverChannelButton", "Сгладить русло (без новых узлов)", _editor.SmoothSelectedChannel, "compact"),
                AppearanceNumberBox("RiverMeanderBox", "Извилины", "%", () => _editor.RiverMeanderPercent, value => _editor.RiverMeanderPercent = value),
                _riverMeander,
                _riverApplyPreview,
                Ui.Advanced("RiverChannelAdvanced", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        AppearanceNumberBox("RiverSimplifyToleranceBox", "Точность упрощения", "ед.", () => _editor.RiverSimplifyTolerance, value => _editor.RiverSimplifyTolerance = value),
                        Ui.TextButton("SimplifyRiverChannelButton", "Упростить русло", _editor.SimplifySelectedChannel, "compact"),
                        _riverReverse,
                        _riverDetach,
                        _riverDetachSource,
                    },
                }),
            },
        });

        _riverTaper = AppearanceToggle("RiverTaperSourceToggle", "Сужать исток", () => _editor.SelectedRiver?.Style.TaperSource, _editor.SetRiverTaperSource);
        _riverCutsLand = AppearanceToggle("RiverCutsLandToggle", "Резать землю (берег огибает реку)", () => _editor.SelectedRiver?.Style.CutsLand, _editor.SetRiverCutsLand);
        _riverClipToLand = AppearanceToggle("RiverClipToLandToggle", "Только по суше (обрывать у берега)", () => _editor.SelectedRiver?.Style.ClipToLand, _editor.SetRiverClipToLand);
        _riverFlowBlock = Ui.Subgroup("Ширина по течению", new StackPanel
        {
            Spacing = 4, Children =
            {
                Ui.Hint("Ширина растёт по длине и после каждого притока; рукав получает половину."),
                AppearanceNumberBox("RiverFlowSourceWidthBox", "Ширина истоков", "ед.", () => _editor.RiverFlowSourceWidth, value => _editor.RiverFlowSourceWidth = value),
                AppearanceNumberBox("RiverFlowGrowthBox", "Рост на 1000 ед.", "ед.", () => _editor.RiverFlowGrowth, value => _editor.RiverFlowGrowth = value),
                Ui.TextButton("ApplyRiverFlowWidthButton", "Рассчитать ширину по притокам", _editor.ApplyWidthByFlow, "compact"),
            },
        });
        // Water, then material, then the bank: the same order the land panel uses, so the two
        // read as one system. Everything below that is behind a single disclosure.
        _riverStyleBlock = new StackPanel
        {
            Spacing = 4, Children =
            {
                _riverName,
                Ui.Subgroup("Вода", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        AppearanceColorBox("RiverWaterColorBox", "Цвет воды", () => _editor.RiverWaterColor, _editor.SetRiverWaterColor),
                        BuildFillTexturePicker("RiverTexturePicker", Map.Core.Documents.MapFillTextureKind.Water,
                            () => _editor.RiverTextureId, _editor.SetRiverTexture),
                        AppearanceNumberBox("RiverTextureOpacityBox", "Сила материала", "%",
                            () => _editor.RiverTextureOpacityPercent, _editor.SetRiverTextureOpacityPercent),
                        AppearanceNumberBox("RiverShallowWidthBox", "Отмель у берега", "ед.",
                            () => _editor.RiverShallowWidth, _editor.SetRiverShallowWidth),
                    },
                }),
                Ui.Subgroup("Берег", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        AppearanceColorBox("RiverBankColorBox", "Цвет берега", () => _editor.RiverBankColor, _editor.SetRiverBankColor),
                        AppearanceNumberBox("RiverBankWidthBox", "Кромка берега", "px", () => _editor.SelectedRiver?.Style.BankWidth, _editor.SetRiverBankWidth),
                    },
                }),
                Ui.Advanced("RiverStyleAdvanced", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        AppearanceNumberBox("RiverWidthScaleBox", "Ширина всей реки", "%", () => _editor.SelectedRiver is { } r ? r.WidthScale * 100 : null, _editor.SetRiverWidthScalePercent),
                        AppearanceNumberBox("RiverTextureScaleBox", "Размер материала", "ед.",
                            () => _editor.RiverTextureScale, _editor.SetRiverTextureScale),
                        AppearanceColorBox("RiverShallowColorBox", "Цвет отмели", () => _editor.RiverShallowColor, _editor.SetRiverShallowColor),
                        AppearanceNumberBox("RiverBankShadowBox", "Тень берега", "%", () => _editor.SelectedRiver is { } r ? r.Style.BankShadowOpacity * 100 : null, _editor.SetRiverBankShadowPercent),
                        AppearanceNumberBox("RiverEdgeRoughnessBox", "Шероховатость кромки", "%", () => _editor.SelectedRiver is { } r ? r.Style.EdgeRoughness * 100 : null, _editor.SetRiverEdgeRoughnessPercent),
                        AppearanceColorBox("RiverHighlightColorBox", "Блики", () => _editor.RiverHighlightColor, _editor.SetRiverHighlightColor),
                        AppearanceNumberBox("RiverHighlightBox", "Сила бликов", "%", () => _editor.SelectedRiver is { } r ? r.Style.HighlightOpacity * 100 : null, _editor.SetRiverHighlightPercent),
                        AppearanceNumberBox("RiverOpacityBox", "Непрозрачность", "%", () => _editor.SelectedRiver is { } r ? r.Style.Opacity * 100 : null, _editor.SetRiverOpacityPercent),
                        _riverTaper,
                        AppearanceNumberBox("RiverMouthFadeBox", "Растворять устье", "ед.", () => _editor.SelectedRiver?.Style.MouthFade, _editor.SetRiverMouthFade),
                        _riverCutsLand,
                        _riverClipToLand,
                    },
                }),
            },
        };

        // Four categories in the order of the work: make it, choose it, shape it, dress it.
        // The same four accents and the same order are used by the land panel.
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Ui.Category("RiverCreateCategory", "Создание", "accent-create", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        modes,
                        Ui.TextButton("StartRiverButton", "Новая река, приток или рукав", _editor.StartRiver, "compact"),
                        Ui.TextButton("StartLakeButton", "Новое озеро", _editor.StartLake, "compact"),
                        _riverFinish,
                        AppearanceNumberBox("NewRiverWidthBox", "Ширина новых узлов", "ед.", () => _editor.NewRiverWidth, value => _editor.NewRiverWidth = value),
                        Ui.Hint("Узлы: клики от истока к устью, Enter — свободный конец. Клик по руслу — рукав или приток. Esc — отмена."),
                    },
                }),
                Ui.Category("RiverSelectCategory", "Выбор", "accent-select", _riverSelectionBlock),
                Ui.Category("RiverShapeCategory", "Форма", "accent-shape", new StackPanel
                {
                    Spacing = 4, Children = { _riverNodeBlock, _riverChannelBlock, _riverFlowBlock },
                }, expanded: false),
                Ui.Category("RiverStyleCategory", "Оформление", "accent-look", _riverStyleBlock, expanded: false),
            },
        };
    }

    private void SyncRiverPanel()
    {
        if (_riverPicker is null || _riverChannelPicker is null || _riverSummary is null) return;
        _syncingRivers = true;
        try
        {
            var rivers = _editor.Rivers.Select(r => new RiverChoice(r.Id, r.Name)).ToArray();
            if (_riverPicker.ItemsSource is not RiverChoice[] previous || !previous.SequenceEqual(rivers))
                _riverPicker.ItemsSource = rivers;
            _riverPicker.SelectedItem = rivers.FirstOrDefault(r => r.Id == _editor.SelectedRiver?.Id);
            var channels = _editor.SelectedRiver?.Channels.Select(c => new RiverChoice(c.Id, c.Name + (c.Closed ? " ◯" : c.Mouth is null ? "" : " ↳") + (c.Source is null ? "" : " ↰"))).ToArray() ?? [];
            if (_riverChannelPicker.ItemsSource is not RiverChoice[] previousChannels || !previousChannels.SequenceEqual(channels))
                _riverChannelPicker.ItemsSource = channels;
            _riverChannelPicker.SelectedItem = channels.FirstOrDefault(c => c.Id == _editor.SelectedRiverChannel?.Id);
            _riverChannelPicker.IsVisible = channels.Length > 1;
            _riverSummary.Text = _editor.RiverSummary;
            _riverFinish!.IsVisible = _editor.IsCreatingRiver;
            _riverFinish.Content = _editor.IsCreatingLake ? "Замкнуть озеро (Enter)" : "Завершить (Enter)";
            if (_riverLevelButtons is not null)
            {
                var level = _editor.WaterLevel switch
                {
                    WaterSelectionLevel.Node => 2, WaterSelectionLevel.Channel => 1, _ => 0,
                };
                for (var i = 0; i < _riverLevelButtons.Count; i++)
                {
                    var active = i == level;
                    ((ToggleButton)_riverLevelButtons[i]).IsChecked = active; Ui.SetClass(_riverLevelButtons[i], "active", active);
                }
            }
            _riverSelectionSummary!.Text = _editor.WaterSelectionSummary;
            var hasWater = _editor.WaterSelectionCount > 0;
            _riverSelectionBlock!.IsVisible = true;
            _riverHide!.IsEnabled = hasWater;
            _riverHide.Content = _editor.SelectedWaterVisible ? "Скрыть воду" : "Показать воду";
            _riverLock!.IsEnabled = hasWater;
            _riverLock.Content = _editor.SelectedWaterLocked ? "Разблокировать воду" : "Заблокировать воду";
            _riverDeleteSelection!.IsEnabled = hasWater;
            // One delete for the whole panel, and it says out loud what it is about to take.
            _riverDeleteSelection.Content = _editor.WaterLevel switch
            {
                WaterSelectionLevel.Node => "Удалить узел (Delete)",
                WaterSelectionLevel.Channel => _editor.SelectedChannelIsLake ? "Удалить озеро (Delete)" : "Удалить русло (Delete)",
                _ => _editor.WaterSelectionCount > 1 ? "Удалить выбранные системы (Delete)" : "Удалить реку целиком (Delete)",
            };
            _riverDeleteChannel!.IsVisible = _editor.SelectedRiver is { Channels.Length: > 1 };
            _riverApplyPreview!.IsVisible = _editor.HasRiverPreview;
            var layers = _editor.Layers.OfType<ObjectLayer>().Select(l => new LayerChoice(l.Id, l.Name)).ToArray();
            if (_riverLayerPicker!.ItemsSource is not LayerChoice[] previousLayers || !previousLayers.SequenceEqual(layers))
                _riverLayerPicker.ItemsSource = layers;
            _riverLayerPicker.SelectedItem = null;
            _riverLayerPicker.IsEnabled = hasWater && layers.Length > 1;
            if (_riverModeButtons is not null)
                for (var i = 0; i < _riverModeButtons.Count; i++)
                {
                    var active = i == (_editor.RiverDrawMode == RiverDrawMode.Nodes ? 0 : 1);
                    ((ToggleButton)_riverModeButtons[i]).IsChecked = active; Ui.SetClass(_riverModeButtons[i], "active", active);
                }
            var hasRiver = _editor.SelectedRiver is not null && !_editor.IsCreatingRiver;
            var lake = _editor.SelectedChannelIsLake;
            _riverNodeBlock!.IsVisible = hasRiver && _editor.IsWaterNodeMode && _editor.SelectedRiverNodeSharp is not null;
            _riverNodeWidth!.IsVisible = !lake;
            _riverSplit!.IsVisible = !lake; _riverSplitOff!.IsVisible = !lake; _riverTrimBefore!.IsVisible = !lake; _riverTrimAfter!.IsVisible = !lake;
            _riverSplit.IsEnabled = _editor.CanSplitSelectedChannel; _riverSplitOff.IsEnabled = _editor.CanSplitSelectedChannel;
            _riverChannelBlock!.IsVisible = hasRiver && _editor.SelectedRiverChannel is not null;
            _riverStyleBlock!.IsVisible = hasRiver;
            _riverProfileBlock!.IsVisible = !lake;
            _riverReverse!.IsVisible = !lake; _riverMeander!.IsVisible = !lake;
            _riverDetach!.IsVisible = _editor.SelectedChannelIsTributary;
            _riverDetachSource!.IsVisible = _editor.SelectedChannelHasSource;
            _riverFlowBlock!.IsVisible = hasRiver && _editor.SelectedRiver!.Channels.Any(c => !c.Closed);
            if (_riverName is not null && !_riverName.IsFocused) _riverName.Text = _editor.SelectedRiver?.Name ?? "";
            SyncAppearanceSections();
        }
        finally { _syncingRivers = false; }
    }

    private sealed record RiverChoice(Guid Id, string Name)
    { public override string ToString() => Name; }

    private sealed record LayerChoice(Map.Core.Documents.LayerId Id, string Name)
    { public override string ToString() => Name; }
}
