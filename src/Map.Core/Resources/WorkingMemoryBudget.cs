using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Map.Core.Resources;

public readonly record struct WorkingMemorySnapshot(long PhysicalBytes, long AvailableBytes, long MapPrivateBytes);

/// <summary>Shared admission for live documents/history and overlapping native operations.
/// It measures private commitment across Map processes; it is not a replacement allocator or
/// a claim that driver allocations can be hard-capped by a managed application.</summary>
public sealed class WorkingMemoryBudget
{
    public const long MiB = 1024L * 1024;
    public const long EditReserveBytes = 16 * MiB;
    public const long HeavyReserveBytes = 128 * MiB;
    // A small edit/save plus its nested validation must not require 2 GiB of idle RAM.
    // The allowance is cumulative, so many small reservations cannot bypass the large-work gate.
    public const long SmallOperationLimitBytes = 256 * MiB;
    public const long SmallSystemReserveBytes = 512 * MiB;
    // A vector edit that only rewrites a handful of nodes commits kilobytes, not megabytes. Such
    // an operation must not inherit the reserve of a raster edit: on a busy desktop the machine
    // can be down to a few hundred MiB free and still owe the user a moved river node. The floor
    // stays well above zero so the OS keeps a working set even while Map admits the edit.
    public const long TinyOperationLimitBytes = 4 * MiB;
    public const long TinySystemReserveBytes = 64 * MiB;
    public static WorkingMemoryBudget Shared { get; } = new(ReadSystem);
    private readonly Func<WorkingMemorySnapshot> _read;
    private readonly object _gate = new();
    private long _reserved;
    private volatile bool _pressure;
    private static readonly Mutex HeavyGate = new(false, "Local\\Map.HeavyOperation."
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..16]);

    public WorkingMemoryBudget(Func<WorkingMemorySnapshot> read) => _read = read;
    public long ReservedBytes { get { lock (_gate) return _reserved; } }
    public WorkingMemorySnapshot Snapshot => _read();
    public bool IsUnderPressure => _pressure;
    public static long PrivateLimit(WorkingMemorySnapshot memory) => Math.Min(6 * 1024 * MiB, memory.PhysicalBytes * 2 / 5);
    public static long SystemReserve(WorkingMemorySnapshot memory) => Math.Max(2 * 1024 * MiB, memory.PhysicalBytes / 8);

    /// <summary>
    /// Derived caches (render tiles, overview images, carved land coverage) drop what they hold.
    /// Raised before an admission is refused, so a real edit is not rejected while megabytes of
    /// recomputable pixels are still resident. Handlers must not reserve memory themselves.
    /// </summary>
    public static event Action? ReleaseDerivedCaches;

    private bool _shedding;

    public bool TryReserve(long bytes, out IDisposable? reservation, out string? reason)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            var memory = _read();
            var headroom = Headroom(memory, bytes);
            if (bytes > headroom - _reserved && !_shedding)
            {
                // Under pressure the caches are asked to let go once, and the decision is retaken
                // on freshly measured memory. Nothing owned by the document or history is touched.
                _pressure = true;
                _shedding = true;
                try { ReleaseDerivedCaches?.Invoke(); }
                finally { _shedding = false; }
                memory = _read();
                headroom = Headroom(memory, bytes);
            }
            if (bytes > headroom - _reserved)
            {
                _pressure = true;
                reservation = null;
                reason = "Недостаточно свободной памяти с резервом для Windows и открытых карт. "
                    + $"Свободно в Windows: {Math.Max(0, memory.AvailableBytes) / MiB} МиБ. "
                    + $"Для действия доступно {Math.Max(0, headroom - _reserved) / MiB} МиБ, требуется {bytes / MiB + (bytes % MiB == 0 ? 0 : 1)} МиБ. "
                    + "Действие не применено; карта и история сохранены. Дождитесь завершения текущей операции или освободите память.";
                return false;
            }
            _reserved += bytes;
            // Continue shedding unpinned render caches below the normal reserve even when
            // a small operation is admitted. Document and history ownership are unaffected.
            _pressure = Math.Min(memory.AvailableBytes - SystemReserve(memory),
                PrivateLimit(memory) - memory.MapPrivateBytes) - _reserved < HeavyReserveBytes;
            reservation = new Reservation(this, bytes); reason = null;
            return true;
        }
    }

    /// <summary>
    /// Memory this budget may hand out for one operation of the given size. The OS reserve is
    /// tiered by the real cost of the work: a node edit, a small edit and a raster operation do
    /// not owe the same idle RAM. The tier is chosen with the already reserved bytes included,
    /// so repeated tiny reservations cannot add up into an unguarded large one.
    /// </summary>
    private long Headroom(WorkingMemorySnapshot memory, long bytes)
    {
        var systemReserve = bytes <= TinyOperationLimitBytes - _reserved
            ? Math.Min(SystemReserve(memory), TinySystemReserveBytes)
            : bytes <= SmallOperationLimitBytes - _reserved
                ? Math.Min(SystemReserve(memory), SmallSystemReserveBytes)
                : SystemReserve(memory);
        return Math.Min(memory.AvailableBytes - systemReserve, PrivateLimit(memory) - memory.MapPrivateBytes);
    }

    /// <summary>
    /// Takes the cross-window slot for one heavy operation and reserves what it will actually
    /// use. <paramref name="reserveBytes"/> is the caller's own estimate, clamped to the shared
    /// heavy reserve: a 1024x768 export is not the same amount of work as a whole-map raster, and
    /// only the second one owes the machine the full figure. Anything above the clamp is reserved
    /// by the caller on top of this lease, so the total still matches the real estimate.
    /// </summary>
    public Task<IDisposable> EnterHeavyAsync(CancellationToken token = default, long? reserveBytes = null)
    {
        var reserve = Math.Clamp(reserveBytes ?? HeavyReserveBytes, MiB, HeavyReserveBytes);
        // The mutex owner stays on one worker until disposal. OS abandonment recovery releases
        // the slot after a crashed process; a named semaphore would strand other open windows.
        var completion = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() =>
        {
            var owned = false;
            using var released = new ManualResetEventSlim();
            try
            {
                try { owned = WaitHandle.WaitAny([HeavyGate, token.WaitHandle]) == 0; }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) throw new OperationCanceledException(token);
                token.ThrowIfCancellationRequested();
                if (!TryReserve(reserve, out var reservation, out var reason))
                    throw new InvalidOperationException(reason);
                completion.TrySetResult(new HeavyReservation(reservation!, released));
                released.Wait();
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { if (owned) HeavyGate.ReleaseMutex(); }
        });
        return completion.Task;
    }

    private sealed class Reservation(WorkingMemoryBudget owner, long bytes) : IDisposable
    {
        private WorkingMemoryBudget? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } budget)
                lock (budget._gate)
                {
                    budget._reserved -= bytes;
                    var memory = budget._read();
                    budget._pressure = Math.Min(memory.AvailableBytes - SystemReserve(memory),
                        PrivateLimit(memory) - memory.MapPrivateBytes) - budget._reserved < HeavyReserveBytes;
                }
        }
    }
    private sealed class HeavyReservation(IDisposable reservation, ManualResetEventSlim released) : IDisposable
    {
        private IDisposable? _reservation = reservation;
        public void Dispose()
        { if (Interlocked.Exchange(ref _reservation, null) is { } held) { held.Dispose(); released.Set(); } }
    }

    private static WorkingMemorySnapshot ReadSystem()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        using var current = Process.GetCurrentProcess();
        var totalPrivate = current.PrivateMemorySize64;
        foreach (var process in Process.GetProcessesByName("Map.App"))
        {
            using (process)
                try { if (process.Id != current.Id) totalPrivate += process.PrivateMemorySize64; }
                catch (InvalidOperationException) { /* A concurrently closed process owns no next operation. */ }
        }
        if (OperatingSystem.IsWindows() && GlobalMemoryStatusEx(ref status))
            return new((long)status.TotalPhysical, (long)status.AvailablePhysical, totalPrivate);
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return new(available, Math.Max(0, available - totalPrivate), totalPrivate);
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
}
