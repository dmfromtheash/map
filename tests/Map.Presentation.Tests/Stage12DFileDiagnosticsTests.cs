using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Platform.Windows;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class Stage12DFileDiagnosticsTests
{
    [Fact]
    public void Stage12D_diagnostics_redacts_absolute_paths_and_never_writes_raw_exception()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-stage12d-log", Guid.NewGuid().ToString("N"));
        try
        {
            var log = new DiagnosticsLog(root);
            log.Error(
                @"FILE_IO_FAILURE C:\private\owner title\secret.mapproj",
                new IOException(@"raw exception C:\private\owner title\secret.mapproj"));
            var text = File.ReadAllText(log.Path_);

            Assert.Contains("FILE_IO_FAILURE", text, StringComparison.Ordinal);
            Assert.Contains("exception=IOException", text, StringComparison.Ordinal);
            Assert.DoesNotContain(@"C:\private", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret.mapproj", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(" at ", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Stage12D_current_v2_opens_without_prompt_and_logs_only_stable_code()
    {
        var source = @"C:\private\named-project.mapproj";
        var (dashboard, store, recent, prompts, _, log) = Create();
        store.OnInspect = _ => Ready("private title");

        var result = await dashboard.TryOpenAsync(source);

        Assert.NotNull(result);
        Assert.Equal(source, result.Path);
        Assert.Equal(0, prompts.MigrationAsked);
        Assert.Single(recent.Items);
        Assert.Contains("FILE_OPENED", Assert.Single(log.Lines));
        Assert.DoesNotContain("private", Assert.Single(log.Lines), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stage12D_v1_cancel_is_safe_and_dashboard_remains_usable()
    {
        var (dashboard, store, recent, prompts, _, log) = Create();
        store.OnInspect = _ => MigrationRequired();
        prompts.NextMigrationConfirmation = false;

        var result = await dashboard.TryOpenAsync(@"C:\private\legacy.mapproj");

        Assert.Null(result);
        Assert.Equal(1, prompts.MigrationAsked);
        Assert.Empty(recent.Items);
        Assert.False(dashboard.IsBusy);
        Assert.Equal("", dashboard.LastError);
        Assert.Contains(log.Lines, line => line.Contains("FILE_MIGRATION_CANCELLED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stage12D_successful_migration_binds_only_destination_and_blocks_duplicate_click()
    {
        var source = @"C:\private\legacy.mapproj";
        var destination = @"C:\private\legacy — migrated v2.mapproj";
        var (dashboard, store, recent, prompts, picker, log) = Create();
        store.OnInspect = _ => MigrationRequired();
        prompts.NextMigrationConfirmation = true;
        picker.NextSavePath = destination;
        store.OnMigrate = (actualSource, actualDestination) =>
        {
            Assert.Equal(source, actualSource);
            Assert.Equal(destination, actualDestination);
            return new ProjectMigrationResult
            {
                Project = Opened("migrated"),
                SourceSchema = 1,
                TargetSchema = 2,
            };
        };

        var result = await dashboard.TryOpenAsync(source);

        Assert.NotNull(result);
        Assert.Equal(destination, result.Path);
        Assert.Equal(destination, Assert.Single(recent.Items).Path);
        Assert.DoesNotContain(recent.Items, item => item.Path == source);
        Assert.Contains(log.Lines, line => line.Contains("FILE_MIGRATION_SUCCEEDED_V1_V2", StringComparison.Ordinal));

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.OnInspect = _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return Ready("one");
        };
        var first = dashboard.TryOpenAsync(source);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var duplicate = await dashboard.TryOpenAsync(source);
        release.Set();
        await first;
        Assert.Null(duplicate);
        Assert.Contains(log.Lines, line => line.Contains("FILE_OPEN_DUPLICATE_IGNORED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stage12D_failure_categories_are_distinct_and_recent_removal_is_only_for_not_found()
    {
        var (dashboard, store, recent, _, _, log) = Create();
        var path = @"C:\private\project.mapproj";
        recent.Items.Add(new(path, "private title", DateTimeOffset.UnixEpoch, true));
        var cases = new[]
        {
            Inspection(ProjectFileStatus.Corrupted, "FILE_CORRUPTED", "Проект повреждён"),
            Inspection(ProjectFileStatus.ResourceLimit, "FILE_RESOURCE_LIMIT", "Проект превышает безопасный лимит"),
            Inspection(ProjectFileStatus.UnsupportedVersion, "FILE_UNSUPPORTED_VERSION", "Проект создан более новой версией Map"),
            Inspection(ProjectFileStatus.AccessDenied, "FILE_ACCESS_DENIED", "Нет доступа к файлу"),
            Inspection(ProjectFileStatus.IoFailure, "FILE_IO_FAILURE", "Не удалось прочитать файл"),
        };

        foreach (var item in cases)
        {
            store.OnInspect = _ => item;
            Assert.Null(await dashboard.TryOpenAsync(path));
            Assert.Contains(item.Title, dashboard.LastError, StringComparison.Ordinal);
            Assert.Single(recent.Items);
        }

        store.OnInspect = _ => Inspection(ProjectFileStatus.NotFound, "FILE_NOT_FOUND", "Файл не найден");
        Assert.Null(await dashboard.TryOpenAsync(path));
        Assert.Empty(recent.Items);
        Assert.All(log.Lines, line => Assert.DoesNotContain(@"C:\private", line, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(cases.Length + 1, log.Lines.Count(line => line.StartsWith("ERROR FILE_", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Stage12D_unrelated_open_failure_keeps_recovery_cards_visible()
    {
        var (dashboard, store, _, _, _, _) = Create();
        store.Candidates.Add(new RecoveryCandidate
        {
            DocumentId = Guid.NewGuid(),
            TargetPath = @"C:\private\target.mapproj",
            CandidatePath = @"C:\private\candidate.mapproj",
            UpdatedUtc = DateTimeOffset.UnixEpoch,
            Title = "recoverable",
        });
        store.OnInspect = _ => Inspection(ProjectFileStatus.Corrupted, "FILE_CORRUPTED", "Проект повреждён");

        Assert.Null(await dashboard.TryOpenAsync(@"C:\private\bad.mapproj"));
        Assert.Single(dashboard.RecoveryCards);
    }

    private static (
        DashboardViewModel Dashboard,
        FakeProjectStore Store,
        FakeRecentMaps Recent,
        FakePrompts Prompts,
        FakeFilePicker Picker,
        FakeLog Log) Create()
    {
        var store = new FakeProjectStore();
        var recent = new FakeRecentMaps();
        var prompts = new FakePrompts();
        var picker = new FakeFilePicker();
        var log = new FakeLog();
        return (new DashboardViewModel(store, recent, log, picker, prompts),
            store, recent, prompts, picker, log);
    }

    private static ProjectFileInspection Ready(string title) => new()
    {
        Status = ProjectFileStatus.Ready,
        Code = "FILE_READY",
        Title = "Проект готов к открытию",
        Message = "Проверка файла завершена.",
        SourceSchema = 2,
        Project = Opened(title),
    };

    private static ProjectFileInspection MigrationRequired() => new()
    {
        Status = ProjectFileStatus.MigrationRequired,
        Code = "FILE_MIGRATION_REQUIRED",
        Title = "Нужно обновить копию проекта",
        Message = "Исходный файл останется без изменений.",
        SourceSchema = 1,
        TargetSchema = 2,
    };

    private static ProjectFileInspection Inspection(
        ProjectFileStatus status,
        string code,
        string title) => new()
    {
        Status = status,
        Code = code,
        Title = title,
        Message = "Безопасное диагностическое сообщение.",
    };

    private static OpenedProject Opened(string title) => new()
    {
        Document = TestData.Document(title),
        AppVersion = "test",
        ModifiedUtc = DateTimeOffset.UnixEpoch,
        PreviewPng = [1, 2, 3],
    };
}
