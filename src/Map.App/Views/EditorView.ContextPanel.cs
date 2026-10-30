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
    // ============================================================ context panel

    private Control BuildContextPanel()
    {
        BuildToolPanels();
        BuildSelectionSections();
        BuildContextRoots();

        var collapse = new Button { Content = MapIcons.Grid24(MapIcons.LinePanelLeft, 16) }
            .Named("CollapseContextPanelButton").Classed("icon");
        collapse.Click += (_, _) => ToggleContextPanel();
        AutomationProperties.SetName(collapse, "Свернуть или развернуть панель инструмента");
        ToolTip.SetTip(collapse, "Свернуть или развернуть панель");
        _contextTitle.Bind(ToolTip.TipProperty, new Binding(nameof(TextBlock.Text)) { Source = _contextTitle });
        _contextTitle.Classes.Add("panel-title");
        BuildContextSwitch();
        // Header: what the panel is about (tool or selected object), its key, and on the right the
        // one panel-wide command the current content offers (the asset panel's "К базовым").
        _contextHeaderTitle = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _contextIcon, _contextTitle, _contextKey },
        };
        var header = new Border
        {
            Child = new DockPanel { LastChildFill = true, Children =
            {
                collapse.Docked(Dock.Right),
                _contextHeaderAction.Docked(Dock.Right),
                _contextHeaderTitle,
            } },
        }.Classed("panel-header");
        // Scope line: whom the settings below act on, in words, with the target layer.
        _contextScopeTarget.TextTrimming = TextTrimming.CharacterEllipsis;
        _contextScopeTarget.Classes.Add("scope-target");
        _contextScopeLine = new Border
        {
            Child = new DockPanel { LastChildFill = true, Children =
            {
                _contextScopeChip.Docked(Dock.Left),
                _contextScopeTarget,
            } },
        }.Classed("scope-line").Named("ContextScopeLine");
        _contextScopeTarget.Margin = new Thickness(8, 0, 0, 0);

        _contextBody = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border
            {
                Padding = new Thickness(12, 10, 12, 16),
                // The look transfer belongs to the selected object and scrolls with its panel,
                // instead of a fixed footer that ate the height of a short window.
                Child = new StackPanel { Children = { _contextHost, BuildAppearanceTransferHost() } },
            },
        };

        _contextPanel = new Border
        {
            Width = Ui.ContextPanelWidth,
            Child = new DockPanel { Children =
            {
                header.Docked(Dock.Top),
                new Border { Padding = new Thickness(8, 6, 8, 2), Child = _contextSwitch }.Docked(Dock.Top),
                _contextScopeLine.Docked(Dock.Top),
                // The footer lives outside the scrolling body; collapsing hides it with the body,
                // otherwise its hints wrapped one letter per line in the 42 px strip.
                (_contextFooter = new StackPanel { Children =
                {

                    BuildEditableAreaPlacementActions(),
                } }).Docked(Dock.Bottom),
                _contextBody,
            } },
        }.Classed("context-panel", "calm-modes").Named("ContextPanel");
        return _contextPanel;
    }

    private Control BuildAppearanceTransferHost() =>
        _appearanceTransferHost = new Border { Padding = new Thickness(0, 6, 0, 0), Child = BuildAppearanceTransfer() };

    private void BuildToolPanels()
    {
        _toolPanels[EditorTool.Select] = BuildSelectPanel();
        _toolPanels[EditorTool.Line] = BuildLinePanel();
        _toolPanels[EditorTool.Shape] = BuildShapePanel();
        _toolPanels[EditorTool.Text] = BuildTextPanel();
        _toolPanels[EditorTool.Brush] = BuildBrushPanel();
        _toolPanels[EditorTool.Mask] = BuildMaskPanel();
        _toolPanels[EditorTool.Land] = BuildLandPanel();
        _toolPanels[EditorTool.Stamp] = BuildStampToolPanel();
        _toolPanels[EditorTool.Notes] = BuildNotesTab();
        _toolPanels[EditorTool.River] = BuildRiverPanel();
        _toolPanels[EditorTool.Road] = BuildRoadPanel();
        _toolPanels[EditorTool.Measure] = BuildMeasurePanel();
        if (_isHexWorkshop) _toolPanels[EditorTool.Hex] = BuildHexPanel();
    }

    // Select only selects existing objects. Assets are a separate rail command, while object
    // creation remains owned by Shape and Stamp, so the tool never changes meaning under the user.
    private static Control BuildSelectPanel() => new Border();

    private Control BuildLinePanel()
    {
        var (modes, buttons) = Ui.Segmented("LineDrawMode",
            [("FreeDraw", "От руки", null), ("Pen", "По узлам", null)],
            key =>
            {
                _editor.ActiveLineDrawMode = key == "Pen" ? LineDrawMode.Pen : LineDrawMode.FreeDraw;
                _editor.CancelPenPath();
                SyncAppearanceSections();
            });
        _appearanceSyncs.Add(() =>
        {
            for (var i = 0; i < buttons.Count; i++)
            {
                var active = i == (_editor.ActiveLineDrawMode == LineDrawMode.FreeDraw ? 0 : 1);
                ((ToggleButton)buttons[i]).IsChecked = active; Ui.SetClass(buttons[i], "active", active);
            }
        });
        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Explanation("От руки — провести по холсту. По узлам — ставить узлы кликами, Enter завершает, Esc отменяет."),
                modes,
                BuildPenPathControls("Line"),
                Ui.Section("Новая линия", new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        AppearanceColorBox("NewLineStrokeColorBox", "Цвет линии",
                            () => _editor.NewLineStrokeColor,
                            value => _editor.NewLineStrokeColor = value),
                        AppearanceSlider("NewLineStrokeWidthSlider", "Толщина", 1, 64, "px",
                            () => _editor.NewLineStrokeWidth,
                            (value, _) => _editor.NewLineStrokeWidth = value),
                    },
                }),
            },
        };
    }

    /// <summary>
    /// The state and the two exits of an unfinished pen path. Without them the mode had no visible
    /// state at all: nodes went down and the only way to learn whether anything was being built
    /// was to guess.
    /// </summary>
    private Control BuildPenPathControls(string prefix)
    {
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap }
            .Classed("faint", "role-caption").Named(prefix + "PenPathStateText");

        var finish = Ui.TextButton(prefix + "FinishPathButton", "Завершить контур", () =>
        {
            _editor.FinishPenPath();
            SyncAppearanceSections();
        }, "compact");

        var cancel = Ui.TextButton(prefix + "CancelPathButton", "Отменить контур", () =>
        {
            _editor.CancelPenPath();
            SyncAppearanceSections();
        }, "compact");

        var panel = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                state,
                new WrapPanel { ItemSpacing = 4, LineSpacing = 4, Children = { finish, cancel } },
            },
        };

        _appearanceSyncs.Add(() =>
        {
            panel.IsVisible = _editor.IsPenMode;
            var nodes = _editor.PenNodeCount;
            state.Text = nodes == 0
                ? "Контур не начат: щёлкайте по холсту, чтобы ставить узлы."
                : $"Узлов в контуре: {nodes}.";
            finish.IsEnabled = _editor.PenPathActive;
            cancel.IsEnabled = _editor.PenPathActive;
        });

        return panel;
    }

    private Control BuildShapePanel()
    {
        var options = new[]
        {
            ("FreeDraw", "От руки", ShapeDrawMode.FreeDraw), ("Pen", "По узлам", ShapeDrawMode.Pen),
            ("Brush", "Кистью", ShapeDrawMode.Brush), ("Rectangle", "Прямоугольник", ShapeDrawMode.Rectangle),
            ("Ellipse", "Эллипс", ShapeDrawMode.Ellipse), ("Polygon", "Многоугольник", ShapeDrawMode.Polygon),
        };
        var (modes, buttons) = Ui.Segmented("ShapeDrawMode",
            [.. options.Select(item => (item.Item1, item.Item2, (string?)null))],
            key =>
            {
                _editor.ActiveShapeDrawMode = options.First(item => item.Item1 == key).Item3;
                _editor.CancelPenPath();
                SyncAppearanceSections();
            });
        _appearanceSyncs.Add(() =>
        {
            for (var i = 0; i < buttons.Count; i++)
            {
                var active = options[i].Item3 == _editor.ActiveShapeDrawMode;
                ((ToggleButton)buttons[i]).IsChecked = active; Ui.SetClass(buttons[i], "active", active);
            }
        });
        var sides = AppearanceSlider("ShapePolygonSidesSlider", "Сторон", 3, 24, "",
            () => _editor.ShapePolygonSides,
            (value, _) => _editor.ShapePolygonSides = (int)Math.Round(value));

        var brushWidth = AppearanceSlider("ShapeBrushWidthSlider", "Ширина полосы", 1, 256, "px",
            () => _editor.ShapeBrushWidth,
            (value, _) => _editor.ShapeBrushWidth = value);

        _appearanceSyncs.Add(() =>
        {
            sides.IsVisible = _editor.ActiveShapeDrawMode == ShapeDrawMode.Polygon;
            brushWidth.IsVisible = _editor.ActiveShapeDrawMode == ShapeDrawMode.Brush;
        });

        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Explanation("Перетаскивание задаёт фигуру: Shift — правильная, Alt — от центра. «По узлам» ставит узлы кликами. Сложение и вычитание работают над двумя выделенными фигурами."),
                modes,
                sides,
                brushWidth,
                BuildPenPathControls("Shape"),
                Ui.Section("Новая фигура", new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        AppearanceColorBox("NewShapeFillColorBox", "Цвет заливки",
                            () => _editor.NewShapeFillColor,
                            value => _editor.NewShapeFillColor = value),
                        AppearanceColorBox("NewShapeStrokeColorBox", "Цвет обводки",
                            () => _editor.NewShapeStrokeColor,
                            value => _editor.NewShapeStrokeColor = value),
                        AppearanceSlider("NewShapeStrokeWidthSlider", "Толщина обводки", 0, 64, "px",
                            () => _editor.NewShapeStrokeWidth,
                            (value, _) => _editor.NewShapeStrokeWidth = value),
                    },
                }),
            },
        };
    }

    private Control BuildTextPanel() => new StackPanel
    {
        Spacing = 10,
        Children =
        {
            Explanation("Щёлкните для стандартного блока или протяните его размер. До отпускания виден контур; оформление наследуется только переключателем ниже."),
            AppearanceToggle("TextInheritToggle", "Синхронизировать с выделенным текстом",
                () => _editor.TextInheritFromSelection,
                value => _editor.TextInheritFromSelection = value),
            BuildLabelSettingsBlock("Tool"),
        },
    };

    private Control BuildBrushPanel()
    {
        void UpdateSettings(Func<BrushSettings, BrushSettings> change)
        {
            _editor.BrushSettings = change(_editor.BrushSettings);
            SyncAppearanceSections();
        }

        _eraserButton = new ToggleButton
        {
            Content = TallCaption(MapIcons.HexErase, "Ластик"), MinHeight = 44, Padding = new Thickness(2, 4),
        }.Named("EraserButton").Classed("button-command", "segment");
        AutomationProperties.SetName(_eraserButton, "Ластик");
        _eraserButton.Click += (_, _) =>
        {
            _editor.BrushSettings = _editor.BrushSettings with { Eraser = !_editor.BrushSettings.Eraser };
            _eraserButton.IsChecked = _editor.BrushSettings.Eraser;
            Ui.SetClass(_eraserButton, "active", _editor.BrushSettings.Eraser);
            ToolTip.SetTip(_eraserButton, _editor.BrushSettings.Eraser
                ? "Ластик включён: штрих стирает"
                : "Ластик выключен: штрих рисует");
        };
        _eraserButton.HorizontalAlignment = HorizontalAlignment.Stretch;

        _brushTipNameText = new TextBlock
        {
            Name = "BrushTipNameText",
            TextWrapping = TextWrapping.Wrap,
        }.Classed("muted");
        var clearTip = Ui.TextButton("ClearBrushTipButton", "Сбросить отпечаток", () =>
        {
            _editor.ClearBrushTip();
            SyncAppearanceSections();
        }, "compact");

        _appearanceSyncs.Add(() =>
        {
            var eraser = _editor.BrushSettings.Eraser;
            _eraserButton.IsChecked = eraser;
            Ui.SetClass(_eraserButton, "active", eraser);
            _brushTipNameText.Text = _editor.BrushTipName is { Length: > 0 } tipName
                ? $"Отпечаток: {tipName}\nМатериал: {_editor.BrushTextureName ?? "сплошной цвет"}"
                : $"Отпечаток: круглая кисть\nМатериал: {_editor.BrushTextureName ?? "сплошной цвет"}";
            AutomationProperties.SetName(_brushTipNameText, _brushTipNameText.Text);
            clearTip.IsEnabled = _editor.BrushSettings.Tip is not null;
        });

        var presetName = new TextBox
        {
            Name = "BrushPresetNameBox",
            PlaceholderText = "Название пресета",
            MaxLength = 80,
        };
        var presetChoice = new ComboBox
        {
            Name = "BrushPresetChoice",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Выберите пресет",
        };
        void RefreshPresets()
        {
            var selectedId = presetChoice.SelectedIndex >= 0
                && presetChoice.SelectedIndex < _editor.BrushPresets.Count
                ? _editor.BrushPresets[presetChoice.SelectedIndex].Id
                : null;
            presetChoice.ItemsSource = _editor.BrushPresets.Select(item => item.Name).ToList();
            presetChoice.SelectedIndex = selectedId is null
                ? (_editor.BrushPresets.Count > 0 ? 0 : -1)
                : _editor.BrushPresets.ToList().FindIndex(item => item.Id == selectedId);
        }
        RefreshPresets();

        bool TrySelectedPreset(out LocalBrushPreset preset)
        {
            preset = null!;
            if (presetChoice.SelectedIndex < 0 || presetChoice.SelectedIndex >= _editor.BrushPresets.Count)
                return false;
            preset = _editor.BrushPresets[presetChoice.SelectedIndex];
            return true;
        }

        var presetCommands = new Ui.ChoiceGrid
        {
            MaxColumns = 2,
            MinimumItemWidth = 88,
            Children =
            {
                Ui.TextButton("SaveBrushPresetButton", "Сохранить текущий", () =>
                {
                    if (_editor.SaveBrushPreset(presetName.Text ?? ""))
                    {
                        presetName.Text = "";
                        RefreshPresets();
                    }
                }, "segment"),
                Ui.TextButton("ApplyBrushPresetButton", "Применить", () =>
                {
                    if (TrySelectedPreset(out var preset) && _editor.ApplyLocalBrushPreset(preset.Id))
                        SyncAppearanceSections();
                }, "segment"),
                Ui.TextButton("RenameBrushPresetButton", "Переименовать", () =>
                {
                    if (TrySelectedPreset(out var preset)
                        && _editor.RenameBrushPreset(preset.Id, presetName.Text ?? ""))
                    {
                        presetName.Text = "";
                        RefreshPresets();
                    }
                }, "segment"),
                Ui.AsyncButton("DeleteBrushPresetButton", "Удалить", async () =>
                {
                    if (!TrySelectedPreset(out var preset) || TopLevel.GetTopLevel(this) is not Window owner)
                        return;
                    if (await ConfirmationWindow.ShowAsync(owner, "Удалить пресет кисти?",
                            $"Локальный пресет «{preset.Name}» будет удалён. Ассет материала останется в библиотеке.",
                            "Удалить пресет", "DeleteBrushPresetConfirmButton", destructive: true)
                        && _editor.DeleteBrushPreset(preset.Id))
                        RefreshPresets();
                }, "segment"),
            },
        };

        var solid = new Button
        {
            Content = TallCaption(MapIcons.LinePalette, "Цвет"), MinHeight = 44, Padding = new Thickness(2, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        }.Named("BrushSolidMaterialButton").Classed("segment");
        AutomationProperties.SetName(solid, "Цвет");
        ToolTip.SetTip(solid, "Сплошной цвет без текстуры");
        solid.Click += (_, _) =>
        {
            _editor.ClearBrushTexture();
            _editor.BrushSettings = _editor.BrushSettings with { Eraser = false };
            SyncAppearanceSections();
        };
        var texture = new Button
        {
            Content = TallCaption(MapIcons.LineCatalog, "Текстура"), MinHeight = 44, Padding = new Thickness(2, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        }.Named("BrushTextureMaterialButton").Classed("segment");
        AutomationProperties.SetName(texture, "Текстура из библиотеки");
        ToolTip.SetTip(texture, "Текстура из библиотеки: открывает каталог");
        texture.Click += (_, _) => OpenAssetCatalog(focusSearch: true);

        // Three tabs (audit 2026-10-19): what and how big the brush paints, the shape of its dab
        // with the fine settings, and the saved presets.
        var brushPage = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                BuildRasterToolReadiness("Brush"),
                Ui.Subgroup("Чем рисует", new Avalonia.Controls.Primitives.UniformGrid { Rows = 1, Columns = 3, Children = { solid, texture, _eraserButton } }),
                AppearanceColorBox("BrushColorBox", "Цвет",
                    () => _editor.BrushSettings.Color, _editor.SetBrushColor),
                AppearanceSlider("BrushSizeSlider", "Размер кисти", 1, 256, "px",
                    () => _editor.BrushSettings.Radius,
                    (value, _) => UpdateSettings(settings => settings with { Radius = value })),
                AppearanceSlider("BrushOpacitySlider", "Непрозрачность", 5, 100, "%",
                    () => _editor.BrushSettings.Opacity * 100,
                    (value, _) => UpdateSettings(settings => settings with { Opacity = value / 100.0 }), 5),
            },
        };
        var presetsPage = new StackPanel
        {
            Spacing = 6,
            Children = { Ui.Hint("Пресеты хранятся только на этом компьютере."), presetName, presetChoice, presetCommands },
        };
        var tipPage = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                BuildBrushTipList(),
                Ui.Advanced("BrushAdvanced", new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        AppearanceSlider("BrushSoftnessSlider", "Мягкость края", 0, 100, "%",
                            () => _editor.BrushSettings.Softness * 100,
                            (value, _) => UpdateSettings(settings => settings with
                            {
                                Softness = value / 100.0,
                            }), 5),
                        AppearanceSlider("BrushSpacingSlider", "Шаг отпечатков", 5, 100, "%",
                            () => _editor.BrushSettings.Spacing * 100,
                            (value, _) => UpdateSettings(settings => settings with
                            {
                                Spacing = value / 100.0,
                            }), 5),
                        AppearanceSlider("BrushScatterSlider", "Разброс", 0, 100, "%",
                            () => _editor.BrushSettings.Scatter * 100,
                            (value, _) => UpdateSettings(settings => settings with
                            {
                                Scatter = value / 100.0,
                            }), 5),
                        AppearanceSlider("BrushAngleSlider", "Угол отпечатка", -180, 180, "°",
                            () => _editor.BrushSettings.AngleDegrees,
                            (value, _) => UpdateSettings(settings => settings with
                            {
                                AngleDegrees = value,
                            }), 5),
                        new WrapPanel
                        {
                            Orientation = Orientation.Horizontal,
                            ItemSpacing = 4,
                            LineSpacing = 4,
                            Children =
                            {
                                AppearanceToggle("BrushRandomFlipHorizontal", "Случайно отражать X",
                                    () => _editor.BrushSettings.RandomFlipHorizontal,
                                    value => UpdateSettings(settings => settings with
                                    {
                                        RandomFlipHorizontal = value,
                                    })),
                                AppearanceToggle("BrushRandomFlipVertical", "Случайно отражать Y",
                                    () => _editor.BrushSettings.RandomFlipVertical,
                                    value => UpdateSettings(settings => settings with
                                    {
                                        RandomFlipVertical = value,
                                    })),
                            },
                        },
                        AppearanceSlider("BrushSizeJitterSlider", "Разброс размера", 0, 100, "%",
                            () => _editor.BrushSettings.SizeJitter * 100,
                            (value, _) => UpdateSettings(settings => settings with
                            {
                                SizeJitter = value / 100.0,
                            }), 5),
                        AppearanceSlider("BrushOpacityJitterSlider", "Разброс непрозрачности", 0, 100, "%",
                            () => _editor.BrushSettings.OpacityJitter * 100,
                            (value, _) => UpdateSettings(settings => settings with
                            {
                                OpacityJitter = value / 100.0,
                            }), 5),
                        new TextBlock
                        {
                            Text = "Параметры текстуры применяются тем же evaluator, что и commit.",
                            TextWrapping = TextWrapping.Wrap,
                        }.Classed("faint", "role-caption"),
                        AppearanceSlider("BrushTextureScaleSlider", "Масштаб текстуры", 5, 6400, "%",
                            () => _editor.BrushSettings.TextureScale * 100,
                            (v, _) => UpdateSettings(s => s with { TextureScale = v / 100 }), 5),
                        AppearanceSlider("BrushTextureRotationSlider", "Поворот текстуры", -180, 180, "°",
                            () => _editor.BrushSettings.TextureRotationDegrees,
                            (v, _) => UpdateSettings(s => s with { TextureRotationDegrees = v }), 5),
                        AppearanceSlider("BrushTextureOffsetXSlider", "Смещение текстуры X", -512, 512, "px",
                            () => _editor.BrushSettings.TextureOffsetX,
                            (v, _) => UpdateSettings(s => s with { TextureOffsetX = v }), 5),
                        AppearanceSlider("BrushTextureOffsetYSlider", "Смещение текстуры Y", -512, 512, "px",
                            () => _editor.BrushSettings.TextureOffsetY,
                            (v, _) => UpdateSettings(s => s with { TextureOffsetY = v }), 5),
                        AppearanceSlider("BrushTextureOpacitySlider", "Непрозрачность текстуры", 0, 100, "%",
                            () => _editor.BrushSettings.TextureOpacity * 100,
                            (v, _) => UpdateSettings(s => s with { TextureOpacity = v / 100 }), 5),
                        AppearanceSlider("BrushHueSlider", "Оттенок", -180, 180, "°",
                            () => _editor.BrushSettings.HueDegrees,
                            (v, _) => UpdateSettings(s => s with { HueDegrees = v }), 5),
                        AppearanceSlider("BrushSaturationSlider", "Насыщенность", -100, 100, "%",
                            () => _editor.BrushSettings.Saturation * 100,
                            (v, _) => UpdateSettings(s => s with { Saturation = v / 100 }), 5),
                        AppearanceSlider("BrushBrightnessSlider", "Яркость", -100, 100, "%",
                            () => _editor.BrushSettings.Brightness * 100,
                            (v, _) => UpdateSettings(s => s with { Brightness = v / 100 }), 5),
                        AppearanceSlider("BrushContrastSlider", "Контраст", -100, 100, "%",
                            () => _editor.BrushSettings.Contrast * 100,
                            (v, _) => UpdateSettings(s => s with { Contrast = v / 100 }), 5),
                        AppearanceToggle("BrushPenDynamics", "Нажим пера",
                            () => _editor.BrushSettings.PressureAffectsSize
                                && _editor.BrushSettings.PressureAffectsOpacity
                                && _editor.BrushSettings.PressureAffectsSoftness,
                            value => UpdateSettings(s => s with
                            {
                                PressureAffectsSize = value,
                                PressureAffectsOpacity = value,
                                PressureAffectsSoftness = value,
                            })),
                        AppearanceToggle("BrushPressureSize", "Давление → размер",
                            () => _editor.BrushSettings.PressureAffectsSize,
                            value => UpdateSettings(s => s with { PressureAffectsSize = value })),
                        AppearanceToggle("BrushPressureOpacity", "Давление → непрозрачность",
                            () => _editor.BrushSettings.PressureAffectsOpacity,
                            value => UpdateSettings(s => s with { PressureAffectsOpacity = value })),
                        AppearanceToggle("BrushPressureSoftness", "Давление → мягкость",
                            () => _editor.BrushSettings.PressureAffectsSoftness,
                            value => UpdateSettings(s => s with { PressureAffectsSoftness = value })),
                        _brushTipNameText,
                        clearTip,
                    },
                }),
            },
        };
        return Ui.CategoryTabs("BrushTabs",
            new Ui.CategoryTab("Paint", "Кисть", MapIcons.LineBrush, brushPage, "Цвет, текстура или ластик, размер и непрозрачность"),
            new Ui.CategoryTab("Tip", "Отпечаток", MapIcons.LineStamp, tipPage, "Форма отпечатка, мягкость, шаг, разброс и текстура"),
            new Ui.CategoryTab("Presets", "Пресеты", MapIcons.LineSparkle, presetsPage, "Сохранить, применить, переименовать пресет кисти"));
    }

    /// <summary>
    /// The tip chooser as a list: one row per shape, each showing the stamp the engine actually
    /// prints, its name and what it is for, with the current one selected.
    /// <para>
    /// It used to be seven identical text buttons in a wrap panel. Nothing showed which tip was in
    /// effect, the names alone did not say what a shape looked like, and at a narrow panel width
    /// the row wrapped into an unreadable block.
    /// </para>
    /// </summary>
    /// <summary>A glyph over a short caption, the content of a compact square-ish choice.</summary>
    private static Control TallCaption(string icon, string text) => new StackPanel
    {
        Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center,
        Children =
        {
            new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = MapIcons.Grid24(icon, 18) },
            new TextBlock { Text = text, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
        },
    };

    private Control BuildBrushTipList()
    {
        var tips = new (BrushPresetKind Kind, string Name, string Purpose)[]
        {
            (BrushPresetKind.RoundHard, "Круг · жёсткий", "Ровный край без растушёвки"),
            (BrushPresetKind.RoundSoft, "Круг · мягкий", "Плавное затухание к краю"),
            (BrushPresetKind.EdgeBlock, "Краевой блок", "Рамка: печатает только края квадрата"),
            (BrushPresetKind.GridBlock, "Блок", "Сплошной квадрат под сетку"),
            (BrushPresetKind.Rectangle, "Прямоугольник", "Широкий мазок с прямыми углами"),
            (BrushPresetKind.Ellipse, "Эллипс", "Приплюснутый овал"),
            (BrushPresetKind.Polygon, "Многоугольник", "Скошенный к низу силуэт"),
        };
        static string Short(BrushPresetKind kind) => kind switch
        {
            BrushPresetKind.RoundHard => "Жёсткий", BrushPresetKind.RoundSoft => "Мягкий", BrushPresetKind.EdgeBlock => "Рамка",
            BrushPresetKind.GridBlock => "Блок", BrushPresetKind.Rectangle => "Мазок", BrushPresetKind.Ellipse => "Овал",
            _ => "Силуэт",
        };

        // No height cap and no scroller of its own: seven tips are meant to be compared at a
        // glance, and the context panel around them already scrolls.
        var list = new ListBox
        {
            Name = "BrushTipList",
            SelectionMode = SelectionMode.Single,
            // This fixed seven-item chooser is already inside the context scroller. A virtual
            // panel can leave its last rows unrealized when the nested scroller is disabled.
            // Four dabs in a row: seven shapes are compared at a glance instead of a column of seven rows.
            ItemsPanel = new FuncTemplate<Panel?>(() => new Avalonia.Controls.Primitives.UniformGrid { Columns = 4 }),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(list, "Форма отпечатка кисти");

        var rows = new List<ListBoxItem>(tips.Length);
        foreach (var tip in tips)
        {
            var row = new ListBoxItem
            {
                Padding = new Thickness(2, 5), Margin = new Thickness(0, 0, 3, 3), HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new StackPanel
                {
                    Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new Border
                        {
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Child = new BrushTipPreview(
                                EditorViewModel.PreviewTip(tip.Kind),
                                EditorViewModel.PreviewSoftness(tip.Kind)),
                        },
                        new TextBlock { Text = Short(tip.Kind), FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
                Tag = tip.Kind,
            }.Named("BrushTip" + tip.Kind);
            AutomationProperties.SetName(row, tip.Name + ". " + tip.Purpose);
            ToolTip.SetTip(row, tip.Name + ": " + tip.Purpose);
            rows.Add(row);
        }

        list.ItemsSource = rows;
        list.SelectionChanged += (_, _) =>
        {
            if (_syncingAppearance || list.SelectedItem is not ListBoxItem { Tag: BrushPresetKind kind })
            {
                return;
            }

            _editor.ApplyBrushPreset(kind);
            SyncAppearanceSections();
        };

        _appearanceSyncs.Add(() =>
        {
            var active = _editor.ActiveBrushPreset;
            var wanted = rows.FirstOrDefault(row => row.Tag is BrushPresetKind kind && kind == active);
            if (!ReferenceEquals(list.SelectedItem, wanted))
            {
                list.SelectedItem = wanted;
            }
        });

        return list;
    }

    private Control BuildMaskPanel()
    {
        // Two named states instead of one "toggle" button: the user sees which mode is active
        // without having to press it and watch what happens.
        var (modes, buttons) = Ui.Segmented("MaskMode", [("Hide", "Скрывать", null), ("Reveal", "Возвращать", null)],
            key =>
            {
                _editor.MaskMode = key == "Hide" ? MaskPaintMode.Hide : MaskPaintMode.Reveal;
                SyncMaskModeButtons();
            });
        _maskModeButtons.AddRange(buttons);

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Explanation("Маска прячет участки слоя, не удаляя их."),
                BuildRasterToolReadiness("Mask"),
                Ui.Section("Режим штриха", modes),
                Ui.SliderRow("MaskSizeSlider", "Размер кисти", 1, 256, _editor.BrushSettings.Radius, "px",
                    value => _editor.BrushSettings = _editor.BrushSettings with { Radius = value }),
                Ui.SliderRow("MaskOpacitySlider", "Непрозрачность", 5, 100, _editor.BrushSettings.Opacity * 100, "%",
                    value => _editor.BrushSettings = _editor.BrushSettings with { Opacity = value / 100.0 }, 5),
                Ui.SliderRow("MaskSoftnessSlider", "Мягкость", 0, 100, _editor.BrushSettings.Softness * 100, "%",
                    value => _editor.BrushSettings = _editor.BrushSettings with { Softness = value / 100.0 }, 5),
                Ui.Section("Форма", new Ui.ChoiceGrid
                {
                    MaxColumns = 2,
                    Children =
                    {
                        Ui.TextButton("MaskRoundButton", "Круг", () => _editor.ApplyBrushPreset(BrushPresetKind.RoundSoft), "segment"),
                        Ui.TextButton("MaskRectangleButton", "Прямоугольник", () => _editor.ApplyBrushPreset(BrushPresetKind.Rectangle), "segment"),
                        Ui.TextButton("MaskEllipseButton", "Эллипс", () => _editor.ApplyBrushPreset(BrushPresetKind.Ellipse), "segment"),
                        Ui.TextButton("MaskPolygonButton", "Многоугольник", () => _editor.ApplyBrushPreset(BrushPresetKind.Polygon), "segment"),
                    },
                }),
                AppearanceToggle("MaskPressureSize", "Давление → размер",
                    () => _editor.BrushSettings.PressureAffectsSize,
                    value => _editor.BrushSettings = _editor.BrushSettings with { PressureAffectsSize = value }),
                AppearanceToggle("MaskPressureOpacity", "Давление → непрозрачность",
                    () => _editor.BrushSettings.PressureAffectsOpacity,
                    value => _editor.BrushSettings = _editor.BrushSettings with { PressureAffectsOpacity = value }),
            },
        };
    }

    private Control BuildLandPanel()
    {
        var (actions, actionButtons) = Ui.Segmented(
            "LandAction", [("Add", "Добавить", null), ("Subtract", "Убрать", null)],
            key =>
            {
                _editor.LandSubtract = key == "Subtract";
                SyncLandButtons();
            });
        _landActionButtons.AddRange(actionButtons);

        var (shapes, shapeButtons) = Ui.IconSegmented("Land", [
            ("CircleButton", "Круг", MapIcons.ShapeCircle),
            ("RectangleButton", "Прямоугольник", MapIcons.ShapeRectangle),
            ("EllipseButton", "Эллипс", MapIcons.ShapeEllipse),
            ("PolygonButton", "Многоугольник", MapIcons.ShapePolygon),
            ("GridButton", "Блок по сетке", MapIcons.ShapeGridBlock),
            ("FillButton", "Заливка области", MapIcons.ShapeFill),
        ], key =>
        {
            _editor.ActiveLandShape = LandShapeByKey(key);
            SyncLandButtons();
        });

        for (var index = 0; index < shapeButtons.Count; index++)
        {
            _landShapeButtons[shapeButtons[index].Name!] = shapeButtons[index];
        }

        var x = NumberBox("LandRegionXBox"); x.Text = "0";
        var y = NumberBox("LandRegionYBox"); y.Text = "0";
        var width = NumberBox("LandRegionWidthBox"); width.Text = "512";
        var height = NumberBox("LandRegionHeightBox"); height.Text = "512";
        var dx = NumberBox("LandMoveDeltaXBox"); dx.Text = "0";
        var dy = NumberBox("LandMoveDeltaYBox"); dy.Text = "0";
        var snap = new CheckBox
        {
            Name = "LandMoveSnapToggle",
            Content = "Привязать delta к сетке",
            IsChecked = true,
        };

        bool ReadBounds(out WorldBounds bounds)
        {
            bounds = default;
            if (!TryWholeNumber(x.Text, out var left) || !TryWholeNumber(y.Text, out var top)
                || !TryWholeNumber(width.Text, out var w) || !TryWholeNumber(height.Text, out var h)
                || w <= 0 || h <= 0) return false;
            bounds = new WorldBounds(left, top, left + w, top + h);
            return true;
        }

        _landLayerBlock = BuildLandLayerBlock();
        _landStyleMissing = Ui.EmptyState(
            "LandStyleMissingState",
            MapIcons.Layers,
            "Слой земли не выбран",
            "Выберите слой земли в списке слоёв, чтобы задать цвет, материал и берег.");

        // Three tabs (audit 2026-10-19): draw the shape or paint it, dress the selected land, and the
        // rare exact-area tools. «Форма земли» needs no header of its own inside its tab.
        var drawPage = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                BuildRasterToolReadiness("Land"),
                BuildLandModePicker(),
                // The land tool carries the same two categories the river tool does, in the same
                // order and the same colours: make the shape first, dress it afterwards.
                (_landShapeControls = Ui.Category("LandCreateCategory", "Форма земли", "accent-create", new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        HeroButton("NewLandButton", "Новая земля", "Новый слой суши — затем протяните по холсту",
                            MapIcons.SolidLand, () =>
                            {
                                _editor.AddLandLayer();
                                _editor.ActiveTool = EditorTool.Land;
                                SyncSidePanels();
                                SyncContextArea();
                            }),
                        // The ground is chosen in sight, before the land exists: the next new
                        // land starts with it, and the active land takes it at once.
                        Ui.Subgroup("Текстура земли", BuildFillTexturePicker("NewLandTexturePicker", MapFillTextureKind.Ground,
                            () => _editor.ActiveLayer is LandLayer land ? land.EdgeStyle.FillTextureId : _editor.NewLandTextureId,
                            id => _editor.ChooseLandTexture(id),
                            () => MaterialPreviewContext.RawTexture(768),
                            "Для новой земли и выбранного слоя суши")),
                        LandShapeHint(),
                        Ui.Subgroup("Действие", actions),
                        Ui.Subgroup("Форма", shapes),
                        AppearanceSlider("LandSizeSlider", "Размер кисти", 1, 256, "px", () => _editor.BrushSettings.Radius,
                            (value, _) => _editor.BrushSettings = _editor.BrushSettings with { Radius = value }),
                        AppearanceSlider("LandRoughnessSlider", "Неровность края", 0, 256, "px",
                            () => _editor.LandRoughness,
                            (value, _) => _editor.LandRoughness = value, 8),
                        AppearanceSlider("LandSmoothingSlider", "Сглаживание", 0, 100, "%",
                            () => _editor.BrushSettings.Softness * 100,
                            (value, _) => _editor.BrushSettings = _editor.BrushSettings with { Softness = value / 100 }, 5),
                    },
                })),
                (_landPaintControls = BuildLandPaintPanel()),
                (_landCliffMaskControls = BuildLandCliffMaskPanel()),
                (_landReliefControls = BuildLandReliefPanel()),
                BuildContinentPanel(),
            },
        };
        _landShapeControls!.Classes.Add("tabbed");
        var stylePage = Ui.Category("LandStyleCategory", "Оформление", "accent-look", new StackPanel
        {
            Spacing = 4, Children = { _landLayerBlock, _landStyleMissing },
        }, expanded: false);
        var morePage = (_landShapeAdvanced = Ui.Advanced("LandAdvanced", new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = "Точная область" }.Classed("role-panel"),
                        Ui.PropertyRow("X", x), Ui.PropertyRow("Y", y),
                        Ui.PropertyRow("Ширина", width), Ui.PropertyRow("Высота", height),
                        Ui.TextButton("LandApplyShapeButton", "Применить выбранную форму", () =>
                        {
                            if (ReadBounds(out var region))
                                _editor.ApplyLandShape(_editor.ActiveLandShape, region);
                        }, "compact"),
                        new TextBlock { Text = "Перемещение" }.Classed("role-panel"),
                        Ui.PropertyRow("Сдвиг X", dx), Ui.PropertyRow("Сдвиг Y", dy), snap,
                        Ui.TextButton("LandMoveButton", "Сдвинуть выбранную область", () =>
                        {
                            if (ReadBounds(out var region) && TryWholeNumber(dx.Text, out var moveX)
                                && TryWholeNumber(dy.Text, out var moveY))
                                _editor.MoveActiveLandArea(
                                    region, (int)Math.Round(moveX), (int)Math.Round(moveY),
                                    snap.IsChecked == true);
                        }, "compact"),
                        Ui.AsyncButton("LandClearButton", "Очистить слой…", ClearActiveLandAsync,
                            "destructive", "compact"),
                    },
                }));
        return Ui.CategoryTabs("LandTabs",
            new Ui.CategoryTab("Draw", "Рисовать", MapIcons.LineBrush, drawPage, "Новая земля, форма кисти и раскраска суши материалами"),
            new Ui.CategoryTab("Style", "Оформление", MapIcons.LinePalette, stylePage, "Цвет и материал выбранной суши, контур, прибой, тени и обрывы"),
            new Ui.CategoryTab("More", "Ещё", MapIcons.LineGear, morePage, "Точная область, сдвиг и очистка слоя"));
    }

    /// <summary>
    /// Runs an explicit "add one object" command and hands the user to the Select tool, where the
    /// new object's properties are already on screen. Drawing on the canvas is unaffected.
    /// </summary>
    private void Create(Action command)
    {
        command();
        if (_editor.Session.SelectedObject is not null)
        {
            _editor.ActiveTool = EditorTool.Select;
        }

        SyncContextArea();
    }

    /// <summary>One line on the brush shapes; the whole explanation is its tooltip.</summary>
    private static Control LandShapeHint()
    {
        var hint = Ui.Hint("Круг рисует от руки, остальные формы — областью.");
        ToolTip.SetTip(hint, "Круг рисует сушу от руки; любая другая форма превращает протягивание в область. "
            + "Неровность — размер «ячеек» берега: 0 даёт ровный круг, 64 — изрезанный берег. "
            + "Сглаживание скругляет их углы; для ровного круга это мягкость края.");
        return hint;
    }

    private static LandShapeKind LandShapeByKey(string key) => key switch
    {
        "CircleButton" => LandShapeKind.Circle,
        "RectangleButton" => LandShapeKind.Rectangle,
        "EllipseButton" => LandShapeKind.Ellipse,
        "PolygonButton" => LandShapeKind.Polygon,
        "GridButton" => LandShapeKind.GridBlock,
        _ => LandShapeKind.BoundedFill,
    };

    private async System.Threading.Tasks.Task ClearActiveLandAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (await ConfirmationWindow.ShowAsync(
                owner, "Очистить слой земли?",
                "Вся суша активного слоя земли будет очищена одной командой. Отмена (Ctrl+Z) вернёт её.",
                "Очистить", "ClearLandConfirmButton", destructive: true))
            _editor.ClearActiveLand();
    }

    private static double WholeNumber(double value)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }

    private static bool TryWholeNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// The stamp tool's context panel, in the order a map maker reads it: the asset (name,
    /// picture, variants, recents and favourites), then how it will be placed (scale, rotation,
    /// level, flips, opacity, shadow, blend, blur, mask), then the placement mode and the rare
    /// options. The picture opens the catalog; the catalog is the only other place to go.
    /// </summary>
    private StackPanel BuildStampToolPanel()
    {
        var current = new TextBlock { Name = "StampToolCurrentText", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center };
        var area = new TextBlock { Name = "StampToolAreaText", TextWrapping = TextWrapping.Wrap }
            .Classed("faint", "role-caption");

        _stampToolCurrentText = current;
        _stampToolAreaText = area;
        _stampGestureBlock = BuildStampGestureBlock();

        // "К базовым" is a quiet header command, not the loudest button of the panel: it restores
        // the base placement settings (same command as before) and shows how many differ now.
        _stampResetCaption = new TextBlock { Text = "К базовым", VerticalAlignment = VerticalAlignment.Center };
        _stampResetButton = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children =
                { MapIcons.Grid24(MapIcons.LineReset, 14), _stampResetCaption } },
        }.Named("ResetStampToolSettingsButton").Classed("quiet");
        _stampResetButton.Click += (_, _) =>
        {
            _editor.ResetStampToolSettings();
            SyncContextArea();
            SyncStampGestureReadout();
            _canvas.ShowPlacementPreviewAtCenter();
            _canvas.Focus();
        };
        AutomationProperties.SetName(_stampResetButton, "Отменить до базовых настроек");
        ToolTip.SetTip(_stampResetButton, "Отменить до базовых настроек: вернуть исходные параметры размещения текущего ассета. "
            + "Черновик размещения отменяется, выбранное изображение и завершённые объекты карты сохраняются.");

        var catalog = BuildCompactStampCatalog();
        _stampHowSection = new Ui.SectionPanel("StampPlacementSection", "Как ставить",
            new StackPanel { Spacing = 6, Children = { _stampGestureBlock, _stampAutoSelectRow } }, true, Ui.Scope.Tool);
        var future = BuildFutureStampProperties();
        _stampRecentSection = new Ui.SectionPanel("StampRecentSection", "Недавние и избранное", _stampStripsHost, false);
        _stampRecentSection.SetSummary("недавние, ★");

        var openCatalog = Ui.CommandButton("StampEmptyOpenCatalogButton", "Открыть каталог ассетов (F)", MapIcons.Assets,
            () => OpenAssetCatalog(focusSearch: true), "accent", "compact");
        openCatalog.HorizontalAlignment = HorizontalAlignment.Stretch;
        _stampEmptyState = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.EmptyState("StampEmptyState", MapIcons.ObjectStamp, "Ассет не выбран",
                    "Выберите дерево, гору или здание в каталоге — затем щёлкните по карте, чтобы поставить."),
                openCatalog,
            },
        };
        _stampCatalogHost = new Border { Padding = new Thickness(0, 0, 0, 10), Child = catalog };
        return new StackPanel
        {
            Spacing = 0,
            Children =
            {
                _stampEmptyState,
                _stampCatalogHost,
                _stampHowSection,
                BuildEditableAreaPanel(),
                BuildEditablePathPanel(),
                future,
                _stampRandomSection,
                _stampRecentSection,
                BuildRegionalModes(),
                _stampPlacementOptions,
            },
        };
    }

    /// <summary>
    /// The properties of the stamp about to be placed, flat and in one order: size, turn, level,
    /// mirror, opacity, shadow, blend, blur, mask. Colour tweaks unfold from the palette toggle
    /// in the asset header. Every control keeps its automation name from the old layout.
    /// </summary>
    private Control BuildFutureStampProperties()
    {
        const string prefix = "FutureStamp";
        StampStyle? Style() => _editor.StampPanelStyle;

        _randomRotationToggle = new ToggleButton { Content = MapIcons.Stroked(MapIcons.Rotate, 14), Width = 26, Height = 26, MinHeight = 26,
            Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0) }.Named("RandomRotationToggle").Classed("segment");
        _randomRotationToggle.Click += (_, _) =>
        {
            _editor.StampRandomAngle = !_editor.StampRandomAngle;
            SyncAssetList();
        };
        var rotationTrailing = new StackPanel { Orientation = Orientation.Horizontal, Children =
        {
            ResetGlyph(prefix + "RotationResetButton", "Сбросить поворот", () => _editor.SetStampRotationDegrees(0)),
            _randomRotationToggle,
        } };

        var transform = new StackPanel { Spacing = 2 };
        transform.Children.Add(AppearanceSlider(prefix + "ScaleSlider", "Масштаб", 10, 400, "%",
            () => _editor.StampScalePercent,
            (value, commit) => _editor.SetStampScalePercent(value, commit), 5,
            ResetGlyph(prefix + "ScaleResetButton", "Сбросить масштаб к 100 %",
                () => _editor.SetStampScalePercent(100))));
        transform.Children.Add(AppearanceSlider(prefix + "RotationSlider", "Поворот", 0, 359, "°",
            () => _editor.StampRotationDegrees,
            (value, commit) => _editor.SetStampRotationDegrees(value, commit), 1, rotationTrailing));
        transform.Children.Add(AppearanceSlider(prefix + "SublayerSlider", "Уровень",
            EditorViewModel.MinimumSublayer, EditorViewModel.MaximumSublayer, "",
            () => _editor.StampSublayer,
            (value, commit) => _editor.SetStampSublayer((int)Math.Round(value), commit)));
        var flips = new Ui.ChoiceGrid
        {
            MaxColumns = 2, MinimumItemWidth = 96, Gap = 4,
            Children =
            {
                AppearanceToggle(prefix + "FlipHorizontalToggle", "Отразить ↔",
                    () => Style()?.FlipHorizontal, value => _editor.SetStampFlip(horizontal: value)),
                AppearanceToggle(prefix + "FlipVerticalToggle", "Отразить ↕",
                    () => Style()?.FlipVertical, value => _editor.SetStampFlip(vertical: value)),
            },
        };
        transform.Children.Add(flips);
        transform.Children.Add(AppearanceSlider(prefix + "OpacitySlider", "Непрозрачность", 0, 100, "%",
            () => _editor.StampOpacityPercent,
            (value, commit) => _editor.SetStampOpacityPercent(value, commit), 5));
        // Blend sits with size, turn, level and opacity: the four plus blend were the assets strip over the map,
        // which is gone (DEC-070), so none of them may end up deeper than it was.
        transform.Children.Add(BlendChoice(prefix + "BlendChoice", () => Style()?.Blend, _editor.SetStampBlend));

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
        var shadowDetails = Disclosure(prefix + "ShadowDetails", "Настройка тени", new StackPanel
        {
            Spacing = 4,
            Children =
            {
                shadowColor,
                ShadowBlock(prefix,
                    () => Style() is { } s
                        ? (s.ShadowOffsetX, s.ShadowOffsetY, s.ShadowBlur, s.ShadowOpacity)
                        : null,
                    _editor.SetStampShadowNone, _editor.SetStampShadowStandard,
                    (x, y, b, o, c) => _editor.SetStampShadow(x, y, b, o, c)),
            },
        }, false);

        var looks = new StackPanel { Spacing = 4 };
        looks.Children.Add(shadowState);
        looks.Children.Add(shadowDetails);
        looks.Children.Add(AppearanceSlider(prefix + "BlurSlider", "Размытие", 0, 32, "px",
            () => Style()?.Blur, (v, c) => _editor.SetStampBlur(v, c)));
        looks.Children.Add(Disclosure(prefix + "Geometry", "Маска", BuildStampClipMaskGroup(prefix), false));

        _stampColorsBlock = new StackPanel
        {
            Spacing = 4, IsVisible = false,
            Children =
            {
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
                Ui.TextButton(prefix + "ResetAppearanceButton", "Сбросить весь внешний вид",
                    _editor.ResetStampAppearance, "compact"),
            },
        }.Named(prefix + "Appearance");
        AutomationProperties.SetName(_stampColorsBlock, "Цвета");

        _stampNextSection = new Ui.SectionPanel("StampNextAssetSection", "Новый ассет", transform, true, Ui.Scope.New);
        _stampLookSection = new Ui.SectionPanel("FutureStampLooks", "Внешний вид",
            new StackPanel { Spacing = 4, Children = { looks, _stampColorsBlock } }, false);
        return new StackPanel { Spacing = 0, Children = { _stampNextSection, _stampLookSection } };
    }

    /// <summary>
    /// The readiness line of a raster tool plus the one action that fixes it.
    /// <para>
    /// Brush, Land and Mask need a surface of their own kind, and a new map has none. That
    /// refusal used to live only in the status bar, so the tool simply looked broken: the pointer
    /// moved and nothing happened. The panel now states which layer the strokes go to, or what is
    /// missing, and offers to create it without leaving the tool.
    /// </para>
    /// </summary>
    private Control BuildRasterToolReadiness(string prefix)
    {
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap }
            .Classed("faint", "role-caption").Named(prefix + "TargetHintText");

        var fix = Ui.TextButton(prefix + "CreateTargetButton", "", () =>
        {
            _editor.CreateLayerForActiveTool();
            SyncSidePanels();
            SyncAppearanceSections();
        }, "compact");

        _appearanceSyncs.Add(() =>
        {
            hint.Text = _editor.RasterToolHint;
            Ui.SetClass(hint, "danger", !_editor.RasterToolReady);
            if (fix.Content is TextBlock caption) caption.Text = _editor.RasterToolFixLabel;
            fix.IsVisible = !_editor.RasterToolReady && _editor.RasterToolFixLabel.Length > 0;
            AutomationProperties.SetName(fix, _editor.RasterToolFixLabel);
        });

        return new StackPanel { Spacing = 4, Children = { hint, fix } };
    }

    private static Control Explanation(string text) => new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
    }.Classed("faint", "role-caption");

    /// <summary>
    /// The big call to action of a creating tool: a framed, lit, full-width command with a
    /// solid glyph, a serif title and a one-line hint, so "where do I start" is never a question.
    /// </summary>
    private static Button HeroButton(string name, string title, string hint, MapIcons.SolidGlyph glyph, Action action)
    {
        var caption = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold }.Classed("hero-title"),
                new TextBlock { Text = hint, FontSize = 11.5, TextWrapping = TextWrapping.Wrap }.Classed("hero-hint"),
            },
        };
        var button = new Button
        {
            Name = name,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            // A grid, not a horizontal stack: the hint gets the remaining width and wraps.
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 14,
                Children = { MapIcons.Solid24(glyph, 30), caption },
            },
        };
        Grid.SetColumn(caption, 1);
        button.Classes.Add("create-hero");
        AutomationProperties.SetName(button, title);
        ToolTip.SetTip(button, title + " — " + hint);
        button.Click += (_, _) => action();
        return Tactile.Attach(button, ornaments: false);
    }
}
