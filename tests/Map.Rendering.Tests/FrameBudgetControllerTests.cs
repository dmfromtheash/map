using Map.Rendering;

namespace Map.Rendering.Tests;

public sealed class FrameBudgetControllerTests
{
    private static readonly MachineLimits Limits = new MachineProfile(16, 16 * MachineProfile.GiB, 12 * MachineProfile.GiB).Limits();

    [Theory]
    [InlineData(60, 4)]
    [InlineData(144, 2)]
    public void Steady_headroom_grows_all_budgets_to_machine_ceiling(double hz, double workMs)
    {
        var clock = new FrameTestClock();
        var controller = new FrameBudgetController(clock);
        Assert.Equal(new FrameWorkBudget(8 * MachineProfile.MiB, 2 * MachineProfile.MiB, 512), controller.Budget(Limits));
        Feed(controller, clock, 600, workMs, hz);
        Assert.Equal(8, controller.GrowthLevel);
        Assert.Equal(new FrameWorkBudget(Limits.AdoptBytes, Limits.GestureAdoptBytes, Limits.DirectDraws), controller.Budget(Limits));
        Assert.Equal(1000 / hz, controller.TargetMilliseconds, 8);
    }

    [Fact]
    public void Unknown_refresh_after_a_known_display_restores_the_60_hz_target()
    {
        var controller = new FrameBudgetController(new FrameTestClock());
        controller.RecordFrame(TimeSpan.FromMilliseconds(1), 144);
        Assert.Equal(1000d / 144, controller.TargetMilliseconds, 8);
        controller.RecordFrame(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1000d / 60, controller.TargetMilliseconds, 8);
    }

    [Fact]
    public void A_burst_without_elapsed_time_cannot_raise_the_budget()
    {
        var clock = new FrameTestClock();
        var controller = new FrameBudgetController(clock);
        for (var i = 0; i < 1000; i++) controller.RecordFrame(TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, controller.GrowthLevel);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        controller.RecordFrame(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, controller.GrowthLevel);
    }

    [Fact]
    public void One_slow_frame_shrinks_immediately_and_delays_regrowth()
    {
        var clock = new FrameTestClock();
        var controller = new FrameBudgetController(clock);
        Feed(controller, clock, 600, 3);
        var before = controller.Budget(Limits);
        controller.RecordFrame(TimeSpan.FromMilliseconds(40));
        var after = controller.Budget(Limits);
        Assert.True(after.AdoptBytes < before.AdoptBytes);
        Assert.True(after.GestureAdoptBytes < before.GestureAdoptBytes);
        Assert.True(after.DirectDraws < before.DirectDraws);
        Feed(controller, clock, 60, 3);
        Assert.Equal(after, controller.Budget(Limits));
    }

    [Fact]
    public void Alternating_fast_and_slow_frames_do_not_oscillate()
    {
        var clock = new FrameTestClock();
        var controller = new FrameBudgetController(clock);
        Feed(controller, clock, 600, 3);
        var previous = controller.GrowthLevel;
        for (var i = 0; i < 600; i++)
        {
            Feed(controller, clock, 1, i % 2 == 0 ? 30 : 2);
            Assert.True(controller.GrowthLevel <= previous);
            previous = controller.GrowthLevel;
        }
        Assert.Equal(0, controller.GrowthLevel);
    }

    [Fact]
    public void Refresh_rate_and_pressure_change_the_effective_budget()
    {
        var clock = new FrameTestClock();
        var controller = new FrameBudgetController(clock);
        Feed(controller, clock, 600, 3, 144);
        controller.RecordFrame(TimeSpan.FromMilliseconds(7), 144);
        Assert.Equal(4, controller.GrowthLevel);
        var floor = new MachineProfile(16, 16 * MachineProfile.GiB, MachineProfile.GiB, LowMemory: true).Limits();
        Assert.Equal(new FrameWorkBudget(8 * MachineProfile.MiB, 2 * MachineProfile.MiB, 512), controller.Budget(floor));
        Feed(controller, clock, 600, 12, 60); // Neutral frames never cause growth.
        Assert.Equal(4, controller.GrowthLevel);
    }

    private static void Feed(FrameBudgetController controller, FrameTestClock clock, int frames, double workMs, double hz = 60)
    {
        for (var i = 0; i < frames; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1000 / hz));
            controller.RecordFrame(TimeSpan.FromMilliseconds(workMs), hz);
        }
    }
}

internal sealed class FrameTestClock : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public void Advance(TimeSpan time) => _ticks += time.Ticks;
}
