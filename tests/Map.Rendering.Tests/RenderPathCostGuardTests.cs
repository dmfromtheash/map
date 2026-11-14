using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// DEC-111, CPU only. Fixed view [0,512]^2, ten visible objects, one changed object.
/// Fresh target layer/variant per sample: caching an earlier delta must not make a guard green.
/// Nine interleaved pairs, two warmups; construction and witnesses outside the stopwatch.
/// MAP_RENDER_GUARDS_AUDIT=1 measures documented O(N) paths; HUGE=1 adds 1M objects.
/// </summary>
[CollectionDefinition("RenderPathCostGuards", DisableParallelization = true)]
public sealed class RenderPathCostGuardCollection;

[Collection("RenderPathCostGuards")]
public sealed class RenderPathCostGuardTests : IClassFixture<RenderPathCostGuardTests.Maps>
{
    private readonly Maps _maps;
    public RenderPathCostGuardTests(Maps maps) => _maps = maps;
    private const string DenseLinear = "SceneRenderer.cs:6540-6552, overview delta rebuilds every object in the affected 8192-unit chunk, including off-view objects";
    private const string RiverLinear = "RiverRenderer.cs:39-45, live override Resolve copies/scans every river for one local edit";
    private const string RoadLinear = "RoadRenderer.cs:39-45, live override Resolve copies/scans every road for one local edit";
    // The internal plan is test-visible, but the shared plan registry is private. Use the same
    // registry as Render so the constructor really takes its incremental branch. Reflection is
    // done once, outside measurements; no product API is added for a benchmark.
    private static readonly ConditionalWeakTable<ObjectLayer, SceneRenderer.ObjectOverviewPlan> Plans =
        (ConditionalWeakTable<ObjectLayer, SceneRenderer.ObjectOverviewPlan>)typeof(SceneRenderer)
            .GetField("OverviewPlans", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    [Theory]
    [InlineData("overview-delta")]
    [InlineData("visible-scene-conversion")]
    [InlineData("visible-raster-frame")]
    public void Bounded_render_path(string key) => Compare(key, 200_000, true);

    [Fact(Skip = "O(N): " + DenseLinear)]
    public void Dense_overview_delta() => Compare("overview-delta-dense", 200_000, true);

    [Fact(Skip = "O(N): " + RiverLinear)]
    public void River_preview_resolution() => Compare("river-preview", 200_000, true);

    [Fact(Skip = "O(N): " + RoadLinear)]
    public void Road_preview_resolution() => Compare("road-preview", 200_000, true);

    [EnvironmentFact("MAP_RENDER_GUARDS_AUDIT")]
    public void Audit_documented_linear_paths()
    {
        foreach (var key in new[] { "overview-delta-dense", "river-preview", "road-preview" }) Compare(key, 200_000, false);
    }

    [EnvironmentFact("MAP_RENDER_GUARDS_HUGE")]
    public void Million_object_variant()
    {
        foreach (var key in new[] { "overview-delta", "visible-scene-conversion", "visible-raster-frame" }) Compare(key, 1_000_000, true);
    }

    private void Compare(string key, int count, bool enforce)
    {
        var a = _maps.Get(10_000); var b = _maps.Get(count);
        CompareSamples(key, a.N, b.N, () => Prepare(key, a), () => Prepare(key, b), enforce);
    }

    private static Operation Prepare(string key, Fixture f)
    {
        switch (key)
        {
            case "overview-delta":
            case "overview-delta-dense":
                var source = key == "overview-delta" ? f.Document : f.Dense;
                var original = (ObjectLayer)source.Layers[1];
                var revised = DocumentEditor.ReplaceObject(source, SceneObjectClone.At(original.Objects[0], 140, 100));
                var target = (ObjectLayer)revised.Layers[1];
                SceneRenderer.ObjectOverviewPlan? plan = null;
                Assert.True(ObjectLayerChanges.TryGet(target, out var link));
                Assert.Same(original, link.Source);
                Assert.Single(link.Added); Assert.Single(link.Removed);
                return new(() => plan = new(target), () =>
                {
                    Assert.NotNull(plan);
                    Assert.NotSame(Plans.GetValue(original, _ => throw new InvalidOperationException()), plan);
                    Assert.Equal(key == "overview-delta" ? 10 : f.N, plan.Chunks[(0, 0)].Objects.Length);
                    Assert.Equal(140, plan.Chunks[(0, 0)].Objects.Single(o => o.Item.Id == original.Objects[0].Id).Item.X);
                    if (key == "overview-delta")
                        Assert.Same(Plans.GetValue(original, _ => throw new InvalidOperationException()).Chunks[(1, 1)], plan.Chunks[(1, 1)]);
                });
            case "visible-scene-conversion":
                var layer = (ObjectLayer)f.Document.Layers[1];
                var local = ObjectSpatialIndex.For(layer).Query(new(0, 0, 512, 512));
                Assert.Equal(10, local.Count);
                // Same CPU conversion used by overview rendering, including ordering, hashing,
                // color/point batches and exact-object classification. New chunk => no variant hit.
                var chunk = new SceneRenderer.OverviewChunk(0, 0, [.. SceneGeometry.InDrawOrder(layer, local).Select((o, i) => ((long)i, o))]);
                SceneRenderer.OverviewVariant? variant = null;
                return new(() => variant = chunk.GetVariant(-7, 1d / 128, new Dictionary<string, SKColor>(), 0),
                    () => { Assert.Equal(10, variant!.OverviewObjectCount); Assert.Empty(variant.ExactObjects); Assert.True(variant.BatchCount > 0); });
            case "visible-raster-frame":
                var stats = new RenderStats();
                var options = new RenderOptions { VisibleWorld = new(0, 0, 512, 512), ViewScale = 1, Stats = stats };
                return new(() =>
                {
                    stats.Reset(); f.Surface.Canvas.Clear(SKColors.Transparent);
                    SceneRenderer.Render(f.Surface.Canvas, f.Document, options);
                }, () => Assert.Equal(10, stats.ObjectsDrawn), 16);
            case "river-preview":
                var rivers = f.Features;
                var lastRiver = rivers.Rivers[^1];
                var movedRiver = RiverEditor.MoveNode(lastRiver, lastRiver.Channels[0].Id, 1, 410, 500);
                var riverOptions = new RenderOptions { RiverOverrides = new[] { movedRiver }, RiverPreviewLayerId = rivers.Id };
                IReadOnlyList<River> resolvedRivers = [];
                return new(() => resolvedRivers = RiverRenderer.Resolve(rivers, riverOptions),
                    () => { Assert.Equal(f.N, resolvedRivers.Count); Assert.Same(movedRiver, resolvedRivers[^1]); Assert.Same(rivers.Rivers[0], resolvedRivers[0]); });
            case "road-preview":
                var roads = f.Features;
                var lastRoad = roads.Roads[^1];
                var movedRoad = RoadEditor.MoveNode(lastRoad, lastRoad.Nodes[1].Id, 410, 700);
                var roadOptions = new RenderOptions { RoadOverrides = new[] { movedRoad }, RoadPreviewLayerId = roads.Id };
                IReadOnlyList<Road> resolvedRoads = [];
                return new(() => resolvedRoads = RoadRenderer.Resolve(roads, roadOptions),
                    () => { Assert.Equal(f.N, resolvedRoads.Count); Assert.Same(movedRoad, resolvedRoads[^1]); Assert.Same(roads.Roads[0], resolvedRoads[0]); });
            default: throw new ArgumentOutOfRangeException(nameof(key), key, null);
        }
    }

    public sealed class Maps : IDisposable
    {
        private readonly Dictionary<int, Fixture> _maps = [];
        internal Fixture Get(int n) => _maps.TryGetValue(n, out var f) ? f : _maps[n] = new(n);
        public void Dispose() { foreach (var f in _maps.Values) f.Surface.Dispose(); }
    }

    internal sealed class Fixture
    {
        internal int N { get; }
        internal MapDocument Document { get; }
        internal SKSurface Surface { get; } = SKSurface.Create(new SKImageInfo(512, 512))!;
        private MapDocument? _dense;
        internal MapDocument Dense => _dense ??= Make(true);
        private ObjectLayer? _features;
        internal ObjectLayer Features => _features ??= MakeFeatures();
        internal Fixture(int n) { N = n; Document = Make(false); }
        private MapDocument Make(bool dense)
        {
            var objects = Enumerable.Range(0, N).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle,
                "guard " + i, i < 10 ? 100 + i * 40 : dense ? 2048 + i % 1000 * 4 : 8192 + i % 1000 * 48,
                i < 10 ? 100 : dense ? 2048 + i / 1000 * 4 : 8192 + i / 1000 * 48,
                24, 24, 0, "#FF445566", "#00000000", 0, true, false)).ToArray();
            var layer = new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects);
            var document = new MapDocument(DocumentId.New(), "render paths", DateTimeOffset.UnixEpoch,
                new CanvasDefinition(65_536, 65_536, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"), layer]);
            DocumentDerivedIndexes.Prepare(document);
            Plans.Add(layer, new(layer));
            return document;
        }
        private ObjectLayer MakeFeatures()
        {
            var river = new River { Channels = [RiverEditor.NewChannel([new(100, 500), new(400, 500)], 20)] };
            var road = RoadEditor.NewRoad([new(100, 700), new(400, 700)], 20, seed: 7);
            return new(LayerId.New(), "features", true, false, 1, [])
            {
                Rivers = [.. Enumerable.Range(1, N - 1).Select(i => RiverEditor.Translate(river, 8192 + i % 1000 * 48, 8192 + i / 1000 * 48) with { Id = Guid.NewGuid() }), river],
                Roads = [.. Enumerable.Range(1, N - 1).Select(i => RoadEditor.Translate(road, 8192 + i % 1000 * 48, 8192 + i / 1000 * 48) with { Id = Guid.NewGuid() }), road],
            };
        }
    }

    private sealed record Operation(Action Run, Action Verify, int Calls = 1);
    private readonly record struct Sample(double Us, double Bytes);
    // Kept local so each test project builds independently; same protocol as HotPathCostGuardTests.
    private static void CompareSamples(string key, int n, int m, Func<Operation> small, Func<Operation> large, bool enforce)
    {
        for (var i = 0; i < 2; i++) { Measure(small); Measure(large); }
        var a = new Sample[9]; var b = new Sample[9];
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var noGc = enforce && GC.TryStartNoGCRegion(128L * 1024 * 1024);
        var collections = GC.CollectionCount(0);
        try
        {
            for (var i = 0; i < 9; i++)
                if ((i & 1) == 0) { a[i] = Measure(small); b[i] = Measure(large); }
                else { b[i] = Measure(large); a[i] = Measure(small); }
        }
        finally
        {
            collections = GC.CollectionCount(0) - collections;
            if (noGc && System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
        }
        var x = Median(a); var y = Median(b);
        var time = Ratio(x.Us, y.Us); var bytes = Ratio(x.Bytes, y.Bytes);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"RENDER_COST|{key}|{n}|{m}|{x.Us:F3}|{y.Us:F3}|{x.Bytes:F1}|{y.Bytes:F1}|{time:F3}|{bytes:F3}|{(enforce ? "GUARD" : "O(N)")}|noGC={noGc}|GC0={collections}"));
        if (!enforce) return;
        Assert.True(time <= 3, $"{key}: time ratio {time:F3} > 3 ({x.Us:F3} -> {y.Us:F3} us)");
        Assert.True(bytes <= 3, $"{key}: bytes ratio {bytes:F3} > 3 ({x.Bytes:F1} -> {y.Bytes:F1} B)");
    }
    private static Sample Measure(Func<Operation> prepare)
    {
        var op = prepare();
        var bytes = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < op.Calls; i++) op.Run();
        var us = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        op.Verify();
        return new(us / op.Calls, (double)allocated / op.Calls);
    }
    private static Sample Median(Sample[] a) => new(a.Select(s => s.Us).Order().ElementAt(4), a.Select(s => s.Bytes).Order().ElementAt(4));
    private static double Ratio(double a, double b) => a == 0 ? b == 0 ? 1 : double.PositiveInfinity : b / a;
    public sealed class EnvironmentFactAttribute : FactAttribute
    {
        public EnvironmentFactAttribute(string variable)
        { if (Environment.GetEnvironmentVariable(variable) != "1") Skip = "Set " + variable + "=1"; }
    }
}
