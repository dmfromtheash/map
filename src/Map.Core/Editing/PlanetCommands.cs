using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>The planet flag replaced as one undo step (R39).</summary>
public sealed class SetPlanetCommand(MapPlanet before, MapPlanet after) : IEditCommand
{
    public string Name => after.Enabled ? "Карта — планета" : "Карта не планета";

    public MapDocument Apply(MapDocument document)
    {
        if (after.Enabled && !MapPlanet.Fits(document.Canvas)) throw new InvalidOperationException("Планета — это карта 2:1.");
        return document with { Planet = after };
    }

    public MapDocument Revert(MapDocument document) => document with { Planet = before };
}
