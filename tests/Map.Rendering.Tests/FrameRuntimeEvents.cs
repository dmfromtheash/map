using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

namespace Map.Rendering.Tests;

/// <summary>Opt-in runtime events retain their original timestamps, including GC and contention start/stop.</summary>
internal sealed class FrameRuntimeEvents : EventListener
{
    private readonly ConcurrentQueue<string> _events = new();
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x4001);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is not { } name || !(name.Contains("GCStart") || name.Contains("GCEnd")
            || name.Contains("SuspendEE") || name.Contains("RestartEE") || name.Contains("Contention")
            || name.Contains("AllocationWait") || name.Contains("BGC") || name.Contains("GCAllocationTick"))) return;
        var payload = e.Payload is null ? "" : string.Join(',', e.Payload.Select((v, i) =>
            (e.PayloadNames?[i] ?? i.ToString()) + "=" + v));
        _events.Enqueue($"RUNTIME {e.TimeStamp:O} {name} thread={e.OSThreadId} {payload}");
    }

    public void Print()
    {
        foreach (var line in _events) Console.WriteLine(line);
    }
}
