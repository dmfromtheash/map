using Map.Core.Documents;
using Map.Persistence.Container;

namespace Map.Persistence;

/// <summary>R12 «Вода и обрывы» in the scene (schema v32): cliff light and mask, river node features.</summary>
public static partial class MapProjectStore
{
    private static bool HasWaterCliffsV32(IEnumerable<MapLayer> layers) =>
        layers.Any(layer => layer switch
        {
            LandLayer land => land.HasCliffMask || land.EdgeStyle.Cliff.LightAngle != LandCliffStyle.DefaultLightAngle,
            ObjectLayer objects => objects.Rivers.Any(r => r.HasNodeFeatures),
            _ => false,
        });

    private static bool HasWaterCliffsV32(MapDocument document) =>
        HasWaterCliffsV32(document.Layers) || document.Checkpoints.Any(c => HasWaterCliffsV32(c.Snapshot.Layers));

    /// <summary>A file of an older schema may not carry any R12 construct.</summary>
    private static void ValidateWaterCliffsSchemaV32(SceneDto scene, int schema)
    {
        if (schema >= MapProjectFormat.WaterCliffsSchemaVersion || scene.Layers is null) return;
        foreach (var layer in scene.Layers)
        {
            if (layer is null) continue;
            if (layer.CliffWallTiles is { Count: > 0 } || layer.CliffBeachTiles is { Count: > 0 })
                throw new MapProjectCorruptedException("Маска обрыва требует схему v32.");
            if (layer.LandStyle?.Cliff?.LightAngle is not null)
                throw new MapProjectCorruptedException("Угол света обрывов требует схему v32.");
            if (layer.Rivers?.Any(r => r is not null && r.HasNodeFeatures) == true)
                throw new MapProjectCorruptedException("Водопады, пороги и броды рек требуют схему v32.");
        }
    }
}
