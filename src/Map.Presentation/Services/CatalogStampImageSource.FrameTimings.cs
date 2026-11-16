using System.Diagnostics;

namespace Map.Presentation.Services;

public sealed partial class CatalogStampImageSource
{
    /// <summary>Opt-in, frame-thread-only work. Read after disposing BeginFrame; no worker gate is acquired.</summary>
    public readonly record struct FrameTimingSnapshot(double GateWaitMs, double LookupMs, double MipAdoptMs,
        long MipAdoptBytes, int DecodeCalls, double PinMs);

    private long _frameGateTicks, _frameLookupTicks, _frameAdoptTicks, _frameAdoptBytes;
    private int _frameDecodeCalls;
    private long _framePinTicks;

    public FrameTimingSnapshot FrameTimings
    {
        get
        {
            var ms = 1000d / Stopwatch.Frequency;
            return new(_frameGateTicks * ms, _frameLookupTicks * ms, _frameAdoptTicks * ms,
                _frameAdoptBytes, _frameDecodeCalls, _framePinTicks * ms);
        }
    }

    private void ResetFrameTimings()
    {
        _frameGateTicks = _frameLookupTicks = _frameAdoptTicks = _frameAdoptBytes = 0;
        _frameDecodeCalls = 0;
        _framePinTicks = 0;
    }
}
