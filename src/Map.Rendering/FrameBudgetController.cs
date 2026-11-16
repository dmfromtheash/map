namespace Map.Rendering;

/// <summary>
/// Optional frame work grows only after 30 consecutive frames below 65% of the display interval
/// (at least 500ms per step). A frame above 90% halves the granted extra work immediately and
/// prevents growth for two seconds. Neutral/alternating frames reset the streak. Eight growth
/// steps, bounded by machine ceilings, keep the historical byte/draw floors and avoid oscillation.
/// Input is measured work, not the time spent waiting for vsync. The clock is injectable.
/// </summary>
public sealed class FrameBudgetController(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int _level, _fastFrames;
    private long? _lastChange = (clock ?? TimeProvider.System).GetTimestamp();
    private bool _cooling;
    public double TargetMilliseconds { get; private set; } = 1000d / 60;
    public int GrowthLevel => _level;
    public static FrameWorkBudget MinimumBudget { get; } = new(8 * MachineProfile.MiB, 2 * MachineProfile.MiB, 512);

    public void RecordFrame(TimeSpan work, double? displayRefreshHz = null)
    {
        TargetMilliseconds = displayRefreshHz is { } hz && double.IsFinite(hz) && hz is >= 24 and <= 1000
            ? 1000 / hz : 1000d / 60;
        var ms = work.TotalMilliseconds;
        if (!double.IsFinite(ms) || ms < 0) return;
        var now = _clock.GetTimestamp();
        var since = _lastChange is { } last ? _clock.GetElapsedTime(last, now) : TimeSpan.MaxValue;
        if (ms > TargetMilliseconds * .9)
        {
            _level /= 2;
            _fastFrames = 0;
            _lastChange = now;
            _cooling = true;
            return;
        }
        if (ms >= TargetMilliseconds * .65) { _fastFrames = 0; return; }
        if (_cooling && since < TimeSpan.FromSeconds(2)) { _fastFrames = 0; return; }
        _cooling = false;
        if (++_fastFrames < 30 || since < TimeSpan.FromMilliseconds(500)) return;
        _level = Math.Min(8, _level + 1);
        _fastFrames = 0;
        _lastChange = now;
    }

    public FrameWorkBudget Budget(MachineLimits limits)
    {
        long Scale(long floor, long ceiling) => floor + (Math.Max(floor, ceiling) - floor) * _level / 8;
        return new(Scale(MinimumBudget.AdoptBytes, limits.AdoptBytes),
            Scale(MinimumBudget.GestureAdoptBytes, limits.GestureAdoptBytes), (int)Scale(MinimumBudget.DirectDraws, limits.DirectDraws));
    }
}

public readonly record struct FrameWorkBudget(long AdoptBytes, long GestureAdoptBytes, int DirectDraws);
