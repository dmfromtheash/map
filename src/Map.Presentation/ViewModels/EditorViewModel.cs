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
    River,
    Road,

    /// <summary>The hex constructor's tool: puts ready painted hex tiles on the hex board.</summary>
    Hex,

    /// <summary>«Масштаб и украшения» (R08): a drag measures a distance in map units; the panel sets the scale and adds decorations.</summary>
    Measure,
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

    private async Task<T> RunResourceOperationAsync<T>(
        Func<Task<T>> operation, CancellationToken token = default, long? reserveBytes = null)
        => await RunLocalResourceOperationAsync(async () =>
        {
            using var reservation = await _workingMemory.EnterHeavyAsync(token, reserveBytes);
            return await operation();
        }, token);

    /// <summary>
    /// Working bytes a save, an autosave or a version of this document is expected to need: its
    /// raster tiles are decompressed and re-encoded, its objects are serialized, and a preview is
    /// rendered. A map made of a few rivers and a handful of stamps costs megabytes, so it must
    /// not be admitted on the same reserve as a map full of painted tiles. The figure is capped by
    /// the shared heavy reserve, which is what every one of these operations used to demand
    /// unconditionally - a small map is now the only case that changes.
    /// </summary>
    private static long DocumentOperationBytes(MapDocument document)
    {
        var raster = 0L;
        var objects = 0L;
        foreach (var layer in document.Layers)
        {
            if (layer is IRasterContentLayer content) raster += content.ContentTiles.TotalBytes;
            if (layer.Mask is { } mask) raster += mask.Tiles.TotalBytes;
            if (layer is ObjectLayer objectLayer) objects += objectLayer.Objects.Count;
        }
        if (document.StampForeground is { } foreground) raster += foreground.Tiles.TotalBytes;
        // The preview is a few hundred kilobytes of pixels; the encoder and the archive buffers
        // work over the tiles counted above. One megabyte covers the fixed part with room to
        // spare, and a map made of one rectangle must not cost more than that.
        var preview = Map.Core.Resources.WorkingMemoryBudget.MiB;
        return preview + raster * 2 + objects * EditCost.PerObjectBytes;
    }

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
    private bool _libraryShowArchived;
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
        _hexGenerator = document.Layers.OfType<HexLayer>().FirstOrDefault()?.GenerationRecipe?.Settings ?? _hexGenerator;
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
        _libraryTileSize = double.IsFinite(preferences.LibraryTileSize) ? Math.Clamp(preferences.LibraryTileSize, 64, 176) : 112;
        _libraryShowNames = preferences.LibraryShowNames;
        _stampToolSettings = NormalizeStampToolSettings(preferences.Stamp ?? new StampToolSettings
        {
            AutoSelect = preferences.AutoSelect,
        });
        // «Область» and «Путь» are constructions for one task, not a standing preference: after one
        // «Лес по области» every later session used to open trees as an area instead of one tree.
        if (_stampToolSettings.Mode is StampPlacementMode.FillArea or StampPlacementMode.Path)
            _stampToolSettings = _stampToolSettings with { Mode = StampPlacementMode.One };
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

            if (Session.SelectedObject is { } selected) return EditorLabels.DescribeObject(selected);

            // Water is a selection of the map like any other: saying nothing is selected while a
            // river carries a selection frame on the canvas is simply wrong. So is a road.
            if (WaterSelectionCount > 0 && RoadSelectionCount > 0) return $"Выбрано: водных систем {WaterSelectionCount}, дорог {RoadSelectionCount}";
            if (RoadSelectionCount > 0) return RoadSelectionSummary;
            return WaterSelectionCount > 0 ? WaterSelectionSummary : "Ничего не выбрано";
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
            // A hex map has its own grid: the hexes'. The page grid of the standard maker is not offered there.
            if (IsHexWorkshop)
            {
                return HexGrid is { Visible: true } hexGrid
                    ? hexGrid.Numbers ? "Сетка гексов · номера" : "Сетка гексов"
                    : "Сетка гексов скрыта";
            }

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

    public bool CanUndo => Session.CanUndo || IsEditableAreaBusy
        || _areaCreating && _areaDraft is { Nodes.Length: > 0 } || _pathCreating && _pathDraft is { Nodes.Length: > 0 };

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

            // Categories never mix: a hex map has no land, river, road or brush tools, so a
            // shortcut of the standard constructor does nothing there.
            if (IsHexWorkshop && !EditorSurfaceLayout.OffersTool(hexWorkshop: true, value))
            {
                return;
            }

            // Switching tools abandons every kind of unfinished gesture, not only the drawing
            // ones. A surviving drag, marquee or node grab used to commit itself on the next
            // release under the new tool - history the user never asked for.
            CancelStroke();
            CancelHexGesture();
            CancelVectorDraw();
            CancelPenPath();
            CancelLandShapeGesture();
            CancelTextPlacement();
            CancelShapeBooleanPreview();
            var hadConstructionDraft = _areaCreating || _pathCreating;
            CancelPlacement();
            CancelEditablePathGesture(); CancelEditableAreaGesture(); CancelRiverGesture(); CancelRoadGesture();
            if (hadConstructionDraft) StatusDetail = "Незавершённый контур отменён: выбран другой инструмент.";
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
            RefreshHexCursor();
            // The river tool is where nodes are edited, and the select tool is where whole things
            // are moved and deleted. Entering either sets its natural level; the level control in
            // the panel still overrides it for as long as the user stays in that tool.
            if (value == EditorTool.River && _waterLevel == WaterSelectionLevel.System)
                _waterLevel = WaterSelectionLevel.Node;
            else if (value == EditorTool.Select && _waterLevel == WaterSelectionLevel.Node)
                _waterLevel = WaterSelectionLevel.System;
            if (value == EditorTool.Road && _roadLevel == RoadSelectionLevel.Road)
                _roadLevel = RoadSelectionLevel.Node;
            else if (value == EditorTool.Select && _roadLevel != RoadSelectionLevel.Road)
                _roadLevel = RoadSelectionLevel.Road;
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
        ActiveTool == EditorTool.Land && !LandPaintMode && ActiveLandShape != LandShapeKind.Circle;

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

    // The render thread reads the preview while the UI thread adds dabs; it gets the snapshot
    // the UI thread published after its last dab, never the live dictionaries.
    private (IReadOnlyDictionary<TileKey, byte[]> Coverage, IReadOnlyDictionary<TileKey, int> Versions)? _strokeSnapshot;

    private void PublishStrokeSnapshot()
    {
        if (_stroke is null) { _strokeSnapshot = null; return; }
        _strokeSnapshot = (new Dictionary<TileKey, byte[]>(_stroke.CommittedCoverage), new Dictionary<TileKey, int>(_stroke.CommittedVersions));
    }

    public StrokePreview? StrokePreview => _stroke is null
        || (!_editingStampForeground && _activeLayerId is null)
        ? null
        : new StrokePreview
        {
            LayerId = _activeLayerId ?? default,
            Coverage = _strokeSnapshot?.Coverage ?? _stroke.CommittedCoverage,
            Versions = _strokeSnapshot?.Versions ?? _stroke.CommittedVersions,
            Color = ActiveTool == EditorTool.Land && LandReliefMode ? ReliefPreviewColor
                : ActiveTool == EditorTool.Land && LandCliffMaskMode ? CliffMaskPreviewColor
                : ActiveTool == EditorTool.Land && !LandPaintMode && ActiveLayer is LandLayer land
                ? land.EdgeStyle.FillColor
                : _stroke.Settings.Color,
            Opacity = _stroke.Settings.Opacity,
            Eraser = _stroke.Settings.Eraser,
            PaintSettings = ActiveTool == EditorTool.Mask || (ActiveTool == EditorTool.Land && (LandCliffMaskMode || LandReliefMode)) ? null
                : ActiveTool == EditorTool.Land && !LandPaintMode && ActiveLayer is LandLayer previewLand
                    ? _stroke.Settings with { Texture = null, Color = previewLand.EdgeStyle.FillColor }
                    : _stroke.Settings,
            IsMask = ActiveTool == EditorTool.Mask,
            IsStampForeground = _editingStampForeground,
            IsLandPaint = ActiveTool == EditorTool.Land && LandPaintMode && !LandCliffMaskMode && !LandReliefMode,
            // A relief stroke previews as a tint over the land, like the cliff mask.
            IsCliffMask = ActiveTool == EditorTool.Land && (LandCliffMaskMode || LandReliefMode),
        };

    public bool IsEditingStampForeground => _editingStampForeground;

    public LayerId? MaskEditLayerId =>
        ActiveTool == EditorTool.Mask && ActiveLayer?.Mask is not null ? _activeLayerId : null;

    public IReadOnlyList<AssetRecord> Assets => _assetCatalog?.Records ?? [];

    public IReadOnlyList<AssetRecord> SearchAssets(string? query) =>
        _assetCatalog?.Search(query) ?? [];

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
                    _store.Save(AtlasSnapshot(snapshot, path), path, metadata, thumbnail);
                    return thumbnail;
                }), reserveBytes: DocumentOperationBytes(snapshot));

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
                    _store.SaveAutosave(AtlasSnapshot(snapshot, sourcePath), sourcePath, metadata, thumbnail);
                    return true;
                }), reserveBytes: DocumentOperationBytes(snapshot));

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
                var savedSnapshot = AtlasSnapshot(snapshot, path);
                using var imageSource = new ExportStampImageSource(snapshot, DocumentPath, _assetCatalog);
                var thumbnail = await RunResourceOperationAsync(() => Task.Run(() =>
                {
                    // The thumbnail comes from the same scene evaluator as the canvas, without
                    // selection or any editor chrome.
                    var thumbnail = ThumbnailRenderer.RenderPng(snapshot, stamps: imageSource);
                    _store.Save(savedSnapshot, path!, metadata, thumbnail);
                    return thumbnail;
                }), reserveBytes: DocumentOperationBytes(snapshot));

                DocumentPath = path;
                _atlasLocator = savedSnapshot.Atlas;
                if (_atlasLocator is not null) AtlasPath = AtlasStore.ManifestPath(savedSnapshot, path!);
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
                if (_atlasLocator is not null) await RefreshAtlasAsync();
                _log.Info($"SAVE_SUCCEEDED revision={checkpoint.Revision}");
                return true;
            }
            catch (MapProjectSaveException ex)
            {
                SaveState = SaveState.SaveFailed;
                StatusDetail = ex.TempPath is null
                    ? "Не удалось сохранить файл. Документ остаётся открытым, данные не потеряны."
                    : $"Не удалось заменить основной файл. Документ остаётся открыт, копия для восстановления сохранена здесь: {ex.TempPath}";
                _log.Error("SAVE_FAILED", ex);
                return false;
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
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
            CancelGeneration();
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
            StatusDetail = "Не удалось удалить локальную копию для восстановления. Документ остаётся открытым.";
            _log.Error("RECOVERY_DISCARD_FAILED", ex);
            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
