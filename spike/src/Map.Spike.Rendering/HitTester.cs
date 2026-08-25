using Map.Spike.Core.Documents;
using Map.Spike.Core.Geometry;
using Map.Spike.Core.Spatial;

namespace Map.Spike.Rendering;

public readonly record struct HitTestResult(SpikeObject? Object, SpatialQueryStats Stats)
{
    public bool Hit => Object is not null;
}

/// <summary>
/// Screen-space entry point for picking. The broad phase is the object layer's spatial index; the
/// narrow phase is exact per-shape containment in world space.
/// </summary>
public static class HitTester
{
    public static HitTestResult HitTestScreen(SpikeDocument document, Viewport viewport, Vec2 screenPoint)
    {
        var world = viewport.ScreenToWorld(screenPoint);
        return HitTestWorld(document, world);
    }

    public static HitTestResult HitTestWorld(SpikeDocument document, Vec2 world)
    {
        var stats = default(SpatialQueryStats);
        var hit = document.Objects.HitTest(world, ref stats);
        return new HitTestResult(hit, stats);
    }
}
