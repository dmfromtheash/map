namespace Map.Presentation.ViewModels;

/// <summary>User-configurable command bindings; presentation state, never part of a map file.</summary>
public sealed class EditorShortcutSettings
{
    private readonly Dictionary<string, string> _bindings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["select"] = "V", ["text"] = "T", ["grid-snap"] = "G",
        ["undo"] = "Ctrl+Z", ["redo"] = "Ctrl+Y", ["save"] = "Ctrl+S",
    };

    public IReadOnlyDictionary<string, string> Bindings => _bindings;

    public void Set(string command, string gesture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(gesture);
        _bindings[command.Trim()] = gesture.Trim();
    }

    public string Get(string command) => _bindings.TryGetValue(command, out var value) ? value : "";
}
