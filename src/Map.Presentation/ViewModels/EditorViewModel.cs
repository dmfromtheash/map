using System.Globalization;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

public enum SaveState
{
    Saved,
    Saving,
    Unsaved,
    SaveFailed,
}

public enum AutosaveState
{
    Idle,
    Saving,
    Saved,
    Failed,
}

/// <summary>What a canvas drag is doing to the selected object.</summary>
public enum DragMode
{
    Move,
    Resize,
    Rotate,
}

public enum EditorTool
{
    Select,
    Brush,
    Mask,
    Land,
    Line,
    Shape,
    Text,
    Stamp,
    Notes,
}

/// <summary>
/// File paths supplied by the project's own UI driver instead of an OS dialog.
///
/// The native Windows file picker neither shows a window nor returns in this project's automation
/// host, so a driven smoke can otherwise never reach import, save or reopen in a real window. This
/// mirrors the isolated-root switch the recovery driver already uses: it applies ONLY when the UI test
/// mode is on AND an explicit fully-qualified path is supplied, so no user flow can reach it.
/// </summary>
public static class UiTestFilePaths
{
    public const string TestModeVariable = "MAP_UI_TEST_MODE";

    /// <summary>Groups separated by '|', paths inside a group separated by ';'.</summary>
    public const string ImportPathsVariable = "MAP_UI_TEST_IMPORT_PATHS";

    public const string MapPathVariable = "MAP_UI_TEST_MAP_PATH";

    private static int _consumedGroups;

    public static bool Enabled => string.Equals(
        Environment.GetEnvironmentVariable(TestModeVariable), "1", StringComparison.Ordinal);

    /// <summary>The next queued import group, or null when the driver supplied none.</summary>
    public static IReadOnlyList<string>? NextImportGroup()
    {
        if (!Enabled)
        {
            return null;
        }

        var raw = Environment.GetEnvironmentVariable(ImportPathsVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var groups = raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (_consumedGroups >= groups.Length)
        {
            return null;
        }

        var paths = groups[_consumedGroups]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => Path.IsPathFullyQualified(path) && File.Exists(path))
            .ToList();
        if (paths.Count == 0)
        {
            return null;
        }

        _consumedGroups++;
        return paths;
    }

    public static string? MapPath
    {
        get
        {
            if (!Enabled)
            {
                return null;
            }

            var value = Environment.GetEnvironmentVariable(MapPathVariable);
            return string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
                ? null
                : Path.GetFullPath(value);
        }
    }

}

public enum BrushPresetKind { RoundHard, RoundSoft, EdgeBlock, GridBlock, Rectangle, Ellipse, Polygon }

public enum LineDrawMode { FreeDraw, Pen }
public enum ShapeDrawMode { FreeDraw, Pen, Brush, Rectangle, Ellipse, Polygon }

/// <summary>What one stamp gesture produces: a single stamp, or a whole Area/Density scatter.</summary>
public enum StampPlacementMode
{
    One,
    Series,
    Area,
    Path,
    FillArea,
}

/// <summary>
/// Live drag preview over the whole selection: never part of the document and never part of
/// history. Before/Proposed are parallel lists; a single-object drag is simply a list of one.
/// </summary>
public sealed record DragPreview(
    IReadOnlyList<SceneObject> Before,
    IReadOnlyList<SceneObject> Proposed,
    DragMode Mode,
    HandleKind? Handle,
    double StartWorldX,
    double StartWorldY,
    WorldBounds? StartFrame);

/// <summary>What one canvas press started, so the control routes the following drag correctly.</summary>
public enum CanvasGesture
{
    None,
    Drag,
    Marquee,
}

/// <summary>
/// Fine-grained derived-state notifications. A canvas-only preview must not masquerade as a
/// document or side-panel change and make the Objects list rebuild on every pointer event.
/// </summary>
[Flags]
public enum EditorChangeReason
{
    None = 0,
    DocumentStructure = 1 << 0,
    ObjectProperties = 1 << 1,
    Selection = 1 << 2,
    Viewport = 1 << 3,
    CanvasInvalidation = 1 << 4,
    SidePanelData = 1 << 5,
}

public sealed class EditorSceneInvalidatedEventArgs(EditorChangeReason reasons) : EventArgs
{
    public EditorChangeReason Reasons { get; } = reasons;

    public bool Has(EditorChangeReason reason) => (Reasons & reason) != 0;
}

/// <summary>
/// The editor around one <see cref="EditingSession"/>. The session's snapshot is the single
/// source of truth; this class only orchestrates commands, selection, saving and status.
/// </summary>
public sealed partial class EditorViewModel : ViewModelBase
{
    private readonly IProjectStore _store;
    private readonly IRecentMaps _recent;
    private readonly IFilePicker _filePicker;
    private readonly IUserPrompts _prompts;
    private readonly IAppLog _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _appVersion;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    // Save/read-back, versions, export and preview share one expensive operation slot.
    // Cancelling a preview cannot leave multiple native render/encode buffers running together.
    private readonly SemaphoreSlim _resourceGate = new(1, 1);
    private readonly Map.Core.Resources.WorkingMemoryBudget _workingMemory;
    public int ActiveResourceOperations { get; private set; }

    private async Task<T> RunResourceOperationAsync<T>(Func<Task<T>> operation, CancellationToken token = default)
        => await RunLocalResourceOperationAsync(async () =>
        {
            using var reservation = await _workingMemory.EnterHeavyAsync(token);
            return await operation();
        }, token);

    private async Task<T> RunLocalResourceOperationAsync<T>(Func<Task<T>> operation, CancellationToken token = default)
    {
        await _resourceGate.WaitAsync(token);
        ActiveResourceOperations++;
        try
        {
            token.ThrowIfCancellationRequested(); return await operation();
        }
        finally { ActiveResourceOperations--; _resourceGate.Release(); }
    }
    private readonly AssetCatalog? _assetCatalog;
    private readonly BrushPresetStore? _brushPresetStore;
    private readonly IReadOnlyList<string> _localFontFamilies;
    private readonly Action<EditorLocalPreferences>? _saveLocalPreferences;

    private SaveState _saveState;
    private AutosaveState _autosaveState;
    private DateTimeOffset? _lastAutosaveUtc;
    private MapDocument? _lastAutosavedDocument;
    private MapDocument? _lastPrimaryAutosavedDocument;
    private DateTimeOffset? _lastPrimarySaveUtc;
    private DateTimeOffset? _lastCommittedEditUtc;
    private bool _primaryAutosaveArmed;
    private bool _primaryAutosaveEnabled;
    private bool _recoveryAvailable;
    private string _statusDetail = "";
    private string _inspectorError = "";
    private DragPreview? _dragPreview;
    private string _xText = "";
    private string _yText = "";
    private string _widthText = "";
    private string _heightText = "";
    private string _rotationText = "";
    private LayerId? _activeLayerId;
    private EditorTool _activeTool;
    private EditorTool _previousTool = EditorTool.Select;
    private RasterStrokeSession? _stroke;
    private LayerId? _copiedMaskLayerId;
    private bool _editingStampForeground;
    private List<VectorPoint>? _vectorPoints;
    private SceneObject? _vectorPreviewObject;
    private LayerId? _vectorPreviewLayerId;
    private ObjectId? _vectorPreviewObjectId;
    private VectorNodeId? _selectedVectorNodeId;
    private PortalId? _selectedPortalId;
    private (SceneObject Before, VectorNodeId? NodeId, PortalId? PortalId)? _vectorElementDrag;
    private (LayerId LayerId, ObjectId First, ObjectId Second, ShapeBooleanOperation Operation)? _pendingShapeBoolean;
    private SceneObject? _shapeBooleanPreviewObject;
    private SceneObject? _textPreviewObject;
    private LayerId? _textPreviewLayerId;
    private VectorPoint? _textPlacementStart;
    private TextStyle? _textPlacementStyle;
    private SceneObject? _textPlacementTemplate;
    private bool _textInheritFromSelection;
    private PlacementSession? _pendingPlacement;

    // ------------------------------------------------------- multi-selection state
    private string _objectSearchText = "";
    private ObjectTypeFilter _objectTypeFilter = ObjectTypeFilter.All;
    private Guid? _openGroupId;
    private (double X, double Y, bool Additive)? _marqueeStart;
    private WorldBounds? _marqueeWorld;

    // ---------------------------------------------------------------- library state
    private const double MinStampAreaRadius = 1;
    private const double MaxStampAreaRadius = 4_096;
    private const double MinStampDensity = 0;
    private const double MaxStampDensity = 10_000;

    private IReadOnlyList<AssetSetSummary> _librarySets = [];
    private IReadOnlyList<AssetSetGroup> _libraryGroups = [];
    private IReadOnlyList<AssetCollectionNode> _libraryCollections = [];
    private AssetTileWindow _visibleTiles = AssetTileWindow.Empty;
    private string _librarySearchText = "";
    private string? _libraryCollectionId;
    private string? _libraryTag;
    private bool _libraryUntaggedOnly;
    private bool _libraryFavoritesOnly;
    private bool _libraryRecentOnly;
    private bool _libraryUsedInMapOnly;
    private string? _libraryMediaType;
    private DateTimeOffset? _libraryImportedFromUtc;
    private DateTimeOffset? _libraryImportedToUtc;
    private AssetSortOrder _librarySort = AssetSortOrder.ImportedNewest;
    private AssetGrouping _libraryGrouping = AssetGrouping.None;
    private List<AssetLibraryRow> _libraryRows = [];
    private IReadOnlyList<string> _libraryTagChoices = [];
    private IReadOnlyList<string> _libraryMediaTypeChoices = [];
    private int _selectedLibraryRowIndex = -1;
    private int _visibleRowFirst;
    private int _visibleRowCount;
    private string _libraryImportedFromText = "";
    private string _libraryImportedToText = "";
    private bool _libraryDateError;
    private bool _suppressLibraryRefresh;
    private readonly Dictionary<string, AssetAvailability> _availability =
        new(StringComparer.OrdinalIgnoreCase);

    private int _availabilityGeneration;
    private int _availabilityRevision;

    private string? _selectedSetId;
    private IReadOnlyList<AssetVariant> _selectedSetVariants = [];
    private int _currentVariantIndex;
    private StampToolSettings _stampToolSettings = new();
    private int _gestureCounter;
    private readonly IAssetContentResolver? _assetResolver;

    public EditorViewModel(
        MapDocument document,
        string? documentPath,
        bool startDirty,
        IProjectStore store,
        IRecentMaps recent,
        IFilePicker filePicker,
        IUserPrompts prompts,
        IAppLog log,
        Func<DateTimeOffset>? clock = null,
        string? appVersion = null,
        AssetCatalog? assetCatalog = null,
        IAssetContentResolver? assetResolver = null,
        EditorShortcutSettings? shortcuts = null,
        IReadOnlyList<string>? availableFontFamilies = null,
        IImageExporter? imageExporter = null,
        IExportDestinationPicker? exportDestinationPicker = null,
        IFolderLauncher? folderLauncher = null,
        EditorLocalPreferences? localPreferences = null,
        Action<EditorLocalPreferences>? saveLocalPreferences = null,
        DateTimeOffset? lastSavedUtc = null,
        Map.Core.Resources.WorkingMemoryBudget? memory = null)
    {
        _workingMemory = memory ?? Map.Core.Resources.WorkingMemoryBudget.Shared;
        Session = new EditingSession(document, _workingMemory);
        Session.ResourceAdmissionChanged += (_, _) => Raise(nameof(StatusDetail));
        DocumentPath = documentPath;
        _store = store;
        _recent = recent;
        _filePicker = filePicker;
        _prompts = prompts;
        _log = log;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _appVersion = appVersion
            ?? typeof(EditorViewModel).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        _assetCatalog = assetCatalog;
        _brushPresetStore = assetCatalog is null ? null : new BrushPresetStore(assetCatalog.Root);
        _brushPresets = _brushPresetStore?.Load() ?? [];
        _imageExporter = imageExporter ?? new ImageExportEngine();
        _exportDestinationPicker = exportDestinationPicker;
        _folderLauncher = folderLauncher;
        var preferences = localPreferences ?? new EditorLocalPreferences();
        _stampToolSettings = NormalizeStampToolSettings(preferences.Stamp ?? new StampToolSettings
        {
            AutoSelect = preferences.AutoSelect,
        });
        _primaryAutosaveEnabled = preferences.PrimaryAutosaveEnabled;
        _saveLocalPreferences = saveLocalPreferences;
        Shortcuts = shortcuts ?? new EditorShortcutSettings();
        _localFontFamilies = SystemFontCatalog.Normalize(
            availableFontFamilies ?? SystemFontCatalog.Families);
        _assetResolver = assetResolver
            ?? (assetCatalog is null ? null : new CatalogAssetContentResolver(assetCatalog));
        StampImages = assetCatalog is null
            ? null
            : new CatalogStampImageSource(assetCatalog, () => Session.Document, () => DocumentPath);

        if (startDirty)
        {
            // A brand-new or recovered document has no durable revision yet.
            Session.MarkUnsaved();
        }

        _saveState = Session.IsDirty ? SaveState.Unsaved : SaveState.Saved;
        _primaryAutosaveArmed = !string.IsNullOrWhiteSpace(documentPath) && !startDirty;
        _lastPrimarySaveUtc = _primaryAutosaveArmed ? lastSavedUtc : null;
        _activeLayerId = document.Layers.LastOrDefault(layer => layer is not BaseColorLayer)?.Id
            ?? document.BaseColor.Id;

        if (assetCatalog is not null)
        {
            RefreshLibrary();
            RestoreStampCatalogIdentity();
        }

        SceneChanged += (_, _) => RefreshOpenExportAfterSceneChange();
    }

    public EditingSession Session { get; }

    public EditorShortcutSettings Shortcuts { get; }

    public IStampImageSource? StampImages { get; }

    public string? DocumentPath { get; private set; }

    public event EventHandler? SceneChanged;

    /// <summary>
    /// Raised when the editor abandoned an unfinished canvas gesture on its own - today only when
    /// the active tool changes. The canvas host listens so its own pointer state is dropped in the
    /// same step; without it the control would still believe a gesture is running.
    /// </summary>
    public event EventHandler? CanvasGestureCancelled;

    /// <summary>
    /// Narrow notification for session-only interaction. Legacy <see cref="SceneChanged"/>
    /// remains the document/whole-editor signal while existing edit paths are migrated in bounded
    /// slices.
    /// </summary>
    public event EventHandler<EditorSceneInvalidatedEventArgs>? SceneInvalidated;

    private void InvalidateScene(EditorChangeReason reasons) =>
        SceneInvalidated?.Invoke(this, new EditorSceneInvalidatedEventArgs(reasons));

    // ---------------------------------------------------------------- status

    public SaveState SaveState
    {
        get => _saveState;
        private set
        {
            if (SetField(ref _saveState, value))
            {
                Raise(nameof(SaveStateText));
                Raise(nameof(SaveSummary));
            }
        }
    }

    public string SaveStateText => _saveState switch
    {
        SaveState.Saved => "Сохранено",
        SaveState.Saving => "Сохранение…",
        SaveState.SaveFailed => "Ошибка сохранения",
        _ => "Не сохранено",
    };

    public AutosaveState AutosaveState
    {
        get => _autosaveState;
        private set
        {
            if (SetField(ref _autosaveState, value))
            {
                Raise(nameof(AutosaveSummary));
            }
        }
    }

    /// <summary>
    /// Recovery status is deliberately separate from manual save. A durable autosave protects
    /// work after a crash, but it never claims that the user's .mapproj matches the editor.
    /// </summary>
    public string AutosaveSummary => _autosaveState switch
    {
        AutosaveState.Saving => "Создаётся восстановление…",
        AutosaveState.Failed => "Восстановление: ошибка",
        AutosaveState.Saved when _lastAutosaveUtc is { } time
            && ReferenceEquals(_lastAutosavedDocument, Session.Document) =>
            $"Есть восстановление · {time.ToLocalTime():t}",
        AutosaveState.Saved when _lastAutosaveUtc is { } time =>
            $"Есть восстановление до {time.ToLocalTime():t}",
        _ when _recoveryAvailable => "Есть восстановление",
        _ when Session.IsDirty => "Восстановление ожидает",
        _ => "Восстановление не требуется",
    };

    public bool PrimaryAutosaveEnabled
    {
        get => _primaryAutosaveEnabled;
        set
        {
            if (SetField(ref _primaryAutosaveEnabled, value))
            {
                PersistLocalPreferences();
                Raise(nameof(PrimaryAutosaveSummary));
                Raise(nameof(SaveSummary));
            }
        }
    }

    public string PrimaryAutosaveSummary => !_primaryAutosaveEnabled
        ? "Автосохранение выключено"
        : !_primaryAutosaveArmed || string.IsNullOrWhiteSpace(DocumentPath)
            ? "Автосохранение после первого сохранения"
            : SaveState switch
            {
                SaveState.Saving => "Сохраняется…",
                SaveState.SaveFailed => "Ошибка сохранения · Повторить или Сохранить как…",
                SaveState.Saved when _lastPrimarySaveUtc is { } time =>
                    $"Сохранено {time.ToLocalTime():HH:mm}",
                _ => "Не сохранено · автосохранение ожидает",
            };

    public bool RecoveryAvailable => _recoveryAvailable;

    public DateTimeOffset? LastPrimarySaveUtc => _lastPrimarySaveUtc;

    /// <summary>Short human explanation of the last failure, with the recovery path when one exists.</summary>
    public string StatusDetail
    {
        get => Session.LastResourceError ?? _statusDetail;
        private set => SetField(ref _statusDetail, value);
    }

    public string Title => Session.Document.Title;

    public string ViewportStatus => $"Масштаб {Math.Round(Session.Viewport.Scale * 100)}%";

    /// <summary>
    /// Selection shown the way a person reads it: one object by name and kind, a whole group by
    /// its name and member count, any other multi-selection by an honest count.
    /// </summary>
    public string SelectionStatus
    {
        get
        {
            if (SelectionCount > 1)
            {
                return SelectedGroupId is not null
                    ? $"Группа «{SelectedGroupName ?? "Группа"}» · {EditorLabels.Objects(SelectionCount)}"
                    : $"Выбрано: {EditorLabels.Objects(SelectionCount)}";
            }

            return Session.SelectedObject is { } selected
                ? EditorLabels.DescribeObject(selected)
                : "Ничего не выбрано";
        }
    }

    /// <summary>How many objects the selection holds right now.</summary>
    public int SelectionCount => Session.SelectedObjectIds.Count;

    /// <summary>
    /// The numeric single-object inspector may only exist for exactly one selected object;
    /// showing one member's numbers as if they were shared would simply be wrong.
    /// </summary>
    public bool ShowSingleInspector => SelectionCount == 1;

    /// <summary>
    /// The group id when the selection is exactly one whole group - every selected object shares
    /// it and no member is left out. Null for anything else.
    /// </summary>
    public Guid? SelectedGroupId
    {
        get
        {
            var selected = Session.SelectedObjects;
            if (selected.Count == 0)
            {
                return null;
            }

            var groupId = selected[0].GroupId;
            if (groupId is null || selected.Any(o => o.GroupId != groupId))
            {
                return null;
            }

            var members = SceneGeometry.GroupMembers(Session.Document, groupId.Value);
            return members.Count == selected.Count ? groupId : null;
        }
    }

    public string? SelectedGroupName
    {
        get
        {
            if (SelectedGroupId is not { } groupId)
            {
                return null;
            }

            var members = SceneGeometry.GroupMembers(Session.Document, groupId);
            return members.Count > 0 ? members[0].GroupName : null;
        }
    }

    /// <summary>The group whose children are being edited directly, or null. Session state.</summary>
    public Guid? OpenGroupId => _openGroupId;

    /// <summary>Visibility of the whole selection as one honest value, never a fake single one.</summary>
    public string SelectionVisibilitySummary => SummarizeSelection(o => o.Visible,
        EditorLabels.ObjectVisibilityValue(true), EditorLabels.ObjectVisibilityValue(false));

    public string SelectionLockSummary => SummarizeSelection(o => o.Locked,
        EditorLabels.ObjectLockValue(true), EditorLabels.ObjectLockValue(false));

    private string SummarizeSelection(Func<SceneObject, bool> flag, string whenTrue, string whenFalse)
    {
        var selected = Session.SelectedObjects;
        if (selected.Count == 0)
        {
            return "";
        }

        var first = flag(selected[0]);
        return selected.All(o => flag(o) == first)
            ? first ? whenTrue : whenFalse
            : "Разные значения";
    }

    /// <summary>Kind of the current selection, or null - the only switch the inspector may use.</summary>
    public SceneObjectKind? SelectionKind => Session.SelectedObject?.Kind;

    /// <summary>Edits made since the last durable save. Zero means the file matches the screen.</summary>
    public long PendingChanges => Session.PendingChanges;

    /// <summary>
    /// Save state as one honest phrase. Unsaved work is reported with a count, because "не
    /// сохранено" alone does not tell the user how much is at stake.
    /// </summary>
    public string SaveSummary => _saveState switch
    {
        SaveState.Saved when _lastPrimarySaveUtc is { } time =>
            $"Сохранено {time.ToLocalTime():HH:mm}",
        SaveState.Saved => "Сохранено",
        SaveState.Saving => "Сохранение…",
        SaveState.SaveFailed => "Ошибка сохранения",
        _ => PendingChanges > 0 ? $"Не сохранено: {EditorLabels.Changes(PendingChanges)}" : "Не сохранено",
    };

    /// <summary>Active layer for the status bar: its name, never its id.</summary>
    public string ActiveLayerSummary =>
        ActiveLayer is { } layer ? $"Слой: {layer.Name}" : "Слой не выбран";

    /// <summary>Grid and snapping state, resolved across every grid definition in the document.</summary>
    public string GridSummary
    {
        get
        {
            var grids = Session.Document.Layers.OfType<GridLayer>()
                .Where(layer => layer.Visible)
                .SelectMany(layer => layer.Grids)
                .ToList();

            if (grids.Count == 0)
            {
                return "Сетка выключена";
            }

            return grids.Any(grid => grid.SnapEnabled)
                ? "Сетка вкл · привязка вкл"
                : "Сетка вкл · привязка выкл";
        }
    }

    public bool CanUndo => Session.CanUndo;

    public bool CanRedo => Session.CanRedo;

    public IReadOnlyList<MapLayer> Layers => Session.Document.Layers;

    public IReadOnlyList<MapNote> Notes => Session.Document.Notes;

    public IReadOnlyList<MapCheckpoint> Checkpoints => Session.Document.Checkpoints;

    public IReadOnlyList<MapNote> SearchNotes(string? query) => string.IsNullOrWhiteSpace(query)
        ? Notes
        : [.. Notes.Where(note => note.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || note.Markdown.Contains(query, StringComparison.OrdinalIgnoreCase))];

    public MapLayer? ActiveLayer =>
        _activeLayerId is { } id ? Session.Document.FindLayer(id) : null;

    public LayerId? ActiveLayerId => _activeLayerId;

    public EditorTool ActiveTool
    {
        get => _activeTool;
        set
        {
            if (_activeTool == value)
            {
                return;
            }

            // Switching tools abandons every kind of unfinished gesture, not only the drawing
            // ones. A surviving drag, marquee or node grab used to commit itself on the next
            // release under the new tool - history the user never asked for.
            CancelStroke();
            CancelVectorDraw();
            CancelPenPath();
            CancelLandShapeGesture();
            CancelTextPlacement();
            CancelShapeBooleanPreview();
            CancelPlacement();
            CancelEditablePathGesture(); CancelEditableAreaGesture();
            CancelDrag();
            CancelMarquee();
            CancelVectorElementDrag();
            CanvasGestureCancelled?.Invoke(this, EventArgs.Empty);
            if (value != EditorTool.Brush)
            {
                _editingStampForeground = false;
            }
            _previousTool = _activeTool;
            _activeTool = value;
            Raise();
            Raise(nameof(MaskEditLayerId));
            Raise(nameof(RasterToolReady));
            Raise(nameof(RasterToolHint));
            Raise(nameof(RasterToolFixLabel));
            SceneChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Returns to the previously active canvas tool; repeated calls toggle the pair.</summary>
    public void SelectPreviousTool()
    {
        var previous = _previousTool;
        ActiveTool = previous;
    }

    public BrushSettings BrushSettings { get; set; } = new();

    public LineDrawMode ActiveLineDrawMode { get; set; } = LineDrawMode.FreeDraw;

    public ShapeDrawMode ActiveShapeDrawMode { get; set; } = ShapeDrawMode.FreeDraw;

    /// <summary>
    /// Corner count of the Polygon draw mode. It used to be a literal 6 inside the gesture, so the
    /// mode could only ever make hexagons and the name promised something it did not do.
    /// </summary>
    public int ShapePolygonSides
    {
        get => _shapePolygonSides;
        set => _shapePolygonSides = Math.Clamp(value, 3, 24);
    }

    /// <summary>
    /// Width of the Brush draw mode in canvas units. Brush used to be an alias of Free Draw - the
    /// same thin outline - which is why it read as broken. It now sweeps a band of this width.
    /// </summary>
    public double ShapeBrushWidth
    {
        get => _shapeBrushWidth;
        set => _shapeBrushWidth = double.IsFinite(value) ? Math.Clamp(value, 1, 512) : _shapeBrushWidth;
    }

    /// <summary>Shift: rectangles become squares, ellipses circles, polygons regular.</summary>
    public bool VectorConstrainProportions { get; set; }

    /// <summary>Alt: the press point is the centre of the new shape instead of a corner.</summary>
    public bool VectorDrawFromCenter { get; set; }

    /// <summary>
    /// What the next drawn shape and line look like. Without these the tool could only ever make
    /// the one built-in green, and every shape had to be recoloured after the fact.
    /// </summary>
    public string NewShapeFillColor
    {
        get => _newShapeFillColor;
        set => _newShapeFillColor = AppearanceColor.Normalize(value) ?? _newShapeFillColor;
    }

    public string NewShapeStrokeColor
    {
        get => _newShapeStrokeColor;
        set => _newShapeStrokeColor = AppearanceColor.Normalize(value) ?? _newShapeStrokeColor;
    }

    public double NewShapeStrokeWidth
    {
        get => _newShapeStrokeWidth;
        set => _newShapeStrokeWidth = double.IsFinite(value) ? Math.Clamp(value, 0, 64) : _newShapeStrokeWidth;
    }

    public string NewLineStrokeColor
    {
        get => _newLineStrokeColor;
        set => _newLineStrokeColor = AppearanceColor.Normalize(value) ?? _newLineStrokeColor;
    }

    public double NewLineStrokeWidth
    {
        get => _newLineStrokeWidth;
        set => _newLineStrokeWidth = double.IsFinite(value) ? Math.Clamp(value, 1, 64) : _newLineStrokeWidth;
    }

    private int _shapePolygonSides = 6;
    private double _shapeBrushWidth = 24;
    private string _newShapeFillColor = new ShapeFillStyle().Color;
    private string _newShapeStrokeColor = new VectorStrokeStyle().Color;
    private double _newShapeStrokeWidth = 3;
    private string _newLineStrokeColor = new VectorStrokeStyle().Color;
    private double _newLineStrokeWidth = new VectorStrokeStyle().Width;

    private ShapeFillStyle NewShapeFill() => new() { Color = NewShapeFillColor };

    private VectorStrokeStyle NewShapeStroke() =>
        new() { Color = NewShapeStrokeColor, Width = NewShapeStrokeWidth };

    private VectorStrokeStyle NewLineStroke() =>
        new() { Color = NewLineStrokeColor, Width = NewLineStrokeWidth };

    public bool TextInheritFromSelection
    {
        get => _textInheritFromSelection;
        set => SetField(ref _textInheritFromSelection, value);
    }

    public MaskPaintMode MaskMode { get; set; } = MaskPaintMode.Hide;

    public bool LandSubtract { get; set; }

    // ------------------------------------------------------------------ land shapes

    private LandShapeKind _activeLandShape = LandShapeKind.Circle;
    private VectorPoint? _landShapeStart;
    private VectorPoint? _landShapeCurrent;

    /// <summary>
    /// Which shape a Land drag makes. Circle keeps the freehand round brush; every other choice
    /// turns the drag into a region gesture.
    /// <para>
    /// The six shape buttons used to write a local variable in the panel that nothing else read.
    /// Choosing a shape changed nothing on the canvas and the button did not even look picked, so
    /// the whole row read as broken.
    /// </para>
    /// </summary>
    public LandShapeKind ActiveLandShape
    {
        get => _activeLandShape;
        set
        {
            if (_activeLandShape == value)
            {
                return;
            }

            _activeLandShape = value;
            CancelLandShapeGesture();
            Raise(nameof(ActiveLandShape));
            Raise(nameof(RasterToolHint));
            SceneChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>True while a Land drag describes a region instead of painting freehand.</summary>
    public bool LandDrawsRegion =>
        ActiveTool == EditorTool.Land && ActiveLandShape != LandShapeKind.Circle;

    /// <summary>The region the current Land drag would fill; the canvas outlines it.</summary>
    public WorldBounds? LandShapePreview =>
        _landShapeStart is { } start && _landShapeCurrent is { } current
            ? new WorldBounds(
                Math.Min(start.X, current.X), Math.Min(start.Y, current.Y),
                Math.Max(start.X, current.X), Math.Max(start.Y, current.Y))
            : null;

    public bool BeginLandShapeGesture(double worldX, double worldY)
    {
        if (!LandDrawsRegion)
        {
            return false;
        }

        if (RasterTargetLayer(EditorTool.Land) is not LandLayer land)
        {
            StatusDetail = RasterToolRefusal(EditorTool.Land);
            return false;
        }

        UseAsActiveLayer(land);
        _landShapeStart = new VectorPoint(worldX, worldY);
        _landShapeCurrent = _landShapeStart;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        return true;
    }

    public void UpdateLandShapeGesture(double worldX, double worldY)
    {
        if (_landShapeStart is null)
        {
            return;
        }

        _landShapeCurrent = new VectorPoint(worldX, worldY);
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    /// <summary>Fills the dragged region with the chosen shape as one undoable command.</summary>
    public bool EndLandShapeGesture()
    {
        var bounds = LandShapePreview;
        var shape = ActiveLandShape;
        CancelLandShapeGesture();
        if (bounds is not { } region)
        {
            return false;
        }

        if (region.Right - region.Left < 1 || region.Bottom - region.Top < 1)
        {
            StatusDetail = "Область слишком мала — форма не применена.";
            return false;
        }

        return ApplyLandShape(shape, region);
    }

    public bool CancelLandShapeGesture()
    {
        if (_landShapeStart is null)
        {
            return false;
        }

        _landShapeStart = null;
        _landShapeCurrent = null;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        return true;
    }

    public StrokePreview? StrokePreview => _stroke is null
        || (!_editingStampForeground && _activeLayerId is null)
        ? null
        : new StrokePreview
        {
            LayerId = _activeLayerId ?? default,
            Coverage = _stroke.Coverage,
            Color = ActiveTool == EditorTool.Land && ActiveLayer is LandLayer land
                ? land.EdgeStyle.FillColor
                : _stroke.Settings.Color,
            Opacity = _stroke.Settings.Opacity,
            Eraser = _stroke.Settings.Eraser,
            PaintSettings = ActiveTool == EditorTool.Mask ? null
                : ActiveTool == EditorTool.Land && ActiveLayer is LandLayer previewLand
                    ? _stroke.Settings with { Texture = null, Color = previewLand.EdgeStyle.FillColor }
                    : _stroke.Settings,
            IsMask = ActiveTool == EditorTool.Mask,
            IsStampForeground = _editingStampForeground,
        };

    public bool IsEditingStampForeground => _editingStampForeground;

    public LayerId? MaskEditLayerId =>
        ActiveTool == EditorTool.Mask && ActiveLayer?.Mask is not null ? _activeLayerId : null;

    public IReadOnlyList<AssetRecord> Assets => _assetCatalog?.Records ?? [];

    public IReadOnlyList<AssetRecord> SearchAssets(string? query) =>
        _assetCatalog?.Search(query) ?? [];

    // ================================================================ local library

    /// <summary>How the local library loaded; the UI shows migrated and damaged apart.</summary>
    public AssetLibraryStatus LibraryStatus =>
        _assetCatalog?.Status ?? AssetLibraryStatus.Ready;

    public string LibraryDiagnostic => _assetCatalog?.Diagnostic ?? "";

    public bool IsLibraryAvailable =>
        _assetCatalog is not null && _assetCatalog.Status != AssetLibraryStatus.Damaged;

    public IReadOnlyList<AssetSetSummary> LibrarySets => _librarySets;

    public IReadOnlyList<AssetSetGroup> LibraryGroups => _libraryGroups;

    public IReadOnlyList<AssetCollectionNode> LibraryCollections => _libraryCollections;

    /// <summary>The tiles a virtualized grid is currently allowed to realise.</summary>
    public AssetTileWindow VisibleLibraryTileWindow => _visibleTiles;

    public IReadOnlyList<AssetSetSummary> VisibleLibraryTiles => _visibleTiles.Tiles;

    public string LibrarySearchText
    {
        get => _librarySearchText;
        set
        {
            if (SetField(ref _librarySearchText, value ?? ""))
            {
                RefreshLibrary();
            }
        }
    }

    public string? LibraryCollectionId
    {
        get => _libraryCollectionId;
        set
        {
            if (SetField(ref _libraryCollectionId, value))
            {
                RefreshLibrary();
            }
        }
    }

    public string? LibraryTag
    {
        get => _libraryTag;
        set
        {
            if (SetField(ref _libraryTag, value))
            {
                RefreshLibrary();
            }
        }
    }

    public bool LibraryUntaggedOnly
    {
        get => _libraryUntaggedOnly;
        set
        {
            if (SetField(ref _libraryUntaggedOnly, value))
            {
                RefreshLibrary();
            }
        }
    }

    public bool LibraryFavoritesOnly
    {
        get => _libraryFavoritesOnly;
        set
        {
            if (SetField(ref _libraryFavoritesOnly, value))
            {
                RefreshLibrary();
            }
        }
    }

    public bool LibraryRecentOnly
    {
        get => _libraryRecentOnly;
        set
        {
            if (SetField(ref _libraryRecentOnly, value))
            {
                RefreshLibrary();
            }
        }
    }

    /// <summary>Restricts the library to content the open map actually places.</summary>
    public bool LibraryUsedInMapOnly
    {
        get => _libraryUsedInMapOnly;
        set
        {
            if (SetField(ref _libraryUsedInMapOnly, value))
            {
                RefreshLibrary();
            }
        }
    }

    public string? LibraryMediaType
    {
        get => _libraryMediaType;
        set
        {
            if (SetField(ref _libraryMediaType, value))
            {
                RefreshLibrary();
            }
        }
    }

    public DateTimeOffset? LibraryImportedFromUtc
    {
        get => _libraryImportedFromUtc;
        set
        {
            if (SetField(ref _libraryImportedFromUtc, value))
            {
                RefreshLibrary();
            }
        }
    }

    public DateTimeOffset? LibraryImportedToUtc
    {
        get => _libraryImportedToUtc;
        set
        {
            if (SetField(ref _libraryImportedToUtc, value))
            {
                RefreshLibrary();
            }
        }
    }

    public AssetSortOrder LibrarySort
    {
        get => _librarySort;
        set
        {
            if (SetField(ref _librarySort, value))
            {
                RefreshLibrary();
            }
        }
    }

    public AssetGrouping LibraryGrouping
    {
        get => _libraryGrouping;
        set
        {
            if (SetField(ref _libraryGrouping, value))
            {
                RefreshLibrary();
            }
        }
    }

    /// <summary>Re-runs the current query. Metadata only: no original image is opened here.</summary>
    public void RefreshLibrary()
    {
        _libraryQueryVersion++;
        if (_suppressLibraryRefresh)
        {
            return;
        }

        if (_assetCatalog is null || _assetCatalog.Status == AssetLibraryStatus.Damaged)
        {
            _librarySets = [];
            _libraryGroups = [];
            _libraryCollections = [];
            _libraryRows = [];
            _libraryTagChoices = [];
            _libraryMediaTypeChoices = [];
            _visibleTiles = AssetTileWindow.Empty;
            _visibleRowFirst = 0;
            _visibleRowCount = 0;
            ForgetAvailability();
            RaiseLibrary();
            return;
        }

        var result = _assetCatalog.Query(CurrentLibraryQuery());

        _librarySets = result.Sets;
        _libraryGroups = result.Groups;
        _libraryCollections = _assetCatalog.CollectionTree();
        _libraryRows = BuildLibraryRows(result);
        RefreshFilterChoices();
        SyncSelectedLibraryRow();

        // A new result means the previous verdicts describe rows that may no longer be on screen,
        // and a file may have changed since. Nothing is carried over as "still fine".
        ForgetAvailability();
        _visibleTiles = SliceVisibleRows(_visibleRowFirst, _visibleRowCount);
        RaiseLibrary();
    }

    /// <summary>
    /// Turns one query result into the flat row sequence the grid virtualizes. Without grouping
    /// every row is a card; with grouping each header is followed by the cards it covers, so the
    /// same single list control shows both without ever materialising the whole library.
    /// </summary>
    private int _libraryQueryVersion;
    private readonly SemaphoreSlim _libraryQueryGate = new(1, 1);
    private AssetQuery CurrentLibraryQuery() => new AssetQuery
        {
            Text = _librarySearchText,
            CollectionId = _libraryCollectionId,
            Tag = _libraryTag,
            UntaggedOnly = _libraryUntaggedOnly,
            FavoritesOnly = _libraryFavoritesOnly,
            RecentOnly = _libraryRecentOnly,
            UsedContentHashes = _libraryUsedInMapOnly ? UsedContentHashes() : null,
            MediaType = _libraryMediaType,
            ImportedFromUtc = _libraryImportedFromUtc,
            ImportedToUtc = _libraryImportedToUtc,
            Sort = _librarySort,
            Group = _libraryGrouping,
        };

    public async Task SearchLibraryAsync(string text, CancellationToken cancellationToken)
    {
        if (_assetCatalog is null) return;
        SetField(ref _librarySearchText, text, nameof(LibrarySearchText));
        var version = ++_libraryQueryVersion;
        var query = CurrentLibraryQuery();
        await _libraryQueryGate.WaitAsync(cancellationToken);
        AssetQueryResult result;
        try { result = await _assetCatalog.QueryAsync(query, cancellationToken); }
        finally { _libraryQueryGate.Release(); }
        if (version != _libraryQueryVersion || cancellationToken.IsCancellationRequested) return;
        _librarySets = result.Sets;
        _libraryGroups = result.Groups;
        _libraryRows = BuildLibraryRows(result);
        SyncSelectedLibraryRow();
        _visibleTiles = SliceVisibleRows(_visibleRowFirst, _visibleRowCount);
        RaiseLibrary();
    }

    private List<AssetLibraryRow> BuildLibraryRows(AssetQueryResult result)
    {
        if (_libraryGrouping == AssetGrouping.None || result.Groups.Count == 0)
        {
            return [.. result.Sets.Select(card => AssetLibraryRow.ForCard("", card))];
        }

        var rows = new List<AssetLibraryRow>(result.Sets.Count + result.Groups.Count);
        foreach (var group in result.Groups)
        {
            rows.Add(AssetLibraryRow.Header(group.Key, group.Title, group.Items.Count));
            foreach (var card in group.Items)
            {
                rows.Add(AssetLibraryRow.ForCard(group.Key, card));
            }
        }

        return rows;
    }

    /// <summary>
    /// The choices the filter controls offer. They come from the whole catalog rather than from
    /// the current result, so choosing one tag does not make every other tag disappear.
    /// </summary>
    private void RefreshFilterChoices()
    {
        if (_assetCatalog is null)
        {
            return;
        }

        var tags = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var set in _assetCatalog.Sets)
        {
            foreach (var tag in set.Tags)
            {
                tags.Add(tag);
            }
        }

        var media = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in _assetCatalog.Records)
        {
            if (record.MediaType.Length > 0)
            {
                media.Add(record.MediaType);
            }
        }

        _libraryTagChoices = [.. tags];
        _libraryMediaTypeChoices = [.. media];
    }

    private void SyncSelectedLibraryRow()
    {
        _selectedLibraryRowIndex = -1;
        if (_selectedSetId is null)
        {
            return;
        }

        for (var index = 0; index < _libraryRows.Count; index++)
        {
            if (_libraryRows[index].Card is { } card
                && string.Equals(card.SetId, _selectedSetId, StringComparison.Ordinal))
            {
                _selectedLibraryRowIndex = index;
                return;
            }
        }
    }

    /// <summary>The rows a virtualized grid may realise, and the cards inside that span.</summary>
    public IReadOnlyList<AssetLibraryRow> LibraryRows => _libraryRows;

    /// <summary>Row of the selected card in the current grouping, or -1 when it is not shown.</summary>
    public int SelectedLibraryRowIndex => _selectedLibraryRowIndex;

    /// <summary>
    /// Makes the card on one row current. A header is not a card: selecting it must leave the
    /// tool holding whatever set it already had, so the answer is simply no.
    /// </summary>
    public bool SelectLibraryRow(int index)
    {
        if (index < 0 || index >= _libraryRows.Count || _libraryRows[index].Card is not { } card)
        {
            return false;
        }

        SelectAssetSet(card.SetId);
        return true;
    }

    /// <summary>
    /// Declares which slice of the result the grid can actually show. Only these rows ever ask
    /// for a thumbnail or a file check, so ten thousand cards never become ten thousand bitmaps.
    /// </summary>
    public void SetLibraryVisibleRange(int firstIndex, int count)
    {
        var window = SliceVisibleRows(firstIndex, count);
        if (window.FirstIndex == _visibleTiles.FirstIndex && window.Count == _visibleTiles.Count
            && _visibleRowFirst == Math.Max(0, firstIndex))
        {
            return;
        }

        _visibleRowFirst = window.FirstIndex;
        _visibleRowCount = Math.Max(0, Math.Min(count, _libraryRows.Count - window.FirstIndex));
        _visibleTiles = window;
        Raise(nameof(VisibleLibraryTiles));
        Raise(nameof(VisibleLibraryTileWindow));
    }

    /// <summary>
    /// Reports the rows the grid really realised. The cost of this is the number of realised
    /// containers, never the size of the result: a layout pass must not walk the whole library.
    /// </summary>
    public void ReportRealizedLibraryRows(IEnumerable<int> realizedRowIndexes)
    {
        ArgumentNullException.ThrowIfNull(realizedRowIndexes);
        var first = int.MaxValue;
        var last = -1;
        foreach (var index in realizedRowIndexes)
        {
            if (index < 0 || index >= _libraryRows.Count)
            {
                continue;
            }

            if (index < first)
            {
                first = index;
            }

            if (index > last)
            {
                last = index;
            }
        }

        if (last < 0)
        {
            SetLibraryVisibleRange(0, 0);
            return;
        }

        SetLibraryVisibleRange(first, last - first + 1);
    }

    private AssetTileWindow SliceVisibleRows(int firstIndex, int count)
    {
        if (_libraryRows.Count == 0 || count <= 0)
        {
            return AssetTileWindow.Empty;
        }

        var first = Math.Clamp(firstIndex, 0, Math.Max(0, _libraryRows.Count - 1));
        var take = Math.Clamp(count, 0, _libraryRows.Count - first);
        var cards = new List<AssetSetSummary>(take);
        for (var index = first; index < first + take; index++)
        {
            if (_libraryRows[index].Card is { } card)
            {
                cards.Add(card);
            }
        }

        return new AssetTileWindow(first, cards.Count, cards);
    }
    /// <summary>Absolute path of a card cover thumbnail, or null when the library has none.</summary>
    public string? LibraryThumbnailPath(AssetSetSummary summary)
    {
        if (_assetCatalog is null || summary is null
            || string.IsNullOrEmpty(summary.CoverThumbnailRelativePath))
        {
            return null;
        }

        return _assetCatalog.ThumbnailPathForVariant(summary.CoverVariantId);
    }

    public string? LibraryThumbnailPathForVariant(string variantId) =>
        _assetCatalog?.ThumbnailPathForVariant(variantId);

    /// <summary>
    /// The catalog thumbnail of the exact asset a placed object draws, or null when the object is
    /// not asset-backed or the catalog cannot show it. Lists use this so a placed tree looks like
    /// that tree instead of like the tool that placed it.
    /// </summary>
    public string? ObjectThumbnailPath(SceneObject sceneObject)
    {
        if (_assetCatalog is null || sceneObject?.Stamp?.AssetSha256 is not { } sha)
        {
            return null;
        }

        return _assetCatalog.Find(sha) is { } record && _assetCatalog.TryThumbnailPath(record, out var path)
            ? path
            : null;
    }

    // ---------------------------------------------------------------- filter controls

    /// <summary>Every tag the catalog knows, so choosing one never hides the others.</summary>
    public IReadOnlyList<string> LibraryTagChoices => _libraryTagChoices;

    public IReadOnlyList<string> LibraryMediaTypeChoices => _libraryMediaTypeChoices;

    /// <summary>Import date bound as the user types it, in ISO <c>ГГГГ-ММ-ДД</c> form.</summary>
    public string LibraryImportedFromText
    {
        get => _libraryImportedFromText;
        set => SetImportedBound(value, from: true);
    }

    public string LibraryImportedToText
    {
        get => _libraryImportedToText;
        set => SetImportedBound(value, from: false);
    }

    /// <summary>True while one of the date fields holds something that is not a date.</summary>
    public bool HasLibraryDateError => _libraryDateError;

    public bool HasActiveLibraryFilters =>
        _librarySearchText.Length > 0
        || _libraryCollectionId is not null
        || _libraryTag is not null
        || _libraryMediaType is not null
        || _libraryUntaggedOnly
        || _libraryFavoritesOnly
        || _libraryRecentOnly
        || _libraryUsedInMapOnly
        || _libraryImportedFromText.Length > 0
        || _libraryImportedToText.Length > 0;

    /// <summary>
    /// Says in words which filters are on. Icon-only toggles alone would leave the user guessing
    /// why the library looks empty, so the panel can always show the reason.
    /// </summary>
    public string ActiveLibraryFilterSummary
    {
        get
        {
            var parts = new List<string>();
            if (_librarySearchText.Length > 0)
            {
                parts.Add($"поиск «{_librarySearchText}»");
            }

            if (_libraryCollectionId is not null)
            {
                parts.Add("коллекция " + CollectionTitle(_libraryCollectionId));
            }

            if (_libraryTag is { } tag)
            {
                parts.Add($"тег «{tag}»");
            }

            if (_libraryUntaggedOnly)
            {
                parts.Add("без тегов");
            }

            if (_libraryFavoritesOnly)
            {
                parts.Add("избранное");
            }

            if (_libraryRecentOnly)
            {
                parts.Add("недавние");
            }

            if (_libraryUsedInMapOnly)
            {
                parts.Add("используется на карте");
            }

            if (_libraryMediaType is { } media)
            {
                parts.Add("тип " + media);
            }

            if (_libraryImportedFromText.Length > 0)
            {
                parts.Add(_libraryImportedFromUtc is null
                    ? "с (дата не распознана)"
                    : "с " + _libraryImportedFromText);
            }

            if (_libraryImportedToText.Length > 0)
            {
                parts.Add(_libraryImportedToUtc is null
                    ? "по (дата не распознана)"
                    : "по " + _libraryImportedToText);
            }

            return parts.Count == 0 ? "Фильтры не заданы" : string.Join(" · ", parts);
        }
    }

    /// <summary>Drops every filter in one pass, so the grid is rebuilt once rather than ten times.</summary>
    public void ClearLibraryFilters()
    {
        _suppressLibraryRefresh = true;
        try
        {
            LibrarySearchText = "";
            LibraryCollectionId = null;
            LibraryTag = null;
            LibraryMediaType = null;
            LibraryUntaggedOnly = false;
            LibraryFavoritesOnly = false;
            LibraryRecentOnly = false;
            LibraryUsedInMapOnly = false;
            LibraryImportedFromText = "";
            LibraryImportedToText = "";
        }
        finally
        {
            _suppressLibraryRefresh = false;
        }

        RefreshLibrary();
        RaiseFilterState();
        StatusDetail = "Фильтры библиотеки сброшены.";
    }

    private void SetImportedBound(string? value, bool from)
    {
        var text = (value ?? "").Trim();
        if (from ? text == _libraryImportedFromText : text == _libraryImportedToText)
        {
            return;
        }

        DateTimeOffset? bound = null;
        if (text.Length > 0 && DateTimeOffset.TryParseExact(
            text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            // "to" means the whole of that day, otherwise a card imported at noon would fall
            // outside a range whose upper bound is its own import date.
            bound = from ? parsed : parsed.AddDays(1).AddTicks(-1);
        }

        if (from)
        {
            _libraryImportedFromText = text;
            _libraryImportedFromUtc = bound;
        }
        else
        {
            _libraryImportedToText = text;
            _libraryImportedToUtc = bound;
        }

        _libraryDateError = IsInvalidDate(_libraryImportedFromText) || IsInvalidDate(_libraryImportedToText);
        Raise(from ? nameof(LibraryImportedFromText) : nameof(LibraryImportedToText));
        Raise(nameof(HasLibraryDateError));
        RefreshLibrary();
        RaiseFilterState();

        static bool IsInvalidDate(string text) => text.Length > 0 && !DateTimeOffset.TryParseExact(
            text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);
    }

    private string CollectionTitle(string id)
    {
        string? found = null;
        Walk(_libraryCollections);
        return found ?? id;

        void Walk(IReadOnlyList<AssetCollectionNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (found is not null)
                {
                    return;
                }

                if (string.Equals(node.Id, id, StringComparison.Ordinal))
                {
                    found = "«" + node.Name + "»";
                    return;
                }

                Walk(node.Children);
            }
        }
    }

    private void RaiseFilterState()
    {
        Raise(nameof(HasActiveLibraryFilters));
        Raise(nameof(ActiveLibraryFilterSummary));
        Raise(nameof(LibraryTagChoices));
        Raise(nameof(LibraryMediaTypeChoices));
    }

    // ---------------------------------------------------------------- set management

    public bool CanRenameSelectedSet =>
        _assetCatalog is not null && IsLibraryAvailable && _selectedSetId is not null;

    /// <summary>Whether the current variant has anywhere to move in the set's manual order.</summary>
    public bool CanMoveCurrentVariant(int direction)
    {
        if (!CanRenameSelectedSet || direction == 0 || _selectedSetVariants.Count <= 1)
        {
            return false;
        }

        var target = _currentVariantIndex + Math.Sign(direction);
        return target >= 0 && target < _selectedSetVariants.Count;
    }

    public bool CanMakeCurrentVariantCover =>
        CanRenameSelectedSet && _selectedSetVariants.Count > 1
        && CurrentVariant is { } variant && SelectedSet is { } set
        && !string.Equals(set.CoverVariantId, variant.Id, StringComparison.Ordinal);

    /// <summary>Renames the card. The set id and every variant id stay exactly as they were.</summary>
    public bool RenameSelectedSet(string? name)
    {
        if (!CanRenameSelectedSet)
        {
            StatusDetail = "Сначала выберите набор в библиотеке.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            StatusDetail = "Имя набора не может быть пустым.";
            return false;
        }

        if (!MutateSet(() => _assetCatalog!.RenameSet(_selectedSetId!, name)))
        {
            return false;
        }

        StatusDetail = $"Набор переименован: «{name.Trim()}».";
        return true;
    }

    /// <summary>
    /// Moves the current variant one place in the set's own order. The variant stays current, so
    /// the position readout follows the thumbnail the user is looking at.
    /// </summary>
    public bool MoveCurrentVariant(int direction)
    {
        if (!CanMoveCurrentVariant(direction))
        {
            StatusDetail = _selectedSetId is null
                ? "Сначала выберите набор в библиотеке."
                : "Этот вариант уже на краю набора.";
            return false;
        }

        var order = _selectedSetVariants.Select(variant => variant.Id).ToList();
        var from = _currentVariantIndex;
        var to = from + Math.Sign(direction);
        (order[from], order[to]) = (order[to], order[from]);
        var moved = order[to];
        if (!MutateSet(() => _assetCatalog!.ReorderSetVariants(_selectedSetId!, order)))
        {
            return false;
        }

        SelectVariantById(moved);
        StatusDetail = $"Порядок изменён: вариант {CurrentVariantLabel}.";
        return true;
    }

    /// <summary>Promotes the current variant to the card's cover without touching the order.</summary>
    public bool MakeCurrentVariantCover()
    {
        if (!CanMakeCurrentVariantCover)
        {
            StatusDetail = _selectedSetId is null
                ? "Сначала выберите набор в библиотеке."
                : "Этот вариант уже является обложкой набора.";
            return false;
        }

        var variantId = CurrentVariant!.Id;
        if (!MutateSet(() => _assetCatalog!.SetCoverVariant(_selectedSetId!, variantId)))
        {
            return false;
        }

        SelectVariantById(variantId);
        StatusDetail = $"Обложкой набора стал вариант {CurrentVariantLabel}.";
        return true;
    }

    /// <summary>Runs one catalog mutation and rebuilds the panel, or reports why it failed.</summary>
    private bool MutateSet(Action mutation)
    {
        try
        {
            mutation();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException
            or ArgumentException or KeyNotFoundException)
        {
            StatusDetail = "Не удалось изменить набор: " + ex.Message;
            _log.Error("Asset set change failed", ex);
            return false;
        }

        var setId = _selectedSetId;
        RefreshLibrary();
        SelectAssetSet(setId);
        Raise(nameof(SelectedSet));
        RaiseSetManagementState();
        return true;
    }

    private void SelectVariantById(string variantId)
    {
        for (var index = 0; index < _selectedSetVariants.Count; index++)
        {
            if (string.Equals(_selectedSetVariants[index].Id, variantId, StringComparison.Ordinal))
            {
                _currentVariantIndex = index;
                break;
            }
        }

        RememberCurrentVariant();
        RaiseVariantState();
        RaiseSetManagementState();
    }

    private void RaiseSetManagementState()
    {
        Raise(nameof(CanRenameSelectedSet));
        Raise(nameof(CanMakeCurrentVariantCover));
    }

    // ---------------------------------------------------------------- availability

    /// <summary>What the library currently knows about one content file.</summary>
    public AssetAvailability AvailabilityOf(string? sha256) =>
        sha256 is not null && _availability.TryGetValue(sha256, out var state)
            ? state
            : AssetAvailability.Unknown;

    /// <summary>Bumped whenever a verdict lands, so the panel knows to redraw its badges.</summary>
    public int AvailabilityRevision => _availabilityRevision;

    public static string AvailabilityText(AssetAvailability availability) => availability switch
    {
        AssetAvailability.Available => "файл на месте",
        AssetAvailability.Missing => "файл отсутствует",
        AssetAvailability.HashMismatch => "файл не совпадает с сохранённым SHA-256",
        AssetAvailability.Unsupported => "формат не поддерживается",
        AssetAvailability.Checking => "проверка файла…",
        _ => "файл не проверен",
    };

    /// <summary>
    /// Verifies the content behind the rows that are actually on screen and behind the set the
    /// stamp tool holds - and nothing else. Hashing happens off the UI thread, and a verdict from
    /// a query the user has already moved on from is discarded rather than shown.
    /// </summary>
    public async Task RefreshVisibleAvailabilityAsync()
    {
        if (_assetCatalog is null || _assetResolver is null
            || _assetCatalog.Status == AssetLibraryStatus.Damaged)
        {
            return;
        }

        var pending = new List<AssetRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tile in _visibleTiles.Tiles)
        {
            Consider(tile.CoverSha256);
        }

        if (CurrentVariant is { } variant) Consider(variant.Sha256);

        if (pending.Count == 0)
        {
            return;
        }

        foreach (var record in pending)
        {
            _availability[record.Sha256] = AssetAvailability.Checking;
        }

        var generation = ++_availabilityGeneration;
        BumpAvailability();

        var resolver = _assetResolver;
        var verdicts = await Task.Run(
            () => pending.Select(record => (record.Sha256, State: resolver.Check(record))).ToList());

        // The query moved on while we were hashing: this answer describes rows nobody is looking
        // at any more, and writing it back would show a stale state.
        if (generation != _availabilityGeneration)
        {
            return;
        }

        foreach (var (sha256, state) in verdicts)
        {
            _availability[sha256] = state;
        }

        BumpAvailability();

        void Consider(string? sha256)
        {
            if (string.IsNullOrEmpty(sha256) || !seen.Add(sha256)
                || AvailabilityOf(sha256) != AssetAvailability.Unknown)
            {
                return;
            }

            if (_assetCatalog.Find(sha256) is { } record)
            {
                pending.Add(record);
            }
        }
    }

    private void ForgetAvailability()
    {
        if (_availability.Count == 0)
        {
            return;
        }

        _availability.Clear();
        _availabilityGeneration++;
        BumpAvailability();
    }

    private void BumpAvailability()
    {
        _availabilityRevision++;
        Raise(nameof(AvailabilityRevision));
    }

    /// <summary>
    /// Every content hash the open map really reaches: stamps, line and shape strokes, shape
    /// texture fills and nested composition items all come from the one walk the composition
    /// transfer already defines, and filter textures are added on top of it.
    /// </summary>
    private HashSet<string> UsedContentHashes()
    {
        var objects = Session.Document.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects);
        var hashes = new HashSet<string>(
            CompositionTransfer.UsedAssetHashes(objects), StringComparer.OrdinalIgnoreCase);
        foreach (var filter in Session.Document.Layers.OfType<FilterLayer>())
        {
            if (filter.Settings.TextureAssetSha256 is { Length: > 0 } texture)
            {
                hashes.Add(texture);
            }
        }

        return hashes;
    }

    /// <summary>How many entries the bounded recent list currently holds.</summary>
    public int RecentCount => _assetCatalog?.Recent.Count ?? 0;

    private long _compactMetadataRevision = -1;
    private long _compactContentRevision = -1;
    private IReadOnlyList<AssetSetSummary> _compactRecentSets = [];
    private IReadOnlyList<AssetSetSummary> _compactFavoriteSets = [];
    public IReadOnlyList<AssetSetSummary> CompactRecentSets { get { RefreshCompactSets(); return _compactRecentSets; } }
    public IReadOnlyList<AssetSetSummary> CompactFavoriteSets { get { RefreshCompactSets(); return _compactFavoriteSets; } }
    private void RefreshCompactSets()
    {
        if (_assetCatalog is null || _compactMetadataRevision == _assetCatalog.MetadataRevision) return;
        _compactMetadataRevision = _assetCatalog.MetadataRevision;
        _compactRecentSets = _assetCatalog.Recent.Select(e => _assetCatalog.SummarizeSet(e.SetId))
            .OfType<AssetSetSummary>().ToArray();
        if (_compactContentRevision == _assetCatalog.ContentRevision) return;
        _compactContentRevision = _assetCatalog.ContentRevision;
        _compactFavoriteSets = _assetCatalog.Sets.Where(s => s.Favorite)
            .Select(s => _assetCatalog.SummarizeSet(s.Id)).OfType<AssetSetSummary>().ToArray();
    }

    // ---------------------------------------------------------------- set and variant

    public string? SelectedSetId => _selectedSetId;

    public AssetSetSummary? SelectedSet =>
        _selectedSetId is null ? null : _assetCatalog?.SummarizeSet(_selectedSetId);

    public IReadOnlyList<AssetVariant> SelectedSetVariants => _selectedSetVariants;

    public int CurrentVariantIndex => _currentVariantIndex;

    public AssetVariant? CurrentVariant =>
        _currentVariantIndex >= 0 && _currentVariantIndex < _selectedSetVariants.Count
            ? _selectedSetVariants[_currentVariantIndex]
            : null;

    /// <summary>The tool's own position readout, for example "3 / 12".</summary>
    public string CurrentVariantLabel => _selectedSetVariants.Count == 0
        ? ""
        : $"{_currentVariantIndex + 1} / {_selectedSetVariants.Count}";

    public bool IsCurrentVariantTheCover =>
        SelectedSet is { } set && CurrentVariant is { } variant && set.CoverVariantId == variant.Id;

    public StampToolSettings StampToolSettings => _stampToolSettings;

    public bool IsEditingFutureStamp =>
        ActiveTool == EditorTool.Stamp && (PendingPlacement?.Target == PlacementTargetKind.Stamp
            || SelectedEditablePath is not null || IsCreatingEditablePath
            || SelectedEditableArea is not null || IsCreatingEditableArea);

    public string StampPanelHeading =>
        ActiveTool == EditorTool.Stamp && SelectedEditableArea is not null ? "Параметры области — примените кнопкой ниже"
            : ActiveTool == EditorTool.Stamp && SelectedEditablePath is not null ? "Параметры пути — примените кнопкой ниже"
            : IsEditingFutureStamp ? "Ассет для размещения" : "Свойства ассета";

    public StampStyle? StampPanelStyle => IsEditingFutureStamp
        ? _stampToolSettings.Style
        : Session.SelectedObject?.Stamp;

    private AssetVariant? StampPanelVariant
    {
        get
        {
            if (IsEditingFutureStamp) return CurrentVariant;
            if (_assetCatalog is null || Session.SelectedObject?.Stamp is not { } stamp) return null;
            return _assetCatalog.Variants.FirstOrDefault(variant =>
                       string.Equals(variant.Sha256, stamp.AssetSha256, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(variant.MaskSha256, stamp.MaskAssetSha256,
                           StringComparison.OrdinalIgnoreCase))
                   ?? _assetCatalog.Variants.FirstOrDefault(variant =>
                       string.Equals(variant.Sha256, stamp.AssetSha256, StringComparison.OrdinalIgnoreCase));
        }
    }

    public string? StampPanelMaskSha256 =>
        StampPanelStyle?.MaskAssetSha256 ?? StampPanelVariant?.MaskSha256;

    public string? StampClipMaskThumbnailPath
    {
        get
        {
            if (_assetCatalog?.Find(StampPanelMaskSha256 ?? "") is not { } record) return null;
            return _assetCatalog.TryThumbnailPath(record, out var path) ? path : null;
        }
    }

    public string StampClipMaskStatus
    {
        get
        {
            if (StampPanelStyle is not { } style) return "Недоступно — ассет не выбран";
            var maskSha = StampPanelMaskSha256;
            if (maskSha is null) return "Недоступно — у варианта нет маски";
            if (!IsMaskAvailable(maskSha)) return "Маска отсутствует или повреждена";
            return style.ClipMaskEnabled ? "Включено" : "Выключено";
        }
    }

    public bool StampClipMaskCanToggle => StampPanelMaskSha256 is { } sha && IsMaskAvailable(sha);

    public bool StampPanelCanEditLibraryMask => StampPanelVariant is not null;

    public bool StampPanelVariantHasLibraryMask => StampPanelVariant?.MaskSha256 is not null;

    public string StampForegroundStatus => Session.Document.StampForeground switch
    {
        null => "Foreground-поверхность не создана",
        { IsEmpty: true } => "Foreground-поверхность пуста",
        _ => "Foreground-поверхность готова",
    };

    /// <summary>True once the user has chosen an asset, so placement has something to place.</summary>
    public bool HasChosenAsset => _selectedSetId is not null;

    /// <summary>Makes one logical card current and restores the variant last used inside it.</summary>
    public void SelectAssetSet(string? setId) => SelectAssetSet(setId, armPlacement: true);

    private void SelectAssetSet(string? setId, bool armPlacement)
    {
        if (setId is null || _assetCatalog is null || _assetCatalog.FindSet(setId) is not { } set)
        {
            _selectedSetId = null;
            _selectedSetVariants = [];
            _currentVariantIndex = 0;
            _stampToolSettings = _stampToolSettings with
            {
                AssetSetId = null,
                VariantId = null,
                Style = null,
            };
            SyncSelectedLibraryRow();
            RaiseVariantState();
            RaiseStampToolState();
            return;
        }

        _selectedSetId = set.Id;
        _selectedSetVariants =
        [
            .. set.VariantIds.Select(_assetCatalog.FindVariant).Where(variant => variant is not null)
                .Select(variant => variant!),
        ];

        var remembered = _assetCatalog.CurrentVariantOf(set.Id);
        var index = remembered is null
            ? -1
            : _selectedSetVariants.ToList().FindIndex(variant => variant.Id == remembered);
        var coverIndex = _selectedSetVariants.ToList().FindIndex(variant => variant.Id == set.CoverVariantId);
        _currentVariantIndex = index >= 0 ? index : Math.Max(0, coverIndex);
        RememberCurrentVariant();
        BindStampToolToCurrentVariant();
        SyncSelectedLibraryRow();
        RaiseVariantState();
        RaiseStampToolState();
        PersistLocalPreferences();
        if (armPlacement) ArmStampPlacement();
    }

    public void NextVariant() => MoveVariant(1);

    public IReadOnlyList<AssetVariant> VariantsForSet(string setId) => _assetCatalog?.FindSet(setId) is { } set
        ? set.VariantIds.Select(_assetCatalog.FindVariant).OfType<AssetVariant>().ToArray() : [];

    public void PreviousVariant() => MoveVariant(-1);

    /// <summary>Validate a gallery action before changing the tool it will replace.</summary>
    public bool ChooseGalleryVariant(string setId, string? variantId, bool armPlacement = true)
    {
        if (_assetCatalog?.FindSet(setId) is not { } set || _assetResolver is null)
        { StatusDetail = "Набор больше не доступен в каталоге."; return false; }
        var candidates = variantId is not null ? new[] { variantId }
            : new[] { _assetCatalog.CurrentVariantOf(setId), set.CoverVariantId }
                .Concat(set.VariantIds).OfType<string>().Distinct().ToArray();
        foreach (var id in candidates)
        {
            if (!set.VariantIds.Contains(id) || _assetCatalog.FindVariant(id) is not { } variant
                || _assetCatalog.Find(variant.Sha256) is not { } record) continue;
            var embedded = Session.Document.Assets.Any(a => a.Sha256.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase));
            if (!embedded && _assetResolver.Check(record) != AssetAvailability.Available) continue;
            SelectAssetSet(setId, armPlacement: false);
            SelectVariantAt(_selectedSetVariants.ToList().FindIndex(v => v.Id == id), armPlacement);
            return true;
        }
        StatusDetail = "Вариант недоступен: проверьте исходный файл или выберите другой.";
        return false;
    }

    public void SelectVariantAt(int index) => SelectVariantAt(index, armPlacement: true);

    private void SelectVariantAt(int index, bool armPlacement)
    {
        if (_selectedSetVariants.Count == 0)
        {
            return;
        }

        _newAreaTemplate = null; _selectedAreaId = null; CancelEditableAreaGesture();
        _currentVariantIndex = Math.Clamp(index, 0, _selectedSetVariants.Count - 1);
        _stampToolSettings = _stampToolSettings with { RandomizeVariant = false };
        RememberCurrentVariant();
        BindStampToolToCurrentVariant();
        RaiseVariantState();
        RaiseStampToolState();
        PersistLocalPreferences();
        if (armPlacement) ArmStampPlacement();
    }

    private void MoveVariant(int direction)
    {
        if (_selectedSetVariants.Count <= 1)
        {
            StatusDetail = _selectedSetVariants.Count == 0
                ? "Сначала выберите набор в библиотеке."
                : "В этом наборе один вариант.";
            return;
        }

        var count = _selectedSetVariants.Count;
        _currentVariantIndex = ((_currentVariantIndex + direction) % count + count) % count;
        RememberCurrentVariant();
        BindStampToolToCurrentVariant();
        StatusDetail = $"Вариант {CurrentVariantLabel}.";
        RaiseVariantState();
        RaiseStampToolState();
        PersistLocalPreferences();
        RefreshArmedPlacement();
    }

    private void RememberCurrentVariant()
    {
        if (_assetCatalog is null || _selectedSetId is null || CurrentVariant is not { } variant
            || _assetCatalog.Status == AssetLibraryStatus.Damaged)
        {
            return;
        }

        _assetCatalog.RememberVariant(_selectedSetId, variant.Id);
    }

    private void RestoreStampCatalogIdentity()
    {
        if (_assetCatalog is null || _stampToolSettings.AssetSetId is not { } setId
            || _assetCatalog.FindSet(setId) is not { } set)
        {
            return;
        }

        _selectedSetId = set.Id;
        _selectedSetVariants =
        [
            .. set.VariantIds.Select(_assetCatalog.FindVariant).Where(item => item is not null)
                .Select(item => item!),
        ];
        var wanted = _stampToolSettings.VariantId;
        var index = wanted is null ? -1 : _selectedSetVariants.ToList().FindIndex(item => item.Id == wanted);
        _currentVariantIndex = index >= 0 ? index : 0;
        BindStampToolToCurrentVariant();
        SyncSelectedLibraryRow();
        RaiseVariantState();
        RaiseStampToolState();
    }

    private void BindStampToolToCurrentVariant()
    {
        if (CurrentVariant is not { } variant || _assetCatalog?.Find(variant.Sha256) is not { } record)
        {
            return;
        }

        var referenceWidth = DefaultStampReferenceWidth();
        var referenceHeight = referenceWidth * record.Height / Math.Max(1.0, record.Width);
        var before = _stampToolSettings.Style;
        var resolvedMask = variant.MaskSha256;
        _stampToolSettings = _stampToolSettings with
        {
            AssetSetId = _selectedSetId,
            VariantId = variant.Id,
            Style = new StampStyle(variant.Sha256)
            {
                ReferenceWidth = referenceWidth,
                ReferenceHeight = referenceHeight,
                FlipHorizontal = before?.FlipHorizontal ?? false,
                FlipVertical = before?.FlipVertical ?? false,
                MaskAssetSha256 = resolvedMask,
                ClipMaskEnabled = resolvedMask is not null && (before?.ClipMaskEnabled ?? false),
                Blend = before?.Blend ?? BlendModes.Normal,
                Blur = before?.Blur ?? 0,
                ShadowOffsetX = before?.ShadowOffsetX ?? ShadowPreset.StandardOffsetX,
                ShadowOffsetY = before?.ShadowOffsetY ?? ShadowPreset.StandardOffsetY,
                ShadowBlur = before?.ShadowBlur ?? ShadowPreset.StandardBlur,
                ShadowOpacity = before?.ShadowOpacity ?? 0,
                ShadowPlacement = before?.ShadowPlacement ?? StampShadowPlacement.Object,
                ShadowColor = before?.ShadowColor ?? AppearanceColor.OpaqueBlack,
                Hue = before?.Hue ?? 0,
                Saturation = before?.Saturation ?? 0,
                Brightness = before?.Brightness ?? 0,
                Contrast = before?.Contrast ?? 0,
            },
        };
    }

    private void RefreshSelectedVariantsFromCatalog()
    {
        if (_assetCatalog is null || _selectedSetId is not { } setId
            || _assetCatalog.FindSet(setId) is not { } set)
        {
            return;
        }

        var currentId = CurrentVariant?.Id;
        _selectedSetVariants = [.. set.VariantIds.Select(_assetCatalog.FindVariant)
            .Where(variant => variant is not null).Select(variant => variant!)];
        var index = currentId is null ? -1 : _selectedSetVariants.ToList()
            .FindIndex(variant => variant.Id == currentId);
        _currentVariantIndex = index >= 0 ? index : Math.Clamp(_currentVariantIndex, 0,
            Math.Max(0, _selectedSetVariants.Count - 1));
    }

    public bool AssignCurrentVariantMask(string path)
    {
        if (_assetCatalog is null || StampPanelVariant is not { } variant)
        {
            StatusDetail = "Сначала выберите вариант ассета.";
            return false;
        }

        try
        {
            _assetCatalog.AssignVariantMask(variant.Id, path, _clock());
            RefreshSelectedVariantsFromCatalog();
            if (IsEditingFutureStamp && CurrentVariant?.Id == variant.Id)
            {
                BindStampToolToCurrentVariant();
                PersistLocalPreferences();
                RefreshArmedPlacement();
            }
            RaiseVariantState();
            RaiseStampToolState();
            StatusDetail = variant.MaskSha256 is null ? "Маска назначена." : "Маска заменена.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StatusDetail = "Не удалось назначить маску: " + ex.Message;
            _log.Error("Variant clip mask assignment failed", ex);
            return false;
        }
    }

    public bool RemoveCurrentVariantMask()
    {
        if (_assetCatalog is null || StampPanelVariant is not { } variant)
        {
            StatusDetail = "Сначала выберите вариант ассета.";
            return false;
        }

        try
        {
            _assetCatalog.RemoveVariantMask(variant.Id);
            RefreshSelectedVariantsFromCatalog();
            if (IsEditingFutureStamp && CurrentVariant?.Id == variant.Id)
            {
                BindStampToolToCurrentVariant();
                PersistLocalPreferences();
                RefreshArmedPlacement();
            }
            RaiseVariantState();
            RaiseStampToolState();
            StatusDetail = "Маска удалена из варианта. Уже размещённые объекты не изменены.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException)
        {
            StatusDetail = "Не удалось удалить маску: " + ex.Message;
            _log.Error("Variant clip mask removal failed", ex);
            return false;
        }
    }

    public void SetStampClipMaskEnabled(bool enabled)
    {
        if (StampPanelStyle is not { } style)
        {
            return;
        }

        if (IsEditingFutureStamp)
        {
            var maskSha = CurrentVariant?.MaskSha256;
            if (enabled && (maskSha is null || !IsMaskAvailable(maskSha)))
            {
                StatusDetail = maskSha is null
                    ? "Недоступно — у варианта нет маски."
                    : "Маска отсутствует или повреждена.";
                return;
            }
            UpdateStampToolSettings(settings => settings with
            {
                Style = style with
                {
                    MaskAssetSha256 = maskSha,
                    ClipMaskEnabled = enabled,
                },
            }, persist: true);
            return;
        }

        if (Session.SelectedObject is not { Stamp: { } selectedStyle } selected)
        {
            return;
        }
        if (!enabled)
        {
            EditStamp("Disable stamp clip mask", commit: true,
                stamp => stamp with { ClipMaskEnabled = false });
            return;
        }

        var resolvedMask = selectedStyle.MaskAssetSha256 ?? StampPanelVariant?.MaskSha256;
        if (resolvedMask is null)
        {
            StatusDetail = "Недоступно — у варианта нет маски.";
            return;
        }
        var maskReference = ResolveMaskForDocument(resolvedMask, out var refusal);
        if (maskReference is null)
        {
            StatusDetail = refusal ?? "Маска отсутствует или повреждена.";
            return;
        }

        Session.Execute(new UpdateStampStyleWithAssetCommand(
            selected,
            selectedStyle with { MaskAssetSha256 = resolvedMask, ClipMaskEnabled = true },
            maskReference));
        AfterEdit();
    }

    public void CreateStampForegroundSurface()
    {
        if (Session.Document.StampForeground is not null)
        {
            StatusDetail = StampForegroundStatus;
            return;
        }
        Session.Execute(new UpdateStampForegroundCommand(
            null, StampForegroundSurface.CreateEmpty(), "Create stamp foreground"));
        AfterEdit();
        RaiseStampToolState();
        StatusDetail = "Foreground-поверхность создана. Выберите её редактирование и рисуйте кистью.";
    }

    public void EditStampForegroundSurface()
    {
        if (Session.Document.StampForeground is null)
        {
            StatusDetail = "Сначала создайте foreground-поверхность.";
            return;
        }
        ActiveTool = EditorTool.Brush;
        _editingStampForeground = true;
        Raise(nameof(IsEditingStampForeground));
        StatusDetail = "Редактируется foreground-поверхность: рисуйте обычной кистью на холсте.";
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    private bool IsMaskAvailable(string sha256)
    {
        if (Session.Document.Assets.Any(asset =>
                string.Equals(asset.Sha256, sha256, StringComparison.OrdinalIgnoreCase)))
        {
            return StampImages?.TryGetImage(sha256) is not null;
        }
        if (_assetCatalog?.Find(sha256) is not { } record) return false;
        try
        {
            return _assetCatalog.Resolve(record).Status == AssetResolutionStatus.Available;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private MapAssetReference? ResolveMaskForDocument(string sha256, out string? refusal)
    {
        var existing = Session.Document.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            refusal = StampImages?.TryGetImage(sha256) is null
                ? "Маска отсутствует или повреждена."
                : null;
            return refusal is null ? existing : null;
        }
        if (_assetCatalog?.Find(sha256) is not { } record)
        {
            refusal = "Маска отсутствует в Library.";
            return null;
        }
        var result = ResolveAssetForDocument(record, out _, out refusal);
        return result;
    }

    /// <summary>Favorite belongs to the logical card, not to one variant's bytes.</summary>
    public void ToggleSelectedSetFavorite()
    {
        if (_assetCatalog is null || SelectedSet is not { } set)
        {
            StatusDetail = "Сначала выберите набор в библиотеке.";
            return;
        }

        try
        {
            _assetCatalog.SetSetFavorite(set.SetId, !set.Favorite);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            StatusDetail = "Не удалось изменить избранное: " + ex.Message;
            return;
        }

        RefreshLibrary();
        Raise(nameof(SelectedSet));
    }

    /// <summary>Points the library at the set that owns a placed stamp, found back by hash.</summary>
    public void SyncLibraryToSelection(string? sha256)
    {
        if (_assetCatalog is null || string.IsNullOrWhiteSpace(sha256))
        {
            return;
        }

        if (_assetCatalog.FindSetForContent(sha256) is not { } set)
        {
            // The map is self-sufficient: a stamp whose set is gone still renders and exports.
            StatusDetail = "Этот ассет не найден в локальном каталоге; объект работает как раньше.";
            return;
        }

        SelectAssetSet(set.Id);
        var index = _selectedSetVariants.ToList().FindIndex(variant =>
            string.Equals(variant.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _currentVariantIndex = index;
            RaiseVariantState();
            RefreshArmedPlacement();
        }
    }

    // ---------------------------------------------------------------- area and density

    public StampPlacementMode StampPlacementMode
    {
        get => _stampToolSettings.Mode;
        set
        {
            if (_stampToolSettings.Mode != value && Enum.IsDefined(value))
            {
                var leavingPath = _selectedPathId is not null && value != StampPlacementMode.Path;
                var leavingArea = _selectedAreaId is not null && value != StampPlacementMode.FillArea;
                CancelEditablePathGesture(); CancelEditableAreaGesture();
                CancelStampSeries();
                _stampToolSettings = _stampToolSettings with { Mode = value };
                if (leavingPath) { _selectedPathId = null; ArmStampPlacement(); RaisePathState(); }
                if (leavingArea) { _selectedAreaId = null; _newAreaTemplate = null; ArmStampPlacement(); RaiseAreaState(); }
                Raise();
                Raise(nameof(PlannedStampCount));
                Raise(nameof(StampAreaSummary));
                RefreshArmedPlacement();
                PersistLocalPreferences();
            }
        }
    }

    public PlacementAutoSelectPolicy PlacementAutoSelect
    {
        get => _stampToolSettings.AutoSelect;
        set
        {
            if (_stampToolSettings.AutoSelect != value && Enum.IsDefined(value))
            {
                _stampToolSettings = _stampToolSettings with { AutoSelect = value };
                Raise();
                PersistLocalPreferences();
                if (_pendingPlacement is { } pending)
                {
                    PendingPlacement = pending with { AutoSelect = value };
                }
            }
        }
    }

    /// <summary>Gesture radius in world units.</summary>
    public double StampAreaRadius
    {
        get => _stampToolSettings.AreaRadius;
        set
        {
            var bounded = double.IsFinite(value)
                ? Math.Clamp(value, MinStampAreaRadius, MaxStampAreaRadius)
                : _stampToolSettings.AreaRadius;
            if (_stampToolSettings.AreaRadius != bounded)
            {
                _stampToolSettings = _stampToolSettings with { AreaRadius = bounded };
                Raise();
                Raise(nameof(PlannedStampCount));
                Raise(nameof(StampAreaInCells));
                Raise(nameof(StampAreaSummary));
                RefreshArmedPlacement();
            }
        }
    }

    public void SetStampAreaRadius(double value, bool persist)
    {
        StampAreaRadius = value;
        if (persist) PersistLocalPreferences();
    }

    /// <summary>Stamps per 10 000 square world units.</summary>
    public double StampDensity
    {
        get => _stampToolSettings.Density;
        set
        {
            var bounded = double.IsFinite(value)
                ? Math.Clamp(value, MinStampDensity, MaxStampDensity)
                : _stampToolSettings.Density;
            if (_stampToolSettings.Density != bounded)
            {
                _stampToolSettings = _stampToolSettings with { Density = bounded };
                Raise();
                Raise(nameof(PlannedStampCount));
                Raise(nameof(StampAreaSummary));
                RefreshArmedPlacement();
            }
        }
    }

    public void SetStampDensity(double value, bool persist)
    {
        StampDensity = value;
        if (persist) PersistLocalPreferences();
    }

    public bool RandomVariantAfterPlacement
    {
        get => _stampToolSettings.RandomizeVariant;
        set
        {
            if (_stampToolSettings.RandomizeVariant != value)
            {
                _stampToolSettings = _stampToolSettings with { RandomizeVariant = value };
                Raise();
                RefreshArmedPlacement();
                PersistLocalPreferences();
            }
        }
    }

    public StampRandomAngleStep StampRandomAngleStep
    {
        get => _stampToolSettings.RandomAngleStep;
        set
        {
            if (_stampToolSettings.RandomAngleStep != value && IsSupportedRandomAngleStep(value))
            {
                _stampToolSettings = _stampToolSettings with { RandomAngleStep = value };
                Raise();
                Raise(nameof(StampRandomAngle));
                RefreshArmedPlacement();
                PersistLocalPreferences();
            }
        }
    }

    public bool StampRandomAngle
    {
        get => StampRandomAngleStep != Map.Core.Editing.StampRandomAngleStep.Off;
        set => StampRandomAngleStep = value
            ? Map.Core.Editing.StampRandomAngleStep.Degrees15
            : Map.Core.Editing.StampRandomAngleStep.Off;
    }

    public bool StampRandomFlip
    {
        get => _stampToolSettings.RandomFlip;
        set
        {
            if (_stampToolSettings.RandomFlip != value)
            {
                _stampToolSettings = _stampToolSettings with { RandomFlip = value };
                Raise();
                RefreshArmedPlacement();
                PersistLocalPreferences();
            }
        }
    }

    /// <summary>How many stamps the current Area and Density would place.</summary>
    public int PlannedStampCount => StampPlacementMode is StampPlacementMode.One or StampPlacementMode.Series
        ? 1
        : StampPlacement.CountFor(StampDensity, StampAreaRadius);

    /// <summary>The same radius in cells of the active snap grid, or null without one.</summary>
    public double? StampAreaInCells =>
        ActiveSnapGrid() is { } grid ? StampPlacement.RadiusInCells(StampAreaRadius, grid.CellSize) : null;

    public string StampAreaSummary
    {
        get
        {
            var area = StampAreaInCells is { } cells
                ? $"Область {StampAreaRadius:0} ед. ({cells:0.##} кл.)"
                : $"Область {StampAreaRadius:0} ед.";
            return StampPlacementMode switch
            {
                StampPlacementMode.One => area + " · один ассет",
                StampPlacementMode.Series => area + " · серия по одному ассету",
                _ => $"{area} · плотность {StampDensity:0} на 10 000 ед² · {PlannedStampCount} шт.",
            };
        }
    }

    public void AdjustStampArea(int direction, bool largerStep = false)
    {
        if (direction == 0)
        {
            return;
        }

        StampAreaRadius += Math.Sign(direction) * (largerStep ? 10 : 1);
        StatusDetail = StampAreaSummary;
    }

    public void AdjustStampDensity(int direction, bool largerStep = false)
    {
        if (direction == 0)
        {
            return;
        }

        StampDensity += Math.Sign(direction) * (largerStep ? 10 : 1);
        StatusDetail = StampAreaSummary;
    }

    // ---------------------------------------------------------------- placement

    private const double PlacementPreviewOpacity = 0.55;
    private const int MaximumScatterPreviewObjects = 64;
    private const double PlacementScaleStep = 0.01;
    private const double PlacementScaleLargeStep = 0.1;
    private const double PlacementRotationStep = 1;
    private const double PlacementRotationLargeStep = 15;

    public PlacementSession? PendingPlacement
    {
        get => _pendingPlacement;
        private set
        {
            var before = _pendingPlacement;
            if (SetField(ref _pendingPlacement, value))
            {
                if ((before is null) != (value is null)) Raise(nameof(HasPendingPlacement));
                Raise(nameof(PlacementPreviewObjects));
                if (before?.InvalidReason != value?.InvalidReason) Raise(nameof(PlacementRefusal));
                if (before?.Target != value?.Target) RaiseStampToolState();
                InvalidateScene(EditorChangeReason.CanvasInvalidation);
            }
        }
    }

    public bool HasPendingPlacement => _pendingPlacement is not null;

    public IReadOnlyList<SceneObject> PlacementPreviewObjects => HasAreaGesture ? _areaPreviewObjects : HasPathGesture ? _pathPreviewObjects
        : _pendingPlacement is { PointerVisible: true } pending
        ? pending.PreviewObjects
        : _vectorPreviewObject is { } vector ? [vector]
        : _textPreviewObject is { } text ? [text]
        : _shapeBooleanPreviewObject is { } boolean ? [boolean]
        : [];

    public LayerId? PlacementPreviewLayerId => _areaLayer ?? _pathLayer ?? _pendingPlacement?.LayerId
        ?? _vectorPreviewLayerId ?? _textPreviewLayerId ?? _pendingShapeBoolean?.LayerId;

    public string PlacementRefusal => _pendingPlacement?.InvalidReason ?? "";

    public void ArmPrimitivePlacement(SceneObjectKind kind)
    {
        if (kind is not (SceneObjectKind.Rectangle or SceneObjectKind.Ellipse))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ActiveTool = EditorTool.Select;
        var centre = ViewCentre();
        var mode = StampPlacementMode == StampPlacementMode.Area
            ? StampPlacementMode.One
            : StampPlacementMode;
        PendingPlacement = BuildPrimitivePlacement(kind, centre.X, centre.Y, mode, pointerVisible: false);
        StatusDetail = kind == SceneObjectKind.Ellipse
            ? "Объект: эллипс · укажите место на холсте."
            : "Объект: прямоугольник · укажите место на холсте.";
    }

    public bool ArmStampPlacement()
    {
        CancelStampSeries();
        ActiveTool = EditorTool.Stamp;
        var centre = ViewCentre();
        PendingPlacement = BuildStampPlacement(centre.X, centre.Y, pointerVisible: false);
        if (PendingPlacement.InvalidReason is { Length: > 0 } refusal)
        {
            StatusDetail = refusal;
            return false;
        }

        StatusDetail = "Ассет выбран · укажите место на холсте.";
        return true;
    }

    private PlacementSession? _seriesOrigin;
    private readonly List<StampPlacementItem> _seriesPlan = [];
    private StampPlacementRequest? _seriesRequest;
    private IReadOnlyList<StampVariantSource> _seriesSources = [];
    private double _seriesLastX, _seriesLastY, _seriesRemaining, _seriesStep;
    public bool IsStampSeriesActive => _seriesOrigin is not null;
    public double SeriesSpacingPercent => _stampToolSettings.SeriesSpacingPercent;
    public void SetSeriesSpacingPercent(double value, bool persist)
    {
        CancelStampSeries();
        _stampToolSettings = _stampToolSettings with { SeriesSpacingPercent = Math.Clamp(value, 10, 400) };
        Raise(nameof(SeriesSpacingPercent));
        Raise(nameof(StampToolSettings));
        if (persist) PersistLocalPreferences();
    }

    public bool BeginStampSeries(double worldX, double worldY)
    {
        CancelStampSeries();
        if (PendingPlacement is not { Target: PlacementTargetKind.Stamp, Mode: StampPlacementMode.Series, CanCommit: true } pending)
            return false;
        var settings = pending.StampSettingsSnapshot ?? _stampToolSettings;
        if (!TryStampInputs(settings, out var request, out var sources, out _, out var refusal))
        { StatusDetail = refusal; return false; }
        _seriesOrigin = pending;
        _seriesRequest = request with { AreaRadius = 0, Density = 0, Seed = pending.Seed };
        _seriesSources = sources;
        _seriesStep = Math.Max(1, request.BaseWidth * settings.SeriesSpacingPercent / 100);
        _seriesRemaining = _seriesStep;
        _seriesLastX = worldX;
        _seriesLastY = worldY;
        AddSeriesPoint(worldX, worldY);
        UpdateSeriesPreview();
        return true;
    }

    public void ContinueStampSeries(double worldX, double worldY)
    {
        if (_seriesOrigin is null) return;
        if (worldX < 0 || worldY < 0 || worldX > Session.Document.Canvas.Width || worldY > Session.Document.Canvas.Height)
        { CancelStampSeries(); return; }
        var dx = worldX - _seriesLastX;
        var dy = worldY - _seriesLastY;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0.000001) return;
        var distance = _seriesRemaining;
        while (distance <= length)
        {
            AddSeriesPoint(_seriesLastX + dx * distance / length, _seriesLastY + dy * distance / length);
            distance += _seriesStep;
        }
        _seriesRemaining = distance - length;
        _seriesLastX = worldX;
        _seriesLastY = worldY;
        UpdateSeriesPreview();
    }

    private void AddSeriesPoint(double x, double y)
    {
        var request = _seriesRequest! with { CenterX = x, CenterY = y,
            Seed = unchecked(_seriesRequest!.Seed + _seriesPlan.Count * 104729) };
        var item = StampPlacement.PlanArea(request, _seriesSources)[0];
        _seriesPlan.Add(ApplyPlacementRotation([item], _seriesOrigin!.RotationDegrees)[0]);
    }

    private void UpdateSeriesPreview()
    {
        if (_seriesOrigin is not { } pending) return;
        PendingPlacement = pending with
        {
            PlannedCount = _seriesPlan.Count,
            PreviewObjects = _seriesPlan.TakeLast(MaximumScatterPreviewObjects)
                .Select(item => StampObject(item, "Серия", pending.StampSettingsSnapshot!, PlacementPreviewOpacity)).ToArray(),
            WorldX = _seriesLastX, WorldY = _seriesLastY,
        };
    }

    public bool EndStampSeries()
    {
        if (_seriesOrigin is not { } origin) return false;
        var plan = _seriesPlan.ToArray();
        var pending = origin with { WorldX = _seriesLastX, WorldY = _seriesLastY };
        _seriesOrigin = null;
        _seriesPlan.Clear();
        var (layer, createLayer) = EnsureObjectLayer();
        if (layer is null || plan.Length == 0) { RefreshArmedPlacement(); return false; }
        return CommitStampPlacement(pending, layer, createLayer, plan);
    }

    public bool CancelStampSeries()
    {
        if (_seriesOrigin is not { } origin) return false;
        _seriesOrigin = null;
        _seriesPlan.Clear();
        _seriesSources = [];
        _seriesRequest = null;
        PendingPlacement = origin;
        return true;
    }

    public void UpdatePlacementPreview(double worldX, double worldY)
    {
        if (PendingPlacement is not { } pending)
        {
            return;
        }

        var snapped = SnapPlacementPoint(worldX, worldY);
        PendingPlacement = pending.Target switch
        {
            PlacementTargetKind.Rectangle or PlacementTargetKind.Ellipse =>
                RepositionPrimitivePlacement(pending, snapped.X, snapped.Y, pointerVisible: true),
            _ => BuildStampPlacement(
                snapped.X, snapped.Y, pointerVisible: true),
        };
    }

    public void SetPlacementPointerVisible(bool visible)
    {
        if (PendingPlacement is { } pending && pending.PointerVisible != visible)
        {
            PendingPlacement = pending with { PointerVisible = visible };
        }
    }

    /// <summary>
    /// Resizes the transient object around its own centre before placement. W/S therefore control
    /// the same size before and after placement without touching the document or history.
    /// </summary>
    public bool ResizePendingPlacement(int direction, bool largerStep = false)
    {
        if (PendingPlacement is not { } pending || direction == 0)
        {
            return false;
        }

        var delta = Math.Sign(direction)
            * (largerStep ? PlacementScaleLargeStep : PlacementScaleStep);
        if (pending.Target == PlacementTargetKind.Stamp)
        {
            SetStampScalePercent(_stampToolSettings.ScalePercent + (delta * 100), commit: true);
            StatusDetail = $"Размер перед установкой: {_stampToolSettings.ScalePercent:0.#}% · W/S.";
            return true;
        }

        var factor = MultiSelectionGeometry.ClampScaleFactor(pending.PreviewObjects, 1 + delta);
        var preview = MultiSelectionGeometry.ScaleAroundCentre(
            pending.PreviewObjects, factor, pending.WorldX, pending.WorldY);
        PendingPlacement = pending with
        {
            Width = preview[0].Width,
            Height = preview[0].Height,
            PreviewObjects = preview,
        };
        StatusDetail = $"Размер перед установкой: {preview[0].Width:0.#} × {preview[0].Height:0.#} · W/S.";
        return true;
    }

    /// <summary>
    /// Turns the transient object around its own centre before placement. The document, selection
    /// and history stay untouched; pointer moves keep the same angle and commit consumes the exact
    /// previewed rotation.
    /// </summary>
    public bool RotatePendingPlacement(int direction, bool largerStep = false)
    {
        if (PendingPlacement is not { } pending || direction == 0)
        {
            return false;
        }

        var delta = Math.Sign(direction)
            * (largerStep ? PlacementRotationLargeStep : PlacementRotationStep);
        if (pending.Target == PlacementTargetKind.Stamp)
        {
            SetStampRotationDegrees(_stampToolSettings.RotationDegrees + delta, commit: true);
            StatusDetail = $"Поворот перед установкой: {_stampToolSettings.RotationDegrees:0}° · A/D.";
            return true;
        }

        var rotation = NormalizeAngle(pending.RotationDegrees + delta);
        var preview = pending.PreviewObjects.Select(item => WithGeometry(
            item, item.X, item.Y, item.Width, item.Height,
            NormalizeAngle(item.RotationDegrees + delta))).ToArray();
        PendingPlacement = pending with
        {
            RotationDegrees = rotation,
            PreviewObjects = preview,
        };
        StatusDetail = $"Поворот перед установкой: {rotation:0}° · A/D.";
        return true;
    }

    public bool CancelPlacement()
    {
        var cancelledPath = CancelEditablePathGesture() | CancelEditableAreaGesture();
        CancelStampSeries();
        if (PendingPlacement is null)
        {
            return cancelledPath;
        }

        PendingPlacement = null;
        StatusDetail = "Размещение отменено.";
        return true;
    }

    public bool CreateObjectLayerAndContinuePlacement()
    {
        if (PendingPlacement is null)
        {
            return false;
        }

        AddObjectLayer();
        RefreshArmedPlacement();
        StatusDetail = "Создан слой объектов. Размещение можно продолжить.";
        return true;
    }

    public bool CommitPendingPlacement()
    {
        if (PendingPlacement is not { } pending)
        {
            return false;
        }

        var layer = pending.LayerId is { } layerId
            ? Session.Document.FindLayer(layerId) as ObjectLayer
            : null;
        AddLayerCommand? createLayer = null;
        if (layer is not { Visible: true, Locked: false })
        {
            // Putting a tree on the map must not begin with housekeeping. When the document has no
            // usable object layer we make one here and hand it to the commit, which folds it into
            // the same history entry, so one undo takes the object and the layer away together.
            (layer, createLayer) = EnsureObjectLayer();
            if (layer is null)
            {
                PendingPlacement = pending with { LayerId = null, InvalidReason = NoUsableObjectLayer };
                StatusDetail = NoUsableObjectLayer;
                return false;
            }
        }

        return pending.Target == PlacementTargetKind.Stamp
            ? CommitStampPlacement(pending, layer, createLayer)
            : CommitPrimitivePlacement(pending, layer, createLayer);
    }

    /// <summary>
    /// The object layer that receives new objects. Prefers the active layer, then the topmost
    /// visible unlocked object layer, and only otherwise describes a new one. The returned command
    /// is null when an existing layer was reused; when it is not null the caller MUST execute it as
    /// part of its own edit, because the returned layer is not in the document yet.
    /// </summary>
    private (ObjectLayer? Layer, AddLayerCommand? Create) EnsureObjectLayer()
    {
        if (ActiveObjectLayer() is { } existing)
        {
            return (existing, null);
        }

        // Object layers exist but the user hid or locked every one of them. That is a deliberate
        // decision about this map, and quietly adding a sibling layer would work around it, so the
        // caller refuses instead. Only a document with no object layer at all gets one made for it.
        if (!HasNoObjectLayer)
        {
            return (null, null);
        }

        var created = new ObjectLayer(
            LayerId.New(), EntityNameGenerator.Layer(Session.Document, typeof(ObjectLayer)),
            true, false, 1.0, []);
        return (created, new AddLayerCommand(created, _activeLayerId));
    }

    /// <summary>The map has no object layer at all - an unprepared document, not a locked one.</summary>
    private bool HasNoObjectLayer => !Session.Document.Layers.OfType<ObjectLayer>().Any();

    /// <summary>What to tell the user when every existing object layer refuses new objects.</summary>
    private const string NoUsableObjectLayer =
        "Слой объектов скрыт или заблокирован. Снимите блокировку или создайте новый слой объектов.";

    /// <summary>Runs an edit, together with the layer it needed, as exactly one history entry.</summary>
    private void ExecuteWithOptionalLayer(IEditCommand edit, AddLayerCommand? createLayer, string name) =>
        Session.Execute(createLayer is null ? edit : new CompositeCommand(name, [createLayer, edit]));

    private bool CommitPrimitivePlacement(
        PlacementSession pending, ObjectLayer layer, AddLayerCommand? createLayer)
    {
        var preview = pending.PreviewObjects.Single();
        var kind = pending.Target == PlacementTargetKind.Ellipse
            ? SceneObjectKind.Ellipse
            : SceneObjectKind.Rectangle;
        var added = new SceneObject(
            ObjectId.New(), kind, EntityNameGenerator.Object(Session.Document, kind),
            preview.X, preview.Y, preview.Width, preview.Height, preview.RotationDegrees,
            preview.FillColor, preview.StrokeColor, preview.StrokeWidth, true, false);
        var previousSelection = Session.SelectedObjectIds.ToArray();
        ExecuteWithOptionalLayer(
            new AddObjectCommand(layer.Id, added), createLayer, "Place object on a new layer");
        ApplyPlacementSelection(previousSelection, added.Id, pending);
        _activeLayerId = layer.Id;
        AfterEdit();
        FinishOrContinuePlacement(pending, added.Name);
        return true;
    }

    public (double Prepare, double Names, double Commit, double Metadata) LastPlacementTiming { get; private set; }

    private bool CommitStampPlacement(
        PlacementSession pending, ObjectLayer layer, AddLayerCommand? createLayer,
        IReadOnlyList<StampPlacementItem>? gesturePlan = null)
    {
        var timing = System.Diagnostics.Stopwatch.StartNew();
        var settings = pending.StampSettingsSnapshot ?? _stampToolSettings;
        if (!TryStampInputs(settings, out var requestTemplate, out var sources, out var records, out var refusal))
        {
            StatusDetail = refusal;
            return false;
        }

        var request = requestTemplate with
        {
            CenterX = pending.WorldX,
            CenterY = pending.WorldY,
            Seed = pending.Seed,
        };
        var plan = gesturePlan ?? (pending.Mode == StampPlacementMode.Area
            ? StampPlacement.PlanArea(request, sources)
            : StampPlacement.PlanOne(request, sources[0]));
        if (gesturePlan is null) plan = ApplyPlacementRotation(plan, settings.RotationDegrees);
        if (ResolvePlan(plan, records, out var references, out refusal) is not { } byHash)
        {
            StatusDetail = refusal;
            return false;
        }

        var preferredName = SelectedSet?.Name
            ?? records[plan[0].Sha256].DisplayName;
        var prepareMs = timing.Elapsed.TotalMilliseconds;
        var names = EntityNameGenerator.Objects(
            Session.Document, SceneObjectKind.Stamp, plan.Count, preferredName);
        var namesMs = timing.Elapsed.TotalMilliseconds - prepareMs;
        var objects = new List<SceneObject>(plan.Count);
        for (var index = 0; index < plan.Count; index++)
        {
            var item = plan[index];
            objects.Add(StampObject(item, names[index], settings, previewOpacityMultiplier: null));
        }

        var placedLabel = CurrentVariantLabel;
        var previousSelection = Session.SelectedObjectIds.ToArray();
        _gestureCounter++;
        ExecuteWithOptionalLayer(
            new PlaceAssetObjectsCommand(layer.Id, references, objects),
            createLayer, "Place stamp on a new layer");
        ApplyPlacementSelection(previousSelection, objects[^1].Id, pending);
        _activeLayerId = layer.Id;
        AfterEdit();
        var commitMs = timing.Elapsed.TotalMilliseconds - prepareMs - namesMs;
        if (_selectedSetId is not null && CurrentVariant is { } placedVariant)
        {
            _assetCatalog!.TouchRecent(_selectedSetId, placedVariant.Id, _clock());
        }

        if (RandomVariantAfterPlacement && _selectedSetVariants.Count > 1)
        {
            _currentVariantIndex = StampPlacement.NextVariantIndex(
                request.Seed, _currentVariantIndex, _selectedSetVariants.Count);
            RememberCurrentVariant();
            RaiseVariantState();
        }

        var placementMessage = objects.Count == 1
            ? $"Размещён вариант {placedLabel}."
            : $"Размещено объектов: {objects.Count}, вариантов: {plan.Select(item => item.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count()}.";
        Raise(nameof(CompactRecentSets));
        Raise(nameof(RecentCount));
        FinishOrContinuePlacement(pending, objects[^1].Name);
        StatusDetail = placementMessage + " Ассет остаётся активным · Esc завершить.";
        if (_assetCatalog?.MetadataSaveError is not null)
            StatusDetail += " Объекты размещены; недавние пока не удалось сохранить на диск.";
        LastPlacementTiming = (prepareMs, namesMs, commitMs,
            timing.Elapsed.TotalMilliseconds - prepareMs - namesMs - commitMs);
        return true;
    }

    private void ApplyPlacementSelection(
        IReadOnlyList<ObjectId> previousSelection, ObjectId addedId, PlacementSession placement)
    {
        var select = placement.AutoSelect == PlacementAutoSelectPolicy.Always
            || placement.AutoSelect == PlacementAutoSelectPolicy.Auto
                && placement.Target != PlacementTargetKind.Stamp
                && placement.Mode == StampPlacementMode.One;
        if (select)
        {
            Session.SelectedObjectId = addedId;
        }
        else
        {
            Session.SetSelection(previousSelection);
        }
    }

    private void FinishOrContinuePlacement(PlacementSession committed, string name)
    {
        if (committed.Target != PlacementTargetKind.Stamp
            && committed.Mode == StampPlacementMode.One)
        {
            PendingPlacement = null;
            StatusDetail = $"Создан «{name}».";
            return;
        }

        PendingPlacement = committed.Target == PlacementTargetKind.Stamp
            ? BuildStampPlacement(
                committed.WorldX, committed.WorldY, pointerVisible: true)
            : RepositionPrimitivePlacement(
                committed, committed.WorldX, committed.WorldY, pointerVisible: true);
        StatusDetail = committed.Target == PlacementTargetKind.Stamp
            ? $"Размещён «{name}». Ассет остаётся активным · Esc завершить."
            : $"Создан «{name}». Режим серии остаётся активным.";
    }

    private PlacementSession RepositionPrimitivePlacement(
        PlacementSession pending,
        double centerX,
        double centerY,
        bool pointerVisible)
    {
        var layer = ActiveObjectLayer();
        return pending with
        {
            Mode = StampPlacementMode == StampPlacementMode.Area
                ? StampPlacementMode.One
                : StampPlacementMode,
            AutoSelect = PlacementAutoSelect,
            LayerId = layer?.Id,
            WorldX = centerX,
            WorldY = centerY,
            PointerVisible = pointerVisible,
            InvalidReason = layer is null && !HasNoObjectLayer ? NoUsableObjectLayer : null,
            WillCreateLayer = layer is null && HasNoObjectLayer,
            PreviewObjects = MultiSelectionGeometry.Translate(
                pending.PreviewObjects, centerX - pending.WorldX, centerY - pending.WorldY),
        };
    }

    private PlacementSession BuildPrimitivePlacement(
        SceneObjectKind kind,
        double centerX,
        double centerY,
        StampPlacementMode mode,
        bool pointerVisible,
        double rotationDegrees = 0)
    {
        rotationDegrees = NormalizeAngle(rotationDegrees);
        var layer = ActiveObjectLayer();
        var viewportWorldWidth = Session.Viewport.ViewWidth > 0
            ? Session.Viewport.ViewWidth / Math.Max(Session.Viewport.Scale, 0.01)
            : 1_000;
        var width = Math.Clamp(viewportWorldWidth * 0.18, 96, 240);
        width = Math.Min(width, Session.Document.Canvas.Width);
        var height = Math.Min(kind == SceneObjectKind.Ellipse ? width * 0.68 : width * 0.62,
            Session.Document.Canvas.Height);
        var preview = new SceneObject(
            PendingPlacement?.PreviewObjects.FirstOrDefault()?.Id ?? ObjectId.New(),
            kind,
            kind == SceneObjectKind.Ellipse ? "Будущий эллипс" : "Будущий прямоугольник",
            centerX - width / 2,
            centerY - height / 2,
            width,
            height,
            rotationDegrees,
            kind == SceneObjectKind.Ellipse ? "#FF5B8266" : "#FF3E5C76",
            "#FF10131A",
            2,
            true,
            false)
        {
            ObjectOpacity = PlacementPreviewOpacity,
        };
        return new PlacementSession
        {
            Target = kind == SceneObjectKind.Ellipse
                ? PlacementTargetKind.Ellipse
                : PlacementTargetKind.Rectangle,
            Mode = mode,
            AutoSelect = PlacementAutoSelect,
            LayerId = layer?.Id,
            WorldX = centerX,
            WorldY = centerY,
            Width = width,
            Height = height,
            RotationDegrees = rotationDegrees,
            FillColor = preview.FillColor,
            StrokeColor = preview.StrokeColor,
            StrokeWidth = preview.StrokeWidth,
            PreviewOpacity = PlacementPreviewOpacity,
            PointerVisible = pointerVisible,
            InvalidReason = layer is null && !HasNoObjectLayer ? NoUsableObjectLayer : null,
            WillCreateLayer = layer is null && HasNoObjectLayer,
            Seed = 0,
            PlannedCount = 1,
            PreviewObjects = [preview],
        };
    }

    private PlacementSession BuildStampPlacement(
        double centerX,
        double centerY,
        bool pointerVisible)
    {
        var settings = _stampToolSettings;
        var rotationDegrees = NormalizeAngle(settings.RotationDegrees);
        var layer = ActiveObjectLayer();
        if (!TryStampInputs(settings, out var requestTemplate, out var sources, out var records, out var refusal))
        {
            return EmptyStampPlacement(
                centerX, centerY, layer?.Id, pointerVisible, refusal, rotationDegrees);
        }

        var request = requestTemplate with
        {
            CenterX = centerX,
            CenterY = centerY,
            Seed = NextGestureSeed(),
        };
        var plan = StampPlacementMode == StampPlacementMode.Area
            ? StampPlacement.PlanAreaPreview(request, sources, MaximumScatterPreviewObjects)
            : StampPlacement.PlanOne(request, sources[0]);
        plan = ApplyPlacementRotation(plan, rotationDegrees);
        var preview = plan.Select((item, index) => StampObject(
            item,
            index == 0 ? "Размещаемый ассет" : $"Размещаемый ассет {index + 1}",
            settings,
            PlacementPreviewOpacity)).ToList();
        return new PlacementSession
        {
            Target = PlacementTargetKind.Stamp,
            Mode = StampPlacementMode,
            AutoSelect = PlacementAutoSelect,
            LayerId = layer?.Id,
            WorldX = centerX,
            WorldY = centerY,
            Width = preview[0].Width,
            Height = preview[0].Height,
            RotationDegrees = rotationDegrees,
            FillColor = "#FFFFFFFF",
            StrokeColor = "#00000000",
            StrokeWidth = 0,
            PreviewOpacity = PlacementPreviewOpacity,
            PointerVisible = pointerVisible,
            InvalidReason = layer is null && !HasNoObjectLayer ? NoUsableObjectLayer : null,
            WillCreateLayer = layer is null && HasNoObjectLayer,
            Seed = request.Seed,
            PlannedCount = StampPlacementMode == StampPlacementMode.Area
                ? StampPlacement.CountFor(request.Density, request.AreaRadius)
                : 1,
            PreviewObjects = preview,
            AssetSetId = _selectedSetId,
            VariantId = CurrentVariant?.Id,
            VariantSha256 = CurrentVariant?.Sha256,
            StampSettingsSnapshot = settings,
        };
    }

    private PlacementSession EmptyStampPlacement(
        double centerX,
        double centerY,
        LayerId? layerId,
        bool pointerVisible,
        string refusal,
        double rotationDegrees) => new()
    {
        Target = PlacementTargetKind.Stamp,
        Mode = StampPlacementMode,
        AutoSelect = PlacementAutoSelect,
        LayerId = layerId,
        WorldX = centerX,
        WorldY = centerY,
        Width = 0,
        Height = 0,
        RotationDegrees = rotationDegrees,
        FillColor = "#FFFFFFFF",
        StrokeColor = "#00000000",
        StrokeWidth = 0,
        PreviewOpacity = PlacementPreviewOpacity,
        PointerVisible = pointerVisible,
        InvalidReason = refusal,
        Seed = NextGestureSeed(),
        PlannedCount = 0,
        PreviewObjects = [],
        AssetSetId = _selectedSetId,
        VariantId = CurrentVariant?.Id,
        VariantSha256 = CurrentVariant?.Sha256,
        StampSettingsSnapshot = _stampToolSettings,
    };

    private bool TryStampInputs(
        StampToolSettings settings,
        out StampPlacementRequest request,
        out IReadOnlyList<StampVariantSource> sources,
        out Dictionary<string, AssetRecord> records,
        out string refusal)
    {
        request = new StampPlacementRequest();
        sources = [];
        records = new Dictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
        refusal = "";
        if (_assetCatalog is null || _assetResolver is null)
        {
            refusal = "Локальный каталог ассетов недоступен.";
            return false;
        }
        if (_assetCatalog.Status == AssetLibraryStatus.Damaged)
        {
            refusal = "Индекс библиотеки повреждён. " + _assetCatalog.Diagnostic;
            return false;
        }
        if (SelectedSet is null || CurrentVariant is null)
        {
            refusal = "Сначала выберите набор и вариант в библиотеке.";
            return false;
        }

        var scatter = settings.Mode is StampPlacementMode.Area or StampPlacementMode.Series or StampPlacementMode.Path or StampPlacementMode.FillArea;
        var randomize = settings.RandomizeVariant && scatter;
        var candidates = randomize
            ? _selectedSetVariants
            : [_selectedSetVariants[_currentVariantIndex]];
        var resolvedSources = new List<StampVariantSource>(candidates.Count);
        foreach (var variant in candidates)
        {
            if (_assetCatalog.Find(variant.Sha256) is not { } record)
            {
                continue;
            }
            records.TryAdd(record.Sha256, record);
            if (variant.MaskSha256 is { } maskSha
                && _assetCatalog.Find(maskSha) is { } maskRecord)
            {
                records.TryAdd(maskRecord.Sha256, maskRecord);
            }
            resolvedSources.Add(new StampVariantSource(
                record.Sha256, record.Width, record.Height, variant.MaskSha256));
        }
        if (resolvedSources.Count == 0)
        {
            refusal = "Содержимое выбранного набора отсутствует в каталоге.";
            return false;
        }

        sources = resolvedSources;
        request = new StampPlacementRequest
        {
            AreaRadius = settings.AreaRadius,
            Density = settings.Density,
            RandomizeVariant = randomize && sources.Count > 1,
            RandomAngleStep = scatter ? settings.RandomAngleStep : Map.Core.Editing.StampRandomAngleStep.Off,
            RandomFlip = settings.RandomFlip && scatter,
            BaseWidth = DefaultStampReferenceWidth() * settings.ScalePercent / 100.0,
        };
        return true;
    }

    private static SceneObject StampObject(
        StampPlacementItem item,
        string name,
        StampToolSettings settings,
        double? previewOpacityMultiplier) => new(
        ObjectId.New(), SceneObjectKind.Stamp, name,
        item.X, item.Y, item.Width, item.Height, item.RotationDegrees,
        "#FFFFFFFF", "#00000000", 0, true, false)
    {
        Sublayer = settings.Sublayer,
        ObjectOpacity = settings.ObjectOpacity * (previewOpacityMultiplier ?? 1),
        Stamp = new StampStyle(item.Sha256)
        {
            ReferenceWidth = item.Width / Math.Max(0.1, settings.ScalePercent / 100.0),
            ReferenceHeight = item.Height / Math.Max(0.1, settings.ScalePercent / 100.0),
            FlipHorizontal = (settings.Style?.FlipHorizontal ?? false) ^ item.FlipHorizontal,
            FlipVertical = (settings.Style?.FlipVertical ?? false) ^ item.FlipVertical,
            MaskAssetSha256 = item.MaskSha256,
            ClipMaskEnabled = item.MaskSha256 is not null
                && (settings.Style?.ClipMaskEnabled ?? false),
            Blend = settings.Style?.Blend ?? BlendModes.Normal,
            Blur = settings.Style?.Blur ?? 0,
            ShadowOffsetX = settings.Style?.ShadowOffsetX ?? ShadowPreset.StandardOffsetX,
            ShadowOffsetY = settings.Style?.ShadowOffsetY ?? ShadowPreset.StandardOffsetY,
            ShadowBlur = settings.Style?.ShadowBlur ?? ShadowPreset.StandardBlur,
            ShadowOpacity = settings.Style?.ShadowOpacity ?? 0,
            ShadowPlacement = settings.Style?.ShadowPlacement ?? StampShadowPlacement.Object,
            ShadowColor = settings.Style?.ShadowColor ?? AppearanceColor.OpaqueBlack,
            Hue = settings.Style?.Hue ?? 0,
            Saturation = settings.Style?.Saturation ?? 0,
            Brightness = settings.Style?.Brightness ?? 0,
            Contrast = settings.Style?.Contrast ?? 0,
        },
    };

    private static IReadOnlyList<StampPlacementItem> ApplyPlacementRotation(
        IReadOnlyList<StampPlacementItem> plan,
        double rotationDegrees)
    {
        if (rotationDegrees == 0)
        {
            return plan;
        }

        return [.. plan.Select(item => item with
        {
            RotationDegrees = NormalizeUnsignedAngle(item.RotationDegrees + rotationDegrees),
        })];
    }

    private VectorPoint SnapPlacementPoint(double worldX, double worldY) =>
        ActiveSnapGrid() is { } grid
            ? GridSnapService.Snap(grid, new VectorPoint(worldX, worldY))
            : new VectorPoint(WholeGeometryValue(worldX), WholeGeometryValue(worldY));

    private void RefreshArmedPlacement()
    {
        RefreshAreaDraftSettings();
        if (PendingPlacement is not { } pending)
        {
            return;
        }

        PendingPlacement = pending.Target == PlacementTargetKind.Stamp
            ? BuildStampPlacement(
                pending.WorldX, pending.WorldY, pending.PointerVisible)
            : RepositionPrimitivePlacement(
                pending, pending.WorldX, pending.WorldY, pending.PointerVisible);
    }

    private void UpdateStampToolSettings(
        Func<StampToolSettings, StampToolSettings> change,
        bool persist)
    {
        var changed = NormalizeStampToolSettings(change(_stampToolSettings));
        if (changed == _stampToolSettings)
        {
            if (persist) PersistLocalPreferences();
            return;
        }

        _stampToolSettings = changed;
        RaiseStampToolState();
        RefreshArmedPlacement();
        if (persist)
        {
            PersistLocalPreferences();
        }
    }

    private void RaiseStampToolState()
    {
        Raise(nameof(StampToolSettings));
        Raise(nameof(IsEditingFutureStamp));
        Raise(nameof(StampPanelHeading));
        Raise(nameof(StampPanelStyle));
        Raise(nameof(StampPanelMaskSha256));
        Raise(nameof(StampClipMaskThumbnailPath));
        Raise(nameof(StampClipMaskStatus));
        Raise(nameof(StampClipMaskCanToggle));
        Raise(nameof(StampPanelCanEditLibraryMask));
        Raise(nameof(StampPanelVariantHasLibraryMask));
        Raise(nameof(StampForegroundStatus));
        Raise(nameof(StampScalePercent));
        Raise(nameof(StampRotationDegrees));
        Raise(nameof(StampSublayer));
        Raise(nameof(StampOpacityPercent));
        Raise(nameof(StampPlacementMode));
        Raise(nameof(PlacementAutoSelect));
        Raise(nameof(StampAreaRadius));
        Raise(nameof(StampDensity));
        Raise(nameof(RandomVariantAfterPlacement));
        Raise(nameof(StampRandomAngleStep));
        Raise(nameof(StampRandomAngle));
        Raise(nameof(StampRandomFlip));
        Raise(nameof(PlannedStampCount));
        Raise(nameof(StampAreaSummary));
    }

    private static StampToolSettings NormalizeStampToolSettings(StampToolSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var style = settings.Style;
        if (style is not null)
        {
            var color = AppearanceColor.Normalize(style.ShadowColor) ?? AppearanceColor.OpaqueBlack;
            var blend = BlendModes.IsSupported(style.Blend) ? style.Blend : BlendModes.Normal;
            var maskSha256 = NormalizeSha256(style.MaskAssetSha256);
            style = style with
            {
                MaskAssetSha256 = maskSha256,
                ClipMaskEnabled = maskSha256 is not null && style.ClipMaskEnabled,
                Blend = blend,
                Blur = Finite(style.Blur, 0, 0, AppearanceLimits.MaxBlur),
                ShadowOffsetX = Finite(style.ShadowOffsetX, ShadowPreset.StandardOffsetX,
                    -AppearanceLimits.MaxShadowOffset, AppearanceLimits.MaxShadowOffset),
                ShadowOffsetY = Finite(style.ShadowOffsetY, ShadowPreset.StandardOffsetY,
                    -AppearanceLimits.MaxShadowOffset, AppearanceLimits.MaxShadowOffset),
                ShadowBlur = Finite(style.ShadowBlur, ShadowPreset.StandardBlur, 0,
                    AppearanceLimits.MaxShadowBlur),
                ShadowOpacity = Finite(style.ShadowOpacity, 0, 0, 1),
                ShadowPlacement = Enum.IsDefined(style.ShadowPlacement)
                    ? style.ShadowPlacement
                    : StampShadowPlacement.Object,
                ShadowColor = color,
                Hue = Finite(style.Hue, 0, -180, 180),
                Saturation = Finite(style.Saturation, 0, -1, 1),
                Brightness = Finite(style.Brightness, 0, -1, 1),
                Contrast = Finite(style.Contrast, 0, -1, 1),
            };
        }

        return settings with
        {
            ScalePercent = Finite(settings.ScalePercent, 100, 10, 400),
            RotationDegrees = NormalizeUnsignedAngle(Finite(settings.RotationDegrees, 0, -360_000, 360_000)),
            Sublayer = Math.Clamp(settings.Sublayer, -10, 10),
            ObjectOpacity = Finite(settings.ObjectOpacity, 1, 0, 1),
            Style = style,
            Mode = Enum.IsDefined(settings.Mode) ? settings.Mode : StampPlacementMode.One,
            AutoSelect = Enum.IsDefined(settings.AutoSelect)
                ? settings.AutoSelect
                : PlacementAutoSelectPolicy.Auto,
            AreaRadius = Finite(settings.AreaRadius, 160, MinStampAreaRadius, MaxStampAreaRadius),
            Density = Finite(settings.Density, 40, MinStampDensity, MaxStampDensity),
            SeriesSpacingPercent = Finite(settings.SeriesSpacingPercent, 75, 10, 400),
            RandomAngleStep = IsSupportedRandomAngleStep(settings.RandomAngleStep)
                ? settings.RandomAngleStep
                : Map.Core.Editing.StampRandomAngleStep.Off,
        };
    }

    private static bool IsSupportedRandomAngleStep(Map.Core.Editing.StampRandomAngleStep value) =>
        value is Map.Core.Editing.StampRandomAngleStep.Off
            or Map.Core.Editing.StampRandomAngleStep.Degrees15
            or Map.Core.Editing.StampRandomAngleStep.Degrees30
            or Map.Core.Editing.StampRandomAngleStep.Degrees45
            or Map.Core.Editing.StampRandomAngleStep.Degrees60
            or Map.Core.Editing.StampRandomAngleStep.Degrees90
            or Map.Core.Editing.StampRandomAngleStep.Degrees180;

    private void PersistLocalPreferences() => _saveLocalPreferences?.Invoke(
        new EditorLocalPreferences(PlacementAutoSelect, _primaryAutosaveEnabled, _stampToolSettings));

    /// <summary>
    /// One pointer gesture, all or nothing. The plan is resolved first, then every distinct hash
    /// it needs is verified and read exactly once, and only a fully resolved plan is committed as
    /// a single undoable command. If one variant is missing or changed, nothing is placed at all:
    /// a shortened plan committed as a success is the one outcome the user cannot detect.
    /// </summary>
    public bool PlaceCurrentVariantAt(double worldX, double worldY)
    {
        if (PendingPlacement is not { Target: PlacementTargetKind.Stamp })
        {
            ArmStampPlacement();
        }
        UpdatePlacementPreview(worldX, worldY);
        return CommitPendingPlacement();
    }

    /// <summary>
    /// Resolves every distinct hash the plan needs, once each, and returns null the moment one of
    /// them cannot be placed. The cost of a gesture therefore follows the number of variants it
    /// uses, not the thousands of stamps it may put on the canvas.
    /// </summary>
    private Dictionary<string, MapAssetReference>? ResolvePlan(
        IReadOnlyList<StampPlacementItem> plan,
        Dictionary<string, AssetRecord> records,
        out List<MapAssetReference> references,
        out string refusal)
    {
        var byHash = new Dictionary<string, MapAssetReference>(StringComparer.OrdinalIgnoreCase);
        references = [];
        refusal = "";

        var existing = Session.Document.Assets.ToDictionary(
            asset => asset.Sha256, StringComparer.OrdinalIgnoreCase);
        var neededHashes = plan.SelectMany(item => item.MaskSha256 is { } mask
                ? new[] { item.Sha256, mask }
                : new[] { item.Sha256 })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var newRecords = new List<AssetRecord>();
        long advertisedBytes = 0;
        try
        {
            foreach (var sha256 in neededHashes)
            {
                if (existing.ContainsKey(sha256))
                {
                    continue;
                }

                if (!records.TryGetValue(sha256, out var record))
                {
                    refusal = "Содержимое выбранного набора отсутствует в каталоге. Ничего не размещено.";
                    return null;
                }

                newRecords.Add(record);
                advertisedBytes = checked(advertisedBytes + record.ByteLength);
            }
        }
        catch (OverflowException)
        {
            refusal = "Размер выбранного набора переполняет допустимый бюджет проекта. Ничего не размещено.";
            return null;
        }

        if (!MapProjectStore.CanAdmitEmbeddedBytes(
                Session.Document, advertisedBytes, newRecords.Count, out refusal))
        {
            refusal += " Ничего не размещено.";
            return null;
        }

        foreach (var sha256 in neededHashes)
        {
            if (byHash.ContainsKey(sha256))
            {
                continue;
            }

            if (existing.TryGetValue(sha256, out var existingReference))
            {
                byHash[sha256] = existingReference;
                references.Add(existingReference);
                continue;
            }

            if (!records.TryGetValue(sha256, out var record))
            {
                refusal = "Содержимое варианта или его маски отсутствует в каталоге. Ничего не размещено.";
                return null;
            }

            var resolution = _assetResolver!.Resolve(record);
            if (!resolution.IsAvailable)
            {
                refusal = Unplaceable(record, resolution.Availability);
                return null;
            }

            byHash[sha256] = resolution.Reference!;
            references.Add(resolution.Reference!);
            if (!MapProjectStore.CanAdmitAdditionalAssets(Session.Document, references, out refusal))
            {
                refusal += " Ничего не размещено.";
                return null;
            }
        }

        return byHash;
    }

    /// <summary>
    /// Reuses the immutable payload already owned by the document. This keeps repeated actions
    /// independent of a deleted catalog original and prevents undo entries from retaining a fresh
    /// 64 MiB clone for the same SHA on every click.
    /// </summary>
    private MapAssetReference? ResolveAssetForDocument(
        AssetRecord record,
        out AssetAvailability availability,
        out string? refusal)
    {
        if (Session.Document.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Sha256, record.Sha256, StringComparison.OrdinalIgnoreCase))
            is { } existing)
        {
            availability = AssetAvailability.Available;
            refusal = null;
            return existing;
        }

        if (!MapProjectStore.CanAdmitEmbeddedBytes(
                Session.Document, record.ByteLength, additionalAssets: 1, out var preflight))
        {
            availability = AssetAvailability.Missing;
            refusal = preflight;
            return null;
        }

        var resolution = _assetResolver!.Resolve(record);
        availability = resolution.Availability;
        if (!resolution.IsAvailable || resolution.Reference is null)
        {
            refusal = null;
            return null;
        }

        if (!MapProjectStore.CanAdmitAdditionalAssets(
                Session.Document, [resolution.Reference], out var exactFailure))
        {
            refusal = exactFailure;
            return null;
        }

        refusal = null;
        return resolution.Reference;
    }

    /// <summary>Names the variant and the exact reason it stopped the whole gesture.</summary>
    private static string Unplaceable(AssetRecord record, AssetAvailability availability) => availability switch
    {
        AssetAvailability.Missing =>
            $"Вариант «{record.DisplayName}» отсутствует на диске. Ничего не размещено.",
        AssetAvailability.HashMismatch =>
            $"Файл варианта «{record.DisplayName}» не совпадает с сохранённым SHA-256. Ничего не размещено.",
        AssetAvailability.Unsupported =>
            $"Формат варианта «{record.DisplayName}» не поддерживается. Ничего не размещено.",
        _ => $"Вариант «{record.DisplayName}» сейчас недоступен. Ничего не размещено.",
    };

    /// <summary>The same gesture a canvas press runs, centred on the current view.</summary>
    public bool PlaceCurrentVariantAtViewCentre()
    {
        var (x, y) = ViewCentre();
        return PlaceCurrentVariantAt(x, y);
    }

    /// <summary>Swaps only the asset of the selected stamp; transform and style are untouched.</summary>
    public void ReplaceSelectedStampWithCurrentVariant()
    {
        if (_assetCatalog is null || CurrentVariant is not { } variant)
        {
            StatusDetail = "Сначала выберите набор и вариант в библиотеке.";
            return;
        }

        if (_assetCatalog.Find(variant.Sha256) is not { } record)
        {
            StatusDetail = "Содержимое варианта отсутствует в каталоге.";
            return;
        }

        ReplaceSelectedStampAsset(record, variant);
    }

    /// <summary>One import of many files becomes one named card the tool can immediately use.</summary>
    public AssetSet? ImportAssetSet(IReadOnlyList<string> paths, string setName)
    {
        if (_assetCatalog is null)
        {
            StatusDetail = "Локальный каталог ассетов недоступен.";
            return null;
        }

        try
        {
            var set = _assetCatalog.ImportSet(paths, setName, new AssetImportOptions
            {
                Provenance = new AssetProvenance
                {
                    Source = "user-imported",
                    LicenseStatus = AssetLicenseStatus.Unknown,
                },
            }, _clock());
            Raise(nameof(Assets));
            RefreshLibrary();
            SelectAssetSet(set.Id);
            StatusDetail = set.VariantIds.Count == 1
                ? $"Импортирован ассет «{set.Name}»."
                : $"Импортирован набор «{set.Name}»: {set.VariantIds.Count} вариантов.";
            return set;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StatusDetail = "Не удалось импортировать набор: " + ex.Message;
            _log.Error("Asset set import failed", ex);
            return null;
        }
    }

    private double DefaultStampReferenceWidth() =>
        Math.Max(1, Math.Min(256.0, Session.Document.Canvas.Width / 4.0));

    private double DefaultStampWidth() =>
        DefaultStampReferenceWidth() * _stampToolSettings.ScalePercent / 100.0;

    /// <summary>
    /// The seed of the gesture that is about to be planned. It is a fixed mix rather than a
    /// framework hash, because the framework randomises string hashing per process and a replay
    /// promised in the documentation has to survive a restart. The counter is not consumed here:
    /// only a committed gesture advances it, so a refused one changes nothing.
    /// </summary>
    private int NextGestureSeed() =>
        GestureSeed.From(Session.Revision, _selectedSetId, _currentVariantIndex, _gestureCounter + 1);

    private void RaiseLibrary()
    {
        Raise(nameof(LibrarySets));
        Raise(nameof(LibraryGroups));
        Raise(nameof(LibraryCollections));
        Raise(nameof(VisibleLibraryTiles));
        Raise(nameof(VisibleLibraryTileWindow));
        Raise(nameof(LibraryStatus));
        Raise(nameof(LibraryDiagnostic));
        Raise(nameof(IsLibraryAvailable));
        Raise(nameof(LibraryRows));
        Raise(nameof(SelectedLibraryRowIndex));
        Raise(nameof(HasActiveLibraryFilters));
        Raise(nameof(ActiveLibraryFilterSummary));
        Raise(nameof(CompactRecentSets));
        Raise(nameof(CompactFavoriteSets));
    }

    private void RaiseVariantState()
    {
        Raise(nameof(SelectedSetId));
        Raise(nameof(SelectedSet));
        Raise(nameof(SelectedSetVariants));
        Raise(nameof(CurrentVariantIndex));
        Raise(nameof(CurrentVariant));
        Raise(nameof(CurrentVariantLabel));
        Raise(nameof(IsCurrentVariantTheCover));
        Raise(nameof(SelectedLibraryRowIndex));
        Raise(nameof(CanMakeCurrentVariantCover));
    }

    public AssetRecord? ImportAsset(string path, bool linked)
    {
        if (_assetCatalog is null)
        {
            StatusDetail = "Локальный каталог ассетов недоступен.";
            return null;
        }

        try
        {
            var imported = _assetCatalog.Import(path, new AssetImportOptions
            {
                Linked = linked,
                Provenance = new AssetProvenance
                {
                    Source = "user-imported",
                    LicenseStatus = AssetLicenseStatus.Unknown,
                },
            }, _clock());
            Raise(nameof(Assets));
            StatusDetail = $"Импортирован ассет «{imported.DisplayName}».";
            return imported;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusDetail = "Не удалось импортировать изображение: " + ex.Message;
            _log.Error("Asset import failed", ex);
            return null;
        }
    }

    public void PlaceStamp(AssetRecord record, bool embedded = true, double? x = null, double? y = null)
    {
        if (_assetCatalog is null || _assetResolver is null)
        {
            return;
        }

        var (layer, createLayer) = EnsureObjectLayer();
        if (layer is null)
        {
            StatusDetail = NoUsableObjectLayer;
            return;
        }

        var knownReference = Session.Document.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Sha256, record.Sha256, StringComparison.OrdinalIgnoreCase));
        if (knownReference is null
            && !MapProjectStore.CanAdmitEmbeddedBytes(
                Session.Document,
                embedded ? record.ByteLength : 0,
                additionalAssets: 1,
                out var preflightFailure))
        {
            StatusDetail = preflightFailure;
            return;
        }

        MapAssetReference? reference = knownReference;
        if (reference is null)
        {
            var resolution = _assetCatalog.ReadVerified(record);
            if (resolution.Status != AssetResolutionStatus.Available
                || resolution.Path is null || resolution.Content is null)
            {
                StatusDetail = resolution.Status == AssetResolutionStatus.HashMismatch
                    ? "Файл ассета изменился и не совпадает с сохранённым hash. Выберите замену явно."
                    : "Выберите доступный ассет и разблокированный слой объектов.";
                return;
            }

            var thumbnail = _assetCatalog.TryThumbnailPath(record, out var thumbnailPath)
                ? AssetImagePolicy.TryReadOptionalThumbnail(thumbnailPath!)
                : null;
            string? relative = null;
            if (!embedded && DocumentPath is { } documentPath
                && ProjectLinkedAssetPath.TryMakeRelative(documentPath, resolution.Path!, out var projectRelative))
            {
                relative = projectRelative;
            }
            reference = new MapAssetReference(
                record.Sha256,
                record.DisplayName,
                record.MediaType,
                record.Extension,
                embedded ? ProjectAssetStorage.Embedded : ProjectAssetStorage.Linked,
                record.ImportedUtc,
                record.Provenance.Source,
                record.Provenance.LicenseId,
                embeddedContent: embedded ? resolution.Content : null,
                catalogId: embedded ? null : record.Sha256,
                relativePath: embedded ? null : relative,
                fallbackThumbnailPng: thumbnail);
            if (!MapProjectStore.CanAdmitAdditionalAssets(
                    Session.Document, [reference], out var exactFailure))
            {
                StatusDetail = exactFailure;
                return;
            }
        }
        var width = Math.Min(256.0, Session.Document.Canvas.Width / 4.0);
        var height = width * record.Height / record.Width;
        var stamp = new SceneObject(
            ObjectId.New(), SceneObjectKind.Stamp, record.DisplayName,
            x ?? ((Session.Document.Canvas.Width - width) / 2),
            y ?? ((Session.Document.Canvas.Height - height) / 2),
            width, height, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        {
            Stamp = new StampStyle(record.Sha256)
            {
                ReferenceWidth = width,
                ReferenceHeight = height,
            },
        };

        ExecuteWithOptionalLayer(
            new PlaceAssetObjectsCommand(layer.Id, reference, [stamp]),
            createLayer, "Place stamp on a new layer");
        Session.SelectedObjectId = stamp.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void ScatterStamp(AssetRecord record, int count, int seed = 12345)
    {
        if (_assetResolver is null || count < 1)
        {
            return;
        }

        var (layer, createLayer) = EnsureObjectLayer();
        var reference = ResolveAssetForDocument(
            record, out _, out var assetRefusal);
        if (layer is null || reference is null)
        {
            StatusDetail = layer is null ? NoUsableObjectLayer : assetRefusal ?? "Ассет недоступен для размещения.";
            return;
        }

        var random = new Random(seed);
        var objects = new List<SceneObject>(count);
        for (var i = 0; i < count; i++)
        {
            var size = 48 + (random.NextDouble() * 80);
            objects.Add(new SceneObject(
                ObjectId.New(), SceneObjectKind.Stamp, $"{record.DisplayName} {i + 1}",
                random.NextDouble() * Math.Max(1, Session.Document.Canvas.Width - size),
                random.NextDouble() * Math.Max(1, Session.Document.Canvas.Height - size),
                size, size * record.Height / record.Width,
                random.Next(-180, 181), "#FFFFFFFF", "#00000000", 0, true, false)
            {
                Stamp = new StampStyle(record.Sha256)
                {
                    ReferenceWidth = size,
                    ReferenceHeight = size * record.Height / record.Width,
                    FlipHorizontal = random.Next(2) == 0,
                },
            });
        }

        ExecuteWithOptionalLayer(
            new PlaceAssetObjectsCommand(layer.Id, reference, objects),
            createLayer, "Scatter stamps on a new layer");
        Session.SelectedObjectId = objects[^1].Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void ReplaceSelectedStampAsset(AssetRecord record, AssetVariant? variant = null)
    {
        if (_assetResolver is null || Session.SelectedObject is not { Stamp: not null } selected)
        {
            StatusDetail = "Выберите stamp и новый ассет.";
            return;
        }

        var reference = ResolveAssetForDocument(
            record, out _, out var assetRefusal);
        if (reference is null)
        {
            StatusDetail = assetRefusal ?? "Новый ассет отсутствует или не совпадает с его SHA-256.";
            return;
        }

        variant ??= _assetCatalog?.Variants.FirstOrDefault(item =>
            string.Equals(item.Sha256, record.Sha256, StringComparison.OrdinalIgnoreCase));
        MapAssetReference? maskReference = null;
        if (variant?.MaskSha256 is { } maskSha)
        {
            maskReference = ResolveMaskForDocument(maskSha, out var maskRefusal);
            if (maskReference is null)
            {
                StatusDetail = maskRefusal ?? "Маска нового варианта отсутствует или повреждена.";
                return;
            }
        }

        Session.Execute(new ReplaceStampAssetCommand(
            selected,
            reference,
            record.Height / Math.Max(1.0, record.Width),
            variant?.MaskSha256,
            maskReference));
        AfterEdit();
    }

    public void KeepSelectedStampPlaceholder()
    {
        StatusDetail = "Placeholder оставлен: ссылка и исходный SHA-256 не изменены.";
    }

    public void UpdateSelectedStamp(
        double? opacity = null, bool toggleFlip = false, bool toggleShadow = false,
        double? hue = null, double? saturation = null)
    {
        if (Session.SelectedObject is not { Stamp: { } style } before || before.Locked)
        {
            StatusDetail = "Выберите разблокированный stamp.";
            return;
        }

        var after = CopyObject(before) with
        {
            ObjectOpacity = opacity ?? before.ObjectOpacity,
            Stamp = style with
            {
                FlipHorizontal = toggleFlip ? !style.FlipHorizontal : style.FlipHorizontal,
                ShadowOpacity = toggleShadow ? (style.ShadowOpacity > 0 ? 0 : 0.55) : style.ShadowOpacity,
                Hue = hue ?? style.Hue,
                Saturation = saturation ?? style.Saturation,
            },
        };
        Session.Execute(new ReplaceObjectsCommand([before], [after], "Edit stamp style"));
        AfterEdit();
    }

    // ---------------------------------------------------------------- editing

    public bool BeginVectorDraw(double worldX, double worldY)
    {
        if (ActiveTool is not (EditorTool.Line or EditorTool.Shape))
        {
            return false;
        }

        if (ActiveObjectLayer() is not { Visible: true, Locked: false })
        {
            // Refusing silently used to swallow the press whole. Naming the reason lets the user
            // fix it, and the caller falls back to plain selection so the scene stays reachable.
            StatusDetail = "Нет видимого разблокированного слоя объектов — фигура не создана.";
            return false;
        }

        var point = SnapVectorGesturePoint(new VectorPoint(worldX, worldY));
        _vectorPoints = [point];
        _vectorPreviewObjectId = ObjectId.New();
        _vectorPreviewLayerId = ActiveObjectLayer()!.Id;
        _vectorPreviewObject = null;
        Raise(nameof(PlacementPreviewObjects));
        return true;
    }

    public void ContinueVectorDraw(double worldX, double worldY)
    {
        if (_vectorPoints is not { Count: > 0 } points)
        {
            return;
        }

        var next = SnapVectorGesturePoint(new VectorPoint(worldX, worldY));

        // Free Draw follows the pointer; Brush does too, because the band it sweeps is built from
        // the same path. The primitives keep only the press point and the current one.
        var continuous = ActiveTool == EditorTool.Line
            ? ActiveLineDrawMode == LineDrawMode.FreeDraw
            : ActiveShapeDrawMode is ShapeDrawMode.FreeDraw or ShapeDrawMode.Brush;
        if (continuous)
        {
            var last = points[^1];
            if (Math.Pow(next.X - last.X, 2) + Math.Pow(next.Y - last.Y, 2) >= 4)
                points.Add(next);
        }
        else if (points.Count == 1)
            points.Add(next);
        else
            points[^1] = next;

        _vectorPreviewObject = BuildVectorGestureObject(points, preview: true);
        Raise(nameof(PlacementPreviewObjects));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void EndVectorDraw()
    {
        if (_vectorPoints is not { } points)
        {
            return;
        }

        var layerId = _vectorPreviewLayerId;
        var added = BuildVectorGestureObject(points, preview: false);
        _vectorPoints = null;
        _vectorPreviewObject = null;
        _vectorPreviewLayerId = null;
        _vectorPreviewObjectId = null;
        Raise(nameof(PlacementPreviewObjects));
        if (layerId is null || Session.Document.FindLayer(layerId.Value) is not ObjectLayer layer || added is null)
        {
            return;
        }
        Session.Execute(new AddObjectCommand(layer.Id, added));
        Session.SelectedObjectId = added.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void CancelVectorDraw()
    {
        _vectorPoints = null;
        _vectorPreviewObject = null;
        _vectorPreviewLayerId = null;
        _vectorPreviewObjectId = null;
        Raise(nameof(PlacementPreviewObjects));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    private VectorPoint SnapVectorGesturePoint(VectorPoint point) =>
        ActiveSnapGrid() is { SnapEnabled: true } grid
            ? GridSnapService.Snap(grid, point)
            : new VectorPoint(WholeGeometryValue(point.X), WholeGeometryValue(point.Y));

    private SceneObject? BuildVectorGestureObject(IReadOnlyList<VectorPoint> source, bool preview)
    {
        if (_vectorPreviewObjectId is not { } objectId || source.Count < 2) return null;
        var points = source;
        SceneObject? result = null;
        try
        {
            if (ActiveTool == EditorTool.Line)
            {
                var linePoints = ActiveLineDrawMode == LineDrawMode.Pen ? [source[0], source[^1]] : source;
                if (DistanceSquared(linePoints[0], linePoints[^1]) < 1) return null;
                result = VectorGeometry.CreateLine(objectId,
                    EntityNameGenerator.Object(Session.Document, SceneObjectKind.Line),
                    linePoints, NewLineStroke());
            }
            else if (ActiveTool == EditorTool.Shape)
            {
                var start = source[0]; var end = source[^1];
                if (DistanceSquared(start, end) < 1) return null;
                var name = EntityNameGenerator.Object(Session.Document, SceneObjectKind.Shape);
                var fill = NewShapeFill();
                var stroke = NewShapeStroke();
                result = ActiveShapeDrawMode switch
                {
                    ShapeDrawMode.Rectangle => VectorGeometry.CreateShape(objectId, name,
                        ShapePrimitiveKind.Rectangle, GestureBounds(start, end), fill, stroke),
                    ShapeDrawMode.Ellipse => VectorGeometry.CreateShape(objectId, name,
                        ShapePrimitiveKind.Ellipse, GestureBounds(start, end), fill, stroke),
                    ShapeDrawMode.Polygon => VectorGeometry.CreateFreeShape(objectId, name,
                        RegularPolygon(GestureBounds(start, end), ShapePolygonSides), fill, stroke),
                    ShapeDrawMode.Brush => VectorGeometry.CreateFreeShape(objectId, name,
                        StrokeOutline(source, ShapeBrushWidth), fill, stroke),
                    _ when source.Count >= 3 => VectorGeometry.CreateFreeShape(
                        objectId, name, source, fill, stroke),
                    _ => null,
                };
            }
        }
        catch (ArgumentException)
        {
            return null;
        }
        return result is null ? null : result with { ObjectOpacity = preview ? .55 : 1 };

        static IReadOnlyList<VectorPoint> RegularPolygon(WorldBounds bounds, int sides)
        {
            var cx = (bounds.Left + bounds.Right) / 2; var cy = (bounds.Top + bounds.Bottom) / 2;
            var rx = (bounds.Right - bounds.Left) / 2; var ry = (bounds.Bottom - bounds.Top) / 2;
            return [.. Enumerable.Range(0, sides).Select(index =>
            {
                var angle = (-Math.PI / 2) + index * Math.PI * 2 / sides;
                return new VectorPoint(cx + Math.Cos(angle) * rx, cy + Math.Sin(angle) * ry);
            })];
        }
    }

    /// <summary>
    /// The rectangle a press-drag describes, with the two modifier rules applied: Shift makes the
    /// two sides equal, Alt treats the press point as the centre. Both are what every drawing
    /// program does, and without them a square or a circle was a matter of luck.
    /// </summary>
    private WorldBounds GestureBounds(VectorPoint start, VectorPoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (VectorConstrainProportions)
        {
            var size = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = dx < 0 ? -size : size;
            dy = dy < 0 ? -size : size;
        }

        if (VectorDrawFromCenter)
        {
            return new WorldBounds(
                start.X - Math.Abs(dx), start.Y - Math.Abs(dy),
                start.X + Math.Abs(dx), start.Y + Math.Abs(dy));
        }

        return new WorldBounds(
            Math.Min(start.X, start.X + dx), Math.Min(start.Y, start.Y + dy),
            Math.Max(start.X, start.X + dx), Math.Max(start.Y, start.Y + dy));
    }

    /// <summary>
    /// The closed outline of a stroked path: one offset side forward, the other back. It is what
    /// turns the Brush mode into an actual band instead of a hairline copy of Free Draw.
    /// </summary>
    private static IReadOnlyList<VectorPoint> StrokeOutline(IReadOnlyList<VectorPoint> path, double width)
    {
        var half = Math.Max(0.5, width / 2);
        var forward = new List<VectorPoint>(path.Count);
        var back = new List<VectorPoint>(path.Count);
        for (var index = 0; index < path.Count; index++)
        {
            var previous = path[Math.Max(0, index - 1)];
            var next = path[Math.Min(path.Count - 1, index + 1)];
            var dx = next.X - previous.X;
            var dy = next.Y - previous.Y;
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length < 1e-6)
            {
                dx = 1; dy = 0; length = 1;
            }

            var normalX = -dy / length * half;
            var normalY = dx / length * half;
            forward.Add(new VectorPoint(path[index].X + normalX, path[index].Y + normalY));
            back.Add(new VectorPoint(path[index].X - normalX, path[index].Y - normalY));
        }

        back.Reverse();
        forward.AddRange(back);
        return forward;
    }

    // --------------------------------------------------------------------- pen paths

    private List<VectorPoint>? _penPoints;
    private VectorPoint? _penCursor;

    /// <summary>True when the active tool draws by placing nodes rather than by dragging.</summary>
    public bool IsPenMode => ActiveTool switch
    {
        EditorTool.Shape => ActiveShapeDrawMode == ShapeDrawMode.Pen,
        EditorTool.Line => ActiveLineDrawMode == LineDrawMode.Pen,
        _ => false,
    };

    /// <summary>True while a pen path is being built, so the canvas keeps previewing it.</summary>
    public bool PenPathActive => _penPoints is { Count: > 0 };

    /// <summary>How many nodes the pen path already has; the panel shows it.</summary>
    public int PenNodeCount => _penPoints?.Count ?? 0;

    /// <summary>
    /// Places one pen node. Pen used to be a drag gesture that always produced the same right
    /// triangle whatever the user did, which is the single worst defect of the Shape tool: the
    /// mode existed, was selectable, and could not draw what it named.
    /// </summary>
    public bool AddPenNode(double worldX, double worldY)
    {
        if (!IsPenMode)
        {
            return false;
        }

        if (ActiveObjectLayer() is not { Visible: true, Locked: false } layer)
        {
            StatusDetail = "Нет видимого разблокированного слоя объектов — узел не поставлен.";
            return false;
        }

        var point = SnapVectorGesturePoint(new VectorPoint(worldX, worldY));
        if (_penPoints is null)
        {
            _penPoints = [point];
            _vectorPreviewObjectId = ObjectId.New();
            _vectorPreviewLayerId = layer.Id;
            StatusDetail = ActiveTool == EditorTool.Shape
                ? "Ставьте узлы фигуры. Клик по первому узлу или Enter — замкнуть, Esc — отмена."
                : "Ставьте узлы линии. Enter — завершить, Esc — отмена.";
        }
        else
        {
            // Clicking the first node closes the path, the way a pen tool is expected to work.
            var closing = _penPoints.Count >= 2
                && DistanceSquared(point, _penPoints[0]) <= CloseToleranceSquared();
            if (closing)
            {
                return FinishPenPath();
            }

            _penPoints.Add(point);
        }

        _penCursor = point;
        RefreshPenPreview();
        return true;
    }

    /// <summary>Rubber-bands the segment between the last node and the pointer.</summary>
    public void UpdatePenPreview(double worldX, double worldY)
    {
        if (_penPoints is null)
        {
            return;
        }

        _penCursor = SnapVectorGesturePoint(new VectorPoint(worldX, worldY));
        RefreshPenPreview();
    }

    /// <summary>Commits the pen path as one object, or reports why it could not be one.</summary>
    public bool FinishPenPath()
    {
        if (_penPoints is null)
        {
            return false;
        }

        var layerId = _vectorPreviewLayerId;
        var added = BuildPenObject(preview: false);
        CancelPenPath();
        if (added is null || layerId is null
            || Session.Document.FindLayer(layerId.Value) is not ObjectLayer layer)
        {
            StatusDetail = "Контур не создан: фигуре нужно не меньше трёх узлов, линии — двух.";
            return false;
        }

        Session.Execute(new AddObjectCommand(layer.Id, added));
        Session.SelectedObjectId = added.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
        return true;
    }

    /// <summary>Drops the unfinished pen path; nothing reaches the document or the history.</summary>
    public void CancelPenPath()
    {
        if (_penPoints is null)
        {
            return;
        }

        _penPoints = null;
        _penCursor = null;
        _vectorPreviewObject = null;
        _vectorPreviewLayerId = null;
        _vectorPreviewObjectId = null;
        Raise(nameof(PlacementPreviewObjects));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    /// <summary>Cancels a pen path if there is one, and says whether it did.</summary>
    public bool CancelPenPathIfActive()
    {
        if (!PenPathActive)
        {
            return false;
        }

        CancelPenPath();
        StatusDetail = "Контур отменён.";
        return true;
    }

    private double CloseToleranceSquared()
    {
        var world = 8 / Math.Max(0.0001, Session.Viewport.Scale);
        return world * world;
    }

    private void RefreshPenPreview()
    {
        _vectorPreviewObject = BuildPenObject(preview: true);
        Raise(nameof(PlacementPreviewObjects));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    private SceneObject? BuildPenObject(bool preview)
    {
        if (_penPoints is not { Count: > 0 } placed || _vectorPreviewObjectId is not { } objectId)
        {
            return null;
        }

        var points = new List<VectorPoint>(placed);
        if (preview && _penCursor is { } cursor
            && DistanceSquared(points[^1], cursor) > 0)
        {
            points.Add(cursor);
        }

        try
        {
            SceneObject? result = ActiveTool == EditorTool.Line
                ? points.Count < 2
                    ? null
                    : VectorGeometry.CreateLine(objectId,
                        EntityNameGenerator.Object(Session.Document, SceneObjectKind.Line),
                        points, NewLineStroke())
                : points.Count < 3
                    ? null
                    : VectorGeometry.CreateFreeShape(objectId,
                        EntityNameGenerator.Object(Session.Document, SceneObjectKind.Shape),
                        points, NewShapeFill(), NewShapeStroke());

            return result is null ? null : result with { ObjectOpacity = preview ? .55 : 1 };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static double DistanceSquared(VectorPoint a, VectorPoint b) =>
        ((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y));

    public DragPreview? DragPreview
    {
        get => _dragPreview;
        private set => SetField(ref _dragPreview, value);
    }

    /// <summary>What the canvas should draw: the snapshot, with the live drag substituted in.</summary>
    public MapDocument RenderDocument
    {
        get
        {
            if (CanvasWorkflowOpen
                && CanvasWorkflowMode == ResizeRegionMode.Resize
                && CanvasResizePreviewDocument is { } resizePreview)
            {
                return resizePreview;
            }

            if (DragPreview is not { } preview)
            {
                return Session.Document;
            }

            var document = Session.Document;
            foreach (var proposed in preview.Proposed)
            {
                document = DocumentEditor.ReplaceObject(document, proposed);
            }

            return document;
        }
    }

    public void AddObject(SceneObjectKind kind)
    {
        var (layer, createLayer) = EnsureObjectLayer();
        if (layer is null)
        {
            StatusDetail = NoUsableObjectLayer;
            return;
        }

        var canvas = Session.Document.Canvas;

        // Stagger new objects so consecutive adds stay individually visible.
        var offset = (Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count) % 5) * 40;
        var width = Math.Min(240.0, canvas.Width / 4.0);
        var height = Math.Min(160.0, canvas.Height / 4.0);

        var added = new SceneObject(
            ObjectId.New(), kind,
            EntityNameGenerator.Object(Session.Document, kind),
            ((canvas.Width - width) / 2) + offset,
            ((canvas.Height - height) / 2) + offset,
            width, height, 0,
            kind == SceneObjectKind.Ellipse ? "#FF5B8266" : "#FF3E5C76",
            "#FF10131A", 2,
            visible: true, locked: false);

        ExecuteWithOptionalLayer(
            new AddObjectCommand(layer.Id, added), createLayer, "Add object on a new layer");
        Session.SelectedObjectId = added.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void AddLine()
    {
        var layer = ActiveObjectLayer();
        if (layer is null) return;
        var centre = ViewCentre();
        var line = VectorGeometry.CreateLine(
            ObjectId.New(), EntityNameGenerator.Object(Session.Document, SceneObjectKind.Line),
        [
            new VectorPoint(centre.X - 240, centre.Y + 80),
            new VectorPoint(centre.X, centre.Y - 100),
            new VectorPoint(centre.X + 240, centre.Y + 40),
        ]);
        Session.Execute(new AddObjectCommand(layer.Id, line));
        Session.SelectedObjectId = line.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void AddShape(ShapePrimitiveKind kind)
    {
        var layer = ActiveObjectLayer();
        if (layer is null) return;
        var centre = ViewCentre();
        var shape = VectorGeometry.CreateShape(
            ObjectId.New(), EntityNameGenerator.Object(Session.Document, SceneObjectKind.Shape), kind,
            new WorldBounds(centre.X - 180, centre.Y - 130, centre.X + 180, centre.Y + 130));
        Session.Execute(new AddObjectCommand(layer.Id, shape));
        Session.SelectedObjectId = shape.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void AddText()
    {
        var layer = ActiveObjectLayer();
        if (layer is null) return;
        var centre = ViewCentre();
        const string defaultContent = "Новый текст\nна карте";
        var text = new SceneObject(
            ObjectId.New(), SceneObjectKind.Text,
            EntityNameGenerator.Text(Session.Document, defaultContent),
            centre.X - 180, centre.Y - 70, 360, 140, 0,
            "#FF20242B", "#FFFFFFFF", 0, true, false)
        {
            Text = new TextStyle { Content = defaultContent },
        };
        Session.Execute(new AddObjectCommand(layer.Id, text));
        Session.SelectedObjectId = text.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void CycleSelectedTextStyle()
    {
        if (EditableSelection() is not { Text: { } style } before) return;
        var after = before with
        {
            Text = style with
            {
                Alignment = style.Alignment == TextAlignment.Left ? TextAlignment.Center : TextAlignment.Left,
                OutlineWidth = style.OutlineWidth == 0 ? 2 : 0,
                ShadowOpacity = style.ShadowOpacity == 0 ? 0.35 : 0,
                Curve = style.Curve == 0 ? 12 : 0,
            },
        };
        ReplaceSelected(before, after, "Edit text style");
    }

    public TextFontResolution? SelectedTextFontResolution =>
        Session.SelectedObject?.Text is { } text
            ? TextFontResolver.Resolve(text.RequestedFontFamily, text.FallbackFontFamily)
            : null;

    public IReadOnlyList<string> LocalFontFamilies => _localFontFamilies;

    /// <summary>
    /// Case-insensitive local search. A missing requested family remains visible so fallback is
    /// never mistaken for a silent document rewrite.
    /// </summary>
    public IReadOnlyList<string> FontFamiliesForSearch(string? search)
    {
        var query = search?.Trim() ?? "";
        var requested = Session.SelectedObject?.Text?.RequestedFontFamily;
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(requested))
        {
            result.Add(requested);
        }

        result.AddRange(_localFontFamilies.Where(family =>
            !string.Equals(family, requested, StringComparison.OrdinalIgnoreCase)
            && (query.Length == 0 || family.Contains(query, StringComparison.OrdinalIgnoreCase))));
        return result;
    }

    public bool IsLocalFontAvailable(string family) =>
        _localFontFamilies.Contains(family, StringComparer.OrdinalIgnoreCase);

    public void SetSelectedTextFontFamily(string? family)
    {
        var requested = family?.Trim();
        if (Session.SelectedObjectIds.Count != 1
            || string.IsNullOrWhiteSpace(requested)
            || requested.Length > 256)
        {
            return;
        }

        EditText("Text font family", commit: true,
            text => text with { RequestedFontFamily = requested });
        Raise(nameof(SelectedTextFontResolution));
    }

    public void SelectAdjacentTextFont(int direction)
    {
        if (direction == 0
            || Session.SelectedObjectIds.Count != 1
            || _localFontFamilies.Count == 0
            || EditableSelection() is not { Text: { } text })
        {
            return;
        }

        var current = -1;
        for (var index = 0; index < _localFontFamilies.Count; index++)
        {
            if (string.Equals(_localFontFamilies[index], text.RequestedFontFamily,
                    StringComparison.OrdinalIgnoreCase))
            {
                current = index;
                break;
            }
        }

        var next = current < 0
            ? direction > 0 ? 0 : _localFontFamilies.Count - 1
            : (current + Math.Sign(direction) + _localFontFamilies.Count) % _localFontFamilies.Count;
        SetSelectedTextFontFamily(_localFontFamilies[next]);
    }

    public void UseMissingFontOnSelectedText()
    {
        if (EditableSelection() is not { Text: { } style } before) return;
        ReplaceSelected(before, before with
        {
            Text = style with { RequestedFontFamily = "Map Stage 7 Missing Font", FallbackFontFamily = "Arial" },
        }, "Set requested text font");
        Raise(nameof(SelectedTextFontResolution));
    }

    public IReadOnlyList<VectorNode> SelectedVectorNodes => Session.SelectedObject is { } selected
        ? selected.Line?.Nodes ?? selected.Shape?.Regions.SelectMany(region => region.Nodes).ToList() ?? []
        : [];

    public IReadOnlyList<LinePortal> SelectedVectorPortals => Session.SelectedObject is { } selected
        ? selected.Line?.Portals ?? selected.Shape?.Portals ?? []
        : [];

    public VectorNode? SelectedVectorNode => _selectedVectorNodeId is { } id
        ? SelectedVectorNodes.FirstOrDefault(node => node.Id == id)
        : null;

    public LinePortal? SelectedVectorPortal => _selectedPortalId is { } id
        ? SelectedVectorPortals.FirstOrDefault(portal => portal.Id == id)
        : null;

    public bool SelectVectorNode(VectorNodeId id)
    {
        if (SelectedVectorNodes.All(node => node.Id != id)) return false;
        _selectedVectorNodeId = id; _selectedPortalId = null;
        Raise(nameof(SelectedVectorNode)); Raise(nameof(SelectedVectorPortal)); return true;
    }

    public bool SelectVectorPortal(PortalId id)
    {
        if (SelectedVectorPortals.All(portal => portal.Id != id)) return false;
        _selectedPortalId = id; _selectedVectorNodeId = null;
        Raise(nameof(SelectedVectorNode)); Raise(nameof(SelectedVectorPortal)); return true;
    }

    public void AddNodeToSelectedLine(double localX = .5, double localY = .5)
    {
        if (EditableSelection() is not { Line: not null } before || !double.IsFinite(localX) || !double.IsFinite(localY)) return;
        var after = VectorObjectEditor.AddLineNode(before, localX, localY);
        _selectedVectorNodeId = after.Line!.Nodes[^1].Id;
        ReplaceSelected(before, after, "Add line node");
    }

    public void AddNodeToSelectedShape(double localX, double localY)
    {
        if (EditableSelection() is not { Shape: { Regions.Count: > 0 } shape } before
            || !double.IsFinite(localX) || !double.IsFinite(localY)) return;
        var after = VectorObjectEditor.AddShapeNode(before, shape.Regions[0].Id, localX, localY);
        _selectedVectorNodeId = after.Shape!.Regions[0].Nodes[^1].Id;
        ReplaceSelected(before, after, "Add shape node");
    }

    public bool SetSelectedVectorNode(
        double localX, double localY, VectorNodeKind kind,
        double controlInX, double controlInY, double controlOutX, double controlOutY)
    {
        if (EditableSelection() is not { } before || _selectedVectorNodeId is not { } id
            || new[] { localX, localY, controlInX, controlInY, controlOutX, controlOutY }.Any(value => !double.IsFinite(value)))
            return false;
        ReplaceSelected(before, VectorObjectEditor.SetNode(before, id, localX, localY, kind,
            controlInX, controlInY, controlOutX, controlOutY), "Edit selected vector node");
        return true;
    }

    public void MoveSelectedVectorNode()
    {
        if (SelectedVectorNode is { } node)
            SetSelectedVectorNode(node.X, node.Y, node.Kind, node.ControlInX, node.ControlInY,
                node.ControlOutX, node.ControlOutY);
    }

    public void ToggleSelectedNodeCurve()
    {
        if (EditableSelection() is not { } before || SelectedVectorNode is not { } node) return;
        ReplaceSelected(before, VectorObjectEditor.SetNodeCurve(before, node.Id, node.Kind != VectorNodeKind.Curve),
            "Toggle selected node curve");
    }

    public void DeleteSelectedVectorNode()
    {
        if (EditableSelection() is not { } before || _selectedVectorNodeId is not { } id) return;
        var after = before.Line is not null
            ? VectorObjectEditor.DeleteLineNode(before, id)
            : VectorObjectEditor.DeleteShapeNode(before, id);
        _selectedVectorNodeId = null;
        ReplaceSelected(before, after, "Delete selected vector node");
    }

    public void AddPortalToSelected(double position = .5, double width = 48)
    {
        if (EditableSelection() is not { } before || before.Line is null && before.Shape is null
            || !double.IsFinite(position) || !double.IsFinite(width)) return;
        var portal = new LinePortal(PortalId.New(), Math.Clamp(position, 0, 1), Math.Max(0, width));
        _selectedPortalId = portal.Id;
        ReplaceSelected(before, VectorObjectEditor.AddPortal(before, portal), "Add portal");
    }

    public bool SetSelectedPortal(double position, double width)
    {
        if (EditableSelection() is not { } before || _selectedPortalId is not { } id
            || !double.IsFinite(position) || !double.IsFinite(width)) return false;
        ReplaceSelected(before, VectorObjectEditor.SetPortal(before, id, Math.Clamp(position, 0, 1), Math.Max(0, width)),
            "Edit selected portal");
        return true;
    }

    public void MoveSelectedPortal()
    {
        if (SelectedVectorPortal is { } portal) SetSelectedPortal(portal.Position, portal.Width);
    }

    public void DeleteSelectedPortal()
    {
        if (EditableSelection() is not { } before || _selectedPortalId is not { } id) return;
        ReplaceSelected(before, VectorObjectEditor.DeletePortal(before, id), "Delete selected portal");
        _selectedPortalId = null;
    }

    public bool SelectVectorElementAt(double worldX, double worldY, double tolerance)
    {
        if (Session.SelectedObject is not { } selected || selected.Line is null && selected.Shape is null)
            return false;
        tolerance = Math.Max(.001, tolerance);
        var best = double.PositiveInfinity;
        VectorNodeId? nodeId = null; PortalId? portalId = null;
        foreach (var node in selected.Line?.Nodes ?? selected.Shape!.Regions.SelectMany(region => region.Nodes))
        {
            var point = VectorNodeWorld(selected, node);
            var distance = Math.Sqrt(Math.Pow(point.X - worldX, 2) + Math.Pow(point.Y - worldY, 2));
            if (distance <= tolerance && distance < best)
            {
                best = distance; nodeId = node.Id; portalId = null;
            }
        }
        var path = SelectedVectorPath(selected);
        foreach (var portal in selected.Line?.Portals ?? selected.Shape!.Portals)
        {
            var point = PointAtPosition(path, portal.Position);
            var distance = Math.Sqrt(Math.Pow(point.X - worldX, 2) + Math.Pow(point.Y - worldY, 2));
            if (distance <= tolerance && distance < best)
            {
                best = distance; portalId = portal.Id; nodeId = null;
            }
        }
        if (nodeId is { } selectedNodeId) return SelectVectorNode(selectedNodeId);
        if (portalId is { } selectedPortalId) return SelectVectorPortal(selectedPortalId);
        return false;
    }

    public bool BeginVectorElementDrag(double worldX, double worldY, double viewScale)
    {
        if (EditableSelection() is not { } before || before.Line is null && before.Shape is null
            || !SelectVectorElementAt(worldX, worldY, 10 / Math.Max(.001, viewScale))) return false;
        _vectorElementDrag = (before, _selectedVectorNodeId, _selectedPortalId);
        DragPreview = new DragPreview([before], [before], DragMode.Move, null, worldX, worldY, null);
        return true;
    }

    public void UpdateVectorElementDrag(double worldX, double worldY)
    {
        if (_vectorElementDrag is not { } drag) return;
        SceneObject proposed;
        if (drag.NodeId is { } nodeId)
        {
            var world = SnapVectorGesturePoint(new VectorPoint(worldX, worldY));
            var local = ObjectLocalPoint(drag.Before, world);
            proposed = VectorObjectEditor.MoveNode(drag.Before, nodeId, local.X, local.Y);
        }
        else if (drag.PortalId is { } portalId)
        {
            var position = NearestPosition(SelectedVectorPath(drag.Before), new VectorPoint(worldX, worldY));
            proposed = VectorObjectEditor.MovePortal(drag.Before, portalId, position);
        }
        else return;
        DragPreview = new DragPreview([drag.Before], [proposed], DragMode.Move, null, worldX, worldY, null);
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void EndVectorElementDrag()
    {
        if (_vectorElementDrag is not { } drag) return;
        var proposed = DragPreview?.Proposed.FirstOrDefault();
        _vectorElementDrag = null; DragPreview = null;
        if (proposed is not null && proposed != drag.Before)
        {
            Session.Execute(new ReplaceObjectsCommand([drag.Before], [proposed], "Drag vector node or portal"));
            AfterEdit();
        }
        else InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void CancelVectorElementDrag()
    {
        if (_vectorElementDrag is null) return;
        _vectorElementDrag = null; DragPreview = null;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    private static VectorPoint VectorNodeWorld(SceneObject owner, VectorNode node) =>
        RotateObjectPoint(owner, new VectorPoint(owner.X + node.X * owner.Width, owner.Y + node.Y * owner.Height));

    private static VectorPoint ObjectLocalPoint(SceneObject owner, VectorPoint world)
    {
        var unrotated = RotateObjectPoint(owner, world, inverse: true);
        return new VectorPoint((unrotated.X - owner.X) / owner.Width, (unrotated.Y - owner.Y) / owner.Height);
    }

    private static VectorPoint RotateObjectPoint(SceneObject owner, VectorPoint point, bool inverse = false)
    {
        var radians = owner.RotationDegrees * (inverse ? -1 : 1) * Math.PI / 180;
        var cos = Math.Cos(radians); var sin = Math.Sin(radians);
        var cx = owner.X + owner.Width / 2; var cy = owner.Y + owner.Height / 2;
        var dx = point.X - cx; var dy = point.Y - cy;
        return new VectorPoint(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
    }

    private static IReadOnlyList<VectorPoint> SelectedVectorPath(SceneObject owner)
    {
        var raw = owner.Line is { } line
            ? VectorGeometry.Flatten(owner, line.Nodes, line.Closed)
            : VectorGeometry.Flatten(owner, owner.Shape!.Regions[0].Nodes, closed: true);
        return [.. raw.Select(point => RotateObjectPoint(owner, point))];
    }

    private static VectorPoint PointAtPosition(IReadOnlyList<VectorPoint> path, double position)
    {
        if (path.Count == 0) return default;
        var lengths = path.Zip(path.Skip(1)).Select(pair => Math.Sqrt(
            Math.Pow(pair.Second.X - pair.First.X, 2) + Math.Pow(pair.Second.Y - pair.First.Y, 2))).ToList();
        var target = lengths.Sum() * Math.Clamp(position, 0, 1); var cursor = 0.0;
        for (var i = 0; i < lengths.Count; i++)
        {
            if (target <= cursor + lengths[i])
            {
                var t = (target - cursor) / Math.Max(.001, lengths[i]);
                return new VectorPoint(path[i].X + (path[i + 1].X - path[i].X) * t,
                    path[i].Y + (path[i + 1].Y - path[i].Y) * t);
            }
            cursor += lengths[i];
        }
        return path[^1];
    }

    private static double NearestPosition(IReadOnlyList<VectorPoint> path, VectorPoint target)
    {
        if (path.Count < 2) return 0;
        var lengths = path.Zip(path.Skip(1)).Select(pair => Math.Sqrt(
            Math.Pow(pair.Second.X - pair.First.X, 2) + Math.Pow(pair.Second.Y - pair.First.Y, 2))).ToList();
        var total = Math.Max(.001, lengths.Sum()); var cursor = 0.0; var best = double.PositiveInfinity; var result = 0.0;
        for (var i = 0; i < lengths.Count; i++)
        {
            var a = path[i]; var b = path[i + 1]; var dx = b.X - a.X; var dy = b.Y - a.Y;
            var t = Math.Clamp(((target.X - a.X) * dx + (target.Y - a.Y) * dy)
                / Math.Max(.000001, dx * dx + dy * dy), 0, 1);
            var px = a.X + dx * t; var py = a.Y + dy * t;
            var distance = Math.Pow(target.X - px, 2) + Math.Pow(target.Y - py, 2);
            if (distance < best) { best = distance; result = (cursor + lengths[i] * t) / total; }
            cursor += lengths[i];
        }
        return Math.Clamp(result, 0, 1);
    }

    public void CutSelectedLine()
    {
        if (EditableSelection() is not { Line: { Nodes.Count: >= 3 } line } before
            || _selectedVectorNodeId is not { } selectedNode
            || DocumentEditor.FindLayerOf(Session.Document, before.Id) is not { } layer)
        {
            return;
        }

        var points = line.Nodes.Select(node => new VectorPoint(
            before.X + node.X * before.Width, before.Y + node.Y * before.Height)).ToList();
        var cut = line.Nodes.ToList().FindIndex(node => node.Id == selectedNode);
        if (cut <= 0 || cut >= points.Count - 1)
        {
            StatusDetail = "Для разреза выберите внутренний узел линии.";
            return;
        }
        var pieces = new[]
        {
            VectorGeometry.CreateLine(ObjectId.New(), before.Name + " A", points.Take(cut + 1).ToList(), line.Stroke),
            VectorGeometry.CreateLine(ObjectId.New(), before.Name + " B", points.Skip(cut).ToList(), line.Stroke),
        };
        Session.Execute(new ReplaceObjectWithManyCommand(Session.Document, layer.Id, before.Id, pieces));
        Session.SelectedObjectId = pieces[0].Id;
        AfterEdit();
    }

    public void CycleSelectedVectorStroke()
    {
        if (EditableSelection() is not { } before) return;
        var current = before.Line?.Stroke ?? before.Shape?.Stroke;
        if (current is null) return;
        var changed = current with
        {
            Offset = current.Offset == 0 ? 12 : 0,
            EndCap = current.EndCap == VectorCap.Round ? VectorCap.Square : VectorCap.Round,
            ShadowOpacity = current.ShadowOpacity == 0 ? 0.35 : 0,
        };
        var after = before.Line is { } line
            ? before with { Line = new LineStyle(line.Nodes, changed, line.Portals, line.Closed) }
            : before with
            {
                Shape = new ShapeStyle(before.Shape!.Regions, before.Shape.Fill, changed, before.Shape.Portals),
            };
        ReplaceSelected(before, after, "Edit vector stroke");
    }

    public void UseAssetAsSelectedVectorStroke(AssetRecord record)
    {
        if (_assetResolver is null || EditableAppearanceSelection() is not { } before)
        {
            return;
        }

        var stroke = before.Line?.Stroke ?? before.Shape?.Stroke;
        var reference = ResolveAssetForDocument(
            record, out _, out var assetRefusal);
        if (stroke is null || reference is null)
        {
            StatusDetail = assetRefusal ?? "Выберите Line/Shape и доступный локальный ассет.";
            return;
        }

        var changed = stroke with { AssetSha256 = record.Sha256, AssetSpacing = 1.25 };
        var after = before.Line is { } line
            ? before with { Line = new LineStyle(line.Nodes, changed, line.Portals, line.Closed) }
            : before with
            {
                Shape = new ShapeStyle(before.Shape!.Regions, before.Shape.Fill, changed, before.Shape.Portals),
            };
        Session.Execute(new VectorAssetStyleCommand(before, after, reference));
        AfterEdit();
        StatusDetail = $"Ассет «{record.DisplayName}» используется для обводки.";
    }

    /// <summary>Embeds one available library asset as the selected shape's repeating fill.</summary>
    public void UseAssetAsSelectedShapeFill(AssetRecord record)
    {
        if (_assetResolver is null || EditableAppearanceSelection() is not { Shape: { } shape } before)
        {
            StatusDetail = "Выберите доступную фигуру и локальный ассет.";
            return;
        }

        var reference = ResolveAssetForDocument(
            record, out _, out var assetRefusal);
        if (reference is null)
        {
            StatusDetail = assetRefusal ?? "Файл заливки отсутствует или не совпадает с его SHA-256.";
            return;
        }

        var changedFill = shape.Fill with { TextureAssetSha256 = record.Sha256 };
        if (changedFill.Equals(shape.Fill))
        {
            return;
        }

        var after = before with
        {
            Shape = new ShapeStyle(shape.Regions, changedFill, shape.Stroke, shape.Portals),
        };
        Session.Execute(new VectorAssetStyleCommand(before, after, reference, "Use asset fill"));
        AfterEdit();
        StatusDetail = $"Ассет «{record.DisplayName}» используется для заливки фигуры.";
    }

    public void ApplyShapeBoolean(ShapeBooleanOperation operation)
    {
        if (ResolveShapeBooleanSelection() is not { } operands)
        {
            StatusDetail = "Для boolean явно выделите две совместимые фигуры на одном доступном Object layer.";
            return;
        }

        var command = new ShapeBooleanCommand(Session.Document, operands.Layer.Id,
            operands.First.Id, operands.Second.Id, operation);
        Session.Execute(command);
        Session.SelectedObjectId = command.ResultId;
        CancelShapeBooleanPreview();
        AfterEdit();
    }

    public bool PreviewShapeBoolean(ShapeBooleanOperation operation)
    {
        if (ResolveShapeBooleanSelection() is not { } operands)
        {
            StatusDetail = "Для preview явно выделите две фигуры на одном доступном Object layer.";
            return false;
        }
        var command = new ShapeBooleanCommand(Session.Document, operands.Layer.Id,
            operands.First.Id, operands.Second.Id, operation);
        _pendingShapeBoolean = (operands.Layer.Id, operands.First.Id, operands.Second.Id, operation);
        _shapeBooleanPreviewObject = command.Result with { ObjectOpacity = .55 };
        Raise(nameof(PlacementPreviewObjects));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        return true;
    }

    public bool CommitShapeBooleanPreview()
    {
        if (_pendingShapeBoolean is not { } pending
            || Session.Document.FindLayer(pending.LayerId) is not ObjectLayer layer
            || layer.Objects.All(item => item.Id != pending.First)
            || layer.Objects.All(item => item.Id != pending.Second)) return false;
        var command = new ShapeBooleanCommand(Session.Document, pending.LayerId,
            pending.First, pending.Second, pending.Operation);
        CancelShapeBooleanPreview();
        Session.Execute(command); Session.SelectedObjectId = command.ResultId; AfterEdit(); return true;
    }

    public void CancelShapeBooleanPreview()
    {
        _pendingShapeBoolean = null; _shapeBooleanPreviewObject = null;
        Raise(nameof(PlacementPreviewObjects));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    private (ObjectLayer Layer, SceneObject First, SceneObject Second)? ResolveShapeBooleanSelection()
    {
        var shapes = Session.SelectedObjectIds.Select(id => DocumentEditor.FindObject(Session.Document, id))
            .Where(item => item?.Shape is not null).Select(item => item!).ToList();
        if (shapes.Count != 2
            || DocumentEditor.FindLayerOf(Session.Document, shapes[0].Id) is not { } layer
            || DocumentEditor.FindLayerOf(Session.Document, shapes[1].Id)?.Id != layer.Id
            || layer.Locked || !layer.Visible) return null;
        return (layer, shapes[0], shapes[1]);
    }

    private ObjectLayer? ActiveObjectLayer() => ActiveLayer is ObjectLayer { Visible: true, Locked: false } active
        ? active
        : Session.Document.Layers.OfType<ObjectLayer>()
            .LastOrDefault(layer => layer.Visible && !layer.Locked);

    private (double X, double Y) ViewCentre() => Session.Viewport.ScreenToWorld(
        Session.Viewport.ViewWidth / 2, Session.Viewport.ViewHeight / 2);

    private void ReplaceSelected(SceneObject before, SceneObject after, string name)
    {
        Session.Execute(new ReplaceObjectsCommand([before], [after], name));
        AfterEdit();
    }

    public void SelectLayer(LayerId id)
    {
        if (Session.Document.FindLayer(id) is null)
        {
            return;
        }

        _activeLayerId = id;
        Raise(nameof(ActiveLayerId));
        Raise(nameof(ActiveLayer));
        Raise(nameof(ActiveLayerSummary));
        Raise(nameof(MaskEditLayerId));
        Raise(nameof(IsEditingStampForeground));
        Raise(nameof(StampClipMaskStatus));
        Raise(nameof(StampClipMaskCanToggle));
        Raise(nameof(StampForegroundStatus));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddObjectLayer()
    {
        var layer = new ObjectLayer(
            LayerId.New(), EntityNameGenerator.Layer(Session.Document, typeof(ObjectLayer)),
            true, false, 1.0, []);
        Session.Execute(new AddLayerCommand(layer, _activeLayerId));
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void AddBrushLayer()
    {
        var layer = BrushLayer.CreateEmpty(
            LayerId.New(), EntityNameGenerator.Layer(Session.Document, typeof(BrushLayer)));
        Session.Execute(new AddLayerCommand(layer, _activeLayerId));
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void AddLandLayer()
    {
        var layer = LandLayer.CreateEmpty(
            LayerId.New(), EntityNameGenerator.Layer(Session.Document, typeof(LandLayer)));
        Session.Execute(new AddLayerCommand(layer, _activeLayerId));
        _activeLayerId = layer.Id;
        ActiveTool = EditorTool.Land;
        AfterEdit();
    }

    public void AddGridLayer()
    {
        var grid = new GridLayer(
            LayerId.New(), EntityNameGenerator.Layer(Session.Document, typeof(GridLayer)),
            true, false, 1,
        [
            new GridDefinition(GridId.New(), GridGeometry.Square, 128)
            {
                Subdivisions = 2, IncludeInExport = false,
            },
        ]);
        Session.Execute(new AddLayerCommand(grid, _activeLayerId));
        _activeLayerId = grid.Id;
        AfterEdit();
    }

    public void AddFilterLayer()
    {
        var layer = new FilterLayer(
            LayerId.New(), EntityNameGenerator.Layer(Session.Document, typeof(FilterLayer)),
            true, false, 1,
            new FilterSettings { Brightness = 0.08, Contrast = 0.12, Saturation = 0.1 });
        Session.Execute(new AddLayerCommand(layer, _activeLayerId));
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void CycleActiveFilter()
    {
        if (ActiveLayer is not FilterLayer { Locked: false } before) return;
        var warm = before.Settings.Temperature <= 0;
        var after = before with
        {
            Settings = before.Settings with
            {
                Brightness = warm ? 0.1 : -0.05,
                Contrast = warm ? 0.18 : 0.05,
                Saturation = warm ? 0.16 : -0.12,
                HueDegrees = warm ? 12 : -18,
                Temperature = warm ? 0.25 : -0.2,
            },
        };
        Session.Execute(new UpdateFilterCommand(before, after)); AfterEdit();
    }

    public bool SetActiveFilter(
        double? brightness = null, double? contrast = null, double? saturation = null,
        double? hueDegrees = null, double? temperature = null,
        double? textureOpacity = null, double? textureScale = null,
        bool resetTexture = false)
    {
        if (ActiveLayer is not FilterLayer { Locked: false } before) return false;
        var current = before.Settings;
        var updated = current with
        {
            Brightness = Math.Clamp(brightness ?? current.Brightness, -1, 1),
            Contrast = Math.Clamp(contrast ?? current.Contrast, -1, 1),
            Saturation = Math.Clamp(saturation ?? current.Saturation, -1, 1),
            HueDegrees = Math.Clamp(hueDegrees ?? current.HueDegrees, -180, 180),
            Temperature = Math.Clamp(temperature ?? current.Temperature, -1, 1),
            TextureOpacity = Math.Clamp(textureOpacity ?? current.TextureOpacity, 0, 1),
            TextureScale = Math.Clamp(textureScale ?? current.TextureScale, 0.05, 64),
            TextureAssetSha256 = resetTexture ? null : current.TextureAssetSha256,
        };
        if (updated == current) return false;
        Session.Execute(new UpdateFilterCommand(before, before with { Settings = updated }));
        AfterEdit();
        return true;
    }

    public void UseAssetAsActiveFilterTexture(AssetRecord record)
    {
        if (_assetResolver is null || ActiveLayer is not FilterLayer { Locked: false } before) return;
        var reference = ResolveAssetForDocument(
            record, out _, out var assetRefusal);
        if (reference is null)
        { StatusDetail = assetRefusal ?? "Текстура фильтра недоступна или изменила SHA-256."; return; }
        var after = before with
        {
            Settings = before.Settings with
            { TextureAssetSha256 = record.Sha256, TextureOpacity = 0.28, TextureScale = 1 },
        };
        Session.Execute(new UpdateFilterWithAssetCommand(
            before, after, reference)); AfterEdit();
    }

    public void CreateEditableComposition(
        bool includeBackground = false, bool includeGrids = false, bool includeFilters = false)
    {
        if (Session.SelectedObject is not { } selected
            || DocumentEditor.FindLayerOf(Session.Document, selected.Id) is not { } layer) return;
        var ids = selected.GroupId is { } group
            ? SceneGeometry.GroupMembers(Session.Document, group).Select(item => item.Id).ToArray()
            : [selected.Id];
        var sourceObjects = layer.Objects.Where(item => ids.Contains(item.Id)).ToList();
        ids = [.. sourceObjects.Select(item => item.Id)];
        var bounds = sourceObjects.Select(SceneGeometry.BoundsOf).Aggregate((a, b) => new WorldBounds(
            Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)));
        var reference = ActiveSnapGrid()?.CellSize ?? 1;
        var package = CompositionTransfer.Copy(Session.Document, ids, new CompositionCaptureOptions
        {
            IncludeBackground = includeBackground,
            IncludeGrids = includeGrids,
            IncludeFilters = includeFilters,
            ReferenceGridScale = reference,
        }, selected.Name + " composition");
        var instance = CompositionTransfer.CreateInstance(package,
            (bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2, reference);
        Session.Execute(new ReplaceObjectsWithCompositionCommand(Session.Document, layer.Id, ids, instance));
        Session.SelectedObjectId = instance.Id; CompositionClipboard.Set(package); AfterEdit();
    }

    /// <summary>
    /// The selection as one nested composition package, for the explicit "Копировать композицию"
    /// command and for other map windows. This is a different product feature from Ctrl+C, which
    /// copies the objects themselves - see <see cref="CopySelectedObjects"/>.
    /// </summary>
    public void CopySelectedComposition()
    {
        var ids = SelectionWithWholeGroups();
        if (ids.Count == 0 || Session.SelectedObject is not { } primary) return;
        var package = CompositionTransfer.Copy(Session.Document, ids,
            new CompositionCaptureOptions { ReferenceGridScale = ActiveSnapGrid()?.CellSize ?? 1 },
            primary.Name + " copy");
        CompositionClipboard.Set(package); StatusDetail = "Композиция скопирована для другого окна карты.";
    }

    /// <summary>Pastes the composition buffer as one nested composition object.</summary>
    public void PasteComposition()
    {
        var package = CompositionClipboard.Get();
        if (package is null) { StatusDetail = "Сначала скопируйте композицию."; return; }
        var (layer, createLayer) = EnsureObjectLayer();
        if (layer is null) { StatusDetail = NoUsableObjectLayer; return; }
        var centre = ViewCentre();
        var instance = CompositionTransfer.CreateInstance(package, centre.X, centre.Y, ActiveSnapGrid()?.CellSize);
        ExecuteWithOptionalLayer(
            new PasteCompositionCommand(layer.Id, instance, package.Assets),
            createLayer, "Paste composition on a new layer");
        Session.SelectedObjectId = instance.Id; _activeLayerId = layer.Id; AfterEdit();
    }

    /// <summary>
    /// Every selected object, whole groups included, into Map's own object buffer. Copying shows
    /// no duplicate on the canvas and does not touch the document, so it is not an undoable edit.
    /// </summary>
    public void CopySelectedObjects()
    {
        var ids = SelectionWithWholeGroups();
        if (ids.Count == 0)
        {
            StatusDetail = "Сначала выделите хотя бы один объект.";
            return;
        }

        ObjectClipboard.Set(ObjectTransfer.Copy(Session.Document, ids));
        StatusDetail = $"Скопировано: {EditorLabels.Objects(ids.Count)}.";
        if (ids.Any(id => PlacementOwnership.Owner(Session.Document, id) is not null))
            StatusDetail += " Копии будут обычными объектами; исходная конструкция сохранена.";
        Raise(nameof(CanPasteObjects));
    }

    /// <summary>True when the object buffer holds something this window could paste.</summary>
    public bool CanPasteObjects => ObjectClipboard.Count > 0;

    /// <summary>
    /// The selection widened to whole groups: copying one member of a group copies the group, the
    /// same unit the user selects and moves on the canvas.
    /// </summary>
    private IReadOnlyList<ObjectId> SelectionWithWholeGroups()
    {
        var selected = Session.SelectedObjects;
        if (selected.Count == 0)
        {
            return [];
        }

        var ids = new List<ObjectId>();
        var seen = new HashSet<ObjectId>();
        foreach (var sceneObject in selected)
        {
            var unit = sceneObject.GroupId is { } group
                ? SceneGeometry.GroupMembers(Session.Document, group).Select(member => member.Id)
                : [sceneObject.Id];
            foreach (var id in unit)
            {
                if (seen.Add(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    public AssetRecord? RepairAssetSource(AssetRecord record, string replacementPath)
    {
        if (_assetCatalog is null) return null;
        try
        {
            var repaired = _assetCatalog.RepairSource(record, replacementPath);
            RefreshLibrary();
            StatusDetail = "Исходник ассета восстановлен и проверен по SHA-256 и формату.";
            return repaired;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            StatusDetail = "Ассет не заменён: " + error.Message;
            _log.Error("ASSET_REPAIR_REJECTED", error);
            return null;
        }
    }

    public bool RemoveAssetFromCatalog(AssetRecord record)
    {
        if (_assetCatalog is null) return false;
        try
        {
            _assetCatalog.RemoveRecord(record.Sha256);
            RefreshLibrary();
            StatusDetail = "Ассет удалён из каталога; сохранённые исходные bytes не уничтожены.";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException)
        {
            StatusDetail = "Ассет не удалён из каталога: " + error.Message;
            _log.Error("ASSET_CATALOG_REMOVE_FAILED", error);
            return false;
        }
    }

    public bool ExportSelectedCompositionPackage(string path)
    {
        if (Session.SelectedObjectIds.Count == 0)
        {
            StatusDetail = "Для package сначала выделите хотя бы один объект.";
            return false;
        }
        try
        {
            var package = CompositionTransfer.Copy(
                Session.Document, Session.SelectedObjectIds,
                new CompositionCaptureOptions { ReferenceGridScale = ActiveSnapGrid()?.CellSize ?? 1 },
                Session.SelectedObject?.Name + " package" ?? "Composition package");
            var now = _clock();
            CompositionPackageStore.Export(
                package, path, new SaveMetadata(_appVersion, now),
                RenderPreview(Session.Document));
            StatusDetail = "Composition package сохранён локально с hashes и provenance.";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or MapProjectException or ArgumentException)
        {
            StatusDetail = "Composition package не сохранён; карта не изменена.";
            _log.Error("COMPOSITION_PACKAGE_EXPORT_FAILED", error);
            return false;
        }
    }

    public bool ImportCompositionPackage(string path)
    {
        var layer = ActiveObjectLayer();
        if (layer is null)
        {
            StatusDetail = "Для импорта package выберите разблокированный слой объектов.";
            return false;
        }
        try
        {
            var package = CompositionPackageStore.Import(path);
            if (!MapProjectStore.CanAdmitAdditionalAssets(Session.Document, package.Assets, out var reason))
            {
                StatusDetail = "Package не помещается в бюджет проекта: " + reason;
                return false;
            }
            var centre = ViewCentre();
            var instance = CompositionTransfer.CreateInstance(
                package, centre.X, centre.Y, ActiveSnapGrid()?.CellSize);
            Session.Execute(new PasteCompositionCommand(layer.Id, instance, package.Assets));
            Session.SelectedObjectId = instance.Id;
            _activeLayerId = layer.Id;
            AfterEdit();
            StatusDetail = "Composition package импортирован как редактируемая композиция.";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or MapProjectException or ArgumentException)
        {
            StatusDetail = "Composition package отклонён; карта не изменена.";
            _log.Error("COMPOSITION_PACKAGE_IMPORT_FAILED", error);
            return false;
        }
    }

    /// <summary>
    /// Restores the whole copied buffer as real, separately editable objects: the same count, the
    /// same mutual layout, sizes, rotations, flips, opacity, sublayers, styles and asset identity,
    /// in the same draw order, but with fresh ids. It is ONE history entry, so a single undo takes
    /// the entire pasted group away and a single redo brings all of it back, and the new group -
    /// not one arbitrary member of it - becomes the selection. Repeating the paste cascades by a
    /// steady step instead of stacking every copy on one spot.
    /// </summary>
    public void PasteObjects()
    {
        if (ObjectClipboard.Get() is not { } payload)
        {
            StatusDetail = "Сначала скопируйте объекты.";
            return;
        }

        if (payload.Assets.Count > 0
            && !MapProjectStore.CanAdmitAdditionalAssets(Session.Document, payload.Assets, out var budgetRefusal))
        {
            StatusDetail = "Вставка не помещается в бюджет проекта: " + budgetRefusal;
            return;
        }

        var (layer, createLayer) = EnsureObjectLayer();
        if (layer is null)
        {
            StatusDetail = NoUsableObjectLayer;
            return;
        }

        var step = Math.Clamp(ActiveSnapGrid()?.CellSize ?? 24, 8, 128);
        var shift = step * ObjectClipboard.TakePasteOrdinal();
        var objects = ObjectTransfer.Instantiate(
            payload, payload.SourceLeft + shift, payload.SourceTop + shift);

        var paste = new PasteObjectsCommand(layer.Id, objects, payload.Assets);
        ExecuteWithOptionalLayer(paste, createLayer, "Paste objects on a new layer");
        _activeLayerId = layer.Id;
        Session.SetSelection(paste.PastedObjectIds);
        AfterEdit();
        AfterSelectionChange();
        StatusDetail = $"Вставлено: {EditorLabels.Objects(objects.Count)}.";
    }

    public void FlattenSelectedComposition()
    {
        if (EditableSelection() is not { Composition: not null } source) return;
        var flattened = CompositionFlattener.Flatten(source, StampImages);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(flattened.Png));
        var provenance = source.Composition!.Assets.Count == 0
            ? "flattened user composition"
            : "flattened composition: " + string.Join(", ", source.Composition.Assets.Select(item => item.Provenance).Distinct());
        var asset = new MapAssetReference(hash, source.Name + " flattened", "image/png", "png",
            ProjectAssetStorage.Embedded, _clock(), provenance, "derived from user-provided content",
            embeddedContent: flattened.Png);
        var stamp = new SceneObject(source.Id, SceneObjectKind.Stamp, source.Name + " flattened",
            source.X, source.Y, source.Width, source.Height, source.RotationDegrees,
            "#FFFFFFFF", "#00000000", 0, source.Visible, source.Locked)
        { Stamp = new StampStyle(hash), ObjectOpacity = source.ObjectOpacity };
        Session.Execute(new FlattenCompositionCommand(source, stamp, asset)); AfterEdit();
    }

    public void CycleActiveGridGeometry()
    {
        if (ActiveLayer is not GridLayer { Locked: false, Grids.Count: > 0 } before) return;
        var current = before.Grids[0];
        var next = current.Geometry switch
        {
            GridGeometry.Square => GridGeometry.IsometricSquares,
            GridGeometry.IsometricSquares => GridGeometry.HexFlat,
            GridGeometry.HexFlat => GridGeometry.HexPointy,
            _ => GridGeometry.Square,
        };
        SetActiveGridGeometry(next);
    }

    public void ToggleActiveGridExport()
    {
        if (ActiveLayer is GridLayer { Grids.Count: > 0 } layer)
            SetActiveGridIncludeInExport(!layer.Grids[0].IncludeInExport);
    }

    public bool SetActiveGridGeometry(GridGeometry value) =>
        EditActiveGrid(grid => grid with { Geometry = value }, "геометрию");

    public bool SetActiveGridCellSize(double value) =>
        EditActiveGrid(grid => grid with { CellSize = value }, "размер ячейки");

    public bool SetActiveGridSubdivisions(double value)
    {
        if (!double.IsFinite(value) || value != Math.Truncate(value))
            return RejectGridValue("«Деления» должно быть целым числом.");
        return EditActiveGrid(grid => grid with { Subdivisions = checked((int)value) }, "деления");
    }

    public bool SetActiveGridLineWidth(double value) =>
        EditActiveGrid(grid => grid with { LineWidth = value }, "толщину линии");

    public bool SetActiveGridColor(string value) =>
        EditActiveGrid(grid => grid with { Color = value }, "цвет");

    public bool SetActiveGridOpacityPercent(double value) =>
        EditActiveGrid(grid => grid with { Opacity = value / 100 }, "непрозрачность");

    public bool SetActiveGridOffsetX(double value) =>
        EditActiveGrid(grid => grid with { OffsetX = value }, "смещение по X");

    public bool SetActiveGridOffsetY(double value) =>
        EditActiveGrid(grid => grid with { OffsetY = value }, "смещение по Y");

    public bool SetActiveGridRotation(double value) =>
        EditActiveGrid(grid => grid with { RotationDegrees = value }, "поворот");

    public bool SetActiveGridSnapEnabled(bool value) =>
        EditActiveGrid(grid => grid with { SnapEnabled = value }, "привязку");

    public bool SetActiveGridIncludeInExport(bool value) =>
        EditActiveGrid(grid => grid with { IncludeInExport = value }, "экспорт");

    private bool EditActiveGrid(Func<GridDefinition, GridDefinition> rewrite, string propertyName)
    {
        if (ActiveLayer is not GridLayer { Grids.Count: > 0 } before)
            return RejectGridValue("Сначала выберите слой сетки.");
        if (before.Locked)
        {
            StatusDetail = "Заблокированный слой сетки доступен только для чтения.";
            return false;
        }

        try
        {
            var current = before.Grids[0];
            var updated = rewrite(current);
            if (updated == current) return false;
            Session.Execute(new ReplaceGridLayerCommand(before, before with
            {
                Grids = [updated, .. before.Grids.Skip(1)],
            }));
            AfterEdit();
            return true;
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        {
            return RejectGridValue($"Не удалось изменить {propertyName}: значение вне допустимого диапазона.");
        }
    }

    private bool RejectGridValue(string message)
    {
        ReportInspectorError(message);
        StatusDetail = message;
        return false;
    }

    /// <summary>Global snap switch used by the G shortcut. The topmost visible grid is authoritative.</summary>
    public void ToggleGridSnap()
    {
        var before = Session.Document.Layers.OfType<GridLayer>()
            .LastOrDefault(layer => layer.Visible && !layer.Locked && layer.Grids.Count > 0);
        if (before is null)
        {
            StatusDetail = "Нет видимой разблокированной сетки для привязки.";
            return;
        }

        var current = before.Grids[0];
        Session.Execute(new ReplaceGridLayerCommand(before, before with
        {
            Grids = [current with { SnapEnabled = !current.SnapEnabled }, .. before.Grids.Skip(1)],
        }));
        AfterEdit();
        StatusDetail = current.SnapEnabled ? "Привязка к сетке выключена." : "Привязка к сетке включена.";
    }

    private GridDefinition? ActiveSnapGrid() => Session.Document.Layers
        .OfType<GridLayer>().Where(layer => layer.Visible)
        .SelectMany(layer => layer.Grids).LastOrDefault(grid => grid.SnapEnabled);

    public void SnapSelectedObjectToGrid()
    {
        if (EditableSelection() is not { } before || ActiveSnapGrid() is not { } grid) return;
        ReplaceSelected(before, GridSnapService.SnapObject(before, grid), "Snap object to grid");
    }

    public void SnapSelectedNodeToGrid()
    {
        if (EditableSelection() is not { } before || ActiveSnapGrid() is not { } grid
            || _selectedVectorNodeId is not { } nodeId) return;
        ReplaceSelected(before, GridSnapService.SnapNode(before, nodeId, grid), "Snap selected node to grid");
    }

    public void SnapSelectedPortalToGrid()
    {
        if (EditableSelection() is not { Line: not null } before || ActiveSnapGrid() is not { } grid
            || _selectedPortalId is not { } portalId) return;
        var after = GridSnapService.SnapPortal(before, portalId, grid, out var outcome);
        if (outcome == PortalSnapOutcome.AlreadySnapped)
        {
            StatusDetail = "Проход уже привязан к сетке.";
            return;
        }

        if (outcome == PortalSnapOutcome.NotRepresentable)
        {
            StatusDetail = "Точная привязка прохода невозможна без перемещения линии.";
            return;
        }

        ReplaceSelected(before, after, "Snap portal to grid");
        StatusDetail = "Проход привязан к сетке.";
    }

    public void AddNoteAtViewCentre()
    {
        var centre = ViewCentre();
        AddNoteAt(centre.X, centre.Y);
    }

    /// <summary>
    /// Creates one world-anchored note where the user pointed. The Notes tool had no canvas
    /// behaviour at all, so it looked active while the only way to make a note was a panel button
    /// that always dropped it in the middle of the view.
    /// </summary>
    public NoteId AddNoteAt(double worldX, double worldY)
    {
        var note = new MapNote(NoteId.New(), $"Заметка {Notes.Count + 1}", "Локальная заметка",
            NoteAnchorKind.World, worldX, worldY);
        Session.Execute(new AddNoteCommand(note));
        AfterEdit();
        StatusDetail = $"Создана заметка «{note.Title}».";
        return note.Id;
    }

    public void AddNoteForSelection()
    {
        if (Session.SelectedObject is not { } selected) return;
        Session.Execute(new AddNoteCommand(new MapNote(
            NoteId.New(), $"О {selected.Name}", "Локальная заметка объекта",
            NoteAnchorKind.Object, objectId: selected.Id)));
        AfterEdit();
    }

    public void EditNote(NoteId id, string title, string markdown)
    {
        var before = Notes.FirstOrDefault(note => note.Id == id);
        if (before is null || string.IsNullOrWhiteSpace(title)) return;
        try
        {
            Session.Execute(new ReplaceNoteCommand(before, before with { Title = title.Trim(), Markdown = markdown }));
            AfterEdit();
        }
        catch (ArgumentException ex) { StatusDetail = ex.Message; }
    }

    public void DeleteNote(NoteId id)
    {
        if (Notes.All(note => note.Id != id)) return;
        Session.Execute(new DeleteNoteCommand(Session.Document, id));
        AfterEdit();
    }

    public void GoToNote(NoteId id)
    {
        var note = Notes.FirstOrDefault(item => item.Id == id);
        if (note is null) return;
        var point = (X: note.WorldX, Y: note.WorldY);
        if (note.AnchorKind == NoteAnchorKind.Object && note.ObjectId is { } objectId)
        {
            var target = Session.Document.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects)
                .FirstOrDefault(item => item.Id == objectId);
            if (target is null) { StatusDetail = "Якорный объект заметки удалён."; return; }
            Session.SelectedObjectId = target.Id;
            point = (target.X + target.Width / 2, target.Y + target.Height / 2);
        }
        var viewport = Session.Viewport;
        viewport.Set(point.X - viewport.ViewWidth / (2 * viewport.Scale),
            point.Y - viewport.ViewHeight / (2 * viewport.Scale), viewport.Scale);
        Raise(nameof(ViewportStatus)); SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RenameActiveLayer(string name)
    {
        if (ActiveLayer is not { } layer || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Session.Execute(UpdateLayerCommand.Rename(layer, name.Trim()));
        AfterEdit();
    }

    public void ToggleActiveLayerVisibility() => ToggleLayerVisibility(_activeLayerId);

    public void ToggleActiveLayerLock() => ToggleLayerLock(_activeLayerId);

    /// <summary>
    /// Hides or shows one named layer without making it active. The list rows used to select the
    /// layer first, so clicking an eye quietly moved the target of the next new object.
    /// </summary>
    public void ToggleLayerVisibility(LayerId? layerId)
    {
        if (layerId is not { } id || Session.Document.FindLayer(id) is not { } layer)
        {
            return;
        }

        Session.Execute(UpdateLayerCommand.SetVisible(layer, !layer.Visible));
        AfterEdit();
    }

    /// <summary>Locks or unlocks one named layer without making it active.</summary>
    public void ToggleLayerLock(LayerId? layerId)
    {
        if (layerId is not { } id || Session.Document.FindLayer(id) is not { } layer)
        {
            return;
        }

        Session.Execute(UpdateLayerCommand.SetLocked(layer, !layer.Locked));
        AfterEdit();
    }

    public void CycleActiveLayerBlend()
    {
        if (ActiveLayer is not { } layer)
        {
            return;
        }

        var next = layer.Blend switch
        {
            "Normal" => "Multiply",
            "Multiply" => "Screen",
            _ => "Normal",
        };
        Session.Execute(UpdateLayerCommand.SetBlend(layer, next));
        AfterEdit();
    }

    public bool BeginTextPlacement(double worldX, double worldY)
    {
        if (ActiveTool != EditorTool.Text) return false;
        if (ActiveObjectLayer() is not { } layer)
        {
            StatusDetail = "Нет видимого разблокированного слоя объектов — текст не создан.";
            return false;
        }

        _textPlacementStart = SnapVectorGesturePoint(new VectorPoint(worldX, worldY));
        _textPreviewLayerId = layer.Id;
        _textPlacementTemplate = TextInheritFromSelection && Session.SelectedObject?.Text is not null
            ? Session.SelectedObject
            : null;
        _textPlacementStyle = (_textPlacementTemplate?.Text ?? new TextStyle()) with
        {
            Content = "Новый текст\nна карте",
        };
        _textPreviewObject = BuildTextPlacement(_textPlacementStart.Value, _textPlacementStart.Value, preview: true);
        Raise(nameof(PlacementPreviewObjects));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        return true;
    }

    public void ContinueTextPlacement(double worldX, double worldY)
    {
        if (_textPlacementStart is not { } start) return;
        var end = SnapVectorGesturePoint(new VectorPoint(worldX, worldY));
        _textPreviewObject = BuildTextPlacement(start, end, preview: true);
        Raise(nameof(PlacementPreviewObjects));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void EndTextPlacement()
    {
        if (_textPlacementStart is null || _textPreviewLayerId is not { } layerId
            || _textPreviewObject is not { } preview
            || Session.Document.FindLayer(layerId) is not ObjectLayer)
        {
            CancelTextPlacement(); return;
        }
        var added = preview with { ObjectOpacity = _textPlacementTemplate?.ObjectOpacity ?? 1 };
        Session.Execute(new AddObjectCommand(layerId, added));
        Session.SelectedObjectId = added.Id;
        _activeLayerId = layerId;
        CancelTextPlacement(clearOnly: true);
        AfterEdit();
    }

    public void CancelTextPlacement() => CancelTextPlacement(clearOnly: false);

    private void CancelTextPlacement(bool clearOnly)
    {
        _textPlacementStart = null;
        _textPlacementStyle = null;
        _textPlacementTemplate = null;
        _textPreviewObject = null;
        _textPreviewLayerId = null;
        Raise(nameof(PlacementPreviewObjects));
        if (!clearOnly) InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    private SceneObject BuildTextPlacement(VectorPoint start, VectorPoint end, bool preview)
    {
        var dragged = Math.Abs(end.X - start.X) >= 8 || Math.Abs(end.Y - start.Y) >= 8;
        var left = dragged ? Math.Min(start.X, end.X) : start.X - 180;
        var top = dragged ? Math.Min(start.Y, end.Y) : start.Y - 70;
        var width = dragged ? Math.Max(32, Math.Abs(end.X - start.X)) : 360;
        var height = dragged ? Math.Max(24, Math.Abs(end.Y - start.Y)) : 140;
        var template = _textPlacementTemplate;
        return new SceneObject(
            _textPreviewObject?.Id ?? ObjectId.New(), SceneObjectKind.Text,
            EntityNameGenerator.Text(Session.Document, _textPlacementStyle!.Content),
            left, top, width, height, template?.RotationDegrees ?? 0,
            "#FF20242B", "#FFFFFFFF", 0, true, false)
        {
            Text = _textPlacementStyle,
            Sublayer = template?.Sublayer ?? 0,
            ObjectOpacity = preview ? .55 : template?.ObjectOpacity ?? 1,
        };
    }

    public bool SetActiveLayerOpacity(double percent)
    {
        if (ActiveLayer is not { Locked: false } layer || !double.IsFinite(percent)) return false;
        var value = Math.Clamp(percent / 100, 0, 1);
        if (Math.Abs(value - layer.Opacity) < 0.000001) return false;
        Session.Execute(UpdateLayerCommand.SetOpacity(layer, value)); AfterEdit(); return true;
    }

    public bool SetActiveLayerBlend(string blend)
    {
        if (ActiveLayer is not { Locked: false } layer || !BlendModes.IsSupported(blend)) return false;
        if (layer.Blend == blend) return false;
        Session.Execute(UpdateLayerCommand.SetBlend(layer, blend)); AfterEdit(); return true;
    }

    public LayerEffects? ActiveObjectLayerEffects => (ActiveLayer as ObjectLayer)?.Effects;

    public ObjectSublayerState? ActiveObjectSublayerState(int sublayer) =>
        ActiveLayer is ObjectLayer layer ? layer.StateFor(Math.Clamp(sublayer, -10, 10)) : null;

    public bool SetActiveObjectLayerEffects(LayerEffects value)
    {
        if (ActiveLayer is not ObjectLayer { Locked: false } before || value is null) return false;
        var color = AppearanceColor.Normalize(value.ShadowColor);
        if (color is null) return false;
        var safe = value with
        {
            ShadowColor = color,
            OuterShadowOpacity = Math.Clamp(value.OuterShadowOpacity, 0, 1),
            InnerShadowOpacity = Math.Clamp(value.InnerShadowOpacity, 0, 1),
            ShadowOffsetX = Math.Clamp(value.ShadowOffsetX, -512, 512),
            ShadowOffsetY = Math.Clamp(value.ShadowOffsetY, -512, 512),
            ShadowBlur = Math.Clamp(value.ShadowBlur, 0, 256),
        };
        if (safe == before.Effects) return false;
        Session.Execute(UpdateLayerCommand.Replace(before, before with { Effects = safe }, "Object layer effects"));
        AfterEdit(); return true;
    }

    public bool SetActiveObjectSublayerState(int sublayer, bool? visible = null, bool? locked = null)
    {
        if (ActiveLayer is not ObjectLayer { Locked: false } before) return false;
        sublayer = Math.Clamp(sublayer, -10, 10);
        var old = before.StateFor(sublayer);
        var next = old with { Visible = visible ?? old.Visible, Locked = locked ?? old.Locked };
        if (next == old) return false;
        var states = before.SublayerStates.Where(item => item.Sublayer != sublayer).ToList();
        if (!next.Visible || next.Locked) states.Add(next);
        states.Sort((left, right) => left.Sublayer.CompareTo(right.Sublayer));
        Session.Execute(UpdateLayerCommand.Replace(before,
            before with { SublayerStates = states }, "Object sublayer state"));
        AfterEdit(); return true;
    }

    public void ReorderActiveLayer(bool up)
    {
        if (ActiveLayer is null)
        {
            return;
        }

        if (!LayerOrderPolicy.CanMove(Session.Document, ActiveLayer.Id, up))
        {
            StatusDetail = ActiveLayer switch
            {
                LandLayer when up => "Слой земли остаётся ниже слоёв объектов.",
                ObjectLayer when !up => "Слой объектов остаётся выше слоёв земли.",
                _ => up ? "Слой уже находится сверху." : "Слой уже находится снизу.",
            };
            return;
        }

        Session.Execute(new ReorderLayerCommand(ActiveLayer.Id, up));
        AfterEdit();
    }

    public void DuplicateActiveLayer()
    {
        if (ActiveLayer is ObjectLayer owned && (!owned.PlacementPaths.IsEmpty || !owned.PlacementAreas.IsEmpty))
        { StatusDetail = "Перед дублированием слоя превратите его пути и области в обычные объекты."; return; }
        if (ActiveLayer is null or BaseColorLayer)
        {
            return;
        }

        var command = new DuplicateLayerCommand(ActiveLayer.Id);
        Session.Execute(command);
        _activeLayerId = command.CopyLayerId;
        AfterEdit();
    }

    public void DeleteActiveLayer()
    {
        if (ActiveLayer is null or BaseColorLayer)
        {
            return;
        }

        var deleted = ActiveLayer.Id;
        Session.Execute(new DeleteLayerCommand(Session.Document, deleted));
        _activeLayerId = Session.Document.Layers[Session.Document.Layers.Count - 1].Id;
        AfterEdit();
    }

    public void ToggleActiveMask()
    {
        if (ActiveLayer is null or BaseColorLayer)
        {
            return;
        }

        Session.Execute(ActiveLayer.Mask is null
            ? UpdateMaskCommand.AddMask(ActiveLayer)
            : UpdateMaskCommand.SetEnabled(ActiveLayer, !ActiveLayer.Mask.Enabled));
        AfterEdit();
        Raise(nameof(MaskEditLayerId));
        Raise(nameof(Notes));
        Raise(nameof(SelectedTextFontResolution));
    }

    public void RemoveActiveMask()
    {
        if (ActiveLayer is not { Mask: not null } layer || layer is BaseColorLayer) return;
        Session.Execute(UpdateMaskCommand.RemoveMask(layer)); AfterEdit();
    }

    public void CopyActiveMask()
    {
        if (ActiveLayer is not { Mask: not null } layer) return;
        _copiedMaskLayerId = layer.Id;
        StatusDetail = "Маска скопирована; исходный слой не изменён.";
    }

    public bool PasteCopiedMask()
    {
        if (_copiedMaskLayerId is not { } sourceId
            || Session.Document.FindLayer(sourceId) is not { Mask: not null } source
            || ActiveLayer is not { } target || target is BaseColorLayer || source.Id == target.Id)
        {
            StatusDetail = "Выберите другой совместимый слой для копии маски.";
            return false;
        }
        Session.Execute(UpdateMaskCommand.CopyMask(source, target)); AfterEdit(); return true;
    }

    // ------------------------------------------------------------- raster tool targets

    /// <summary>Whether one layer is the kind of surface a given raster tool paints on.</summary>
    private static bool FitsRasterTool(MapLayer layer, EditorTool tool) => tool switch
    {
        EditorTool.Brush => layer is BrushLayer,
        EditorTool.Land => layer is LandLayer,
        EditorTool.Mask => layer is not BaseColorLayer && layer.Mask is not null,
        _ => false,
    };

    /// <summary>
    /// The layer a raster tool would paint on: the active one when it fits, otherwise the topmost
    /// visible unlocked layer of the kind the tool needs.
    /// <para>
    /// The vector tools have always resolved their layer like this - see
    /// <see cref="ActiveObjectLayer"/> - which is why Stamp, Line and Shape "just work". Brush,
    /// Land and Mask instead demanded that the right kind of layer was already active, and a new
    /// map starts on its object layer, so all three refused every stroke and looked broken.
    /// </para>
    /// <para>
    /// A layer that fits but is hidden or locked is deliberately NOT replaced by another: the user
    /// locked it on purpose, and quietly painting somewhere else would be worse than refusing.
    /// </para>
    /// </summary>
    private MapLayer? RasterTargetLayer(EditorTool tool)
    {
        if (ActiveLayer is { } active && FitsRasterTool(active, tool))
        {
            return active is { Visible: true, Locked: false } ? active : null;
        }

        return Session.Document.Layers.LastOrDefault(
            layer => layer is { Visible: true, Locked: false } && FitsRasterTool(layer, tool));
    }

    /// <summary>True when the active tool has somewhere to paint right now.</summary>
    public bool RasterToolReady =>
        ActiveTool is not (EditorTool.Brush or EditorTool.Land or EditorTool.Mask)
        || RasterTargetLayer(ActiveTool) is not null;

    /// <summary>
    /// What the panel tells the user about the active raster tool: either which layer it will
    /// paint on, or what is missing. Empty for the tools this does not apply to.
    /// </summary>
    public string RasterToolHint
    {
        get
        {
            if (ActiveTool is not (EditorTool.Brush or EditorTool.Land or EditorTool.Mask))
            {
                return "";
            }

            return RasterTargetLayer(ActiveTool) is { } target
                ? $"Рисует по слою «{target.Name}»."
                : RasterToolRefusal(ActiveTool);
        }
    }

    /// <summary>The label of the panel action that makes the active raster tool usable.</summary>
    public string RasterToolFixLabel => ActiveTool switch
    {
        EditorTool.Brush => "Создать слой кисти и продолжить",
        EditorTool.Land => "Создать слой земли и продолжить",
        EditorTool.Mask => "Добавить маску активному слою",
        _ => "",
    };

    private string RasterToolRefusal(EditorTool tool)
    {
        var blocked = ActiveLayer is { } active && FitsRasterTool(active, tool)
            && (!active.Visible || active.Locked);
        if (blocked)
        {
            return $"Слой «{ActiveLayer!.Name}» скрыт или заблокирован — штрих не применён.";
        }

        return tool switch
        {
            EditorTool.Mask => "Ни у одного слоя нет маски. Добавьте маску слою — кнопкой ниже или на вкладке «Слои».",
            EditorTool.Land => "В карте нет слоя земли. Создайте его кнопкой ниже.",
            _ => "В карте нет слоя кисти. Создайте его кнопкой ниже.",
        };
    }

    /// <summary>
    /// Creates whatever the active raster tool is missing and leaves it ready to paint. One
    /// command, so a wrong click is one undo.
    /// </summary>
    public bool CreateLayerForActiveTool()
    {
        switch (ActiveTool)
        {
            case EditorTool.Brush:
                AddBrushLayer();
                StatusDetail = "Создан слой кисти. Можно рисовать.";
                return true;
            case EditorTool.Land:
                AddLandLayer();
                StatusDetail = "Создан слой земли. Можно рисовать.";
                return true;
            case EditorTool.Mask:
                // A mask belongs to an existing layer, so pick one that can hold it first.
                if (ActiveLayer is null or BaseColorLayer)
                {
                    if (Session.Document.Layers.LastOrDefault(
                        layer => layer is not BaseColorLayer) is not { } host)
                    {
                        StatusDetail = "Нет слоя, которому можно добавить маску.";
                        return false;
                    }

                    SelectLayer(host.Id);
                }

                if (ActiveLayer?.Mask is not null)
                {
                    StatusDetail = $"У слоя «{ActiveLayer.Name}» уже есть маска.";
                    return false;
                }

                ToggleActiveMask();
                StatusDetail = $"Маска добавлена слою «{ActiveLayer?.Name}». Можно рисовать.";
                return true;
            default:
                return false;
        }
    }

    /// <summary>Makes one layer active, with the same notifications a click in the panel sends.</summary>
    private void UseAsActiveLayer(MapLayer layer)
    {
        if (_activeLayerId != layer.Id)
        {
            SelectLayer(layer.Id);
        }
    }

    public bool BeginStroke(double worldX, double worldY, double pressure = 1)
    {
        if (_editingStampForeground)
        {
            if (ActiveTool != EditorTool.Brush || Session.Document.StampForeground is not { } foreground)
            {
                StatusDetail = "Foreground-поверхность недоступна для рисования.";
                return false;
            }
            BrushSettings = BrushSettings with
            {
                Seed = GestureSeed.From(Session.Revision, "stamp-foreground-stroke", 0, ++_brushSeedCounter),
            };
            _stroke = new RasterStrokeSession(foreground.Tiles, BrushSettings);
            _stroke.AddPoint(worldX, worldY, pressure);
            InvalidateScene(EditorChangeReason.CanvasInvalidation);
            return true;
        }

        if (RasterTargetLayer(ActiveTool) is not { } layer)
        {
            StatusDetail = RasterToolRefusal(ActiveTool);
            return false;
        }

        // Painting and the layers panel have to agree: the stroke is committed against
        // _activeLayerId, so the layer the tool fell back to becomes the active one.
        UseAsActiveLayer(layer);

        TileMap target = ActiveTool switch
        {
            EditorTool.Brush => ((BrushLayer)layer).Tiles,
            EditorTool.Mask => layer.Mask!.Tiles,
            _ => ((LandLayer)layer).GeometryTiles,
        };

        // The stroke's randomness is decided here, BEFORE any command exists: the committed
        // tiles never depend on replay, so undo/redo restore bytes instead of re-rolling.
        BrushSettings = BrushSettings with
        {
            Seed = GestureSeed.From(Session.Revision, "brush-stroke", 0, ++_brushSeedCounter),
        };

        _stroke = new RasterStrokeSession(target, BrushSettings);
        _stroke.AddPoint(worldX, worldY, pressure);
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        return true;
    }

    public void ContinueStroke(double worldX, double worldY, double pressure = 1)
    {
        if (_stroke is null)
        {
            return;
        }

        _stroke.AddPoint(worldX, worldY, pressure);
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void EndStroke()
    {
        var activeLayerId = _activeLayerId;
        if (_stroke is null || (!_editingStampForeground && activeLayerId is null))
        {
            return;
        }

        var stroke = _stroke;
        _stroke = null;
        if (stroke.IsEmpty)
        {
            InvalidateScene(EditorChangeReason.CanvasInvalidation);
            return;
        }

        if (_editingStampForeground)
        {
            var (Before, After) = stroke.Commit();
            Session.Execute(new StampForegroundStrokeCommand(
                Before, After, BrushSettings.Eraser ? "Erase stamp foreground" : "Paint stamp foreground"));
        }
        else if (ActiveTool == EditorTool.Mask)
        {
            var (Before, After) = stroke.CommitCoverage(
                missingTileValue: 255,
                subtract: MaskMode == MaskPaintMode.Hide);
            Session.Execute(new MaskStrokeCommand(activeLayerId.GetValueOrDefault(), Before, After));
        }
        else if (ActiveTool == EditorTool.Land)
        {
            var (Before, After) = stroke.CommitCoverage(missingTileValue: 0, subtract: LandSubtract);
            Session.Execute(new BrushStrokeCommand(
                activeLayerId.GetValueOrDefault(), Before, After,
                LandSubtract ? "Subtract land" : "Add land"));
        }
        else
        {
            var (Before, After) = stroke.Commit();
            Session.Execute(new BrushStrokeCommand(
                activeLayerId.GetValueOrDefault(), Before, After,
                BrushSettings.Eraser ? "Erase stroke" : "Brush stroke"));
        }

        AfterEdit();
    }

    public void ApplyLandShape(LandShapeKind kind)
    {
        var centre = Session.Viewport.ScreenToWorld(
            Session.Viewport.ViewWidth / 2, Session.Viewport.ViewHeight / 2);
        const double size = 512;
        var bounds = new WorldBounds(
            centre.X - (size / 2), centre.Y - (size / 2),
            centre.X + (size / 2), centre.Y + (size / 2));
        ApplyLandShape(kind, bounds);
    }

    public bool ApplyLandShape(
        LandShapeKind kind, WorldBounds bounds, IReadOnlyList<LandPoint>? points = null)
    {
        if (RasterTargetLayer(EditorTool.Land) is not LandLayer land)
        {
            StatusDetail = RasterToolRefusal(EditorTool.Land);
            return false;
        }

        UseAsActiveLayer(land);
        var centre = ((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2);
        IReadOnlyList<LandPoint> polygon = kind == LandShapeKind.Polygon
            ? points is { Count: >= 3 } ? points :
            [
                new LandPoint(bounds.Left, bounds.Bottom),
                new LandPoint(centre.Item1, bounds.Top),
                new LandPoint(bounds.Right, bounds.Bottom),
            ]
            : [];
        var (Before, After) = LandRasterizer.ApplyShape(land.GeometryTiles, new LandShape
        {
            Kind = kind,
            Bounds = bounds,
            Points = polygon,
            GridSize = 64,
        }, LandSubtract);
        if (Before.Count == 0)
        {
            return false;
        }

        Session.Execute(new BrushStrokeCommand(
            land.Id, Before, After,
            LandSubtract ? $"Subtract land {kind}" : $"Add land {kind}"));
        AfterEdit();
        return true;
    }

    public void CycleActiveLandStyle()
    {
        if (ActiveLayer is not LandLayer land)
        {
            return;
        }

        var coast = land.EdgeStyle.RippleCount == 0;
        var style = land.EdgeStyle with
        {
            FillColor = coast ? "#FF8CA36B" : "#FFC69B62",
            OutlineColor = coast ? "#FF405533" : "#FF65452B",
            RippleCount = coast ? 3 : 0,
            RippleSpacing = coast ? 12 : 10,
            OuterShadowOpacity = coast ? 0.3 : 0.15,
        };
        Session.Execute(new UpdateLandStyleCommand(land, style));
        AfterEdit();
    }

    public void MoveActiveLandArea(int deltaX, int deltaY)
    {
        if (ActiveLayer is not LandLayer land || land.Locked)
        {
            return;
        }

        var centre = Session.Viewport.ScreenToWorld(
            Session.Viewport.ViewWidth / 2, Session.Viewport.ViewHeight / 2);
        var (Before, After) = LandRasterizer.MoveRegion(
            land.GeometryTiles,
            new WorldBounds(centre.X - 256, centre.Y - 256, centre.X + 256, centre.Y + 256),
            deltaX,
            deltaY);
        if (Before.Count == 0)
        {
            return;
        }

        Session.Execute(new BrushStrokeCommand(land.Id, Before, After, "Move land area"));
        AfterEdit();
    }

    public bool MoveActiveLandArea(WorldBounds region, int deltaX, int deltaY, bool snapToGrid)
    {
        if (ActiveLayer is not LandLayer land || land.Locked) return false;
        if (snapToGrid && ActiveSnapGrid() is { } grid)
        {
            var size = grid.CellSize / Math.Max(1, grid.Subdivisions);
            deltaX = (int)Math.Round(deltaX / size) * (int)Math.Max(1, Math.Round(size));
            deltaY = (int)Math.Round(deltaY / size) * (int)Math.Max(1, Math.Round(size));
        }
        var (before, after) = LandRasterizer.MoveRegion(land.GeometryTiles, region, deltaX, deltaY);
        if (before.Count == 0) return false;
        Session.Execute(new BrushStrokeCommand(land.Id, before, after, "Move selected land area"));
        AfterEdit(); return true;
    }

    public bool ClearActiveLand()
    {
        if (ActiveLayer is not LandLayer { Locked: false } land || !land.GeometryTiles.Keys.Any()) return false;
        var before = land.GeometryTiles.Keys.ToDictionary(key => key, key => land.GeometryTiles.TryGet(key));
        var after = before.Keys.ToDictionary(key => key, _ => (byte[]?)null);
        Session.Execute(new BrushStrokeCommand(land.Id, before, after, "Clear land"));
        AfterEdit(); return true;
    }

    public bool SetActiveLandStyle(Func<LandEdgeStyle, LandEdgeStyle> change)
    {
        if (ActiveLayer is not LandLayer { Locked: false } land) return false;
        var proposed = change(land.EdgeStyle);
        var updated = proposed with
        {
            FillColor = AppearanceColor.Normalize(proposed.FillColor) ?? land.EdgeStyle.FillColor,
            OutlineColor = AppearanceColor.Normalize(proposed.OutlineColor) ?? land.EdgeStyle.OutlineColor,
            OutlineWidth = Math.Clamp(proposed.OutlineWidth, 0, 64),
            InnerShadowOpacity = Math.Clamp(proposed.InnerShadowOpacity, 0, 1),
            OuterShadowOpacity = Math.Clamp(proposed.OuterShadowOpacity, 0, 1),
            ShadowOffsetX = Math.Clamp(proposed.ShadowOffsetX, -512, 512),
            ShadowOffsetY = Math.Clamp(proposed.ShadowOffsetY, -512, 512),
            ShadowBlur = Math.Clamp(proposed.ShadowBlur, 0, 64),
            RippleColor = AppearanceColor.Normalize(proposed.RippleColor) ?? land.EdgeStyle.RippleColor,
            RippleWidth = Math.Clamp(proposed.RippleWidth, 0, 32),
            RippleSpacing = Math.Clamp(proposed.RippleSpacing, 1, 128),
            RippleCount = Math.Clamp(proposed.RippleCount, 0, 12),
        };
        if (updated == land.EdgeStyle) return false;
        Session.Execute(new UpdateLandStyleCommand(land, updated)); AfterEdit(); return true;
    }

    /// <summary>
    /// The map background colour. The command existed and the layer is mandatory, but nothing in
    /// the editor could reach it: the layer property sheet hid itself for the base colour layer,
    /// so the background could only ever be chosen once, in the new-map wizard.
    /// </summary>
    public void SetBaseColor(string? colorText)
    {
        if (RequireColorInput(colorText) is not { } color)
        {
            return;
        }

        var layer = Session.Document.BaseColor;
        if (string.Equals(layer.Color, color, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Session.Execute(UpdateLayerCommand.SetBaseColor(layer, color));
        AfterEdit();
    }

    public void CancelStroke()
    {
        if (_stroke is null)
        {
            return;
        }

        _stroke = null;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    /// <summary>
    /// One canvas click. Outside an open group a grouped object stands for its whole group;
    /// inside the open group the click picks the child itself. Additive (Ctrl) toggles the unit.
    /// </summary>
    public void SelectAt(double worldX, double worldY, bool additive = false)
    {
        var hit = SceneGeometry.HitTest(Session.Document, worldX, worldY);
        SelectHit(hit, additive);
    }

    private void SelectHit(SceneObject? hit, bool additive)
    {
        if (hit is null)
        {
            if (!additive)
            {
                Session.ClearSelection();
            }

            AfterSelectionChange();
            return;
        }

        var unit = SelectionUnit(hit);
        if (additive)
        {
            if (unit.All(id => Session.SelectedObjectIds.Contains(id)))
            {
                Session.SetSelection(Session.SelectedObjectIds.Where(id => !unit.Contains(id)));
            }
            else
            {
                Session.SetSelection([.. Session.SelectedObjectIds, .. unit], hit.Id);
            }
        }
        else if (unit.All(id => Session.SelectedObjectIds.Contains(id))
            && (_openGroupId is null || hit.GroupId != _openGroupId))
        {
            // Clicking inside the current selection keeps it, so a drag can move the whole set.
            // Inside an open group the click means "this child", never "keep the group".
            Session.SetSelection(Session.SelectedObjectIds, hit.Id);
        }
        else
        {
            Session.SetSelection(unit, hit.Id);
        }

        AfterSelectionChange();
    }

    /// <summary>The ids one clicked object stands for: its whole group, unless that group is open.</summary>
    private IReadOnlyList<ObjectId> SelectionUnit(SceneObject hit) =>
        hit.GroupId is { } groupId && _openGroupId != groupId
            ? [.. SceneGeometry.GroupMembers(Session.Document, groupId).Select(o => o.Id)]
            : [hit.Id];

    public void SelectObject(ObjectId id, bool additive = false)
    {
        var layer = DocumentEditor.FindLayerOf(Session.Document, id);
        if (layer is null)
        {
            return;
        }

        if (additive)
        {
            Session.ToggleSelection(id);
        }
        else
        {
            Session.SetSelection([id], id);
        }

        _activeLayerId = layer.Id;
        AfterSelectionChange();
        Raise(nameof(ActiveLayer));
        Raise(nameof(ActiveLayerId));
        Raise(nameof(ActiveLayerSummary));
    }

    /// <summary>Selects one existing group whole, in one action.</summary>
    public void SelectGroup(Guid groupId)
    {
        var members = SceneGeometry.GroupMembers(Session.Document, groupId);
        if (members.Count == 0)
        {
            return;
        }

        Session.SetSelection(members.Select(o => o.Id));
        AfterSelectionChange();
    }

    public void DeselectObject()
    {
        Session.ClearSelection();
        AfterSelectionChange();
    }

    /// <summary>The ` / ~ shortcut: the whole previous set comes back, not one object.</summary>
    public void RestoreLastSelection()
    {
        if (Session.RestorePreviousSelection())
        {
            AfterSelectionChange();
        }
    }

    // ---------------------------------------------------------------- select all

    /// <summary>Live search text of the objects panel; Ctrl+A honours it.</summary>
    public string ObjectSearchText
    {
        get => _objectSearchText;
        set => SetField(ref _objectSearchText, value ?? "");
    }

    public ObjectTypeFilter ObjectTypeFilter
    {
        get => _objectTypeFilter;
        set => SetField(ref _objectTypeFilter, value);
    }

    /// <summary>The rows the objects panel shows for the current search, filter and open group.</summary>
    public IReadOnlyList<ObjectTreeRow> ObjectTree => ObjectTreeRows.Build(
        Session.Document, _objectSearchText, _objectTypeFilter, _openGroupId,
        [.. Session.SelectedObjectIds]);

    /// <summary>
    /// Ctrl+A: selects the visible, unlocked objects that match BOTH the panel search and the
    /// type filter. Hidden and locked objects never enter; the tree stays their only door.
    /// </summary>
    public void SelectAllByFilter()
    {
        var availableIds = AvailableObjectIds();
        var matching = new List<ObjectId>();
        foreach (var row in ObjectTree)
        {
            if (row.Kind == ObjectTreeRowKind.Group && row.GroupId is { } groupId)
            {
                var members = SceneGeometry.GroupMembers(Session.Document, groupId);
                if (members.Count > 0 && members.All(member => availableIds.Contains(member.Id)))
                {
                    matching.AddRange(members.Select(member => member.Id));
                }

                continue;
            }

            if (row.Kind is ObjectTreeRowKind.Object or ObjectTreeRowKind.GroupChild
                && row.Object is { } sceneObject
                && availableIds.Contains(sceneObject.Id))
            {
                matching.Add(sceneObject.Id);
            }
        }

        Session.SetSelection(matching);
        StatusDetail = matching.Count == 0
            ? "Под текущий поиск и фильтр не попал ни один видимый объект."
            : $"Выбрано: {EditorLabels.Objects(matching.Count)}.";
        AfterSelectionChange();
    }

    private HashSet<ObjectId> AvailableObjectIds() =>
    [
        .. Session.Document.Layers
            .OfType<ObjectLayer>()
            .Where(layer => layer.Visible && !layer.Locked)
            .SelectMany(layer => layer.Objects.Where(sceneObject =>
                layer.StateFor(sceneObject.Sublayer) is { Visible: true, Locked: false }))
            .Where(sceneObject => sceneObject.Visible && !sceneObject.Locked)
            .Select(sceneObject => sceneObject.Id),
    ];

    private bool IsObjectAvailable(SceneObject sceneObject)
    {
        if (!DocumentRevisionIndex.For(Session.Document).TryFind(sceneObject.Id, out var location)
            || Session.Document.Layers[location.LayerIndex] is not ObjectLayer { Visible: true, Locked: false } layer)
            return false;
        return location.Object is { Visible: true, Locked: false }
            && layer.StateFor(location.Object.Sublayer) is { Visible: true, Locked: false };
    }

    // ---------------------------------------------------------------- groups

    /// <summary>At least two existing, ungrouped objects are selected.</summary>
    public bool CanGroupSelection
    {
        get
        {
            var selected = Session.SelectedObjects;
            return selected.Count >= 2 && selected.All(o => o.GroupId is null);
        }
    }

    /// <summary>
    /// Creates a named group from the whole selection as one undoable command and selects the
    /// new group. A blank name receives the Stage 14 automatic group label. Refusals (too few
    /// objects or partial regrouping) change nothing and explain themselves in the status line.
    /// </summary>
    public bool GroupSelection(string? name)
    {
        var selected = Session.SelectedObjects;
        if (selected.Count < 2)
        {
            StatusDetail = "Для группы выберите минимум два объекта.";
            return false;
        }

        SetGroupCommand command;
        try
        {
            var groupName = string.IsNullOrWhiteSpace(name)
                ? EntityNameGenerator.Group(selected.Count)
                : name;
            command = SetGroupCommand.Group(
                Session.Document, [.. selected.Select(o => o.Id)], groupName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            StatusDetail = ex.Message;
            return false;
        }

        Session.Execute(command);
        Session.SetSelection(selected.Select(o => o.Id));
        AfterEdit();
        StatusDetail = $"Создана группа «{SelectedGroupName}»: {EditorLabels.Objects(selected.Count)}.";
        return true;
    }

    /// <summary>Dissolves the one fully selected group. Anything else is refused untouched.</summary>
    public bool UngroupSelection()
    {
        if (SelectedGroupId is not { } groupId)
        {
            StatusDetail = "Разгруппировать можно одну полностью выбранную группу.";
            return false;
        }

        var members = SceneGeometry.GroupMembers(Session.Document, groupId);
        var memberIds = members.Select(o => o.Id).ToList();
        var name = members[0].GroupName;
        if (_openGroupId == groupId)
        {
            _openGroupId = null;
        }

        Session.Execute(SetGroupCommand.Ungroup(Session.Document, memberIds));
        Session.SetSelection(memberIds);
        AfterEdit();
        StatusDetail = $"Группа «{name ?? "Группа"}» разгруппирована.";
        return true;
    }

    /// <summary>Renames the currently selected group as one undoable command.</summary>
    public bool RenameSelectedGroup(string? name)
    {
        if (SelectedGroupId is not { } groupId)
        {
            StatusDetail = "Сначала выберите группу целиком.";
            return false;
        }

        var memberIds = SceneGeometry.GroupMembers(Session.Document, groupId)
            .Select(o => o.Id).ToList();
        try
        {
            Session.Execute(SetGroupCommand.Rename(Session.Document, groupId, name ?? ""));
        }
        catch (ArgumentException ex)
        {
            StatusDetail = ex.Message;
            return false;
        }

        Session.SetSelection(memberIds);
        AfterEdit();
        StatusDetail = $"Группа переименована: «{SelectedGroupName}».";
        return true;
    }

    /// <summary>Opens the selected group so its children become directly selectable.</summary>
    public void EnterSelectedGroup()
    {
        var selected = Session.SelectedObjects;
        var groupId = SelectedGroupId ?? (selected.Count > 0 ? selected[0].GroupId : null);
        if (groupId is null)
        {
            StatusDetail = "Сначала выберите группу.";
            return;
        }

        EnterGroup(groupId.Value);
    }

    public void EnterGroup(Guid groupId)
    {
        if (SceneGeometry.GroupMembers(Session.Document, groupId).Count == 0)
        {
            return;
        }

        _openGroupId = groupId;
        AfterSelectionChange();
    }

    /// <summary>The breadcrumb: closes the open group and selects it whole again.</summary>
    public void ExitGroup()
    {
        if (_openGroupId is not { } groupId)
        {
            return;
        }

        _openGroupId = null;
        SelectGroup(groupId);
    }

    /// <summary>
    /// Esc ladder: an open group closes first (selecting the group whole); a second press clears
    /// the selection. Returns false when there was nothing to do.
    /// </summary>
    /// <summary>
    /// One Escape undoes one layer of "I am in the middle of something", from the most transient
    /// to the least: a live gesture, an armed placement, an open group, the selection, and finally
    /// the drawing tool itself. The last step matters as much as the first - after Escape the
    /// pointer is always back to plain selecting, so nothing can leave the user stuck inside a
    /// tool with no obvious way out.
    /// </summary>
    public bool HandleEscape()
    {
        if (CancelEditableAreaGesture())
        { CanvasGestureCancelled?.Invoke(this, EventArgs.Empty); PendingPlacement = null; StatusDetail = "Правка области отменена."; return true; }
        if (CancelEditablePathGesture())
        { CanvasGestureCancelled?.Invoke(this, EventArgs.Empty); StatusDetail = "Правка пути отменена."; return true; }
        if (_pendingShapeBoolean is not null)
        {
            CancelShapeBooleanPreview();
            return true;
        }

        // A drag, marquee or node grab is as much "an action in progress" as a half-drawn shape,
        // and the canvas host has to forget its own matching state in the same breath.
        if (_vectorElementDrag is not null || _vectorPoints is not null
            || _penPoints is not null || _landShapeStart is not null
            || _textPlacementStart is not null || _stroke is not null
            || DragPreview is not null || _marqueeStart is not null)
        {
            CancelVectorElementDrag();
            CancelVectorDraw();
            CancelPenPath();
            CancelLandShapeGesture();
            CancelTextPlacement();
            CancelStroke();
            CancelDrag();
            CancelMarquee();
            CancelAppearanceGesture();
            CanvasGestureCancelled?.Invoke(this, EventArgs.Empty);
            StatusDetail = "Действие отменено.";
            return true;
        }

        if (CancelPlacement())
        {
            CanvasGestureCancelled?.Invoke(this, EventArgs.Empty);
            StatusDetail = "Размещение отменено.";
            return true;
        }

        if (_openGroupId is not null)
        {
            ExitGroup();
            return true;
        }

        if (SelectionCount > 0)
        {
            DeselectObject();
            return true;
        }

        if (ActiveTool != EditorTool.Select)
        {
            ActiveTool = EditorTool.Select;
            StatusDetail = "Обычное управление: выбор и перемещение объектов.";
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- marquee

    /// <summary>Live rubber-band rectangle for the renderer, or null when no marquee drag runs.</summary>
    public WorldBounds? MarqueeWorld => _marqueeWorld;

    /// <summary>Starts a marquee drag on empty canvas. Additive keeps the current selection.</summary>
    public bool BeginMarquee(double worldX, double worldY, bool additive)
    {
        _marqueeStart = (worldX, worldY, additive);
        _marqueeWorld = null;
        return true;
    }

    public void UpdateMarquee(double worldX, double worldY)
    {
        if (_marqueeStart is not { } start)
        {
            return;
        }

        _marqueeWorld = MultiSelectionGeometry.MarqueeBounds(start.X, start.Y, worldX, worldY);
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    /// <summary>
    /// Completes the marquee: visible unlocked objects intersecting the rectangle, each grouped
    /// object standing for its whole group, replace or extend the selection.
    /// </summary>
    public void EndMarquee()
    {
        var start = _marqueeStart;
        var rect = _marqueeWorld;
        _marqueeStart = null;
        _marqueeWorld = null;
        if (start is not { } begun || rect is not { } marquee)
        {
            InvalidateScene(EditorChangeReason.CanvasInvalidation);
            return;
        }

        var found = new List<ObjectId>();
        foreach (var hit in MultiSelectionGeometry.ObjectsInMarquee(Session.Document, marquee))
        {
            found.AddRange(SelectionUnit(hit));
        }

        if (begun.Additive)
        {
            Session.AddToSelection(found);
        }
        else
        {
            Session.SetSelection(found);
        }

        AfterSelectionChange();
    }

    public void CancelMarquee()
    {
        if (_marqueeStart is null && _marqueeWorld is null)
        {
            return;
        }

        _marqueeStart = null;
        _marqueeWorld = null;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    /// <summary>Shared frame of the current multi-selection (live drag included), or null.</summary>
    public WorldBounds? SelectionFrame
    {
        get
        {
            if (DragPreview is { } preview && preview.Proposed.Count > 1)
            {
                return MultiSelectionGeometry.CombinedBounds(preview.Proposed);
            }

            return SelectionCount > 1
                ? MultiSelectionGeometry.CombinedBounds(Session.SelectedObjects)
                : null;
        }
    }

    /// <summary>One notification bundle for every way the selection can change.</summary>
    private void AfterSelectionChange()
    {
        if (_selectedVectorNodeId is { } nodeId && SelectedVectorNodes.All(node => node.Id != nodeId))
            _selectedVectorNodeId = null;
        if (_selectedPortalId is { } portalId && SelectedVectorPortals.All(portal => portal.Id != portalId))
            _selectedPortalId = null;
        if (_pendingShapeBoolean is { } pending
            && !Session.SelectedObjectIds.SequenceEqual(new[] { pending.First, pending.Second }))
        {
            _pendingShapeBoolean = null; _shapeBooleanPreviewObject = null;
            Raise(nameof(PlacementPreviewObjects));
        }
        RefreshInspector();
        Raise(nameof(SelectionStatus));
        Raise(nameof(SelectionKind));
        Raise(nameof(SelectionCount));
        Raise(nameof(ShowSingleInspector));
        Raise(nameof(SelectedGroupId));
        Raise(nameof(SelectedGroupName));
        Raise(nameof(OpenGroupId));
        Raise(nameof(SelectionVisibilitySummary));
        Raise(nameof(SelectionLockSummary));
        InvalidateScene(EditorChangeReason.Selection | EditorChangeReason.CanvasInvalidation);
    }

    public void NudgeSelectedObject(double deltaX, double deltaY)
    {
        if (EditableSelectionList() is not { } before) return;
        var after = QuantizeTransformedGeometries(before, MultiSelectionGeometry.Translate(before, deltaX, deltaY));
        // Other tools expose moved node coordinates; integer normalization can also alter an
        // older stamp's size/angle. Those cases still need the complete context-panel refresh.
        var positionOnly = before.Zip(after).All(pair => pair.First.Kind == SceneObjectKind.Stamp
            && pair.First.Width == pair.Second.Width && pair.First.Height == pair.Second.Height
            && pair.First.RotationDegrees == pair.Second.RotationDegrees);
        ExecuteWholeSelection(before, after, "Nudge selection", geometryOnly: positionOnly);
    }

    public void ResizeSelectedObject(int direction, bool largerStep = false)
    {
        if (EditableSelectionList() is not { } before || direction == 0) return;
        var factor = MultiSelectionGeometry.ClampScaleFactor(
            before, 1 + Math.Sign(direction) * (largerStep ? 0.1 : 0.01));
        var frame = MultiSelectionGeometry.CombinedBounds(before)!.Value;
        ExecuteWholeSelection(before, QuantizeTransformedGeometries(before, MultiSelectionGeometry.ScaleAroundCentre(
            before, factor, (frame.Left + frame.Right) / 2, (frame.Top + frame.Bottom) / 2)),
            "Resize selection from shortcut");
    }

    public void RotateSelectedObject(int direction, bool largerStep = false, bool fineStep = false)
    {
        if (EditableSelectionList() is not { } before || direction == 0) return;
        // Geometry is intentionally integer-only. Alt therefore keeps the normal one-degree
        // step instead of creating hidden quarter-degree values.
        var step = largerStep ? 15 : 1;
        var frame = MultiSelectionGeometry.CombinedBounds(before)!.Value;
        ExecuteWholeSelection(before, QuantizeTransformedGeometries(before, MultiSelectionGeometry.RotateAroundCentre(
            before, Math.Sign(direction) * step,
            (frame.Left + frame.Right) / 2, (frame.Top + frame.Bottom) / 2)),
            "Rotate selection from shortcut");
    }

    public void AdjustSelectedSublayer(int direction)
    {
        if (EditableSelectionList() is not { } before || direction == 0) return;
        ExecuteWholeSelection(before,
            [.. before.Select(o => CopyObject(o) with
            {
                Sublayer = Math.Clamp(o.Sublayer + Math.Sign(direction), -10, 10),
            })],
            "Change selection sublayer");
    }

    // ------------------------------------- common properties of the whole selection

    /// <summary>The lowest and highest level an object may hold. One integer band, no fractions.</summary>
    public const int MinimumSublayer = -10;

    public const int MaximumSublayer = 10;

    /// <summary>An object level as the user reads it: a signed integer, never a bare "3".</summary>
    public static string FormatSublayer(int value) =>
        value > 0 ? "+" + value.ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The level every selected object shares, or null when they disagree or none is selected.</summary>
    public int? SelectionSublayer
    {
        get
        {
            var selected = Session.SelectedObjects;
            if (selected.Count == 0)
            {
                return null;
            }

            var first = selected[0].Sublayer;
            return selected.All(item => item.Sublayer == first) ? first : null;
        }
    }

    /// <summary>True when the selection holds more than one level, so no single number is honest.</summary>
    public bool SelectionSublayerIsMixed =>
        Session.SelectedObjects.Count > 1 && SelectionSublayer is null;

    /// <summary>Signed level, the word "разные" for a mixed selection, or a dash for none.</summary>
    public string SelectionSublayerReadout => Session.SelectedObjects.Count == 0
        ? "—"
        : SelectionSublayer is { } value ? FormatSublayer(value) : "разные";

    /// <summary>
    /// Gives the whole selection one level. Used when the objects already agree, so the slider
    /// shows a real shared number and dragging it means exactly what it says.
    /// </summary>
    public bool SetSelectionSublayer(int value, bool commit = true)
    {
        var bounded = Math.Clamp(value, MinimumSublayer, MaximumSublayer);
        return EditSelection("Change selection level", commit,
            before => before.Sublayer == bounded ? before : CopyObject(before) with { Sublayer = bounded });
    }

    /// <summary>
    /// Moves every selected object by the same number of levels, keeping the differences between
    /// them. This is what a mixed selection needs: nothing is flattened onto one value, and the
    /// whole shift is one undoable command.
    /// </summary>
    public bool ShiftSelectionSublayer(int delta, bool commit = true)
    {
        if (delta == 0)
        {
            return false;
        }

        // A member already at the edge would silently swallow part of the shift and change the
        // relative order, so the shift is trimmed to what every member can actually travel.
        var selected = Session.SelectedObjects;
        if (selected.Count == 0)
        {
            return false;
        }

        var room = delta > 0
            ? selected.Min(item => MaximumSublayer - item.Sublayer)
            : selected.Min(item => item.Sublayer - MinimumSublayer);
        var applied = Math.Sign(delta) * Math.Min(Math.Abs(delta), Math.Max(0, room));
        if (applied == 0)
        {
            StatusDetail = delta > 0
                ? "Выделение уже на максимальном уровне."
                : "Выделение уже на минимальном уровне.";
            return false;
        }

        return EditSelection("Shift selection level", commit, before => CopyObject(before) with
        {
            Sublayer = Math.Clamp(before.Sublayer + applied, MinimumSublayer, MaximumSublayer),
        });
    }

    /// <summary>Opacity shared by the whole selection, or null when the members disagree.</summary>
    public double? SelectionOpacityPercent
    {
        get
        {
            var selected = Session.SelectedObjects;
            if (selected.Count == 0)
            {
                return null;
            }

            var first = selected[0].ObjectOpacity;
            return selected.All(item => Math.Abs(item.ObjectOpacity - first) < 0.0001)
                ? Math.Round(first * 100, 1)
                : null;
        }
    }

    public bool SetSelectionOpacityPercent(double percent, bool commit = true)
    {
        if (!double.IsFinite(percent))
        {
            InspectorError = "Непрозрачность задаётся числом 0..100.";
            return false;
        }

        var opacity = Math.Clamp(percent, 0, 100) / 100.0;
        return EditSelection("Change selection opacity", commit,
            before => before.ObjectOpacity == opacity
                ? before
                : CopyObject(before) with { ObjectOpacity = opacity });
    }

    /// <summary>Rotation shared by the whole selection, or null when the members disagree.</summary>
    public double? SelectionRotationDegrees
    {
        get
        {
            var selected = Session.SelectedObjects;
            if (selected.Count == 0)
            {
                return null;
            }

            var first = NormalizeUnsignedAngle(WholeGeometryValue(selected[0].RotationDegrees));
            return selected.All(item =>
                NormalizeUnsignedAngle(WholeGeometryValue(item.RotationDegrees)) == first)
                ? first
                : null;
        }
    }

    /// <summary>Turns every selected object to the same angle around its own centre.</summary>
    public bool SetSelectionRotationDegrees(double degrees, bool commit = true)
    {
        if (!double.IsFinite(degrees))
        {
            InspectorError = "Поворот задаётся числом 0..359 градусов.";
            return false;
        }

        var normalized = NormalizeUnsignedAngle(WholeGeometryValue(degrees));
        return EditSelection("Change selection rotation", commit,
            before => Math.Abs(before.RotationDegrees - normalized) < 0.0001
                ? before
                : WithGeometry(before, before.X, before.Y, before.Width, before.Height, normalized));
    }

    /// <summary>
    /// True when every selected object is a placed asset, which is the only case where a single
    /// "scale %" has a shared meaning: each stamp carries its own 100 % reference size.
    /// </summary>
    public bool SelectionIsAllStamps =>
        Session.SelectedObjects.Count > 0 && Session.SelectedObjects.All(item => item.Stamp is not null);

    /// <summary>Scale shared by an all-stamp selection, or null when the members disagree.</summary>
    public double? SelectionScalePercent
    {
        get
        {
            if (!SelectionIsAllStamps)
            {
                return null;
            }

            var values = Session.SelectedObjects
                .Select(item =>
                {
                    var reference = item.Stamp!.ReferenceWidth ?? item.Width;
                    return reference <= 0 ? (double?)null : WholeGeometryValue(item.Width / reference * 100);
                })
                .ToList();
            return values.All(value => value is not null)
                && values.Skip(1).All(value => value == values[0])
                ? values[0]
                : null;
        }
    }

    public bool SetSelectionScalePercent(double percent, bool commit = true)
    {
        if (!double.IsFinite(percent))
        {
            InspectorError = "Масштаб задаётся числом в процентах.";
            return false;
        }

        var factor = Math.Clamp(WholeGeometryValue(percent), 10, 400) / 100.0;
        return EditSelection("Change selection scale", commit, before =>
        {
            if (before.Stamp is not { } stamp)
            {
                return before;
            }

            var referenceWidth = stamp.ReferenceWidth ?? before.Width;
            var referenceHeight = stamp.ReferenceHeight ?? before.Height;
            var width = Math.Max(0.5, referenceWidth * factor);
            var height = Math.Max(0.5, referenceHeight * factor);
            var adopted = stamp.ReferenceWidth is null
                ? stamp with { ReferenceWidth = referenceWidth, ReferenceHeight = referenceHeight }
                : stamp;
            var moved = WithGeometry(
                before,
                before.X + ((before.Width - width) / 2),
                before.Y + ((before.Height - height) / 2),
                width, height, before.RotationDegrees);
            return moved with { Stamp = adopted };
        });
    }

    private IReadOnlyList<SceneObject>? _selectionGestureBefore;

    /// <summary>
    /// One property gesture over the WHOLE selection: previews while the pointer is down and lands
    /// exactly one history entry when it is released. The "before" list belongs to the gesture, so
    /// a preview that started on this selection can never commit onto a different one.
    /// </summary>
    private bool EditSelection(string name, bool commit, Func<SceneObject, SceneObject> change)
    {
        var current = EditableSelectionList();
        if (_selectionGestureBefore is { } pending
            && (current is null
                || !pending.Select(item => item.Id).SequenceEqual(current.Select(item => item.Id))))
        {
            _selectionGestureBefore = null;
            DragPreview = null;
        }

        var before = _selectionGestureBefore ?? current;
        if (before is null || before.Count == 0)
        {
            return false;
        }

        var after = before.Select(change).ToList();
        if (!commit)
        {
            _selectionGestureBefore = before;
            DragPreview = new DragPreview(before, after, DragMode.Move, null, 0, 0, null);
            SceneChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        _selectionGestureBefore = null;
        DragPreview = null;
        if (before.SequenceEqual(after))
        {
            SceneChanged?.Invoke(this, EventArgs.Empty);
            return false; // no-op: no history entry
        }

        Session.Execute(new ReplaceObjectsCommand(before, after, name));
        AfterEdit();
        return true;
    }

    // ------------------------------------------------ owner layer and draw order

    /// <summary>
    /// The layer that owns the selection, by its user-visible name. Never a GUID or an internal
    /// key: the user has to recognise the layer they are about to move an object out of.
    /// </summary>
    public string SelectedOwnerLayerName
    {
        get
        {
            var owners = SelectionOwnerLayerIds();
            return owners.Count switch
            {
                0 => "—",
                1 => Session.Document.FindLayer(owners[0])?.Name ?? "—",
                _ => "Разные слои",
            };
        }
    }

    /// <summary>
    /// Every object layer of the document, in document order, each with the name the layers panel
    /// shows. Hidden and locked layers stay in the list and carry the reason they cannot be
    /// chosen, so the selector never hides a layer the user is looking for.
    /// </summary>
    public IReadOnlyList<ObjectLayerTarget> ObjectLayerTargets
    {
        get
        {
            var owners = SelectionOwnerLayerIds();
            var current = owners.Count == 1 ? owners[0] : (LayerId?)null;
            return
            [
                .. Session.Document.Layers.OfType<ObjectLayer>()
                    .Select(layer => new ObjectLayerTarget(
                        layer.Id,
                        layer.Name,
                        layer.Id == current,
                        layer.Visible && !layer.Locked,
                        !layer.Visible ? "скрыт" : layer.Locked ? "заблокирован" : null)),
            ];
        }
    }

    /// <summary>Object layers that would actually accept the selection right now.</summary>
    public IReadOnlyList<ObjectLayerTarget> AvailableObjectLayerTargets =>
        [.. ObjectLayerTargets.Where(target => target.Available)];

    private IReadOnlyList<LayerId> SelectionOwnerLayerIds() =>
    [
        .. Session.SelectedObjects
            .Select(sceneObject => DocumentEditor.FindLayerOf(Session.Document, sceneObject.Id))
            .OfType<MapLayer>()
            .Select(layer => layer.Id)
            .Distinct(),
    ];

    /// <summary>
    /// The selection's own level inside its layer, as the user sees it: one number, or a plain
    /// word when the selected objects disagree.
    /// </summary>
    public string SelectedSublayerSummary
    {
        get
        {
            var selected = Session.SelectedObjects;
            if (selected.Count == 0)
            {
                return "—";
            }

            var levels = selected.Select(item => item.Sublayer).Distinct().ToList();
            return levels.Count == 1 ? levels[0].ToString() : "разные";
        }
    }

    /// <summary>
    /// Moves the selection to the next compatible object layer, wrapping around. This is the one
    /// click behind the "Слой" control: the user reads the current layer and steps to another one
    /// without hunting for a separate dialog.
    /// </summary>
    public bool CycleSelectionLayer()
    {
        var targets = AvailableObjectLayerTargets;
        if (targets.Count == 0)
        {
            StatusDetail = "Нет другого видимого разблокированного слоя объектов.";
            return false;
        }

        if (targets.Count == 1 && targets[0].IsCurrent)
        {
            StatusDetail = "Это единственный доступный слой объектов.";
            return false;
        }

        var currentIndex = targets.ToList().FindIndex(target => target.IsCurrent);
        var next = targets[(currentIndex + 1 + targets.Count) % targets.Count];
        return MoveSelectionToLayer(next.Id);
    }

    /// <summary>
    /// Moves the whole selection to another object layer as one undoable step. Whole groups travel
    /// together, and a selection that is not fully editable refuses outright rather than moving
    /// part of itself. Each object keeps its own sublayer, so the level the user set survives.
    /// </summary>
    public bool MoveSelectionToLayer(LayerId targetId)
    {
        if (EditableSelectionList() is null)
        {
            return false;
        }

        if (Session.Document.FindLayer(targetId) is not ObjectLayer { Visible: true, Locked: false } target)
        {
            StatusDetail = "Слой недоступен: он скрыт, заблокирован или не хранит объекты.";
            return false;
        }

        var ids = SelectionWithWholeGroups();
        var bySource = ids
            .Select(id => (Id: id, Layer: DocumentEditor.FindLayerOf(Session.Document, id)))
            .Where(pair => pair.Layer is not null && pair.Layer.Id != targetId)
            .GroupBy(pair => pair.Layer!.Id)
            .ToList();
        if (bySource.Count == 0)
        {
            StatusDetail = $"Объекты уже находятся на слое «{target.Name}».";
            return false;
        }

        var steps = bySource
            .Select(group => (IEditCommand)new MoveObjectsToLayerCommand(
                Session.Document, group.Key, targetId, [.. group.Select(pair => pair.Id)]))
            .ToList();
        Session.Execute(steps.Count == 1
            ? steps[0]
            : new CompositeCommand("Move selection to another layer", steps));
        _activeLayerId = targetId;
        Session.SetSelection(ids);
        AfterEdit();
        AfterSelectionChange();
        StatusDetail = $"Перенесено на слой «{target.Name}»: {EditorLabels.Objects(ids.Count)}.";
        return true;
    }

    /// <summary>
    /// Moves the selection above or below its neighbours inside its own layer. This is the fine
    /// order that breaks ties inside a sublayer band; <see cref="AdjustSelectedSublayer"/> is the
    /// coarse band itself. <paramref name="toExtreme"/> sends the selection all the way.
    /// </summary>
    public bool ReorderSelection(int direction, bool toExtreme = false)
    {
        if (direction == 0 || EditableSelectionList() is not { } selection)
        {
            return false;
        }

        var owners = SelectionOwnerLayerIds();
        if (owners.Count != 1 || Session.Document.FindLayer(owners[0]) is not ObjectLayer layer)
        {
            StatusDetail = "Порядок меняется в пределах одного слоя: выделение лежит на разных слоях.";
            return false;
        }

        var order = layer.Objects.ToList();
        var selected = selection.Select(item => item.Id).ToHashSet();
        var up = direction > 0;

        if (toExtreme)
        {
            var moved = order.Where(item => selected.Contains(item.Id)).ToList();
            var rest = order.Where(item => !selected.Contains(item.Id)).ToList();
            order = up ? [.. rest, .. moved] : [.. moved, .. rest];
        }
        else if (up)
        {
            // Walk from the front so a run of selected objects keeps its own internal order.
            for (var index = order.Count - 2; index >= 0; index--)
            {
                if (selected.Contains(order[index].Id) && !selected.Contains(order[index + 1].Id)
                    && order[index].Sublayer == order[index + 1].Sublayer)
                {
                    (order[index], order[index + 1]) = (order[index + 1], order[index]);
                }
            }
        }
        else
        {
            for (var index = 1; index < order.Count; index++)
            {
                if (selected.Contains(order[index].Id) && !selected.Contains(order[index - 1].Id)
                    && order[index].Sublayer == order[index - 1].Sublayer)
                {
                    (order[index], order[index - 1]) = (order[index - 1], order[index]);
                }
            }
        }

        if (order.Select(item => item.Id).SequenceEqual(layer.Objects.Select(item => item.Id)))
        {
            StatusDetail = up
                ? "Выделение уже выше всех соседей своего подслоя."
                : "Выделение уже ниже всех соседей своего подслоя.";
            return false;
        }

        Session.Execute(new ReorderObjectsCommand(
            layer.Id, layer.Objects, order, up ? "Move selection up" : "Move selection down"));
        AfterEdit();
        return true;
    }

    public void AdjustSelectedOpacity(int direction, bool largerStep = false)
    {
        if (EditableSelectionList() is not { } before || direction == 0) return;
        var step = largerStep ? 0.1 : 0.05;
        ExecuteWholeSelection(before,
            [.. before.Select(o => CopyObject(o) with
            {
                ObjectOpacity = Math.Clamp(o.ObjectOpacity + Math.Sign(direction) * step, 0, 1),
            })],
            "Change selection opacity");
    }

    /// <summary>One whole-selection edit = one history entry; an unchanged result = none.</summary>
    private void ExecuteWholeSelection(
        IReadOnlyList<SceneObject> before, IReadOnlyList<SceneObject> after, string name, bool geometryOnly = false)
    {
        if (before.SequenceEqual(after))
        {
            return;
        }

        Session.Execute(new ReplaceObjectsCommand(before, after, name));
        AfterEdit(geometryOnly);
    }

    /// <summary>
    /// The whole selection, editable or nothing: a hidden or locked member refuses the entire
    /// geometric change so no partial result can ever land (Stage 10C contract).
    /// </summary>
    private IReadOnlyList<SceneObject>? EditableSelectionList()
    {
        var selected = Session.SelectedObjects;
        if (selected.Count == 0)
        {
            return null;
        }
        if (selected.Any(o => PlacementOwnership.Owner(Session.Document, o.Id) is not null))
        { StatusDetail = PlacementOwnership.DetachFirst; return null; }

        if (selected.Any(sceneObject => !IsObjectAvailable(sceneObject)))
        {
            StatusDetail = "В выделении есть скрытые или заблокированные объекты либо слои — изменение отклонено целиком.";
            return null;
        }

        return selected;
    }

    public void AdjustBrushSize(int direction, bool largerStep = false)
    {
        if (direction == 0) return;
        BrushSettings = BrushSettings with
        {
            Radius = Math.Clamp(BrushSettings.Radius + Math.Sign(direction) * (largerStep ? 10 : 1), 1, 256),
        };
        StatusDetail = $"Размер кисти: {BrushSettings.Radius:0}.";
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AdjustBrushOpacity(int direction, bool largerStep = false)
    {
        if (direction == 0) return;
        BrushSettings = BrushSettings with
        {
            Opacity = Math.Clamp(BrushSettings.Opacity + Math.Sign(direction) * (largerStep ? 0.1 : 0.05), 0.05, 1),
        };
        StatusDetail = $"Непрозрачность кисти: {BrushSettings.Opacity:P0}.";
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AdjustBrushSoftness(int direction, bool largerStep = false)
    {
        if (direction == 0) return;
        BrushSettings = BrushSettings with
        {
            Softness = Math.Clamp(BrushSettings.Softness + Math.Sign(direction) * (largerStep ? 0.1 : 0.05), 0, 1),
        };
        StatusDetail = $"Мягкость кисти: {BrushSettings.Softness:P0}.";
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleMaskPaintMode()
    {
        MaskMode = MaskMode == MaskPaintMode.Hide ? MaskPaintMode.Reveal : MaskPaintMode.Hide;
        StatusDetail = MaskMode == MaskPaintMode.Hide ? "Маска: скрывать." : "Маска: возвращать.";
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AdjustSelectedTextFontSize(int direction, bool largerStep = false)
    {
        if (EditableSelection() is not { Text: { } text } before || direction == 0) return;
        var value = Math.Clamp(text.FontSize + Math.Sign(direction) * (largerStep ? 5 : 1), 1, 512);
        ReplaceSelected(before, before with { Text = text with { FontSize = value } }, "Change text size");
    }

    public void AdjustSelectedTextCurve(int direction, bool largerStep = false)
    {
        if (EditableSelection() is not { Text: { } text } before || direction == 0) return;
        var value = Math.Clamp(text.Curve + Math.Sign(direction) * (largerStep ? 10 : 1), -100, 100);
        ReplaceSelected(before, before with { Text = text with { Curve = value } }, "Change text curve");
    }

    /// <summary>
    /// Visibility over the whole selection. A mixed state resolves towards showing everything
    /// first - hiding objects the user cannot see being toggled would read as loss.
    /// </summary>
    public void ToggleSelectedObjectVisibility()
        => SetSelectedObjectVisibility(!Session.SelectedObjects.All(o => o.Visible));

    /// <summary>Applies the chosen value to the whole selection as one undoable edit.</summary>
    public void SetSelectedObjectVisibility(bool visible)
    {
        var before = Session.SelectedObjects;
        if (before.Count == 0 || before.All(o => o.Visible == visible))
        {
            return;
        }

        ExecuteWholeSelection(before,
            [.. before.Select(o => CopyObject(o, visible: visible))], "Selection visibility");
    }

    /// <summary>Lock over the whole selection: any unlocked member means "lock all" first.</summary>
    public void ToggleSelectedObjectLock()
        => SetSelectedObjectLock(!Session.SelectedObjects.All(o => o.Locked));

    /// <summary>Applies a named lock state; mixed values never force an intermediate state.</summary>
    public void SetSelectedObjectLock(bool locked)
    {
        var before = Session.SelectedObjects;
        if (before.Count == 0 || before.All(o => o.Locked == locked))
        {
            return;
        }

        ExecuteWholeSelection(before,
            [.. before.Select(o => CopyObject(o, locked: locked))], "Selection lock");
    }

    /// <summary>Deletes the whole selection, across layers, as one history entry.</summary>
    public void DeleteSelectedObject()
    {
        if (Session.SelectedObjectIds.Any(id => PlacementOwnership.Owner(Session.Document, id) is not null))
        { StatusDetail = PlacementOwnership.DetachFirst; return; }
        var ids = Session.SelectedObjectIds;
        if (ids.Count == 0)
        {
            return;
        }

        Session.Execute(new DeleteObjectsCommand(Session.Document, [.. ids]));
        Session.ClearSelection();
        AfterEdit();
    }

    /// <summary>Selected, editable object - locked and hidden objects cannot be edited.</summary>
    private SceneObject? EditableSelection()
    {
        var selected = Session.SelectedObject;
        if (selected is not null && PlacementOwnership.Owner(Session.Document, selected.Id) is not null) return null;
        return selected is { Locked: false, Visible: true } ? selected : null;
    }

    /// <summary>World-space reach of a handle grab and the rotation handle's stem, per zoom.</summary>
    public static double HandleHitRadius(double viewScale) => 8.0 / Math.Max(viewScale, 0.01);

    public static double RotationHandleOffset(double viewScale) => 28.0 / Math.Max(viewScale, 0.01);

    /// <summary>
    /// The gizmo handle under the cursor, for pointer feedback. One object hit-tests its own
    /// rotated gizmo; a multi-selection hit-tests the shared axis-aligned frame. Null when the
    /// selection is empty or holds a hidden/locked member.
    /// </summary>
    public HandleKind? HitHandle(double worldX, double worldY, double viewScale)
    {
        var selected = Session.SelectedObjects;
        if (selected.Count == 0 || selected.Any(o => !o.Visible || o.Locked))
        {
            return null;
        }

        if (selected.Count == 1)
        {
            return SelectionHandles.HitTest(
                selected[0], worldX, worldY, HandleHitRadius(viewScale), RotationHandleOffset(viewScale));
        }

        var frame = MultiSelectionGeometry.CombinedBounds(selected)!.Value;
        return MultiSelectionGeometry.HitTestFrame(
            frame, worldX, worldY, HandleHitRadius(viewScale), RotationHandleOffset(viewScale));
    }

    /// <summary>
    /// One canvas press: a handle grab starts resize/rotate of the whole selection; a press on an
    /// object selects (Ctrl toggles) and starts a move; a press on empty canvas starts a marquee.
    /// Whatever the gesture, the completed edit lands as exactly one history entry.
    /// </summary>
    public CanvasGesture BeginCanvasInteraction(
        double worldX, double worldY, double viewScale, bool additive = false)
    {
        if (!additive && HitHandle(worldX, worldY, viewScale) is { } grabbed)
        {
            var selected = Session.SelectedObjects;
            if (selected.Any(o => PlacementOwnership.Owner(Session.Document, o.Id) is not null))
            { StatusDetail = PlacementOwnership.DetachFirst; return CanvasGesture.None; }
            DragPreview = new DragPreview(
                selected, selected,
                grabbed == HandleKind.Rotate ? DragMode.Rotate : DragMode.Resize,
                grabbed, worldX, worldY,
                selected.Count > 1 ? MultiSelectionGeometry.CombinedBounds(selected) : null);
            return CanvasGesture.Drag;
        }

        var hit = SceneGeometry.HitTest(Session.Document, worldX, worldY);
        if (hit is null)
        {
            if (!additive && SelectionCount > 0)
            {
                Session.ClearSelection();
                AfterSelectionChange();
            }

            BeginMarquee(worldX, worldY, additive);
            return CanvasGesture.Marquee;
        }

        SelectHit(hit, additive);
        RefreshInspector();
        if (Session.SelectedObjectIds.Any(id => PlacementOwnership.Owner(Session.Document, id) is not null))
        { StatusDetail = PlacementOwnership.DetachFirst; return CanvasGesture.None; }
        if (additive)
        {
            return CanvasGesture.None;
        }

        if (BeginDrag(worldX, worldY))
        {
            return CanvasGesture.Drag;
        }

        // The object is selected and its properties stay readable, but it cannot be moved. Saying
        // why beats a frame that silently refuses to follow the pointer.
        if (Session.SelectedObjects is { Count: > 0 } blocked)
        {
            StatusDetail = blocked.Any(item => item.Locked)
                ? "Объект заблокирован: снимите блокировку, чтобы перемещать его."
                : "Объект скрыт: включите видимость, чтобы перемещать его.";
        }

        return CanvasGesture.None;
    }

    public bool BeginDrag(double startWorldX = 0, double startWorldY = 0)
    {
        var selected = Session.SelectedObjects;
        if (selected.Any(o => PlacementOwnership.Owner(Session.Document, o.Id) is not null))
        { StatusDetail = PlacementOwnership.DetachFirst; return false; }
        if (selected.Count == 0 || selected.Any(o => !o.Visible || o.Locked))
        {
            return false;
        }

        DragPreview = new DragPreview(
            selected, selected, DragMode.Move, null, startWorldX, startWorldY,
            selected.Count > 1 ? MultiSelectionGeometry.CombinedBounds(selected) : null);
        return true;
    }

    /// <summary>Absolute-cursor drag update covering move, resize and rotate of the whole set.</summary>
    public void UpdateDragTo(double worldX, double worldY)
    {
        if (DragPreview is not { } preview)
        {
            return;
        }

        var before = preview.Before;
        IReadOnlyList<SceneObject> proposed;
        if (preview.Mode == DragMode.Resize && before.Count == 1)
        {
            proposed = [SelectionHandles.Resize(before[0], preview.Handle!.Value, worldX, worldY)];
        }
        else if (preview.Mode == DragMode.Resize)
        {
            var frame = preview.StartFrame!.Value;
            var factor = MultiSelectionGeometry.ClampScaleFactor(
                before, MultiSelectionGeometry.ResizeFactor(frame, preview.Handle!.Value, worldX, worldY));
            proposed = MultiSelectionGeometry.ScaleAroundCentre(
                before, factor, (frame.Left + frame.Right) / 2, (frame.Top + frame.Bottom) / 2);
        }
        else if (preview.Mode == DragMode.Rotate && before.Count == 1)
        {
            proposed = [SelectionHandles.Rotate(
                before[0], preview.StartWorldX, preview.StartWorldY, worldX, worldY)];
        }
        else if (preview.Mode == DragMode.Rotate)
        {
            var frame = preview.StartFrame!.Value;
            var centreX = (frame.Left + frame.Right) / 2;
            var centreY = (frame.Top + frame.Bottom) / 2;
            var delta = (Math.Atan2(worldY - centreY, worldX - centreX)
                - Math.Atan2(preview.StartWorldY - centreY, preview.StartWorldX - centreX))
                * 180.0 / Math.PI;
            proposed = MultiSelectionGeometry.RotateAroundCentre(before, delta, centreX, centreY);
        }
        else
        {
            proposed = MultiSelectionGeometry.Translate(
                before, worldX - preview.StartWorldX, worldY - preview.StartWorldY);
        }

        DragPreview = preview with { Proposed = proposed };
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void UpdateDrag(double worldDx, double worldDy)
    {
        if (DragPreview is { } preview)
        {
            UpdateDragTo(preview.StartWorldX + worldDx, preview.StartWorldY + worldDy);
        }
    }

    /// <summary>The whole drag becomes exactly one history entry; a no-move drag becomes none.</summary>
    public void EndDrag()
    {
        if (DragPreview is not { } preview)
        {
            return;
        }

        DragPreview = null;

        var proposed = QuantizeTransformedGeometries(preview.Before, preview.Proposed);
        if (!proposed.SequenceEqual(preview.Before))
        {
            Session.Execute(new ReplaceObjectsCommand(
                preview.Before, proposed, "Transform selection"));
            AfterEdit();
        }
        else
        {
            InvalidateScene(EditorChangeReason.CanvasInvalidation);
        }
    }

    public void CancelDrag()
    {
        if (DragPreview is null)
        {
            return;
        }

        DragPreview = null;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void Undo()
    {
        CancelEditablePathGesture(); CancelEditableAreaGesture();
        if (Session.Undo())
        {
            SyncEditablePathSettings(); SyncEditableAreaSettings();
            AfterEdit();
        }
    }

    public void Redo()
    {
        CancelEditablePathGesture(); CancelEditableAreaGesture();
        if (Session.Redo())
        {
            SyncEditablePathSettings(); SyncEditableAreaSettings();
            AfterEdit();
        }
    }

    public bool CreateVersion()
    {
        var timestamp = _clock();
        return CreateCheckpoint(NextVersionName(timestamp), timestamp, userTerm: "версия");
    }

    private string NextVersionName(DateTimeOffset timestamp)
    {
        var used = new HashSet<int>();
        foreach (var checkpoint in Session.Document.Checkpoints)
        {
            const string prefix = "Версия ";
            if (checkpoint.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && checkpoint.Name.Length >= prefix.Length + 2
                && int.TryParse(checkpoint.Name.AsSpan(prefix.Length, 2), out var number))
            {
                used.Add(number);
            }
        }

        var next = Enumerable.Range(1, MapCheckpointLimits.MaxCount + 1)
            .First(number => !used.Contains(number));
        return $"Версия {next:00} · {timestamp.ToLocalTime():dd.MM.yyyy HH:mm}";
    }

    /// <summary>The native command prepares the immutable snapshot off the UI thread. The same
    /// operation gate as Save/close prevents publication after the editor has closed.</summary>
    public async Task<bool> CreateVersionAsync()
    {
        if (!await _saveGate.WaitAsync(0))
        {
            StatusDetail = "Дождитесь завершения текущего сохранения или создания версии.";
            return false;
        }
        try
        {
            var source = Session.Document;
            if (source.Checkpoints.Count >= MapCheckpointLimits.MaxCount)
            {
                StatusDetail = $"Достигнут лимит: не больше {MapCheckpointLimits.MaxCount} версий.";
                return false;
            }
            var timestamp = _clock();
            var name = NextVersionName(timestamp);
            using var images = new ExportStampImageSource(source, DocumentPath, _assetCatalog);
            var checkpoint = await RunResourceOperationAsync(() => Task.Run(() =>
            {
                var preview = ThumbnailRenderer.RenderPng(source,
                    MapCheckpointLimits.PreviewWidth, MapCheckpointLimits.PreviewHeight, images);
                var result = new MapCheckpoint(CheckpointId.New(), name, timestamp,
                    MapCheckpointSnapshot.Capture(source), preview);
                MapProjectStore.ValidateCheckpointAddition(source, result);
                return result;
            }));
            if (!ReferenceEquals(Session.Document, source))
            {
                StatusDetail = "Карта изменилась во время подготовки версии. Повторите создание версии.";
                return false;
            }
            PublishCheckpoint(checkpoint, "версия");
            return true;
        }
        catch (Exception ex) when (ex is MapProjectResourceLimitException or ArgumentException or InvalidOperationException or IOException)
        {
            StatusDetail = "Не удалось создать версию: " + ex.Message;
            _log.Error("Checkpoint creation failed", ex);
            return false;
        }
        finally { _saveGate.Release(); }
    }

    public bool CreateCheckpoint(string? name) =>
        CreateCheckpoint(name, _clock(), userTerm: "версия");

    private bool CreateCheckpoint(string? name, DateTimeOffset timestamp, string userTerm)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusDetail = "Введите непустое название версии.";
            return false;
        }

        if (name.Trim().Length > MapCheckpointLimits.MaxNameLength)
        {
            StatusDetail = $"Название должно быть не длиннее {MapCheckpointLimits.MaxNameLength} символов.";
            return false;
        }

        if (Session.Document.Checkpoints.Count >= MapCheckpointLimits.MaxCount)
        {
            StatusDetail = $"Достигнут лимит: не больше {MapCheckpointLimits.MaxCount} версий.";
            return false;
        }

        try
        {
            var source = Session.Document;
            var preview = RenderPreview(
                source, MapCheckpointLimits.PreviewWidth, MapCheckpointLimits.PreviewHeight);
            var checkpoint = new MapCheckpoint(
                CheckpointId.New(),
                name,
                timestamp,
                MapCheckpointSnapshot.Capture(source),
                preview);

            MapProjectStore.ValidateCheckpointAddition(source, checkpoint);
            PublishCheckpoint(checkpoint, userTerm);
            return true;
        }
        catch (MapProjectResourceLimitException ex)
        {
            StatusDetail = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            StatusDetail = "Не удалось создать версию: " + ex.Message;
            _log.Error("Checkpoint creation failed", ex);
            return false;
        }
    }

    private void PublishCheckpoint(MapCheckpoint checkpoint, string userTerm)
    {
        Session.Execute(new AddCheckpointCommand(checkpoint));
        AfterEdit();
        StatusDetail = string.IsNullOrWhiteSpace(DocumentPath)
            ? $"Создана {userTerm} «{checkpoint.Name}» в памяти. Укажите путь командой «Сохранить»."
            : $"Создана {userTerm} «{checkpoint.Name}». Она будет сохранена автоматически.";
    }

    public bool RenameVersion(CheckpointId id, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusDetail = "Название версии не может быть пустым.";
            return false;
        }
        if (name.Trim().Length > MapCheckpointLimits.MaxNameLength)
        {
            StatusDetail = $"Название должно быть не длиннее {MapCheckpointLimits.MaxNameLength} символов.";
            return false;
        }

        try
        {
            Session.Execute(RenameCheckpointCommand.Create(Session.Document, id, name));
            AfterEdit();
            StatusDetail = $"Версия переименована в «{name.Trim()}».";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            StatusDetail = "Не удалось переименовать версию: " + ex.Message;
            return false;
        }
    }

    public async Task<bool> RestoreCheckpointAsync(CheckpointId id)
    {
        var checkpoint = Session.Document.Checkpoints.FirstOrDefault(item => item.Id == id);
        if (checkpoint is null)
        {
            StatusDetail = "Версия не найдена. Текущая карта не изменена.";
            return false;
        }

        if (!await _prompts.ConfirmRestoreCheckpointAsync(checkpoint.Name))
        {
            StatusDetail = $"Восстановление «{checkpoint.Name}» отменено. Карта не изменена.";
            return false;
        }

        try
        {
            Session.Execute(RestoreCheckpointCommand.Create(Session.Document, id));
            AfterEdit();
            StatusDetail = $"Восстановлена версия «{checkpoint.Name}». Действие можно отменить одной командой Undo.";
            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            StatusDetail = "Не удалось восстановить версию. Текущая карта не изменена.";
            _log.Error("Checkpoint restore failed", ex);
            return false;
        }
    }

    public async Task<bool> DeleteCheckpointAsync(CheckpointId id)
    {
        var checkpoint = Session.Document.Checkpoints.FirstOrDefault(item => item.Id == id);
        if (checkpoint is null)
        {
            StatusDetail = "Версия не найдена. Ничего не удалено.";
            return false;
        }

        if (!await _prompts.ConfirmDeleteCheckpointAsync(checkpoint.Name))
        {
            StatusDetail = $"Удаление «{checkpoint.Name}» отменено. Ничего не изменено.";
            return false;
        }

        try
        {
            Session.Execute(DeleteCheckpointCommand.Create(Session.Document, id));
            AfterEdit();
            StatusDetail = $"Версия «{checkpoint.Name}» удалена. Изменение будет сохранено автоматически.";
            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            StatusDetail = "Не удалось удалить версию. Ничего не изменено.";
            _log.Error("Checkpoint deletion failed", ex);
            return false;
        }
    }

    /// <summary>Refreshes the derived session state the status bar and the toolbar read.</summary>
    private void RaiseSessionState()
    {
        Raise(nameof(PendingChanges));
        Raise(nameof(SaveSummary));
        Raise(nameof(AutosaveSummary));
        Raise(nameof(PrimaryAutosaveSummary));
        Raise(nameof(RecoveryAvailable));
        Raise(nameof(CanUndo));
        Raise(nameof(CanRedo));
    }

    private void AfterEdit(bool geometryOnly = false)
    {
        RaisePathState();
        RaiseAreaState();
        _lastCommittedEditUtc = _clock();
        SaveState = Session.IsDirty ? SaveState.Unsaved : SaveState.Saved;
        StatusDetail = "";
        RefreshCanvasWorkflowAfterDocumentChange();

        if (_activeLayerId is null || Session.Document.FindLayer(_activeLayerId.Value) is null)
        {
            _activeLayerId = Session.Document.Layers
                .LastOrDefault(layer => layer is not BaseColorLayer)?.Id
                ?? Session.Document.BaseColor.Id;
        }

        // An edit (or an undo/redo) may have emptied the open group; an empty open group would
        // strand the tree behind a breadcrumb with nothing under it.
        if (_openGroupId is { } openGroup
            && SceneGeometry.GroupMembers(Session.Document, openGroup).Count == 0)
        {
            _openGroupId = null;
        }
        RefreshInspector();
        if (geometryOnly)
        {
            // A nudge changes coordinates, not layer/library/note/version rows. Keep the
            // inspector, dirty/save state and canvas current without rebuilding every panel.
            Raise(nameof(Title));
            RaiseSessionState();
            InvalidateScene(EditorChangeReason.ObjectProperties | EditorChangeReason.CanvasInvalidation);
            return;
        }
        Raise(nameof(SelectionStatus));
        Raise(nameof(SelectionKind));
        Raise(nameof(SelectionCount));
        Raise(nameof(ShowSingleInspector));
        Raise(nameof(SelectedGroupId));
        Raise(nameof(SelectedGroupName));
        Raise(nameof(OpenGroupId));
        Raise(nameof(SelectionVisibilitySummary));
        Raise(nameof(SelectionLockSummary));
        Raise(nameof(Title));
        Raise(nameof(Layers));
        Raise(nameof(Notes));
        Raise(nameof(Checkpoints));
        Raise(nameof(PrimaryAutosaveSummary));
        Raise(nameof(ActiveLayer));
        Raise(nameof(ActiveLayerId));
        Raise(nameof(ActiveLayerSummary));
        Raise(nameof(GridSummary));
        Raise(nameof(MaskEditLayerId));
        Raise(nameof(SelectedTextFontResolution));
        RaiseSessionState();
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- inspector

    public string XText { get => _xText; set => SetField(ref _xText, value); }
    public string YText { get => _yText; set => SetField(ref _yText, value); }
    public string WidthText { get => _widthText; set => SetField(ref _widthText, value); }
    public string HeightText { get => _heightText; set => SetField(ref _heightText, value); }
    public string RotationText { get => _rotationText; set => SetField(ref _rotationText, value); }

    public string InspectorError
    {
        get => _inspectorError;
        private set => SetField(ref _inspectorError, value);
    }

    public bool HasEditableSelection => EditableSelection() is not null;

    /// <summary>The view funnels its local input-parse failures into the one visible error line.</summary>
    public void ReportInspectorError(string message) => InspectorError = message ?? "";

    public void RefreshInspector()
    {
        var selected = Session.SelectedObject;
        XText = selected is null ? "" : WholeGeometryValue(selected.X).ToString("0", CultureInfo.InvariantCulture);
        YText = selected is null ? "" : WholeGeometryValue(selected.Y).ToString("0", CultureInfo.InvariantCulture);
        WidthText = selected is null ? "" : WholeGeometryValue(selected.Width).ToString("0", CultureInfo.InvariantCulture);
        HeightText = selected is null ? "" : WholeGeometryValue(selected.Height).ToString("0", CultureInfo.InvariantCulture);
        RotationText = selected is null ? "" : WholeGeometryValue(selected.RotationDegrees).ToString("0", CultureInfo.InvariantCulture);
        InspectorError = "";
        Raise(nameof(HasEditableSelection));
    }

    /// <summary>
    /// Applies the numeric fields as ONE transform command. Invalid input produces a message and
    /// no command; unchanged values produce no history entry at all.
    /// </summary>
    public bool CommitInspector()
    {
        var before = EditableSelection();
        if (before is null)
        {
            InspectorError = "Выбранный объект нельзя редактировать.";
            return false;
        }

        if (!TryParseWhole(XText, out var x) || !TryParseWhole(YText, out var y)
            || !TryParseWhole(WidthText, out var width) || !TryParseWhole(HeightText, out var height)
            || !TryParseWhole(RotationText, out var rotation)
            || width <= 0 || height <= 0)
        {
            InspectorError = "Поля геометрии принимают только целые числа; ширина и высота должны быть больше нуля.";
            return false;
        }

        // Displaying legacy fractional data as integers is not an edit of the hidden precision.
        // Only a field whose entered value differs from its displayed baseline is changed.
        if (x == WholeGeometryValue(before.X)) x = before.X;
        if (y == WholeGeometryValue(before.Y)) y = before.Y;
        if (width == WholeGeometryValue(before.Width)) width = before.Width;
        if (height == WholeGeometryValue(before.Height)) height = before.Height;
        if (rotation == WholeGeometryValue(before.RotationDegrees)) rotation = before.RotationDegrees;

        // A locked stamp aspect fills in the untouched dimension from the touched one, so the
        // numeric fields and the proportional scale can never disagree about the shape.
        if (before.Stamp is not null && StampAspectLocked && before.Width > 0 && before.Height > 0)
        {
            if (width != before.Width && height == before.Height)
            {
                height = WholeGeometryValue(width * before.Height / before.Width);
            }
            else if (height != before.Height && width == before.Width)
            {
                width = WholeGeometryValue(height * before.Width / before.Height);
            }
        }

        var after = WithGeometry(before, x, y, width, height, rotation);

        if (after == before)
        {
            InspectorError = "";
            return true;
        }

        Session.Execute(new TransformObjectCommand(before, after));
        AfterEdit();
        return true;
    }

    private static bool TryParseWhole(string text, out double value) =>
        double.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    private static double WholeGeometryValue(double value)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }

    private static double NormalizeAngle(double angle)
    {
        var normalized = angle % 360;
        if (normalized <= -180) normalized += 360;
        if (normalized > 180) normalized -= 360;
        return normalized;
    }

    private static double NormalizeUnsignedAngle(double angle)
    {
        var normalized = angle % 360;
        if (normalized < 0) normalized += 360;
        return normalized;
    }

    private static SceneObject WithGeometry(
        SceneObject source, double x, double y, double width, double height, double rotation) =>
        new(
            source.Id, source.Kind, source.Name,
            QuantizeChanged(x, source.X), QuantizeChanged(y, source.Y),
            QuantizeChanged(width, source.Width, size: true),
            QuantizeChanged(height, source.Height, size: true),
            QuantizeChanged(rotation, source.RotationDegrees),
            source.FillColor, source.StrokeColor, source.StrokeWidth,
            source.Visible, source.Locked)
        {
            Sublayer = source.Sublayer,
            GroupId = source.GroupId,
            GroupName = source.GroupName,
            Stamp = source.Stamp,
            Line = source.Line,
            Shape = source.Shape,
            FillGradient = source.FillGradient,
            Text = source.Text,
            Composition = source.Composition,
            PlaceholderReason = source.PlaceholderReason,
            OpaquePayload = source.OpaquePayload,
            ObjectOpacity = source.ObjectOpacity,
        };

    private static double QuantizeChanged(double value, double original, bool size = false) =>
        value == original ? original
            : size ? Math.Max(1, WholeGeometryValue(value)) : WholeGeometryValue(value);

    private static IReadOnlyList<SceneObject> QuantizeTransformedGeometries(
        IReadOnlyList<SceneObject> before, IEnumerable<SceneObject> proposed)
    {
        var originals = before.ToDictionary(item => item.Id);
        return [.. proposed.Select(item =>
        {
            var original = originals[item.Id];
            return WithGeometry(item,
                QuantizeChanged(item.X, original.X), QuantizeChanged(item.Y, original.Y),
                QuantizeChanged(item.Width, original.Width, size: true),
                QuantizeChanged(item.Height, original.Height, size: true),
                QuantizeChanged(item.RotationDegrees, original.RotationDegrees));
        })];
    }

    private static SceneObject CopyObject(
        SceneObject source, bool? visible = null, bool? locked = null,
        string? fillColor = null, string? strokeColor = null, double? strokeWidth = null) =>
        new(
            source.Id, source.Kind, source.Name,
            source.X, source.Y, source.Width, source.Height, source.RotationDegrees,
            fillColor ?? source.FillColor, strokeColor ?? source.StrokeColor,
            strokeWidth ?? source.StrokeWidth,
            visible ?? source.Visible, locked ?? source.Locked)
        {
            Sublayer = source.Sublayer,
            GroupId = source.GroupId,
            GroupName = source.GroupName,
            Stamp = source.Stamp,
            Line = source.Line,
            Shape = source.Shape,
            FillGradient = source.FillGradient,
            Text = source.Text,
            Composition = source.Composition,
            PlaceholderReason = source.PlaceholderReason,
            OpaquePayload = source.OpaquePayload,
            ObjectOpacity = source.ObjectOpacity,
        };

    // ------------------------------------------------- Stage 10D appearance inspector

    private SceneObject? _appearanceGestureBefore;
    private bool _stampAspectLocked = true;
    private string? _brushTipName;
    private string? _brushTextureName;
    private string? _brushTextureSha256;
    private BrushPresetKind _brushPresetKind = BrushPresetKind.RoundSoft;
    private IReadOnlyList<LocalBrushPreset> _brushPresets = [];
    private int _brushSeedCounter;

    /// <summary>
    /// The one editable single selection for a style edit. Unlike the pure object-flag check,
    /// this honours the layer too: an object on a hidden or locked layer refuses entirely.
    /// </summary>
    private SceneObject? EditableAppearanceSelection()
    {
        if (Session.SelectedObjectIds.Count != 1 || Session.SelectedObject is not { } selected)
        {
            return null;
        }
        if (PlacementOwnership.Owner(Session.Document, selected.Id) is not null)
        { StatusDetail = PlacementOwnership.DetachFirst; return null; }

        if (!IsObjectAvailable(selected))
        {
            StatusDetail = "Объект или его слой скрыт либо заблокирован — изменение отклонено.";
            return null;
        }

        return selected;
    }

    /// <summary>
    /// One appearance edit. commit=false previews live from the gesture's captured "before";
    /// commit=true lands the whole gesture (or a direct edit) as exactly ONE history entry.
    /// A change that returns the same value writes no history at all.
    /// </summary>
    private void EditAppearance(string name, bool commit, Func<SceneObject, SceneObject> change)
    {
        // A live gesture belongs to the object it started on. If the selection has moved on
        // since - or the gesture was never closed - the stale "before" would send this edit to the
        // previous object, which is exactly how a colour landed on the wrong shape.
        var selected = EditableAppearanceSelection();
        if (_appearanceGestureBefore is { } pending && pending.Id != selected?.Id)
        {
            _appearanceGestureBefore = null;
            DragPreview = null;
        }

        var before = _appearanceGestureBefore ?? selected;
        if (before is null)
        {
            return;
        }

        if (!commit)
        {
            _appearanceGestureBefore = before;
            DragPreview = new DragPreview([before], [change(before)], DragMode.Move, null, 0, 0, null);
            SceneChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _appearanceGestureBefore = null;
        DragPreview = null;
        var after = change(before);
        if (ReferenceEquals(after, before) || after.Equals(before))
        {
            SceneChanged?.Invoke(this, EventArgs.Empty);
            return; // no-op: no history entry
        }

        Session.Execute(new ReplaceObjectsCommand([before], [after], name));
        AfterEdit();
    }

    /// <summary>Abandons a live slider gesture without touching the document or the history.</summary>
    public void CancelAppearanceGesture()
    {
        _appearanceGestureBefore = null;
        _selectionGestureBefore = null;
        DragPreview = null;
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"#RRGGBB" or "#AARRGGBB" → canonical "#AARRGGBB"; anything else is null.</summary>
    private static string? NormalizeColor(string? text) => AppearanceColor.Normalize(text);

    private static string? NormalizeSha256(string? text)
    {
        var value = (text ?? "").Trim().ToLowerInvariant();
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value : null;
    }

    private string? RequireColorInput(string? text)
    {
        var color = NormalizeColor(text);
        if (color is null)
        {
            InspectorError = "Цвет задаётся как #RRGGBB или #AARRGGBB.";
        }

        return color;
    }

    private bool RequireSupportedBlend(string blend)
    {
        if (BlendModes.IsSupported(blend))
        {
            return true;
        }

        InspectorError = $"Режим смешивания «{blend}» не поддерживается.";
        return false;
    }

    // ------------------------------------------------------------- common object properties

    public void SetSelectedSublayer(int value) => EditAppearance("Object sublayer", commit: true,
        before => before.Sublayer == Math.Clamp(value, -10, 10)
            ? before
            : CopyObject(before) with { Sublayer = Math.Clamp(value, -10, 10) });

    public void SetSelectedOpacityPercent(double percent, bool commit = true)
    {
        if (!double.IsFinite(percent))
        {
            InspectorError = "Непрозрачность задаётся числом 0..100.";
            return;
        }

        var opacity = Math.Clamp(percent, 0, 100) / 100.0;
        EditAppearance("Object opacity", commit, before => before.ObjectOpacity == opacity
            ? before
            : CopyObject(before) with { ObjectOpacity = opacity });
    }

    // ----------------------------------------------------------------------- stamp: scale

    public double? StampRotationDegrees => IsEditingFutureStamp
        ? NormalizeUnsignedAngle(WholeGeometryValue(_stampToolSettings.RotationDegrees))
        : Session.SelectedObject is { Stamp: not null } selected
            ? NormalizeUnsignedAngle(WholeGeometryValue(selected.RotationDegrees)) : null;

    public int? StampSublayer => IsEditingFutureStamp
        ? _stampToolSettings.Sublayer
        : Session.SelectedObject is { Stamp: not null } selected ? selected.Sublayer : null;

    public double? StampOpacityPercent => IsEditingFutureStamp
        ? _stampToolSettings.ObjectOpacity * 100
        : Session.SelectedObject is { Stamp: not null } selected ? selected.ObjectOpacity * 100 : null;

    public void SetStampRotationDegrees(double degrees, bool commit = true)
    {
        if (!double.IsFinite(degrees))
        {
            InspectorError = "Поворот задаётся числом 0..359 градусов.";
            return;
        }

        var normalized = NormalizeUnsignedAngle(WholeGeometryValue(degrees));
        if (IsEditingFutureStamp)
        {
            UpdateStampToolSettings(settings => settings with { RotationDegrees = normalized }, commit);
            return;
        }

        EditAppearance("Stamp rotation", commit, before => before.Stamp is null
            ? before
            : WithGeometry(before, before.X, before.Y, before.Width, before.Height, normalized));
    }

    public void SetStampSublayer(int value, bool commit = true)
    {
        var bounded = Math.Clamp(value, -10, 10);
        if (IsEditingFutureStamp)
        {
            UpdateStampToolSettings(settings => settings with { Sublayer = bounded }, persist: commit);
            return;
        }

        EditAppearance("Object sublayer", commit,
            before => before.Sublayer == bounded ? before : CopyObject(before) with { Sublayer = bounded });
    }

    public void SetStampOpacityPercent(double percent, bool commit = true)
    {
        if (!double.IsFinite(percent))
        {
            InspectorError = "Непрозрачность задаётся числом 0..100.";
            return;
        }

        if (IsEditingFutureStamp)
        {
            UpdateStampToolSettings(
                settings => settings with { ObjectOpacity = Math.Clamp(percent, 0, 100) / 100.0 }, commit);
            return;
        }

        SetSelectedOpacityPercent(percent, commit);
    }

    /// <summary>
    /// Scale against the stamp's 100 % reference. An old file without a reference reports its
    /// current size as the baseline, and the first scale edit adopts it permanently.
    /// </summary>
    public double? StampScalePercent
    {
        get
        {
            if (IsEditingFutureStamp)
            {
                return WholeGeometryValue(_stampToolSettings.ScalePercent);
            }

            if (Session.SelectedObject is not { Stamp: { } stamp } selected)
            {
                return null;
            }

            var reference = stamp.ReferenceWidth ?? selected.Width;
            return reference <= 0 ? null : WholeGeometryValue(selected.Width / reference * 100);
        }
    }

    /// <summary>Editor-only preference: exact W/H edits keep the stamp's proportions.</summary>
    public bool StampAspectLocked
    {
        get => _stampAspectLocked;
        set => SetField(ref _stampAspectLocked, value);
    }

    public void SetStampScalePercent(double percent, bool commit = true)
    {
        if (!double.IsFinite(percent))
        {
            InspectorError = "Масштаб задаётся числом в процентах.";
            return;
        }

        var bounded = Math.Clamp(WholeGeometryValue(percent), 10, 400);
        if (IsEditingFutureStamp)
        {
            UpdateStampToolSettings(settings => settings with { ScalePercent = bounded }, commit);
            return;
        }

        var factor = bounded / 100.0;
        EditAppearance("Stamp scale", commit, before =>
        {
            if (before.Stamp is not { } stamp)
            {
                return before;
            }

            var referenceWidth = stamp.ReferenceWidth ?? before.Width;
            var referenceHeight = stamp.ReferenceHeight ?? before.Height;
            var width = Math.Max(0.5, referenceWidth * factor);
            var height = Math.Max(0.5, referenceHeight * factor);
            if (width == before.Width && height == before.Height && stamp.ReferenceWidth is not null)
            {
                return before;
            }

            var centreX = before.X + (before.Width / 2);
            var centreY = before.Y + (before.Height / 2);
            return WithGeometry(
                before, centreX - (width / 2), centreY - (height / 2), width, height,
                before.RotationDegrees) with
            {
                Stamp = stamp with { ReferenceWidth = referenceWidth, ReferenceHeight = referenceHeight },
            };
        });
    }

    /// <summary>Exact width/height; the locked aspect fills in the other dimension.</summary>
    public void SetStampExactSize(double? width = null, double? height = null)
    {
        if (width is { } w && (!double.IsFinite(w) || w <= 0)
            || height is { } h && (!double.IsFinite(h) || h <= 0))
        {
            InspectorError = "Размер должен быть положительным целым числом.";
            return;
        }

        width = width is { } wholeWidth ? WholeGeometryValue(wholeWidth) : null;
        height = height is { } wholeHeight ? WholeGeometryValue(wholeHeight) : null;

        EditAppearance("Stamp size", commit: true, before =>
        {
            if (before.Stamp is null)
            {
                return before;
            }

            var aspect = before.Width <= 0 ? 1 : before.Height / before.Width;
            var newWidth = width ?? (StampAspectLocked && height is { } setH
                ? Math.Max(0.5, setH / Math.Max(0.0001, aspect))
                : before.Width);
            var newHeight = height ?? (StampAspectLocked && width is { } setW
                ? Math.Max(0.5, setW * aspect)
                : before.Height);
            if (newWidth == before.Width && newHeight == before.Height)
            {
                return before;
            }

            var centreX = before.X + (before.Width / 2);
            var centreY = before.Y + (before.Height / 2);
            return WithGeometry(
                before, centreX - (newWidth / 2), centreY - (newHeight / 2),
                newWidth, newHeight, before.RotationDegrees);
        });
    }

    // ------------------------------------------------------------- stamp: flips and effects

    private void EditStamp(string name, bool commit, Func<StampStyle, StampStyle> change)
    {
        if (IsEditingFutureStamp)
        {
            if (_stampToolSettings.Style is not { } style)
            {
                return;
            }

            UpdateStampToolSettings(settings => settings with { Style = change(style) }, commit);
            return;
        }

        EditAppearance(name, commit, before =>
        {
            if (before.Stamp is not { } stamp)
            {
                return before;
            }

            var changed = change(stamp);
            return changed.Equals(stamp) ? before : CopyObject(before) with { Stamp = changed };
        });
    }

    public void SetStampFlip(bool? horizontal = null, bool? vertical = null) =>
        EditStamp("Stamp flip", commit: true, stamp => stamp with
        {
            FlipHorizontal = horizontal ?? stamp.FlipHorizontal,
            FlipVertical = vertical ?? stamp.FlipVertical,
        });

    public void SetStampShadowNone() =>
        EditStamp("Stamp shadow", commit: true, stamp => stamp with { ShadowOpacity = 0 });

    public void SetStampShadowStandard() =>
        EditStamp("Stamp shadow", commit: true, stamp => stamp with
        {
            ShadowOffsetX = ShadowPreset.StandardOffsetX,
            ShadowOffsetY = ShadowPreset.StandardOffsetY,
            ShadowBlur = ShadowPreset.StandardBlur,
            ShadowOpacity = ShadowPreset.StandardOpacity,
            ShadowPlacement = StampShadowPlacement.Object,
            ShadowColor = AppearanceColor.OpaqueBlack,
        });

    public void SetStampShadowPlacement(StampShadowPlacement placement) =>
        EditStamp("Stamp shadow placement", commit: true, stamp => stamp with
        {
            ShadowPlacement = Enum.IsDefined(placement) ? placement : StampShadowPlacement.Object,
            ShadowOpacity = stamp.ShadowOpacity > 0 ? stamp.ShadowOpacity : ShadowPreset.StandardOpacity,
        });

    public void SetStampShadowColor(string? colorText, bool commit = true)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditStamp("Stamp shadow colour", commit, stamp => stamp with { ShadowColor = color });
        }
    }

    public void SetStampShadow(
        double? offsetX = null, double? offsetY = null, double? blur = null,
        double? opacityPercent = null, bool commit = true) =>
        EditStamp("Stamp shadow", commit, stamp => stamp with
        {
            ShadowOffsetX = Finite(offsetX, stamp.ShadowOffsetX, -64, 64),
            ShadowOffsetY = Finite(offsetY, stamp.ShadowOffsetY, -64, 64),
            ShadowBlur = Finite(blur, stamp.ShadowBlur, 0, 64),
            ShadowOpacity = Finite(opacityPercent, stamp.ShadowOpacity * 100, 0, 100) / 100.0,
        });

    public void SetStampBlend(string blend)
    {
        if (RequireSupportedBlend(blend))
        {
            EditStamp("Stamp blend", commit: true, stamp => stamp with { Blend = blend });
        }
    }

    public void SetStampBlur(double sigma, bool commit = true) =>
        EditStamp("Stamp blur", commit, stamp => stamp with { Blur = Finite(sigma, stamp.Blur, 0, 32) });

    public void SetStampHueDegrees(double degrees, bool commit = true) =>
        EditStamp("Stamp hue", commit, stamp => stamp with { Hue = Finite(degrees, stamp.Hue, -180, 180) });

    public void SetStampSaturationPercent(double percent, bool commit = true) =>
        EditStamp("Stamp saturation", commit, stamp => stamp with
        {
            Saturation = PercentOffset(percent, stamp.Saturation),
        });

    public void SetStampBrightnessPercent(double percent, bool commit = true) =>
        EditStamp("Stamp brightness", commit, stamp => stamp with
        {
            Brightness = PercentOffset(percent, stamp.Brightness),
        });

    public void SetStampContrastPercent(double percent, bool commit = true) =>
        EditStamp("Stamp contrast", commit, stamp => stamp with
        {
            Contrast = PercentOffset(percent, stamp.Contrast),
        });

    /// <summary>Hue/Saturation/Brightness/Contrast back to neutral, one entry, nothing else.</summary>
    public void ResetStampColorAdjustments() =>
        EditStamp("Reset stamp colors", commit: true, stamp => stamp with
        {
            Hue = 0,
            Saturation = 0,
            Brightness = 0,
            Contrast = 0,
        });

    /// <summary>The whole appearance back to neutral in one entry; transform stays untouched.</summary>
    public void ResetStampAppearance() =>
        ResetStampAppearanceCore();

    private void ResetStampAppearanceCore()
    {
        if (IsEditingFutureStamp && _stampToolSettings.Style is { } toolStyle)
        {
            UpdateStampToolSettings(settings => settings with
            {
                ObjectOpacity = 1,
                Style = new StampStyle(toolStyle.AssetSha256)
                {
                    ReferenceWidth = toolStyle.ReferenceWidth,
                    ReferenceHeight = toolStyle.ReferenceHeight,
                    FlipHorizontal = toolStyle.FlipHorizontal,
                    FlipVertical = toolStyle.FlipVertical,
                    MaskAssetSha256 = toolStyle.MaskAssetSha256,
                    ClipMaskEnabled = toolStyle.ClipMaskEnabled,
                },
            }, persist: true);
            return;
        }

        EditAppearance("Reset stamp appearance", commit: true, before =>
        {
            if (before.Stamp is not { } stamp)
            {
                return before;
            }

            var neutral = new StampStyle(stamp.AssetSha256)
            {
                ReferenceWidth = stamp.ReferenceWidth,
                ReferenceHeight = stamp.ReferenceHeight,
                FlipHorizontal = stamp.FlipHorizontal,
                FlipVertical = stamp.FlipVertical,
                MaskAssetSha256 = stamp.MaskAssetSha256,
                ClipMaskEnabled = stamp.ClipMaskEnabled,
            };
            return neutral.Equals(stamp) && before.ObjectOpacity == 1.0
                ? before
                : CopyObject(before) with { Stamp = neutral, ObjectOpacity = 1.0 };
        });
    }

    private double PercentOffset(double percent, double fallback)
    {
        if (!double.IsFinite(percent))
        {
            InspectorError = "Значение задаётся в процентах 0..200.";
            return fallback;
        }

        return ColorAdjustment.PercentToOffset(percent);
    }

    private static double Finite(double? value, double fallback, double min, double max) =>
        value is { } v && double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    // ----------------------------------------------------------------------------- text

    private void EditText(string name, bool commit, Func<TextStyle, TextStyle> change) =>
        EditAppearance(name, commit, before =>
        {
            if (before.Text is not { } text)
            {
                return before;
            }

            var changed = change(text);
            return changed.Equals(text) ? before : CopyObject(before) with { Text = changed };
        });

    public void SetTextContent(string content) =>
        EditText("Text content", commit: true, text => text with { Content = content ?? "" });

    public void SetTextBold(bool bold) =>
        EditText("Text weight", commit: true, text => text with { FontWeight = bold ? 700 : 400 });

    public void SetTextFontSize(double size, bool commit = true) =>
        EditText("Text size", commit, text => text with { FontSize = Finite(size, text.FontSize, 1, 512) });

    public void SetTextFillColor(string? colorText, bool commit = true)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditText("Text fill", commit, text => text with { FillColor = color });
        }
    }

    public void SetTextOutlineColor(string? colorText, bool commit = true)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditText("Text outline", commit, text => text with { OutlineColor = color });
        }
    }

    public void SetTextOutlineWidth(double width, bool commit = true) =>
        EditText("Text outline", commit, text => text with
        {
            OutlineWidth = Finite(width, text.OutlineWidth, 0, 32),
        });

    public void SetTextShadowNone() =>
        EditText("Text shadow", commit: true, text => text with { ShadowOpacity = 0 });

    public void SetTextShadowStandard() =>
        EditText("Text shadow", commit: true, text => text with
        {
            ShadowOffsetX = ShadowPreset.StandardOffsetX,
            ShadowOffsetY = ShadowPreset.StandardOffsetY,
            ShadowBlur = ShadowPreset.StandardBlur,
            ShadowOpacity = ShadowPreset.StandardOpacity,
        });

    public void SetTextShadow(
        double? offsetX = null, double? offsetY = null, double? blur = null,
        double? opacityPercent = null, bool commit = true) =>
        EditText("Text shadow", commit, text => text with
        {
            ShadowOffsetX = Finite(offsetX, text.ShadowOffsetX, -64, 64),
            ShadowOffsetY = Finite(offsetY, text.ShadowOffsetY, -64, 64),
            ShadowBlur = Finite(blur, text.ShadowBlur, 0, 64),
            ShadowOpacity = Finite(opacityPercent, text.ShadowOpacity * 100, 0, 100) / 100.0,
        });

    public void SetTextCurve(double value, bool commit = true) =>
        EditText("Text curve", commit, text => text with { Curve = Finite(value, text.Curve, -100, 100) });

    public void SetTextLetterSpacing(double value, bool commit = true) =>
        EditText("Text letter spacing", commit, text => text with
        {
            LetterSpacing = Finite(value, text.LetterSpacing, 0, 40),
        });

    public void SetTextLineHeight(double value, bool commit = true) =>
        EditText("Text line height", commit, text => text with
        {
            LineHeight = Finite(value, text.LineHeight, 0.5, 3),
        });

    public void SetTextAlignment(TextAlignment alignment) =>
        EditText("Text alignment", commit: true, text => text with { Alignment = alignment });

    public void SetTextFlip(bool? horizontal = null, bool? vertical = null) =>
        EditText("Text flip", commit: true, text => text with
        {
            FlipHorizontal = horizontal ?? text.FlipHorizontal,
            FlipVertical = vertical ?? text.FlipVertical,
        });

    public void SetTextBlend(string blend)
    {
        if (RequireSupportedBlend(blend))
        {
            EditText("Text blend", commit: true, text => text with { Blend = blend });
        }
    }

    public void SetTextBlur(double sigma, bool commit = true) =>
        EditText("Text blur", commit, text => text with { Blur = Finite(sigma, text.Blur, 0, 32) });

    // -------------------------------------------------------- vector stroke (line or shape)

    /// <summary>True when the selected Line/Shape strokes with a repeating asset.</summary>
    public bool SelectedStrokeHasAsset =>
        (Session.SelectedObject?.Line?.Stroke ?? Session.SelectedObject?.Shape?.Stroke)
            ?.AssetSha256 is not null;

    private static SceneObject WithStroke(SceneObject before, VectorStrokeStyle stroke) =>
        before.Line is { } line
            ? before with { Line = new LineStyle(line.Nodes, stroke, line.Portals, line.Closed) }
            : before.Shape is { } shape
                ? before with { Shape = new ShapeStyle(shape.Regions, shape.Fill, stroke, shape.Portals) }
                : before;

    private void EditStroke(string name, bool commit, Func<VectorStrokeStyle, VectorStrokeStyle> change) =>
        EditAppearance(name, commit, before =>
        {
            var stroke = before.Line?.Stroke ?? before.Shape?.Stroke;
            if (stroke is null)
            {
                return before;
            }

            var changed = change(stroke);
            return changed.Equals(stroke) ? before : WithStroke(before, changed);
        });

    public void SetStrokeColor(string? colorText, bool commit = true)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditStroke("Stroke color", commit, stroke => stroke with { Color = color });
        }
    }

    public void SetStrokeWidth(double width, bool commit = true) =>
        EditStroke("Stroke width", commit, stroke => stroke with
        {
            Width = Finite(width, stroke.Width, 0.1, 256),
        });

    public void SetStrokeOpacityPercent(double percent, bool commit = true) =>
        EditStroke("Stroke opacity", commit, stroke => stroke with
        {
            Opacity = Finite(percent, stroke.Opacity * 100, 0, 100) / 100.0,
        });

    public void SetStrokeBlend(string blend)
    {
        if (RequireSupportedBlend(blend))
        {
            EditStroke("Stroke blend", commit: true, stroke => stroke with { Blend = blend });
        }
    }

    public void SetStrokeBlur(double sigma, bool commit = true) =>
        EditStroke("Stroke blur", commit, stroke => stroke with { Blur = Finite(sigma, stroke.Blur, 0, 32) });

    public void SetStrokeOffset(double offset, bool commit = true) =>
        EditStroke("Stroke offset", commit, stroke => stroke with
        {
            Offset = Finite(offset, stroke.Offset, -128, 128),
        });

    public void SetStrokeEndCap(VectorCap cap) =>
        EditStroke("Stroke end cap", commit: true, stroke => stroke with { EndCap = cap });

    public void SetStrokeNodeCap(VectorCap cap) =>
        EditStroke("Stroke node cap", commit: true, stroke => stroke with { NodeCap = cap });

    public void SetStrokePattern(StrokePattern pattern) =>
        EditStroke("Stroke pattern", commit: true, stroke => stroke with { Pattern = pattern });

    public void SetStrokeShadowNone() =>
        EditStroke("Stroke shadow", commit: true, stroke => stroke with { ShadowOpacity = 0 });

    public void SetStrokeShadowStandard() =>
        EditStroke("Stroke shadow", commit: true, stroke => stroke with
        {
            ShadowOffsetX = ShadowPreset.StandardOffsetX,
            ShadowOffsetY = ShadowPreset.StandardOffsetY,
            ShadowBlur = ShadowPreset.StandardBlur,
            ShadowOpacity = ShadowPreset.StandardOpacity,
        });

    public void SetStrokeShadow(
        double? offsetX = null, double? offsetY = null, double? blur = null,
        double? opacityPercent = null, bool commit = true) =>
        EditStroke("Stroke shadow", commit, stroke => stroke with
        {
            ShadowOffsetX = Finite(offsetX, stroke.ShadowOffsetX, -64, 64),
            ShadowOffsetY = Finite(offsetY, stroke.ShadowOffsetY, -64, 64),
            ShadowBlur = Finite(blur, stroke.ShadowBlur, 0, 64),
            ShadowOpacity = Finite(opacityPercent, stroke.ShadowOpacity * 100, 0, 100) / 100.0,
        });

    public void SetStrokeHueDegrees(double degrees, bool commit = true)
    {
        if (!SelectedStrokeHasAsset)
        {
            StatusDetail = "Оттенок доступен только для обводки из ассета.";
            return;
        }

        EditStroke("Stroke hue", commit, stroke => stroke with
        {
            Hue = Finite(degrees, stroke.Hue, -180, 180),
        });
    }

    public void SetStrokeSaturationPercent(double percent, bool commit = true)
    {
        if (!SelectedStrokeHasAsset)
        {
            StatusDetail = "Насыщенность доступна только для обводки из ассета.";
            return;
        }

        EditStroke("Stroke saturation", commit, stroke => stroke with
        {
            Saturation = PercentOffset(percent, stroke.Saturation),
        });
    }

    /// <summary>Stroke appearance back to defaults; width, offset, caps and asset survive.</summary>
    public void ResetStrokeAppearance() =>
        EditStroke("Reset stroke", commit: true, stroke => new VectorStrokeStyle
        {
            Width = stroke.Width,
            Offset = stroke.Offset,
            EndCap = stroke.EndCap,
            NodeCap = stroke.NodeCap,
            AssetSha256 = stroke.AssetSha256,
            AssetSpacing = stroke.AssetSpacing,
        });

    // -------------------------------------------------------------------------- shape fill

    private void EditShapeFill(string name, bool commit, Func<ShapeFillStyle, ShapeFillStyle> change) =>
        EditAppearance(name, commit, before =>
        {
            if (before.Shape is not { } shape)
            {
                return before;
            }

            var changed = change(shape.Fill);
            return changed.Equals(shape.Fill)
                ? before
                : before with { Shape = new ShapeStyle(shape.Regions, changed, shape.Stroke, shape.Portals) };
        });

    public void SetShapeFillColor(string? colorText, bool commit = true)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditShapeFill("Shape fill", commit, fill => fill with { Color = color });
        }
    }

    public void SetShapeFillOpacityPercent(double percent, bool commit = true) =>
        EditShapeFill("Shape fill opacity", commit, fill => fill with
        {
            Opacity = Finite(percent, fill.Opacity * 100, 0, 100) / 100.0,
        });

    public void SetShapeFillTextureScale(double value, bool commit = true) =>
        EditShapeFill("Shape texture scale", commit, fill => fill with
        { TextureScale = Finite(value, fill.TextureScale, .05, 64) });

    public void SetShapeFillTextureRotation(double value, bool commit = true) =>
        EditShapeFill("Shape texture rotation", commit, fill => fill with
        { TextureRotation = Finite(value, fill.TextureRotation, -180, 180) });

    public void SetShapeFillTextureOffsetX(double value, bool commit = true) =>
        EditShapeFill("Shape texture offset X", commit, fill => fill with
        { TextureOffsetX = Finite(value, fill.TextureOffsetX, -4096, 4096) });

    public void SetShapeFillTextureOffsetY(double value, bool commit = true) =>
        EditShapeFill("Shape texture offset Y", commit, fill => fill with
        { TextureOffsetY = Finite(value, fill.TextureOffsetY, -4096, 4096) });

    public void ResetShapeFillTexture() =>
        EditShapeFill("Reset shape texture", commit: true, fill => fill with
        {
            TextureAssetSha256 = null, TextureScale = 1, TextureRotation = 0,
            TextureOffsetX = 0, TextureOffsetY = 0,
        });

    /// <summary>Fill back to its defaults; the texture reference is dropped with the rest.</summary>
    public void ResetShapeFill() =>
        EditShapeFill("Reset shape fill", commit: true, _ => new ShapeFillStyle());

    // ------------------------------------------------------- plain primitives (rectangle, ellipse)

    /// <summary>
    /// True for the objects whose colour lives on the object itself rather than in a Shape/Line
    /// style. They had no inspector block at all, so a placed rectangle kept its creation colour
    /// for ever - these three setters are what makes it editable.
    /// </summary>
    public bool SelectionIsPrimitive =>
        Session.SelectedObject is { Kind: SceneObjectKind.Rectangle or SceneObjectKind.Ellipse };

    public void SetPrimitiveFillColor(string? colorText, bool commit = true)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditAppearance("Object fill", commit, before =>
                before.FillColor == color ? before : CopyObject(before, fillColor: color));
        }
    }

    public void SetPrimitiveStrokeColor(string? colorText, bool commit = true)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditAppearance("Object stroke", commit, before =>
                before.StrokeColor == color ? before : CopyObject(before, strokeColor: color));
        }
    }

    public void SetPrimitiveStrokeWidth(double value, bool commit = true) =>
        EditAppearance("Object stroke width", commit, before =>
        {
            var width = Finite(value, before.StrokeWidth, 0, 64);
            return Math.Abs(before.StrokeWidth - width) < 0.0001
                ? before
                : CopyObject(before, strokeWidth: width);
        });

    // ------------------------------------------------------------------------ gradient fill

    /// <summary>
    /// The gradient of the single selected object, whichever kind it is: a Shape keeps it in its
    /// fill, a plain Rectangle/Ellipse keeps it on the object. The inspector reads one property so
    /// there is one gradient control instead of two that drift apart.
    /// </summary>
    public GradientFill? SelectedGradient =>
        Session.SelectedObject is { } selected
            ? selected.Shape?.Fill.Gradient ?? selected.FillGradient
            : null;

    /// <summary>True for the object kinds that actually have a fill a gradient can replace.</summary>
    public bool SelectionSupportsGradient =>
        Session.SelectedObject is { Kind: SceneObjectKind.Shape or SceneObjectKind.Rectangle
            or SceneObjectKind.Ellipse };

    /// <summary>The flat colour a new gradient starts from, so turning one on is not a jump.</summary>
    private string SelectedFlatFillColor() =>
        Session.SelectedObject is { } selected
            ? selected.Shape?.Fill.Color ?? selected.FillColor
            : AppearanceColor.OpaqueBlack;

    private void EditGradient(string name, bool commit, Func<GradientFill?, GradientFill?> change) =>
        EditAppearance(name, commit, before =>
        {
            if (before.Shape is { } shape)
            {
                var changed = change(shape.Fill.Gradient);
                return Equals(changed, shape.Fill.Gradient)
                    ? before
                    : before with
                    {
                        Shape = new ShapeStyle(
                            shape.Regions, shape.Fill with { Gradient = changed },
                            shape.Stroke, shape.Portals),
                    };
            }

            if (before.Kind is SceneObjectKind.Rectangle or SceneObjectKind.Ellipse)
            {
                var changed = change(before.FillGradient);
                return Equals(changed, before.FillGradient)
                    ? before
                    : before with { FillGradient = changed };
            }

            return before;
        });

    /// <summary>
    /// One edit of a gradient that must already exist. A control that fires while the object has
    /// no gradient is a no-op rather than a silent creation the user did not ask for.
    /// </summary>
    private void EditExistingGradient(string name, bool commit, Func<GradientFill, GradientFill> change) =>
        EditGradient(name, commit, current => current is null ? null : change(current));

    /// <summary>
    /// The one entry point of the gradient toggle: null turns the gradient off, a kind turns it on
    /// (seeded from the current flat colour) or switches an existing one. Creating and choosing the
    /// kind in a single command means picking "radial" on a flat object is one undo, not two.
    /// </summary>
    public void SetGradientMode(GradientKind? kind)
    {
        if (!SelectionSupportsGradient)
        {
            StatusDetail = "Градиент доступен фигурам, прямоугольникам и эллипсам.";
            return;
        }

        var seed = SelectedFlatFillColor();
        EditGradient(kind is null ? "Disable gradient" : "Gradient mode", commit: true, current =>
            kind is not { } wanted
                ? null
                : current is null
                    ? GradientFill.Default(seed, wanted)
                    : current with { Kind = wanted });
    }

    public void SetGradientAngle(double degrees, bool commit = true) =>
        EditExistingGradient("Gradient angle", commit, current => new GradientFill(
            current.Kind, current.Stops,
            Finite(degrees, current.AngleDegrees, 0, 360),
            current.CenterX, current.CenterY, current.Radius));

    public void SetGradientCenterX(double value, bool commit = true) =>
        EditExistingGradient("Gradient centre X", commit, current => new GradientFill(
            current.Kind, current.Stops, current.AngleDegrees,
            Finite(value, current.CenterX, 0, 1), current.CenterY, current.Radius));

    public void SetGradientCenterY(double value, bool commit = true) =>
        EditExistingGradient("Gradient centre Y", commit, current => new GradientFill(
            current.Kind, current.Stops, current.AngleDegrees,
            current.CenterX, Finite(value, current.CenterY, 0, 1), current.Radius));

    public void SetGradientRadius(double value, bool commit = true) =>
        EditExistingGradient("Gradient radius", commit, current => new GradientFill(
            current.Kind, current.Stops, current.AngleDegrees,
            current.CenterX, current.CenterY, Finite(value, current.Radius, 0.05, 4)));

    public void SetGradientStopColor(int index, string? colorText, bool commit = true)
    {
        if (RequireColorInput(colorText) is not { } color)
        {
            return;
        }

        EditExistingGradient("Gradient stop colour", commit, current =>
            index < 0 || index >= current.Stops.Count
                ? current
                : new GradientFill(
                    current.Kind,
                    [.. current.Stops.Select((stop, at) =>
                        at == index ? new GradientStopStyle(stop.Offset, color) : stop)],
                    current.AngleDegrees, current.CenterX, current.CenterY, current.Radius));
    }

    public void SetGradientStopOffset(int index, double offset, bool commit = true) =>
        EditExistingGradient("Gradient stop position", commit, current =>
            index < 0 || index >= current.Stops.Count
                ? current
                : new GradientFill(
                    current.Kind,
                    [.. current.Stops.Select((stop, at) => at == index
                        ? new GradientStopStyle(Finite(offset, stop.Offset, 0, 1), stop.Color)
                        : stop)],
                    current.AngleDegrees, current.CenterX, current.CenterY, current.Radius));

    /// <summary>
    /// Adds a stop in the widest gap, with the colour the ramp already shows there, so the new
    /// stop changes nothing until the user moves or recolours it.
    /// </summary>
    public void AddGradientStop()
    {
        if (SelectedGradient is { } current && current.Stops.Count >= GradientFill.MaxStops)
        {
            StatusDetail = $"У градиента не больше {GradientFill.MaxStops} точек цвета.";
            return;
        }

        EditExistingGradient("Add gradient stop", commit: true, gradient =>
        {
            var widest = 0;
            var widestGap = double.MinValue;
            for (var index = 0; index < gradient.Stops.Count - 1; index++)
            {
                var gap = gradient.Stops[index + 1].Offset - gradient.Stops[index].Offset;
                if (gap > widestGap)
                {
                    widestGap = gap;
                    widest = index;
                }
            }

            var left = gradient.Stops[widest];
            var right = gradient.Stops[widest + 1];
            var stops = gradient.Stops.ToList();
            stops.Insert(widest + 1, new GradientStopStyle(
                (left.Offset + right.Offset) / 2, MixColors(left.Color, right.Color)));
            return new GradientFill(gradient.Kind, stops, gradient.AngleDegrees,
                gradient.CenterX, gradient.CenterY, gradient.Radius);
        });
    }

    public void RemoveGradientStop(int index)
    {
        if (SelectedGradient is { } current && current.Stops.Count <= GradientFill.MinStops)
        {
            StatusDetail = "У градиента не меньше двух точек цвета.";
            return;
        }

        EditExistingGradient("Remove gradient stop", commit: true, gradient =>
            index < 0 || index >= gradient.Stops.Count
                ? gradient
                : new GradientFill(
                    gradient.Kind,
                    [.. gradient.Stops.Where((_, at) => at != index)],
                    gradient.AngleDegrees, gradient.CenterX, gradient.CenterY, gradient.Radius));
    }

    /// <summary>The midpoint of two canonical colours, channel by channel including alpha.</summary>
    private static string MixColors(string left, string right)
    {
        var a = uint.Parse(left.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = uint.Parse(right.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var mixed = 0u;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var channel = (byte)Math.Round(((((a >> shift) & 0xFF) + ((b >> shift) & 0xFF)) / 2.0));
            mixed |= (uint)channel << shift;
        }

        return "#" + mixed.ToString("X8", CultureInfo.InvariantCulture);
    }

    /// <summary>Whole-object effects back to neutral: opacity, blend, blur.</summary>
    public void ResetShapeEffects() =>
        EditAppearance("Reset shape effects", commit: true, before =>
        {
            if (before.Shape is not { } shape)
            {
                return before;
            }

            var stroke = shape.Stroke with { Blend = "Normal", Blur = 0 };
            var unchanged = stroke.Equals(shape.Stroke) && before.ObjectOpacity == 1.0;
            return unchanged
                ? before
                : CopyObject(before) with
                {
                    ObjectOpacity = 1.0,
                    Shape = new ShapeStyle(shape.Regions, shape.Fill, stroke, shape.Portals),
                };
        });

    // ------------------------------------------------------------------- brush tip state

    public void SetBrushColor(string colorText)
    {
        if (RequireColorInput(colorText) is not { } color)
        {
            return;
        }

        InspectorError = "";
        BrushSettings = BrushSettings with { Color = color };
        StatusDetail = $"Цвет кисти: {color}.";
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Display name of the active tip, or null for the procedural round brush.</summary>
    public string? BrushTipName => _brushTipName;

    /// <summary>Sets the alpha tip the brush stamps with. Tool state, not a document edit.</summary>
    public void SetBrushTip(BrushTipShape tip, string name)
    {
        ArgumentNullException.ThrowIfNull(tip);
        BrushSettings = BrushSettings with { Tip = tip };
        _brushTipName = name;
        Raise(nameof(BrushTipName));
        StatusDetail = $"Отпечаток кисти: {name}.";
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearBrushTip()
    {
        BrushSettings = BrushSettings with { Tip = null };
        _brushTipName = null;
        _brushPresetKind = BrushSettings.Softness > 0
            ? BrushPresetKind.RoundSoft
            : BrushPresetKind.RoundHard;
        Raise(nameof(BrushTipName));
        Raise(nameof(ActiveBrushPreset));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public string? BrushTextureName => _brushTextureName;

    public void ClearBrushTexture()
    {
        BrushSettings = BrushSettings with { Texture = null };
        _brushTextureName = null;
        _brushTextureSha256 = null;
        Raise(nameof(BrushTextureName));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool UseAssetAsBrushTexture(AssetRecord record)
    {
        if (_assetCatalog is null) return false;
        var resolution = _assetCatalog.ReadVerified(record);
        if (resolution.Status != AssetResolutionStatus.Available || resolution.Content is null)
        {
            StatusDetail = "Файл материала отсутствует или не совпадает с SHA-256.";
            return false;
        }
        try
        {
            using var bitmap = AssetImagePolicy.DecodeBitmap(resolution.Content, 1024, 1024);
            var pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                var alpha = color.Alpha / 255.0;
                var offset = ((y * bitmap.Width) + x) * 4;
                pixels[offset] = (byte)Math.Round(color.Red * alpha);
                pixels[offset + 1] = (byte)Math.Round(color.Green * alpha);
                pixels[offset + 2] = (byte)Math.Round(color.Blue * alpha);
                pixels[offset + 3] = color.Alpha;
            }
            BrushSettings = BrushSettings with { Texture = new BrushTexture(bitmap.Width, bitmap.Height, pixels), Eraser = false };
            _brushTextureName = record.DisplayName;
            _brushTextureSha256 = record.Sha256;
            if (CurrentVariant is { } variant && variant.Sha256 == record.Sha256)
                RememberCurrentVariant();
            Raise(nameof(BrushTextureName));
            StatusDetail = $"Материал кисти: {record.DisplayName}.";
            SceneChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StatusDetail = "Материал кисти отклонён: " + error.Message;
            return false;
        }
    }

    /// <summary>
    /// Which tip shape is chosen right now. The panel used to offer seven identical buttons with
    /// no indication of which one was in effect; a list needs to know what to select.
    /// </summary>
    public BrushPresetKind ActiveBrushPreset => _brushPresetKind;

    /// <summary>
    /// The actual stamp of a tip, so the panel can show what the engine will print rather than a
    /// decorative icon. Null means the plain circle, whose edge comes from Softness instead.
    /// </summary>
    public static BrushTipShape? PreviewTip(BrushPresetKind preset) =>
        preset is BrushPresetKind.RoundHard or BrushPresetKind.RoundSoft ? null : PresetTip(preset);

    /// <summary>The edge softness a tip is applied with, for the same preview.</summary>
    public static double PreviewSoftness(BrushPresetKind preset) =>
        preset == BrushPresetKind.RoundSoft ? 0.75 : 0;

    public void ApplyBrushPreset(BrushPresetKind preset)
    {
        _brushPresetKind = preset;
        BrushSettings = preset switch
        {
            BrushPresetKind.RoundHard => BrushSettings with { Tip = null, Softness = 0 },
            BrushPresetKind.RoundSoft => BrushSettings with { Tip = null, Softness = 0.75 },
            _ => BrushSettings with { Tip = PresetTip(preset), Softness = 0 },
        };
        _brushTipName = preset switch
        {
            BrushPresetKind.RoundHard => "Круглая жёсткая",
            BrushPresetKind.RoundSoft => "Круглая мягкая",
            _ => preset.ToString(),
        };
        Raise(nameof(BrushTipName));
        Raise(nameof(ActiveBrushPreset));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    private static BrushTipShape PresetTip(BrushPresetKind preset)
    {
        const int size = 64;
        var alpha = new byte[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var nx = ((x + .5) / size * 2) - 1;
            var ny = ((y + .5) / size * 2) - 1;
            var inside = preset switch
            {
                BrushPresetKind.Ellipse => (nx * nx / 1.0) + (ny * ny / .36) <= 1,
                BrushPresetKind.Polygon => ny >= -1 && ny <= .85 && Math.Abs(nx) <= 1 - ((ny + 1) * .18),
                BrushPresetKind.EdgeBlock => Math.Abs(nx) >= .55 || Math.Abs(ny) >= .55,

                // Rectangle used to fall into the same "whole square" branch as GridBlock, so the
                // two tips stamped identical marks and one of them was a duplicate entry.
                BrushPresetKind.Rectangle => Math.Abs(ny) <= .5,
                _ => true,
            };
            if (inside) alpha[(y * size) + x] = 255;
        }
        return new BrushTipShape(size, size, alpha);
    }

    public IReadOnlyList<LocalBrushPreset> BrushPresets => _brushPresets;

    public bool SaveBrushPreset(string name)
    {
        if (_brushPresetStore is null || string.IsNullOrWhiteSpace(name)) return false;
        var settings = BrushSettings;
        var preset = new LocalBrushPreset
        {
            Id = Guid.NewGuid().ToString("N"), Name = name.Trim(), TextureSha256 = _brushTextureSha256,
            TipKind = _brushPresetKind.ToString(), Radius = settings.Radius, Softness = settings.Softness,
            Opacity = settings.Opacity, Color = settings.Color, Spacing = settings.Spacing,
            Scatter = settings.Scatter, AngleDegrees = settings.AngleDegrees,
            RandomFlipHorizontal = settings.RandomFlipHorizontal,
            RandomFlipVertical = settings.RandomFlipVertical,
            SizeJitter = settings.SizeJitter, OpacityJitter = settings.OpacityJitter,
            TextureScale = settings.TextureScale,
            TextureRotationDegrees = settings.TextureRotationDegrees,
            TextureOffsetX = settings.TextureOffsetX, TextureOffsetY = settings.TextureOffsetY,
            TextureOpacity = settings.TextureOpacity, HueDegrees = settings.HueDegrees,
            Saturation = settings.Saturation, Brightness = settings.Brightness, Contrast = settings.Contrast,
            PressureAffectsSize = settings.PressureAffectsSize,
            PressureAffectsOpacity = settings.PressureAffectsOpacity,
            PressureAffectsSoftness = settings.PressureAffectsSoftness,
        };
        _brushPresets = _brushPresetStore.Save([.. _brushPresets, preset]);
        Raise(nameof(BrushPresets)); StatusDetail = $"Пресет «{preset.Name}» сохранён локально."; return true;
    }

    public bool ApplyLocalBrushPreset(string id)
    {
        if (_brushPresets.FirstOrDefault(item => item.Id == id) is not { } preset) return false;
        if (!Enum.TryParse<BrushPresetKind>(preset.TipKind, out var kind)) kind = BrushPresetKind.RoundSoft;
        ApplyBrushPreset(kind);
        BrushSettings = BrushSettings with
        {
            Radius = preset.Radius, Softness = preset.Softness, Opacity = preset.Opacity,
            Color = preset.Color, Spacing = preset.Spacing, Scatter = preset.Scatter,
            AngleDegrees = preset.AngleDegrees,
            RandomFlipHorizontal = preset.RandomFlipHorizontal,
            RandomFlipVertical = preset.RandomFlipVertical,
            SizeJitter = preset.SizeJitter, OpacityJitter = preset.OpacityJitter,
            TextureScale = preset.TextureScale, TextureRotationDegrees = preset.TextureRotationDegrees,
            TextureOffsetX = preset.TextureOffsetX, TextureOffsetY = preset.TextureOffsetY,
            TextureOpacity = preset.TextureOpacity, HueDegrees = preset.HueDegrees,
            Saturation = preset.Saturation, Brightness = preset.Brightness, Contrast = preset.Contrast,
            PressureAffectsSize = preset.PressureAffectsSize,
            PressureAffectsOpacity = preset.PressureAffectsOpacity,
            PressureAffectsSoftness = preset.PressureAffectsSoftness,
        };
        if (preset.TextureSha256 is { } sha && _assetCatalog?.Find(sha) is { } record)
            UseAssetAsBrushTexture(record);
        else
            ClearBrushTexture();
        SceneChanged?.Invoke(this, EventArgs.Empty); return true;
    }

    public bool RenameBrushPreset(string id, string name)
    {
        if (_brushPresetStore is null || string.IsNullOrWhiteSpace(name)) return false;
        var changed = false;
        _brushPresets = _brushPresetStore.Save([.. _brushPresets.Select(item =>
        {
            if (item.Id != id) return item;
            changed = true; return item with { Name = name.Trim() };
        })]);
        if (changed) Raise(nameof(BrushPresets)); return changed;
    }

    public bool DeleteBrushPreset(string id)
    {
        if (_brushPresetStore is null || _brushPresets.All(item => item.Id != id)) return false;
        _brushPresets = _brushPresetStore.Save([.. _brushPresets.Where(item => item.Id != id)]);
        Raise(nameof(BrushPresets)); return true;
    }

    /// <summary>Loads a library asset as the brush tip: its alpha channel becomes the dab shape.</summary>
    public bool UseAssetAsBrushTip(AssetRecord record)
    {
        if (_assetCatalog is null)
        {
            StatusDetail = "Локальный каталог ассетов недоступен.";
            return false;
        }

        var resolution = _assetCatalog.ReadVerified(record);
        if (resolution.Status != AssetResolutionStatus.Available || resolution.Content is null)
        {
            StatusDetail = "Файл отпечатка отсутствует или не совпадает с его SHA-256.";
            return false;
        }

        SkiaSharp.SKBitmap bitmap;
        try
        {
            bitmap = AssetImagePolicy.DecodeBitmap(resolution.Content, 1024, 1024);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StatusDetail = "Отпечаток кисти должен быть изображением не больше 1024×1024.";
            return false;
        }

        using (bitmap)
        {
            var alpha = new byte[checked(bitmap.Width * bitmap.Height)];
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    alpha[(y * bitmap.Width) + x] = bitmap.GetPixel(x, y).Alpha;
                }
            }

            SetBrushTip(new BrushTipShape(bitmap.Width, bitmap.Height, alpha), record.DisplayName);
        }
        return true;
    }

    // ---------------------------------------------------------------- autosave

    public static TimeSpan PrimaryAutosaveDebounce { get; } = TimeSpan.FromSeconds(4);

    public bool HasTransientEditorAction => HasAreaGesture || HasPathGesture || IsStampSeriesActive
        || PendingPlacement is { Target: not PlacementTargetKind.Stamp }
        || DragPreview is not null
        || _stroke is not null
        || _vectorPoints is not null
        || _textPlacementStart is not null
        || _pendingShapeBoolean is not null
        || CanvasWorkflowOpen;

    /// <summary>
    /// Writes one recovery snapshot if the current dirty state is not already covered. The call
    /// never queues behind a manual save: a timer tick may be skipped, while Ctrl+S must win.
    /// </summary>
    public async Task<bool> TryAutosaveAsync()
    {
        if (HasTransientEditorAction) return false;
        if (!Session.IsDirty)
        {
            await ClearAutosaveIfCleanAsync();
            return false;
        }

        var primarySaved = await TryPrimaryAutosaveAsync();
        if (!Session.IsDirty)
        {
            return primarySaved;
        }

        var recoverySaved = await TryRecoveryAutosaveAsync();
        return primarySaved || recoverySaved;
    }

    /// <summary>
    /// Coalesced atomic publication to the current .mapproj after the user has established that
    /// destination with an explicit Save. This is intentionally distinct from recovery.
    /// </summary>
    public async Task<bool> TryPrimaryAutosaveAsync(bool ignoreDebounce = false)
    {
        var now = _clock();
        if (!_primaryAutosaveEnabled
            || !_primaryAutosaveArmed
            || string.IsNullOrWhiteSpace(DocumentPath)
            || !Session.IsDirty
            || HasTransientEditorAction
            || ReferenceEquals(_lastPrimaryAutosavedDocument, Session.Document)
            || !ignoreDebounce && (_lastCommittedEditUtc is null
                || now - _lastCommittedEditUtc < PrimaryAutosaveDebounce)
            || !await _saveGate.WaitAsync(0))
        {
            return false;
        }

        try
        {
            var checkpoint = Session.CaptureSaveCheckpoint();
            var snapshot = checkpoint.Document;
            var metadata = new SaveMetadata(_appVersion, now, checkpoint.Revision);
            var path = DocumentPath!;
            SaveState = SaveState.Saving;
            Raise(nameof(PrimaryAutosaveSummary));
            try
            {
                using var imageSource = new ExportStampImageSource(snapshot, path, _assetCatalog);
                var thumbnail = await RunResourceOperationAsync(() => Task.Run(() =>
                {
                    var thumbnail = ThumbnailRenderer.RenderPng(snapshot, stamps: imageSource);
                    _store.Save(snapshot, path, metadata, thumbnail);
                    return thumbnail;
                }));

                Session.MarkSaved(checkpoint);
                _lastPrimaryAutosavedDocument = snapshot;
                _lastPrimarySaveUtc = now;
                SaveState = Session.IsDirty ? SaveState.Unsaved : SaveState.Saved;
                TouchRecentAfterSave(path, snapshot, metadata, thumbnail);
                if (!Session.IsDirty)
                {
                    ClearRecoveryAfterProvenSave();
                }
                RaiseSessionState();
                Raise(nameof(PrimaryAutosaveSummary));
                _log.Info($"PRIMARY_AUTOSAVE_SUCCEEDED revision={checkpoint.Revision}");
                return true;
            }
            catch (Exception ex)
            {
                SaveState = SaveState.SaveFailed;
                StatusDetail = "Автосохранение не удалось. Повторите Save или выберите «Сохранить как…».";
                Raise(nameof(PrimaryAutosaveSummary));
                _log.Error("PRIMARY_AUTOSAVE_FAILED", ex);
                return false;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<bool> TryRecoveryAutosaveAsync()
    {

        if (ReferenceEquals(_lastAutosavedDocument, Session.Document)
            || !await _saveGate.WaitAsync(0))
        {
            return false;
        }

        try
        {
            var checkpoint = Session.CaptureSaveCheckpoint();
            var snapshot = checkpoint.Document;
            var timestamp = _clock();
            var metadata = new SaveMetadata(_appVersion, timestamp, checkpoint.Revision);

            AutosaveState = AutosaveState.Saving;
            try
            {
                var sourcePath = DocumentPath;
                using var imageSource = new ExportStampImageSource(snapshot, sourcePath, _assetCatalog);
                await RunResourceOperationAsync(() => Task.Run(() =>
                {
                    var thumbnail = ThumbnailRenderer.RenderPng(snapshot, stamps: imageSource);
                    _store.SaveAutosave(snapshot, sourcePath, metadata, thumbnail);
                    return true;
                }));

                _lastAutosavedDocument = snapshot;
                _lastAutosaveUtc = timestamp;
                _recoveryAvailable = true;
                AutosaveState = AutosaveState.Saved;
                _log.Info($"AUTOSAVE_SUCCEEDED revision={checkpoint.Revision}");
                return true;
            }
            catch (Exception ex)
            {
                // Autosave is a safety net, never a reason to close the editor. The exception is
                // retained in the local diagnostic log and the next timer tick may retry.
                AutosaveState = AutosaveState.Failed;
                _log.Error("AUTOSAVE_FAILED", ex);
                return false;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<bool> ClearAutosaveIfCleanAsync()
    {
        if (!_recoveryAvailable && _lastAutosavedDocument is null)
        {
            return false;
        }

        if (!await _saveGate.WaitAsync(0))
        {
            return false;
        }

        try
        {
            await Task.Run(() => _store.ClearRecovery(Session.Document.Id.Value));
            ForgetAutosave();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AutosaveState = AutosaveState.Failed;
            _log.Error("AUTOSAVE_CLEANUP_FAILED", ex);
            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void ForgetAutosave()
    {
        _lastAutosavedDocument = null;
        _lastAutosaveUtc = null;
        _recoveryAvailable = false;
        AutosaveState = AutosaveState.Idle;
        Raise(nameof(AutosaveSummary));
        Raise(nameof(RecoveryAvailable));
    }

    private void ClearRecoveryAfterProvenSave()
    {
        try
        {
            _store.ClearRecovery(Session.Document.Id.Value);
            ForgetAutosave();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _recoveryAvailable = true;
            Raise(nameof(RecoveryAvailable));
            Raise(nameof(AutosaveSummary));
            _log.Error("RECOVERY_CLEANUP_AFTER_SAVE_FAILED", ex);
        }
    }

    private byte[] RenderPreview(MapDocument snapshot, int width = ThumbnailRenderer.Width,
        int height = ThumbnailRenderer.Height)
    {
        using var source = new ExportStampImageSource(snapshot, DocumentPath, _assetCatalog);
        return ThumbnailRenderer.RenderPng(snapshot, width, height, source);
    }

    private void TouchRecentAfterSave(string path, MapDocument snapshot, SaveMetadata metadata, byte[] preview)
    {
        try { _recent.Touch(path, snapshot.Id.Value, snapshot.Title, metadata.ModifiedUtc, preview); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusDetail = "Карта сохранена. Не удалось обновить список недавних карт.";
            _log.Error("RECENT_UPDATE_AFTER_SAVE_FAILED", ex);
        }
    }

    // ---------------------------------------------------------------- saving

    public async Task<bool> SaveAsync(bool saveAs = false)
    {
        var path = DocumentPath;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            path = UiTestFilePaths.MapPath
                ?? await _filePicker.PickSaveAsync(Session.Document.Title + MapProjectFormat.Extension)
                    ;
            if (string.IsNullOrEmpty(path))
            {
                return false; // user cancelled; nothing changes
            }
        }

        // Concurrent saves of one document must queue, not interleave.
        await _saveGate.WaitAsync();
        try
        {
            var checkpoint = Session.CaptureSaveCheckpoint();
            var snapshot = checkpoint.Document;
            var metadata = new SaveMetadata(_appVersion, _clock(), checkpoint.Revision);

            SaveState = SaveState.Saving;
            StatusDetail = "";

            try
            {
                using var imageSource = new ExportStampImageSource(snapshot, DocumentPath, _assetCatalog);
                var thumbnail = await RunResourceOperationAsync(() => Task.Run(() =>
                {
                    // The thumbnail comes from the same scene evaluator as the canvas, without
                    // selection or any editor chrome.
                    var thumbnail = ThumbnailRenderer.RenderPng(snapshot, stamps: imageSource);
                    _store.Save(snapshot, path!, metadata, thumbnail);
                    return thumbnail;
                }));

                DocumentPath = path;
                Session.MarkSaved(checkpoint);
                _primaryAutosaveArmed = true;
                _lastPrimaryAutosavedDocument = snapshot;
                _lastPrimarySaveUtc = metadata.ModifiedUtc;
                SaveState = Session.IsDirty ? SaveState.Unsaved : SaveState.Saved;
                if (!Session.IsDirty)
                {
                    ClearRecoveryAfterProvenSave();
                }
                Raise(nameof(DocumentPath));
                RaiseSessionState();
                Raise(nameof(PrimaryAutosaveSummary));

                TouchRecentAfterSave(path!, snapshot, metadata, thumbnail);
                _log.Info($"SAVE_SUCCEEDED revision={checkpoint.Revision}");
                return true;
            }
            catch (MapProjectSaveException ex)
            {
                SaveState = SaveState.SaveFailed;
                StatusDetail = ex.TempPath is null
                    ? "Не удалось сохранить файл. Документ остаётся открытым, данные не потеряны."
                    : $"Не удалось заменить основной файл. Документ остаётся открыт, recovery сохранён здесь: {ex.TempPath}";
                _log.Error("SAVE_FAILED", ex);
                return false;
            }
            catch (InvalidOperationException ex)
            {
                // Resource admission happens before the store's save exception boundary.
                // Keep a refused save inside the editor, including when closing with Save.
                SaveState = SaveState.SaveFailed;
                StatusDetail = "Сохранение не выполнено. Карта остаётся открытой. " + ex.Message;
                _log.Error("SAVE_REFUSED", ex);
                return false;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>True when it is safe to leave the editor. Never loses work silently.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        await _saveGate.WaitAsync();
        _saveGate.Release();
        bool allowClose;
        if (!Session.IsDirty)
        {
            allowClose = _lastAutosavedDocument is null
                || await DiscardRecoveryAsync();
        }
        else
        {
            allowClose = await _prompts.ConfirmCloseDirtyAsync(Session.Document.Title) switch
            {
                CloseChoice.Save => await SaveAsync() && !Session.IsDirty,
                CloseChoice.DontSave => await DiscardRecoveryAsync(),
                _ => false,
            };
        }

        if (allowClose)
        {
            CancelPlacement();
        }
        return allowClose;
    }

    private async Task<bool> DiscardRecoveryAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            var documentId = Session.Document.Id.Value;
            await Task.Run(() => _store.ClearRecovery(documentId));
            ForgetAutosave();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusDetail = "Не удалось удалить локальную recovery-копию. Документ остаётся открытым.";
            _log.Error("RECOVERY_DISCARD_FAILED", ex);
            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
