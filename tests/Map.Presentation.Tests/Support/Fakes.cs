using Map.Core.Documents;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;

namespace Map.Presentation.Tests.Support;

public sealed class FakeFilePicker : IFilePicker
{
    public string? NextOpenPath { get; set; }
    public string? NextSavePath { get; set; }

    public Task<string?> PickOpenAsync() => Task.FromResult(NextOpenPath);

    public Task<string?> PickSaveAsync(string suggestedFileName) => Task.FromResult(NextSavePath);
}

public sealed class FakePrompts : IUserPrompts
{
    public CloseChoice NextCloseChoice { get; set; } = CloseChoice.Cancel;
    public List<string> Errors { get; } = [];
    public int CloseAsked { get; private set; }

    public Task<CloseChoice> ConfirmCloseDirtyAsync(string documentTitle)
    {
        CloseAsked++;
        return Task.FromResult(NextCloseChoice);
    }

    public Task ShowErrorAsync(string title, string message)
    {
        Errors.Add(message);
        return Task.CompletedTask;
    }
}

public sealed class FakeLog : IAppLog
{
    public List<string> Lines { get; } = [];

    public void Info(string message) => Lines.Add("INFO " + message);

    public void Error(string message, Exception? exception = null) => Lines.Add("ERROR " + message);
}

public sealed class FakeRecentMaps : IRecentMaps
{
    public List<RecentMapItem> Items { get; } = [];

    public IReadOnlyList<RecentMapItem> Load() => [.. Items];

    public void Touch(string path, Guid documentId, string title, DateTimeOffset modifiedUtc)
    {
        Items.RemoveAll(i => i.Path == path);
        Items.Insert(0, new RecentMapItem(path, title, modifiedUtc, IsAvailable: true));
    }

    public void Remove(string path) => Items.RemoveAll(i => i.Path == path);
}

/// <summary>In-memory store: saves become dictionary entries, failures are scripted per call.</summary>
public sealed class FakeProjectStore : IProjectStore
{
    public Dictionary<string, (MapDocument Document, SaveMetadata Metadata, byte[] Preview)> Saved { get; } = [];
    public Queue<Exception> NextSaveFailures { get; } = new();
    public List<RecoveryCandidate> Candidates { get; } = [];
    public Func<string, OpenedProject>? OnOpen { get; set; }
    public int SaveCalls { get; private set; }

    public OpenedProject Open(string path)
    {
        if (OnOpen is not null)
        {
            return OnOpen(path);
        }

        if (!Saved.TryGetValue(path, out var stored))
        {
            throw new FileNotFoundException(path);
        }

        return new OpenedProject
        {
            Document = stored.Document,
            AppVersion = stored.Metadata.AppVersion,
            ModifiedUtc = stored.Metadata.ModifiedUtc,
            PreviewPng = stored.Preview,
        };
    }

    public void Save(MapDocument document, string path, SaveMetadata metadata, byte[] previewPng)
    {
        SaveCalls++;
        if (NextSaveFailures.Count > 0)
        {
            throw NextSaveFailures.Dequeue();
        }

        Saved[path] = (document, metadata, previewPng);
    }

    public IReadOnlyList<RecoveryCandidate> FindRecoveryCandidates() => [.. Candidates];

    public OpenedProject RestoreRecovery(RecoveryCandidate candidate) => Open(candidate.CandidatePath);

    public void DiscardRecovery(RecoveryCandidate candidate) => Candidates.Remove(candidate);
}

public static class TestData
{
    public static MapDocument Document(string title = "map") => new(
        DocumentId.New(), title, DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1920, 1080, "px", "#FF1E2430"),
        [
            new BaseColorLayer(LayerId.New(), "Фон", true, false, 1.0, "#FF1E2430"),
            new ObjectLayer(LayerId.New(), "Объекты", true, false, 1.0, []),
        ]);
}
