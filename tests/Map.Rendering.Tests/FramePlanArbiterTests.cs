using Map.Rendering.Reserve;
using static Map.Rendering.Tests.FramePlanTestData;
using Stream = Map.Rendering.Tests.FramePlanTestData.Stream;

namespace Map.Rendering.Tests;

public sealed class FramePlanArbiterTests
{
    [Theory]
    [InlineData(FramePlanKnob.SceneResolution, FramePlanOwner.GestureResolution)]
    [InlineData(FramePlanKnob.UploadBudget, FramePlanOwner.FrameBudget)]
    [InlineData(FramePlanKnob.AdoptionBudget, FramePlanOwner.FrameBudget)]
    [InlineData(FramePlanKnob.CameraFrame, FramePlanOwner.CameraPolicy)]
    [InlineData(FramePlanKnob.BackgroundConcurrency, FramePlanOwner.WorkScheduler)]
    [InlineData(FramePlanKnob.Prefetch, FramePlanOwner.WorkScheduler)]
    [InlineData(FramePlanKnob.Exactness, FramePlanOwner.ExactnessPolicy)]
    public void Each_knob_has_one_explicit_owner(FramePlanKnob knob, FramePlanOwner owner)
    {
        Assert.Equal(owner, FramePlanner.OwnerOf(knob));
        Assert.Throws<ArgumentOutOfRangeException>(() => FramePlanner.OwnerOf((FramePlanKnob)999));
    }

    [Fact]
    public void Defaults_are_exact_and_do_not_request_assistance_at_rest()
    {
        var planner = new FramePlanner();
        var initial = planner.Current;
        for (int t = 0; t <= 20_000; t += 20)
            Assert.Equal(initial, planner.Plan(Healthy with { GestureActive = false }, t));
        Assert.Equal(1, initial.SceneResolutionScale);
        Assert.True(initial.RequireExact);
        Assert.False(initial.UseCameraFrame);
        Assert.Null(planner.LastChangedKnob);
    }

    [Fact]
    public void Conflicting_requests_are_combined_by_the_owner_without_ping_pong()
    {
        var stream = new Stream();
        var requests = Normal with { UploadLimitFactor = 0.75, CpuTileConcurrencyHint = 99 };
        var sample = Hot with { Reserve = requests };
        var plan = stream.To(4_000, sample);
        // FrameBudget chooses min(reserve .75, measured-pressure .5); scheduler caps CPU help.
        Assert.Equal(new FrameWorkBudget(500, 5), plan.UploadBudget);
        Assert.Equal(new FrameWorkBudget(1_000, 10), plan.AdoptionBudget);
        Assert.Equal(1, plan.BackgroundConcurrencyHint);
        Assert.Equal(0.5, plan.SceneResolutionScale); // reserve requested full scale
        Assert.True(plan.StopPrefetch);
        Assert.True(plan.UseCameraFrame);
        for (int t = 4_020; t <= 10_000; t += 20)
            Assert.Equal(plan, stream.Planner.Plan(sample with
            {
                Reserve = requests with { UploadLimitFactor = t % 40 == 0 ? 1 : 0.75 }
            }, t));
    }

    [Fact]
    public void At_most_one_knob_changes_per_period_even_on_repeated_timestamps_or_large_jumps()
    {
        var planner = new FramePlanner(Tuning);
        var sample = Healthy with { Reserve = Normal with
        {
            UploadLimitFactor = 0.25, GestureResolutionScale = 0.5,
            KeepCameraFrameLonger = true, StopPrefetch = true, CpuTileConcurrencyHint = 4
        } };
        double lastChange = double.NegativeInfinity;
        var changed = new HashSet<FramePlanKnob>();
        for (int t = 0; t <= 4_000; t += 20)
        {
            var before = planner.Current;
            var after = planner.Plan(sample, t);
            int count = ChangedKnobs(before, after);
            Assert.InRange(count, 0, 1);
            if (count == 1)
            {
                Assert.True(t - lastChange >= Tuning.WorseningPeriodMs);
                lastChange = t;
                changed.Add(planner.LastChangedKnob!.Value);
            }
            Assert.Equal(after, planner.Plan(sample, t));
            Assert.Null(planner.LastChangedKnob);
        }
        Assert.Equal(6, changed.Count); // all independently owned normal knobs eventually settle
        var snapshot = planner.Current;
        var next = planner.Plan(Hot with { Reserve = sample.Reserve with { UploadLimitFactor = 0.1 } }, 1_000_000);
        Assert.InRange(ChangedKnobs(snapshot, next), 0, 1);
        Assert.Equal(next, planner.Plan(Hot with { Reserve = sample.Reserve with { UploadLimitFactor = 0.1 } }, 1_000_000));
    }

    [Fact]
    public void Worsening_beats_a_due_recovery_and_recovery_waits_longer()
    {
        var stream = new Stream();
        stream.To(780, Healthy with { Reserve = Normal with { UploadLimitFactor = 0.5 } });
        var previous = stream.Last;
        var camera = Healthy with { Reserve = Normal with { KeepCameraFrameLonger = true } };
        var next = stream.To(800, camera);
        Assert.True(next.UseCameraFrame);
        Assert.Equal(FramePlanKnob.CameraFrame, stream.Planner.LastChangedKnob);
        Assert.Equal(previous.UploadBudget, next.UploadBudget);
        stream.To(1_280, camera);
        Assert.Equal(previous.UploadBudget, stream.Last.UploadBudget);
        Assert.Equal(Tuning.UploadBudget, stream.To(1_300, camera).UploadBudget);
        Assert.Equal(FramePlanKnob.UploadBudget, stream.Planner.LastChangedKnob);
    }

    [Fact]
    public void Budgets_intersect_gesture_ceiling_reserve_and_frame_pressure_in_both_dimensions()
    {
        var tuning = Tuning with { GestureUploadBudget = new(400, 8), GestureAdoptionBudget = new(800, 12) };
        var stream = new Stream(tuning);
        var sample = Healthy with { Reserve = Normal with { UploadLimitFactor = 0.25 } };
        var plan = stream.To(2_000, sample);
        Assert.Equal(new FrameWorkBudget(100, 2), plan.UploadBudget);
        Assert.Equal(new FrameWorkBudget(200, 3), plan.AdoptionBudget);
        // Counts round DOWN, and zero really means no work (no implicit minimum of one).
        plan = stream.To(4_000, sample with { Reserve = sample.Reserve with { UploadLimitFactor = 0.01 } });
        Assert.Equal(new FrameWorkBudget(4, 0), plan.UploadBudget);
        Assert.Equal(new FrameWorkBudget(8, 0), plan.AdoptionBudget);
    }

    [Fact]
    public void Asymmetric_budget_change_tightens_before_recovering_the_other_dimension()
    {
        var tuning = Tuning with
        {
            GestureUploadBudget = new(1_000, 1), AdoptionBudget = new(0, 0), GestureAdoptionBudget = new(0, 0)
        };
        var stream = new Stream(tuning);
        stream.To(300, Healthy);
        Assert.Equal(new FrameWorkBudget(1_000, 1), stream.Last.UploadBudget);
        var sample = Healthy with { GestureActive = false, Reserve = Normal with { UploadLimitFactor = 0.5 } };
        Assert.Equal(new FrameWorkBudget(500, 1), stream.To(320, sample).UploadBudget);
        Assert.Equal(new FrameWorkBudget(500, 1), stream.To(800, sample).UploadBudget);
        Assert.Equal(new FrameWorkBudget(500, 5), stream.To(820, sample).UploadBudget);
    }

    [Fact]
    public void Concurrency_is_only_a_bounded_hint_and_zero_reserve_request_keeps_existing_workers()
    {
        var stream = new Stream(Tuning with { BackgroundConcurrency = 2, MaxBackgroundConcurrency = 3 });
        Assert.Equal(2, stream.To(1_000, Healthy).BackgroundConcurrencyHint);
        Assert.Equal(3, stream.To(2_000, Healthy with { Reserve = Normal with { CpuTileConcurrencyHint = 100 } }).BackgroundConcurrencyHint);
        Assert.Equal(2, stream.To(3_000, Healthy).BackgroundConcurrencyHint);
    }

    [Fact]
    public void Byte_ceiling_handles_long_max_value_without_overflow()
    {
        var ceiling = new FrameWorkBudget(long.MaxValue, int.MaxValue);
        var tuning = Tuning with { UploadBudget = ceiling, GestureUploadBudget = ceiling };
        var stream = new Stream(tuning);
        Assert.Equal(ceiling, stream.To(1_000, Healthy).UploadBudget);
        var plan = stream.To(2_000, Healthy with { Reserve = Normal with { UploadLimitFactor = 0.5 } });
        Assert.InRange(plan.UploadBudget.Bytes, 0, long.MaxValue / 2 + 1);
        Assert.Equal(int.MaxValue / 2, plan.UploadBudget.Count);
    }
}
