using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// One change of the map's light (a switch, a slider released, a light placed or removed) as one
/// Undo step. Apply sets the new lighting, Revert brings back the old one exactly.
/// </summary>
public sealed class SetLightingCommand : IEditCommand
{
    private readonly MapLighting? _before;
    private readonly MapLighting? _after;

    public SetLightingCommand(string name, MapLighting? before, MapLighting? after)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Свет" : name;
        _before = before;
        _after = after?.Normalized();
    }

    public string Name { get; }

    public long? MeasuredWorkingBytes => 4096;

    public MapDocument Apply(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with { Lighting = _after };
    }

    public MapDocument Revert(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with { Lighting = _before };
    }
}
