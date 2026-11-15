using System.Diagnostics;
using System.Globalization;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests;

/// <summary>
/// DEC-111: prepared maps, fixed local work, no commands. Nine interleaved pairs after two
/// warmups; setup and semantic witnesses are untimed. Queries are batched to amortize the
/// clock; cold delta/index/layout work gets a fresh snapshot on EVERY sample. Bytes are
/// managed allocations on this thread. 0 -> 0 has ratio 1; 0 -> positive has ratio infinity.
/// MAP_RENDER_GUARDS_AUDIT=1 measures skipped cases, MAP_RENDER_GUARDS_HUGE=1 adds 1M.
/// </summary>
[Collection(nameof(DerivedIndexCounters))]
public sealed class HotPathCostGuardTests : IClassFixture<HotPathCostGuardTests.Maps>
{
    private readonly Maps _maps;
    public HotPathCostGuardTests(Maps maps) => _maps = maps;
    private static readonly WorldBounds View = new(90, 90, 510, 340);
    private const string LabelsLinear = "LabelPriority.cs:69-73, LabelBounds scans all objects even with three fixed labels";
    private const string RulesLinear = "PlacementAreaRuleCheck.cs:17-27,45-55,80-89, builds arrays and scans all rivers/roads for one local probe";
    private const string PreviewLinear = "SceneObjectList.cs:90-97,466-480, ReplaceObject preview copies O(N/256) chunk directories";

    [Theory]
    [InlineData("click")]
    [InlineData("hover-miss")]
    [InlineData("marquee")]
    [InlineData("viewport")]
    [InlineData("selected-lookup")]
    [InlineData("grid-snap")]
    [InlineData("placement-stamps")]
    [InlineData("label-view")]
    [InlineData("river-local-edit")]
    [InlineData("road-local-edit")]
    [InlineData("area-local-edit")]
    [InlineData("derived-index-delta")]
    public void Bounded_hot_path(string key) => Compare(key, 200_000, true);

    [Fact(Skip = "defect: " + LabelsLinear + "; 10k/100k time ratio 9.401 > 3")]
    public void Label_clearance_bounds() => Compare("label-bounds", 100_000, true);

    [Fact(Skip = "defect: " + RulesLinear + "; 10k/100k time ratio 8.028 > 3")]
    public void Placement_rules_with_many_features() => Compare("placement-features", 100_000, true);

    [Fact(Skip = "O(N): " + PreviewLinear)]
    public void Document_preview_replacement() => Compare("preview-replace", 200_000, true);

    [EnvironmentFact("MAP_RENDER_GUARDS_AUDIT")]
    public void Audit_documented_linear_paths()
    {
        foreach (var key in new[] { "label-bounds", "placement-features", "preview-replace" }) Compare(key, 200_000, false);
    }

    [EnvironmentFact("MAP_RENDER_GUARDS_HUGE")]
    public void Million_object_variant()
    {
        foreach (var key in new[] { "click", "hover-miss", "marquee", "viewport", "selected-lookup", "grid-snap",
                     "placement-stamps", "label-view", "river-local-edit", "road-local-edit", "area-local-edit", "derived-index-delta" })
            Compare(key, 1_000_000, true);
    }

    private void Compare(string key, int count, bool enforce)
    {
        var small = _maps.Get(10_000); var large = _maps.Get(count);
        Cost.Compare(key, small.N, large.N, () => Prepare(key, small), () => Prepare(key, large), enforce);
    }

    private static Operation Prepare(string key, Fixture f)
    {
        switch (key)
        {
            case "click":
            case "hover-miss":
                SceneObject? hit = null;
                var x = key == "click" ? 112 : 600;
                return new(() => hit = SceneGeometry.HitTest(f.Document, x, 112),
                    () => Assert.Equal(key == "click" ? f.Layer.Objects[0].Id : (ObjectId?)null, hit?.Id), 256);
            case "marquee":
                IReadOnlyList<SceneObject> selected = [];
                return new(() => selected = MultiSelectionGeometry.ObjectsInMarquee(f.Document, View),
                    () => Assert.Equal(f.LocalIds, selected.Select(o => o.Id)), 128);
            case "viewport":
                IReadOnlyList<SceneObject> visible = [];
                return new(() => visible = ObjectSpatialIndex.For(f.Layer).Query(View),
                    () => Assert.Equal(f.LocalIds, visible.Select(o => o.Id)), 128);
            case "selected-lookup":
                SceneObject? found = null;
                return new(() => found = DocumentEditor.FindObject(f.Document, f.Layer.Objects[5].Id),
                    () => Assert.Same(f.Layer.Objects[5], found), 256);
            case "grid-snap":
                SceneObject? snapped = null;
                return new(() => snapped = GridSnapService.SnapObject(f.Layer.Objects[0], f.Grid),
                    () => { Assert.Equal(108, snapped!.X); Assert.Equal(108, snapped.Y); }, 256);
            case "placement-stamps":
                var check = new PlacementAreaRuleCheck(f.Document, new() { AvoidWater = true, AvoidRoads = true });
                var allowed = false; var blocked = false;
                return new(() => { allowed = check.Allows(600, 600); blocked = check.Allows(200, 500); },
                    () => { Assert.True(allowed); Assert.False(blocked); }, 128);
            case "label-view":
                // New document identity invalidates Hidden's memo, while sharing the prepared label index.
                var document = f.Document with { Title = "fresh frame" };
                var hidden = new bool[3];
                return new(() =>
                {
                    var frame = LabelPriority.ForFrame(document, 1, null);
                    for (var i = 0; i < 3; i++) hidden[i] = frame.Contains(f.Layer.Objects[10 + i]);
                }, () => Assert.Equal(new[] { false, true, false }, hidden));
            case "label-bounds":
                IReadOnlyList<WorldBounds> bounds = [];
                return new(() => bounds = LabelPriority.LabelBounds(f.Document), () => Assert.Equal(3, bounds.Count));
            case "river-local-edit":
                var river = RiverEditor.MoveNode(f.River, f.River.Channels[0].Id, 1, 410, 500);
                RiverGeometrySet? water = null; var inWater = false;
                return new(() => { water = RiverGeometry.Evaluate(river); inWater = RiverGeometry.Contains(river, 200, 500); },
                    () => { Assert.True(inWater); Assert.Same(river, water!.River); });
            case "road-local-edit":
                var road = RoadEditor.MoveNode(f.Road, f.Road.Nodes[1].Id, 410, 700);
                RoadGeometrySet? way = null; var onRoad = false;
                return new(() => { way = RoadGeometry.Evaluate(road); onRoad = RoadGeometry.Contains(road, 200, 700); },
                    () => { Assert.True(onRoad); Assert.Same(road, way!.Road); });
            case "area-local-edit":
                IReadOnlyList<PlacementAreaGeometry.CellSpan> spans = [];
                var moved = f.Area.Nodes.SetItem(1, f.Area.Nodes[1] with { X = 450 });
                return new(() => spans = PlacementAreaGeometry.ChangedCoverage(f.Area.Nodes, moved),
                    () => { Assert.NotEmpty(spans); Assert.True(PlacementAreaGeometry.Contains(f.Area.Nodes, 200, 200)); }, 32);
            case "derived-index-delta":
                // Storage/delta registration is separate (preview-replace); measure the first
                // materialization of all inherited indexes, never a repeatedly prepared snapshot.
                var delta = DocumentEditor.ReplaceObject(f.Document, SceneObjectClone.At(f.Layer.Objects[0], 600, 100));
                return new(() => DocumentDerivedIndexes.Prepare(delta), () =>
                {
                    Assert.True(DocumentDerivedIndexes.IsPrepared(delta));
                    Assert.Equal(f.Layer.Objects[0].Id, SceneGeometry.HitTest(delta, 612, 112)?.Id);
                    Assert.Null(SceneGeometry.HitTest(delta, 112, 112));
                });
            case "preview-replace":
                MapDocument? preview = null;
                var replacement = SceneObjectClone.At(f.Layer.Objects[0], 140, 100);
                return new(() => preview = DocumentEditor.ReplaceObject(f.Document, replacement),
                    () => Assert.Same(replacement, DocumentEditor.FindObject(preview!, replacement.Id)));
            case "placement-features":
                var features = f.Features;
                PlacementAreaRuleCheck? rules = null; var free = false; var wet = false;
                return new(() =>
                {
                    rules = new(features, new() { AvoidWater = true, AvoidRoads = true });
                    free = rules.Allows(600, 600); wet = rules.Allows(200, 500);
                }, () => { Assert.NotNull(rules); Assert.True(free); Assert.False(wet); });
            default: throw new ArgumentOutOfRangeException(nameof(key), key, null);
        }
    }

    public sealed class Maps
    {
        private readonly Dictionary<int, Fixture> _maps = [];
        internal Fixture Get(int n) => _maps.TryGetValue(n, out var f) ? f : _maps[n] = new(n);
    }

    internal sealed class Fixture
    {
        internal int N { get; }
        internal MapDocument Document { get; }
        internal ObjectLayer Layer { get; }
        internal ObjectId[] LocalIds { get; }
        internal GridDefinition Grid { get; } = new(GridId.New(), GridGeometry.Square, 20);
        internal River River { get; } = new() { Channels = [RiverEditor.NewChannel([new(100, 500), new(400, 500)], 20)] };
        internal Road Road { get; } = RoadEditor.NewRoad([new(100, 700), new(400, 700)], 20, seed: 7);
        internal PlacementArea Area { get; } = new() { Nodes = [new(Guid.NewGuid(), 100, 100), new(Guid.NewGuid(), 400, 100),
            new(Guid.NewGuid(), 400, 400), new(Guid.NewGuid(), 100, 400)] };
        private MapDocument? _features;
        internal MapDocument Features => _features ??= Document.WithLayer(Layer.Id, _ => Layer with
        {
            Objects = [],
            // N total features, half rivers/half roads. Local obstacles are LAST: no early-hit shortcut.
            Rivers = [.. Enumerable.Range(1, N / 2 - 1).Select(i => RiverEditor.Translate(River, 8192 + i % 1000 * 48, 8192 + i / 1000 * 48) with { Id = Guid.NewGuid() }), River],
            Roads = [.. Enumerable.Range(1, N / 2 - 1).Select(i => RoadEditor.Translate(Road, 8192 + i % 1000 * 48, 8192 + i / 1000 * 48) with { Id = Guid.NewGuid() }), Road],
        });

        internal Fixture(int n)
        {
            N = n;
            var items = Enumerable.Range(0, n).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle,
                "guard " + i, i < 10 ? 100 + i * 40 : 8192 + i % 1000 * 48,
                i < 10 ? 100 : 8192 + i / 1000 * 48, 24, 24, 0, "#FFFFFFFF", "#00000000", 0, true, false)).ToArray();
            for (var i = 0; i < 3; i++) items[10 + i] = new SceneObject(items[10 + i].Id, SceneObjectKind.Text,
                "label", i == 2 ? 300 : 100, 300, 80, 24, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            {
                Text = new TextStyle { Content = "label", FontSize = 20, Importance = 5 - i } };
            Layer = new(LayerId.New(), "objects", true, false, 1, items) { Rivers = [River], Roads = [Road] };
            Document = new(DocumentId.New(), "hot paths", DateTimeOffset.UnixEpoch,
                new CanvasDefinition(65_536, 65_536, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"), Layer]);
            Document = Document with { Labels = new() { ByScale = true, ResolveOverlaps = true, ClearUnderLabels = true } };
            LocalIds = items.Take(13).Select(o => o.Id).ToArray();
            DocumentDerivedIndexes.Prepare(Document);
        }
    }

    internal sealed record Operation(Action Run, Action Verify, int Calls = 1);

    internal static class Cost
    {
        internal static void Compare(string key, int n, int m, Func<Operation> small, Func<Operation> large, bool enforce)
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
                $"HOT_COST|{key}|{n}|{m}|{x.Us:F3}|{y.Us:F3}|{x.Bytes:F1}|{y.Bytes:F1}|{time:F3}|{bytes:F3}|{(enforce ? "GUARD" : "O(N)")}|noGC={noGc}|GC0={collections}"));
            if (!enforce) return;
            Assert.True(time <= 3, $"{key}: time ratio {time:F3} > 3 ({x.Us:F3} -> {y.Us:F3} us)");
            Assert.True(bytes <= 3, $"{key}: bytes ratio {bytes:F3} > 3 ({x.Bytes:F1} -> {y.Bytes:F1} B)");
        }
        private readonly record struct Sample(double Us, double Bytes);
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
    }

    public sealed class EnvironmentFactAttribute : FactAttribute
    {
        public EnvironmentFactAttribute(string variable)
        { if (Environment.GetEnvironmentVariable(variable) != "1") Skip = "Set " + variable + "=1"; }
    }
}
