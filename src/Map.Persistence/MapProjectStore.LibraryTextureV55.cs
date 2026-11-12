using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Container;

namespace Map.Persistence;

/// <summary>
/// v54 → v55 (2026-11-12): a fill texture from the asset library, <c>asset:&lt;sha256&gt;</c>, in the existing texture
/// fields (the sea material, the land fill, a river or a road). The id is stored where a built-in id always was and
/// the bytes are an ordinary embedded asset, so the container does not change; the number only tells an older Map
/// that it cannot read the map instead of calling the river invalid. Every older map has no such id and keeps its
/// schema number.
/// </summary>
public static partial class MapProjectStore
{
    private static bool HasLibraryTextureV55(IEnumerable<MapLayer> layers) => FillTextureAssets.UsesLibraryTextures(layers);

    private static void ValidateLibraryTextureSchemaV55(SceneDto scene, int schema)
    {
        if (schema >= MapProjectFormat.LibraryTextureSchemaVersion || scene.Layers is null) return;
        foreach (var layer in scene.Layers)
        {
            if (layer is null) continue;
            if (MapFillTextures.IsAssetId(layer.BaseMaterial?.TextureId) || MapFillTextures.IsAssetId(layer.LandStyle?.FillTextureId)
                || layer.Rivers?.Any(r => r is not null && MapFillTextures.IsAssetId(r.Style?.TextureId)) == true
                || layer.Roads?.Any(r => r is not null && (MapFillTextures.IsAssetId(r.Style?.TextureId)
                    || !r.Branches.IsDefault && r.Branches.Any(b => MapFillTextures.IsAssetId(b?.Style?.TextureId)))) == true)
                throw new MapProjectCorruptedException("Текстура из библиотеки требует схему v55.");
        }
    }
}
