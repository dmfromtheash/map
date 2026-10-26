using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Map.App.Controls;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>Stage 20: the road tool panel - creation, selection level, node, branch and appearance of one road network.</summary>
public sealed partial class EditorView
{
    private ComboBox? _roadPicker, _roadBranchPicker, _roadLayerPicker;
    private TextBlock? _roadSummary, _roadSelectionSummary, _roadStyleTargetText;
    private Button? _roadFinish, _roadHide, _roadLock, _roadDeleteSelection, _roadDeleteBranch, _roadDetachBranch, _roadDuplicate;
    private Button? _roadSplit, _roadDetachVertex, _roadClose;
    private Control? _roadNodeBlock, _roadBranchBlock, _roadStyleBlock, _roadSelectionBlock, _roadProfileBlock;
    private ToggleButton? _roadSharp, _roadOwnStyle;
    private TextBox? _roadName, _roadBranchName;
    private System.Collections.Generic.IReadOnlyList<Button>? _roadModeButtons, _roadLevelButtons, _roadPresetButtons, _roadStartCapButtons, _roadEndCapButtons;
    private bool _syncingRoads;
    private Expander? _roadCreateCategory, _roadSelectCategory, _roadShapeCategory, _roadStyleCategory;

    private Control BuildRoadPanel()
    {
        _roadPicker = new ComboBox { Name = "RoadPicker", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, PlaceholderText = "Дороги этой карты" };
        _roadPicker.SelectionChanged += (_, _) =>
        {
            if (!_syncingRoads && _roadPicker.SelectedItem is RiverChoice item) _editor.SelectRoad(item.Id);
        };
        _roadBranchPicker = new ComboBox { Name = "RoadBranchPicker", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, PlaceholderText = "Участки дороги" };
        _roadBranchPicker.SelectionChanged += (_, _) =>
        {
            if (!_syncingRoads && _roadBranchPicker.SelectedItem is RiverChoice item) _editor.SelectRoadBranch(item.Id);
        };
        _roadSummary = new TextBlock { Name = "RoadSummary", TextWrapping = TextWrapping.Wrap };
        _roadFinish = Ui.TextButton("FinishRoadButton", "Завершить (Enter)", () => _editor.FinishRoad(), "compact");
        _roadName = new TextBox { Name = "RoadNameBox", PlaceholderText = "Название дороги" };
        _roadName.LostFocus += (_, _) => { if (!_syncingRoads) _editor.SetRoadName(_roadName.Text ?? ""); };
        _roadName.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { _editor.SetRoadName(_roadName.Text ?? ""); e.Handled = true; } };
        _roadBranchName = new TextBox { Name = "RoadBranchNameBox", PlaceholderText = "Название участка" };
        _roadBranchName.LostFocus += (_, _) => { if (!_syncingRoads) _editor.SetSelectedBranchName(_roadBranchName.Text ?? ""); };
        _roadBranchName.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { _editor.SetSelectedBranchName(_roadBranchName.Text ?? ""); e.Handled = true; } };

        var (modes, modeButtons) = Ui.Segmented("RoadDrawMode",
            [("Nodes", "Узлы", null), ("Freehand", "От руки", null)],
            key => _editor.RoadDrawMode = key == "Freehand" ? RoadDrawMode.Freehand : RoadDrawMode.Nodes);
        _roadModeButtons = modeButtons;

        var (levels, levelButtons) = Ui.TallSegmented("RoadSelectionLevel",
            [("Road", "Дорога", MapIcons.LineRoad), ("Branch", "Участок", MapIcons.ModePath), ("Node", "Узлы", MapIcons.ModeSeries)],
            key => _editor.RoadLevel = key switch
            {
                "Node" => RoadSelectionLevel.Node,
                "Branch" => RoadSelectionLevel.Branch,
                _ => RoadSelectionLevel.Road,
            });
        _roadLevelButtons = levelButtons;
        _roadSelectionSummary = new TextBlock { Name = "RoadSelectionSummary", TextWrapping = TextWrapping.Wrap };
        _roadHide = ActionIcon("ToggleRoadVisibleButton", "Скрыть дорогу", MapIcons.Eye, _editor.ToggleSelectedRoadVisible);
        _roadLock = ActionIcon("ToggleRoadLockButton", "Заблокировать дорогу", MapIcons.Lock, _editor.ToggleSelectedRoadLock);
        _roadDuplicate = ActionIcon("DuplicateRoadButton", "Дублировать дорогу", MapIcons.Duplicate, () => _editor.DuplicateSelectedRoads());
        _roadDeleteSelection = ActionIcon("DeleteRoadSelectionButton", "Удалить выбранное (Delete)", MapIcons.Trash, () => _editor.DeleteSelection(), danger: true);
        Avalonia.Automation.AutomationProperties.SetName(_roadDeleteSelection, "Удалить выбранное");
        _roadLayerPicker = new ComboBox { Name = "RoadLayerPicker", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, PlaceholderText = "Перенести дорогу на слой" };
        _roadLayerPicker.SelectionChanged += (_, _) =>
        {
            if (!_syncingRoads && _roadLayerPicker.SelectedItem is LayerChoice choice) _editor.MoveSelectedRoadsToLayer(choice.Id);
        };
        _roadDeleteBranch = Ui.TextButton("DeleteRoadBranchButton", "Удалить участок", _editor.DeleteSelectedBranch, "compact", "destructive");
        _roadDetachBranch = Ui.TextButton("DetachRoadBranchButton", "Отделить участок в отдельную дорогу", _editor.DetachSelectedBranchAsRoad, "compact");
        _roadSelectionBlock = new StackPanel
        {
            Spacing = 4, Children =
            {
                _roadPicker,
                _roadBranchPicker,
                Ui.Subgroup("Что выделяет щелчок", levels),
                _roadSelectionSummary,
                _roadSummary,
                ActionRow(_roadHide, _roadLock, _roadDuplicate, _roadDeleteSelection),
                Ui.Advanced("RoadSelectionAdvanced", new StackPanel
                {
                    Spacing = 4, Children = { _roadLayerPicker, _roadDetachBranch, _roadDeleteBranch },
                }),
            },
        };

        _roadSharp = AppearanceToggle("RoadVertexSharpToggle", "Острый узел", () => _editor.SelectedRoadVertexSharp, _editor.SetSelectedRoadVertexSharp);
        _roadSplit = Ui.TextButton("SplitRoadBranchButton", "Разделить участок в узле", _editor.SplitSelectedBranch, "compact");
        _roadDetachVertex = Ui.TextButton("DetachRoadVertexButton", "Отсоединить участок от общего узла", _editor.DetachSelectedRoadVertex, "compact");
        _roadNodeBlock = Ui.Subgroup("Узел", new StackPanel
        {
            Spacing = 4, Children =
            {
                AppearanceNumberBox("RoadVertexWidthBox", "Ширина в узле", "ед.", () => _editor.SelectedRoadVertexWidth, _editor.SetSelectedRoadVertexWidth),
                AppearanceNumberBox("RoadVertexTensionBox", "Кривизна узла", "%",
                    () => _editor.SelectedRoadVertexTensionPercent, _editor.SetSelectedRoadVertexTensionPercent, slider: (0, 250)),
                Ui.Hint("0 % — угол, 100 % — обычная кривая, больше — шире размах. Общий узел двигает все ветви."),
                _roadSharp,
                Ui.TextButton("InsertRoadVertexButton", "Добавить узел на участке", _editor.InsertRoadVertex, "compact"),
                Ui.TextButton("DeleteRoadVertexButton", "Удалить выбранный узел (Delete)", () => _editor.DeleteSelectedRoadVertex(), "compact"),
                Ui.Advanced("RoadNodeAdvanced", new StackPanel
                {
                    Spacing = 4, Children = { _roadSplit, _roadDetachVertex },
                }),
            },
        });

        _roadClose = Ui.TextButton("ToggleRoadBranchClosedButton", "Замкнуть маршрут", _editor.ToggleSelectedBranchClosed, "compact");
        _roadProfileBlock = new StackPanel
        {
            Spacing = 4, Children =
            {
                AppearanceNumberBox("RoadBranchStartWidthBox", "Ширина в начале", "ед.", () => _editor.SelectedBranchStartWidth, _editor.SetSelectedBranchStartWidth),
                AppearanceNumberBox("RoadBranchEndWidthBox", "Ширина в конце", "ед.", () => _editor.SelectedBranchEndWidth, _editor.SetSelectedBranchEndWidth),
                Ui.Hint("Профиль распределяется по всем узлам участка; общий узел сохраняет соединение."),
            },
        };
        _roadBranchBlock = Ui.Subgroup("Участок", new StackPanel
        {
            Spacing = 4, Children =
            {
                _roadBranchName,
                Ui.TextButton("RoadAddLabelButton", "Подпись вдоль участка", () => _editor.AddLabelAlongSelectedPath(Map.Core.Documents.TextPathTarget.Road), "compact"),
                AppearanceNumberBox("RoadBranchWidthBox", "Ширина участка", "ед.", () => _editor.SelectedBranchWidth, _editor.SetSelectedBranchWidth),
                _roadProfileBlock,
                BuildRoadCrossingsBlock(),
                AppearanceNumberBox("RoadBranchTensionBox", "Плавность участка", "%", () => _editor.SelectedBranchTensionPercent, _editor.SetSelectedBranchTensionPercent, slider: (0, 250)),
                Ui.TextButton("SmoothRoadBranchButton", "Сгладить участок (без новых узлов)", _editor.SmoothSelectedBranch, "compact"),
                _roadClose,
                Ui.Advanced("RoadBranchAdvanced", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        AppearanceNumberBox("RoadSimplifyToleranceBox", "Точность упрощения", "ед.", () => _editor.RoadSimplifyTolerance, value => _editor.RoadSimplifyTolerance = value),
                        Ui.TextButton("SimplifyRoadBranchButton", "Упростить участок", _editor.SimplifySelectedBranch, "compact"),
                        Ui.TextButton("ReverseRoadBranchButton", "Развернуть участок", _editor.ReverseSelectedBranch, "compact"),
                    },
                }),
            },
        });

        var (presets, presetButtons) = Ui.Segmented("RoadPreset",
            [("Country", "Просёлочная", null), ("PenRuts", "Перо и колея", null), ("Painterly", "Мягкая живописная", null)],
            key => _editor.ApplyRoadPreset(key switch
            {
                "PenRuts" => RoadDrawing.PenRuts,
                "Painterly" => RoadDrawing.Painterly,
                _ => RoadDrawing.Country,
            }));
        _roadPresetButtons = presetButtons;
        _roadOwnStyle = AppearanceToggle("RoadBranchOwnStyleToggle", "Свой стиль у этого участка", () => _editor.SelectedBranchHasOwnStyle, _editor.SetSelectedBranchOwnStyle);
        _roadStyleTargetText = new TextBlock { Name = "RoadStyleTarget", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        var (startCaps, startCapButtons) = Ui.Segmented("RoadStartCap",
            [("Flat", "Ровно", null), ("Round", "Округло", null), ("Taper", "Затухает", null)],
            key => _editor.SetRoadStartCap(ParseCap(key)));
        _roadStartCapButtons = startCapButtons;
        var (endCaps, endCapButtons) = Ui.Segmented("RoadEndCap",
            [("Flat", "Ровно", null), ("Round", "Округло", null), ("Taper", "Затухает", null)],
            key => _editor.SetRoadEndCap(ParseCap(key)));
        _roadEndCapButtons = endCapButtons;

        _roadStyleBlock = new StackPanel
        {
            Spacing = 4, Children =
            {
                _roadName,
                _roadStyleTargetText,
                _roadOwnStyle,
                Ui.Subgroup("Готовые стили", new Ui.ChoiceGrid
                {
                    MaxColumns = 2, MinimumItemWidth = 100, Gap = 4, Children =
                    {
                        LookButton("RoadLookPainted", "Как горы", "Как горы владельца: контур пером, мазки, тень от света с северо-запада", () => _editor.ApplyRoadLook(RoadLookPreset.Painted)),
                        LookButton("RoadLookTrail", "Тропа", "Узкая, без колеи, прерывистая кромка", () => _editor.ApplyRoadLook(RoadLookPreset.Trail)),
                        LookButton("RoadLookCountryRoad", "Просёлок", "Обычная грунтовая дорога", () => _editor.ApplyRoadLook(RoadLookPreset.CountryRoad)),
                        LookButton("RoadLookHighway", "Тракт", "Широкая, перо и колея", () => _editor.ApplyRoadLook(RoadLookPreset.Highway)),
                        LookButton("RoadLookPainterly", "Живописный путь", "Мягкий живописный рисунок", () => _editor.ApplyRoadLook(RoadLookPreset.Painterly)),
                    },
                }),
                Ui.Subgroup("Способ рисунка", presets),
                Ui.Subgroup("Полотно", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        AppearanceColorBox("RoadSurfaceColorBox", "Цвет дороги", () => _editor.RoadSurfaceColor, _editor.SetRoadSurfaceColor),
                        AppearanceColorBox("RoadToneColorBox", "Тон грунта", () => _editor.RoadToneColor, _editor.SetRoadToneColor),
                        AppearanceNumberBox("RoadToneVariationBox", "Вариация тона", "%", () => _editor.RoadStyleShown is { } s ? s.ToneVariation * 100 : null, _editor.SetRoadToneVariationPercent, slider: (0, 100)),
                        AppearanceNumberBox("RoadEdgeRoughnessBox", "Неровность края", "%", () => _editor.RoadStyleShown is { } s ? s.EdgeRoughness * 100 : null, _editor.SetRoadEdgeRoughnessPercent, slider: (0, 100)),
                        BuildFillTexturePicker("RoadTexturePicker", MapFillTextureKind.Ground, () => _editor.RoadTextureId, _editor.SetRoadTexture,
                            // A road material is laid over the road's surface colour (the tone patches come on top of both).
                            () => MaterialPreviewContext.Over(_editor.RoadSurfaceColor ?? new RoadStyle().SurfaceColor,
                                _editor.RoadTextureOpacityPercent / 100 ?? new RoadStyle().TextureOpacity, _editor.RoadStyleShown?.TextureScale)),
                        AppearanceNumberBox("RoadTextureOpacityBox", "Сила материала", "%", () => _editor.RoadTextureOpacityPercent, _editor.SetRoadTextureOpacityPercent, slider: (0, 100)),
                    },
                }),
                Ui.Subgroup("Обочина и кромка", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        AppearanceColorBox("RoadShoulderColorBox", "Цвет обочины", () => _editor.RoadShoulderColor, _editor.SetRoadShoulderColor),
                        AppearanceNumberBox("RoadShoulderWidthBox", "Ширина обочины", "ед.", () => _editor.RoadStyleShown?.ShoulderWidth, _editor.SetRoadShoulderWidth),
                        AppearanceNumberBox("RoadShoulderOpacityBox", "Сила обочины", "%", () => _editor.RoadStyleShown is { } s ? s.ShoulderOpacity * 100 : null, _editor.SetRoadShoulderPercent, slider: (0, 100)),
                        AppearanceColorBox("RoadEdgeColorBox", "Цвет кромки", () => _editor.RoadEdgeColor, _editor.SetRoadEdgeColor),
                        AppearanceNumberBox("RoadEdgeWidthBox", "Толщина кромки", "ед.", () => _editor.RoadStyleShown?.EdgeWidth, _editor.SetRoadEdgeWidth, "0.##"),
                        AppearanceNumberBox("RoadEdgeOpacityBox", "Сила кромки", "%", () => _editor.RoadStyleShown is { } s ? s.EdgeOpacity * 100 : null, _editor.SetRoadEdgePercent, slider: (0, 100)),
                        AppearanceNumberBox("RoadEdgeBreakBox", "Прерывистость кромки", "%", () => _editor.RoadStyleShown is { } s ? s.EdgeBreak * 100 : null, _editor.SetRoadEdgeBreakPercent, slider: (0, 100)),
                    },
                }),
                Ui.Subgroup("Колея и детали", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        AppearanceColorBox("RoadRutColorBox", "Цвет колеи", () => _editor.RoadRutColor, _editor.SetRoadRutColor),
                        AppearanceNumberBox("RoadRutOpacityBox", "Сила колеи", "%", () => _editor.RoadStyleShown is { } s ? s.RutOpacity * 100 : null, _editor.SetRoadRutPercent, slider: (0, 100)),
                        AppearanceNumberBox("RoadDetailOpacityBox", "Детали у кромки", "%", () => _editor.RoadStyleShown is { } s ? s.DetailOpacity * 100 : null, _editor.SetRoadDetailPercent, slider: (0, 100)),
                    },
                }),
                Ui.Advanced("RoadStyleAdvanced", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        AppearanceNumberBox("RoadWidthScaleBox", "Ширина всей дороги", "%", () => _editor.SelectedRoad is { } r ? r.WidthScale * 100 : null, _editor.SetRoadWidthScalePercent, slider: (10, 400)),
                        AppearanceNumberBox("RoadOpacityBox", "Непрозрачность", "%", () => _editor.RoadStyleShown is { } s ? s.Opacity * 100 : null, _editor.SetRoadOpacityPercent, slider: (0, 100)),
                        AppearanceNumberBox("RoadTextureScaleBox", "Размер материала", "ед.", () => _editor.RoadTextureScale, _editor.SetRoadTextureScale),
                        Ui.Subgroup("Начало участка", startCaps),
                        Ui.Subgroup("Конец участка", endCaps),
                        AppearanceNumberBox("RoadTaperLengthBox", "Длина затухания", "ед.", () => _editor.RoadStyleShown?.TaperLength, _editor.SetRoadTaperLength),
                        Ui.Hint("0 — длина затухания берётся от ширины."),
                    },
                }),
            },
        };

        // Four tabs in the order of the work: make it, choose it, shape it, dress it. A tab is
        // offered while its category is: creation for the tool, the rest for a selected one.
        return Ui.CategoryTabs("RoadTabs",
            new Ui.CategoryTab("Create", "Создание", MapIcons.LineSparkle, (_roadCreateCategory = Ui.Category("RoadCreateCategory", "Создание", "accent-create", new StackPanel
                {
                    Spacing = 4, Children =
                    {
                        modes,
                        HeroButton("StartRoadButton", "Новая дорога", "Клики по холсту; клик по дороге — ветвь от общего узла",
                            MapIcons.SolidRoad, _editor.StartRoad),
                        Ui.TextButton("AutoRoadButton", "Проложить дорогу A→B", _editor.ArmAutoRoad, "compact")
                            .WithTip("Два щелчка: откуда и куда. Дорога пойдёт по суше самым лёгким путём — в обход моря, крутых склонов и обрывов."),
                        _roadFinish,
                        AppearanceNumberBox("NewRoadWidthBox", "Ширина новых узлов", "ед.", () => _editor.NewRoadWidth, value => _editor.NewRoadWidth = value),
                        Ui.Hint("Узлы: клики по холсту. Первый клик по дороге — ветвь от общего узла; клик по дороге с узлами в черновике — соединение; клик по первому узлу — замкнуть. Enter — свободный конец, Esc — отмена."),
                    },
                })), "Новая дорога и ширина новых узлов"),
            new Ui.CategoryTab("Select", "Выбор", MapIcons.LineTarget, (_roadSelectCategory = Ui.Category("RoadSelectCategory", "Выбор", "accent-select", _roadSelectionBlock)), "Какую дорогу и что в ней выделяет щелчок; скрыть, заблокировать, удалить"),
            new Ui.CategoryTab("Shape", "Форма", MapIcons.ModePath, (_roadShapeCategory = Ui.Category("RoadShapeCategory", "Форма", "accent-shape", new StackPanel
                {
                    Spacing = 4, Children = { _roadNodeBlock, _roadBranchBlock },
                }, expanded: false)), "Ширина и кривизна узлов, всё русло или участок"),
            new Ui.CategoryTab("Style", "Вид", MapIcons.LineRoad, (_roadStyleCategory = Ui.Category("RoadStyleCategory", "Оформление", "accent-look", _roadStyleBlock, expanded: false)), "Материал, цвет, берега и оформление"));
    }

    private static RoadEndCap ParseCap(string key) => key switch
    {
        "Flat" => RoadEndCap.Flat,
        "Taper" => RoadEndCap.Taper,
        _ => RoadEndCap.Round,
    };

    private void SyncRoadPanel()
    {
        if (_roadPicker is null || _roadBranchPicker is null || _roadSummary is null) return;
        _syncingRoads = true;
        try
        {
            var roads = _editor.Roads.Select(r => new RiverChoice(r.Id, r.Name)).ToArray();
            if (_roadPicker.ItemsSource is not RiverChoice[] previous || !previous.SequenceEqual(roads))
                _roadPicker.ItemsSource = roads;
            _roadPicker.SelectedItem = roads.FirstOrDefault(r => r.Id == _editor.SelectedRoad?.Id);
            var branches = _editor.SelectedRoad?.Branches.Select(b => new RiverChoice(b.Id, b.Name + (b.Closed ? " ◯" : "") + (b.Style is null ? "" : " ✎"))).ToArray() ?? [];
            if (_roadBranchPicker.ItemsSource is not RiverChoice[] previousBranches || !previousBranches.SequenceEqual(branches))
                _roadBranchPicker.ItemsSource = branches;
            _roadBranchPicker.SelectedItem = branches.FirstOrDefault(b => b.Id == _editor.SelectedRoadBranch?.Id);
            _roadBranchPicker.IsVisible = branches.Length > 1;
            _roadSummary.Text = _editor.RoadSummary;
            _roadFinish!.IsVisible = _editor.IsCreatingRoad;
            if (_roadLevelButtons is not null)
            {
                var level = _editor.RoadLevel switch { RoadSelectionLevel.Node => 2, RoadSelectionLevel.Branch => 1, _ => 0 };
                for (var i = 0; i < _roadLevelButtons.Count; i++)
                {
                    var active = i == level;
                    ((ToggleButton)_roadLevelButtons[i]).IsChecked = active; Ui.SetClass(_roadLevelButtons[i], "active", active);
                }
            }
            _roadSelectionSummary!.Text = _editor.RoadSelectionSummary;
            var hasRoads = _editor.RoadSelectionCount > 0;
            _roadHide!.IsEnabled = hasRoads;
            SetActionIcon(_roadHide, _editor.SelectedRoadVisible ? "Скрыть дорогу" : "Показать дорогу", _editor.SelectedRoadVisible ? MapIcons.Eye : MapIcons.EyeOff);
            _roadLock!.IsEnabled = hasRoads;
            SetActionIcon(_roadLock, _editor.SelectedRoadLocked ? "Разблокировать дорогу" : "Заблокировать дорогу", _editor.SelectedRoadLocked ? MapIcons.Unlock : MapIcons.Lock);
            _roadDuplicate!.IsEnabled = hasRoads;
            _roadDeleteSelection!.IsEnabled = hasRoads;
            SetActionIcon(_roadDeleteSelection, _editor.RoadLevel switch
            {
                RoadSelectionLevel.Node => "Удалить узел (Delete)",
                RoadSelectionLevel.Branch => "Удалить участок (Delete)",
                _ => _editor.RoadSelectionCount > 1 ? "Удалить выбранные дороги (Delete)" : "Удалить дорогу целиком (Delete)",
            }, MapIcons.Trash);
            _roadDeleteBranch!.IsVisible = _editor.SelectedRoad is { Branches.Length: > 1 };
            _roadDetachBranch!.IsVisible = _editor.SelectedRoad is { Branches.Length: > 1 };
            var layers = _editor.Layers.OfType<ObjectLayer>().Select(l => new LayerChoice(l.Id, l.Name)).ToArray();
            if (_roadLayerPicker!.ItemsSource is not LayerChoice[] previousLayers || !previousLayers.SequenceEqual(layers))
                _roadLayerPicker.ItemsSource = layers;
            _roadLayerPicker.SelectedItem = null;
            _roadLayerPicker.IsEnabled = hasRoads && layers.Length > 1;
            if (_roadModeButtons is not null)
                for (var i = 0; i < _roadModeButtons.Count; i++)
                {
                    var active = i == (_editor.RoadDrawMode == RoadDrawMode.Nodes ? 0 : 1);
                    ((ToggleButton)_roadModeButtons[i]).IsChecked = active; Ui.SetClass(_roadModeButtons[i], "active", active);
                }
            var hasRoad = _editor.SelectedRoad is not null && !_editor.IsCreatingRoad;
            _roadNodeBlock!.IsVisible = hasRoad && _editor.IsRoadNodeMode && _editor.SelectedRoadVertexSharp is not null;
            _roadSplit!.IsEnabled = _editor.CanSplitSelectedBranch;
            _roadDetachVertex!.IsEnabled = _editor.SelectedRoadVertexIsShared;
            _roadBranchBlock!.IsVisible = hasRoad && _editor.SelectedRoadBranch is not null;
            _roadProfileBlock!.IsVisible = !_editor.SelectedBranchIsClosed;
            _roadClose!.Content = _editor.SelectedBranchIsClosed ? "Разомкнуть маршрут" : "Замкнуть маршрут";
            _roadStyleBlock!.IsVisible = hasRoad;
            _roadOwnStyle!.IsVisible = hasRoad && _editor.RoadLevel != RoadSelectionLevel.Road && _editor.SelectedRoadBranch is not null;
            _roadStyleTargetText!.Text = _editor.RoadStyleTargetsBranch
                ? "Оформление ниже относится к выбранному участку; узел и остальные участки не меняются."
                : "Оформление ниже относится ко всей дороге; участок со своим стилем сохраняет его.";
            if (_roadPresetButtons is not null)
            {
                var shown = _editor.RoadDrawingShown;
                for (var i = 0; i < _roadPresetButtons.Count; i++)
                {
                    var active = shown is { } d && (int)d == i;
                    ((ToggleButton)_roadPresetButtons[i]).IsChecked = active; Ui.SetClass(_roadPresetButtons[i], "active", active);
                }
            }
            SyncCaps(_roadStartCapButtons, _editor.RoadStyleShown?.StartCap);
            SyncCaps(_roadEndCapButtons, _editor.RoadStyleShown?.EndCap);
            if (_roadName is not null && !_roadName.IsFocused) _roadName.Text = _editor.SelectedRoad?.Name ?? "";
            if (_roadBranchName is not null && !_roadBranchName.IsFocused) _roadBranchName.Text = _editor.SelectedRoadBranch?.Name ?? "";
            SyncRoadCrossings();
            SyncAppearanceSections();
        }
        finally { _syncingRoads = false; }
        SyncSurfaceScope();

        static void SyncCaps(System.Collections.Generic.IReadOnlyList<Button>? buttons, RoadEndCap? cap)
        {
            if (buttons is null) return;
            for (var i = 0; i < buttons.Count; i++)
            {
                var active = cap is { } c && (int)c == i;
                ((ToggleButton)buttons[i]).IsChecked = active; Ui.SetClass(buttons[i], "active", active);
            }
        }
    }

    /// <summary>
    /// One road in the objects panel: selecting it is the same selection the canvas uses, and the
    /// two switches beside it hide and lock that road alone.
    /// </summary>
    private Border BuildRoadRow(ObjectTreeRow row)
    {
        var roadId = row.RoadId!.Value;
        var visible = Ui.RowAction(
            $"RoadRowVisible_{roadId:D}",
            row.WaterVisible ? "Скрыть дорогу" : "Показать дорогу",
            row.WaterVisible ? MapIcons.Eye : MapIcons.EyeOff,
            () =>
            {
                _editor.SelectRoadOnly(roadId);
                _editor.SetSelectedRoadVisible(!row.WaterVisible);
                SyncObjectList();
                SyncContextArea();
            });
        var locked = Ui.RowAction(
            $"RoadRowLocked_{roadId:D}",
            row.WaterLocked ? "Разблокировать дорогу" : "Заблокировать дорогу",
            row.WaterLocked ? MapIcons.Lock : MapIcons.Unlock,
            () =>
            {
                _editor.SelectRoadOnly(roadId);
                _editor.SetSelectedRoadLocked(!row.WaterLocked);
                SyncObjectList();
                SyncContextArea();
            });
        var states = row.WaterVisible ? row.WaterLocked ? " · заблокирована" : "" : " · скрыта";
        var owner = row.LayerName is { } layerName ? $" · слой «{layerName}»" : "";
        var host = Ui.StructureRow(
            row.AutomationId,
            MapIcons.Road,
            row.Title,
            row.Subtitle + " · уровень " + EditorViewModel.FormatSublayer(row.Level) + states + owner,
            () =>
            {
                _editor.SelectRoadOnly(roadId);
                SyncObjectListSelection();
                SyncContextArea();
            },
            $"Дорога «{row.Title}» · {row.Subtitle}{states}",
            visible,
            locked);
        Ui.SetStructureSelected(host, row.Selected);
        return host;
    }

    /// <summary>
    /// Rivers and roads split like every other object: the "Объект" tab holds what belongs to the
    /// selected water or road (choice, shape, style); "Инструмент" holds only creating new ones.
    /// With nothing selected the panel shows creation plus the picker to choose an existing one.
    /// </summary>
    private void SyncSurfaceScope()
    {
        void Apply(Expander? create, Expander? select, Expander? shape, Expander? style, bool selected, bool objectScope)
        {
            if (create is null || select is null || shape is null || style is null) return;
            create.IsVisible = !objectScope;
            select.IsVisible = objectScope || !selected;
            shape.IsVisible = objectScope;
            style.IsVisible = objectScope;
        }
        var water = _editor.WaterSelectionCount > 0 && !_editor.IsCreatingRiver;
        var road = _editor.RoadSelectionCount > 0;
        Apply(_riverCreateCategory, _riverSelectCategory, _riverShapeCategory, _riverStyleCategory,
            water, water && !_contextPrefersToolPanel);
        Apply(_roadCreateCategory, _roadSelectCategory, _roadShapeCategory, _roadStyleCategory,
            road, road && !_contextPrefersToolPanel);
    }

    /// <summary>True while the river or road panel shows the selected surface, not the tool.</summary>
    private bool RiverRoadObjectScope() => !_contextPrefersToolPanel && _editor.ActiveTool switch
    {
        EditorTool.River => _editor.WaterSelectionCount > 0 && !_editor.IsCreatingRiver,
        EditorTool.Road => _editor.RoadSelectionCount > 0,
        _ => false,
    };

    private bool RiverRoadHasSelection() => _editor.ActiveTool switch
    {
        EditorTool.River => _editor.WaterSelectionCount > 0 && !_editor.IsCreatingRiver,
        EditorTool.Road => _editor.RoadSelectionCount > 0,
        _ => false,
    };

    /// <summary>One named look: a short caption, the change it makes in the tooltip.</summary>
    private Button LookButton(string name, string text, string hint, Action apply)
    {
        var button = Ui.TextButton(name, text, () => { apply(); SyncAppearanceSections(); }, "compact", "centred");
        ToolTip.SetTip(button, text + ". " + hint + ". Отменяется одной командой.");
        return button;
    }
}
