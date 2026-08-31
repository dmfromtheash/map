using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

internal sealed class Stage13PerformanceRunner
{
    private static readonly WorldBounds SmallViewport = new(0, 0, 1_920, 1_080);
    private readonly PerformanceOptions _options;
    private readonly Stage13Fixture _fixture;

    public Stage13PerformanceRunner(PerformanceOptions options)
    {
        _options = options;
        _fixture = Stage13Fixtures.Simple(options.ObjectCount, options.Seed);
    }

    public PerformanceReport Run()
    {
        IReadOnlyList<ProfileResult> profiles = _options.Profile switch
        {
            "stage13a" =>
            [
                MeasureSmallViewportRender(),
                MeasurePointHitTest(),
                MeasureMarquee(),
                MeasureObjectTree(),
                MeasureWarmSpatialQuery(),
            ],
            "stage13b-baseline" =>
            [
                MeasureOneObjectEdit(enforceGate: false),
                MeasureLastObjectSelection(enforceGate: false),
                MeasureWarmSpatialQuery(enforceGate: false),
                MeasureColdDocumentIndexBuild(enforceGate: false),
                MeasureColdIndexBuild(enforceGate: false),
                .. MeasureCollectionStrategies(),
                MeasureStressEdit(enforceGate: false),
            ],
            "stage13b" =>
            [
                MeasureOneObjectEdit(enforceGate: true),
                MeasureLastObjectSelection(enforceGate: true),
                MeasureWarmSpatialQuery(enforceGate: true),
                MeasureColdDocumentIndexBuild(enforceGate: true),
                MeasureColdIndexBuild(enforceGate: true),
                .. MeasureCollectionStrategies(),
                MeasureStressEdit(enforceGate: true),
            ],
            "stage13c-baseline" =>
            [
                MeasureOverview(_fixture, "full-overview-simple-exact", enforceGate: false),
                MeasureOverview(Stage13Fixtures.Mixed50K(_options.Seed),
                    "full-overview-mixed-exact", enforceGate: false),
                MeasureNormalMixed(enforceGate: false),
                MeasurePictureReplay(_fixture, "picture-replay-simple"),
                MeasurePictureReplay(Stage13Fixtures.Mixed50K(_options.Seed), "picture-replay-mixed"),
            ],
            "stage13c" =>
            [
                MeasureOverview(_fixture, "full-overview-simple-lod", enforceGate: true, useLod: true),
                MeasureOverview(Stage13Fixtures.Mixed50K(_options.Seed),
                    "full-overview-mixed-lod", enforceGate: true, useLod: true),
                MeasureNormalMixed(enforceGate: true),
                MeasureOverviewInvalidation(),
                MeasureDrawAtlasProbe(),
                MeasureVerticesProbe(),
            ],
            "small-render" => [MeasureSmallViewportRender()],
            "hit-test" => [MeasurePointHitTest()],
            "marquee" => [MeasureMarquee()],
            "object-tree" => [MeasureObjectTree()],
            "spatial-query" => [MeasureWarmSpatialQuery()],
            _ => throw new ArgumentException($"Unknown Stage 13 profile: {_options.Profile}"),
        };

        return new PerformanceReport(
            SchemaVersion: 1,
            RecordedUtc: DateTimeOffset.UtcNow,
            Options: _options,
            Machine: ReadMachineFingerprint(),
            Fixture: _fixture.Manifest,
            Profiles: profiles,
            GatePassed: profiles.All(result => result.Passed),
            Limitations:
            [
                "Stage 13A CPU render timing uses a fixed Skia raster surface; native input-to-present is a separate hidden-window smoke.",
                "Draw-call counters are semantic renderer operations; native Skia may fuse or expand them internally.",
                "100K is a record-only stress boundary; thresholds in this report apply to the requested object count only.",
                "DrawAtlas is measured only for compatible repeated bitmaps; the assets-free mixed fixture has no decoded stamp atlas input.",
                "SKVertices is measured only as static triangles and is rejected for editable text/curves that first require tessellation.",
            ]);
    }

    private ProfileResult MeasureSmallViewportRender()
    {
        using var surface = CreateSurface(1_920, 1_080);
        var stats = new RenderStats();
        void Render()
        {
            surface.Canvas.Clear(SKColors.Transparent);
            SceneRenderer.Render(surface.Canvas, _fixture.Document, new RenderOptions
            {
                VisibleWorld = SmallViewport,
                ViewScale = 1,
                Stats = stats,
            });
            surface.Canvas.Flush();
        }

        var timing = Measure(Render);
        return new ProfileResult(
            "small-viewport-render",
            "P95 <= 8 ms",
            timing.P95 <= 8,
            timing,
            new Dictionary<string, object?>
            {
                ["totalObjects"] = _fixture.Manifest.ObjectCount,
                ["visibleCandidates"] = stats.ObjectCandidates,
                ["exactDrawnObjects"] = stats.ObjectsDrawn,
                ["drawOperations"] = stats.ObjectsDrawn,
                ["renderer"] = "Skia CPU raster / SKSurface",
            });
    }

    private ProfileResult MeasurePointHitTest()
    {
        var target = _fixture.PointTarget;
        SceneObject? hit = null;
        var timing = Measure(() =>
            hit = SceneGeometry.HitTest(
                _fixture.Document,
                target.X + (target.Width / 2),
                target.Y + (target.Height / 2)));
        if (hit?.Id != target.Id)
        {
            throw new InvalidOperationException("Point hit-test did not return the deterministic top object.");
        }

        return new ProfileResult(
            "point-hit-test",
            "warm P95 <= 2 ms",
            timing.P95 <= 2,
            timing,
            new Dictionary<string, object?>
            {
                ["totalObjects"] = _fixture.Manifest.ObjectCount,
                ["targetObjectId"] = target.Id.ToString(),
            });
    }

    private ProfileResult MeasureMarquee()
    {
        IReadOnlyList<SceneObject> hits = [];
        var timing = Measure(() =>
            hits = MultiSelectionGeometry.ObjectsInMarquee(_fixture.Document, _fixture.Marquee));
        if (hits.Count == 0)
        {
            throw new InvalidOperationException("The deterministic marquee returned no objects.");
        }

        return new ProfileResult(
            "small-region-marquee",
            "P95 <= 4 ms",
            timing.P95 <= 4,
            timing,
            new Dictionary<string, object?>
            {
                ["totalObjects"] = _fixture.Manifest.ObjectCount,
                ["exactMatches"] = hits.Count,
                ["bounds"] = _fixture.Marquee.ToString(),
            });
    }

    private ProfileResult MeasureObjectTree()
    {
        IReadOnlyList<ObjectTreeRow> rows = [];
        var timing = Measure(() =>
            rows = ObjectTreeRows.Build(
                _fixture.Document,
                searchText: "",
                ObjectTypeFilter.All,
                openGroupId: null,
                selectedIds: Array.Empty<ObjectId>()));
        var expectedRows = _fixture.Manifest.ObjectCount
            - _fixture.Manifest.GroupedObjectCount
            + _fixture.Manifest.GroupCount;
        if (rows.Count != expectedRows)
        {
            throw new InvalidOperationException(
                $"Object tree returned {rows.Count} rows instead of {expectedRows}.");
        }

        // Model construction is measured here. Realized Avalonia rows are measured by the native
        // hidden-window profile because controls do not exist in this CPU runner.
        return new ProfileResult(
            "object-tree-model",
            "P95 <= 40 ms model build; native realized controls < 500",
            timing.P95 <= 40,
            timing,
            new Dictionary<string, object?>
            {
                ["totalObjects"] = _fixture.Manifest.ObjectCount,
                ["modelRows"] = rows.Count,
                ["groupRows"] = _fixture.Manifest.GroupCount,
                ["realizedUiRows"] = null,
            });
    }

    private ProfileResult MeasureWarmSpatialQuery(bool enforceGate = false)
    {
        var index = ObjectSpatialIndex.For(_fixture.ObjectLayer);
        IReadOnlyList<SceneObject> candidates = [];
        var timing = Measure(() => candidates = index.Query(SmallViewport));
        return new ProfileResult(
            "warm-spatial-query",
            enforceGate ? "warm P95 <= 1 ms" : "record-only in Stage 13A; Stage 13B target P95 <= 1 ms",
            Passed: !enforceGate || timing.P95 <= 1,
            timing,
            new Dictionary<string, object?>
            {
                ["totalObjects"] = _fixture.Manifest.ObjectCount,
                ["candidateCount"] = candidates.Count,
                ["cellCount"] = index.CellCount,
                ["oversizedCount"] = index.OversizedCount,
            });
    }

    private ProfileResult MeasureOneObjectEdit(bool enforceGate)
    {
        var before = _fixture.PointTarget;
        var after = MultiSelectionGeometry.Translate([before], 1, 1)[0];
        _ = ObjectSpatialIndex.For(_fixture.ObjectLayer);
        MapDocument? edited = null;
        var timing = Measure(() =>
            edited = new ReplaceObjectsCommand([before], [after], "Stage 13B probe")
                .Apply(_fixture.Document));
        var actual = DocumentEditor.FindObject(edited!, before.Id);
        if (actual != after)
        {
            throw new InvalidOperationException("The deterministic one-object edit was not exact.");
        }

        const string gate = "P95 <= 8 ms and allocation P95 <= 512 KiB";
        var editedLayer = edited!.Layers.OfType<ObjectLayer>().Single();
        var editedSpatial = ObjectSpatialIndex.For(editedLayer);
        return new ProfileResult(
            "one-object-edit-50k",
            enforceGate ? gate : "record-only baseline; final gate: " + gate,
            !enforceGate || timing.P95 <= 8 && timing.AllocationP95 <= 512 * 1024,
            timing,
            new Dictionary<string, object?>
            {
                ["totalObjects"] = _fixture.Manifest.ObjectCount,
                ["targetObjectId"] = before.Id.ToString(),
                ["resultObjectCount"] = edited!.Layers.OfType<ObjectLayer>().Sum(layer => layer.Objects.Count),
                ["incrementalSpatialOverrides"] = editedSpatial.IncrementalOverrideCount,
            });
    }

    private ProfileResult MeasureLastObjectSelection(bool enforceGate)
    {
        SceneObject? selected = null;
        var timing = Measure(() =>
            selected = DocumentEditor.FindObject(_fixture.Document, _fixture.PointTarget.Id));
        if (selected?.Id != _fixture.PointTarget.Id)
        {
            throw new InvalidOperationException("The last deterministic object was not found.");
        }

        const string gate = "warm P95 <= 2 ms";
        return new ProfileResult(
            "last-object-selection-50k",
            enforceGate ? gate : "record-only baseline; final gate: " + gate,
            !enforceGate || timing.P95 <= 2,
            timing,
            new Dictionary<string, object?>
            {
                ["totalObjects"] = _fixture.Manifest.ObjectCount,
                ["targetObjectId"] = selected.Id.ToString(),
            });
    }

    private ProfileResult MeasureColdIndexBuild(bool enforceGate)
    {
        var stress = Stage13Fixtures.Simple(100_000, _options.Seed);
        var layers = Enumerable.Range(0, _options.WarmupIterations + _options.MeasuredIterations)
            .Select(_ => new ObjectLayer(
                stress.ObjectLayer.Id,
                stress.ObjectLayer.Name,
                stress.ObjectLayer.Visible,
                stress.ObjectLayer.Locked,
                stress.ObjectLayer.Opacity,
                stress.ObjectLayer.Objects,
                stress.ObjectLayer.Mask))
            .ToArray();
        var cursor = 0;
        var timing = Measure(() =>
        {
            var index = ObjectSpatialIndex.For(layers[cursor++]);
            if (index.ObjectCount != 100_000)
            {
                throw new InvalidOperationException("The cold index lost deterministic objects.");
            }
        });

        const string gate = "cold 100K <= 50 ms";
        return new ProfileResult(
            "cold-spatial-index-100k",
            enforceGate ? gate : "record-only baseline; final gate: " + gate,
            !enforceGate || timing.Max <= 50,
            timing,
            new Dictionary<string, object?>
            {
                ["totalObjects"] = 100_000,
                ["distinctLayerSnapshots"] = layers.Length,
            });
    }

    private ProfileResult MeasureColdDocumentIndexBuild(bool enforceGate)
    {
        var stress = Stage13Fixtures.Simple(100_000, _options.Seed);
        var documents = Enumerable.Range(0, _options.WarmupIterations + _options.MeasuredIterations)
            .Select(_ => new MapDocument(
                stress.Document.Id,
                stress.Document.Title,
                stress.Document.CreatedUtc,
                stress.Document.Canvas,
                stress.Document.Layers,
                stress.Document.Assets,
                stress.Document.Notes,
                stress.Document.Checkpoints))
            .ToArray();
        var cursor = 0;
        var timing = Measure(() =>
        {
            var index = DocumentRevisionIndex.For(documents[cursor++]);
            if (index.ObjectCount != 100_000)
            {
                throw new InvalidOperationException("The cold document index lost deterministic objects.");
            }
        });

        const string gate = "cold revision index 100K <= 50 ms";
        return new ProfileResult(
            "cold-document-index-100k",
            enforceGate ? gate : "record-only baseline; final gate: " + gate,
            !enforceGate || timing.Max <= 50,
            timing,
            new Dictionary<string, object?>
            {
                ["totalObjects"] = 100_000,
                ["distinctDocumentSnapshots"] = documents.Length,
            });
    }

    private IReadOnlyList<ProfileResult> MeasureCollectionStrategies()
    {
        var source = _fixture.ObjectLayer.Objects;
        var replacement = MultiSelectionGeometry.Translate([source[^1]], 1, 0)[0];

        IReadOnlyList<SceneObject>? current = null;
        var currentEdit = Measure(() =>
            current = [.. source.Take(source.Count - 1), replacement]);
        var currentIteration = Measure(() => Consume(current!));

        var chunkedSource = new ProbeChunkedList(source);
        IReadOnlyList<SceneObject>? chunked = null;
        var chunkedEdit = Measure(() => chunked = chunkedSource.Replace(source.Count - 1, replacement));
        var chunkedIteration = Measure(() => Consume(chunked!));

        var segmentedSource = new ProbeSegmentList(source, replacementIndex: -1, replacement: null);
        IReadOnlyList<SceneObject>? segmented = null;
        var segmentedEdit = Measure(() =>
            segmented = segmentedSource.Replace(source.Count - 1, replacement));
        var segmentedIteration = Measure(() => Consume(segmented!));

        return
        [
            CollectionResult("collection-current-flat-copy", currentEdit, currentIteration,
                "full reference-array copy"),
            CollectionResult("collection-chunked-256", chunkedEdit, chunkedIteration,
                "copy one 256-reference chunk plus bounded directory"),
            CollectionResult("collection-shared-segment", segmentedEdit, segmentedIteration,
                "share source and overlay one replacement"),
        ];
    }

    private static ProfileResult CollectionResult(
        string name, MetricSummary edit, MetricSummary iteration, string structure) =>
        new(
            name,
            "comparison probe; not an acceptance gate",
            Passed: true,
            edit,
            new Dictionary<string, object?>
            {
                ["structure"] = structure,
                ["iterationP50Ms"] = iteration.P50,
                ["iterationP95Ms"] = iteration.P95,
                ["iterationAllocationP95"] = iteration.AllocationP95,
            });

    private ProfileResult MeasureStressEdit(bool enforceGate)
    {
        var stress = Stage13Fixtures.Simple(100_000, _options.Seed);
        var before = stress.PointTarget;
        var after = MultiSelectionGeometry.Translate([before], 1, 0)[0];
        MapDocument? edited = null;
        var timing = Measure(() =>
            edited = new ReplaceObjectsCommand([before], [after], "Stage 13B 100K stress")
                .Apply(stress.Document));
        var exact = DocumentEditor.FindObject(edited!, before.Id) == after;
        return new ProfileResult(
            "one-object-edit-100k-stress",
            "no crash/resource-limit; exact result; time record-only",
            !enforceGate || exact,
            timing,
            new Dictionary<string, object?>
            {
                ["totalObjects"] = 100_000,
                ["exactResult"] = exact,
            });
    }

    private static double _consume;

    private ProfileResult MeasureOverview(
        Stage13Fixture fixture, string name, bool enforceGate, bool useLod = false)
    {
        const int width = 1_280;
        const int height = 720;
        var scale = Math.Min(
            width / (double)fixture.Manifest.CanvasWidth,
            height / (double)fixture.Manifest.CanvasHeight);
        using var surface = CreateSurface(width, height);
        var stats = new RenderStats();
        using var objectCache = useLod ? new ObjectOverviewCache() : null;
        void Render()
        {
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Save();
            surface.Canvas.Translate(
                (float)((width - (fixture.Manifest.CanvasWidth * scale)) / 2),
                (float)((height - (fixture.Manifest.CanvasHeight * scale)) / 2));
            surface.Canvas.Scale((float)scale);
            SceneRenderer.Render(surface.Canvas, fixture.Document, new RenderOptions
            {
                VisibleWorld = new WorldBounds(
                    0, 0, fixture.Manifest.CanvasWidth, fixture.Manifest.CanvasHeight),
                ViewScale = scale,
                Stats = stats,
                LodTier = useLod ? RenderLodTier.Overview : RenderLodTier.Exact,
                ObjectCache = objectCache,
            });
            surface.Canvas.Restore();
            surface.Canvas.Flush();
        }

        var timing = Measure(Render);
        const string gate = "P95 <= 33.3 ms";
        return new ProfileResult(
            name,
            enforceGate ? gate : "record-only Stage 13C baseline; final gate: " + gate,
            !enforceGate || timing.P95 <= 33.3 && timing.Max <= 100,
            timing,
            new Dictionary<string, object?>
            {
                ["fixtureHash"] = fixture.Manifest.Hash,
                ["totalObjects"] = fixture.Manifest.ObjectCount,
                ["kindCounts"] = fixture.Manifest.KindCounts,
                ["expensiveEffects"] = fixture.Manifest.ExpensiveEffectCount,
                ["visibleCandidates"] = stats.ObjectCandidates,
                ["objectsDrawn"] = stats.ObjectsDrawn,
                ["missingAssets"] = stats.MissingAssets,
                ["viewScale"] = scale,
                ["exactObjects"] = stats.ExactObjects,
                ["overviewObjects"] = stats.OverviewObjects,
                ["overviewChunks"] = stats.OverviewChunks,
                ["overviewBatches"] = stats.OverviewBatches,
                ["drawCalls"] = stats.DrawCalls,
                ["cacheHits"] = objectCache?.Hits ?? 0,
                ["cacheMisses"] = objectCache?.Misses ?? 0,
                ["cacheInvalidations"] = objectCache?.Invalidations ?? 0,
                ["cacheItems"] = objectCache?.Count ?? 0,
                ["cacheBytes"] = objectCache?.Bytes ?? 0,
                ["lod"] = useLod ? "Overview" : "Exact",
            });
    }

    private ProfileResult MeasureNormalMixed(bool enforceGate)
    {
        var fixture = Stage13Fixtures.Mixed50K(_options.Seed);
        const int width = 1_280;
        const int height = 720;
        using var surface = CreateSurface(width, height);
        var stats = new RenderStats();
        var viewport = new WorldBounds(9_600, 9_600, 9_600 + width, 9_600 + height);
        void Render()
        {
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Save();
            surface.Canvas.Translate((float)-viewport.Left, (float)-viewport.Top);
            SceneRenderer.Render(surface.Canvas, fixture.Document, new RenderOptions
            {
                VisibleWorld = viewport,
                ViewScale = 1,
                Stats = stats,
            });
            surface.Canvas.Restore();
            surface.Canvas.Flush();
        }

        var timing = Measure(Render);
        const string gate = "P95 <= 16.7 ms";
        return new ProfileResult(
            "normal-zoom-mixed-exact",
            enforceGate ? gate : "record-only Stage 13C baseline; final gate: " + gate,
            !enforceGate || timing.P95 <= 16.7 && timing.Max <= 100,
            timing,
            new Dictionary<string, object?>
            {
                ["fixtureHash"] = fixture.Manifest.Hash,
                ["totalObjects"] = fixture.Manifest.ObjectCount,
                ["visibleCandidates"] = stats.ObjectCandidates,
                ["objectsDrawn"] = stats.ObjectsDrawn,
                ["missingAssets"] = stats.MissingAssets,
                ["exactObjects"] = stats.ExactObjects,
                ["drawCalls"] = stats.ObjectsDrawn,
                ["stampInstances"] = stats.StampInstances,
                ["repeatedAssetInstances"] = stats.RepeatedAssetInstances,
                ["textObjects"] = stats.TextObjects,
                ["vectorObjects"] = stats.VectorObjects,
                ["compositionObjects"] = stats.CompositionObjects,
                ["effectObjects"] = stats.EffectObjects,
                ["visibleWorld"] = viewport.ToString(),
                ["surface"] = $"{width}x{height}",
                ["lod"] = "Exact",
            });
    }

    private ProfileResult MeasurePictureReplay(Stage13Fixture fixture, string name)
    {
        const int width = 1_280;
        const int height = 720;
        var scale = Math.Min(
            width / (double)fixture.Manifest.CanvasWidth,
            height / (double)fixture.Manifest.CanvasHeight);
        using var recorder = new SKPictureRecorder();
        var recording = recorder.BeginRecording(new SKRect(0, 0, width, height), useRTree: true);
        recording.Translate(
            (float)((width - (fixture.Manifest.CanvasWidth * scale)) / 2),
            (float)((height - (fixture.Manifest.CanvasHeight * scale)) / 2));
        recording.Scale((float)scale);
        SceneRenderer.Render(recording, fixture.Document, new RenderOptions
        {
            VisibleWorld = new WorldBounds(
                0, 0, fixture.Manifest.CanvasWidth, fixture.Manifest.CanvasHeight),
            ViewScale = scale,
        });
        using var picture = recorder.EndRecording();
        using var surface = CreateSurface(width, height);
        var timing = Measure(() =>
        {
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.DrawPicture(picture);
            surface.Canvas.Flush();
        });
        return new ProfileResult(
            name,
            "isolated replay probe; not an acceptance gate",
            Passed: true,
            timing,
            new Dictionary<string, object?>
            {
                ["fixtureHash"] = fixture.Manifest.Hash,
                ["approximateOperations"] = picture.ApproximateOperationCount,
                ["approximateBytes"] = picture.ApproximateBytesUsed,
                ["rtreeRecording"] = true,
            });
    }

    private ProfileResult MeasureOverviewInvalidation()
    {
        const int width = 1_280;
        const int height = 720;
        var fixture = Stage13Fixtures.Simple(50_000, _options.Seed);
        var scale = Math.Min(
            width / (double)fixture.Manifest.CanvasWidth,
            height / (double)fixture.Manifest.CanvasHeight);
        using var surface = CreateSurface(width, height);
        var cache = new ObjectOverviewCache(itemBudget: 512, byteBudget: 64L * 1024 * 1024);
        var stats = new RenderStats();

        void Render(MapDocument document)
        {
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Save();
            surface.Canvas.Scale((float)scale);
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions
            {
                VisibleWorld = new WorldBounds(0, 0,
                    fixture.Manifest.CanvasWidth, fixture.Manifest.CanvasHeight),
                ViewScale = scale,
                LodTier = RenderLodTier.Overview,
                ObjectCache = cache,
                Stats = stats,
            });
            surface.Canvas.Restore();
            surface.Canvas.Flush();
        }

        Render(fixture.Document);
        Render(fixture.Document);
        var target = fixture.PointTarget;
        var replacement = MultiSelectionGeometry.Translate([target], 1, 1)[0];
        var revised = new ReplaceObjectsCommand([target], [replacement], "Stage 13C cache probe")
            .Apply(fixture.Document);
        var missesBefore = cache.Misses;
        var invalidationsBefore = cache.Invalidations;
        var timing = Measure(() => Render(revised));
        var misses = cache.Misses - missesBefore;
        var invalidations = cache.Invalidations - invalidationsBefore;
        var crossed = MultiSelectionGeometry.Translate([replacement], 5_000, 0)[0];
        var crossedDocument = new ReplaceObjectsCommand(
            [replacement], [crossed], "Stage 13C old/new chunk probe").Apply(revised);
        var crossMissesBefore = cache.Misses;
        var crossInvalidationsBefore = cache.Invalidations;
        var crossTiming = Measure(() => Render(crossedDocument));
        var crossMisses = cache.Misses - crossMissesBefore;
        var crossInvalidations = cache.Invalidations - crossInvalidationsBefore;
        var peakItems = cache.Count;
        var peakBytes = cache.Bytes;
        cache.Dispose();
        var disposedItems = cache.Count;
        var disposedBytes = cache.Bytes;

        return new ProfileResult(
            "overview-one-object-invalidation",
            "same-chunk edit invalidates <= 1; cross-chunk edit invalidates <= 2; dispose is empty",
            invalidations <= 1 && misses <= 1
                && crossInvalidations <= 2 && crossMisses <= 2
                && disposedItems == 0 && disposedBytes == 0,
            crossTiming,
            new Dictionary<string, object?>
            {
                ["cacheMissesAfterRevision"] = misses,
                ["cacheInvalidationsAfterRevision"] = invalidations,
                ["cacheMissesAfterCrossChunkMove"] = crossMisses,
                ["cacheInvalidationsAfterCrossChunkMove"] = crossInvalidations,
                ["peakItems"] = peakItems,
                ["peakBytes"] = peakBytes,
                ["itemsAfterDispose"] = disposedItems,
                ["bytesAfterDispose"] = disposedBytes,
                ["disposals"] = cache.Disposals,
            });
    }

    private ProfileResult MeasureDrawAtlasProbe()
    {
        const int instanceCount = 8_000;
        using var sourceSurface = CreateSurface(8, 8);
        sourceSurface.Canvas.Clear(new SKColor(210, 80, 60, 255));
        using var source = sourceSurface.Snapshot();
        var sourceRects = Enumerable.Repeat(new SKRect(0, 0, 8, 8), instanceCount).ToArray();
        var transforms = Enumerable.Range(0, instanceCount)
            .Select(index => SKRotationScaleMatrix.CreateTranslation(
                (index % 160) * 8,
                (index / 160) * 8))
            .ToArray();
        using var atlasSurface = CreateSurface(1_280, 400);
        using var paint = new SKPaint { IsAntialias = false };
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        void DrawAtlas()
        {
            atlasSurface.Canvas.Clear(SKColors.Transparent);
            atlasSurface.Canvas.DrawAtlas(source, sourceRects, transforms, sampling, paint);
            atlasSurface.Canvas.Flush();
        }

        var atlasTiming = Measure(DrawAtlas);
        using var loopSurface = CreateSurface(1_280, 400);
        var loopTiming = Measure(() =>
        {
            loopSurface.Canvas.Clear(SKColors.Transparent);
            for (var index = 0; index < instanceCount; index++)
            {
                var left = (index % 160) * 8;
                var top = (index / 160) * 8;
                loopSurface.Canvas.DrawImage(source, sourceRects[index],
                    new SKRect(left, top, left + 8, top + 8), sampling, paint);
            }
            loopSurface.Canvas.Flush();
        });
        DrawAtlas();
        using var atlasImage = atlasSurface.Snapshot();
        using var loopImage = loopSurface.Snapshot();
        using var atlasData = atlasImage.Encode(SKEncodedImageFormat.Png, 100);
        using var loopData = loopImage.Encode(SKEncodedImageFormat.Png, 100);
        var equalOutput = SHA256.HashData(atlasData.ToArray())
            .SequenceEqual(SHA256.HashData(loopData.ToArray()));

        return new ProfileResult(
            "draw-atlas-compatible-stamps-probe",
            "isolated candidate probe; selected only for decoded same-image compatible effects",
            Passed: true,
            atlasTiming,
            new Dictionary<string, object?>
            {
                ["instances"] = instanceCount,
                ["loopP95Ms"] = loopTiming.P95,
                ["samePixelOutput"] = equalOutput,
                ["decision"] = "not used by assets-free mixed fixture; retain as compatible stamp path candidate",
            });
    }

    private ProfileResult MeasureVerticesProbe()
    {
        const int quadCount = 10_000;
        var vertices = new SKPoint[quadCount * 6];
        var colors = new SKColor[vertices.Length];
        for (var index = 0; index < quadCount; index++)
        {
            var left = (index % 200) * 6f;
            var top = (index / 200) * 6f;
            var offset = index * 6;
            vertices[offset] = new SKPoint(left, top);
            vertices[offset + 1] = new SKPoint(left + 4, top);
            vertices[offset + 2] = new SKPoint(left + 4, top + 4);
            vertices[offset + 3] = new SKPoint(left, top);
            vertices[offset + 4] = new SKPoint(left + 4, top + 4);
            vertices[offset + 5] = new SKPoint(left, top + 4);
            Array.Fill(colors, new SKColor(85, 119, 153, 255), offset, 6);
        }

        using var surface = CreateSurface(1_200, 300);
        using var paint = new SKPaint { IsAntialias = false };
        var timing = Measure(() =>
        {
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.DrawVertices(SKVertexMode.Triangles, vertices, colors, paint);
            surface.Canvas.Flush();
        });
        return new ProfileResult(
            "vertices-static-triangles-probe",
            "isolated candidate probe; rejected for heterogeneous editable scene primitives",
            Passed: true,
            timing,
            new Dictionary<string, object?>
            {
                ["quads"] = quadCount,
                ["vertices"] = vertices.Length,
                ["managedGeometryBytes"] = (long)vertices.Length * 8 + (long)colors.Length * 4,
                ["decision"] = "rejected: text, curves, effects and compositions are not static triangulated geometry",
            });
    }

    private static void Consume(IReadOnlyList<SceneObject> objects)
    {
        var sum = 0.0;
        for (var index = 0; index < objects.Count; index++)
        {
            sum += objects[index].X;
        }

        _consume = sum;
    }

    private MetricSummary Measure(Action action)
    {
        for (var index = 0; index < _options.WarmupIterations; index++)
        {
            action();
        }

        var samples = new List<Sample>(_options.MeasuredIterations);
        for (var index = 0; index < _options.MeasuredIterations; index++)
        {
            var workingSetBefore = Environment.WorkingSet;
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();
            samples.Add(new Sample(
                stopwatch.Elapsed.TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
                GC.CollectionCount(0) - gen0,
                GC.CollectionCount(1) - gen1,
                GC.CollectionCount(2) - gen2,
                workingSetBefore,
                Environment.WorkingSet));
        }

        return MetricSummary.From(samples);
    }

    private static MachineFingerprint ReadMachineFingerprint()
    {
        var root = RunGit("rev-parse --show-toplevel");
        return new MachineFingerprint(
            root,
            RunGit("branch --show-current"),
            RunGit("rev-parse HEAD"),
            !string.IsNullOrWhiteSpace(RunGit("status --porcelain=v1 --untracked-files=all")),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
            Environment.ProcessorCount,
            "Skia CPU raster / SKSurface");
    }

    private static string RunGit(string arguments)
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
        }) ?? throw new InvalidOperationException("Could not start git for the performance fingerprint.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {error}");
        }

        return output.Trim();
    }

    private static SKSurface CreateSurface(int width, int height) =>
        SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
        ?? throw new InvalidOperationException($"Cannot create a {width}x{height} Skia surface.");

    private sealed class ProbeChunkedList : IReadOnlyList<SceneObject>
    {
        private const int ChunkSize = 256;
        private readonly SceneObject[][] _chunks;

        public ProbeChunkedList(IReadOnlyList<SceneObject> source)
        {
            Count = source.Count;
            _chunks = new SceneObject[(source.Count + ChunkSize - 1) / ChunkSize][];
            for (var chunkIndex = 0; chunkIndex < _chunks.Length; chunkIndex++)
            {
                var length = Math.Min(ChunkSize, source.Count - (chunkIndex * ChunkSize));
                var chunk = new SceneObject[length];
                for (var offset = 0; offset < length; offset++)
                {
                    chunk[offset] = source[(chunkIndex * ChunkSize) + offset];
                }

                _chunks[chunkIndex] = chunk;
            }
        }

        private ProbeChunkedList(SceneObject[][] chunks, int count)
        {
            _chunks = chunks;
            Count = count;
        }

        public int Count { get; }

        public SceneObject this[int index] => _chunks[index / ChunkSize][index % ChunkSize];

        public ProbeChunkedList Replace(int index, SceneObject replacement)
        {
            var chunks = (SceneObject[][])_chunks.Clone();
            var chunkIndex = index / ChunkSize;
            var chunk = (SceneObject[])chunks[chunkIndex].Clone();
            chunk[index % ChunkSize] = replacement;
            chunks[chunkIndex] = chunk;
            return new ProbeChunkedList(chunks, Count);
        }

        public IEnumerator<SceneObject> GetEnumerator() =>
            _chunks.SelectMany(static chunk => chunk).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ProbeSegmentList(
        IReadOnlyList<SceneObject> source,
        int replacementIndex,
        SceneObject? replacement) : IReadOnlyList<SceneObject>
    {
        public int Count => source.Count;

        public SceneObject this[int index] =>
            index == replacementIndex ? replacement! : source[index];

        public ProbeSegmentList Replace(int index, SceneObject value) => new(source, index, value);

        public IEnumerator<SceneObject> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
