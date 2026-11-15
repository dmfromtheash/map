using System.Diagnostics;

namespace Map.Rendering;

/// <summary>Refinement clients. The two detail clients share the highest priority and its reserve.</summary>
public enum FrameWorkKind { VisibleDetailTiles, DetailPreparation, MipAdoption, Overview, Hex, Trims }

/// <summary>
/// One render-thread CPU deadline. Old client budgets remain ceilings; higher-priority ceilings
/// are reserved even when a lower-priority client runs first. Complete releases an unused reserve.
/// Admission requires room for the estimated unit in both the client share and the frame.
/// Indivisible work can still overrun its estimate; Report cannot preempt synchronous code.
/// </summary>
public sealed class FrameBudget
{
    private readonly Func<double> _clock;
    private readonly double[] _ceilings = [8, 3, 2, 4, 8, 2];
    private readonly double[] _actual = new double[6];
    private readonly double[] _estimates = new double[6];
    private readonly bool[] _hasEstimate = new bool[6];
    private readonly int[] _units = new int[6];
    private readonly bool[] _complete = new bool[6];
    private double _started, _available, _reported;
    private bool _open;

    public FrameBudget() : this(() => Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency)) { }
    internal FrameBudget(Func<double> clock) => _clock = clock;

    public void Begin(double refreshIntervalMs, double alreadySpentMs)
    {
        if (!double.IsFinite(refreshIntervalMs) || refreshIntervalMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(refreshIntervalMs));
        Validate(alreadySpentMs);
        Array.Clear(_actual); Array.Clear(_units); Array.Clear(_complete);
        _reported = 0;
        _available = refreshIntervalMs - alreadySpentMs;
        _started = _clock();
        _open = true;
    }

    /// <summary>Reserve the client's existing ceiling. The client still enforces its own local limit.</summary>
    public void SetCeiling(FrameWorkKind kind, double milliseconds)
    {
        Validate(milliseconds);
        _ceilings[(int)kind] = milliseconds;
    }

    public double RemainingMilliseconds => _open
        ? Math.Max(0, _available - Math.Max(_reported, _clock() - _started)) : 0;

    /// <summary>
    /// Checks one synchronous unit; it does not consume a slot until Report. A cache hit or an
    /// unfinished background result therefore cannot charge the client's measured share.
    /// The supplied estimate seeds the EWMA until the first real measurement (weight 1/4).
    /// </summary>
    public bool TryTake(FrameWorkKind kind, double estimateMs)
    {
        Validate(estimateMs);
        if (!_open) throw new InvalidOperationException("Begin a frame before taking work.");
        var index = (int)kind;
        if (_complete[index]) return false;
        // A cold-start outlier must not permanently deadlock an entire kind of work. The
        // caller's estimate is still a hard admission requirement; learned cost can request
        // the whole share, but cannot demand more than the share forever.
        var estimate = _hasEstimate[index] ? Math.Max(Math.Min(_estimates[index], _ceilings[index]), estimateMs) : estimateMs;
        return AvailableMilliseconds(kind) > 0 && AvailableMilliseconds(kind) >= estimate;
    }

    /// <summary>Time left in the client's share after higher-priority reservations.</summary>
    public double AvailableMilliseconds(FrameWorkKind kind)
    {
        var index = (int)kind;
        return Math.Max(0, Math.Min(_ceilings[index] - _actual[index], AfterReservations(index)));
    }

    private double AfterReservations(int index)
    {
        // Readiness was already inside the old detail pass's 8 ms wall-clock ceiling.
        // Reserve max(8, 3), not 8 + 3, and debit both phases against the same reserve.
        if (index <= (int)FrameWorkKind.DetailPreparation) return RemainingMilliseconds;
        var detail = (int)FrameWorkKind.VisibleDetailTiles;
        var preparation = (int)FrameWorkKind.DetailPreparation;
        var detailCeiling = Math.Max(_complete[detail] ? 0 : _ceilings[detail],
            _complete[preparation] ? 0 : _ceilings[preparation]);
        var reserve = Math.Max(0, detailCeiling - _actual[detail] - _actual[preparation]);
        for (var higher = (int)FrameWorkKind.MipAdoption; higher < index; higher++)
            if (!_complete[higher]) reserve += Math.Max(0, _ceilings[higher] - _actual[higher]);
        return RemainingMilliseconds - reserve;
    }

    public void Report(FrameWorkKind kind, double actualMs)
    {
        Validate(actualMs);
        if (!_open) throw new InvalidOperationException("Begin a frame before reporting work.");
        var index = (int)kind;
        _units[index]++;
        _actual[index] += actualMs;
        _reported += actualMs;
        _estimates[index] = !_hasEstimate[index]
            ? actualMs : _estimates[index] + .25 * (actualMs - _estimates[index]);
        _hasEstimate[index] = true;
    }

    /// <summary>No further work of this kind in this frame; lower priorities may use its reserve.</summary>
    public void Complete(FrameWorkKind kind) => _complete[(int)kind] = true;
    public double EstimateMilliseconds(FrameWorkKind kind) => _estimates[(int)kind];
    public double SpentMilliseconds(FrameWorkKind kind) => _actual[(int)kind];

    private static void Validate(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
    }
}
