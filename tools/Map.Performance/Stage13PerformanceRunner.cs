using System.Diagnostics;
using System.Runtime.InteropServices;
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
                "The baseline renderer exposes candidate and drawn-object counts but not exact Skia draw-call counts or a chunk-cache state yet.",
                "100K is a record-only stress boundary; thresholds in this report apply to the requested object count only.",
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

    private ProfileResult MeasureWarmSpatialQuery()
    {
        var index = ObjectSpatialIndex.For(_fixture.ObjectLayer);
        IReadOnlyList<SceneObject> candidates = [];
        var timing = Measure(() => candidates = index.Query(SmallViewport));
        return new ProfileResult(
            "warm-spatial-query",
            "record-only in Stage 13A; Stage 13B target P95 <= 1 ms",
            Passed: true,
            timing,
            new Dictionary<string, object?>
            {
                ["totalObjects"] = _fixture.Manifest.ObjectCount,
                ["candidateCount"] = candidates.Count,
                ["cellCount"] = index.CellCount,
                ["oversizedCount"] = index.OversizedCount,
            });
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
}
