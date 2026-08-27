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
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
    private readonly TextBlock _autosaveState = Status("AutosaveStatusText");
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
    private readonly TextBox _assetSearch = new()
    {
        Name = "AssetSearchBox",
        PlaceholderText = "Найти в библиотеке",
    };
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
    private TabControl _sideTabs = null!;
    private Border _sidePanel = null!;

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

    // ---------------------------------------------------------------- library
    private readonly ListBox _libraryGrid = new()
    {
        Name = "LibraryGrid",
        MinHeight = 220,
        MaxHeight = 340,
        SelectionMode = SelectionMode.Single,
    };

    private readonly StackPanel _collectionTree = new() { Spacing = 2 };
    // A DockPanel, not a StackPanel: the ancestor segments keep their natural width while the
    // trailing segment takes what is left and ellipsises, so the trail can never run underneath
    // the import buttons docked beside it.
    private readonly DockPanel _breadcrumbs = new()
    {
        Name = "LibraryBreadcrumbs",
        LastChildFill = true,
        Margin = new Thickness(0, 0, 8, 0),
    };

    private readonly StackPanel _variantStrip = new()
    {
        Name = "VariantStrip",
        Orientation = Orientation.Horizontal,
        Spacing = 4,
    };

    private readonly TextBlock _libraryCountText = new() { Name = "LibraryCountText" };
    private readonly TextBlock _selectedSetNameText = new() { Name = "SelectedSetNameText" };
    private readonly TextBlock _variantPositionText = new() { Name = "VariantPositionText" };
    private readonly TextBlock _stampAreaSummaryText = new() { Name = "StampAreaSummaryText" };
    private readonly Border _libraryDiagnostic = new() { Name = "LibraryDiagnosticBanner", IsVisible = false };
    private readonly TextBlock _libraryDiagnosticText = new() { Name = "LibraryDiagnosticText" };
    private Border _setBadge = null!;
    private TextBlock _setBadgeText = null!;
    private ToggleButton _favoriteButton = null!;
    private Button _previousVariantButton = null!;
    private Button _nextVariantButton = null!;
    private readonly Dictionary<string, Button> _librarySortButtons = [];
    private readonly Dictionary<string, Button> _libraryGroupButtons = [];
    private readonly Dictionary<string, Button> _libraryFilterButtons = [];
    private readonly Dictionary<string, Button> _placementModeButtons = [];
    private ToggleButton _randomVariantToggle = null!;
    private ComboBox _libraryTagFilter = null!;
    private ComboBox _libraryMediaFilter = null!;
    private TextBox _libraryImportedFrom = null!;
    private TextBox _libraryImportedTo = null!;
    private Button _clearFiltersButton = null!;
    private TextBox _setRenameBox = null!;
    private Button _setRenameButton = null!;
    private Button _variantEarlierButton = null!;
    private Button _variantLaterButton = null!;
    private Button _setCoverButton = null!;
    private readonly TextBlock _libraryDateHint = new() { Name = "LibraryDateHint" };
    private readonly TextBlock _libraryFilterSummary = new() { Name = "LibraryFilterSummary" };
    private readonly TextBlock _variantStateText = new() { Name = "VariantStateText" };
    private readonly Dictionary<string, List<TileState>> _tileStates = [];
    private Expander _libraryFilterDetails = null!;
    private Expander _libraryCollections = null!;
    private Expander _setManagementDetails = null!;
    private Control _selectedSetBlock = null!;
    private Control _stampGestureBlock = null!;
    private StackPanel _stampAreaControls = null!;
    private (int First, int Count) _reportedLibraryRange = (-1, -1);
    private int _lastSelectedLibraryRow = -1;
    private ToggleButton _randomAngleToggle = null!;
    private ToggleButton _randomFlipToggle = null!;
    private TextBlock _stampToolCurrentText = null!;
    private TextBlock _stampToolAreaText = null!;
    private bool _syncingLibrary;
    private readonly ThumbnailCache _thumbnails = new(96);
    private Button _objectVisibilityValue = null!;
    private Button _objectLockValue = null!;
    private ToggleButton _eraserButton = null!;
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
        _canvas.StampPlaced += (_, _) => RefreshLibraryPanel();
        _canvas.VariantCycled += (_, _) => RefreshLibraryPanel();
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

    public bool IsNotesOpen => _sidePanel.IsVisible && _sideTabs.SelectedIndex == 3;

    public void OpenAssetCatalog(bool focusSearch = false)
    {
        _sidePanel.IsVisible = true;
        _sideTabs.SelectedIndex = 2;
        if (focusSearch)
        {
            Dispatcher.UIThread.Post(() => _assetSearch.Focus(), DispatcherPriority.Input);
        }
    }

    public void OpenNotes(bool focusEditor = false)
    {
        _sidePanel.IsVisible = true;
        _sideTabs.SelectedIndex = 3;
        if (focusEditor)
        {
            Dispatcher.UIThread.Post(
                () => (_selectedNoteId is not null ? _noteTitle : _noteSearch).Focus(),
                DispatcherPriority.Input);
        }
    }

    public void ToggleSidePanel() => _sidePanel.IsVisible = !_sidePanel.IsVisible;

    /// <summary>Re-reads the library panel after a shortcut or a canvas gesture changed it.</summary>
    public void RefreshLibraryPanel()
    {
        SyncAssetList();
        SyncStatus();
    }

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
                Tool("StampToolButton", EditorTool.Stamp, MapIcons.ObjectStamp),
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
        _toolPanels[EditorTool.Stamp] = BuildStampToolPanel();
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
        _eraserButton = new ToggleButton
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children =
                {
                    MapIcons.Stroked(MapIcons.Eraser, 16),
                    new TextBlock { Text = "Ластик", VerticalAlignment = VerticalAlignment.Center },
                },
            },
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

    /// <summary>
    /// The stamp tool's context panel. It states what is current and how to change it; the full
    /// library, the variant strip and the Area/Density controls live in the side panel.
    /// </summary>
    private StackPanel BuildStampToolPanel()
    {
        var current = new TextBlock { Name = "StampToolCurrentText", TextWrapping = TextWrapping.Wrap }
            .Classed("muted");
        var area = new TextBlock { Name = "StampToolAreaText", TextWrapping = TextWrapping.Wrap }
            .Classed("faint", "role-caption");

        _stampToolCurrentText = current;
        _stampToolAreaText = area;

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Explanation(EditorLabels.ToolHint(EditorTool.Stamp)),
                current,
                area,
                Ui.TextButton("OpenLibraryButton", "Открыть библиотеку (F)",
                    () => OpenAssetCatalog(focusSearch: true), "compact"),
                ChipRow(["Alt+←", "Alt+→"], "вариант"),
                ChipRow(["Q", "A"], "область"),
                ChipRow(["Alt+Q", "Alt+A"], "плотность"),
            },
        };
    }

    /// <summary>One key-hint row. Each row holds one meaning so nothing overflows the panel.</summary>
    private static WrapPanel ChipRow(IReadOnlyList<string> keys, string meaning)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var key in keys)
        {
            var chip = Ui.KeyChip(key);
            chip.Margin = new Thickness(0, 2, 4, 2);
            row.Children.Add(chip);
        }

        row.Children.Add(new TextBlock
        {
            Text = meaning,
            Margin = new Thickness(0, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Center,
        }.Classed("faint", "role-caption"));
        return row;
    }

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
        _sideTabs = new TabControl
        {
            Name = "EditorSideTabs",
            ItemsSource = new[]
            {
                new TabItem { Header = "Слои", Content = BuildLayersTab() },
                new TabItem { Header = "Объекты", Content = BuildObjectsTab() },
                new TabItem { Header = "Библиотека", Content = BuildAssetsTab() },
                new TabItem { Header = "Заметки", Content = BuildNotesTab() },
            },
        };

        _sidePanel = new Border { Width = Ui.SidePanelWidth, Child = _sideTabs }.Classed("panel");
        _sidePanel.Name = "EditorSidePanel";
        return _sidePanel;
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
        _libraryCountText.Classes.Add("faint");
        _libraryCountText.Classes.Add("role-caption");
        _libraryCountText.TextWrapping = TextWrapping.Wrap;
        _selectedSetNameText.Classes.Add("muted");
        _selectedSetNameText.TextTrimming = TextTrimming.CharacterEllipsis;
        _variantPositionText.Classes.Add("muted");
        _stampAreaSummaryText.Classes.Add("faint");
        _stampAreaSummaryText.Classes.Add("role-caption");
        _stampAreaSummaryText.TextWrapping = TextWrapping.Wrap;

        BuildLibraryDiagnosticBanner();
        BuildLibraryGrid();

        var importOne = Ui.TextButton(
            "ImportAssetButton", "Добавить файл", () => _ = ImportAssetAsync(linked: false), "compact");
        AutomationProperties.SetName(importOne, "Добавить один ассет в библиотеку");
        ToolTip.SetTip(importOne, "Скопировать PNG, JPEG или WebP в локальную библиотеку");
        var importSet = Ui.TextButton(
            "ImportAssetSetButton", "Добавить набор", () => _ = ImportAssetSetAsync(), "compact");
        AutomationProperties.SetName(importSet, "Добавить набор вариантов в библиотеку");
        ToolTip.SetTip(importSet, "Объединить несколько изображений в один ассет с вариантами");
        var linkFile = Ui.TextButton(
            "LinkAssetButton", "Связать файл", () => _ = ImportAssetAsync(linked: true), "compact");
        AutomationProperties.SetName(linkFile, "Связать ассет с файлом на диске");
        ToolTip.SetTip(linkFile, "Не копировать файл; проверять его по исходному пути");

        _libraryCollections = Disclosure(
            "LibraryCollectionsDetails", "Коллекции", _collectionTree, expanded: false);
        _selectedSetBlock = BuildSelectedSetBlock();
        _stampGestureBlock = BuildStampGestureBlock();

        var body = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 18),
            Spacing = 10,
            Children =
            {
                new DockPanel
                {
                    Children = { _breadcrumbs },
                },
                new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    ItemSpacing = 4,
                    LineSpacing = 4,
                    Children = { importOne, importSet, linkFile },
                },
                _libraryDiagnostic,
                _assetSearch,
                BuildLibraryFilterControls(),
                _libraryCollections,
                new DockPanel { Children = { _libraryCountText } },
                _libraryGrid,
                _selectedSetBlock,
                _stampGestureBlock,
            },
        };

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = body,
        };
    }

    private static Expander Disclosure(string name, string title, Control content, bool expanded)
    {
        var expander = new Expander
        {
            Header = new TextBlock { Text = title }.Classed("role-panel"),
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
    /// One real virtualized list over a flat sequence of group headers and cards. Avalonia
    /// realises only the containers it can show, so grouping ten thousand cards by tag still
    /// costs the handful of rows on screen rather than a second control tree.
    /// </summary>
    private void BuildLibraryGrid()
    {
        AutomationProperties.SetName(_libraryGrid, "Карточки библиотеки");
        _libraryGrid.ItemTemplate = new FuncDataTemplate<AssetLibraryRow>(
            row => row is not null,
            (row, _) => row!.IsHeader ? BuildLibraryGroupHeader(row) : BuildLibraryTile(row.Card),
            // These controls intentionally capture the row while they are built. Reusing one for
            // another row would leave its visible title, thumbnail and badges pointing at the old
            // asset while the ListBox selection already points at the new one.
            supportsRecycling: false);

        _libraryGrid.SelectionChanged += (_, _) =>
        {
            if (_syncingLibrary)
            {
                return;
            }

            var index = _libraryGrid.SelectedIndex;
            if (index < 0)
            {
                return;
            }

            if (!_editor.SelectLibraryRow(index))
            {
                // A group header is a caption, not a card: it never becomes the selected set. The
                // correction is posted rather than applied inline because changing the selection
                // from inside the notification would abort the caller's own selection request -
                // including a screen reader's.
                var step = index >= _lastSelectedLibraryRow ? 1 : -1;
                Dispatcher.UIThread.Post(() => MoveSelectionOffHeader(index, step), DispatcherPriority.Input);
                return;
            }

            _lastSelectedLibraryRow = index;
            SyncAssetList();
            SyncStatus();
        };

        // A Border or panel inside the item template never reaches the accessibility tree, so the
        // realised container itself is what gets named.
        _libraryGrid.ContainerPrepared += (_, e) => NameLibraryContainer(e.Container, e.Index);

        // A recycled container is handed a new row without being "prepared" again. Without this
        // the accessible name of a scrolled or re-filtered row keeps describing the card it used
        // to hold, which is worse than having no name at all.
        _libraryGrid.ContainerIndexChanged += (_, e) => NameLibraryContainer(e.Container, e.NewIndex);

        // The realised range is reported back so the view-model knows what is actually on screen.
        _libraryGrid.LayoutUpdated += (_, _) => ReportVisibleLibraryRange();
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
        _ = CheckVisibleAvailabilityAsync();
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
    /// The whole filter surface. Every control says in words what it does: a row of mute icons
    /// would leave the user unable to tell why the library looks empty, so each toggle carries
    /// its label, and the summary line underneath names everything that is switched on.
    /// </summary>
    private StackPanel BuildLibraryFilterControls()
    {
        var (sort, sortButtons) = Ui.Segmented("LibrarySort", [
            ("Newest", "Новые", null),
            ("Oldest", "Старые", null),
            ("NameAsc", "А–Я", null),
            ("NameDesc", "Я–А", null),
        ], key =>
        {
            _editor.LibrarySort = key switch
            {
                "Oldest" => AssetSortOrder.ImportedOldest,
                "NameAsc" => AssetSortOrder.NameAscending,
                "NameDesc" => AssetSortOrder.NameDescending,
                _ => AssetSortOrder.ImportedNewest,
            };
            SyncAssetList();
        });
        Register(_librarySortButtons, ["Newest", "Oldest", "NameAsc", "NameDesc"], sortButtons);

        var (group, groupButtons) = Ui.Segmented("LibraryGroup", [
            ("None", "Без групп", null),
            ("Collection", "По коллекции", null),
            ("Tag", "По тегу", null),
        ], key =>
        {
            _editor.LibraryGrouping = key switch
            {
                "Collection" => AssetGrouping.Collection,
                "Tag" => AssetGrouping.Tag,
                _ => AssetGrouping.None,
            };
            SyncAssetList();
        });
        Register(_libraryGroupButtons, ["None", "Collection", "Tag"], groupButtons);

        var quickFilters = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 4,
            LineSpacing = 4,
        };
        var advancedFilters = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 4,
            LineSpacing = 4,
        };
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
            if (key == "Untagged")
            {
                advancedFilters.Children.Add(button);
            }
            else
            {
                quickFilters.Children.Add(button);
            }
        }

        _libraryTagFilter = ChoiceBox("LibraryTagFilter", "Тег", "Любой тег", value =>
        {
            _editor.LibraryTag = value;
            SyncAssetList();
        });
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

        _clearFiltersButton = Ui.TextButton("LibraryClearFiltersButton", "Сбросить", () =>
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

        _libraryFilterDetails = Disclosure(
            "LibraryFilterDetails",
            "Фильтры и порядок",
            new StackPanel
            {
                Spacing = 7,
                Children =
                {
                    Ui.Section("Сначала", sort),
                    Ui.Section("Показывать", group),
                    Ui.Section("Дополнительный фильтр", advancedFilters),
                    Ui.PropertyRow("Тег", _libraryTagFilter),
                    Ui.PropertyRow("Формат", _libraryMediaFilter),
                    Ui.PropertyRow("Добавлено с", _libraryImportedFrom),
                    Ui.PropertyRow("Добавлено по", _libraryImportedTo),
                    _libraryDateHint,
                },
            },
            expanded: false);

        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                quickFilters,
                _libraryFilterSummary,
                _clearFiltersButton,
                _libraryFilterDetails,
            },
        };

        static string FilterHint(string key) => key switch
        {
            "Favorites" => "Только избранное",
            "Recent" => "Только недавние",
            "Untagged" => "Только без тегов",
            _ => "Только используемое на карте",
        };

        static void Register(
            Dictionary<string, Button> target, string[] keys, IReadOnlyList<Button> buttons)
        {
            for (var index = 0; index < keys.Length; index++)
            {
                target[keys[index]] = buttons[index];
            }
        }
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
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children =
                {
                    MapIcons.Stroked(icon, 14),
                    new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center },
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
            "PreviousVariantButton", "Предыдущий вариант (Alt+Влево)", MapIcons.Back, () =>
            {
                _editor.PreviousVariant();
                SyncAssetList();
                SyncStatus();
            });

        _nextVariantButton = Ui.IconButton(
            "NextVariantButton", "Следующий вариант (Alt+Вправо)", MapIcons.ChevronRight, () =>
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
                    new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        MaxHeight = 64,
                        Content = _variantStrip,
                    },
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
        var (mode, modeButtons) = Ui.Segmented("StampMode", [
            ("One", "Один штамп", null),
            ("Area", "Россыпь", null),
        ], key =>
        {
            _editor.StampPlacementMode = key == "Area" ? StampPlacementMode.Area : StampPlacementMode.One;
            SyncAssetList();
        });
        _placementModeButtons["One"] = modeButtons[0];
        _placementModeButtons["Area"] = modeButtons[1];

        // Labelled, not icon-only: these three decide whether a scatter multiplies the variant or
        // turns and mirrors every copy, and a user must be able to see which without hovering.
        _randomVariantToggle = LabelledToggle(
            "RandomVariantToggle", "Смешивать варианты", "Случайный вариант после размещения",
            MapIcons.Duplicate, () =>
            {
                _editor.RandomVariantAfterPlacement = !_editor.RandomVariantAfterPlacement;
                SyncAssetList();
            });
        _randomAngleToggle = LabelledToggle(
            "RandomAngleToggle", "Поворачивать", "Случайный угол поворота каждого штампа",
            MapIcons.Redo, () =>
            {
                _editor.StampRandomAngle = !_editor.StampRandomAngle;
                SyncAssetList();
            });
        _randomFlipToggle = LabelledToggle(
            "RandomFlipToggle", "Отражать", "Случайное зеркальное отражение штампа",
            MapIcons.Select, () =>
            {
                _editor.StampRandomFlip = !_editor.StampRandomFlip;
                SyncAssetList();
            });

        _stampAreaControls = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Ui.SliderRow("StampAreaSlider", "Радиус", 1, 1024, _editor.StampAreaRadius, "ед.",
                    value =>
                    {
                        _editor.StampAreaRadius = value;
                        SyncStampGestureReadout();
                    }),
                Ui.SliderRow("StampDensitySlider", "Плотность", 0, 400, _editor.StampDensity, "",
                    value =>
                    {
                        _editor.StampDensity = value;
                        SyncStampGestureReadout();
                    }),
                _stampAreaSummaryText,
                Disclosure(
                    "StampScatterDetails",
                    "Разброс и поворот",
                    new WrapPanel
                    {
                        Orientation = Orientation.Horizontal,
                        ItemSpacing = 4,
                        LineSpacing = 4,
                        Children = { _randomAngleToggle, _randomFlipToggle },
                    },
                    expanded: false),
            },
        };

        return new Border
        {
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Как разместить" }.Classed("role-panel"),
                    mode,
                    _stampAreaControls,
                    _randomVariantToggle,
                    AssetCommand("PlaceStampButton", "Разместить в центре карты", () =>
                    {
                        _editor.PlaceCurrentVariantAtViewCentre();
                        RefreshLibraryPanel();
                    }, "compact"),
                    AssetCommand("ReplaceStampVariantButton", "Заменить ассет выделенного штампа", () =>
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
                            AssetCommand("PlaceLinkedStampButton", "Разместить как связанный", () =>
                            {
                                if (_selectedAsset is not null) _editor.PlaceStamp(_selectedAsset, embedded: false);
                            }, "compact"),
                            AssetCommand("ReplaceStampAssetButton", "Заменить ассет у выделенного штампа", () =>
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
        }.Classed("group");
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
        _stampAreaSummaryText.Text = _editor.StampAreaSummary;
        AutomationProperties.SetName(_stampAreaSummaryText, _editor.StampAreaSummary);
        SyncStampToolPanel();
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
            : $"{set.Name} · вариант {_editor.CurrentVariantLabel}";
        _stampToolAreaText.Text = _editor.StampAreaSummary;
        AutomationProperties.SetName(_stampToolCurrentText, _stampToolCurrentText.Text);
        AutomationProperties.SetName(_stampToolAreaText, _stampToolAreaText.Text);
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
    /// A tiny bounded LRU of decoded covers. Without the bound, scrolling a large library would
    /// hold every thumbnail it ever showed.
    /// </summary>
    private sealed class ThumbnailCache(int capacity)
    {
        private readonly Dictionary<string, (Bitmap Image, long Used)> _entries = new(StringComparer.OrdinalIgnoreCase);
        private long _clock;

        public Bitmap? Image(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            if (_entries.TryGetValue(path, out var hit))
            {
                _entries[path] = (hit.Image, ++_clock);
                return hit.Image;
            }

            if (!File.Exists(path))
            {
                return null;
            }

            Bitmap bitmap;
            try
            {
                bitmap = new Bitmap(path);
            }
            catch (Exception exception) when (exception is IOException or ArgumentException)
            {
                return null;
            }

            _entries[path] = (bitmap, ++_clock);
            while (_entries.Count > capacity)
            {
                var oldest = _entries.OrderBy(pair => pair.Value.Used).First();
                oldest.Value.Image.Dispose();
                _entries.Remove(oldest.Key);
            }

            return bitmap;
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
        foreach (var block in new[]
                 {
                     _autosaveState, _selectionStatus, _activeLayerStatus, _gridStatus, _viewportStatus,
                 })
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
                _saveState, StatusDot(), _autosaveState, StatusDot(), _activeLayerStatus, StatusDot(), _selectionStatus,
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

        _autosaveState.Text = _editor.AutosaveSummary;
        Ui.SetClass(_autosaveState, "danger", _editor.AutosaveState == AutosaveState.Failed);
        Ui.SetClass(_autosaveState, "success", _editor.AutosaveState == AutosaveState.Saved);

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
            var active = (index == 1) == _editor.LandSubtract;
            Ui.SetClass(_landActionButtons[index], "active", active);
            if (_landActionButtons[index] is ToggleButton toggle) toggle.IsChecked = active;
        }
    }

    private void SyncMaskModeButtons()
    {
        for (var index = 0; index < _maskModeButtons.Count; index++)
        {
            var active = (index == 1) == (_editor.MaskMode == MaskPaintMode.Reveal);
            Ui.SetClass(_maskModeButtons[index], "active", active);
            if (_maskModeButtons[index] is ToggleButton toggle) toggle.IsChecked = active;
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

            Ui.SetStructureSelected(row, layer.Id == _editor.ActiveLayerId);
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

            Ui.SetSelected(row, sceneObject.Id == _editor.Session.SelectedObjectId);
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
        _syncingLibrary = true;
        try
        {
            _editor.LibrarySearchText = (_assetSearch.Text ?? "").Trim();
            SyncLibraryDiagnostic();
            SyncLibraryFilterState();
            SyncCollectionTree();
            SyncBreadcrumbs();
            SyncLibraryGrid();
            SyncSelectedSet();
            SyncStampGestureReadout();
            SyncStampToolPanel();
        }
        finally
        {
            _syncingLibrary = false;
        }

        // Choosing a card brings twelve more files into view that the visible-range report never
        // saw, so the check is kicked here too. It is idempotent: the view model verifies only
        // what it has no verdict for, and returns at once when there is nothing left to check.
        if (_editor.CurrentVariant is { } current
            && _editor.AvailabilityOf(current.Sha256) == AssetAvailability.Unknown)
        {
            _ = CheckVisibleAvailabilityAsync();
        }
    }

    private void SyncLibraryDiagnostic()
    {
        var (visible, text) = _editor.LibraryStatus switch
        {
            AssetLibraryStatus.Damaged => (true, "Индекс библиотеки повреждён и защищён от записи. "
                + _editor.LibraryDiagnostic),
            AssetLibraryStatus.Migrated => (true, _editor.LibraryDiagnostic),
            _ => (false, ""),
        };

        _libraryDiagnostic.IsVisible = visible;
        _libraryDiagnosticText.Text = text;
        AutomationProperties.SetName(_libraryDiagnosticText, visible ? text : "Библиотека в порядке");
    }

    private void SyncLibraryFilterState()
    {
        SetSegment(_librarySortButtons, _editor.LibrarySort switch
        {
            AssetSortOrder.ImportedOldest => "Oldest",
            AssetSortOrder.NameAscending => "NameAsc",
            AssetSortOrder.NameDescending => "NameDesc",
            _ => "Newest",
        });
        SetSegment(_libraryGroupButtons, _editor.LibraryGrouping switch
        {
            AssetGrouping.Collection => "Collection",
            AssetGrouping.Tag => "Tag",
            _ => "None",
        });
        SetSegment(_placementModeButtons,
            _editor.StampPlacementMode == StampPlacementMode.Area ? "Area" : "One");

        SetToggle(_libraryFilterButtons["Favorites"], "Только избранное", _editor.LibraryFavoritesOnly);
        SetToggle(_libraryFilterButtons["Recent"], "Только недавние", _editor.LibraryRecentOnly);
        SetToggle(_libraryFilterButtons["Untagged"], "Только без тегов", _editor.LibraryUntaggedOnly);
        SetToggle(
            _libraryFilterButtons["UsedInMap"], "Только используемое на карте", _editor.LibraryUsedInMapOnly);
        SetToggle(
            _randomVariantToggle, "Случайный вариант после размещения", _editor.RandomVariantAfterPlacement);
        SetToggle(
            _randomAngleToggle, "Случайный угол поворота каждого штампа", _editor.StampRandomAngle);
        SetToggle(
            _randomFlipToggle, "Случайное зеркальное отражение штампа", _editor.StampRandomFlip);

        FillChoices(_libraryTagFilter, "Любой тег", _editor.LibraryTagChoices, _editor.LibraryTag);
        FillChoices(
            _libraryMediaFilter, "Любой тип", _editor.LibraryMediaTypeChoices, _editor.LibraryMediaType);
        if ((_libraryImportedFrom.Text ?? "") != _editor.LibraryImportedFromText)
        {
            _libraryImportedFrom.Text = _editor.LibraryImportedFromText;
        }

        if ((_libraryImportedTo.Text ?? "") != _editor.LibraryImportedToText)
        {
            _libraryImportedTo.Text = _editor.LibraryImportedToText;
        }

        var dateHint = _editor.HasLibraryDateError
            ? "Дата не распознана. Ожидается ГГГГ-ММ-ДД, например 2026-08-26."
            : "Дата в формате ГГГГ-ММ-ДД.";
        _libraryDateHint.Text = dateHint;
        Ui.SetClass(_libraryDateHint, "danger", _editor.HasLibraryDateError);
        Ui.SetClass(_libraryDateHint, "faint", !_editor.HasLibraryDateError);
        AutomationProperties.SetName(_libraryDateHint, dateHint);
        _libraryDateHint.IsVisible = _editor.HasLibraryDateError;
        if (_editor.HasLibraryDateError)
        {
            _libraryFilterDetails.IsExpanded = true;
        }

        var summary = "Фильтры: " + _editor.ActiveLibraryFilterSummary;
        _libraryFilterSummary.Text = summary;
        Ui.SetClass(_libraryFilterSummary, "accent", _editor.HasActiveLibraryFilters);
        Ui.SetClass(_libraryFilterSummary, "faint", !_editor.HasActiveLibraryFilters);
        AutomationProperties.SetName(_libraryFilterSummary, summary);
        ToolTip.SetTip(_libraryFilterSummary, summary);
        _clearFiltersButton.IsEnabled = _editor.HasActiveLibraryFilters;
        _libraryFilterSummary.IsVisible = _editor.HasActiveLibraryFilters;
        _clearFiltersButton.IsVisible = _editor.HasActiveLibraryFilters;

        static void FillChoices(
            ComboBox box, string anyLabel, IReadOnlyList<string> choices, string? selected)
        {
            var wanted = new List<string> { anyLabel };
            wanted.AddRange(choices);
            if (box.ItemsSource is not IReadOnlyList<string> current || !current.SequenceEqual(wanted))
            {
                box.ItemsSource = wanted;
            }

            box.SelectedItem = selected is not null && wanted.Contains(selected) ? selected : anyLabel;
        }

        static void SetSegment(Dictionary<string, Button> buttons, string activeKey)
        {
            foreach (var (key, button) in buttons)
            {
                Ui.SetSelected(button, key == activeKey);
            }
        }
    }

    private void SyncCollectionTree()
    {
        _collectionTree.Children.Clear();
        var all = Ui.ListRow(
            "Collection_All", MapIcons.Layers, "Все коллекции",
            _editor.LibrarySets.Count.ToString(), [],
            () =>
            {
                _editor.LibraryCollectionId = null;
                SyncAssetList();
            },
            "Все коллекции");
        Ui.SetSelected(all, _editor.LibraryCollectionId is null);
        _collectionTree.Children.Add(all);

        Add(_editor.LibraryCollections, 0);

        if (_editor.LibraryCollections.Count == 0)
        {
            _collectionTree.Children.Add(new TextBlock
            {
                Text = "Коллекции появятся после первого импорта.",
                TextWrapping = TextWrapping.Wrap,
            }.Classed("faint", "role-caption").Named("NoCollectionsText"));
        }

        void Add(IReadOnlyList<AssetCollectionNode> nodes, int depth)
        {
            foreach (var node in nodes)
            {
                var captured = node.Id;
                var row = Ui.ListRow(
                    "Collection_" + node.Id, MapIcons.Layers, node.Name,
                    node.TotalCount.ToString(), [],
                    () =>
                    {
                        _editor.LibraryCollectionId = captured;
                        SyncAssetList();
                    },
                    $"{node.Name}, карточек: {node.TotalCount}");
                row.Margin = new Thickness(depth * 12, 0, 0, 0);
                Ui.SetSelected(row, _editor.LibraryCollectionId == node.Id);
                _collectionTree.Children.Add(row);
                Add(node.Children, depth + 1);
            }
        }
    }

    /// <summary>
    /// The breadcrumb trail. Each ancestor segment is a real button that navigates back up, so
    /// the trail is reachable from the keyboard and visible to UI Automation; a bare panel of
    /// text blocks would not appear in the accessibility tree at all.
    /// </summary>
    private void SyncBreadcrumbs()
    {
        _breadcrumbs.Children.Clear();
        _breadcrumbs.VerticalAlignment = VerticalAlignment.Center;

        var root = Ui.TextButton("BreadcrumbRoot", "Библиотека", () =>
        {
            _editor.LibraryCollectionId = null;
            _editor.SelectAssetSet(null);
            SyncAssetList();
        }, "compact");
        AutomationProperties.SetName(root, "Библиотека, все коллекции");
        ToolTip.SetTip(root, "Показать все коллекции");
        _breadcrumbs.Children.Add(root.Docked(Dock.Left));

        var current = "Все коллекции";
        if (_editor.LibraryCollectionId is { } collectionId
            && FindCollectionName(_editor.LibraryCollections, collectionId) is { } collectionName)
        {
            current = collectionName;
            _breadcrumbs.Children.Add(Separator().Docked(Dock.Left));
            var collection = Ui.TextButton("BreadcrumbCollection", collectionName, () =>
            {
                _editor.SelectAssetSet(null);
                SyncAssetList();
            }, "compact");
            AutomationProperties.SetName(collection, $"Коллекция {collectionName}");
            _breadcrumbs.Children.Add(collection.Docked(Dock.Left));
        }

        if (_editor.SelectedSet is { } selected)
        {
            current = selected.Name;
            _breadcrumbs.Children.Add(Separator().Docked(Dock.Left));
        }

        var leaf = new TextBlock
        {
            Text = current,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }.Classed("muted", "role-caption").Named("BreadcrumbCurrent");
        AutomationProperties.SetName(leaf, current);
        _breadcrumbs.Children.Add(leaf);

        static Control Separator() => new TextBlock
        {
            Text = "/",
            Margin = new Thickness(4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        }.Classed("faint");

        static string? FindCollectionName(IReadOnlyList<AssetCollectionNode> nodes, string id)
        {
            foreach (var node in nodes)
            {
                if (node.Id == id)
                {
                    return node.Name;
                }

                if (FindCollectionName(node.Children, id) is { } nested)
                {
                    return nested;
                }
            }

            return null;
        }
    }

    private void SyncLibraryGrid()
    {
        var rows = _editor.LibraryRows;
        if (!ReferenceEquals(_libraryGrid.ItemsSource, rows))
        {
            // Rebuilding the source drops the realised containers, so the tiles they registered
            // are stale: a recycled container would otherwise keep writing into a dead badge.
            _tileStates.Clear();
            _reportedLibraryRange = (-1, -1);
            _libraryGrid.ItemsSource = rows;
        }

        _libraryGrid.SelectedIndex = _editor.SelectedLibraryRowIndex;
        _lastSelectedLibraryRow = _editor.SelectedLibraryRowIndex;

        // The grid stays in the accessibility tree even when empty - a control that disappears
        // cannot be found, focused or reported by a screen reader.
        var cards = _editor.LibrarySets.Count;
        _libraryGrid.MinHeight = rows.Count > 0 ? 220 : 0;

        // The UIA name opens with the exact words on screen; the empty states then add the fix
        // a screen-reader user would otherwise only get from the surrounding visuals.
        var accessibleCount = _editor.LibraryStatus == AssetLibraryStatus.Damaged
            ? "Библиотека недоступна"
            : cards == 0
                ? (_editor.HasActiveLibraryFilters
                    ? "Ничего не найдено. Измените запрос или снимите фильтры."
                    : "Библиотека пуста. Импортируйте PNG, JPEG или WebP.")
                : $"Ассеты · {cards}";
        _libraryCountText.Text = _editor.LibraryStatus == AssetLibraryStatus.Damaged
            ? "Библиотека недоступна"
            : cards == 0
                ? (_editor.HasActiveLibraryFilters ? "Ничего не найдено" : "Библиотека пуста")
                : $"Ассеты · {cards}";
        AutomationProperties.SetName(_libraryCountText, accessibleCount);
        ToolTip.SetTip(_libraryCountText, accessibleCount);
        AutomationProperties.SetName(_libraryGrid, $"Карточки библиотеки, {cards}");
    }
    private void SyncSelectedSet()
    {
        var set = _editor.SelectedSet;
        var hasSet = set is not null;
        _selectedSetBlock.IsVisible = hasSet;
        _stampGestureBlock.IsVisible = hasSet;
        _setManagementDetails.IsVisible = hasSet;
        _selectedAsset = _editor.CurrentVariant is { } current
            ? _editor.Assets.FirstOrDefault(record =>
                string.Equals(record.Sha256, current.Sha256, StringComparison.OrdinalIgnoreCase))
            : null;

        _selectedSetNameText.Text = set?.Name ?? "Набор не выбран";
        // Named explicitly: a bare TextBlock is not reliably reported, and the name of the card
        // the tool is holding is exactly what a screen reader has to be able to read back.
        AutomationProperties.SetName(_selectedSetNameText, _selectedSetNameText.Text);
        ToolTip.SetTip(_selectedSetNameText, _selectedSetNameText.Text);
        _setBadge.IsVisible = set is { IsSet: true };
        if (set is { IsSet: true })
        {
            // A TextBlock reports its own Text to UI Automation, so a bare digit here would be
            // exactly the mute badge the catalog research rules out. The panel has room for the
            // whole phrase, so the badge simply says it.
            var badgeName = $"{set.VariantCount} вариантов";
            _setBadgeText.Text = badgeName;
            AutomationProperties.SetName(_setBadgeText, badgeName);
            ToolTip.SetTip(_setBadge, badgeName);
        }

        SetToggle(
            _favoriteButton, set?.Favorite == true ? "Убрать из избранного" : "В избранное",
            set?.Favorite == true);
        _favoriteButton.IsEnabled = set is not null;

        _variantPositionText.Text = _editor.CurrentVariantLabel.Length == 0
            ? "Вариант не выбран"
            : $"Вариант {_editor.CurrentVariantLabel}"
                + (_editor.IsCurrentVariantTheCover ? " · обложка" : "");
        AutomationProperties.SetName(_variantPositionText, _variantPositionText.Text);

        var many = _editor.SelectedSetVariants.Count > 1;
        _previousVariantButton.IsEnabled = many;
        _nextVariantButton.IsEnabled = many;

        // A singleton card has nothing to reorder and nothing to choose a cover from, so those
        // controls are visibly unavailable rather than silently doing nothing.
        _setRenameBox.IsEnabled = _editor.CanRenameSelectedSet;
        _setRenameButton.IsEnabled = _editor.CanRenameSelectedSet;
        if (!_setRenameBox.IsFocused)
        {
            _setRenameBox.Text = set?.Name ?? "";
        }

        _variantEarlierButton.IsEnabled = _editor.CanMoveCurrentVariant(-1);
        _variantLaterButton.IsEnabled = _editor.CanMoveCurrentVariant(1);
        _setCoverButton.IsEnabled = _editor.CanMakeCurrentVariantCover;
        SetCoverButtonLabel(set);
        SyncSelectedSetAvailability();

        _assetSelectionText.Text = _selectedAsset is { } selected
            ? $"{selected.DisplayName} · {selected.Width}×{selected.Height}"
            : "Ассет не выбран. Выберите карточку в списке выше.";
        AutomationProperties.SetName(_assetSelectionText, _assetSelectionText.Text);

        SyncVariantStrip();

        foreach (var command in _assetSelectionCommands)
        {
            command.IsEnabled = _selectedAsset is not null;
        }
    }

    /// <summary>The cover button says which state it would move the card into, or why it cannot.</summary>
    private void SetCoverButtonLabel(AssetSetSummary? set)
    {
        var title = set is null
            ? "Сделать обложкой: набор не выбран"
            : _editor.IsCurrentVariantTheCover
                ? "Этот вариант уже обложка набора"
                : set.IsSet
                    ? $"Сделать вариант {_editor.CurrentVariantLabel} обложкой набора"
                    : "У набора из одного варианта обложка одна";
        AutomationProperties.SetName(_setCoverButton, title);
        ToolTip.SetTip(_setCoverButton, title);
    }

    private void SyncVariantStrip()
    {
        _variantStrip.Children.Clear();
        var variants = _editor.SelectedSetVariants;
        if (variants.Count == 0)
        {
            return;
        }

        var cover = _editor.SelectedSet?.CoverVariantId;
        for (var index = 0; index < variants.Count; index++)
        {
            var variant = variants[index];
            var position = index;
            var thumbnail = _thumbnails.Image(_editor.LibraryThumbnailPathForVariant(variant.Id));
            var isCover = variant.Id == cover;
            var availability = _editor.AvailabilityOf(variant.Sha256);
            var flagged = availability is not (AssetAvailability.Unknown or AssetAvailability.Available);

            Control face = new Border
            {
                Width = 40,
                Height = 40,
                Child = thumbnail is null
                    ? MapIcons.Stroked(MapIcons.Missing, 16)
                    : new Image { Source = thumbnail, Stretch = Stretch.Uniform },
            };

            // The cover needs a mark of its own: "selected" already means "current variant", and
            // the two are usually different thumbnails. The mark is composed before the content is
            // handed to the button, because a control that already has a parent cannot be reparented.
            if (isCover || flagged)
            {
                var mark = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Padding = new Thickness(2, 0),
                    Child = MapIcons.Stroked(flagged ? MapIcons.Warning : MapIcons.Check, 11),
                }.Classed("badge");
                if (availability is AssetAvailability.Missing or AssetAvailability.HashMismatch)
                {
                    mark.Classes.Add("danger");
                }
                else if (flagged)
                {
                    mark.Classes.Add("warn");
                }

                face = new Panel { Children = { face, mark } };
            }

            var button = new ToggleButton { Content = face }
                .Named("Variant_" + variant.Id).Classed("segment");

            var name = $"Вариант {position + 1} из {variants.Count}"
                + (isCover ? ", обложка" : "")
                + (position == _editor.CurrentVariantIndex ? ", текущий" : "")
                + (flagged ? ", " + EditorViewModel.AvailabilityText(availability) : "");

            AutomationProperties.SetName(button, name);
            ToolTip.SetTip(button, name);
            Ui.SetSelected(button, position == _editor.CurrentVariantIndex);
            button.Click += (_, _) =>
            {
                _editor.SelectVariantAt(position);
                SyncAssetList();
                SyncStatus();
            };
            _variantStrip.Children.Add(button);
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

            Ui.SetSelected(row, note.Id == _selectedNoteId);
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

        string? path;
        if (UiTestFilePaths.NextImportGroup() is { } supplied)
        {
            path = supplied[0];
        }
        else
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = linked ? "Связать изображение" : "Импортировать изображение",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Изображения") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] },
                ],
            });

            path = files.Count == 1 ? files[0].TryGetLocalPath() : null;
        }

        if (path is null)
        {
            return;
        }

        _selectedAsset = _editor.ImportAsset(path, linked);
        if (_selectedAsset is { } imported)
        {
            _editor.RefreshLibrary();
            if (_editor.LibrarySets.FirstOrDefault(set =>
                string.Equals(set.CoverSha256, imported.Sha256, StringComparison.OrdinalIgnoreCase))
                is { } card)
            {
                _editor.SelectAssetSet(card.SetId);
            }
        }

        SyncAssetList();
        SyncStatus();
    }

    /// <summary>One import of many files becomes one named logical card with ordered variants.</summary>
    private async System.Threading.Tasks.Task ImportAssetSetAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        List<string> paths;
        if (UiTestFilePaths.NextImportGroup() is { } supplied)
        {
            paths = [.. supplied];
        }
        else
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Импортировать набор вариантов",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("Изображения") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] },
                ],
            });

            paths = [.. files.Select(file => file.TryGetLocalPath())
                .Where(path => path is not null)
                .Select(path => path!)];
        }
        if (paths.Count == 0)
        {
            return;
        }

        var suggested = paths.Count == 1
            ? Path.GetFileNameWithoutExtension(paths[0])
            : Path.GetFileName(Path.GetDirectoryName(paths[0])) ?? "Новый набор";
        var name = await AskSetNameAsync(topLevel as Window, suggested);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _editor.ImportAssetSet(paths, name);
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
