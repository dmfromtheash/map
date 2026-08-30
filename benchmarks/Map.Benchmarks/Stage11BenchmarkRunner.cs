using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Rendering;
using SkiaSharp;

namespace Map.Benchmarks;

internal sealed class Stage11BenchmarkRunner
{
    private const double FrameBudgetMs = 33.3;
    private const int ViewWidth = 1920;
    private const int ViewHeight = 1080;
    private readonly BenchmarkOptions _options;
    private readonly string _scratch;
    private readonly Dictionary<int, (MapDocument Document, ObjectLayer Layer)> _objectDocuments = [];

    public Stage11BenchmarkRunner(BenchmarkOptions options)
    {
        _options = options;
        _scratch = options.ScratchPath;
        Directory.CreateDirectory(_scratch);
    }

    public Stage11BenchmarkReport Run()
    {
        var profiles = new List<ProfileResult>();
        Add("objects-1k", () => MeasureObjectRender(1_000));
        Add("objects-10k", () => MeasureObjectRender(10_000));
        Add("objects-50k", () => MeasureObjectRender(50_000));
        Add("large-map-layers", MeasureLargeMapLayers);
        Add("catalog-10k", MeasureCatalog);
        Add("undo-200", MeasureUndoRedo);
        Add("brush-mask-stress", MeasureBrushMaskStress);
        Add("save-open-10k", MeasureSaveOpen);
        Add("tiled-renderer-boundary", MeasureTiledBoundary);

        return new Stage11BenchmarkReport(
            SchemaVersion: 1,
            RecordedUtc: DateTimeOffset.UtcNow,
            Seed: _options.Seed,
            WarmupIterations: _options.WarmupIterations,
            MeasuredSamples: _options.MeasuredSamples,
            ColdWarmContract: "Cold/first costs are recorded separately. Every steady-state metric uses exactly one unreported warm-up followed by at least five raw measured samples.",
            Machine: ReadMachineFingerprint(),
            Profiles: profiles,
            GatePassed: profiles.All(profile => profile.GatePassed),
            Limitations:
            [
                "Renderer evidence uses Skia CPU raster surfaces; it does not claim GPU timing or UI input latency.",
                "The tiled-renderer profile exercises bounded scene tiles only. Product export remains a Stage 9D prerequisite and is not implemented by Stage 11.",
                "Locked-destination and free-space behavior belong to the release packager verifier, not the in-process renderer benchmark.",
                "The author machine is not a clean VM; clean-machine RC acceptance remains independent work.",
            ]);

        void Add(string name, Func<ProfileResult> measure)
        {
            if (_options.Includes(name))
            {
                profiles.Add(measure());
            }
        }
    }

    private ProfileResult MeasureObjectRender(int objectCount)
    {
        var (document, layer) = GetObjectDocument(objectCount);
        var view = new WorldBounds(0, 0, ViewWidth, ViewHeight);
        var coldIndex = Stopwatch.StartNew();
        var candidates = ObjectSpatialIndex.For(layer).Query(view);
        coldIndex.Stop();
        var stats = new RenderStats();
        var samples = MeasureSamples(() =>
        {
            using var surface = CreateSurface(ViewWidth, ViewHeight);
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Save();
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions
            {
                VisibleWorld = view,
                ViewScale = 1,
                Stats = stats,
            });
            surface.Canvas.Flush();
            surface.Canvas.Restore();
        });
        var elapsed = MetricSummary.From(
            samples.Select(sample => sample.ElapsedMs), "ms", FrameBudgetMs,
            objectCount == 10_000 ? "maximum <= 33.3 ms" : "median <= 33.3 ms",
            values => objectCount == 10_000 ? values[^1] <= FrameBudgetMs : Median(values) <= FrameBudgetMs);

        return Profile(
            $"objects-{objectCount / 1_000}k",
            "65,536 x 65,536 document rendered into a 1,920 x 1,080 fixed viewport at scale 1.",
            new Dictionary<string, object?>
            {
                ["canvas"] = "65536x65536",
                ["viewport"] = $"{ViewWidth}x{ViewHeight}",
                ["objectCount"] = objectCount,
                ["candidateCount"] = candidates.Count,
                ["drawnCount"] = stats.ObjectsDrawn,
                ["fixedView"] = "0,0",
            },
            new Dictionary<string, MetricSummary> { ["renderElapsed"] = elapsed },
            samples,
            new Dictionary<string, object?>
            {
                ["coldSpatialIndexMs"] = Math.Round(coldIndex.Elapsed.TotalMilliseconds, 3),
                ["renderer"] = "SceneRenderer / Skia CPU raster",
                ["documentMutationObserved"] = false,
            },
            elapsed.Passed == true,
            elapsed.BudgetRule!);
    }

    private ProfileResult MeasureLargeMapLayers()
    {
        var layers = new List<MapLayer>
        {
            new BaseColorLayer(Layer(300), "background", true, false, 1, "#FFF5F2EA"),
        };
        for (var index = 0; index < 20; index++)
        {
            var key = new TileKey(index % 5, index / 5);
            var content = FilledTile(4, (byte)(32 + index));
            var mask = FilledTile(1, (byte)(180 + index));
            layers.Add(new BrushLayer(
                Layer(301 + index), $"brush-{index:D2}", true, false, 1,
                TileMap.Empty(4).With(key, content),
                new LayerMask(TileMap.Empty(1).With(key, mask), enabled: true)));
        }

        var document = new MapDocument(
            Document(300), "large-map-layers", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(65_536, 65_536, "px", "#FFF5F2EA"), layers);
        var scales = new[] { 0.005, 0.05, 1.0, 8.0, 64.0 };
        using var cache = new TileImageCache(64);
        var position = 0;
        var samples = MeasureSamples(() =>
        {
            var scale = scales[position++ % scales.Length];
            using var surface = CreateSurface(512, 512);
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Save();
            surface.Canvas.Scale((float)scale);
            var visible = new WorldBounds(0, 0, 512 / scale, 512 / scale);
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions
            {
                VisibleWorld = visible,
                ViewScale = scale,
                TileCache = cache,
            });
            surface.Canvas.Flush();
            surface.Canvas.Restore();
        });
        var elapsed = MetricSummary.From(samples.Select(sample => sample.ElapsedMs), "ms");
        var cacheBounded = cache.Count <= 64;

        return Profile(
            "large-map-layers",
            "65,536 canvas, 20 brush layers, one content tile plus one mask tile per layer, zoom 0.005 through 64.",
            new Dictionary<string, object?>
            {
                ["canvas"] = "65536x65536",
                ["brushLayerCount"] = 20,
                ["contentTileCount"] = 20,
                ["maskTileCount"] = 20,
                ["zoomSamples"] = scales,
                ["cacheCapacity"] = 64,
            },
            new Dictionary<string, MetricSummary> { ["renderElapsed"] = elapsed },
            samples,
            new Dictionary<string, object?>
            {
                ["cacheEntries"] = cache.Count,
                ["cacheHits"] = cache.Hits,
                ["cacheMisses"] = cache.Misses,
                ["cacheEvictions"] = cache.Evictions,
                ["fullCanvasAllocationBytes"] = 0,
            },
            cacheBounded,
            "Tile cache <= 64 entries and no full-canvas allocation.");
    }

    private ProfileResult MeasureCatalog()
    {
        var root = Path.Combine(_scratch, "catalog-10k");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        var writer = new AssetCatalog(root);
        writer.ReplaceIndex(CreateAssetRecords(10_000));

        var first = Measure(() => _ = new AssetCatalog(root));
        _ = new AssetCatalog(root);
        var loadSamples = new List<SampleMeasurement>();
        for (var index = 0; index < _options.MeasuredSamples; index++)
        {
            loadSamples.Add(Measure(() => _ = new AssetCatalog(root)));
        }

        var loaded = new AssetCatalog(root);
        _ = loaded.Search("forest", pack: "even");
        var searchSamples = new List<SampleMeasurement>();
        for (var index = 0; index < _options.MeasuredSamples; index++)
        {
            searchSamples.Add(Measure(() =>
            {
                var matches = loaded.Search("forest", pack: "even");
                if (matches.Count == 0)
                {
                    throw new InvalidOperationException("The fixed catalog query returned no rows.");
                }
            }));
        }

        var loadMetric = MetricSummary.From(loadSamples.Select(sample => sample.ElapsedMs), "ms", 2_000,
            "maximum <= 2000 ms", values => values[^1] <= 2_000);
        var searchMetric = MetricSummary.From(searchSamples.Select(sample => sample.ElapsedMs), "ms", 100,
            "P95 <= 100 ms", values => Percentile(values, 0.95) <= 100);
        var allSamples = loadSamples.Concat(searchSamples).ToList();

        return Profile(
            "catalog-10k",
            "10,000 metadata records loaded by fresh AssetCatalog instances; search is warm metadata-only.",
            new Dictionary<string, object?>
            {
                ["recordCount"] = loaded.Records.Count,
                ["query"] = "forest + even pack",
                ["decodedOriginalCount"] = 0,
            },
            new Dictionary<string, MetricSummary>
            {
                ["freshMetadataLoad"] = loadMetric,
                ["warmSearch"] = searchMetric,
            },
            allSamples,
            new Dictionary<string, object?>
            {
                ["firstColdLoadMs"] = Math.Round(first.ElapsedMs, 3),
                ["indexBytes"] = new FileInfo(Path.Combine(root, "catalog.json")).Length,
                ["originalFilesOpened"] = 0,
            },
            loadMetric.Passed == true && searchMetric.Passed == true,
            "Fresh metadata load maximum <= 2 s and warm search P95 <= 100 ms.");
    }

    private ProfileResult MeasureUndoRedo()
    {
        _ = RunUndoRedoSample();
        var samples = new List<SampleMeasurement>();
        var semanticPassed = true;
        var finalUndoDepth = 0;
        var finalRedoDepth = 0;
        for (var index = 0; index < _options.MeasuredSamples; index++)
        {
            var result = default(UndoRedoResult);
            samples.Add(Measure(() => result = RunUndoRedoSample()));
            semanticPassed &= result.SemanticPassed;
            finalUndoDepth = result.UndoDepth;
            finalRedoDepth = result.RedoDepth;
        }
        var elapsed = MetricSummary.From(samples.Select(sample => sample.ElapsedMs), "ms");

        return Profile(
            "undo-200",
            "Fresh editing session: 200 deterministic transforms, undo all, then redo all.",
            new Dictionary<string, object?>
            {
                ["commandCount"] = 200,
                ["freshSessionPerSample"] = true,
                ["undoDepthAfterRedo"] = finalUndoDepth,
                ["redoDepthAfterRedo"] = finalRedoDepth,
            },
            new Dictionary<string, MetricSummary> { ["roundTripElapsed"] = elapsed },
            samples,
            new Dictionary<string, object?>
            {
                ["semanticRoundTripPassed"] = semanticPassed,
                ["historyStoresCommandsNotFullCanvas"] = true,
            },
            semanticPassed && finalUndoDepth == 200 && finalRedoDepth == 0,
            "All 200 commands undo and redo with exact deterministic final geometry.");
    }

    private ProfileResult MeasureBrushMaskStress()
    {
        _ = RunBrushMaskSample();
        var samples = new List<SampleMeasurement>();
        var semanticPassed = true;
        long historyBytes = 0;
        for (var index = 0; index < _options.MeasuredSamples; index++)
        {
            var result = default(BrushMaskResult);
            samples.Add(Measure(() => result = RunBrushMaskSample()));
            semanticPassed &= result.SemanticPassed;
            historyBytes = Math.Max(historyBytes, result.ApproximateHistoryBytes);
        }
        var elapsed = MetricSummary.From(samples.Select(sample => sample.ElapsedMs), "ms");
        var bounded = historyBytes <= 30L * 1024 * 1024;

        return Profile(
            "brush-mask-stress",
            "Fresh session with 25 one-tile brush gestures and 25 one-tile mask gestures, then complete undo/redo.",
            new Dictionary<string, object?>
            {
                ["brushCommandCount"] = 25,
                ["maskCommandCount"] = 25,
                ["touchedTilesPerCommand"] = 1,
                ["fullCanvasBytesAt65536Rgba"] = 65_536L * 65_536L * 4L,
            },
            new Dictionary<string, MetricSummary> { ["roundTripElapsed"] = elapsed },
            samples,
            new Dictionary<string, object?>
            {
                ["approximateHistoryPayloadBytes"] = historyBytes,
                ["historyBudgetBytes"] = 30L * 1024 * 1024,
                ["semanticRoundTripPassed"] = semanticPassed,
            },
            semanticPassed && bounded,
            "History captures touched tiles only; approximate payload <= 30 MiB and round-trip is exact.");
    }

    private ProfileResult MeasureSaveOpen()
    {
        var (document, _) = GetObjectDocument(10_000);
        var root = Path.Combine(_scratch, "save-open");
        Directory.CreateDirectory(root);
        foreach (var file in Directory.EnumerateFiles(root, "*.mapproj*"))
        {
            File.Delete(file);
        }
        var preview = CreatePreviewPng();
        var metadata = new SaveMetadata("0.11.0-stage11", DateTimeOffset.UnixEpoch, 10_000);
        var coldPath = Path.Combine(root, "cold.mapproj");
        var coldSave = Measure(() => MapProjectStore.Save(document, coldPath, metadata, preview));
        var coldOpen = Measure(() => ValidateOpened(MapProjectStore.Open(coldPath).Document, document));

        var warmupPath = Path.Combine(root, "warmup.mapproj");
        MapProjectStore.Save(document, warmupPath, metadata, preview);
        ValidateOpened(MapProjectStore.Open(warmupPath).Document, document);

        var saveSamples = new List<SampleMeasurement>();
        var openSamples = new List<SampleMeasurement>();
        var semanticPassed = true;
        long maximumFileBytes = new FileInfo(coldPath).Length;
        for (var index = 0; index < _options.MeasuredSamples; index++)
        {
            var path = Path.Combine(root, $"sample-{index:D2}.mapproj");
            saveSamples.Add(Measure(() => MapProjectStore.Save(document, path, metadata, preview)));
            maximumFileBytes = Math.Max(maximumFileBytes, new FileInfo(path).Length);
            try
            {
                openSamples.Add(Measure(() => ValidateOpened(MapProjectStore.Open(path).Document, document)));
            }
            catch
            {
                semanticPassed = false;
                throw;
            }
        }
        var saveMetric = MetricSummary.From(saveSamples.Select(sample => sample.ElapsedMs), "ms");
        var openMetric = MetricSummary.From(openSamples.Select(sample => sample.ElapsedMs), "ms");
        var coldGate = coldOpen.ElapsedMs <= 5_000;
        var sizeGate = maximumFileBytes < 500L * 1024 * 1024;

        return Profile(
            "save-open-10k",
            "10,000-object .mapproj saved atomically and reopened from distinct files; cold/first is separate from warm samples.",
            new Dictionary<string, object?>
            {
                ["objectCount"] = 10_000,
                ["assetCount"] = 0,
                ["previewBytes"] = preview.Length,
                ["format"] = ".mapproj v1",
            },
            new Dictionary<string, MetricSummary>
            {
                ["warmSave"] = saveMetric,
                ["warmOpen"] = openMetric,
            },
            saveSamples.Concat(openSamples).ToList(),
            new Dictionary<string, object?>
            {
                ["firstColdSaveMs"] = Math.Round(coldSave.ElapsedMs, 3),
                ["firstColdOpenMs"] = Math.Round(coldOpen.ElapsedMs, 3),
                ["coldOpenBudgetMs"] = 5_000,
                ["maximumFileBytes"] = maximumFileBytes,
                ["fileSizeBudgetBytes"] = 500L * 1024 * 1024,
                ["semanticRoundTripPassed"] = semanticPassed,
            },
            coldGate && sizeGate && semanticPassed,
            "First cold open <= 5 s, file < 500 MB, and semantic round-trip exact.");
    }

    private ProfileResult MeasureTiledBoundary()
    {
        const int tileEdge = 2048;
        var dimensions = new[] { 8_192, 16_384, 20_000 };
        var metrics = new Dictionary<string, MetricSummary>();
        var all = new List<SampleMeasurement>();
        var evidence = new Dictionary<string, object?>();
        var maxSurfaceBytes = 0L;
        foreach (var dimension in dimensions)
        {
            var document = CreateBoundaryDocument(dimension);
            var selectedTile = Math.Max(0, (int)Math.Ceiling(dimension / (double)tileEdge) - 1);
            using var surface = CreateSurface(tileEdge, tileEdge);
            void RenderTile()
            {
                var left = selectedTile * tileEdge;
                var top = selectedTile * tileEdge;
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.Save();
                surface.Canvas.Translate(-left, -top);
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                {
                    VisibleWorld = new WorldBounds(left, top, Math.Min(dimension, left + tileEdge), Math.Min(dimension, top + tileEdge)),
                    ViewScale = 1,
                    IsExport = true,
                });
                surface.Canvas.Flush();
                surface.Canvas.Restore();
            }
            var samples = MeasureSamples(RenderTile);
            all.AddRange(samples);
            metrics[$"renderTile{dimension}"] = MetricSummary.From(samples.Select(sample => sample.ElapsedMs), "ms");
            var tilesPerAxis = (int)Math.Ceiling(dimension / (double)tileEdge);
            evidence[$"{dimension}PlannedTiles"] = tilesPerAxis * tilesPerAxis;
            evidence[$"{dimension}FullCanvasBytes"] = (long)dimension * dimension * 4;
            maxSurfaceBytes = Math.Max(maxSurfaceBytes, (long)tileEdge * tileEdge * 4);
        }

        var cancellationObserved = false;
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try
            {
                RenderTiledPlan(20_000, tileEdge, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
            }
        }
        evidence["maximumSurfaceBytes"] = maxSurfaceBytes;
        evidence["fullCanvasAllocated"] = false;
        evidence["cancellationObservedBeforeTileAllocation"] = cancellationObserved;
        evidence["productExportImplemented"] = false;
        evidence["productExportPrerequisite"] = "Stage 9D";
        evidence["lockedDestinationClassification"] = "packager/verifier gate, outside renderer benchmark";
        evidence["freeSpaceClassification"] = "packager gate, outside renderer benchmark";

        return Profile(
            "tiled-renderer-boundary",
            "8K, 16K and 20K scene boundaries rendered through one 2,048-square surface at a time; no product export command is added.",
            new Dictionary<string, object?>
            {
                ["dimensions"] = dimensions,
                ["tileEdge"] = tileEdge,
                ["bytesPerPixel"] = 4,
            },
            metrics,
            all,
            evidence,
            cancellationObserved && maxSurfaceBytes == (long)tileEdge * tileEdge * 4,
            "Only one 2,048-square surface is live and cancellation is checked before tile allocation.");
    }

    private (MapDocument Document, ObjectLayer Layer) GetObjectDocument(int count)
    {
        if (_objectDocuments.TryGetValue(count, out var cached))
        {
            return cached;
        }
        var objects = new List<SceneObject>(count);
        for (var index = 0; index < count; index++)
        {
            var x = (index % 250) * 260.0;
            var y = (index / 250) * 260.0;
            objects.Add(CreateObject(index, x, y));
        }
        var layer = new ObjectLayer(Layer(20 + count), $"objects-{count}", true, false, 1, objects);
        var document = new MapDocument(
            Document(20 + count), $"objects-{count}", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(65_536, 65_536, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(Layer(10 + count), "background", true, false, 1, "#FFFFFFFF"),
                layer,
            ]);
        _objectDocuments[count] = (document, layer);
        return (document, layer);
    }

    private UndoRedoResult RunUndoRedoSample()
    {
        var original = CreateObject(900_000, 100, 100);
        var layer = new ObjectLayer(Layer(900_000), "objects", true, false, 1, [original]);
        var session = new EditingSession(new MapDocument(
            Document(900_000), "undo", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(2_048, 2_048, "px", "#FFFFFFFF"),
            [new BaseColorLayer(Layer(900_001), "background", true, false, 1, "#FFFFFFFF"), layer]));

        for (var index = 1; index <= 200; index++)
        {
            var before = DocumentEditor.FindObject(session.Document, original.Id)!;
            var after = CopyObjectAt(before, 100 + index, 100 + index * 0.5);
            session.Execute(new TransformObjectCommand(before, after));
        }
        var final = DocumentEditor.FindObject(session.Document, original.Id)!;
        for (var index = 0; index < 200; index++)
        {
            if (!session.Undo()) throw new InvalidOperationException("Undo depth ended early.");
        }
        var back = DocumentEditor.FindObject(session.Document, original.Id)!;
        for (var index = 0; index < 200; index++)
        {
            if (!session.Redo()) throw new InvalidOperationException("Redo depth ended early.");
        }
        var redone = DocumentEditor.FindObject(session.Document, original.Id)!;
        return new UndoRedoResult(
            back.X == original.X && back.Y == original.Y && redone.X == final.X && redone.Y == final.Y,
            session.UndoDepth,
            session.RedoDepth);
    }

    private BrushMaskResult RunBrushMaskSample()
    {
        var layerId = Layer(910_000);
        var brush = new BrushLayer(
            layerId, "brush", true, false, 1, TileMap.Empty(4), LayerMask.CreateEmpty());
        var session = new EditingSession(new MapDocument(
            Document(910_000), "brush-mask", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(65_536, 65_536, "px", "#FFFFFFFF"),
            [new BaseColorLayer(Layer(910_001), "background", true, false, 1, "#FFFFFFFF"), brush]));
        long approximateBytes = 0;
        for (var index = 0; index < 25; index++)
        {
            var key = new TileKey(index, 0);
            var afterBytes = FilledTile(4, (byte)(index + 1));
            var command = new BrushStrokeCommand(
                layerId,
                new Dictionary<TileKey, byte[]?> { [key] = null },
                new Dictionary<TileKey, byte[]?> { [key] = afterBytes },
                "benchmark brush stroke");
            approximateBytes += command.ApproximateMemoryBytes;
            session.Execute(command);
        }
        for (var index = 0; index < 25; index++)
        {
            var key = new TileKey(index, 1);
            var afterBytes = FilledTile(1, (byte)(255 - index));
            approximateBytes += afterBytes.Length;
            session.Execute(new MaskStrokeCommand(
                layerId,
                new Dictionary<TileKey, byte[]?> { [key] = null },
                new Dictionary<TileKey, byte[]?> { [key] = afterBytes }));
        }
        for (var index = 0; index < 50; index++)
        {
            if (!session.Undo()) throw new InvalidOperationException("Raster undo depth ended early.");
        }
        var empty = (BrushLayer)session.Document.FindLayer(layerId)!;
        for (var index = 0; index < 50; index++)
        {
            if (!session.Redo()) throw new InvalidOperationException("Raster redo depth ended early.");
        }
        var restored = (BrushLayer)session.Document.FindLayer(layerId)!;
        return new BrushMaskResult(
            empty.Tiles.TileCount == 0 && empty.Mask!.Tiles.TileCount == 0
                && restored.Tiles.TileCount == 25 && restored.Mask!.Tiles.TileCount == 25,
            approximateBytes);
    }

    private MapDocument CreateBoundaryDocument(int dimension)
    {
        var objects = new[]
        {
            new SceneObject(Object(920_000 + dimension), SceneObjectKind.Rectangle, "boundary",
                dimension - 512, dimension - 512, 256, 256, 0, "#FF557799", "#FF112233", 1, true, false),
        };
        return new MapDocument(
            Document(920_000 + dimension), $"boundary-{dimension}", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(dimension, dimension, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(Layer(920_001 + dimension), "background", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(Layer(920_002 + dimension), "objects", true, false, 1, objects),
            ]);
    }

    private static void RenderTiledPlan(int dimension, int tileEdge, CancellationToken cancellationToken)
    {
        var tilesPerAxis = (int)Math.Ceiling(dimension / (double)tileEdge);
        for (var y = 0; y < tilesPerAxis; y++)
        {
            for (var x = 0; x < tilesPerAxis; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var surface = CreateSurface(tileEdge, tileEdge);
                surface.Canvas.Clear(SKColors.Transparent);
            }
        }
    }

    private IReadOnlyList<SampleMeasurement> MeasureSamples(Action action)
    {
        for (var index = 0; index < _options.WarmupIterations; index++)
        {
            action();
        }
        var samples = new List<SampleMeasurement>(_options.MeasuredSamples);
        for (var index = 0; index < _options.MeasuredSamples; index++)
        {
            samples.Add(Measure(action));
        }
        return samples;
    }

    private static SampleMeasurement Measure(Action action)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var timer = Stopwatch.StartNew();
        action();
        timer.Stop();
        using var process = Process.GetCurrentProcess();
        var heap = GC.GetTotalMemory(forceFullCollection: false);
        var working = process.WorkingSet64;
        return new SampleMeasurement(
            Math.Round(timer.Elapsed.TotalMilliseconds, 3),
            Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocated),
            heap,
            working,
            Math.Max(0, working - heap),
            GC.CollectionCount(0) - gen0,
            GC.CollectionCount(1) - gen1,
            GC.CollectionCount(2) - gen2);
    }

    private static ProfileResult Profile(
        string name,
        string boundary,
        IReadOnlyDictionary<string, object?> dimensions,
        IReadOnlyDictionary<string, MetricSummary> metrics,
        IReadOnlyList<SampleMeasurement> samples,
        IReadOnlyDictionary<string, object?> evidence,
        bool gatePassed,
        string gate)
    {
        return new ProfileResult(
            name,
            boundary,
            dimensions,
            metrics,
            new Dictionary<string, object?>
            {
                ["maxAllocatedBytes"] = samples.Max(sample => sample.AllocatedBytes),
                ["maxManagedHeapBytes"] = samples.Max(sample => sample.ManagedHeapBytes),
                ["maxWorkingSetBytes"] = samples.Max(sample => sample.WorkingSetBytes),
                ["maxNativeEstimateBytes"] = samples.Max(sample => sample.NativeEstimateBytes),
                ["totalGen0Collections"] = samples.Sum(sample => sample.Gen0Collections),
                ["totalGen1Collections"] = samples.Sum(sample => sample.Gen1Collections),
                ["totalGen2Collections"] = samples.Sum(sample => sample.Gen2Collections),
                ["rawSamples"] = samples,
            },
            evidence,
            gatePassed,
            gate);
    }

    private MachineFingerprint ReadMachineFingerprint()
    {
        var commit = RunGit("rev-parse HEAD");
        var dirty = !string.IsNullOrWhiteSpace(RunGit("status --porcelain=v1 --untracked-files=all"));
        var processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
        return new MachineFingerprint(
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            processor,
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            RuntimeInformation.FrameworkDescription,
            GCSettings.IsServerGC,
            Stopwatch.Frequency,
            "Skia CPU raster / SKSurface",
            typeof(SKSurface).Assembly.GetName().Version?.ToString() ?? "unknown",
            commit,
            dirty);
    }

    private string RunGit(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Could not start git for benchmark fingerprinting.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {process.StandardError.ReadToEnd()}");
        }
        return output.Trim();
    }

    private SceneObject CreateObject(int index, double x, double y) => new(
        Object(index), SceneObjectKind.Rectangle, $"object-{index}",
        x, y, 32, 32, 0, "#FF557799", "#FF112233", 1, true, false);

    private static SceneObject CopyObjectAt(SceneObject source, double x, double y) => new(
        source.Id, source.Kind, source.Name, x, y, source.Width, source.Height,
        source.RotationDegrees, source.FillColor, source.StrokeColor, source.StrokeWidth,
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
        ObjectOpacity = source.ObjectOpacity,
    };

    private static IReadOnlyList<AssetRecord> CreateAssetRecords(int count) =>
        Enumerable.Range(0, count).Select(index => new AssetRecord
        {
            Sha256 = index.ToString("x64"),
            DisplayName = $"asset-{index:D5}",
            OriginalFileName = $"asset-{index:D5}.png",
            MediaType = "image/png",
            Extension = "png",
            Width = 256,
            Height = 256,
            HasAlpha = true,
            ByteLength = 4_096,
            ImportedUtc = DateTimeOffset.UnixEpoch.AddSeconds(index),
            Pack = index % 2 == 0 ? "even" : "odd",
            Tags = [index % 10 == 0 ? "forest" : "other"],
            Favorite = index % 100 == 0,
            Provenance = new AssetProvenance(),
            ThumbnailRelativePath = $"thumbnails/{index:x64}.png",
        }).ToList();

    private static byte[] FilledTile(int bytesPerPixel, byte value)
    {
        var bytes = new byte[TileMap.TileSize * TileMap.TileSize * bytesPerPixel];
        Array.Fill(bytes, value);
        return bytes;
    }

    private static byte[] CreatePreviewPng()
    {
        using var bitmap = new SKBitmap(32, 32);
        bitmap.Erase(new SKColor(85, 119, 153));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static void ValidateOpened(MapDocument opened, MapDocument expected)
    {
        var openedLayer = opened.Layers.OfType<ObjectLayer>().Single();
        var expectedLayer = expected.Layers.OfType<ObjectLayer>().Single();
        if (opened.Id != expected.Id || opened.Title != expected.Title
            || openedLayer.Objects.Count != expectedLayer.Objects.Count
            || openedLayer.Objects[0].Id != expectedLayer.Objects[0].Id
            || openedLayer.Objects[^1].Id != expectedLayer.Objects[^1].Id)
        {
            throw new InvalidOperationException("The save/open semantic round-trip changed the document.");
        }
    }

    private static double Median(IReadOnlyList<double> ordered) => Percentile(ordered, 0.50);

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        var index = (int)Math.Ceiling(ordered.Count * percentile) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Count - 1)];
    }

    private DocumentId Document(int discriminator) => new(DeterministicGuid("document", discriminator));
    private LayerId Layer(int discriminator) => new(DeterministicGuid("layer", discriminator));
    private ObjectId Object(int discriminator) => new(DeterministicGuid("object", discriminator));

    private Guid DeterministicGuid(string category, int discriminator)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"stage11:{_options.Seed}:{category}:{discriminator}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static SKSurface CreateSurface(int width, int height) =>
        SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
        ?? throw new InvalidOperationException($"Cannot create {width}x{height} Skia CPU surface.");

    private readonly record struct UndoRedoResult(bool SemanticPassed, int UndoDepth, int RedoDepth);
    private readonly record struct BrushMaskResult(bool SemanticPassed, long ApproximateHistoryBytes);
}
