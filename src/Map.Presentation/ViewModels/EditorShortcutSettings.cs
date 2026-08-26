namespace Map.Presentation.ViewModels;

/// <summary>User-configurable command bindings; presentation state, never part of a map file.</summary>
public sealed class EditorShortcutSettings
{
    private readonly Dictionary<string, string> _bindings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["select"] = "V",
        ["mask"] = "M",
        ["brush"] = "B",
        ["stamp"] = "O",
        ["shape"] = "R",
        ["line"] = "L",
        ["text"] = "T",
        ["notes"] = "N",
        ["shortcut-help"] = "K",
        ["side-panel"] = "/",
        ["previous-tool"] = "Tab",
        ["grid-snap"] = "G",
        ["undo"] = "Ctrl+Z",
        ["redo"] = "Ctrl+Shift+Z",
        ["save"] = "Ctrl+S",
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
