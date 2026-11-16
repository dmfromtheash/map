using Map.Rendering;

namespace Map.Presentation.Services;

public sealed partial class StampDerivedAssetStore
{
    /// <summary>
    /// A bounded resource with live priorities, FIFO for ties. Selection runs on the pool even when
    /// the canvas releases a result slot; cancellation only unlinks its waiter, in constant time.
    /// </summary>
    internal sealed class PriorityGate(int capacity) : IDisposable
    {
        private readonly object _gate = new();
        private readonly LinkedList<Waiter> _waiting = [];
        private int _available = capacity;
        private bool _pumping;
        private bool _disposed;
        internal int WaitingCount { get { lock (_gate) return _waiting.Count; } }

        private sealed class Waiter(Func<double> priority)
        {
            internal readonly Func<double> Priority = priority;
            internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal LinkedListNode<Waiter>? Node;
        }

        internal async Task WaitAsync(Func<double> priority, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var waiter = new Waiter(priority);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_available > 0 && _waiting.Count == 0) { _available--; return; }
                waiter.Node = _waiting.AddLast(waiter);
                Schedule();
            }
            using var cancellation = token.Register(() =>
            {
                lock (_gate)
                {
                    if (waiter.Node?.List is null) return; // already owns the resource
                    _waiting.Remove(waiter.Node);
                    waiter.Completion.TrySetCanceled(token);
                }
            });
            await waiter.Completion.Task.ConfigureAwait(false);
        }

        internal void Release()
        {
            lock (_gate) { _available++; Schedule(); }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                foreach (var waiter in _waiting) waiter.Completion.TrySetCanceled();
                _waiting.Clear();
            }
        }

        // Under _gate. A single scheduled pump covers any number of slot releases.
        private void Schedule()
        {
            if (_pumping || _available == 0 || _waiting.Count == 0) return;
            _pumping = true;
            _ = Task.Run(Pump);
        }

        private void Pump()
        {
            while (true)
            {
                Waiter[] candidates;
                int count;
                lock (_gate)
                {
                    count = _waiting.Count;
                    if (_available == 0 || count == 0) { _pumping = false; return; }
                    candidates = System.Buffers.ArrayPool<Waiter>.Shared.Rent(count);
                    _waiting.CopyTo(candidates, 0);
                }
                // The canvas can release/cancel a slot while thousands of priorities are compared.
                var best = candidates[0];
                var value = best.Priority();
                for (var i = 1; i < count; i++)
                {
                    var candidate = candidates[i].Priority();
                    if (candidate < value) { best = candidates[i]; value = candidate; }
                }
                System.Buffers.ArrayPool<Waiter>.Shared.Return(candidates, clearArray: true);
                lock (_gate)
                {
                    if (best.Node?.List is null) continue;
                    _waiting.Remove(best.Node);
                    _available--;
                    best.Completion.TrySetResult();
                }
            }
        }
    }
}
