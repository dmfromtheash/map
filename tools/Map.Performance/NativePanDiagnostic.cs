using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using Map.App.Controls;
using Map.Presentation.Services;

namespace Map.Performance;

public sealed partial class ZoomAssetsApplication
{
    private async Task Pan100Diagnostic(ZoomScenarioReport scenario, (double X, double Y) anchor)
    {
        var legacy = Environment.GetEnvironmentVariable("MAP_PERF_PAN100_ONLY") == "legacy";
        _report.Environment["scope"] = legacy
            ? "pan100 diagnostic: original fit/scale25/pan25/capture/scale100/pan100 prefix, then settled 600-step pan; no edit/save routes"
            : "pan100 diagnostic: fit/settled25/unsettled100 setup, immediate 120-step then settled 600-step pan; no edit/save routes";
        await Route(scenario, "scale-25-setup", 1, _ => SetScale(anchor, .25));
        if (legacy)
        {
            await BurstRoute(scenario, "pan-25", 120, i => _view.Canvas.PanByScreenDeltaForSmoke(i < 60 ? -3 : 3, 0));
            await MillionCapture("standard-25");
        }
        else await Settled(scenario.Routes[^1]);
        await Route(scenario, "scale-100-setup", 1, _ => SetScale(anchor, 1));
        // Match the legacy route: pan starts immediately after changing scale. Waiting
        // here would hide any competition with cold mip preparation during the gesture.
        await BurstRoute(scenario, "pan-100", 120, i => _view.Canvas.PanByScreenDeltaForSmoke(i < 60 ? -3 : 3, 0));
        await BurstRoute(scenario, "pan-100-long", 600, i => _view.Canvas.PanByScreenDeltaForSmoke(i % 120 < 60 ? -3 : 3, 0));
        await MillionCapture("standard-pan100");
        _report.Checks["gpu"] = scenario.Routes.All(route => route.Gpu);
        _report.Checks["pan100-route-complete"] = scenario.Routes.Count(route => route.Name.StartsWith("pan-100", StringComparison.Ordinal)) == 2;
        _report.Checks["fps-target"] = scenario.Routes.Where(route => route.Name.StartsWith("pan-100", StringComparison.Ordinal))
            .All(route => route.OnePercentLowFps >= 60 && route.FrameWorkP95 <= 16.7 && route.LatencyP95 <= 33);
        await Finish(scenario);
    }

    private PanDiagnostic? StartPanDiagnostic() => Environment.GetEnvironmentVariable("MAP_PERF_PAN100_ONLY") is "1" or "legacy"
        && _editor.StampImages is CatalogStampImageSource source ? new(source) : null;

    private sealed class PanDiagnostic
    {
        private readonly CatalogStampImageSource _source;
        private readonly StampDerivedAssetStore.IoSnapshot _io;
        private readonly NativeIo _processIo;
        private readonly double _cpuMs, _adoptMs, _jitMs;
        private readonly long _allocated, _jitMethods;
        private int _frames, _cameraFrames;
        private long _drawn, _queueMax;
        private double _gpuTotal, _gpuMax, _batchTotal, _sceneTotal;

        public PanDiagnostic(CatalogStampImageSource source)
        {
            _source = source; _io = source.DerivedIoDiagnostics; _adoptMs = source.Timings.AdoptMs;
            _allocated = GC.GetTotalAllocatedBytes(precise: false);
            _jitMs = JitInfo.GetCompilationTime().TotalMilliseconds;
            _jitMethods = JitInfo.GetCompiledMethodCount();
            using var process = Process.GetCurrentProcess();
            _cpuMs = process.TotalProcessorTime.TotalMilliseconds;
            _processIo = ReadProcessIo(process);
        }

        public void Frame(MapCanvasControl canvas)
        {
            _frames++; _cameraFrames += canvas.LastRenderStats.CameraFramesReused;
            _drawn += canvas.LastFrameObjectsDrawn;
            _batchTotal += canvas.LastRenderStats.StampBatchMilliseconds;
            _sceneTotal += canvas.LastSceneRenderMilliseconds;
            var gpu = MapCanvasControl.LastFrameGpuFlushMilliseconds;
            _gpuTotal += gpu; _gpuMax = Math.Max(_gpuMax, gpu);
            _queueMax = Math.Max(_queueMax, ThreadPool.PendingWorkItemCount);
        }

        public void Complete(ZoomRouteReport route, (long Stamp, long Revision)[] inputs)
        {
            var now = _source.DerivedIoDiagnostics;
            using var process = Process.GetCurrentProcess();
            var io = ReadProcessIo(process);
            var values = route.RuntimeDuringRoute;
            values["portableHeaderReads"] = now.PortableHeaders - _io.PortableHeaders;
            values["portablePixelReads"] = now.PortablePixels - _io.PortablePixels;
            values["localMipReads"] = now.LocalMips - _io.LocalMips;
            values["sourcePreparations"] = now.SourcePreparations - _io.SourcePreparations;
            values["sourceVerifications"] = now.SourceVerifications - _io.SourceVerifications;
            values["readsOnCanvasThread"] = now.ReadsOnCanvasThread - _io.ReadsOnCanvasThread;
            values["portableDecompressMs"] = now.PortableDecompressMs - _io.PortableDecompressMs;
            values["sourceDecodeMs"] = now.SourceDecodeMs - _io.SourceDecodeMs;
            values["beginFrameAdoptMs"] = _source.Timings.AdoptMs - _adoptMs;
            values["processCpuMs"] = process.TotalProcessorTime.TotalMilliseconds - _cpuMs;
            values["jitCompilationMs"] = JitInfo.GetCompilationTime().TotalMilliseconds - _jitMs;
            values["jitCompiledMethods"] = JitInfo.GetCompiledMethodCount() - _jitMethods;
            values["processReadOps"] = io.ReadOperationCount - _processIo.ReadOperationCount;
            values["processReadBytes"] = io.ReadTransferCount - _processIo.ReadTransferCount;
            values["allocatedBytes"] = GC.GetTotalAllocatedBytes(precise: false) - _allocated;
            values["diagnosticFrames"] = _frames;
            values["cameraFrames"] = _cameraFrames;
            values["objectsDrawnTotal"] = _drawn;
            values["sceneMsTotal"] = _sceneTotal;
            values["batchMsTotal"] = _batchTotal;
            values["gpuFlushMsTotal"] = _gpuTotal;
            values["gpuFlushMsMax"] = _gpuMax;
            values["threadPoolQueueMax"] = _queueMax;
            var gaps = inputs.Skip(1).Select((input, index) =>
                (input.Stamp - inputs[index].Stamp) * 1000d / Stopwatch.Frequency).Order().ToArray();
            values["inputGapP95Ms"] = Percentile(gaps, .95);
            values["inputGapMaxMs"] = gaps.Length == 0 ? 0 : gaps[^1];
            values["inputBurstsUnder1Ms"] = gaps.Count(gap => gap < 1);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeIo
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount,
                ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        private static NativeIo ReadProcessIo(Process process)
        {
            var counters = default(NativeIo);
            if (OperatingSystem.IsWindows() && !GetProcessIoCounters(process.Handle, out counters))
                throw new InvalidOperationException("GetProcessIoCounters failed.");
            return counters;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessIoCounters(IntPtr process, out NativeIo counters);
    }
}
