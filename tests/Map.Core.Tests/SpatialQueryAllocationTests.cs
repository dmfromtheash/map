using Map.Core.Documents;

namespace Map.Core.Tests;

public sealed class SpatialQueryAllocationTests
{
    [Fact]
    public void Every_warm_dense_query_allocates_only_its_result_and_keeps_order_and_uniqueness()
    {
        const int count = 100_000;
        var objects = Enumerable.Range(0, count).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "mountain geometry", i % 400 * 30, i / 400 * 30, 100 + i % 900, 64, i % 45,
            "#FFFFFFFF", "#00000000", 0, true, false)).ToArray();
        var layer = new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects);
        var index = ObjectSpatialIndex.For(layer);
        var view = new WorldBounds(0, 0, 32768, 32768);
        // Warm the pool and immutable enumerator bookkeeping before sampling. Assertions use their own
        // enumeration pools, so compare snapshots after the measured batch instead of between queries.
        Assert.Equal(objects, index.Query(view));
        for (var i = 0; i < 8; i++) index.Query(view);
        var allocations = new List<long>(5);
        var durations = new List<double>(5);
        var snapshots = new IReadOnlyList<SceneObject>[5];
        for (var frame = 0; frame < 5; frame++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = index.Query(view);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocations.Add(bytes);
            durations.Add(ms);
            snapshots[frame] = result;
        }
        foreach (var snapshot in snapshots) Assert.Equal(objects, snapshot);
        Console.WriteLine($"SPATIAL-QUERY {count}: max allocated={allocations.Max()} resultBytes={count * IntPtr.Size} maxMs={durations.Max():F3}");
        Assert.All(allocations, bytes => Assert.True(bytes <= count * (long)IntPtr.Size + 8192,
            $"a dense query allocated {bytes} bytes, including disposable LOH scratch"));
    }
}
