using Map.Core.Editing;

namespace Map.Presentation.Services;

/// <summary>
/// Process-local object clipboard shared by independent native editor windows. Map keeps its own
/// buffer rather than using the Windows clipboard: a copied selection carries scene objects and
/// their asset references, which no system clipboard format describes, and nothing about the
/// user's map leaves the process.
/// </summary>
public static class ObjectClipboard
{
    private static readonly object Gate = new();
    private static ObjectClipboardPayload? _payload;
    private static int _pasteOrdinal;

    /// <summary>How many objects a paste would restore right now; zero when the buffer is empty.</summary>
    public static int Count
    {
        get { lock (Gate) return _payload?.Count ?? 0; }
    }

    public static void Set(ObjectClipboardPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        lock (Gate)
        {
            _payload = payload;
            _pasteOrdinal = 0;
        }
    }

    public static ObjectClipboardPayload? Get()
    {
        lock (Gate) return _payload;
    }

    /// <summary>
    /// 1 for the first paste of the current buffer, 2 for the next, and so on. Repeated pastes
    /// therefore cascade instead of stacking on one spot, and a fresh copy starts over.
    /// </summary>
    public static int TakePasteOrdinal()
    {
        lock (Gate) return ++_pasteOrdinal;
    }
}
