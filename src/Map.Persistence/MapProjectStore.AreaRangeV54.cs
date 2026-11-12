using Map.Core.Documents;
using Map.Persistence.Container;

namespace Map.Persistence;

/// <summary>
/// v53 → v54 (G1, 2026-11-12): an area of layout 5 (mountains in size classes along ranges) or with the «Горный массив»
/// pattern. Both are written with the area record; every older map has neither and keeps its schema number.
/// </summary>
public static partial class MapProjectStore
{
    private static bool HasAreaRangeV54(IEnumerable<MapLayer> layers) =>
        layers.OfType<ObjectLayer>().Any(l => l.PlacementAreas.Any(a => a.NeedsRangeSchema));

    private static void ValidateAreaRangeSchemaV54(SceneDto scene, int schema)
    {
        if (schema >= MapProjectFormat.AreaRangeSchemaVersion) return;
        if (scene.Layers?.Any(l => l?.PlacementAreas?.Any(a => a is not null && a.NeedsRangeSchema) == true) == true)
            throw new MapProjectCorruptedException("Раскладка областей версии 5 и «Горный массив» требуют схему v54.");
    }
}
