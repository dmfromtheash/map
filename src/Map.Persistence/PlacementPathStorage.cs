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
        if (schema < MapProjectFormat.PaintedRiversSchemaVersion
            && scene.Layers?.Any(l => l?.Rivers?.Any(r => r is not null && r.Style.PaintStrokes > 0) == true) == true)
            throw new MapProjectCorruptedException("Живописные мазки реки требуют схему v22.");
        if (scene.Layers?.Any(l => l?.Rivers?.Any(r => r is not null && (!double.IsFinite(r.Style.PaintStrokes) || r.Style.PaintStrokes is < 0 or > 1)) == true) == true)
            throw new MapProjectCorruptedException("Сила мазков реки — от 0 до 1.");
        if (schema < MapProjectFormat.GameplaySchemaVersion && scene.Layers?.Any(l => l?.Hex?.Gameplay is not null) == true)
            throw new MapProjectCorruptedException("Игровые данные гексов требуют схему v20.");
        if (schema < MapProjectFormat.AreaRulesSchemaVersion
            && scene.Layers?.Any(l => l?.PlacementAreas?.Any(a => a is not null && a.NeedsRulesSchema) == true) == true)
            throw new MapProjectCorruptedException("Правила областей расстановки требуют схему v19.");
        if (schema < MapProjectFormat.HexStructureSchemaVersion
            && scene.Layers?.Any(l => l?.Hex?.Points is not null || l?.Hex?.Regions?.Any(r => r.ParentRegion != 0) == true) == true)
            throw new MapProjectCorruptedException("Список точек и вложенность регионов требуют схему v18.");
        if (schema < MapProjectFormat.HexPathBlockingSchemaVersion
            && scene.Layers?.Any(l => l?.Hex?.Paths?.Any(p => p is not null
                && (p.BlocksSight.HasValue || p.BlocksMove.HasValue || p.BlocksLight.HasValue)) == true) == true)
            throw new MapProjectCorruptedException("Hex path blocking properties require schema v14.");
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
