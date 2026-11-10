using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// A style pack applied to the map (R09d, DEC-094): the layers and the style of the map before and after, one undo step.
/// Only what a pack touches is swapped back, so the history, versions, notes and atlas stay as they are.
/// </summary>
public sealed class ApplyStylePackCommand(string title, MapDocument before, MapDocument after) : IEditCommand
{
    public string Name { get; } = "Пакет стиля «" + title + "»";

    public MapDocument Apply(MapDocument document) => document.WithLayers(after.Layers) with { Style = after.Style };

    public MapDocument Revert(MapDocument document) => document.WithLayers(before.Layers) with { Style = before.Style };
}
