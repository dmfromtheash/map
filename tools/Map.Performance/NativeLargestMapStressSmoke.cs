using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Map.App.Views;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Performance;

internal sealed record NativeLargestMapStressReport(
    int SchemaVersion,
    DateTimeOffset RecordedUtc,
    string WindowMode,
    int CanvasWidth,
    int CanvasHeight,
    int TargetObjects,
    int PlacedObjects,
    int AssetVariants,
    bool CanvasExtentReached,
    IReadOnlyList<double> PlacementBatchMilliseconds,
    double PlacementP50,
    double PlacementP95,
    double PlacementMax,
    double PlacementTotalMilliseconds,
    int LandTargetTiles,
    int LandTiles,
    double LandTotalMilliseconds,
    IReadOnlyList<double> OverviewMilliseconds,
    double OverviewP50,
    double OverviewP95,
    double OverviewMax,
    IReadOnlyList<double> SceneRenderMilliseconds,
    double SceneRenderP50,
    double SceneRenderP95,
    double SceneRenderMax,
    string OverviewTier,
    int OverviewObjects,
    int LogicalObjectRows,
    int RealizedObjectRows,
    long PeakWorkingSetBytes,
    long PeakPrivateBytes,
    long BeforeSaveWorkingSetBytes,
    long BeforeSavePrivateBytes,
    long SavePeakWorkingSetBytes,
    long SavePeakPrivateBytes,
    long AfterReopenWorkingSetBytes,
    long AfterReopenPrivateBytes,
    bool SaveAttempted,
    bool SaveSucceeded,
    double SaveMilliseconds,
    string? SaveFailure,
    bool ReopenSucceeded,
    double ReopenMilliseconds,
    string? ReopenFailure,
    int ReopenedObjects,
    int ReopenedLandTiles,
    string SourceObjectIdentityOrderSha256,
    string ReopenedObjectIdentityOrderSha256,
    bool ObjectIdentityOrderPreserved,
    bool RepresentativeCornersPreserved,
    bool EmbeddedAssetsPreserved,
    double ReopenedOverviewMilliseconds,
    double ReopenedSceneRenderMilliseconds,
    string ReopenedOverviewTier,
    int ReopenedOverviewObjects,
    string ScreenshotPath,
    bool GatePassed,
    IReadOnlyList<string> Limitations);

internal static class NativeLargestMapStressSmoke
{
    public static NativeLargestMapStressReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The largest-map stress profile targets Windows.");
        }

        var completion = new TaskCompletionSource<NativeLargestMapStressReport>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                NativeLargestMapStressApplication.Configure(options, completion);
                AppBuilder.Configure<NativeLargestMapStressApplication>()
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
            Name = "Map largest-map native stress",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            return completion.Task.WaitAsync(TimeSpan.FromMinutes(30)).GetAwaiter().GetResult();
        }
        finally
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}

public sealed class NativeLargestMapStressApplication : Application
{
    private const int CanvasSize = 131_072;
    private const int PlacementBatchSize = 5_000;
    private const int LandPatchSize = 1_024;
    private const int TilesPerLandPatch = 16;

    private static PerformanceOptions? _options;
    private static TaskCompletionSource<NativeLargestMapStressReport>? _completion;

    internal static void Configure(
        PerformanceOptions options,
        TaskCompletionSource<NativeLargestMapStressReport> completion)
    {
        _options = options;
        _completion = completion;
    }

    public override void Initialize()
    {
        var production = new global::Map.App.App();
        production.Initialize();
        RequestedThemeVariant = production.RequestedThemeVariant;
        foreach (var resource in production.Resources)
        {
            Resources[resource.Key] = resource.Value;
        }
        var styles = production.Styles.ToArray();
        production.Styles.Clear();
        Styles.AddRange(styles);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || _options is null
            || _completion is null)
        {
            throw new InvalidOperationException("Largest-map stress was not configured.");
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var workspace = Path.Combine(
            Path.GetTempPath(), "map-largest-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(workspace, "profile"));

        var catalog = new AssetCatalog(Path.Combine(workspace, "catalog"));
        var editor = new EditorViewModel(
            CreateDocument(), null, false, new StressProjectStore(), new StressRecentMaps(),
            new StressFilePicker(), new StressPrompts(), new StressLog(),
            clock: static () => DateTimeOffset.UnixEpoch,
            assetCatalog: catalog,
            availableFontFamilies: ["Arial"]);
        var view = new EditorView(
            editor, static () => Task.CompletedTask, static () => { }, static () => { });
        var window = new Window
        {
            Title = "Map — largest-map stress",
            Width = 1_400,
            Height = 900,
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
                _completion.TrySetResult(await ExecuteAsync(_options, workspace, window));
            }
            catch (Exception exception)
            {
                WriteProgress(_options, "fault", 0, 0, 0, exception.GetType().Name + ": " + exception.Message);
                _completion.TrySetException(exception);
            }
            finally
            {
                if (window.Content is EditorView activeView)
                {
                    activeView.CloseAuxiliaryWindowsForSmoke();
                    activeView.Canvas.DisposeRenderCaches();
                }
                window.Content = null;
                window.Close();
                desktop.Shutdown();
                try
                {
                    Directory.Delete(workspace, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        };
        desktop.MainWindow = window;
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task<NativeLargestMapStressReport> ExecuteAsync(
        PerformanceOptions options,
        string workspace,
        Window window)
    {
        var view = (EditorView)window.Content!;
        var editor = view.Editor;
        await SettleAsync();
        var process = Process.GetCurrentProcess();
        long peakWorkingSet = 0;
        long peakPrivate = 0;
        void SampleMemory()
        {
            process.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
            peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
        }

        var repositoryRoot = FindRepositoryRoot(Environment.CurrentDirectory);
        var assetPaths = new[]
        {
            OwnerMountainFixtures.Path(1, repositoryRoot),
            OwnerMountainFixtures.Path(2, repositoryRoot),
        };
        var assets = assetPaths.Select(path => editor.ImportAsset(path, linked: false)).ToArray();
        if (assets.Any(asset => asset is null))
        {
            throw new InvalidOperationException("The original starter assets could not be imported.");
        }
        var records = assets.Select(asset => asset!).ToArray();

        editor.SelectLayer(editor.Session.Document.BaseColor.Id);
        editor.SetBaseColor("#FF3C5870");
        var landLayers = editor.Session.Document.Layers.OfType<LandLayer>().ToArray();
        editor.SelectLayer(landLayers[0].Id);
        editor.SetActiveLandStyle(style => style with
        {
            FillColor = "#FF6F8F52",
            OutlineColor = "#FF31452A",
            RippleCount = 2,
        });
        editor.SelectLayer(landLayers[1].Id);
        editor.SetActiveLandStyle(style => style with
        {
            FillColor = "#FFA47C4A",
            OutlineColor = "#FF5D4028",
            RippleCount = 0,
        });

        var landTargetTiles = LandTileTarget(options.Profile, options.ObjectCount);
        var landPatchCount = landTargetTiles / TilesPerLandPatch;
        var landWatch = Stopwatch.StartNew();
        for (var patchIndex = 0; patchIndex < landPatchCount; patchIndex++)
        {
            var cellIndex = (patchIndex * 97) % (128 * 128);
            var x = (cellIndex % 128) * LandPatchSize;
            var y = (cellIndex / 128) * LandPatchSize;
            editor.SelectLayer(landLayers[patchIndex % landLayers.Length].Id);
            editor.ActiveTool = EditorTool.Land;
            if (!editor.ApplyLandShape(
                    LandShapeKind.Rectangle,
                    new WorldBounds(x, y, x + LandPatchSize - 0.001, y + LandPatchSize - 0.001)))
            {
                throw new InvalidOperationException($"Land patch {patchIndex} was not applied.");
            }
            if ((patchIndex + 1) % 8 == 0 || patchIndex + 1 == landPatchCount)
            {
                await SettleAsync();
                SampleMemory();
                WriteProgress(options, "land", 0, (patchIndex + 1) * TilesPerLandPatch,
                    landWatch.Elapsed.TotalMilliseconds, null);
            }
        }
        landWatch.Stop();
        var actualLandTiles = editor.Session.Document.Layers.OfType<LandLayer>()
            .Sum(layer => layer.GeometryTiles.TileCount);

        var objectLayer = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
        editor.SelectLayer(objectLayer.Id);
        var edge = CanvasSize - 256d;
        editor.PlaceStamp(records[0], x: 0, y: 0);
        editor.PlaceStamp(records[1], x: edge, y: 0);
        editor.PlaceStamp(records[0], x: 0, y: edge);
        editor.PlaceStamp(records[1], x: edge, y: edge);

        var placementSamples = new List<double>();
        var remaining = options.ObjectCount - 4;
        var placementWatch = Stopwatch.StartNew();
        var batchIndex = 0;
        while (remaining > 0)
        {
            var batch = Math.Min(PlacementBatchSize, remaining);
            var watch = Stopwatch.StartNew();
            editor.ScatterStamp(records[batchIndex % records.Length], batch, options.Seed + batchIndex);
            watch.Stop();
            placementSamples.Add(watch.Elapsed.TotalMilliseconds);
            remaining -= batch;
            batchIndex++;
            if (batchIndex % 5 == 0 || remaining == 0)
            {
                await SettleAsync();
                SampleMemory();
                var placedNow = options.ObjectCount - remaining;
                WriteProgress(options, "objects", placedNow, actualLandTiles,
                    watch.Elapsed.TotalMilliseconds, null);
            }
        }
        placementWatch.Stop();

        var objects = editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects;
        var canvasExtentReached = objects.Count >= 4
            && objects[0].X <= 0.001 && objects[0].Y <= 0.001
            && objects[1].X >= edge - 0.001 && objects[1].Y <= 0.001
            && objects[2].X <= 0.001 && objects[2].Y >= edge - 0.001
            && objects[3].X >= edge - 0.001 && objects[3].Y >= edge - 0.001;

        WriteProgress(options, "overview-start", objects.Count, actualLandTiles, 0, null);
        _ = await MeasureNextFrameAsync(view, view.Canvas.FitToCanvas);
        var overviewSamples = new List<double>(options.MeasuredIterations);
        var sceneRenderSamples = new List<double>(options.MeasuredIterations);
        for (var index = 0; index < options.WarmupIterations; index++)
        {
            _ = await MeasureNextFrameAsync(
                view, () => view.Canvas.PanByScreenDeltaForSmoke(index % 2 == 0 ? 1 : -1, 0));
        }
        for (var index = 0; index < options.MeasuredIterations; index++)
        {
            var frame = await MeasureNextFrameAsync(
                view, () => view.Canvas.PanByScreenDeltaForSmoke(index % 2 == 0 ? 1 : -1, 0));
            overviewSamples.Add(frame.InputToRenderMilliseconds);
            sceneRenderSamples.Add(frame.SceneRenderMilliseconds);
        }

        WriteProgress(options, "objects-panel-start", objects.Count, actualLandTiles, 0, null);
        view.OpenObjects();
        await SettleAsync();
        var logicalRows = view.ObjectListLogicalCount;
        view.ScrollObjectsTo(Math.Max(0, logicalRows - 1));
        await SettleAsync();
        var realizedRows = view.ObjectListRealizedCount;
        SampleMemory();
        WriteProgress(options, "objects-panel-complete", objects.Count, actualLandTiles, 0,
            $"logical={logicalRows}; realized={realizedRows}");

        var sourceDocument = editor.Session.Document;
        var sourceObjectCount = objects.Count;
        var sourceObjectDigest = ObjectIdentityOrderSha256(sourceDocument.Layers);
        var sourceCorners = objects.Take(4).Select(CornerSignature.From).ToArray();
        var sourceAssets = sourceDocument.Assets.Select(AssetSignature.From)
            .OrderBy(asset => asset.Sha256, StringComparer.Ordinal).ToArray();
        var expectedStampAssets = records.Select(record => record.Sha256)
            .OrderBy(sha => sha, StringComparer.Ordinal).ToArray();
        var sourceOverviewTier = view.Canvas.LastLodTier.ToString();
        var sourceOverviewObjects = view.Canvas.LastRenderStats.OverviewObjects;
        var beforeSave = ReadMemory();
        var saveAttempted = true;
        var saveSucceeded = false;
        var saveMilliseconds = 0d;
        string? saveFailure = null;
        var savePath = Path.Combine(workspace, $"largest-{options.ObjectCount}.mapproj");
        WriteProgress(options, "save-start", objects.Count, actualLandTiles, 0, savePath);
        var saveSampler = new ProcessMemorySampler();
        var saveWatch = Stopwatch.StartNew();
        try
        {
            MapProjectStore.Save(
                sourceDocument,
                savePath,
                new SaveMetadata("largest-map-stress", DateTimeOffset.UnixEpoch),
                OnePixelPng());
            saveSucceeded = true;
        }
        catch (Exception exception)
        {
            saveFailure = exception.GetType().Name + ": " + exception.Message;
        }
        finally
        {
            saveWatch.Stop();
            saveMilliseconds = saveWatch.Elapsed.TotalMilliseconds;
            await saveSampler.StopAsync();
        }
        peakWorkingSet = Math.Max(peakWorkingSet, saveSampler.PeakWorkingSetBytes);
        peakPrivate = Math.Max(peakPrivate, saveSampler.PeakPrivateBytes);
        WriteProgress(options, "save-complete", sourceObjectCount, actualLandTiles, saveMilliseconds,
            saveSucceeded ? "saved" : saveFailure);

        // Reopen is deliberately performed only after the source editor/view/document are detached
        // and made collectable. This keeps the post-open memory sample honest and exercises the same
        // native editor surface a user gets after closing and opening the saved project.
        view.CloseAuxiliaryWindowsForSmoke();
        view.Canvas.DisposeRenderCaches();
        window.Content = null;
        sourceDocument = null!;
        objects = null!;
        editor = null!;
        view = null!;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        var reopenSucceeded = false;
        var reopenMilliseconds = 0d;
        string? reopenFailure = null;
        var reopenedObjects = 0;
        var reopenedLandTiles = 0;
        var reopenedObjectDigest = "";
        var objectIdentityOrderPreserved = false;
        var representativeCornersPreserved = false;
        var embeddedAssetsPreserved = false;
        var reopenedOverviewMilliseconds = double.NaN;
        var reopenedSceneRenderMilliseconds = double.NaN;
        var reopenedOverviewTier = "not-rendered";
        var reopenedOverviewObjects = 0;
        var screenshotPath = Path.ChangeExtension(options.OutputPath, ".png");
        var afterReopen = ReadMemory();

        if (saveSucceeded)
        {
            WriteProgress(options, "reopen-start", sourceObjectCount, actualLandTiles, 0, savePath);
            var reopenSampler = new ProcessMemorySampler();
            var reopenWatch = Stopwatch.StartNew();
            OpenedProject? reopenedProject = null;
            try
            {
                reopenedProject = MapProjectStore.Open(savePath);
                reopenSucceeded = true;
            }
            catch (Exception exception)
            {
                reopenFailure = exception.GetType().Name + ": " + exception.Message;
            }
            finally
            {
                reopenWatch.Stop();
                reopenMilliseconds = reopenWatch.Elapsed.TotalMilliseconds;
                await reopenSampler.StopAsync();
            }
            peakWorkingSet = Math.Max(peakWorkingSet, reopenSampler.PeakWorkingSetBytes);
            peakPrivate = Math.Max(peakPrivate, reopenSampler.PeakPrivateBytes);

            if (reopenedProject is not null)
            {
                var reopenedDocument = reopenedProject.Document;
                var reopenedObjectList = reopenedDocument.Layers.OfType<ObjectLayer>().Single().Objects;
                reopenedObjects = reopenedObjectList.Count;
                reopenedLandTiles = reopenedDocument.Layers.OfType<LandLayer>()
                    .Sum(layer => layer.GeometryTiles.TileCount);
                reopenedObjectDigest = ObjectIdentityOrderSha256(reopenedDocument.Layers);
                objectIdentityOrderPreserved = string.Equals(
                    sourceObjectDigest, reopenedObjectDigest, StringComparison.Ordinal);
                representativeCornersPreserved = sourceCorners.SequenceEqual(
                    reopenedObjectList.Take(4).Select(CornerSignature.From));
                embeddedAssetsPreserved = sourceAssets.SequenceEqual(
                        reopenedDocument.Assets.Select(AssetSignature.From)
                            .OrderBy(asset => asset.Sha256, StringComparer.Ordinal))
                    && ExpectedStampAssetsAreReferenced(reopenedObjectList, expectedStampAssets);

                var reopenedEditor = new EditorViewModel(
                    reopenedDocument, savePath, false, new StressProjectStore(), new StressRecentMaps(),
                    new StressFilePicker(), new StressPrompts(), new StressLog(),
                    clock: static () => DateTimeOffset.UnixEpoch,
                    assetCatalog: new AssetCatalog(Path.Combine(workspace, "catalog")),
                    availableFontFamilies: ["Arial"]);
                var reopenedView = new EditorView(
                    reopenedEditor, static () => Task.CompletedTask, static () => { }, static () => { });
                window.Content = reopenedView;
                await SettleAsync();
                var reopenedFrame = await MeasureNextFrameAsync(reopenedView, reopenedView.Canvas.FitToCanvas);
                reopenedOverviewMilliseconds = reopenedFrame.InputToRenderMilliseconds;
                reopenedSceneRenderMilliseconds = reopenedFrame.SceneRenderMilliseconds;
                reopenedOverviewTier = reopenedView.Canvas.LastLodTier.ToString();
                reopenedOverviewObjects = reopenedView.Canvas.LastRenderStats.OverviewObjects;

                Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
                using (var bitmap = new RenderTargetBitmap(new PixelSize(1_400, 900)))
                {
                    bitmap.Render(reopenedView);
                    await using var stream = File.Create(screenshotPath);
                    bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                }
                afterReopen = ReadMemory();
                peakWorkingSet = Math.Max(peakWorkingSet, afterReopen.WorkingSetBytes);
                peakPrivate = Math.Max(peakPrivate, afterReopen.PrivateBytes);
                WriteProgress(options, "reopen-render-complete", reopenedObjects, reopenedLandTiles,
                    reopenMilliseconds,
                    $"digest={reopenedObjectDigest}; tier={reopenedOverviewTier}; screenshot={screenshotPath}");
            }
        }

        SampleMemory();
        var placementOrdered = placementSamples.Order().ToArray();
        var overviewOrdered = overviewSamples.Order().ToArray();
        var sceneRenderOrdered = sceneRenderSamples.Order().ToArray();
        var passed = sourceObjectCount == options.ObjectCount
            && actualLandTiles == landTargetTiles
            && canvasExtentReached
            && double.IsFinite(Percentile(overviewOrdered, 0.95))
            && Percentile(sceneRenderOrdered, 0.95) <= 33.3
            && logicalRows == sourceObjectCount
            && realizedRows is >= 0 and < 500
            && saveSucceeded
            && reopenSucceeded
            && reopenedObjects == options.ObjectCount
            && reopenedLandTiles == landTargetTiles
            && objectIdentityOrderPreserved
            && representativeCornersPreserved
            && embeddedAssetsPreserved
            && double.IsFinite(reopenedSceneRenderMilliseconds);
        WriteProgress(options, "complete", reopenedObjects, reopenedLandTiles,
            placementWatch.Elapsed.TotalMilliseconds, saveFailure ?? reopenFailure);

        return new NativeLargestMapStressReport(
            MapProjectFormat.SchemaVersion,
            DateTimeOffset.UtcNow,
            "real native Avalonia window with production styles; off-screen; ShowActivated=false; no system input",
            CanvasSize,
            CanvasSize,
            options.ObjectCount,
            sourceObjectCount,
            records.Length,
            canvasExtentReached,
            placementSamples,
            placementOrdered.Length == 0 ? 0 : Percentile(placementOrdered, 0.50),
            placementOrdered.Length == 0 ? 0 : Percentile(placementOrdered, 0.95),
            placementOrdered.Length == 0 ? 0 : placementOrdered[^1],
            placementWatch.Elapsed.TotalMilliseconds,
            landTargetTiles,
            actualLandTiles,
            landWatch.Elapsed.TotalMilliseconds,
            overviewSamples,
            Percentile(overviewOrdered, 0.50),
            Percentile(overviewOrdered, 0.95),
            overviewOrdered[^1],
            sceneRenderSamples,
            Percentile(sceneRenderOrdered, 0.50),
            Percentile(sceneRenderOrdered, 0.95),
            sceneRenderOrdered[^1],
            sourceOverviewTier,
            sourceOverviewObjects,
            logicalRows,
            realizedRows,
            peakWorkingSet,
            peakPrivate,
            beforeSave.WorkingSetBytes,
            beforeSave.PrivateBytes,
            saveSampler.PeakWorkingSetBytes,
            saveSampler.PeakPrivateBytes,
            afterReopen.WorkingSetBytes,
            afterReopen.PrivateBytes,
            saveAttempted,
            saveSucceeded,
            saveMilliseconds,
            saveFailure,
            reopenSucceeded,
            reopenMilliseconds,
            reopenFailure,
            reopenedObjects,
            reopenedLandTiles,
            sourceObjectDigest,
            reopenedObjectDigest,
            objectIdentityOrderPreserved,
            representativeCornersPreserved,
            embeddedAssetsPreserved,
            reopenedOverviewMilliseconds,
            reopenedSceneRenderMilliseconds,
            reopenedOverviewTier,
            reopenedOverviewObjects,
            screenshotPath,
            passed,
            [
                "The stress uses Map's real editor/view-model commands and both original starter assets in an isolated profile.",
                "The window is real but intentionally off-screen so it cannot take focus or input from the owner.",
                "The source editor is detached before production Open creates and renders a fresh editor from the saved file.",
                "A full 131K single-channel Land layer would require about 16 GiB before undo/history overhead; this profile increases distributed materialized coverage instead.",
            ]);
    }

    private static MapDocument CreateDocument()
    {
        var green = new LandLayer(
            LayerId.New(), "Зелёная земля", true, false, 1,
            TileMap.Empty(1), new LandEdgeStyle());
        var dry = new LandLayer(
            LayerId.New(), "Сухая земля", true, false, 1,
            TileMap.Empty(1), new LandEdgeStyle());
        var objects = new ObjectLayer(LayerId.New(), "Ассеты", true, false, 1, []);
        return new MapDocument(
            DocumentId.New(), "Largest map stress", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(CanvasSize, CanvasSize, "px", "#FF304A60"),
            [
                new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF304A60"),
                green,
                dry,
                objects,
            ]);
    }

    private static int LandTileTarget(string profile, int objectCount) =>
        profile == "native-largest-map-assets-only" ? 0 : objectCount switch
    {
        <= 65_536 => 1_024,
        <= 262_144 => 4_096,
        <= 1_048_576 => 8_192,
        _ => 16_384,
    };

    private static string ObjectIdentityOrderSha256(IReadOnlyList<MapLayer> layers)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> guidBytes = stackalloc byte[16];
        Span<byte> countBytes = stackalloc byte[4];
        foreach (var layer in layers.OfType<ObjectLayer>())
        {
            layer.Id.Value.TryWriteBytes(guidBytes);
            hash.AppendData(guidBytes);
            BinaryPrimitives.WriteInt32LittleEndian(countBytes, layer.Objects.Count);
            hash.AppendData(countBytes);
            foreach (var sceneObject in layer.Objects)
            {
                sceneObject.Id.Value.TryWriteBytes(guidBytes);
                hash.AppendData(guidBytes);
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool ExpectedStampAssetsAreReferenced(
        IReadOnlyList<SceneObject> objects,
        IReadOnlyList<string> expectedAssetSha256)
    {
        var expected = expectedAssetSha256.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sceneObject in objects)
        {
            if (sceneObject.Stamp?.AssetSha256 is not { } sha || !expected.Contains(sha))
            {
                return false;
            }
            seen.Add(sha);
        }
        return seen.SetEquals(expected);
    }

    private readonly record struct CornerSignature(
        ObjectId Id,
        double X,
        double Y,
        double Width,
        double Height,
        string? AssetSha256)
    {
        public static CornerSignature From(SceneObject sceneObject) => new(
            sceneObject.Id,
            sceneObject.X,
            sceneObject.Y,
            sceneObject.Width,
            sceneObject.Height,
            sceneObject.Stamp?.AssetSha256);
    }

    private readonly record struct AssetSignature(
        string Sha256,
        string DisplayName,
        string MediaType,
        string Extension,
        ProjectAssetStorage Storage,
        string EmbeddedPayloadSha256)
    {
        public static AssetSignature From(MapAssetReference asset) => new(
            asset.Sha256,
            asset.DisplayName,
            asset.MediaType,
            asset.Extension,
            asset.Storage,
            asset.EmbeddedContent is null
                ? ""
                : Convert.ToHexStringLower(SHA256.HashData(asset.EmbeddedContent)));
    }

    private readonly record struct MemorySnapshot(long WorkingSetBytes, long PrivateBytes);

    private static MemorySnapshot ReadMemory()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new MemorySnapshot(process.WorkingSet64, process.PrivateMemorySize64);
    }

    private sealed class ProcessMemorySampler
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _sampling;
        private int _stopped;

        public ProcessMemorySampler()
        {
            Sample();
            _sampling = Task.Run(SampleUntilStoppedAsync);
        }

        public long PeakWorkingSetBytes { get; private set; }
        public long PeakPrivateBytes { get; private set; }

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }
            _cancellation.Cancel();
            try
            {
                await _sampling;
            }
            catch (OperationCanceledException)
            {
            }
            Sample();
            _cancellation.Dispose();
        }

        private async Task SampleUntilStoppedAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                Sample();
                await Task.Delay(25, _cancellation.Token);
            }
        }

        private void Sample()
        {
            var current = ReadMemory();
            PeakWorkingSetBytes = Math.Max(PeakWorkingSetBytes, current.WorkingSetBytes);
            PeakPrivateBytes = Math.Max(PeakPrivateBytes, current.PrivateBytes);
        }
    }

    private static byte[] OnePixelPng()
    {
        using var surface = SKSurface.Create(new SKImageInfo(1, 1))!;
        surface.Canvas.Clear(SKColors.Transparent);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private readonly record struct FrameMeasurement(
        double InputToRenderMilliseconds,
        double SceneRenderMilliseconds);

    private static async Task<FrameMeasurement> MeasureNextFrameAsync(EditorView view, Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(object? sender, EventArgs args) => completion.TrySetResult();
        view.Canvas.FrameRendered += OnFrame;
        try
        {
            var watch = Stopwatch.StartNew();
            action();
            view.Canvas.InvalidateVisual();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
            watch.Stop();
            return new FrameMeasurement(
                watch.Elapsed.TotalMilliseconds,
                view.Canvas.LastSceneRenderMilliseconds);
        }
        finally
        {
            view.Canvas.FrameRendered -= OnFrame;
        }
    }

    private static async Task SettleAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        await Task.Delay(25);
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        if (ordered.Count == 0)
        {
            return double.NaN;
        }
        var index = (int)Math.Ceiling(ordered.Count * percentile) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Count - 1)];
    }

    private static void WriteProgress(
        PerformanceOptions options,
        string stage,
        int objects,
        int landTiles,
        double elapsedMilliseconds,
        string? detail)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var line = JsonSerializer.Serialize(new
        {
            recordedUtc = DateTimeOffset.UtcNow,
            stage,
            objects,
            landTiles,
            elapsedMilliseconds,
            workingSetBytes = process.WorkingSet64,
            privateBytes = process.PrivateMemorySize64,
            detail,
        });
        var path = options.OutputPath + ".progress.jsonl";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, line + Environment.NewLine);
    }

    private static string FindRepositoryRoot(string start)
    {
        var current = new DirectoryInfo(Path.GetFullPath(start));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Map.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException("Map.slnx was not found above the current directory.");
    }

    private sealed class StressFilePicker : IFilePicker
    {
        public Task<string?> PickOpenAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickSaveAsync(string suggestedFileName) => Task.FromResult<string?>(null);
    }

    private sealed class StressPrompts : IUserPrompts
    {
        public Task<bool> ConfirmMigrationCopyAsync() => Task.FromResult(false);
        public Task<bool> ConfirmDeleteProjectAsync(string projectTitle, bool fileAlreadyMissing = false) => Task.FromResult(false);
        public Task<CloseChoice> ConfirmCloseDirtyAsync(string documentTitle) => Task.FromResult(CloseChoice.Cancel);
        public Task<bool> ConfirmRestoreCheckpointAsync(string checkpointName) => Task.FromResult(false);
        public Task<bool> ConfirmDeleteCheckpointAsync(string checkpointName) => Task.FromResult(false);
    }

    private sealed class StressLog : IAppLog
    {
        public void Info(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class StressRecentMaps : IRecentMaps
    {
        public IReadOnlyList<RecentMapItem> Load() => [];
        public void Touch(
            string path, Guid documentId, string title, DateTimeOffset modifiedUtc,
            byte[]? thumbnailPng = null, string mapType = "Локальная карта") { }
        public void Remove(string path) { }
    }

    private sealed class StressProjectStore : IProjectStore
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
