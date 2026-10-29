using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Map.App.Services;
using Map.App.Views;
using Map.Core.Documents;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;

namespace Map.Performance;

internal sealed record NativeProjectFileDiagnosticsReport(
    int SchemaVersion,
    DateTimeOffset RecordedUtc,
    string WindowMode,
    bool DashboardVisible,
    bool MigrationPromptVisible,
    bool CancelIsSafeDefault,
    bool MigratedCopyOpenedClean,
    bool SourceUnchanged,
    bool CorruptionWasDistinct,
    bool ResourceLimitWasDistinct,
    bool FutureVersionWasDistinct,
    bool UiResponsive,
    bool WindowClosed,
    bool TempRootRemoved,
    bool GatePassed,
    IReadOnlyList<string> Limitations);

internal static class NativeProjectFileDiagnosticsSmoke
{
    public static NativeProjectFileDiagnosticsReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The native Stage 12D smoke targets Map's Windows runtime.");
        }

        var root = Path.Combine(Path.GetTempPath(), "map-stage12d-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fixture = NativeProjectFileDiagnosticsFixture.Create(root);
        var completion = new TaskCompletionSource<NativeProjectFileDiagnosticsReport>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                NativeProjectFileDiagnosticsApplication.Configure(fixture, completion);
                AppBuilder.Configure<NativeProjectFileDiagnosticsApplication>()
                    .UsePlatformDetect()
                    .WithInterFont()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Map Stage 12D native smoke",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        NativeProjectFileDiagnosticsReport report;
        try
        {
            report = completion.Task.WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        }
        finally
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }

        var closed = !thread.IsAlive;
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reflected in the report; generated smoke state is never mixed with the real profile.
        }
        var removed = !Directory.Exists(root);
        return report with
        {
            WindowClosed = closed,
            TempRootRemoved = removed,
            GatePassed = report.GatePassed && closed && removed,
        };
    }
}

public sealed class NativeProjectFileDiagnosticsApplication : Application
{
    private static NativeProjectFileDiagnosticsFixture? _fixture;
    private static TaskCompletionSource<NativeProjectFileDiagnosticsReport>? _completion;

    internal static void Configure(
        NativeProjectFileDiagnosticsFixture fixture,
        TaskCompletionSource<NativeProjectFileDiagnosticsReport> completion)
    {
        _fixture = fixture;
        _completion = completion;
    }

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || _fixture is null
            || _completion is null)
        {
            throw new InvalidOperationException("Native Stage 12D smoke was not configured.");
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var store = new SmokeStore();
        var recent = new SmokeRecentMaps();
        var picker = new SmokeFilePicker(_fixture.Destination);
        var prompts = new SmokePrompts();
        var log = new SmokeLog();
        var dashboard = new DashboardViewModel(store, recent, log, picker, prompts);
        var view = new DashboardView(
            dashboard,
            static () => Task.CompletedTask,
            static () => Task.CompletedTask,
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask);
        var window = new Window
        {
            Title = "Map Stage 12D native smoke",
            Width = 1_100,
            Height = 720,
            CanResize = false,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-32_000, -32_000),
            Content = view,
        };

        window.Opened += async (_, _) =>
        {
            try
            {
                var report = await ExecuteAsync(window, view, dashboard, store, recent, picker, prompts, log, _fixture);
                _completion.TrySetResult(report);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                window.Close();
                desktop.Shutdown();
            }
        };
        desktop.MainWindow = window;
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task<NativeProjectFileDiagnosticsReport> ExecuteAsync(
        Window window,
        DashboardView view,
        DashboardViewModel dashboard,
        SmokeStore store,
        SmokeRecentMaps recent,
        SmokeFilePicker picker,
        SmokePrompts prompts,
        SmokeLog log,
        NativeProjectFileDiagnosticsFixture fixture)
    {
        view.Refresh();
        await SettleAsync();
        var dashboardVisible = view.IsVisible && window.IsVisible;

        var prompt = new MigrationPromptWindow
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-32_000, -32_000),
        };
        prompt.Show(window);
        await SettleAsync();
        var controls = prompt.GetVisualDescendants().OfType<Button>().ToList();
        var cancel = controls.SingleOrDefault(button => button.Name == "CancelMigrationButton");
        var create = controls.SingleOrDefault(button => button.Name == "CreateMigratedCopyButton");
        var promptVisible = prompt.IsVisible && cancel is not null && create is not null;
        var cancelSafe = cancel is { IsDefault: true, IsCancel: true };
        create!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();

        var sourceHash = SHA256.HashData(File.ReadAllBytes(fixture.LegacySource));
        var sourceTime = File.GetLastWriteTimeUtc(fixture.LegacySource);
        var openResult = await dashboard.TryOpenAsync(fixture.LegacySource);
        var editor = openResult is null
            ? null
            : new EditorViewModel(
                openResult.Project.Document,
                openResult.Path,
                startDirty: false,
                store,
                recent,
                picker,
                prompts,
                log,
                availableFontFamilies: ["Arial"]);
        if (editor is not null)
        {
            window.Content = new EditorView(editor, static () => Task.CompletedTask, static () => { }, static () => { });
            await SettleAsync();
        }
        var openedClean = editor is not null
            && !editor.Session.IsDirty
            && string.Equals(editor.DocumentPath, fixture.Destination, StringComparison.OrdinalIgnoreCase)
            && MapProjectStore.Inspect(fixture.Destination).Status == ProjectFileStatus.Ready;
        var sourceUnchanged = sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture.LegacySource)))
            && sourceTime == File.GetLastWriteTimeUtc(fixture.LegacySource);

        window.Content = view;
        AssertState(await dashboard.TryOpenAsync(fixture.Corrupted), dashboard, ProjectFileStatus.Corrupted);
        var corruptionDistinct = dashboard.LastError.Contains("повреждён", StringComparison.OrdinalIgnoreCase);
        AssertState(await dashboard.TryOpenAsync(fixture.ResourceLimited), dashboard, ProjectFileStatus.ResourceLimit);
        var resourceDistinct = dashboard.LastError.Contains("лимит", StringComparison.OrdinalIgnoreCase);
        AssertState(await dashboard.TryOpenAsync(fixture.Future), dashboard, ProjectFileStatus.UnsupportedVersion);
        var futureDistinct = dashboard.LastError.Contains("более новой", StringComparison.OrdinalIgnoreCase);

        var responsive = false;
        Dispatcher.UIThread.Post(() => responsive = true, DispatcherPriority.Background);
        await SettleAsync();
        var gate = dashboardVisible && promptVisible && cancelSafe && openedClean && sourceUnchanged
            && corruptionDistinct && resourceDistinct && futureDistinct && responsive;
        return new NativeProjectFileDiagnosticsReport(
            1,
            DateTimeOffset.UtcNow,
            "real Avalonia/Win32 hidden own-process window; ShowActivated=false; no system input",
            dashboardVisible,
            promptVisible,
            cancelSafe,
            openedClean,
            sourceUnchanged,
            corruptionDistinct,
            resourceDistinct,
            futureDistinct,
            responsive,
            WindowClosed: false,
            TempRootRemoved: false,
            GatePassed: gate,
            Limitations:
            [
                "The smoke invokes only its own controls and view-model methods.",
                "No foreground activation, global input, clipboard, screen capture or real Map profile is used.",
            ]);
    }

    private static void AssertState(
        DashboardOpenResult? result,
        DashboardViewModel dashboard,
        ProjectFileStatus expected)
    {
        if (result is not null || string.IsNullOrWhiteSpace(dashboard.LastError))
            throw new InvalidOperationException($"Expected {expected} dashboard state.");
    }

    private static async Task SettleAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        await Task.Delay(20);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private sealed class SmokeFilePicker(string destination) : IFilePicker
    {
        public Task<string?> PickOpenAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickSaveAsync(string suggestedFileName) => Task.FromResult<string?>(destination);
    }

    private sealed class SmokePrompts : IUserPrompts
    {
        public Task<bool> ConfirmMigrationCopyAsync() => Task.FromResult(true);
        public Task<bool> ConfirmDeleteProjectAsync(string projectTitle, bool fileAlreadyMissing = false) => Task.FromResult(false);
        public Task<CloseChoice> ConfirmCloseDirtyAsync(string documentTitle) => Task.FromResult(CloseChoice.Cancel);
        public Task<bool> ConfirmRestoreCheckpointAsync(string checkpointName) => Task.FromResult(false);
        public Task<bool> ConfirmDeleteCheckpointAsync(string checkpointName) => Task.FromResult(false);
        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
    }

    private sealed class SmokeLog : IAppLog
    {
        public void Info(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class SmokeRecentMaps : IRecentMaps
    {
        private readonly List<RecentMapItem> _items = [];
        public IReadOnlyList<RecentMapItem> Load() => [.. _items];
        public void Touch(string path, Guid documentId, string title, DateTimeOffset modifiedUtc,
            byte[]? thumbnailPng = null, string mapType = "Локальная карта")
        {
            _items.RemoveAll(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
            _items.Insert(0, new(path, title, modifiedUtc, true));
        }
        public void Remove(string path) =>
            _items.RemoveAll(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class SmokeStore : IProjectStore
    {
        public ProjectFileInspection Inspect(string path) => MapProjectStore.Inspect(path);
        public ProjectMigrationResult MigrateCopy(string sourcePath, string destinationPath) =>
            MapProjectStore.MigrateCopy(sourcePath, destinationPath);
        public OpenedProject Open(string path) => MapProjectStore.Open(path);
        public void Save(MapDocument document, string path, SaveMetadata metadata, byte[] previewPng) =>
            MapProjectStore.Save(document, path, metadata, previewPng);
        public void SaveAutosave(MapDocument document, string? targetPath, SaveMetadata metadata, byte[] previewPng) { }
        public void ClearRecovery(Guid documentId) { }
        public IReadOnlyList<RecoveryCandidate> FindRecoveryCandidates() => [];
        public OpenedProject RestoreRecovery(RecoveryCandidate candidate) => throw new NotSupportedException();
        public void DiscardRecovery(RecoveryCandidate candidate) { }
    }
}

internal sealed record NativeProjectFileDiagnosticsFixture(
    string LegacySource,
    string Destination,
    string Corrupted,
    string ResourceLimited,
    string Future)
{
    public static NativeProjectFileDiagnosticsFixture Create(string root)
    {
        var current = Path.Combine(root, "current-v2.mapproj");
        var source = Path.Combine(root, "legacy-v1.mapproj");
        var destination = Path.Combine(root, "migrated-v2.mapproj");
        var corrupted = Path.Combine(root, "corrupted-v2.mapproj");
        var resource = Path.Combine(root, "resource-v2.mapproj");
        var future = Path.Combine(root, "future-v3.mapproj");
        var document = new MapDocument(
            DocumentId.Parse("11111111-2222-3333-4444-555555555555"),
            "smoke",
            DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FF203040"),
            [
                new BaseColorLayer(
                    LayerId.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
                    "background", true, false, 1, "#FF203040"),
                new ObjectLayer(
                    LayerId.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                    "objects", true, false, 1,
                    [
                        new SceneObject(
                            ObjectId.Parse("01234567-89ab-cdef-0123-456789abcdef"),
                            SceneObjectKind.Rectangle,
                            "object",
                            10, 20, 30, 40, 0,
                            "#FFFFFFFF", "#FF000000", 0, true, false),
                    ]),
            ]);
        MapProjectStore.Save(
            document,
            current,
            new SaveMetadata("stage12d-smoke", DateTimeOffset.UnixEpoch),
            PreviewPng());

        File.Copy(current, source);
        Rewrite(source, (manifest, scene, preview) =>
        {
            manifest["schemaVersion"] = 1;
            var layers = scene["layers"]!.AsArray();
            layers.RemoveAt(0);
            foreach (var layer in layers) layer!.AsObject().Remove("type");
        });
        File.SetLastWriteTimeUtc(source, DateTime.UnixEpoch);

        File.Copy(current, corrupted);
        Rewrite(corrupted, (manifest, scene, preview) => scene["documentId"] = Guid.NewGuid().ToString(),
            updateSceneChecksum: false);

        File.Copy(current, resource);
        Rewrite(resource, (manifest, scene, preview) =>
        {
            BinaryPrimitives.WriteUInt32BigEndian(preview.AsSpan(16, 4), 9_000);
        });

        File.Copy(current, future);
        Rewrite(future, (manifest, scene, preview) => manifest["schemaVersion"] = 3);
        return new(source, destination, corrupted, resource, future);
    }

    private static void Rewrite(
        string path,
        Action<JsonObject, JsonObject, byte[]> mutate,
        bool updateSceneChecksum = true)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var manifestBytes = Read(archive, MapProjectFormat.ManifestEntry);
        var sceneBytes = Read(archive, MapProjectFormat.SceneEntry);
        var previewBytes = Read(archive, MapProjectFormat.PreviewEntry);
        var manifest = JsonNode.Parse(manifestBytes)!.AsObject();
        var scene = JsonNode.Parse(sceneBytes)!.AsObject();
        mutate(manifest, scene, previewBytes);
        sceneBytes = JsonSerializer.SerializeToUtf8Bytes(scene);
        if (updateSceneChecksum)
        {
            manifest["sceneSha256"] = Convert.ToHexStringLower(SHA256.HashData(sceneBytes));
        }
        Replace(archive, MapProjectFormat.SceneEntry, sceneBytes);
        Replace(archive, MapProjectFormat.ManifestEntry, JsonSerializer.SerializeToUtf8Bytes(manifest));
        Replace(archive, MapProjectFormat.PreviewEntry, previewBytes);
    }

    private static byte[] Read(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void Replace(ZipArchive archive, string name, byte[] bytes)
    {
        archive.GetEntry(name)!.Delete();
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = MapProjectFormat.DeterministicEntryTime;
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static byte[] PreviewPng() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89,
    ];
}
