using System.Diagnostics;

namespace Map.Presentation.ViewModels;

/// <summary>Latest work per key, cooperatively yielded after a row limit or time budget.
/// A callback is one row/small operation; callers must split loops before enqueueing.</summary>
public sealed class UiWorkQueue(Action<Action> post, int rowsPerSlice = 16, double millisecondsPerSlice = 4)
{
    private readonly Dictionary<string, IEnumerator<Action>> _work = new(StringComparer.Ordinal);
    private bool _queued;
    public event Action<double, int>? SliceCompleted;
    public void Replace(string key, IEnumerable<Action> work)
    {
        if (_work.Remove(key, out var previous)) previous.Dispose();
        _work[key] = work.GetEnumerator();
        Queue();
    }
    public void Clear()
    {
        foreach (var work in _work.Values) work.Dispose();
        _work.Clear();
    }
    private void Queue()
    {
        if (_queued || _work.Count == 0) return;
        _queued = true;
        post(Run);
    }
    private void Run()
    {
        _queued = false;
        var started = Stopwatch.GetTimestamp();
        var rows = 0;
        for (; rows < rowsPerSlice && _work.Count > 0;)
        {
            rows++;
            var (key, work) = _work.First();
            if (work.MoveNext()) work.Current();
            else if (_work.TryGetValue(key, out var current) && ReferenceEquals(work, current))
            { _work.Remove(key); work.Dispose(); }
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= millisecondsPerSlice) break;
        }
        SliceCompleted?.Invoke(Stopwatch.GetElapsedTime(started).TotalMilliseconds, rows);
        Queue();
    }
}
