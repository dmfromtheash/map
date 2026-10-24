using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// One change of the map's style (a preset applied, a switch, a slider released) as one Undo
/// step. Apply sets the new style, Revert brings back the old one exactly.
/// </summary>
public sealed class SetMapStyleCommand : IEditCommand
{
    private readonly MapStyle? _before;
    private readonly MapStyle? _after;

    public SetMapStyleCommand(string name, MapStyle? before, MapStyle? after)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Стиль" : name;
        _before = before;
        _after = after?.Normalized();
    }

    public string Name { get; }

    public long? MeasuredWorkingBytes => 1024;

    public MapDocument Apply(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with { Style = _after };
    }

    public MapDocument Revert(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with { Style = _before };
    }
}
