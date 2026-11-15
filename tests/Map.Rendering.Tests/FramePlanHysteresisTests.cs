using Map.Rendering.Reserve;
using static Map.Rendering.Tests.FramePlanTestData;
using Stream = Map.Rendering.Tests.FramePlanTestData.Stream;

namespace Map.Rendering.Tests;

public sealed class FramePlanHysteresisTests
{
    [Fact]
    public void Enter_requires_two_hot_windows_and_each_discrete_step_obeys_dwell()
    {
        var stream = new Stream();
        Assert.Equal(1, stream.To(280, Hot).SceneResolutionScale);
        Assert.Equal(0.75, stream.To(300, Hot).SceneResolutionScale);
        Assert.Equal(0.75, stream.To(580, Hot).SceneResolutionScale);
        Assert.Equal(0.5, stream.To(600, Hot).SceneResolutionScale);
    }

    [Fact]
    public void Strong_reserve_request_also_uses_discrete_steps_and_minimum_dwell()
    {
        var stream = new Stream();
        var sample = Healthy with { Reserve = Normal with { GestureResolutionScale = 0.1 } };
        Assert.Equal(1, stream.To(280, sample).SceneResolutionScale);
        Assert.Equal(0.75, stream.To(300, sample).SceneResolutionScale);
        Assert.Equal(0.5, stream.To(600, sample).SceneResolutionScale);
    }

    [Fact]
    public void Exit_requires_sustained_low_cost_and_recovers_more_slowly_than_entry()
    {
        var stream = new Stream();
        stream.To(600, Hot);
        Assert.Equal(0.5, stream.To(1_080, Healthy).SceneResolutionScale);
        Assert.Equal(0.75, stream.To(1_100, Healthy).SceneResolutionScale);
        Assert.Equal(0.75, stream.To(1_580, Healthy).SceneResolutionScale);
        Assert.Equal(1, stream.To(1_600, Healthy).SceneResolutionScale);
        Assert.True(stream.Last.RequireExact);
    }

    [Fact]
    public void Threshold_noise_never_alternates_reduction_and_recovery()
    {
        var planner = new FramePlanner(Tuning);
        double previous = 1;
        int reductions = 0;
        for (int t = 0; t <= 4_000; t += 20)
        {
            var plan = planner.Plan(Hot with { FrameWorkMs = t % 40 == 0 ? 7.4 : 7.6 }, t);
            Assert.True(plan.SceneResolutionScale <= previous);
            if (plan.SceneResolutionScale < previous) reductions++;
            previous = plan.SceneResolutionScale;
        }
        Assert.Equal(2, reductions);
        // Noise around the EXIT threshold contains a non-healthy worst frame in every window.
        for (int t = 4_020; t <= 10_000; t += 20)
            Assert.Equal(0.5, planner.Plan(Healthy with { FrameWorkMs = t % 40 == 0 ? 5.4 : 5.6 }, t).SceneResolutionScale);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gesture_stop_or_force_exact_restores_both_visual_knobs_immediately(bool forceExact)
    {
        var stream = new Stream();
        var sample = Healthy with { Reserve = Normal with { GestureResolutionScale = 0.5, KeepCameraFrameLonger = true } };
        var reduced = stream.To(900, sample);
        Assert.Equal(0.5, reduced.SceneResolutionScale);
        Assert.True(reduced.UseCameraFrame);
        Assert.False(reduced.RequireExact);
        var exact = stream.To(920, sample with { GestureActive = forceExact, ForceExact = forceExact });
        Assert.Equal(1, exact.SceneResolutionScale);
        Assert.False(exact.UseCameraFrame);
        Assert.True(exact.RequireExact);
        Assert.Equal(FramePlanKnob.Exactness, stream.Planner.LastChangedKnob);
        Assert.Equal(2, ChangedKnobs(reduced, exact)); // documented safety exception
        Assert.Equal(reduced.UploadBudget, exact.UploadBudget);
        Assert.Equal(reduced.AdoptionBudget, exact.AdoptionBudget);
        // A new gesture does not inherit an expired full-resolution dwell deadline.
        Assert.Equal(1, stream.To(1_220, sample).SceneResolutionScale);
        Assert.Equal(0.75, stream.To(1_240, sample).SceneResolutionScale);
    }

    [Fact]
    public void Idle_and_force_exact_ignore_visual_requests_even_under_sustained_pressure()
    {
        var requests = Normal with { GestureResolutionScale = 0.5, KeepCameraFrameLonger = true };
        foreach (bool force in new[] { false, true })
        {
            var stream = new Stream();
            var plan = stream.To(5_000, Hot with { Reserve = requests, GestureActive = force, ForceExact = force });
            Assert.True(plan.RequireExact);
            Assert.False(plan.UseCameraFrame);
            Assert.Equal(1, plan.SceneResolutionScale);
            Assert.True(plan.StopPrefetch);
            Assert.True(plan.AdoptionBudget.Bytes < Tuning.AdoptionBudget.Bytes);
        }
    }

    [Fact]
    public void Worst_frame_not_average_controls_entry_and_refresh_period_is_per_sample()
    {
        var planner = new FramePlanner(Tuning);
        for (int t = 0; t <= 600; t += 20)
            planner.Plan(Healthy with { FrameWorkMs = t % 100 == 0 ? 9 : 1 }, t);
        Assert.Equal(0.5, planner.Current.SceneResolutionScale);
        var stream = new Stream();
        Assert.Equal(1, stream.To(2_000, Hot with { RefreshPeriodMs = 20 }).SceneResolutionScale);
        Assert.Equal(0.5, stream.To(3_000, Hot).SceneResolutionScale);
    }

    [Fact]
    public void Alternating_hot_and_healthy_windows_do_not_enter()
    {
        var planner = new FramePlanner(Tuning);
        for (int t = 0; t <= 4_000; t += 20)
        {
            var plan = planner.Plan(t / 100 % 2 == 0 ? Hot : Healthy, t);
            Assert.True(plan.RequireExact);
        }
    }

    [Theory]
    [InlineData(double.NaN, 10)]
    [InlineData(double.PositiveInfinity, 10)]
    [InlineData(-1, 10)]
    [InlineData(1, 0)]
    [InlineData(1, double.NaN)]
    [InlineData(1, double.PositiveInfinity)]
    [InlineData(double.MaxValue, double.Epsilon)]
    public void Unknown_cost_cannot_enter_or_recover(double cost, double refresh)
    {
        var unknown = Healthy with { FrameWorkMs = cost, RefreshPeriodMs = refresh };
        var initial = new Stream();
        Assert.True(initial.To(5_000, unknown).RequireExact);
        var reduced = new Stream();
        reduced.To(600, Hot);
        Assert.Equal(0.5, reduced.To(5_000, unknown).SceneResolutionScale);
    }

    [Fact]
    public void Sampling_gap_does_not_fabricate_healthy_time_or_hot_windows()
    {
        var stream = new Stream();
        stream.To(600, Hot);
        Assert.Equal(0.5, stream.Planner.Plan(Healthy, 100_000).SceneResolutionScale);
        Assert.Equal(0.5, stream.Planner.Plan(Healthy, 100_000).SceneResolutionScale);
        var planner = new FramePlanner(Tuning);
        planner.Plan(Hot, 0);
        Assert.True(planner.Plan(Hot, 100_000).RequireExact);
    }

    [Fact]
    public void Bad_sample_on_recovery_boundary_blocks_the_due_step()
    {
        var stream = new Stream();
        stream.To(600, Hot);
        stream.To(1_080, Healthy);
        Assert.Equal(0.5, stream.To(1_100, Healthy with { FrameWorkMs = double.NaN }).SceneResolutionScale);
        Assert.Equal(0.5, stream.To(1_280, Healthy).SceneResolutionScale);
        Assert.Equal(0.75, stream.To(1_400, Healthy).SceneResolutionScale);
    }
}
