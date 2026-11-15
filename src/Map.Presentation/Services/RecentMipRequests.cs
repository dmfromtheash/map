namespace Map.Presentation.Services;

internal sealed record MipRequestRetentionTuning(int Frames = 8);

/// <summary>Canvas-frame demand history. The caller serializes access; no clock, workers or storage.</summary>
internal sealed class RecentMipRequests<TKey> where TKey : notnull
{
    private readonly MipRequestRetentionTuning _tuning;
    private readonly Dictionary<TKey, long> _wantedAt = [];
    private readonly Dictionary<TKey, long> _cancelledAt = [];
    private readonly List<TKey> _expired = [];
    private long _frame;

    public RecentMipRequests(MipRequestRetentionTuning? tuning = null)
    {
        _tuning = tuning ?? new();
        ArgumentOutOfRangeException.ThrowIfNegative(_tuning.Frames);
    }

    public long CancelledStillWanted { get; private set; }
    public IEnumerable<TKey> Wanted => _wantedAt.Keys;

    public void BeginFrame(long frame)
    {
        if (frame < _frame) throw new ArgumentOutOfRangeException(nameof(frame));
        _frame = frame;
        Expire(_wantedAt);
        Expire(_cancelledAt);
    }

    // A request submitted in F survives the next N canvas frames, including empty frames.
    // It is eligible for cancellation in F + N + 1, unless a lease still holds interest.
    private void Expire(Dictionary<TKey, long> history)
    {
        _expired.Clear();
        foreach (var (key, at) in history)
            if (_frame - at > _tuning.Frames) _expired.Add(key);
        foreach (var key in _expired) history.Remove(key);
    }

    public void Submit(TKey key)
    {
        if (_cancelledAt.Remove(key, out var at) && _frame - at <= _tuning.Frames)
            CancelledStillWanted++;
        _wantedAt[key] = _frame;
    }

    public bool IsWanted(TKey key) => _wantedAt.ContainsKey(key);

    /// <summary>Call only when the worker queue actually cancels a request. Detects both early cancels
    /// and a request submitted again within N frames after cancellation (once per cancellation).</summary>
    public void Cancelled(TKey key)
    {
        if (IsWanted(key)) CancelledStillWanted++;
        else _cancelledAt[key] = _frame;
    }
}
