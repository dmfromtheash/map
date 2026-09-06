using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Container;

namespace Map.Persistence;

public static partial class MapProjectStore
{
    private static void ValidatePathSchema(SceneDto scene, int schema)
    {
        if (scene.Layers?.Any(l => l?.PlacementPaths is { Count: > 0 }) == true
            && schema != MapProjectFormat.EditablePathsSchemaVersion)
            throw new MapProjectCorruptedException("Editable paths require schema v5.");
        if (scene.Layers?.Any(l => l?.PlacementPaths is { Count: > 0 } && l.Type != "Object") == true)
            throw new MapProjectCorruptedException("Only object layers can own paths.");
    }
}
