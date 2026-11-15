using Map.Core.Documents;
using Map.Core.Resources;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>Overview tiles under the shared cache budget's pressure (no admission refusal involved).</summary>
public sealed class CacheMemoryPressureTests
{
    private const long TileBytes = 32 * 32 * 4;

    private static WorkingMemoryBudget Budget(bool pressure)
    {
        var budget = new WorkingMemoryBudget(() => new(16L * 1024 * WorkingMemoryBudget.MiB,
            8L * 1024 * WorkingMemoryBudget.MiB, 512 * WorkingMemoryBudget.MiB))
        { SimulatedCachePressure = pressure };
        for (var i = 0; i < 3; i++) budget.PollCachePressure(); // Stage 2 reaches the expensive overview class.
        return budget;
    }

    private static SKImage Tile()
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32))!;
        return surface.Snapshot();
    }

    // The on-all cache enforces its fixed product budget internally.
    private static ObjectOverviewCache Cache(WorkingMemoryBudget budget) =>
        new(1024, 16 * TileBytes, budget, memoryAccount: "object-overview")
        { PressureProtectedFrames = 3, PressureProtectedMilliseconds = 0 };

    /// <summary>Frame 1 builds 16 tiles; then the view keeps drawing tiles 0..3 only.</summary>
    private static void Route(ObjectOverviewCache cache, LayerId layer, int frames, ref int builds)
    {
        var made = 0;
        SKImage Count() { made++; return Tile(); }
        cache.BeginFrame();
        for (var x = 0; x < 16; x++) cache.GetOrCreate(layer, x, 0, 1, 1, Count);
        cache.EndFrame();
        var worst = 0d;
        for (var frame = 0; frame < frames; frame++)
        {
            cache.BeginFrame();
            for (var x = 0; x < 4; x++) cache.GetOrCreate(layer, x, 0, 1, 1, Count);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            cache.EndFrame();
            worst = Math.Max(worst, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        Console.WriteLine($"PRESSURE budgetMs=2 frames={frames} worstEndFrameMs={worst:F3} remainingBytes={cache.Bytes}");
        builds += made;
    }

    [Fact]
    public void Under_pressure_tiles_off_the_view_go_down_to_the_watermark_and_the_view_stays()
    {
        using var cache = Cache(Budget(pressure: true));
        var layer = LayerId.New();
        var builds = 0;
        Route(cache, layer, frames: 12, ref builds);
        Assert.Equal(16, builds); // The visible tiles were never rebuilt.
        Assert.Equal(8 * TileBytes, cache.Bytes);
        for (var x = 0; x < 4; x++) Assert.True(cache.TryGet(layer, x, 0, 1, 1, out _), "visible " + x);
        Assert.True(cache.PressureTrims >= 1);
    }

    [Fact]
    public void Without_pressure_the_cache_keeps_everything_within_its_limit()
    {
        using var cache = Cache(Budget(pressure: false));
        var layer = LayerId.New();
        var builds = 0;
        Route(cache, layer, frames: 12, ref builds);
        Assert.Equal(16 * TileBytes, cache.Bytes);
        Assert.Equal(0, cache.PressureTrims);
    }

    [Fact]
    public void Tiles_drawn_within_the_protected_frames_are_never_trimmed()
    {
        using var cache = Cache(Budget(pressure: true));
        var layer = LayerId.New();
        var builds = 0;
        // Two frames after the build: every tile is still inside the three protected frames.
        Route(cache, layer, frames: 2, ref builds);
        Assert.Equal(16 * TileBytes, cache.Bytes);
        Assert.Equal(0, cache.PressureTrims);
    }

    [Fact]
    public void A_trimmed_tile_is_rebuilt_on_the_next_request()
    {
        using var cache = Cache(Budget(pressure: true));
        var layer = LayerId.New();
        var builds = 0;
        Route(cache, layer, frames: 12, ref builds);
        Assert.False(cache.TryGet(layer, 4, 0, 1, 1, out _));
        cache.BeginFrame();
        var rebuilt = cache.GetOrCreate(layer, 4, 0, 1, 1, Tile);
        Assert.False(rebuilt.Hit);
        Assert.NotEqual(IntPtr.Zero, rebuilt.Image.Handle);
        Assert.Equal(32, rebuilt.Image.Width);
        cache.EndFrame();
        // The new tile is the working set now: the next pressure pass keeps it.
        Assert.True(cache.TryGet(layer, 4, 0, 1, 1, out _));
    }

    [Fact]
    public void Page_fallbacks_are_trimmed_after_ordinary_detail()
    {
        using var cache = Cache(Budget(pressure: true));
        var layer = LayerId.New();
        cache.BeginFrame();
        cache.GetOrCreate(layer, 0, 0, 0, 1, Tile, fallback: true);
        for (var x = 0; x < 15; x++) cache.GetOrCreate(layer, x, 0, 1, 1, Tile);
        cache.EndFrame();
        // Fallbacks are pinned in every open frame; let the protected frames pass without drawing.
        for (var frame = 0; frame < 12; frame++) { cache.BeginFrame(); cache.EndFrame(); }
        Assert.Equal(8 * TileBytes, cache.Bytes);
        Assert.True(cache.TryGet(layer, 0, 0, 0, 1, out _));
    }
}
