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
    // ========================================================= selection panels

    private void BuildSelectionSections()
    {
        // Empty is a starting point: what to do now, and the three first steps of a map one
        // click away (the same commands as the rail).
        Button Start(string name, string text, string icon, EditorTool tool, Action? then = null)
        {
            var button = Ui.CommandButton(name, text, icon, () =>
            {
                _editor.ActiveTool = tool;
                then?.Invoke();
                SyncContextArea();
            }, "compact");
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            return button;
        }
        _selectionEmpty = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Ui.EmptyState(
                    "SelectionEmptyState",
                    MapIcons.Select,
                    "Ничего не выбрано",
                    "Щёлкните объект на карте или строку в панели «Карта» справа. Рамкой можно выбрать несколько."),
                new TextBlock { Text = "Начать" }.Classed("faint", "role-caption", "subgroup-title"),
            },
        };
        if (_isHexWorkshop)
        {
            ((StackPanel)_selectionEmpty).Children.Add(Start("StartHexButton", "Поставить гексы", MapIcons.Hex, EditorTool.Hex));
            ((StackPanel)_selectionEmpty).Children.Add(Start("StartHexTextButton", "Подписать место", MapIcons.Text, EditorTool.Text));
        }
        else
        {
            ((StackPanel)_selectionEmpty).Children.Add(Start("StartAssetsButton", "Поставить ассет", MapIcons.Assets, EditorTool.Stamp, () => OpenAssetCatalog(focusSearch: true)));
            ((StackPanel)_selectionEmpty).Children.Add(Start("StartLandButton", "Нарисовать сушу", MapIcons.Land, EditorTool.Land));
            ((StackPanel)_selectionEmpty).Children.Add(Start("StartRiverFromEmptyButton", "Новая река", MapIcons.River, EditorTool.River));
            ((StackPanel)_selectionEmpty).Children.Add(NewRoomButton("StartRoomFromEmptyButton"));
        }

        _inspectorPanel = BuildInspector();

        _stampSection = BuildStampSection();
        _lineSection = BuildLineSection();
        _shapeSection = BuildShapeSection();
        _primitiveSection = BuildPrimitiveSection();
        _textSection = BuildTextSection();
        _decorationSection = BuildDecorationSection();

        _compositionSection = Ui.Section("Композиция", new StackPanel
        {
            Spacing = 5,
            Children =
            {
                Ui.TextButton("CopyCompositionButton", "Копировать композицию",
                    _editor.CopySelectedComposition, "compact"),
                Ui.AsyncButton("ExportCompositionPackageButton", "Сохранить композицию в файл…",
                    ExportCompositionPackageAsync, "compact"),
                Ui.AsyncButton("FlattenCompositionButton", "Растеризовать композицию…",
                    FlattenSelectedCompositionAsync, "compact"),
            },
        }).Named("CompositionObjectActions");
    }

    /// <summary>The selected stamp adds only selection-specific commands around the shared editor.</summary>
    private Control BuildStampSection()
    {
        var replace = Ui.TextButton("StampReplaceAssetButton", "Заменить ассет…", () =>
        {
            OpenAssetCatalog(focusSearch: true);
        }, "compact");
        ToolTip.SetTip(replace,
            "Открывает библиотеку. Выберите набор и нажмите «Заменить ассет выделенного объекта» — " +
            "положение, размер, поворот и эффекты сохранятся.");

        var aspect = new ToggleButton { Content = "Сохранять пропорции" }
            .Named("StampAspectLockToggle").Classed("segment");
        AutomationProperties.SetName(aspect, "Сохранять пропорции");
        ToolTip.SetTip(aspect, "Точные Ширина/Высота меняются согласованно, пока включено.");
        aspect.Click += (_, _) => _editor.StampAspectLocked = aspect.IsChecked == true;
        _appearanceSyncs.Add(() =>
        {
            aspect.IsChecked = _editor.StampAspectLocked;
            Ui.SetClass(aspect, "active", _editor.StampAspectLocked);
        });

        return Ui.Section("Ассет", new StackPanel
        {
            Spacing = 5,
            Children = { replace, aspect, BuildStampEditor("SelectedStamp", includeTransform: false) },
        }).Named("StampObjectActions");
    }

    /// <summary>
    /// One binding surface for both a future stamp and a selected stamp. The read side follows
    /// <see cref="EditorViewModel.StampPanelStyle"/>; every write uses the same view-model API.
    /// </summary>
    private Control BuildStampEditor(string prefix, bool includeTransform = true)
    {
        StampStyle? Style() => _editor.StampPanelStyle;

        var flips = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                AppearanceToggle(prefix + "FlipHorizontalToggle", "Отразить по горизонтали",
                    () => Style()?.FlipHorizontal, value => _editor.SetStampFlip(horizontal: value)),
                AppearanceToggle(prefix + "FlipVerticalToggle", "Отразить по вертикали",
                    () => Style()?.FlipVertical, value => _editor.SetStampFlip(vertical: value)),
            },
        };

        // This one used to be its own hand-wired TextBox whose sync had no focus guard, so any
        // scene change overwrote what the user was typing. It now uses the shared colour row.
        var shadowColor = AppearanceColorBox(
            prefix + "ShadowColorBox", "Цвет тени",
            () => Style()?.ShadowColor ?? AppearanceColor.OpaqueBlack,
            (value, commit) => _editor.SetStampShadowColor(value, commit),
            cancelPreview: _editor.CancelAppearanceGesture);

        var shadowState = AppearanceChoice(
            prefix + "ShadowPlacementChoice", "Тень",
            [
                ("None", "Нет"),
                ("Standard", "Стандартная"),
                ("Custom", "Своя"),
                ("Layer", "За слоем"),
            ],
            () => Style() is not { ShadowOpacity: > 0 } ? "None"
                : Style()!.ShadowPlacement == StampShadowPlacement.Layer ? "Layer"
                : IsStandardStampShadow(Style()!) ? "Standard" : "Custom",
            value =>
            {
                if (value == "None") _editor.SetStampShadowNone();
                else if (value == "Standard") _editor.SetStampShadowStandard();
                else _editor.SetStampShadowPlacement(value == "Layer"
                    ? StampShadowPlacement.Layer : StampShadowPlacement.Object);
            });

        var clipMask = BuildStampClipMaskGroup(prefix);

        var appearance = new Expander
        {
            Header = new TextBlock { Text = "Внешний вид" }.Classed("role-panel"),
            IsExpanded = false,
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    BlendChoice(prefix + "BlendChoice", () => Style()?.Blend, _editor.SetStampBlend),
                    AppearanceSlider(prefix + "BlurSlider", "Размытие", 0, 32, "px",
                        () => Style()?.Blur, (v, c) => _editor.SetStampBlur(v, c)),
                    AppearanceSlider(prefix + "HueSlider", "Оттенок", -180, 180, "°",
                        () => Style()?.Hue, (v, c) => _editor.SetStampHueDegrees(v, c), 5,
                        ResetGlyph(prefix + "HueResetButton", "Сбросить оттенок",
                            () => _editor.SetStampHueDegrees(0))),
                    AppearanceSlider(prefix + "SaturationSlider", "Насыщенность", 0, 200, "%",
                        () => Style() is { } s ? ColorAdjustment.OffsetToPercent(s.Saturation) : null,
                        (v, c) => _editor.SetStampSaturationPercent(v, c), 5,
                        ResetGlyph(prefix + "SaturationResetButton", "Сбросить насыщенность",
                            () => _editor.SetStampSaturationPercent(100))),
                    AppearanceSlider(prefix + "BrightnessSlider", "Яркость", 0, 200, "%",
                        () => Style() is { } s ? ColorAdjustment.OffsetToPercent(s.Brightness) : null,
                        (v, c) => _editor.SetStampBrightnessPercent(v, c), 5,
                        ResetGlyph(prefix + "BrightnessResetButton", "Сбросить яркость",
                            () => _editor.SetStampBrightnessPercent(100))),
                    AppearanceSlider(prefix + "ContrastSlider", "Контраст", 0, 200, "%",
                        () => Style() is { } s ? ColorAdjustment.OffsetToPercent(s.Contrast) : null,
                        (v, c) => _editor.SetStampContrastPercent(v, c), 5,
                        ResetGlyph(prefix + "ContrastResetButton", "Сбросить контраст",
                            () => _editor.SetStampContrastPercent(100))),
                    Ui.TextButton(prefix + "ResetColorsButton", "Сбросить цветокоррекцию",
                        _editor.ResetStampColorAdjustments, "compact"),
                    shadowState,
                    shadowColor,
                    ShadowBlock(prefix,
                        () => Style() is { } s
                            ? (s.ShadowOffsetX, s.ShadowOffsetY, s.ShadowBlur, s.ShadowOpacity)
                            : null,
                        _editor.SetStampShadowNone, _editor.SetStampShadowStandard,
                        (x, y, b, o, c) => _editor.SetStampShadow(x, y, b, o, c)),
                    Ui.TextButton(prefix + "ResetAppearanceButton", "Сбросить весь внешний вид",
                        _editor.ResetStampAppearance, "compact"),
                },
            },
        }.Named(prefix + "Appearance");
        AutomationProperties.SetName(appearance, "Внешний вид");

        var panel = new StackPanel { Spacing = 8 };
        var transform = new StackPanel { Spacing = 2 };

        // A placed object gets scale, rotation, level and opacity ONCE, at the top of the object
        // panel, where they also work on a multi-selection. Only the asset about to be placed
        // carries its own copies, because there is no selection to read them from yet.
        if (includeTransform)
        {
            transform.Children.Add(AppearanceSlider(prefix + "ScaleSlider", "Масштаб", 10, 400, "%",
                () => _editor.StampScalePercent,
                (value, commit) => _editor.SetStampScalePercent(value, commit), 5,
                ResetGlyph(prefix + "ScaleResetButton", "Сбросить масштаб к 100 %",
                    () => _editor.SetStampScalePercent(100))));
            transform.Children.Add(AppearanceSlider(prefix + "RotationSlider", "Поворот", 0, 359, "°",
                () => _editor.StampRotationDegrees,
                (value, commit) => _editor.SetStampRotationDegrees(value, commit), 1,
                ResetGlyph(prefix + "RotationResetButton", "Сбросить поворот",
                    () => _editor.SetStampRotationDegrees(0))));
            transform.Children.Add(AppearanceSlider(prefix + "OpacitySlider", "Непрозрачность", 0, 100, "%",
                () => _editor.StampOpacityPercent,
                (value, commit) => _editor.SetStampOpacityPercent(value, commit), 5));
            panel.Children.Add(Ui.Section("Размер и вид", transform));
        }
        var geometry = new StackPanel { Spacing = 6, Children = { flips } };
        if (includeTransform)
            geometry.Children.Insert(0, AppearanceSlider(prefix + "SublayerSlider", "Уровень",
                EditorViewModel.MinimumSublayer, EditorViewModel.MaximumSublayer, "",
                () => _editor.StampSublayer,
                (value, commit) => _editor.SetStampSublayer((int)Math.Round(value), commit)));
        geometry.Children.Add(clipMask);
        panel.Children.Add(Disclosure(prefix + "Geometry", "Геометрия и маска", geometry, false));
        panel.Children.Add(appearance);
        return panel;
    }

    private Control BuildStampClipMaskGroup(string prefix)
    {
        var thumbnail = new Image
        {
            Name = prefix + "ClipMaskThumbnail",
            Width = 72,
            Height = 72,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(thumbnail, "Миниатюра маски обрезки");

        var state = new TextBlock
        {
            Name = prefix + "ClipMaskState",
            TextWrapping = TextWrapping.Wrap,
        }.Classed("role-caption").Classed("faint");
        var foregroundState = new TextBlock
        {
            Name = prefix + "StampForegroundState",
            TextWrapping = TextWrapping.Wrap,
        }.Classed("role-caption").Classed("faint");

        var toggle = new ToggleButton { Content = "Маска обрезки" }
            .Named(prefix + "ClipMaskToggle").Classed("segment");
        AutomationProperties.SetName(toggle, "Включить маску обрезки");
        toggle.Click += (_, _) => _editor.SetStampClipMaskEnabled(toggle.IsChecked == true);

        var assign = Ui.TextButton(
            prefix + "AssignClipMaskButton",
            "Назначить маску…",
            () => _ = AssignClipMaskAsync(),
            "compact");
        var remove = Ui.TextButton(
            prefix + "RemoveClipMaskButton",
            "Удалить маску",
            () =>
            {
                _editor.RemoveCurrentVariantMask();
                SyncAssetList();
                SyncInspector();
                SyncStatus();
            },
            "compact");
        var createForeground = Ui.TextButton(
            prefix + "CreateStampForegroundButton",
            "Создать передний план",
            _editor.CreateStampForegroundSurface,
            "compact");
        var editForeground = Ui.TextButton(
            prefix + "EditStampForegroundButton",
            "Рисовать передний план",
            _editor.EditStampForegroundSurface,
            "compact");

        _appearanceSyncs.Add(() =>
        {
            thumbnail.Source = _thumbnails.Image(_editor.StampClipMaskThumbnailPath);
            thumbnail.IsVisible = thumbnail.Source is not null;
            state.Text = _editor.StampClipMaskStatus;
            foregroundState.Text = _editor.StampForegroundStatus;
            toggle.IsChecked = _editor.StampPanelStyle?.ClipMaskEnabled == true;
            toggle.IsEnabled = _editor.StampClipMaskCanToggle;
            Ui.SetClass(toggle, "active", toggle.IsChecked == true);
            assign.IsEnabled = _editor.StampPanelCanEditLibraryMask;
            assign.Content = _editor.StampPanelVariantHasLibraryMask
                ? "Заменить маску…"
                : "Назначить маску…";
            remove.IsEnabled = _editor.StampPanelVariantHasLibraryMask;
            createForeground.IsEnabled = _editor.Session.Document.StampForeground is null;
            editForeground.IsEnabled = _editor.Session.Document.StampForeground is not null;
        });

        return new Border
        {
            Child = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock { Text = "Маска обрезки" }.Classed("role-panel"),
                    thumbnail,
                    toggle,
                    state,
                    new Ui.ChoiceGrid { MaxColumns = 1, Children = { assign, remove } },
                    foregroundState,
                    new Ui.ChoiceGrid { MaxColumns = 1, Children = { createForeground, editForeground } },
                },
            },
        }.Classed("group").Named(prefix + "ClipMaskGroup");
    }

    private static bool IsStandardStampShadow(StampStyle style) =>
        style.ShadowPlacement == StampShadowPlacement.Object
        && Math.Abs(style.ShadowOffsetX - ShadowPreset.StandardOffsetX) < 0.001
        && Math.Abs(style.ShadowOffsetY - ShadowPreset.StandardOffsetY) < 0.001
        && Math.Abs(style.ShadowBlur - ShadowPreset.StandardBlur) < 0.001
        && Math.Abs(style.ShadowOpacity - ShadowPreset.StandardOpacity) < 0.001
        && style.ShadowColor == AppearanceColor.OpaqueBlack;

    /// <summary>Stroke controls shared verbatim by the line and the shape inspectors.</summary>
    private StackPanel BuildStrokeControls(string prefix)
    {
        VectorStrokeStyle? Stroke() =>
            _editor.Session.SelectedObject?.Line?.Stroke ?? _editor.Session.SelectedObject?.Shape?.Stroke;

        var assetOnly = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                AppearanceSlider(prefix + "StrokeHueSlider", "Оттенок ассета", -180, 180, "°",
                    () => Stroke()?.Hue, (v, c) => _editor.SetStrokeHueDegrees(v, c), 5),
                AppearanceSlider(prefix + "StrokeSaturationSlider", "Насыщенность ассета", 0, 200, "%",
                    () => Stroke() is { } s ? ColorAdjustment.OffsetToPercent(s.Saturation) : null,
                    (v, c) => _editor.SetStrokeSaturationPercent(v, c), 5),
            },
        };
        _appearanceSyncs.Add(() => assetOnly.IsVisible = _editor.SelectedStrokeHasAsset);

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                AppearanceColorBox(prefix + "StrokeColorBox", "Цвет обводки",
                    () => Stroke()?.Color,
                    (value, commit) => _editor.SetStrokeColor(value, commit),
                    cancelPreview: _editor.CancelAppearanceGesture),
                AppearanceSlider(prefix + "StrokeWidthSlider", "Толщина", 1, 64, "px",
                    () => Stroke()?.Width, (v, c) => _editor.SetStrokeWidth(v, c)),
                AppearanceSlider(prefix + "StrokeOpacitySlider", "Плотность обводки", 0, 100, "%",
                    () => Stroke()?.Opacity * 100, (v, c) => _editor.SetStrokeOpacityPercent(v, c), 5),
                AppearanceChoice(prefix + "StrokePatternChoice", "Линия",
                    [("Solid", EditorLabels.Pattern(StrokePattern.Solid)),
                     ("Dotted", EditorLabels.Pattern(StrokePattern.Dotted)),
                     ("Dashed", EditorLabels.Pattern(StrokePattern.Dashed))],
                    () => Stroke()?.Pattern.ToString(),
                    key => _editor.SetStrokePattern(Enum.Parse<StrokePattern>(key))),
                assetOnly,
            },
        };
    }

    /// <summary>Rare stroke effects, one collapsed block for both vector kinds.</summary>
    private Expander BuildStrokeAppearanceExpander(string prefix)
    {
        VectorStrokeStyle? Stroke() =>
            _editor.Session.SelectedObject?.Line?.Stroke ?? _editor.Session.SelectedObject?.Shape?.Stroke;

        var expander = new Expander
        {
            Header = new TextBlock { Text = "Внешний вид" }.Classed("role-panel"),
            IsExpanded = false,
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    BlendChoice(prefix + "StrokeBlendChoice", () => Stroke()?.Blend, _editor.SetStrokeBlend),
                    AppearanceSlider(prefix + "StrokeBlurSlider", "Размытие", 0, 32, "px",
                        () => Stroke()?.Blur, (v, c) => _editor.SetStrokeBlur(v, c)),
                    AppearanceSlider(prefix + "StrokeOffsetSlider", "Смещение", -64, 64, "px",
                        () => Stroke()?.Offset, (v, c) => _editor.SetStrokeOffset(v, c)),
                    AppearanceChoice(prefix + "StrokeEndCapChoice", "Концы",
                        [("Butt", EditorLabels.Cap(VectorCap.Butt)),
                         ("Round", EditorLabels.Cap(VectorCap.Round)),
                         ("Square", EditorLabels.Cap(VectorCap.Square))],
                        () => Stroke()?.EndCap.ToString(),
                        key => _editor.SetStrokeEndCap(Enum.Parse<VectorCap>(key))),
                    AppearanceChoice(prefix + "StrokeNodeCapChoice", "Узлы",
                        [("Butt", EditorLabels.Cap(VectorCap.Butt)),
                         ("Round", EditorLabels.Cap(VectorCap.Round)),
                         ("Square", EditorLabels.Cap(VectorCap.Square))],
                        () => Stroke()?.NodeCap.ToString(),
                        key => _editor.SetStrokeNodeCap(Enum.Parse<VectorCap>(key))),
                    ShadowBlock(prefix + "Stroke",
                        () => Stroke() is { } s
                            ? (s.ShadowOffsetX, s.ShadowOffsetY, s.ShadowBlur, s.ShadowOpacity)
                            : null,
                        _editor.SetStrokeShadowNone, _editor.SetStrokeShadowStandard,
                        (x, y, b, o, c) => _editor.SetStrokeShadow(x, y, b, o, c)),
                    Ui.TextButton(prefix + "StrokeResetButton", "Сбросить оформление обводки",
                        _editor.ResetStrokeAppearance, "compact"),
                },
            },
        }.Named(prefix + "StrokeAppearance");
        AutomationProperties.SetName(expander, "Внешний вид");
        return expander;
    }

    private Control BuildVectorGeometryEditor(bool shape)
    {
        var nodeChoice = new ComboBox
        {
            Name = shape ? "ShapeNodeChoice" : "LineNodeChoice",
            PlaceholderText = "Выберите узел",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var portalChoice = new ComboBox
        {
            Name = shape ? "ShapePortalChoice" : "LinePortalChoice",
            PlaceholderText = "Выберите проход",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        nodeChoice.SelectionChanged += (_, _) =>
        {
            if (!_syncingAppearance && nodeChoice.SelectedIndex >= 0
                && nodeChoice.SelectedIndex < _editor.SelectedVectorNodes.Count)
                _editor.SelectVectorNode(_editor.SelectedVectorNodes[nodeChoice.SelectedIndex].Id);
        };
        portalChoice.SelectionChanged += (_, _) =>
        {
            if (!_syncingAppearance && portalChoice.SelectedIndex >= 0
                && portalChoice.SelectedIndex < _editor.SelectedVectorPortals.Count)
                _editor.SelectVectorPortal(_editor.SelectedVectorPortals[portalChoice.SelectedIndex].Id);
        };
        _appearanceSyncs.Add(() =>
        {
            var nodes = _editor.SelectedVectorNodes;
            nodeChoice.ItemsSource = nodes.Select((node, index) =>
                $"{index + 1} · {node.Kind} · {node.X * 100:0}%, {node.Y * 100:0}%").ToList();
            nodeChoice.SelectedIndex = _editor.SelectedVectorNode is { } selected
                ? nodes.ToList().FindIndex(node => node.Id == selected.Id) : -1;
            var portals = _editor.SelectedVectorPortals;
            portalChoice.ItemsSource = portals.Select((portal, index) =>
                $"{index + 1} · {portal.Position:P0} · {portal.Width:0} px").ToList();
            portalChoice.SelectedIndex = _editor.SelectedVectorPortal is { } selectedPortal
                ? portals.ToList().FindIndex(portal => portal.Id == selectedPortal.Id) : -1;
        });

        void EditNode(Func<VectorNode, VectorNode> change)
        {
            if (_editor.SelectedVectorNode is not { } current) return;
            var node = change(current);
            _editor.SetSelectedVectorNode(node.X, node.Y, node.Kind,
                node.ControlInX, node.ControlInY, node.ControlOutX, node.ControlOutY);
        }
        var addX = NumberBox(shape ? "ShapeAddNodeXBox" : "LineAddNodeXBox"); addX.Text = "50";
        var addY = NumberBox(shape ? "ShapeAddNodeYBox" : "LineAddNodeYBox"); addY.Text = "50";
        var addNode = Ui.TextButton(shape ? "ShapeAddNodeButton" : "AddLineNodeButton", "Добавить узел по X/Y", () =>
        {
            if (!TryWholeNumber(addX.Text, out var x) || !TryWholeNumber(addY.Text, out var y)) return;
            if (shape) _editor.AddNodeToSelectedShape(x / 100, y / 100); else _editor.AddNodeToSelectedLine(x / 100, y / 100);
            SyncAppearanceSections();
        }, "compact");
        var position = NumberBox(shape ? "ShapePortalPositionBox" : "LinePortalPositionBox"); position.Text = "50";
        var width = NumberBox(shape ? "ShapePortalWidthBox" : "LinePortalWidthBox"); width.Text = "48";
        var addPortal = Ui.TextButton(shape ? "ShapeAddPortalButton" : "AddPortalButton", "Добавить проход по position/width", () =>
        {
            if (TryWholeNumber(position.Text, out var p) && TryWholeNumber(width.Text, out var w))
                _editor.AddPortalToSelected(p / 100, w);
            SyncAppearanceSections();
        }, "compact");

        var controls = new StackPanel
        {
            Spacing = 5,
            Children =
            {
                new TextBlock { Text = "Узлы", TextWrapping = TextWrapping.Wrap }.Classed("role-panel"),
                nodeChoice,
                Ui.PropertyRow("Новый X", addX, "%"), Ui.PropertyRow("Новый Y", addY, "%"), addNode,
                AppearanceNumberBox((shape ? "Shape" : "Line") + "NodeXBox", "X узла", "%",
                    () => _editor.SelectedVectorNode?.X * 100, value => EditNode(node => node with { X = value / 100 })),
                AppearanceNumberBox((shape ? "Shape" : "Line") + "NodeYBox", "Y узла", "%",
                    () => _editor.SelectedVectorNode?.Y * 100, value => EditNode(node => node with { Y = value / 100 })),
                AppearanceChoice((shape ? "Shape" : "Line") + "NodeKindChoice", "Тип узла",
                    [("Corner", "Прямой"), ("Curve", "Кривой")],
                    () => _editor.SelectedVectorNode?.Kind.ToString(),
                    key => EditNode(node => node with { Kind = Enum.Parse<VectorNodeKind>(key) })),
                AppearanceNumberBox((shape ? "Shape" : "Line") + "ControlInXBox", "Входная ручка X", "%",
                    () => _editor.SelectedVectorNode?.ControlInX * 100, value => EditNode(node => node with { ControlInX = value / 100 })),
                AppearanceNumberBox((shape ? "Shape" : "Line") + "ControlInYBox", "Входная ручка Y", "%",
                    () => _editor.SelectedVectorNode?.ControlInY * 100, value => EditNode(node => node with { ControlInY = value / 100 })),
                AppearanceNumberBox((shape ? "Shape" : "Line") + "ControlOutXBox", "Выходная ручка X", "%",
                    () => _editor.SelectedVectorNode?.ControlOutX * 100, value => EditNode(node => node with { ControlOutX = value / 100 })),
                AppearanceNumberBox((shape ? "Shape" : "Line") + "ControlOutYBox", "Выходная ручка Y", "%",
                    () => _editor.SelectedVectorNode?.ControlOutY * 100, value => EditNode(node => node with { ControlOutY = value / 100 })),
                new Ui.ChoiceGrid
                {
                    MaxColumns = 1,
                    Children =
                    {
                        Ui.TextButton((shape ? "Shape" : "Line") + "DeleteNodeButton", "Удалить выбранный узел",
                            _editor.DeleteSelectedVectorNode, "compact"),
                        Ui.TextButton((shape ? "Shape" : "Line") + "SnapNodeButton", "Привязать выбранный узел",
                            _editor.SnapSelectedNodeToGrid, "compact"),
                        Ui.TextButton((shape ? "Shape" : "Line") + "CutNodeButton", "Разрезать в выбранном узле",
                            _editor.CutSelectedLine, "compact"),
                    },
                },
                new TextBlock { Text = "Проходы", TextWrapping = TextWrapping.Wrap }.Classed("role-panel"),
                BlocksLightToggle(shape),
                portalChoice,
                Ui.PropertyRow("Положение, %", position), Ui.PropertyRow("Ширина", width), addPortal,
                AppearanceNumberBox((shape ? "Shape" : "Line") + "SelectedPortalPosition", "Положение", "%",
                    () => _editor.SelectedVectorPortal?.Position * 100,
                    value => _editor.SetSelectedPortal(value / 100, _editor.SelectedVectorPortal?.Width ?? 0)),
                AppearanceNumberBox((shape ? "Shape" : "Line") + "SelectedPortalWidth", "Ширина", "px",
                    () => _editor.SelectedVectorPortal?.Width,
                    value => _editor.SetSelectedPortal(_editor.SelectedVectorPortal?.Position ?? 0, value)),
                new Ui.ChoiceGrid
                {
                    MaxColumns = 1,
                    Children =
                    {
                        Ui.TextButton((shape ? "Shape" : "Line") + "DeletePortalButton", "Удалить выбранный проход",
                            _editor.DeleteSelectedPortal, "compact"),
                        Ui.TextButton((shape ? "Shape" : "Line") + "SnapPortalButton", "Привязать выбранный проход",
                            _editor.SnapSelectedPortalToGrid, "compact"),
                    },
                },
            },
        };
        return controls;
    }

    /// <summary>
    /// «Стена блокирует свет»: the selected strokes become walls of the dd2vtt export, and their
    /// passages become doors. Nothing changes on the map itself.
    /// </summary>
    private Control BlocksLightToggle(bool shape)
    {
        var toggle = AppearanceToggle((shape ? "Shape" : "Line") + "BlocksLightToggle", "Стена блокирует свет",
            () => _editor.SelectedBlocksLight, on =>
            {
                _editor.SetSelectedBlocksLight(on);
                SyncAppearanceSections();
            });
        ToolTip.SetTip(toggle, "Для экспорта в виртуальные столы (*.dd2vtt): линия становится стеной, через неё не видно, "
            + "а её проходы — дверями. На самой карте ничего не меняется.");
        return toggle;
    }

    private Control BuildLineSection()
    {
        var geometry = new Expander
        {
            Header = new TextBlock { Text = "Геометрия" }.Classed("role-panel"),
            IsExpanded = false,
            Content = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    BuildVectorGeometryEditor(shape: false),
                },
            },
        }.Named("LineGeometry");
        AutomationProperties.SetName(geometry, "Геометрия");

        return Ui.Section("Линия", new StackPanel
        {
            Spacing = 5,
            Children =
            {
                BuildStrokeControls("Line"),
                BuildStrokeAppearanceExpander("Line"),
                geometry,
            },
        }).Named("VectorObjectActions");
    }

    /// <summary>«Новая комната»: a rectangular dungeon room in the middle of the view, selected.</summary>
    private Button NewRoomButton(string name)
    {
        var button = Ui.CommandButton(name, "Новая комната", MapIcons.LineShape, () =>
        {
            _editor.CreateRoom();
            SyncContextArea();
            SyncAppearanceSections();
            SyncStatus();
        }, "compact");
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        ToolTip.SetTip(button, "Комната для подземелья: пол, стена, которая блокирует свет, скруглённые углы. "
            + "Потом — двери, шапки на углах и материалы в панели «Комната».");
        return button;
    }

    /// <summary>
    /// «Комната» of the selected shape: three layers - floor (fill), wall (stroke) and caps -
    /// plus rounded corners, doors and «стена блокирует свет». Every number is a ValueRow.
    /// </summary>
    private Control BuildRoomBlock()
    {
        RoomStyle? Room() => _editor.SelectedRoom;
        void After()
        {
            SyncAppearanceSections();
            SyncStatus();
        }
        var make = Ui.TextButton("RoomMakeButton", "Сделать комнатой", () => { _editor.MakeSelectedRoom(); After(); }, "compact");
        ToolTip.SetTip(make, "Заливка станет полом, обводка — стеной, проходы — дверями. Добавятся скругление углов и шапки.");

        Button FromAsset(string name, string text, string hint, Func<AssetRecord, bool> use)
        {
            var button = Ui.TextButton(name, text, () =>
            {
                if (_editor.CurrentAssetRecord is { } record) use(record);
                else _editor.StatusDetailForView("Сначала выберите ассет в панели «Ассеты» или в каталоге.");
                After();
            }, "compact");
            ToolTip.SetTip(button, hint);
            return button;
        }

        var capShadow = AppearanceToggle("RoomCapShadowToggle", "Тень шапок",
            () => Room()?.CapShadow, on => { _editor.SetSelectedRoom(r => r with { CapShadow = on }); After(); });
        ToolTip.SetTip(capShadow, "Шапки отбрасывают ту же тень, что и стена.");
        var blocks = AppearanceToggle("RoomBlocksLightToggle", "Стена блокирует свет",
            () => _editor.SelectedBlocksLight, on => { _editor.SetSelectedBlocksLight(on); After(); });
        ToolTip.SetTip(blocks, "Для виртуальных столов (*.dd2vtt): стена комнаты — препятствие для взгляда и света, двери — проходы.");

        var details = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Ui.Hint("Три слоя: пол — заливка ниже, стена — обводка, шапки — картинка на каждом углу. Двери — проходы стены."),
                AppearanceColorBox("RoomFloorColorBox", "Пол · цвет",
                    () => _editor.Session.SelectedObject?.Shape?.Fill.Color,
                    (value, commit) => _editor.SetShapeFillColor(value, commit), cancelPreview: _editor.CancelAppearanceGesture),
                FromAsset("RoomFloorFromAssetButton", "Пол из выбранного ассета",
                    "Выбранный в панели «Ассеты» рисунок ляжет повторяющейся фактурой пола.",
                    record => { _editor.UseAssetAsSelectedShapeFill(record); return true; }),
                AppearanceColorBox("RoomWallColorBox", "Стена · цвет",
                    () => _editor.Session.SelectedObject?.Shape?.Stroke.Color,
                    (value, commit) => _editor.SetStrokeColor(value, commit), cancelPreview: _editor.CancelAppearanceGesture),
                AppearanceSlider("RoomWallWidthSlider", "Толщина стены", 1, 128, "px",
                    () => _editor.Session.SelectedObject?.Shape?.Stroke.Width,
                    (v, _) => { _editor.SetSelectedRoomWallWidth(v); After(); }, 1, defaultValue: EditorViewModel.RoomWallWidth,
                    description: "Ширина стены. Шапки на углах масштабируются вместе с ней."),
                FromAsset("RoomWallFromAssetButton", "Стена из выбранного ассета",
                    "Выбранный рисунок повторяется вдоль всей стены (кирпич, частокол, скалы).",
                    record => { _editor.UseAssetAsSelectedVectorStroke(record); return true; }),
                AppearanceSlider("RoomCornerRadiusSlider", "Скругление углов", 0, 256, "px",
                    () => Room()?.CornerRadius,
                    (v, _) => { _editor.SetSelectedRoom(r => r with { CornerRadius = v }); After(); }, 1, defaultValue: EditorViewModel.RoomWallWidth,
                    description: "Радиус скругления каждого угла. 0 — острые углы. Очень острые углы скругляются меньше, чтобы не потерять форму."),
                FromAsset("RoomCapsFromAssetButton", "Шапки из выбранного ассета",
                    "Выбранный рисунок (колонна, валун, башенка) встанет на каждый угол, повёрнутый вдоль стены.",
                    record => _editor.UseAssetAsRoomCaps(record)),
                Ui.TextButton("RoomRemoveCapsButton", "Убрать шапки",
                    () => { _editor.SetSelectedRoom(r => r with { CapAssetSha256 = null }); After(); }, "compact"),
                AppearanceSlider("RoomCapScaleSlider", "Размер шапок", 25, 800, "%",
                    () => Room()?.CapScale * 100,
                    (v, _) => { _editor.SetSelectedRoom(r => r with { CapScale = v / 100 }); After(); }, 5, defaultValue: 250,
                    description: "Ширина шапки в процентах от толщины стены."),
                AppearanceSlider("RoomCapAngleSlider", "Угол шапок", -180, 180, "°",
                    () => Room()?.CapAngleDegrees,
                    (v, _) => { _editor.SetSelectedRoom(r => r with { CapAngleDegrees = v }); After(); }, 5, defaultValue: 0,
                    description: "Дополнительный поворот всех шапок поверх направления стены в углу."),
                capShadow,
                blocks,
                Ui.TextButton("RoomAddDoorButton", "Добавить дверь",
                    () => { _editor.AddRoomDoor(); After(); }, "compact"),
                Ui.TextButton("RoomClearButton", "Сделать обычной фигурой",
                    () => { _editor.ClearSelectedRoom(); After(); }, "compact"),
            },
        };
        _appearanceSyncs.Add(() =>
        {
            make.IsVisible = _editor.CanMakeSelectedRoom;
            details.IsVisible = Room() is not null;
        });
        return Ui.Section("Комната", new StackPanel { Spacing = 4, Children = { make, details } }).Named("RoomBlock");
    }

    private Control BuildShapeSection()
    {
        ShapeFillStyle? Fill() => _editor.Session.SelectedObject?.Shape?.Fill;

        var geometry = new Expander
        {
            Header = new TextBlock { Text = "Геометрия" }.Classed("role-panel"),
            IsExpanded = false,
            Content = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    BuildVectorGeometryEditor(shape: true),
                    Ui.TextButton("BooleanAddButton", "Сложить две выделенные фигуры",
                        () => _editor.PreviewShapeBoolean(ShapeBooleanOperation.Add), "compact"),
                    Ui.TextButton("BooleanSubtractButton", "Вычесть вторую фигуру из первой",
                        () => _editor.PreviewShapeBoolean(ShapeBooleanOperation.Subtract), "compact"),
                    Ui.TextButton("BooleanMergeButton", "Объединить две выделенные фигуры",
                        () => _editor.PreviewShapeBoolean(ShapeBooleanOperation.Merge), "compact"),
                    Ui.TextButton("BooleanApplyButton", "Применить показанный результат",
                        () => _editor.CommitShapeBooleanPreview(), "accent", "compact"),
                    Ui.TextButton("BooleanCancelButton", "Отменить предпросмотр",
                        _editor.CancelShapeBooleanPreview, "compact"),
                },
            },
        }.Named("ShapeGeometry");
        AutomationProperties.SetName(geometry, "Геометрия");

        var appearance = new Expander
        {
            Header = new TextBlock { Text = "Внешний вид" }.Classed("role-panel"),
            IsExpanded = false,
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    BlendChoice("ShapeBlendChoice",
                        () => _editor.Session.SelectedObject?.Shape?.Stroke.Blend, _editor.SetStrokeBlend),
                    AppearanceSlider("ShapeBlurSlider", "Размытие", 0, 32, "px",
                        () => _editor.Session.SelectedObject?.Shape?.Stroke.Blur,
                        (v, c) => _editor.SetStrokeBlur(v, c)),
                    ShadowBlock("ShapeStroke",
                        () => _editor.Session.SelectedObject?.Shape?.Stroke is { } s
                            ? (s.ShadowOffsetX, s.ShadowOffsetY, s.ShadowBlur, s.ShadowOpacity)
                            : null,
                        _editor.SetStrokeShadowNone, _editor.SetStrokeShadowStandard,
                        (x, y, b, o, c) => _editor.SetStrokeShadow(x, y, b, o, c)),
                    Ui.TextButton("ShapeResetFillButton", "Сбросить заливку",
                        _editor.ResetShapeFill, "compact"),
                    Ui.TextButton("ShapeResetStrokeButton", "Сбросить оформление обводки",
                        _editor.ResetStrokeAppearance, "compact"),
                    Ui.TextButton("ShapeResetEffectsButton", "Сбросить общие эффекты",
                        _editor.ResetShapeEffects, "compact"),
                },
            },
        }.Named("ShapeAppearance");
        AutomationProperties.SetName(appearance, "Внешний вид");

        var regionLabel = Ui.TextButton("ShapeRegionLabelButton", "Подписать область", () => { _editor.AddRegionLabel(); SyncContextArea(); }, "compact");
        ToolTip.SetTip(regionLabel, "Имя встанет внутри фигуры по её длине и пойдёт за контуром; имя берётся из карточки, привязанной к фигуре.");
        return Ui.Section("Фигура", new StackPanel
        {
            Spacing = 5,
            Children =
            {
                regionLabel,
                BuildRoomBlock(),
                AppearanceColorBox("ShapeFillColorBox", "Цвет заливки",
                    () => Fill()?.Color,
                    (value, commit) => _editor.SetShapeFillColor(value, commit),
                    cancelPreview: _editor.CancelAppearanceGesture),
                AppearanceSlider("ShapeFillOpacitySlider", "Плотность заливки", 0, 100, "%",
                    () => Fill()?.Opacity * 100, (v, c) => _editor.SetShapeFillOpacityPercent(v, c), 5),
                BuildGradientBlock("Shape"),
                new TextBlock
                {
                    Text = "Фактура заливки выбирается в библиотеке; эти значения хранятся в фигуре.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
                AppearanceSlider("ShapeTextureScaleSlider", "Масштаб фактуры", 5, 6400, "%",
                    () => Fill()?.TextureScale * 100,
                    (v, c) => _editor.SetShapeFillTextureScale(v / 100, c), 5),
                AppearanceSlider("ShapeTextureRotationSlider", "Поворот фактуры", -180, 180, "°",
                    () => Fill()?.TextureRotation,
                    (v, c) => _editor.SetShapeFillTextureRotation(v, c), 5),
                AppearanceSlider("ShapeTextureOffsetXSlider", "Сдвиг фактуры X", -512, 512, "px",
                    () => Fill()?.TextureOffsetX,
                    (v, c) => _editor.SetShapeFillTextureOffsetX(v, c), 5),
                AppearanceSlider("ShapeTextureOffsetYSlider", "Сдвиг фактуры Y", -512, 512, "px",
                    () => Fill()?.TextureOffsetY,
                    (v, c) => _editor.SetShapeFillTextureOffsetY(v, c), 5),
                Ui.TextButton("ShapeResetTextureButton", "Сбросить фактуру заливки",
                    _editor.ResetShapeFillTexture, "compact"),
                AppearanceColorBox("ShapeStrokeColorBox", "Цвет обводки",
                    () => _editor.Session.SelectedObject?.Shape?.Stroke.Color,
                    (value, commit) => _editor.SetStrokeColor(value, commit),
                    cancelPreview: _editor.CancelAppearanceGesture),
                AppearanceSlider("ShapeStrokeWidthSlider", "Толщина обводки", 0, 64, "px",
                    () => _editor.Session.SelectedObject?.Shape?.Stroke.Width,
                    (v, c) => _editor.SetStrokeWidth(v, c)),
                appearance,
                geometry,
            },
        }).Named("ShapeObjectActions");
    }

    /// <summary>
    /// The gradient block, built once per inspector section that can host one.
    /// <para>
    /// A gradient is judged by eye, so the ramp comes first and everything else edits the stop the
    /// user picked on it. Turning the gradient off leaves the flat colour untouched, which makes
    /// the toggle safe to try.
    /// </para>
    /// </summary>
    private Control BuildGradientBlock(string prefix)
    {
        GradientFill? Gradient() => _editor.SelectedGradient;
        int StopIndex() => Gradient() is { } gradient
            ? Math.Clamp(_gradientStop, 0, gradient.Stops.Count - 1)
            : 0;

        var ramp = new GradientRamp().Named(prefix + "GradientRamp");
        ramp.StopSelected += (_, index) =>
        {
            _gradientStop = index;
            SyncAppearanceSections();
        };
        ramp.StopMoved += (_, moved) =>
        {
            _gradientStop = moved.Index;
            _editor.SetGradientStopOffset(moved.Index, moved.Offset, moved.Commit);
            SyncAppearanceSections();
        };

        var (modes, modeButtons) = Ui.Segmented(prefix + "GradientMode",
            [("Off", "Нет", null), ("Linear", "Линейный", null),
             ("Radial", "Радиальный", null)],
            key =>
            {
                _editor.SetGradientMode(key switch
                {
                    "Linear" => GradientKind.Linear,
                    "Radial" => GradientKind.Radial,
                    _ => null,
                });
                SyncAppearanceSections();
            });

        var stopColor = AppearanceColorBox(prefix + "GradientStopColorBox", "Цвет точки",
            () => Gradient() is { } gradient ? gradient.Stops[StopIndex()].Color : null,
            (value, commit) => _editor.SetGradientStopColor(StopIndex(), value, commit),
            cancelPreview: _editor.CancelAppearanceGesture);

        var stopPosition = AppearanceSlider(prefix + "GradientStopPositionSlider", "Позиция точки", 0, 100, "%",
            () => Gradient() is { } gradient ? gradient.Stops[StopIndex()].Offset * 100 : null,
            (value, commit) => _editor.SetGradientStopOffset(StopIndex(), value / 100, commit), 1);

        var angle = AppearanceSlider(prefix + "GradientAngleSlider", "Угол", 0, 360, "°",
            () => Gradient() is { Kind: GradientKind.Linear } gradient ? gradient.AngleDegrees : null,
            (value, commit) => _editor.SetGradientAngle(value, commit), 5);

        var centreX = AppearanceSlider(prefix + "GradientCenterXSlider", "Центр X", 0, 100, "%",
            () => Gradient() is { Kind: GradientKind.Radial } gradient ? gradient.CenterX * 100 : null,
            (value, commit) => _editor.SetGradientCenterX(value / 100, commit), 1);

        var centreY = AppearanceSlider(prefix + "GradientCenterYSlider", "Центр Y", 0, 100, "%",
            () => Gradient() is { Kind: GradientKind.Radial } gradient ? gradient.CenterY * 100 : null,
            (value, commit) => _editor.SetGradientCenterY(value / 100, commit), 1);

        var radius = AppearanceSlider(prefix + "GradientRadiusSlider", "Радиус", 5, 200, "%",
            () => Gradient() is { Kind: GradientKind.Radial } gradient ? gradient.Radius * 100 : null,
            (value, commit) => _editor.SetGradientRadius(value / 100, commit), 5);

        var addStop = Ui.TextButton(prefix + "GradientAddStopButton", "Добавить точку", () =>
        {
            _editor.AddGradientStop();
            SyncAppearanceSections();
        }, "compact");

        var removeStop = Ui.TextButton(prefix + "GradientRemoveStopButton", "Удалить точку", () =>
        {
            _editor.RemoveGradientStop(StopIndex());
            _gradientStop = Math.Max(0, _gradientStop - 1);
            SyncAppearanceSections();
        }, "compact");

        var editors = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                ramp,
                new Ui.ChoiceGrid { MaxColumns = 2, Children = { addStop, removeStop } },
                stopColor,
                stopPosition,
                angle,
                centreX,
                centreY,
                radius,
            },
        };

        _appearanceSyncs.Add(() =>
        {
            var gradient = Gradient();
            var key = gradient is null ? "Off" : gradient.Kind == GradientKind.Radial ? "Radial" : "Linear";
            var keys = new[] { "Off", "Linear", "Radial" };
            for (var index = 0; index < modeButtons.Count; index++)
            {
                var active = keys[index] == key;
                ((ToggleButton)modeButtons[index]).IsChecked = active;
                Ui.SetClass(modeButtons[index], "active", active);
            }

            editors.IsVisible = gradient is not null;
            if (gradient is null)
            {
                return;
            }

            _gradientStop = StopIndex();
            ramp.Stops = [.. gradient.Stops.Select(stop => new GradientRampStop(
                stop.Offset, ParseSwatchColor(stop.Color)))];
            ramp.SelectedIndex = _gradientStop;
            removeStop.IsEnabled = gradient.Stops.Count > GradientFill.MinStops;
            addStop.IsEnabled = gradient.Stops.Count < GradientFill.MaxStops;

            var linear = gradient.Kind == GradientKind.Linear;
            angle.IsVisible = linear;
            centreX.IsVisible = !linear;
            centreY.IsVisible = !linear;
            radius.IsVisible = !linear;
        });

        var expander = new Expander
        {
            Header = new TextBlock { Text = "Градиент" }.Classed("role-panel"),
            IsExpanded = false,
            Content = new StackPanel { Spacing = 6, Children = { modes, editors } },
        }.Named(prefix + "Gradient");
        AutomationProperties.SetName(expander, "Градиент заливки");
        return expander;
    }

    /// <summary>A stored colour as a brush colour; an unreadable one falls back to black.</summary>
    private static Color ParseSwatchColor(string canonical)
    {
        try
        {
            return Color.Parse(canonical);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return Colors.Black;
        }
    }

    /// <summary>
    /// The inspector of a plain Rectangle/Ellipse object.
    /// <para>
    /// These objects had no block at all: the panel fell through to null, so a placed rectangle
    /// kept its creation colour for ever and read as a decoration rather than an object. They own
    /// their fill and stroke directly, so this is the block that makes those three values real.
    /// </para>
    /// </summary>
    private Control BuildPrimitiveSection() => Ui.Section("Фигура объекта", new StackPanel
    {
        Spacing = 5,
        Children =
        {
            AppearanceColorBox("PrimitiveFillColorBox", "Цвет заливки",
                () => _editor.Session.SelectedObject?.FillColor,
                (value, commit) => _editor.SetPrimitiveFillColor(value, commit),
                cancelPreview: _editor.CancelAppearanceGesture),
            BuildGradientBlock("Primitive"),
            AppearanceColorBox("PrimitiveStrokeColorBox", "Цвет обводки",
                () => _editor.Session.SelectedObject?.StrokeColor,
                (value, commit) => _editor.SetPrimitiveStrokeColor(value, commit),
                cancelPreview: _editor.CancelAppearanceGesture),
            AppearanceSlider("PrimitiveStrokeWidthSlider", "Толщина обводки", 0, 64, "px",
                () => _editor.Session.SelectedObject?.StrokeWidth,
                (value, commit) => _editor.SetPrimitiveStrokeWidth(value, commit)),
        },
    }).Named("PrimitiveObjectActions");

    private Control BuildTextSection()
    {
        TextStyle? Style() => _editor.Session.SelectedObject?.Text;

        var content = new TextBox
        {
            Name = "TextContentBox",
            AcceptsReturn = true,
            MinHeight = 64,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(content, "Текст подписи");
        ToolTip.SetTip(content, "Многострочный текст. Применяется при выходе из поля.");
        content.LostFocus += (_, _) =>
        {
            if (!_syncingAppearance && (content.Text ?? "") != (Style()?.Content ?? ""))
            {
                _editor.SetTextContent(content.Text ?? "");
            }
        };
        _appearanceSyncs.Add(() =>
        {
            if (!content.IsFocused)
            {
                content.Text = Style()?.Content ?? "";
            }
        });

        var fontSearch = new TextBox
        {
            Name = "TextFontSearchBox",
            PlaceholderText = "Найти локальный шрифт",
        };
        AutomationProperties.SetName(fontSearch, "Поиск локального шрифта");
        var fontList = new ListBox
        {
            Name = "TextFontList",
            MinHeight = 96,
            MaxHeight = 180,
            SelectionMode = SelectionMode.Single,
        };
        AutomationProperties.SetName(fontList, "Локальные шрифты Windows");
        var syncingFont = false;
        void RefreshFontChoices()
        {
            syncingFont = true;
            try
            {
                var requested = Style()?.RequestedFontFamily;
                fontList.ItemsSource = _editor.FontFamiliesForSearch(fontSearch.Text);
                fontList.SelectedItem = requested;
                if (requested is not null)
                {
                    fontList.ScrollIntoView(requested);
                }
            }
            finally
            {
                syncingFont = false;
            }
        }
        fontSearch.TextChanged += (_, _) => RefreshFontChoices();
        fontList.SelectionChanged += (_, _) =>
        {
            if (!syncingFont && fontList.SelectedItem is string selected)
            {
                _editor.SetSelectedTextFontFamily(selected);
            }
        };

        var previousFont = Ui.TextButton(
            "TextPreviousFontButton", "Предыдущий", () => _editor.SelectAdjacentTextFont(-1), "compact");
        var nextFont = Ui.TextButton(
            "TextNextFontButton", "Следующий", () => _editor.SelectAdjacentTextFont(1), "compact");
        _commandButtons.Add((previousFont, "Предыдущий шрифт", EditorShortcutSettings.Commands.FontPrevious));
        _commandButtons.Add((nextFont, "Следующий шрифт", EditorShortcutSettings.Commands.FontNext));

        var font = new TextBlock { TextWrapping = TextWrapping.Wrap }
            .Classed("faint", "role-caption").Named("TextFontStateText");
        _appearanceSyncs.Add(() =>
        {
            var resolution = _editor.SelectedTextFontResolution;
            font.Text = resolution is null
                ? ""
                : resolution.UsedFallback
                    ? $"Запрошено в документе: {resolution.RequestedFamily}.\n" +
                      $"Фактически показано: {resolution.ResolvedFamily} — локальная замена; запрос сохранён."
                    : $"Запрошено в документе: {resolution.RequestedFamily}.\n" +
                      $"Доступно в Windows и показано: {resolution.ResolvedFamily}.";
            RefreshFontChoices();
        });

        var appearance = new Expander
        {
            Header = new TextBlock { Text = "Внешний вид" }.Classed("role-panel"),
            IsExpanded = false,
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    AppearanceColorBox("TextOutlineColorBox", "Цвет контура",
                        () => Style()?.OutlineColor,
                        (value, commit) => _editor.SetTextOutlineColor(value, commit),
                        cancelPreview: _editor.CancelAppearanceGesture),
                    AppearanceSlider("TextOutlineWidthSlider", "Толщина контура", 0, 32, "px",
                        () => Style()?.OutlineWidth, (v, c) => _editor.SetTextOutlineWidth(v, c)),
                    AppearanceSlider("TextCurveSlider", "Изгиб", -100, 100, "",
                        () => Style()?.Curve, (v, c) => _editor.SetTextCurve(v, c), 5,
                        ResetGlyph("TextCurveResetButton", "Убрать изгиб", () => _editor.SetTextCurve(0))),
                    AppearanceSlider("TextLetterSpacingSlider", "Межбуквенный интервал", 0, 40, "px",
                        () => Style()?.LetterSpacing, (v, c) => _editor.SetTextLetterSpacing(v, c)),
                    AppearanceSlider("TextLineHeightSlider", "Межстрочный интервал", 50, 300, "%",
                        () => Style()?.LineHeight * 100,
                        (v, c) => _editor.SetTextLineHeight(v / 100.0, c), 10),
                    new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            AppearanceToggle("TextFlipHorizontalToggle", "Отразить по горизонтали",
                                () => Style()?.FlipHorizontal,
                                value => _editor.SetTextFlip(horizontal: value)),
                            AppearanceToggle("TextFlipVerticalToggle", "Отразить по вертикали",
                                () => Style()?.FlipVertical,
                                value => _editor.SetTextFlip(vertical: value)),
                        },
                    },
                    ShadowBlock("Text",
                        () => Style() is { } s
                            ? (s.ShadowOffsetX, s.ShadowOffsetY, s.ShadowBlur, s.ShadowOpacity)
                            : null,
                        _editor.SetTextShadowNone, _editor.SetTextShadowStandard,
                        (x, y, b, o, c) => _editor.SetTextShadow(x, y, b, o, c)),
                    BlendChoice("TextBlendChoice", () => Style()?.Blend, _editor.SetTextBlend),
                    AppearanceSlider("TextBlurSlider", "Размытие", 0, 32, "px",
                        () => Style()?.Blur, (v, c) => _editor.SetTextBlur(v, c)),
                },
            },
        }.Named("TextAppearance");
        AutomationProperties.SetName(appearance, "Внешний вид");

        var children = new StackPanel
        {
            Spacing = 5,
            Children =
            {
                content,
                AppearanceToggle("TextBoldToggle", "Полужирный",
                    () => Style()?.FontWeight >= 600, _editor.SetTextBold),
                AppearanceSlider("TextFontSizeSlider", "Размер шрифта", 6, 200, "px",
                    () => Style()?.FontSize, (v, c) => _editor.SetTextFontSize(v, c)),
                AppearanceColorBox("TextFillColorBox", "Цвет текста",
                    () => Style()?.FillColor,
                    (value, commit) => _editor.SetTextFillColor(value, commit),
                    cancelPreview: _editor.CancelAppearanceGesture),
                new TextBlock
                {
                    Text = "Локальные шрифты Windows. Map использует их без копирования файлов шрифтов.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
                fontSearch,
                fontList,
                new Ui.ChoiceGrid { MaxColumns = 2, Children = { previousFont, nextFont } },
                AppearanceChoice("TextAlignmentChoice", "Выравнивание",
                    [("Left", EditorLabels.Alignment(Core.Documents.TextAlignment.Left)),
                     ("Center", EditorLabels.Alignment(Core.Documents.TextAlignment.Center)),
                     ("Right", EditorLabels.Alignment(Core.Documents.TextAlignment.Right))],
                    () => Style()?.Alignment.ToString(),
                    key => _editor.SetTextAlignment(Enum.Parse<Core.Documents.TextAlignment>(key))),
                font,
                BuildLabelPresetBlock(),
                BuildTextPathBlock(),
                BuildTextRegionBlock(),
                BuildLabelSettingsBlock("Text"),
                appearance,
            },
        };

        // A font-substitution probe is a test fixture, not a user command: it appears only when the
        // process is started in explicit test mode by the UIA driver.
        if (TestMode.Enabled)
        {
            children.Children.Add(Ui.TextButton(
                "TextMissingFontButton", "Проверить замену шрифта (тест)",
                _editor.UseMissingFontOnSelectedText, "compact"));
        }

        return Ui.Section("Подпись", children).Named("TextObjectActions");
    }

    private Control BuildInspector()
    {
        var apply = Ui.TextButton("ApplyButton", "Применить", CommitInspector, "primary");
        apply.Margin = new Thickness(0, 8, 0, 0);
        apply.HorizontalAlignment = HorizontalAlignment.Stretch;

        foreach (var box in new[] { _xBox, _yBox, _widthBox, _heightBox, _rotationBox })
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter)
                {
                    CommitInspector();
                    e.Handled = true;
                }
            };
        }

        _inspectorError.TextWrapping = TextWrapping.Wrap;
        _inspectorError.Classes.Add("danger");

        // Rotation, level and opacity are NOT repeated here. They live once, at the top of the
        // object panel, where they apply to the whole selection; a second copy in this block used
        // to edit only the primary object and so disagreed with the value above it.
        _rotationInspectorRow = Ui.PropertyRow("Поворот", _rotationBox, "°");
        _rotationInspectorRow.IsVisible = false;

        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Ui.PropertyRow("X", _xBox, "px"),
                Ui.PropertyRow("Y", _yBox, "px"),
                Ui.PropertyRow("Ширина", _widthBox, "px"),
                Ui.PropertyRow("Высота", _heightBox, "px"),
                apply,
                _inspectorError,
            },
        };
    }

    /// <summary>
    /// The first thing a selected object shows: what it is, where it lives and the handful of
    /// properties that are touched constantly. Layer and level come first because they decide what
    /// covers what; scale, opacity and rotation follow; the rare geometry and appearance settings
    /// stay below in their own blocks. One builder serves the single-object and the multi-selection
    /// panel, so the two can never drift apart - each host gets its own instance because a control
    /// has a single visual parent.
    /// </summary>
    private Control BuildObjectPrimarySection(string prefix)
    {
        var identityImage = new Image
        {
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        }.Named(prefix + "PrimaryThumbnail");
        var identityText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }.Named(prefix + "PrimaryName");
        var identity = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 2),
            Children =
            {
                new Border
                {
                    Width = 28,
                    Height = 28,
                    Margin = new Thickness(0, 0, 8, 0),
                    Child = identityImage,
                }.Docked(Dock.Left),
                identityText,
            },
        };

        var (layerRow, _) = BuildOwnerLayerSelector(prefix);
        var levelRow = BuildLevelSlider(prefix);

        Button Order(string name, string caption, int direction, bool extreme, string tip)
        {
            var button = Ui.TextButton(prefix + name, caption, () =>
            {
                _editor.ReorderSelection(direction, extreme);
                RefreshAfterSelectionCommand();
            }, "compact");
            ToolTip.SetTip(button, tip);
            return button;
        }

        // Scale is offered only where a single percentage has one meaning: every selected object
        // is a placed asset with its own 100 % reference. A shape or a caption sizes itself
        // through its own geometry, and pretending otherwise would force a bitmap idea onto an
        // object that has no use for it.
        var scale = AppearanceSlider(prefix + "ScaleSlider", "Масштаб", 10, 400, "%",
            () => _editor.SelectionScalePercent,
            (value, commit) => _editor.SetSelectionScalePercent(value, commit), 5,
            ResetGlyph(prefix + "ScaleResetButton", "Сбросить масштаб к 100 %",
                () => _editor.SetSelectionScalePercent(100)));

        var opacity = AppearanceSlider(prefix + "OpacitySlider", "Непрозрачность", 0, 100, "%",
            () => _editor.SelectionOpacityPercent,
            (value, commit) => _editor.SetSelectionOpacityPercent(value, commit), 5);

        var rotation = AppearanceSlider(prefix + "RotationSlider", "Поворот", 0, 359, "°",
            () => _editor.SelectionRotationDegrees,
            (value, commit) => _editor.SetSelectionRotationDegrees(value, commit), 1,
            ResetGlyph(prefix + "RotationResetButton", "Сбросить поворот",
                () => _editor.SetSelectionRotationDegrees(0)));

        _levelSyncs.Add(() =>
        {
            var selected = _editor.Session.SelectedObject;
            var count = _editor.SelectionCount;
            identityImage.Source = count == 1 && selected is not null
                ? _thumbnails.Image(_editor.ObjectThumbnailPath(selected))
                : null;
            identityImage.IsVisible = identityImage.Source is not null;
            identityText.Text = count switch
            {
                0 when _editor.SurfaceSelectionCount > 0 => _editor.SelectionStatus,
                0 => "Ничего не выбрано",
                1 => selected?.Name ?? "Объект",
                _ => "Выбрано: " + EditorLabels.Objects(count),
            };
            ToolTip.SetTip(identityText, identityText.Text);
            AutomationProperties.SetName(identityText, identityText.Text);
            scale.IsVisible = _editor.SelectionIsAllStamps;
        });

        var ordering = new Ui.ChoiceGrid
        {
            MaxColumns = 2,
            Children =
            {
                Order("RaiseButton", "Выше", 1, false,
                    "Поднять выделение над соседями того же уровня."),
                Order("LowerButton", "Ниже", -1, false,
                    "Опустить выделение под соседей того же уровня."),
                Order("RaiseTopButton", "Наверх", 1, true,
                    "Сделать выделение самым верхним в его слое."),
                Order("LowerBottomButton", "Вниз", -1, true,
                    "Сделать выделение самым нижним в его слое."),
            },
        };

        // The order of this block is the owner's decision, so it is read from the shared layout
        // instead of being spelled out again in a Children collection no test can see.
        var body = new StackPanel { Spacing = 4 };
        foreach (var property in EditorSurfaceLayout.ObjectPrimaryOrder)
        {
            body.Children.Add(property switch
            {
                ObjectPrimaryProperty.Identity => identity,
                ObjectPrimaryProperty.Layer => layerRow,
                ObjectPrimaryProperty.Level => levelRow,
                ObjectPrimaryProperty.Scale => scale,
                ObjectPrimaryProperty.Opacity => opacity,
                ObjectPrimaryProperty.Rotation => rotation,
                ObjectPrimaryProperty.Ordering => ordering,
                _ => throw new InvalidOperationException("Unhandled primary property " + property),
            });
        }

        // Every value of this block is also in the quick property strip over the canvas, which
        // is the one place for them now; the block stays built for its syncs but is not shown.
        return new Expander
        {
            Header = "Выделение: общие свойства и порядок", Content = body,
            IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch,
            IsVisible = false,
        }.Named(prefix + "PrimarySection");
    }

    /// <summary>The word a selection spread over several layers shows instead of an empty box.</summary>
    private const string MixedLayerCaption = "Разные слои";

    /// <summary>
    /// The owning layer as an explicit list, not a button that steps to "the next one". Every
    /// object layer of the document is offered by its own name; a hidden or locked layer stays in
    /// the list with the reason written next to it and simply refuses to be chosen.
    /// <para>
    /// It is a button with a list in a flyout rather than a ComboBox: the stock combo arranged its
    /// selection box to zero width here, so the control showed an empty field while holding the
    /// right layer all along - exactly the "пустое поле" the owner reported. This shape paints the
    /// current name itself, so it cannot happen again.
    /// </para>
    /// One builder serves the quick strip above the canvas and the object panel, so the two can
    /// never disagree about which layer the selection is in - they are two views of one model.
    /// </summary>
    private Button BuildOwnerLayerPicker(string prefix)
    {
        var caption = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }.Named(prefix + "OwnerLayerCaption");

        var list = new ListBox
        {
            MaxHeight = 320,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncDataTemplate<ObjectLayerTarget>(
                target => target is not null,
                (target, _) => new TextBlock
                {
                    Text = target!.Display,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(8, 5),
                }.Classed(target.Available ? "role-body" : "faint")),
        }.Named(prefix + "OwnerLayerList");
        AutomationProperties.SetName(list, "Список слоёв объектов");

        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = new Border { MinWidth = 220, Child = list },
        };

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new DockPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "▾",
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0),
                    }.Classed("muted").Docked(Dock.Right),
                    caption,
                },
            },
            Flyout = flyout,
        }.Named(prefix + "OwnerLayerSelector");

        var syncing = false;
        list.SelectionChanged += (_, _) =>
        {
            if (syncing || list.SelectedItem is not ObjectLayerTarget target || target.IsCurrent)
            {
                return;
            }

            flyout.Hide();
            if (!target.Available)
            {
                _editor.ReportInspectorError(
                    "Слой «" + target.Name + "» недоступен: он " + target.Unavailable + ".");
                RefreshAfterSelectionCommand();
                return;
            }

            _editor.MoveSelectionToLayer(target.Id);
            RefreshAfterSelectionCommand();
        };

        void Sync()
        {
            syncing = true;
            try
            {
                var targets = _editor.ObjectLayerTargets;
                list.ItemsSource = targets;
                var current = targets.FirstOrDefault(target => target.IsCurrent);
                list.SelectedItem = current;

                // A selection spread over several layers must not show one of them as if it were
                // the answer - and it must not look like an empty field either, which is exactly
                // how the earlier "—" read. It says so in words.
                // A selected road is a selection too: without it the strip showed "—" and a
                // disabled picker for roads, although the model moves roads between layers.
                var any = _editor.SelectionCount > 0 || _editor.WaterSelectionCount > 0 || _editor.RoadSelectionCount > 0;
                var mixed = any && current is null;
                caption.Text = !any
                    ? "—"
                    : mixed ? MixedLayerCaption : current!.Name;
                Ui.SetClass(caption, "mixed", mixed);
                Ui.SetClass(button, "mixed", mixed);
                button.IsEnabled = any && targets.Count > 0;

                AutomationProperties.SetName(button, "Слой-владелец выделения: " + caption.Text);
                ToolTip.SetTip(button, mixed
                    ? "Выделение лежит в разных слоях. Выбор слоя перенесёт в него всё выделение "
                        + "одной отменяемой командой; уровень каждого объекта сохранится."
                    : "Слой, которому принадлежит выделение. Выбор переносит всё выделение одной "
                        + "отменяемой командой; уровень каждого объекта сохраняется.");
            }
            finally
            {
                syncing = false;
            }
        }

        _levelSyncs.Add(Sync);
        _quickSyncs.Add(Sync);
        _layerLists[prefix] = list;
        return button;
    }

    /// <summary>
    /// The layer list of one picker. It lives inside a flyout, so it joins the visual tree only
    /// while the flyout is open; the native smoke needs a handle to it to prove the open state.
    /// </summary>
    public ListBox? LayerListForSmoke(string prefix) =>
        _layerLists.TryGetValue(prefix, out var list) ? list : null;

    private (Control Row, Button Selector) BuildOwnerLayerSelector(string prefix)
    {
        var selector = BuildOwnerLayerPicker(prefix);
        return (Ui.PropertyRow("Слой", selector), selector);
    }

    /// <summary>
    /// The object's own level: one integer band from -10 to +10, as a slider that can only stop on
    /// whole numbers. Dragging previews and lands exactly one history entry. A selection whose
    /// members disagree says "разные" instead of inventing a shared number, and moving the slider
    /// then shifts every member by the same amount so their differences survive.
    /// </summary>
    private Control BuildLevelSlider(string prefix)
    {
        var readout = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
        }.Classed("muted").Named(prefix + "LevelValue");

        var slider = new Slider
        {
            Minimum = EditorViewModel.MinimumSublayer,
            Maximum = EditorViewModel.MaximumSublayer,
            SmallChange = 1,
            LargeChange = 5,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            // Twenty-one ticks under the track say "whole steps" without twenty-one numbers
            // shouting it. The two ends are labelled separately, below.
            TickPlacement = Avalonia.Controls.TickPlacement.BottomRight,
            Value = 0,
        }.Named(prefix + "LevelSlider");
        ToolTip.SetTip(slider,
            "Уровень объекта от -10 до +10 целыми шагами. Стрелки — на 1, PageUp/PageDown — на 5, " +
            "Home и End — к краям.");

        var gesture = false;
        var anchor = 0;
        var mixed = false;

        void Describe()
        {
            readout.Text = _editor.SelectionSublayerReadout;
            AutomationProperties.SetName(slider, "Уровень объекта: " + readout.Text);
        }

        void Apply(bool commit)
        {
            var value = (int)Math.Round(slider.Value);
            if (mixed)
            {
                _editor.ShiftSelectionSublayer(value - anchor, commit);
            }
            else
            {
                _editor.SetSelectionSublayer(value, commit);
            }

            Describe();
        }

        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _syncingAppearance)
            {
                return;
            }

            // The document never sees a fraction: the control snaps to ticks and the value is
            // rounded again before it is applied.
            Apply(commit: false);
        };
        slider.AddHandler(PointerPressedEvent, (_, _) => gesture = true,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        slider.PointerCaptureLost += (_, _) =>
        {
            gesture = false;
            if (!_syncingAppearance)
            {
                Apply(commit: true);
                RefreshAfterSelectionCommand();
            }
        };
        slider.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Home or Key.End)
            {
                gesture = true;
                slider.Value = e.Key == Key.Home
                    ? EditorViewModel.MinimumSublayer
                    : EditorViewModel.MaximumSublayer;
                e.Handled = true;
                return;
            }

            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown)
            {
                gesture = true;
            }
        };
        slider.KeyUp += (_, e) =>
        {
            if (_syncingAppearance
                || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down
                    or Key.PageUp or Key.PageDown or Key.Home or Key.End))
            {
                return;
            }

            gesture = false;
            Apply(commit: true);
            RefreshAfterSelectionCommand();
        };
        slider.LostFocus += (_, _) =>
        {
            if (!gesture && !_syncingAppearance)
            {
                Apply(commit: true);
            }
        };

        _levelSyncs.Add(() =>
        {
            if (gesture)
            {
                return;
            }

            var previous = _syncingAppearance;
            _syncingAppearance = true;
            try
            {
                mixed = _editor.SelectionSublayerIsMixed;
                var levels = _editor.Session.SelectedObjects.Select(item => item.Sublayer).ToList();
                anchor = levels.Count == 0
                    ? 0
                    : mixed ? (int)Math.Round(levels.Average()) : levels[0];
                slider.Value = Math.Clamp(
                    anchor, EditorViewModel.MinimumSublayer, EditorViewModel.MaximumSublayer);
                slider.IsEnabled = levels.Count > 0;
                Ui.SetClass(readout, "warn", mixed);
                Describe();
            }
            finally
            {
                _syncingAppearance = previous;
            }
        });

        // The exact pair stays available for a single step without aiming at the handle; the
        // slider remains the control that shows and sets the value.
        Button Step(string name, string caption, int direction, string tip)
        {
            var button = Ui.TextButton(prefix + name, caption, () =>
            {
                _editor.ShiftSelectionSublayer(direction);
                RefreshAfterSelectionCommand();
            }, "compact");
            ToolTip.SetTip(button, tip);
            return button;
        }

        // The exact number, typed. The slider is the control that shows the value and the one the
        // hand reaches for; this is how a user says "exactly -7" without aiming at a tick.
        var entry = new TextBox
        {
            Width = 58,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
        }.Named(prefix + "LevelBox");
        AutomationProperties.SetName(entry, "Уровень объекта, точный ввод");
        ToolTip.SetTip(entry,
            "Целое число от " + EditorViewModel.MinimumSublayer + " до +" + EditorViewModel.MaximumSublayer
                + ". Введите значение и нажмите Enter.");

        var entrySyncing = false;

        void CommitEntry()
        {
            if (entrySyncing)
            {
                return;
            }

            var text = (entry.Text ?? "").Trim().Replace('\u2212', '-').Replace("+", "").Trim();
            if (text.Length == 0)
            {
                RefreshAfterSelectionCommand();
                return;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var typed))
            {
                _editor.ReportInspectorError(
                    "Уровень — целое число от " + EditorViewModel.MinimumSublayer
                        + " до +" + EditorViewModel.MaximumSublayer + ".");
                RefreshAfterSelectionCommand();
                return;
            }

            var bounded = Math.Clamp(
                typed, EditorViewModel.MinimumSublayer, EditorViewModel.MaximumSublayer);
            if (bounded != typed)
            {
                _editor.ReportInspectorError(
                    "Уровень ограничен диапазоном " + EditorViewModel.MinimumSublayer
                        + "…+" + EditorViewModel.MaximumSublayer + ".");
            }

            _editor.SetSelectionSublayer(bounded, commit: true);
            RefreshAfterSelectionCommand();
        }

        entry.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitEntry();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                RefreshAfterSelectionCommand();
                e.Handled = true;
            }
        };
        entry.LostFocus += (_, _) => CommitEntry();

        var reset = ResetGlyph(prefix + "LevelResetButton", "Сбросить уровень к нулю", () =>
        {
            _editor.SetSelectionSublayer(0, commit: true);
            RefreshAfterSelectionCommand();
        });

        _levelSyncs.Add(() =>
        {
            entrySyncing = true;
            try
            {
                entry.Text = _editor.SelectionSublayerReadout;
                Ui.SetClass(entry, "mixed", _editor.SelectionSublayerIsMixed);
                entry.IsEnabled = _editor.SelectionCount > 0;
                reset.IsEnabled = _editor.SelectionCount > 0;
                AutomationProperties.SetName(entry, "Уровень объекта: " + entry.Text);
            }
            finally
            {
                entrySyncing = false;
            }
        });

        var header = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Ui.ParameterLabel("Уровень"),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 3,
                    Children =
                    {
                        Step("SublayerDownButton", "−", -1, "Опустить выделение на один уровень ниже."),
                        entry,
                        Step("SublayerUpButton", "+", 1, "Поднять выделение на один уровень выше."),
                        reset,
                    },
                },
            },
        };

        // The band has to be readable off the control itself, not guessed from a tooltip.
        var ends = new DockPanel
        {
            Margin = new Thickness(0, 1, 0, 0),
            Children =
            {
                new TextBlock { Text = "+" + EditorViewModel.MaximumSublayer.ToString(CultureInfo.InvariantCulture) }
                    .Classed("faint").Named(prefix + "LevelMaximumCaption").Docked(Dock.Right),
                new TextBlock { Text = EditorViewModel.MinimumSublayer.ToString(CultureInfo.InvariantCulture) }
                    .Classed("faint").Named(prefix + "LevelMinimumCaption").Docked(Dock.Left),
                readout,
            },
        };

        return new StackPanel
        {
            Margin = new Thickness(0, 2),
            Children = { header, slider, ends },
        }.Named(prefix + "LevelRow");
    }

    /// <summary>
    /// What every selection can do, whatever its kind. State is shown as a value, not hidden
    /// behind an "or" verb, so the user reads the object's condition before changing it.
    /// </summary>
    private Control BuildSelectionCommon()
    {
        var (visibilityRow, visibilityButton) = PropertyValueRow(
            "ObjectVisibilityButton", "Видимость", "Показан",
            [("Показан", () => _editor.SetSelectedObjectVisibility(true)),
             ("Скрыт", () => _editor.SetSelectedObjectVisibility(false))]);
        _objectVisibilityValue = visibilityButton;

        var (lockRow, lockButton) = PropertyValueRow(
            "ObjectLockButton", "Блокировка", "Свободен",
            [("Свободен", () => _editor.SetSelectedObjectLock(false)),
             ("Заблокирован", () => _editor.SetSelectedObjectLock(true))]);
        _objectLockValue = lockButton;

        _objectDeleteButton = Ui.CommandButton("DeleteObjectButton", "Удалить объект", MapIcons.Trash,
            _editor.DeleteSelectedObject, "danger-text");
        DynamicTip(_objectDeleteButton, () =>
            $"Удалить выделенный объект (Delete). Действие можно отменить ({_editor.Shortcuts.Get(EditorShortcutSettings.Commands.Undo)}).");

        return new StackPanel
        {
            Spacing = 0,
            Children =
            {
                BuildObjectPrimarySection("Object"),
                new Ui.SectionPanel("CommonObjectActions", "Состояние", new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        visibilityRow,
                        lockRow,
                        Ui.TextButton("GridSnapObjectButton", "Привязать к сетке",
                            _editor.SnapSelectedObjectToGrid, "compact"),
                    },
                }, true),
            },
        };
    }
}
