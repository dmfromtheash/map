using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>The isometric view's side and heights replaced as one undo step (plan 2.3).</summary>
public sealed class SetIsometricCommand(MapIsometric before, MapIsometric after) : IEditCommand
{
    public string Name => "Вид в изометрии";

    public MapDocument Apply(MapDocument document) => document with { Isometric = after };

    public MapDocument Revert(MapDocument document) => document with { Isometric = before };
}
