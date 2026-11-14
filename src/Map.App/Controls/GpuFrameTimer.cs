using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;
using Avalonia.Skia;
using SkiaSharp;

namespace Map.App.Controls;

/// <summary>
/// DEC-111 sensor 1: how long the GPU worked on a canvas frame, read a few frames later from asynchronous
/// <c>GL_EXT_disjoint_timer_query</c> time-elapsed queries (ANGLE on D3D11 exposes it). It never waits for the GPU:
/// a frame's result is taken only once the driver reports it available, and a result spanning a disjoint event
/// (power state, reset) is dropped. Where the extension or a GL context is missing it stays off and reports nothing;
/// the synchronous flush measurement (<c>MAP_MEASURE_GPU_FLUSH</c>) remains a separate diagnostic.
/// Render thread only.
/// </summary>
internal sealed class GpuFrameTimer
{
    private const int TimeElapsed = 0x88BF;      // GL_TIME_ELAPSED_EXT
    private const int QueryResult = 0x8866;      // GL_QUERY_RESULT_EXT
    private const int QueryResultAvailable = 0x8867; // GL_QUERY_RESULT_AVAILABLE_EXT
    private const int GpuDisjoint = 0x8FBB;      // GL_GPU_DISJOINT_EXT
    private const int Extensions = 0x1F03;       // GL_EXTENSIONS
    private const int Ring = 6;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GenQueries(int n, uint[] ids);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BeginQuery(int target, uint id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void EndQuery(int target);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GetQueryObjectUi(uint id, int name, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GetQueryObjectUi64(uint id, int name, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GetIntegerv(int name, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr GetString(int name);

    private enum State { Unknown, On, Off }
    private State _state;
    private object? _context;
    private BeginQuery? _begin;
    private EndQuery? _end;
    private GetQueryObjectUi? _available;
    private GetQueryObjectUi64? _result;
    private GetIntegerv? _integer;
    private readonly uint[] _queries = new uint[Ring];
    private readonly bool[] _pending = new bool[Ring];
    private readonly bool[] _ignored = new bool[Ring];
    private readonly long[] _frameQpc = new long[Ring];
    /// <summary>QPC (Stopwatch ticks) at which the frame of <see cref="LastMilliseconds"/> opened its query.</summary>
    public long LastFrameQpc { get; private set; }
    private int _next;
    private bool _open;

    /// <summary>
    /// Diagnostics (MAP_GPU_TIMER_SPAN=1): measure frame start → next frame start on the GPU timeline instead of the
    /// canvas's own commands — the whole window's GPU work (composition, other controls, present) falls inside.
    /// Note: GPU idle time between frames is included as well when the GPU waits.
    /// </summary>
    public static bool Span { get; } = Environment.GetEnvironmentVariable("MAP_GPU_TIMER_SPAN") == "1";

    /// <summary>GPU milliseconds of the most recent frame whose query completed; null until one did.</summary>
    public double? LastMilliseconds { get; private set; }
    public long Completed { get; private set; }
    public long Dropped { get; private set; }
    public bool Available => _state == State.On;

    /// <summary>Opens the time-elapsed query for this frame. Call before the frame's drawing.</summary>
    public void Begin(ISkiaSharpApiLease lease)
    {
        if (_state == State.Off) return;
        try
        {
            using var api = lease.TryLeasePlatformGraphicsApi();
            if (api?.Context is not IGlContext gl) { _state = State.Off; return; }
            if (!ReferenceEquals(_context, gl)) { _open = false; Initialise(gl); }
            if (_state != State.On) return;
            if (_open)
            {
                // Span mode: the previous query runs from that frame's start to this one's, so it also covers the
                // window composition and present work Avalonia submits after the canvas. Otherwise the previous
                // frame left early without End: close its query and ignore its result.
                _end!(TimeElapsed);
                _open = false;
                _pending[_next] = true; _ignored[_next] = !Span;
                _next = (_next + 1) % Ring;
            }
            Collect();
            if (_pending[_next]) { Dropped++; return; } // ring full: the GPU is several frames behind
            _begin!(TimeElapsed, _queries[_next]);
            _frameQpc[_next] = System.Diagnostics.Stopwatch.GetTimestamp();
            _open = true;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or MarshalDirectiveException or InvalidOperationException)
        { _state = State.Off; }
    }

    /// <summary>Submits the frame's recorded work (asynchronously) and closes its query.</summary>
    public void End(ISkiaSharpApiLease lease, GRContext? gpu)
    {
        if (!_open || Span) return; // span mode closes the query at the next frame's Begin
        _open = false;
        try
        {
            // Skia records the frame; flush (not submit-and-wait) so its GPU commands fall inside the query.
            gpu?.Flush();
            using var api = lease.TryLeasePlatformGraphicsApi();
            if (api?.Context is not IGlContext) { _state = State.Off; return; }
            _end!(TimeElapsed);
            _pending[_next] = true;
            _next = (_next + 1) % Ring;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or MarshalDirectiveException or InvalidOperationException)
        { _state = State.Off; }
    }

    private void Initialise(IGlContext gl)
    {
        _context = gl;
        _state = State.Off;
        Array.Clear(_pending);
        Array.Clear(_ignored);
        var glInterface = gl.GlInterface;
        T? Proc<T>(string name) where T : Delegate
        {
            var address = glInterface.GetProcAddress(name);
            return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
        }
        var getString = Proc<GetString>("glGetString");
        var extensions = getString is null ? null : Marshal.PtrToStringAnsi(getString(Extensions));
        if (extensions is null || !extensions.Contains("GL_EXT_disjoint_timer_query", StringComparison.Ordinal)) return;
        var generate = Proc<GenQueries>("glGenQueriesEXT");
        _begin = Proc<BeginQuery>("glBeginQueryEXT");
        _end = Proc<EndQuery>("glEndQueryEXT");
        _available = Proc<GetQueryObjectUi>("glGetQueryObjectuivEXT");
        _result = Proc<GetQueryObjectUi64>("glGetQueryObjectui64vEXT");
        _integer = Proc<GetIntegerv>("glGetIntegerv");
        if (generate is null || _begin is null || _end is null || _available is null || _result is null || _integer is null) return;
        generate(Ring, _queries);
        if (_queries.Any(q => q == 0)) return;
        _integer(GpuDisjoint, out _); // reading clears a stale disjoint flag
        _state = State.On;
    }

    private void Collect()
    {
        // Oldest first; stop at the first one the GPU has not finished.
        for (var n = 1; n <= Ring; n++)
        {
            var index = (_next + n) % Ring;
            if (!_pending[index]) continue;
            _available!(_queries[index], QueryResultAvailable, out var ready);
            if (ready == 0) break;
            _result!(_queries[index], QueryResult, out var nanoseconds);
            _pending[index] = false;
            _integer!(GpuDisjoint, out var disjoint);
            if (_ignored[index]) { _ignored[index] = false; continue; }
            if (disjoint != 0) { Dropped++; continue; }
            LastMilliseconds = nanoseconds / 1_000_000d;
            LastFrameQpc = _frameQpc[index];
            Completed++;
        }
    }
}
