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
}

/// <summary>
/// File paths supplied by the project's own UI driver instead of an OS dialog.
///
/// The native Windows file picker neither shows a window nor returns in this project's automation
/// host, so a driven smoke can otherwise never reach import, save or reopen in a real window. This
/// mirrors the sandbox switch the recovery driver already uses: it applies ONLY when the UI test
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

/// <summary>What one stamp gesture produces: a single stamp, or a whole Area/Density scatter.</summary>
public enum StampPlacementMode
{
    One,
    Area,
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
    private readonly AssetCatalog? _assetCatalog;
    private readonly IReadOnlyList<string> _localFontFamilies;

    private SaveState _saveState;
    private AutosaveState _autosaveState;
    private DateTimeOffset? _lastAutosaveUtc;
    private MapDocument? _lastAutosavedDocument;
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
    private List<VectorPoint>? _vectorPoints;

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
    private StampPlacementMode _stampPlacementMode = StampPlacementMode.One;
    private double _stampAreaRadius = 160;
    private double _stampDensity = 40;
    private bool _randomVariantAfterPlacement;
    // A scatter multiplies the chosen variant; it does not turn or mirror it. Both randomisations
    // remain available as explicit toggles, because a forest wants them and a row of houses does
    // not - but neither is something the user has to notice and switch off first.
    private bool _stampRandomAngle;
    private bool _stampRandomFlip;
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
        IFolderLauncher? folderLauncher = null)
    {
        Session = new EditingSession(document);
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
        _imageExporter = imageExporter ?? new ImageExportEngine();
        _exportDestinationPicker = exportDestinationPicker;
        _folderLauncher = folderLauncher;
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
        _activeLayerId = document.Layers.LastOrDefault(layer => layer is not BaseColorLayer)?.Id
            ?? document.BaseColor.Id;

        if (assetCatalog is not null)
        {
            RefreshLibrary();
        }

        SceneChanged += (_, _) => RefreshOpenExportAfterSceneChange();
    }

    public EditingSession Session { get; }

    public EditorShortcutSettings Shortcuts { get; }

    public IStampImageSource? StampImages { get; }

    public string? DocumentPath { get; private set; }

    public event EventHandler? SceneChanged;

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
        AutosaveState.Saving => "Автокопия создаётся…",
        AutosaveState.Failed => "Автокопия: ошибка",
        AutosaveState.Saved when _lastAutosaveUtc is { } time
            && ReferenceEquals(_lastAutosavedDocument, Session.Document) =>
            $"Автокопия: {time.ToLocalTime():t}",
        AutosaveState.Saved when _lastAutosaveUtc is { } time =>
            $"Автокопия до {time.ToLocalTime():t}",
        _ when Session.IsDirty => "Автокопия ожидает",
        _ => "Автокопия не требуется",
    };

    /// <summary>Short human explanation of the last failure, with the recovery path when one exists.</summary>
    public string StatusDetail
    {
        get => _statusDetail;
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

            CancelStroke();
            CancelVectorDraw();
            _previousTool = _activeTool;
            _activeTool = value;
            Raise();
            Raise(nameof(MaskEditLayerId));
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

    public MaskPaintMode MaskMode { get; set; } = MaskPaintMode.Hide;

    public bool LandSubtract { get; set; }

    public StrokePreview? StrokePreview => _stroke is null || _activeLayerId is null
        ? null
        : new StrokePreview
        {
            LayerId = _activeLayerId.Value,
            Coverage = _stroke.Coverage,
            Color = ActiveTool == EditorTool.Land && ActiveLayer is LandLayer land
                ? land.EdgeStyle.FillColor
                : _stroke.Settings.Color,
            Opacity = _stroke.Settings.Opacity,
            Eraser = _stroke.Settings.Eraser,
            IsMask = ActiveTool == EditorTool.Mask,
        };

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

        var result = _assetCatalog.Query(new AssetQuery
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
        });

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

        foreach (var variant in _selectedSetVariants)
        {
            Consider(variant.Sha256);
        }

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

    /// <summary>Makes one logical card current and restores the variant last used inside it.</summary>
    public void SelectAssetSet(string? setId)
    {
        if (setId is null || _assetCatalog is null || _assetCatalog.FindSet(setId) is not { } set)
        {
            _selectedSetId = null;
            _selectedSetVariants = [];
            _currentVariantIndex = 0;
            SyncSelectedLibraryRow();
            RaiseVariantState();
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
        _currentVariantIndex = index >= 0 ? index : 0;
        SyncSelectedLibraryRow();
        RaiseVariantState();
    }

    public void NextVariant() => MoveVariant(1);

    public void PreviousVariant() => MoveVariant(-1);

    public void SelectVariantAt(int index)
    {
        if (_selectedSetVariants.Count == 0)
        {
            return;
        }

        _currentVariantIndex = Math.Clamp(index, 0, _selectedSetVariants.Count - 1);
        RememberCurrentVariant();
        RaiseVariantState();
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
        StatusDetail = $"Вариант {CurrentVariantLabel}.";
        RaiseVariantState();
    }

    private void RememberCurrentVariant()
    {
        if (_assetCatalog is null || _selectedSetId is null || CurrentVariant is not { } variant
            || _assetCatalog.Status == AssetLibraryStatus.Damaged)
        {
            return;
        }

        _assetCatalog.TouchRecent(_selectedSetId, variant.Id, _clock());
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
            StatusDetail = "Этот штамп не найден в локальной библиотеке; объект работает как раньше.";
            return;
        }

        SelectAssetSet(set.Id);
        var index = _selectedSetVariants.ToList().FindIndex(variant =>
            string.Equals(variant.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _currentVariantIndex = index;
            RaiseVariantState();
        }
    }

    // ---------------------------------------------------------------- area and density

    public StampPlacementMode StampPlacementMode
    {
        get => _stampPlacementMode;
        set
        {
            if (SetField(ref _stampPlacementMode, value))
            {
                Raise(nameof(PlannedStampCount));
            }
        }
    }

    /// <summary>Gesture radius in world units.</summary>
    public double StampAreaRadius
    {
        get => _stampAreaRadius;
        set
        {
            if (SetField(ref _stampAreaRadius, Math.Clamp(value, MinStampAreaRadius, MaxStampAreaRadius)))
            {
                Raise(nameof(PlannedStampCount));
                Raise(nameof(StampAreaInCells));
                Raise(nameof(StampAreaSummary));
            }
        }
    }

    /// <summary>Stamps per 10 000 square world units.</summary>
    public double StampDensity
    {
        get => _stampDensity;
        set
        {
            if (SetField(ref _stampDensity, Math.Clamp(value, MinStampDensity, MaxStampDensity)))
            {
                Raise(nameof(PlannedStampCount));
                Raise(nameof(StampAreaSummary));
            }
        }
    }

    public bool RandomVariantAfterPlacement
    {
        get => _randomVariantAfterPlacement;
        set => SetField(ref _randomVariantAfterPlacement, value);
    }

    public bool StampRandomAngle
    {
        get => _stampRandomAngle;
        set => SetField(ref _stampRandomAngle, value);
    }

    public bool StampRandomFlip
    {
        get => _stampRandomFlip;
        set => SetField(ref _stampRandomFlip, value);
    }

    /// <summary>How many stamps the current Area and Density would place.</summary>
    public int PlannedStampCount => _stampPlacementMode == StampPlacementMode.One
        ? 1
        : StampPlacement.CountFor(_stampDensity, _stampAreaRadius);

    /// <summary>The same radius in cells of the active snap grid, or null without one.</summary>
    public double? StampAreaInCells =>
        ActiveSnapGrid() is { } grid ? StampPlacement.RadiusInCells(_stampAreaRadius, grid.CellSize) : null;

    public string StampAreaSummary
    {
        get
        {
            var area = StampAreaInCells is { } cells
                ? $"Область {_stampAreaRadius:0} ед. ({cells:0.##} кл.)"
                : $"Область {_stampAreaRadius:0} ед.";
            return _stampPlacementMode == StampPlacementMode.One
                ? area + " · один штамп"
                : $"{area} · плотность {_stampDensity:0} на 10 000 ед² · {PlannedStampCount} шт.";
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

    /// <summary>
    /// One pointer gesture, all or nothing. The plan is resolved first, then every distinct hash
    /// it needs is verified and read exactly once, and only a fully resolved plan is committed as
    /// a single undoable command. If one variant is missing or changed, nothing is placed at all:
    /// a shortened plan committed as a success is the one outcome the user cannot detect.
    /// </summary>
    public bool PlaceCurrentVariantAt(double worldX, double worldY)
    {
        if (_assetCatalog is null || _assetResolver is null)
        {
            StatusDetail = "Локальный каталог ассетов недоступен.";
            return false;
        }

        if (_assetCatalog.Status == AssetLibraryStatus.Damaged)
        {
            StatusDetail = "Индекс библиотеки повреждён. " + _assetCatalog.Diagnostic;
            return false;
        }

        if (SelectedSet is null || CurrentVariant is null)
        {
            StatusDetail = "Сначала выберите набор и вариант в библиотеке.";
            return false;
        }

        var layer = ActiveObjectLayer();
        if (layer is null || layer.Locked || !layer.Visible)
        {
            StatusDetail = "Выберите видимый и разблокированный слой объектов.";
            return false;
        }

        var randomize = _randomVariantAfterPlacement && _stampPlacementMode == StampPlacementMode.Area;
        var candidates = randomize
            ? _selectedSetVariants
            : [_selectedSetVariants[_currentVariantIndex]];

        var sources = new List<StampVariantSource>(candidates.Count);
        var records = new Dictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var variant in candidates)
        {
            if (_assetCatalog.Find(variant.Sha256) is not { } record)
            {
                continue;
            }

            if (records.TryAdd(record.Sha256, record))
            {
                sources.Add(new StampVariantSource(record.Sha256, record.Width, record.Height));
            }
        }

        if (sources.Count == 0)
        {
            StatusDetail = "Содержимое выбранного набора отсутствует в каталоге.";
            return false;
        }

        // The seed is derived from the counter this gesture WOULD take, and the counter only moves
        // once the command is committed. A refused gesture therefore leaves no trace at all, and
        // retrying it after the file is restored replays the very same plan.
        var request = new StampPlacementRequest
        {
            CenterX = worldX,
            CenterY = worldY,
            AreaRadius = _stampAreaRadius,
            Density = _stampDensity,
            Seed = NextGestureSeed(),
            RandomizeVariant = randomize && sources.Count > 1,
            RandomAngle = _stampRandomAngle && _stampPlacementMode == StampPlacementMode.Area,
            RandomFlip = _stampRandomFlip && _stampPlacementMode == StampPlacementMode.Area,
            BaseWidth = DefaultStampWidth(),
        };
        var plan = _stampPlacementMode == StampPlacementMode.One
            ? StampPlacement.PlanOne(request, sources[0])
            : StampPlacement.PlanArea(request, sources);

        if (ResolvePlan(plan, records, out var references, out var refusal) is not { } byHash)
        {
            StatusDetail = refusal;
            return false;
        }

        var objects = new List<SceneObject>(plan.Count);
        foreach (var item in plan)
        {
            var record = records[item.Sha256];
            objects.Add(new SceneObject(
                ObjectId.New(), SceneObjectKind.Stamp, record.DisplayName,
                item.X, item.Y, item.Width, item.Height, item.RotationDegrees,
                "#FFFFFFFF", "#00000000", 0, true, false)
            {
                Stamp = new StampStyle(item.Sha256)
                {
                    // The placed size IS the honest 100 % baseline for later scaling.
                    ReferenceWidth = item.Width,
                    ReferenceHeight = item.Height,
                    FlipHorizontal = item.FlipHorizontal,
                    FlipVertical = item.FlipVertical,
                },
            });
        }

        // Captured before the random toggle is allowed to move on, so the confirmation names the
        // variant that was actually placed rather than the one that will be placed next.
        var placedLabel = CurrentVariantLabel;
        _gestureCounter++;
        Session.Execute(new PlaceAssetObjectsCommand(layer.Id, references, objects));
        Session.SelectedObjectId = objects[^1].Id;
        _activeLayerId = layer.Id;
        AfterEdit();
        if (_selectedSetId is not null && CurrentVariant is { } placedVariant)
        {
            _assetCatalog.TouchRecent(_selectedSetId, placedVariant.Id, _clock());
        }

        // Only a command that actually succeeded is allowed to move the current variant on.
        if (_randomVariantAfterPlacement && _selectedSetVariants.Count > 1)
        {
            _currentVariantIndex = StampPlacement.NextVariantIndex(
                request.Seed, _currentVariantIndex, _selectedSetVariants.Count);
            RememberCurrentVariant();
            RaiseVariantState();
        }

        // Phrased as "noun: number" so the Russian numeral never has to agree with the noun -
        // "Размещено 322 штампов" is simply wrong, and 1/2/5 would each need a different form.
        StatusDetail = objects.Count == 1
            ? $"Размещён вариант {placedLabel}."
            : $"Размещено штампов: {objects.Count}, вариантов: {byHash.Count}.";
        RefreshLibrary();
        return true;
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
        var neededHashes = plan.Select(item => item.Sha256)
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

        foreach (var item in plan)
        {
            if (byHash.ContainsKey(item.Sha256))
            {
                continue;
            }

            if (existing.TryGetValue(item.Sha256, out var existingReference))
            {
                byHash[item.Sha256] = existingReference;
                references.Add(existingReference);
                continue;
            }

            if (!records.TryGetValue(item.Sha256, out var record))
            {
                refusal = "Содержимое выбранного набора отсутствует в каталоге. Ничего не размещено.";
                return null;
            }

            var resolution = _assetResolver!.Resolve(record);
            if (!resolution.IsAvailable)
            {
                refusal = Unplaceable(record, resolution.Availability);
                return null;
            }

            byHash[item.Sha256] = resolution.Reference!;
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

        ReplaceSelectedStampAsset(record);
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

    private double DefaultStampWidth() => _stampPlacementMode == StampPlacementMode.One
        ? Math.Min(256.0, Session.Document.Canvas.Width / 4.0)
        : Math.Clamp(_stampAreaRadius / 3.0, 8, 256);

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

        var layer = ActiveLayer as ObjectLayer
            ?? Session.Document.Layers.OfType<ObjectLayer>().LastOrDefault(item => item.Visible && !item.Locked);
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

        if (layer is null || layer.Locked || !layer.Visible)
        {
            StatusDetail = "Выберите доступный ассет и разблокированный слой объектов.";
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

        Session.Execute(new PlaceAssetObjectsCommand(layer.Id, reference, [stamp]));
        Session.SelectedObjectId = stamp.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void ScatterStamp(AssetRecord record, int count, int seed = 12345)
    {
        if (_assetResolver is null || count is < 1 or > 5_000)
        {
            return;
        }

        var layer = ActiveLayer as ObjectLayer
            ?? Session.Document.Layers.OfType<ObjectLayer>().LastOrDefault(item => item.Visible && !item.Locked);
        var reference = ResolveAssetForDocument(
            record, out _, out var assetRefusal);
        if (layer is null || reference is null)
        {
            StatusDetail = assetRefusal ?? "Ассет недоступен для размещения.";
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

        Session.Execute(new PlaceAssetObjectsCommand(layer.Id, reference, objects));
        Session.SelectedObjectId = objects[^1].Id;
        AfterEdit();
    }

    public void ReplaceSelectedStampAsset(AssetRecord record)
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

        Session.Execute(new ReplaceStampAssetCommand(selected, reference));
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
        if (ActiveTool is not (EditorTool.Line or EditorTool.Shape)
            || ActiveObjectLayer() is not { Visible: true, Locked: false })
        {
            return false;
        }

        _vectorPoints = [new VectorPoint(worldX, worldY)];
        return true;
    }

    public void ContinueVectorDraw(double worldX, double worldY)
    {
        if (_vectorPoints is not { Count: > 0 } points)
        {
            return;
        }

        var last = points[^1];
        if (Math.Pow(worldX - last.X, 2) + Math.Pow(worldY - last.Y, 2) >= 4)
        {
            points.Add(new VectorPoint(worldX, worldY));
        }
    }

    public void EndVectorDraw()
    {
        if (_vectorPoints is not { } points)
        {
            return;
        }

        _vectorPoints = null;
        var layer = ActiveObjectLayer();
        var required = ActiveTool == EditorTool.Shape ? 3 : 2;
        if (layer is null || points.Count < required)
        {
            return;
        }

        var added = ActiveTool == EditorTool.Shape
            ? VectorGeometry.CreateFreeShape(ObjectId.New(), "Свободная фигура", points)
            : VectorGeometry.CreateLine(ObjectId.New(), "Свободная линия", points);
        Session.Execute(new AddObjectCommand(layer.Id, added));
        Session.SelectedObjectId = added.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void CancelVectorDraw() => _vectorPoints = null;

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
        var layer = ActiveLayer as ObjectLayer
            ?? Session.Document.Layers.OfType<ObjectLayer>().LastOrDefault();
        if (layer is null || layer.Locked || !layer.Visible)
        {
            StatusDetail = "Выберите видимый разблокированный слой объектов.";
            return;
        }

        var canvas = Session.Document.Canvas;

        // Stagger new objects so consecutive adds stay individually visible.
        var offset = (Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count) % 5) * 40;
        var width = Math.Min(240.0, canvas.Width / 4.0);
        var height = Math.Min(160.0, canvas.Height / 4.0);

        var added = new SceneObject(
            ObjectId.New(), kind,
            kind == SceneObjectKind.Ellipse ? "Эллипс" : "Прямоугольник",
            ((canvas.Width - width) / 2) + offset,
            ((canvas.Height - height) / 2) + offset,
            width, height, 0,
            kind == SceneObjectKind.Ellipse ? "#FF5B8266" : "#FF3E5C76",
            "#FF10131A", 2,
            visible: true, locked: false);

        Session.Execute(new AddObjectCommand(layer.Id, added));
        Session.SelectedObjectId = added.Id;
        AfterEdit();
    }

    public void AddLine()
    {
        var layer = ActiveObjectLayer();
        if (layer is null) return;
        var centre = ViewCentre();
        var line = VectorGeometry.CreateLine(ObjectId.New(), "Линия",
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
        var shape = VectorGeometry.CreateShape(ObjectId.New(), $"Фигура {kind}", kind,
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
        var text = new SceneObject(
            ObjectId.New(), SceneObjectKind.Text, "Текст",
            centre.X - 180, centre.Y - 70, 360, 140, 0,
            "#FF20242B", "#FFFFFFFF", 0, true, false)
        {
            Text = new TextStyle { Content = "Новый текст\nна карте" },
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

    public void AddNodeToSelectedLine()
    {
        if (EditableSelection() is not { Line: not null } before) return;
        ReplaceSelected(before, VectorObjectEditor.AddLineNode(before, 1, 0.5), "Add line node");
    }

    public void MoveSelectedVectorNode()
    {
        if (EditableSelection() is not { } before) return;
        var node = before.Line?.Nodes.FirstOrDefault() ?? before.Shape?.Regions[0].Nodes.FirstOrDefault();
        if (node is null) return;
        ReplaceSelected(before,
            VectorObjectEditor.MoveNode(before, node.Id, Math.Clamp(node.X + 0.08, -1, 2), Math.Clamp(node.Y + 0.05, -1, 2)),
            "Move vector node");
    }

    public void ToggleSelectedNodeCurve()
    {
        if (EditableSelection() is not { Line: { } line } before) return;
        var node = line.Nodes[Math.Min(1, line.Nodes.Count - 1)];
        ReplaceSelected(before, VectorObjectEditor.SetNodeCurve(before, node.Id, node.Kind != VectorNodeKind.Curve),
            "Toggle line curve");
    }

    public void AddPortalToSelected()
    {
        if (EditableSelection() is not { } before || before.Line is null && before.Shape is null) return;
        ReplaceSelected(before, VectorObjectEditor.AddPortal(before, 0.5, 48), "Add portal");
    }

    public void MoveSelectedPortal()
    {
        if (EditableSelection() is not { } before) return;
        var portal = before.Line?.Portals.FirstOrDefault() ?? before.Shape?.Portals.FirstOrDefault();
        if (portal is null) return;
        ReplaceSelected(before, VectorObjectEditor.MovePortal(before, portal.Id, Math.Min(0.9, portal.Position + 0.1)),
            "Move portal");
    }

    public void DeleteSelectedPortal()
    {
        if (EditableSelection() is not { } before) return;
        var portal = before.Line?.Portals.FirstOrDefault() ?? before.Shape?.Portals.FirstOrDefault();
        if (portal is null) return;
        ReplaceSelected(before, VectorObjectEditor.DeletePortal(before, portal.Id), "Delete portal");
    }

    public void CutSelectedLine()
    {
        if (EditableSelection() is not { Line: { Nodes.Count: >= 3 } line } before
            || DocumentEditor.FindLayerOf(Session.Document, before.Id) is not { } layer)
        {
            return;
        }

        var points = line.Nodes.Select(node => new VectorPoint(
            before.X + node.X * before.Width, before.Y + node.Y * before.Height)).ToList();
        var cut = points.Count / 2;
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
        var layer = ActiveObjectLayer();
        var shapes = layer?.Objects.Where(item => item.Shape is not null).TakeLast(2).ToList();
        if (layer is null || shapes?.Count != 2)
        {
            StatusDetail = "Для boolean нужны две последние фигуры на активном Object layer.";
            return;
        }

        var command = new ShapeBooleanCommand(Session.Document, layer.Id, shapes[0].Id, shapes[1].Id, operation);
        Session.Execute(command);
        Session.SelectedObjectId = command.ResultId;
        AfterEdit();
    }

    private ObjectLayer? ActiveObjectLayer() => ActiveLayer as ObjectLayer
        ?? Session.Document.Layers.OfType<ObjectLayer>().LastOrDefault(layer => layer.Visible && !layer.Locked);

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
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddObjectLayer()
    {
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1.0, []);
        Session.Execute(new AddLayerCommand(layer, _activeLayerId));
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void AddBrushLayer()
    {
        var layer = BrushLayer.CreateEmpty(LayerId.New(), "Кисть");
        Session.Execute(new AddLayerCommand(layer, _activeLayerId));
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void AddLandLayer()
    {
        var layer = LandLayer.CreateEmpty(LayerId.New(), "Земля");
        Session.Execute(new AddLayerCommand(layer, _activeLayerId));
        _activeLayerId = layer.Id;
        ActiveTool = EditorTool.Land;
        AfterEdit();
    }

    public void AddGridLayer()
    {
        var grid = new GridLayer(LayerId.New(), "Сетка", true, false, 1,
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
        var layer = new FilterLayer(LayerId.New(), "Фильтр", true, false, 1,
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

    public void CopySelectedComposition()
    {
        if (Session.SelectedObject is not { } selected) return;
        var package = CompositionTransfer.Copy(Session.Document, [selected.Id],
            new CompositionCaptureOptions { ReferenceGridScale = ActiveSnapGrid()?.CellSize ?? 1 },
            selected.Name + " copy");
        CompositionClipboard.Set(package); StatusDetail = "Композиция скопирована для другого окна карты.";
    }

    public void PasteComposition()
    {
        var layer = ActiveObjectLayer(); var package = CompositionClipboard.Get();
        if (layer is null || package is null) { StatusDetail = "Сначала скопируйте композицию."; return; }
        var centre = ViewCentre();
        var instance = CompositionTransfer.CreateInstance(package, centre.X, centre.Y, ActiveSnapGrid()?.CellSize);
        Session.Execute(new PasteCompositionCommand(layer.Id, instance, package.Assets));
        Session.SelectedObjectId = instance.Id; _activeLayerId = layer.Id; AfterEdit();
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
        if (EditableSelection() is not { } before || ActiveSnapGrid() is not { } grid) return;
        var node = before.Line?.Nodes.FirstOrDefault()
            ?? before.Shape?.Regions.SelectMany(region => region.Nodes).FirstOrDefault();
        if (node is null) return;
        ReplaceSelected(before, GridSnapService.SnapNode(before, node.Id, grid), "Snap node to grid");
    }

    public void SnapSelectedPortalToGrid()
    {
        if (EditableSelection() is not { Line: { } line } before || ActiveSnapGrid() is not { } grid) return;
        var portal = line.Portals.FirstOrDefault();
        if (portal is null) return;
        var after = GridSnapService.SnapPortal(before, portal.Id, grid, out var outcome);
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
        var note = new MapNote(NoteId.New(), $"Заметка {Notes.Count + 1}", "Локальная заметка",
            NoteAnchorKind.World, centre.X, centre.Y);
        Session.Execute(new AddNoteCommand(note));
        AfterEdit();
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

    public void ToggleActiveLayerVisibility()
    {
        if (ActiveLayer is not { } layer)
        {
            return;
        }

        Session.Execute(UpdateLayerCommand.SetVisible(layer, !layer.Visible));
        AfterEdit();
    }

    public void ToggleActiveLayerLock()
    {
        if (ActiveLayer is not { } layer)
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

    public void ReorderActiveLayer(bool up)
    {
        if (ActiveLayer is null)
        {
            return;
        }

        Session.Execute(new ReorderLayerCommand(ActiveLayer.Id, up));
        AfterEdit();
    }

    public void DuplicateActiveLayer()
    {
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

    public bool BeginStroke(double worldX, double worldY)
    {
        if (ActiveLayer is not { Visible: true, Locked: false } layer)
        {
            StatusDetail = "Активный слой скрыт или заблокирован.";
            return false;
        }

        TileMap target;
        if (ActiveTool == EditorTool.Brush && layer is BrushLayer brush)
        {
            target = brush.Tiles;
        }
        else if (ActiveTool == EditorTool.Mask && layer.Mask is { } mask && layer is not BaseColorLayer)
        {
            target = mask.Tiles;
        }
        else if (ActiveTool == EditorTool.Land && layer is LandLayer land)
        {
            target = land.GeometryTiles;
        }
        else
        {
            StatusDetail = ActiveTool switch
            {
                EditorTool.Mask => "Сначала добавьте маску к выбранному слою.",
                EditorTool.Land => "Для земли выберите Land layer.",
                _ => "Для рисования выберите слой кисти.",
            };
            return false;
        }

        // The stroke's randomness is decided here, BEFORE any command exists: the committed
        // tiles never depend on replay, so undo/redo restore bytes instead of re-rolling.
        BrushSettings = BrushSettings with
        {
            Seed = GestureSeed.From(Session.Revision, "brush-stroke", 0, ++_brushSeedCounter),
        };

        _stroke = new RasterStrokeSession(target, BrushSettings);
        _stroke.AddPoint(worldX, worldY);
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        return true;
    }

    public void ContinueStroke(double worldX, double worldY)
    {
        if (_stroke is null)
        {
            return;
        }

        _stroke.AddPoint(worldX, worldY);
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void EndStroke()
    {
        if (_stroke is null || _activeLayerId is null)
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

        if (ActiveTool == EditorTool.Mask)
        {
            var (Before, After) = stroke.CommitCoverage(
                missingTileValue: 255,
                subtract: MaskMode == MaskPaintMode.Hide);
            Session.Execute(new MaskStrokeCommand(_activeLayerId.Value, Before, After));
        }
        else if (ActiveTool == EditorTool.Land)
        {
            var (Before, After) = stroke.CommitCoverage(missingTileValue: 0, subtract: LandSubtract);
            Session.Execute(new BrushStrokeCommand(
                _activeLayerId.Value, Before, After,
                LandSubtract ? "Subtract land" : "Add land"));
        }
        else
        {
            var (Before, After) = stroke.Commit();
            Session.Execute(new BrushStrokeCommand(
                _activeLayerId.Value, Before, After,
                BrushSettings.Eraser ? "Erase stroke" : "Brush stroke"));
        }

        AfterEdit();
    }

    public void ApplyLandShape(LandShapeKind kind)
    {
        if (ActiveLayer is not LandLayer land || land.Locked || !land.Visible)
        {
            StatusDetail = "Выберите видимый разблокированный Land layer.";
            return;
        }

        var centre = Session.Viewport.ScreenToWorld(
            Session.Viewport.ViewWidth / 2, Session.Viewport.ViewHeight / 2);
        const double size = 512;
        var bounds = new WorldBounds(
            centre.X - (size / 2), centre.Y - (size / 2),
            centre.X + (size / 2), centre.Y + (size / 2));
        IReadOnlyList<LandPoint> polygon = kind == LandShapeKind.Polygon
            ?
            [
                new LandPoint(bounds.Left, bounds.Bottom),
                new LandPoint(centre.X, bounds.Top),
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
            return;
        }

        Session.Execute(new BrushStrokeCommand(
            land.Id, Before, After,
            LandSubtract ? $"Subtract land {kind}" : $"Add land {kind}"));
        AfterEdit();
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
            .SelectMany(layer => layer.Objects)
            .Where(sceneObject => sceneObject.Visible && !sceneObject.Locked)
            .Select(sceneObject => sceneObject.Id),
    ];

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
    /// new group. Refusals (too few objects, empty name, partial regrouping) change nothing and
    /// explain themselves in the status line.
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
            command = SetGroupCommand.Group(
                Session.Document, [.. selected.Select(o => o.Id)], name ?? "");
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
    public bool HandleEscape()
    {
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
        ExecuteWholeSelection(before,
            MultiSelectionGeometry.Translate(before, deltaX, deltaY), "Nudge selection");
    }

    public void ResizeSelectedObject(int direction, bool largerStep = false)
    {
        if (EditableSelectionList() is not { } before || direction == 0) return;
        var factor = MultiSelectionGeometry.ClampScaleFactor(
            before, 1 + Math.Sign(direction) * (largerStep ? 0.1 : 0.01));
        var frame = MultiSelectionGeometry.CombinedBounds(before)!.Value;
        ExecuteWholeSelection(before, MultiSelectionGeometry.ScaleAroundCentre(
            before, factor, (frame.Left + frame.Right) / 2, (frame.Top + frame.Bottom) / 2),
            "Resize selection from shortcut");
    }

    public void RotateSelectedObject(int direction, bool largerStep = false, bool fineStep = false)
    {
        if (EditableSelectionList() is not { } before || direction == 0) return;
        var step = fineStep ? 0.25 : largerStep ? 15 : 1;
        var frame = MultiSelectionGeometry.CombinedBounds(before)!.Value;
        ExecuteWholeSelection(before, MultiSelectionGeometry.RotateAroundCentre(
            before, Math.Sign(direction) * step,
            (frame.Left + frame.Right) / 2, (frame.Top + frame.Bottom) / 2),
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
        IReadOnlyList<SceneObject> before, IReadOnlyList<SceneObject> after, string name)
    {
        if (before.SequenceEqual(after))
        {
            return;
        }

        Session.Execute(new ReplaceObjectsCommand(before, after, name));
        AfterEdit();
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

        var availableIds = AvailableObjectIds();
        if (selected.Any(sceneObject => !availableIds.Contains(sceneObject.Id)))
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
    {
        var before = Session.SelectedObjects;
        if (before.Count == 0)
        {
            return;
        }

        var target = !before.All(o => o.Visible);
        ExecuteWholeSelection(before,
            [.. before.Select(o => CopyObject(o, visible: target))], "Selection visibility");
    }

    /// <summary>Lock over the whole selection: any unlocked member means "lock all" first.</summary>
    public void ToggleSelectedObjectLock()
    {
        var before = Session.SelectedObjects;
        if (before.Count == 0)
        {
            return;
        }

        var target = !before.All(o => o.Locked);
        ExecuteWholeSelection(before,
            [.. before.Select(o => CopyObject(o, locked: target))], "Selection lock");
    }

    /// <summary>Deletes the whole selection, across layers, as one history entry.</summary>
    public void DeleteSelectedObject()
    {
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
        if (additive)
        {
            return CanvasGesture.None;
        }

        return BeginDrag(worldX, worldY) ? CanvasGesture.Drag : CanvasGesture.None;
    }

    public bool BeginDrag(double startWorldX = 0, double startWorldY = 0)
    {
        var selected = Session.SelectedObjects;
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

        if (!preview.Proposed.SequenceEqual(preview.Before))
        {
            Session.Execute(new ReplaceObjectsCommand(
                preview.Before, preview.Proposed, "Transform selection"));
            AfterEdit();
        }
        else
        {
            InvalidateScene(EditorChangeReason.CanvasInvalidation);
        }
    }

    public void CancelDrag()
    {
        DragPreview = null;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void Undo()
    {
        if (Session.Undo())
        {
            AfterEdit();
        }
    }

    public void Redo()
    {
        if (Session.Redo())
        {
            AfterEdit();
        }
    }

    public bool CreateCheckpoint(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusDetail = "Введите непустое название контрольной точки.";
            return false;
        }

        if (name.Trim().Length > MapCheckpointLimits.MaxNameLength)
        {
            StatusDetail = $"Название должно быть не длиннее {MapCheckpointLimits.MaxNameLength} символов.";
            return false;
        }

        if (Session.Document.Checkpoints.Count >= MapCheckpointLimits.MaxCount)
        {
            StatusDetail = $"Достигнут лимит: не больше {MapCheckpointLimits.MaxCount} контрольных точек.";
            return false;
        }

        try
        {
            var source = Session.Document;
            var preview = ThumbnailRenderer.RenderPng(
                source, MapCheckpointLimits.PreviewWidth, MapCheckpointLimits.PreviewHeight);
            var checkpoint = new MapCheckpoint(
                CheckpointId.New(),
                name,
                _clock(),
                MapCheckpointSnapshot.Capture(source),
                preview);

            MapProjectStore.ValidateCheckpointAddition(source, checkpoint);
            Session.Execute(new AddCheckpointCommand(checkpoint));
            AfterEdit();
            StatusDetail = $"Создана контрольная точка «{checkpoint.Name}». Сохраните проект, чтобы записать её в файл.";
            return true;
        }
        catch (MapProjectResourceLimitException ex)
        {
            StatusDetail = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            StatusDetail = "Не удалось создать контрольную точку: " + ex.Message;
            _log.Error("Checkpoint creation failed", ex);
            return false;
        }
    }

    public async Task<bool> RestoreCheckpointAsync(CheckpointId id)
    {
        var checkpoint = Session.Document.Checkpoints.FirstOrDefault(item => item.Id == id);
        if (checkpoint is null)
        {
            StatusDetail = "Контрольная точка не найдена. Текущая карта не изменена.";
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
            StatusDetail = $"Восстановлена контрольная точка «{checkpoint.Name}». Действие можно отменить одной командой Undo.";
            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            StatusDetail = "Не удалось восстановить контрольную точку. Текущая карта не изменена.";
            _log.Error("Checkpoint restore failed", ex);
            return false;
        }
    }

    public async Task<bool> DeleteCheckpointAsync(CheckpointId id)
    {
        var checkpoint = Session.Document.Checkpoints.FirstOrDefault(item => item.Id == id);
        if (checkpoint is null)
        {
            StatusDetail = "Контрольная точка не найдена. Ничего не удалено.";
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
            StatusDetail = $"Контрольная точка «{checkpoint.Name}» удалена. Сохраните проект, чтобы записать изменение в файл.";
            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            StatusDetail = "Не удалось удалить контрольную точку. Ничего не изменено.";
            _log.Error("Checkpoint deletion failed", ex);
            return false;
        }
    }

    /// <summary>Republishes the derived session state the status bar and the toolbar read.</summary>
    private void RaiseSessionState()
    {
        Raise(nameof(PendingChanges));
        Raise(nameof(SaveSummary));
        Raise(nameof(AutosaveSummary));
        Raise(nameof(CanUndo));
        Raise(nameof(CanRedo));
    }

    private void AfterEdit()
    {
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
        XText = selected?.X.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        YText = selected?.Y.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        WidthText = selected?.Width.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        HeightText = selected?.Height.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        RotationText = selected?.RotationDegrees.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
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

        if (!TryParse(XText, out var x) || !TryParse(YText, out var y)
            || !TryParse(WidthText, out var width) || !TryParse(HeightText, out var height)
            || !TryParse(RotationText, out var rotation)
            || width <= 0 || height <= 0)
        {
            InspectorError = "Числовые поля должны содержать корректные значения; размеры больше нуля.";
            return false;
        }

        // A locked stamp aspect fills in the untouched dimension from the touched one, so the
        // numeric fields and the proportional scale can never disagree about the shape.
        if (before.Stamp is not null && StampAspectLocked && before.Width > 0 && before.Height > 0)
        {
            if (width != before.Width && height == before.Height)
            {
                height = width * before.Height / before.Width;
            }
            else if (height != before.Height && width == before.Width)
            {
                width = height * before.Width / before.Height;
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

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    private static double NormalizeAngle(double angle)
    {
        var normalized = angle % 360;
        if (normalized <= -180) normalized += 360;
        if (normalized > 180) normalized -= 360;
        return normalized;
    }

    private static SceneObject WithGeometry(
        SceneObject source, double x, double y, double width, double height, double rotation) =>
        new(
            source.Id, source.Kind, source.Name,
            x, y, width, height, rotation,
            source.FillColor, source.StrokeColor, source.StrokeWidth,
            source.Visible, source.Locked)
        {
            Sublayer = source.Sublayer,
            GroupId = source.GroupId,
            GroupName = source.GroupName,
            Stamp = source.Stamp,
            Line = source.Line,
            Shape = source.Shape,
            Text = source.Text,
            Composition = source.Composition,
            PlaceholderReason = source.PlaceholderReason,
            OpaquePayload = source.OpaquePayload,
            ObjectOpacity = source.ObjectOpacity,
        };

    private static SceneObject CopyObject(
        SceneObject source, bool? visible = null, bool? locked = null) =>
        new(
            source.Id, source.Kind, source.Name,
            source.X, source.Y, source.Width, source.Height, source.RotationDegrees,
            source.FillColor, source.StrokeColor, source.StrokeWidth,
            visible ?? source.Visible, locked ?? source.Locked)
        {
            Sublayer = source.Sublayer,
            GroupId = source.GroupId,
            GroupName = source.GroupName,
            Stamp = source.Stamp,
            Line = source.Line,
            Shape = source.Shape,
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

        if (!AvailableObjectIds().Contains(selected.Id))
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
        var before = _appearanceGestureBefore ?? EditableAppearanceSelection();
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
        DragPreview = null;
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"#RRGGBB" or "#AARRGGBB" → canonical "#AARRGGBB"; anything else is null.</summary>
    private static string? NormalizeColor(string? text)
    {
        var value = (text ?? "").Trim().TrimStart('#');
        if (value.Length is not (6 or 8) || !value.All(Uri.IsHexDigit))
        {
            return null;
        }

        return "#" + (value.Length == 6 ? "FF" + value : value).ToUpperInvariant();
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

    /// <summary>
    /// Scale against the stamp's 100 % reference. An old file without a reference reports its
    /// current size as the baseline, and the first scale edit adopts it permanently.
    /// </summary>
    public double? StampScalePercent
    {
        get
        {
            if (Session.SelectedObject is not { Stamp: { } stamp } selected)
            {
                return null;
            }

            var reference = stamp.ReferenceWidth ?? selected.Width;
            return reference <= 0 ? null : Math.Round(selected.Width / reference * 100, 1);
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

        var factor = Math.Clamp(percent, 1, 1000) / 100.0;
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
            InspectorError = "Размер должен быть положительным числом.";
            return;
        }

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

    private void EditStamp(string name, bool commit, Func<StampStyle, StampStyle> change) =>
        EditAppearance(name, commit, before =>
        {
            if (before.Stamp is not { } stamp)
            {
                return before;
            }

            var changed = change(stamp);
            return changed.Equals(stamp) ? before : CopyObject(before) with { Stamp = changed };
        });

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
        });

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
            };
            return neutral.Equals(stamp) && before.ObjectOpacity == 1.0
                ? before
                : CopyObject(before) with { Stamp = neutral, ObjectOpacity = 1.0 };
        });

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

    public void SetTextFillColor(string? colorText)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditText("Text fill", commit: true, text => text with { FillColor = color });
        }
    }

    public void SetTextOutlineColor(string? colorText)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditText("Text outline", commit: true, text => text with { OutlineColor = color });
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

    public void SetStrokeColor(string? colorText)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditStroke("Stroke color", commit: true, stroke => stroke with { Color = color });
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

    public void SetShapeFillColor(string? colorText)
    {
        if (RequireColorInput(colorText) is { } color)
        {
            EditShapeFill("Shape fill", commit: true, fill => fill with { Color = color });
        }
    }

    public void SetShapeFillOpacityPercent(double percent, bool commit = true) =>
        EditShapeFill("Shape fill opacity", commit, fill => fill with
        {
            Opacity = Finite(percent, fill.Opacity * 100, 0, 100) / 100.0,
        });

    /// <summary>Fill back to its defaults; the texture reference is dropped with the rest.</summary>
    public void ResetShapeFill() =>
        EditShapeFill("Reset shape fill", commit: true, _ => new ShapeFillStyle());

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
        Raise(nameof(BrushTipName));
        SceneChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>
    /// Writes one recovery snapshot if the current dirty state is not already covered. The call
    /// never queues behind a manual save: a timer tick may be skipped, while Ctrl+S must win.
    /// </summary>
    public async Task<bool> TryAutosaveAsync()
    {
        if (!Session.IsDirty)
        {
            await ClearAutosaveIfCleanAsync().ConfigureAwait(false);
            return false;
        }

        if (ReferenceEquals(_lastAutosavedDocument, Session.Document)
            || !await _saveGate.WaitAsync(0).ConfigureAwait(false))
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
                await Task.Run(() =>
                {
                    var thumbnail = ThumbnailRenderer.RenderPng(snapshot);
                    _store.SaveAutosave(snapshot, DocumentPath, metadata, thumbnail);
                }).ConfigureAwait(false);

                _lastAutosavedDocument = snapshot;
                _lastAutosaveUtc = timestamp;
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
        if (_lastAutosavedDocument is null || !await _saveGate.WaitAsync(0).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await Task.Run(() => _store.ClearRecovery(Session.Document.Id.Value)).ConfigureAwait(false);
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
        AutosaveState = AutosaveState.Idle;
        Raise(nameof(AutosaveSummary));
    }

    // ---------------------------------------------------------------- saving

    public async Task<bool> SaveAsync(bool saveAs = false)
    {
        var path = DocumentPath;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            path = UiTestFilePaths.MapPath
                ?? await _filePicker.PickSaveAsync(Session.Document.Title + MapProjectFormat.Extension)
                    .ConfigureAwait(false);
            if (string.IsNullOrEmpty(path))
            {
                return false; // user cancelled; nothing changes
            }
        }

        // Concurrent saves of one document must queue, not interleave.
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var checkpoint = Session.CaptureSaveCheckpoint();
            var snapshot = checkpoint.Document;
            var metadata = new SaveMetadata(_appVersion, _clock(), checkpoint.Revision);

            SaveState = SaveState.Saving;
            StatusDetail = "";

            try
            {
                await Task.Run(() =>
                {
                    // The thumbnail comes from the same scene evaluator as the canvas, without
                    // selection or any editor chrome.
                    var thumbnail = ThumbnailRenderer.RenderPng(snapshot);
                    _store.Save(snapshot, path!, metadata, thumbnail);
                }).ConfigureAwait(false);

                DocumentPath = path;
                Session.MarkSaved(checkpoint);
                ForgetAutosave();
                SaveState = Session.IsDirty ? SaveState.Unsaved : SaveState.Saved;
                Raise(nameof(DocumentPath));
                RaiseSessionState();

                _recent.Touch(path!, Session.Document.Id.Value, snapshot.Title, metadata.ModifiedUtc);
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
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>True when it is safe to leave the editor. Never loses work silently.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!Session.IsDirty)
        {
            return _lastAutosavedDocument is null
                || await DiscardRecoveryAsync().ConfigureAwait(false);
        }

        return await _prompts.ConfirmCloseDirtyAsync(Session.Document.Title).ConfigureAwait(false) switch
        {
            CloseChoice.Save => await SaveAsync().ConfigureAwait(false) && !Session.IsDirty,
            CloseChoice.DontSave => await DiscardRecoveryAsync().ConfigureAwait(false),
            _ => false,
        };
    }

    private async Task<bool> DiscardRecoveryAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() => _store.ClearRecovery(Session.Document.Id.Value)).ConfigureAwait(false);
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
