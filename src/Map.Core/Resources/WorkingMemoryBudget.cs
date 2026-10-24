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

    /// <summary>Measured heavy operations up to this size skip the cross-window slot (see <see cref="EnterHeavyAsync"/>).</summary>
    public const long SmallMeasuredBypassBytes = 64 * MiB;
    public const long SmallSystemReserveBytes = 512 * MiB;
    // What separates a cautious reserve from a mean one is not the size of the work but whether
    // its size is known. An operation that measured itself - a river of four nodes, a save of a
    // map made of one rectangle - is asked to leave MeasuredReserveFactor times what it takes,
    // between a floor that keeps the OS working and the same ceiling unknown work owes. There is
    // no step in that curve: one more object on a small map must not move the save into a
    // category that demands half a gigabyte of idle RAM.
    public const long TinySystemReserveBytes = 64 * MiB;
    public const long MeasuredReserveFactor = 4;
    // Shared reads through a replaceable source. Test processes pin it to a fixed machine, so
    // their results do not follow how much memory Windows happens to have free: with about 2 GB
    // free 1-19 UI tests were refused (2026-10-23). The program itself never replaces it; the
    // refusals are tested on budgets of their own.
    private static Func<WorkingMemorySnapshot> _sharedRead = ReadSystem;
    public static WorkingMemoryBudget Shared { get; } = new(() => Volatile.Read(ref _sharedRead)());

    /// <summary>Pins <see cref="Shared"/> of a test process to the given machine.</summary>
    internal static void UseFixedSharedMemoryForTests(WorkingMemorySnapshot snapshot) =>
        Volatile.Write(ref _sharedRead, () => snapshot);
    private readonly Func<WorkingMemorySnapshot> _read;
    private readonly object _gate = new();
    private long _reserved;
    private volatile bool _pressure;
    private long _lastPressureProbe;
    private int _pressureProbeRunning;
    private static readonly Mutex HeavyGate = new(false, "Local\\Map.HeavyOperation."
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..16]);

    public WorkingMemoryBudget(Func<WorkingMemorySnapshot> read) => _read = read;
    public long ReservedBytes { get { lock (_gate) return _reserved; } }
    public WorkingMemorySnapshot Snapshot => _read();
    public bool IsUnderPressure
    {
        get
        {
            // A failed or completed edit may leave this flag set for an entire camera-only
            // session. Recheck recovery without scanning processes on the render thread.
            // Admission still reads fresh memory synchronously inside TryReserve.
            if (_pressure && Environment.TickCount64 - Volatile.Read(ref _lastPressureProbe) >= 500
                && Interlocked.CompareExchange(ref _pressureProbeRunning, 1, 0) == 0)
            {
                Volatile.Write(ref _lastPressureProbe, Environment.TickCount64);
                _ = Task.Run(() =>
                {
                    try
                    {
                        lock (_gate)
                        {
                            var memory = _read();
                            _pressure = Math.Min(memory.AvailableBytes - SystemReserve(memory),
                                PrivateLimit(memory) - memory.MapPrivateBytes) - _reserved < HeavyReserveBytes;
                        }
                    }
                    catch { /* A failed probe must not relax the last known pressure state. */ }
                    finally { Volatile.Write(ref _pressureProbeRunning, 0); }
                });
            }
            return _pressure;
        }
    }
    public static long PrivateLimit(WorkingMemorySnapshot memory) => Math.Min(6 * 1024 * MiB, memory.PhysicalBytes * 2 / 5);
    public static long SystemReserve(WorkingMemorySnapshot memory) => Math.Max(2 * 1024 * MiB, memory.PhysicalBytes / 8);

    /// <summary>
    /// Derived caches (render tiles, overview images, carved land coverage) drop what they hold.
    /// Raised before an admission is refused, so a real edit is not rejected while megabytes of
    /// recomputable pixels are still resident. Handlers must not reserve memory themselves.
    /// </summary>
    public static event Action? ReleaseDerivedCaches;

    private bool _shedding;

    /// <summary>
    /// Admits one operation. <paramref name="measured"/> says the caller computed
    /// <paramref name="bytes"/> from the work in front of it rather than taking a shared default;
    /// only then is the OS reserve allowed to follow the size of the operation.
    /// </summary>
    public bool TryReserve(long bytes, out IDisposable? reservation, out string? reason, bool measured = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            var memory = _read();
            var headroom = Headroom(memory, bytes, measured);
            if (bytes > headroom - _reserved && !_shedding)
            {
                // Under pressure the caches are asked to let go once, and the decision is retaken
                // on freshly measured memory. Nothing owned by the document or history is touched.
                _pressure = true;
                _shedding = true;
                try { ReleaseDerivedCaches?.Invoke(); }
                finally { _shedding = false; }
                memory = _read();
                headroom = Headroom(memory, bytes, measured);
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
    /// Memory this budget may hand out for one operation of the given size. Work whose size is
    /// known leaves <see cref="MeasuredReserveFactor"/> times what it takes, never less than
    /// <see cref="TinySystemReserveBytes"/> and never more than work of unknown size owes. Work of
    /// unknown size keeps the cautious floor it always had. Either way the figure is computed with
    /// the already reserved bytes included, so repeated small admissions cannot add up into an
    /// unguarded large one, and large work still answers to the full OS reserve.
    /// </summary>
    private long Headroom(WorkingMemorySnapshot memory, long bytes, bool measured)
    {
        var effective = bytes + _reserved;
        var systemReserve = effective > SmallOperationLimitBytes
            ? SystemReserve(memory)
            : Math.Min(SystemReserve(memory), measured
                ? Math.Clamp(effective * MeasuredReserveFactor, TinySystemReserveBytes, SmallSystemReserveBytes)
                : SmallSystemReserveBytes);
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
        // A small measured operation - the save, autosave or version of an ordinary map - does not
        // queue for the cross-window slot: an hours-long whole-map export in another window held it
        // and froze every save and the close of the owner's map behind it (2026-10-21). The memory
        // reservation still admits it only when the machine has room.
        if (reserveBytes is { } measured && measured <= SmallMeasuredBypassBytes)
        {
            token.ThrowIfCancellationRequested();
            return TryReserve(reserve, out var small, out var smallReason, measured: true)
                ? Task.FromResult(small!)
                : Task.FromException<IDisposable>(new InvalidOperationException(smallReason));
        }

        // The mutex owner stays on one worker until disposal. OS abandonment recovery releases
        // the slot after a crashed process; a named semaphore would strand other open windows.
        // The worker is a thread of its own: it waits for the whole lease, and holding a pool
        // thread that long left the pool short on a 2-core machine (5 s waits in CI).
        var completion = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Factory.StartNew(() =>
        {
            var owned = false;
            using var released = new ManualResetEventSlim();
            try
            {
                try { owned = WaitHandle.WaitAny([HeavyGate, token.WaitHandle]) == 0; }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) throw new OperationCanceledException(token);
                token.ThrowIfCancellationRequested();
                if (!TryReserve(reserve, out var reservation, out var reason, measured: reserveBytes is not null))
                    throw new InvalidOperationException(reason);
                completion.TrySetResult(new HeavyReservation(reservation!, released));
                released.Wait();
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { if (owned) HeavyGate.ReleaseMutex(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
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

    // Private bytes of the other open Map windows. Enumerating processes walks a snapshot of every
    // process in Windows (7-20 ms on the owner's PC) and ran two to three times per edit on the
    // UI thread. The figure is refreshed in the background at most once per second; this
    // process's own commitment and the free physical memory stay exact on every read.
    private static long _otherMapPrivate;
    private static long _otherMapPrivateRead = long.MinValue;
    private static int _otherMapPrivateRefreshing;
    private const long OtherMapPrivateRefreshMs = 1000;

    private static WorkingMemorySnapshot ReadSystem()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var totalPrivate = OwnPrivateBytes() + OtherMapPrivateBytes();
        if (OperatingSystem.IsWindows() && GlobalMemoryStatusEx(ref status))
            return new((long)status.TotalPhysical, (long)status.AvailablePhysical, totalPrivate);
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return new(available, Math.Max(0, available - totalPrivate), totalPrivate);
    }

    private static long OwnPrivateBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
            if (K32GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size))
                return (long)counters.PrivateUsage;
        }
        using var current = Process.GetCurrentProcess();
        return current.PrivateMemorySize64;
    }

    private static long OtherMapPrivateBytes()
    {
        var read = Interlocked.Read(ref _otherMapPrivateRead);
        if (read == long.MinValue)
        {
            // The first admission of the process has no earlier figure to lean on.
            RefreshOtherMapPrivate();
            return Interlocked.Read(ref _otherMapPrivate);
        }
        if (Environment.TickCount64 - read >= OtherMapPrivateRefreshMs
            && Interlocked.CompareExchange(ref _otherMapPrivateRefreshing, 1, 0) == 0)
        {
            _ = Task.Run(() =>
            {
                try { RefreshOtherMapPrivate(); }
                catch { /* The last known figure stays in use. */ }
                finally { Volatile.Write(ref _otherMapPrivateRefreshing, 0); }
            });
        }
        return Interlocked.Read(ref _otherMapPrivate);
    }

    private static void RefreshOtherMapPrivate()
    {
        var own = Environment.ProcessId;
        long total = 0;
        foreach (var process in Process.GetProcessesByName("Map.App"))
        {
            using (process)
                try { if (process.Id != own) total += process.PrivateMemorySize64; }
                catch (InvalidOperationException) { /* A concurrently closed process owns no next operation. */ }
        }
        Interlocked.Exchange(ref _otherMapPrivate, total);
        Interlocked.Exchange(ref _otherMapPrivateRead, Environment.TickCount64);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
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
