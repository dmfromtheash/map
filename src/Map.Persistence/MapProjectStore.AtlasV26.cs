using Map.Core.Documents;
using Map.Persistence.Container;

namespace Map.Persistence;

public static partial class MapProjectStore
{
    private static AtlasLocator? ReadAtlasV26(ManifestDto manifest)
    {
        if (manifest.Atlas is not { } atlas) return null;
        if (manifest.SchemaVersion < MapProjectFormat.AtlasSchemaVersion || atlas.Id == Guid.Empty
            || string.IsNullOrWhiteSpace(atlas.File) || Path.IsPathRooted(atlas.File)
            || !atlas.File.EndsWith(AtlasStore.Extension, StringComparison.OrdinalIgnoreCase))
            throw new MapProjectCorruptedException("Неверная ссылка на атлас (схема v26).");
        return atlas;
    }
}
