using Map.Presentation.Services;

namespace Map.Presentation.Tests;

public sealed class ImageDemandQueueTests
{
    private static readonly ImageDemandKey Centre = new("centre"), Edge = new("edge"), Warm = new("warm");
    private static void Submit(ImageDemandQueue queue, ImageDemandKey key, long bytes = 64,
        DemandBin bin = DemandBin.Now, double distance = 0, int side = 64) =>
        queue.Submit(key, bytes, new(bin, distance), new(side, side));

    private static FitResult View(ImageDemandQueue queue, long frame, long shelf)
    {
        queue.BeginFrame(frame);
        Submit(queue, Centre);
        Submit(queue, Edge, distance: 10);
        return queue.Fit(shelf);
    }

    [Fact]
    public void Fits_without_bias_when_need_is_below_budget()
    {
        var q = new ImageDemandQueue();
        var fit = View(q, 1, 200);
        Assert.Equal(128, fit.NeedBytes);
        Assert.Equal(128, fit.GrantedBytes);
        Assert.Empty(fit.Cutoff);
        Assert.All(fit.Granted.Values, g => Assert.Equal(0, g.MipBias));
    }

    [Fact]
    public void Over_budget_drops_one_level_from_lowest_priority_first()
    {
        var q = new ImageDemandQueue();
        var fit = View(q, 1, 100);
        Assert.Equal(0, fit.Granted[Centre].MipBias);
        Assert.Equal(1, fit.Granted[Edge].MipBias);
        Assert.Equal(new ImageDemandKey("edge", 1), fit.Granted[Edge].Key);
        Assert.Equal(80, fit.GrantedBytes);
        Assert.Equal(128, fit.NeedBytes);
    }

    [Fact]
    public void Bias_never_goes_below_minimum_side_and_cutoff_takes_the_rest()
    {
        var q = new ImageDemandQueue();
        var fit = View(q, 1, 5);
        Assert.Equal(2, fit.Granted[Centre].MipBias);
        Assert.Equal(new DemandShape(16, 16), fit.Granted[Centre].Shape);
        Assert.Contains(Edge, fit.Cutoff);
        Assert.Single(q.Dispatch());
        Assert.False(q.IsProtected(Edge with { Level = 2 }));
        Assert.True(q.IsProtected(Centre with { Level = 2 }));
    }

    [Fact]
    public void Rectangular_shapes_do_not_shrink_either_side_below_the_minimum()
    {
        var q = new ImageDemandQueue();
        q.BeginFrame(1);
        q.Submit(Centre, 1024, new(DemandBin.Now, 0), new(256, 16));
        var fit = q.Fit(100);
        Assert.Empty(fit.Granted);
        Assert.Single(fit.Cutoff);
    }

    [Fact]
    public void Hysteresis_keeps_bias_until_need_falls_below_low_water()
    {
        var q = new ImageDemandQueue();
        Assert.Equal(1, View(q, 1, 100).Granted[Edge].MipBias);
        Assert.Equal(1, View(q, 2, 150).Granted[Edge].MipBias);
        Assert.Equal(0, View(q, 3, 200).Granted[Edge].MipBias);
    }

    [Fact]
    public void No_oscillation_when_need_hovers_between_low_and_high_water()
    {
        var q = new ImageDemandQueue();
        var original = View(q, 1, 100);
        for (var frame = 2; frame < 50; frame++)
        {
            var fit = View(q, frame, frame % 2 == 0 ? 145 : 160);
            Assert.Equal(original.Granted[Edge], fit.Granted[Edge]);
            Assert.Equal(original.Granted[Centre], fit.Granted[Centre]);
        }
    }

    [Fact]
    public void Removing_demand_lifts_bias_only_below_low_water()
    {
        var q = new ImageDemandQueue();
        View(q, 1, 100);
        q.BeginFrame(2);
        Submit(q, Edge, distance: 10);
        Assert.Equal(0, q.Fit(100).Granted[Edge].MipBias); // 64 < 70
    }

    [Fact]
    public void Min_priority_per_key_deduplicates_demand_and_keeps_now_before_soon()
    {
        var q = new ImageDemandQueue();
        q.BeginFrame(1);
        Submit(q, Centre, bin: DemandBin.Eventually, distance: 500);
        Submit(q, Edge, bin: DemandBin.Soon);
        Submit(q, Centre, bin: DemandBin.Now, distance: 20);
        Submit(q, Centre, bin: DemandBin.Soon);
        var fit = q.Fit(1000);
        Assert.Equal(128, fit.NeedBytes);
        Assert.Equal(new[] { Centre, Edge }, q.Dispatch());
        Assert.True(q.IsProtected(Centre));
        Assert.True(q.IsProtected(Edge));
    }

    [Fact]
    public void Priority_order_of_dispatch_and_adoption()
    {
        var q = new ImageDemandQueue();
        q.BeginFrame(1);
        Submit(q, Warm, bin: DemandBin.Eventually);
        Submit(q, Edge, bin: DemandBin.Soon);
        Submit(q, Centre, distance: 100);
        q.Fit(1000);
        var order = new[] { Centre, Edge, Warm };
        Assert.Equal(order, q.Dispatch());
        Assert.Equal(order, q.OrderReady([Warm, Edge, Centre, new("unwanted")]));
        Assert.False(q.IsProtected(Warm));
        Assert.Empty(q.Dispatch());
    }

    [Fact]
    public void Dispatch_bounds_outstanding_bytes_and_completion_releases_the_budget()
    {
        var q = new ImageDemandQueue(new(InFlightBytes: 100));
        View(q, 1, 1000);
        Assert.Equal(new[] { Centre }, q.Dispatch());
        Assert.Equal(64, q.InFlightBytes);
        Assert.Empty(q.Dispatch());
        q.Complete(Centre);
        Assert.Equal(new[] { Edge }, q.Dispatch());
        Assert.Equal(64, q.InFlightBytes);
    }

    [Theory]
    [InlineData(128, 256)]
    [InlineData(100, 80)]
    public void A_request_that_cannot_fit_does_not_starve_smaller_requests(long limit, long largeBytes)
    {
        var q = new ImageDemandQueue(new(InFlightBytes: limit));
        q.BeginFrame(1);
        Submit(q, Warm, bytes: 64);
        q.Fit(1000);
        Assert.Equal(new[] { Warm }, q.Dispatch());

        q.BeginFrame(2);
        Submit(q, Centre, bytes: largeBytes);
        Submit(q, Edge, bytes: 16, bin: DemandBin.Soon);
        q.Fit(1000);
        Assert.Equal(new[] { Edge }, q.Dispatch());
        Assert.Equal(80, q.InFlightBytes);
        Assert.InRange(q.InFlightBytes, 0, limit);
    }

    [Fact]
    public void Resident_granted_bytes_reduce_the_temporary_budget()
    {
        var q = new ImageDemandQueue();
        var fit = View(q, 1, 100);
        q.SetResident(Centre, fit.Granted[Centre].Bytes);
        Assert.Equal(new[] { fit.Granted[Edge].Key }, q.Dispatch());
        Assert.InRange(q.InFlightBytes, 0, 100 - fit.Granted[Centre].Bytes);
    }

    [Fact]
    public void Dispatch_limit_preserves_priority_and_refused_work_can_retry()
    {
        var q = new ImageDemandQueue(new(DispatchLimit: 1));
        View(q, 1, 1000);
        Assert.Equal(new[] { Centre }, q.Dispatch());
        q.Complete(Centre, resident: false);
        Assert.Equal(new[] { Centre }, q.Dispatch());
        q.Complete(Centre);
        Assert.Equal(new[] { Edge }, q.Dispatch());
    }

    [Fact]
    public void Skipped_by_time_budget_is_not_cancelled()
    {
        var q = new ImageDemandQueue();
        View(q, 1, 1000);
        q.Dispatch();
        for (var frame = 2; frame <= 9; frame++)
        {
            q.BeginFrame(frame);
            Submit(q, Centre); // the edge plan was skipped by the check budget
            q.Fit(1000);
            Assert.Empty(q.CancelUnwanted());
        }
        q.BeginFrame(10);
        Submit(q, Centre);
        q.Fit(1000);
        Assert.Equal(new[] { Edge }, q.CancelUnwanted());
        Assert.Equal(0, q.CancelledStillWanted);
    }

    [Fact]
    public void Biased_inflight_keys_are_retained_and_old_work_still_counts_against_dispatch()
    {
        var q = new ImageDemandQueue(new(InFlightBytes: 80));
        var fit = View(q, 1, 100);
        q.Dispatch();
        q.BeginFrame(2);
        Submit(q, Warm);
        q.Fit(1000);
        Assert.Empty(q.Dispatch());
        Assert.Empty(q.CancelUnwanted());
        Assert.Equal(80, q.InFlightBytes);
        q.Complete(fit.Granted[Edge].Key, resident: false);
        Assert.Empty(q.Dispatch()); // 64 still outstanding, only 16 free
        q.Complete(Centre, resident: false);
        Assert.Equal(new[] { Warm }, q.Dispatch());
    }

    [Fact]
    public void More_budget_never_gives_less_detail_for_any_key()
    {
        var previous = new Dictionary<ImageDemandKey, int>();
        for (var budget = 0; budget <= 400; budget++)
        {
            var q = new ImageDemandQueue();
            q.BeginFrame(1);
            Submit(q, Centre);
            Submit(q, Edge, distance: 10);
            Submit(q, Warm, bin: DemandBin.Soon);
            var fit = q.Fit(budget);
            foreach (var key in new[] { Centre, Edge, Warm })
            {
                var detail = fit.Granted.TryGetValue(key, out var grant) ? grant.MipBias : int.MaxValue;
                if (previous.TryGetValue(key, out var before)) Assert.True(detail <= before, $"budget={budget}, key={key}");
                previous[key] = detail;
            }
            Assert.InRange(fit.GrantedBytes, 0, (long)(budget * 0.9m));
        }
    }

    [Fact]
    public void Deterministic_replay_of_recorded_frames_is_independent_of_submit_order()
    {
        var a = new ImageDemandQueue();
        var b = new ImageDemandQueue();
        long[] shelves = [100, 150, 145, 200, 5, 150, 200];
        for (var i = 0; i < shelves.Length; i++)
        {
            a.BeginFrame(i); b.BeginFrame(i);
            Submit(a, Centre); Submit(a, Edge, distance: 10);
            Submit(b, Edge, distance: 10); Submit(b, Centre);
            var fa = a.Fit(shelves[i]); var fb = b.Fit(shelves[i]);
            Assert.Equal(fa.NeedBytes, fb.NeedBytes);
            Assert.Equal(fa.GrantedBytes, fb.GrantedBytes);
            Assert.Equal(fa.Granted.OrderBy(p => p.Key.Identity), fb.Granted.OrderBy(p => p.Key.Identity));
            Assert.Equal(fa.Cutoff.OrderBy(k => k.Identity), fb.Cutoff.OrderBy(k => k.Identity));
            Assert.Equal(a.Dispatch(), b.Dispatch());
            Assert.Equal(a.CancelUnwanted(), b.CancelUnwanted());
        }
    }

    [Fact]
    public void A_shrunk_view_has_no_protected_keys_from_the_previous_fit()
    {
        var q = new ImageDemandQueue();
        View(q, 1, 200);
        Assert.True(q.IsProtected(Edge));
        q.BeginFrame(2);
        Assert.False(q.IsProtected(Edge));
        q.Fit(200);
        Assert.False(q.IsProtected(Centre));
        Assert.Empty(q.Dispatch());
    }

    [Fact]
    public void Invalid_inputs_and_dispatch_before_fit_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageDemandQueue(new(LowWater: .95)));
        var q = new ImageDemandQueue();
        Assert.Throws<InvalidOperationException>(() => q.Fit(100));
        q.BeginFrame(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => Submit(q, Centre, bytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Submit(q, Centre, distance: double.NaN));
        Assert.Throws<InvalidOperationException>(() => q.Dispatch());
        Submit(q, Centre);
        Assert.Throws<ArgumentException>(() => Submit(q, Centre, bytes: 32));
        Assert.Throws<ArgumentOutOfRangeException>(() => q.BeginFrame(1));
    }

    [Fact]
    public void Desired_levels_mapping_to_one_granted_key_count_and_dispatch_its_bytes_once()
    {
        var q = new ImageDemandQueue();
        q.BeginFrame(1);
        var fine = new ImageDemandKey("asset", 0);
        var coarse = new ImageDemandKey("asset", 1);
        q.Submit(fine, 64, new(DemandBin.Soon, 0), new(64, 64));
        q.Submit(coarse, 16, new(DemandBin.Now, 0), new(32, 32));
        var fit = q.Fit(40);
        Assert.Equal(80, fit.NeedBytes);
        Assert.Equal(16, fit.GrantedBytes);
        Assert.Equal(coarse, fit.Granted[fine].Key);
        Assert.Equal(coarse, fit.Granted[coarse].Key);
        Assert.Equal(new[] { coarse }, q.Dispatch());
        Assert.Equal(16, q.InFlightBytes);
    }

    [Fact]
    public void Increasing_budget_on_the_same_queue_never_reduces_detail()
    {
        var q = new ImageDemandQueue();
        int centre = int.MaxValue, edge = int.MaxValue;
        for (var budget = 1; budget <= 200; budget++)
        {
            var fit = View(q, budget, budget);
            var nextCentre = fit.Granted.TryGetValue(Centre, out var c) ? c.MipBias : int.MaxValue;
            var nextEdge = fit.Granted.TryGetValue(Edge, out var e) ? e.MipBias : int.MaxValue;
            Assert.True(nextCentre <= centre);
            Assert.True(nextEdge <= edge);
            (centre, edge) = (nextCentre, nextEdge);
        }
        Assert.Equal(0, centre);
        Assert.Equal(0, edge);
    }

    [Fact]
    public void Equal_priority_ties_are_deterministic_and_tuning_controls_minimum_and_watermarks()
    {
        var q = new ImageDemandQueue(new(HighWater: 1, LowWater: .5, MinimumSide: 32));
        q.BeginFrame(1);
        Submit(q, Edge);
        Submit(q, Centre);
        var fit = q.Fit(16);
        Assert.Equal(16, fit.GrantedBytes);
        Assert.Equal(new DemandShape(32, 32), fit.Granted[Centre].Shape);
        Assert.Contains(Edge, fit.Cutoff);
    }
}
