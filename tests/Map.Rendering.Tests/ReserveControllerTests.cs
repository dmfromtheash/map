using Map.Rendering.Reserve;

namespace Map.Rendering.Tests;

public sealed class ReserveControllerTests
{
    private static RenderHealthSample Healthy => new(20, 4, 6, true, true, 0, 50, 100, false, 20);
    private static RenderHealthSample Hot => Healthy with { GpuMs = 17 };

    private sealed class Stream(ReserveTuning? tuning = null)
    {
        public ReserveController Controller { get; } = new(tuning);
        public double Now { get; private set; }
        public double LastTime => Now - 20;
        public ReserveDecision Last { get; private set; }

        public ReserveDecision Tick(RenderHealthSample sample)
        {
            Last = Controller.Observe(sample, Now);
            Now += 20;
            return Last;
        }

        public ReserveDecision Run(double durationMs, RenderHealthSample sample)
        {
            double end = Now + durationMs;
            while (Now < end) Tick(sample);
            return Last;
        }

        public void Until(ReserveState state, RenderHealthSample sample, double timeoutMs = 60_000)
        {
            double end = Now + timeoutMs;
            do
            {
                Tick(sample);
                if (Last.State == state) return;
            } while (Now < end);
            Assert.Fail($"Expected {state}, got {Last.State} at {LastTime} ms");
        }
    }

    [Fact]
    public void Steady_healthy_frames_finish_probing_and_stay_normal()
    {
        var stream = new Stream();
        Assert.Equal(ReserveState.Probing, stream.Tick(Healthy).State);
        stream.Run(1_000, Healthy);
        for (int i = 0; i < 2_000; i++)
        {
            var decision = stream.Tick(Healthy);
            Assert.Equal(ReserveState.Normal, decision.State);
            Assert.Equal(ReserveReasons.None, decision.Reasons);
            Assert.Equal(0, decision.AssistLevel);
            Assert.Equal(1, decision.UploadLimitFactor);
            Assert.Equal(1, decision.GestureResolutionScale);
            Assert.False(decision.KeepCameraFrameLonger);
            Assert.False(decision.SimplerEffects);
            Assert.False(decision.StopPrefetch);
        }
    }

    [Fact]
    public void Two_hot_gpu_windows_enter_assistance_and_healthy_windows_step_back_slowly()
    {
        var stream = new Stream();
        stream.Run(2_000, Healthy);
        Assert.Equal(ReserveState.Normal, stream.Run(2_000, Hot).State);
        var assisted = stream.Tick(Hot); // Window two ends at 4000 ms.
        Assert.Equal(ReserveState.Assisted, assisted.State);
        Assert.Equal(1, assisted.AssistLevel);
        Assert.True(assisted.Reasons.HasFlag(ReserveReasons.GpuCost));
        Assert.True(assisted.UploadLimitFactor < 1);
        Assert.True(assisted.GestureResolutionScale < 1);
        Assert.Equal(ReserveState.Assisted, stream.Run(3_000, Healthy).State);
        stream.Until(ReserveState.Normal, Healthy);
        Assert.True(stream.LastTime >= 9_000);
        Assert.Equal(ReserveReasons.None, stream.Last.Reasons);
        Assert.Equal(1, stream.Last.GestureResolutionScale);
    }

    [Fact]
    public void P95_uses_tail_cost_not_average_and_ignores_isolated_spikes()
    {
        var stream = new Stream();
        stream.Run(2_000, Healthy);
        // Four of fifty frames (8%) exceed the threshold; mean GPU cost stays low.
        for (int window = 0; window < 2; window++)
            for (int frame = 0; frame < 50; frame++) stream.Tick(frame < 4 ? Hot : Healthy);
        Assert.Equal(ReserveState.Assisted, stream.Tick(Healthy).State);

        var isolated = new Stream();
        isolated.Run(2_000, Healthy);
        for (int frame = 0; frame < 500; frame++) isolated.Tick(frame % 50 == 0 ? Hot : Healthy);
        Assert.Equal(ReserveState.Normal, isolated.Last.State);
    }

    [Fact]
    public void Alternating_hot_and_healthy_windows_do_not_flap()
    {
        var stream = new Stream();
        stream.Run(2_000, Healthy);
        for (int i = 0; i < 20; i++) stream.Run(1_000, i % 2 == 0 ? Hot : Healthy);
        Assert.Equal(ReserveState.Normal, stream.Last.State);
        stream.Run(3_000, Hot);
        Assert.Equal(ReserveState.Assisted, stream.Last.State);
        for (int i = 0; i < 30; i++)
        {
            var decision = stream.Run(1_000, i % 2 == 0 ? Healthy : Hot);
            Assert.Equal(ReserveState.Assisted, decision.State);
            Assert.InRange(decision.AssistLevel, 1, 3);
        }
    }

    [Fact]
    public void Device_loss_is_immediate_and_recovered_device_has_a_long_cooldown()
    {
        var stream = new Stream();
        stream.Run(2_000, Healthy);
        double lostAt = stream.Now;
        var lost = stream.Tick(Healthy with { DeviceLost = true, GpuTimeValid = false });
        Assert.Equal(ReserveState.Recovering, lost.State);
        Assert.True(lost.Reasons.HasFlag(ReserveReasons.DeviceFailure));
        Assert.Equal(3, lost.AssistLevel);
        Assert.Equal(ReserveState.Recovering, stream.Run(200, Healthy with { GpuTimeValid = false }).State);
        Assert.Equal(ReserveState.Assisted, stream.Tick(Healthy).State);
        Assert.Equal(ReserveState.Assisted, stream.Run(29_000, Healthy).State);
        stream.Until(ReserveState.Normal, Healthy);
        Assert.True(stream.LastTime >= lostAt + new ReserveTuning().DeviceCooldownMs);
        Assert.False(stream.Last.Reasons.HasFlag(ReserveReasons.DeviceFailure));
    }

    [Fact]
    public void Missing_gpu_at_start_is_software_but_invalid_timer_is_probing()
    {
        var software = new Stream();
        var decision = software.Tick(Healthy with { GpuAvailable = false, GpuTimeValid = false });
        Assert.Equal(ReserveState.Software, decision.State);
        Assert.Equal(3, decision.AssistLevel);
        Assert.True(decision.Reasons.HasFlag(ReserveReasons.WeakProfile));
        Assert.False(decision.Reasons.HasFlag(ReserveReasons.UnknownTelemetry));
        var unknown = new Stream();
        decision = unknown.Run(20_000, Healthy with { GpuTimeValid = false });
        Assert.Equal(ReserveState.Probing, decision.State);
        Assert.True(decision.Reasons.HasFlag(ReserveReasons.UnknownTelemetry));
    }

    [Fact]
    public void Software_only_probes_an_available_device_after_cooldown()
    {
        var tuning = new ReserveTuning { SoftwareProbeCooldownMs = 3_000 };
        var stream = new Stream(tuning);
        stream.Tick(Healthy with { GpuAvailable = false });
        Assert.Equal(ReserveState.Software, stream.Run(2_980, Healthy).State);
        Assert.Equal(ReserveState.RecoveryProbe, stream.Tick(Healthy).State);
        Assert.True(stream.Last.GestureResolutionScale < 1);
        Assert.Equal(ReserveState.Software, stream.Tick(Healthy with { GpuTimeValid = false }).State);
        stream.Until(ReserveState.RecoveryProbe, Healthy, 5_000);
        stream.Until(ReserveState.Assisted, Healthy, 5_000);
        Assert.True(stream.Last.GestureResolutionScale < 1);
        stream.Until(ReserveState.Normal, Healthy);
    }

    [Fact]
    public void Unknown_gpu_does_not_release_assistance_or_count_as_a_second_hot_window()
    {
        var stream = new Stream();
        stream.Run(2_000, Healthy);
        stream.Run(1_000, Hot);
        stream.Run(1_000, Healthy with { GpuTimeValid = false });
        stream.Run(1_000, Hot);
        Assert.Equal(ReserveState.Normal, stream.Tick(Hot).State);
        stream.Run(2_000, Hot);
        Assert.Equal(ReserveState.Assisted, stream.Last.State);
        var decision = stream.Run(60_000, Healthy with { GpuTimeValid = false });
        Assert.Equal(ReserveState.Assisted, decision.State);
        Assert.True(decision.Reasons.HasFlag(ReserveReasons.UnknownTelemetry));
        Assert.True(decision.Reasons.HasFlag(ReserveReasons.GpuCost));
    }

    [Fact]
    public void Unknown_gpu_does_not_hide_independently_measured_pacing_and_upload_pressure()
    {
        var stream = new Stream();
        stream.Run(2_000, Healthy);
        var decision = stream.Run(2_020, Healthy with
        {
            GpuTimeValid = false, FrameIntervalMs = 40, AdoptionBytes = 3 * 1024 * 1024
        });
        Assert.Equal(ReserveState.Assisted, decision.State);
        Assert.True(decision.Reasons.HasFlag(ReserveReasons.UnknownTelemetry));
        Assert.True(decision.Reasons.HasFlag(ReserveReasons.Pacing));
        Assert.True(decision.Reasons.HasFlag(ReserveReasons.UploadPressure));
        Assert.False(decision.Reasons.HasFlag(ReserveReasons.GpuCost));
        var startup = new Stream();
        decision = startup.Run(2_020, Healthy with { GpuTimeValid = false, FrameIntervalMs = 40 });
        Assert.Equal(ReserveState.Assisted, decision.State);
        Assert.True(decision.Reasons.HasFlag(ReserveReasons.UnknownTelemetry));
    }

    [Fact]
    public void Memory_pressure_alone_stops_prefetch_without_visual_requests()
    {
        var stream = new Stream();
        stream.Run(2_000, Healthy);
        var decision = stream.Tick(Healthy with { VramUsageBytes = 91 });
        Assert.Equal(ReserveState.Assisted, decision.State);
        Assert.Equal(ReserveReasons.MemoryPressure, decision.Reasons);
        Assert.True(decision.StopPrefetch);
        Assert.Equal(1, decision.UploadLimitFactor);
        Assert.Equal(1, decision.GestureResolutionScale);
        Assert.False(decision.KeepCameraFrameLonger);
        Assert.False(decision.SimplerEffects);
        Assert.Equal(0, decision.CpuTileConcurrencyHint);
        Assert.True(stream.Tick(Healthy with { VramUsageBytes = 80 }).StopPrefetch);
        Assert.True(stream.Tick(Healthy with { VramUsageBytes = null, VramBudgetBytes = null }).StopPrefetch);
        Assert.True(stream.Tick(Healthy with { VramUsageBytes = 75 }).StopPrefetch);
        Assert.False(stream.Tick(Healthy with { VramUsageBytes = 74 }).StopPrefetch);
        stream.Until(ReserveState.Normal, Healthy);
    }

    [Fact]
    public void Reentry_delays_double_from_five_seconds_to_sixty_seconds()
    {
        var stream = new Stream();
        stream.Run(2_000, Healthy);
        foreach (double expectedDelay in new double[] { 5_000, 10_000, 20_000, 40_000, 60_000, 60_000 })
        {
            stream.Until(ReserveState.Assisted, Hot, 70_000);
            stream.Until(ReserveState.Normal, Healthy);
            double exit = stream.LastTime;
            double deadline = stream.Controller.NextAssistanceAllowedAtMs;
            Assert.Equal(expectedDelay, deadline - exit);
            while (stream.Now < deadline)
                Assert.Equal(ReserveState.Normal, stream.Tick(Hot).State);
            Assert.Equal(ReserveState.Assisted, stream.Tick(Hot).State);
            // The next iteration measures the episode entered at this deadline.
        }
    }

    [Fact]
    public void Pacing_uses_gesture_miss_fraction_and_ten_seconds_of_good_gestures_to_release()
    {
        var stream = new Stream(new ReserveTuning { EscalationDwellMs = 30_000, ImprovementDwellMs = 30_000 });
        stream.Run(2_000, Healthy);
        for (int i = 0; i < 50; i++) stream.Tick(Healthy with { FrameIntervalMs = i < 3 ? 31 : 20 });
        var decision = stream.Tick(Healthy);
        Assert.True(decision.Reasons.HasFlag(ReserveReasons.Pacing));
        Assert.False(decision.Reasons.HasFlag(ReserveReasons.GpuCost));
        Assert.True(stream.Run(9_000, Healthy).Reasons.HasFlag(ReserveReasons.Pacing));
        Assert.False(stream.Run(2_000, Healthy).Reasons.HasFlag(ReserveReasons.Pacing));

        var idle = new Stream();
        Assert.Equal(ReserveState.Normal, idle.Run(20_000, Healthy with { GestureActive = false, FrameIntervalMs = 40 }).State);
    }

    [Fact]
    public void Measured_healthy_pacing_at_rest_can_release_gesture_pressure()
    {
        var stream = new Stream();
        stream.Run(2_000, Healthy);
        stream.Run(1_020, Healthy with { FrameIntervalMs = 40 });
        Assert.True(stream.Last.Reasons.HasFlag(ReserveReasons.Pacing));
        stream.Until(ReserveState.Normal, Healthy with { GestureActive = false });
        Assert.True(stream.LastTime >= 13_000);
    }

    [Fact]
    public void Idle_frames_do_not_dilute_bad_gesture_pacing_when_releasing_pressure()
    {
        var controller = new ReserveController();
        for (int frame = 0; frame <= 4_000; frame++)
        {
            bool gesture = frame % 200 == 0;
            var decision = controller.Observe(Healthy with
            {
                RefreshPeriodMs = 5, GpuMs = 1, CpuWorkMs = 1,
                GestureActive = gesture, FrameIntervalMs = gesture ? 10 : 5
            }, frame * 5);
            // Only 0.5% of all frames miss, but every observed gesture frame misses.
            if (frame >= 200) Assert.True(decision.Reasons.HasFlag(ReserveReasons.Pacing));
        }
    }

    [Fact]
    public void Requests_stay_active_until_steps_are_released_at_least_five_seconds_apart()
    {
        var stream = new Stream();
        stream.Run(2_000, Healthy);
        stream.Run(6_020, Healthy with { WeakProfile = true });
        Assert.Equal(3, stream.Last.AssistLevel);
        var healthy = stream.Tick(Healthy);
        Assert.Equal(3, healthy.AssistLevel);
        Assert.True(healthy.GestureResolutionScale < 1);
        double lastStep = 8_000; // Weak profile entered at 2000; up at 5000 and 8000.
        int previous = healthy.AssistLevel;
        while (stream.Last.State != ReserveState.Normal && stream.Now < 30_000)
        {
            var decision = stream.Tick(Healthy);
            if (decision.AssistLevel == previous) continue;
            Assert.Equal(previous - 1, decision.AssistLevel);
            Assert.True(stream.LastTime - lastStep >= 5_000);
            lastStep = stream.LastTime;
            previous = decision.AssistLevel;
        }
        Assert.Equal(ReserveState.Normal, stream.Last.State);
        Assert.Equal(23_000, lastStep);
    }

    [Fact]
    public void Device_failure_bypasses_reentry_delay_and_missing_device_can_report_software()
    {
        var stream = new Stream();
        stream.Run(2_000, Healthy);
        stream.Until(ReserveState.Assisted, Hot);
        stream.Until(ReserveState.Normal, Healthy);
        Assert.True(stream.Now < stream.Controller.NextAssistanceAllowedAtMs);
        Assert.Equal(ReserveState.Recovering, stream.Tick(Healthy with { DeviceLost = true }).State);
        var software = stream.Tick(Healthy with { GpuAvailable = false, GpuTimeValid = false });
        Assert.Equal(ReserveState.Software, software.State);
        Assert.True(software.Reasons.HasFlag(ReserveReasons.DeviceFailure));
        Assert.Equal(ReserveState.Software, stream.Run(30_000, Healthy).State);
        stream.Until(ReserveState.RecoveryProbe, Healthy, 31_000);
        stream.Until(ReserveState.Assisted, Healthy, 5_000);
        stream.Until(ReserveState.Normal, Healthy);
    }

    [Fact]
    public void Memory_suppression_is_immediate_even_during_reentry_delay()
    {
        var stream = new Stream();
        stream.Run(2_000, Healthy);
        stream.Until(ReserveState.Assisted, Hot);
        stream.Until(ReserveState.Normal, Healthy);
        var decision = stream.Tick(Healthy with { VramUsageBytes = 95 });
        Assert.Equal(ReserveState.Normal, decision.State);
        Assert.True(decision.StopPrefetch);
        Assert.True(decision.Reasons.HasFlag(ReserveReasons.MemoryPressure));
    }

    [Fact]
    public void Invalid_sample_inside_a_healthy_window_blocks_a_due_step_down()
    {
        var stream = new Stream();
        stream.Run(2_000, Healthy);
        stream.Run(1_000, Healthy with { WeakProfile = true });
        stream.Run(3_000, Healthy);
        // The entry step would normally be released at 7000 ms.
        stream.Tick(Healthy with { GpuTimeValid = false });
        Assert.Equal(ReserveState.Assisted, stream.Run(1_100, Healthy).State);
        stream.Until(ReserveState.Normal, Healthy);
    }

    [Fact]
    public void Upload_pressure_has_its_own_reason_and_does_not_reduce_resolution()
    {
        var stream = new Stream();
        stream.Run(2_000, Healthy);
        var decision = stream.Run(2_020, Healthy with { AdoptionBytes = 3 * 1024 * 1024 });
        Assert.Equal(ReserveState.Assisted, decision.State);
        Assert.Equal(ReserveReasons.UploadPressure, decision.Reasons);
        Assert.True(decision.UploadLimitFactor < 1);
        Assert.Equal(1, decision.GestureResolutionScale);
        Assert.False(decision.KeepCameraFrameLonger);
        stream.Until(ReserveState.Normal, Healthy);
    }

    [Fact]
    public void Weak_profile_steps_up_after_three_seconds_and_limits_cpu_help_when_cpu_is_busy()
    {
        var stream = new Stream();
        stream.Run(2_000, Healthy);
        var weak = Healthy with { WeakProfile = true };
        Assert.Equal(1, stream.Tick(weak).AssistLevel);
        Assert.Equal(1, stream.Run(2_980, weak).AssistLevel);
        Assert.Equal(2, stream.Tick(weak).AssistLevel);
        Assert.Equal(2, stream.Last.CpuTileConcurrencyHint);
        Assert.Equal(1, stream.Tick(weak with { CpuWorkMs = 19 }).CpuTileConcurrencyHint);
        stream.Run(3_000, weak);
        Assert.Equal(3, stream.Last.AssistLevel);
        Assert.Equal(4, stream.Last.CpuTileConcurrencyHint);
        Assert.True(stream.Last.SimplerEffects);
        var idle = stream.Tick(weak with { GestureActive = false });
        Assert.Equal(1, idle.GestureResolutionScale);
        Assert.False(idle.SimplerEffects);
    }

    [Fact]
    public void Refresh_period_is_used_instead_of_a_fixed_sixty_hertz_budget()
    {
        var stream = new Stream();
        Assert.Equal(ReserveState.Normal, stream.Run(10_000, Healthy with { RefreshPeriodMs = 40, GpuMs = 20 }).State);
        Assert.Equal(ReserveState.Assisted, stream.Run(3_000, Healthy with { RefreshPeriodMs = 10, GpuMs = 8 }).State);
    }

    [Fact]
    public void Sampling_gap_and_window_overflow_are_unknown_and_do_not_fabricate_health()
    {
        var controller = new ReserveController(new ReserveTuning { MaxSamplesPerWindow = 2 });
        controller.Observe(Healthy, 0);
        controller.Observe(Healthy, 20);
        Assert.True(controller.Observe(Healthy, 40).Reasons.HasFlag(ReserveReasons.UnknownTelemetry));
        Assert.Equal(ReserveState.Probing, controller.Observe(Healthy, 1_000).State);
        Assert.Equal(ReserveState.Probing, controller.Observe(Healthy, 100_000).State);

        var stream = new Stream();
        stream.Run(2_000, Healthy);
        stream.Run(3_000, Hot);
        Assert.Equal(ReserveState.Assisted, stream.Last.State);
        var decision = stream.Controller.Observe(Healthy, 100_000);
        Assert.Equal(ReserveState.Assisted, decision.State);
        Assert.True(decision.Reasons.HasFlag(ReserveReasons.UnknownTelemetry));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void Invalid_gpu_values_are_unknown_even_if_marked_valid(double gpuMs)
    {
        var stream = new Stream();
        var decision = stream.Run(10_000, Healthy with { GpuMs = gpuMs });
        Assert.Equal(ReserveState.Probing, decision.State);
        Assert.True(decision.Reasons.HasFlag(ReserveReasons.UnknownTelemetry));
    }

    [Fact]
    public void Invalid_time_is_rejected_without_changing_the_state()
    {
        var controller = new ReserveController();
        controller.Observe(Healthy, 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Observe(Hot, 99));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Observe(Hot, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Observe(Hot, double.PositiveInfinity));
        Assert.Equal(ReserveState.Probing, controller.Observe(Healthy, 120).State);
    }

    [Fact]
    public void Tuning_is_validated_and_custom_thresholds_change_the_policy()
    {
        Assert.Throws<ArgumentException>(() => new ReserveController(new ReserveTuning { WindowMs = 0 }));
        Assert.Throws<ArgumentException>(() => new ReserveController(new ReserveTuning { GpuExitRatio = 0.8 }));
        Assert.Throws<ArgumentException>(() => new ReserveController(new ReserveTuning { Level1UploadFactor = double.NaN }));
        var stream = new Stream(new ReserveTuning { GpuEnterRatio = 0.9 });
        Assert.Equal(ReserveState.Normal, stream.Run(20_000, Hot).State);
    }

    [Fact]
    public void Observation_does_not_allocate_per_frame_after_warmup()
    {
        var controller = new ReserveController();
        var sample = Healthy;
        double now = 0;
        // Tiered JIT promotion (and OSR of the measuring loop) allocates on this thread while it settles;
        // the controller itself must reach zero once compilation is stable (as the journal check, 0e2aa37).
        long allocated = -1;
        for (int attempt = 0; attempt < 20 && allocated != 0; attempt++)
        {
            for (int i = 0; i < 2_000; i++, now += 20) controller.Observe(sample, now);
            if (attempt > 0) Thread.Sleep(150);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100_000; i++, now += 20) controller.Observe(sample, now);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0L, allocated);
    }
}
