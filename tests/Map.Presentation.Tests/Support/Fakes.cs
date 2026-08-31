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
    public bool NextRestoreCheckpointConfirmation { get; set; }
    public bool NextDeleteCheckpointConfirmation { get; set; }
    public bool NextMigrationConfirmation { get; set; }
    public List<string> Errors { get; } = [];
    public int CloseAsked { get; private set; }
    public int RestoreCheckpointAsked { get; private set; }
    public int DeleteCheckpointAsked { get; private set; }
    public int MigrationAsked { get; private set; }

    public Task<bool> ConfirmMigrationCopyAsync()
    {
        MigrationAsked++;
        return Task.FromResult(NextMigrationConfirmation);
    }

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

    public Task<bool> ConfirmRestoreCheckpointAsync(string checkpointName)
    {
        RestoreCheckpointAsked++;
        return Task.FromResult(NextRestoreCheckpointConfirmation);
    }

    public Task<bool> ConfirmDeleteCheckpointAsync(string checkpointName)
    {
        DeleteCheckpointAsked++;
        return Task.FromResult(NextDeleteCheckpointConfirmation);
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
    public Queue<Exception> NextAutosaveFailures { get; } = new();
    public List<(MapDocument Document, string? TargetPath, SaveMetadata Metadata, byte[] Preview)> Autosaves { get; } = [];
    public List<Guid> ClearedRecovery { get; } = [];
    public ManualResetEventSlim? ManualSaveEntered { get; set; }
    public ManualResetEventSlim? ManualSaveRelease { get; set; }
    public ManualResetEventSlim? AutosaveEntered { get; set; }
    public ManualResetEventSlim? AutosaveRelease { get; set; }
    public List<RecoveryCandidate> Candidates { get; } = [];
    public Func<string, OpenedProject>? OnOpen { get; set; }
    public Func<string, ProjectFileInspection>? OnInspect { get; set; }
    public Func<string, string, ProjectMigrationResult>? OnMigrate { get; set; }
    public int SaveCalls { get; private set; }

    public ProjectFileInspection Inspect(string path)
    {
        if (OnInspect is not null) return OnInspect(path);
        try
        {
            return new ProjectFileInspection
            {
                Status = ProjectFileStatus.Ready,
                Code = "FILE_READY",
                Title = "Проект готов к открытию",
                Message = "Проверка файла завершена.",
                SourceSchema = MapProjectFormat.SchemaVersion,
                Project = Open(path),
            };
        }
        catch (FileNotFoundException)
        {
            return new ProjectFileInspection
            {
                Status = ProjectFileStatus.NotFound,
                Code = "FILE_NOT_FOUND",
                Title = "Файл не найден",
                Message = "Выбранный файл больше недоступен.",
            };
        }
        catch (UnauthorizedAccessException)
        {
            return new ProjectFileInspection
            {
                Status = ProjectFileStatus.AccessDenied,
                Code = "FILE_ACCESS_DENIED",
                Title = "Нет доступа к файлу",
                Message = "Проверьте права доступа к файлу.",
            };
        }
        catch (UnsupportedSchemaVersionException ex)
        {
            return new ProjectFileInspection
            {
                Status = ProjectFileStatus.UnsupportedVersion,
                Code = "FILE_UNSUPPORTED_VERSION",
                Title = "Проект создан более новой версией Map",
                Message = "Эта версия приложения не поддерживает схему проекта.",
                SourceSchema = ex.SchemaVersion,
                MaxSupportedSchema = MapProjectFormat.SchemaVersion,
            };
        }
        catch (MapProjectException)
        {
            return new ProjectFileInspection
            {
                Status = ProjectFileStatus.Corrupted,
                Code = "FILE_CORRUPTED",
                Title = "Проект повреждён",
                Message = "Структура проекта не прошла проверку.",
            };
        }
        catch (IOException)
        {
            return new ProjectFileInspection
            {
                Status = ProjectFileStatus.IoFailure,
                Code = "FILE_IO_FAILURE",
                Title = "Не удалось прочитать файл",
                Message = "Проверьте доступность файла.",
            };
        }
    }

    public ProjectMigrationResult MigrateCopy(string sourcePath, string destinationPath) =>
        OnMigrate?.Invoke(sourcePath, destinationPath)
        ?? throw new InvalidOperationException("No fake migration was configured.");

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
        ManualSaveEntered?.Set();
        if (ManualSaveRelease is not null && !ManualSaveRelease.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new IOException("Timed out waiting for the scripted save release.");
        }

        if (NextSaveFailures.Count > 0)
        {
            throw NextSaveFailures.Dequeue();
        }

        Saved[path] = (document, metadata, previewPng);
    }

    public void SaveAutosave(
        MapDocument document, string? targetPath, SaveMetadata metadata, byte[] previewPng)
    {
        AutosaveEntered?.Set();
        if (AutosaveRelease is not null && !AutosaveRelease.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new IOException("Timed out waiting for the scripted autosave release.");
        }

        if (NextAutosaveFailures.Count > 0)
        {
            throw NextAutosaveFailures.Dequeue();
        }

        Autosaves.Add((document, targetPath, metadata, previewPng));
    }

    public void ClearRecovery(Guid documentId)
    {
        ClearedRecovery.Add(documentId);
        Candidates.RemoveAll(candidate => candidate.DocumentId == documentId);
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
