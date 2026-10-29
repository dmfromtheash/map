using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Map.App.Controls;
using Map.App.Services;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class EditorView
{
    // ================================================================ side panel

    private Control BuildSidePanel()
    {
        static Control TabHeader(string text, string icon) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Children =
            {
                MapIcons.Stroked(icon, 15),
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
            },
        };

        _assetCatalogContent = BuildAssetsTab();
        _versionsContent = BuildCheckpointsTab();
        _canvasWorkflowContent = BuildCanvasWorkflowTab();
        _exportContent = BuildExportTab();
        _ = TabHeader("", "");

        var collapse = new Button { Content = MapIcons.Grid24(MapIcons.LinePanelRight, 16) }
            .Named("CollapseRightPanelButton").Classed("icon");
        collapse.Click += (_, _) => ToggleRightPanel();
        AutomationProperties.SetName(collapse, "Свернуть или развернуть правую рабочую панель");
        ToolTip.SetTip(collapse, "Свернуть или развернуть панель «Карта» (/)");
        // Land, water, assets, roads, the other objects and the layer stack are separate working
        // sections of one panel (see EditorView.PanelSections), not one long mixed list.
        _sideBody = BuildSectionedPanel();
        BuildSideStrip();
        _sidePanel = new Border
        {
            Width = Ui.SidePanelWidth,
            Child = new DockPanel
            {
                Children =
                {
                    new Border
                    {
                        Child = new DockPanel
                        {
                            Children =
                            {
                                collapse.Docked(Dock.Right),
                                (_sideTitle = new StackPanel
                                {
                                    Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center,
                                    Children =
                                    {
                                        MapIcons.Grid24(MapIcons.LineLayers, 17, Application.Current?.FindResource("MapAccentBrush") as IBrush),
                                        new TextBlock { Text = "Карта", VerticalAlignment = VerticalAlignment.Center }.Classed("panel-title"),
                                    },
                                }),
                            },
                        },
                    }.Classed("panel-header").Docked(Dock.Top),
                    _sideStrip.Docked(Dock.Top),
                    _sideBody,
                },
            },
        }.Classed("panel", "calm-modes");
        _sidePanel.Name = "EditorSidePanel";
        return _sidePanel;
    }

    private Control BuildLayersTab()
    {
        MenuItem AddLayer(string name, string title, Action action)
        {
            var item = new MenuItem { Name = name, Header = title };
            item.Click += (_, _) => action();
            return item;
        }

        var addMenu = _isHexWorkshop
            ? new MenuFlyout
            {
                Items =
                {
                    AddLayer("AddHexLayerButton", "Слой гексов", _editor.AddHexLayer),
                    AddLayer("AddObjectLayerButton", "Слой подписей", _editor.AddObjectLayer),
                },
            }
            : new MenuFlyout
            {
                Items =
                {
                    AddLayer("AddObjectLayerButton", "Слой объектов", _editor.AddObjectLayer),
                    AddLayer("AddBrushLayerButton", "Слой кисти", _editor.AddBrushLayer),
                    AddLayer("AddLandLayerButton", "Слой земли", _editor.AddLandLayer),
                    AddLayer("AddGridLayerButton", "Слой сетки", _editor.AddGridLayer),
                    AddLayer("AddFilterLayerButton", "Слой фильтра", _editor.AddFilterLayer),
                },
            };
        var addLayerButton = Ui.CommandButton("AddLayerButton", "Добавить слой", MapIcons.Plus, () => { }, "compact");
        addLayerButton.HorizontalAlignment = HorizontalAlignment.Left;
        addLayerButton.Flyout = addMenu;

        var properties = BuildLayerProperties();
        var body = new StackPanel
        {
            Margin = new Thickness(12, 8, 12, 18),
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Сверху — то, что рисуется поверх" }.Classed("faint", "role-caption"),
                _layerList,
                addLayerButton,
                properties,
            },
        };

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = body,
        };
    }

    /// <summary>
    /// The active layer's property sheet. It is a form, not a pile of verbs: one identity header,
    /// one row per setting carrying that setting's current value, and exactly one destructive
    /// action, separated and last.
    /// </summary>
    private Control BuildLayerProperties()
    {
        _layerKindIcon = new ContentControl
        {
            Content = MapIcons.Stroked(
                MapIcons.Layers, 16, Application.Current?.FindResource("MapAccentBrush") as IBrush),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        _layerNameHeading.Classes.Add("role-section");
        _layerNameHeading.TextTrimming = TextTrimming.CharacterEllipsis;
        _layerKindText.Classes.Add("role-caption");
        _layerKindText.Classes.Add("faint");

        _layerIdentity = new Border
        {
            IsVisible = false,
            Padding = new Thickness(10, 8),
            Background = Application.Current?.FindResource("MapElevatedBrush") as IBrush,
            CornerRadius = new CornerRadius(7),
            Child = new DockPanel
            {
                Children =
                {
                    new Border
                    {
                        Width = 40,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 10, 0),
                        Child = _layerKindIcon,
                    }.Docked(Dock.Left),
                    new StackPanel
                    {
                        Spacing = 1,
                        Children = { _layerNameHeading, _layerKindText },
                    },
                },
            },
        };

        _noLayerState = Ui.EmptyState(
            "NoActiveLayerState",
            MapIcons.Layers,
            "Слой не выбран",
            "Выберите слой в списке выше, чтобы изменить его свойства.");

        _layerCommonBlock = BuildLayerCommonBlock();
        _baseColorLayerBlock = BuildBaseColorLayerBlock();
        _landStylePointer = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Ui.Hint("Цвет, материал и берег земли — в панели инструмента «Земля»."),
                Ui.TextButton("OpenLandStyleButton", "Открыть оформление земли",
                    () => { _editor.ActiveTool = EditorTool.Land; SyncSidePanels(); }, "compact"),
            },
        };
        _gridLayerBlock = BuildGridLayerBlock();
        _filterLayerBlock = BuildFilterLayerBlock();

        return new Border
        {
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                    {
                        new TextBlock { Text = "Свойства слоя", VerticalAlignment = VerticalAlignment.Center }.Classed("role-panel"),
                        Ui.ScopeChip(Ui.Scope.Layer),
                    } },
                    _noLayerState,
                    _layerIdentity,
                    _layerCommonBlock,
                    _baseColorLayerBlock,
                    _landStylePointer,
                    _gridLayerBlock,
                    _filterLayerBlock,
                },
            },
        }.Classed("group");
    }

    /// <summary>The mandatory background layer: nothing to reorder or delete, but a real colour.</summary>
    private Control BuildBaseColorLayerBlock() => new StackPanel
    {
        Spacing = 4,
        Children =
        {
            new TextBlock
            {
                Text = "Фон карты нельзя удалить или скрыть; можно изменить его цвет.",
                TextWrapping = TextWrapping.Wrap,
            }.Classed("faint", "role-caption"),
            AppearanceColorBox(
                "BaseColorBox", "Цвет фона",
                () => _editor.Session.Document.BaseColor.Color,
                value => _editor.SetBaseColor(value)),
            // The sea of a map is its background: the same water material as rivers and lakes
            // gives it clouds, grain and wave strokes instead of a flat colour.
            BuildFillTexturePicker("BaseTexturePicker", MapFillTextureKind.Water,
                () => _editor.Session.Document.BaseColor.TextureId,
                id => _editor.SetBaseMaterial(id),
                () => MaterialPreviewContext.Over(_editor.Session.Document.BaseColor.Color,
                    _editor.Session.Document.BaseColor.TextureOpacity, _editor.Session.Document.BaseColor.TextureScale)),
        },
    }.Named("BaseColorLayerBlock");

    private Control BuildLayerCommonBlock()
    {
        var rename = Ui.IconButton("RenameLayerButton", "Применить новое имя слоя", MapIcons.Check,
            () => _editor.RenameActiveLayer(_layerName.Text ?? ""));
        _layerName.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                _editor.RenameActiveLayer(_layerName.Text ?? "");
                e.Handled = true;
            }
        };

        var nameField = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8,
            Children = { _layerName, rename },
        };
        Grid.SetColumn(rename, 1);
        var nameRow = new StackPanel { Spacing = 4, Children = { Ui.ParameterLabel("Имя"), nameField } };

        var (visibilityRow, visibilityButton) = PropertyValueRow(
            "LayerVisibilityButton", "Видимость", "Показан",
            [("Показан", () => { if (_editor.ActiveLayer?.Visible == false) _editor.ToggleActiveLayerVisibility(); }),
             ("Скрыт", () => { if (_editor.ActiveLayer?.Visible == true) _editor.ToggleActiveLayerVisibility(); })]);
        _visibilityValue = visibilityButton;

        var (lockRow, lockButton) = PropertyValueRow(
            "LayerLockButton", "Блокировка", "Свободен",
            [("Свободен", () => { if (_editor.ActiveLayer?.Locked == true) _editor.ToggleActiveLayerLock(); }),
             ("Заблокирован", () => { if (_editor.ActiveLayer?.Locked == false) _editor.ToggleActiveLayerLock(); })]);
        _lockValue = lockButton;

        var exactBlend = BlendChoice(
            "LayerBlendChoice", () => _editor.ActiveLayer?.Blend,
            value => _editor.SetActiveLayerBlend(value));
        var exactOpacity = AppearanceSlider(
            "LayerOpacitySlider", "Непрозрачность слоя", 0, 100, "%",
            () => _editor.ActiveLayer?.Opacity * 100,
            (value, _) => _editor.SetActiveLayerOpacity(value), 5);

        var maskChoice = PropertyChoice("LayerMaskButton", "Маска", "Нет", () =>
            _editor.ActiveLayer?.Mask is null
                ? [("Создать и включить", _editor.ToggleActiveMask)]
                : [
                    ("Включена", () => { if (_editor.ActiveLayer?.Mask?.Enabled == false) _editor.ToggleActiveMask(); }),
                    ("Выключена", () => { if (_editor.ActiveLayer?.Mask?.Enabled == true) _editor.ToggleActiveMask(); }),
                ]);
        var maskRow = Ui.PropertyRow("Маска", maskChoice);
        _maskValue = maskChoice.Header;
        var maskCommands = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.TextButton("CopyLayerMaskButton", "Копировать маску", _editor.CopyActiveMask, "compact"),
                Ui.TextButton("PasteLayerMaskButton", "Вставить копию", () => _editor.PasteCopiedMask(), "compact"),
                Ui.AsyncButton("RemoveLayerMaskButton", "Удалить маску…", RemoveActiveMaskAsync,
                    "destructive", "compact"),
            },
        };
        foreach (var command in maskCommands.Children)
            command.HorizontalAlignment = HorizontalAlignment.Stretch;

        var selectedSublayer = 0;
        LayerEffects? Effects() => _editor.ActiveObjectLayerEffects;
        void ChangeEffects(Func<LayerEffects, LayerEffects> change) =>
            _editor.SetActiveObjectLayerEffects(change(Effects() ?? new LayerEffects()));
        var objectEffects = new Ui.SectionPanel("ObjectLayerEffectsSection", "Тень, обрезка и подслои", new StackPanel
        {
            Spacing = 4,
            Children =
            {
                AppearanceToggle("ObjectLayerClippingToggle", "Обрезать по слою ниже",
                    () => Effects()?.ClippingEnabled,
                    value => ChangeEffects(current => current with { ClippingEnabled = value })),
                // R10: the lower object on the map is drawn over the ones above it, for the whole layer.
                AppearanceToggle("ObjectLayerDepthSortToggle", "Ближние поверх дальних",
                    () => Effects()?.DepthSort,
                    value => ChangeEffects(current => current with { DepthSort = value })),
                AppearanceColorBox("ObjectLayerShadowColor", "Цвет тени",
                    () => Effects()?.ShadowColor,
                    value => ChangeEffects(current => current with { ShadowColor = value })),
                AppearanceSlider("ObjectLayerOuterShadow", "Внешняя тень", 0, 100, "%",
                    () => Effects()?.OuterShadowOpacity * 100,
                    (value, commit) => { if (commit) ChangeEffects(current => current with { OuterShadowOpacity = value / 100 }); }, 5),
                AppearanceSlider("ObjectLayerInnerShadow", "Внутренняя тень", 0, 100, "%",
                    () => Effects()?.InnerShadowOpacity * 100,
                    (value, commit) => { if (commit) ChangeEffects(current => current with { InnerShadowOpacity = value / 100 }); }, 5),
                AppearanceNumberBox("ObjectLayerShadowOffsetX", "Тень X", "px",
                    () => Effects()?.ShadowOffsetX,
                    value => ChangeEffects(current => current with { ShadowOffsetX = value })),
                AppearanceNumberBox("ObjectLayerShadowOffsetY", "Тень Y", "px",
                    () => Effects()?.ShadowOffsetY,
                    value => ChangeEffects(current => current with { ShadowOffsetY = value })),
                AppearanceSlider("ObjectLayerShadowBlur", "Размытие тени", 0, 256, "px",
                    () => Effects()?.ShadowBlur,
                    (value, commit) => { if (commit) ChangeEffects(current => current with { ShadowBlur = value }); }),
                AppearanceSlider("ObjectLayerSublayerBand", "Текущий подслой", -10, 10, "",
                    () => selectedSublayer,
                    (value, commit) =>
                    {
                        if (!commit) return;
                        selectedSublayer = (int)Math.Round(value);
                        SyncAppearanceSections();
                    }),
                AppearanceToggle("ObjectSublayerVisibleToggle", "Подслой видим",
                    () => _editor.ActiveObjectSublayerState(selectedSublayer)?.Visible,
                    value => _editor.SetActiveObjectSublayerState(selectedSublayer, visible: value)),
                AppearanceToggle("ObjectSublayerLockedToggle", "Подслой заблокирован",
                    () => _editor.ActiveObjectSublayerState(selectedSublayer)?.Locked,
                    value => _editor.SetActiveObjectSublayerState(selectedSublayer, locked: value)),
            },
        }, false);
        _appearanceSyncs.Add(() =>
        {
            var effects = Effects();
            var parts = new List<string>();
            if (effects is { OuterShadowOpacity: > 0 } || effects is { InnerShadowOpacity: > 0 }) parts.Add("тень");
            if (effects is { ClippingEnabled: true }) parts.Add("обрезка");
            if (effects is { DepthSort: true }) parts.Add("ближние поверх");
            objectEffects.SetSummary(parts.Count == 0 ? "без тени" : string.Join(", ", parts));
        });
        _appearanceSyncs.Add(() => objectEffects.IsVisible = _editor.ActiveLayer is ObjectLayer);

        var orderRow = new DockPanel
        {
            Margin = new Thickness(0, 2),
            Children =
            {
                Ui.ParameterLabel("Порядок", 92).Docked(Dock.Left),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        Ui.IconButton("LayerUpButton", "Поднять слой выше", MapIcons.ArrowUp,
                            () => _editor.ReorderActiveLayer(true)),
                        Ui.IconButton("LayerDownButton", "Опустить слой ниже", MapIcons.ArrowDown,
                            () => _editor.ReorderActiveLayer(false)),
                        Ui.IconButton("DuplicateLayerButton", "Дублировать слой", MapIcons.Duplicate,
                            _editor.DuplicateActiveLayer),
                    },
                },
            },
        };

        var delete = Ui.CommandButton("DeleteLayerButton", "Удалить слой", MapIcons.Trash,
            _editor.DeleteActiveLayer, "danger-text");
        delete.Margin = new Thickness(0, 6, 0, 0);
        DynamicTip(delete, () =>
            $"Удалить слой вместе с содержимым. Действие можно отменить ({_editor.Shortcuts.Get(EditorShortcutSettings.Commands.Undo)}).");

        // Visibility and lock of a layer are the eye and the lock of its row; the two value rows
        // stay built for their syncs but are not shown twice.
        visibilityRow.IsVisible = false;
        lockRow.IsVisible = false;
        var maskSection = new Ui.SectionPanel("LayerMaskSection", "Маска слоя",
            new StackPanel { Spacing = 8, Children = { maskRow, maskCommands } }, false);
        _appearanceSyncs.Add(() => maskSection.SetSummary(_editor.ActiveLayer is { } layer ? EditorLabels.MaskState(layer).ToLowerInvariant() : ""));
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                nameRow, orderRow, visibilityRow, lockRow, exactOpacity, exactBlend,
                maskSection, objectEffects, delete,
            },
        };
    }

    private async System.Threading.Tasks.Task RemoveActiveMaskAsync()
    {
        if (_editor.ActiveLayer?.Mask is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (await ConfirmationWindow.ShowAsync(
                owner, "Удалить маску слоя?",
                "Маска будет удалена одной отменяемой командой. Содержимое слоя не изменится.",
                "Удалить маску", "RemoveLayerMaskConfirmButton", destructive: true))
        {
            _editor.RemoveActiveMask();
            SyncSidePanels();
        }
    }

    private async System.Threading.Tasks.Task FlattenSelectedCompositionAsync()
    {
        if (_editor.Session.SelectedObject?.Composition is null
            || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (await ConfirmationWindow.ShowAsync(
                owner, "Растеризовать композицию?",
                "Композиция станет одним растровым ассетом. Исходная векторная композиция останется редактируемой до подтверждения; после операции её можно вернуть отменой (Ctrl+Z).",
                "Растеризовать", "FlattenCompositionConfirmButton", destructive: true))
        {
            _editor.FlattenSelectedComposition();
            SyncSidePanels();
        }
    }

    private Control BuildLandLayerBlock()
    {
        LandEdgeStyle? Style() => (_editor.ActiveLayer as LandLayer)?.EdgeStyle;
        var (row, button) = PropertyValueRow(
            "LandStyleButton", "Берег", "Обрыв",
            [("Обрыв", () => { if (Style()?.RippleCount > 0) _editor.CycleActiveLandStyle(); }),
             ("Берег с прибоем", () => { if (Style()?.RippleCount == 0) _editor.CycleActiveLandStyle(); })]);
        _landEdgeValue = button;

        // The same order the river panel uses inside its own Оформление: the surface first, then
        // its edge, then everything rarely touched. The category that holds this is built by the
        // land tool panel, so land and water are dressed in the same place and the same way.
        var defaults = LandEdgeStyle.SoftWatercolor();
        void Set(Func<LandEdgeStyle, LandEdgeStyle> change) => _editor.SetActiveLandStyle(change);
        Control Colour(string name, string label, string description, Func<string?> read, Func<LandEdgeStyle, string, LandEdgeStyle> change)
        {
            var field = AppearanceColorBox(name, label, read, value =>
            {
                if (AppearanceColor.Normalize(value) is { } color) Set(style => change(style, color));
            });
            ToolTip.SetTip(field, description);
            return field;
        }

        // The look of the land in the order it is read on the map, in two parts (R12, D8): the
        // «Заливка» - the ground itself - and the «Берег» - the coast line, the surf beyond it,
        // the shadows that give the shore its depth and the cliffs. Every effect is a switch with
        // its own settings; every number is a slider with a box, a unit, a plain explanation and a
        // way back to the default.
        var fill = Ui.Category("LandFillCategory", "Заливка", "accent-look", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        Colour("LandFillColorBox", "Цвет суши", "Основной цвет земли; материал ложится поверх него.",
                            () => Style()?.FillColor, (style, color) => style with { FillColor = color }),
                        BuildFillTexturePicker("LandTexturePicker", MapFillTextureKind.Ground,
                            () => Style()?.FillTextureId,
                            id => Set(style => style with { FillTextureId = id }),
                            // The shared material is mixed into the land's own colour at the chosen strength.
                            () => MaterialPreviewContext.Over(Style()?.FillColor ?? new LandEdgeStyle().FillColor,
                                Style()?.FillTextureOpacity ?? new LandEdgeStyle().FillTextureOpacity, Style()?.FillTextureScale)),
                        AppearanceSlider("LandTextureOpacitySlider", "Заметность материала", 0, 100, "%",
                            () => Style()?.FillTextureOpacity * 100,
                            (v, _) => Set(style => style with { FillTextureOpacity = v / 100 }), 5,
                            defaultValue: defaults.FillTextureOpacity * 100,
                            description: "Насколько фактура материала проступает поверх цвета суши. 0 — только цвет."),
                        AppearanceSlider("LandTextureScaleSlider", "Крупность материала", 16, 1024, "ед.",
                            () => Style()?.FillTextureScale,
                            (v, _) => Set(style => style with { FillTextureScale = v }), 8,
                            defaultValue: defaults.FillTextureScale,
                            description: "Размер одной плитки фактуры в единицах карты. Меньше — мельче зерно, больше — крупнее пятна."),
                    },
                },
                // R22: how the heights painted with «Суша → Рельеф» are drawn.
                Ui.Subgroup("Рельеф", new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        AppearanceSlider("LandReliefShadingSlider", "Отмывка", 0, 100, "%",
                            () => Style()?.ReliefShading * 100, (v, _) => Set(style => style with { ReliefShading = v / 100 }), 5,
                            defaultValue: defaults.ReliefShading * 100,
                            description: "Тень на склонах, отвёрнутых от света, и немного света на обращённых к нему. Свет — тот же, что у обрывов («Свет с»)."),
                        AppearanceSlider("LandReliefContoursSlider", "Изолинии", 0, 100, "%",
                            () => Style()?.ReliefContours * 100, (v, _) => Set(style => style with { ReliefContours = v / 100 }), 5,
                            defaultValue: defaults.ReliefContours * 100,
                            description: "Линии равной высоты тушью; каждая пятая жирнее. 0 — без линий."),
                        AppearanceSlider("LandReliefContourStepSlider", "Шаг изолиний", 4, 64, "",
                            () => Style()?.ReliefContourStep, (v, _) => Set(style => style with { ReliefContourStep = v }), 1,
                            defaultValue: defaults.ReliefContourStep,
                            description: "Через сколько ступеней высоты идёт следующая линия. Меньше — линии гуще."),
                        AppearanceSlider("LandReliefSteepSlider", "Штрихи на крутизне", 0, 100, "%",
                            () => Style()?.ReliefSteepInk * 100, (v, _) => Set(style => style with { ReliefSteepInk = v / 100 }), 5,
                            defaultValue: 0,
                            description: "Короткие штрихи по линии стока там, где склон крутой, — как обрыв по крутизне."),
                    },
                }),
            },
        });
        var coast = Ui.Category("LandCoastCategory", "Берег", "accent-look", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                row,
                EffectSection("LandOutline", "Контур берега",
                    "Линия по кромке суши со стороны воды — как обводка пером.",
                    () => Style()?.OutlineWidth, defaults.OutlineWidth, v => Set(style => style with { OutlineWidth = v }),
                    Colour("LandOutlineColorBox", "Цвет контура", "Цвет линии по кромке суши.",
                        () => Style()?.OutlineColor, (style, color) => style with { OutlineColor = color }),
                    AppearanceSlider("LandOutlineWidthSlider", "Толщина контура", 0, 64, "px",
                        () => Style()?.OutlineWidth, (v, _) => Set(style => style with { OutlineWidth = v }), 1,
                        defaultValue: defaults.OutlineWidth,
                        description: "Ширина линии в пикселях карты. 0 выключает контур.")),
                EffectSection("LandRipples", "Прибой",
                    "Линии воды, повторяющие берег: от одной тонкой до нескольких, бледнеющих вдаль.",
                    () => Style()?.RippleCount, defaults.RippleCount, v => Set(style => style with { RippleCount = (int)Math.Round(v) }),
                    Colour("LandRippleColorBox", "Цвет прибоя", "Цвет линий прибоя; полупрозрачный выглядит естественнее.",
                        () => Style()?.RippleColor, (style, color) => style with { RippleColor = color }),
                    AppearanceSlider("LandRippleCountSlider", "Число линий", 0, 12, "",
                        () => Style()?.RippleCount, (v, _) => Set(style => style with { RippleCount = (int)Math.Round(v) }), 1,
                        defaultValue: defaults.RippleCount,
                        description: "Сколько линий идёт от берега в воду. Каждая следующая дальше и бледнее предыдущей."),
                    AppearanceSlider("LandRippleSpacingSlider", "Расстояние от берега", 1, 128, "px",
                        () => Style()?.RippleSpacing, (v, _) => Set(style => style with { RippleSpacing = v }), 1,
                        defaultValue: defaults.RippleSpacing,
                        description: "Отступ первой линии от кромки; следующие линии отступают всё больше."),
                    AppearanceSlider("LandRippleWidthSlider", "Толщина линии", 0, 32, "px",
                        () => Style()?.RippleWidth, (v, _) => Set(style => style with { RippleWidth = v }), 1,
                        defaultValue: Math.Max(1, Math.Round(defaults.RippleWidth)),
                        description: "Толщина каждой линии прибоя.")),
                EffectSection("LandOuterShadow", "Тень на воде",
                    "Затемнение воды вдоль берега: суша будто приподнята над морем.",
                    () => Style()?.OuterShadowOpacity * 100, defaults.OuterShadowOpacity * 100, v => Set(style => style with { OuterShadowOpacity = v / 100 }),
                    AppearanceSlider("LandOuterShadowOpacitySlider", "Сила тени на воде", 0, 100, "%",
                        () => Style()?.OuterShadowOpacity * 100,
                        (v, _) => Set(style => style with { OuterShadowOpacity = v / 100 }), 5,
                        defaultValue: defaults.OuterShadowOpacity * 100,
                        description: "Насколько темнее вода у самого берега.")),
                EffectSection("LandInnerShadow", "Тень на суше",
                    "Затемнение суши у кромки: берег получает объём.",
                    () => Style()?.InnerShadowOpacity * 100, defaults.InnerShadowOpacity * 100, v => Set(style => style with { InnerShadowOpacity = v / 100 }),
                    AppearanceSlider("LandInnerShadowOpacitySlider", "Сила тени на суше", 0, 100, "%",
                        () => Style()?.InnerShadowOpacity * 100,
                        (v, _) => Set(style => style with { InnerShadowOpacity = v / 100 }), 5,
                        defaultValue: defaults.InnerShadowOpacity * 100,
                        description: "Насколько темнее суша у самой кромки.")),
                BuildLandCliffSection(Style, Set),
                Ui.Subgroup("Мягкость и направление теней", new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        AppearanceSlider("LandShadowBlurSlider", "Мягкость теней", 0, 64, "px",
                            () => Style()?.ShadowBlur,
                            (v, _) => Set(style => style with { ShadowBlur = v }), 1,
                            defaultValue: defaults.ShadowBlur,
                            description: "Ширина перехода обеих теней. 0 — резкая граница, больше — плавное затухание."),
                        AppearanceSlider("LandShadowOffsetXSlider", "Сдвиг теней по X", -64, 64, "px",
                            () => Style()?.ShadowOffsetX,
                            (v, _) => Set(style => style with { ShadowOffsetX = v }), 1,
                            defaultValue: 0,
                            description: "Смещение теней вправо (+) или влево (−), как от бокового света."),
                        AppearanceSlider("LandShadowOffsetYSlider", "Сдвиг теней по Y", -64, 64, "px",
                            () => Style()?.ShadowOffsetY,
                            (v, _) => Set(style => style with { ShadowOffsetY = v }), 1,
                            defaultValue: 0,
                            description: "Смещение теней вниз (+) или вверх (−)."),
                    },
                }),
            },
        });
        return new StackPanel { Spacing = 6, Children = { fill, coast } }.Named("LandLayerBlock");
    }

    /// <summary>
    /// One row of material swatches plus "без материала". The swatch is the tile itself, so what
    /// the user picks is what the map gets, and only materials of the right kind are offered.
    /// </summary>
    private Control BuildFillTexturePicker(
        string name, MapFillTextureKind kind, Func<string?> current, Action<string?> choose,
        Func<MaterialPreviewContext> context, string? fixedCaption = null)
    {
        var offered = MapFillTextures.Of(kind).Count(texture => Map.Rendering.MapTextureLibrary.Image(texture.Id) is not null);
        // «нет» and every material in one row while they fit: the cells narrow a little instead of
        // leaving one swatch alone, stretched across a second row.
        var grid = new Ui.ChoiceGrid { MaxColumns = Math.Max(4, offered + 1), MinimumItemWidth = offered + 1 > 4 ? 40 : 52, Gap = 6 }.Named(name);
        var buttons = new List<(Button Button, string? Id)>();
        var preview = context();
        void Add(string? id, string title)
        {
            var swatch = TextureSwatch(id, title, preview);
            if (offered + 1 > 4) swatch.Width = 36;
            var button = new Button
            {
                Name = name + "_" + (id ?? "none"),
                Content = swatch,
                Padding = new Thickness(2),
            }.Classed("swatch");
            ToolTip.SetTip(button, title);
            AutomationProperties.SetName(button, title);
            button.Click += (_, _) => { choose(id); SyncAppearanceSections(); Canvas.InvalidateVisual(); };
            buttons.Add((button, id));
            _textureSwatchBorders[button] = swatch;
            grid.Children.Add(button);
        }

        Add(null, "Без материала");
        foreach (var texture in MapFillTextures.Of(kind))
            if (Map.Rendering.MapTextureLibrary.Image(texture.Id) is not null) Add(texture.Id, texture.Title);
        _textureSwatchGroups.Add(new TextureSwatchGroup(grid, buttons, current, context) { LastSignature = preview.Signature });
        var caption = new TextBlock { Text = "Материал" }.Classed("faint", "role-caption");
        // The caption says what the swatches are mixed with, so a grey stone that shows green
        // is understood as green land with stone grain, not as a wrong picture.
        _appearanceSyncs.Add(() =>
        {
            var now = context();
            caption.Text = fixedCaption ?? (now.Raw ? "Материал · как кладёт кисть"
                : kind == MapFillTextureKind.Water ? "Материал · с цветом воды" : "Материал · с цветом и силой");
        });
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                caption,
                grid,
                Ui.AsyncButton(name + "CatalogButton", "Каталог материалов…",
                    () => OpenMaterialCatalog(kind, current(), choose, context), "compact"),
            },
        };
    }

    /// <summary>A small preview of one material in its context, drawn by the renderer's own fill call.</summary>
    private Border TextureSwatch(string? id, string title, MaterialPreviewContext context)
    {
        var border = new Border
        {
            Width = 46,
            Height = 34,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            ClipToBounds = true,
        };
        if (MaterialPreviewBrush(id, context) is { } previewBrush)
        {
            border.Background = previewBrush;
        }
        else
        {
            border.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            border.Child = new TextBlock
            {
                Text = "нет",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }.Classed("faint");
        }
        _ = title;
        return border;
    }

    private sealed record TextureSwatchGroup(
        Ui.ChoiceGrid Grid, List<(Button Button, string? Id)> Buttons, Func<string?> Current,
        Func<MaterialPreviewContext> Context)
    {
        public string LastSignature { get; set; } = "";
    }

    private readonly List<TextureSwatchGroup> _textureSwatchGroups = [];
    private readonly Dictionary<Button, Border> _textureSwatchBorders = [];

    /// <summary>
    /// Marks the swatch the current style actually uses, and repaints every swatch of a picker
    /// whose context changed - the land colour, the material strength, another layer or river,
    /// an undo - so the pictures always predict the map they are about to be applied to.
    /// </summary>
    private void SyncTextureSwatches()
    {
        foreach (var group in _textureSwatchGroups)
        {
            var selected = group.Current();
            var context = group.Context();
            var repaint = context.Signature != group.LastSignature;
            group.LastSignature = context.Signature;
            foreach (var (button, id) in group.Buttons)
            {
                var active = string.Equals(id, selected, StringComparison.Ordinal);
                Ui.SetClass(button, "active", active);
                if (!_textureSwatchBorders.TryGetValue(button, out var swatch)) continue;
                if (repaint && id is not null && MaterialPreviewBrush(id, context) is { } brush) swatch.Background = brush;
                swatch.BorderThickness = new Thickness(active ? 2.5 : 1);
                swatch.BorderBrush = new SolidColorBrush(active
                    ? Color.FromArgb(255, 0xE7, 0xC9, 0x82)
                    : Color.FromArgb(90, 255, 255, 255));
            }
        }
    }

    private Control BuildGridLayerBlock()
    {
        GridDefinition? Grid() => _editor.ActiveLayer is GridLayer { Grids.Count: > 0 } layer
            ? layer.Grids[0]
            : null;

        var geometryButtons = new Dictionary<GridGeometry, RadioButton>();
        var geometryChoices = new StackPanel { Spacing = 8 };
        foreach (var geometry in Enum.GetValues<GridGeometry>())
        {
            var captured = geometry;
            var label = EditorLabels.GridGeometry(geometry);
            var button = new RadioButton
            {
                GroupName = "GridGeometry",
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                HorizontalContentAlignment = HorizontalAlignment.Left,
            }.Named("GridGeometry" + geometry + "Radio").Classed("segment");
            AutomationProperties.SetName(button, label);
            ToolTip.SetTip(button, label);
            button.IsCheckedChanged += (_, _) =>
            {
                if (!_syncingAppearance && button.IsChecked == true)
                    _editor.SetActiveGridGeometry(captured);
            };
            geometryButtons.Add(geometry, button);
            geometryChoices.Children.Add(button);
        }
        var geometryHint = new TextBlock { TextWrapping = TextWrapping.Wrap }.Named("GridGeometryHint").Classed("faint", "role-caption");
        geometryChoices.Children.Add(geometryHint);
        _appearanceSyncs.Add(() =>
        {
            var selected = Grid()?.Geometry;
            foreach (var pair in geometryButtons)
                pair.Value.IsChecked = pair.Key == selected;
            geometryHint.Text = selected is { } geometry ? EditorLabels.GridGeometryHint(geometry) : "";
        });

        var exactProperties = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                AppearanceNumberBox("GridCellSizeBox", "Размер ячейки", "px",
                    () => Grid()?.CellSize, value => _editor.SetActiveGridCellSize(value), "0", 116),
                AppearanceNumberBox("GridSubdivisionsBox", "Деления", null,
                    () => Grid()?.Subdivisions, value => _editor.SetActiveGridSubdivisions(value), "0", 116),
                AppearanceNumberBox("GridLineWidthBox", "Толщина линии", "px",
                    () => Grid()?.LineWidth, value => _editor.SetActiveGridLineWidth(value), "0", 116),
                AppearanceColorBox("GridColorBox", "Цвет линии",
                    () => Grid()?.Color, value => _editor.SetActiveGridColor(value), 116),
                AppearanceNumberBox("GridOpacityBox", "Непрозрачность", "%",
                    () => Grid() is { } grid ? grid.Opacity * 100 : null,
                    value => _editor.SetActiveGridOpacityPercent(value), "0", 116),
                AppearanceNumberBox("GridOffsetXBox", "Смещение X", "px",
                    () => Grid()?.OffsetX, value => _editor.SetActiveGridOffsetX(value), "0", 116),
                AppearanceNumberBox("GridOffsetYBox", "Смещение Y", "px",
                    () => Grid()?.OffsetY, value => _editor.SetActiveGridOffsetY(value), "0", 116),
                AppearanceNumberBox("GridRotationBox", "Поворот", "°",
                    () => Grid()?.RotationDegrees, value => _editor.SetActiveGridRotation(value), "0", 116),
            },
        };

        var snap = new CheckBox
        {
            Content = new TextBlock { Text = "Привязка к сетке", TextWrapping = TextWrapping.Wrap },
        }.Named("GridSnapToggle");
        AutomationProperties.SetName(snap, "Привязка к сетке");
        snap.IsCheckedChanged += (_, _) =>
        {
            if (!_syncingAppearance) _editor.SetActiveGridSnapEnabled(snap.IsChecked == true);
        };
        var export = new CheckBox
        {
            Content = new TextBlock { Text = "Добавлять сетку в экспорт", TextWrapping = TextWrapping.Wrap },
        }.Named("GridIncludeInExportToggle");
        AutomationProperties.SetName(export, "Добавлять сетку в экспорт");
        export.IsCheckedChanged += (_, _) =>
        {
            if (!_syncingAppearance) _editor.SetActiveGridIncludeInExport(export.IsChecked == true);
        };
        _appearanceSyncs.Add(() =>
        {
            snap.IsChecked = Grid()?.SnapEnabled == true;
            export.IsChecked = Grid()?.IncludeInExport == true;
        });

        return Ui.Section("Сетка", new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Section("Геометрия", geometryChoices),
                Ui.Section("Точные параметры", exactProperties),
                new StackPanel { Spacing = 4, Children = { snap, export } },
            },
        }).Named("GridLayerBlock");
    }

    private Control BuildFilterLayerBlock()
    {
        FilterSettings? Filter() => (_editor.ActiveLayer as FilterLayer)?.Settings;
        var (row, button) = PropertyValueRow(
            "FilterStyleButton", "Тон", "Тёплый",
            [("Тёплый", () => { if (Filter()?.Temperature <= 0) _editor.CycleActiveFilter(); }),
             ("Холодный", () => { if (Filter()?.Temperature > 0) _editor.CycleActiveFilter(); })]);
        _filterPresetValue = button;
        return Ui.Section("Фильтр", new StackPanel
        {
            Spacing = 4,
            Children =
            {
                BuildFilterColorBlock(Filter),
                row,
                AppearanceSlider("FilterBrightnessSlider", "Яркость", -100, 100, "%",
                    () => Filter()?.Brightness * 100,
                    (v, _) => _editor.SetActiveFilter(brightness: v / 100), 5),
                AppearanceSlider("FilterContrastSlider", "Контраст", -100, 100, "%",
                    () => Filter()?.Contrast * 100,
                    (v, _) => _editor.SetActiveFilter(contrast: v / 100), 5),
                AppearanceSlider("FilterSaturationSlider", "Насыщенность", -100, 100, "%",
                    () => Filter()?.Saturation * 100,
                    (v, _) => _editor.SetActiveFilter(saturation: v / 100), 5),
                AppearanceSlider("FilterHueSlider", "Оттенок", -180, 180, "°",
                    () => Filter()?.HueDegrees,
                    (v, _) => _editor.SetActiveFilter(hueDegrees: v), 5),
                AppearanceSlider("FilterTemperatureSlider", "Температура", -100, 100, "%",
                    () => Filter()?.Temperature * 100,
                    (v, _) => _editor.SetActiveFilter(temperature: v / 100), 5),
                AppearanceSlider("FilterTextureOpacitySlider", "Текстура · непрозрачность", 0, 100, "%",
                    () => Filter()?.TextureOpacity * 100,
                    (v, _) => _editor.SetActiveFilter(textureOpacity: v / 100), 5),
                AppearanceSlider("FilterTextureScaleSlider", "Текстура · масштаб", 5, 6400, "%",
                    () => Filter()?.TextureScale * 100,
                    (v, _) => _editor.SetActiveFilter(textureScale: v / 100), 5),
                Ui.TextButton("ResetFilterTextureButton", "Сбросить текстуру",
                    () => _editor.SetActiveFilter(resetTexture: true), "compact"),
            },
        }).Named("FilterLayerBlock");
    }


    /// <summary>
    /// Colour looks of a filter layer: twelve one-click presets, the strength of the whole look,
    /// a three-colour gradient map and true post-processing. Every number is a <see cref="ValueRow"/>.
    /// </summary>
    private Control BuildFilterColorBlock(Func<FilterSettings?> filter)
    {
        var presets = new Ui.ChoiceGrid { MaxColumns = 3, MinimumItemWidth = 84, Gap = 6 }.Named("FilterColorPresets");
        foreach (var preset in FilterColorEffects.Presets)
        {
            var captured = preset;
            var toggle = new ToggleButton
            {
                Content = new TextBlock { Text = preset.Title, TextWrapping = TextWrapping.Wrap },
            }.Named("FilterColorPreset_" + preset.Id).Classed("segment", "filter-chip");
            AutomationProperties.SetName(toggle, "Цветовой пресет: " + preset.Title);
            ToolTip.SetTip(toggle, preset.Description + " Один щелчок — применить; сила меняется ползунком «Сила эффекта».");
            toggle.Click += (_, _) =>
            {
                if (!_syncingAppearance) _editor.ApplyActiveFilterPreset(captured.Id);
                SyncAppearanceSections();
            };
            _appearanceSyncs.Add(() =>
            {
                var active = filter()?.ColorPresetId == captured.Id;
                toggle.IsChecked = active;
                Ui.SetClass(toggle, "active", active);
            });
            presets.Children.Add(toggle);
        }
        var clear = Ui.TextButton("FilterColorClearButton", "Без цветового эффекта",
            () => { _editor.ClearActiveFilterColor(); SyncAppearanceSections(); }, "compact");
        ToolTip.SetTip(clear, "Убрать градиентную карту и пост-обработку этого слоя. Тон и текстура ниже останутся.");
        var look = new TextBlock { TextWrapping = TextWrapping.Wrap }.Named("FilterColorLookText").Classed("faint");
        _appearanceSyncs.Add(() => look.Text = _editor.ActiveLayer is FilterLayer layer
            ? "Сейчас: " + EditorLabels.FilterColorLook(layer)
            : "");

        var strength = AppearanceSlider("FilterColorStrengthSlider", "Сила эффекта", 0, 100, "%",
            () => filter()?.ColorStrength * 100,
            (v, _) => _editor.SetActiveFilterColor(colorStrength: v / 100), 5, defaultValue: 100,
            description: "Насколько сильно выбранный пресет или свои настройки меняют карту. 0 — карта как без фильтра.");

        var gradient = Ui.Section("Градиентная карта", new StackPanel
        {
            Spacing = 4,
            Children =
            {
                AppearanceColorBox("FilterGradientShadowColor", "Тени", () => filter()?.GradientMap.ShadowColor,
                    value => _editor.SetActiveFilterColor(shadowColor: value)),
                AppearanceColorBox("FilterGradientMidtoneColor", "Полутона", () => filter()?.GradientMap.MidtoneColor,
                    value => _editor.SetActiveFilterColor(midtoneColor: value)),
                AppearanceColorBox("FilterGradientHighlightColor", "Света", () => filter()?.GradientMap.HighlightColor,
                    value => _editor.SetActiveFilterColor(highlightColor: value)),
                AppearanceSlider("FilterGradientStrengthSlider", "Сила градиента", 0, 100, "%",
                    () => filter()?.GradientMap.Strength * 100,
                    (v, _) => _editor.SetActiveFilterColor(gradientStrength: v / 100), 5, defaultValue: 0,
                    description: "Перекрашивает карту по яркости: тёмные места — в цвет теней, средние — в цвет полутонов, светлые — в цвет светов. 0 — выключено."),
            },
        });
        var post = Ui.Section("Пост-обработка", new StackPanel
        {
            Spacing = 4,
            Children =
            {
                AppearanceSlider("FilterPostSaturationSlider", "Насыщенность", -100, 100, "%",
                    () => filter()?.PostProcess.Saturation * 100,
                    (v, _) => _editor.SetActiveFilterColor(saturation: v / 100), 5, defaultValue: 0,
                    description: "Плюс — краски сочнее, минус — бледнее; −100 — без цвета."),
                AppearanceSlider("FilterPostBrightnessSlider", "Яркость", -100, 100, "%",
                    () => filter()?.PostProcess.Brightness * 100,
                    (v, _) => _editor.SetActiveFilterColor(brightness: v / 100), 5, defaultValue: 0,
                    description: "Светлее или темнее вся карта под фильтром."),
                AppearanceSlider("FilterPostContrastSlider", "Контраст", -100, 100, "%",
                    () => filter()?.PostProcess.Contrast * 100,
                    (v, _) => _editor.SetActiveFilterColor(contrast: v / 100), 5, defaultValue: 0,
                    description: "Плюс — тени темнее и света светлее, минус — всё ближе к серому."),
                AppearanceSlider("FilterPostSharpnessSlider", "Резкость", 0, 100, "%",
                    () => filter()?.PostProcess.Sharpness * 100,
                    (v, _) => _editor.SetActiveFilterColor(sharpness: v / 100), 5, defaultValue: 0,
                    description: "Подчёркивает мелкие детали и края. Во время движения карты не показывается, в покое и в экспорте — всегда."),
            },
        });
        return new StackPanel
        {
            Spacing = 6,
            Children = { Ui.Section("Цветовые пресеты", new StackPanel { Spacing = 6, Children = { presets, look, clear } }), strength, gradient, post },
        }.Named("FilterColorBlock");
    }

    private Control BuildObjectsTab()
    {
        _selectionCountText.Classes.Add("faint");
        _selectionCountText.Classes.Add("role-caption");
        _selectionCountText.TextWrapping = TextWrapping.Wrap;

        AutomationProperties.SetName(_objectList, "Список объектов");
        _objectList.ItemTemplate = new FuncDataTemplate<ObjectTreeRow>(
            row => row is not null,
            (_, _) => new RecyclingObjectTreeRow(this),
            supportsRecycling: true);
        _objectList.ContainerPrepared += (_, e) => NameObjectContainer(e.Container, e.Index);
        _objectList.ContainerIndexChanged += (_, e) => NameObjectContainer(e.Container, e.NewIndex);

        // The compact type filter of the "Прочее" section, honoured by Ctrl+A together with the
        // search box above it. The other sections carry their own kind and need no strip.
        var filterStrip = new Ui.ChoiceGrid { MaxColumns = 3, MinimumItemWidth = 60, Gap = 6 }.Named("OtherTypeFilterStrip");
        foreach (var filter in new[]
                 {
                     ObjectTypeFilter.NotStamps, ObjectTypeFilter.Text,
                     ObjectTypeFilter.Lines, ObjectTypeFilter.Shapes, ObjectTypeFilter.Other,
                 })
        {
            var captured = filter;
            var title = ObjectTreeRows.FilterTitle(filter);
            var button = new ToggleButton { Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap } }
                .Named("ObjectTypeFilter" + filter)
                .Classed("segment", "filter-chip");
            AutomationProperties.SetName(button, "Фильтр типа: " + title);
            DynamicTip(button, () =>
                $"Показывать только: {title}. {_editor.Shortcuts.Get(EditorShortcutSettings.Commands.SelectAll)} выбирает по этому фильтру.");
            button.Click += (_, _) =>
            {
                _editor.ObjectTypeFilter = captured;
                SyncObjectList();
            };
            _objectFilterButtons[filter.ToString()] = button;
            filterStrip.Children.Add(button);
        }

        _otherFilterStrip = Disclosure("ObjectTypeFilters", "Тип объектов", filterStrip, false);
        var selectAll = ShortcutTextButton(
            "SelectAllButton", "Выбрать всё", EditorShortcutSettings.Commands.SelectAll, () =>
        {
            _editor.SelectAllByFilter();
            RefreshAfterSelectionCommand();
        }, "compact");
        DynamicTip(selectAll, () =>
            $"Выбрать всё видимое и незаблокированное в разделе «{ObjectTreeRows.SectionTitle(_editor.PanelSection)}» " +
            $"по текущему поиску ({_editor.Shortcuts.Get(EditorShortcutSettings.Commands.SelectAll)}). Другие разделы не затрагиваются.");

        var header = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                _objectSearch,
                _otherFilterStrip,
                selectAll,
                _selectionCountText,
            },
        };

        var compositionActions = new Border
        {
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Композиции" }.Classed("role-panel"),
                    Ui.TextButton("MakeCompositionButton", "Собрать из выделенного",
                        () => _editor.CreateEditableComposition(), "compact"),
                    Ui.TextButton("SaveSelectionAsAssetButton", "Сохранить выделенное как ассет",
                        () => _editor.SaveSelectionAsCatalogAsset(), "compact"),
                    Ui.TextButton("PasteCompositionButton", "Вставить композицию",
                        _editor.PasteComposition, "compact"),
                    Ui.AsyncButton("ImportCompositionPackageButton", "Вставить композицию из файла…",
                        ImportCompositionPackageAsync, "compact"),
                },
            },
        }.Classed("group");

        var listHost = new Grid
        {
            Children = { _objectList, _objectEmptyStateHost },
        };
        _compositionsBlock = Disclosure("ObjectCompositions", "Композиции", compositionActions, false);

        // The footer is the section's own property block (see EditorView.PanelSections); it
        // is bounded so a long list still keeps most of the panel.
        var footer = new ScrollViewer
        {
            MaxHeight = 250,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _sectionFooter,
        };
        var body = new Grid
        {
            Margin = new Thickness(12, 6, 12, 12),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 10,
            Children = { header, listHost, footer },
        };
        Grid.SetRow(listHost, 1);
        Grid.SetRow(footer, 2);
        return body;
    }


    private static Expander Disclosure(string name, string title, Control content, bool expanded)
    {
        var expander = new Expander
        {
            Header = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap }.Classed("role-panel"),
            Content = content,
            IsExpanded = expanded,
        }.Named(name);
        AutomationProperties.SetName(expander, title);
        return expander;
    }

    private void BuildLibraryDiagnosticBanner()
    {
        _libraryDiagnosticText.TextWrapping = TextWrapping.Wrap;
        _libraryDiagnosticText.Classes.Add("role-caption");
        _libraryDiagnostic.Classes.Add("group");
        _libraryDiagnostic.Padding = new Thickness(8, 6);
        // A horizontal stack gives its children infinite width, so the wrapped text ran straight
        // off the panel and the reason a catalog was refused could not be read to the end.
        _libraryDiagnostic.Child = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                new Border
                {
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = MapIcons.Stroked(MapIcons.Warning, 16),
                }.Docked(Dock.Left),
                _libraryDiagnosticText,
            },
        };
        AutomationProperties.SetName(_libraryDiagnostic, "Состояние библиотеки");
    }


    /// <summary>
    /// Moves the visual selection from a header onto the nearest card, continuing in the
    /// direction the user was travelling so a header never blocks keyboard navigation.
    /// </summary>
    private void MoveSelectionOffHeader(int headerIndex, int step)
    {
        if (headerIndex >= _editor.LibraryRows.Count || !_editor.LibraryRows[headerIndex].IsHeader)
        {
            return;
        }

        var target = NextCardRow(headerIndex, step);
        if (target < 0 || !_editor.SelectLibraryRow(target))
        {
            return;
        }

        _lastSelectedLibraryRow = target;
        SyncAssetList();
        SyncStatus();
    }

    /// <summary>The nearest row holding a card, searching in one direction and then the other.</summary>
    private int NextCardRow(int from, int step)
    {
        var rows = _editor.LibraryRows;
        for (var index = from + step; index >= 0 && index < rows.Count; index += step)
        {
            if (!rows[index].IsHeader)
            {
                return index;
            }
        }

        for (var index = from - step; index >= 0 && index < rows.Count; index -= step)
        {
            if (!rows[index].IsHeader)
            {
                return index;
            }
        }

        return -1;
    }

    private void NameLibraryContainer(Control? container, int index)
    {
        var rows = _editor.LibraryRows;
        if (container is not ListBoxItem item || index < 0 || index >= rows.Count)
        {
            return;
        }

        var row = rows[index];
        var name = row.IsHeader
            ? $"Группа {row.Title}, карточек: {row.Count}"
            : CardAccessibleName(row.Card!);
        AutomationProperties.SetName(item, name);
        ToolTip.SetTip(item, name);
    }

    private string CardAccessibleName(AssetSetSummary summary)
    {
        var name = summary.IsSet
            ? $"{summary.Name}, набор, {summary.VariantCount} вариантов"
            : $"{summary.Name}, один вариант";
        var availability = _editor.AvailabilityOf(summary.CoverSha256);
        return availability is AssetAvailability.Unknown or AssetAvailability.Available
            ? name
            : name + ", " + EditorViewModel.AvailabilityText(availability);
    }

    /// <summary>
    /// The caption of one group. It is a row of the same list, so it virtualizes with the cards,
    /// but it is never a selectable asset set.
    /// </summary>
    private static Border BuildLibraryGroupHeader(AssetLibraryRow row)
    {
        var title = new TextBlock
        {
            Text = row.Title,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }.Classed("role-panel");

        var count = new TextBlock
        {
            Text = row.Count.ToString(CultureInfo.InvariantCulture),
            VerticalAlignment = VerticalAlignment.Center,
        }.Classed("faint", "role-caption");

        var host = new Border
        {
            Padding = new Thickness(2, 8, 4, 2),
            Child = new DockPanel { Children = { count.Docked(Dock.Right), title } },
        }.Named("LibraryGroupHeader_" + Sanitize(row.Key));

        var name = $"Группа {row.Title}, карточек: {row.Count}";
        AutomationProperties.SetName(host, name);
        ToolTip.SetTip(host, name);
        return host;
    }

    /// <summary>Automation ids stay to letters, digits and underscores whatever the tag says.</summary>
    private static string Sanitize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }

    private Border BuildLibraryTile(AssetSetSummary? summary)
    {
        if (summary is null)
        {
            return new Border();
        }

        var thumbnail = new Border
        {
            Width = 44,
            Height = 44,
            Child = _thumbnails.Image(_editor.LibraryThumbnailPath(summary)) is { } bitmap
                ? new Image { Source = bitmap, Stretch = Stretch.Uniform }
                : MapIcons.Stroked(MapIcons.Missing, 20),
        }.Classed("group");

        var title = new TextBlock
        {
            Text = summary.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var caption = new TextBlock
        {
            Text = $"{summary.CoverWidth}×{summary.CoverHeight}",
            VerticalAlignment = VerticalAlignment.Center,
        }.Classed("faint", "role-caption");

        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (summary.IsSet)
        {
            // The count is never a mute digit: it carries its own accessible name.
            var badge = new Border
            {
                Padding = new Thickness(6, 2),
                Child = new TextBlock { Text = summary.VariantCount.ToString(CultureInfo.InvariantCulture), FontSize = 11 },
            }.Classed("badge").Named("SetBadge_" + summary.SetId);
            var badgeName = $"{summary.VariantCount} вариантов";
            AutomationProperties.SetName(badge, badgeName);
            ToolTip.SetTip(badge, badgeName);
            badges.Children.Add(badge);
        }

        if (summary.Favorite)
        {
            var star = new Border { Padding = new Thickness(3, 2), Child = MapIcons.Stroked(MapIcons.Check, 13) }
                .Classed("badge");
            AutomationProperties.SetName(star, "В избранном");
            ToolTip.SetTip(star, "В избранном");
            badges.Children.Add(star);
        }

        // A card whose file is gone or altered must not look like a healthy one. The badge is
        // built for every tile and shown only when there is really something to say.
        var stateLabel = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var stateBadge = new Border
        {
            Padding = new Thickness(5, 2),
            IsVisible = false,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { MapIcons.Stroked(MapIcons.Warning, 12), stateLabel },
            },
        }.Classed("badge").Named("TileState_" + summary.SetId);
        badges.Children.Add(stateBadge);

        var host = new Border
        {
            Padding = new Thickness(4),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    badges.Docked(Dock.Right),
                    thumbnail.Docked(Dock.Left),
                    new StackPanel
                    {
                        Margin = new Thickness(8, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { title, caption },
                    },
                },
            },
        }.Named("LibraryTile_" + summary.SetId);

        var tile = new TileState(stateBadge, stateLabel, host, summary);
        if (!_tileStates.TryGetValue(summary.CoverSha256, out var states))
        {
            states = [];
            _tileStates.Add(summary.CoverSha256, states);
        }

        states.Add(tile);
        host.DetachedFromVisualTree += (_, _) => RemoveTileState(summary.CoverSha256, tile);
        ApplyTileState(tile);
        return host;
    }

    private void RemoveTileState(string sha256, TileState tile)
    {
        if (!_tileStates.TryGetValue(sha256, out var states))
        {
            return;
        }

        states.Remove(tile);
        if (states.Count == 0)
        {
            _tileStates.Remove(sha256);
        }
    }

    /// <summary>Shows the current verdict on one realised tile, in colour, words and UIA name.</summary>
    private void ApplyTileState(TileState tile)
    {
        var availability = _editor.AvailabilityOf(tile.Summary.CoverSha256);
        var text = EditorViewModel.AvailabilityText(availability);
        tile.Badge.IsVisible = availability is not (AssetAvailability.Unknown or AssetAvailability.Available);
        tile.Label.Text = availability switch
        {
            AssetAvailability.Missing => "нет файла",
            AssetAvailability.HashMismatch => "изменён",
            AssetAvailability.Unsupported => "формат",
            AssetAvailability.Checking => "проверка",
            _ => "",
        };

        Ui.SetClass(tile.Badge, "danger", availability is AssetAvailability.Missing or AssetAvailability.HashMismatch);
        Ui.SetClass(tile.Badge, "warn", availability is AssetAvailability.Unsupported or AssetAvailability.Checking);
        AutomationProperties.SetName(tile.Badge, text);
        ToolTip.SetTip(tile.Badge, text);

        var name = CardAccessibleName(tile.Summary);
        AutomationProperties.SetName(tile.Host, name);
        ToolTip.SetTip(tile.Host, name);
    }

    /// <summary>One realised tile and the controls its availability verdict writes into.</summary>
    private sealed record TileState(Border Badge, TextBlock Label, Border Host, AssetSetSummary Summary);

    /// <summary>
    /// Reports the containers the grid really realised. Asking the list for its realised
    /// containers costs those containers; walking every index of the result would make each
    /// layout pass proportional to the whole library instead.
    /// </summary>
    private void ReportVisibleLibraryRange()
    {
        var realized = new List<int>();
        foreach (var container in _libraryGrid.GetRealizedContainers())
        {
            var index = _libraryGrid.IndexFromContainer(container);
            if (index >= 0)
            {
                realized.Add(index);
            }
        }

        _editor.ReportRealizedLibraryRows(realized);

        var window = _editor.VisibleLibraryTileWindow;
        var current = (window.FirstIndex, window.Count);
        if (current == _reportedLibraryRange)
        {
            return;
        }

        _reportedLibraryRange = current;
        UiTasks.Forget(this, CheckVisibleAvailabilityAsync);
    }

    /// <summary>
    /// Verifies the files behind the rows now on screen. The hashing runs off the UI thread and a
    /// verdict for a query the user has already left is dropped by the view model.
    /// </summary>
    private async System.Threading.Tasks.Task CheckVisibleAvailabilityAsync()
    {
        try
        {
            await _editor.RefreshVisibleAvailabilityAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var tile in _tileStates.Values.SelectMany(states => states).ToArray())
        {
            ApplyTileState(tile);
        }

        // The container is what a screen reader reads, and its name was composed before the file
        // was checked. Renaming the realised containers is what makes the verdict actually
        // reachable through UI Automation rather than only visible as a coloured badge.
        foreach (var container in _libraryGrid.GetRealizedContainers())
        {
            NameLibraryContainer(container, _libraryGrid.IndexFromContainer(container));
        }

        // The strip carries one mark per variant, so it has to be rebuilt when the verdicts land;
        // it is bounded by the variants of the one set the tool is holding.
        SyncVariantStrip();
        SyncSelectedSetAvailability();
    }
    /// <summary>
    /// The content of the "Фильтр" flyout. Every control says in words what it does, and the
    /// toolbar shows a chip for each filter that is on, so the user can always tell why the
    /// library looks empty.
    /// </summary>
    private StackPanel BuildLibraryFilterControls()
    {
        var quickFilters = new Ui.ChoiceGrid { MaxColumns = 2, MinimumItemWidth = 112 };
        foreach (var (key, title, icon) in new[]
        {
            ("Favorites", "Избранное", MapIcons.Check),
            ("Recent", "Недавние", MapIcons.Undo),
            ("Untagged", "Без тегов", MapIcons.Close),
            ("UsedInMap", "На карте", MapIcons.Objects),
        })
        {
            var button = LabelledToggle("LibraryFilter" + key, title, FilterHint(key), icon, () =>
            {
                switch (key)
                {
                    case "Favorites":
                        _editor.LibraryFavoritesOnly = !_editor.LibraryFavoritesOnly;
                        break;
                    case "Recent":
                        _editor.LibraryRecentOnly = !_editor.LibraryRecentOnly;
                        break;
                    case "Untagged":
                        _editor.LibraryUntaggedOnly = !_editor.LibraryUntaggedOnly;
                        break;
                    default:
                        _editor.LibraryUsedInMapOnly = !_editor.LibraryUsedInMapOnly;
                        break;
                }

                SyncAssetList();
            });
            _libraryFilterButtons[key] = button;
            button.Classes.Add("filter-chip");
            quickFilters.Children.Add(button);
        }

        _libraryTagTree = new StackPanel { Spacing = 3 }.Named("LibraryTagFilter");
        _libraryMediaFilter = ChoiceBox("LibraryMediaFilter", "Тип файла", "Любой тип", value =>
        {
            _editor.LibraryMediaType = value;
            SyncAssetList();
        });

        _libraryImportedFrom = DateBox("LibraryImportedFromBox", "Импортировано с", value =>
        {
            _editor.LibraryImportedFromText = value;
            SyncAssetList();
        });
        _libraryImportedTo = DateBox("LibraryImportedToBox", "Импортировано по", value =>
        {
            _editor.LibraryImportedToText = value;
            SyncAssetList();
        });

        _libraryDateHint.TextWrapping = TextWrapping.Wrap;
        _libraryDateHint.Text = "Дата в формате ГГГГ-ММ-ДД.";
        _libraryDateHint.Classes.Add("faint");
        _libraryDateHint.Classes.Add("role-caption");

        _libraryFilterSummary.TextWrapping = TextWrapping.Wrap;
        _libraryFilterSummary.Classes.Add("faint");
        _libraryFilterSummary.Classes.Add("role-caption");
        _libraryFilterSummary.IsVisible = false;

        _clearFiltersButton = Ui.TextButton("LibraryClearFiltersButton", "Сбросить фильтры", () =>
        {
            _editor.ClearLibraryFilters();
            _assetSearch.Text = "";
            _libraryImportedFrom.Text = "";
            _libraryImportedTo.Text = "";
            SyncAssetList();
            SyncStatus();
        }, "compact");
        AutomationProperties.SetName(_clearFiltersButton, "Сбросить все фильтры библиотеки");
        ToolTip.SetTip(_clearFiltersButton, "Снять поиск, коллекцию, теги, тип и даты");

        return new StackPanel
        {
            Spacing = 7,
            Children =
            {
                Ui.Section("Показывать только", quickFilters),
                Ui.Section("Теги", _libraryTagTree),
                Ui.PropertyRow("Формат", _libraryMediaFilter),
                Ui.PropertyRow("Добавлено с", _libraryImportedFrom),
                Ui.PropertyRow("Добавлено по", _libraryImportedTo),
                _libraryDateHint,
            },
        };

        static string FilterHint(string key) => key switch
        {
            "Favorites" => "Только избранное",
            "Recent" => "Только недавние",
            "Untagged" => "Только без тегов",
            _ => "Только используемое на карте",
        };
    }

    /// <summary>
    /// A filter toggle that says what it filters. The icon stays for recognition, but the label
    /// is what makes the bar readable, and the accessible name repeats the full sentence.
    /// </summary>
    private static ToggleButton LabelledToggle(
        string name, string title, string hint, string icon, Action action)
    {
        var button = new ToggleButton
        {
            Content = new DockPanel
            {
                Children =
                {
                    new Border { Child = MapIcons.Stroked(icon, 14), Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center }.Docked(Dock.Left),
                    new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        }.Named(name).Classed("segment");
        AutomationProperties.SetName(button, hint);
        ToolTip.SetTip(button, hint);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>A named drop-down whose first entry means "do not filter by this at all".</summary>
    private ComboBox ChoiceBox(string name, string label, string anyLabel, Action<string?> chosen)
    {
        var box = new ComboBox { MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Stretch }.Named(name);
        AutomationProperties.SetName(box, label);
        ToolTip.SetTip(box, label + ": " + anyLabel + " или одно значение из каталога");
        box.SelectionChanged += (_, _) =>
        {
            if (_syncingLibrary)
            {
                return;
            }

            var value = box.SelectedItem as string;
            chosen(string.Equals(value, anyLabel, StringComparison.Ordinal) ? null : value);
        };
        return box;
    }

    private TextBox DateBox(string name, string label, Action<string> changed)
    {
        var box = new TextBox { PlaceholderText = "ГГГГ-ММ-ДД", MinWidth = 120 }.Named(name);
        AutomationProperties.SetName(box, label);
        ToolTip.SetTip(box, label + " в формате ГГГГ-ММ-ДД, например 2026-08-26");
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !_syncingLibrary)
            {
                changed((box.Text ?? "").Trim());
            }
        };
        return box;
    }
    /// <summary>
    /// The selected card: what it is called, which variant is current, whether that variant is
    /// the cover, whether its file is really there, and the three things the user may change
    /// about the card itself - its name, the order of its variants and its cover.
    /// </summary>
    /// <summary>
    /// «Паспорт ассета»: how this card prefers to be placed. The tool takes these as starting
    /// values when the card is chosen; the panel «Как ставить» can still override them.
    /// </summary>
    private Control BuildPlacementPassportBlock()
    {
        // Functional R11 control using the existing ComboBox; visual refinement belongs to Claude.
        var roleNames = new[] { "Без роли", "Дерево", "Ель", "Куст", "Вершина", "Склон", "Дом", "Башня", "Стена", "Дверь", "Знак" };
        var role = new ComboBox { ItemsSource = roleNames, HorizontalAlignment = HorizontalAlignment.Stretch }
            .Named("PassportAssetRoleBox");
        role.SelectionChanged += (_, _) =>
        {
            if (_syncingAppearance || role.SelectedIndex < 0) return;
            _editor.SetSelectedSetRole((AssetRole)role.SelectedIndex);
            SyncAssetList(); SyncAppearanceSections(); SyncStatus();
        };
        _appearanceSyncs.Add(() =>
        {
            role.IsEnabled = _editor.SelectedSet is not null;
            role.SelectedIndex = (int)_editor.SelectedSetRole;
        });
        void Change(Func<AssetPlacementDefaults, AssetPlacementDefaults> change)
        {
            if (_syncingAppearance) return;
            _editor.SetSelectedSetPlacement(change);
            SyncAssetList();
            SyncAppearanceSections();
            SyncStatus();
        }
        var rotation = AppearanceToggle("PassportRandomRotationToggle", "Случайный поворот",
            () => _editor.SelectedSet is null ? null : _editor.SelectedSetPlacement.RandomRotation,
            on => Change(p => p with { RandomRotation = on }));
        ToolTip.SetTip(rotation, "При выборе ассета разброс поворота сразу ±180°: камни, кусты, кроны деревьев.");
        var flip = AppearanceToggle("PassportCanFlipToggle", "Можно отражать",
            () => _editor.SelectedSet is null ? null : _editor.SelectedSetPlacement.CanFlip,
            on => Change(p => p with { CanFlip = on }));
        ToolTip.SetTip(flip, "Выключите для надписей, компаса, гербов: случайное и ручное отражение при выборе снимается.");
        var shadow = AppearanceToggle("PassportNoShadowToggle", "Без тени",
            () => _editor.SelectedSet is null ? null : _editor.SelectedSetPlacement.NoShadow,
            on => Change(p => p with { NoShadow = on }));
        ToolTip.SetTip(shadow, "При выборе ассета тень выключается: плоские значки, дороги, пятна.");
        return Ui.Section("Паспорт ассета", new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Ui.Hint("Стартовые значения инструмента для этого набора. После выбора их можно менять в «Как ставить» и «Случайность»."),
                Ui.Hint("Роль набора для «Лес / Хребет / Поселение»"),
                role,
                rotation,
                AppearanceSlider("PassportRandomSizeSlider", "Случайный размер ±", 0, 100, "%",
                    () => _editor.SelectedSet is null ? null : _editor.SelectedSetPlacement.RandomSizePercent,
                    (v, _) => Change(p => p with { RandomSizePercent = v }), 5, defaultValue: 0,
                    description: "Каждый ассет серии или россыпи больше или меньше на случайную долю до этого значения."),
                flip,
                shadow,
                AppearanceSlider("PassportCollisionSlider", "Рамка «без наложений»", 5, 100, "%",
                    () => _editor.SelectedSet is null ? null : (_editor.SelectedSetPlacement.CollisionFraction ?? StampCollision.DefaultFraction) * 100,
                    (v, _) => Change(p => p with { CollisionFraction = Math.Abs(v / 100 - StampCollision.DefaultFraction) < 0.001 ? null : v / 100 }),
                    5, defaultValue: 30,
                    description: "Какую долю размера ассета россыпь «Без наложений» держит свободной. Меньше — гуще."),
            },
        }).Named("PlacementPassportBlock");
    }

    private Border BuildSelectedSetBlock()
    {
        _setBadgeText = new TextBlock { Text = "", FontSize = 11 }.Named("SelectedSetBadge");
        _setBadge = new Border
        {
            Padding = new Thickness(6, 2),
            Child = _setBadgeText,
            IsVisible = false,
        }.Classed("badge");

        _favoriteButton = FilterToggle(
            "SetFavoriteButton", "В избранное", MapIcons.Check, () =>
            {
                _editor.ToggleSelectedSetFavorite();
                SyncAssetList();
                SyncStatus();
            });

        _previousVariantButton = Ui.IconButton(
            "PreviousVariantButton", "Предыдущий вариант", MapIcons.Back, () =>
            {
                _editor.PreviousVariant();
                SyncAssetList();
                SyncStatus();
            });

        _nextVariantButton = Ui.IconButton(
            "NextVariantButton", "Следующий вариант", MapIcons.ChevronRight, () =>
            {
                _editor.NextVariant();
                SyncAssetList();
                SyncStatus();
            });

        _setRenameBox = new TextBox { PlaceholderText = "Имя набора", MinWidth = 120 }.Named("SetRenameBox");
        AutomationProperties.SetName(_setRenameBox, "Имя выбранного набора");
        ToolTip.SetTip(_setRenameBox, "Новое имя набора; пустое имя не принимается");
        _setRenameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                RenameSelectedSet();
                e.Handled = true;
            }
        };

        _setRenameButton = Ui.TextButton("SetRenameButton", "Переименовать", RenameSelectedSet, "compact");
        AutomationProperties.SetName(_setRenameButton, "Переименовать выбранный набор");
        ToolTip.SetTip(_setRenameButton, "Сохранить новое имя набора");

        _variantEarlierButton = Ui.IconButton(
            "VariantMoveEarlierButton", "Переместить вариант раньше", MapIcons.ArrowUp, () =>
            {
                _editor.MoveCurrentVariant(-1);
                SyncAssetList();
                SyncStatus();
            });
        _variantLaterButton = Ui.IconButton(
            "VariantMoveLaterButton", "Переместить вариант позже", MapIcons.ArrowDown, () =>
            {
                _editor.MoveCurrentVariant(1);
                SyncAssetList();
                SyncStatus();
            });
        var archiveButton = Ui.TextButton("SetArchiveButton", "В архив", () =>
        {
            _editor.ToggleSelectedSetArchived();
            SyncAssetList();
            SyncStatus();
        }, "compact");
        _quickFilterSyncs.Add(() =>
        {
            var archived = _editor.SelectedSetArchived;
            var caption = archived ? "Вернуть из архива" : "В архив";
            if (archiveButton.Content is TextBlock archiveText) archiveText.Text = caption;
            AutomationProperties.SetName(archiveButton, caption);
            ToolTip.SetTip(archiveButton, archived
                ? "Набор снова будет виден в каталоге без фильтра «Архивные»"
                : "Скрыть набор из каталога, ничего не удаляя; карты, где он стоит, не меняются");
        });
        _setCoverButton = Ui.TextButton("SetCoverButton", "Сделать обложкой", () =>
        {
            _editor.MakeCurrentVariantCover();
            SyncAssetList();
            SyncStatus();
        }, "compact");
        AutomationProperties.SetName(_setCoverButton, "Сделать текущий вариант обложкой набора");
        ToolTip.SetTip(_setCoverButton, "Обложка — вариант, который представляет набор в списке");

        _variantStateText.TextWrapping = TextWrapping.Wrap;
        _variantStateText.Classes.Add("role-caption");

        AutomationProperties.SetName(_variantStrip, "Варианты набора");

        _setManagementDetails = Disclosure(
            "SetManagementDetails",
            "Настроить набор",
            new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    BuildPlacementPassportBlock(),
                    archiveButton,
                    new DockPanel
                    {
                        Children =
                        {
                            _setRenameButton.Docked(Dock.Right),
                            _setRenameBox,
                        },
                    },
                    new DockPanel
                    {
                        Children =
                        {
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 2,
                                Children = { _variantEarlierButton, _variantLaterButton },
                            }.Docked(Dock.Right),
                            _setCoverButton,
                        },
                    },
                },
            },
            expanded: false);

        return new Border
        {
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new DockPanel
                    {
                        Children =
                        {
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 4,
                                Children = { _setBadge, _favoriteButton },
                            }.Docked(Dock.Right),
                            new TextBlock { Text = "Выбранный ассет" }.Classed("role-panel"),
                        },
                    },
                    _selectedSetNameText,
                    _assetSelectionText,
                    _variantStateText,
                    new DockPanel
                    {
                        Children =
                        {
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 2,
                                Children = { _previousVariantButton, _nextVariantButton },
                            }.Docked(Dock.Right),
                            _variantPositionText,
                        },
                    },
                    _variantStrip,
                    _setManagementDetails,
                },
            },
        }.Classed("group");
    }

    private void RenameSelectedSet()
    {
        if (_editor.RenameSelectedSet(_setRenameBox.Text))
        {
            SyncAssetList();
        }

        SyncStatus();
    }

    /// <summary>Shows what is known about the file behind the variant the tool is holding.</summary>
    private void SyncSelectedSetAvailability()
    {
        var availability = _editor.CurrentVariant is { } variant
            ? _editor.AvailabilityOf(variant.Sha256)
            : AssetAvailability.Unknown;
        // Without a variant there is no file to report on, and the position line right below
        // already says "Вариант не выбран" - printing it twice reads as a rendering fault.
        _variantStateText.IsVisible = _editor.CurrentVariant is not null;
        var visibleText = availability switch
        {
            AssetAvailability.Available => "Готов к размещению",
            AssetAvailability.Checking => "Проверяем файл…",
            AssetAvailability.Missing => "Файл варианта не найден",
            AssetAvailability.HashMismatch => "Файл изменён после импорта",
            AssetAvailability.Unsupported => "Формат варианта не поддерживается",
            _ => "Файл ещё не проверен",
        };
        // The UIA name starts with the words a sighted user sees and keeps the precise
        // diagnostic in the same breath, so a screen reader and a tooltip lose nothing.
        var accessibleText =
            $"{visibleText} ({EditorViewModel.AvailabilityText(availability)})";
        _variantStateText.Text = visibleText;
        Ui.SetClass(_variantStateText, "danger",
            availability is AssetAvailability.Missing or AssetAvailability.HashMismatch);
        Ui.SetClass(_variantStateText, "warn", availability is AssetAvailability.Unsupported);
        Ui.SetClass(_variantStateText, "faint",
            availability is AssetAvailability.Unknown or AssetAvailability.Checking);
        AutomationProperties.SetName(_variantStateText, accessibleText);
        ToolTip.SetTip(_variantStateText, accessibleText);
    }
    private Border BuildStampGestureBlock()
    {
        var (mode, modeButtons) = Ui.TallSegmented("StampMode", [
            ("One", "Один", MapIcons.ModeOne),
            ("Series", "Серия", MapIcons.ModeSeries),
            ("Area", "Россыпь", MapIcons.ModeScatter),
            ("Path", "Путь", MapIcons.ModePath),
            ("FillArea", "Область", MapIcons.ModeArea),
        ], key =>
        {
            _editor.StampPlacementMode = key switch
            {
                "Series" => StampPlacementMode.Series,
                "Area" => StampPlacementMode.Area,
                "Path" => StampPlacementMode.Path,
                "FillArea" => StampPlacementMode.FillArea,
                _ => StampPlacementMode.One,
            };
            SyncAssetList();
        });
        _placementModeButtons["One"] = modeButtons[0];
        _placementModeButtons["Series"] = modeButtons[1];
        _placementModeButtons["Area"] = modeButtons[2];
        _placementModeButtons["Path"] = modeButtons[3];
        _placementModeButtons["FillArea"] = modeButtons[4];

        var (autoSelect, autoSelectButtons) = Ui.Segmented("PlacementAutoSelect", [
            ("Off", "Не выделять", null),
            ("Always", "Выделять", null),
        ], key =>
        {
            _editor.SelectAfterPlacement = key == "Always";
            SyncStampGestureReadout();
        });
        _selectAfterPlacementButtons = (autoSelectButtons[0], autoSelectButtons[1]);
        ToolTip.SetTip(autoSelectButtons[0], "Поставленный ассет не выделяется: следующий щелчок сразу ставит ещё один. По умолчанию.");
        ToolTip.SetTip(autoSelectButtons[1], "Поставленное сразу выделяется — можно двигать и менять свойства; инструмент остаётся активным.");
        _stampAutoSelectRow = Ui.Subgroup("После размещения", autoSelect);

        var randomAngleChoice = AppearanceChoice(
            "RandomAngleStepChoice", "Случайный угол",
            [
                (nameof(StampRandomAngleStep.Off), "Выкл."),
                (nameof(StampRandomAngleStep.Degrees15), "15°"),
                (nameof(StampRandomAngleStep.Degrees30), "30°"),
                (nameof(StampRandomAngleStep.Degrees45), "45°"),
                (nameof(StampRandomAngleStep.Degrees60), "60°"),
                (nameof(StampRandomAngleStep.Degrees90), "90°"),
                (nameof(StampRandomAngleStep.Degrees180), "180°"),
            ],
            () => _editor.StampRandomAngleStep.ToString(),
            value => _editor.StampRandomAngleStep = Enum.Parse<StampRandomAngleStep>(value));
        _randomFlipToggle = LabelledToggle(
            "RandomFlipToggle", "Случайное отражение", "Случайное зеркальное отражение ассета",
            MapIcons.Select, () =>
            {
                _editor.StampRandomFlip = !_editor.StampRandomFlip;
                SyncAssetList();
            });

        _avoidOverlapToggle = LabelledToggle(
            "StampAvoidOverlapToggle", "Без наложений",
            "Россыпь не ставит ассет, если его рамка (около 30 % размера, задаётся в паспорте ассета) "
            + "пересекается с уже стоящими на слое или поставленными этим щелчком. Плотность — сколько попыток, "
            + "а не сколько точно встанет.",
            MapIcons.Select, () =>
            {
                _editor.StampAvoidOverlap = !_editor.StampAvoidOverlap;
                SyncAssetList();
            });
        _stampAreaControls = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _avoidOverlapToggle,
                AppearanceSlider("StampAreaSlider", "Радиус", 1, 1024, "ед.",
                    () => _editor.StampAreaRadius,
                    (value, commit) => _editor.SetStampAreaRadius(value, commit)),
                AppearanceSlider("StampDensitySlider", "Плотность", 0, 400, "",
                    () => _editor.StampDensity,
                    (value, commit) => _editor.SetStampDensity(value, commit)),
                _stampAreaSummaryText,
            },
        };

        _seriesSpacingControl = AppearanceSlider("StampSeriesSpacingSlider", "Шаг серии", 10, 400, "% размера",
            () => _editor.SeriesSpacingPercent, (value, commit) => _editor.SetSeriesSpacingPercent(value, commit));
        // Continuous variation per placed stamp, for series, paths and scatter: a forest or a
        // field stops reading as copies of one picture. Each slider is a ± range around the
        // chosen value; 0 keeps every stamp identical.
        var jitters = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Ui.Hint("Разброс действует в режимах «Серия», «Россыпь», «Путь» и «Область»: каждый поставленный ассет получает своё случайное отклонение."),
                AppearanceSlider("StampRotationJitterSlider", "Разброс поворота", 0, 180, "°",
                    () => _editor.StampRotationJitterDegrees, (v, _) => _editor.StampRotationJitterDegrees = v, 5,
                    defaultValue: 0, description: "Каждый ассет поворачивается на случайный угол в пределах ± этого значения."),
                AppearanceSlider("StampSizeJitterSlider", "Разброс размера", 0, 100, "%",
                    () => _editor.StampSizeJitterPercent, (v, _) => _editor.StampSizeJitterPercent = v, 5,
                    defaultValue: 0, description: "Размер каждого ассета меняется в пределах ± этой доли от заданного."),
                AppearanceSlider("StampOpacityJitterSlider", "Разброс непрозрачности", 0, 100, "%",
                    () => _editor.StampOpacityJitterPercent, (v, _) => _editor.StampOpacityJitterPercent = v, 5,
                    defaultValue: 0, description: "Часть ассетов становится бледнее — до этой доли."),
                AppearanceSlider("StampHueJitterSlider", "Разброс оттенка", 0, 180, "°",
                    () => _editor.StampHueJitterDegrees, (v, _) => _editor.StampHueJitterDegrees = v, 5,
                    defaultValue: 0, description: "Сдвиг оттенка каждого ассета в пределах ± этого угла цветового круга."),
                AppearanceSlider("StampSaturationJitterSlider", "Разброс насыщенности", 0, 100, "%",
                    () => _editor.StampSaturationJitterPercent, (v, _) => _editor.StampSaturationJitterPercent = v, 5,
                    defaultValue: 0, description: "Одни ассеты ярче по цвету, другие бледнее — в этих пределах."),
                AppearanceSlider("StampBrightnessJitterSlider", "Разброс яркости", 0, 100, "%",
                    () => _editor.StampBrightnessJitterPercent, (v, _) => _editor.StampBrightnessJitterPercent = v, 5,
                    defaultValue: 0, description: "Одни ассеты светлее, другие темнее — в этих пределах."),
            },
        };
        _stampRandomSection = new Ui.SectionPanel("StampRandomSection", "Случайность",
            new StackPanel { Spacing = 6, Children = { randomAngleChoice, _randomFlipToggle, jitters } }, false);
        _stampRandomSection.SetSummary("выкл.");
        _stampPlacementOptions = new Ui.SectionPanel("PlacementOptions", "Редкие действия", new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Ui.TextButton(
                        "CreateStampPlacementLayerButton",
                        "Создать слой объектов и продолжить",
                        () =>
                        {
                            _editor.CreateObjectLayerAndContinuePlacement();
                            SyncSidePanels();
                        }, "compact"),
                    AssetCommand("ReplaceStampVariantButton", "Заменить ассет выделенного объекта", () =>
                    {
                        _editor.ReplaceSelectedStampWithCurrentVariant();
                        SyncAssetList();
                        SyncStatus();
                    }, "compact"),
                    Ui.Advanced("AssetAdvanced", new StackPanel
                    {
                        Spacing = 5,
                        Children =
                        {
                            AssetCommand("PlaceStampButton", "Разместить в центре вида", () =>
                            {
                                _editor.PlaceCurrentVariantAtViewCentre();
                                RefreshLibraryPanel();
                            }, "compact"),
                            AssetCommand("PlaceLinkedStampButton", "Разместить как связанный", () =>
                            {
                                if (_selectedAsset is not null) _editor.PlaceStamp(_selectedAsset, embedded: false);
                            }, "compact"),
                            AssetCommand("ReplaceStampAssetButton", "Заменить ассет у выделенного объекта", () =>
                            {
                                if (_selectedAsset is not null)
                                    _editor.ReplaceSelectedStampAsset(_selectedAsset);
                            }, "compact"),
                            Ui.TextButton("KeepStampPlaceholderButton", "Оставить заменитель",
                                _editor.KeepSelectedStampPlaceholder, "compact"),
                            AssetCommand("RepairAssetSourceButton", "Найти исходник…",
                                () => UiTasks.Forget(this, RepairSelectedAssetAsync), "compact"),
                            AssetCommand("RemoveAssetRecordButton", "Удалить из каталога…",
                                () => UiTasks.Forget(this, RemoveSelectedAssetAsync), "destructive", "compact"),
                            AssetCommand("UseAssetStrokeButton", "Использовать для обводки", () =>
                            {
                                if (_selectedAsset is not null)
                                    _editor.UseAssetAsSelectedVectorStroke(_selectedAsset);
                                SyncAssetList();
                                SyncStatus();
                            }, "compact"),
                            AssetCommand("UseAssetShapeFillButton", "Использовать для заливки фигуры", () =>
                            {
                                if (_selectedAsset is not null)
                                    _editor.UseAssetAsSelectedShapeFill(_selectedAsset);
                                SyncAssetList();
                                SyncStatus();
                            }, "compact"),
                            AssetCommand("UseAssetBrushTipButton", "Использовать как отпечаток кисти", () =>
                            {
                                if (_selectedAsset is not null && _editor.UseAssetAsBrushTip(_selectedAsset))
                                {
                                    _editor.ActiveTool = EditorTool.Brush;
                                    SyncContextArea();
                                    SyncStatus();
                                }
                            }, "compact"),
                            AssetCommand("UseAssetBrushTextureButton", "Использовать как материал кисти", () =>
                            {
                                if (_selectedAsset is not null && _editor.UseAssetAsBrushTexture(_selectedAsset))
                                {
                                    _editor.ActiveTool = EditorTool.Brush;
                                    SyncContextArea();
                                    SyncStatus();
                                }
                            }, "compact"),
                            AssetCommand("UseAssetFilterButton", "Использовать в фильтре", () =>
                            {
                                if (_selectedAsset is not null)
                                    _editor.UseAssetAsActiveFilterTexture(_selectedAsset);
                            }, "compact"),
                        },
                    }),
                },
            }, false);
        ((Ui.SectionPanel)_stampPlacementOptions).SetSummary("замена, связанный, исходник…");
        return new Border
        {
            Child = new StackPanel { Spacing = 4, Children = { mode, _stampAreaControls, _seriesSpacingControl } },
        };
    }

    /// <summary>
    /// A standalone, always-visible toggle. The row-action class is deliberately invisible until
    /// its host row is hovered, which is right inside a list row and wrong for a filter bar.
    /// </summary>
    private static ToggleButton FilterToggle(string name, string title, string icon, Action action)
    {
        var button = new ToggleButton { Content = MapIcons.Stroked(icon, 15) }
            .Named(name).Classed("segment");
        AutomationProperties.SetName(button, title);
        ToolTip.SetTip(button, title);
        button.Click += (_, _) => action();
        return button;
    }

    private static void SetToggle(Button button, string title, bool on)
    {
        AutomationProperties.SetName(button, title);
        ToolTip.SetTip(button, title);
        Ui.SetSelected(button, on);
    }

    private void SyncStampGestureReadout()
    {
        _stampAreaControls.IsVisible = _editor.StampPlacementMode == StampPlacementMode.Area;
        if (_seriesSpacingControl is not null) _seriesSpacingControl.IsVisible = _editor.StampPlacementMode == StampPlacementMode.Series;
        _stampAreaSummaryText.Text = _editor.StampAreaSummary;
        AutomationProperties.SetName(_stampAreaSummaryText, _editor.StampAreaSummary);
        SetSegment(_placementModeButtons, _editor.StampPlacementMode switch
        {
            StampPlacementMode.Series => "Series",
            StampPlacementMode.Area => "Area",
            StampPlacementMode.Path => "Path",
            StampPlacementMode.FillArea => "FillArea",
            _ => "One",
        });
        Ui.SetSelected(_selectAfterPlacementButtons.DontSelect, !_editor.SelectAfterPlacement);
        Ui.SetSelected(_selectAfterPlacementButtons.Select, _editor.SelectAfterPlacement);
        SyncStampToolPanel();
    }

    private Control BuildCompactStampCatalog()
    {
        _stampToolThumbnail = new Image { Width = 88, Height = 88, Stretch = Stretch.Uniform };
        _stampVariantStrip = BuildCompactVariantList();
        _stampRecentStrip = BuildCompactSetList("StampRecentSet");
        _stampFavoriteStrip = BuildCompactSetList("StampFavoriteSet");
        AutomationProperties.SetName(_stampVariantStrip, "Варианты текущего семейства");
        AutomationProperties.SetName(_stampRecentStrip, "Недавние ассеты");
        AutomationProperties.SetName(_stampFavoriteStrip, "Избранные ассеты");
        _stampVariantCounter = new TextBlock { MinWidth = 42, TextAlignment = Avalonia.Media.TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center }.Classed("role-caption");
        _stampVariantName = Ui.Trimmed("", "role-caption");
        var previous = Ui.TextButton("StampPreviousVariantButton", "‹", () =>
        {
            _editor.PreviousStampFamilyVariant(); SyncStampToolPanel();
        }, "compact");
        var next = Ui.TextButton("StampNextVariantButton", "›", () =>
        {
            _editor.NextStampFamilyVariant(); SyncStampToolPanel();
        }, "compact");
        previous.Width = next.Width = 30;
        _commandButtons.Add((previous, "Предыдущий вариант", EditorShortcutSettings.Commands.StampPreviousVariant));
        _commandButtons.Add((next, "Следующий вариант", EditorShortcutSettings.Commands.StampNextVariant));

        // Header: the name of the asset and the two switches that belong to the asset itself.
        _randomVariantToggle = new ToggleButton { Content = MapIcons.Stroked(MapIcons.Duplicate, 15), Width = 28, Height = 28, MinHeight = 28,
            Padding = new Thickness(0) }.Named("RandomVariantToggle").Classed("segment");
        _randomVariantToggle.Click += (_, _) =>
        {
            _editor.RandomVariantAfterPlacement = !_editor.RandomVariantAfterPlacement;
            SyncAssetList();
        };
        _tweakColorsToggle = new ToggleButton { Content = MapIcons.Stroked(MapIcons.Color, 15), Width = 28, Height = 28, MinHeight = 28,
            Padding = new Thickness(0) }.Named("StampTweakColorsToggle").Classed("segment");
        AutomationProperties.SetName(_tweakColorsToggle, "Настроить цвета");
        ToolTip.SetTip(_tweakColorsToggle, "Оттенок, насыщенность, яркость и контраст будущего ассета");
        _tweakColorsToggle.Click += (_, _) =>
        {
            var on = _tweakColorsToggle.IsChecked == true;
            _stampColorsBlock.IsVisible = on;
            Ui.SetSelected(_tweakColorsToggle, on);
        };
        var header = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _randomVariantToggle, _tweakColorsToggle } }
                    .Docked(Dock.Right),
                _stampToolCurrentText,
            },
        };

        _stampPreviewButton = new Button
        {
            Name = "StampPreviewButton", Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand),
            Content = _stampToolThumbnail,
        }.Classed("asset-tile");
        _stampPreviewButton.Click += (_, _) => OpenAssetCatalog(focusSearch: false);
        var openLibrary = ShortcutTextButton(
            "OpenLibraryButton", "Каталог",
            EditorShortcutSettings.Commands.StampFocusLibrary,
            () => OpenAssetCatalog(focusSearch: true), "compact");
        var pager = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                openLibrary.Docked(Dock.Right),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { previous, _stampVariantCounter, next } },
            },
        };

        _stampFavoriteToggle = new ToggleButton { Content = new TextBlock { Text = "★", FontSize = 15 }, Width = 28, Height = 28, MinHeight = 28,
            Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center }.Named("StampFavoriteToggle").Classed("segment");
        _stampFavoriteToggle.Click += (_, _) =>
        {
            _editor.ToggleSelectedSetFavorite();
            SyncAssetList();
            SyncStatus();
        };
        var strips = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Недавние", Content = _stampRecentStrip },
                new TabItem { Header = "Избранное", Content = _stampFavoriteStrip },
            },
        };
        _stampStripsHost = new DockPanel { LastChildFill = true, Children = { _stampFavoriteToggle.Docked(Dock.Right), strips } };

        _stampPlaceCaption = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        _stampCurrentAssetButton = new Button
        {
            Name = "StampCurrentAssetButton",
            Padding = new Thickness(8, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { MapIcons.Stroked(MapIcons.ObjectStamp, 14), _stampPlaceCaption } },
        }.Classed("compact", "accent");
        _stampCurrentAssetButton.Click += (_, _) =>
        {
            if (_editor.StampPlacementMode == StampPlacementMode.FillArea) _editor.StartEditableArea();
            else if (_editor.StampPlacementMode == StampPlacementMode.Path) _editor.StartEditablePath();
            else _editor.ArmStampPlacement();
            _canvas.ShowPlacementPreviewAtCenter();
            SyncContextArea();
            _canvas.Focus();
        };
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                header,
                _stampPreviewButton,
                pager,
                _stampVariantName,
                _stampVariantStrip,
                _stampCurrentAssetButton,
            },
        };
    }
    private Button CompactStampSetButton(AssetSetSummary summary, string prefix)
    {
        var image = new Image { Stretch = Stretch.Uniform };
        _thumbnails.Assign(image, _editor.LibraryThumbnailPath(summary));
        var button = new Button
        {
            Name = prefix + "_" + Sanitize(summary.SetId),
            Width = 62,
            Padding = new Thickness(3),
            Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new Border
                    {
                        Width = 40,
                        Height = 40,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Child = image,
                    },
                    new TextBlock
                    {
                        Text = (summary.Favorite ? "★ " : "") + summary.Name,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextAlignment = Avalonia.Media.TextAlignment.Center,
                        FontSize = 12,
                    },
                },
            },
        }.Classed("compact");
        AutomationProperties.SetName(button, $"Выбрать набор {summary.Name}"); ToolTip.SetTip(button, summary.Name);
        button.Click += (_, _) =>
        {
            _editor.UseSinglePlacementForNewAsset();
            _editor.SelectAssetSet(summary.SetId);
            SyncStampToolPanel();
        };
        return button;
    }

    /// <summary>
    /// The count on "К базовым" and the change marks of the asset sections, from one list of what
    /// differs from the base values: a folded section can never hide a changed setting.
    /// </summary>
    private void SyncStampResetButton()
    {
        if (_stampResetButton is null || _stampHowSection is null) return;
        var changes = _editor.StampToolChanges;
        _stampResetCaption.Text = changes.Count == 0 ? "К базовым" : $"К базовым · {changes.Count}";
        string? Changed(StampSettingGroup group) =>
            string.Join(", ", changes.Where(c => c.Group == group).Select(c => c.Label)) is { Length: > 0 } text ? text : null;
        _stampHowSection.SetSummary(_editor.StampPlacementMode switch
        {
            StampPlacementMode.Series => "серия",
            StampPlacementMode.Area => "россыпь",
            StampPlacementMode.Path => "путь",
            StampPlacementMode.FillArea => "область",
            _ => "один объект",
        });
        _stampHowSection.SetChange(Changed(StampSettingGroup.Placement));
        _stampNextSection.SetSummary($"{_editor.StampScalePercent:0}% · {_editor.StampRotationDegrees:0}° · ур. {_editor.StampSublayer}");
        _stampNextSection.SetChange(Changed(StampSettingGroup.NextAsset));
        _stampLookSection.SetSummary("без эффектов");
        _stampLookSection.SetChange(Changed(StampSettingGroup.Appearance));
        _stampRandomSection.SetChange(Changed(StampSettingGroup.Randomness));
    }

    private void SyncStampToolPanel()
    {
        if (_stampToolCurrentText is null || _stampToolAreaText is null)
        {
            return;
        }

        var set = _editor.SelectedSet;
        _stampToolCurrentText.Text = set is null
            ? "Набор не выбран. Откройте библиотеку и выберите карточку."
            : _editor.StampFamilyName;
        _stampToolCurrentText.MaxLines = 1;
        _stampToolCurrentText.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(_stampToolCurrentText, _stampToolCurrentText.Text);
        _stampVariantCounter.Text = _editor.CurrentStampFamilyLabel;
        _stampVariantName.Text = _editor.SelectedSetVariants.Count == 1 && set is not null
            ? set.Name : _editor.CurrentVariant?.Label ?? "Выберите вариант";
        _randomVariantToggle.IsVisible = _editor.SelectedSetVariants.Count > 1;
        ToolTip.SetTip(_stampVariantName, _stampVariantName.Text);
        _stampCurrentAssetButton.IsEnabled = _editor.CurrentVariant is not null;
        var currentAction = _editor.StampPlacementMode switch
        {
            StampPlacementMode.Path => "Начать новый путь",
            StampPlacementMode.FillArea => "Новая область",
            _ => "Разместить ещё",
        };
        _stampPlaceCaption.Text = currentAction;
        var currentHint = _editor.CurrentVariant is null ? "Сначала выберите ассет в каталоге."
            : $"{currentAction}: {set?.Name} · {_editor.CurrentVariant.Label}";
        AutomationProperties.SetName(_stampCurrentAssetButton, currentHint);
        ToolTip.SetTip(_stampCurrentAssetButton, currentHint);
        var previewHint = set is null ? "Открыть каталог ассетов (F)" : $"{set.Name} · открыть каталог (F)";
        AutomationProperties.SetName(_stampPreviewButton, previewHint);
        ToolTip.SetTip(_stampPreviewButton, previewHint);
        SetToggle(_stampFavoriteToggle, set?.Favorite == true ? "Убрать из избранного" : "В избранное", set?.Favorite == true);
        _stampFavoriteToggle.IsEnabled = set is not null;
        _stampToolAreaText.Text = _editor.StampAreaSummary;
        _thumbnails.Assign(_stampToolThumbnail,
            _editor.CurrentVariant is { } variant
                ? _editor.LibraryThumbnailPathForVariant(variant.Id)
                : null);
        SyncStampResetButton();
        // Without a chosen asset the placement settings have nothing to describe: the panel
        // says what to do instead of showing sliders at their minimum.
        var chosen = _editor.CurrentVariant is not null;
        _stampEmptyState.IsVisible = !chosen;
        _stampCatalogHost.IsVisible = chosen;
        _stampHowSection.IsVisible = chosen;
        _stampNextSection.IsVisible = chosen;
        _stampLookSection.IsVisible = chosen;
        _stampRandomSection.IsVisible = chosen;
        SyncAreaAssetCard();
        SyncCompactVariantStrip();
        SyncCompactSetList(_stampRecentStrip, _editor.CompactRecentSets);
        SyncCompactSetList(_stampFavoriteStrip, _editor.CompactFavoriteSets);
        AutomationProperties.SetName(_stampToolCurrentText, _stampToolCurrentText.Text);
        AutomationProperties.SetName(_stampToolAreaText, _stampToolAreaText.Text);
    }

    private void SyncCompactVariantStrip()
    {
        var variants = _editor.StampFamilyVariants;
        if (!ReferenceEquals(_compactVariants, variants))
        {
            _compactVariants = variants;
            _stampVariantStrip.ItemsSource = variants.Select((v, i) => new CompactVariant(v.Variant, i)).Chunk(3).ToArray();
        }
        foreach (var button in _stampVariantStrip.GetVisualDescendants().OfType<ToggleButton>())
            Ui.SetSelected(button, button.Name == "StampVariant_" + _editor.CurrentVariant?.Id);
        _stampVariantStrip.IsVisible = variants.Count > 0;
    }

    /// <summary>
    /// Asks for the name of a newly imported set. It lives here rather than beside the other
    /// prompts because it belongs to this one flow and needs its own automation ids.
    /// </summary>
    private static async System.Threading.Tasks.Task<string?> AskSetNameAsync(
        Window? owner, string suggested)
    {
        var input = new TextBox { Name = "SetNameBox", Text = suggested, MinWidth = 320 };
        AutomationProperties.SetName(input, "Имя набора");

        var completion = new System.Threading.Tasks.TaskCompletionSource<string?>();
        var window = new Window
        {
            Title = "Имя набора",
            Name = "SetNameWindow",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var confirm = Ui.TextButton("ConfirmSetNameButton", "Создать набор", () =>
        {
            completion.TrySetResult((input.Text ?? "").Trim());
            window.Close();
        });
        var cancel = Ui.TextButton("CancelSetNameButton", "Отмена", () =>
        {
            completion.TrySetResult(null);
            window.Close();
        });

        window.Content = new Border
        {
            Padding = new Thickness(18),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Как назвать набор вариантов?" }.Classed("role-panel"),
                    input,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, confirm },
                    },
                },
            },
        };

        window.Closed += (_, _) => completion.TrySetResult(null);
        window.Opened += (_, _) => input.Focus();

        if (owner is not null)
        {
            await window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }

        return await completion.Task;
    }

    /// <summary>
    /// A bounded LRU of decoded previews. A tile asks for its picture with <see cref="Assign"/>:
    /// a cached preview is shown at once, anything else is decoded on a worker and delivered when
    /// it lands, so scrolling a large library never decodes on the UI thread. A preview smaller
    /// than the catalog standard is rebuilt from its original on the same worker before it is
    /// shown, which is how catalogs written with 128 px previews sharpen up on first sight.
    /// </summary>
    private sealed class ThumbnailCache(int capacity) : IDisposable
    {
        private const int MaxThumbnailDimension = 1024;
        private const long MaxDecodedBytes = 64L * 1024 * 1024;
        private static readonly SemaphoreSlim DecodeGate = new(2, 2);
        private readonly Dictionary<string, (Bitmap Image, long Used, long Bytes)> _entries =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Image>> _pending = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _rebuilt = new(StringComparer.OrdinalIgnoreCase);
        private long _clock;
        private long _decodedBytes;
        private bool _disposed;

        /// <summary>Names the original behind a preview file; null when it cannot be rebuilt.</summary>
        public Func<string, string?>? OriginalFor { get; set; }

        public void Dispose()
        {
            _disposed = true;
            // Evicted and disposed pictures are not disposed here either: an Image that is still
            // on screen may draw them once more before it is torn down.
            _entries.Clear(); _decodedBytes = 0; _pending.Clear();
        }

        /// <summary>Shows the preview in the target as soon as it is available.</summary>
        public void Assign(Image target, string? path)
        {
            ArgumentNullException.ThrowIfNull(target);
            target.Tag = path;
            if (string.IsNullOrEmpty(path)) { target.Source = null; return; }
            if (_entries.TryGetValue(path, out var hit))
            {
                _entries[path] = (hit.Image, ++_clock, hit.Bytes);
                target.Source = hit.Image;
                return;
            }

            target.Source = null;
            if (_pending.TryGetValue(path, out var waiters)) { waiters.Add(target); return; }
            _pending[path] = [target];
            string? original = null;
            if (_rebuilt.Add(path)) original = OriginalFor?.Invoke(path);
            UiTasks.Forget(target, () => LoadAsync(path, original));
        }

        private async Task LoadAsync(string path, string? original)
        {
            (Bitmap? Image, long Bytes) loaded = default;
            await DecodeGate.WaitAsync();
            try { loaded = await Task.Run(() => Decode(path, original)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                loaded = default;
            }
            finally { DecodeGate.Release(); }

            // Task.Run continuations come back on the UI thread through the Avalonia context.
            if (_disposed) return;
            _pending.Remove(path, out var waiters);
            if (loaded.Image is null) return;
            Store(path, loaded.Image, loaded.Bytes);
            if (waiters is null) return;
            foreach (var image in waiters)
                if (string.Equals(image.Tag as string, path, StringComparison.OrdinalIgnoreCase)) image.Source = loaded.Image;
        }

        private static (Bitmap? Image, long Bytes) Decode(string path, string? original)
        {
            var encoded = AssetImagePolicy.ReadEncodedFile(path);
            var metadata = AssetImagePolicy.Inspect(encoded);
            if (original is not null && Math.Max(metadata.Width, metadata.Height) < AssetCatalog.ThumbnailSize)
            {
                try
                {
                    AssetCatalog.RebuildThumbnailFile(original, path);
                    encoded = AssetImagePolicy.ReadEncodedFile(path);
                    metadata = AssetImagePolicy.Inspect(encoded);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or InvalidDataException or InvalidOperationException)
                {
                    // The old preview is still a preview; it is shown as it is.
                }
            }

            if (metadata.Width > MaxThumbnailDimension || metadata.Height > MaxThumbnailDimension) return default;
            using var stream = new MemoryStream(encoded, writable: false);
            return (new Bitmap(stream), metadata.DecodedBytes);
        }

        private void Store(string path, Bitmap bitmap, long bytes)
        {
            while (_entries.Count > 0 && (_entries.Count >= capacity || _decodedBytes > MaxDecodedBytes - bytes))
            {
                string? oldestKey = null; var oldestUsed = long.MaxValue;
                foreach (var pair in _entries)
                    if (pair.Value.Used < oldestUsed) { oldestUsed = pair.Value.Used; oldestKey = pair.Key; }
                if (oldestKey is null) break;
                _decodedBytes -= _entries[oldestKey].Bytes;
                _entries.Remove(oldestKey);
            }
            _entries[path] = (bitmap, ++_clock, bytes);
            _decodedBytes += bytes;
        }

        /// <summary>A preview right now, or null while it is still being decoded.</summary>
        public Bitmap? Image(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_entries.TryGetValue(path, out var hit))
            {
                _entries[path] = (hit.Image, ++_clock, hit.Bytes);
                return hit.Image;
            }

            Bitmap bitmap;
            AssetImageMetadata metadata;
            try
            {
                var encoded = AssetImagePolicy.ReadEncodedFile(path);
                metadata = AssetImagePolicy.Inspect(encoded);
                if (metadata.Width > MaxThumbnailDimension || metadata.Height > MaxThumbnailDimension) return null;
                using var stream = new MemoryStream(encoded, writable: false);
                bitmap = new Bitmap(stream);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException)
            {
                return null;
            }

            Store(path, bitmap, metadata.DecodedBytes);
            return bitmap;
        }
    }

    private sealed class CheckpointPreviewCache : IDisposable
    {
        private readonly Dictionary<CheckpointId, Bitmap> _entries = [];

        public Bitmap? Image(MapCheckpoint checkpoint)
        {
            if (_entries.TryGetValue(checkpoint.Id, out var cached))
            {
                return cached;
            }

            try
            {
                using var stream = new MemoryStream(checkpoint.PreviewPng.ToArray(), writable: false);
                var bitmap = new Bitmap(stream);
                _entries.Add(checkpoint.Id, bitmap);
                return bitmap;
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                return null;
            }
        }

        public void Prune(IReadOnlyCollection<CheckpointId> activeIds)
        {
            var active = new HashSet<CheckpointId>(activeIds);
            foreach (var id in _entries.Keys.Where(id => !active.Contains(id)).ToList())
            {
                _entries[id].Dispose();
                _entries.Remove(id);
            }
        }

        public void Dispose()
        {
            foreach (var bitmap in _entries.Values)
            {
                bitmap.Dispose();
            }
            _entries.Clear();
        }
    }

    /// <summary>
    /// An asset command that is only enabled once an asset is actually selected. A control that
    /// silently does nothing when pressed is worse than one that says it is unavailable.
    /// </summary>
    private Button AssetCommand(string name, string text, Action action, params string[] classes)
    {
        var button = Ui.TextButton(name, text, action, classes);
        button.IsEnabled = false;
        ToolTip.SetTip(button, $"{text}. Сначала выберите ассет в списке выше.");
        _assetSelectionCommands.Add(button);
        return button;
    }

    private Control BuildNotesTab()
    {
        var preview = new TextBlock
        {
            Name = "NoteRenderedPreview",
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 72,
        };
        var linkTarget = new TextBox
        {
            Name = "NoteLinkTargetBox",
            PlaceholderText = "Адрес ссылки",
        };
        void ReplaceSelection(string before, string after, string placeholder)
        {
            var text = _noteMarkdown.Text ?? "";
            var start = Math.Clamp(_noteMarkdown.SelectionStart, 0, text.Length);
            var end = Math.Clamp(_noteMarkdown.SelectionEnd, start, text.Length);
            var selected = end > start ? text[start..end] : placeholder;
            _noteMarkdown.Text = text[..start] + before + selected + after + text[end..];
            _noteMarkdown.SelectionStart = start + before.Length;
            _noteMarkdown.SelectionEnd = start + before.Length + selected.Length;
            _noteMarkdown.Focus();
        }
        void PrefixSelection(string prefix)
        {
            var text = _noteMarkdown.Text ?? "";
            var start = Math.Clamp(_noteMarkdown.SelectionStart, 0, text.Length);
            var end = Math.Clamp(_noteMarkdown.SelectionEnd, start, text.Length);
            var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
            var lineEnd = text.IndexOf('\n', end);
            if (lineEnd < 0) lineEnd = text.Length;
            var block = text[lineStart..lineEnd];
            var changed = string.Join("\n", block.Split('\n').Select(line => prefix + line));
            _noteMarkdown.Text = text[..lineStart] + changed + text[lineEnd..];
            _noteMarkdown.SelectionStart = lineStart;
            _noteMarkdown.SelectionEnd = lineStart + changed.Length;
            _noteMarkdown.Focus();
        }
        void RefreshPreview()
        {
            try
            {
                preview.Text = NoteMarkdown.RenderPlain(_noteMarkdown.Text ?? "");
                Ui.SetClass(preview, "error", false);
            }
            catch (ArgumentException error)
            {
                preview.Text = "Предпросмотр недоступен: " + error.Message;
                Ui.SetClass(preview, "error", true);
            }
        }
        _noteMarkdown.TextChanged += (_, _) => RefreshPreview();
        RefreshPreview();

        // Formatting as one toolbar row, its words in the tooltips.
        Button Format(string name, string caption, string tip, Action action, FontWeight weight = default, FontStyle style = FontStyle.Normal)
        {
            var button = new Button
            {
                Content = new TextBlock { Text = caption, FontWeight = weight == default ? FontWeight.SemiBold : weight, FontStyle = style },
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                MinHeight = 30, Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 0),
            }.Named(name).Classed("compact");
            AutomationProperties.SetName(button, tip);
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => action();
            return button;
        }

        var link = Ui.IconButton("NoteLinkButton", "Ссылка для выделенного текста", MapIcons.Open, () =>
            ReplaceSelection("[", $"]({(linkTarget.Text ?? "").Trim()})", "текст ссылки"));
        var listPage = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Hint("Заметки видны только вам: на карту и в экспорт они не попадают."),
                _noteSearch,
                _noteList,
                Ui.Subgroup("Новая заметка", new Avalonia.Controls.Primitives.UniformGrid
                {
                    Rows = 1, Columns = 2,
                    Children =
                    {
                        Ui.CommandButton("AddWorldNoteButton", "В центре", MapIcons.Plus, _editor.AddNoteAtViewCentre, "compact"),
                        Ui.CommandButton("AddObjectNoteButton", "К объекту", MapIcons.LineTarget, _editor.AddNoteForSelection, "compact"),
                    },
                }),
            },
        };
        _noteSelectionEditor = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Ui.Subgroup("Заголовок", _noteTitle),
                new Avalonia.Controls.Primitives.UniformGrid
                {
                    Rows = 1, Columns = 4,
                    Children =
                    {
                        Format("NoteHeadingButton", "H", "Заголовок: строка начнётся с ##", () => PrefixSelection("## ")),
                        Format("NoteListButton", "•", "Список: строки начнутся с -", () => PrefixSelection("- ")),
                        Format("NoteBoldButton", "Ж", "Полужирный", () => ReplaceSelection("**", "**", "текст"), FontWeight.Black),
                        Format("NoteItalicButton", "К", "Курсив", () => ReplaceSelection("*", "*", "текст"), FontWeight.Normal, FontStyle.Italic),
                    },
                },
                new DockPanel { Children = { link.Docked(Dock.Right), linkTarget } },
                Ui.Subgroup("Текст заметки", _noteMarkdown),
                Ui.Subgroup("Как выглядит", new Border { Padding = new Thickness(8), Child = preview }.Classed("group")),
                BuildNoteWiki(),
                new Avalonia.Controls.Primitives.UniformGrid
                {
                    Rows = 1, Columns = 3,
                    Children =
                    {
                        Ui.CommandButton("SaveNoteButton", "Сохранить", MapIcons.Check, () =>
                        {
                            if (_selectedNoteId is { } id)
                                _editor.EditNote(id, _noteTitle.Text ?? "", _noteMarkdown.Text ?? "");
                        }, "compact"),
                        Ui.CommandButton("GoToNoteButton", "Перейти", MapIcons.LineTarget, () =>
                        {
                            if (_selectedNoteId is { } id) _editor.GoToNote(id);
                        }, "compact"),
                        Ui.CommandButton("DeleteNoteButton", "Удалить", MapIcons.Trash, () =>
                        {
                            if (_selectedNoteId is { } id)
                            {
                                _editor.DeleteNote(id);
                                _selectedNoteId = null;
                            }
                        }, "destructive", "compact"),
                    },
                },
            },
        };
        linkTarget.MinWidth = 0;
        linkTarget.Margin = new Thickness(0, 0, 4, 0);

        // Two tabs (audit 2026-10-19): the list with the new-note commands, and the chosen note,
        // which opens by itself when a note is picked.
        return Ui.CategoryTabs("NotesTabs",
            new Ui.CategoryTab("List", "Все заметки", MapIcons.LineList, listPage, "Поиск и список заметок, новая заметка"),
            new Ui.CategoryTab("Note", "Заметка", MapIcons.LineNotes, _noteSelectionEditor, "Заголовок, текст и действия выбранной заметки", Prefer: true),
            new Ui.CategoryTab("Entities", "Сущности", MapIcons.LineCatalog, BuildEntitiesPage(), "Карточки мира: места, реки, персонажи, фракции"),
            new Ui.CategoryTab("Fog", "Туман", MapIcons.HexPlayer, BuildFogPage(), "Туман войны: что уже видели игроки; окно игрока"),
            new Ui.CategoryTab("Transitions", "Переходы", MapIcons.LineRoad, BuildTransitionsPage(), "Лестницы, пещеры, шахты и порталы между уровнями и картами"));
    }

    private Control BuildCheckpointsTab()
    {
        var create = Ui.AsyncButton("CreateVersionButton", "Снимок версии", async () =>
        {
            await _editor.CreateVersionAsync();
            SyncCheckpointList();
            SyncStatus();
        }, "accent", "compact");
        AutomationProperties.SetName(create, "Создать версию текущей карты");
        _createVersionButton = create;

        var body = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Версии" }.Classed("role-panel"),
                new TextBlock
                {
                    Text = "Один щелчок — снимок карты с миниатюрой, названием «Версия NN» и временем. Название можно изменить позже, "
                        + "восстановление отменяется Ctrl+Z.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
                new DockPanel { Children = { _checkpointCountText.Docked(Dock.Right), create } },
                BuildCompareBlock(),
                BuildPackageBlock(),
                new Expander { Header = "Ограничения версий", Content = new TextBlock
                {
                    Name = "CheckpointLimitsText",
                    Text = $"До {MapCheckpointLimits.MaxCount} версий · всего до "
                        + $"{MapCheckpointLimits.MaxTotalStoredBytes / (1024 * 1024)} МиБ данных",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption") },
                _checkpointList,
            },
        };

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = body,
        };
    }

    private Control BuildCanvasWorkflowTab()
    {
        _resizeModeButton = Ui.TextButton(
            "ResizeModeButton", "Изменить размер карты",
            () =>
            {
                _editor.SetCanvasWorkflowMode(ResizeRegionMode.Resize);
                SyncCanvasWorkflow();
                _canvas.FitToCanvas();
            }, "segment", "mode-choice");
        _regionModeButton = Ui.TextButton(
            "RegionModeButton", "Создать карту из региона",
            () =>
            {
                _editor.SetCanvasWorkflowMode(ResizeRegionMode.Region);
                SyncCanvasWorkflow();
                _canvas.FitToCanvas();
            }, "segment", "mode-choice");

        var modeRow = new Ui.ChoiceGrid
        {
            MaxColumns = 2,
            MinimumItemWidth = 180,
            Children = { _resizeModeButton, _regionModeButton },
        };

        var sizeFields = new UniformGrid
        {
            Columns = 2,
            Children =
            {
                Field("Ширина, px", _canvasWidthBox),
                Field("Высота, px", _canvasHeightBox),
            },
        };

        var anchorGrid = new Ui.ChoiceGrid { MaxColumns = 3, MinimumItemWidth = 36, Gap = 6,
            Width = 138, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (anchor, glyph, name) in new[]
                 {
                     (CanvasAnchor.TopLeft, "↖", "Верхний левый"),
                     (CanvasAnchor.TopCenter, "↑", "Верхний центр"),
                     (CanvasAnchor.TopRight, "↗", "Верхний правый"),
                     (CanvasAnchor.CenterLeft, "←", "Центр слева"),
                     (CanvasAnchor.Center, "●", "Центр"),
                     (CanvasAnchor.CenterRight, "→", "Центр справа"),
                     (CanvasAnchor.BottomLeft, "↙", "Нижний левый"),
                     (CanvasAnchor.BottomCenter, "↓", "Нижний центр"),
                     (CanvasAnchor.BottomRight, "↘", "Нижний правый"),
                 })
        {
            var captured = anchor;
            var button = Ui.TextButton(
                "CanvasAnchor_" + anchor, glyph,
                () =>
                {
                    _editor.SetCanvasAnchor(captured);
                    SyncCanvasWorkflow();
                }, "segment", "mode-choice");
            AutomationProperties.SetName(button, "Anchor: " + name);
            ToolTip.SetTip(button, "Сохранить на месте: " + name.ToLowerInvariant());
            _anchorButtons.Add(anchor, button);
            anchorGrid.Children.Add(button);
        }

        var applyResize = Ui.TextButton(
            "ApplyCanvasResizeButton", "Применить размер",
            () =>
            {
                if (_editor.ApplyCanvasResize())
                {
                    _canvasWorkflowWindow?.Hide();
                    _canvas.FitToCanvas();
                }
                SyncCanvasWorkflow();
            }, "primary");

        _resizeWorkflowPanel = new StackPanel
        {
            Name = "ResizeWorkflowPanel",
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Новый размер не масштабирует содержимое. Красная область станет скрытой, зелёная — новой.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
                sizeFields,
                new TextBlock { Text = "Какая точка старого содержимого остаётся на месте" }
                    .Classed("role-caption"),
                anchorGrid,
                applyResize,
            },
        };

        var regionFields = new UniformGrid
        {
            Columns = 2,
            Children =
            {
                Field("X, px", _regionXBox),
                Field("Y, px", _regionYBox),
                Field("Ширина, px", _regionWidthBox),
                Field("Высота, px", _regionHeightBox),
            },
        };
        var createRegion = Ui.AsyncButton(
            "CreateRegionMapButton", "Создать отдельный .mapproj",
            async () =>
            {
                if (await _editor.CreateMapFromRegionAsync())
                {
                    _canvasWorkflowWindow?.Hide();
                }
                SyncCanvasWorkflow();
            }, "primary");

        _regionWorkflowPanel = new StackPanel
        {
            Name = "RegionWorkflowPanel",
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Растяните прямоугольник на карте в любом направлении или задайте точные поля. Исходная карта останется открытой и не изменится.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
                regionFields,
                Field("Название новой карты", _regionTitleBox),
                createRegion,
            },
        };

        _boundarySnapToggle = new ToggleButton
        {
            Name = "BoundarySnapToggle",
            Content = "Привязать границы к активной сетке",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        }.Classed("segment");
        AutomationProperties.SetName(_boundarySnapToggle, "Привязать границы к активной сетке");
        _boundarySnapToggle.IsCheckedChanged += (_, _) =>
        {
            if (!_syncingCanvasWorkflow)
            {
                _editor.SetCanvasBoundarySnap(_boundarySnapToggle.IsChecked == true);
                SyncCanvasWorkflow();
            }
        };

        WireText(_canvasWidthBox, value => _editor.CanvasWidthText = value);
        WireText(_canvasHeightBox, value => _editor.CanvasHeightText = value);
        WireText(_regionXBox, value => _editor.RegionXText = value);
        WireText(_regionYBox, value => _editor.RegionYText = value);
        WireText(_regionWidthBox, value => _editor.RegionWidthText = value);
        WireText(_regionHeightBox, value => _editor.RegionHeightText = value);
        WireText(_regionTitleBox, value => _editor.RegionTitleText = value);

        var cancel = Ui.TextButton(
            "CancelCanvasWorkflowButton", "Отмена",
            () =>
            {
                _editor.CancelCanvasWorkflow();
                _canvasWorkflowWindow?.Hide();
                _canvas.FitToCanvas();
            }, "ghost");

        _canvasWorkflowSummary.TextWrapping = TextWrapping.Wrap;
        _canvasObjectSummary.TextWrapping = TextWrapping.Wrap;
        _canvasWorkflowError.TextWrapping = TextWrapping.Wrap;
        _canvasWorkflowError.Classed("danger", "role-caption");

        var body = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Размер и регион" }.Classed("role-panel"),
                modeRow,
                _boundarySnapToggle,
                _resizeWorkflowPanel,
                _regionWorkflowPanel,
                _canvasWorkflowSummary,
                _canvasObjectSummary,
                _canvasWorkflowError,
                cancel,
            },
        };

        SyncCanvasWorkflow();
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = body,
        };

        static Control Field(string label, Control control) => new StackPanel
        {
            Margin = new Thickness(3),
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = label }.Classed("faint", "role-caption"),
                control,
            },
        };

        void WireText(TextBox box, Action<string> write) => box.TextChanged += (_, _) =>
        {
            if (!_syncingCanvasWorkflow)
            {
                write(box.Text ?? "");
                SyncCanvasWorkflow();
            }
        };
    }

    private TextBlock? _vttSummaryText;

    /// <summary>The dd2vtt export: grid size, what will be written, and its own button.</summary>
    private Control BuildVttExportBlock()
    {
        _vttSummaryText = new TextBlock { TextWrapping = TextWrapping.Wrap }.Named("ExportVttSummary").Classed("role-caption");
        var start = Ui.AsyncButton("StartVttExportButton", "Экспорт для виртуального стола (*.dd2vtt)…", async () =>
        {
            await _editor.StartVttExportAsync();
            SyncExport();
        }, "compact");
        ToolTip.SetTip(start, "Один файл .dd2vtt: картинка выбранной области (PNG), сетка, стены и двери. "
            + "Открывается в Foundry VTT, Arkenforge и других столах, понимающих Universal VTT.");
        return Ui.Section("Для виртуальных столов", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                AppearanceSlider("ExportVttGridSizeSlider", "Клетка стола", 4, 1024, "ед.",
                    () => _editor.ExportVttGridSize,
                    (v, _) => { _editor.ExportVttGridSize = v; SyncExport(); }, 1, defaultValue: null,
                    description: "Сколько единиц карты занимает одна клетка на столе. Берётся из видимой сетки карты, иначе 100."),
                _vttSummaryText,
                start,
            },
        }).Named("ExportVttBlock");
    }

    private Control BuildExportTab()
    {
        Button Choice<T>(
            string name,
            string label,
            T value,
            IDictionary<T, Button> registry,
            Action<T> select) where T : notnull
        {
            var button = Ui.TextButton(name, label, () =>
            {
                select(value);
                SyncExport();
            }, "segment", "mode-choice");
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.Margin = new Thickness(2);
            registry.Add(value, button);
            AutomationProperties.SetName(button, label);
            return button;
        }

        var formats = new UniformGrid
        {
            Columns = 3,
            Children =
            {
                Choice("ExportPngButton", "PNG", ExportImageFormat.Png,
                    _exportFormatButtons, _editor.SetExportFormat),
                Choice("ExportJpegButton", "JPEG", ExportImageFormat.Jpeg,
                    _exportFormatButtons, _editor.SetExportFormat),
                Choice("ExportWebpButton", "WebP", ExportImageFormat.Webp,
                    _exportFormatButtons, _editor.SetExportFormat),
            },
        };

        var areas = new UniformGrid
        {
            Columns = 2,
            Children =
            {
                Choice("ExportWholeCanvasButton", "Вся карта", ExportAreaMode.WholeCanvas,
                    _exportAreaButtons, _editor.SetExportAreaMode),
                Choice("ExportRegionButton", "Прямоугольный регион", ExportAreaMode.Region,
                    _exportAreaButtons, _editor.SetExportAreaMode),
            },
        };

        var regionFields = new UniformGrid
        {
            Columns = 2,
            Children =
            {
                Field("X, px", _exportRegionXBox),
                Field("Y, px", _exportRegionYBox),
                Field("Ширина, px", _exportRegionWidthBox),
                Field("Высота, px", _exportRegionHeightBox),
            },
        };
        _exportRegionPanel = new StackPanel
        {
            Name = "ExportRegionFields",
            Spacing = 4,
            Children = { regionFields },
        };

        var outputSize = new UniformGrid
        {
            Columns = 2,
            Children =
            {
                Field("Итоговая ширина", _exportOutputWidthBox),
                Field("Итоговая высота", _exportOutputHeightBox),
            },
        };
        var detailScale = new UniformGrid
        {
            Columns = 3,
            Children =
            {
                Choice("ExportNativeSizeButton", "Полный размер", 1,
                    _exportScaleButtons, _editor.SetExportScaleFactor),
                Choice("ExportDoubleSizeButton", "Детализация 2×", 2,
                    _exportScaleButtons, _editor.SetExportScaleFactor),
                Choice("ExportQuadrupleSizeButton", "Детализация 4×", 4,
                    _exportScaleButtons, _editor.SetExportScaleFactor),
            },
        };
        _exportAspectToggle = Toggle(
            "ExportAspectToggle", "Связать пропорции", _editor.SetExportAspectLocked);

        var modes = new UniformGrid
        {
            Columns = 2,
            Children =
            {
                Choice("ExportSingleButton", "Один файл", ExportOutputMode.SingleImage,
                    _exportModeButtons, _editor.SetExportOutputMode),
                Choice("ExportTilesButton", "Пакет тайлов", ExportOutputMode.TilePackage,
                    _exportModeButtons, _editor.SetExportOutputMode),
                Choice("ExportMbTilesButton", "MBTiles", ExportOutputMode.MbTiles,
                    _exportModeButtons, _editor.SetExportOutputMode),
            },
        };

        _exportTransparencyToggle = Toggle(
            "ExportTransparencyToggle", "Прозрачный фон", _editor.SetExportTransparentBackground);
        _exportGridToggle = Toggle(
            "ExportGridToggle", "Экспортировать разрешённые сетки", _editor.SetExportIncludeGrid);

        _exportQualityText = new TextBlock { Name = "ExportQualityText" }.Classed("role-caption");
        _exportQualitySlider = new Slider
        {
            Name = "ExportQualitySlider",
            Minimum = 1,
            Maximum = 100,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
        };
        AutomationProperties.SetName(_exportQualitySlider, "Качество JPEG/WebP от 1 до 100");
        _exportQualitySlider.PropertyChanged += (_, e) =>
        {
            if (!_syncingExport && e.Property == RangeBase.ValueProperty)
            {
                _editor.SetExportQuality((int)Math.Round(_exportQualitySlider.Value));
                SyncExport();
            }
        };
        _exportQualityPanel = new StackPanel
        {
            Name = "ExportQualityPanel",
            Spacing = 4,
            Children = { _exportQualityText, _exportQualitySlider },
        };

        Wire(_exportRegionXBox, value => _editor.ExportRegionXText = value);
        Wire(_exportRegionYBox, value => _editor.ExportRegionYText = value);
        Wire(_exportRegionWidthBox, value => _editor.ExportRegionWidthText = value);
        Wire(_exportRegionHeightBox, value => _editor.ExportRegionHeightText = value);
        Wire(_exportOutputWidthBox, value => _editor.ExportOutputWidthText = value);
        Wire(_exportOutputHeightBox, value => _editor.ExportOutputHeightText = value);

        _startExportButton = Ui.AsyncButton(
            "StartExportButton", "Начать экспорт", async () =>
            {
                await _editor.StartExportAsync();
                SyncExport();
            }, "primary");
        _cancelExportButton = Ui.TextButton(
            "CancelExportButton", "Отменить экспорт", _editor.CancelExport, "destructive");
        _openExportFolderButton = Ui.TextButton(
            "OpenExportFolderButton", "Открыть папку", _editor.OpenExportFolder, "accent");
        _viewExportedPngButton = Ui.TextButton("ViewExportedPngButton", "Просмотреть в Map",
            () => PngMapWindow.ShowFor(_exportWindow, _editor.ExportResultPath), "accent");
        var close = Ui.TextButton("CloseExportButton", "Закрыть", () =>
        {
            _editor.CloseExport();
            _exportWindow?.Hide();
            SyncExport();
        }, "ghost");

        _exportFormatExplanation.TextWrapping = TextWrapping.Wrap;
        _exportEstimate.TextWrapping = TextWrapping.Wrap;
        _exportDetailSummary.TextWrapping = TextWrapping.Wrap;
        _exportDetailSummary.Classed("role-caption");
        _exportError.TextWrapping = TextWrapping.Wrap;
        _exportError.Classed("danger", "role-caption");
        _exportProgressText.TextWrapping = TextWrapping.Wrap;
        _exportResult.TextWrapping = TextWrapping.Wrap;

        var previewHost = new Border
        {
            Name = "ExportPreviewHost",
            MinHeight = 184,
            Child = _exportPreview,
        }.Classed("surface-raised");
        AutomationProperties.SetName(previewHost, "Предпросмотр экспорта");

        var body = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Экспорт изображения" }.Classed("role-panel"),
                BuildExportRecipient(),
                BuildPosterAndLayersExport(),
                new TextBlock
                {
                    Text = "Предпросмотр уменьшен для этого окна. Размер готового файла задаётся ниже; заметки и панели редактора в него не попадают.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
                formats,
                _exportFormatExplanation,
                areas,
                _exportRegionPanel,
                detailScale,
                BuildExportUnits(),
                outputSize,
                _exportDetailSummary,
                _exportAspectToggle,
                modes,
                _exportTransparencyToggle,
                _exportGridToggle,
                _exportQualityPanel,
                BuildVttExportBlock(),
                previewHost,
                _exportEstimate,
                new Expander { Header = "Оценка памяти и ограничения", Content = _exportMemoryDetails },
            },
        };

        SyncExport();
        var footer = new Border
        {
            Padding = new Thickness(12),
            Child = new StackPanel { Spacing = 8, Children =
            {
                _exportError, _exportProgress, _exportProgressText, _exportResult,
                new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children =
                    { _startExportButton, _cancelExportButton, _viewExportedPngButton, _openExportFolderButton, close } },
            } },
        }.Classed("dialog-footer");
        return new DockPanel { Children = { footer.Docked(Dock.Bottom), new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = body,
        } } };

        ToggleButton Toggle(string name, string label, Action<bool> write)
        {
            var toggle = Ui.PropertyToggle(name, label);
            toggle.IsCheckedChanged += (_, _) =>
            {
                if (!_syncingExport)
                {
                    write(toggle.IsChecked == true);
                    SyncExport();
                }
            };
            return toggle;
        }

        static Control Field(string label, Control control) => new StackPanel
        {
            Margin = new Thickness(3),
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = label }.Classed("faint", "role-caption"),
                control,
            },
        };

        // TextChanged is queued by Avalonia. A preview/state refresh could overwrite a newly
        // typed dimension before that event ran; commit the Text property change immediately.
        void Wire(TextBox box, Action<string> write) => box.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty && !_syncingExport)
            {
                write(box.Text ?? "");
                SyncExport();
            }
        };
    }
}
