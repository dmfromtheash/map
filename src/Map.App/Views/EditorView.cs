using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Map.App.Controls;
using Map.App.Services;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// The editor screen. Its spatial grammar is fixed and never rearranges under the user: global
/// commands on top, tools in one vertical rail, exactly one context area for the active tool or
/// the current selection, the canvas in the middle, structure on the right, state at the bottom.
///
/// Built in code so every control carries an explicit UIA name. The view only reads the
/// view-model and forwards intent; it never owns document state.
/// </summary>
public sealed class EditorView : UserControl
{
    private readonly EditorViewModel _editor;
    private readonly MapCanvasControl _canvas = new();

    // ------------------------------------------------------------ status line
    private readonly TextBlock _saveState = Status("SaveStateText");
    private readonly TextBlock _statusDetail = Status("StatusDetailText");
    private readonly TextBlock _viewportStatus = Status("ViewportStatusText");
    private readonly TextBlock _selectionStatus = Status("SelectionStatusText");
    private readonly TextBlock _activeLayerStatus = Status("ActiveLayerText");
    private readonly TextBlock _gridStatus = Status("GridStatusText");
    private readonly TextBlock _hintText = new() { Name = "ToolHintText", VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _hintKeys = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 5,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // -------------------------------------------------------------- inspector
    private readonly TextBlock _inspectorError = Status("InspectorErrorText");
    private readonly TextBox _xBox = NumberBox("XBox");
    private readonly TextBox _yBox = NumberBox("YBox");
    private readonly TextBox _widthBox = NumberBox("WidthBox");
    private readonly TextBox _heightBox = NumberBox("HeightBox");
    private readonly TextBox _rotationBox = NumberBox("RotationBox");

    // ------------------------------------------------------------- side lists
    private readonly StackPanel _layerList = new() { Spacing = 2 };
    private readonly StackPanel _objectList = new() { Spacing = 2 };
    private readonly StackPanel _assetList = new() { Spacing = 2 };
    private readonly StackPanel _noteList = new() { Spacing = 2 };
    private readonly TextBox _layerName = new() { Name = "LayerNameBox", PlaceholderText = "Имя слоя" };
    private readonly TextBox _objectSearch = new() { Name = "ObjectSearchBox", PlaceholderText = "Найти объект" };
    private readonly TextBox _assetSearch = new() { Name = "AssetSearchBox", PlaceholderText = "Найти ассет" };
    private readonly TextBox _noteSearch = new() { Name = "NoteSearchBox", PlaceholderText = "Найти заметку" };
    private readonly TextBox _noteTitle = new() { Name = "NoteTitleBox", PlaceholderText = "Заголовок заметки" };
    private readonly TextBox _noteMarkdown = new()
    {
        Name = "NoteMarkdownBox",
        PlaceholderText = "Текст заметки. Разметка Markdown, без HTML.",
        AcceptsReturn = true,
        MinHeight = 96,
        TextWrapping = TextWrapping.Wrap,
    };

    // ------------------------------------------------------------ context area
    private readonly TextBlock _contextTitle =
        new TextBlock { Name = "ContextPanelTitle" }.Classed("role-panel");
    private readonly ContentControl _contextHost = new() { Name = "ContextPanelHost" };
    private readonly Dictionary<EditorTool, Control> _toolPanels = [];
    private readonly Dictionary<EditorTool, Button> _toolButtons = [];
    private readonly Dictionary<string, Button> _landShapeButtons = [];
    private readonly List<Button> _landActionButtons = [];
    private readonly List<Button> _maskModeButtons = [];

    // ------------------------------------------------------ selection sections
    // Every reusable block below lives in exactly one container for the lifetime of the view.
    // Rebuilding containers on each sync would re-parent a control that already has a parent,
    // which Avalonia rejects outright - so the containers are built once and only swapped.
    private Control _inspectorPanel = null!;
    private Control _selectionEmpty = null!;
    private Control _stampSection = null!;
    private Control _vectorSection = null!;
    private Control _textSection = null!;
    private Control _compositionSection = null!;
    private ContentControl _kindSlot = null!;
    private Control _selectionRoot = null!;
    private Control _selectIdleRoot = null!;

    // ------------------------------------------------------- layer type blocks
    private Control _layerCommonBlock = null!;
    private Control _landLayerBlock = null!;
    private Control _gridLayerBlock = null!;
    private Control _filterLayerBlock = null!;
    private Control _layerIdentity = null!;
    private Control _noLayerState = null!;
    private readonly TextBlock _layerNameHeading = new() { Name = "ActiveLayerNameText" };
    private readonly TextBlock _layerKindText = new() { Name = "ActiveLayerKindText" };
    private Avalonia.Controls.Shapes.Path _layerKindIcon = null!;
    private Button _blendValue = null!;
    private Button _maskValue = null!;
    private Button _visibilityValue = null!;
    private Button _lockValue = null!;
    private Button _landEdgeValue = null!;
    private Button _gridCellsValue = null!;
    private Button _gridExportValue = null!;
    private Button _filterPresetValue = null!;

    private NoteId? _selectedNoteId;
    private AssetRecord? _selectedAsset;
    private readonly TextBlock _assetSelectionText = new() { Name = "SelectedAssetText" };
    private readonly List<Button> _assetSelectionCommands = [];
    private Button _objectVisibilityValue = null!;
    private Button _objectLockValue = null!;
    private Button _eraserButton = null!;
    private Button _undoButton = null!;
    private Button _redoButton = null!;

    public EditorView(
        EditorViewModel editor, Func<System.Threading.Tasks.Task> requestBack, Action requestSecondWindow)
    {
        _editor = editor;
        Name = "EditorView";

        var topBar = BuildTopBar(requestBack, requestSecondWindow);
        var rail = BuildToolRail();
        var contextPanel = BuildContextPanel();
        var sidePanel = BuildSidePanel();
        var hintBar = BuildHintBar();
        var statusBar = BuildStatusBar();

        Content = new DockPanel
        {
            Children =
            {
                topBar.Docked(Dock.Top),
                statusBar.Docked(Dock.Bottom),
                hintBar.Docked(Dock.Bottom),
                rail.Docked(Dock.Left),
                contextPanel.Docked(Dock.Left),
                sidePanel.Docked(Dock.Right),

                // The canvas is the fill child: panels resize it, never cover it.
                new Border { Child = _canvas }.Classed("canvas-surround"),
            },
        };

        _canvas.Attach(_editor);
        _canvas.ViewportChanged += (_, _) => _viewportStatus.Text = _editor.ViewportStatus;
        _editor.PropertyChanged += OnEditorChanged;
        _editor.SceneChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            SyncStatus();
            SyncSidePanels();
            SyncContextArea();
        });

        _objectSearch.TextChanged += (_, _) => SyncObjectList();
        _assetSearch.TextChanged += (_, _) => SyncAssetList();
        _noteSearch.TextChanged += (_, _) => SyncNoteList();

        SyncStatus();
        SyncInspector();
        SyncSidePanels();
        SyncContextArea();

        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(_canvas.FitToCanvas, DispatcherPriority.Loaded);
    }

    public EditorViewModel Editor => _editor;

    public MapCanvasControl Canvas => _canvas;

    // ======================================================================= top

    private Control BuildTopBar(Func<System.Threading.Tasks.Task> requestBack, Action requestSecondWindow)
    {
        var back = Ui.AsyncButton("BackButton", "Библиотека", requestBack, "ghost");
        back.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Children =
            {
                MapIcons.Stroked(MapIcons.Back, 16),
                new TextBlock { Text = "Библиотека", VerticalAlignment = VerticalAlignment.Center },
            },
        };
        ToolTip.SetTip(back, "Вернуться в библиотеку карт");

        var title = Ui.Trimmed(_editor.Title, "role-section").Named("DocumentTitleText");
        title.MaxWidth = 340;
        title.Margin = new Thickness(14, 0, 8, 0);

        _undoButton = Ui.IconButton("UndoButton", "Отменить  (Ctrl+Z)", MapIcons.Undo, _editor.Undo);
        _redoButton = Ui.IconButton("RedoButton", "Повторить  (Ctrl+Y)", MapIcons.Redo, _editor.Redo);
        var fit = Ui.IconButton("FitButton", "Вписать карту в окно", MapIcons.FitCanvas, _canvas.FitToCanvas);
        var second = Ui.IconButton("SecondWindowButton", "Открыть второе окно", MapIcons.Window, requestSecondWindow);

        var saveAs = Ui.AsyncButton("SaveAsButton", "Сохранить как…", () => _editor.SaveAsync(saveAs: true));
        var save = Ui.AsyncButton("SaveButton", "Сохранить", () => _editor.SaveAsync(), "primary");
        ToolTip.SetTip(save, "Сохранить проект  (Ctrl+S)");

        var bar = new DockPanel
        {
            Margin = new Thickness(8, 6),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { second, saveAs, save },
                }.Docked(Dock.Right),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Margin = new Thickness(16, 0, 0, 0),
                    Children = { _undoButton, _redoButton, Divider(), fit },
                }.Docked(Dock.Right),
                back.Docked(Dock.Left),
                title,
            },
        };

        return new Border { Child = bar }.Classed("app-bar");
    }

    private static Control Divider() => new Border
    {
        Width = 1,
        Margin = new Thickness(5, 6),
        Background = Application.Current?.FindResource("MapBorderBrush") as IBrush,
    };

    // ====================================================================== rail

    private Control BuildToolRail()
    {
        Button Tool(string name, EditorTool tool, string icon)
        {
            var button = Ui.ToolButton(
                name,
                EditorLabels.Tool(tool),
                icon,
                EditorLabels.ToolShortcut(tool),
                () => _editor.ActiveTool = tool);
            _toolButtons[tool] = button;
            return button;
        }

        var stack = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(6, 8),
            Children =
            {
                Tool("SelectToolButton", EditorTool.Select, MapIcons.Select),
                RailDivider(),
                Tool("LineToolButton", EditorTool.Line, MapIcons.Line),
                Tool("ShapeToolButton", EditorTool.Shape, MapIcons.Shape),
                Tool("TextToolButton", EditorTool.Text, MapIcons.Text),
                RailDivider(),
                Tool("BrushToolButton", EditorTool.Brush, MapIcons.Brush),
                Tool("MaskToolButton", EditorTool.Mask, MapIcons.Mask),
                Tool("LandToolButton", EditorTool.Land, MapIcons.Land),
            },
        };

        return new Border { Width = Ui.RailWidth, Child = stack }.Classed("rail");
    }

    private static Control RailDivider() => new Border
    {
        Height = 1,
        Margin = new Thickness(8, 5),
        Background = Application.Current?.FindResource("MapBorderBrush") as IBrush,
    };

    // ============================================================ context panel

    private Control BuildContextPanel()
    {
        BuildToolPanels();
        BuildSelectionSections();
        BuildContextRoots();

        var header = new Border
        {
            Child = new DockPanel { Children = { _contextTitle } },
        }.Classed("panel-header");

        var body = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border { Padding = new Thickness(12, 10, 12, 16), Child = _contextHost },
        };

        return new Border
        {
            Width = Ui.ContextPanelWidth,
            Child = new DockPanel { Children = { header.Docked(Dock.Top), body } },
        }.Classed("context-panel").Named("ContextPanel");
    }

    private void BuildToolPanels()
    {
        _toolPanels[EditorTool.Select] = BuildSelectPanel();
        _toolPanels[EditorTool.Line] = BuildLinePanel();
        _toolPanels[EditorTool.Shape] = BuildShapePanel();
        _toolPanels[EditorTool.Text] = BuildTextPanel();
        _toolPanels[EditorTool.Brush] = BuildBrushPanel();
        _toolPanels[EditorTool.Mask] = BuildMaskPanel();
        _toolPanels[EditorTool.Land] = BuildLandPanel();
    }

    // No explanation here: this panel is only ever shown beneath the "Ничего не выбрано" state,
    // which already says how to make a selection. Saying it twice is noise, not guidance.
    private Control BuildSelectPanel() => new StackPanel
    {
        Spacing = 8,
        Children =
        {
            Ui.CommandButton("AddRectangleButton", "Добавить прямоугольник", MapIcons.ObjectRectangle,
                () => Create(() => _editor.AddObject(SceneObjectKind.Rectangle))),
            Ui.CommandButton("AddEllipseButton", "Добавить эллипс", MapIcons.ObjectEllipse,
                () => Create(() => _editor.AddObject(SceneObjectKind.Ellipse))),
        },
    };

    private Control BuildLinePanel() => new StackPanel
    {
        Spacing = 10,
        Children =
        {
            Explanation("Протяните по холсту, чтобы нарисовать линию, или добавьте прямую кнопкой ниже. " +
                        "Узлы и порталы редактируются после выделения линии."),
            Ui.CommandButton("AddLineButton", "Добавить линию", MapIcons.Line, () => Create(_editor.AddLine)),
        },
    };

    private Control BuildShapePanel() => new StackPanel
    {
        Spacing = 10,
        Children =
        {
            Explanation("Добавьте фигуру, затем правьте её контур в свойствах выделения. " +
                        "Две фигуры можно объединить или вычесть."),
            Ui.CommandButton("AddVectorRectangleButton", "Добавить прямоугольник", MapIcons.ShapeRectangle,
                () => Create(() => _editor.AddShape(ShapePrimitiveKind.Rectangle))),
            Ui.CommandButton("AddShapeEllipseButton", "Добавить эллипс", MapIcons.ShapeEllipse,
                () => Create(() => _editor.AddShape(ShapePrimitiveKind.Ellipse))),
        },
    };

    private Control BuildTextPanel() => new StackPanel
    {
        Spacing = 10,
        Children =
        {
            Explanation("Добавьте подпись на карту. Начертание меняется в свойствах выделенной подписи."),
            Ui.CommandButton("AddTextButton", "Добавить подпись", MapIcons.Text, () => Create(_editor.AddText)),
        },
    };

    private Control BuildBrushPanel()
    {
        _eraserButton = Ui.CommandButton("EraserButton", "Ластик", MapIcons.Eraser, () =>
        {
            _editor.BrushSettings = _editor.BrushSettings with { Eraser = !_editor.BrushSettings.Eraser };
            Ui.SetClass(_eraserButton, "active", _editor.BrushSettings.Eraser);
            ToolTip.SetTip(_eraserButton, _editor.BrushSettings.Eraser
                ? "Ластик включён: штрих стирает"
                : "Ластик выключен: штрих рисует");
        }, "segment");
        _eraserButton.HorizontalAlignment = HorizontalAlignment.Left;

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Explanation("Рисуйте по холсту. Штрих ложится на активный слой."),
                Ui.SliderRow("BrushSizeSlider", "Размер кисти", 1, 256, _editor.BrushSettings.Radius, "px",
                    value => _editor.BrushSettings = _editor.BrushSettings with { Radius = value }),
                Ui.SliderRow("BrushOpacitySlider", "Непрозрачность", 5, 100, _editor.BrushSettings.Opacity * 100, "%",
                    value => _editor.BrushSettings = _editor.BrushSettings with { Opacity = value / 100.0 }, 5),
                _eraserButton,
                Ui.Advanced("BrushAdvanced", new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Ui.SliderRow("BrushSoftnessSlider", "Мягкость края", 0, 100,
                            _editor.BrushSettings.Softness * 100, "%",
                            value => _editor.BrushSettings = _editor.BrushSettings with { Softness = value / 100.0 }, 5),
                        Ui.SliderRow("BrushSpacingSlider", "Шаг отпечатков", 5, 100,
                            _editor.BrushSettings.Spacing * 100, "%",
                            value => _editor.BrushSettings = _editor.BrushSettings with { Spacing = value / 100.0 }, 5),
                    },
                }),
            },
        };
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
                Explanation("Маска прячет участки активного слоя, не удаляя их. " +
                            "Маску слоя можно включить на вкладке «Слои»."),
                Ui.Section("Режим штриха", modes),
                Ui.SliderRow("MaskSizeSlider", "Размер кисти", 1, 256, _editor.BrushSettings.Radius, "px",
                    value => _editor.BrushSettings = _editor.BrushSettings with { Radius = value }),
                Ui.SliderRow("MaskOpacitySlider", "Непрозрачность", 5, 100, _editor.BrushSettings.Opacity * 100, "%",
                    value => _editor.BrushSettings = _editor.BrushSettings with { Opacity = value / 100.0 }, 5),
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
        ], ApplyLandShapeByKey);

        for (var index = 0; index < shapeButtons.Count; index++)
        {
            _landShapeButtons[shapeButtons[index].Name!] = shapeButtons[index];
        }

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Explanation("Протяните по холсту, чтобы нарисовать сушу, или примените готовую форму."),
                Ui.Section("Действие", actions),
                Ui.Section("Форма", shapes),
                Ui.SliderRow("LandSizeSlider", "Размер кисти", 1, 256, _editor.BrushSettings.Radius, "px",
                    value => _editor.BrushSettings = _editor.BrushSettings with { Radius = value }),
                Ui.Advanced("LandAdvanced", new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        // The edge style belongs to the land layer, so it lives once, on the
                        // layer's property sheet. Duplicating it here would create two controls
                        // for one setting - exactly the pattern the UX rules forbid.
                        Ui.TextButton("LandMoveButton", "Сдвинуть область вправо",
                            () => _editor.MoveActiveLandArea(128, 0), "compact"),
                    },
                }),
            },
        };
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

    private void ApplyLandShapeByKey(string key) => _editor.ApplyLandShape(key switch
    {
        "CircleButton" => LandShapeKind.Circle,
        "RectangleButton" => LandShapeKind.Rectangle,
        "EllipseButton" => LandShapeKind.Ellipse,
        "PolygonButton" => LandShapeKind.Polygon,
        "GridButton" => LandShapeKind.GridBlock,
        _ => LandShapeKind.BoundedFill,
    });

    private static Control Explanation(string text) => new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
    }.Classed("faint", "role-caption");

    // ========================================================= selection panels

    private void BuildSelectionSections()
    {
        _selectionEmpty = Ui.EmptyState(
            "SelectionEmptyState",
            MapIcons.Select,
            "Ничего не выбрано",
            "Щёлкните объект на холсте или выберите его в списке «Объекты».");

        _inspectorPanel = BuildInspector();

        _stampSection = Ui.Section("Штамп", new StackPanel
        {
            Spacing = 5,
            Children =
            {
                Ui.TextButton("StampFlipButton", "Отразить по горизонтали",
                    () => _editor.UpdateSelectedStamp(toggleFlip: true), "compact"),
                Ui.TextButton("StampShadowButton", "Тень включить/выключить",
                    () => _editor.UpdateSelectedStamp(toggleShadow: true), "compact"),
                Ui.TextButton("StampHueButton", "Подкрасить",
                    () => _editor.UpdateSelectedStamp(hue: 35, saturation: 0.2), "compact"),
            },
        }).Named("StampObjectActions");

        _vectorSection = Ui.Section("Геометрия", new StackPanel
        {
            Spacing = 5,
            Children =
            {
                Ui.TextButton("AddLineNodeButton", "Добавить узел", _editor.AddNodeToSelectedLine, "compact"),
                Ui.TextButton("MoveVectorNodeButton", "Сдвинуть узел", _editor.MoveSelectedVectorNode, "compact"),
                Ui.TextButton("CurveNodeButton", "Узел: прямая или кривая",
                    _editor.ToggleSelectedNodeCurve, "compact"),
                Ui.TextButton("CutLineButton", "Разрезать линию", _editor.CutSelectedLine, "compact"),
                Ui.TextButton("VectorStrokeButton", "Сменить стиль обводки",
                    _editor.CycleSelectedVectorStroke, "compact"),
                Ui.Advanced("VectorAdvanced", new StackPanel
                {
                    Spacing = 5,
                    Children =
                    {
                        Ui.TextButton("AddPortalButton", "Добавить проход", _editor.AddPortalToSelected, "compact"),
                        Ui.TextButton("MovePortalButton", "Сдвинуть проход", _editor.MoveSelectedPortal, "compact"),
                        Ui.TextButton("DeletePortalButton", "Удалить проход",
                            _editor.DeleteSelectedPortal, "compact"),
                        Ui.TextButton("BooleanAddButton", "Объединить с предыдущей",
                            () => _editor.ApplyShapeBoolean(ShapeBooleanOperation.Add), "compact"),
                        Ui.TextButton("BooleanSubtractButton", "Вычесть из предыдущей",
                            () => _editor.ApplyShapeBoolean(ShapeBooleanOperation.Subtract), "compact"),
                        Ui.TextButton("BooleanMergeButton", "Слить контуры",
                            () => _editor.ApplyShapeBoolean(ShapeBooleanOperation.Merge), "compact"),
                        Ui.TextButton("GridSnapNodeButton", "Привязать узел к сетке",
                            _editor.SnapSelectedNodeToGrid, "compact"),
                        Ui.TextButton("GridSnapPortalButton", "Привязать проход к сетке",
                            _editor.SnapSelectedPortalToGrid, "compact"),
                    },
                }),
            },
        }).Named("VectorObjectActions");

        var textChildren = new StackPanel
        {
            Spacing = 5,
            Children = { Ui.TextButton("TextStyleButton", "Сменить начертание", _editor.CycleSelectedTextStyle, "compact") },
        };

        // A font-substitution probe is a test fixture, not a user command: it appears only when the
        // process is started in explicit test mode by the UIA driver.
        if (TestMode.Enabled)
        {
            textChildren.Children.Add(Ui.TextButton(
                "TextMissingFontButton", "Проверить замену шрифта (тест)",
                _editor.UseMissingFontOnSelectedText, "compact"));
        }

        _textSection = Ui.Section("Подпись", textChildren).Named("TextObjectActions");

        _compositionSection = Ui.Section("Композиция", new StackPanel
        {
            Spacing = 5,
            Children =
            {
                Ui.TextButton("CopyCompositionButton", "Копировать композицию",
                    _editor.CopySelectedComposition, "compact"),
                Ui.TextButton("FlattenCompositionButton", "Растеризовать композицию",
                    _editor.FlattenSelectedComposition, "compact"),
            },
        }).Named("CompositionObjectActions");
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

        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Ui.PropertyRow("X", _xBox, "px"),
                Ui.PropertyRow("Y", _yBox, "px"),
                Ui.PropertyRow("Ширина", _widthBox, "px"),
                Ui.PropertyRow("Высота", _heightBox, "px"),
                Ui.PropertyRow("Поворот", _rotationBox, "°"),
                apply,
                _inspectorError,
            },
        };
    }

    /// <summary>
    /// What every selection can do, whatever its kind. State is shown as a value, not hidden
    /// behind an "or" verb, so the user reads the object's condition before changing it.
    /// </summary>
    private Control BuildSelectionCommon()
    {
        var (visibilityRow, visibilityButton) = Ui.ValueRow(
            "ObjectVisibilityButton", "Видимость", "Показан", _editor.ToggleSelectedObjectVisibility);
        _objectVisibilityValue = visibilityButton;

        var (lockRow, lockButton) = Ui.ValueRow(
            "ObjectLockButton", "Блокировка", "Свободен", _editor.ToggleSelectedObjectLock);
        _objectLockValue = lockButton;

        var delete = Ui.CommandButton("DeleteObjectButton", "Удалить объект", MapIcons.Trash,
            _editor.DeleteSelectedObject, "destructive", "compact", "centred");
        delete.Margin = new Thickness(0, 6, 0, 0);
        ToolTip.SetTip(delete, "Удалить выделенный объект. Действие можно отменить (Ctrl+Z).");

        return Ui.Section("Объект", new StackPanel
        {
            Spacing = 2,
            Children =
            {
                visibilityRow,
                lockRow,
                Ui.TextButton("GridSnapObjectButton", "Привязать к сетке",
                    _editor.SnapSelectedObjectToGrid, "compact"),
                delete,
            },
        }).Named("CommonObjectActions");
    }

    // ================================================================ side panel

    private Control BuildSidePanel()
    {
        var tabs = new TabControl
        {
            Name = "EditorSideTabs",
            ItemsSource = new[]
            {
                new TabItem { Header = "Слои", Content = BuildLayersTab() },
                new TabItem { Header = "Объекты", Content = BuildObjectsTab() },
                new TabItem { Header = "Ассеты", Content = BuildAssetsTab() },
                new TabItem { Header = "Заметки", Content = BuildNotesTab() },
            },
        };

        return new Border { Width = Ui.SidePanelWidth, Child = tabs }.Classed("panel");
    }

    private Control BuildLayersTab()
    {
        Button AddLayer(string name, string title, string icon, Action action)
        {
            var button = new Button { Content = MapIcons.Stroked(icon, 16) }.Named(name).Classed("segment");
            AutomationProperties.SetName(button, "Добавить слой: " + title);
            ToolTip.SetTip(button, "Добавить слой: " + title);
            button.Height = 32;
            button.Click += (_, _) => action();
            return button;
        }

        var addGrid = new UniformGrid
        {
            Columns = 5,
            Children =
            {
                AddLayer("AddObjectLayerButton", "объекты", MapIcons.LayerObjects, _editor.AddObjectLayer),
                AddLayer("AddBrushLayerButton", "кисть", MapIcons.LayerBrush, _editor.AddBrushLayer),
                AddLayer("AddLandLayerButton", "земля", MapIcons.LayerLand, _editor.AddLandLayer),
                AddLayer("AddGridLayerButton", "сетка", MapIcons.LayerGrid, _editor.AddGridLayer),
                AddLayer("AddFilterLayerButton", "фильтр", MapIcons.LayerFilter, _editor.AddFilterLayer),
            },
        };

        var body = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 18),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Слои карты" }.Classed("role-panel"),
                _layerList,
                new Border
                {
                    Margin = new Thickness(0, 2, 0, 0),
                    Child = new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = "Добавить слой" }.Classed("role-panel"),
                            new Border
                            {
                                Background = Application.Current?.FindResource("MapFieldBrush") as IBrush,
                                BorderBrush = Application.Current?.FindResource("MapBorderBrush") as IBrush,
                                BorderThickness = new Thickness(1),
                                CornerRadius = new CornerRadius(7),
                                Padding = new Thickness(2),
                                Child = addGrid,
                            },
                        },
                    },
                }.Classed("group"),
                BuildLayerProperties(),
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
        _layerKindIcon = MapIcons.Stroked(
            MapIcons.Layers, 18, Application.Current?.FindResource("MapAccentBrush") as IBrush);

        _layerNameHeading.Classes.Add("role-section");
        _layerNameHeading.TextTrimming = TextTrimming.CharacterEllipsis;
        _layerKindText.Classes.Add("role-caption");
        _layerKindText.Classes.Add("faint");

        _layerIdentity = new Border
        {
            Padding = new Thickness(10, 8),
            Background = Application.Current?.FindResource("MapElevatedBrush") as IBrush,
            CornerRadius = new CornerRadius(7),
            Child = new DockPanel
            {
                Children =
                {
                    new Border
                    {
                        Width = 24,
                        VerticalAlignment = VerticalAlignment.Center,
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
        _landLayerBlock = BuildLandLayerBlock();
        _gridLayerBlock = BuildGridLayerBlock();
        _filterLayerBlock = BuildFilterLayerBlock();

        return new Border
        {
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Свойства слоя" }.Classed("role-panel"),
                    _noLayerState,
                    _layerIdentity,
                    _layerCommonBlock,
                    _landLayerBlock,
                    _gridLayerBlock,
                    _filterLayerBlock,
                },
            },
        }.Classed("group");
    }

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

        var nameRow = new DockPanel
        {
            Margin = new Thickness(0, 2),
            Children =
            {
                rename.Docked(Dock.Right),
                new TextBlock
                {
                    Text = "Имя",
                    Width = 92,
                    VerticalAlignment = VerticalAlignment.Center,
                }.Classed("muted").Docked(Dock.Left),
                _layerName,
            },
        };

        var (visibilityRow, visibilityButton) = Ui.ValueRow(
            "LayerVisibilityButton", "Видимость", "Показан", _editor.ToggleActiveLayerVisibility);
        _visibilityValue = visibilityButton;

        var (lockRow, lockButton) = Ui.ValueRow(
            "LayerLockButton", "Блокировка", "Свободен", _editor.ToggleActiveLayerLock);
        _lockValue = lockButton;

        var (blendRow, blendButton) = Ui.ValueRow(
            "LayerBlendButton", "Наложение", "Обычное", _editor.CycleActiveLayerBlend);
        _blendValue = blendButton;

        var (maskRow, maskButton) = Ui.ValueRow(
            "LayerMaskButton", "Маска", "Нет", _editor.ToggleActiveMask);
        _maskValue = maskButton;

        var orderRow = new DockPanel
        {
            Margin = new Thickness(0, 2),
            Children =
            {
                new TextBlock
                {
                    Text = "Порядок",
                    Width = 92,
                    VerticalAlignment = VerticalAlignment.Center,
                }.Classed("muted").Docked(Dock.Left),
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
            _editor.DeleteActiveLayer, "destructive");
        delete.HorizontalAlignment = HorizontalAlignment.Stretch;
        delete.HorizontalContentAlignment = HorizontalAlignment.Center;
        delete.Margin = new Thickness(0, 6, 0, 0);
        ToolTip.SetTip(delete, "Удалить слой вместе с содержимым. Действие можно отменить (Ctrl+Z).");

        return new StackPanel
        {
            Spacing = 2,
            Children = { nameRow, visibilityRow, lockRow, blendRow, maskRow, orderRow, delete },
        };
    }

    private Control BuildLandLayerBlock()
    {
        var (row, button) = Ui.ValueRow(
            "LandStyleButton", "Берег", "Обрыв", _editor.CycleActiveLandStyle);
        _landEdgeValue = button;
        return Ui.Section("Земля", row).Named("LandLayerBlock");
    }

    private Control BuildGridLayerBlock()
    {
        var (cellsRow, cellsButton) = Ui.ValueRow(
            "GridGeometryButton", "Ячейка", "Квадратная", _editor.CycleActiveGridGeometry);
        _gridCellsValue = cellsButton;

        var (exportRow, exportButton) = Ui.ValueRow(
            "GridExportButton", "В экспорт", "Нет", _editor.ToggleActiveGridExport);
        _gridExportValue = exportButton;

        return Ui.Section("Сетка", new StackPanel
        {
            Spacing = 2,
            Children = { cellsRow, exportRow },
        }).Named("GridLayerBlock");
    }

    private Control BuildFilterLayerBlock()
    {
        var (row, button) = Ui.ValueRow(
            "FilterStyleButton", "Пресет", "Тёплый", _editor.CycleActiveFilter);
        _filterPresetValue = button;
        return Ui.Section("Фильтр", row).Named("FilterLayerBlock");
    }


    private Control BuildObjectsTab()
    {
        var body = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 18),
            Spacing = 10,
            Children =
            {
                _objectSearch,
                _objectList,
                new Border
                {
                    Child = new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = "Композиции" }.Classed("role-panel"),
                            Ui.TextButton("MakeCompositionButton", "Собрать из выделенного",
                                () => _editor.CreateEditableComposition(), "compact"),
                            Ui.TextButton("PasteCompositionButton", "Вставить композицию",
                                _editor.PasteComposition, "compact"),
                        },
                    },
                }.Classed("group"),
            },
        };

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = body,
        };
    }

    private Control BuildAssetsTab()
    {
        _assetSelectionText.Classes.Add("role-caption");
        _assetSelectionText.Classes.Add("faint");
        _assetSelectionText.TextWrapping = TextWrapping.Wrap;

        var body = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 18),
            Spacing = 10,
            Children =
            {
                new DockPanel
                {
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 2,
                            Children =
                            {
                                Ui.IconButton("ImportAssetButton", "Импортировать изображение в проект",
                                    MapIcons.Plus, () => _ = ImportAssetAsync(linked: false)),
                                Ui.IconButton("LinkAssetButton", "Связать с файлом на диске",
                                    MapIcons.Open, () => _ = ImportAssetAsync(linked: true)),
                            },
                        }.Docked(Dock.Right),
                        new TextBlock
                        {
                            Text = "Каталог ассетов",
                            VerticalAlignment = VerticalAlignment.Center,
                        }.Classed("role-panel"),
                    },
                },
                _assetSearch,
                _assetList,
                new Border
                {
                    Child = new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = "Выбранный ассет" }.Classed("role-panel"),
                            _assetSelectionText,
                            AssetCommand("PlaceStampButton", "Разместить на карте", () =>
                            {
                                if (_selectedAsset is not null) _editor.PlaceStamp(_selectedAsset, embedded: true);
                            }, "compact"),
                            AssetCommand("PlaceLinkedStampButton", "Разместить как связанный", () =>
                            {
                                if (_selectedAsset is not null) _editor.PlaceStamp(_selectedAsset, embedded: false);
                            }, "compact"),
                            Ui.Advanced("AssetAdvanced", new StackPanel
                            {
                                Spacing = 5,
                                Children =
                                {
                                    AssetCommand("ScatterStampButton", "Рассыпать по области", () =>
                                    {
                                        if (_selectedAsset is not null) _editor.ScatterStamp(_selectedAsset, 100);
                                    }, "compact"),
                                    AssetCommand("ReplaceStampAssetButton", "Заменить у выделенного штампа", () =>
                                    {
                                        if (_selectedAsset is not null)
                                            _editor.ReplaceSelectedStampAsset(_selectedAsset);
                                    }, "compact"),
                                    Ui.TextButton("KeepStampPlaceholderButton", "Оставить заменитель",
                                        _editor.KeepSelectedStampPlaceholder, "compact"),
                                    AssetCommand("UseAssetStrokeButton", "Использовать для обводки", () =>
                                    {
                                        if (_selectedAsset is not null)
                                            _editor.UseAssetAsSelectedVectorStroke(_selectedAsset);
                                    }, "compact"),
                                    AssetCommand("UseAssetFilterButton", "Использовать в фильтре", () =>
                                    {
                                        if (_selectedAsset is not null)
                                            _editor.UseAssetAsActiveFilterTexture(_selectedAsset);
                                    }, "compact"),
                                },
                            }),
                        },
                    },
                }.Classed("group"),
                new TextBlock
                {
                    Text = "Связанный файл сверяется по контрольной сумме: подмена или пропажа видна на карте.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
            },
        };

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = body,
        };
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
        var body = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 18),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Заметки видны только вам: на карту и в экспорт они не попадают.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
                _noteSearch,
                _noteList,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        Ui.TextButton("AddWorldNoteButton", "В центре экрана",
                            _editor.AddNoteAtViewCentre, "compact"),
                        Ui.TextButton("AddObjectNoteButton", "К выделенному",
                            _editor.AddNoteForSelection, "compact"),
                    },
                },
                new Border
                {
                    Child = new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = "Выбранная заметка" }.Classed("role-panel"),
                            _noteTitle,
                            _noteMarkdown,
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 6,
                                Children =
                                {
                                    Ui.TextButton("SaveNoteButton", "Сохранить", () =>
                                    {
                                        if (_selectedNoteId is { } id)
                                            _editor.EditNote(id, _noteTitle.Text ?? "", _noteMarkdown.Text ?? "");
                                    }, "compact"),
                                    Ui.TextButton("GoToNoteButton", "Перейти", () =>
                                    {
                                        if (_selectedNoteId is { } id) _editor.GoToNote(id);
                                    }, "compact"),
                                    Ui.TextButton("DeleteNoteButton", "Удалить", () =>
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
                    },
                }.Classed("group"),
            },
        };

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = body,
        };
    }

    // ================================================================ hint + status

    private Control BuildHintBar()
    {
        _hintText.Classes.Add("role-caption");
        _hintText.Classes.Add("muted");
        _hintText.TextTrimming = TextTrimming.CharacterEllipsis;

        return new Border
        {
            Background = Application.Current?.FindResource("MapElevatedBrush") as IBrush,
            Padding = new Thickness(14, 6),
            Child = new DockPanel
            {
                Children = { _hintKeys.Docked(Dock.Right), _hintText },
            },
        }.Named("ToolHintBar");
    }

    private Control BuildStatusBar()
    {
        foreach (var block in new[] { _selectionStatus, _activeLayerStatus, _gridStatus, _viewportStatus })
        {
            block.Classes.Add("role-caption");
            block.Classes.Add("muted");
            block.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        _saveState.Classes.Add("role-caption");
        _statusDetail.Classes.Add("role-caption");
        _statusDetail.Classes.Add("danger");
        _statusDetail.TextTrimming = TextTrimming.CharacterEllipsis;
        _statusDetail.MaxWidth = 420;

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _saveState, StatusDot(), _activeLayerStatus, StatusDot(), _selectionStatus,
                StatusDot(), _gridStatus,
            },
        };

        var bar = new DockPanel
        {
            Margin = new Thickness(12, 5),
            Children =
            {
                _viewportStatus.Docked(Dock.Right),
                _statusDetail.Docked(Dock.Right),
                left,
            },
        };

        return new Border { Child = bar }.Classed("status-bar");
    }

    private static Control StatusDot() => new TextBlock
    {
        Text = "·",
        VerticalAlignment = VerticalAlignment.Center,
    }.Classed("faint");

    // ==================================================================== sync

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            SyncStatus();

            if (e.PropertyName is nameof(EditorViewModel.XText) or nameof(EditorViewModel.YText)
                or nameof(EditorViewModel.WidthText) or nameof(EditorViewModel.HeightText)
                or nameof(EditorViewModel.RotationText) or nameof(EditorViewModel.HasEditableSelection))
            {
                SyncInspector();
            }

            if (e.PropertyName is nameof(EditorViewModel.ActiveTool)
                or nameof(EditorViewModel.SelectionKind)
                or nameof(EditorViewModel.ActiveLayer))
            {
                SyncContextArea();
            }
        });

    private void SyncStatus()
    {
        _saveState.Text = _editor.SaveSummary;
        Ui.SetClass(_saveState, "danger", _editor.SaveState == SaveState.SaveFailed);
        Ui.SetClass(_saveState, "success", _editor.SaveState == SaveState.Saved);
        Ui.SetClass(_saveState, "warn", _editor.SaveState == SaveState.Unsaved);

        _statusDetail.Text = _editor.StatusDetail;
        _statusDetail.IsVisible = _editor.StatusDetail.Length > 0;
        if (_editor.StatusDetail.Length > 0)
        {
            ToolTip.SetTip(_statusDetail, _editor.StatusDetail);
        }

        _selectionStatus.Text = _editor.SelectionStatus;
        ToolTip.SetTip(_selectionStatus, _editor.SelectionStatus);
        _activeLayerStatus.Text = _editor.ActiveLayerSummary;
        ToolTip.SetTip(_activeLayerStatus, _editor.ActiveLayerSummary);
        _gridStatus.Text = _editor.GridSummary;
        _viewportStatus.Text = _editor.ViewportStatus;
        _inspectorError.Text = _editor.InspectorError;
        _inspectorError.IsVisible = _editor.InspectorError.Length > 0;

        _undoButton.IsEnabled = _editor.CanUndo;
        _redoButton.IsEnabled = _editor.CanRedo;
    }

    private void SyncInspector()
    {
        _xBox.Text = _editor.XText;
        _yBox.Text = _editor.YText;
        _widthBox.Text = _editor.WidthText;
        _heightBox.Text = _editor.HeightText;
        _rotationBox.Text = _editor.RotationText;

        var editable = _editor.HasEditableSelection;
        foreach (var box in new[] { _xBox, _yBox, _widthBox, _heightBox, _rotationBox })
        {
            box.IsEnabled = editable;
        }
    }

    /// <summary>
    /// Decides what the single context area shows. Exactly one of two things: the settings of the
    /// active tool, or the properties of the current selection - never both, and never the
    /// properties of a kind that is not selected.
    /// </summary>
    private void SyncContextArea()
    {
        foreach (var pair in _toolButtons)
        {
            Ui.SetToolActive(pair.Value, pair.Key == _editor.ActiveTool);
        }

        SyncLandButtons();
        SyncMaskModeButtons();
        SyncLayerBlocks();
        SyncHint();

        // Selection properties belong to the Select tool. While a drawing tool is active the panel
        // keeps showing that tool, so switching tools always has a predictable, visible effect.
        if (_editor.ActiveTool == EditorTool.Select && _editor.SelectionKind is { } kind)
        {
            _contextTitle.Text = "Свойства: " + EditorLabels.ObjectKind(kind);

            if (_editor.Session.SelectedObject is { } selected)
            {
                Ui.SetValue(_objectVisibilityValue, "Видимость",
                    EditorLabels.ObjectVisibilityValue(selected.Visible));
                Ui.SetValue(_objectLockValue, "Блокировка",
                    EditorLabels.ObjectLockValue(selected.Locked));
            }

            _kindSlot.Content = KindSection(kind);
            _contextHost.Content = _selectionRoot;
            return;
        }

        _contextTitle.Text = "Инструмент: " + EditorLabels.Tool(_editor.ActiveTool);
        _kindSlot.Content = null;
        _contextHost.Content = _editor.ActiveTool == EditorTool.Select
            ? _selectIdleRoot
            : _toolPanels[_editor.ActiveTool];
    }

    /// <summary>
    /// The only place that decides which kind-specific block is shown. Exactly one block can be
    /// mounted at a time because they all share a single slot.
    /// </summary>
    private Control? KindSection(SceneObjectKind kind) => kind switch
    {
        SceneObjectKind.Stamp or SceneObjectKind.Placeholder => _stampSection,
        SceneObjectKind.Line or SceneObjectKind.Shape => _vectorSection,
        SceneObjectKind.Text => _textSection,
        SceneObjectKind.Composition => _compositionSection,
        _ => null,
    };

    private void BuildContextRoots()
    {
        _kindSlot = new ContentControl { Name = "SelectionKindSlot" };

        _selectionRoot = new StackPanel
        {
            Spacing = 12,
            Children = { _inspectorPanel, BuildSelectionCommon(), _kindSlot },
        };

        _selectIdleRoot = new StackPanel
        {
            Spacing = 12,
            Children = { _selectionEmpty, _toolPanels[EditorTool.Select] },
        };
    }

    private void SyncHint()
    {
        _hintText.Text = EditorLabels.ToolHint(_editor.ActiveTool);
        ToolTip.SetTip(_hintText, _hintText.Text);

        _hintKeys.Children.Clear();
        foreach (var pair in new (string Label, string Key)[]
                 {
                     ("Отменить", "Ctrl+Z"),
                     ("Сохранить", "Ctrl+S"),
                     (EditorLabels.Tool(_editor.ActiveTool), EditorLabels.ToolShortcut(_editor.ActiveTool)),
                 })
        {
            if (pair.Key.Length == 0)
            {
                continue;
            }

            _hintKeys.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children =
                {
                    new TextBlock { Text = pair.Label, VerticalAlignment = VerticalAlignment.Center }
                        .Classed("faint", "role-caption"),
                    Ui.KeyChip(pair.Key),
                },
            });
        }
    }

    private void SyncLandButtons()
    {
        for (var index = 0; index < _landActionButtons.Count; index++)
        {
            Ui.SetClass(_landActionButtons[index], "active", (index == 1) == _editor.LandSubtract);
        }
    }

    private void SyncMaskModeButtons()
    {
        for (var index = 0; index < _maskModeButtons.Count; index++)
        {
            Ui.SetClass(_maskModeButtons[index], "active", (index == 1) == (_editor.MaskMode == MaskPaintMode.Reveal));
        }
    }

    /// <summary>
    /// Publishes the active layer into its property sheet. Every value control is refreshed with
    /// the value it currently holds, so the panel can never claim a setting it does not have.
    /// </summary>
    private void SyncLayerBlocks()
    {
        var layer = _editor.ActiveLayer;
        var editable = layer is not null and not BaseColorLayer;

        _noLayerState.IsVisible = layer is null;
        _layerIdentity.IsVisible = layer is not null;
        _layerCommonBlock.IsVisible = editable;
        _landLayerBlock.IsVisible = layer is LandLayer;
        _gridLayerBlock.IsVisible = layer is GridLayer;
        _filterLayerBlock.IsVisible = layer is FilterLayer;

        if (layer is null)
        {
            return;
        }

        _layerNameHeading.Text = layer.Name;
        ToolTip.SetTip(_layerNameHeading, layer.Name);
        _layerKindIcon.Data = Geometry.Parse(LayerIcon(layer));

        // Say something the heading does not already say. A layer named "Объекты" of kind
        // "Объекты" gains nothing from a second line repeating the word.
        var kind = EditorLabels.LayerKind(layer);
        var subtitle = layer is BaseColorLayer
            ? "Фон карты · нельзя удалить"
            : string.Equals(kind, layer.Name, StringComparison.OrdinalIgnoreCase)
                ? $"Наложение: {EditorLabels.Blend(layer.Blend)}"
                : kind;
        _layerKindText.Text = subtitle;

        if (!editable)
        {
            return;
        }

        Ui.SetValue(_visibilityValue, "Видимость", layer.Visible ? "Показан" : "Скрыт");
        Ui.SetValue(_lockValue, "Блокировка", layer.Locked ? "Заблокирован" : "Свободен");
        Ui.SetValue(_blendValue, "Наложение", EditorLabels.Blend(layer.Blend));
        Ui.SetValue(_maskValue, "Маска", EditorLabels.MaskState(layer));

        if (layer is LandLayer land)
        {
            Ui.SetValue(_landEdgeValue, "Берег", EditorLabels.LandEdge(land));
        }

        if (layer is GridLayer grid)
        {
            Ui.SetValue(_gridCellsValue, "Ячейка", EditorLabels.GridCells(grid));
            Ui.SetValue(_gridExportValue, "В экспорт", EditorLabels.GridExport(grid));
        }

        if (layer is FilterLayer filter)
        {
            Ui.SetValue(_filterPresetValue, "Пресет", EditorLabels.FilterPreset(filter));
        }
    }

    private void SyncSidePanels()
    {
        _layerList.Children.Clear();
        foreach (var layer in _editor.Layers.Reverse())
        {
            var captured = layer;
            var isBase = layer is BaseColorLayer;

            // The base colour layer cannot be hidden, locked or removed, so it gets no toggles
            // rather than disabled ones that would only invite a pointless click.
            var actions = new List<Button>();
            if (!isBase)
            {
                var visibility = Ui.RowAction(
                    "LayerRowVisibility_" + layer.Id,
                    EditorLabels.VisibilityCommand(layer.Visible),
                    layer.Visible ? MapIcons.Eye : MapIcons.EyeOff,
                    () =>
                    {
                        _editor.SelectLayer(captured.Id);
                        _editor.ToggleActiveLayerVisibility();
                    });
                Ui.SetRowAction(visibility, EditorLabels.VisibilityCommand(layer.Visible), !layer.Visible);

                var locking = Ui.RowAction(
                    "LayerRowLock_" + layer.Id,
                    EditorLabels.LockCommand(layer.Locked),
                    layer.Locked ? MapIcons.Lock : MapIcons.Unlock,
                    () =>
                    {
                        _editor.SelectLayer(captured.Id);
                        _editor.ToggleActiveLayerLock();
                    });
                Ui.SetRowAction(locking, EditorLabels.LockCommand(layer.Locked), layer.Locked);

                actions.Add(visibility);
                actions.Add(locking);
            }

            // The kind is a useful subtitle only when it adds something the name does not say.
            var kind = EditorLabels.LayerKind(layer);
            var secondary = string.Equals(kind, layer.Name, StringComparison.OrdinalIgnoreCase) ? null : kind;

            var tooltip = $"{layer.Name} · {kind} · наложение: {EditorLabels.Blend(layer.Blend)}";
            if (layer.Mask is { } mask)
            {
                tooltip += mask.Enabled ? " · маска включена" : " · маска выключена";
            }

            var row = Ui.StructureRow(
                "Layer_" + layer.Id,
                LayerIcon(layer),
                layer.Name,
                secondary,
                () =>
                {
                    _editor.SelectLayer(captured.Id);
                    _layerName.Text = captured.Name;
                    SyncSidePanels();
                    SyncContextArea();
                },
                tooltip,
                [.. actions]);

            Ui.SetClass(row, "selected", layer.Id == _editor.ActiveLayerId);
            _layerList.Children.Add(row);
        }

        _layerName.Text = _editor.ActiveLayer?.Name ?? "";
        SyncLayerBlocks();
        SyncObjectList();
        SyncAssetList();
        SyncNoteList();
    }

    private static string LayerIcon(MapLayer layer) => layer switch
    {
        BaseColorLayer => MapIcons.LayerBase,
        ObjectLayer => MapIcons.LayerObjects,
        BrushLayer => MapIcons.LayerBrush,
        LandLayer => MapIcons.LayerLand,
        GridLayer => MapIcons.LayerGrid,
        FilterLayer => MapIcons.LayerFilter,
        _ => MapIcons.Layers,
    };

    private void SyncObjectList()
    {
        var query = (_objectSearch.Text ?? "").Trim();
        _objectList.Children.Clear();

        var objects = _editor.Layers.OfType<ObjectLayer>()
            .SelectMany(layer => layer.Objects)
            .Where(item => query.Length == 0
                || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (objects.Count == 0)
        {
            _objectList.Children.Add(Ui.EmptyState(
                "NoObjectsText",
                MapIcons.Objects,
                query.Length == 0 ? "На карте пока нет объектов" : "Ничего не найдено",
                query.Length == 0
                    ? "Добавьте объект инструментом «Линия», «Фигура», «Текст» или из каталога ассетов."
                    : "Измените запрос или очистите поле поиска."));
            return;
        }

        foreach (var sceneObject in objects)
        {
            var states = new List<(string, string)>();
            if (!sceneObject.Visible)
            {
                states.Add((MapIcons.EyeOff, "Объект скрыт"));
            }

            if (sceneObject.Locked)
            {
                states.Add((MapIcons.Lock, "Объект заблокирован"));
            }

            if (sceneObject.Kind == SceneObjectKind.Placeholder)
            {
                states.Add((MapIcons.Missing, "Файл ассета не найден"));
            }

            var captured = sceneObject;
            var row = Ui.ListRow(
                "Object_" + sceneObject.Id,
                ObjectIcon(sceneObject.Kind),
                sceneObject.Name,
                EditorLabels.ObjectSubtitle(sceneObject),
                states,
                () =>
                {
                    _editor.SelectObject(captured.Id);
                    SyncObjectList();
                    SyncContextArea();
                },
                $"{sceneObject.Name} · {EditorLabels.ObjectKind(sceneObject.Kind)}");

            Ui.SetClass(row, "selected", sceneObject.Id == _editor.Session.SelectedObjectId);
            _objectList.Children.Add(row);
        }
    }

    private static string ObjectIcon(SceneObjectKind kind) => kind switch
    {
        SceneObjectKind.Rectangle => MapIcons.ObjectRectangle,
        SceneObjectKind.Ellipse => MapIcons.ObjectEllipse,
        SceneObjectKind.Stamp => MapIcons.ObjectStamp,
        SceneObjectKind.Line => MapIcons.Line,
        SceneObjectKind.Shape => MapIcons.Shape,
        SceneObjectKind.Text => MapIcons.Text,
        SceneObjectKind.Composition => MapIcons.ObjectComposition,
        _ => MapIcons.ObjectPlaceholder,
    };

    private void SyncAssetList()
    {
        var query = (_assetSearch.Text ?? "").Trim();
        _assetList.Children.Clear();

        var assets = _editor.SearchAssets(query).ToList();
        if (assets.Count == 0)
        {
            _assetList.Children.Add(Ui.EmptyState(
                "NoAssetsText",
                MapIcons.Assets,
                query.Length == 0 ? "Каталог пуст" : "Ничего не найдено",
                query.Length == 0
                    ? "Импортируйте PNG, JPEG или WebP, чтобы размещать их на карте."
                    : "Измените запрос или очистите поле поиска."));
        }
        else
        {
            foreach (var asset in assets)
            {
                var states = new List<(string, string)>();
                if (asset.LinkedPath is not null)
                {
                    states.Add((MapIcons.Open, "Связанный файл на диске"));
                }

                var captured = asset;
                var row = Ui.ListRow(
                    "Asset_" + asset.Sha256[..12],
                    MapIcons.Assets,
                    asset.DisplayName,
                    $"{asset.Width}×{asset.Height}",
                    states,
                    () =>
                    {
                        _selectedAsset = captured;
                        SyncAssetList();
                    },
                    asset.LinkedPath is null
                        ? $"{asset.DisplayName} · хранится в проекте"
                        : $"{asset.DisplayName} · связанный файл");

                Ui.SetClass(row, "selected", asset.Sha256 == _selectedAsset?.Sha256);
                _assetList.Children.Add(row);
            }
        }

        _assetSelectionText.Text = _selectedAsset is { } selected
            ? $"{selected.DisplayName} · {selected.Width}×{selected.Height}"
            : "Ассет не выбран. Выберите его в списке выше.";

        foreach (var command in _assetSelectionCommands)
        {
            command.IsEnabled = _selectedAsset is not null;
        }
    }

    private void SyncNoteList()
    {
        var query = (_noteSearch.Text ?? "").Trim();
        _noteList.Children.Clear();

        var notes = _editor.SearchNotes(query).ToList();
        if (notes.Count == 0)
        {
            _noteList.Children.Add(Ui.EmptyState(
                "NoNotesText",
                MapIcons.Notes,
                query.Length == 0 ? "Заметок пока нет" : "Ничего не найдено",
                query.Length == 0
                    ? "Добавьте заметку в центре экрана или привяжите её к выделенному объекту."
                    : "Измените запрос или очистите поле поиска."));
            return;
        }

        foreach (var note in notes)
        {
            var states = new List<(string, string)>();
            if (note.IsDetached(_editor.Session.Document))
            {
                states.Add((MapIcons.Warning, "Объект-якорь удалён"));
            }

            var captured = note;
            var row = Ui.ListRow(
                "Note_" + note.Id,
                MapIcons.Notes,
                note.Title,
                null,
                states,
                () =>
                {
                    _selectedNoteId = captured.Id;
                    _noteTitle.Text = captured.Title;
                    _noteMarkdown.Text = captured.Markdown;
                    SyncNoteList();
                },
                note.Title);

            Ui.SetClass(row, "selected", note.Id == _selectedNoteId);
            _noteList.Children.Add(row);
        }
    }

    // ================================================================== actions

    private void CommitInspector()
    {
        _editor.XText = _xBox.Text ?? "";
        _editor.YText = _yBox.Text ?? "";
        _editor.WidthText = _widthBox.Text ?? "";
        _editor.HeightText = _heightBox.Text ?? "";
        _editor.RotationText = _rotationBox.Text ?? "";
        _editor.CommitInspector();
    }

    private async System.Threading.Tasks.Task ImportAssetAsync(bool linked)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = linked ? "Связать изображение" : "Импортировать изображение",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Изображения") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] },
            ],
        });

        var path = files.Count == 1 ? files[0].TryGetLocalPath() : null;
        if (path is null)
        {
            return;
        }

        _selectedAsset = _editor.ImportAsset(path, linked);
        SyncAssetList();
        SyncStatus();
    }

    // =================================================================== helpers

    private static TextBlock Status(string name) => new()
    {
        Name = name,
        Text = "",
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBox NumberBox(string name) => new()
    {
        Name = name,
        MinWidth = 92,
    };
}
