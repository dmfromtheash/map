using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>The cards of the world of a map replaced as one undo step (R26).</summary>
public sealed class SetEntitiesCommand(ImmutableArray<MapEntity> before, ImmutableArray<MapEntity> after, string name) : IEditCommand
{
    public string Name { get; } = name;
    public long ApproximateMemoryBytes => after.Sum(e => (long)(e.Markdown.Length + e.Name.Length) * 2 + (e.Bindings.Length * 64) + 256);
    public MapDocument Apply(MapDocument document)
    {
        foreach (var entity in after) entity.Validate();
        if (after.Select(e => e.Id).Distinct().Count() != after.Length) throw new InvalidOperationException("Карточки должны иметь разные идентификаторы.");
        return document with { Entities = after };
    }
    public MapDocument Revert(MapDocument document) => document with { Entities = before };
}
