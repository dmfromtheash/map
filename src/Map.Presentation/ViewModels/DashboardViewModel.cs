using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

/// <summary>One card of the dashboard's recent list, thumbnail included.</summary>
public sealed record RecentCard(
    string Path,
    string Title,
    DateTimeOffset ModifiedUtc,
    bool IsAvailable,
    byte[]? ThumbnailPng,
    string MapType = "Локальная карта",
    long SizeBytes = 0)
{
    public string Subtitle => IsAvailable
        ? $"{MapType} · {ModifiedUtc.ToLocalTime():g} · {FormatSize(SizeBytes)}"
        : "Файл не найден";

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.#} GiB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.#} MiB",
        >= 1024 => $"{bytes / 1024d:0.#} KiB",
        _ => $"{bytes} B",
    };
}

public enum DashboardSortOrder { ModifiedNewest, NameAscending, TypeAscending, SizeDescending }

/// <summary>One offered recovery candidate.</summary>
public sealed record RecoveryCard(RecoveryCandidate Candidate)
{
    public string Description =>
        $"{(Candidate.Kind == RecoveryCandidateKind.Autosave ? "Автосохранённая" : "Несохранённая")} " +
        $"копия «{Candidate.Title}» от {Candidate.UpdatedUtc.ToLocalTime():g}";
}

public sealed record DashboardOpenResult(OpenedProject Project, string Path);

/// <summary>
/// The local dashboard: recent maps with real thumbnails, plus recovery candidates found in the
/// journal. Missing or damaged files are shown honestly and never take the app down.
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private readonly IProjectStore _store;
    private readonly IRecentMaps _recent;
    private readonly IAppLog _log;
    private readonly IFilePicker? _filePicker;
    private readonly IUserPrompts? _prompts;
    private readonly IProjectTrash? _trash;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _appVersion;
    private readonly SemaphoreSlim _openGate = new(1, 1);

    private IReadOnlyList<RecentCard> _recentCards = [];
    private IReadOnlyList<RecentCard> _allRecentCards = [];
    private IReadOnlyList<RecoveryCard> _recoveryCards = [];
    private string _lastError = "";
    private bool _isBusy;
    private string _searchText = "";
    private DashboardSortOrder _sortOrder;

    public DashboardViewModel(
        IProjectStore store,
        IRecentMaps recent,
        IAppLog log,
        IFilePicker? filePicker = null,
        IUserPrompts? prompts = null,
        IProjectTrash? trash = null,
        Func<DateTimeOffset>? clock = null,
        string appVersion = "Map")
    {
        _store = store;
        _recent = recent;
        _log = log;
        _filePicker = filePicker;
        _prompts = prompts;
        _trash = trash;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _appVersion = appVersion;
    }

    public IReadOnlyList<RecentCard> RecentCards
    {
        get => _recentCards;
        private set => SetField(ref _recentCards, value);
    }

    public IReadOnlyList<RecoveryCard> RecoveryCards
    {
        get => _recoveryCards;
        private set => SetField(ref _recoveryCards, value);
    }

    /// <summary>Last user-facing problem, e.g. a corrupt file the user tried to open.</summary>
    public string LastError
    {
        get => _lastError;
        set => SetField(ref _lastError, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value ?? "")) ApplyQuery();
        }
    }

    public DashboardSortOrder SortOrder
    {
        get => _sortOrder;
        set
        {
            if (SetField(ref _sortOrder, value)) ApplyQuery();
        }
    }

    public void Refresh()
    {
        var cards = new List<RecentCard>();
        foreach (var entry in _recent.Load())
        {
            byte[]? thumbnail = entry.ThumbnailPng;
            var available = entry.IsAvailable;

            // Old recent indexes did not carry their already-bounded preview. Inspect only those
            // legacy cards once; every Stage 16 search/sort thereafter is metadata-only.
            if (available && thumbnail is null)
            {
                try
                {
                    var inspection = _store.Inspect(entry.Path);
                    if (inspection.Status == ProjectFileStatus.Ready)
                    {
                        thumbnail = inspection.Project?.PreviewPng;
                    }
                    else if (inspection.Status != ProjectFileStatus.MigrationRequired)
                    {
                        available = false;
                    }
                }
                catch (Exception ex) when (ex is MapProjectException or IOException or UnauthorizedAccessException)
                {
                    // The card stays, marked unavailable - an honest state, not a crash.
                    available = false;
                    _log.Error("RECENT_INSPECTION_FAILED", ex);
                }
            }

            cards.Add(new RecentCard(
                entry.Path, entry.Title, entry.ModifiedUtc, available, thumbnail,
                entry.MapType, entry.SizeBytes));
        }

        _allRecentCards = cards;
        ApplyQuery();

        try
        {
            RecoveryCards = _store.FindRecoveryCandidates().Select(c => new RecoveryCard(c)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecoveryCards = [];
            _log.Error("Recovery scan failed", ex);
        }
    }

    /// <summary>Compatibility wrapper used by older unit tests; UI uses the asynchronous path.</summary>
    public OpenedProject? TryOpen(string path) =>
        TryOpenAsync(path).GetAwaiter().GetResult()?.Project;

    /// <summary>Inspects, optionally migrates a copy, and returns the exact editor-bound path.</summary>
    public async Task<DashboardOpenResult?> TryOpenAsync(string path)
    {
        if (!await _openGate.WaitAsync(0))
        {
            _log.Info("FILE_OPEN_DUPLICATE_IGNORED");
            return null;
        }

        IsBusy = true;
        try
        {
            var inspection = await Task.Run(() => _store.Inspect(path));
            if (inspection.Status == ProjectFileStatus.Ready && inspection.Project is { } opened)
            {
                return CompleteOpen(path, opened, "FILE_OPENED");
            }

            if (inspection.Status == ProjectFileStatus.MigrationRequired)
            {
                if (_prompts is null || _filePicker is null)
                {
                    LastError = inspection.Message;
                    _log.Error("FILE_MIGRATION_UI_UNAVAILABLE");
                    return null;
                }

                if (!await _prompts.ConfirmMigrationCopyAsync())
                {
                    LastError = "";
                    _log.Info("FILE_MIGRATION_CANCELLED");
                    return null;
                }

                var stem = Path.GetFileNameWithoutExtension(path);
                var destination = await _filePicker.PickSaveAsync($"{stem} — migrated v2.mapproj");
                if (destination is null)
                {
                    LastError = "";
                    _log.Info("FILE_MIGRATION_CANCELLED");
                    return null;
                }

                try
                {
                    var migrated = await Task.Run(() => _store.MigrateCopy(path, destination));
                    return CompleteOpen(destination, migrated.Project, "FILE_MIGRATION_SUCCEEDED_V1_V2");
                }
                catch (ArgumentException ex)
                {
                    LastError = "Выберите новый файл, отличный от исходного проекта.";
                    _log.Error("FILE_MIGRATION_DESTINATION_INVALID", ex);
                    return null;
                }
                catch (UnauthorizedAccessException ex)
                {
                    LastError = "Нет доступа для создания выбранной копии.";
                    _log.Error("FILE_MIGRATION_ACCESS_DENIED", ex);
                    return null;
                }
                catch (IOException ex)
                {
                    LastError = "Не удалось создать новую копию. Возможно, файл уже существует или недоступен.";
                    _log.Error("FILE_MIGRATION_IO_FAILURE", ex);
                    return null;
                }
                catch (MapProjectException ex)
                {
                    LastError = "Копия не создана: проект не прошёл безопасную проверку миграции.";
                    _log.Error("FILE_MIGRATION_VALIDATION_FAILED", ex);
                    return null;
                }
            }

            ShowInspectionFailure(path, inspection);
            return null;
        }
        finally
        {
            IsBusy = false;
            _openGate.Release();
        }
    }

    private void ApplyQuery()
    {
        IEnumerable<RecentCard> query = _allRecentCards;
        var text = SearchText.Trim();
        if (text.Length > 0)
        {
            query = query.Where(card =>
                card.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || card.MapType.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        }

        query = SortOrder switch
        {
            DashboardSortOrder.NameAscending => query
                .OrderBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase),
            DashboardSortOrder.TypeAscending => query
                .OrderBy(card => card.MapType, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase),
            DashboardSortOrder.SizeDescending => query
                .OrderByDescending(card => card.SizeBytes)
                .ThenBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderByDescending(card => card.ModifiedUtc),
        };
        RecentCards = query.ToList();
    }

    /// <summary>
    /// Creates an atomic independent project copy. The immutable source snapshot is never edited;
    /// the new document identity and title are assigned before the destination is published.
    /// </summary>
    public async Task<string?> CloneAsync(RecentCard source, bool asTemplate = false)
    {
        if (_filePicker is null || IsBusy) return null;
        IsBusy = true;
        try
        {
            var inspection = await Task.Run(() => _store.Inspect(source.Path));
            if (inspection.Status != ProjectFileStatus.Ready || inspection.Project is not { } opened)
            {
                ShowInspectionFailure(source.Path, inspection);
                return null;
            }

            var sourceDocument = opened.Document;
            var title = sourceDocument.Title + (asTemplate ? " — из шаблона" : " — копия");
            var destination = await _filePicker.PickSaveAsync(title + ".mapproj");
            if (destination is null)
            {
                _log.Info("PROJECT_CLONE_CANCELLED");
                return null;
            }

            if (string.Equals(Path.GetFullPath(source.Path), Path.GetFullPath(destination),
                    StringComparison.OrdinalIgnoreCase))
            {
                LastError = "Для копии выберите новый файл, отличный от исходного проекта.";
                return null;
            }

            var created = _clock();
            var copy = new Map.Core.Documents.MapDocument(
                Map.Core.Documents.DocumentId.New(), title, created,
                sourceDocument.Canvas, sourceDocument.Layers, sourceDocument.Assets,
                sourceDocument.Notes, checkpoints: [], stampForeground: sourceDocument.StampForeground);
            var preview = ThumbnailRenderer.RenderPng(copy);
            await Task.Run(() => _store.Save(
                copy, destination, new SaveMetadata(_appVersion, created), preview));
            _recent.Touch(destination, copy.Id.Value, copy.Title, created, preview,
                asTemplate ? "Карта из шаблона" : "Копия карты");
            LastError = "";
            _log.Info(asTemplate ? "PROJECT_TEMPLATE_CREATED" : "PROJECT_CLONED");
            Refresh();
            return destination;
        }
        catch (Exception error) when (error is MapProjectException or IOException
            or UnauthorizedAccessException or ArgumentException)
        {
            LastError = "Копия не создана. Исходный проект не изменён.";
            _log.Error("PROJECT_CLONE_FAILED", error);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Moves exactly one confirmed project to the platform trash.</summary>
    public async Task<bool> DeleteAsync(RecentCard source)
    {
        if (_prompts is null || _trash is null || IsBusy) return false;
        if (!await _prompts.ConfirmDeleteProjectAsync(source.Title))
        {
            _log.Info("PROJECT_DELETE_CANCELLED");
            return false;
        }

        IsBusy = true;
        try
        {
            await Task.Run(() => _trash.MoveToTrash(source.Path));
            _recent.Remove(source.Path);
            LastError = "";
            _log.Info("PROJECT_MOVED_TO_TRASH");
            Refresh();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LastError = "Не удалось переместить карту в Корзину. Файл не изменён.";
            _log.Error("PROJECT_DELETE_FAILED", error);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private DashboardOpenResult CompleteOpen(string path, OpenedProject opened, string code)
    {
        _recent.Touch(
            path, opened.Document.Id.Value, opened.Document.Title, opened.ModifiedUtc,
            opened.PreviewPng);
        LastError = "";
        _log.Info(code);
        return new DashboardOpenResult(opened, path);
    }

    private void ShowInspectionFailure(string path, ProjectFileInspection inspection)
    {
        LastError = $"{inspection.Title}. {inspection.Message}";
        if (inspection.Status == ProjectFileStatus.NotFound)
        {
            _recent.Remove(path);
        }
        _log.Error(inspection.Code);
        Refresh();
    }

    public OpenedProject? TryRestore(RecoveryCard card)
    {
        try
        {
            var restored = _store.RestoreRecovery(card.Candidate);
            _log.Info("RECOVERY_RESTORED");
            return restored;
        }
        catch (Exception ex) when (ex is MapProjectException or IOException)
        {
            LastError = "Не удалось восстановить копию. Данные на диске не изменены.";
            _log.Error("Recovery restore failed", ex);
            Refresh();
            return null;
        }
    }

    public void Discard(RecoveryCard card)
    {
        try
        {
            _store.DiscardRecovery(card.Candidate);
            _log.Info("RECOVERY_DISCARDED");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = "Не удалось удалить копию восстановления.";
            _log.Error("Recovery discard failed", ex);
        }

        Refresh();
    }
}
