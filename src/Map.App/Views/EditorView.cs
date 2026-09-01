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
using Avalonia.VisualTree;
using Map.App.Controls;
using Map.App.Services;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
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
    private Control _rotationInspectorRow = null!;
    private Control _sublayerInspectorRow = null!;
    private Control _opacityInspectorRow = null!;

    // ------------------------------------------------------------- side lists
    private readonly StackPanel _layerList = new() { Spacing = 2 };
    private readonly ListBox _objectList = new()
    {
        Name = "ObjectList",
        SelectionMode = SelectionMode.Multiple,
    };
    private readonly ContentControl _objectEmptyStateHost = new();
    private readonly StackPanel _assetList = new() { Spacing = 2 };
    private readonly StackPanel _noteList = new() { Spacing = 2 };
    private readonly StackPanel _checkpointList = new() { Spacing = 8 };
    private readonly TextBox _canvasWidthBox = NumberBox("CanvasWidthBox");
    private readonly TextBox _canvasHeightBox = NumberBox("CanvasHeightBox");
    private readonly TextBox _regionXBox = NumberBox("RegionXBox");
    private readonly TextBox _regionYBox = NumberBox("RegionYBox");
    private readonly TextBox _regionWidthBox = NumberBox("RegionWidthBox");
    private readonly TextBox _regionHeightBox = NumberBox("RegionHeightBox");
    private readonly TextBox _regionTitleBox = new()
    {
        Name = "RegionTitleBox",
        PlaceholderText = "Название новой карты",
        MaxLength = 160,
    };
    private readonly TextBlock _canvasWorkflowSummary = Status("CanvasWorkflowSummaryText");
    private readonly TextBlock _canvasObjectSummary = Status("CanvasObjectSummaryText");
    private readonly TextBlock _canvasWorkflowError = Status("CanvasWorkflowErrorText");
    private readonly Dictionary<CanvasAnchor, Button> _anchorButtons = [];
    private Button _resizeModeButton = null!;
    private Button _regionModeButton = null!;
    private ToggleButton _boundarySnapToggle = null!;
    private Control _resizeWorkflowPanel = null!;
    private Control _regionWorkflowPanel = null!;
    private bool _syncingCanvasWorkflow;
    private readonly TextBox _exportRegionXBox = NumberBox("ExportRegionXBox");
    private readonly TextBox _exportRegionYBox = NumberBox("ExportRegionYBox");
    private readonly TextBox _exportRegionWidthBox = NumberBox("ExportRegionWidthBox");
    private readonly TextBox _exportRegionHeightBox = NumberBox("ExportRegionHeightBox");
    private readonly TextBox _exportOutputWidthBox = NumberBox("ExportOutputWidthBox");
    private readonly TextBox _exportOutputHeightBox = NumberBox("ExportOutputHeightBox");
    private readonly TextBlock _exportFormatExplanation = Status("ExportFormatExplanationText");
    private readonly TextBlock _exportEstimate = Status("ExportEstimateText");
    private readonly TextBlock _exportError = Status("ExportValidationErrorText");
    private readonly TextBlock _exportProgressText = Status("ExportProgressText");
    private readonly TextBlock _exportResult = Status("ExportResultText");
    private readonly Image _exportPreview = new()
    {
        Name = "ExportPreviewImage",
        Height = 180,
        Stretch = Stretch.Uniform,
    };
    private readonly ProgressBar _exportProgress = new()
    {
        Name = "ExportProgressBar",
        Minimum = 0,
        Maximum = 100,
        Height = 8,
    };
    private readonly Dictionary<ExportImageFormat, Button> _exportFormatButtons = [];
    private readonly Dictionary<ExportAreaMode, Button> _exportAreaButtons = [];
    private readonly Dictionary<ExportOutputMode, Button> _exportModeButtons = [];
    private ToggleButton _exportAspectToggle = null!;
    private ToggleButton _exportTransparencyToggle = null!;
    private ToggleButton _exportGridToggle = null!;
    private Slider _exportQualitySlider = null!;
    private TextBlock _exportQualityText = null!;
    private Control _exportRegionPanel = null!;
    private Control _exportQualityPanel = null!;
    private Button _startExportButton = null!;
    private Button _cancelExportButton = null!;
    private Button _openExportFolderButton = null!;
    private bool _syncingExport;
    private Bitmap? _exportPreviewBitmap;
    private byte[]? _shownExportPreviewPng;
    private readonly TextBox _layerName = new() { Name = "LayerNameBox", PlaceholderText = "Имя слоя" };
    private readonly TextBox _objectSearch = new() { Name = "ObjectSearchBox", PlaceholderText = "Найти объект" };
    private readonly TextBox _assetSearch = new()
    {
        Name = "AssetSearchBox",
        PlaceholderText = "Найти в библиотеке",
    };
    private readonly TextBox _noteSearch = new() { Name = "NoteSearchBox", PlaceholderText = "Найти заметку" };
    private readonly TextBox _noteTitle = new() { Name = "NoteTitleBox", PlaceholderText = "Заголовок заметки" };
    private readonly TextBox _checkpointName = new()
    {
        Name = "CheckpointNameBox",
        PlaceholderText = "Например: До эксперимента",
        MaxLength = MapCheckpointLimits.MaxNameLength,
    };
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
    private readonly List<(Button Button, string Label, string Command)> _commandButtons = [];
    private readonly List<Action> _shortcutSurfaceSyncs = [];
    private readonly Dictionary<string, Button> _landShapeButtons = [];
    private readonly List<Button> _landActionButtons = [];
    private readonly List<Button> _maskModeButtons = [];
    private TabControl _sideTabs = null!;
    private Border _sidePanel = null!;
    private Control _sideBody = null!;
    private Border _contextPanel = null!;
    private Control _contextBody = null!;
    private bool _contextCollapsed;
    private bool _sideCollapsed;
    private Window? _assetCatalogWindow;
    private Window? _versionsWindow;
    private Window? _canvasWorkflowWindow;
    private Window? _exportWindow;
    private Control _assetCatalogContent = null!;
    private Control _versionsContent = null!;
    private Control _canvasWorkflowContent = null!;
    private Control _exportContent = null!;
    private bool _closingAuxiliaryWindows;

    // ------------------------------------------------------ selection sections
    // Every reusable block below lives in exactly one container for the lifetime of the view.
    // Rebuilding containers on each sync would re-parent a control that already has a parent,
    // which Avalonia rejects outright - so the containers are built once and only swapped.
    private Control _inspectorPanel = null!;
    private Control _selectionEmpty = null!;

    // -------------------------------------------------- multi-selection controls
    private readonly TextBlock _selectionCountText = new() { Name = "SelectionCountText" };
    private readonly TextBox _groupNameBox = new()
    {
        Name = "GroupNameBox",
        PlaceholderText = "Имя группы",
        MaxLength = 80,
    };

    private Button _groupCreateButton = null!;
    private Button _groupRenameButton = null!;
    private Button _ungroupButton = null!;
    private Button _enterGroupButton = null!;
    private Button _multiVisibilityValue = null!;
    private Button _multiLockValue = null!;
    private Control _multiSelectionRoot = null!;
    private Control _groupNameSection = null!;
    private readonly Dictionary<string, Button> _objectFilterButtons = [];
    private bool _objectRowCtrl;
    private Control _stampSection = null!;
    private Control _lineSection = null!;
    private Control _shapeSection = null!;
    private Control _textSection = null!;
    private Control _compositionSection = null!;
    private ContentControl _kindSlot = null!;
    private Control _selectionRoot = null!;
    private Control _selectIdleRoot = null!;

    // -------------------------------------------------- appearance inspector plumbing
    // Each control registers one sync action that re-reads its current value from the selection.
    // The guard keeps a programmatic sync from being mistaken for user input.
    private readonly List<Action> _appearanceSyncs = [];
    private bool _syncingAppearance;
    private TextBlock _brushTipNameText = null!;

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
    private readonly Dictionary<PlacementAutoSelectPolicy, Button> _autoSelectButtons = [];
    private Button _addRectangleButton = null!;
    private Button _addEllipseButton = null!;
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
    private ToggleButton _randomFlipToggle = null!;
    private TextBlock _stampToolCurrentText = null!;
    private TextBlock _stampToolAreaText = null!;
    private Image _stampToolThumbnail = null!;
    private StackPanel _stampRecentStrip = null!;
    private StackPanel _stampFavoriteStrip = null!;
    private bool _syncingLibrary;
    private readonly ThumbnailCache _thumbnails = new(96);
    private readonly CheckpointPreviewCache _checkpointPreviews = new();
    private Button _objectVisibilityValue = null!;
    private Button _objectLockValue = null!;
    private ToggleButton _eraserButton = null!;
    private Button _undoButton = null!;
    private Button _redoButton = null!;
    private Button _saveButton = null!;
    private Button _shortcutSettingsButton = null!;
    private int _objectListRebuildCount;
    private readonly HashSet<ObjectId> _objectSelectionSet = [];

    public EditorView(
        EditorViewModel editor,
        Func<System.Threading.Tasks.Task> requestBack,
        Action requestSecondWindow,
        Action requestShortcutSettings)
    {
        _editor = editor;
        Name = "EditorView";

        var topBar = BuildTopBar(requestBack, requestSecondWindow, requestShortcutSettings);
        var rail = BuildToolRail();
        var contextPanel = BuildContextPanel();
        var sidePanel = BuildSidePanel();
        var hintBar = BuildHintBar();
        var statusBar = BuildStatusBar();
        SyncShortcutSurfaces();

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
        _editor.Shortcuts.Changed += OnShortcutsChanged;
        _editor.SceneChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            SyncStatus();
            SyncSidePanels();
            SyncContextArea();
        });
        _editor.SceneInvalidated += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            if (e.Has(EditorChangeReason.Selection))
            {
                SyncStatus();
                SyncInspector();
                SyncObjectListSelection();
                SyncContextArea();
            }
        });
        DetachedFromVisualTree += (_, _) =>
        {
            _closingAuxiliaryWindows = true;
            foreach (var window in new[]
                     { _assetCatalogWindow, _versionsWindow, _canvasWorkflowWindow, _exportWindow })
            {
                window?.Close();
            }
            _exportPreviewBitmap?.Dispose();
            _exportPreviewBitmap = null;
        };

        _objectSearch.TextChanged += (_, _) =>
        {
            _editor.ObjectSearchText = _objectSearch.Text ?? "";
            SyncObjectList();
        };

        // Rows read the modifier from the press that is about to click them, so Ctrl+click on a
        // row toggles membership exactly like Ctrl+click on the canvas.
        _objectList.AddHandler(PointerPressedEvent,
            (_, e) => _objectRowCtrl = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
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

    /// <summary>Stage 13 diagnostics: structure rebuilds, never pointer-render invalidations.</summary>
    public int ObjectListRebuildCount => _objectListRebuildCount;

    /// <summary>Actual Avalonia containers alive for the virtual Objects list.</summary>
    public int ObjectListRealizedCount => _objectList.GetRealizedContainers().Count();

    public int ObjectListLogicalCount =>
        _objectList.ItemsSource is IReadOnlyCollection<ObjectTreeRow> rows ? rows.Count : 0;

    public int WorkspaceTabCount => (_sideTabs.ItemsSource as IEnumerable<object>)?.Count() ?? 0;
    public bool ContextPanelCollapsed => _contextCollapsed;
    public bool RightPanelCollapsed => _sideCollapsed;
    public bool AssetCatalogVisible => _assetCatalogWindow?.IsVisible == true;
    public bool StampCompactCurrentThumbnailVisibleForSmoke =>
        _stampToolThumbnail is { Bounds.Width: > 0, Bounds.Height: > 0, Source: not null };
    public int StampCompactRecentCountForSmoke => _stampRecentStrip?.Children.Count ?? 0;
    public int StampCompactFavoriteCountForSmoke => _stampFavoriteStrip?.Children.Count ?? 0;

    public bool ShowFutureClipMaskForSmoke()
    {
        var group = this.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => control.Name == "FutureStampClipMaskGroup");
        group?.BringIntoView();
        return group is not null;
    }

    public bool CreateVersionThroughUiForSmoke()
    {
        OpenVersions();
        var before = _editor.Checkpoints.Count;
        var button = _versionsContent.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(control => control.Name == "CreateVersionButton");
        button?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        return button is not null && _editor.Checkpoints.Count == before + 1;
    }

    public void CloseAuxiliaryWindowsForSmoke()
    {
        _closingAuxiliaryWindows = true;
        foreach (var window in new[]
                 { _assetCatalogWindow, _versionsWindow, _canvasWorkflowWindow, _exportWindow })
        {
            window?.Close();
        }
    }

    public bool IsNotesOpen => _editor.ActiveTool == EditorTool.Notes;

    public void OpenObjects(bool focusSearch = false)
    {
        _sidePanel.IsVisible = true;
        _sideTabs.SelectedIndex = 1;
        if (focusSearch)
        {
            Dispatcher.UIThread.Post(() => _objectSearch.Focus(), DispatcherPriority.Input);
        }
    }

    public void ScrollObjectsTo(int index) => _objectList.ScrollIntoView(index);

    public void SetObjectSearchText(string query) => _objectSearch.Text = query ?? "";

    public void OpenAssetCatalog(bool focusSearch = false)
    {
        _assetCatalogWindow = ShowAuxiliaryWindow(
            _assetCatalogWindow,
            "Локальная библиотека ассетов",
            _assetCatalogContent,
            1080,
            760,
            "AssetCatalogWindow");
        if (focusSearch)
        {
            Dispatcher.UIThread.Post(() => _assetSearch.Focus(), DispatcherPriority.Input);
        }
    }

    public void OpenNotes(bool focusEditor = false)
    {
        _editor.ActiveTool = EditorTool.Notes;
        SyncContextArea();
        if (focusEditor)
        {
            Dispatcher.UIThread.Post(
                () => (_selectedNoteId is not null ? _noteTitle : _noteSearch).Focus(),
                DispatcherPriority.Input);
        }
    }

    public void ToggleSidePanel() => _sidePanel.IsVisible = !_sidePanel.IsVisible;

    public void ToggleContextPanel()
    {
        _contextCollapsed = !_contextCollapsed;
        _contextBody.IsVisible = !_contextCollapsed;
        _contextTitle.IsVisible = !_contextCollapsed;
        _contextPanel.Width = _contextCollapsed ? 42 : Ui.ContextPanelWidth;
    }

    public void ToggleRightPanel()
    {
        _sideCollapsed = !_sideCollapsed;
        _sideBody.IsVisible = !_sideCollapsed;
        _sidePanel.Width = _sideCollapsed ? 42 : Ui.SidePanelWidth;
    }

    public void OpenVersions()
    {
        SyncCheckpointList();
        _versionsWindow = ShowAuxiliaryWindow(
            _versionsWindow,
            "Версии карты",
            _versionsContent,
            780,
            700,
            "VersionsWindow");
    }

    public void OpenCanvasWorkflow()
    {
        _editor.BeginCanvasWorkflow();
        SyncCanvasWorkflow();
        _canvasWorkflowWindow = ShowAuxiliaryWindow(
            _canvasWorkflowWindow,
            "Размер и область карты",
            _canvasWorkflowContent,
            820,
            760,
            "CanvasWorkflowWindow");
    }

    public void OpenExport()
    {
        _editor.BeginExport();
        SyncExport();
        _exportWindow = ShowAuxiliaryWindow(
            _exportWindow,
            "Экспорт изображения",
            _exportContent,
            860,
            800,
            "ExportWorkflowWindow");
    }

    private Window ShowAuxiliaryWindow(
        Window? existing,
        string title,
        Control content,
        double width,
        double height,
        string name)
    {
        if (existing is not null)
        {
            existing.Show();
            if (!TestMode.Enabled) existing.Activate();
            return existing;
        }

        var window = new Window
        {
            Name = name,
            Title = title,
            Width = width,
            Height = height,
            MinWidth = 620,
            MinHeight = 480,
            ShowActivated = !TestMode.Enabled,
            ShowInTaskbar = !TestMode.Enabled,
            Content = content,
        };
        window.Closing += (_, e) =>
        {
            if (!_closingAuxiliaryWindows)
            {
                e.Cancel = true;
                window.Hide();
            }
        };
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
        return window;
    }

    public void CloseAssetCatalogAndArmPlacement()
    {
        _editor.ArmStampPlacement();
        _assetCatalogWindow?.Hide();
        SyncContextArea();
        _canvas.Focus();
    }

    /// <summary>Re-reads the library panel after a shortcut or a canvas gesture changed it.</summary>
    public void RefreshLibraryPanel()
    {
        SyncAssetList();
        SyncStatus();
    }

    // ======================================================================= top

    private Control BuildTopBar(
        Func<System.Threading.Tasks.Task> requestBack,
        Action requestSecondWindow,
        Action requestShortcutSettings)
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

        _undoButton = Ui.IconButton("UndoButton", "Отменить", MapIcons.Undo, _editor.Undo);
        _redoButton = Ui.IconButton("RedoButton", "Повторить", MapIcons.Redo, _editor.Redo);
        var fit = Ui.IconButton("FitButton", "Вписать карту в окно", MapIcons.FitCanvas, _canvas.FitToCanvas);
        var export = Ui.TextButton("ExportButton", "Экспорт…", OpenExport, "accent", "compact");
        AutomationProperties.SetName(export, "Экспорт изображения");

        _saveButton = Ui.AsyncButton("SaveButton", "Сохранить", () => _editor.SaveAsync(), "primary");
        _shortcutSettingsButton = Ui.TextButton(
            "ShortcutSettingsButton", "Сочетания…", requestShortcutSettings, "compact");

        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        var autosaveItem = new MenuItem();
        void SyncAutosaveMenu() => autosaveItem.Header = _editor.PrimaryAutosaveEnabled
            ? "Автосохранение: включено"
            : "Автосохранение: выключено";
        SyncAutosaveMenu();
        autosaveItem.Click += (_, _) =>
        {
            _editor.PrimaryAutosaveEnabled = !_editor.PrimaryAutosaveEnabled;
            SyncAutosaveMenu();
            SyncStatus();
        };

        var fileMenu = new MenuFlyout
        {
            Items =
            {
                Item("Версии…", OpenVersions),
                Item("Сохранить как…", () => _ = _editor.SaveAsync(saveAs: true)),
                autosaveItem,
                new Separator(),
                Item("Размер и область…", OpenCanvasWorkflow),
                Item("Экспорт…", OpenExport),
                new Separator(),
                Item("Сочетания клавиш…", requestShortcutSettings),
                Item("Открыть второе окно", requestSecondWindow),
            },
        };
        var file = Ui.TextButton("FileMenuButton", "Файл", () => { }, "compact");
        file.Flyout = fileMenu;

        var bar = new DockPanel
        {
            Margin = new Thickness(8, 6),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { file, export, _saveButton },
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

        SyncShortcutSurfaces();

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
                ToolShortcut(tool),
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
                Tool("NotesToolButton", EditorTool.Notes, MapIcons.Notes),
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

        var collapse = Ui.TextButton("CollapseContextPanelButton", "‹", ToggleContextPanel, "compact");
        AutomationProperties.SetName(collapse, "Свернуть или развернуть панель инструмента");
        var header = new Border
        {
            Child = new DockPanel { Children = { collapse.Docked(Dock.Right), _contextTitle } },
        }.Classed("panel-header");

        _contextBody = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border { Padding = new Thickness(12, 10, 12, 16), Child = _contextHost },
        };

        _contextPanel = new Border
        {
            Width = Ui.ContextPanelWidth,
            Child = new DockPanel { Children = { header.Docked(Dock.Top), _contextBody } },
        }.Classed("context-panel").Named("ContextPanel");
        return _contextPanel;
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
        _toolPanels[EditorTool.Notes] = BuildNotesTab();
    }

    // No explanation here: this panel is only ever shown beneath the "Ничего не выбрано" state,
    // which already says how to make a selection. Saying it twice is noise, not guidance.
    private Control BuildSelectPanel()
    {
        _addRectangleButton = Ui.CommandButton(
            "AddRectangleButton", "Объект: прямоугольник", MapIcons.ObjectRectangle,
            () =>
            {
                _editor.ArmPrimitivePlacement(SceneObjectKind.Rectangle);
                SyncContextArea();
            });
        _addEllipseButton = Ui.CommandButton(
            "AddEllipseButton", "Объект: эллипс", MapIcons.ObjectEllipse,
            () =>
            {
                _editor.ArmPrimitivePlacement(SceneObjectKind.Ellipse);
                SyncContextArea();
            });
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Explanation("Выберите объект и укажите его место на холсте. До клика карта не меняется; W/S — поворот."),
                _addRectangleButton,
                _addEllipseButton,
                Ui.TextButton(
                    "CreatePlacementLayerButton",
                    "Создать слой объектов и продолжить",
                    () =>
                    {
                        _editor.CreateObjectLayerAndContinuePlacement();
                        SyncSidePanels();
                    },
                    "compact"),
            },
        };
    }

    private Control BuildLinePanel()
    {
        var (modes, buttons) = Ui.Segmented("LineDrawMode",
            [("FreeDraw", "Free Draw", null), ("Pen", "Pen / Nodes", null)],
            key => _editor.ActiveLineDrawMode = key == "Pen" ? LineDrawMode.Pen : LineDrawMode.FreeDraw);
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
                Explanation("Выберите режим и проведите по холсту. До отпускания кнопки виден ghost из той же геометрии; Esc отменяет без изменения карты."),
                modes,
            },
        };
    }

    private Control BuildShapePanel()
    {
        var options = new[]
        {
            ("FreeDraw", "Free Draw", ShapeDrawMode.FreeDraw), ("Pen", "Pen", ShapeDrawMode.Pen),
            ("Brush", "Brush", ShapeDrawMode.Brush), ("Rectangle", "Прямоугольник", ShapeDrawMode.Rectangle),
            ("Ellipse", "Эллипс", ShapeDrawMode.Ellipse), ("Polygon", "Многоугольник", ShapeDrawMode.Polygon),
        };
        var (modes, buttons) = Ui.Segmented("ShapeDrawMode",
            [.. options.Select(item => (item.Item1, item.Item2, (string?)null))],
            key => _editor.ActiveShapeDrawMode = options.First(item => item.Item1 == key).Item3);
        _appearanceSyncs.Add(() =>
        {
            for (var i = 0; i < buttons.Count; i++)
            {
                var active = options[i].Item3 == _editor.ActiveShapeDrawMode;
                ((ToggleButton)buttons[i]).IsChecked = active; Ui.SetClass(buttons[i], "active", active);
            }
        });
        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Explanation("Выберите способ и задайте фигуру жестом на холсте. Boolean выполняется только над двумя явно выделенными фигурами."),
                modes,
            },
        };
    }

    private Control BuildTextPanel() => new StackPanel
    {
        Spacing = 10,
        Children =
        {
            Explanation("Щёлкните для стандартного блока или протяните его размер. До отпускания виден ghost; оформление наследуется только переключателем ниже."),
            AppearanceToggle("TextInheritToggle", "Синхронизировать с выделенным Text",
                () => _editor.TextInheritFromSelection,
                value => _editor.TextInheritFromSelection = value),
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
            PlaceholderText = "Название локального пресета",
            MaxLength = 80,
        };
        var presetChoice = new ComboBox
        {
            Name = "BrushPresetChoice",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Выберите сохранённый пресет",
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

        var presetCommands = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 4,
            LineSpacing = 4,
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
                Ui.TextButton("DeleteBrushPresetButton", "Удалить", async () =>
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

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Explanation("Рисуйте по холсту. Штрих ложится на активный слой."),
                Ui.Section("Материал", new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    ItemSpacing = 4,
                    LineSpacing = 4,
                    Children =
                    {
                        Ui.TextButton("BrushSolidMaterialButton", "Цвет", () =>
                        {
                            _editor.ClearBrushTexture();
                            _editor.BrushSettings = _editor.BrushSettings with { Eraser = false };
                            SyncAppearanceSections();
                        }, "segment"),
                        Ui.TextButton("BrushTextureMaterialButton", "Текстура из библиотеки", () =>
                            OpenAssetCatalog(focusSearch: true), "segment"),
                        _eraserButton,
                    },
                }),
                AppearanceColorBox("BrushColorBox", "Цвет",
                    () => _editor.BrushSettings.Color, _editor.SetBrushColor),
                AppearanceSlider("BrushSizeSlider", "Размер кисти", 1, 256, "px",
                    () => _editor.BrushSettings.Radius,
                    (value, _) => UpdateSettings(settings => settings with { Radius = value })),
                AppearanceSlider("BrushOpacitySlider", "Непрозрачность", 5, 100, "%",
                    () => _editor.BrushSettings.Opacity * 100,
                    (value, _) => UpdateSettings(settings => settings with { Opacity = value / 100.0 }), 5),
                Ui.Section("Форма отпечатка", new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    ItemSpacing = 4,
                    LineSpacing = 4,
                    Children =
                    {
                        Ui.TextButton("BrushRoundHardPreset", "Круг · жёсткий", () => _editor.ApplyBrushPreset(BrushPresetKind.RoundHard), "segment"),
                        Ui.TextButton("BrushRoundSoftPreset", "Круг · мягкий", () => _editor.ApplyBrushPreset(BrushPresetKind.RoundSoft), "segment"),
                        Ui.TextButton("BrushEdgeBlockPreset", "Краевой блок", () => _editor.ApplyBrushPreset(BrushPresetKind.EdgeBlock), "segment"),
                        Ui.TextButton("BrushGridBlockPreset", "Блок", () => _editor.ApplyBrushPreset(BrushPresetKind.GridBlock), "segment"),
                        Ui.TextButton("BrushRectanglePreset", "Прямоугольник", () => _editor.ApplyBrushPreset(BrushPresetKind.Rectangle), "segment"),
                        Ui.TextButton("BrushEllipsePreset", "Эллипс", () => _editor.ApplyBrushPreset(BrushPresetKind.Ellipse), "segment"),
                        Ui.TextButton("BrushPolygonPreset", "Многоугольник", () => _editor.ApplyBrushPreset(BrushPresetKind.Polygon), "segment"),
                    },
                }),
                Ui.Section("Локальные пресеты", new StackPanel
                {
                    Spacing = 6,
                    Children = { presetName, presetChoice, presetCommands },
                }),
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
                        AppearanceToggle("BrushPenDynamics", "Pen dynamics",
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
                Ui.SliderRow("MaskSoftnessSlider", "Мягкость", 0, 100, _editor.BrushSettings.Softness * 100, "%",
                    value => _editor.BrushSettings = _editor.BrushSettings with { Softness = value / 100.0 }, 5),
                Ui.Section("Форма", new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    ItemSpacing = 4,
                    LineSpacing = 4,
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
        var selectedShape = LandShapeKind.Circle;
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
        ], key => selectedShape = LandShapeByKey(key));

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
            if (!TryNumber(x.Text, out var left) || !TryNumber(y.Text, out var top)
                || !TryNumber(width.Text, out var w) || !TryNumber(height.Text, out var h)
                || w <= 0 || h <= 0) return false;
            bounds = new WorldBounds(left, top, left + w, top + h);
            return true;
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
                Ui.SliderRow("LandRoughnessSlider", "Неровность края", 0, 100,
                    _editor.BrushSettings.Scatter * 100, "%",
                    value => _editor.BrushSettings = _editor.BrushSettings with { Scatter = value / 100 }, 5),
                Ui.SliderRow("LandSmoothingSlider", "Сглаживание", 0, 100,
                    _editor.BrushSettings.Softness * 100, "%",
                    value => _editor.BrushSettings = _editor.BrushSettings with { Softness = value / 100 }, 5),
                Ui.Advanced("LandAdvanced", new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = "Точная область" }.Classed("role-panel"),
                        Ui.PropertyRow("X", x), Ui.PropertyRow("Y", y),
                        Ui.PropertyRow("Ширина", width), Ui.PropertyRow("Высота", height),
                        Ui.TextButton("LandApplyShapeButton", "Применить выбранную форму", () =>
                        {
                            if (ReadBounds(out var region)) _editor.ApplyLandShape(selectedShape, region);
                        }, "compact"),
                        new TextBlock { Text = "Перемещение" }.Classed("role-panel"),
                        Ui.PropertyRow("Delta X", dx), Ui.PropertyRow("Delta Y", dy), snap,
                        Ui.TextButton("LandMoveButton", "Сдвинуть выбранную область", () =>
                        {
                            if (ReadBounds(out var region) && TryNumber(dx.Text, out var moveX)
                                && TryNumber(dy.Text, out var moveY))
                                _editor.MoveActiveLandArea(
                                    region, (int)Math.Round(moveX), (int)Math.Round(moveY),
                                    snap.IsChecked == true);
                        }, "compact"),
                        Ui.AsyncButton("LandClearButton", "Очистить слой…", ClearActiveLandAsync,
                            "destructive", "compact"),
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
                "Вся геометрия активного Land layer будет очищена одной командой. Undo восстановит её.",
                "Очистить", "ClearLandConfirmButton", destructive: true))
            _editor.ClearActiveLand();
    }

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

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
        _stampGestureBlock = BuildStampGestureBlock();

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Explanation(EditorLabels.ToolHint(EditorTool.Stamp)),
                current,
                area,
                BuildCompactStampCatalog(),
                ShortcutTextButton("OpenLibraryButton", "Открыть библиотеку",
                    EditorShortcutSettings.Commands.StampFocusLibrary,
                    () => OpenAssetCatalog(focusSearch: true), "compact"),
                BuildStampEditor("FutureStamp"),
                _stampGestureBlock,
                Ui.TextButton(
                    "CreateStampPlacementLayerButton",
                    "Создать слой объектов и продолжить",
                    () =>
                    {
                        _editor.CreateObjectLayerAndContinuePlacement();
                        SyncSidePanels();
                    }, "compact"),
                ShortcutChipRow(
                    [EditorShortcutSettings.Commands.StampPreviousVariant,
                     EditorShortcutSettings.Commands.StampNextVariant], "вариант"),
                ShortcutChipRow(
                    [EditorShortcutSettings.Commands.PrimaryUp,
                     EditorShortcutSettings.Commands.PrimaryDown], "поворот до установки"),
                ShortcutChipRow(
                    [EditorShortcutSettings.Commands.TertiaryUp,
                     EditorShortcutSettings.Commands.TertiaryDown], "область"),
                ShortcutChipRow(
                    [EditorShortcutSettings.Commands.StampDensityUp,
                     EditorShortcutSettings.Commands.StampDensityDown], "плотность"),
            },
        };
    }

    private static Control Explanation(string text) => new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
    }.Classed("faint", "role-caption");

    // ========================================== appearance inspector building blocks

    /// <summary>
    /// A slider that previews while dragging and lands the whole gesture as ONE undo entry when
    /// the pointer or the keyboard lets go. The registered sync re-reads the actual value, so the
    /// control can never display a number the document does not hold.
    /// </summary>
    private StackPanel AppearanceSlider(
        string name, string label, double minimum, double maximum, string unit,
        Func<double?> read, Action<double, bool> apply, double step = 1, Control? trailing = null)
    {
        var readout = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 52,
            TextAlignment = Avalonia.Media.TextAlignment.Right,
        }.Classed("muted").Named(name + "Value");

        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = Math.Clamp(read() ?? minimum, minimum, maximum),
            SmallChange = step,
            LargeChange = step * 5,
        }.Named(name);
        AutomationProperties.SetName(slider, $"{label}, {unit}");
        ToolTip.SetTip(slider, $"{label}. Стрелками — шаг {step:0.##} {unit}");

        var dragging = false;
        string Format(double value) => unit.Length == 0
            ? value.ToString("0.##", CultureInfo.InvariantCulture)
            : $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {unit}";
        readout.Text = Format(slider.Value);

        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty)
            {
                return;
            }

            readout.Text = Format(slider.Value);
            if (!_syncingAppearance)
            {
                apply(slider.Value, false);
            }
        };
        slider.AddHandler(PointerPressedEvent,
            (_, _) => dragging = true, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        slider.PointerCaptureLost += (_, _) =>
        {
            dragging = false;
            if (!_syncingAppearance)
            {
                apply(slider.Value, true);
            }
        };
        slider.KeyUp += (_, e) =>
        {
            if (!_syncingAppearance && e.Key is Key.Left or Key.Right or Key.Up or Key.Down
                or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            {
                apply(slider.Value, true);
            }
        };
        slider.LostFocus += (_, _) =>
        {
            if (!_syncingAppearance && !dragging)
            {
                apply(slider.Value, true);
            }
        };

        _appearanceSyncs.Add(() =>
        {
            if (dragging)
            {
                return;
            }

            var current = read();
            if (current is { } value && Math.Abs(slider.Value - value) > 0.0001)
            {
                slider.Value = Math.Clamp(value, minimum, maximum);
            }

            readout.Text = Format(slider.Value);
        });

        var header = new DockPanel();
        if (trailing is not null)
        {
            header.Children.Add(trailing.Docked(Dock.Right));
        }

        header.Children.Add(readout.Docked(Dock.Right));
        header.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });

        return new StackPanel
        {
            Margin = new Thickness(0, 2),
            Children = { header, slider },
        };
    }

    /// <summary>A small per-parameter reset that simply commits the neutral value.</summary>
    private static Button ResetGlyph(string name, string title, Action reset)
    {
        var button = new Button
        {
            Content = MapIcons.Stroked(MapIcons.Undo, 12),
            Padding = new Thickness(4, 1),
            Margin = new Thickness(4, 0),
        }.Named(name).Classed("icon");
        AutomationProperties.SetName(button, title);
        ToolTip.SetTip(button, title);
        button.Click += (_, _) => reset();
        return button;
    }

    /// <summary>A numeric field committing on Enter or focus loss; bad input is an error, not a command.</summary>
    private Control AppearanceNumberBox(
        string name, string label, string? unit, Func<double?> read, Action<double> apply,
        string format = "0.##", double captionWidth = 92)
    {
        var box = NumberBox(name);
        void Commit()
        {
            if (_syncingAppearance)
            {
                return;
            }

            var text = box.Text ?? "";
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value))
            {
                _editor.ReportInspectorError($"«{label}» должно быть числом.");
                SyncStatus();
                return;
            }

            apply(value);
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Commit();

        _appearanceSyncs.Add(() =>
        {
            if (!box.IsFocused)
            {
                box.Text = read()?.ToString(format, CultureInfo.InvariantCulture) ?? "";
            }
        });

        return Ui.PropertyRow(label, box, unit, captionWidth);
    }

    /// <summary>A colour field: #RRGGBB / #AARRGGBB, committing on Enter or focus loss.</summary>
    private Control AppearanceColorBox(
        string name, string label, Func<string?> read, Action<string> apply, double captionWidth = 92)
    {
        var box = new TextBox { Name = name, PlaceholderText = "#AARRGGBB" };
        void Commit()
        {
            if (!_syncingAppearance && (box.Text ?? "") != (read() ?? ""))
            {
                apply(box.Text ?? "");
            }
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Commit();

        _appearanceSyncs.Add(() =>
        {
            if (!box.IsFocused)
            {
                box.Text = read() ?? "";
            }
        });

        return Ui.PropertyRow(label, box, captionWidth: captionWidth);
    }

    /// <summary>A named on/off property whose checked state always mirrors the document.</summary>
    private ToggleButton AppearanceToggle(string name, string label, Func<bool?> read, Action<bool> apply)
    {
        var toggle = new ToggleButton { Content = label }.Named(name).Classed("segment");
        AutomationProperties.SetName(toggle, label);
        ToolTip.SetTip(toggle, label);
        toggle.Click += (_, _) =>
        {
            if (!_syncingAppearance)
            {
                apply(toggle.IsChecked == true);
            }
        };

        _appearanceSyncs.Add(() =>
        {
            var value = read() == true;
            toggle.IsChecked = value;
            Ui.SetClass(toggle, "active", value);
        });

        return toggle;
    }

    /// <summary>One dropdown over a fixed set of stored keys, shown with Russian labels.</summary>
    private Control AppearanceChoice(
        string name, string label, IReadOnlyList<(string Key, string Text)> options,
        Func<string?> read, Action<string> apply)
    {
        var combo = new ComboBox
        {
            Name = name,
            ItemsSource = options.Select(option => option.Text).ToList(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(combo, label);
        ToolTip.SetTip(combo, label);
        combo.SelectionChanged += (_, _) =>
        {
            if (!_syncingAppearance && combo.SelectedIndex >= 0
                && options[combo.SelectedIndex].Key != read())
            {
                apply(options[combo.SelectedIndex].Key);
            }
        };

        _appearanceSyncs.Add(() =>
        {
            var current = read();
            var index = 0;
            for (var i = 0; i < options.Count; i++)
            {
                if (options[i].Key == current)
                {
                    index = i;
                    break;
                }
            }

            combo.SelectedIndex = index;
        });

        return Ui.PropertyRow(label, combo);
    }

    private Control BlendChoice(string name, Func<string?> read, Action<string> apply) =>
        AppearanceChoice(
            name, "Смешивание",
            [.. BlendModes.Supported.Select(key => (key, EditorLabels.Blend(key)))],
            read, apply);

    /// <summary>
    /// The shared drop-shadow block: named state, three presets as real commands and the custom
    /// sliders, disclosed only because a shadow is a rare setting.
    /// </summary>
    private StackPanel ShadowBlock(
        string prefix,
        Func<(double OffsetX, double OffsetY, double Blur, double Opacity)?> read,
        Action none, Action standard,
        Action<double?, double?, double?, double?, bool> custom)
    {
        var state = new TextBlock { VerticalAlignment = VerticalAlignment.Center }
            .Classed("muted").Named(prefix + "ShadowStateText");

        var noneButton = Ui.TextButton(prefix + "ShadowNoneButton", "Нет", () =>
        {
            none();
            SyncAppearanceSections();
        }, "segment");
        var standardButton = Ui.TextButton(prefix + "ShadowStandardButton", "Стандартная", () =>
        {
            standard();
            SyncAppearanceSections();
        }, "segment");

        var sliders = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                AppearanceSlider(prefix + "ShadowOffsetXSlider", "Сдвиг по X", -64, 64, "px",
                    () => read()?.OffsetX, (v, c) => custom(v, null, null, null, c)),
                AppearanceSlider(prefix + "ShadowOffsetYSlider", "Сдвиг по Y", -64, 64, "px",
                    () => read()?.OffsetY, (v, c) => custom(null, v, null, null, c)),
                AppearanceSlider(prefix + "ShadowBlurSlider", "Размытие тени", 0, 64, "px",
                    () => read()?.Blur, (v, c) => custom(null, null, v, null, c)),
                AppearanceSlider(prefix + "ShadowOpacitySlider", "Плотность тени", 0, 100, "%",
                    () => read()?.Opacity * 100, (v, c) => custom(null, null, null, v, c), 5),
            },
        };

        _appearanceSyncs.Add(() =>
        {
            var shadow = read();
            state.Text = shadow is { } value
                ? EditorLabels.ShadowState(value.OffsetX, value.OffsetY, value.Blur, value.Opacity)
                : "";
        });

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new DockPanel
                {
                    Children =
                    {
                        state.Docked(Dock.Right),
                        new TextBlock { Text = "Тень", VerticalAlignment = VerticalAlignment.Center },
                    },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 2,
                    Children = { noneButton, standardButton },
                },
                sliders,
            },
        };
    }

    /// <summary>Runs every registered value sync with user-input handlers muted.</summary>
    private void SyncAppearanceSections()
    {
        _syncingAppearance = true;
        try
        {
            foreach (var sync in _appearanceSyncs)
            {
                sync();
            }
        }
        finally
        {
            _syncingAppearance = false;
        }
    }

    // ========================================================= selection panels

    private void BuildSelectionSections()
    {
        _selectionEmpty = Ui.EmptyState(
            "SelectionEmptyState",
            MapIcons.Select,
            "Ничего не выбрано",
            "Щёлкните объект на холсте или выберите его в списке «Объекты».");

        _inspectorPanel = BuildInspector();

        _stampSection = BuildStampSection();
        _lineSection = BuildLineSection();
        _shapeSection = BuildShapeSection();
        _textSection = BuildTextSection();

        _compositionSection = Ui.Section("Композиция", new StackPanel
        {
            Spacing = 5,
            Children =
            {
                Ui.TextButton("CopyCompositionButton", "Копировать композицию",
                    _editor.CopySelectedComposition, "compact"),
                Ui.AsyncButton("ExportCompositionPackageButton", "Экспорт package…",
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
            "Открывает библиотеку. Выберите набор и нажмите «Заменить ассет выделенного штампа» — " +
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

        return Ui.Section("Штамп", new StackPanel
        {
            Spacing = 5,
            Children = { replace, aspect, BuildStampEditor("SelectedStamp") },
        }).Named("StampObjectActions");
    }

    /// <summary>
    /// One binding surface for both a future stamp and a selected stamp. The read side follows
    /// <see cref="EditorViewModel.StampPanelStyle"/>; every write uses the same view-model API.
    /// </summary>
    private Control BuildStampEditor(string prefix)
    {
        StampStyle? Style() => _editor.StampPanelStyle;
        var heading = new TextBlock { Name = prefix + "Heading", TextWrapping = TextWrapping.Wrap }
            .Classed("role-panel");
        _appearanceSyncs.Add(() =>
        {
            heading.Text = _editor.StampPanelHeading;
            AutomationProperties.SetName(heading, heading.Text);
        });

        var flips = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 2,
            LineSpacing = 2,
            Children =
            {
                AppearanceToggle(prefix + "FlipHorizontalToggle", "Отразить по горизонтали",
                    () => Style()?.FlipHorizontal, value => _editor.SetStampFlip(horizontal: value)),
                AppearanceToggle(prefix + "FlipVerticalToggle", "Отразить по вертикали",
                    () => Style()?.FlipVertical, value => _editor.SetStampFlip(vertical: value)),
            },
        };

        var shadowColor = new TextBox
        {
            Name = prefix + "ShadowColorBox",
            MaxLength = 9,
            PlaceholderText = "#AARRGGBB",
        };
        void CommitShadowColor() => _editor.SetStampShadowColor(shadowColor.Text);
        shadowColor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitShadowColor(); e.Handled = true; }
        };
        shadowColor.LostFocus += (_, _) => CommitShadowColor();
        _appearanceSyncs.Add(() => shadowColor.Text = Style()?.ShadowColor ?? AppearanceColor.OpaqueBlack);

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
                    Ui.PropertyRow("Цвет тени", shadowColor),
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

        return new StackPanel
        {
            Spacing = 5,
            Children =
            {
                heading,
                AppearanceSlider(prefix + "ScaleSlider", "Масштаб", 10, 400, "%",
                    () => _editor.StampScalePercent,
                    (value, commit) => _editor.SetStampScalePercent(value, commit), 5,
                    ResetGlyph(prefix + "ScaleResetButton", "Сбросить масштаб к 100 %",
                        () => _editor.SetStampScalePercent(100))),
                AppearanceSlider(prefix + "RotationSlider", "Поворот", 0, 359, "°",
                    () => _editor.StampRotationDegrees,
                    (value, commit) => _editor.SetStampRotationDegrees(value, commit), 1,
                    ResetGlyph(prefix + "RotationResetButton", "Сбросить поворот",
                        () => _editor.SetStampRotationDegrees(0))),
                AppearanceSlider(prefix + "SublayerSlider", "Подслой", -10, 10, "",
                    () => _editor.StampSublayer,
                    (value, commit) => _editor.SetStampSublayer((int)Math.Round(value), commit)),
                flips,
                AppearanceSlider(prefix + "OpacitySlider", "Непрозрачность", 0, 100, "%",
                    () => _editor.StampOpacityPercent,
                    (value, commit) => _editor.SetStampOpacityPercent(value, commit), 5),
                clipMask,
                appearance,
            },
        };
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
        AutomationProperties.SetName(thumbnail, "Миниатюра Clip Mask");

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

        var toggle = new ToggleButton { Content = "Clip Mask" }
            .Named(prefix + "ClipMaskToggle").Classed("segment");
        AutomationProperties.SetName(toggle, "Включить Clip Mask");
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
            "Создать foreground",
            _editor.CreateStampForegroundSurface,
            "compact");
        var editForeground = Ui.TextButton(
            prefix + "EditStampForegroundButton",
            "Редактировать foreground",
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
                    new TextBlock { Text = "Clip Mask" }.Classed("role-panel"),
                    thumbnail,
                    toggle,
                    state,
                    new WrapPanel
                    {
                        Orientation = Orientation.Horizontal,
                        ItemSpacing = 4,
                        LineSpacing = 4,
                        Children = { assign, remove },
                    },
                    foregroundState,
                    new WrapPanel
                    {
                        Orientation = Orientation.Horizontal,
                        ItemSpacing = 4,
                        LineSpacing = 4,
                        Children = { createForeground, editForeground },
                    },
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
                    () => Stroke()?.Color, _editor.SetStrokeColor),
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
                $"{index + 1} · {node.Kind} · {node.X:0.###}, {node.Y:0.###}").ToList();
            nodeChoice.SelectedIndex = _editor.SelectedVectorNode is { } selected
                ? nodes.ToList().FindIndex(node => node.Id == selected.Id) : -1;
            var portals = _editor.SelectedVectorPortals;
            portalChoice.ItemsSource = portals.Select((portal, index) =>
                $"{index + 1} · {portal.Position:P1} · {portal.Width:0.##} px").ToList();
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
        var addX = NumberBox(shape ? "ShapeAddNodeXBox" : "LineAddNodeXBox"); addX.Text = "0.5";
        var addY = NumberBox(shape ? "ShapeAddNodeYBox" : "LineAddNodeYBox"); addY.Text = "0.5";
        var addNode = Ui.TextButton(shape ? "ShapeAddNodeButton" : "AddLineNodeButton", "Добавить узел по X/Y", () =>
        {
            if (!TryNumber(addX.Text, out var x) || !TryNumber(addY.Text, out var y)) return;
            if (shape) _editor.AddNodeToSelectedShape(x, y); else _editor.AddNodeToSelectedLine(x, y);
            SyncAppearanceSections();
        }, "compact");
        var position = NumberBox(shape ? "ShapePortalPositionBox" : "LinePortalPositionBox"); position.Text = "50";
        var width = NumberBox(shape ? "ShapePortalWidthBox" : "LinePortalWidthBox"); width.Text = "48";
        var addPortal = Ui.TextButton(shape ? "ShapeAddPortalButton" : "AddPortalButton", "Добавить проход по position/width", () =>
        {
            if (TryNumber(position.Text, out var p) && TryNumber(width.Text, out var w))
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
                Ui.PropertyRow("Новый X", addX), Ui.PropertyRow("Новый Y", addY), addNode,
                AppearanceNumberBox((shape ? "Shape" : "Line") + "NodeXBox", "X узла", null,
                    () => _editor.SelectedVectorNode?.X, value => EditNode(node => node with { X = value })),
                AppearanceNumberBox((shape ? "Shape" : "Line") + "NodeYBox", "Y узла", null,
                    () => _editor.SelectedVectorNode?.Y, value => EditNode(node => node with { Y = value })),
                AppearanceChoice((shape ? "Shape" : "Line") + "NodeKindChoice", "Тип узла",
                    [("Corner", "Прямой"), ("Curve", "Кривой")],
                    () => _editor.SelectedVectorNode?.Kind.ToString(),
                    key => EditNode(node => node with { Kind = Enum.Parse<VectorNodeKind>(key) })),
                AppearanceNumberBox((shape ? "Shape" : "Line") + "ControlInXBox", "Handle in X", null,
                    () => _editor.SelectedVectorNode?.ControlInX, value => EditNode(node => node with { ControlInX = value })),
                AppearanceNumberBox((shape ? "Shape" : "Line") + "ControlInYBox", "Handle in Y", null,
                    () => _editor.SelectedVectorNode?.ControlInY, value => EditNode(node => node with { ControlInY = value })),
                AppearanceNumberBox((shape ? "Shape" : "Line") + "ControlOutXBox", "Handle out X", null,
                    () => _editor.SelectedVectorNode?.ControlOutX, value => EditNode(node => node with { ControlOutX = value })),
                AppearanceNumberBox((shape ? "Shape" : "Line") + "ControlOutYBox", "Handle out Y", null,
                    () => _editor.SelectedVectorNode?.ControlOutY, value => EditNode(node => node with { ControlOutY = value })),
                new WrapPanel
                {
                    ItemSpacing = 4, LineSpacing = 4,
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
                portalChoice,
                Ui.PropertyRow("Position, %", position), Ui.PropertyRow("Width", width), addPortal,
                AppearanceNumberBox((shape ? "Shape" : "Line") + "SelectedPortalPosition", "Position", "%",
                    () => _editor.SelectedVectorPortal?.Position * 100,
                    value => _editor.SetSelectedPortal(value / 100, _editor.SelectedVectorPortal?.Width ?? 0)),
                AppearanceNumberBox((shape ? "Shape" : "Line") + "SelectedPortalWidth", "Width", "px",
                    () => _editor.SelectedVectorPortal?.Width,
                    value => _editor.SetSelectedPortal(_editor.SelectedVectorPortal?.Position ?? 0, value)),
                new WrapPanel
                {
                    ItemSpacing = 4, LineSpacing = 4,
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
                    Ui.TextButton("BooleanAddButton", "Add · две выделенные фигуры",
                        () => _editor.PreviewShapeBoolean(ShapeBooleanOperation.Add), "compact"),
                    Ui.TextButton("BooleanSubtractButton", "Subtract · две выделенные фигуры",
                        () => _editor.PreviewShapeBoolean(ShapeBooleanOperation.Subtract), "compact"),
                    Ui.TextButton("BooleanMergeButton", "Merge · две выделенные фигуры",
                        () => _editor.PreviewShapeBoolean(ShapeBooleanOperation.Merge), "compact"),
                    Ui.TextButton("BooleanApplyButton", "Применить показанный результат",
                        () => _editor.CommitShapeBooleanPreview(), "accent", "compact"),
                    Ui.TextButton("BooleanCancelButton", "Отменить preview",
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

        return Ui.Section("Фигура", new StackPanel
        {
            Spacing = 5,
            Children =
            {
                AppearanceColorBox("ShapeFillColorBox", "Цвет заливки",
                    () => Fill()?.Color, _editor.SetShapeFillColor),
                AppearanceSlider("ShapeFillOpacitySlider", "Плотность заливки", 0, 100, "%",
                    () => Fill()?.Opacity * 100, (v, c) => _editor.SetShapeFillOpacityPercent(v, c), 5),
                new TextBlock
                {
                    Text = "Texture fill выбирается в библиотеке; эти значения хранятся в Shape.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
                AppearanceSlider("ShapeTextureScaleSlider", "Масштаб texture", 5, 6400, "%",
                    () => Fill()?.TextureScale * 100,
                    (v, c) => _editor.SetShapeFillTextureScale(v / 100, c), 5),
                AppearanceSlider("ShapeTextureRotationSlider", "Поворот texture", -180, 180, "°",
                    () => Fill()?.TextureRotation,
                    (v, c) => _editor.SetShapeFillTextureRotation(v, c), 5),
                AppearanceSlider("ShapeTextureOffsetXSlider", "Texture X", -512, 512, "px",
                    () => Fill()?.TextureOffsetX,
                    (v, c) => _editor.SetShapeFillTextureOffsetX(v, c), 5),
                AppearanceSlider("ShapeTextureOffsetYSlider", "Texture Y", -512, 512, "px",
                    () => Fill()?.TextureOffsetY,
                    (v, c) => _editor.SetShapeFillTextureOffsetY(v, c), 5),
                Ui.TextButton("ShapeResetTextureButton", "Сбросить texture fill",
                    _editor.ResetShapeFillTexture, "compact"),
                AppearanceColorBox("ShapeStrokeColorBox", "Цвет обводки",
                    () => _editor.Session.SelectedObject?.Shape?.Stroke.Color, _editor.SetStrokeColor),
                AppearanceSlider("ShapeStrokeWidthSlider", "Толщина обводки", 0, 64, "px",
                    () => _editor.Session.SelectedObject?.Shape?.Stroke.Width,
                    (v, c) => _editor.SetStrokeWidth(v, c)),
                appearance,
                geometry,
            },
        }).Named("ShapeObjectActions");
    }

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
                        () => Style()?.OutlineColor, _editor.SetTextOutlineColor),
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
                        Orientation = Orientation.Horizontal,
                        Spacing = 2,
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
                    () => Style()?.FillColor, _editor.SetTextFillColor),
                new TextBlock
                {
                    Text = "Локальные шрифты Windows. Map не встраивает и не распространяет файлы шрифтов.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
                fontSearch,
                fontList,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    Children = { previousFont, nextFont },
                },
                AppearanceChoice("TextAlignmentChoice", "Выравнивание",
                    [("Left", EditorLabels.Alignment(Core.Documents.TextAlignment.Left)),
                     ("Center", EditorLabels.Alignment(Core.Documents.TextAlignment.Center)),
                     ("Right", EditorLabels.Alignment(Core.Documents.TextAlignment.Right))],
                    () => Style()?.Alignment.ToString(),
                    key => _editor.SetTextAlignment(Enum.Parse<Core.Documents.TextAlignment>(key))),
                font,
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

        _rotationInspectorRow = Ui.PropertyRow("Поворот", _rotationBox, "°");
        _sublayerInspectorRow = AppearanceNumberBox("SublayerBox", "Подслой", null,
            () => _editor.Session.SelectedObject?.Sublayer,
            value => _editor.SetSelectedSublayer((int)Math.Round(value)));
        _opacityInspectorRow = AppearanceSlider("ObjectOpacitySlider", "Непрозрачность", 0, 100, "%",
            () => _editor.Session.SelectedObject?.ObjectOpacity * 100,
            (value, commit) => _editor.SetSelectedOpacityPercent(value, commit), 5);

        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Ui.PropertyRow("X", _xBox, "px"),
                Ui.PropertyRow("Y", _yBox, "px"),
                Ui.PropertyRow("Ширина", _widthBox, "px"),
                Ui.PropertyRow("Высота", _heightBox, "px"),
                _rotationInspectorRow,
                apply,
                _sublayerInspectorRow,
                _opacityInspectorRow,
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
        DynamicTip(delete, () =>
            $"Удалить выделенный объект. Действие можно отменить ({_editor.Shortcuts.Get(EditorShortcutSettings.Commands.Undo)}).");

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
        _assetCatalogContent = BuildAssetsTab();
        _versionsContent = BuildCheckpointsTab();
        _canvasWorkflowContent = BuildCanvasWorkflowTab();
        _exportContent = BuildExportTab();
        _sideTabs = new TabControl
        {
            Name = "EditorSideTabs",
            ItemsSource = new[]
            {
                new TabItem { Header = "Слои", Content = BuildLayersTab() },
                new TabItem { Header = "Объекты", Content = BuildObjectsTab() },
            },
        };

        var collapse = Ui.TextButton("CollapseRightPanelButton", "›", ToggleRightPanel, "compact");
        AutomationProperties.SetName(collapse, "Свернуть или развернуть правую рабочую панель");
        _sideBody = _sideTabs;
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
                                new TextBlock { Text = "Рабочая панель" }.Classed("role-panel"),
                            },
                        },
                    }.Classed("panel-header").Docked(Dock.Top),
                    _sideBody,
                },
            },
        }.Classed("panel");
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

        var addMenu = new MenuFlyout
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
        var addLayerButton = Ui.TextButton("AddLayerButton", "Добавить слой", () => { }, "accent");
        addLayerButton.Flyout = addMenu;

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
                            addLayerButton,
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
        blendRow.IsVisible = false;

        var exactBlend = BlendChoice(
            "LayerBlendChoice", () => _editor.ActiveLayer?.Blend,
            value => _editor.SetActiveLayerBlend(value));
        var exactOpacity = AppearanceSlider(
            "LayerOpacitySlider", "Непрозрачность слоя", 0, 100, "%",
            () => _editor.ActiveLayer?.Opacity * 100,
            (value, _) => _editor.SetActiveLayerOpacity(value), 5);

        var (maskRow, maskButton) = Ui.ValueRow(
            "LayerMaskButton", "Маска", "Нет", _editor.ToggleActiveMask);
        _maskValue = maskButton;
        var maskCommands = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 4,
            LineSpacing = 4,
            Children =
            {
                Ui.TextButton("CopyLayerMaskButton", "Копировать маску", _editor.CopyActiveMask, "compact"),
                Ui.TextButton("PasteLayerMaskButton", "Вставить копию", () => _editor.PasteCopiedMask(), "compact"),
                Ui.AsyncButton("RemoveLayerMaskButton", "Удалить маску…", RemoveActiveMaskAsync,
                    "destructive", "compact"),
            },
        };

        var selectedSublayer = 0;
        LayerEffects? Effects() => _editor.ActiveObjectLayerEffects;
        void ChangeEffects(Func<LayerEffects, LayerEffects> change) =>
            _editor.SetActiveObjectLayerEffects(change(Effects() ?? new LayerEffects()));
        var objectEffects = Ui.Section("Object layer · эффекты и подслой", new StackPanel
        {
            Spacing = 4,
            Children =
            {
                AppearanceToggle("ObjectLayerClippingToggle", "Clipping к слою ниже",
                    () => Effects()?.ClippingEnabled,
                    value => ChangeEffects(current => current with { ClippingEnabled = value })),
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
        });
        _appearanceSyncs.Add(() => objectEffects.IsVisible = _editor.ActiveLayer is ObjectLayer);

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
        DynamicTip(delete, () =>
            $"Удалить слой вместе с содержимым. Действие можно отменить ({_editor.Shortcuts.Get(EditorShortcutSettings.Commands.Undo)}).");

        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                nameRow, visibilityRow, lockRow, blendRow, exactOpacity, exactBlend,
                maskRow, maskCommands, objectEffects, orderRow, delete,
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
                "Композиция станет одним raster stamp. Исходная vector-композиция останется редактируемой до подтверждения; после операции её можно вернуть через Undo.",
                "Растеризовать", "FlattenCompositionConfirmButton", destructive: true))
        {
            _editor.FlattenSelectedComposition();
            SyncSidePanels();
        }
    }

    private Control BuildLandLayerBlock()
    {
        LandEdgeStyle? Style() => (_editor.ActiveLayer as LandLayer)?.EdgeStyle;
        var (row, button) = Ui.ValueRow(
            "LandStyleButton", "Берег", "Обрыв", _editor.CycleActiveLandStyle);
        _landEdgeValue = button;
        return Ui.Section("Земля", new StackPanel
        {
            Spacing = 4,
            Children =
            {
                row,
                AppearanceColorBox("LandFillColorBox", "Цвет суши", () => Style()?.FillColor,
                    value =>
                    {
                        if (AppearanceColor.Normalize(value) is { } color)
                            _editor.SetActiveLandStyle(style => style with { FillColor = color });
                    }),
                AppearanceColorBox("LandOutlineColorBox", "Цвет контура", () => Style()?.OutlineColor,
                    value =>
                    {
                        if (AppearanceColor.Normalize(value) is { } color)
                            _editor.SetActiveLandStyle(style => style with { OutlineColor = color });
                    }),
                AppearanceSlider("LandOutlineWidthSlider", "Толщина контура", 0, 64, "px",
                    () => Style()?.OutlineWidth,
                    (v, _) => _editor.SetActiveLandStyle(style => style with { OutlineWidth = v })),
                AppearanceSlider("LandInnerShadowOpacitySlider", "Внутренняя тень", 0, 100, "%",
                    () => Style()?.InnerShadowOpacity * 100,
                    (v, _) => _editor.SetActiveLandStyle(style => style with { InnerShadowOpacity = v / 100 }), 5),
                AppearanceSlider("LandOuterShadowOpacitySlider", "Внешняя тень", 0, 100, "%",
                    () => Style()?.OuterShadowOpacity * 100,
                    (v, _) => _editor.SetActiveLandStyle(style => style with { OuterShadowOpacity = v / 100 }), 5),
                AppearanceSlider("LandShadowBlurSlider", "Размытие теней", 0, 64, "px",
                    () => Style()?.ShadowBlur,
                    (v, _) => _editor.SetActiveLandStyle(style => style with { ShadowBlur = v })),
                AppearanceNumberBox("LandShadowOffsetXBox", "Тень X", "px",
                    () => Style()?.ShadowOffsetX,
                    v => _editor.SetActiveLandStyle(style => style with { ShadowOffsetX = v })),
                AppearanceNumberBox("LandShadowOffsetYBox", "Тень Y", "px",
                    () => Style()?.ShadowOffsetY,
                    v => _editor.SetActiveLandStyle(style => style with { ShadowOffsetY = v })),
                AppearanceColorBox("LandRippleColorBox", "Цвет ряби", () => Style()?.RippleColor,
                    value =>
                    {
                        if (AppearanceColor.Normalize(value) is { } color)
                            _editor.SetActiveLandStyle(style => style with { RippleColor = color });
                    }),
                AppearanceSlider("LandRippleWidthSlider", "Толщина ряби", 0, 32, "px",
                    () => Style()?.RippleWidth,
                    (v, _) => _editor.SetActiveLandStyle(style => style with { RippleWidth = v })),
                AppearanceSlider("LandRippleSpacingSlider", "Шаг ряби", 1, 128, "px",
                    () => Style()?.RippleSpacing,
                    (v, _) => _editor.SetActiveLandStyle(style => style with { RippleSpacing = v })),
                AppearanceSlider("LandRippleCountSlider", "Число линий ряби", 0, 12, "",
                    () => Style()?.RippleCount,
                    (v, _) => _editor.SetActiveLandStyle(style => style with { RippleCount = (int)Math.Round(v) })),
            },
        }).Named("LandLayerBlock");
    }

    private Control BuildGridLayerBlock()
    {
        GridDefinition? Grid() => _editor.ActiveLayer is GridLayer { Grids.Count: > 0 } layer
            ? layer.Grids[0]
            : null;

        var geometryButtons = new Dictionary<GridGeometry, RadioButton>();
        var geometryChoices = new StackPanel { Spacing = 2 };
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
        _appearanceSyncs.Add(() =>
        {
            var selected = Grid()?.Geometry;
            foreach (var pair in geometryButtons)
                pair.Value.IsChecked = pair.Key == selected;
        });

        var exactProperties = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                AppearanceNumberBox("GridCellSizeBox", "Размер ячейки", "px",
                    () => Grid()?.CellSize, value => _editor.SetActiveGridCellSize(value), "0.######", 116),
                AppearanceNumberBox("GridSubdivisionsBox", "Деления", null,
                    () => Grid()?.Subdivisions, value => _editor.SetActiveGridSubdivisions(value), "0", 116),
                AppearanceNumberBox("GridLineWidthBox", "Толщина линии", "px",
                    () => Grid()?.LineWidth, value => _editor.SetActiveGridLineWidth(value), "0.######", 116),
                AppearanceColorBox("GridColorBox", "Цвет линии",
                    () => Grid()?.Color, value => _editor.SetActiveGridColor(value), 116),
                AppearanceNumberBox("GridOpacityBox", "Непрозрачность", "%",
                    () => Grid() is { } grid ? grid.Opacity * 100 : null,
                    value => _editor.SetActiveGridOpacityPercent(value), "0.######", 116),
                AppearanceNumberBox("GridOffsetXBox", "Смещение X", "px",
                    () => Grid()?.OffsetX, value => _editor.SetActiveGridOffsetX(value), "0.######", 116),
                AppearanceNumberBox("GridOffsetYBox", "Смещение Y", "px",
                    () => Grid()?.OffsetY, value => _editor.SetActiveGridOffsetY(value), "0.######", 116),
                AppearanceNumberBox("GridRotationBox", "Поворот", "°",
                    () => Grid()?.RotationDegrees, value => _editor.SetActiveGridRotation(value), "0.######", 116),
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
        var (row, button) = Ui.ValueRow(
            "FilterStyleButton", "Пресет", "Тёплый", _editor.CycleActiveFilter);
        _filterPresetValue = button;
        return Ui.Section("Фильтр", new StackPanel
        {
            Spacing = 4,
            Children =
            {
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

        // The compact type filter Ctrl+A honours together with the search box above it.
        var filterStrip = new WrapPanel { };
        foreach (var filter in new[]
                 {
                     ObjectTypeFilter.All, ObjectTypeFilter.Stamps, ObjectTypeFilter.Text,
                     ObjectTypeFilter.Lines, ObjectTypeFilter.Shapes, ObjectTypeFilter.Other,
                 })
        {
            var captured = filter;
            var title = ObjectTreeRows.FilterTitle(filter);
            var button = new ToggleButton { Content = title, Margin = new Thickness(0, 0, 2, 2) }
                .Named("ObjectTypeFilter" + filter)
                .Classed("segment");
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

        var selectAll = ShortcutTextButton(
            "SelectAllButton", "Выбрать всё", EditorShortcutSettings.Commands.SelectAll, () =>
        {
            _editor.SelectAllByFilter();
            RefreshAfterSelectionCommand();
        }, "compact");
        DynamicTip(selectAll, () =>
            $"Выбрать видимые незаблокированные объекты по текущему поиску и фильтру типа " +
            $"({_editor.Shortcuts.Get(EditorShortcutSettings.Commands.SelectAll)}).");

        var header = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                _objectSearch,
                filterStrip,
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
                    Ui.TextButton("PasteCompositionButton", "Вставить композицию",
                        _editor.PasteComposition, "compact"),
                    Ui.AsyncButton("ImportCompositionPackageButton", "Импорт package…",
                        ImportCompositionPackageAsync, "compact"),
                },
            },
        }.Classed("group");

        var listHost = new Grid
        {
            Children = { _objectList, _objectEmptyStateHost },
        };

        var body = new Grid
        {
            Margin = new Thickness(12, 10, 12, 18),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 10,
            Children = { header, listHost, compositionActions },
        };
        Grid.SetRow(listHost, 1);
        Grid.SetRow(compositionActions, 2);
        return body;
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
                Ui.TextButton(
                    "UseSelectedStampButton",
                    "Выбрать и разместить на холсте",
                    CloseAssetCatalogAndArmPlacement,
                    "accent"),
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
            ("One", "Один", null),
            ("Series", "Серия", null),
            ("Area", "Россыпь", null),
        ], key =>
        {
            _editor.StampPlacementMode = key switch
            {
                "Series" => StampPlacementMode.Series,
                "Area" => StampPlacementMode.Area,
                _ => StampPlacementMode.One,
            };
            SyncAssetList();
        });
        _placementModeButtons["One"] = modeButtons[0];
        _placementModeButtons["Series"] = modeButtons[1];
        _placementModeButtons["Area"] = modeButtons[2];

        var (autoSelect, autoSelectButtons) = Ui.Segmented("PlacementAutoSelect", [
            ("Auto", "Авто", null),
            ("Always", "Всегда", null),
            ("Off", "Выкл.", null),
        ], key =>
        {
            _editor.PlacementAutoSelect = key switch
            {
                "Always" => PlacementAutoSelectPolicy.Always,
                "Off" => PlacementAutoSelectPolicy.Off,
                _ => PlacementAutoSelectPolicy.Auto,
            };
            SyncStampGestureReadout();
        });
        _autoSelectButtons[PlacementAutoSelectPolicy.Auto] = autoSelectButtons[0];
        _autoSelectButtons[PlacementAutoSelectPolicy.Always] = autoSelectButtons[1];
        _autoSelectButtons[PlacementAutoSelectPolicy.Off] = autoSelectButtons[2];

        // Labelled, not icon-only: these three decide whether a scatter multiplies the variant or
        // turns and mirrors every copy, and a user must be able to see which without hovering.
        _randomVariantToggle = LabelledToggle(
            "RandomVariantToggle", "Смешивать варианты", "Случайный вариант после размещения",
            MapIcons.Duplicate, () =>
            {
                _editor.RandomVariantAfterPlacement = !_editor.RandomVariantAfterPlacement;
                SyncAssetList();
            });
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
                AppearanceSlider("StampAreaSlider", "Радиус", 1, 1024, "ед.",
                    () => _editor.StampAreaRadius,
                    (value, commit) => _editor.SetStampAreaRadius(value, commit)),
                AppearanceSlider("StampDensitySlider", "Плотность", 0, 400, "",
                    () => _editor.StampDensity,
                    (value, commit) => _editor.SetStampDensity(value, commit)),
                _stampAreaSummaryText,
                Disclosure(
                    "StampScatterDetails",
                    "Больше параметров",
                    new StackPanel
                    {
                        Spacing = 4,
                        Children = { _randomVariantToggle, randomAngleChoice, _randomFlipToggle },
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
                    new TextBlock { Text = "Автовыбор" }.Classed("role-panel"),
                    autoSelect,
                    _stampAreaControls,
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
                            AssetCommand("PlaceStampButton", "Разместить в центре вида", () =>
                            {
                                _editor.PlaceCurrentVariantAtViewCentre();
                                RefreshLibraryPanel();
                            }, "compact"),
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
                            AssetCommand("RepairAssetSourceButton", "Найти исходник…",
                                () => _ = RepairSelectedAssetAsync(), "compact"),
                            AssetCommand("RemoveAssetRecordButton", "Удалить из каталога…",
                                () => _ = RemoveSelectedAssetAsync(), "destructive", "compact"),
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
        SetSegment(_placementModeButtons, _editor.StampPlacementMode switch
        {
            StampPlacementMode.Series => "Series",
            StampPlacementMode.Area => "Area",
            _ => "One",
        });
        foreach (var pair in _autoSelectButtons)
        {
            Ui.SetSelected(pair.Value, pair.Key == _editor.PlacementAutoSelect);
        }
        SyncStampToolPanel();
    }

    private Control BuildCompactStampCatalog()
    {
        _stampToolThumbnail = new Image
        {
            Width = 72,
            Height = 72,
            Stretch = Stretch.Uniform,
        };
        _stampRecentStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _stampFavoriteStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var variantButtons = new StackPanel
        {
            Margin = new Thickness(8, 0, 0, 0),
            Spacing = 4,
            Children =
            {
                Ui.TextButton("StampPreviousVariantButton", "Предыдущий", () =>
                {
                    _editor.PreviousVariant();
                    SyncStampToolPanel();
                }, "compact"),
                Ui.TextButton("StampNextVariantButton", "Следующий", () =>
                {
                    _editor.NextVariant();
                    SyncStampToolPanel();
                }, "compact"),
            },
        };
        Grid.SetColumn(variantButtons, 1);
        var currentVariant = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("72,*"),
            Children = { _stampToolThumbnail, variantButtons },
        };

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                currentVariant,
                new TextBlock { Text = "Недавние" }.Classed("faint", "role-caption"),
                _stampRecentStrip,
                new TextBlock { Text = "Избранное" }.Classed("faint", "role-caption"),
                _stampFavoriteStrip,
            },
        };
    }

    private Button CompactStampSetButton(AssetSetSummary summary, string prefix)
    {
        var image = _thumbnails.Image(_editor.LibraryThumbnailPath(summary));
        var button = new Button
        {
            Name = prefix + "_" + Sanitize(summary.SetId),
            Width = 72,
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
                        Child = image is null
                            ? MapIcons.Stroked(MapIcons.Missing, 18)
                            : new Image { Source = image, Stretch = Stretch.Uniform },
                    },
                    new TextBlock
                    {
                        Text = summary.Name,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextAlignment = Avalonia.Media.TextAlignment.Center,
                        FontSize = 10,
                    },
                },
            },
        }.Classed("compact");
        AutomationProperties.SetName(button, $"Выбрать набор {summary.Name}");
        button.Click += (_, _) =>
        {
            _editor.SelectAssetSet(summary.SetId);
            SyncStampToolPanel();
        };
        return button;
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
        _stampToolThumbnail.Source = _thumbnails.Image(
            _editor.CurrentVariant is { } variant
                ? _editor.LibraryThumbnailPathForVariant(variant.Id)
                : null);
        _stampRecentStrip.Children.Clear();
        foreach (var recent in _editor.CompactRecentSets)
        {
            _stampRecentStrip.Children.Add(CompactStampSetButton(recent, "StampRecentSet"));
        }
        _stampFavoriteStrip.Children.Clear();
        foreach (var favorite in _editor.CompactFavoriteSets)
        {
            _stampFavoriteStrip.Children.Add(CompactStampSetButton(favorite, "StampFavoriteSet"));
        }
        _stampRecentStrip.IsVisible = _stampRecentStrip.Children.Count > 0;
        _stampFavoriteStrip.IsVisible = _stampFavoriteStrip.Children.Count > 0;
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
        private const int MaxThumbnailDimension = 512;
        private const long MaxDecodedBytes = 64L * 1024 * 1024;
        private readonly Dictionary<string, (Bitmap Image, long Used, long Bytes)> _entries =
            new(StringComparer.OrdinalIgnoreCase);
        private long _clock;
        private long _decodedBytes;

        public Bitmap? Image(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

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
                if (metadata.Width > MaxThumbnailDimension || metadata.Height > MaxThumbnailDimension)
                {
                    return null;
                }

                while (_entries.Count >= capacity
                    || _decodedBytes > MaxDecodedBytes - metadata.DecodedBytes)
                {
                    var oldest = _entries.OrderBy(pair => pair.Value.Used).First();
                    _decodedBytes -= oldest.Value.Bytes;
                    oldest.Value.Image.Dispose();
                    _entries.Remove(oldest.Key);
                }

                using var stream = new MemoryStream(encoded, writable: false);
                bitmap = new Bitmap(stream);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException)
            {
                return null;
            }

            _entries[path] = (bitmap, ++_clock, metadata.DecodedBytes);
            _decodedBytes += metadata.DecodedBytes;
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
            PlaceholderText = "Относительная ссылка, https://… или mailto:…",
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
                preview.Text = "Preview недоступен: " + error.Message;
                Ui.SetClass(preview, "error", true);
            }
        }
        _noteMarkdown.TextChanged += (_, _) => RefreshPreview();
        RefreshPreview();

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
                            new WrapPanel
                            {
                                ItemSpacing = 4, LineSpacing = 4,
                                Children =
                                {
                                    Ui.TextButton("NoteHeadingButton", "Heading", () => PrefixSelection("## "), "compact"),
                                    Ui.TextButton("NoteListButton", "List", () => PrefixSelection("- "), "compact"),
                                    Ui.TextButton("NoteBoldButton", "Bold", () => ReplaceSelection("**", "**", "текст"), "compact"),
                                    Ui.TextButton("NoteItalicButton", "Italic", () => ReplaceSelection("*", "*", "текст"), "compact"),
                                },
                            },
                            linkTarget,
                            Ui.TextButton("NoteLinkButton", "Link для выделения", () =>
                                ReplaceSelection("[", $"]({(linkTarget.Text ?? "").Trim()})", "текст ссылки"), "compact"),
                            new TextBlock { Text = "Raw Markdown" }.Classed("role-panel"),
                            _noteMarkdown,
                            new TextBlock { Text = "Rendered preview · локальный безопасный subset" }.Classed("role-panel"),
                            new Border { Padding = new Thickness(8), Child = preview }.Classed("group"),
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

    private Control BuildCheckpointsTab()
    {
        var create = Ui.TextButton("CreateVersionButton", "Создать версию", () =>
        {
            _editor.CreateVersion();
            SyncCheckpointList();
            SyncStatus();
        }, "accent", "compact");
        AutomationProperties.SetName(create, "Создать версию текущей карты");

        var body = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Версии" }.Classed("role-panel"),
                new TextBlock
                {
                    Text = "Одна кнопка сохраняет состояние, время и preview. Название можно изменить позже.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
                create,
                new TextBlock
                {
                    Name = "CheckpointLimitsText",
                    Text = $"До {MapCheckpointLimits.MaxCount} версий · всего до "
                        + $"{MapCheckpointLimits.MaxTotalStoredBytes / (1024 * 1024)} MiB",
                }.Classed("faint", "role-caption"),
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
            }, "segment");
        _regionModeButton = Ui.TextButton(
            "RegionModeButton", "Создать карту из региона",
            () =>
            {
                _editor.SetCanvasWorkflowMode(ResizeRegionMode.Region);
                SyncCanvasWorkflow();
                _canvas.FitToCanvas();
            }, "segment");

        var modeRow = new UniformGrid
        {
            Columns = 1,
            Rows = 2,
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

        var anchorGrid = new UniformGrid { Columns = 3, Rows = 3 };
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
                }, "segment");
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
            }, "segment");
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
                Field("Итоговая ширина, px", _exportOutputWidthBox),
                Field("Итоговая высота, px", _exportOutputHeightBox),
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
        var close = Ui.TextButton("CloseExportButton", "Закрыть", () =>
        {
            _editor.CloseExport();
            _exportWindow?.Hide();
            SyncExport();
        }, "ghost");

        _exportFormatExplanation.TextWrapping = TextWrapping.Wrap;
        _exportEstimate.TextWrapping = TextWrapping.Wrap;
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
                new TextBlock
                {
                    Text = "Preview и итог используют один снимок карты и один scene renderer. Заметки и элементы редактора в изображение не попадают.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
                formats,
                _exportFormatExplanation,
                areas,
                _exportRegionPanel,
                outputSize,
                _exportAspectToggle,
                modes,
                _exportTransparencyToggle,
                _exportGridToggle,
                _exportQualityPanel,
                previewHost,
                _exportEstimate,
                _exportError,
                _exportProgress,
                _exportProgressText,
                _exportResult,
                _startExportButton,
                _cancelExportButton,
                _openExportFolderButton,
                close,
            },
        };

        SyncExport();
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = body,
        };

        ToggleButton Toggle(string name, string label, Action<bool> write)
        {
            var toggle = new ToggleButton
            {
                Name = name,
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            }.Classed("segment");
            AutomationProperties.SetName(toggle, label);
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

        void Wire(TextBox box, Action<string> write) => box.TextChanged += (_, _) =>
        {
            if (!_syncingExport)
            {
                write(box.Text ?? "");
                SyncExport();
            }
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

    private void OnShortcutsChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(SyncShortcutSurfaces);

    private void SyncShortcutSurfaces()
    {
        SetCommandTip(_undoButton, "Отменить", EditorShortcutSettings.Commands.Undo);
        SetCommandTip(_redoButton, "Повторить", EditorShortcutSettings.Commands.Redo);
        SetCommandTip(_saveButton, "Сохранить проект", EditorShortcutSettings.Commands.Save);
        SetCommandTip(_shortcutSettingsButton, "Настроить сочетания", EditorShortcutSettings.Commands.ShortcutSettings);

        foreach (var (tool, button) in _toolButtons)
        {
            var title = EditorLabels.Tool(tool);
            var shortcut = ToolShortcut(tool);
            var text = shortcut.Length > 0 ? $"{title}  ({shortcut})" : title;
            AutomationProperties.SetName(button, title);
            AutomationProperties.SetHelpText(button, text);
            ToolTip.SetTip(button, text);
        }

        if (_previousVariantButton is not null)
            SetCommandTip(_previousVariantButton, "Предыдущий вариант", EditorShortcutSettings.Commands.StampPreviousVariant);
        if (_nextVariantButton is not null)
            SetCommandTip(_nextVariantButton, "Следующий вариант", EditorShortcutSettings.Commands.StampNextVariant);
        foreach (var item in _commandButtons)
            SetCommandTip(item.Button, item.Label, item.Command);
        foreach (var sync in _shortcutSurfaceSyncs)
            sync();

        SyncHint();
    }

    private void SetCommandTip(Button button, string label, string command)
    {
        if (button is null) return;
        var shortcut = _editor.Shortcuts.Get(command);
        var text = shortcut.Length > 0 ? $"{label}  ({shortcut})" : label;
        AutomationProperties.SetName(button, text);
        AutomationProperties.SetHelpText(button, text);
        ToolTip.SetTip(button, text);
    }

    private string ToolShortcut(EditorTool tool) =>
        _editor.Shortcuts.Get(EditorShortcutSettings.ToolCommand(tool));

    private Button ShortcutTextButton(
        string name,
        string label,
        string command,
        Action action,
        params string[] classes)
    {
        var button = Ui.TextButton(name, label, action, classes);
        _shortcutSurfaceSyncs.Add(() =>
        {
            var gesture = _editor.Shortcuts.Get(command);
            var text = gesture.Length > 0 ? $"{label} ({gesture})" : label;
            button.Content = text;
            AutomationProperties.SetName(button, text);
            AutomationProperties.SetHelpText(button, text);
            ToolTip.SetTip(button, text);
        });
        return button;
    }

    private void DynamicTip(Control control, Func<string> text)
    {
        _shortcutSurfaceSyncs.Add(() =>
        {
            var value = text();
            AutomationProperties.SetHelpText(control, value);
            ToolTip.SetTip(control, value);
        });
    }

    private WrapPanel ShortcutChipRow(IReadOnlyList<string> commands, string meaning)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        _shortcutSurfaceSyncs.Add(() =>
        {
            row.Children.Clear();
            foreach (var command in commands)
            {
                var gesture = _editor.Shortcuts.Get(command);
                if (gesture.Length == 0) continue;
                var chip = Ui.KeyChip(gesture);
                chip.Margin = new Thickness(0, 2, 4, 2);
                row.Children.Add(chip);
            }

            row.Children.Add(new TextBlock
            {
                Text = meaning,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
            }.Classed("faint", "role-caption"));
        });
        return row;
    }

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
                or nameof(EditorViewModel.SelectionCount)
                or nameof(EditorViewModel.SelectedGroupId)
                or nameof(EditorViewModel.ActiveLayer)
                or nameof(EditorViewModel.PendingPlacement)
                or nameof(EditorViewModel.PlacementAutoSelect)
                or nameof(EditorViewModel.StampPlacementMode))
            {
                SyncContextArea();
                SyncStampGestureReadout();
            }

            if (e.PropertyName is nameof(EditorViewModel.StampToolSettings)
                or nameof(EditorViewModel.CurrentVariant)
                or nameof(EditorViewModel.CompactRecentSets)
                or nameof(EditorViewModel.CompactFavoriteSets))
            {
                SyncAppearanceSections();
                SyncStampToolPanel();
            }

            if (e.PropertyName is nameof(EditorViewModel.CanvasWorkflowOpen)
                or nameof(EditorViewModel.CanvasWorkflowMode)
                or nameof(EditorViewModel.CanvasAnchor)
                or nameof(EditorViewModel.CanvasBoundarySnap)
                or nameof(EditorViewModel.CanvasWidthText)
                or nameof(EditorViewModel.CanvasHeightText)
                or nameof(EditorViewModel.RegionXText)
                or nameof(EditorViewModel.RegionYText)
                or nameof(EditorViewModel.RegionWidthText)
                or nameof(EditorViewModel.RegionHeightText)
                or nameof(EditorViewModel.RegionTitleText)
                or nameof(EditorViewModel.CanvasWorkflowSummary)
                or nameof(EditorViewModel.CanvasObjectSummary)
                or nameof(EditorViewModel.CanvasWorkflowError))
            {
                SyncCanvasWorkflow();
            }

            if (e.PropertyName?.StartsWith("Export", StringComparison.Ordinal) == true
                || e.PropertyName is nameof(EditorViewModel.CurrentExportPlan)
                    or nameof(EditorViewModel.CanStartExport)
                    or nameof(EditorViewModel.CanCancelExport)
                    or nameof(EditorViewModel.CanOpenExportFolder))
            {
                SyncExport();
            }
        });

    private void SyncCanvasWorkflow()
    {
        if (_resizeModeButton is null || _regionModeButton is null
            || _resizeWorkflowPanel is null || _regionWorkflowPanel is null
            || _boundarySnapToggle is null)
        {
            return;
        }

        _syncingCanvasWorkflow = true;
        try
        {
            SetText(_canvasWidthBox, _editor.CanvasWidthText);
            SetText(_canvasHeightBox, _editor.CanvasHeightText);
            SetText(_regionXBox, _editor.RegionXText);
            SetText(_regionYBox, _editor.RegionYText);
            SetText(_regionWidthBox, _editor.RegionWidthText);
            SetText(_regionHeightBox, _editor.RegionHeightText);
            SetText(_regionTitleBox, _editor.RegionTitleText);

            Ui.SetSelected(_resizeModeButton, _editor.CanvasWorkflowMode == ResizeRegionMode.Resize);
            Ui.SetSelected(_regionModeButton, _editor.CanvasWorkflowMode == ResizeRegionMode.Region);
            _resizeWorkflowPanel.IsVisible = _editor.CanvasWorkflowMode == ResizeRegionMode.Resize;
            _regionWorkflowPanel.IsVisible = _editor.CanvasWorkflowMode == ResizeRegionMode.Region;
            _boundarySnapToggle.IsChecked = _editor.CanvasBoundarySnap;
            Ui.SetClass(_boundarySnapToggle, "active", _editor.CanvasBoundarySnap);

            foreach (var (anchor, button) in _anchorButtons)
            {
                Ui.SetSelected(button, anchor == _editor.CanvasAnchor);
            }

            _canvasWorkflowSummary.Text = _editor.CanvasWorkflowSummary;
            _canvasObjectSummary.Text = _editor.CanvasObjectSummary;
            _canvasWorkflowError.Text = _editor.CanvasWorkflowError;
            _canvasWorkflowError.IsVisible = _editor.CanvasWorkflowError.Length > 0;
        }
        finally
        {
            _syncingCanvasWorkflow = false;
        }

        static void SetText(TextBox box, string value)
        {
            if (!string.Equals(box.Text, value, StringComparison.Ordinal))
            {
                box.Text = value;
            }
        }
    }

    private void SyncExport()
    {
        if (_startExportButton is null || _cancelExportButton is null
            || _openExportFolderButton is null || _exportAspectToggle is null
            || _exportTransparencyToggle is null || _exportGridToggle is null
            || _exportQualitySlider is null || _exportRegionPanel is null
            || _exportQualityPanel is null)
        {
            return;
        }

        _syncingExport = true;
        try
        {
            SetText(_exportRegionXBox, _editor.ExportRegionXText);
            SetText(_exportRegionYBox, _editor.ExportRegionYText);
            SetText(_exportRegionWidthBox, _editor.ExportRegionWidthText);
            SetText(_exportRegionHeightBox, _editor.ExportRegionHeightText);
            SetText(_exportOutputWidthBox, _editor.ExportOutputWidthText);
            SetText(_exportOutputHeightBox, _editor.ExportOutputHeightText);

            foreach (var (format, button) in _exportFormatButtons)
                Ui.SetSelected(button, format == _editor.ExportFormat);
            foreach (var (area, button) in _exportAreaButtons)
                Ui.SetSelected(button, area == _editor.ExportAreaMode);
            foreach (var (mode, button) in _exportModeButtons)
                Ui.SetSelected(button, mode == _editor.ExportOutputMode);

            _exportRegionPanel.IsVisible = _editor.ExportAreaMode == ExportAreaMode.Region;
            _exportQualityPanel.IsVisible = _editor.ExportQualityVisible;
            _exportAspectToggle.IsChecked = _editor.ExportAspectLocked;
            _exportTransparencyToggle.IsChecked = _editor.ExportTransparentBackground;
            _exportTransparencyToggle.IsEnabled = _editor.ExportTransparencyAvailable
                && !_editor.ExportRunning;
            _exportGridToggle.IsChecked = _editor.ExportIncludeGrid;
            _exportQualitySlider.Value = _editor.ExportQuality;
            _exportQualityText.Text = $"Качество: {_editor.ExportQuality}";
            _exportFormatExplanation.Text = _editor.ExportFormatExplanation;
            _exportEstimate.Text = _editor.ExportEstimateText;
            _exportError.Text = _editor.ExportValidationError;
            _exportError.IsVisible = _editor.ExportValidationError.Length > 0;
            _exportProgress.Value = _editor.ExportProgress * 100;
            _exportProgress.IsVisible = _editor.ExportRunning || _editor.ExportProgress > 0;
            _exportProgressText.Text = _editor.ExportPreviewBusy
                ? "Обновление bounded preview…"
                : _editor.ExportProgressText;
            _exportProgressText.IsVisible = _exportProgressText.Text?.Length > 0;
            _exportResult.Text = _editor.ExportResultText;
            _exportResult.IsVisible = _editor.ExportResultText.Length > 0;

            if (!ReferenceEquals(_shownExportPreviewPng, _editor.ExportPreviewPng))
            {
                _shownExportPreviewPng = _editor.ExportPreviewPng;
                _exportPreviewBitmap?.Dispose();
                _exportPreviewBitmap = null;
                _exportPreview.Source = null;
                if (_shownExportPreviewPng is { Length: > 0 } bytes)
                {
                    try
                    {
                        using var stream = new MemoryStream(bytes, writable: false);
                        _exportPreviewBitmap = new Bitmap(stream);
                        _exportPreview.Source = _exportPreviewBitmap;
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException)
                    {
                        _exportPreview.Source = null;
                    }
                }
            }

            var settingsEnabled = !_editor.ExportRunning;
            foreach (var button in _exportFormatButtons.Values) button.IsEnabled = settingsEnabled;
            foreach (var button in _exportAreaButtons.Values) button.IsEnabled = settingsEnabled;
            foreach (var button in _exportModeButtons.Values) button.IsEnabled = settingsEnabled;
            _exportRegionXBox.IsEnabled = settingsEnabled;
            _exportRegionYBox.IsEnabled = settingsEnabled;
            _exportRegionWidthBox.IsEnabled = settingsEnabled;
            _exportRegionHeightBox.IsEnabled = settingsEnabled;
            _exportOutputWidthBox.IsEnabled = settingsEnabled;
            _exportOutputHeightBox.IsEnabled = settingsEnabled;
            _exportAspectToggle.IsEnabled = settingsEnabled;
            _exportGridToggle.IsEnabled = settingsEnabled;
            _exportQualitySlider.IsEnabled = settingsEnabled;
            _startExportButton.IsEnabled = _editor.CanStartExport;
            _cancelExportButton.IsEnabled = _editor.CanCancelExport;
            _cancelExportButton.IsVisible = _editor.ExportRunning;
            _openExportFolderButton.IsEnabled = _editor.CanOpenExportFolder;
            _openExportFolderButton.IsVisible = _editor.ExportResultPath is not null;
        }
        finally
        {
            _syncingExport = false;
        }

        static void SetText(TextBox box, string value)
        {
            if (!string.Equals(box.Text, value, StringComparison.Ordinal))
            {
                box.Text = value;
            }
        }
    }

    private void SyncStatus()
    {
        _saveState.Text = _editor.PrimaryAutosaveEnabled
            ? _editor.SaveSummary
            : _editor.SaveSummary + " · автосохранение выключено";
        ToolTip.SetTip(_saveState, _editor.PrimaryAutosaveSummary);
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

        var stampOwnsCommonProperties = _editor.Session.SelectedObject?.Stamp is not null;
        _rotationInspectorRow.IsVisible = !stampOwnsCommonProperties;
        _sublayerInspectorRow.IsVisible = !stampOwnsCommonProperties;
        _opacityInspectorRow.IsVisible = !stampOwnsCommonProperties;
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
        SyncAppearanceSections();
        Ui.SetSelected(_addRectangleButton,
            _editor.PendingPlacement?.Target == PlacementTargetKind.Rectangle);
        Ui.SetSelected(_addEllipseButton,
            _editor.PendingPlacement?.Target == PlacementTargetKind.Ellipse);

        // Selection properties belong to the Select tool. While a drawing tool is active the panel
        // keeps showing that tool, so switching tools always has a predictable, visible effect.
        if (_editor.ActiveTool == EditorTool.Select && _editor.SelectionCount > 1)
        {
            _contextTitle.Text = "Свойства: выделение";
            SyncMultiSelectionPanel();
            _contextHost.Content = _multiSelectionRoot;
            return;
        }

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
    /// Keeps the multi-selection panel honest: the count, the group identity when there is one,
    /// which group actions currently apply, and mixed values reported as mixed.
    /// </summary>
    private void SyncMultiSelectionPanel()
    {
        _multiCountText.Text = _editor.SelectedGroupId is not null
            ? $"Группа «{_editor.SelectedGroupName ?? "Группа"}» · {EditorLabels.Objects(_editor.SelectionCount)}"
            : $"Выбрано: {EditorLabels.Objects(_editor.SelectionCount)}";
        AutomationProperties.SetName(_multiCountText, _multiCountText.Text);

        var hasGroup = _editor.SelectedGroupId is not null;
        var canGroup = _editor.CanGroupSelection;

        // When neither creating nor managing a group applies, the whole section disappears -
        // a leftover "Группа" heading over nothing would read as a broken panel.
        _groupActionsSection.IsVisible = hasGroup || canGroup;
        _groupNameSection.IsVisible = hasGroup || canGroup;
        _groupNameBox.IsVisible = hasGroup || canGroup;
        _groupCreateButton.IsVisible = canGroup;
        _groupRenameButton.IsVisible = hasGroup;
        _enterGroupButton.IsVisible = hasGroup;
        _ungroupButton.IsVisible = hasGroup;
        _groupNameBox.PlaceholderText = hasGroup
            ? _editor.SelectedGroupName ?? "Имя группы"
            : "Имя группы";

        Ui.SetValue(_multiVisibilityValue, "Видимость", _editor.SelectionVisibilitySummary);
        Ui.SetValue(_multiLockValue, "Блокировка", _editor.SelectionLockSummary);
    }

    /// <summary>
    /// The only place that decides which kind-specific block is shown. Exactly one block can be
    /// mounted at a time because they all share a single slot.
    /// </summary>
    private Control? KindSection(SceneObjectKind kind) => kind switch
    {
        SceneObjectKind.Stamp or SceneObjectKind.Placeholder => _stampSection,
        SceneObjectKind.Line => _lineSection,
        SceneObjectKind.Shape => _shapeSection,
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

        _multiSelectionRoot = BuildMultiSelectionPanel();
    }

    /// <summary>
    /// What N selected objects show instead of the single inspector: an honest count, the group
    /// actions when they apply, and the common visibility/lock/delete over the whole set. One
    /// member's numbers are never presented as if they were shared.
    /// </summary>
    private StackPanel BuildMultiSelectionPanel()
    {
        var multiCount = new TextBlock { Name = "MultiSelectionCountText", TextWrapping = TextWrapping.Wrap }
            .Classed("role-panel");
        _multiCountText = multiCount;

        AutomationProperties.SetName(_groupNameBox, "Имя группы");
        _groupNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _ = GroupSelectionInteractiveAsync();
                e.Handled = true;
            }
        };

        _groupCreateButton = ShortcutTextButton(
            "GroupCreateButton", "Сгруппировать", EditorShortcutSettings.Commands.Group,
            () => _ = GroupSelectionInteractiveAsync(), "primary", "compact");
        _groupRenameButton = Ui.TextButton("GroupRenameButton", "Переименовать группу", () =>
        {
            if (_editor.RenameSelectedGroup(_groupNameBox.Text ?? ""))
            {
                _groupNameBox.Text = "";
            }

            RefreshAfterSelectionCommand();
        }, "compact");
        _ungroupButton = ShortcutTextButton(
            "UngroupButton", "Разгруппировать", EditorShortcutSettings.Commands.Ungroup, () =>
        {
            _editor.UngroupSelection();
            RefreshAfterSelectionCommand();
        }, "compact");
        _enterGroupButton = Ui.TextButton("EnterGroupButton", "Открыть группу", () =>
        {
            _editor.EnterSelectedGroup();
            RefreshAfterSelectionCommand();
        }, "compact");

        _groupNameSection = new StackPanel
        {
            Spacing = 5,
            Children =
            {
                _groupNameBox,
                _groupCreateButton,
                _groupRenameButton,
                _enterGroupButton,
                _ungroupButton,
            },
        };

        var (visibilityRow, visibilityButton) = Ui.ValueRow(
            "MultiVisibilityButton", "Видимость", "-", _editor.ToggleSelectedObjectVisibility);
        _multiVisibilityValue = visibilityButton;
        var (lockRow, lockButton) = Ui.ValueRow(
            "MultiLockButton", "Блокировка", "-", _editor.ToggleSelectedObjectLock);
        _multiLockValue = lockButton;

        var delete = Ui.CommandButton("MultiDeleteButton", "Удалить выделение", MapIcons.Trash,
            _editor.DeleteSelectedObject, "destructive", "compact", "centred");
        delete.Margin = new Thickness(0, 6, 0, 0);
        DynamicTip(delete, () =>
            $"Удалить все выбранные объекты. Действие можно отменить ({_editor.Shortcuts.Get(EditorShortcutSettings.Commands.Undo)}).");

        _groupActionsSection = Ui.Section("Группа", _groupNameSection).Named("GroupActionsSection");
        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                multiCount,
                _groupActionsSection,
                Ui.Section("Общие действия", new StackPanel
                {
                    Spacing = 2,
                    Children = { visibilityRow, lockRow, delete },
                }).Named("MultiCommonActions"),
            },
        };
    }

    private Control _groupActionsSection = null!;

    private TextBlock _multiCountText = null!;

    private void RefreshAfterSelectionCommand()
    {
        SyncStatus();
        SyncSidePanels();
        SyncContextArea();
    }

    /// <summary>
    /// The interactive Ctrl+G flow: the panel's name box wins when filled; otherwise a small
    /// dialog asks for the name and states exactly how many objects the group will hold.
    /// </summary>
    public System.Threading.Tasks.Task GroupSelectionInteractiveAsync()
    {
        if (!_editor.CanGroupSelection)
        {
            // Produces the honest refusal message (too few objects / already grouped members).
            _editor.GroupSelection("группа");
            RefreshAfterSelectionCommand();
            return System.Threading.Tasks.Task.CompletedTask;
        }

        var name = (_groupNameBox.Text ?? "").Trim();
        if (_editor.GroupSelection(name))
        {
            _groupNameBox.Text = "";
        }

        RefreshAfterSelectionCommand();
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private static async System.Threading.Tasks.Task<string?> AskGroupNameAsync(
        Window? owner, int objectCount)
    {
        var input = new TextBox { Name = "GroupNameInput", MinWidth = 320, MaxLength = 80 };
        AutomationProperties.SetName(input, "Имя группы");

        var completion = new System.Threading.Tasks.TaskCompletionSource<string?>();
        var window = new Window
        {
            Title = "Имя группы",
            Name = "GroupNameWindow",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var confirm = Ui.TextButton("ConfirmGroupNameButton", "Создать группу", () =>
        {
            completion.TrySetResult((input.Text ?? "").Trim());
            window.Close();
        });
        var cancel = Ui.TextButton("CancelGroupNameButton", "Отмена", () =>
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
                    new TextBlock
                    {
                        Text = $"В группу войдут: {EditorLabels.Objects(objectCount)}. Как её назвать?",
                    }.Classed("role-panel"),
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

    private void SyncHint()
    {
        _hintText.Text = EditorLabels.ToolHint(_editor.ActiveTool);
        ToolTip.SetTip(_hintText, _hintText.Text);

        _hintKeys.Children.Clear();
        foreach (var pair in new (string Label, string Key)[]
                 {
                     ("Отменить", _editor.Shortcuts.Get(EditorShortcutSettings.Commands.Undo)),
                     ("Сохранить", _editor.Shortcuts.Get(EditorShortcutSettings.Commands.Save)),
                     (EditorLabels.Tool(_editor.ActiveTool), ToolShortcut(_editor.ActiveTool)),
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
            _gridLayerBlock.IsEnabled = !grid.Locked;
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
        SyncCheckpointList();
        SyncCanvasWorkflow();
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
        _objectListRebuildCount++;
        RefreshObjectSelectionSet();
        SyncObjectFilterButtons();
        _selectionCountText.Text = _editor.SelectionCount == 0
            ? "Ничего не выбрано"
            : $"Выбрано: {EditorLabels.Objects(_editor.SelectionCount)}";

        var rows = _editor.ObjectTree;
        if (rows.Count == 0)
        {
            var hasAnyObject = _editor.Layers.OfType<ObjectLayer>().Any(layer => layer.Objects.Count > 0);
            _objectEmptyStateHost.Content = Ui.EmptyState(
                "NoObjectsText",
                MapIcons.Objects,
                hasAnyObject ? "Ничего не найдено" : "На карте пока нет объектов",
                hasAnyObject
                    ? "Измените запрос или фильтр типа."
                    : "Добавьте объект инструментом «Линия», «Фигура», «Текст» или из каталога ассетов.");
            _objectEmptyStateHost.IsVisible = true;
            _objectList.IsVisible = false;
            _objectList.ItemsSource = Array.Empty<ObjectTreeRow>();
            return;
        }

        _objectEmptyStateHost.IsVisible = false;
        _objectList.IsVisible = true;
        _objectList.ItemsSource = rows;
        Dispatcher.UIThread.Post(SyncObjectListSelection, DispatcherPriority.Loaded);
    }

    /// <summary>The breadcrumb of an open group: the way back plus the group's own name.</summary>
    private Border BuildGroupBreadcrumbRow(ObjectTreeRow row)
    {
        var back = Ui.TextButton("GroupBackButton", "← Все объекты", () =>
        {
            _editor.ExitGroup();
            SyncObjectList();
            SyncContextArea();
        }, "compact");

        // The identity sits on the TextBlock: a bare Border has no automation peer, so naming
        // the host would leave the breadcrumb invisible to UI Automation.
        var title = Ui.Trimmed($"{row.Title} · {row.Subtitle}", "muted").Named(row.AutomationId);
        AutomationProperties.SetName(title, $"Открыта группа «{row.Title}», {row.Subtitle}");
        return new Border
        {
            Child = new DockPanel
            {
                Children = { back.Docked(Dock.Left), title },
            },
            Padding = new Thickness(0, 2),
        };
    }

    private Border BuildGroupRow(ObjectTreeRow row)
    {
        var groupId = row.GroupId!.Value;
        var enter = Ui.RowAction(
            $"GroupRowEnter_{groupId:D}", "Открыть группу", MapIcons.ChevronDown,
            () =>
            {
                _editor.EnterGroup(groupId);
                SyncObjectList();
                SyncContextArea();
            });

        var host = Ui.StructureRow(
            row.AutomationId,
            MapIcons.Objects,
            row.Title,
            row.Subtitle,
            () =>
            {
                _editor.SelectGroup(groupId);
                SyncObjectListSelection();
                SyncContextArea();
            },
            $"Группа «{row.Title}» · {row.Subtitle}",
            enter);

        Ui.SetStructureSelected(host, row.Selected);
        return host;
    }

    private Button BuildObjectRow(ObjectTreeRow row)
    {
        var sceneObject = row.Object!;
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
        var listRow = Ui.ListRow(
            row.AutomationId,
            ObjectIcon(sceneObject.Kind),
            sceneObject.Name,
            row.Subtitle,
            states,
            () =>
            {
                _editor.SelectObject(captured.Id, additive: _objectRowCtrl);
                SyncObjectListSelection();
                SyncContextArea();
            },
            $"{sceneObject.Name} · {EditorLabels.ObjectKind(sceneObject.Kind)}");

        Ui.SetSelected(listRow, row.Selected);
        return listRow;
    }

    private void SyncObjectFilterButtons()
    {
        foreach (var pair in _objectFilterButtons)
        {
            var active = string.Equals(pair.Key, _editor.ObjectTypeFilter.ToString(), StringComparison.Ordinal);
            Ui.SetClass(pair.Value, "active", active);
            if (pair.Value is ToggleButton toggle)
            {
                toggle.IsChecked = active;
            }
        }
    }

    private void RefreshObjectSelectionSet()
    {
        _objectSelectionSet.Clear();
        _objectSelectionSet.UnionWith(_editor.Session.SelectedObjectIds);
    }

    private bool IsObjectTreeRowSelected(ObjectTreeRow row) => row.Kind switch
    {
        ObjectTreeRowKind.Object or ObjectTreeRowKind.GroupChild =>
            row.Object is { } sceneObject && _objectSelectionSet.Contains(sceneObject.Id),
        ObjectTreeRowKind.Group =>
            row.MemberIds is { Count: > 0 } members
            && members.All(_objectSelectionSet.Contains),
        _ => false,
    };

    /// <summary>
    /// Selection-only notifications touch only realized containers. The ItemsSource and its
    /// tens of thousands of logical rows stay intact.
    /// </summary>
    private void SyncObjectListSelection()
    {
        RefreshObjectSelectionSet();
        if (_objectList.ItemsSource is not IReadOnlyList<ObjectTreeRow> rows)
        {
            return;
        }

        foreach (var container in _objectList.GetRealizedContainers())
        {
            var index = _objectList.IndexFromContainer(container);
            if (index < 0 || index >= rows.Count)
            {
                continue;
            }

            var selected = IsObjectTreeRowSelected(rows[index]);
            if (container is ListBoxItem item)
            {
                item.IsSelected = selected;
            }

            container.GetVisualDescendants()
                .OfType<RecyclingObjectTreeRow>()
                .FirstOrDefault()
                ?.RefreshSelection();
        }
    }

    private void NameObjectContainer(Control? container, int index)
    {
        if (container is not ListBoxItem item
            || _objectList.ItemsSource is not IReadOnlyList<ObjectTreeRow> rows
            || index < 0
            || index >= rows.Count)
        {
            return;
        }

        var row = rows[index];
        var name = row.Kind switch
        {
            ObjectTreeRowKind.Breadcrumb => $"Открыта группа «{row.Title}», {row.Subtitle}",
            ObjectTreeRowKind.Group => $"Группа «{row.Title}», {row.Subtitle}",
            _ => $"{row.Title} · {row.Subtitle}",
        };
        AutomationProperties.SetName(item, name);
        item.IsSelected = IsObjectTreeRowSelected(row);
    }

    /// <summary>
    /// A recycled data-template host. Avalonia reuses the host/container while the bounded row
    /// content is rebuilt from the current DataContext, so no click handler can retain a scrolled-
    /// away object.
    /// </summary>
    private sealed class RecyclingObjectTreeRow : ContentControl
    {
        private readonly EditorView _owner;

        public RecyclingObjectTreeRow(EditorView owner)
        {
            _owner = owner;
            DataContextChanged += (_, _) => RefreshRow();
        }

        public void RefreshSelection()
        {
            if (DataContext is not ObjectTreeRow row || Content is not Control content)
            {
                return;
            }

            var selected = _owner.IsObjectTreeRowSelected(row);
            if (content is Button button)
            {
                Ui.SetSelected(button, selected);
            }
            else if (content is Border border)
            {
                Ui.SetStructureSelected(border, selected);
            }
        }

        private void RefreshRow()
        {
            if (DataContext is not ObjectTreeRow row)
            {
                Content = null;
                return;
            }

            Content = row.Kind switch
            {
                ObjectTreeRowKind.Breadcrumb => _owner.BuildGroupBreadcrumbRow(row),
                ObjectTreeRowKind.Group => _owner.BuildGroupRow(row),
                _ => _owner.BuildObjectRow(row),
            };
            RefreshSelection();
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
        SetSegment(_placementModeButtons, _editor.StampPlacementMode switch
        {
            StampPlacementMode.Series => "Series",
            StampPlacementMode.Area => "Area",
            _ => "One",
        });

        SetToggle(_libraryFilterButtons["Favorites"], "Только избранное", _editor.LibraryFavoritesOnly);
        SetToggle(_libraryFilterButtons["Recent"], "Только недавние", _editor.LibraryRecentOnly);
        SetToggle(_libraryFilterButtons["Untagged"], "Только без тегов", _editor.LibraryUntaggedOnly);
        SetToggle(
            _libraryFilterButtons["UsedInMap"], "Только используемое на карте", _editor.LibraryUsedInMapOnly);
        SetToggle(
            _randomVariantToggle, "Случайный вариант после размещения", _editor.RandomVariantAfterPlacement);
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

    }

    private static void SetSegment(Dictionary<string, Button> buttons, string activeKey)
    {
        foreach (var (key, button) in buttons)
        {
            Ui.SetSelected(button, key == activeKey);
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

    private void SyncCheckpointList()
    {
        _checkpointList.Children.Clear();
        _checkpointPreviews.Prune([.. _editor.Checkpoints.Select(item => item.Id)]);

        if (_editor.Checkpoints.Count == 0)
        {
            _checkpointList.Children.Add(Ui.EmptyState(
                "NoCheckpointsText",
                MapIcons.Save,
                "Версий пока нет",
                "Нажмите «Создать версию», чтобы сохранить текущее состояние и preview."));
            return;
        }

        foreach (var checkpoint in _editor.Checkpoints.Reverse())
        {
            var captured = checkpoint;
            var preview = _checkpointPreviews.Image(checkpoint);
            Control previewControl = preview is null
                ? MapIcons.Stroked(MapIcons.Warning, 22)
                : new Image
                {
                    Source = preview,
                    Stretch = Stretch.Uniform,
                };
            AutomationProperties.SetName(previewControl, $"Preview версии {checkpoint.Name}");

            var renameBox = new TextBox
            {
                Name = "VersionName_" + checkpoint.Id,
                Text = checkpoint.Name,
                MaxLength = MapCheckpointLimits.MaxNameLength,
            };
            AutomationProperties.SetName(renameBox, "Новое название версии");
            var rename = Ui.TextButton(
                "RenameVersion_" + checkpoint.Id,
                "Переименовать",
                () =>
                {
                    _editor.RenameVersion(captured.Id, renameBox.Text);
                    SyncCheckpointList();
                    SyncStatus();
                },
                "compact");

            var restore = Ui.AsyncButton(
                "RestoreCheckpoint_" + checkpoint.Id,
                "Восстановить",
                async () =>
                {
                    await _editor.RestoreCheckpointAsync(captured.Id);
                    SyncCheckpointList();
                    SyncStatus();
                },
                "compact");
            var delete = Ui.AsyncButton(
                "DeleteCheckpoint_" + checkpoint.Id,
                "Удалить",
                async () =>
                {
                    await _editor.DeleteCheckpointAsync(captured.Id);
                    SyncCheckpointList();
                    SyncStatus();
                },
                "destructive",
                "compact");

            var card = new Border
            {
                Padding = new Thickness(8),
                Child = new StackPanel
                {
                    Spacing = 7,
                    Children =
                    {
                        new Border
                        {
                            Height = 128,
                            Background = Application.Current?.FindResource("MapFieldBrush") as IBrush,
                            Child = previewControl,
                        }.Classed("group"),
                        new TextBlock
                        {
                            Text = checkpoint.Name,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        }.Classed("role-panel"),
                        new TextBlock
                        {
                            Text = checkpoint.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                        }.Classed("faint", "role-caption"),
                        new TextBlock
                        {
                            Text = $"Preview: {Math.Max(1, checkpoint.PreviewPng.Length / 1024)} KiB · "
                                + $"объектов: {checkpoint.Snapshot.Layers.OfType<ObjectLayer>().Sum(layer => layer.Objects.Count)}",
                        }.Classed("faint", "role-caption"),
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 6,
                            Children = { renameBox, rename },
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 6,
                            Children = { restore, delete },
                        },
                    },
                },
            }.Classed("group").Named("Checkpoint_" + checkpoint.Id);
            AutomationProperties.SetName(
                card,
                $"Версия {checkpoint.Name}, создана "
                    + checkpoint.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            _checkpointList.Children.Add(card);
        }
    }

    // ================================================================== actions

    private async System.Threading.Tasks.Task AssignClipMaskAsync()
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
                Title = _editor.StampPanelVariantHasLibraryMask
                    ? "Заменить Clip Mask"
                    : "Назначить Clip Mask",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Изображения")
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"],
                    },
                ],
            });
            path = files.Count == 1 ? files[0].TryGetLocalPath() : null;
        }

        if (path is null)
        {
            return;
        }

        _editor.AssignCurrentVariantMask(path);
        SyncAssetList();
        SyncInspector();
        SyncStatus();
    }

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
                    new FilePickerFileType("Изображения") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.svg"] },
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

    private async System.Threading.Tasks.Task ExportCompositionPackageAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Экспорт composition package",
            SuggestedFileName = (_editor.Session.SelectedObject?.Name ?? "composition") + ".mapcomp",
            DefaultExtension = "mapcomp",
            FileTypeChoices = [new FilePickerFileType("Map composition") { Patterns = ["*.mapcomp"] }],
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            _editor.ExportSelectedCompositionPackage(path);
            SyncStatus();
        }
    }

    private async System.Threading.Tasks.Task RepairSelectedAssetAsync()
    {
        if (_selectedAsset is not { } selected) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Найти исходник ассета",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Изображения")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.svg"],
                },
            ],
        });
        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path
            && _editor.RepairAssetSource(selected, path) is { } repaired)
        {
            _selectedAsset = repaired;
            SyncAssetList();
            SyncStatus();
        }
    }

    private async System.Threading.Tasks.Task RemoveSelectedAssetAsync()
    {
        if (_selectedAsset is not { } selected || TopLevel.GetTopLevel(this) is not Window owner) return;
        var confirmed = await ConfirmationWindow.ShowAsync(
            owner,
            "Удалить ассет из каталога?",
            $"«{selected.DisplayName}» исчезнет из библиотеки. Карты и сохранённые исходные bytes не удаляются.",
            "Удалить из каталога",
            "RemoveAssetRecordConfirmButton",
            destructive: true);
        if (!confirmed) return;
        if (_editor.RemoveAssetFromCatalog(selected))
        {
            _selectedAsset = null;
            SyncAssetList();
            SyncStatus();
        }
    }

    private async System.Threading.Tasks.Task ImportCompositionPackageAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Импорт composition package",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Map composition") { Patterns = ["*.mapcomp"] }],
        });
        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path)
        {
            _editor.ImportCompositionPackage(path);
            SyncContextArea();
            SyncSidePanels();
            SyncStatus();
        }
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
                    new FilePickerFileType("Изображения") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.svg"] },
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
