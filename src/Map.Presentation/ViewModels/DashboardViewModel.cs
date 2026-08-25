using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;

namespace Map.Presentation.ViewModels;

/// <summary>One card of the dashboard's recent list, thumbnail included.</summary>
public sealed record RecentCard(
    string Path,
    string Title,
    DateTimeOffset ModifiedUtc,
    bool IsAvailable,
    byte[]? ThumbnailPng)
{
    public string Subtitle => IsAvailable
        ? $"{ModifiedUtc.ToLocalTime():g}"
        : "Файл не найден";
}

/// <summary>One offered recovery candidate.</summary>
public sealed record RecoveryCard(RecoveryCandidate Candidate)
{
    public string Description =>
        $"Несохранённая копия «{Candidate.Title}» от {Candidate.UpdatedUtc.ToLocalTime():g}";
}

/// <summary>
/// The local dashboard: recent maps with real thumbnails, plus recovery candidates found in the
/// journal. Missing or damaged files are shown honestly and never take the app down.
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private readonly IProjectStore _store;
    private readonly IRecentMaps _recent;
    private readonly IAppLog _log;

    private IReadOnlyList<RecentCard> _recentCards = [];
    private IReadOnlyList<RecoveryCard> _recoveryCards = [];
    private string _lastError = "";

    public DashboardViewModel(IProjectStore store, IRecentMaps recent, IAppLog log)
    {
        _store = store;
        _recent = recent;
        _log = log;
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

    public void Refresh()
    {
        var cards = new List<RecentCard>();
        foreach (var entry in _recent.Load())
        {
            byte[]? thumbnail = null;
            var available = entry.IsAvailable;

            if (available)
            {
                try
                {
                    thumbnail = _store.Open(entry.Path).PreviewPng;
                }
                catch (Exception ex) when (ex is MapProjectException or IOException)
                {
                    // The card stays, marked unavailable - an honest state, not a crash.
                    available = false;
                    _log.Error($"Recent map {entry.Path} could not be read", ex);
                }
            }

            cards.Add(new RecentCard(entry.Path, entry.Title, entry.ModifiedUtc, available, thumbnail));
        }

        RecentCards = cards;

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

    /// <summary>Opens a map for the editor, or returns null with a user-facing error message.</summary>
    public OpenedProject? TryOpen(string path)
    {
        try
        {
            var opened = _store.Open(path);
            _recent.Touch(path, opened.Document.Id.Value, opened.Document.Title, opened.ModifiedUtc);
            LastError = "";
            _log.Info($"Opened {path}.");
            return opened;
        }
        catch (UnsupportedSchemaVersionException ex)
        {
            LastError = $"Файл создан более новой версией программы (схема {ex.SchemaVersion}). " +
                        "Файл не изменён.";
        }
        catch (MapProjectException ex)
        {
            LastError = $"Не удалось открыть файл: {ex.Message} Данные на диске не изменены.";
        }
        catch (FileNotFoundException)
        {
            LastError = "Файл не найден. Возможно, он был перемещён или удалён.";
            _recent.Remove(path);
        }
        catch (IOException ex)
        {
            LastError = $"Файл недоступен: {ex.Message}";
        }

        _log.Error($"Open of {path} failed: {LastError}");
        Refresh();
        return null;
    }

    public OpenedProject? TryRestore(RecoveryCard card)
    {
        try
        {
            var restored = _store.RestoreRecovery(card.Candidate);
            _log.Info($"Recovery candidate for {card.Candidate.TargetPath} restored.");
            return restored;
        }
        catch (Exception ex) when (ex is MapProjectException or IOException)
        {
            LastError = $"Не удалось восстановить копию: {ex.Message}";
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
            _log.Info($"Recovery candidate for {card.Candidate.TargetPath} discarded by the user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"Не удалось удалить копию: {ex.Message}";
            _log.Error("Recovery discard failed", ex);
        }

        Refresh();
    }
}
