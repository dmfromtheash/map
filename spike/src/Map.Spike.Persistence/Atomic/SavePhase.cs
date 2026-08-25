namespace Map.Spike.Persistence.Atomic;

/// <summary>
/// Interruption points of the atomic save sequence. These are the fault-injection sites required
/// by docs/08 "Fault injection".
/// </summary>
public enum SavePhase
{
    /// <summary>Nothing on disk has changed yet.</summary>
    BeforeTempCreate,

    /// <summary>The temp file exists and is partially written.</summary>
    MidTempWrite,

    /// <summary>The temp file is fully written and flushed to the device, target untouched.</summary>
    AfterFlushBeforeValidate,

    /// <summary>The temp file was read back and verified, target still untouched.</summary>
    AfterValidateBeforeReplace,

    /// <summary>Immediately before the atomic replace call.</summary>
    DuringReplace,

    /// <summary>The target now holds the new content; temp/journal cleanup has not run.</summary>
    AfterReplaceBeforeCleanup,
}

/// <summary>Test seam. Production code never installs an injector.</summary>
public interface ISaveFaultInjector
{
    /// <summary>Called at every phase. Throw to simulate a failure at that exact point.</summary>
    void OnPhase(SavePhase phase);
}

/// <summary>Throws once, at one chosen phase.</summary>
public sealed class SingleFaultInjector(SavePhase failAt, Func<Exception>? factory = null) : ISaveFaultInjector
{
    private readonly SavePhase _failAt = failAt;
    private readonly Func<Exception> _factory = factory ?? (() => new IOException($"Injected save fault at {failAt}."));
    private bool _fired;

    public SavePhase FailAt => _failAt;

    public bool Fired => _fired;

    public void OnPhase(SavePhase phase)
    {
        if (_fired || phase != _failAt)
        {
            return;
        }

        _fired = true;
        throw _factory();
    }
}
