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
    public void Measure()
    {
        var output = Environment.GetEnvironmentVariable("MAP_DOC_MEM_PROBE");
        if (string.IsNullOrWhiteSpace(output)) return;
        var planType = typeof(SceneRenderer).GetNestedType("ObjectOverviewPlan", BindingFlags.NonPublic)!;
        var ctor = planType.GetConstructor([typeof(ObjectLayer)])!;
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
            var plan = ctor.Invoke([layer]);
            var allocated = GC.GetTotalAllocatedBytes(true) - before;
            var retained = Retained() - t0;
            report.AppendLine($"{n,9} | ObjectOverviewPlan (Rendering, per layer)                  | {retained / (double)n,8:F1} B/obj | {retained / 1048576.0,8:F1} MB");
            report.AppendLine($"{n,9} |   ObjectOverviewPlan allocated while building              | {allocated / (double)n,8:F1} B/obj | {allocated / 1048576.0,8:F1} MB");
            GC.KeepAlive(plan); GC.KeepAlive(layer);
        }
        File.AppendAllText(output, report.ToString());
    }
}
