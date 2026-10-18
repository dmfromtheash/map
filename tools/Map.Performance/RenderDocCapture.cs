using System.Runtime.InteropServices;

namespace Map.Performance;

/// <summary>
/// RenderDoc's in-application API, when this process was launched by RenderDoc
/// (<c>renderdoccmd capture Map.Performance.exe ...</c>): <see cref="TriggerCapture"/> asks it to
/// capture the next presented frame. MAP_RENDERDOC_ROUTE=&lt;route&gt; and MAP_RENDERDOC_STEP=&lt;n&gt;
/// pick the frame of a burst route (the cliff slider, a zoom) whose GPU work is to be examined.
/// Without RenderDoc in the process every call does nothing.
/// </summary>
internal static class RenderDocCapture
{
    // renderdoc_app.h: RENDERDOC_GetAPI(RENDERDOC_Version, void** outAPIPointers); 1.6.0 = 10600.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetApi(int version, out IntPtr api);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TriggerCaptureFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetNumCapturesFn();

    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

    // Positions in RENDERDOC_API_1_6_0, a table of function pointers.
    private const int TriggerCaptureSlot = 15;
    private const int GetNumCapturesSlot = 13;

    private static readonly Lazy<IntPtr> Api = new(() =>
    {
        var module = GetModuleHandle("renderdoc.dll");
        if (module == IntPtr.Zero) return IntPtr.Zero;
        var proc = GetProcAddress(module, "RENDERDOC_GetAPI");
        if (proc == IntPtr.Zero) return IntPtr.Zero;
        var get = Marshal.GetDelegateForFunctionPointer<GetApi>(proc);
        return get(10600, out var api) == 1 ? api : IntPtr.Zero;
    });

    public static bool Available => Api.Value != IntPtr.Zero;

    public static void TriggerCapture()
    {
        if (!Available) return;
        var slot = Marshal.ReadIntPtr(Api.Value, TriggerCaptureSlot * IntPtr.Size);
        Marshal.GetDelegateForFunctionPointer<TriggerCaptureFn>(slot)();
    }

    public static uint Captures()
    {
        if (!Available) return 0;
        var slot = Marshal.ReadIntPtr(Api.Value, GetNumCapturesSlot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<GetNumCapturesFn>(slot)();
    }

    /// <summary>Called for every step of a burst route: triggers on the requested route and step.</summary>
    public static void OnStep(string route, int index)
    {
        if (!Available || Environment.GetEnvironmentVariable("MAP_RENDERDOC_ROUTE") is not { Length: > 0 } wanted) return;
        var step = int.TryParse(Environment.GetEnvironmentVariable("MAP_RENDERDOC_STEP"), out var n) ? n : 10;
        if (string.Equals(route, wanted, StringComparison.Ordinal) && index == step)
        {
            TriggerCapture();
            Console.WriteLine($"RENDERDOC capture triggered on {route} step {index}");
        }
    }
}
