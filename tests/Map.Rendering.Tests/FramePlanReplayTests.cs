using Map.Rendering.Reserve;
using static Map.Rendering.Tests.FramePlanTestData;
using Stream = Map.Rendering.Tests.FramePlanTestData.Stream;

namespace Map.Rendering.Tests;

public sealed class FramePlanReplayTests
{
    // Recorded policy-input fixture: explicit timestamps, refresh changes, missing telemetry,
    // a reserve request, settle, and export. This is synthetic, not a GPU performance capture.
    private static readonly (double At, double Cost, double Refresh, bool Gesture, bool Exact, ReserveDecision Reserve)[] Recording =
    [
        (0, 8, 10, true, false, Normal),
        (100, 8, 10, true, false, Normal),
        (200, 8, 10, true, false, Normal),
        (300, 8, 10, true, false, Normal),
        (400, 8, 10, true, false, Normal),
        (500, 8, 10, true, false, Normal),
        (600, 8, 10, true, false, Normal),
        (700, double.NaN, 10, true, false, Normal),
        (800, 8, 20, true, false, Normal),
        (900, 8, 20, true, false, Normal with { UploadLimitFactor = 0.25 }),
        (920, 8, 20, false, false, Normal with { GestureResolutionScale = 0.5, KeepCameraFrameLonger = true }),
        (1_000, 4, 20, true, false, Normal),
        (1_100, 4, 20, true, false, Normal),
        (1_200, 4, 20, true, false, Normal),
        (1_300, 4, 20, true, false, Normal with { GestureResolutionScale = 0.5 }),
        (1_400, 4, 20, true, true, Normal with { GestureResolutionScale = 0.5 }),
        (10_000, 4, 20, false, false, Normal)
    ];

    [Fact]
    public void Recorded_sequence_replays_identically_independent_of_clock_origin()
    {
        var first = Replay(0);
        var second = Replay(1_234_567);
        Assert.Equal(first, second);
        Assert.Equal(new double[] { 1, 1, 1, 0.75, 0.75, 0.75, 0.5, 0.5, 0.5, 0.5, 1, 1, 1, 1, 0.75, 1, 1 },
            first.Select(p => p.SceneResolutionScale).ToArray());
        Assert.True(first[10].RequireExact); // gesture stop
        Assert.True(first[15].RequireExact); // export during gesture
        Assert.Equal(first[9].UploadBudget, first[10].UploadBudget); // no budget recovery smuggled into settle
    }

    private static FramePlan[] Replay(double origin)
    {
        var planner = new FramePlanner(Tuning);
        return Recording.Select(row => planner.Plan(new(row.Reserve, row.Cost, row.Refresh, row.Gesture, row.Exact), row.At + origin)).ToArray();
    }

    [Fact]
    public void Reserve_controller_and_governor_replay_together_without_renderer_dependencies()
    {
        static FramePlan[] Run()
        {
            var reserve = new ReserveController();
            var planner = new FramePlanner();
            // Reserve recovery and the governor's serialized resource recovery both have dwell.
            var plans = new FramePlan[2_501];
            for (int frame = 0; frame < plans.Length; frame++)
            {
                bool gesture = frame < 400 || frame >= 500;
                bool hot = frame < 400;
                double refresh = frame < 500 ? 10 : 20;
                var health = new RenderHealthSample(refresh, 3, hot ? 8 : 2, true, gesture, 0, null, null, false, refresh);
                double now = frame * 20;
                plans[frame] = planner.Plan(new(reserve.Observe(health, now), hot ? 8 : 3, refresh, gesture), now);
            }
            return plans;
        }
        var first = Run();
        Assert.Equal(first, Run());
        Assert.Equal(0.5, first[399].SceneResolutionScale);
        Assert.True(first[400].RequireExact);
        Assert.True(first[499].RequireExact);
        Assert.True(first[^1].RequireExact, first[^1].ToString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(99)]
    public void Invalid_time_leaves_clock_plan_and_diagnostics_unchanged(double invalid)
    {
        var planner = new FramePlanner(Tuning);
        var control = new FramePlanner(Tuning);
        var sample = Healthy with { Reserve = Normal with { UploadLimitFactor = 0.5 } };
        var expected = planner.Plan(sample, 100);
        control.Plan(sample, 100);
        var knob = planner.LastChangedKnob;
        Assert.Throws<ArgumentOutOfRangeException>(() => planner.Plan(Hot, invalid));
        Assert.Equal(expected, planner.Current);
        Assert.Equal(knob, planner.LastChangedKnob);
        for (int t = 120; t <= 2_000; t += 20)
            Assert.Equal(control.Plan(sample, t), planner.Plan(sample, t));
    }

    [Theory]
    [InlineData(double.NaN, 1, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(2, 1, 0)]
    [InlineData(1, double.PositiveInfinity, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 2, 0)]
    [InlineData(1, 1, -1)]
    public void Invalid_requests_are_rejected_before_state_changes(double upload, double scale, int workers)
    {
        var planner = new FramePlanner(Tuning);
        var before = planner.Plan(Healthy, 100);
        Assert.Throws<ArgumentException>(() => planner.Plan(Healthy with { Reserve = Normal with
        {
            UploadLimitFactor = upload, GestureResolutionScale = scale, CpuTileConcurrencyHint = workers
        } }, 1_000));
        Assert.Equal(before, planner.Plan(Healthy, 120));
    }

    public static IEnumerable<object[]> InvalidTunings()
    {
        yield return [Tuning with { WindowMs = 0 }];
        yield return [Tuning with { EnterRatio = double.NaN }];
        yield return [Tuning with { ExitRatio = 0.8 }];
        yield return [Tuning with { PressureWindows = 0 }];
        yield return [Tuning with { HealthyReleaseMs = double.PositiveInfinity }];
        yield return [Tuning with { WorseningPeriodMs = 0 }];
        yield return [Tuning with { RecoveryPeriodMs = 300 }];
        yield return [Tuning with { ReducedResolutionScale = 1 }];
        yield return [Tuning with { MinimumResolutionScale = 0.8 }];
        yield return [Tuning with { BusyTransferFactor = 2 }];
        yield return [Tuning with { UploadBudget = new(-1, 1) }];
        yield return [Tuning with { AdoptionBudget = new(1, -1) }];
        yield return [Tuning with { GestureUploadBudget = new(2_000, 10) }];
        yield return [Tuning with { GestureAdoptionBudget = new(2_000, 21) }];
        yield return [Tuning with { BackgroundConcurrency = 0 }];
        yield return [Tuning with { MaxBackgroundConcurrency = 0 }];
        yield return [Tuning with { BusyBackgroundConcurrency = 2 }];
    }

    [Theory]
    [MemberData(nameof(InvalidTunings))]
    public void Tuning_is_validated(FramePlanTuning tuning) =>
        Assert.Throws<ArgumentException>(() => new FramePlanner(tuning));

    [Fact]
    public void Custom_thresholds_control_policy_and_zero_budgets_remain_zero()
    {
        var tuning = Tuning with
        {
            EnterRatio = 0.9, ReducedResolutionScale = 0.8, MinimumResolutionScale = 0.6,
            UploadBudget = new(0, 0), GestureUploadBudget = new(0, 0)
        };
        var stream = new Stream(tuning);
        Assert.Equal(1, stream.To(2_000, Hot).SceneResolutionScale);
        Assert.Equal(0.8, stream.To(2_300, Hot with { FrameWorkMs = 9.5 }).SceneResolutionScale);
        Assert.Equal(0.6, stream.To(2_600, Hot with { FrameWorkMs = 9.5 }).SceneResolutionScale);
        Assert.Equal(new FrameWorkBudget(0, 0), stream.Last.UploadBudget);
    }
}
