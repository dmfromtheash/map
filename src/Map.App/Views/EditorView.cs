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
    private readonly RetainedSlot _contextHost = new() { Name = "ContextPanelHost" };
    private readonly ContentControl _contextIcon = new() { Name = "ContextPanelIcon", VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _contextKey = new Border { Name = "ContextPanelKey", VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { FontSize = 11 }.Classed("muted") }.Classed("key");
    private readonly ContentControl _contextHeaderAction = new() { Name = "ContextPanelHeaderAction", VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _contextScopeChip = new() { Name = "ContextScopeChip", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _contextScopeTarget = new() { Name = "ContextScopeTarget" };
    private StackPanel _contextHeaderTitle = null!;
    private StackPanel _sideTitle = null!;
    private Border _appearanceTransferHost = null!;
    private Border _contextScopeLine = null!;
    private Control _contextSwitch = null!;
    private RetainedSlot _toolSlot = null!;
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

    private Border _sidePanel = null!;
    private Control _sideBody = null!;
    private Border _contextPanel = null!;
    private Control _contextBody = null!;
    private Control _contextFooter = null!;
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
    private Control _decorationSection = null!;
    private RetainedSlot _kindSlot = null!;
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
    private Control _landStylePointer = null!;
    private Control _landStyleMissing = null!;
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
    private readonly Dictionary<string, Button> _libraryFilterButtons = [];
    private readonly Dictionary<string, Button> _placementModeButtons = [];
    private (Button DontSelect, Button Select) _selectAfterPlacementButtons;
    private ToggleButton _randomVariantToggle = null!;
    private StackPanel _libraryTagTree = null!;
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
    private Expander _setManagementDetails = null!;
    private Control _selectedSetBlock = null!;
    private Control _stampGestureBlock = null!;
    private Control _stampPlacementOptions = null!;
    private Button _stampResetButton = null!;
    private TextBlock _stampResetCaption = null!;
    private Ui.SectionPanel _stampHowSection = null!, _stampNextSection = null!, _stampLookSection = null!,
        _stampRandomSection = null!, _stampRecentSection = null!;
    private Control _stampStripsHost = null!;
    private Control _stampEmptyState = null!;
    private Control _stampCatalogHost = null!;
    private ToggleButton _randomRotationToggle = null!;
    private ToggleButton _stampFavoriteToggle = null!;
    private ToggleButton _tweakColorsToggle = null!;
    private Button _stampPreviewButton = null!;
    private Control _stampColorsBlock = null!;
    private TextBlock _stampPlaceCaption = null!;
    private Control _stampAutoSelectRow = null!;
    private TextBlock _stampVariantCounter = null!;
    private TextBlock _stampVariantName = null!;
    private StackPanel _stampAreaControls = null!;
    private (int First, int Count) _reportedLibraryRange = (-1, -1);
    private int _lastSelectedLibraryRow = -1;
    private ToggleButton _randomFlipToggle = null!;
    private ToggleButton _avoidOverlapToggle = null!;
    private TextBlock _stampToolCurrentText = null!;
    private TextBlock _stampToolAreaText = null!;
    private Image _stampToolThumbnail = null!;
    private Button _stampCurrentAssetButton = null!;
    private ListBox _stampVariantStrip = null!;
    private ListBox _stampRecentStrip = null!;
    private ListBox _stampFavoriteStrip = null!;
    private Control? _seriesSpacingControl;
    private bool _assetCatalogAutoCloseForStamp;
    private Expander? _libraryOrganization;
    private bool _galleryClickKeepsCatalog;
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
    private readonly bool _isHexWorkshop;
    private int _objectListRebuildCount;
    private readonly HashSet<ObjectId> _objectSelectionSet = [];

    // Once per process, before the first editor: controls without their own tooltip take it from the directory (DEC-070).
    static EditorView()
    {
        UiTips.Install();
        TestWindows.Guard();
    }

    public EditorView(
        EditorViewModel editor,
        Func<System.Threading.Tasks.Task> requestBack,
        Action requestSecondWindow,
        Action requestShortcutSettings)
    {
        _editor = editor;
        // The workshop is fixed by the map's category: a hex map opens with the hex tool set and
        // never shows the standard constructor's tools.
        _isHexWorkshop = editor.IsHexWorkshop;
        if (_isHexWorkshop)
        {
            if (_editor.ActiveLayer is not HexLayer && _editor.ActiveHexLayer is { } hexes) _editor.SelectLayer(hexes.Id);
            _editor.ActiveTool = EditorTool.Hex;
            _editor.PanelSection = PanelSection.Layers;
            foreach (var orientation in editor.Session.Document.Layers.OfType<HexLayer>().Select(l => l.Board.Orientation).Distinct())
            {
                _ = Task.Run(() => Map.Rendering.HexTileArt.Warm(orientation));
            }
        }
        _thumbnails.OriginalFor = path => _editor.TryDescribeLibraryThumbnail(path, out var original) ? original : null;
        Name = "EditorView";

        var topBar = BuildTopBar(requestBack, requestSecondWindow, requestShortcutSettings);
        var quickBar = BuildQuickPropertyBar();
        var rail = BuildToolRail();
        var contextPanel = BuildContextPanel();
        var sidePanel = BuildSidePanel();
        var statusBar = BuildStatusBar();
        SyncShortcutSurfaces();

        // Inkarnate's workspace (measured through REA): the canvas runs from the window's left edge
        // to the layer panel, and the tool rail, the tool panel and the shortcut hint float over it
        // as separate cards. The map gets the whole width; a card covers it only where it stands.
        var canvasHost = (Grid)BuildCanvasHost();
        var atlasBar = BuildAtlasNavigation(canvasHost);
        rail.HorizontalAlignment = HorizontalAlignment.Left;
        rail.VerticalAlignment = VerticalAlignment.Top;
        rail.Margin = new Thickness(0, Ui.FloatingGap, 0, Ui.FloatingGap);
        contextPanel.HorizontalAlignment = HorizontalAlignment.Left;
        contextPanel.VerticalAlignment = VerticalAlignment.Top;
        contextPanel.Margin = new Thickness(Ui.RailWidth + 4, Ui.FloatingGap, 0, Ui.FloatingGap);
        // The tool-hint pill at the bottom of the canvas was removed at the owner's request
        // (2026-10-17): the map keeps that space.
        canvasHost.Children.Add(rail);
        canvasHost.Children.Add(contextPanel);
        _canvas.FitInsetLeft = FloatingInsetLeft;

        Content = new DockPanel
        {
            Children =
            {
                topBar.Docked(Dock.Top),

                // Docked, not floating: the strip takes its own band and the canvas fills what is
                // left, so it can never cover the map.
                quickBar.Docked(Dock.Top),
                atlasBar.Docked(Dock.Top),
                statusBar.Docked(Dock.Bottom),
                sidePanel.Docked(Dock.Right),
                // Свет and Стили: their own ribbon under the map, apart from the object sections (DEC-064).
                new DockPanel { Children = { BuildAppearanceRibbon().Docked(Dock.Bottom), canvasHost } },
            },
        };

        // Panel widths follow the window, but never from inside the layout pass that reported
        // the new size: widths changed there were ignored, and after narrow -> wide the rail and
        // the side panel stayed arranged in their narrow slots (rail at x = -7, panel past the edge).
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(ApplyWindowWidths, DispatcherPriority.Background);
        void ApplyWindowWidths()
        {
            var narrow = Bounds.Width < 1100;
            var compact = narrow || Bounds.Height < 760;
            Ui.SetClass(this, "compact-window", compact);
            Ui.SetClass(this, "narrow-window", narrow);
            rail.Width = compact ? 42 : Ui.RailWidth;
            _contextPanel.Margin = new Thickness((compact ? 42 : Ui.RailWidth) + 4, Ui.FloatingGap, 0, Ui.FloatingGap);
            if (!_contextCollapsed) _contextPanel.Width = narrow ? 248 : Ui.ContextPanelWidth;
            // A narrow window gives the map the width: the map panel folds to its strip of
            // section icons once, and unfolds again when the window grows back.
            if (narrow && !_sideCollapsed && !_sideAutoCollapsed && !_sideUserOpenedNarrow)
            {
                ToggleRightPanel();
                _sideAutoCollapsed = true;
            }
            else if (!narrow && _sideAutoCollapsed)
            {
                _sideAutoCollapsed = false;
                _sideUserOpenedNarrow = false;
                if (_sideCollapsed) ToggleRightPanel();
            }
            if (!_sideCollapsed) _sidePanel.Width = Ui.SidePanelWidth;
            (Content as Control)?.InvalidateMeasure();
        }

        _canvas.Attach(_editor);
        _canvas.ViewportChanged += (_, _) => _viewportStatus.Text = _editor.ViewportStatus;
        _canvas.StampPlaced += (_, _) => OnEditorChanged(this, new(nameof(EditorViewModel.CompactRecentSets)));
        _canvas.VariantCycled += (_, _) => OnEditorChanged(this, new(nameof(EditorViewModel.CurrentVariant)));
        _canvas.SecondaryPressed += (_, e) => ShowCanvasContextMenu(e);
        // Large areas are previewed and recomputed in the background, never on the UI thread.
        _editor.AllowsBackgroundAreaWork = true;
        _editor.PropertyChanged += OnEditorChanged;
        _editor.Shortcuts.Changed += OnShortcutsChanged;
        _editor.SceneChanged += (_, _) =>
        {
            lock (_uiSyncGate) _sceneSyncPending = true;
            OnEditorChanged(this, new(nameof(EditorViewModel.Session)));
            // Undo/Redo of a light or style change brings the ribbon back in line (not while a slider drags).
            Dispatcher.UIThread.Post(SyncAppearanceRibbon);
        };
        _editor.SceneInvalidated += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            if (e.Has(EditorChangeReason.Selection))
            {
                SyncStatus();
                SyncInspector();
                FollowSelectionToSection();
                SyncObjectListSelection();
                SyncSectionFooter();
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
            _materialCatalogWindow?.Close();
            foreach (var bitmap in _materialPreviewImages.Values) bitmap.Dispose();
            _materialPreviewImages.Clear();
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

    public bool ContextPanelCollapsed => _contextCollapsed;
    public bool RightPanelCollapsed => _sideCollapsed;
    public bool AssetCatalogVisible => _assetCatalogWindow?.IsVisible == true;
    public Window? ExportWindowForSmoke => TestMode.Enabled ? _exportWindow : null;
    public bool StampCompactCurrentThumbnailVisibleForSmoke =>
        _stampToolThumbnail is { Bounds.Width: > 0, Bounds.Height: > 0, Source: not null };
    public int StampCompactRecentCountForSmoke => _editor.CompactRecentSets.Count;
    public int StampCompactFavoriteCountForSmoke => _editor.CompactFavoriteSets.Count;
    public int StampCompactVariantCountForSmoke => _editor.StampFamilyVariants.Count;

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
        // The objects live in the section that lists the current selection; with nothing
        // selected the panel opens on the placed assets, the most common thing to look for.
        var section = _editor.SectionForSelection()
            ?? (_editor.PanelSection == PanelSection.Layers ? PanelSection.Assets : _editor.PanelSection);
        ShowPanelSection(section);
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
            "Каталог ассетов",
            _assetCatalogContent,
            1240,
            820,
            "AssetCatalogWindow");
        if (focusSearch)
        {
            // The first catalog layout must finish before choosing its initial focus.
            Dispatcher.UIThread.Post(() => _assetSearch.Focus(), DispatcherPriority.Loaded);
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

    /// <summary>How far the floating rail and tool panel reach into the canvas from the left.</summary>
    private double FloatingInsetLeft() => _contextPanel.IsVisible
        ? _contextPanel.Margin.Left + _contextPanel.Bounds.Width + 8
        : Ui.RailWidth + 8;

    public void ToggleContextPanel()
    {
        _contextCollapsed = !_contextCollapsed;
        _contextBody.IsVisible = !_contextCollapsed;
        _contextFooter.IsVisible = !_contextCollapsed;
        _contextTitle.IsVisible = !_contextCollapsed;
        _contextHeaderTitle.IsVisible = !_contextCollapsed;
        _contextHeaderAction.IsVisible = !_contextCollapsed;
        _contextScopeLine.IsVisible = !_contextCollapsed && _contextScopeChip.Content is not null;
        _contextPanel.Width = _contextCollapsed ? 42 : (Classes.Contains("narrow-window") ? 248 : Ui.ContextPanelWidth);
        _contextSwitch.IsVisible = !_contextCollapsed && (_editor.ActiveTool is EditorTool.River or EditorTool.Road
            ? RiverRoadHasSelection() : EditorContextPanel.ToggleApplies(_editor.SelectionCount));
    }

    public void ToggleRightPanel()
    {
        _sideCollapsed = !_sideCollapsed;
        _sideBody.IsVisible = !_sideCollapsed;
        _sideTitle.IsVisible = !_sideCollapsed;
        _sideStrip.IsVisible = _sideCollapsed;
        _sidePanel.Width = _sideCollapsed ? 44 : Ui.SidePanelWidth;
        // Opening it by hand in a narrow window is the user's choice and is not undone by resizing.
        if (!_sideCollapsed && Classes.Contains("narrow-window")) _sideUserOpenedNarrow = true;
    }

    private bool _sideAutoCollapsed;
    private bool _sideUserOpenedNarrow;
    private readonly StackPanel _sideStrip = new() { Name = "SidePanelSectionStrip", Spacing = 4, IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };

    /// <summary>The folded map panel keeps one icon per section: a click opens that section.</summary>
    private void BuildSideStrip()
    {
        foreach (var section in Sections)
        {
            var captured = section;
            var title = ObjectTreeRows.SectionTitle(section);
            var button = new Button { Content = MapIcons.Grid24(SectionLineIcon(section), 18) }
                .Named("SideStrip" + section).Classed("icon");
            AutomationProperties.SetName(button, "Открыть раздел «" + title + "»");
            ToolTip.SetTip(button, title);
            button.Click += (_, _) =>
            {
                if (_sideCollapsed) ToggleRightPanel();
                ShowPanelSection(captured);
            };
            _sideStrip.Children.Add(button);
        }
    }

    public Window? VersionsWindowForSmoke => TestMode.Enabled ? _versionsWindow : null;

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

    /// <summary>
    /// Test mode: a window opens where the tested main window is. A smoke's own window waits at -32000, which no
    /// screen holds, and Windows pulled windows placed there back onto the owner's main screen: they go to the
    /// second display instead (owner, 2026-10-28).
    /// </summary>
    internal static void PlaceTestWindow(Window window, Window? ownerWindow)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        var position = ownerWindow?.Position ?? new PixelPoint(-32000, -32000);
        if (ownerWindow?.Screens is { } screens && screens.ScreenFromPoint(position) is null
            && screens.All.FirstOrDefault(screen => !screen.IsPrimary) is { } second)
            position = new PixelPoint(second.WorkingArea.X, second.WorkingArea.Y);
        window.Position = position;
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
            }).Classed("calm-modes"),
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
            // Where the tested main window is: the live pass puts it on the second display. A window
            // sent to -32000 was pulled back by Windows onto the owner's main screen.
            PlaceTestWindow(window, ownerWindow);
        }
        else
        {
            // Open on the owner's screen; a window born on a screen of another scale used to keep
            // the old scale's layout there, with black bands right and below.
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        window.ScalingChanged += (_, _) =>
        {
            var size = window.ClientSize;
            window.Width = size.Width;
            window.Height = size.Height;
            window.InvalidateMeasure();
            window.InvalidateArrange();
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
            Children = { SoundToggle.Create("EditorSoundButton", withCaption: false), historyActions, viewActions, file, export, _saveButton },
            Margin = new Thickness(24, 0, 0, 0),
        }.Named("EditorHeaderActions");
        // Save and history stay in place; secondary commands retain their labelled menu entry.
        // Panel widths follow the window, but never from inside the layout pass that reported
        // the new size: widths changed there were ignored, and after narrow -> wide the rail and
        // the side panel stayed arranged in their narrow slots (rail at x = -7, panel past the edge).
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(ApplyWindowWidths, DispatcherPriority.Background);
        void ApplyWindowWidths()
        {
            var narrow = Bounds.Width < 1100;
            export.IsVisible = !narrow;
            viewActions.IsVisible = !narrow;
            file.Content = new TextBlock { Text = narrow ? "Файл и вид ▾" : "Файл ▾" };
        };

        var bar = new DockPanel
        {
            Margin = new Thickness(8, 4),
            Children =
            {
                primaryActions.Docked(Dock.Right),
                back.Docked(Dock.Left),
                BuildStampStrip().Docked(Dock.Left),
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

        // Inkarnate's top bar keeps undo / redo as bare 32 px icons, not boxed in a group.
        stack.Spacing = 2;
        return new Border { Child = stack };
    }

    // ====================================================================== rail

    private Control BuildToolRail()
    {
        Button Tool(EditorRailEntry entry)
        {
            var button = Ui.RailToolButton(
                entry.AutomationId,
                EditorLabels.Tool(entry.Tool),
                EditorLabels.Tool(entry.Tool),
                MapIcons.SolidTool(entry.Tool),
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
            var button = Ui.RailToolButton(
                entry.AutomationId,
                "Ассеты — каталог гор, деревьев и других объектов",
                "Ассеты",
                MapIcons.SolidAssets,
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
        // Inkarnate rail: buttons touch each other (46×48), groups are split by a hairline.
        var stack = new StackPanel { Spacing = 0, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var group in EditorSurfaceLayout.RailGroupsFor(_isHexWorkshop))
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
        Margin = new Thickness(8, 2),
        Background = Application.Current?.FindResource("MapChromeBorderBrush") as IBrush,
    };

    // ================================================================ hint + status

    private readonly Border _statusToast = new Border { Name = "StatusToast", IsVisible = false }.Classed("canvas-toast");
    private DispatcherTimer? _statusToastTimer;
    private string _statusToastText = "";

    private Control BuildCanvasHost()
    {
        _statusToast.Child = _statusDetail;
        _statusToast.PointerPressed += (_, _) => _statusToast.IsVisible = false;
        ToolTip.SetTip(_statusToast, "Нажмите, чтобы скрыть");
        var host = Ui.WorkspaceBackdrop(_canvas).Classed("canvas-surround");
        host.Children.Add(_statusToast);
        return host;
    }

    /// <summary>
    /// The editor's one-line messages ("Ассет выбран…", refusals, errors) appear for a few seconds
    /// over the canvas and leave, instead of a third permanent bottom line that took map height.
    /// </summary>
    private void ShowStatusToast(string text)
    {
        if (text == _statusToastText) return;
        _statusToastText = text;
        _statusDetail.Text = text;
        if (text.Length == 0)
        {
            _statusToast.IsVisible = false;
            return;
        }
        ToolTip.SetTip(_statusDetail, text);
        var error = text.Contains("не удалось", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ошибк", StringComparison.OrdinalIgnoreCase)
            || text.Contains("недоступ", StringComparison.OrdinalIgnoreCase);
        Ui.SetClass(_statusToast, "error", error);
        _statusToast.IsVisible = true;
        _statusToastTimer?.Stop();
        _statusToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(error ? 9 : 5) };
        _statusToastTimer.Tick += (_, _) => { _statusToastTimer?.Stop(); _statusToast.IsVisible = false; };
        _statusToastTimer.Start();
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
        _statusDetail.TextTrimming = TextTrimming.CharacterEllipsis;
        _statusDetail.TextWrapping = TextWrapping.Wrap;
        _statusDetail.MaxLines = 3;
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
        var bar = new Grid { Margin = new Thickness(8, 2) };
        bar.Children.Add(readouts);
        return new Border { Child = bar }.Classed("status-bar");
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
                    ? "Заменить маску обрезки"
                    : "Назначить маску обрезки",
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
            Title = "Сохранение композиции в файл",
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
            Title = "Вставка композиции из файла",
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
