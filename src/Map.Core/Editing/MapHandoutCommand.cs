using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>The handout of the map changed (R09f) as one Undo step.</summary>
public sealed class SetMapHandoutCommand(MapHandout before, MapHandout after) : IEditCommand
{
    public string Name => "Хендаут";

    public long? MeasuredWorkingBytes => 64;

    public MapDocument Apply(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with { Handout = after };
    }

    public MapDocument Revert(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with { Handout = before };
    }
}
