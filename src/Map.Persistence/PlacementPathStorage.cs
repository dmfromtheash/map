using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Container;

namespace Map.Persistence;

public static partial class MapProjectStore
{
    private static BaseColorLayer ToBaseMaterial(BaseColorLayer layer, BaseMaterialDto? material)
    {
        if (material is null) return layer;
        if (string.IsNullOrWhiteSpace(material.TextureId) || !MapFillTextures.IsValidId(material.TextureId)
            || !MapFillTextures.IsValidOpacity(material.Opacity) || !MapFillTextures.IsValidScale(material.Scale))
            throw new MapProjectCorruptedException("The material of the base colour is invalid.");
        return layer with { TextureId = material.TextureId, TextureOpacity = material.Opacity, TextureScale = material.Scale };
    }

    private static void ValidatePathSchema(SceneDto scene, int schema)
    {
        if (scene.Layers?.Any(l => l?.BaseMaterial is not null) == true
            && schema < MapProjectFormat.BaseMaterialSchemaVersion)
            throw new MapProjectCorruptedException("A material on the base colour requires schema v10.");
        if (scene.Layers?.Any(l => l?.BaseMaterial is not null && l.Type != "BaseColor") == true)
            throw new MapProjectCorruptedException("Only the base colour layer carries a page material.");
        if (scene.Layers?.Any(l => l?.LandPaintTiles is { Count: > 0 }) == true
            && schema < MapProjectFormat.LandPaintSchemaVersion)
            throw new MapProjectCorruptedException("Land material painting requires schema v9.");
        if (scene.Layers?.Any(l => l?.PlacementPaths is { Count: > 0 }) == true
            && schema < MapProjectFormat.EditablePathsSchemaVersion)
            throw new MapProjectCorruptedException("Editable paths require schema v5.");
        if (scene.Layers?.Any(l => l?.PlacementPaths is { Count: > 0 } && l.Type != "Object") == true)
            throw new MapProjectCorruptedException("Only object layers can own paths.");
        if (scene.Layers?.Any(l => l?.PlacementAreas is { Count: > 0 }) == true
            && schema < MapProjectFormat.EditableAreasSchemaVersion)
            throw new MapProjectCorruptedException("Editable areas require schema v6.");
        if (scene.Layers?.Any(l => l?.PlacementAreas is { Count: > 0 } && l.Type != "Object") == true)
            throw new MapProjectCorruptedException("Only object layers can own areas.");
        if (scene.Layers?.Any(l => l?.Rivers is { Count: > 0 }) == true
            && schema < MapProjectFormat.RiversSchemaVersion)
            throw new MapProjectCorruptedException("Rivers require schema v7.");
        if (scene.Layers?.Any(l => l?.Rivers is { Count: > 0 } && l.Type != "Object") == true)
            throw new MapProjectCorruptedException("Only object layers can own rivers.");
    }
}
