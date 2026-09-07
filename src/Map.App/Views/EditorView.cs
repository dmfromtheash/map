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

/// <summary>
/// The editor screen. Its spatial grammar is fixed and never rearranges under the user: global
/// commands on top, tools in one vertical rail, exactly one context area for the active tool or
/// the current selection, the canvas in the middle, structure on the right, state at the bottom.
///
/// Built in code so every control carries an explicit UIA name. The view only reads the
/// view-model and forwards intent; it never owns document state.
/// </summary>
public sealed partial class EditorView : UserControl
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
    private readonly TextBlock _exportDetailSummary = Status("ExportDetailSummaryText");
    private readonly TextBlock _exportMemoryDetails = new() { Name = "ExportMemoryDetails", TextWrapping = TextWrapping.Wrap };
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
    private readonly Dictionary<int, Button> _exportScaleButtons = [];
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
    private Button _viewExportedPngButton = null!;
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
        new TextBlock
        {
            Name = "ContextPanelTitle",
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        }.Classed("role-panel");
    private readonly ContentControl _contextHost = new() { Name = "ContextPanelHost" };
    private Control _contextSwitch = null!;
    private ContentControl _toolSlot = null!;
    private readonly List<ColorField> _colorFields = [];
    private readonly List<Button> _contextSwitchButtons = [];
    private bool _contextPrefersToolPanel;
    private EditorTool? _lastContextTool;
    private string? _lastContextSelection;
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
    private Control? _noteSelectionEditor;
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
    private Control _primitiveSection = null!;

    // Which gradient stop the ramp, the colour picker and the position slider are all working on.
    // It lives here rather than in the ramp so the shape block and the primitive block agree.
    private int _gradientStop;
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
    private Control _baseColorLayerBlock = null!;
    private Control _landLayerBlock = null!;
    private Control _gridLayerBlock = null!;
    private Control _filterLayerBlock = null!;
    private Control _layerIdentity = null!;
    private Control _noLayerState = null!;
    private readonly TextBlock _layerNameHeading = new() { Name = "ActiveLayerNameText" };
    private readonly TextBlock _layerKindText = new() { Name = "ActiveLayerKindText" };
    private ContentControl _layerKindIcon = null!;
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
        ClipToBounds = true,
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

    private readonly ListBox _variantStrip = new()
    {
        Name = "VariantStrip",
        MaxHeight = 132,
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
    private Control _stampPlacementOptions = null!;
    private Control _stampCatalogDetails = null!;
    private TextBlock _stampVariantCounter = null!;
    private TextBlock _stampVariantName = null!;
    private StackPanel _stampAreaControls = null!;
    private (int First, int Count) _reportedLibraryRange = (-1, -1);
    private int _lastSelectedLibraryRow = -1;
    private ToggleButton _randomFlipToggle = null!;
    private TextBlock _stampToolCurrentText = null!;
    private TextBlock _stampToolAreaText = null!;
    private Image _stampToolThumbnail = null!;
    private Button _stampCurrentAssetButton = null!;
    private ListBox _stampVariantStrip = null!;
    private ListBox _stampRecentStrip = null!;
    private ListBox _stampFavoriteStrip = null!;
    private Control? _seriesSpacingControl;
    private bool _assetCatalogAutoCloseForStamp;
    private bool _syncingLibrary;
    private readonly ThumbnailCache _thumbnails = new(96);
    private readonly CheckpointPreviewCache _checkpointPreviews = new();
    private readonly List<Action> _levelSyncs = [];
    // The quick strip above the canvas shows the same handful of properties as the object panel.
    // Both read and write one view model, and both refresh from this one list, so neither can
    // show a number the other disagrees with.
    private readonly List<Action> _quickSyncs = [];
    private readonly Dictionary<string, ListBox> _layerLists = [];
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
        var quickBar = BuildQuickPropertyBar();
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

                // Docked, not floating: the strip takes its own band and the canvas fills what is
                // left, so it can never cover the map.
                quickBar.Docked(Dock.Top),
                statusBar.Docked(Dock.Bottom),
                hintBar.Docked(Dock.Bottom),
                rail.Docked(Dock.Left),
                contextPanel.Docked(Dock.Left),
                sidePanel.Docked(Dock.Right),

                // The canvas is the fill child: panels resize it, never cover it.
                Ui.WorkspaceBackdrop(_canvas).Classed("canvas-surround"),
            },
        };

        SizeChanged += (_, _) =>
        {
            var narrow = Bounds.Width < 1100;
            Ui.SetClass(this, "compact-window", narrow || Bounds.Height < 760);
            Ui.SetClass(this, "narrow-window", narrow);
            if (!_contextCollapsed) _contextPanel.Width = narrow ? 240 : Ui.ContextPanelWidth;
            if (!_sideCollapsed) _sidePanel.Width = narrow ? 264 : Ui.SidePanelWidth;
        };

        _canvas.Attach(_editor);
        _canvas.ViewportChanged += (_, _) => _viewportStatus.Text = _editor.ViewportStatus;
        _canvas.StampPlaced += (_, _) => OnEditorChanged(this, new(nameof(EditorViewModel.CompactRecentSets)));
        _canvas.VariantCycled += (_, _) => OnEditorChanged(this, new(nameof(EditorViewModel.CurrentVariant)));
        _editor.PropertyChanged += OnEditorChanged;
        _editor.Shortcuts.Changed += OnShortcutsChanged;
        _editor.SceneChanged += (_, _) =>
        {
            lock (_uiSyncGate) _sceneSyncPending = true;
            OnEditorChanged(this, new(nameof(EditorViewModel.Session)));
        };
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
            _thumbnails.Dispose();
            _fillAreaThumbnail?.Dispose(); _fillAreaThumbnail = null;
            _checkpointPreviews.Dispose();
            _editor.CancelExport();
            _editor.CloseExport();
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
        _assetSearch.TextChanged += (_, _) => QueueLibrarySearch();
        _noteSearch.TextChanged += (_, _) => SyncNoteList();

        SyncStatus();
        SyncInspector();
        SyncSidePanels();
        SyncContextArea();
        InitializeStatistics();

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
    public int StampCompactRecentCountForSmoke => _editor.CompactRecentSets.Count;
    public int StampCompactFavoriteCountForSmoke => _editor.CompactFavoriteSets.Count;
    public int StampCompactVariantCountForSmoke => _editor.SelectedSetVariants.Count;

    public bool SelectFirstCatalogSetForSmoke()
    {
        if (!TestMode.Enabled)
        {
            return false;
        }

        var index = _editor.LibraryRows.ToList().FindIndex(row => !row.IsHeader);
        if (index < 0)
        {
            return false;
        }

        var button = _libraryGrid.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(item => item.Name?.StartsWith("GalleryCover_", StringComparison.Ordinal) == true);
        button?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        return _editor.HasChosenAsset && !AssetCatalogVisible;
    }

    public bool ShowFutureClipMaskForSmoke()
    {
        var group = this.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => control.Name == "FutureStampClipMaskGroup");
        group?.BringIntoView();
        return group is not null;
    }

    public async System.Threading.Tasks.Task<bool> CreateVersionThroughUiForSmokeAsync()
    {
        OpenVersions();
        var before = _editor.Checkpoints.Count;
        var button = _versionsContent.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(control => control.Name == "CreateVersionButton");
        button?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        for (var attempt = 0; button is not null && _editor.Checkpoints.Count == before && attempt < 100; attempt++)
            await System.Threading.Tasks.Task.Delay(50);
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
        RefreshGallerySelection();
        _assetCatalogAutoCloseForStamp = _editor.ActiveTool == EditorTool.Stamp;
        _assetCatalogWindow = ShowAuxiliaryWindow(
            _assetCatalogWindow,
            "Локальная библиотека ассетов",
            _assetCatalogContent,
            1240,
            820,
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
        _contextPanel.Width = _contextCollapsed ? 42 : (Classes.Contains("narrow-window") ? 240 : Ui.ContextPanelWidth);
        _contextSwitch.IsVisible = !_contextCollapsed && EditorContextPanel.ToggleApplies(_editor.SelectionCount);
    }

    public void ToggleRightPanel()
    {
        _sideCollapsed = !_sideCollapsed;
        _sideBody.IsVisible = !_sideCollapsed;
        _sidePanel.Width = _sideCollapsed ? 42 : (Classes.Contains("narrow-window") ? 264 : Ui.SidePanelWidth);
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

        var ownerWindow = TopLevel.GetTopLevel(this) as Window;
        var window = new Window
        {
            Name = name,
            Title = title,
            Width = Math.Min(width, ownerWindow?.ClientSize.Width ?? width),
            Height = Math.Min(height, ownerWindow?.ClientSize.Height ?? height),
            MinWidth = 620,
            MinHeight = 480,
            ShowActivated = !TestMode.Enabled,
            ShowInTaskbar = !TestMode.Enabled,
            Content = Ui.WorkspaceBackdrop(new DockPanel
            {
                Children =
                {
                    new Border
                    {
                        Padding = new Thickness(20, 14),
                        Child = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.Medium },
                    }.Classed("app-bar").Docked(Dock.Top),
                    content,
                },
            }),
        };
        window.Closing += (_, e) =>
        {
            if (!_closingAuxiliaryWindows)
            {
                e.Cancel = true;
                window.Hide();
            }
        };
        if (name == "AssetCatalogWindow") window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            window.Hide();
            _canvas.Focus();
        };
        if (TestMode.Enabled)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(-32000, -32000);
        }
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
        _canvas.ShowPlacementPreviewAtCenter();
        _assetCatalogAutoCloseForStamp = false;
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
        var export = Ui.CommandButton(
            "ExportButton", "Экспорт…", MapIcons.Export, OpenExport, "compact");
        AutomationProperties.SetName(export, "Экспорт изображения");

        _saveButton = Ui.AsyncCommandButton(
            "SaveButton", "Сохранить", MapIcons.Save, () => _editor.SaveAsync(), "primary");
        _shortcutSettingsButton = Ui.TextButton(
            "ShortcutSettingsButton", "Сочетания…", requestShortcutSettings, "compact");

        // A flyout is a logical child of the button that owns it, so the app bar's "every caption
        // in here is light" rule reached inside the file menu and drew its words in #F8F3EB on
        // white paper - 1.08:1. The menu states its own colour, which no style can outrank.
        var menuInk = Application.Current?.FindResource("MapTextBrush") as IBrush;

        MenuItem Item(string header, string icon, Action action)
        {
            var item = new MenuItem
            {
                Foreground = menuInk,
                Header = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        MapIcons.Stroked(icon, 15, menuInk),
                        new TextBlock
                        {
                            Text = header,
                            Foreground = menuInk,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
            };
            AutomationProperties.SetName(item, header);
            item.Click += (_, _) => action();
            return item;
        }

        var autosaveItem = new MenuItem { Foreground = menuInk };
        var autosaveCaption = new TextBlock
        {
            Foreground = menuInk,
            VerticalAlignment = VerticalAlignment.Center,
        };
        autosaveItem.Header = autosaveCaption;
        void SyncAutosaveMenu() => autosaveCaption.Text = _editor.PrimaryAutosaveEnabled
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
                Item("Версии…", MapIcons.Versions, OpenVersions),
                Item("Сохранить как…", MapIcons.Save, () => _ = _editor.SaveAsync(saveAs: true)),
                autosaveItem,
                new Separator(),
                Item("Размер и область…", MapIcons.ResizeRegion, OpenCanvasWorkflow),
                Item("Экспорт…", MapIcons.Export, OpenExport),
                new Separator(),
                Item("Сочетания клавиш…", MapIcons.Gear, requestShortcutSettings),
                Item("Открыть второе окно", MapIcons.Window, requestSecondWindow),
                Item("Вписать карту в окно", MapIcons.FitCanvas, _canvas.FitToCanvas),
            },
        };
        var file = Ui.CommandButton("FileMenuButton", "Файл", MapIcons.File, () => { }, "compact");
        file.Flyout = fileMenu;

        var historyActions = ToolbarGroup(_undoButton, _redoButton);
        var viewActions = ToolbarGroup(fit);
        var primaryActions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { historyActions, viewActions, file, export, _saveButton },
            Margin = new Thickness(24, 0, 0, 0),
        }.Named("EditorHeaderActions");
        // Save and history stay in place; secondary commands retain their labelled menu entry.
        SizeChanged += (_, _) =>
        {
            var narrow = Bounds.Width < 1100;
            export.IsVisible = !narrow;
            viewActions.IsVisible = !narrow;
            file.Content = new TextBlock { Text = narrow ? "Файл и вид ▾" : "Файл ▾" };
        };

        var bar = new DockPanel
        {
            Margin = new Thickness(8, 6),
            Children =
            {
                primaryActions.Docked(Dock.Right),
                back.Docked(Dock.Left),
                title,
            },
        };

        SyncShortcutSurfaces();

        return new Border { Child = bar }.Classed("app-bar");
    }

    private static Border ToolbarGroup(params Control[] controls)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        stack.Children.AddRange(controls);

        return new Border
        {
            Padding = new Thickness(4, 2),
            CornerRadius = new CornerRadius(7),
            Background = Application.Current?.FindResource("MapChromeElevatedBrush") as IBrush,
            BorderBrush = Application.Current?.FindResource("MapChromeBorderBrush") as IBrush,
            BorderThickness = new Thickness(1),
            Child = stack,
        };
    }

    // ========================================================== quick property strip

    /// <summary>
    /// The properties a selected object needs constantly, as a strip across the top of the editor
    /// rather than as the first rows of a panel that has to be scrolled.
    /// <para>
    /// The owner's reference shows these next to each other above the map, and that is the point:
    /// layer, level, scale, opacity and rotation are read and changed far more often than anything
    /// else, so they must be where the eye already is. The detailed panel keeps all of them too -
    /// both surfaces call the same view model and refresh from the same list, so there is one
    /// source of truth and never two numbers that disagree.
    /// </para>
    /// Whole groups wrap to the available editor width. Empty selection gives the space back
    /// to the canvas; viewport resizing preserves the visible world centre.
    /// </summary>
    private Control BuildQuickPropertyBar()
    {
        var thumbnail = new Image
        {
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        }.Named("QuickThumbnail");

        var identity = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 180,
            FontWeight = FontWeight.Medium,
        }.Named("QuickIdentity");

        var layer = BuildOwnerLayerPicker("Quick");
        layer.Width = 152;
        layer.HorizontalAlignment = HorizontalAlignment.Left;

        var (levelBlock, _) = BuildQuickLevel();

        var scale = BuildQuickNumber(
            "QuickScale", "Масштаб", "%", 10, 400,
            () => _editor.SelectionScalePercent,
            value => _editor.SetSelectionScalePercent(value, commit: true));
        var opacity = BuildQuickNumber(
            "QuickOpacity", "Непрозрачность", "%", 0, 100,
            () => _editor.SelectionOpacityPercent,
            value => _editor.SetSelectionOpacityPercent(value, commit: true));
        var rotation = BuildQuickNumber(
            "QuickRotation", "Поворот", "°", 0, 359,
            () => _editor.SelectionRotationDegrees,
            value => _editor.SetSelectionRotationDegrees(value, commit: true));

        var strip = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 12, LineSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                QuickGroup(thumbnail, identity),
                QuickGroup(QuickField("Слой", layer), levelBlock),
                QuickGroup(scale, opacity, rotation),
            },
        };
        var host = new Border { Padding = new Thickness(10, 6), Child = strip }
            .Classed("quick-bar").Named("QuickPropertyBar");

        _quickSyncs.Add(() =>
        {
            var count = _editor.SelectionCount;
            var selected = _editor.Session.SelectedObject;
            host.IsVisible = count > 0;

            thumbnail.Source = count == 1 && selected is not null
                ? _thumbnails.Image(_editor.ObjectThumbnailPath(selected))
                : null;
            thumbnail.IsVisible = thumbnail.Source is not null;
            identity.Text = count switch
            {
                0 => "",
                1 => "Выделено: " + (selected?.Name ?? "Объект"),
                _ => "Выбрано: " + EditorLabels.Objects(count),
            };
            ToolTip.SetTip(identity, identity.Text);
            AutomationProperties.SetName(identity, identity.Text);

            // A percentage of a bitmap's own reference size means nothing for a shape or a
            // caption, so the field is absent rather than lying with a blank.
            scale.IsVisible = _editor.SelectionIsAllStamps;
        });

        return host;
    }

    private static Control QuickGroup(params Control[] fields)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.AddRange(fields);
        return new Border { Child = row, Padding = new Thickness(8, 2) }.Classed("property-group");
    }

    /// <summary>A caption and its editor side by side, the strip's single row shape.</summary>
    private static Control QuickField(string caption, Control editor)
    {
        var label = new TextBlock
        {
            Text = caption,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        }.Classed("muted");
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { label, editor },
        };
    }

    /// <summary>
    /// The level in the strip: the exact number, typed or stepped, plus a reset. It writes through
    /// the same view model the slider in the panel writes through, so the two always agree.
    /// </summary>
    private (Control Block, TextBox Box) BuildQuickLevel()
    {
        var box = new TextBox
        {
            Width = 62,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
        }.Named("QuickLevelBox");
        AutomationProperties.SetName(box, "Уровень объекта");

        var syncing = false;

        void Commit()
        {
            if (syncing)
            {
                return;
            }

            var text = (box.Text ?? "").Trim().Replace('−', '-').Replace('+', ' ').Trim();
            if (text.Length == 0)
            {
                SyncQuickBar();
                return;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                _editor.ReportInspectorError(
                    "Уровень — целое число от " + EditorViewModel.MinimumSublayer
                        + " до +" + EditorViewModel.MaximumSublayer + ".");
                SyncQuickBar();
                return;
            }

            var bounded = Math.Clamp(
                value, EditorViewModel.MinimumSublayer, EditorViewModel.MaximumSublayer);
            if (bounded != value)
            {
                _editor.ReportInspectorError(
                    "Уровень ограничен диапазоном " + EditorViewModel.MinimumSublayer
                        + "…+" + EditorViewModel.MaximumSublayer + ".");
            }

            _editor.SetSelectionSublayer(bounded, commit: true);
            RefreshAfterSelectionCommand();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                SyncQuickBar();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Commit();

        Button Step(string name, string caption, int direction, string tip)
        {
            var button = Ui.TextButton(name, caption, () =>
            {
                _editor.ShiftSelectionSublayer(direction);
                RefreshAfterSelectionCommand();
            }, "compact");
            ToolTip.SetTip(button, tip);
            return button;
        }

        var reset = ResetGlyph("QuickLevelResetButton", "Сбросить уровень к нулю", () =>
        {
            _editor.SetSelectionSublayer(0, commit: true);
            RefreshAfterSelectionCommand();
        });

        _quickSyncs.Add(() =>
        {
            syncing = true;
            try
            {
                box.Text = _editor.SelectionSublayerReadout;
                Ui.SetClass(box, "mixed", _editor.SelectionSublayerIsMixed);
                box.IsEnabled = _editor.SelectionCount > 0;
                reset.IsEnabled = _editor.SelectionCount > 0;
                AutomationProperties.SetName(box, "Уровень объекта: " + box.Text);
                ToolTip.SetTip(box,
                    "Уровень выделения, целое число от " + EditorViewModel.MinimumSublayer
                        + " до +" + EditorViewModel.MaximumSublayer
                        + ". Введите значение и нажмите Enter.");
            }
            finally
            {
                syncing = false;
            }
        });

        var block = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "Уровень",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                }.Classed("muted"),
                Step("QuickLevelDownButton", "−", -1, "Опустить выделение на один уровень."),
                box,
                Step("QuickLevelUpButton", "+", 1, "Поднять выделение на один уровень."),
                reset,
            },
        };
        return (block, box);
    }

    /// <summary>
    /// One compact numeric field of the strip. Empty means "the selected objects disagree", which
    /// is the honest answer; typing a number gives all of them that value as one undoable command.
    /// </summary>
    private Control BuildQuickNumber(
        string name, string caption, string unit, double minimum, double maximum,
        Func<double?> read, Action<double> apply)
    {
        var box = new TextBox
        {
            Width = 68,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
        }.Named(name + "Box");

        var syncing = false;

        void Commit()
        {
            if (syncing)
            {
                return;
            }

            var text = (box.Text ?? "").Trim().Replace(',', '.').Replace('−', '-');
            if (text.Length == 0 || string.Equals(text, "разные", StringComparison.Ordinal))
            {
                SyncQuickBar();
                return;
            }

            if (!TryWholeNumber(text, out var value))
            {
                _editor.ReportInspectorError($"{caption} — целое число от {minimum:0} до {maximum:0} {unit}.");
                SyncQuickBar();
                return;
            }

            var bounded = Math.Clamp(value, minimum, maximum);
            if (Math.Abs(bounded - value) > 0.0001)
            {
                _editor.ReportInspectorError($"{caption} ограничен диапазоном {minimum:0}…{maximum:0} {unit}.");
            }

            apply(bounded);
            RefreshAfterSelectionCommand();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                SyncQuickBar();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Commit();

        _quickSyncs.Add(() =>
        {
            syncing = true;
            try
            {
                var value = read();
                box.Text = value is { } number
                    ? number.ToString("0", CultureInfo.InvariantCulture)
                    : _editor.SelectionCount > 1 ? "разные" : "";
                Ui.SetClass(box, "mixed", value is null && _editor.SelectionCount > 1);
                box.IsEnabled = _editor.SelectionCount > 0;
                AutomationProperties.SetName(box, $"{caption}, {unit}: {box.Text}");
                ToolTip.SetTip(box,
                    $"{caption} выделения, {minimum:0}…{maximum:0} {unit}, только целые значения. Введите число и нажмите Enter.");
            }
            finally
            {
                syncing = false;
            }
        });

        return QuickField(caption, new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                box,
                new TextBlock
                {
                    Text = unit,
                    VerticalAlignment = VerticalAlignment.Center,
                }.Classed("muted"),
            },
        });
    }

    /// <summary>Refreshes the strip and the object panel together - they share one list.</summary>
    private void SyncQuickBar()
    {
        foreach (var sync in _quickSyncs)
        {
            sync();
        }
    }

    // ====================================================================== rail

    private Control BuildToolRail()
    {
        Button Tool(EditorRailEntry entry)
        {
            var button = Ui.ToolButton(
                entry.AutomationId,
                EditorLabels.Tool(entry.Tool),
                MapIcons.Tool(entry.Tool),
                ToolShortcut(entry.Tool),
                () => _editor.ActiveTool = entry.Tool);
            _toolButtons[entry.Tool] = button;
            return button;
        }

        // Assets is the ONE way into placing a catalog object. The old rail also carried a
        // separate "Штамп" tool, so the same job had two competing entries and the user had to
        // know that one of them only worked after the other. Picking an asset arms placement by
        // itself, which is why this button is registered as the Stamp tool's own rail button: the
        // active marker then tells the truth without a second icon existing.
        Button Library(EditorRailEntry entry)
        {
            var button = Ui.ToolButton(
                entry.AutomationId,
                "Ассеты — каталог гор, деревьев и других объектов",
                MapIcons.Assets,
                ToolShortcut(entry.Tool),
                () =>
                {
                    // Pressing Ассеты puts the editor into the placement context AND opens the
                    // catalog. Without the first half the rail marker lied: the button looked
                    // active while the editor was still on whatever tool came before it, and a
                    // user who closed the catalog was left with no way back into placement.
                    _editor.ActiveTool = EditorTool.Stamp;
                    OpenAssetCatalog(focusSearch: true);
                    if (_editor.HasChosenAsset)
                    {
                        _editor.ArmStampPlacement();
                    }
                });
            _toolButtons[entry.Tool] = button;
            return button;
        }

        // The order is the decision, so it is read from the shared layout rather than written out
        // again here: the rail and the test that guards it cannot drift apart.
        var stack = new StackPanel { Spacing = 4, Margin = new Thickness(4, 8) };
        foreach (var group in EditorSurfaceLayout.RailGroups)
        {
            if (stack.Children.Count > 0)
            {
                stack.Children.Add(RailDivider());
            }

            foreach (var entry in group)
            {
                stack.Children.Add(entry.Kind == EditorRailEntryKind.Library ? Library(entry) : Tool(entry));
            }
        }

        // The rail scrolls rather than spilling over the hint bar: the quick strip took a band of
        // its own, and at a short window the last tools used to be drawn on top of the hint text.
        return new Border
        {
            Width = Ui.RailWidth,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = stack,
            },
        }.Classed("rail");
    }

    private static Control RailDivider() => new Border
    {
        Height = 1,
        Margin = new Thickness(8, 3),
        Background = Application.Current?.FindResource("MapChromeBorderBrush") as IBrush,
    };

    // ============================================================ context panel

    private Control BuildContextPanel()
    {
        BuildToolPanels();
        BuildSelectionSections();
        BuildContextRoots();

        var collapse = Ui.TextButton("CollapseContextPanelButton", "‹", ToggleContextPanel, "compact");
        AutomationProperties.SetName(collapse, "Свернуть или развернуть панель инструмента");
        _contextTitle.Bind(ToolTip.TipProperty, new Binding(nameof(TextBlock.Text)) { Source = _contextTitle });
        BuildContextSwitch();
        var header = new Border
        {
            Child = new DockPanel { Children = { collapse.Docked(Dock.Right), _contextTitle } },
        }.Classed("panel-header");

        _contextBody = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border
            {
                Padding = new Thickness(12, 10, 12, 16),
                Child = _contextHost,
            },
        };

        _contextPanel = new Border
        {
            Width = Ui.ContextPanelWidth,
            Child = new DockPanel { Children =
            {
                header.Docked(Dock.Top),
                new Border { Padding = new Thickness(8, 4), Child = _contextSwitch }.Docked(Dock.Top),
                BuildEditableAreaPlacementActions().Docked(Dock.Bottom),
                _contextBody,
            } },
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
        _toolPanels[EditorTool.River] = BuildRiverPanel();
    }

    // Select only selects existing objects. Assets are a separate rail command, while object
    // creation remains owned by Shape and Stamp, so the tool never changes meaning under the user.
    private static Control BuildSelectPanel() => new Border();

    private Control BuildLinePanel()
    {
        var (modes, buttons) = Ui.Segmented("LineDrawMode",
            [("FreeDraw", "Free Draw", null), ("Pen", "Pen / Nodes", null)],
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
                Explanation("Free Draw — провести по холсту. Pen — ставить узлы кликами, Enter завершает, Esc отменяет."),
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
            ("FreeDraw", "Free Draw", ShapeDrawMode.FreeDraw), ("Pen", "Pen", ShapeDrawMode.Pen),
            ("Brush", "Brush", ShapeDrawMode.Brush), ("Rectangle", "Прямоугольник", ShapeDrawMode.Rectangle),
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
                Explanation("Перетаскивание задаёт фигуру: Shift — правильная, Alt — от центра. Pen ставит узлы кликами. Boolean работает над двумя выделенными фигурами."),
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
                Explanation("Рисуйте по холсту. Штрих ложится на слой кисти."),
                BuildRasterToolReadiness("Brush"),
                Ui.Section("Материал", new Ui.ChoiceGrid
                {
                    MaxColumns = 1,
                    Children =
                    {
                        Ui.TextButton("BrushSolidMaterialButton", "Цвет", () =>
                        {
                            _editor.ClearBrushTexture();
                            _editor.BrushSettings = _editor.BrushSettings with { Eraser = false };
                            SyncAppearanceSections();
                        }, "segment"),
                        Ui.CommandButton(
                            "BrushTextureMaterialButton", "Текстура из библиотеки", MapIcons.Assets,
                            () => OpenAssetCatalog(focusSearch: true), "segment"),
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
                Ui.Section("Форма отпечатка", BuildBrushTipList()),
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

    /// <summary>
    /// The tip chooser as a list: one row per shape, each showing the stamp the engine actually
    /// prints, its name and what it is for, with the current one selected.
    /// <para>
    /// It used to be seven identical text buttons in a wrap panel. Nothing showed which tip was in
    /// effect, the names alone did not say what a shape looked like, and at a narrow panel width
    /// the row wrapped into an unreadable block.
    /// </para>
    /// </summary>
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

        // No height cap and no scroller of its own: seven tips are meant to be compared at a
        // glance, and the context panel around them already scrolls.
        var list = new ListBox
        {
            Name = "BrushTipList",
            SelectionMode = SelectionMode.Single,
            // This fixed seven-item chooser is already inside the context scroller. A virtual
            // panel can leave its last rows unrealized when the nested scroller is disabled.
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel()),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(list, "Форма отпечатка кисти");

        var rows = new List<ListBoxItem>(tips.Length);
        foreach (var tip in tips)
        {
            var row = new ListBoxItem
            {
                Padding = new Thickness(6, 5, 6, 5),
                Content = new DockPanel
                {
                    LastChildFill = true,
                    Children =
                    {
                        new Border
                        {
                            Margin = new Thickness(0, 0, 9, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Child = new BrushTipPreview(
                                EditorViewModel.PreviewTip(tip.Kind),
                                EditorViewModel.PreviewSoftness(tip.Kind)),
                        }.Docked(Dock.Left),
                        new StackPanel
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new TextBlock { Text = tip.Name, TextWrapping = TextWrapping.Wrap },
                                new TextBlock { Text = tip.Purpose, TextWrapping = TextWrapping.Wrap }
                                    .Classed("faint", "role-caption"),
                            },
                        },
                    },
                },
                Tag = tip.Kind,
            }.Named("BrushTip" + tip.Kind);
            AutomationProperties.SetName(row, tip.Name + ". " + tip.Purpose);
            ToolTip.SetTip(row, tip.Purpose);
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

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Explanation("Круг рисует сушу от руки. Любая другая форма превращает протягивание "
                            + "по холсту в область: рамка показывает, что будет залито."),
                BuildRasterToolReadiness("Land"),
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
                            if (ReadBounds(out var region))
                                _editor.ApplyLandShape(_editor.ActiveLandShape, region);
                        }, "compact"),
                        new TextBlock { Text = "Перемещение" }.Classed("role-panel"),
                        Ui.PropertyRow("Delta X", dx), Ui.PropertyRow("Delta Y", dy), snap,
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

    private static double WholeNumber(double value)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }

    private static bool TryWholeNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

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
        var openLibrary = ShortcutTextButton(
            "OpenLibraryButton", "Каталог ассетов",
            EditorShortcutSettings.Commands.StampFocusLibrary,
            () => OpenAssetCatalog(focusSearch: true), "compact");
        openLibrary.Classes.Add("accent");

        // The asset itself opens the panel: its picture, its name and the one button that goes
        // back to the catalog. Everything below is a property OF that asset and of the placement
        // about to happen, which is what the old "Штамп" heading never said.
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                BuildCompactStampCatalog(),
                openLibrary,
                _stampGestureBlock,
                BuildEditableAreaPanel(),
                BuildEditablePathPanel(),
                BuildStampEditor("FutureStamp"),
                _stampCatalogDetails,
                _stampPlacementOptions,
                Ui.TextButton(
                    "CreateStampPlacementLayerButton",
                    "Создать слой объектов и продолжить",
                    () =>
                    {
                        _editor.CreateObjectLayerAndContinuePlacement();
                        SyncSidePanels();
                    }, "compact"),
                Disclosure("PlacementShortcuts", "Сочетания для размещения", new StackPanel { Spacing = 4, Children = {
                ShortcutChipRow(
                    [EditorShortcutSettings.Commands.StampPreviousVariant,
                     EditorShortcutSettings.Commands.StampNextVariant], "вариант"),
                ShortcutChipRow(
                    [EditorShortcutSettings.Commands.PrimaryUp,
                     EditorShortcutSettings.Commands.PrimaryDown], "размер"),
                ShortcutChipRow(
                    [EditorShortcutSettings.Commands.RotateLeft,
                     EditorShortcutSettings.Commands.RotateRight], "поворот"),
                ShortcutChipRow(
                    [EditorShortcutSettings.Commands.TertiaryUp,
                     EditorShortcutSettings.Commands.TertiaryDown], "область"),
                ShortcutChipRow(
                    [EditorShortcutSettings.Commands.StampDensityUp,
                     EditorShortcutSettings.Commands.StampDensityDown], "плотность"),
                } }, false),
            },
        };
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
            MinWidth = 40,
            FontSize = 13,
            TextAlignment = Avalonia.Media.TextAlignment.Right,
        }.Classed("muted").Named(name + "Value");

        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = WholeNumber(Math.Clamp(read() ?? minimum, minimum, maximum)),
            SmallChange = step,
            LargeChange = step * 5,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
        }.Named(name);
        AutomationProperties.SetName(slider, $"{label}, {unit}, целое значение");
        ToolTip.SetTip(slider, $"{label}. Только целые значения; стрелками — шаг {step:0} {unit}");

        var dragging = false;
        string Format(double value) => unit.Length == 0
            ? WholeNumber(value).ToString("0", CultureInfo.InvariantCulture)
            : $"{WholeNumber(value).ToString("0", CultureInfo.InvariantCulture)} {unit}";
        readout.Text = Format(slider.Value);

        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty)
            {
                return;
            }

            var value = WholeNumber(slider.Value);
            if (Math.Abs(slider.Value - value) > 0.0001)
            {
                slider.Value = value;
                return;
            }

            readout.Text = Format(value);
            if (!_syncingAppearance)
            {
                apply(value, false);
            }
        };
        slider.AddHandler(PointerPressedEvent,
            (_, _) => dragging = true, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        slider.PointerCaptureLost += (_, _) =>
        {
            dragging = false;
            if (!_syncingAppearance)
            {
                apply(WholeNumber(slider.Value), true);
            }
        };
        slider.KeyUp += (_, e) =>
        {
            if (!_syncingAppearance && e.Key is Key.Left or Key.Right or Key.Up or Key.Down
                or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            {
                apply(WholeNumber(slider.Value), true);
            }
        };
        slider.LostFocus += (_, _) =>
        {
            if (!_syncingAppearance && !dragging)
            {
                apply(WholeNumber(slider.Value), true);
            }
        };

        _appearanceSyncs.Add(() =>
        {
            if (dragging)
            {
                return;
            }

            var current = read();
            if (current is { } value && Math.Abs(slider.Value - WholeNumber(value)) > 0.0001)
            {
                slider.Value = WholeNumber(Math.Clamp(value, minimum, maximum));
            }

            readout.Text = Format(slider.Value);
        });

        var header = new DockPanel();
        if (trailing is not null)
        {
            header.Children.Add(trailing.Docked(Dock.Right));
        }

        header.Children.Add(readout.Docked(Dock.Right));
        header.Children.Add(Ui.ParameterLabel(label));

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
            Width = 24,
            Height = 24,
            MinHeight = 24,
            Padding = new Thickness(4, 1),
            Margin = new Thickness(4, 0, 0, 0),
        }.Named(name).Classed("icon");
        AutomationProperties.SetName(button, title);
        ToolTip.SetTip(button, title);
        button.Click += (_, _) => reset();
        return button;
    }

    /// <summary>A numeric field committing on Enter or focus loss; bad input is an error, not a command.</summary>
    private Control AppearanceNumberBox(
        string name, string label, string? unit, Func<double?> read, Action<double> apply,
        string format = "0", double captionWidth = 92)
    {
        var box = NumberBox(name);
        void Commit()
        {
            if (_syncingAppearance)
            {
                return;
            }

            var text = box.Text ?? "";
            if (!TryWholeNumber(text, out var value))
            {
                _editor.ReportInspectorError($"«{label}» должно быть целым числом.");
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

    /// <summary>
    /// The one colour row of the editor: a swatch showing the current value that opens the shared
    /// picker. Every colour property goes through here, so there is a single behaviour to learn
    /// and a single place where hex parsing, preview and undo granularity are decided.
    /// </summary>
    private Control AppearanceColorBox(
        string name, string label, Func<string?> read, Action<string> apply, double captionWidth = 92)
        => AppearanceColorBox(name, label, read, (value, _) => apply(value), captionWidth,
            supportsAlpha: true, cancelPreview: null);

    /// <summary>
    /// The preview-capable overload. <paramref name="apply"/> receives <c>commit: false</c> while
    /// the user is still dragging inside the picker - that path previews on the canvas and writes
    /// no history - and <c>commit: true</c> exactly once when the choice is applied.
    /// </summary>
    private Control AppearanceColorBox(
        string name, string label, Func<string?> read, Action<string, bool> apply,
        double captionWidth = 92, bool supportsAlpha = true, Action? cancelPreview = null)
    {
        var field = new ColorField(
            name, label, read, apply, cancelPreview,
            supportsAlpha: supportsAlpha,
            supportsPreview: cancelPreview is not null,
            captionWidth: captionWidth);
        _appearanceSyncs.Add(field.Sync);
        _colorFields.Add(field);
        return field.Row;
    }

    /// <summary>Own-process smoke hook: one named colour property of the real editor.</summary>
    public ColorField ColorFieldForSmoke(string name) =>
        _colorFields.FirstOrDefault(field => field.Name == name)
        ?? throw new InvalidOperationException($"No colour field named {name}.");

    /// <summary>A named on/off property whose checked state always mirrors the document.</summary>
    private ToggleButton AppearanceToggle(string name, string label, Func<bool?> read, Action<bool> apply)
    {
        var toggle = Ui.PropertyToggle(name, label);
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

    private Ui.InlineChoice? _expandedPropertyChoice;

    private Ui.InlineChoice PropertyChoice(string name, string label, string value,
        Func<IReadOnlyList<(string Text, Action Choose)>> choices)
    {
        var choice = new Ui.InlineChoice(name, label, value, choices);
        choice.Opened += (_, _) =>
        {
            _expandedPropertyChoice?.Close();
            _expandedPropertyChoice = choice;
        };
        return choice;
    }

    private (Control Row, Button Button) PropertyValueRow(string name, string label, string value,
        IReadOnlyList<(string Text, Action Choose)> options)
    {
        var choice = PropertyChoice(name, label, value, () => options);
        return (Ui.PropertyRow(label, choice), choice.Header);
    }

    /// <summary>Stored keys in a bounded inline list, applied only by choosing a named option.</summary>
    private Control AppearanceChoice(
        string name, string label, IReadOnlyList<(string Key, string Text)> options,
        Func<string?> read, Action<string> apply)
    {
        var choice = PropertyChoice(name, label, options[0].Text,
            () => options.Select(option => (option.Text, (Action)(() =>
            {
                if (!_syncingAppearance && option.Key != read()) apply(option.Key);
            }))).ToArray());

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

            choice.SetValue(options[index].Text);
        });

        return Ui.PropertyRow(label, choice);
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
                new Ui.ChoiceGrid { MaxColumns = 2, Children = { noneButton, standardButton } },
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
        _primitiveSection = BuildPrimitiveSection();
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
                AppearanceNumberBox((shape ? "Shape" : "Line") + "ControlInXBox", "Handle in X", "%",
                    () => _editor.SelectedVectorNode?.ControlInX * 100, value => EditNode(node => node with { ControlInX = value / 100 })),
                AppearanceNumberBox((shape ? "Shape" : "Line") + "ControlInYBox", "Handle in Y", "%",
                    () => _editor.SelectedVectorNode?.ControlInY * 100, value => EditNode(node => node with { ControlInY = value / 100 })),
                AppearanceNumberBox((shape ? "Shape" : "Line") + "ControlOutXBox", "Handle out X", "%",
                    () => _editor.SelectedVectorNode?.ControlOutX * 100, value => EditNode(node => node with { ControlOutX = value / 100 })),
                AppearanceNumberBox((shape ? "Shape" : "Line") + "ControlOutYBox", "Handle out Y", "%",
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
                portalChoice,
                Ui.PropertyRow("Position, %", position), Ui.PropertyRow("Width", width), addPortal,
                AppearanceNumberBox((shape ? "Shape" : "Line") + "SelectedPortalPosition", "Position", "%",
                    () => _editor.SelectedVectorPortal?.Position * 100,
                    value => _editor.SetSelectedPortal(value / 100, _editor.SelectedVectorPortal?.Width ?? 0)),
                AppearanceNumberBox((shape ? "Shape" : "Line") + "SelectedPortalWidth", "Width", "px",
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
                    () => Fill()?.Color,
                    (value, commit) => _editor.SetShapeFillColor(value, commit),
                    cancelPreview: _editor.CancelAppearanceGesture),
                AppearanceSlider("ShapeFillOpacitySlider", "Плотность заливки", 0, 100, "%",
                    () => Fill()?.Opacity * 100, (v, c) => _editor.SetShapeFillOpacityPercent(v, c), 5),
                BuildGradientBlock("Shape"),
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

        return new Expander
        {
            Header = "Выделение: общие свойства и порядок", Content = body,
            IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch,
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
                var mixed = _editor.SelectionCount > 0 && current is null;
                caption.Text = _editor.SelectionCount == 0
                    ? "—"
                    : mixed ? MixedLayerCaption : current!.Name;
                Ui.SetClass(caption, "mixed", mixed);
                Ui.SetClass(button, "mixed", mixed);
                button.IsEnabled = _editor.SelectionCount > 0 && targets.Count > 0;

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

        var delete = Ui.CommandButton("DeleteObjectButton", "Удалить объект", MapIcons.Trash,
            _editor.DeleteSelectedObject, "destructive", "compact", "centred");
        delete.Margin = new Thickness(0, 6, 0, 0);
        DynamicTip(delete, () =>
            $"Удалить выделенный объект. Действие можно отменить ({_editor.Shortcuts.Get(EditorShortcutSettings.Commands.Undo)}).");

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                BuildObjectPrimarySection("Object"),
                Ui.Section("Состояние и действия", new StackPanel
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
                }).Named("CommonObjectActions"),
            },
        };
    }

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
        _sideTabs = new TabControl
        {
            Name = "EditorSideTabs",
            ItemsSource = new[]
            {
                new TabItem { Header = TabHeader("Слои", MapIcons.Layers), Content = BuildLayersTab() },
                new TabItem { Header = TabHeader("Объекты", MapIcons.Objects), Content = BuildObjectsTab() },
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
                    _baseColorLayerBlock,
                    _landLayerBlock,
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
        var objectEffects = Ui.Section("Слой объектов · эффекты и подслой", new StackPanel
        {
            Spacing = 4,
            Children =
            {
                AppearanceToggle("ObjectLayerClippingToggle", "Обрезать по слою ниже",
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
            _editor.DeleteActiveLayer, "destructive");
        delete.HorizontalAlignment = HorizontalAlignment.Stretch;
        delete.HorizontalContentAlignment = HorizontalAlignment.Center;
        delete.Margin = new Thickness(0, 6, 0, 0);
        DynamicTip(delete, () =>
            $"Удалить слой вместе с содержимым. Действие можно отменить ({_editor.Shortcuts.Get(EditorShortcutSettings.Commands.Undo)}).");

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                nameRow, visibilityRow, lockRow, exactOpacity, exactBlend,
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
        var (row, button) = PropertyValueRow(
            "LandStyleButton", "Берег", "Обрыв",
            [("Обрыв", () => { if (Style()?.RippleCount > 0) _editor.CycleActiveLandStyle(); }),
             ("Берег с прибоем", () => { if (Style()?.RippleCount == 0) _editor.CycleActiveLandStyle(); })]);
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
            "FilterStyleButton", "Пресет", "Тёплый",
            [("Тёплый", () => { if (Filter()?.Temperature <= 0) _editor.CycleActiveFilter(); }),
             ("Холодный", () => { if (Filter()?.Temperature > 0) _editor.CycleActiveFilter(); })]);
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
        var filterStrip = new Ui.ChoiceGrid { MaxColumns = 3, MinimumItemWidth = 60, Gap = 6 };
        foreach (var filter in new[]
                 {
                     ObjectTypeFilter.All, ObjectTypeFilter.Stamps, ObjectTypeFilter.Text,
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
        var compositions = Disclosure("ObjectCompositions", "Композиции", compositionActions, false);

        var body = new Grid
        {
            Margin = new Thickness(12, 10, 12, 18),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 10,
            Children = { header, listHost, compositions },
        };
        Grid.SetRow(listHost, 1);
        Grid.SetRow(compositions, 2);
        return body;
    }

    private Control BuildAssetsTab()
    {
        BuildLibraryDiagnosticBanner();
        BuildLibraryGrid();
        _selectedSetBlock = BuildSelectedSetBlock();
        var imports = new Ui.ChoiceGrid
        {
            MaxColumns = 1,
            Children =
            {
                Ui.TextButton("ImportAssetButton", "Добавить файл", () => _ = ImportAssetAsync(linked: false), "compact"),
                Ui.TextButton("ImportAssetSetButton", "Добавить набор", () => _ = ImportAssetSetAsync(), "compact"),
                Ui.TextButton("LinkAssetButton", "Связать файл", () => _ = ImportAssetAsync(linked: true), "compact"),
            },
        };
        _libraryCollections = Disclosure("LibraryCollectionsDetails", "Коллекции и источники", _collectionTree, expanded: true);
        var navigation = new ScrollViewer
        {
            Content = new StackPanel { Spacing = 12, Children = { _libraryCollections, imports } },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var top = new StackPanel
        {
            Spacing = 8, Children =
            {
                _breadcrumbs, _libraryDiagnostic, _assetSearch, BuildLibraryFilterControls(), _libraryCountText,
            },
        };
        var managementScroll = new ScrollViewer { MaxHeight = 230, Content = _selectedSetBlock, ClipToBounds = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var management = Disclosure("LibraryOrganizationDetails", "Организация выбранного набора", managementScroll, expanded: false);
        var filterContent = (Control)_libraryFilterDetails.Content!;
        _libraryFilterDetails.Content = null;
        var filterScroll = new ScrollViewer { Content = filterContent, MaxHeight = 100, ClipToBounds = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _libraryFilterDetails.Content = filterScroll;
        // These are alternate editing sections. Keep the search and gallery available when either opens.
        _libraryFilterDetails.PropertyChanged += (_, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty && _libraryFilterDetails.IsExpanded)
                management.IsExpanded = false;
        };
        management.PropertyChanged += (_, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty && management.IsExpanded)
                _libraryFilterDetails.IsExpanded = false;
        };
        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10,
            Children = { top, _libraryGrid, management } };
        Grid.SetRow(_libraryGrid, 1);
        Grid.SetRow(management, 2);
        var body = new Grid { Margin = new Thickness(16), ColumnDefinitions = new ColumnDefinitions("220,*"),
            ColumnSpacing = 16, Children = { navigation, main } };
        Grid.SetColumn(main, 1);
        body.SizeChanged += (_, _) =>
        {
            body.ColumnDefinitions[0].Width = new GridLength(body.Bounds.Width < 850 ? 170 : 220);
            filterScroll.MaxHeight = Math.Clamp(body.Bounds.Height * .16, 88, 220);
            managementScroll.MaxHeight = Math.Clamp(body.Bounds.Height * .12, 64, 180);
        };
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
    /// One real virtualized list over a flat sequence of group headers and cards. Avalonia
    /// realises only the containers it can show, so grouping ten thousand cards by tag still
    /// costs the handful of rows on screen rather than a second control tree.
    /// </summary>
    private void BuildLibraryGrid()
    {
        AutomationProperties.SetName(_libraryGrid, "Галерея ассетов");
        _libraryGrid.ItemTemplate = new FuncDataTemplate<GalleryRow>(
            (row, _) => BuildGalleryRow(row), supportsRecycling: false);
        _libraryGrid.SizeChanged += (_, _) =>
        {
            if (_galleryResizeQueued) return;
            _galleryResizeQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _galleryResizeQueued = false;
                var columns = Math.Clamp((int)(_libraryGrid.Bounds.Width / 180), 1, 8);
                if (columns == _galleryColumns) return;
                _galleryColumns = columns;
                RebuildGalleryRows();
            }, DispatcherPriority.Loaded);
        };
        _libraryGrid.LayoutUpdated += (_, _) => ReportGalleryRange();
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

        var quickFilters = new Ui.ChoiceGrid { MaxColumns = 3, MinimumItemWidth = 112 };
        var advancedFilters = new Ui.ChoiceGrid { MaxColumns = 3, MinimumItemWidth = 112 };
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
        var (mode, modeButtons) = Ui.Segmented("StampMode", [
            ("One", "Один", null),
            ("Series", "Серия", null),
            ("Area", "Россыпь", null),
            ("Path", "Путь", null),
            ("FillArea", "Область", null),
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
            "RandomFlipToggle", "Отражать", "Случайное зеркальное отражение ассета",
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
            },
        };

        _seriesSpacingControl = AppearanceSlider("StampSeriesSpacingSlider", "Шаг серии", 10, 400, "% размера",
            () => _editor.SeriesSpacingPercent, (value, commit) => _editor.SetSeriesSpacingPercent(value, commit));
        _stampPlacementOptions = Disclosure("PlacementOptions", "Параметры размещения", new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Автовыбор" }.Classed("role-panel"),
                    autoSelect,
                    Disclosure("StampScatterDetails", "Варианты и случайность",
                        new StackPanel { Spacing = 4, Children = { _randomVariantToggle, randomAngleChoice, _randomFlipToggle } }, false),
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
            }, false);
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
        foreach (var pair in _autoSelectButtons)
        {
            Ui.SetSelected(pair.Value, pair.Key == _editor.PlacementAutoSelect);
        }
        SyncStampToolPanel();
    }

    private Control BuildCompactStampCatalog()
    {
        _stampToolThumbnail = new Image { Width = 56, Height = 56, Stretch = Stretch.Uniform };
        _stampVariantStrip = BuildCompactVariantList();
        _stampRecentStrip = BuildCompactSetList("StampRecentSet");
        _stampFavoriteStrip = BuildCompactSetList("StampFavoriteSet");
        AutomationProperties.SetName(_stampVariantStrip, "Варианты текущего ассета");
        AutomationProperties.SetName(_stampRecentStrip, "Недавние ассеты");
        AutomationProperties.SetName(_stampFavoriteStrip, "Избранные ассеты");
        _stampVariantCounter = new TextBlock { MinWidth = 42, TextAlignment = Avalonia.Media.TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center }.Classed("role-caption");
        _stampVariantName = Ui.Trimmed("", "role-caption");
        var previous = Ui.TextButton("StampPreviousVariantButton", "‹", () =>
        {
            _editor.PreviousVariant(); SyncStampToolPanel();
        }, "compact");
        var next = Ui.TextButton("StampNextVariantButton", "›", () =>
        {
            _editor.NextVariant(); SyncStampToolPanel();
        }, "compact");
        previous.Width = next.Width = 30;
        _commandButtons.Add((previous, "Предыдущий вариант", EditorShortcutSettings.Commands.StampPreviousVariant));
        _commandButtons.Add((next, "Следующий вариант", EditorShortcutSettings.Commands.StampNextVariant));
        var details = new StackPanel
        {
            Margin = new Thickness(8, 0, 0, 0), Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _stampToolCurrentText,
                _stampVariantName,
            },
        };
        Grid.SetColumn(details, 1);
        _stampCurrentAssetButton = new Button
        {
            Name = "StampCurrentAssetButton",
            Padding = new Thickness(4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("56,*"),
                Children = { _stampToolThumbnail, details },
            },
        }.Classed("compact", "asset-entry");
        _stampCurrentAssetButton.Click += (_, _) =>
        {
            if (_editor.StampPlacementMode == StampPlacementMode.FillArea) _editor.StartEditableArea();
            else if (_editor.StampPlacementMode == StampPlacementMode.Path) _editor.StartEditablePath();
            else _editor.ArmStampPlacement();
            _canvas.ShowPlacementPreviewAtCenter();
            SyncContextArea();
            _canvas.Focus();
        };
        _stampCatalogDetails = Disclosure("StampCatalogDetails", "Варианты и быстрый доступ", new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Варианты" }.Classed("role-caption"),
                _stampVariantStrip,
                new TabControl { Items =
                {
                    new TabItem { Header = "Недавние", Content = _stampRecentStrip },
                    new TabItem { Header = "Избранное", Content = _stampFavoriteStrip },
                } },
            },
        }, false);
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                _stampCurrentAssetButton,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children = { previous, _stampVariantCounter, next } },
            },
        };
    }
    private Button CompactStampSetButton(AssetSetSummary summary, string prefix)
    {
        var image = _thumbnails.Image(_editor.LibraryThumbnailPath(summary));
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
                        Child = image is null
                            ? MapIcons.Stroked(MapIcons.Missing, 18)
                            : new Image { Source = image, Stretch = Stretch.Uniform },
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
            : set.Name;
        _stampToolCurrentText.MaxLines = 1;
        _stampToolCurrentText.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(_stampToolCurrentText, _stampToolCurrentText.Text);
        _stampVariantCounter.Text = _editor.CurrentVariantLabel;
        _stampVariantName.Text = _editor.CurrentVariant?.Label ?? "Выберите вариант";
        ToolTip.SetTip(_stampVariantName, _stampVariantName.Text);
        _stampCurrentAssetButton.IsEnabled = _editor.CurrentVariant is not null;
        var currentAction = _editor.StampPlacementMode == StampPlacementMode.Path
            ? "Начать новый путь" : "Разместить ещё";
        var currentHint = _editor.CurrentVariant is null ? "Сначала выберите ассет в каталоге."
            : $"{currentAction}: {set?.Name} · {_editor.CurrentVariant.Label}";
        AutomationProperties.SetName(_stampCurrentAssetButton, currentHint);
        ToolTip.SetTip(_stampCurrentAssetButton, currentHint);
        _stampToolAreaText.Text = _editor.StampAreaSummary;
        _stampToolThumbnail.Source = _thumbnails.Image(
            _editor.CurrentVariant is { } variant
                ? _editor.LibraryThumbnailPathForVariant(variant.Id)
                : null);
        SyncAreaAssetCard();
        SyncCompactVariantStrip();
        SyncCompactSetList(_stampRecentStrip, _editor.CompactRecentSets);
        SyncCompactSetList(_stampFavoriteStrip, _editor.CompactFavoriteSets);
        AutomationProperties.SetName(_stampToolCurrentText, _stampToolCurrentText.Text);
        AutomationProperties.SetName(_stampToolAreaText, _stampToolAreaText.Text);
    }

    private void SyncCompactVariantStrip()
    {
        var variants = _editor.SelectedSetVariants;
        if (!ReferenceEquals(_compactVariants, variants))
        {
            _compactVariants = variants;
            _stampVariantStrip.ItemsSource = variants.Select((v, i) => new CompactVariant(v, i)).Chunk(4).ToArray();
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
    /// A tiny bounded LRU of decoded covers. Without the bound, scrolling a large library would
    /// hold every thumbnail it ever showed.
    /// </summary>
    private sealed class ThumbnailCache(int capacity) : IDisposable
    {
        private const int MaxThumbnailDimension = 512;
        private const long MaxDecodedBytes = 64L * 1024 * 1024;
        private readonly Dictionary<string, (Bitmap Image, long Used, long Bytes)> _entries =
            new(StringComparer.OrdinalIgnoreCase);
        private long _clock;
        private long _decodedBytes;

        public void Dispose()
        {
            foreach (var entry in _entries.Values) entry.Image.Dispose();
            _entries.Clear(); _decodedBytes = 0;
        }

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
                new Ui.ChoiceGrid
                {
                    MaxColumns = 1,
                    Children =
                    {
                        Ui.TextButton("AddWorldNoteButton", "В центре экрана",
                            _editor.AddNoteAtViewCentre, "compact"),
                        Ui.TextButton("AddObjectNoteButton", "К выделенному",
                            _editor.AddNoteForSelection, "compact"),
                    },
                },
                (_noteSelectionEditor = new Border
                {
                    Child = new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = "Выбранная заметка" }.Classed("role-panel"),
                            _noteTitle,
                            new Ui.ChoiceGrid
                            {
                                MaxColumns = 2,
                                Children =
                                {
                                    Ui.TextButton("NoteHeadingButton", "Заголовок", () => PrefixSelection("## "), "compact"),
                                    Ui.TextButton("NoteListButton", "Список", () => PrefixSelection("- "), "compact"),
                                    Ui.TextButton("NoteBoldButton", "Полужирный", () => ReplaceSelection("**", "**", "текст"), "compact"),
                                    Ui.TextButton("NoteItalicButton", "Курсив", () => ReplaceSelection("*", "*", "текст"), "compact"),
                                },
                            },
                            linkTarget,
                            Ui.TextButton("NoteLinkButton", "Ссылка для выделенного текста", () =>
                                ReplaceSelection("[", $"]({(linkTarget.Text ?? "").Trim()})", "текст ссылки"), "compact"),
                            new TextBlock { Text = "Текст заметки" }.Classed("role-panel"),
                            _noteMarkdown,
                            new TextBlock { Text = "Предпросмотр заметки", TextWrapping = TextWrapping.Wrap }.Classed("role-panel"),
                            new Border { Padding = new Thickness(8), Child = preview }.Classed("group"),
                            new Ui.ChoiceGrid
                            {
                                MaxColumns = 1,
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
                }.Classed("group")),
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
        var create = Ui.AsyncButton("CreateVersionButton", "Создать версию", async () =>
        {
            await _editor.CreateVersionAsync();
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
                    Text = "Сохраните состояние карты и миниатюру. Название версии можно изменить позже.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
                create,
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
                Field("Итоговая ширина, px", _exportOutputWidthBox),
                Field("Итоговая высота, px", _exportOutputHeightBox),
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
                outputSize,
                _exportDetailSummary,
                _exportAspectToggle,
                modes,
                _exportTransparencyToggle,
                _exportGridToggle,
                _exportQualityPanel,
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
                     _saveState, _autosaveState, _selectionStatus, _activeLayerStatus, _gridStatus, _viewportStatus, _frameRateStatus,
                 })
        {
            block.Classes.Add("role-caption");
            block.Classes.Add("muted");
            block.TextTrimming = TextTrimming.CharacterEllipsis;
            block.MaxWidth = 260;
            block.Bind(ToolTip.TipProperty, new Binding(nameof(TextBlock.Text)) { Source = block });
        }

        _saveState.Classes.Add("role-caption");
        _statusDetail.Classes.Add("role-caption");
        _statusDetail.Classes.Add("danger");
        _statusDetail.TextTrimming = TextTrimming.CharacterEllipsis;
        _statusDetail.TextWrapping = TextWrapping.Wrap;
        _statusDetail.MaxLines = 2;
        _statusDetail.Margin = new Thickness(0, 4, 0, 0);
        _statusDetail.Bind(ToolTip.TipProperty, new Binding(nameof(TextBlock.Text)) { Source = _statusDetail });

        // Status groups wrap inside the remaining width; a long placement/error message owns
        // a separate row instead of painting over the save state or zoom in a compact window.
        var left = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                BuildStatisticsButton(), _autosaveState, _activeLayerStatus, _selectionStatus, _gridStatus,
            },
        };
        foreach (var child in left.Children)
            child.Margin = new Thickness(0, 0, 14, 0);

        var readouts = new DockPanel
        {
            Children =
            {
                _viewportStatus.Docked(Dock.Right),
                left,
            },
        };
        var bar = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(12, 5) };
        bar.Children.Add(readouts);
        Grid.SetRow(_statusDetail, 1);
        bar.Children.Add(_statusDetail);
        return new Border { Child = bar }.Classed("status-bar");
    }

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
            if (button.Content is TextBlock caption) caption.Text = text;
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

    private readonly HashSet<string> _pendingUiProperties = [];
    private readonly object _uiSyncGate = new();
    private bool _uiSyncQueued;
    private bool _sceneSyncPending;

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Cursor-only ghost movement is handled by the canvas; it cannot rebuild the tool panel.
        if (e.PropertyName is nameof(EditorViewModel.PendingPlacement) or nameof(EditorViewModel.PlacementPreviewObjects)) return;
        lock (_uiSyncGate)
        {
            _pendingUiProperties.Add(e.PropertyName ?? "");
            if (_uiSyncQueued) return;
            _uiSyncQueued = true;
        }
        Dispatcher.UIThread.Post(FlushEditorUi, DispatcherPriority.Render);
    }

    private void FlushEditorUi()
    {
        HashSet<string> changed;
        bool scene;
        lock (_uiSyncGate)
        {
            changed = [.. _pendingUiProperties];
            _pendingUiProperties.Clear();
            _uiSyncQueued = false;
            scene = _sceneSyncPending;
            _sceneSyncPending = false;
        }
        SyncStatus();
        SyncEditablePathPanel();
        SyncEditableAreaPanel();
        SyncRiverPanel();
        if (scene) SyncSidePanels();
        if (changed.Overlaps([nameof(EditorViewModel.XText), nameof(EditorViewModel.YText),
            nameof(EditorViewModel.WidthText), nameof(EditorViewModel.HeightText),
            nameof(EditorViewModel.RotationText), nameof(EditorViewModel.HasEditableSelection)])) SyncInspector();
        if (scene || changed.Overlaps([nameof(EditorViewModel.ActiveTool), nameof(EditorViewModel.SelectionKind),
            nameof(EditorViewModel.SelectionCount), nameof(EditorViewModel.SelectedGroupId),
            nameof(EditorViewModel.ActiveLayer), nameof(EditorViewModel.HasPendingPlacement),
            nameof(EditorViewModel.PlacementAutoSelect), nameof(EditorViewModel.StampPlacementMode)]))
        {
            SyncContextArea();
            SyncStampGestureReadout();
        }
        if (changed.Overlaps([nameof(EditorViewModel.StampToolSettings), nameof(EditorViewModel.CurrentVariant),
            nameof(EditorViewModel.CompactRecentSets), nameof(EditorViewModel.CompactFavoriteSets)]))
        {
            SyncAppearanceSections();
            SyncStampToolPanel();
        }
        if (changed.Any(p => p.StartsWith("Canvas", StringComparison.Ordinal) || p.StartsWith("Region", StringComparison.Ordinal)))
            SyncCanvasWorkflow();
        if (changed.Any(p => p.StartsWith("Export", StringComparison.Ordinal)) || changed.Overlaps([
            nameof(EditorViewModel.CurrentExportPlan), nameof(EditorViewModel.CanStartExport),
            nameof(EditorViewModel.CanCancelExport), nameof(EditorViewModel.CanOpenExportFolder)])) SyncExport();
    }

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
            foreach (var (scale, button) in _exportScaleButtons)
                Ui.SetSelected(button, scale == _editor.ExportScaleFactor);

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
            _exportDetailSummary.Text = _editor.ExportDetailSummary;
            _exportMemoryDetails.Text = _editor.ExportMemoryDetails;
            _exportError.Text = _editor.ExportValidationError;
            _exportError.IsVisible = _editor.ExportValidationError.Length > 0;
            _exportProgress.Value = _editor.ExportProgress * 100;
            _exportProgress.IsVisible = _editor.ExportRunning || _editor.ExportProgress > 0;
            _exportProgressText.Text = _editor.ExportPreviewBusy
                ? "Обновление предпросмотра…"
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
            foreach (var button in _exportScaleButtons.Values) button.IsEnabled = settingsEnabled;
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
            _viewExportedPngButton.IsVisible = !_editor.ExportRunning
                && string.Equals(Path.GetExtension(_editor.ExportResultPath), ".png", StringComparison.OrdinalIgnoreCase)
                && File.Exists(_editor.ExportResultPath);
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
        SyncMapStatistics();
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
    /// The two halves of the context area, named so the user can move between them. A selection
    /// opens its inspector, but the tool it was drawn with stays one click away - hiding the tool
    /// would be as wrong as hiding the object, which is what the panel used to do.
    /// </summary>
    private void BuildContextSwitch()
    {
        var (control, buttons) = Ui.Segmented(
            "ContextScope",
            [("Object", "Объект", null), ("Tool", "Инструмент", null)],
            key =>
            {
                _contextPrefersToolPanel = key == "Tool";
                SyncContextArea();
            });

        _contextSwitchButtons.AddRange(buttons);
        _contextSwitch = control;
    }

    /// <summary>
    /// Decides what the single context area shows. Exactly one of two things: the settings of the
    /// active tool, or the properties of the current selection - never both, and never the
    /// properties of a kind that is not selected. Which one is the user's own choice through the
    /// scope switch, defaulting to the object right after a selection and to the tool right after
    /// a tool switch, so both actions always have a visible effect.
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
        // The strip is refreshed on every context change, including "nothing is selected" - the
        // panel's own refresh only runs while an inspector is mounted.
        SyncQuickBar();
        // A new selection points the panel at the object; a new tool points it at the tool. Both
        // remain reachable afterwards, which is the part the old ActiveTool == Select gate broke.
        var selectionSignature = _editor.SelectionCount == 0
            ? null
            : $"{_editor.SelectionCount}:{_editor.Session.SelectedObjectId}";
        if (_lastContextTool != _editor.ActiveTool)
        {
            _lastContextTool = _editor.ActiveTool;
            _contextPrefersToolPanel = true;
        }

        if (_lastContextSelection != selectionSignature)
        {
            _lastContextSelection = selectionSignature;
            if (selectionSignature is not null)
            {
                _contextPrefersToolPanel = false;
            }
        }

        var scope = EditorContextPanel.Resolve(
            _editor.ActiveTool,
            _editor.SelectionCount,
            _editor.SelectionKind is not null,
            _contextPrefersToolPanel);
        if (_editor.ActiveTool == EditorTool.Stamp && (_editor.SelectedEditablePath is not null || _editor.StampPlacementMode == StampPlacementMode.FillArea))
            scope = EditorContextPanelKind.ToolPanel;
        // A river is not a scene object: its panel is the only place its properties live.
        if (_editor.ActiveTool == EditorTool.River)
            scope = EditorContextPanelKind.ToolPanel;

        _contextSwitch.IsVisible = !_contextCollapsed && EditorContextPanel.ToggleApplies(_editor.SelectionCount);
        Ui.SetSelected(_contextSwitchButtons[0], scope is not EditorContextPanelKind.ToolPanel);
        Ui.SetSelected(_contextSwitchButtons[1], scope is EditorContextPanelKind.ToolPanel);
        ToolTip.SetTip(_contextSwitchButtons[1], "Настройки: " + EditorLabels.Tool(_editor.ActiveTool));

        if (scope == EditorContextPanelKind.MultiSelectionInspector)
        {
            _contextTitle.Text = "Свойства: выделение";
            SyncMultiSelectionPanel();
            _contextHost.Content = _multiSelectionRoot;
            return;
        }

        if (scope == EditorContextPanelKind.ObjectInspector && _editor.SelectionKind is { } kind)
        {
            _contextTitle.Text = "Свойства: " + EditorLabels.ObjectKind(kind);

            if (_editor.Session.SelectedObject is { } selected)
            {
                Ui.SetValue(_objectVisibilityValue, "Видимость",
                    EditorLabels.ObjectVisibilityValue(selected.Visible));
                Ui.SetValue(_objectLockValue, "Блокировка",
                    EditorLabels.ObjectLockValue(selected.Locked));
            }

            SyncLayerAndLevel();

            _kindSlot.Content = KindSection(kind);
            _contextHost.Content = _selectionRoot;
            return;
        }

        _contextTitle.Text = _editor.ActiveTool == EditorTool.Stamp ? "Следующее размещение" : EditorLabels.Tool(_editor.ActiveTool);
        _kindSlot.Content = null;
        _selectionEmpty.IsVisible = scope == EditorContextPanelKind.SelectIdle;
        _toolSlot.Content = _toolPanels[_editor.ActiveTool];
        _contextHost.Content = _selectIdleRoot;
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
        SyncLayerAndLevel();
    }

    /// <summary>Both inspector shapes show the same owner layer and level, so both refresh here.</summary>
    private void SyncLayerAndLevel()
    {
        foreach (var sync in _levelSyncs)
        {
            sync();
        }
    }

    /// <summary>
    /// The only place that decides which kind-specific block is shown. Exactly one block can be
    /// mounted at a time because they all share a single slot.
    /// </summary>
    private Control? KindSection(SceneObjectKind kind) =>
        EditorSurfaceLayout.SectionFor(kind) switch
        {
            ObjectKindSection.Asset => _stampSection,
            ObjectKindSection.Line => _lineSection,
            ObjectKindSection.Shape => _shapeSection,
            ObjectKindSection.Primitive => _primitiveSection,
            ObjectKindSection.Text => _textSection,
            ObjectKindSection.Composition => _compositionSection,
            _ => null,
        };

    private void BuildContextRoots()
    {
        _kindSlot = new ContentControl { Name = "SelectionKindSlot" };

        // Order is the message: what the object IS and the properties touched constantly come
        // first, the kind-specific block next, and exact geometry last. The old panel opened with
        // four coordinate boxes, so the level and the owning layer were below the fold.
        _selectionRoot = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                BuildSelectionCommon(),
                _kindSlot,
                Ui.Section("Геометрия", _inspectorPanel).Named("ObjectGeometrySection"),
            },
        };

        // The tool panels live in a slot, never as fixed children: a control can have only one
        // visual parent, and the context area now shows a tool panel in more situations than the
        // old "Select tool only" rule allowed.
        _toolSlot = new ContentControl { Name = "ToolPanelSlot" };
        _selectIdleRoot = new StackPanel
        {
            Spacing = 12,
            Children = { _selectionEmpty, _toolSlot },
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

        var (visibilityRow, visibilityButton) = PropertyValueRow(
            "MultiVisibilityButton", "Видимость", "-",
            [("Показан", () => _editor.SetSelectedObjectVisibility(true)),
             ("Скрыт", () => _editor.SetSelectedObjectVisibility(false))]);
        _multiVisibilityValue = visibilityButton;
        var (lockRow, lockButton) = PropertyValueRow(
            "MultiLockButton", "Блокировка", "-",
            [("Свободен", () => _editor.SetSelectedObjectLock(false)),
             ("Заблокирован", () => _editor.SetSelectedObjectLock(true))]);
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
                BuildObjectPrimarySection("Multi"),
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

        // The shape row never showed a selection at all, so the user could not tell which of the
        // six was in effect - or that clicking one had done anything.
        foreach (var (name, button) in _landShapeButtons)
        {
            var key = name.StartsWith("Land", StringComparison.Ordinal) ? name[4..] : name;
            Ui.SetClass(button, "active", LandShapeByKey(key) == _editor.ActiveLandShape);
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
    /// Copies the active layer into its property sheet. Every value control is refreshed with
    /// the value it currently holds, so the panel can never claim a setting it does not have.
    /// </summary>
    private void SyncLayerBlocks()
    {
        var layer = _editor.ActiveLayer;
        var editable = layer is not null and not BaseColorLayer;

        _noLayerState.IsVisible = layer is null;
        _layerIdentity.IsVisible = layer is not null;
        _layerCommonBlock.IsVisible = editable;
        _baseColorLayerBlock.IsVisible = layer is BaseColorLayer;
        _landLayerBlock.IsVisible = layer is LandLayer;
        _gridLayerBlock.IsVisible = layer is GridLayer;
        _filterLayerBlock.IsVisible = layer is FilterLayer;

        if (layer is null)
        {
            return;
        }

        _layerNameHeading.Text = layer.Name;
        ToolTip.SetTip(_layerNameHeading, layer.Name);
        _layerKindIcon.Content = MapIcons.Stroked(
            LayerIcon(layer), 16, Application.Current?.FindResource("MapAccentBrush") as IBrush);

        // Say something the heading does not already say. A layer named "Объекты" of kind
        // "Объекты" gains nothing from a second line repeating the word.
        var kind = EditorLabels.LayerKind(layer);
        var subtitle = layer is BaseColorLayer
            ? "Фон карты · нельзя удалить"
            : EditorLabels.SecondaryKind(layer.Name, kind) is null
                ? $"Наложение: {EditorLabels.Blend(layer.Blend)}"
                : kind;
        _layerKindText.Text = subtitle;

        if (!editable)
        {
            return;
        }

        Ui.SetValue(_visibilityValue, "Видимость", layer.Visible ? "Показан" : "Скрыт");
        Ui.SetValue(_lockValue, "Блокировка", layer.Locked ? "Заблокирован" : "Свободен");
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
                    () => _editor.ToggleLayerVisibility(captured.Id));
                Ui.SetRowAction(visibility, EditorLabels.VisibilityCommand(layer.Visible), !layer.Visible);

                var locking = Ui.RowAction(
                    "LayerRowLock_" + layer.Id,
                    EditorLabels.LockCommand(layer.Locked),
                    layer.Locked ? MapIcons.Lock : MapIcons.Unlock,
                    () => _editor.ToggleLayerLock(captured.Id));
                Ui.SetRowAction(locking, EditorLabels.LockCommand(layer.Locked), layer.Locked);

                actions.Add(visibility);
                actions.Add(locking);
            }

            // The kind is a useful subtitle only when it adds something the name does not say.
            var kind = EditorLabels.LayerKind(layer);
            var secondary = EditorLabels.SecondaryKind(layer.Name, kind);

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
            $"GroupRowEnter_{groupId:D}", "Открыть группу", MapIcons.ChevronRight,
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

        // A placed asset shows the asset itself. The generic kind glyph and the kind word next to
        // the name only ever repeated what the picture already says ("Дерево  Штамп"), so they are
        // kept for objects that have no picture of their own. The sublayer is what the row still
        // needs to say, because it is the object's own level inside the layer.
        var thumbnail = _thumbnails.Image(_editor.ObjectThumbnailPath(sceneObject));
        var secondary = new List<string>();
        if (thumbnail is null && row.Subtitle is { Length: > 0 } kindLabel)
        {
            secondary.Add(kindLabel);
        }

        // The level is the one number the row still has to carry, and it carries a sign so "+3"
        // and "-3" can never be read as the same thing.
        if (sceneObject.Sublayer != 0)
        {
            secondary.Add(EditorViewModel.FormatSublayer(sceneObject.Sublayer));
        }

        var listRow = Ui.ListRow(
            row.AutomationId,
            ObjectIcon(sceneObject.Kind),
            sceneObject.Name,
            secondary.Count == 0 ? null : string.Join(" · ", secondary),
            states,
            () =>
            {
                _editor.SelectObject(captured.Id, additive: _objectRowCtrl);
                SyncObjectListSelection();
                SyncContextArea();
            },
            $"{sceneObject.Name} · {EditorLabels.ObjectKind(sceneObject.Kind)} · уровень "
                + EditorViewModel.FormatSublayer(sceneObject.Sublayer),
            thumbnail);

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
            StampPlacementMode.Path => "Path",
            StampPlacementMode.FillArea => "FillArea",
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
            _randomFlipToggle, "Случайное зеркальное отражение ассета", _editor.StampRandomFlip);

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
        if (!ReferenceEquals(_gallerySourceRows, rows))
        {
            // Rebuilding the source drops the realised containers, so the tiles they registered
            // are stale: a recycled container would otherwise keep writing into a dead badge.
            _tileStates.Clear();
            _reportedLibraryRange = (-1, -1);
            _gallerySourceRows = rows;
            RebuildGalleryRows();
        }

        _libraryGrid.SelectedIndex = -1;
        _lastSelectedLibraryRow = _editor.SelectedLibraryRowIndex;

        // The grid stays in the accessibility tree even when empty - a control that disappears
        // cannot be found, focused or reported by a screen reader.
        var cards = _editor.LibrarySets.Count;
        // The gallery scrolls in the space left by filters and details; a forced minimum overlaps them.
        _libraryGrid.MinHeight = 0;
        _libraryGrid.ClipToBounds = true;

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
        _stampGestureBlock.IsVisible = hasSet || _editor.SelectedEditablePath is not null || _editor.CurrentAreaSource is not null;
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
        var variants = _editor.SelectedSetVariants;
        if (ReferenceEquals(_variantStrip.Tag, variants)) return;
        _variantStrip.Tag = variants;
        _variantStrip.ItemTemplate = new FuncDataTemplate<CompactVariant[]>((row, _) =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            foreach (var cell in row ?? []) panel.Children.Add(BuildVariantChoice(cell.Variant, cell.Index, "Variant_", 40));
            return panel;
        }, supportsRecycling: false);
        _variantStrip.ItemsSource = variants.Select((v, i) => new CompactVariant(v, i)).Chunk(6).ToArray();
    }

    private ToggleButton BuildVariantChoice(
        AssetVariant variant,
        int position,
        string namePrefix,
        double previewSize)
    {
        var thumbnail = _thumbnails.Image(_editor.LibraryThumbnailPathForVariant(variant.Id));
        var isCover = variant.Id == _editor.SelectedSet?.CoverVariantId;
        var availability = _editor.AvailabilityOf(variant.Sha256);
        var flagged = availability is not (AssetAvailability.Unknown or AssetAvailability.Available);

        Control face = new Border
        {
            Width = previewSize,
            Height = previewSize,
            Child = thumbnail is null
                ? MapIcons.Stroked(MapIcons.Missing, 16)
                : new Image { Source = thumbnail, Stretch = Stretch.Uniform },
        };

        // Cover and file-state marks remain distinct from the selected outline.
        if (isCover || flagged)
        {
            var mark = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Padding = new Thickness(2, 0),
                Child = flagged ? MapIcons.Stroked(MapIcons.Warning, 11)
                    : new TextBlock { Text = "обл.", FontSize = 12 },
            }.Classed("badge");
            ToolTip.SetTip(mark, flagged ? EditorViewModel.AvailabilityText(availability) : "Обложка набора");
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
            .Named(namePrefix + variant.Id).Classed("segment");
        var name = $"Вариант {position + 1} из {_editor.SelectedSetVariants.Count}"
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
        return button;
    }

    private void SyncNoteList()
    {
        if (_noteSelectionEditor is not null)
            _noteSelectionEditor.IsEnabled = _selectedNoteId is { } selected
                && _editor.Session.Document.Notes.Any(note => note.Id == selected);
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
                "Нажмите «Создать версию», чтобы сохранить текущее состояние и миниатюру."));
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
            AutomationProperties.SetName(previewControl, $"Миниатюра версии {checkpoint.Name}");

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

            var renameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 8, Children = { renameBox, rename } };
            Grid.SetColumn(rename, 1);

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
                        new Expander { Header = "Детали версии", Content = new TextBlock
                        {
                            Text = $"Миниатюра: {Math.Max(1, checkpoint.PreviewPng.Length / 1024)} КиБ · "
                                + $"объектов: {checkpoint.Snapshot.Layers.OfType<ObjectLayer>().Sum(layer => layer.Objects.Count)}",
                            TextWrapping = TextWrapping.Wrap,
                        }.Classed("faint", "role-caption") },
                        renameRow,
                        new Ui.ChoiceGrid
                        {
                            MaxColumns = 2, MaxWidth = 340, HorizontalAlignment = HorizontalAlignment.Left,
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
