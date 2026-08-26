using System.Globalization;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
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

/// <summary>Live drag preview: never part of the document and never part of history.</summary>
public sealed record DragPreview(
    SceneObject Before,
    SceneObject Proposed,
    DragMode Mode,
    HandleKind? Handle,
    double StartWorldX,
    double StartWorldY);

/// <summary>
/// The editor around one <see cref="EditingSession"/>. The session's snapshot is the single
/// source of truth; this class only orchestrates commands, selection, saving and status.
/// </summary>
public sealed class EditorViewModel : ViewModelBase
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
    private ObjectId? _lastSelectedObjectId;
    private RasterStrokeSession? _stroke;
    private List<VectorPoint>? _vectorPoints;

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

    private string? _selectedSetId;
    private IReadOnlyList<AssetVariant> _selectedSetVariants = [];
    private int _currentVariantIndex;
    private StampPlacementMode _stampPlacementMode = StampPlacementMode.One;
    private double _stampAreaRadius = 160;
    private double _stampDensity = 40;
    private bool _randomVariantAfterPlacement;
    private bool _stampRandomAngle = true;
    private bool _stampRandomFlip = true;
    private int _gestureCounter;

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
        string appVersion = "0.2.0",
        AssetCatalog? assetCatalog = null)
    {
        Session = new EditingSession(document);
        DocumentPath = documentPath;
        _store = store;
        _recent = recent;
        _filePicker = filePicker;
        _prompts = prompts;
        _log = log;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _appVersion = appVersion;
        _assetCatalog = assetCatalog;
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
    }

    public EditingSession Session { get; }

    public EditorShortcutSettings Shortcuts { get; } = new();

    public IStampImageSource? StampImages { get; }

    public string? DocumentPath { get; private set; }

    public event EventHandler? SceneChanged;

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
    /// Selection shown the way a person reads it: the object's own name and its kind in Russian.
    /// The stable id stays available to code and to the driver, but never reaches the status bar.
    /// </summary>
    public string SelectionStatus =>
        Session.SelectedObject is { } selected
            ? EditorLabels.DescribeObject(selected)
            : "Ничего не выбрано";

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
        if (_assetCatalog is null || _assetCatalog.Status == AssetLibraryStatus.Damaged)
        {
            _librarySets = [];
            _libraryGroups = [];
            _libraryCollections = [];
            _visibleTiles = AssetTileWindow.Empty;
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
        _visibleTiles = AssetTileWindow.Slice(_librarySets, _visibleTiles.FirstIndex, _visibleTiles.Count);
        RaiseLibrary();
    }

    /// <summary>
    /// Declares which slice of the result the grid can actually show. Only these tiles ever ask
    /// for a thumbnail, so ten thousand cards never become ten thousand bitmaps.
    /// </summary>
    public void SetLibraryVisibleRange(int firstIndex, int count)
    {
        var window = AssetTileWindow.Slice(_librarySets, firstIndex, count);
        if (window.FirstIndex == _visibleTiles.FirstIndex && window.Count == _visibleTiles.Count)
        {
            return;
        }

        _visibleTiles = window;
        Raise(nameof(VisibleLibraryTiles));
        Raise(nameof(VisibleLibraryTileWindow));
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

    private HashSet<string> UsedContentHashes() =>
        Session.Document.Layers.OfType<ObjectLayer>()
            .SelectMany(layer => layer.Objects)
            .Select(item => item.Stamp?.AssetSha256)
            .Where(sha => !string.IsNullOrEmpty(sha))
            .Select(sha => sha!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
    /// One pointer gesture. The whole plan - every object id, hash, position and transform - is
    /// resolved first, then committed as exactly one undoable command. Nothing is rerolled later.
    /// </summary>
    public bool PlaceCurrentVariantAt(double worldX, double worldY)
    {
        if (_assetCatalog is null)
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

        var references = new List<MapAssetReference>();
        var objects = new List<SceneObject>(plan.Count);
        var unavailable = 0;
        foreach (var item in plan)
        {
            if (!records.TryGetValue(item.Sha256, out var record))
            {
                continue;
            }

            if (BuildReference(record) is not { } reference)
            {
                unavailable++;
                continue;
            }

            if (references.All(existing => !string.Equals(
                existing.Sha256, reference.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                references.Add(reference);
            }

            objects.Add(new SceneObject(
                ObjectId.New(), SceneObjectKind.Stamp, record.DisplayName,
                item.X, item.Y, item.Width, item.Height, item.RotationDegrees,
                "#FFFFFFFF", "#00000000", 0, true, false)
            {
                Stamp = new StampStyle(item.Sha256)
                {
                    FlipHorizontal = item.FlipHorizontal,
                    FlipVertical = item.FlipVertical,
                },
            });
        }

        if (objects.Count == 0 || references.Count == 0)
        {
            StatusDetail = unavailable > 0
                ? "Файл ассета отсутствует или не совпадает с сохранённым SHA-256."
                : "Не удалось подготовить размещение.";
            return false;
        }

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
            ? $"Размещён вариант {CurrentVariantLabel}."
            : $"Размещено штампов: {objects.Count}, вариантов: {references.Count}.";
        RefreshLibrary();
        return true;
    }

    /// <summary>The same gesture a canvas press runs, centred on the current view.</summary>
    public bool PlaceCurrentVariantAtViewCentre()
    {
        var centre = ViewCentre();
        return PlaceCurrentVariantAt(centre.X, centre.Y);
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
    /// A fresh seed per gesture, derived from the session revision so a test with a fixed clock
    /// and a fixed history replays identically while two live gestures still differ.
    /// </summary>
    private int NextGestureSeed() =>
        HashCode.Combine(Session.Revision, _selectedSetId, _currentVariantIndex, ++_gestureCounter);

    private MapAssetReference? BuildReference(AssetRecord record)
    {
        if (_assetCatalog is null)
        {
            return null;
        }

        var resolution = _assetCatalog.Resolve(record);
        if (resolution.Status != AssetResolutionStatus.Available || resolution.Path is null)
        {
            return null;
        }

        var thumbnailPath = _assetCatalog.ThumbnailPath(record);
        return new MapAssetReference(
            record.Sha256, record.DisplayName, record.MediaType, record.Extension,
            ProjectAssetStorage.Embedded, record.ImportedUtc,
            record.Provenance.Source, record.Provenance.LicenseId,
            embeddedContent: File.ReadAllBytes(resolution.Path),
            fallbackThumbnailPng: File.Exists(thumbnailPath) ? File.ReadAllBytes(thumbnailPath) : null);
    }

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
        if (_assetCatalog is null)
        {
            return;
        }

        var layer = ActiveLayer as ObjectLayer
            ?? Session.Document.Layers.OfType<ObjectLayer>().LastOrDefault(item => item.Visible && !item.Locked);
        var resolution = _assetCatalog.Resolve(record);
        if (layer is null || layer.Locked || !layer.Visible
            || resolution.Status != AssetResolutionStatus.Available || resolution.Path is null)
        {
            StatusDetail = resolution.Status == AssetResolutionStatus.HashMismatch
                ? "Файл ассета изменился и не совпадает с сохранённым hash. Выберите замену явно."
                : "Выберите доступный ассет и разблокированный слой объектов.";
            return;
        }

        var thumbnail = File.Exists(_assetCatalog.ThumbnailPath(record))
            ? File.ReadAllBytes(_assetCatalog.ThumbnailPath(record))
            : null;
        var projectDirectory = DocumentPath is null ? null : Path.GetDirectoryName(DocumentPath);
        var relative = projectDirectory is null
            ? record.OriginalFileName
            : Path.GetRelativePath(projectDirectory, resolution.Path);
        var reference = new MapAssetReference(
            record.Sha256,
            record.DisplayName,
            record.MediaType,
            record.Extension,
            embedded ? ProjectAssetStorage.Embedded : ProjectAssetStorage.Linked,
            record.ImportedUtc,
            record.Provenance.Source,
            record.Provenance.LicenseId,
            embeddedContent: embedded ? File.ReadAllBytes(resolution.Path) : null,
            catalogId: embedded ? null : record.Sha256,
            relativePath: embedded ? null : relative,
            fallbackThumbnailPng: thumbnail);
        var width = Math.Min(256.0, Session.Document.Canvas.Width / 4.0);
        var height = width * record.Height / record.Width;
        var stamp = new SceneObject(
            ObjectId.New(), SceneObjectKind.Stamp, record.DisplayName,
            x ?? ((Session.Document.Canvas.Width - width) / 2),
            y ?? ((Session.Document.Canvas.Height - height) / 2),
            width, height, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        {
            Stamp = new StampStyle(record.Sha256),
        };

        Session.Execute(new PlaceAssetObjectsCommand(layer.Id, reference, [stamp]));
        Session.SelectedObjectId = stamp.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void ScatterStamp(AssetRecord record, int count, int seed = 12345)
    {
        if (_assetCatalog is null || count is < 1 or > 5_000)
        {
            return;
        }

        var layer = ActiveLayer as ObjectLayer
            ?? Session.Document.Layers.OfType<ObjectLayer>().LastOrDefault(item => item.Visible && !item.Locked);
        var resolution = _assetCatalog.Resolve(record);
        if (layer is null || resolution.Status != AssetResolutionStatus.Available || resolution.Path is null)
        {
            StatusDetail = "Ассет недоступен для размещения.";
            return;
        }

        var reference = new MapAssetReference(
            record.Sha256, record.DisplayName, record.MediaType, record.Extension,
            ProjectAssetStorage.Embedded, record.ImportedUtc,
            record.Provenance.Source, record.Provenance.LicenseId,
            embeddedContent: File.ReadAllBytes(resolution.Path),
            fallbackThumbnailPng: File.ReadAllBytes(_assetCatalog.ThumbnailPath(record)));
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
        if (_assetCatalog is null || Session.SelectedObject is not { Stamp: not null } selected)
        {
            StatusDetail = "Выберите stamp и новый ассет.";
            return;
        }

        var resolution = _assetCatalog.Resolve(record);
        if (resolution.Status != AssetResolutionStatus.Available || resolution.Path is null)
        {
            StatusDetail = "Новый ассет отсутствует или не совпадает с его SHA-256.";
            return;
        }

        var reference = new MapAssetReference(
            record.Sha256, record.DisplayName, record.MediaType, record.Extension,
            ProjectAssetStorage.Embedded, record.ImportedUtc,
            record.Provenance.Source, record.Provenance.LicenseId,
            embeddedContent: File.ReadAllBytes(resolution.Path),
            fallbackThumbnailPng: File.ReadAllBytes(_assetCatalog.ThumbnailPath(record)));
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
    public MapDocument RenderDocument =>
        DragPreview is { } preview
            ? DocumentEditor.ReplaceObject(Session.Document, preview.Proposed)
            : Session.Document;

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
        if (_assetCatalog is null || EditableSelection() is not { } before)
        {
            return;
        }

        var stroke = before.Line?.Stroke ?? before.Shape?.Stroke;
        var resolution = _assetCatalog.Resolve(record);
        if (stroke is null || resolution.Status != AssetResolutionStatus.Available || resolution.Path is null)
        {
            StatusDetail = "Выберите Line/Shape и доступный локальный ассет.";
            return;
        }

        var changed = stroke with { AssetSha256 = record.Sha256, AssetSpacing = 1.25 };
        var after = before.Line is { } line
            ? before with { Line = new LineStyle(line.Nodes, changed, line.Portals, line.Closed) }
            : before with
            {
                Shape = new ShapeStyle(before.Shape!.Regions, before.Shape.Fill, changed, before.Shape.Portals),
            };
        var reference = new MapAssetReference(
            record.Sha256, record.DisplayName, record.MediaType, record.Extension,
            ProjectAssetStorage.Embedded, record.ImportedUtc,
            record.Provenance.Source, record.Provenance.LicenseId,
            embeddedContent: File.ReadAllBytes(resolution.Path),
            fallbackThumbnailPng: File.ReadAllBytes(_assetCatalog.ThumbnailPath(record)));
        Session.Execute(new VectorAssetStyleCommand(before, after, reference));
        AfterEdit();
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
        if (_assetCatalog is null || ActiveLayer is not FilterLayer { Locked: false } before) return;
        var resolution = _assetCatalog.Resolve(record);
        if (resolution.Status != AssetResolutionStatus.Available || resolution.Path is null)
        { StatusDetail = "Текстура фильтра недоступна или изменила SHA-256."; return; }
        var reference = new MapAssetReference(
            record.Sha256, record.DisplayName, record.MediaType, record.Extension,
            ProjectAssetStorage.Embedded, record.ImportedUtc,
            record.Provenance.Source, record.Provenance.LicenseId,
            embeddedContent: File.ReadAllBytes(resolution.Path),
            fallbackThumbnailPng: File.ReadAllBytes(_assetCatalog.ThumbnailPath(record)));
        var after = before with
        {
            Settings = before.Settings with
            { TextureAssetSha256 = record.Sha256, TextureOpacity = 0.28, TextureScale = 1 },
        };
        Session.Execute(new UpdateFilterWithAssetCommand(before, after, reference)); AfterEdit();
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
            GridGeometry.Square => GridGeometry.HexFlat,
            GridGeometry.HexFlat => GridGeometry.HexPointy,
            _ => GridGeometry.Square,
        };
        Session.Execute(new ReplaceGridLayerCommand(before, before with
        {
            Grids = [current with { Geometry = next }, .. before.Grids.Skip(1)],
        }));
        AfterEdit();
    }

    public void ToggleActiveGridExport()
    {
        if (ActiveLayer is not GridLayer { Locked: false, Grids.Count: > 0 } before) return;
        var current = before.Grids[0];
        Session.Execute(new ReplaceGridLayerCommand(before, before with
        {
            Grids = [current with { IncludeInExport = !current.IncludeInExport }, .. before.Grids.Skip(1)],
        }));
        AfterEdit();
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
        ReplaceSelected(before, GridSnapService.SnapPortal(before, portal.Id, grid), "Snap portal to grid");
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

        _stroke = new RasterStrokeSession(target, BrushSettings);
        _stroke.AddPoint(worldX, worldY);
        SceneChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ContinueStroke(double worldX, double worldY)
    {
        if (_stroke is null)
        {
            return;
        }

        _stroke.AddPoint(worldX, worldY);
        SceneChanged?.Invoke(this, EventArgs.Empty);
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
            SceneChanged?.Invoke(this, EventArgs.Empty);
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
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAt(double worldX, double worldY)
    {
        var hit = SceneGeometry.HitTest(Session.Document, worldX, worldY)?.Id;
        if (hit is null && Session.SelectedObjectId is { } selected)
        {
            _lastSelectedObjectId = selected;
        }

        Session.SelectedObjectId = hit;
        RefreshInspector();
        Raise(nameof(SelectionStatus));
        Raise(nameof(SelectionKind));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectObject(ObjectId id)
    {
        var layer = DocumentEditor.FindLayerOf(Session.Document, id);
        if (layer is null)
        {
            return;
        }

        if (Session.SelectedObjectId is { } selected && selected != id)
        {
            _lastSelectedObjectId = selected;
        }

        Session.SelectedObjectId = id;
        _activeLayerId = layer.Id;
        RefreshInspector();
        Raise(nameof(SelectionStatus));
        Raise(nameof(SelectionKind));
        Raise(nameof(ActiveLayer));
        Raise(nameof(ActiveLayerId));
        Raise(nameof(ActiveLayerSummary));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeselectObject()
    {
        if (Session.SelectedObjectId is { } selected)
        {
            _lastSelectedObjectId = selected;
        }

        Session.SelectedObjectId = null;
        RefreshInspector();
        Raise(nameof(SelectionStatus));
        Raise(nameof(SelectionKind));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RestoreLastSelection()
    {
        if (_lastSelectedObjectId is not { } id || DocumentEditor.FindLayerOf(Session.Document, id) is null)
        {
            return;
        }

        SelectObject(id);
    }

    public void NudgeSelectedObject(double deltaX, double deltaY)
    {
        if (EditableSelection() is not { } before) return;
        ReplaceSelected(before, WithGeometry(before, before.X + deltaX, before.Y + deltaY,
            before.Width, before.Height, before.RotationDegrees), "Nudge object");
    }

    public void ResizeSelectedObject(int direction, bool largerStep = false)
    {
        if (EditableSelection() is not { } before || direction == 0) return;
        var factor = 1 + Math.Sign(direction) * (largerStep ? 0.1 : 0.01);
        var width = Math.Max(1, before.Width * factor);
        var height = Math.Max(1, before.Height * factor);
        ReplaceSelected(before, WithGeometry(before,
            before.X + (before.Width - width) / 2,
            before.Y + (before.Height - height) / 2,
            width, height, before.RotationDegrees), "Resize object from shortcut");
    }

    public void RotateSelectedObject(int direction, bool largerStep = false, bool fineStep = false)
    {
        if (EditableSelection() is not { } before || direction == 0) return;
        var step = fineStep ? 0.25 : largerStep ? 15 : 1;
        ReplaceSelected(before, WithGeometry(before, before.X, before.Y, before.Width, before.Height,
            NormalizeAngle(before.RotationDegrees + Math.Sign(direction) * step)), "Rotate object from shortcut");
    }

    public void AdjustSelectedSublayer(int direction)
    {
        if (EditableSelection() is not { } before || direction == 0) return;
        var value = Math.Clamp(before.Sublayer + Math.Sign(direction), -10, 10);
        if (value == before.Sublayer) return;
        ReplaceSelected(before, CopyObject(before) with { Sublayer = value }, "Change object sublayer");
    }

    public void AdjustSelectedOpacity(int direction, bool largerStep = false)
    {
        if (EditableSelection() is not { } before || direction == 0) return;
        var step = largerStep ? 0.1 : 0.05;
        var value = Math.Clamp(before.ObjectOpacity + Math.Sign(direction) * step, 0, 1);
        if (value == before.ObjectOpacity) return;
        ReplaceSelected(before, CopyObject(before) with { ObjectOpacity = value }, "Change object opacity");
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

    public void ToggleSelectedObjectVisibility()
    {
        if (Session.SelectedObject is not { } before)
        {
            return;
        }

        var after = CopyObject(before, visible: !before.Visible);
        Session.Execute(new ReplaceObjectsCommand([before], [after], "Object visibility"));
        AfterEdit();
    }

    public void ToggleSelectedObjectLock()
    {
        if (Session.SelectedObject is not { } before)
        {
            return;
        }

        var after = CopyObject(before, locked: !before.Locked);
        Session.Execute(new ReplaceObjectsCommand([before], [after], "Object lock"));
        AfterEdit();
    }

    public void DeleteSelectedObject()
    {
        if (Session.SelectedObject is not { } selected
            || DocumentEditor.FindLayerOf(Session.Document, selected.Id) is not { } layer)
        {
            return;
        }

        Session.Execute(new RemoveObjectsCommand(Session.Document, layer.Id, [selected.Id]));
        Session.SelectedObjectId = null;
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

    /// <summary>The gizmo handle under the cursor, for pointer feedback. Null when none.</summary>
    public HandleKind? HitHandle(double worldX, double worldY, double viewScale)
    {
        var selected = EditableSelection();
        if (selected is null)
        {
            return null;
        }

        return SelectionHandles.HitTest(
            selected, worldX, worldY, HandleHitRadius(viewScale), RotationHandleOffset(viewScale));
    }

    /// <summary>
    /// One canvas press: a grab of a gizmo handle starts a resize/rotate of the current selection;
    /// otherwise the press selects, and an editable object starts a move. Returns true when a
    /// drag began. Whatever the mode, the whole gesture ends as one history entry.
    /// </summary>
    public bool BeginCanvasInteraction(double worldX, double worldY, double viewScale)
    {
        var handle = HitHandle(worldX, worldY, viewScale);
        if (handle is { } grabbed)
        {
            var selected = EditableSelection()!;
            DragPreview = new DragPreview(
                selected, selected,
                grabbed == HandleKind.Rotate ? DragMode.Rotate : DragMode.Resize,
                grabbed, worldX, worldY);
            return true;
        }

        SelectAt(worldX, worldY);
        RefreshInspector();
        return BeginDrag(worldX, worldY);
    }

    public bool BeginDrag(double startWorldX = 0, double startWorldY = 0)
    {
        var selected = EditableSelection();
        if (selected is null)
        {
            return false;
        }

        DragPreview = new DragPreview(selected, selected, DragMode.Move, null, startWorldX, startWorldY);
        return true;
    }

    /// <summary>Absolute-cursor drag update covering move, resize and rotate.</summary>
    public void UpdateDragTo(double worldX, double worldY)
    {
        if (DragPreview is not { } preview)
        {
            return;
        }

        var before = preview.Before;
        var proposed = preview.Mode switch
        {
            DragMode.Resize => SelectionHandles.Resize(before, preview.Handle!.Value, worldX, worldY),
            DragMode.Rotate => SelectionHandles.Rotate(
                before, preview.StartWorldX, preview.StartWorldY, worldX, worldY),
            _ => WithGeometry(
                before,
                before.X + (worldX - preview.StartWorldX),
                before.Y + (worldY - preview.StartWorldY),
                before.Width, before.Height, before.RotationDegrees),
        };

        DragPreview = preview with { Proposed = proposed };
        SceneChanged?.Invoke(this, EventArgs.Empty);
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

        if (preview.Proposed != preview.Before)
        {
            Session.Execute(new TransformObjectCommand(preview.Before, preview.Proposed));
            AfterEdit();
        }
    }

    public void CancelDrag()
    {
        DragPreview = null;
        SceneChanged?.Invoke(this, EventArgs.Empty);
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
        RefreshInspector();
        Raise(nameof(SelectionStatus));
        Raise(nameof(SelectionKind));
        Raise(nameof(Layers));
        Raise(nameof(ActiveLayer));
        Raise(nameof(ActiveLayerId));
        Raise(nameof(ActiveLayerSummary));
        Raise(nameof(GridSummary));
        Raise(nameof(MaskEditLayerId));
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
            Stamp = source.Stamp,
            Line = source.Line,
            Shape = source.Shape,
            Text = source.Text,
            Composition = source.Composition,
            PlaceholderReason = source.PlaceholderReason,
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
            Stamp = source.Stamp,
            Line = source.Line,
            Shape = source.Shape,
            Text = source.Text,
            Composition = source.Composition,
            PlaceholderReason = source.PlaceholderReason,
            ObjectOpacity = source.ObjectOpacity,
        };

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
                _log.Info($"Autosaved document {snapshot.Id} revision {checkpoint.Revision}.");
                return true;
            }
            catch (Exception ex)
            {
                // Autosave is a safety net, never a reason to close the editor. The exception is
                // retained in the local diagnostic log and the next timer tick may retry.
                AutosaveState = AutosaveState.Failed;
                _log.Error($"Autosave of document {snapshot.Id} failed", ex);
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
            _log.Error($"Autosave cleanup for document {Session.Document.Id} failed", ex);
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
                _log.Info($"Saved {path} (revision {checkpoint.Revision}).");
                return true;
            }
            catch (MapProjectSaveException ex)
            {
                SaveState = SaveState.SaveFailed;
                StatusDetail = ex.TempPath is null
                    ? "Не удалось сохранить файл. Документ остаётся открытым, данные не потеряны."
                    : $"Не удалось заменить основной файл. Документ остаётся открыт, recovery сохранён здесь: {ex.TempPath}";
                _log.Error($"Save of {path} failed", ex);
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
            _log.Error($"Recovery discard for document {Session.Document.Id} failed", ex);
            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
