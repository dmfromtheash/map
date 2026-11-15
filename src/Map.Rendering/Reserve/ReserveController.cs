namespace Map.Rendering.Reserve;

/// <summary>
/// Single-owner, deterministic policy. No clock, platform calls, scene enumeration or per-frame
/// allocations. Non-overlapping windows have bounded storage; an overflow or a sampling gap is
/// unknown telemetry, never evidence of health. Percentiles use each frame's refresh period.
/// </summary>
public sealed class ReserveController
{
    private readonly ReserveTuning _tuning;
    private readonly double[] _gpuRatios;
    private ReserveState _state = ReserveState.Probing;
    private ReserveReasons _pressure;
    private ReserveReasons _assistRequests;
    private int _level, _gpuCount, _frameCount, _gestureCount, _gestureMissCount, _missCount, _hotGpu, _hotUpload;
    private double _windowStart, _lastNow = double.NaN, _stateSince, _stepSince;
    private double _gpuHealthyMs, _pacingHealthyMs, _uploadHealthyMs, _uploadRatioSum;
    private double _nextEntryMs, _nextDelayMs, _deviceCooldownUntil, _softwareProbeAfter;
    private bool _windowUnknown, _lastWindowUnknown = true, _hadDeviceFailure, _hasWindow;
    private bool _gpuUnknown, _uploadUnknown, _pacingUnknown;

    public ReserveController(ReserveTuning? tuning = null)
    {
        _tuning = tuning ?? new();
        _tuning.Validate();
        _gpuRatios = new double[_tuning.MaxSamplesPerWindow];
        _nextDelayMs = _tuning.InitialReentryDelayMs;
    }

    /// <summary>Diagnostic policy deadline; doubles on each Assisted → Normal episode, capped by tuning.</summary>
    public double NextAssistanceAllowedAtMs => _nextEntryMs;

    public ReserveDecision Observe(RenderHealthSample sample, double nowMs)
    {
        if (!double.IsFinite(nowMs) || nowMs < 0 || (!double.IsNaN(_lastNow) && nowMs < _lastNow))
            throw new ArgumentOutOfRangeException(nameof(nowMs), "Time must be finite and monotonic.");
        if (double.IsNaN(_lastNow))
        {
            _windowStart = _stateSince = _stepSince = nowMs;
            _softwareProbeAfter = nowMs + _tuning.SoftwareProbeCooldownMs;
        }

        bool valid = ValidTelemetry(sample);
        // Never credit unobserved wall time towards healthy windows or startup/recovery probes.
        bool gap = nowMs - (double.IsNaN(_lastNow) ? nowMs : _lastNow) > _tuning.WindowMs;
        _lastNow = nowMs;
        UpdateMemory(sample);

        if (sample.DeviceLost)
        {
            _hadDeviceFailure = true;
            _deviceCooldownUntil = nowMs + _tuning.DeviceCooldownMs;
            _softwareProbeAfter = nowMs + _tuning.SoftwareProbeCooldownMs;
            Transition(ReserveState.Recovering, nowMs);
            ResetWindow(nowMs);
            return Decision(sample, !valid);
        }

        if (!sample.GpuAvailable)
        {
            if (_state != ReserveState.Software)
                _softwareProbeAfter = nowMs + _tuning.SoftwareProbeCooldownMs;
            Transition(ReserveState.Software, nowMs);
            _level = 3;
            ResetWindow(nowMs);
            return Decision(sample, !valid);
        }

        if (_state == ReserveState.Recovering && valid)
        {
            EnterAssisted(nowMs);
            ResetWindow(nowMs);
        }
        else if (_state == ReserveState.Software)
        {
            if (valid && nowMs >= _softwareProbeAfter)
            {
                Transition(ReserveState.RecoveryProbe, nowMs);
                ResetWindow(nowMs);
            }
            else return Decision(sample, !valid);
        }
        else if (_state == ReserveState.RecoveryProbe && (!valid || gap))
        {
            Transition(ReserveState.Software, nowMs);
            _softwareProbeAfter = nowMs + _tuning.SoftwareProbeCooldownMs;
            ResetWindow(nowMs);
            return Decision(sample, true);
        }

        if (gap)
        {
            ResetWindow(nowMs);
            _stateSince = _stepSince = nowMs;
        }
        if (nowMs - _windowStart >= _tuning.WindowMs)
        {
            CompleteWindow();
            ResetWindowCounts(nowMs);
            if (_state == ReserveState.Probing && (!_lastWindowUnknown || HasPressure)
                && nowMs - _stateSince >= _tuning.ProbeMs)
                Transition(ReserveState.Normal, nowMs);
            else if (_state == ReserveState.RecoveryProbe && !_lastWindowUnknown
                && nowMs - _stateSince >= _tuning.ProbeMs)
                EnterAssisted(nowMs);
        }
        AddSample(sample, valid);

        if (_state == ReserveState.Normal && HasPressure && nowMs >= _nextEntryMs)
            EnterAssisted(nowMs);
        else if (_state == ReserveState.Assisted)
        {
            _assistRequests |= _pressure;
            if (HasPressure && _level < 3 && nowMs - _stepSince >= _tuning.EscalationDwellMs)
            {
                _level++;
                _stepSince = nowMs;
            }
            else if (!HasPressure && valid && !_lastWindowUnknown && !_windowUnknown && _hasWindow && !gap
                && nowMs >= _deviceCooldownUntil && nowMs - _stepSince >= _tuning.ImprovementDwellMs)
            {
                _level--;
                _stepSince = nowMs;
                if (_level == 0)
                {
                    Transition(ReserveState.Normal, nowMs);
                    _hadDeviceFailure = false;
                    _assistRequests = ReserveReasons.None;
                    _nextEntryMs = nowMs + _nextDelayMs;
                    _nextDelayMs = Math.Min(_tuning.MaxReentryDelayMs, _nextDelayMs * _tuning.ReentryMultiplier);
                }
            }
        }
        return Decision(sample, !valid || _lastWindowUnknown || _windowUnknown);
    }

    private bool HasPressure => _pressure != ReserveReasons.None;

    private static bool Nonnegative(double n) => double.IsFinite(n) && n >= 0;

    private static bool ValidRefresh(RenderHealthSample s) => double.IsFinite(s.RefreshPeriodMs) && s.RefreshPeriodMs > 0;

    private static bool ValidGpu(RenderHealthSample s) => ValidRefresh(s) && s.GpuTimeValid
        && Nonnegative(s.GpuMs) && double.IsFinite(s.GpuMs / s.RefreshPeriodMs);

    private static bool ValidTelemetry(RenderHealthSample s) =>
        Nonnegative(s.FrameIntervalMs) && Nonnegative(s.CpuWorkMs)
        && ValidRefresh(s) && s.AdoptionBytes >= 0 && (!s.GpuAvailable || ValidGpu(s))
        && ((s.VramUsageBytes is null && s.VramBudgetBytes is null)
            || (s.VramUsageBytes is >= 0 && s.VramBudgetBytes is > 0));

    private void UpdateMemory(RenderHealthSample s)
    {
        if (s.VramUsageBytes is >= 0 && s.VramBudgetBytes is > 0)
        {
            double ratio = (double)s.VramUsageBytes.Value / s.VramBudgetBytes.Value;
            if (ratio > _tuning.MemoryEnterRatio) _pressure |= ReserveReasons.MemoryPressure;
            else if (ratio < _tuning.MemoryExitRatio) _pressure &= ~ReserveReasons.MemoryPressure;
        }
        if (s.WeakProfile) _pressure |= ReserveReasons.WeakProfile;
        else _pressure &= ~ReserveReasons.WeakProfile;
    }

    private void AddSample(RenderHealthSample s, bool valid)
    {
        // Saturate the entire window, not just GPU storage, to keep all counters bounded.
        _windowUnknown |= !valid;
        if (_frameCount == _gpuRatios.Length)
        {
            _windowUnknown = true;
            _gpuUnknown = _uploadUnknown = _pacingUnknown = true;
            return;
        }
        _frameCount++;
        if (ValidGpu(s)) _gpuRatios[_gpuCount++] = s.GpuMs / s.RefreshPeriodMs;
        else _gpuUnknown = true;
        if (s.AdoptionBytes >= 0)
            _uploadRatioSum += (double)s.AdoptionBytes / (s.GestureActive
                ? _tuning.GestureAdoptionBytesPerFrame : _tuning.AdoptionBytesPerFrame);
        else _uploadUnknown = true;
        if (!ValidRefresh(s) || !Nonnegative(s.FrameIntervalMs))
        {
            _pacingUnknown = true;
            return;
        }
        bool miss = s.FrameIntervalMs > _tuning.MissIntervalRatio * s.RefreshPeriodMs;
        if (miss) _missCount++;
        if (s.GestureActive)
        {
            _gestureCount++;
            if (miss) _gestureMissCount++;
        }
    }

    private void CompleteWindow()
    {
        _hasWindow = true;
        _lastWindowUnknown = _windowUnknown || _frameCount == 0;
        if (_frameCount == 0)
        {
            _hotGpu = _hotUpload = 0;
            _gpuHealthyMs = _pacingHealthyMs = _uploadHealthyMs = 0;
            return;
        }
        double p95 = double.NaN;
        if (!_gpuUnknown && _gpuCount > 0)
        {
            Array.Sort(_gpuRatios, 0, _gpuCount);
            p95 = _gpuRatios[(int)Math.Ceiling(_gpuCount * _tuning.GpuPercentile) - 1];
        }
        UpdatePressure(ReserveReasons.GpuCost, p95 > _tuning.GpuEnterRatio,
            !_lastWindowUnknown && p95 < _tuning.GpuExitRatio,
            _tuning.GpuReleaseMs, ref _hotGpu, ref _gpuHealthyMs);
        double upload = _uploadRatioSum / _frameCount;
        UpdatePressure(ReserveReasons.UploadPressure, !_uploadUnknown && upload > 1,
            !_lastWindowUnknown && upload < _tuning.UploadExitRatio,
            _tuning.UploadReleaseMs, ref _hotUpload, ref _uploadHealthyMs);
        double gestureMisses = _gestureCount == 0 ? 0 : (double)_gestureMissCount / _gestureCount;
        if (!_pacingUnknown && gestureMisses > _tuning.PacingEnterFraction) _pressure |= ReserveReasons.Pacing;
        // Entry is gesture-specific; healthy measured pacing can also release the reason at rest.
        double releaseMisses = _gestureCount > 0 ? gestureMisses : (double)_missCount / _frameCount;
        _pacingHealthyMs = !_lastWindowUnknown && releaseMisses < _tuning.PacingExitFraction
            ? _pacingHealthyMs + _tuning.WindowMs : 0;
        if (_pacingHealthyMs >= _tuning.PacingReleaseMs) _pressure &= ~ReserveReasons.Pacing;
    }

    private void UpdatePressure(ReserveReasons reason, bool hot, bool healthy, double releaseMs,
        ref int hotWindows, ref double healthyMs)
    {
        hotWindows = hot ? Math.Min(hotWindows + 1, _tuning.PressureWindows) : 0;
        if (hotWindows >= _tuning.PressureWindows) _pressure |= reason;
        healthyMs = healthy ? healthyMs + _tuning.WindowMs : 0;
        if (healthyMs >= releaseMs) _pressure &= ~reason;
    }

    private void ResetWindow(double nowMs)
    {
        ResetWindowCounts(nowMs);
        _hasWindow = false;
        _lastWindowUnknown = true;
        _hotGpu = _hotUpload = 0;
        _gpuHealthyMs = _pacingHealthyMs = _uploadHealthyMs = 0;
    }

    private void ResetWindowCounts(double nowMs)
    {
        _windowStart = nowMs;
        _frameCount = _gpuCount = _gestureCount = _gestureMissCount = _missCount = 0;
        _uploadRatioSum = 0;
        _windowUnknown = false;
        _gpuUnknown = _uploadUnknown = _pacingUnknown = false;
    }

    private void Transition(ReserveState state, double nowMs)
    {
        if (_state == state) return;
        _state = state;
        _stateSince = _stepSince = nowMs;
    }

    private void EnterAssisted(double nowMs)
    {
        bool fromSoftwareProbe = _state == ReserveState.RecoveryProbe;
        Transition(ReserveState.Assisted, nowMs);
        _level = 1;
        _assistRequests = _pressure;
        if (fromSoftwareProbe) _assistRequests |= ReserveReasons.WeakProfile;
        if (_hadDeviceFailure) _assistRequests |= ReserveReasons.DeviceFailure;
    }

    private ReserveDecision Decision(RenderHealthSample s, bool unknown)
    {
        var reasons = _pressure;
        if (_hadDeviceFailure || _state == ReserveState.Recovering) reasons |= ReserveReasons.DeviceFailure;
        if (!s.GpuAvailable) reasons |= ReserveReasons.WeakProfile;
        if (unknown) reasons |= ReserveReasons.UnknownTelemetry;
        int level = _state == ReserveState.Recovering ? 3
            : _state is ReserveState.Assisted or ReserveState.Software or ReserveState.RecoveryProbe ? _level : 0;
        // VRAM alone needs prefetch suppression, not a change in scene resolution/effects.
        var requests = reasons | _assistRequests;
        bool renderAssist = level > 0 && ((requests & (ReserveReasons.WeakProfile | ReserveReasons.GpuCost
            | ReserveReasons.Pacing | ReserveReasons.DeviceFailure)) != 0
            || _state is ReserveState.Software or ReserveState.RecoveryProbe);
        bool uploadAssist = renderAssist || (level > 0 && requests.HasFlag(ReserveReasons.UploadPressure));
        double upload = !uploadAssist ? 1 : level switch
        {
            1 => _tuning.Level1UploadFactor, 2 => _tuning.Level2UploadFactor, _ => _tuning.Level3UploadFactor
        };
        double scale = !renderAssist || !s.GestureActive ? 1 : level switch
        {
            1 => _tuning.Level1ResolutionScale, 2 => _tuning.Level2ResolutionScale, _ => _tuning.Level3ResolutionScale
        };
        int concurrency = !renderAssist || level < 2 ? 0
            : !Nonnegative(s.CpuWorkMs) || s.CpuWorkMs > _tuning.CpuBusyRatio * s.RefreshPeriodMs
                ? _tuning.BusyCpuTileConcurrency
            : level == 2 ? _tuning.Level2CpuTileConcurrency : _tuning.Level3CpuTileConcurrency;
        return new(_state, reasons, level, upload, renderAssist, scale, concurrency,
            renderAssist && s.GestureActive && level == 3, reasons.HasFlag(ReserveReasons.MemoryPressure));
    }
}
