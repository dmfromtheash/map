namespace Map.Rendering.Reserve;

/// <summary>
/// Pure, single-thread-owned arbiter. Explicit monotonic milliseconds; constant storage/work,
/// no platform or scene access. Each knob has ONE owner (OwnerOf); all other inputs only request.
/// Normal changes are serialized globally, worsening first, with longer recovery periods.
/// DEC-115 exactness restoration is the sole exception: gesture stop/ForceExact restores both
/// visual knobs immediately and restarts the arbitration period. Resource ceilings recover later.
/// </summary>
public sealed class FramePlanner
{
    private readonly FramePlanTuning _tuning;
    private FramePlan _plan;
    private double _lastNow = double.NaN, _windowStart, _maxRatio, _healthyMs;
    private double _lastChange = double.NegativeInfinity, _resolutionSince;
    private int _hotWindows;
    private bool _hasSample, _unknown, _pressure, _gesture;

    public FramePlanner(FramePlanTuning? tuning = null)
    {
        _tuning = tuning ?? new();
        _tuning.Validate();
        _plan = new(1, _tuning.UploadBudget, _tuning.AdoptionBudget, false, _tuning.BackgroundConcurrency, false);
    }

    public FramePlan Current => _plan;

    /// <summary>Null for unchanged frames; Exactness denotes the immediate DEC-115 safety gate.</summary>
    public FramePlanKnob? LastChangedKnob { get; private set; }

    public static FramePlanOwner OwnerOf(FramePlanKnob knob) => knob switch
    {
        FramePlanKnob.SceneResolution => FramePlanOwner.GestureResolution,
        FramePlanKnob.UploadBudget or FramePlanKnob.AdoptionBudget => FramePlanOwner.FrameBudget,
        FramePlanKnob.CameraFrame => FramePlanOwner.CameraPolicy,
        FramePlanKnob.BackgroundConcurrency or FramePlanKnob.Prefetch => FramePlanOwner.WorkScheduler,
        FramePlanKnob.Exactness => FramePlanOwner.ExactnessPolicy,
        _ => throw new ArgumentOutOfRangeException(nameof(knob))
    };

    public FramePlan Plan(FramePlanSample sample, double nowMs)
    {
        // Validate before any mutation, including diagnostics and clock state.
        if (!double.IsFinite(nowMs) || nowMs < 0 || (!double.IsNaN(_lastNow) && nowMs < _lastNow))
            throw new ArgumentOutOfRangeException(nameof(nowMs), "Time must be finite and monotonic.");
        if (!double.IsFinite(sample.Reserve.UploadLimitFactor) || sample.Reserve.UploadLimitFactor <= 0
            || sample.Reserve.UploadLimitFactor > 1 || !double.IsFinite(sample.Reserve.GestureResolutionScale)
            || sample.Reserve.GestureResolutionScale <= 0 || sample.Reserve.GestureResolutionScale > 1
            || sample.Reserve.CpuTileConcurrencyHint < 0)
            throw new ArgumentException("Invalid reserve requests.", nameof(sample));

        LastChangedKnob = null;
        if (double.IsNaN(_lastNow)) _windowStart = _resolutionSince = nowMs;
        ObserveCost(sample, nowMs);
        _lastNow = nowMs;

        bool gesture = sample.GestureActive && !sample.ForceExact;
        if (gesture != _gesture) _resolutionSince = nowMs;
        _gesture = gesture;
        if (!gesture && !_plan.RequireExact)
        {
            _plan = FramePlanChanges.Apply(_plan, _plan with { SceneResolutionScale = 1, UseCameraFrame = false },
                FramePlanKnob.Exactness, FramePlanOwner.ExactnessPolicy);
            _lastChange = _resolutionSince = nowMs;
            LastChangedKnob = FramePlanKnob.Exactness;
            return _plan;
        }

        bool pendingWorsening = false;
        if (!Adjust(sample, nowMs, true, ref pendingWorsening) && !pendingWorsening && CanRecover)
            Adjust(sample, nowMs, false, ref pendingWorsening);
        return _plan;
    }

    private bool CanRecover => !_unknown && _hasSample && _healthyMs >= _tuning.HealthyReleaseMs && !_pressure;

    private void ObserveCost(FramePlanSample sample, double nowMs)
    {
        // No unobserved time counts as healthy. Retain pressure until measured recovery.
        if (!double.IsNaN(_lastNow) && nowMs - _lastNow > _tuning.WindowMs)
        {
            _windowStart = nowMs;
            _maxRatio = _healthyMs = 0;
            _hotWindows = 0;
            _hasSample = _unknown = false;
        }
        if (nowMs - _windowStart >= _tuning.WindowMs)
        {
            bool valid = _hasSample && !_unknown;
            _hotWindows = valid && _maxRatio > _tuning.EnterRatio
                ? Math.Min(_hotWindows + 1, _tuning.PressureWindows) : 0;
            _healthyMs = valid && _maxRatio < _tuning.ExitRatio ? _healthyMs + _tuning.WindowMs : 0;
            if (_hotWindows >= _tuning.PressureWindows) _pressure = true;
            if (_healthyMs >= _tuning.HealthyReleaseMs) _pressure = false;
            _windowStart = nowMs;
            _maxRatio = 0;
            _hasSample = _unknown = false;
        }
        double ratio = sample.FrameWorkMs / sample.RefreshPeriodMs;
        bool known = double.IsFinite(sample.FrameWorkMs) && sample.FrameWorkMs >= 0
            && double.IsFinite(sample.RefreshPeriodMs) && sample.RefreshPeriodMs > 0 && double.IsFinite(ratio);
        _unknown |= !known;
        _hasSample = true;
        if (known) _maxRatio = Math.Max(_maxRatio, ratio);
        // A new bad/neutral sample must block recovery even at a healthy window boundary.
        if (!known || ratio >= _tuning.ExitRatio) _healthyMs = 0;
    }

    private bool Adjust(FramePlanSample sample, double nowMs, bool worsening, ref bool pending)
    {
        var reserve = sample.Reserve;
        // Fixed order is the deterministic tie-breaker. An unavailable dwell does not permit
        // recovery elsewhere while a worsening request is pending.
        int step = ResolutionStep(_plan.SceneResolutionScale);
        int requested = _gesture ? ResolutionStep(reserve.GestureResolutionScale) : 0;
        int targetStep = step;
        if (_gesture)
        {
            if (_hotWindows >= _tuning.PressureWindows) targetStep = Math.Min(step + 1, 2);
            else if (CanRecover) targetStep = Math.Max(step - 1, 0);
            targetStep = Math.Min(Math.Max(targetStep, requested), step + 1);
        }
        double scale = Scale(targetStep);
        if (Candidate(FramePlanKnob.SceneResolution, _plan with { SceneResolutionScale = scale },
            targetStep > step, nowMs, worsening, ref pending, _resolutionSince)) return true;
        // Reserve the next slot for a pending visual reduction rather than delaying it by
        // spending that slot on background work just before the resolution dwell expires.
        if (pending) return false;

        bool prefetch = reserve.StopPrefetch || _pressure;
        if (Candidate(FramePlanKnob.Prefetch, _plan with { StopPrefetch = prefetch },
            prefetch, nowMs, worsening, ref pending)) return true;

        double factor = Math.Min(reserve.UploadLimitFactor, _pressure ? _tuning.BusyTransferFactor : 1);
        var upload = Limit(_tuning.UploadBudget, _gesture ? _tuning.GestureUploadBudget : _tuning.UploadBudget, factor);
        var adoption = Limit(_tuning.AdoptionBudget, _gesture ? _tuning.GestureAdoptionBudget : _tuning.AdoptionBudget, factor);
        if (BudgetCandidate(FramePlanKnob.UploadBudget, upload, nowMs, worsening, ref pending)) return true;
        if (BudgetCandidate(FramePlanKnob.AdoptionBudget, adoption, nowMs, worsening, ref pending)) return true;

        bool camera = _gesture && (reserve.KeepCameraFrameLonger || _pressure);
        if (Candidate(FramePlanKnob.CameraFrame, _plan with { UseCameraFrame = camera },
            camera, nowMs, worsening, ref pending)) return true;

        // Zero is no CPU-assist request. CPU assistance cannot exceed the shared scheduler's cap;
        // measured frame pressure has precedence over a request to start more workers.
        int concurrency = Math.Clamp(Math.Max(_tuning.BackgroundConcurrency, reserve.CpuTileConcurrencyHint),
            1, _tuning.MaxBackgroundConcurrency);
        if (_pressure) concurrency = Math.Min(concurrency, _tuning.BusyBackgroundConcurrency);
        return Candidate(FramePlanKnob.BackgroundConcurrency, _plan with { BackgroundConcurrencyHint = concurrency },
            concurrency < _plan.BackgroundConcurrencyHint, nowMs, worsening, ref pending);
    }

    private bool BudgetCandidate(FramePlanKnob knob, FrameWorkBudget target, double nowMs, bool worsening, ref bool pending)
    {
        var current = knob == FramePlanKnob.UploadBudget ? _plan.UploadBudget : _plan.AdoptionBudget;
        // If one dimension tightens while another loosens, tighten first. Never smuggle a
        // recovery into the worsening pass, even with asymmetric client ceilings.
        bool tighter = target.Bytes < current.Bytes || target.Count < current.Count;
        if (tighter) target = new(Math.Min(target.Bytes, current.Bytes), Math.Min(target.Count, current.Count));
        var next = knob == FramePlanKnob.UploadBudget ? _plan with { UploadBudget = target } : _plan with { AdoptionBudget = target };
        return Candidate(knob, next, tighter, nowMs, worsening, ref pending);
    }

    private bool Candidate(FramePlanKnob knob, FramePlan next, bool tighter, double nowMs, bool worsening,
        ref bool pending, double since = double.NegativeInfinity)
    {
        if (next == _plan || tighter != worsening) return false;
        pending |= worsening;
        double period = worsening ? _tuning.WorseningPeriodMs : _tuning.RecoveryPeriodMs;
        if (nowMs - _lastChange < period || nowMs - since < period) return false;
        _plan = FramePlanChanges.Apply(_plan, next, knob, OwnerOf(knob));
        _lastChange = nowMs;
        if (knob == FramePlanKnob.SceneResolution) _resolutionSince = nowMs;
        LastChangedKnob = knob;
        return true;
    }

    private int ResolutionStep(double scale) => scale >= 1 ? 0 : scale >= _tuning.ReducedResolutionScale ? 1 : 2;

    private double Scale(int step) => step switch
    {
        0 => 1, 1 => _tuning.ReducedResolutionScale, _ => _tuning.MinimumResolutionScale
    };

    private static FrameWorkBudget Limit(FrameWorkBudget normal, FrameWorkBudget gesture, double factor) => new(
        Math.Min(normal.Bytes, factor == 1 ? gesture.Bytes : (long)Math.Floor(gesture.Bytes * factor)),
        Math.Min(normal.Count, (int)Math.Floor(gesture.Count * factor)));
}
