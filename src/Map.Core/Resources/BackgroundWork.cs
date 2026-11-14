using System.Diagnostics;

namespace Map.Core.Resources;

/// <summary>
/// Long derived-data work of an open map (lookups, overview preparation) runs on its own
/// below-normal threads, at most <see cref="Concurrency"/> of them at once, and steps aside while
/// the user pans, zooms or drags: on a four-core laptop the render and input threads otherwise
/// share their cores with a million-object build exactly when a frame is due.
/// </summary>
public static class BackgroundWork
{
    /// <summary>How long one gesture frame keeps the gate closed; longer than a slow frame interval.</summary>
    public static readonly TimeSpan InteractionHold = TimeSpan.FromMilliseconds(250);

    /// <summary>Heavy background items at once: two cores stay for the UI and render threads, at least one runs.</summary>
    public static int Concurrency { get; } = Math.Max(1, Environment.ProcessorCount - 2);

    private static readonly SemaphoreSlim Slots = new(Concurrency);
    private static long _interactionUntil = long.MinValue;
    private static long _yields, _startsDuringInteraction;
    [ThreadStatic] private static bool _owned;

    /// <summary>The canvas calls this from every frame of a pan, zoom or drag.</summary>
    public static void NoteInteraction() => Volatile.Write(ref _interactionUntil,
        Stopwatch.GetTimestamp() + (long)(InteractionHold.TotalSeconds * Stopwatch.Frequency));

    public static bool InteractionActive => Stopwatch.GetTimestamp() < Volatile.Read(ref _interactionUntil);

    /// <summary>Diagnostics: background checkpoints that waited for a gesture to end.</summary>
    public static long Yields => Interlocked.Read(ref _yields);

    /// <summary>Diagnostics: heavy items started while a gesture was running (they then wait at their first checkpoint).</summary>
    public static long StartsDuringInteraction => Interlocked.Read(ref _startsDuringInteraction);

    /// <summary>True on a thread started by <see cref="RunAsync"/> or marked by <see cref="EnterOwnedThread"/>.</summary>
    public static bool IsBackgroundThread => _owned;

    /// <summary>Runs <paramref name="work"/> on a new below-normal background thread inside one concurrency slot.</summary>
    public static Task RunAsync(Action work, string name)
    {
        ArgumentNullException.ThrowIfNull(work);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            _owned = true;
            try
            {
                using (EnterSlot())
                {
                    YieldToInteraction();
                    work();
                }
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = name };
        thread.Start();
        return completion.Task;
    }

    /// <summary>Marks a long-lived worker thread of its own (below normal, background, gate-aware).</summary>
    public static void EnterOwnedThread()
    {
        _owned = true;
        try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; }
        catch (Exception error) when (error is ThreadStateException or System.Security.SecurityException) { }
    }

    /// <summary>One concurrency slot, held for the duration of a heavy item; dispose to return it.</summary>
    public static IDisposable EnterSlot(CancellationToken cancellationToken = default)
    {
        if (InteractionActive) Interlocked.Increment(ref _startsDuringInteraction);
        Slots.Wait(cancellationToken);
        return new Slot();
    }

    /// <summary>
    /// A checkpoint between indivisible steps: on a background thread it waits while a gesture
    /// runs; on any other thread (an edit that needs the lookup now) it returns at once.
    /// </summary>
    public static void YieldToInteraction(CancellationToken cancellationToken = default)
    {
        if (!_owned || !InteractionActive) return;
        Interlocked.Increment(ref _yields);
        while (InteractionActive && !cancellationToken.IsCancellationRequested) Thread.Sleep(10);
    }

    private sealed class Slot : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) Slots.Release(); }
    }
}
