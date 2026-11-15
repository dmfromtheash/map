using System.Reflection;
using System.Text;
using Map.Core.Documents;
using Map.Rendering;

namespace Map.Rendering.Tests;

/// <summary>Throw-away measurement (doc-mem worktree only): retained bytes of ObjectOverviewPlan.</summary>
public sealed class OverviewMemoryProbe
{
    private static long Retained()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        return GC.GetTotalMemory(true);
    }

    [Fact]
    public void Cold_plan_uses_one_flat_slot_per_visible_object_with_bounded_allocation()
    {
        const int count = 10_000;
        var objects = Enumerable.Range(0, count).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle,
            "probe", i % 100 * 40, i / 100 * 40, 20, 20, 0, "#FFFFFFFF", "#00000000", 0, true, false)).ToArray();
        var layer = new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects);
        _ = new SceneRenderer.ObjectOverviewPlan(layer); // JIT and static initialization before measurement
        var before = GC.GetAllocatedBytesForCurrentThread();
        var plan = new SceneRenderer.ObjectOverviewPlan(layer);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"PLAN_BYTES count={count} allocated={allocated} bytesPerObject={allocated / (double)count:F1}");
        Assert.Equal(count, plan.Chunks.Values.Sum(chunk => chunk.Objects.Length));
        Assert.Same(objects[0], plan.Chunks[(0, 0)].Objects[0].Item);
        // Flat (long order, object reference) slots cost 16 B each; 32 B allows a full slot's headroom.
        // A per-object tree, growing lists plus copies, or a duplicate plan no longer fits.
        Assert.InRange(allocated, count * 16L, count * 32L);
    }

    [OptInProbe("MAP_DOC_MEM_PROBE", nameof(Cold_plan_uses_one_flat_slot_per_visible_object_with_bounded_allocation))]
    public void Measure()
    {
        var output = Environment.GetEnvironmentVariable("MAP_DOC_MEM_PROBE");
        if (string.IsNullOrWhiteSpace(output)) return;
        var planType = typeof(SceneRenderer).GetNestedType("ObjectOverviewPlan", BindingFlags.NonPublic)!;
        var ctor = planType.GetConstructors().Single();
        var report = new StringBuilder();
        foreach (var n in new[] { 100_000, 1_000_000 })
        {
            var random = new Random(42);
            var objects = new SceneObject[n];
            for (var i = 0; i < n; i++)
            {
                var width = 64 * (0.5 + random.NextDouble() * 1.5);
                objects[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Ассет " + i,
                    random.NextDouble() * (131072 - width), random.NextDouble() * (131072 - width),
                    width, width * 0.8, random.Next(-180, 181), "#FFFFFFFF", "#00000000", 0, true, false)
                { Stamp = new StampStyle((i % 16).ToString("x64")) };
            }
            var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects);
            objects = null!;
            var t0 = Retained();
            var before = GC.GetTotalAllocatedBytes(true);
            var plan = ctor.Invoke([layer, CancellationToken.None, null]);
            var allocated = GC.GetTotalAllocatedBytes(true) - before;
            var retained = Retained() - t0;
            report.AppendLine($"{n,9} | ObjectOverviewPlan (Rendering, per layer)                  | {retained / (double)n,8:F1} B/obj | {retained / 1048576.0,8:F1} MB");
            report.AppendLine($"{n,9} |   ObjectOverviewPlan allocated while building              | {allocated / (double)n,8:F1} B/obj | {allocated / 1048576.0,8:F1} MB");
            GC.KeepAlive(plan); GC.KeepAlive(layer);
        }
        File.AppendAllText(output, report.ToString());
    }
}
