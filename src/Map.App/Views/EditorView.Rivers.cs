using System;
using System.Linq;
using Avalonia.Controls;
using Map.App.Controls;
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
    private TextBlock? _riverShapeHint, _riverStyleHint;
    private Expander? _riverCreateCategory, _riverSelectCategory, _riverShapeCategory, _riverStyleCategory;
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

        var (levels, levelButtons) = Ui.TallSegmented("WaterSelectionLevel",
            [("System", "Система", MapIcons.LineRiver), ("Channel", "Русло", MapIcons.ModePath), ("Node", "Узлы", MapIcons.ModeSeries)],
            key => _editor.WaterLevel = key switch
            {
                "Node" => WaterSelectionLevel.Node,
                "Channel" => WaterSelectionLevel.Channel,
                _ => WaterSelectionLevel.System,
            });
        _riverLevelButtons = levelButtons;
        _riverSelectionSummary = new TextBlock { Name = "WaterSelectionSummary", TextWrapping = TextWrapping.Wrap };
        _riverHide = ActionIcon("ToggleWaterVisibleButton", "Скрыть воду", MapIcons.Eye, _editor.ToggleSelectedWaterVisible);
        _riverLock = ActionIcon("ToggleWaterLockButton", "Заблокировать воду", MapIcons.Lock, _editor.ToggleSelectedWaterLock);
        _riverDeleteSelection = ActionIcon("DeleteWaterSelectionButton", "Удалить выбранное (Delete)", MapIcons.Trash, () => _editor.DeleteSelection(), danger: true);
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
                Ui.Subgroup("Что выделяет щелчок", levels),
                _riverSelectionSummary,
                _riverSummary,
                ActionRow(_riverHide, _riverLock, _riverDeleteSelection),
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
        _riverNodeWidth = AppearanceNumberBox("RiverNodeWidthBox", "Ширина в узле", "ед.", () => _editor.SelectedRiverNodeWidth, _editor.SetSelectedRiverNodeWidth,
            slider: (1, 400), description: "Ширина русла в выбранном узле, в единицах карты. Между узлами ширина плавно переходит.");
        _riverNodeTension = AppearanceNumberBox("RiverNodeTensionBox", "Кривизна в узле", "%",
            () => _editor.SelectedRiverNodeTensionPercent, _editor.SetSelectedRiverNodeTensionPercent, slider: (0, 250),
            defaultValue: 100, description: "0 % — острый угол, 100 % — обычная плавная кривая, больше — размашистый изгиб.", step: 5);
        _riverNodeBlock = Ui.Subgroup("Узел", new StackPanel
        {
            Spacing = 4, Children =
            {
                _riverNodeWidth,
                _riverNodeTension,
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
                AppearanceNumberBox("RiverSourceWidthBox", "Ширина у истока", "ед.", () => _editor.SelectedChannelSourceWidth, _editor.SetSelectedChannelSourceWidth,
                    slider: (1, 400), description: "Ширина в первом узле русла. Профиль от истока к устью распределяется по всем узлам."),
                AppearanceNumberBox("RiverMouthWidthBox", "Ширина у устья", "ед.", () => _editor.SelectedChannelMouthWidth, _editor.SetSelectedChannelMouthWidth,
                    slider: (1, 400), description: "Ширина в последнем узле русла. Ромбы истока и устья перетаскиваются вдоль других русел и берегов озёр."),
            },
        };
        _riverChannelBlock = Ui.Subgroup("Русло", new StackPanel
        {
            Spacing = 4, Children =
            {
                _riverProfileBlock,
                AppearanceNumberBox("RiverChannelTensionBox", "Плавность всего русла", "%", () => _editor.SelectedChannelTensionPercent, _editor.SetSelectedChannelTensionPercent, slider: (0, 250),
                    defaultValue: 100, step: 5, description: "Кривизна сразу во всех узлах русла: 0 % — ломаная, 100 % — плавная линия, больше — широкие петли."),
                Ui.TextButton("SmoothRiverChannelButton", "Сгладить русло (без новых узлов)", _editor.SmoothSelectedChannel, "compact"),
                AppearanceNumberBox("RiverMeanderBox", "Извилистость", "%", () => _editor.RiverMeanderPercent, value => _editor.RiverMeanderPercent = value, slider: (1, 100),
                    defaultValue: 20, step: 5, description: "Сколько новых изгибов добавит «Показать извилины». Сначала показывается предпросмотр, затем его можно применить."),
                _riverMeander,
                _riverApplyPreview,
                Ui.Advanced("RiverChannelAdvanced", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        AppearanceNumberBox("RiverSimplifyToleranceBox", "Допуск упрощения", "ед.", () => _editor.RiverSimplifyTolerance, value => _editor.RiverSimplifyTolerance = value,
                            slider: (1, 64), defaultValue: 6, description: "Насколько линия может отойти от нынешней, когда «Упростить русло» убирает лишние узлы."),
                        Ui.TextButton("SimplifyRiverChannelButton", "Упростить русло", _editor.SimplifySelectedChannel, "compact"),
                        _riverReverse,
                        _riverDetach,
                        _riverDetachSource,
                    },
                }),
            },
        });

        _riverTaper = AppearanceToggle("RiverTaperSourceToggle", "Сужать исток", () => _editor.SelectedRiver?.Style.TaperSource, _editor.SetRiverTaperSource);
        _riverCutsLand = AppearanceToggle("RiverCutsLandToggle", "Формировать русло и берега", () => _editor.SelectedRiver?.Style.CutsLand, _editor.SetRiverCutsLand);
        ToolTip.SetTip(_riverCutsLand, "Река прорезает землю своего уровня и ниже. Земля более высокого уровня перекрывает реку.");
        _riverClipToLand = AppearanceToggle("RiverClipToLandToggle", "Переходить в море у берега", () => _editor.SelectedRiver?.Style.ClipToLand, _editor.SetRiverClipToLand);
        _riverFlowBlock = Ui.Subgroup("Ширина по течению", new StackPanel
        {
            Spacing = 4, Children =
            {
                Ui.Hint("Расчёт задаёт ширину каждому узлу: она растёт по длине и после каждого притока; рукав получает половину."),
                AppearanceNumberBox("RiverFlowSourceWidthBox", "Ширина истоков", "ед.", () => _editor.RiverFlowSourceWidth, value => _editor.RiverFlowSourceWidth = value,
                    slider: (1, 200), defaultValue: 4, description: "С какой ширины начинается каждый ручей у своего истока."),
                AppearanceNumberBox("RiverFlowGrowthBox", "Прирост на 1000 ед. длины", "ед.", () => _editor.RiverFlowGrowth, value => _editor.RiverFlowGrowth = value,
                    slider: (0, 200), defaultValue: 6, description: "На сколько единиц река становится шире на каждые 1000 единиц пути."),
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
                Ui.TextButton("RiverAddLabelButton", "Подпись вдоль реки", () => _editor.AddLabelAlongSelectedPath(Map.Core.Documents.TextPathTarget.River), "compact"),
                Ui.Subgroup("Готовые стили", new Ui.ChoiceGrid
                {
                    MaxColumns = 2, MinimumItemWidth = 100, Gap = 4, Children =
                    {
                        LookButton("RiverLookPainted", "Живописная", "Как горы владельца: контур пером, мазки, тень у южного берега", () => _editor.ApplyRiverLook(RiverLookPreset.Painted)),
                        LookButton("RiverLookMountainStream", "Горный ручей", "Узкое русло, неровные берега, тонкая отмель", () => _editor.ApplyRiverLook(RiverLookPreset.MountainStream)),
                        LookButton("RiverLookLowlandRiver", "Равнинная река", "Обычная ширина, спокойные берега", () => _editor.ApplyRiverLook(RiverLookPreset.LowlandRiver)),
                        LookButton("RiverLookWideRiver", "Широкая река", "Ширина ×1,7, широкая отмель и откос", () => _editor.ApplyRiverLook(RiverLookPreset.WideRiver)),
                        LookButton("RiverLookInkAtlas", "Атлас пером", "Рисунок пером, ширина не меняется", () => _editor.ApplyRiverLook(RiverLookPreset.InkAtlas)),
                    },
                }),
                Ui.Subgroup("Только рисунок", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        Ui.TextButton("RiverIllustratedStyleButton", "Рисованная карта", () => _editor.ApplyIllustratedRiverStyle(false), "compact"),
                        Ui.TextButton("RiverPenStyleButton", "Атлас пером", () => _editor.ApplyIllustratedRiverStyle(true), "compact"),
                    },
                }),
                Ui.Subgroup("Вода", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        AppearanceColorBox("RiverWaterColorBox", "Цвет воды", () => _editor.RiverWaterColor, _editor.SetRiverWaterColor),
                        BuildFillTexturePicker("RiverTexturePicker", Map.Core.Documents.MapFillTextureKind.Water,
                            () => _editor.RiverTextureId, _editor.SetRiverTexture,
                            // Water materials are laid over the water colour of the selected river.
                            () => MaterialPreviewContext.Over(_editor.RiverWaterColor ?? new Map.Core.Documents.RiverStyle().WaterColor,
                                _editor.RiverTextureOpacityPercent / 100 ?? new Map.Core.Documents.RiverStyle().TextureOpacity,
                                _editor.RiverTextureScale ?? Map.Core.Documents.MapFillTextures.DefaultWaterScale)),
                        AppearanceNumberBox("RiverTextureOpacityBox", "Заметность материала", "%",
                            () => _editor.RiverTextureOpacityPercent, _editor.SetRiverTextureOpacityPercent, slider: (0, 100),
                            defaultValue: 50, step: 5, description: "Насколько фактура воды проступает поверх цвета. 0 — только цвет."),
                        AppearanceNumberBox("RiverTextureScaleBox", "Крупность материала", "ед.",
                            () => _editor.RiverTextureScale, _editor.SetRiverTextureScale, slider: (16, 1024),
                            defaultValue: Map.Core.Documents.MapFillTextures.DefaultWaterScale, step: 8,
                            description: "Размер одной плитки фактуры воды в единицах карты."),
                        AppearanceNumberBox("RiverOpacityBox", "Непрозрачность", "%", () => _editor.SelectedRiver is { } r ? r.Style.Opacity * 100 : null, _editor.SetRiverOpacityPercent, slider: (0, 100),
                            defaultValue: 100, step: 5, description: "Прозрачность всей реки вместе с берегами. Меньше 100 % — сквозь воду видна карта."),
                        AppearanceColorBox("RiverHighlightColorBox", "Цвет бликов", () => _editor.RiverHighlightColor, _editor.SetRiverHighlightColor),
                        AppearanceNumberBox("RiverHighlightBox", "Сила бликов", "%", () => _editor.SelectedRiver is { } r ? r.Style.HighlightOpacity * 100 : null, _editor.SetRiverHighlightPercent, slider: (0, 100),
                            defaultValue: 55, step: 5, description: "Светлые штрихи по течению, как отблески на воде. 0 выключает."),
                    },
                }),
                Ui.Subgroup("Берег", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        AppearanceColorBox("RiverBankColorBox", "Цвет берега", () => _editor.RiverBankColor, _editor.SetRiverBankColor),
                        AppearanceNumberBox("RiverBankWidthBox", "Линия берега", "px", () => _editor.SelectedRiver?.Style.BankWidth, _editor.SetRiverBankWidth,
                            slider: (0, 32), defaultValue: 2, description: "Толщина тёмной линии по урезу воды. 0 — без линии."),
                        AppearanceNumberBox("RiverBankShadowBox", "Тень под берегом", "%", () => _editor.SelectedRiver is { } r ? r.Style.BankShadowOpacity * 100 : null, _editor.SetRiverBankShadowPercent, slider: (0, 100),
                            defaultValue: 28, step: 5, description: "Затемнение воды вдоль берега — река кажется глубже."),
                        AppearanceNumberBox("RiverEdgeRoughnessBox", "Неровность линии", "%", () => _editor.SelectedRiver is { } r ? r.Style.EdgeRoughness * 100 : null, _editor.SetRiverEdgeRoughnessPercent, slider: (0, 100),
                            defaultValue: 0, step: 5, description: "Дрожание линии берега, как у рисунка от руки. 0 — ровная линия."),
                        _riverCutsLand,
                    },
                }),
                Ui.Subgroup("Откос и отмель", new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        AppearanceColorBox("RiverBankReliefColorBox", "Цвет откоса", () => _editor.SelectedRiver?.Style.BankReliefColor, _editor.SetRiverBankReliefColor),
                        AppearanceNumberBox("RiverBankReliefBox", "Ширина откоса", "ед.", () => _editor.SelectedRiver?.Style.BankReliefWidth, _editor.SetRiverBankReliefWidth,
                            slider: (0, 200), defaultValue: 0, description: "Полоса голой земли между водой и сушей. 0 — без откоса."),
                        AppearanceNumberBox("RiverBankHatchBox", "Штриховка откоса", "%", () => _editor.SelectedRiver is { } r ? r.Style.BankHatchOpacity * 100 : null, _editor.SetRiverBankHatchPercent, slider: (0, 100),
                            defaultValue: 55, step: 5, description: "Перьевые штрихи по откосу. Видны только при ширине откоса больше 0."),
                        AppearanceColorBox("RiverShallowColorBox", "Цвет отмели", () => _editor.RiverShallowColor, _editor.SetRiverShallowColor),
                        AppearanceNumberBox("RiverShallowWidthBox", "Ширина отмели", "ед.",
                            () => _editor.RiverShallowWidth, _editor.SetRiverShallowWidth, slider: (0, 200),
                            defaultValue: 3, description: "Светлая полоса мелководья вдоль берега внутри реки. 0 — без отмели."),
                    },
                }),
                Ui.Subgroup("Размер, исток и устье", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        AppearanceNumberBox("RiverWidthScaleBox", "Ширина всей реки", "%", () => _editor.SelectedRiver is { } r ? r.WidthScale * 100 : null, _editor.SetRiverWidthScalePercent, slider: (10, 400),
                            defaultValue: 100, step: 5, description: "Множитель ширины сразу для всех узлов и русел реки. Форма не меняется."),
                        _riverTaper,
                        AppearanceNumberBox("RiverMouthFadeBox", "Растворение устья", "ед.", () => _editor.SelectedRiver?.Style.MouthFade, _editor.SetRiverMouthFade,
                            slider: (0, 400), defaultValue: 0, description: "На какой длине перед устьем река растворяется в море или озере. 0 — чёткий край."),
                        _riverClipToLand,
                    },
                }),
            },
        };

        // Four categories in the order of the work: make it, choose it, shape it, dress it.
        // The same four accents and the same order are used by the land panel.
        // Four tabs in the order of the work: make it, choose it, shape it, dress it. A tab is
        // offered while its category is: creation for the tool, the rest for a selected one.
        return Ui.CategoryTabs("RiverTabs",
            new Ui.CategoryTab("Create", "Создание", MapIcons.LineSparkle, (_riverCreateCategory = Ui.Category("RiverCreateCategory", "Создание", "accent-create", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        modes,
                        HeroButton("StartRiverButton", "Новая река", "Клики от истока к устью; клик по руслу — приток или рукав",
                            MapIcons.SolidRiver, _editor.StartRiver),
                        Ui.TextButton("StartLakeButton", "Новое озеро", _editor.StartLake, "compact"),
                        _riverFinish,
                        AppearanceNumberBox("NewRiverWidthBox", "Ширина новых узлов", "ед.", () => _editor.NewRiverWidth, value => _editor.NewRiverWidth = value,
                            slider: (1, 200), defaultValue: 12, description: "Ширина, с которой ставится каждый новый узел реки или озера."),
                        Ui.Hint("Узлы: клики от истока к устью, Enter — свободный конец. Клик по руслу — рукав или приток. Esc — отмена."),
                    },
                })), "Новая река или озеро и ширина новых узлов"),
            new Ui.CategoryTab("Select", "Выбор", MapIcons.LineTarget, (_riverSelectCategory = Ui.Category("RiverSelectCategory", "Выбор", "accent-select", _riverSelectionBlock)), "Какую воду и что в ней выделяет щелчок; скрыть, заблокировать, удалить"),
            new Ui.CategoryTab("Shape", "Форма", MapIcons.ModePath, (_riverShapeCategory = Ui.Category("RiverShapeCategory", "Форма", "accent-shape", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        (_riverShapeHint = (TextBlock)Ui.Hint("Выберите реку или озеро в списке выше.").Named("RiverShapeHint")),
                        _riverNodeBlock, _riverChannelBlock, _riverFlowBlock,
                    },
                }, expanded: false)), "Ширина и кривизна узлов, всё русло или участок"),
            new Ui.CategoryTab("Style", "Вид", MapIcons.LineWater, (_riverStyleCategory = Ui.Category("RiverStyleCategory", "Оформление", "accent-look", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        (_riverStyleHint = (TextBlock)Ui.Hint("Выберите реку или озеро в списке выше.").Named("RiverStyleHint")),
                        _riverStyleBlock,
                    },
                }, expanded: false)), "Материал, цвет, берега и оформление"));
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
            SetActionIcon(_riverHide, _editor.SelectedWaterVisible ? "Скрыть воду" : "Показать воду", _editor.SelectedWaterVisible ? MapIcons.Eye : MapIcons.EyeOff);
            _riverLock!.IsEnabled = hasWater;
            SetActionIcon(_riverLock, _editor.SelectedWaterLocked ? "Разблокировать воду" : "Заблокировать воду", _editor.SelectedWaterLocked ? MapIcons.Unlock : MapIcons.Lock);
            _riverDeleteSelection!.IsEnabled = hasWater;
            // One delete for the whole panel, and it says out loud what it is about to take.
            SetActionIcon(_riverDeleteSelection, _editor.WaterLevel switch
            {
                WaterSelectionLevel.Node => "Удалить узел (Delete)",
                WaterSelectionLevel.Channel => _editor.SelectedChannelIsLake ? "Удалить озеро (Delete)" : "Удалить русло (Delete)",
                _ => _editor.WaterSelectionCount > 1 ? "Удалить выбранные системы (Delete)" : "Удалить реку целиком (Delete)",
            }, MapIcons.Trash);
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
            var hint = _editor.IsCreatingRiver
                ? "Завершите рисование кнопкой выше или Enter — затем здесь можно изменить форму и оформление."
                : rivers.Length > 0 ? "Выберите реку или озеро на карте либо в списке выше."
                    : "Создайте реку или озеро — здесь появятся их настройки.";
            _riverShapeHint!.Text = hint;
            _riverStyleHint!.Text = hint;
            _riverShapeHint.IsVisible = !_riverNodeBlock.IsVisible && !_riverChannelBlock.IsVisible && !_riverFlowBlock.IsVisible;
            _riverStyleHint.IsVisible = !hasRiver;
            if (_riverName is not null && !_riverName.IsFocused) _riverName.Text = _editor.SelectedRiver?.Name ?? "";
            SyncAppearanceSections();
        }
        finally { _syncingRivers = false; }
        SyncSurfaceScope();
    }

    private sealed record RiverChoice(Guid Id, string Name)
    { public override string ToString() => Name; }

    private sealed record LayerChoice(Map.Core.Documents.LayerId Id, string Name)
    { public override string ToString() => Name; }
}
