using Map.Core.Editing;

namespace Map.Presentation.Services;

/// <summary>Process-local clipboard shared by independent native editor windows.</summary>
public static class CompositionClipboard
{
    private static readonly object Gate = new();
    private static CompositionPackage? _package;

    public static void Set(CompositionPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        lock (Gate) _package = package;
    }

    public static CompositionPackage? Get()
    {
        lock (Gate) return _package;
    }
}
