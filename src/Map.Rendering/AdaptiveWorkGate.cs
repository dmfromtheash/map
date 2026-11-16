namespace Map.Rendering;

/// <summary>Concurrency follows the current machine snapshot at each admission. A reduction does
/// not interrupt work already in flight; new work waits until enough lanes have drained.</summary>
public sealed class AdaptiveWorkGate(Func<int> limit) : IDisposable
{
    private readonly object _gate = new();
    private int _running;
    private bool _disposed;
    private readonly LinkedList<Waiter> _waiting = [];

    private sealed class Waiter(AdaptiveWorkGate owner, CancellationToken token)
    {
        public readonly AdaptiveWorkGate Owner = owner;
        public readonly CancellationToken Token = token;
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node;
        public CancellationTokenRegistration Registration;
    }

    public Task WaitAsync(CancellationToken cancellation = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellation.ThrowIfCancellationRequested();
            if (_waiting.Count == 0 && _running < Math.Max(1, limit())) { _running++; return Task.CompletedTask; }
            var waiter = new Waiter(this, cancellation);
            waiter.Node = _waiting.AddLast(waiter);
            waiter.Registration = cancellation.UnsafeRegister(static (state, token) =>
            {
                var pending = (Waiter)state!;
                pending.Owner.Cancel(pending, token);
            }, waiter);
            // A token cancelled during Register can have removed the node synchronously.
            if (waiter.Node is null) waiter.Registration.Unregister();
            Drain();
            return waiter.Completion.Task;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_running == 0) throw new SemaphoreFullException();
            _running--;
            if (!_disposed) Drain();
        }
    }

    // Under _gate. Wake only granted waiters, never every queued asset on every completion.
    // Each waiter is visited once (or removed in O(1) on cancellation), FIFO without starvation.
    private void Drain()
    {
        var capacity = Math.Max(1, limit());
        while (_running < capacity && _waiting.First is { } node)
        {
            var waiter = node.Value;
            _waiting.Remove(node);
            waiter.Node = null;
            waiter.Registration.Unregister();
            if (waiter.Token.IsCancellationRequested) waiter.Completion.TrySetCanceled(waiter.Token);
            else { _running++; waiter.Completion.TrySetResult(); }
        }
    }

    private void Cancel(Waiter waiter, CancellationToken token)
    {
        lock (_gate)
        {
            if (waiter.Node is not { } node) return;
            _waiting.Remove(node);
            waiter.Node = null;
            waiter.Completion.TrySetCanceled(token);
            if (!_disposed) Drain();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            while (_waiting.First is { } node)
            {
                _waiting.Remove(node);
                node.Value.Node = null;
                node.Value.Registration.Unregister();
                node.Value.Completion.TrySetException(new ObjectDisposedException(nameof(AdaptiveWorkGate)));
            }
        }
    }
}
