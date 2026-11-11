using Map.Core.Documents;
using Map.Persistence.Container;

namespace Map.Persistence;

/// <summary>The handout of a map in its scene (R09f, schema v51).</summary>
public static partial class MapProjectStore
{
    private static MapHandout ReadHandoutV51(SceneDto scene, int schema)
    {
        if (scene.Handout is not { } id) return MapHandout.None;
        if (schema < MapProjectFormat.HandoutSchemaVersion)
            throw new MapProjectCorruptedException("Хендаут карты требует схему v51.");
        if (!MapHandouts.TryParse(id, out var handout) || handout == MapHandout.None)
            throw new MapProjectCorruptedException($"Неизвестный хендаут карты «{id}».");
        return handout;
    }
}
