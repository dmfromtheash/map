using System;
using System.Runtime.InteropServices;

namespace Map.App.Services;

/// <summary>
/// Diagnostics (DEC-050): when Map runs under RenderDoc (renderdoc.dll is already in the process)
/// and MAP_RENDERDOC_CAPTURE_FRAMES names map frame numbers ("120" or "120,400"), the frame after
/// each named one is captured to a .rdc file through RenderDoc's in-application API, so a scripted
/// scenario (tools/ai/live/renderdoc-capture.ps1) gets a GPU frame without anyone pressing F12.
/// MAP_RENDERDOC_CAPTURE_PATH sets the capture file template (folder + name, no extension).
/// Off in the product: without the variables or without RenderDoc nothing is looked up.
/// </summary>
internal static class RenderDocCapture
{
    private const int ApiVersion112 = 10102;
    private const int UnloadCrashHandlerIndex = 10;
    private const int SetCaptureFilePathTemplateIndex = 11;
    private const int GetNumCapturesIndex = 13;
    private const int TriggerCaptureIndex = 15;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetApiDelegate(int version, out IntPtr api);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoidDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint UIntDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StringDelegate([MarshalAs(UnmanagedType.LPStr)] string value);

    private static readonly long[] Frames = ParseFrames(Environment.GetEnvironmentVariable("MAP_RENDERDOC_CAPTURE_FRAMES"));
    private static readonly object Gate = new();
    private static VoidDelegate? _trigger;
    private static VoidDelegate? _unloadCrashHandler;
    private static UIntDelegate? _numCaptures;
    private static bool _resolved;
    private static int _next;

    public static bool Enabled => Frames.Length > 0;

    /// <summary>
    /// First thing in Main when RenderDoc is in the process: its Breakpad crash handler must go,
    /// because the .NET runtime raises NullReferenceException through a hardware access violation
    /// that the handler would report as a crash and end the process.
    /// </summary>
    public static void DisableCrashHandler()
    {
        lock (Gate)
        {
            if (!_resolved) Resolve();
            try { _unloadCrashHandler?.Invoke(); }
            catch (Exception error) when (error is EntryPointNotFoundException or AccessViolationException) { }
            _unloadCrashHandler = null;
        }
    }

    /// <summary>Called once per completed map frame with its number; cheap when disabled.</summary>
    public static void OnFrameRendered(long frame)
    {
        if (Frames.Length == 0 || _next >= Frames.Length || frame < Frames[_next]) return;
        lock (Gate)
        {
            if (_next >= Frames.Length || frame < Frames[_next]) return;
            if (!_resolved) Resolve();
            _next++;
            try
            {
                _trigger?.Invoke();
            }
            catch (Exception error) when (error is EntryPointNotFoundException or AccessViolationException or InvalidOperationException)
            {
                _trigger = null;
            }
        }
    }

    /// <summary>How many captures RenderDoc has written so far in this process (0 without RenderDoc).</summary>
    public static uint CaptureCount
    {
        get
        {
            lock (Gate)
            {
                if (!_resolved) Resolve();
                try { return _numCaptures?.Invoke() ?? 0; }
                catch (Exception error) when (error is EntryPointNotFoundException or AccessViolationException) { return 0; }
            }
        }
    }

    private static void Resolve()
    {
        _resolved = true;
        try
        {
            var module = GetModuleHandle("renderdoc.dll");
            if (module == IntPtr.Zero) return;
            var entry = GetProcAddress(module, "RENDERDOC_GetAPI");
            if (entry == IntPtr.Zero) return;
            var getApi = Marshal.GetDelegateForFunctionPointer<GetApiDelegate>(entry);
            if (getApi(ApiVersion112, out var api) != 1 || api == IntPtr.Zero) return;
            _trigger = Marshal.GetDelegateForFunctionPointer<VoidDelegate>(Marshal.ReadIntPtr(api, TriggerCaptureIndex * IntPtr.Size));
            _unloadCrashHandler = Marshal.GetDelegateForFunctionPointer<VoidDelegate>(Marshal.ReadIntPtr(api, UnloadCrashHandlerIndex * IntPtr.Size));
            _numCaptures = Marshal.GetDelegateForFunctionPointer<UIntDelegate>(Marshal.ReadIntPtr(api, GetNumCapturesIndex * IntPtr.Size));
            var template = Environment.GetEnvironmentVariable("MAP_RENDERDOC_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(template))
            {
                var setTemplate = Marshal.GetDelegateForFunctionPointer<StringDelegate>(Marshal.ReadIntPtr(api, SetCaptureFilePathTemplateIndex * IntPtr.Size));
                setTemplate(template);
            }
        }
        catch (Exception error) when (error is EntryPointNotFoundException or AccessViolationException or MarshalDirectiveException)
        {
            _trigger = null;
            _numCaptures = null;
        }
    }

    private static long[] ParseFrames(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var list = new System.Collections.Generic.List<long>();
        foreach (var part in value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(part, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var frame) && frame > 0)
            {
                list.Add(frame);
            }
        }

        list.Sort();
        return [.. list];
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);
}
