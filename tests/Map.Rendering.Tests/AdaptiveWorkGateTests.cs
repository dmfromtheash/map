using Map.Rendering;

namespace Map.Rendering.Tests;

public sealed class AdaptiveWorkGateTests
{
    [Fact]
    public async Task Pressure_prevents_new_admissions_until_existing_lanes_drain()
    {
        var limit = 4;
        using var gate = new AdaptiveWorkGate(() => limit);
        for (var i = 0; i < 4; i++) await gate.WaitAsync();
        limit = 1;
        var waiting = gate.WaitAsync();
        Assert.False(waiting.IsCompleted);
        for (var i = 0; i < 3; i++)
        {
            gate.Release();
            Assert.False(waiting.IsCompleted);
        }
        gate.Release();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release();
        limit = 4;
        for (var i = 0; i < 4; i++) Assert.True(gate.WaitAsync().IsCompletedSuccessfully);
        for (var i = 0; i < 4; i++) gate.Release();
    }

    [Fact]
    public async Task A_large_queue_is_admitted_in_order_without_waking_or_starving_other_waiters()
    {
        using var gate = new AdaptiveWorkGate(() => 1);
        await gate.WaitAsync();
        var waiting = Enumerable.Range(0, 1000).Select(_ => gate.WaitAsync()).ToArray();
        for (var i = 0; i < waiting.Length; i++)
        {
            gate.Release();
            await waiting[i].WaitAsync(TimeSpan.FromSeconds(5));
            if (i + 1 < waiting.Length) Assert.False(waiting[i + 1].IsCompleted);
        }
        gate.Release();
    }

    [Fact]
    public async Task Cancellation_of_a_waiter_does_not_consume_capacity()
    {
        using var gate = new AdaptiveWorkGate(() => 1);
        await gate.WaitAsync();
        using var stop = new CancellationTokenSource();
        var waiting = gate.WaitAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        gate.Release();
        Assert.True(gate.WaitAsync().IsCompletedSuccessfully);
        gate.Release();
    }
}
