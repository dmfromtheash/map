using System.Collections.Frozen;

namespace Map.Presentation.Services;

internal enum DemandBin : byte { Now, Soon, Eventually }
internal readonly record struct DemandPriority(DemandBin Bin, double Distance) : IComparable<DemandPriority>
{
    public int CompareTo(DemandPriority other)
    {
        var bin = Bin.CompareTo(other.Bin);
        return bin != 0 ? bin : Distance.CompareTo(other.Distance);
    }
}

/// <summary>Identity is the logical asset; Level is its desired total power-of-two reduction.
/// For §5.2 PixelKey, use normalized SHA as Identity and Step + Level here. Map a granted level L
/// back to Step = min(ladderDepth, L), Level = L - Step (the same canonicalization as PixelKey).
/// Shape and bytes describe the desired pixels, after the desired reduction, before this queue's bias.
/// Export must bypass fitting/degradation; this class is deliberately not wired into any consumer.</summary>
internal readonly record struct ImageDemandKey(string Identity, int Level = 0);
internal readonly record struct DemandShape(int Width, int Height);
internal sealed record ImageDemandTuning(
    double HighWater = 0.9, double LowWater = 0.7, int MinimumSide = 16, int MaximumBias = 15,
    long InFlightBytes = 128L * 1024 * 1024, int DispatchLimit = 512);
internal readonly record struct ImageDemandGrant(ImageDemandKey Key, int MipBias, long Bytes, DemandShape Shape);
internal sealed record FitResult(
    IReadOnlyDictionary<ImageDemandKey, ImageDemandGrant> Granted,
    IReadOnlySet<ImageDemandKey> Cutoff, long NeedBytes, long GrantedBytes, long ShelfBytes);

/// <summary>Pure deterministic demand planner. The owner supplies frames, resident/completion notifications
/// and serializes calls. No Skia, file access, clock or worker dispatch. Each instance budgets one shelf;
/// the future adapter must split <=256 px and larger grants into the source's two shelves (rule 8).
/// Fit holds reductions until desired bytes fall strictly below LowWater. Dispatch charges all outstanding
/// work, including work from an earlier fit, against min(InFlightBytes, shelf - resident(granted)).</summary>
internal sealed class ImageDemandQueue
{
    private sealed record Demand(long Bytes, DemandPriority Priority, DemandShape Shape);
    private readonly ImageDemandTuning _tuning;
    private readonly Dictionary<ImageDemandKey, Demand> _demands = [];
    private readonly Dictionary<ImageDemandKey, int> _bias = [];
    private readonly Dictionary<ImageDemandKey, long> _inFlight = [];
    private readonly Dictionary<ImageDemandKey, long> _resident = [];
    private readonly RecentMipRequests<ImageDemandKey> _recent;
    private ImageDemandKey[] _order = [];
    private ImageDemandKey[] _dispatchOrder = [];
    private readonly Dictionary<ImageDemandKey, long> _grantedCosts = [];
    private readonly HashSet<ImageDemandKey> _protected = [];
    private FitResult? _fit;
    private long _frame = -1;
    private bool _fitted;

    public ImageDemandQueue(ImageDemandTuning? tuning = null, MipRequestRetentionTuning? retention = null)
    {
        _tuning = tuning ?? new();
        if (!double.IsFinite(_tuning.LowWater) || !double.IsFinite(_tuning.HighWater)
            || _tuning.LowWater <= 0 || _tuning.LowWater >= _tuning.HighWater || _tuning.HighWater > 1)
            throw new ArgumentOutOfRangeException(nameof(tuning));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_tuning.MinimumSide);
        ArgumentOutOfRangeException.ThrowIfNegative(_tuning.MaximumBias);
        if (_tuning.MaximumBias > 30) throw new ArgumentOutOfRangeException(nameof(tuning));
        ArgumentOutOfRangeException.ThrowIfNegative(_tuning.InFlightBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_tuning.DispatchLimit);
        _recent = new(retention);
    }

    public long InFlightBytes => _inFlight.Values.Sum();
    public long CancelledStillWanted => _recent.CancelledStillWanted;

    public void BeginFrame(long frame)
    {
        if (frame <= _frame) throw new ArgumentOutOfRangeException(nameof(frame));
        _frame = frame;
        _recent.BeginFrame(frame);
        _demands.Clear();
        _fitted = false;
    }

    public void Submit(ImageDemandKey desired, long bytes, DemandPriority priority, DemandShape shape)
    {
        if (_frame < 0) throw new InvalidOperationException("BeginFrame must precede Submit.");
        ArgumentException.ThrowIfNullOrEmpty(desired.Identity);
        ArgumentOutOfRangeException.ThrowIfNegative(desired.Level);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(shape.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(shape.Height);
        if (!Enum.IsDefined(priority.Bin) || !double.IsFinite(priority.Distance) || priority.Distance < 0)
            throw new ArgumentOutOfRangeException(nameof(priority));
        if (_demands.TryGetValue(desired, out var previous))
        {
            if (previous.Bytes != bytes || previous.Shape != shape)
                throw new ArgumentException("One desired key must describe the same pixels.", nameof(desired));
            if (previous.Priority.CompareTo(priority) < 0) priority = previous.Priority;
        }
        _demands[desired] = new(bytes, priority, shape);
        _recent.Submit(desired);
        _fitted = false;
    }

    public FitResult Fit(long shelfBytes)
    {
        if (_frame < 0) throw new InvalidOperationException("BeginFrame must precede Fit.");
        ArgumentOutOfRangeException.ThrowIfNegative(shelfBytes);
        _order = _demands.Keys.OrderBy(k => _demands[k].Priority).ThenBy(k => k.Identity, StringComparer.Ordinal)
            .ThenBy(k => k.Level).ToArray();
        var need = _demands.Values.Aggregate(0L, (sum, d) => checked(sum + d.Bytes));
        var lift = need < (decimal)shelfBytes * (decimal)_tuning.LowWater;
        var grants = new Dictionary<ImageDemandKey, ImageDemandGrant>();
        foreach (var key in _order)
        {
            var bias = lift ? 0 : Math.Min(_bias.GetValueOrDefault(key), MaxBias(_demands[key].Shape));
            grants[key] = Grant(key, bias);
        }
        var limit = (long)((decimal)shelfBytes * (decimal)_tuning.HighWater);
        // Each change updates only the two affected granted keys. Re-summing all demands per mip
        // drop would make a crowded fit quadratic. A granted key can have at most one contributor
        // per desired level, so its small cost multiset is bounded by the reduction ladder.
        var weights = new Dictionary<ImageDemandKey, SortedDictionary<long, int>>();
        long grantedBytes = 0;
        foreach (var grant in grants.Values) ChangeWeight(grant, 1);
        // Round-robin from least important to most important, one level per key per pass.
        // This is a fixed degradation path: increasing budget can only stop that path earlier.
        while (grantedBytes > limit)
        {
            var changed = false;
            foreach (var key in _order.Reverse())
            {
                var bias = grants[key].MipBias;
                if (bias >= MaxBias(_demands[key].Shape)) continue;
                ChangeWeight(grants[key], -1);
                grants[key] = Grant(key, bias + 1);
                ChangeWeight(grants[key], 1);
                changed = true;
                if (grantedBytes <= limit) break;
            }
            if (!changed) break;
        }
        var cutoff = new HashSet<ImageDemandKey>();
        foreach (var key in _order.Reverse())
        {
            // Remember the bias even for cutoff keys; a middle-water fit must not lift it.
            _bias[key] = grants[key].MipBias;
        }
        foreach (var key in _order.Reverse())
        {
            if (grantedBytes <= limit) break;
            ChangeWeight(grants[key], -1);
            grants.Remove(key);
            cutoff.Add(key);
        }
        // Do not accumulate history for views left behind; in-flight work has its own bounded history.
        foreach (var key in _bias.Keys.Where(k => !_demands.ContainsKey(k)).ToArray()) _bias.Remove(key);
        foreach (var grant in grants.Values) _recent.Submit(grant.Key);
        _grantedCosts.Clear();
        foreach (var (key, costs) in weights) _grantedCosts[key] = costs.Last().Key;
        _dispatchOrder = _order.Where(grants.ContainsKey).Select(k => grants[k].Key).Distinct().ToArray();
        _protected.Clear();
        foreach (var (key, grant) in grants)
            if (_demands[key].Priority.Bin is DemandBin.Now or DemandBin.Soon) _protected.Add(grant.Key);
        _fit = new(grants.ToFrozenDictionary(), cutoff.ToFrozenSet(), need, grantedBytes, shelfBytes);
        _fitted = true;
        return _fit;

        void ChangeWeight(ImageDemandGrant grant, int delta)
        {
            if (!weights.TryGetValue(grant.Key, out var costs)) weights[grant.Key] = costs = [];
            var before = costs.Count == 0 ? 0 : costs.Last().Key;
            var count = costs.GetValueOrDefault(grant.Bytes) + delta;
            if (count == 0) costs.Remove(grant.Bytes); else costs[grant.Bytes] = count;
            var after = costs.Count == 0 ? 0 : costs.Last().Key;
            grantedBytes = checked(grantedBytes - before + after);
            if (costs.Count == 0) weights.Remove(grant.Key);
        }
    }

    private int MaxBias(DemandShape shape)
    {
        var bias = 0;
        while (bias < _tuning.MaximumBias && (shape.Width >> (bias + 1)) >= _tuning.MinimumSide
            && (shape.Height >> (bias + 1)) >= _tuning.MinimumSide) bias++;
        return bias;
    }

    private ImageDemandGrant Grant(ImageDemandKey key, int bias)
    {
        var demand = _demands[key];
        var shape = new DemandShape(Math.Max(1, demand.Shape.Width >> bias), Math.Max(1, demand.Shape.Height >> bias));
        var bytes = (long)decimal.Ceiling((decimal)demand.Bytes * shape.Width / demand.Shape.Width
            * shape.Height / demand.Shape.Height);
        return new(key with { Level = checked(key.Level + bias) }, bias, bytes, shape);
    }

    /// <summary>Records an existing resident image (or its eviction), without starting work.</summary>
    public void SetResident(ImageDemandKey key, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes == 0) _resident.Remove(key); else _resident[key] = bytes;
    }

    /// <summary>Completion releases the temporary budget. A failed/refused result can be dispatched again.</summary>
    public void Complete(ImageDemandKey key, bool resident = true)
    {
        if (!_inFlight.Remove(key, out var bytes)) return;
        if (resident) _resident[key] = bytes;
    }

    public IReadOnlyList<ImageDemandKey> Dispatch()
    {
        var fit = CurrentFit();
        var resident = _dispatchOrder.Sum(k => _resident.GetValueOrDefault(k));
        var free = Math.Min(_tuning.InFlightBytes, Math.Max(0, fit.ShelfBytes - resident)) - InFlightBytes;
        var result = new List<ImageDemandKey>();
        foreach (var key in _dispatchOrder)
        {
            if (_resident.ContainsKey(key) || _inFlight.ContainsKey(key)) continue;
            var bytes = _grantedCosts[key];
            // A large request may exceed even the whole in-flight budget. Keep it pending,
            // but let smaller requests use the remaining bytes instead of starving behind it.
            if (bytes > free) continue;
            _inFlight.Add(key, bytes);
            free -= bytes;
            result.Add(key);
            if (result.Count >= _tuning.DispatchLimit) break;
        }
        return result;
    }

    /// <summary>Same priority order for ready adoption as for dispatch; ungranted completions are excluded.</summary>
    public IReadOnlyList<ImageDemandKey> OrderReady(IEnumerable<ImageDemandKey> ready)
    {
        CurrentFit();
        var available = ready.ToHashSet();
        return _dispatchOrder.Where(available.Contains).ToArray();
    }

    public bool IsProtected(ImageDemandKey key) => _fitted && _protected.Contains(key);

    /// <summary>Optional worker-queue cancellation step, using the same canvas-frame retention as the source.
    /// Work skipped by a plan/time/request slice stays in-flight until genuinely unasked for more than N frames.</summary>
    public IReadOnlyList<ImageDemandKey> CancelUnwanted()
    {
        var cancelled = _inFlight.Keys.Where(k => !_recent.IsWanted(k)).OrderBy(k => k.Identity, StringComparer.Ordinal)
            .ThenBy(k => k.Level).ToArray();
        foreach (var key in cancelled) { _inFlight.Remove(key); _recent.Cancelled(key); }
        return cancelled;
    }

    private FitResult CurrentFit() => _fitted ? _fit! : throw new InvalidOperationException("Fit must precede dispatch/adoption.");
}
