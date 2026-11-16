using System.Reflection;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class EdgePanImagePriorityTests
{
    [Fact]
    public async Task Waiting_reads_follow_the_latest_view_and_abandoned_reads_are_cancelled()
    {
        await using var fixture = new OwnerMipFixture();
        var record = fixture.Import(1)[0];
        await using var store = new StampDerivedAssetStore(fixture.Catalog, fixture.Derived, startWatcher: _ => { });
        // Dispatch does not depend on metadata preparation (its cross-process lock is Windows-only).
        var stamp = StampDerivedAssetStore.FileStamp.Read(fixture.Catalog.SourcePath(record))!;
        using var original = SKBitmap.Decode(stamp.Path);
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, System.Text.Encoding.UTF8, leaveOpen: true))
            foreach (var level in new[] { 2, 3, 4 })
            {
                using var bitmap = original.Resize(new SKImageInfo(record.Width >> level, record.Height >> level,
                    SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear));
                var pixels = bitmap.Bytes;
                writer.Write(level); writer.Write(bitmap.Width); writer.Write(bitmap.Height);
                writer.Write(pixels.Length); writer.Write(pixels);
            }
        var bytes = raw.ToArray();
        var memory = (Dictionary<string, byte[]>)typeof(StampDerivedAssetStore)
            .GetField("_memoryPixels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        memory.Add(record.Sha256, bytes);
        var header = new StampDerivedAssetStore.Header(1, record.Sha256, record.Width, record.Height, 0,
            stamp, stamp, 2, 3, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        Assert.NotNull(header);
        var slots = (StampDerivedAssetStore.PriorityGate)typeof(StampDerivedAssetStore)
            .GetField("_mipSlots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        // Hold the handoff full: none of these requests can have started, independent of disk speed.
        for (var i = 0; i < 64; i++) await slots.WaitAsync(() => 0, CancellationToken.None);
        var oldCentre = new StampImageRequestPriority { Value = 10 };
        var newCentre = new StampImageRequestPriority { Value = 100 };
        store.TryRequestPixels(header, 0, 2, out var old, oldCentre);
        store.TryRequestPixels(header, 0, 3, out var current, newCentre);
        store.TryRequestPixels(header, 0, 4, out var shared, oldCentre);
        // Another visible tile uses the same mip: it must share the task and use its nearer demand.
        store.TryRequestPixels(header, 0, 4, out var same, newCentre);
        Assert.Same(shared, same);
        var deadline = Environment.TickCount64 + 5000;
        while (slots.WaitingCount < 3 && Environment.TickCount64 < deadline) await Task.Delay(1);
        Assert.Equal(3, slots.WaitingCount);
        oldCentre.Value = 1000;
        newCentre.Value = 1;
        slots.Release();
        var first = await Task.WhenAny(current, shared).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False((await first).Cancelled);
        Assert.NotNull((await first).Pixels);
        Assert.False(old.IsCompleted);
        var keep = new HashSet<StampDerivedAssetStore.PixelKey>
        {
            new(header.Sha, 0, ReferenceEquals(first, current) ? 3 : 4),
        };
        store.CancelUnwantedPixels(keep);
        Assert.True((await old.WaitAsync(TimeSpan.FromSeconds(5))).Cancelled);
        var abandoned = ReferenceEquals(first, current) ? shared : current;
        Assert.True((await abandoned.WaitAsync(TimeSpan.FromSeconds(5))).Cancelled);
        foreach (var level in new[] { 2, 3, 4 })
            if (store.TryTakePixels(new(header.Sha, 0, level), out var mip)) mip?.Image?.Dispose();
        for (var i = 0; i < 63; i++) slots.Release();
        Assert.Equal(0, store.PendingMips);
    }

    [Fact]
    public async Task Priority_gate_reorders_waiters_without_losing_capacity_on_cancellation()
    {
        using var gate = new StampDerivedAssetStore.PriorityGate(1);
        await gate.WaitAsync(() => 0, CancellationToken.None);
        var distance = 100d;
        using var cancel = new CancellationTokenSource();
        var abandoned = Enumerable.Range(0, 4094).Select(_ => gate.WaitAsync(() => -100, cancel.Token)).ToArray();
        var ahead = gate.WaitAsync(() => distance, CancellationToken.None);
        var behind = gate.WaitAsync(() => 10, CancellationToken.None);
        Assert.Equal(4096, gate.WaitingCount);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(abandoned));
        Assert.Equal(2, gate.WaitingCount);
        distance = 1;
        gate.Release();
        await ahead.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(behind.IsCompleted);
        gate.Release();
        await behind.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release();
        await gate.WaitAsync(() => 0, CancellationToken.None);
        gate.Release();
    }
}
