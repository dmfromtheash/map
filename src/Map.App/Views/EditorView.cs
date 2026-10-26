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
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = ownerWindow?.Position ?? new PixelPoint(-32000, -32000);
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

        // The size in map units, for the one object whose size has one meaning. It writes
        // through the same geometry commit the panel's fields use: one command, one history entry.
        var width = BuildQuickNumber(
            "QuickWidth", "Ширина", "px", 1, 1_000_000,
            () => _editor.SelectionCount == 1 ? _editor.Session.SelectedObject?.Width : null,
            value => CommitQuickSize(value, isWidth: true));
        var height = BuildQuickNumber(
            "QuickHeight", "Высота", "px", 1, 1_000_000,
            () => _editor.SelectionCount == 1 ? _editor.Session.SelectedObject?.Height : null,
            value => CommitQuickSize(value, isWidth: false));
        var size = QuickGroup(width, height);

        // Order inside the level: the four moves a selected object needs most often, as
        // arrows next to the level they act within, with the current shortcut in the tip.
        Button Order(string name, string caption, string icon, int direction, bool extreme, string command)
        {
            var button = Ui.IconButton(name, caption, icon, () =>
            {
                _editor.ReorderSelection(direction, extreme);
                RefreshAfterSelectionCommand();
            });
            DynamicTip(button, () => caption + " (" + _editor.Shortcuts.Get(command) + ")");
            return button;
        }
        var ordering = QuickField("Порядок", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Order("QuickRaiseTopButton", "Наверх: сделать выделение самым верхним в его слое",
                    MapIcons.ArrowToTop, 1, true, EditorShortcutSettings.Commands.ObjectTop),
                Order("QuickRaiseButton", "Выше: поднять выделение над соседями того же уровня",
                    MapIcons.ArrowUp, 1, false, EditorShortcutSettings.Commands.ObjectUp),
                Order("QuickLowerButton", "Ниже: опустить выделение под соседей того же уровня",
                    MapIcons.ArrowDown, -1, false, EditorShortcutSettings.Commands.ObjectDown),
                Order("QuickLowerBottomButton", "Вниз: сделать выделение самым нижним в его слое",
                    MapIcons.ArrowToBottom, -1, true, EditorShortcutSettings.Commands.ObjectBottom),
            },
        });
        var stacking = QuickGroup(QuickField("Слой", layer), levelBlock, ordering);
        var appearance = QuickGroup(scale, opacity, rotation);

        // The same strip for a piece of land: the Land tool works on one land layer at a time,
        // and what the owner reaches for there is where that piece sits among the others and
        // how much it lets through. Land is always below objects, so its order is among land.
        Button LandOrder(string name, string caption, string icon, bool up, string command)
        {
            var button = Ui.IconButton(name, caption, icon, () =>
            {
                _editor.ReorderActiveLayer(up);
                RefreshAfterSelectionCommand();
            });
            DynamicTip(button, () => caption + " (" + _editor.Shortcuts.Get(command) + ")");
            return button;
        }
        var landOrder = QuickField("Порядок", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                LandOrder("QuickLandUpButton", "Выше: поднять этот слой земли над соседним",
                    MapIcons.ArrowUp, true, EditorShortcutSettings.Commands.LayerUp),
                LandOrder("QuickLandDownButton", "Ниже: опустить этот слой земли под соседний",
                    MapIcons.ArrowDown, false, EditorShortcutSettings.Commands.LayerDown),
            },
        });
        var landOpacity = BuildQuickNumber(
            "QuickLandOpacity", "Непрозрачность", "%", 0, 100,
            () => _editor.ActiveLayer is LandLayer active ? active.Opacity * 100 : null,
            value => _editor.SetActiveLayerOpacity(value),
            () => _editor.ActiveLayer is LandLayer);
        var landVisible = Ui.TextButton("QuickLandVisibilityButton", "Показан", () =>
        {
            _editor.ToggleActiveLayerVisibility();
            RefreshAfterSelectionCommand();
        }, "compact");
        var landLevel = BuildQuickLevelBlock("QuickLandLevel", "Уровень земли",
            () => _editor.LandTargetLevel,
            value => _editor.SetLandLevel(value),
            delta => _editor.ShiftLandLevel(delta),
            () => _editor.LandTarget is not null);
        var land = QuickGroup(landLevel, landOrder, landOpacity, QuickField("Видимость", landVisible));

        // Water and roads have the same level and order as everything else on the map.
        var waterLevel = BuildQuickLevelBlock("QuickWaterLevel", "Уровень воды или дороги",
            () => _editor.SurfaceSelectionLevelValue,
            value => _editor.SetSelectedSurfaceLevel(value),
            delta => _editor.ShiftSelectedSurfaceLevel(delta),
            () => _editor.SurfaceSelectionCount > 0);
        Button WaterOrder(string name, string caption, string icon, int direction, bool extreme, string command)
        {
            var button = Ui.IconButton(name, caption, icon, () =>
            {
                _editor.ReorderSelection(direction, extreme);
                RefreshAfterSelectionCommand();
            });
            DynamicTip(button, () => caption + " (" + _editor.Shortcuts.Get(command) + ")");
            return button;
        }
        var waterOrder = QuickField("Порядок", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                WaterOrder("QuickWaterTopButton", "Наверх: сделать воду самой верхней среди воды слоя",
                    MapIcons.ArrowToTop, 1, true, EditorShortcutSettings.Commands.ObjectTop),
                WaterOrder("QuickWaterUpButton", "Выше: поднять воду над соседней водой",
                    MapIcons.ArrowUp, 1, false, EditorShortcutSettings.Commands.ObjectUp),
                WaterOrder("QuickWaterDownButton", "Ниже: опустить воду под соседнюю воду",
                    MapIcons.ArrowDown, -1, false, EditorShortcutSettings.Commands.ObjectDown),
                WaterOrder("QuickWaterBottomButton", "Вниз: сделать воду самой нижней среди воды слоя",
                    MapIcons.ArrowToBottom, -1, true, EditorShortcutSettings.Commands.ObjectBottom),
            },
        });
        var waterGroup = QuickGroup(waterLevel, waterOrder);

        var strip = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 12, LineSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                QuickGroup(thumbnail, identity),
                stacking,
                waterGroup,
                size,
                appearance,
                land,
            },
        };
        var host = new Border { Padding = new Thickness(10, 6), Child = strip }
            .Classed("quick-bar").Named("QuickPropertyBar");

        _quickSyncs.Add(() =>
        {
            var count = _editor.SelectionCount;
            var water = _editor.SurfaceSelectionCount;
            var selected = _editor.Session.SelectedObject;
            // A selected piece of land, or the active land layer under the Land tool.
            var landLayer = _editor.SelectedLand
                ?? (count == 0 && water == 0 && _editor.ActiveTool == EditorTool.Land ? _editor.ActiveLayer as LandLayer : null);
            // Water is a selection too: the strip names it and offers its layer, and keeps the
            // object-only fields out of the way. Under the Land tool the strip speaks for the
            // active piece of land.
            host.IsVisible = count > 0 || water > 0 || landLayer is not null;

            thumbnail.Source = count == 1 && selected is not null
                ? _thumbnails.Image(_editor.ObjectThumbnailPath(selected))
                : null;
            thumbnail.IsVisible = thumbnail.Source is not null;
            identity.Text = count switch
            {
                0 when water > 0 => "Выделено: " + _editor.SelectionStatus.TrimEnd('.'),
                0 when landLayer is not null => "Земля: " + landLayer.Name,
                0 => "",
                1 => "Выделено: " + (selected?.Name ?? "Объект"),
                _ => "Выбрано: " + EditorLabels.Objects(count),
            };
            ToolTip.SetTip(identity, identity.Text);
            AutomationProperties.SetName(identity, identity.Text);

            stacking.IsVisible = landLayer is null;
            levelBlock.IsVisible = count > 0;
            ordering.IsVisible = count > 0;
            waterGroup.IsVisible = count == 0 && water > 0;
            appearance.IsVisible = count > 0;
            size.IsVisible = count == 1;
            land.IsVisible = landLayer is not null;
            if (landLayer is not null)
            {
                landVisible.Content = landLayer.Visible ? "Показан" : "Скрыт";
                AutomationProperties.SetName(landVisible, "Видимость слоя земли: " + landVisible.Content);
            }

            // A percentage of a bitmap's own reference size means nothing for a shape or a
            // caption, so the field is absent rather than lying with a blank.
            scale.IsVisible = _editor.SelectionIsAllStamps;
        });

        return host;
    }

    /// <summary>
    /// One side of the size from the strip. The other fields are refreshed from the object first,
    /// so only the typed side changes; a locked stamp aspect follows it through the same commit.
    /// </summary>
    private void CommitQuickSize(double value, bool isWidth)
    {
        _editor.RefreshInspector();
        var text = value.ToString("0", CultureInfo.InvariantCulture);
        if (isWidth) _editor.WidthText = text; else _editor.HeightText = text;
        _editor.CommitInspector();
        SyncInspector();
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
    /// A level entry for water or land: the same -10..+10 band, the same −/+ steps, the same
    /// reset, writing through the given commands. Objects keep their own block; this one serves
    /// everything else on the map so the strip reads the same whatever is selected.
    /// </summary>
    private Control BuildQuickLevelBlock(
        string name, string what, Func<int?> read, Action<int> set, Action<int> shift, Func<bool> enabled)
    {
        var box = new TextBox
        {
            Width = 62,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
        }.Named(name + "Box");
        AutomationProperties.SetName(box, what);
        var syncing = false;

        void Commit()
        {
            if (syncing) return;
            var text = (box.Text ?? "").Trim().Replace('−', '-').Replace('+', ' ').Trim();
            if (text.Length == 0 || text == "разные") { SyncQuickBar(); return; }
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                _editor.ReportInspectorError(what + " — целое число от " + TerrainOrder.MinimumLevel + " до +" + TerrainOrder.MaximumLevel + ".");
                SyncQuickBar();
                return;
            }
            set(TerrainOrder.Clamp(value));
            RefreshAfterSelectionCommand();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { SyncQuickBar(); e.Handled = true; }
        };
        box.LostFocus += (_, _) => Commit();

        Button Step(string suffix, string caption, int direction, string tip)
        {
            var button = Ui.TextButton(name + suffix, caption, () =>
            {
                shift(direction);
                RefreshAfterSelectionCommand();
            }, "compact");
            ToolTip.SetTip(button, tip);
            return button;
        }
        var reset = ResetGlyph(name + "ResetButton", "Сбросить уровень к нулю", () =>
        {
            set(0);
            RefreshAfterSelectionCommand();
        });

        _quickSyncs.Add(() =>
        {
            syncing = true;
            try
            {
                var on = enabled();
                var value = read();
                box.Text = !on ? "—" : value is { } level ? EditorViewModel.FormatSublayer(level) : "разные";
                Ui.SetClass(box, "mixed", on && value is null);
                box.IsEnabled = on;
                reset.IsEnabled = on;
                AutomationProperties.SetName(box, what + ": " + box.Text);
                ToolTip.SetTip(box, what + ", целое число от " + TerrainOrder.MinimumLevel + " до +" + TerrainOrder.MaximumLevel
                    + ". Земля и вода более высокого уровня накрывают более низкие. Введите значение и нажмите Enter.");
            }
            finally
            {
                syncing = false;
            }
        });

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Уровень", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }.Classed("muted"),
                Step("DownButton", "−", -1, "Опустить на один уровень."),
                box,
                Step("UpButton", "+", 1, "Поднять на один уровень."),
                reset,
            },
        };
    }

    /// <summary>
    /// One compact numeric field of the strip. Empty means "the selected objects disagree", which
    /// is the honest answer; typing a number gives all of them that value as one undoable command.
    /// </summary>
    private Control BuildQuickNumber(
        string name, string caption, string unit, double minimum, double maximum,
        Func<double?> read, Action<double> apply, Func<bool>? enabled = null)
    {
        enabled ??= () => _editor.SelectionCount > 0;
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
                box.IsEnabled = enabled();
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
        looks.Children.Add(BlendChoice(prefix + "BlendChoice", () => Style()?.Blend, _editor.SetStampBlend));
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

    // ========================================== appearance inspector building blocks

    /// <summary>
    /// A slider that previews while dragging and lands the whole gesture as ONE undo entry when
    /// the pointer or the keyboard lets go. The registered sync re-reads the actual value, so the
    /// control can never display a number the document does not hold.
    /// </summary>
    private Control AppearanceSlider(
        string name, string label, double minimum, double maximum, string unit,
        Func<double?> read, Action<double, bool> apply, double step = 1, Control? trailing = null,
        double? defaultValue = null, string? description = null) =>
        ValueRow(name, name + "Box", name + "Value", label, minimum, maximum, unit, read, apply,
            step, defaultValue, description, trailing);

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
        string format = "0", double captionWidth = 92, (double Min, double Max)? slider = null,
        double? defaultValue = null, string? description = null, double step = 1)
    {
        var box = NumberBox(name);
        var committedText = "";
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
            committedText = text;
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
                // Enter applies and hands the keyboard back to the map, so the next Ctrl+Z,
                // Enter or Esc reaches the editor instead of this field.
                Canvas.Focus();
            }
        };
        box.GotFocus += (_, _) => committedText = box.Text ?? "";
        // Undo/redo inside a field that holds no unapplied typing is the map's undo; before,
        // the text box swallowed Ctrl+Z after every «Плотность», «Шаг» or «Seed» change.
        box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if ((box.Text ?? "") != committedText || !ShortcutKeyAdapter.TryCreate(e, out var input)) return;
            var command = _editor.Shortcuts.Resolve(input, EditorKeyRouting.Contexts(this));
            if (command == EditorShortcutSettings.Commands.Undo) _editor.Undo();
            else if (command is EditorShortcutSettings.Commands.Redo or EditorShortcutSettings.Commands.RedoAlternate) _editor.Redo();
            else return;
            e.Handled = true;
            // The field shows the value the map has after the undo, and owes nothing on blur.
            box.Text = committedText = read()?.ToString(format, CultureInfo.InvariantCulture) ?? "";
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Leaving a field that was only visited applies nothing: re-applying its text used to
        // write a stale value back over an undo.
        box.LostFocus += (_, _) => { if ((box.Text ?? "") != committedText) Commit(); };

        _appearanceSyncs.Add(() =>
        {
            if (!box.IsFocused)
            {
                box.Text = read()?.ToString(format, CultureInfo.InvariantCulture) ?? "";
            }
        });

        if (slider is not { } range)
        {
            if (description is not null) ToolTip.SetTip(box, description);
            return Ui.PropertyRow(label, box, unit, captionWidth);
        }

        // With a range the value gets the full row: slider, box, unit, default and explanation.
        // The slider commits on release, on a wheel notch or on a key - one undo entry each.
        return ValueRow(name + "Slider", name, name + "Value", label, range.Min, range.Max, unit ?? "",
            read, (value, commit) => { if (commit) apply(value); }, step, defaultValue, description, box: box);
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

            SyncTextureSwatches();
        }
        finally
        {
            _syncingAppearance = false;
        }
    }

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
            _ = LoadAsync(path, original);
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
        if (e.PropertyName == nameof(EditorViewModel.HexPresentation)) Canvas.InvalidateVisual();
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
        // Diagnostics only (native profiles): how long each panel sync took on the UI thread.
        var timings = EditorViewModel.CollectEditTimings ? _editor.LastEditTimings : null;
        void Timed(string step, Action action)
        {
            if (timings is null) { action(); return; }
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            action();
            timings.Add(("ui:" + step, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
        // An edit that leaves the tool, the selection and the creation modes as they were (a placed
        // stamp, a released stroke, undo) only changes what the panels list. Those panels cost
        // 15-40 ms together and ran right before the next frame, so every click dropped frames;
        // they now follow a moment later, one at a time, between input and render work.
        var structure = UiStructureKey();
        var deferPanels = scene && structure == _lastUiStructure;
        _lastUiStructure = structure;
        Timed("status", SyncStatus);
        Timed("stamp-strip", SyncStampStrip);
        if (deferPanels)
        {
            DeferPanels(DeferredPanel.Path | DeferredPanel.Area | DeferredPanel.River | DeferredPanel.Road
                | DeferredPanel.Side | DeferredPanel.Context);
        }
        else
        {
            Timed("path-panel", SyncEditablePathPanel);
            Timed("area-panel", SyncEditableAreaPanel);
            Timed("river-panel", SyncRiverPanel);
            Timed("road-panel", SyncRoadPanel);
            _deferredPanels &= ~(DeferredPanel.Path | DeferredPanel.Area | DeferredPanel.River | DeferredPanel.Road);
            if (scene)
            {
                Timed("side-panels", SyncSidePanels);
                _deferredPanels &= ~DeferredPanel.Side;
            }
        }
        // Water, roads and land are selections too: the section that lists them opens, its
        // property block refreshes, and the rows show the selected state.
        if (changed.Overlaps([nameof(EditorViewModel.WaterSelectionIds), nameof(EditorViewModel.RoadSelectionIds),
            nameof(EditorViewModel.SelectedLand), nameof(EditorViewModel.SelectionCount)]))
        {
            Timed("follow-selection", FollowSelectionToSection);
            Timed("object-list-selection", SyncObjectListSelection);
            Timed("section-footer", SyncSectionFooter);
        }
        if (changed.Overlaps([nameof(EditorViewModel.XText), nameof(EditorViewModel.YText),
            nameof(EditorViewModel.WidthText), nameof(EditorViewModel.HeightText),
            nameof(EditorViewModel.RotationText), nameof(EditorViewModel.HasEditableSelection)])) Timed("inspector", SyncInspector);
        if (!deferPanels && (scene || changed.Overlaps([nameof(EditorViewModel.ActiveTool), nameof(EditorViewModel.SelectionKind),
            nameof(EditorViewModel.SelectionCount), nameof(EditorViewModel.SelectedGroupId),
            nameof(EditorViewModel.ActiveLayer), nameof(EditorViewModel.HasPendingPlacement),
            nameof(EditorViewModel.PlacementAutoSelect), nameof(EditorViewModel.StampPlacementMode)])))
        {
            Timed("context-area", SyncContextArea);
            Timed("gesture-readout", SyncStampGestureReadout);
            Timed("quick-bar", SyncQuickBar);
            _deferredPanels &= ~DeferredPanel.Context;
        }
        if (changed.Overlaps([nameof(EditorViewModel.StampToolSettings), nameof(EditorViewModel.CurrentVariant),
            nameof(EditorViewModel.CompactRecentSets), nameof(EditorViewModel.CompactFavoriteSets)]))
        {
            Timed("appearance-sections", SyncAppearanceSections);
            Timed("stamp-tool-panel", SyncStampToolPanel);
        }
        if (changed.Any(p => p.StartsWith("Canvas", StringComparison.Ordinal) || p.StartsWith("Region", StringComparison.Ordinal)))
            Timed("canvas-workflow", SyncCanvasWorkflow);
        if (changed.Any(p => p.StartsWith("Export", StringComparison.Ordinal)) || changed.Overlaps([
            nameof(EditorViewModel.CurrentExportPlan), nameof(EditorViewModel.CanStartExport),
            nameof(EditorViewModel.CanCancelExport), nameof(EditorViewModel.CanOpenExportFolder)])) Timed("export", SyncExport);
    }

    [Flags]
    private enum DeferredPanel { None = 0, Path = 1, Area = 2, River = 4, Road = 8, Side = 16, Context = 32 }

    private DeferredPanel _deferredPanels;
    private bool _deferredPanelQueued;
    private string? _lastUiStructure;

    /// <summary>What decides which panels and inspectors are mounted: tool, selection, active layer, creation modes.</summary>
    private string UiStructureKey() => string.Join('|',
        _editor.ActiveTool, _editor.SelectionKind, _editor.SelectionCount, _editor.Session.SelectedObjectId,
        _editor.SelectedGroupId, _editor.OpenGroupId, _editor.ActiveLayerId, _editor.HasPendingPlacement,
        _editor.StampPlacementMode, _editor.LandPaintMode,
        _editor.WaterSelectionCount, _editor.SelectedRiver?.Id, _editor.IsCreatingRiver,
        _editor.RoadSelectionCount, _editor.SelectedRoad?.Id, _editor.IsCreatingRoad,
        _editor.SelectedEditablePath?.Id, _editor.IsCreatingEditablePath,
        _editor.SelectedEditableArea?.Id, _editor.IsCreatingEditableArea);

    private DispatcherTimer? _deferredPanelTimer;

    /// <summary>
    /// Queues the listed panels for a refresh once the edits pause for a moment. A slider drag or a
    /// burst of placements edits the document 60 times a second; refreshing the context and side
    /// panels after every one of those edits (15-20 ms of UI thread each on the owner's test map)
    /// held a cliff slider at about 40 FPS. The panels show the result of the last edit as soon
    /// as the stream of edits stops.
    /// </summary>
    private void DeferPanels(DeferredPanel panels)
    {
        _deferredPanels |= panels;
        if (_deferredPanelQueued) return;
        if (_deferredPanelTimer is null)
        {
            // 250 ms: placement clicks come about every 130 ms; with 50 000 objects the side and
            // surface panels cost 20-25 ms per refresh, which landed between every two clicks.
            _deferredPanelTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _deferredPanelTimer.Tick += (_, _) =>
            {
                _deferredPanelTimer.Stop();
                if (_deferredPanelQueued) return;
                _deferredPanelQueued = true;
                Dispatcher.UIThread.Post(RunDeferredPanel, DispatcherPriority.Background);
            };
        }
        _deferredPanelTimer.Stop();
        _deferredPanelTimer.Start();
    }

    /// <summary>
    /// Refreshes one deferred panel and queues the next, so no single dispatcher job holds the
    /// UI thread for more than a few milliseconds; input and frames go first (Background priority).
    /// </summary>
    private void RunDeferredPanel()
    {
        _deferredPanelQueued = false;
        var next = DeferredPanel.None;
        foreach (var panel in (ReadOnlySpan<DeferredPanel>)[DeferredPanel.Context, DeferredPanel.Side, DeferredPanel.Path,
                     DeferredPanel.Area, DeferredPanel.River, DeferredPanel.Road])
        {
            if ((_deferredPanels & panel) != 0) { next = panel; break; }
        }
        if (next == DeferredPanel.None) return;
        _deferredPanels &= ~next;

        var timings = EditorViewModel.CollectEditTimings ? _editor.LastEditTimings : null;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        switch (next)
        {
            case DeferredPanel.Context:
                SyncContextArea();
                SyncStampGestureReadout();
                SyncQuickBar();
                break;
            case DeferredPanel.Side: SyncSidePanels(); break;
            case DeferredPanel.Path: SyncEditablePathPanel(); break;
            case DeferredPanel.Area: SyncEditableAreaPanel(); break;
            case DeferredPanel.River: SyncRiverPanel(); break;
            case DeferredPanel.Road: SyncRoadPanel(); break;
        }
        timings?.Add(("ui:deferred/" + next.ToString().ToLowerInvariant(), System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));

        if (_deferredPanels != DeferredPanel.None)
        {
            _deferredPanelQueued = true;
            Dispatcher.UIThread.Post(RunDeferredPanel, DispatcherPriority.Background);
        }
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
        if (_vttSummaryText is not null) _vttSummaryText.Text = _editor.ExportVttSummary;
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
        _autosaveState.IsVisible = _editor.AutosaveSummary != "Восстановление не требуется";
        Ui.SetClass(_autosaveState, "danger", _editor.AutosaveState == AutosaveState.Failed);
        Ui.SetClass(_autosaveState, "success", _editor.AutosaveState == AutosaveState.Saved);

        ShowStatusToast(_editor.StatusDetail);

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
        var timings = EditorViewModel.CollectEditTimings ? _editor.LastEditTimings : null;
        void Timed(string step, Action action)
        {
            if (timings is null) { action(); return; }
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            action();
            timings.Add(("ui:context/" + step, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
        foreach (var pair in _toolButtons)
        {
            Ui.SetToolActive(pair.Value, pair.Key == _editor.ActiveTool);
        }

        Timed("land-buttons", SyncLandButtons);
        Timed("mask-buttons", SyncMaskModeButtons);
        Timed("layer-blocks", SyncLayerBlocks);
        Timed("appearance", SyncAppearanceSections);
        // The strip is refreshed on every context change, including "nothing is selected" - the
        // panel's own refresh only runs while an inspector is mounted.
        Timed("quick-bar", SyncQuickBar);
        // A new selection points the panel at the object; a new tool points it at the tool. Both
        // remain reachable afterwards, which is the part the old ActiveTool == Select gate broke.
        var selectionSignature = _editor.SelectionCount > 0
            ? $"{_editor.SelectionCount}:{_editor.Session.SelectedObjectId}"
            : _editor.WaterSelectionCount > 0 ? "water:" + _editor.SelectedRiver?.Id
            : _editor.RoadSelectionCount > 0 ? "road:" + _editor.SelectedRoad?.Id
            : null;
        if (_lastContextTool != _editor.ActiveTool)
        {
            _lastContextTool = _editor.ActiveTool;
            // Choosing the river or road tool with a river or road already selected (from the
            // list, or "Изменить в панели слева") opens that selection, not the creation tab.
            _contextPrefersToolPanel = !RiverRoadHasSelection();
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
        // A river or a road is not a scene object: its panel is the only place its properties live.
        if (_editor.ActiveTool is EditorTool.River or EditorTool.Road)
            scope = EditorContextPanelKind.ToolPanel;

        var surfaceTool = _editor.ActiveTool is EditorTool.River or EditorTool.Road;
        // The Select tool has no settings of its own, so its "Инструмент" tab would be empty.
        _contextSwitch.IsVisible = !_contextCollapsed && _editor.ActiveTool != EditorTool.Select
            && (surfaceTool ? RiverRoadHasSelection() : EditorContextPanel.ToggleApplies(_editor.SelectionCount));
        var objectTab = surfaceTool ? RiverRoadObjectScope() : scope is not EditorContextPanelKind.ToolPanel;
        Ui.SetSelected(_contextSwitchButtons[0], objectTab);
        Ui.SetSelected(_contextSwitchButtons[1], !objectTab);
        SyncSurfaceScope();
        ToolTip.SetTip(_contextSwitchButtons[1], "Настройки: " + EditorLabels.Tool(_editor.ActiveTool));

        if (scope == EditorContextPanelKind.MultiSelectionInspector)
        {
            _contextTitle.Text = "Выбрано: " + EditorLabels.Objects(_editor.SelectionCount);
            _appearanceTransferHost.IsVisible = true;
            SetContextHeader(MapIcons.LineSelect, "", Ui.Scope.Object,
                EditorLabels.Objects(_editor.SelectionCount) + SelectionLayerSuffix(), null);
            SyncMultiSelectionPanel();
            _contextHost.Content = _multiSelectionRoot;
            return;
        }

        if (scope == EditorContextPanelKind.ObjectInspector && _editor.SelectionKind is { } kind)
        {
            _contextTitle.Text = _editor.Session.SelectedObject?.Name ?? EditorLabels.ObjectKind(kind);
            _appearanceTransferHost.IsVisible = true;
            SetContextHeader(ObjectKindIcon(kind), "", Ui.Scope.Object,
                EditorLabels.ObjectKind(kind).ToLowerInvariant() + SelectionLayerSuffix(), null);

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

        _contextTitle.Text = _editor.ActiveTool != EditorTool.Stamp ? EditorLabels.Tool(_editor.ActiveTool)
            : _editor.SelectedEditableArea is not null ? "Выбранная область"
            : _editor.SelectedEditablePath is not null ? "Выбранный путь" : "Ассеты";
        SyncToolHeader();
        // Copying a look needs something selected to copy from or to: the river and road panels
        // edit a selected surface, every other tool panel has no target, so the footer hides.
        _appearanceTransferHost.IsVisible = RiverRoadObjectScope();
        _kindSlot.Content = null;
        var surfaceSelected = _editor.SelectionCount == 0
            && (_editor.WaterSelectionCount > 0 || _editor.RoadSelectionCount > 0 || _editor.SelectedLand is not null);
        _selectionEmpty.IsVisible = scope == EditorContextPanelKind.SelectIdle && !surfaceSelected;
        _surfaceSelectedState.IsVisible = scope == EditorContextPanelKind.SelectIdle && surfaceSelected;
        if (_surfaceSelectedState.IsVisible) SyncSurfaceSelectedState();
        _toolSlot.Content = _toolPanels[_editor.ActiveTool];
        _contextHost.Content = _selectIdleRoot;
    }

    /// <summary>
    /// The header of the context panel and the scope line under it: which tool or object the
    /// panel is about, its key, and in words whom the settings below act on.
    /// </summary>
    private void SetContextHeader(string icon, string key, Ui.Scope? scope, string target, Control? action)
    {
        _contextIcon.Content = MapIcons.Grid24(icon, 19,
            Application.Current?.FindResource(scope == Ui.Scope.Object ? "MapSuccessBrush" : "MapAccentBrush") as IBrush);
        if (_contextKey.Child is TextBlock keyText) keyText.Text = key;
        _contextKey.IsVisible = key.Length > 0;
        _contextScopeChip.Content = scope is { } kind ? Ui.ScopeChip(kind) : null;
        _contextScopeTarget.Text = target;
        ToolTip.SetTip(_contextScopeTarget, target);
        _contextScopeLine.IsVisible = !_contextCollapsed && scope is not null;
        _contextHeaderAction.Content = action;
    }

    private string SelectionLayerSuffix() =>
        _editor.ObjectLayerTargets.FirstOrDefault(target => target.IsCurrent) is { } layer
            ? " · слой «" + layer.Name + "»" : "";

    private string ActiveLayerTarget(string prefix) =>
        _editor.ActiveLayer is { } layer ? prefix + " «" + layer.Name + "»" : "слой не выбран";

    private static string ObjectKindIcon(SceneObjectKind kind) => kind switch
    {
        SceneObjectKind.Stamp or SceneObjectKind.Placeholder => MapIcons.LineAssets,
        SceneObjectKind.Line => MapIcons.LineStroke,
        SceneObjectKind.Text => MapIcons.LineText,
        SceneObjectKind.Decoration => MapIcons.HexRuler,
        _ => MapIcons.LineShape,
    };

    /// <summary>Header and scope for a tool panel, or for the area/path/river/road it edits.</summary>
    private void SyncToolHeader()
    {
        var tool = _editor.ActiveTool;
        var icon = MapIcons.LineTool(tool);
        var key = ToolShortcut(tool);
        if (tool == EditorTool.Stamp && _editor.SelectedEditableArea is not null)
        {
            // The asset panel keeps its reset in every state it had it before, area included.
            SetContextHeader(MapIcons.ModeArea, "", Ui.Scope.Object, "область размещения", _stampResetButton);
            SyncStampResetButton();
            return;
        }
        if (tool == EditorTool.Stamp && _editor.SelectedEditablePath is not null)
        {
            SetContextHeader(MapIcons.ModePath, "", Ui.Scope.Object, "путь размещения", _stampResetButton);
            SyncStampResetButton();
            return;
        }
        switch (tool)
        {
            case EditorTool.Stamp:
                SetContextHeader(icon, key, Ui.Scope.New, ActiveLayerTarget("на слой"), _stampResetButton);
                SyncStampResetButton();
                break;
            case EditorTool.River or EditorTool.Road:
                var editing = RiverRoadObjectScope();
                SetContextHeader(icon, key, editing ? Ui.Scope.Object : Ui.Scope.New,
                    editing ? (tool == EditorTool.River ? "выбранная вода" : "выбранная дорога") + SurfaceLayerSuffix()
                        : ActiveLayerTarget(tool == EditorTool.River ? "новые реки на слой" : "новые дороги на слой"), null);
                break;
            case EditorTool.Land or EditorTool.Brush or EditorTool.Mask:
                SetContextHeader(icon, key, Ui.Scope.Layer, ActiveLayerTarget("рисует на"), null);
                break;
            case EditorTool.Line or EditorTool.Shape or EditorTool.Text:
                SetContextHeader(icon, key, Ui.Scope.New, ActiveLayerTarget("на слой"), null);
                break;
            default:
                SetContextHeader(icon, key, null, "", null);
                break;
        }
    }

    private string SurfaceLayerSuffix() =>
        _editor.ObjectLayerTargets.FirstOrDefault(target => target.IsCurrent) is { } layer
            ? " · слой «" + layer.Name + "»" : "";

    /// <summary>
    /// Keeps the multi-selection panel honest: the count, the group identity when there is one,
    /// which group actions currently apply, and mixed values reported as mixed.
    /// </summary>
    /// <summary>The short count a one-line layer row shows at its right edge.</summary>
    private static string LayerMeta(MapLayer layer) => layer switch
    {
        ObjectLayer objects when objects.Rivers.Length > 0 || objects.Roads.Length > 0 =>
            string.Join(" · ", new[]
            {
                objects.Objects.Count > 0 ? objects.Objects.Count.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) : null,
                objects.Rivers.Length > 0 ? objects.Rivers.Length + " вод." : null,
                objects.Roads.Length > 0 ? objects.Roads.Length + " дор." : null,
            }.Where(part => part is not null)),
        ObjectLayer objects => objects.Objects.Count == 0 ? "пусто"
            : objects.Objects.Count.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")),
        LandLayer => "земля",
        BaseColorLayer => "фон",
        GridLayer => "сетка",
        _ => "",
    };

    private Control _surfaceSelectedState = null!;
    private TextBlock _surfaceSelectedText = null!;
    private Button _surfaceSelectedOpen = null!;

    /// <summary>
    /// With the Select tool, a river, road or piece of land can be selected, but its properties
    /// live in its own tool. The panel says so and opens that tool instead of claiming that
    /// nothing is selected while the strip above the map names the selection.
    /// </summary>
    private Control BuildSurfaceSelectedState()
    {
        _surfaceSelectedText = new TextBlock { TextWrapping = TextWrapping.Wrap }.Classed("role-body");
        _surfaceSelectedOpen = Ui.CommandButton("OpenSurfaceToolButton", "Изменить", MapIcons.River, () =>
        {
            _editor.ActiveTool = _editor.WaterSelectionCount > 0 ? EditorTool.River
                : _editor.RoadSelectionCount > 0 ? EditorTool.Road : EditorTool.Land;
            SyncContextArea();
        }, "accent", "compact");
        _surfaceSelectedOpen.HorizontalAlignment = HorizontalAlignment.Stretch;
        return new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
            Children =
            {
                _surfaceSelectedText,
                Ui.Hint("Слой, уровень и порядок — в панели над картой."),
                _surfaceSelectedOpen,
            },
        };
    }

    private void SyncSurfaceSelectedState()
    {
        var (what, tool) = _editor.WaterSelectionCount > 0 ? ("Выбрана вода", "«Река»")
            : _editor.RoadSelectionCount > 0 ? ("Выбрана дорога", "«Дорога»") : ("Выбрана земля", "«Земля»");
        var name = _editor.WaterSelectionCount > 0 ? _editor.SelectedRiver?.Name
            : _editor.RoadSelectionCount > 0 ? _editor.SelectedRoad?.Name : _editor.SelectedLand?.Name;
        _surfaceSelectedText.Text = $"{what} «{name}». Форма и оформление меняются в инструменте {tool}.";
        Ui.SetCommandText(_surfaceSelectedOpen, "Изменить в инструменте " + tool);
    }

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
            ObjectKindSection.Decoration => _decorationSection,
            _ => null,
        };

    private void BuildContextRoots()
    {
        _kindSlot = new RetainedSlot { Name = "SelectionKindSlot" };

        // Order is the message: what the object IS and the properties touched constantly come
        // first, the kind-specific block next, and exact geometry last. The old panel opened with
        // four coordinate boxes, so the level and the owning layer were below the fold.
        var geometry = new Ui.SectionPanel("ObjectGeometrySection", "Положение и размер", _inspectorPanel, false);
        geometry.SetSummary("X, Y, ширина, высота");
        _selectionRoot = new StackPanel
        {
            Spacing = 0,
            Children =
            {
                BuildSelectionCommon(),
                new Border { Padding = new Thickness(0, 8, 0, 0), Child = _kindSlot },
                geometry,
                new Border
                {
                    Padding = new Thickness(0, 10, 0, 4),
                    Child = Ui.Hint("Слой, уровень, порядок, размер, масштаб, непрозрачность и поворот — в панели над картой."),
                },
                _objectDeleteButton,
            },
        };

        // The tool panels live in a slot, never as fixed children: a control can have only one
        // visual parent, and the context area now shows a tool panel in more situations than the
        // old "Select tool only" rule allowed.
        _toolSlot = new RetainedSlot { Name = "ToolPanelSlot" };
        _surfaceSelectedState = BuildSurfaceSelectedState();
        _selectIdleRoot = new StackPanel
        {
            Spacing = 12,
            Children = { _selectionEmpty, _surfaceSelectedState, _toolSlot },
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
            _editor.DeleteSelectedObject, "danger-text");
        delete.Margin = new Thickness(0, 8, 0, 0);
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
                new Ui.SectionPanel("MultiCommonActions", "Состояние", new StackPanel
                {
                    Spacing = 2,
                    Children = { visibilityRow, lockRow },
                }, true),
                Ui.Hint("Слой, уровень, порядок, масштаб, непрозрачность и поворот — в панели над картой."),
                delete,
            },
        };
    }

    private Control _groupActionsSection = null!;
    private Button _objectDeleteButton = null!;

    private TextBlock _multiCountText = null!;

    private void RefreshAfterSelectionCommand()
    {
        SyncStatus();
        SyncSidePanels();
        SyncContextArea();
    }

    /// <summary>
    /// The right-click menu of the canvas: what lies under the pointer becomes the selection,
    /// and the menu offers the moves that decide what covers what - order inside the level,
    /// the level itself, the owning layer - plus deletion. Every item calls the same view model
    /// command the strip and the shortcuts call, so the three can never disagree. Empty ground
    /// under the pointer opens nothing.
    /// </summary>
    private void ShowCanvasContextMenu(PointerPressedEventArgs e)
    {
        var position = e.GetPosition(_canvas);
        var (worldX, worldY) = _editor.Session.Viewport.ScreenToWorld(position.X, position.Y);
        var hit = SceneGeometry.HitTest(_editor.Session.Document, worldX, worldY);
        if (hit is not null)
        {
            if (!_editor.Session.SelectedObjects.Any(item => item.Id == hit.Id))
            {
                _editor.SelectObject(hit.Id);
            }
        }
        else if (!_editor.SelectSurfaceAt(worldX, worldY, _editor.Session.Viewport.Scale, additive: false)
            && !_editor.SelectLandAt(worldX, worldY))
        {
            return;
        }
        RefreshAfterSelectionCommand();

        // Objects, water, roads and land answer the same verbs; the view model routes each to
        // its own command. Land has no order of its own besides its level and its layer.
        var objects = _editor.SelectionCount > 0;
        var water = !objects && _editor.SurfaceSelectionCount > 0;
        var landPiece = !objects && !water && _editor.SelectedLand is not null;
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Выше", EditorShortcutSettings.Commands.ObjectUp, () => _editor.ReorderSelection(1), objects || water));
        menu.Items.Add(Item("Ниже", EditorShortcutSettings.Commands.ObjectDown, () => _editor.ReorderSelection(-1), objects || water));
        menu.Items.Add(Item("Наверх", EditorShortcutSettings.Commands.ObjectTop, () => _editor.ReorderSelection(1, toExtreme: true), objects || water));
        menu.Items.Add(Item("Вниз", EditorShortcutSettings.Commands.ObjectBottom, () => _editor.ReorderSelection(-1, toExtreme: true), objects || water));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Уровень выше", EditorShortcutSettings.Commands.SublayerUp, () => _editor.AdjustSelectedSublayer(1), true));
        menu.Items.Add(Item("Уровень ниже", EditorShortcutSettings.Commands.SublayerDown, () => _editor.AdjustSelectedSublayer(-1), true));
        if (landPiece)
        {
            menu.Items.Add(Item("Слой земли выше", EditorShortcutSettings.Commands.LayerUp, () => _editor.ReorderActiveLayer(true), true));
            menu.Items.Add(Item("Слой земли ниже", EditorShortcutSettings.Commands.LayerDown, () => _editor.ReorderActiveLayer(false), true));
        }
        menu.Items.Add(new Separator());

        var layers = new MenuItem { Name = "CanvasMenuLayer", Header = "Перенести на слой" };
        foreach (var target in _editor.ObjectLayerTargets)
        {
            var captured = target;
            var item = new MenuItem
            {
                Name = "CanvasMenuLayer_" + target.Id,
                Header = target.IsCurrent ? "● " + target.Name : target.Display,
                IsEnabled = target.Available && !target.IsCurrent,
            };
            item.Click += (_, _) =>
            {
                _editor.MoveSelectionToLayer(captured.Id);
                RefreshAfterSelectionCommand();
            };
            layers.Items.Add(item);
        }
        layers.IsEnabled = layers.Items.Count > 0 && !landPiece;
        menu.Items.Add(layers);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Удалить", EditorShortcutSettings.Commands.Delete, () => _editor.DeleteSelection(), !landPiece));
        menu.ShowAt(_canvas, showAtPointer: true);
        e.Handled = true;

        MenuItem Item(string header, string command, Action action, bool enabled)
        {
            var item = new MenuItem { Name = "CanvasMenu_" + command, Header = header, IsEnabled = enabled };
            var gesture = _editor.Shortcuts.Get(command);
            try { item.InputGesture = KeyGesture.Parse(gesture); }
            catch (Exception) { item.Header = header + "   " + gesture; }
            item.Click += (_, _) =>
            {
                action();
                RefreshAfterSelectionCommand();
            };
            return item;
        }
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

    private void SyncLandButtons()
    {
        SyncLandMaterialPanel();
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
        // The selected row of the list already names the layer; the card would repeat it.
        _layerIdentity.IsVisible = false;
        _layerCommonBlock.IsVisible = editable;
        _baseColorLayerBlock.IsVisible = layer is BaseColorLayer;
        _landStylePointer.IsVisible = layer is LandLayer;
        // In the tool panel the same block appears only while a land layer is the active one;
        // otherwise the panel says which layer to pick instead of showing dead controls.
        var landActive = _editor.ActiveLayer is LandLayer;
        _landLayerBlock.IsVisible = landActive;
        _landStyleMissing.IsVisible = !landActive;
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

    /// <summary>
    /// The rows of the layer list by layer, with the signature of what each row shows. A row is
    /// rebuilt only when that changes: a stroke, a placement or an undo changes the tiles or the
    /// objects of a layer, not its row (its object count aside), so the list is not torn down
    /// and rebuilt control by control on every edit.
    /// </summary>
    private readonly Dictionary<LayerId, (string Signature, Border Row)> _layerRows = [];

    private void SyncSidePanels()
    {
        var sidePanelsStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var rows = new List<Border>(_editor.Layers.Count);
        var seen = new HashSet<LayerId>();
        foreach (var layer in _editor.Layers.Reverse())
        {
            seen.Add(layer.Id);
            var signature = LayerRowSignature(layer);
            if (_layerRows.TryGetValue(layer.Id, out var kept) && kept.Signature == signature)
            {
                Ui.SetStructureSelected(kept.Row, layer.Id == _editor.ActiveLayerId);
                rows.Add(kept.Row);
                continue;
            }
            var built = BuildLayerRow(layer);
            _layerRows[layer.Id] = (signature, built);
            rows.Add(built);
        }
        foreach (var gone in _layerRows.Keys.Where(id => !seen.Contains(id)).ToArray()) _layerRows.Remove(gone);
        if (_layerList.Children.Count == rows.Count)
        {
            // Same layers in the same order: only the rows that were rebuilt are swapped, so the
            // others keep their visual state and are not detached and re-styled.
            for (var i = 0; i < rows.Count; i++)
                if (!ReferenceEquals(_layerList.Children[i], rows[i])) _layerList.Children[i] = rows[i];
        }
        else
        {
            _layerList.Children.Clear();
            foreach (var row in rows) _layerList.Children.Add(row);
        }

        Border BuildLayerRow(MapLayer layer)
        {
            var captured = layer;
            var isBase = layer is BaseColorLayer;

            // The base colour layer cannot be hidden, locked or removed, so it gets no toggles
            // rather than disabled ones that would only invite a pointless click.
            var actions = new List<Button>();
            if (!isBase)
            {
                // A layer switch acts on everything the layer holds. Where that is more than
                // objects, the label says so, since the sections offer a switch per river or road.
                var scope = layer is ObjectLayer { Rivers.Length: > 0 } or ObjectLayer { Roads.Length: > 0 }
                    ? " целиком: объекты, вода и дороги" : "";
                var visibility = Ui.RowAction(
                    "LayerRowVisibility_" + layer.Id,
                    EditorLabels.VisibilityCommand(layer.Visible) + scope,
                    layer.Visible ? MapIcons.Eye : MapIcons.EyeOff,
                    () => _editor.ToggleLayerVisibility(captured.Id));
                Ui.SetRowAction(visibility, EditorLabels.VisibilityCommand(layer.Visible) + scope, !layer.Visible);

                var locking = Ui.RowAction(
                    "LayerRowLock_" + layer.Id,
                    EditorLabels.LockCommand(layer.Locked) + scope,
                    layer.Locked ? MapIcons.Lock : MapIcons.Unlock,
                    () => _editor.ToggleLayerLock(captured.Id));
                Ui.SetRowAction(locking, EditorLabels.LockCommand(layer.Locked) + scope, layer.Locked);

                actions.Add(visibility);
                actions.Add(locking);
            }

            // The kind is a useful subtitle only when it adds something the name does not say.
            // An object layer also says what it holds, so the stack can be read without opening
            // each layer: how many objects, how many rivers and lakes.
            var kind = EditorLabels.LayerKind(layer);
            var secondary = EditorLabels.SecondaryKind(layer.Name, kind);
            if (layer is ObjectLayer objectLayer)
            {
                var counts = new List<string>();
                if (objectLayer.Objects.Count > 0) counts.Add(EditorLabels.Objects(objectLayer.Objects.Count));
                if (objectLayer.Rivers.Length > 0) counts.Add(EditorLabels.Waters(objectLayer.Rivers.Length));
                if (objectLayer.Roads.Length > 0) counts.Add(EditorLabels.RoadsCount(objectLayer.Roads.Length));
                var held = counts.Count == 0 ? "пусто" : string.Join(" · ", counts);
                secondary = secondary is null ? held : secondary + " · " + held;
            }

            var tooltip = $"{layer.Name} · {kind} · наложение: {EditorLabels.Blend(layer.Blend)}";
            if (layer.Mask is { } mask)
            {
                tooltip += mask.Enabled ? " · маска включена" : " · маска выключена";
            }

            var row = Ui.StructureRow(
                "Layer_" + layer.Id,
                LayerIcon(layer),
                layer.Name,
                null,
                () =>
                {
                    _editor.SelectLayer(captured.Id);
                    _layerName.Text = captured.Name;
                    SyncSidePanels();
                    SyncContextArea();
                },
                tooltip + (secondary is null ? "" : " · " + secondary),
                [.. actions]);
            Ui.SetStructureMeta(row, LayerMeta(layer));

            Ui.SetStructureSelected(row, layer.Id == _editor.ActiveLayerId);
            return row;
        }

        _layerName.Text = _editor.ActiveLayer?.Name ?? "";
        var timings = EditorViewModel.CollectEditTimings ? _editor.LastEditTimings : null;
        void Timed(string step, Action action)
        {
            if (timings is null) { action(); return; }
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            action();
            timings.Add(("ui:side/" + step, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
        if (timings is not null) timings.Add(("ui:side/layer-rows", System.Diagnostics.Stopwatch.GetElapsedTime(sidePanelsStarted).TotalMilliseconds));
        Timed("layer-blocks", SyncLayerBlocks);
        Timed("object-list", SyncObjectList);
        Timed("asset-list", SyncAssetList);
        Timed("note-list", SyncNoteList);
        Timed("entity-list", SyncEntities);
        Timed("fog", SyncFog);
        Timed("compare", SyncCompareBlock);
        Timed("transitions", SyncTransitions);
        Timed("timeline", SyncTimeline);
        Timed("pointcrawl", SyncPointcrawl);
        Timed("planet", SyncPlanet);
        Timed("book", SyncBook);
        Timed("sector", SyncSector);
        Timed("checkpoint-list", SyncCheckpointList);
        Timed("canvas-workflow", SyncCanvasWorkflow);
        Timed("quick-bar", SyncQuickBar);
    }

    /// <summary>Everything a layer row shows, as one string: name, kind, switches, counts, blend and mask state.</summary>
    private static string LayerRowSignature(MapLayer layer)
    {
        var counts = layer is ObjectLayer objects ? $"{objects.Objects.Count}/{objects.Rivers.Length}/{objects.Roads.Length}" : "";
        var mask = layer.Mask is { } m ? (m.Enabled ? "m+" : "m-") : "m0";
        return $"{layer.GetType().Name}|{layer.Name}|{layer.Visible}|{layer.Locked}|{layer.Blend}|{mask}|{counts}|{LayerMeta(layer)}";
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
        _selectionCountText.Text = (_editor.SelectionCount, _editor.WaterSelectionCount, _editor.RoadSelectionCount) switch
        {
            (0, 0, 0) when _editor.SelectedLand is { } land => $"Выбрана земля «{land.Name}»",
            (0, 0, 0) => "Ничего не выбрано",
            (0, var water, 0) => $"Выбрано водных систем: {water}",
            (0, 0, var roads) => $"Выбрано дорог: {roads}",
            (0, var water, var roads) => $"Выбрано водных систем: {water} и дорог: {roads}",
            (var objects, 0, 0) => $"Выбрано: {EditorLabels.Objects(objects)}",
            var (objects, water, roads) => $"Выбрано: {EditorLabels.Objects(objects)}, водных систем: {water}, дорог: {roads}",
        };
        var anySelected = _editor.SelectionCount>0 || _editor.WaterSelectionCount>0
            || _editor.RoadSelectionCount>0 || _editor.SelectedLand is not null;
        // "Выбрано водных систем: 1" under the Assets list only confused: the count is shown in
        // the section that lists the selection.
        _selectionCountText.IsVisible = anySelected && _editor.SectionForSelection() is { } home && home == _editor.PanelSection;

        SyncSectionFooter();
        var rows = _editor.ObjectTree;
        if (rows.Count == 0)
        {
            var searching = !string.IsNullOrWhiteSpace(_editor.ObjectSearchText)
                || (_editor.PanelSection == PanelSection.Other && _editor.ObjectTypeFilter != ObjectTypeFilter.NotStamps
                    && _editor.Layers.OfType<ObjectLayer>().Any(layer => layer.Objects.Any(o => o.Kind != SceneObjectKind.Stamp)));
            _objectEmptyStateHost.Content = SectionEmptyState(_editor.PanelSection, searching);
            _objectEmptyStateHost.IsVisible = true;
            _objectList.IsVisible = false;
            _objectList.ItemsSource = Array.Empty<ObjectTreeRow>();
            return;
        }

        _objectEmptyStateHost.IsVisible = false;
        _objectList.IsVisible = true;
        _objectList.ItemsSource = rows;
        Dispatcher.UIThread.Post(() => { SyncObjectListSelection(); ScrollObjectListToSelection(); }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Every copy of one asset picture as one row: the picture, its name, how many copies the map
    /// holds. A click opens the copies; a row whose copies include the selection is highlighted.
    /// </summary>
    private Button BuildAssetKindRow(ObjectTreeRow row)
    {
        var key = row.AssetKey!;
        var chevron = new Border { Width = 18, Child = MapIcons.Stroked(MapIcons.ChevronRight, 14) };
        var listRow = Ui.ListRow(
            row.AutomationId,
            MapIcons.ObjectStamp,
            row.Title,
            row.Subtitle,
            [],
            () =>
            {
                _editor.OpenAssetKind(key);
                SyncObjectList();
            },
            $"«{row.Title}» · {row.Subtitle} на карте. Открыть список копий.",
            row.Object is { } first ? _thumbnails.Image(_editor.ObjectThumbnailPath(first)) : null,
            chevron);
        listRow.MinHeight = 38;
        Ui.SetSelected(listRow, row.Selected);
        return listRow;
    }

    /// <summary>The way back from an opened asset to the list of assets, naming the open one.</summary>
    private Border BuildAssetKindBreadcrumbRow(ObjectTreeRow row)
    {
        var back = Ui.TextButton("AssetKindBackButton", "← Все ассеты", () =>
        {
            _editor.CloseAssetKind();
            SyncObjectList();
        }, "compact");
        var title = Ui.Trimmed($"{row.Title} · {row.Subtitle}", "muted").Named(row.AutomationId);
        AutomationProperties.SetName(title, $"Открыт ассет «{row.Title}», {row.Subtitle}");
        return new Border
        {
            Child = new DockPanel { Children = { back.Docked(Dock.Left), title } },
            Padding = new Thickness(0, 2),
        };
    }

    /// <summary>
    /// After a selection made elsewhere (on the map), the list scrolls so the selected row is in
    /// view. Only done for a new selection, and only for lists short enough to scan cheaply.
    /// </summary>
    private bool _scrollListToSelection;

    private void ScrollObjectListToSelection()
    {
        if (!_scrollListToSelection || _objectList.ItemsSource is not IReadOnlyList<ObjectTreeRow> rows) return;
        _scrollListToSelection = false;
        if (rows.Count > 20000) return;
        for (var index = 0; index < rows.Count; index++)
        {
            if (!IsObjectTreeRowSelected(rows[index])) continue;
            _objectList.ScrollIntoView(index);
            return;
        }
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

    /// <summary>
    /// One river or lake in the objects panel: selecting it is the same selection the canvas uses,
    /// and the two switches beside it hide and lock that water system alone.
    /// </summary>
    private Border BuildWaterRow(ObjectTreeRow row)
    {
        var waterId = row.WaterId!.Value;
        var visible = Ui.RowAction(
            $"WaterRowVisible_{waterId:D}",
            row.WaterVisible ? "Скрыть воду" : "Показать воду",
            row.WaterVisible ? MapIcons.Eye : MapIcons.EyeOff,
            () =>
            {
                _editor.SelectWater(waterId);
                _editor.SetSelectedWaterVisible(!row.WaterVisible);
                SyncObjectList();
                SyncContextArea();
            });
        var locked = Ui.RowAction(
            $"WaterRowLocked_{waterId:D}",
            row.WaterLocked ? "Разблокировать воду" : "Заблокировать воду",
            row.WaterLocked ? MapIcons.Lock : MapIcons.Unlock,
            () =>
            {
                _editor.SelectWater(waterId);
                _editor.SetSelectedWaterLocked(!row.WaterLocked);
                SyncObjectList();
                SyncContextArea();
            });
        var states = row.WaterVisible ? row.WaterLocked ? " · заблокировано" : "" : " · скрыто";
        var owner = row.LayerName is { } layerName ? $" · слой «{layerName}»" : "";
        var host = Ui.StructureRow(
            row.AutomationId,
            MapIcons.River,
            row.Title,
            row.Subtitle + " · уровень " + EditorViewModel.FormatSublayer(row.Level) + states + owner,
            () =>
            {
                _editor.SelectWater(waterId);
                SyncObjectListSelection();
                SyncContextArea();
            },
            $"Вода «{row.Title}» · {row.Subtitle}{states}",
            visible,
            locked);
        Ui.SetStructureSelected(host, row.Selected);
        return host;
    }

    /// <summary>
    /// One piece of land in the objects panel: selecting it is the same selection a click on
    /// the land makes, and the two switches beside it hide and lock that land layer.
    /// </summary>
    private Border BuildLandRow(ObjectTreeRow row)
    {
        var landId = row.LandId!.Value;
        var visible = Ui.RowAction(
            $"LandRowVisible_{landId}",
            row.WaterVisible ? "Скрыть землю" : "Показать землю",
            row.WaterVisible ? MapIcons.Eye : MapIcons.EyeOff,
            () =>
            {
                _editor.ToggleLayerVisibility(landId);
                SyncObjectList();
                SyncContextArea();
            });
        var locked = Ui.RowAction(
            $"LandRowLocked_{landId}",
            row.WaterLocked ? "Разблокировать землю" : "Заблокировать землю",
            row.WaterLocked ? MapIcons.Lock : MapIcons.Unlock,
            () =>
            {
                _editor.ToggleLayerLock(landId);
                SyncObjectList();
                SyncContextArea();
            });
        var states = row.WaterVisible ? row.WaterLocked ? " · заблокирована" : "" : " · скрыта";
        var host = Ui.StructureRow(
            row.AutomationId,
            MapIcons.LayerLand,
            row.Title,
            "Земля · уровень " + EditorViewModel.FormatSublayer(row.Level) + states,
            () =>
            {
                _editor.SelectLand(landId);
                SyncObjectListSelection();
                SyncContextArea();
                SyncQuickBar();
            },
            $"Земля «{row.Title}» · уровень {EditorViewModel.FormatSublayer(row.Level)}{states}",
            visible,
            locked);
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

        // The level is the one number the row always carries, at the far right of every row so
        // the column can be read down the list, and with a sign so "+3" and "-3" can never be
        // read as the same thing. Zero is shown too: a blank would make the column uneven.
        var levelText = EditorViewModel.FormatSublayer(sceneObject.Sublayer);
        var level = new Border
        {
            Padding = new Thickness(5, 1),
            Margin = new Thickness(6, 0, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                Children =
                {
                    MapIcons.Stroked(MapIcons.Layers, 12, Application.Current?.FindResource("MapMutedBrush") as IBrush),
                    new TextBlock { Text = levelText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }
                        .Classed("muted"),
                },
            },
        }.Classed("badge").Named("ObjectRowLevel_" + sceneObject.Id);
        ToolTip.SetTip(level, "Уровень объекта: " + levelText + ". Чем выше уровень, тем выше объект на карте.");
        AutomationProperties.SetName(level, "Уровень " + levelText);

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
            $"{sceneObject.Name} · {EditorLabels.ObjectKind(sceneObject.Kind)} · уровень " + levelText,
            thumbnail,
            level);

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
        ObjectTreeRowKind.Water =>
            row.WaterId is { } waterId && _editor.WaterSelectionIds.Contains(waterId),
        ObjectTreeRowKind.Road =>
            row.RoadId is { } roadId && _editor.RoadSelectionIds.Contains(roadId),
        ObjectTreeRowKind.Land =>
            row.LandId is { } landId && _editor.SelectedLand?.Id == landId,
        ObjectTreeRowKind.AssetKind =>
            row.MemberIds is { Count: > 0 } kindMembers && kindMembers.Any(_objectSelectionSet.Contains),
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
            ObjectTreeRowKind.AssetKind => $"Ассет «{row.Title}», {row.Subtitle}",
            ObjectTreeRowKind.AssetKindBreadcrumb => $"Открыт ассет «{row.Title}», {row.Subtitle}",
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
                ObjectTreeRowKind.Water => _owner.BuildWaterRow(row),
                ObjectTreeRowKind.Road => _owner.BuildRoadRow(row),
                ObjectTreeRowKind.Land => _owner.BuildLandRow(row),
                ObjectTreeRowKind.AssetKind => _owner.BuildAssetKindRow(row),
                ObjectTreeRowKind.AssetKindBreadcrumb => _owner.BuildAssetKindBreadcrumbRow(row),
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
        SceneObjectKind.Decoration => MapIcons.Star,
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
        SyncLibraryToolbar();
        SetSegment(_placementModeButtons, _editor.StampPlacementMode switch
        {
            StampPlacementMode.Series => "Series",
            StampPlacementMode.Area => "Area",
            StampPlacementMode.Path => "Path",
            StampPlacementMode.FillArea => "FillArea",
            _ => "One",
        });

        SyncQuickFilters();
        SetToggle(_libraryFilterButtons["Favorites"], "Только избранное", _editor.LibraryFavoritesOnly);
        SetToggle(_libraryFilterButtons["Recent"], "Только недавние", _editor.LibraryRecentOnly);
        SetToggle(_libraryFilterButtons["Untagged"], "Только без тегов", _editor.LibraryUntaggedOnly);
        SetToggle(
            _libraryFilterButtons["UsedInMap"], "Только используемое на карте", _editor.LibraryUsedInMapOnly);
        SetToggle(
            _randomVariantToggle, "Случайный вариант после размещения", _editor.RandomVariantAfterPlacement);
        SetToggle(
            _randomFlipToggle, _editor.SelectedSetCanFlip ? "Случайное зеркальное отражение ассета"
                : "Отражение запрещено в паспорте ассета", _editor.StampRandomFlip && _editor.SelectedSetCanFlip);
        _randomFlipToggle.IsEnabled = _editor.SelectedSetCanFlip;
        SetToggle(_avoidOverlapToggle,
            _editor.StampAvoidOverlap ? "Без наложений: включено" : "Без наложений: выключено", _editor.StampAvoidOverlap);
        SetToggle(_randomRotationToggle,
            _editor.StampRandomAngle ? $"Случайный поворот: шаг {EditorLabels.RandomAngleStep(_editor.StampRandomAngleStep)}" : "Случайный поворот выключен",
            _editor.StampRandomAngle);

        SyncLibraryTagTree();
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
        _libraryDateHint.IsVisible = true;

        var summary = "Фильтры: " + _editor.ActiveLibraryFilterSummary;
        _libraryFilterSummary.Text = summary;
        AutomationProperties.SetName(_libraryFilterSummary, summary);
        _clearFiltersButton.IsEnabled = _editor.HasActiveLibraryFilters;

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
        if (_regionalLibraryEmpty is not null) _regionalLibraryEmpty.IsVisible = cards == 0
            && !_editor.HasActiveLibraryFilters && _editor.LibraryStatus != AssetLibraryStatus.Damaged;
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
        SyncAppearanceSections();
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
        double previewSize,
        bool stampFamily = false)
    {
        var picture = new Image { Stretch = Stretch.Uniform };
        _thumbnails.Assign(picture, _editor.LibraryThumbnailPathForVariant(variant.Id));
        var isCover = !stampFamily && variant.Id == _editor.SelectedSet?.CoverVariantId;
        var availability = _editor.AvailabilityOf(variant.Sha256);
        var flagged = availability is not (AssetAvailability.Unknown or AssetAvailability.Available);

        // The number is what the pager and the shortcuts count by, so the cell says it.
        Control face = new Panel
        {
            Width = previewSize,
            Height = previewSize,
            Children =
            {
                picture,
                new TextBlock
                {
                    Text = (position + 1).ToString(), FontSize = 10, IsHitTestVisible = false,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(2, 0, 0, 0),
                }.Classed("muted"),
            },
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
        var selected = variant.Id == _editor.CurrentVariant?.Id;
        var count = stampFamily ? _editor.StampFamilyVariants.Count : _editor.SelectedSetVariants.Count;
        var label = stampFamily ? _editor.StampFamilyVariants[position].SetName : variant.Label;
        var name = $"{label} · вариант {position + 1} из {count}"
            + (isCover ? ", обложка" : "")
            + (selected ? ", текущий" : "")
            + (flagged ? ", " + EditorViewModel.AvailabilityText(availability) : "");
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        Ui.SetSelected(button, selected);
        button.Click += (_, _) =>
        {
            if (stampFamily) _editor.SelectStampFamilyVariantAt(position);
            else _editor.SelectVariantAt(position);
            SyncAssetList();
            SyncStatus();
        };
        return button;
    }

    private void SyncNoteList()
    {
        if (_noteSelectionEditor is not null)
            _noteSelectionEditor.IsVisible = _selectedNoteId is { } selected
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
                    SyncNoteWiki();
                },
                note.Title);

            Ui.SetSelected(row, note.Id == _selectedNoteId);
            _noteList.Children.Add(row);
        }
    }

    private Button? _createVersionButton;
    private readonly TextBlock _checkpointCountText = new TextBlock
    {
        Name = "CheckpointCountText", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
    }.Classed("faint", "role-caption");

    private void SyncCheckpointList()
    {
        var count = _editor.Checkpoints.Count;
        _checkpointCountText.Text = $"{count} из {MapCheckpointLimits.MaxCount}";
        if (_createVersionButton is not null)
        {
            _createVersionButton.IsEnabled = count < MapCheckpointLimits.MaxCount;
            ToolTip.SetTip(_createVersionButton, count < MapCheckpointLimits.MaxCount
                ? "Сохранить текущее состояние карты как версию"
                : $"Достигнут лимит {MapCheckpointLimits.MaxCount} версий: удалите ненужную, чтобы сделать новый снимок");
        }
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
            renameBox.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                _editor.RenameVersion(captured.Id, renameBox.Text);
                SyncCheckpointList();
                SyncStatus();
            };
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

            var compare = Ui.TextButton("CompareCheckpoint_" + checkpoint.Id, "Сравнить", () =>
            {
                _editor.CompareWithCheckpoint(captured.Id);
                SyncCompareBlock();
            }, "compact");
            ToolTip.SetTip(compare, "Что изменилось с этой версии: итог словами, подсветка на карте, «До / После».");
            var export = Ui.AsyncButton("ExportChanges_" + checkpoint.Id, "Выгрузить изменения", () => ExportChangesAsync(captured), "compact");
            ToolTip.SetTip(export, "Файл .mapchanges с тем, что изменилось с этой версии, — для другой копии карты (без сети).");
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
                            MaxColumns = 3, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                            Children = { compare, export, restore, delete },
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
