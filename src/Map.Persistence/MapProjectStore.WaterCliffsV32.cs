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
            BaseColorLayer page => page.Sea is not null,
            ObjectLayer objects => objects.Rivers.Any(r => r.HasNodeFeatures),
            _ => false,
        });

    private static bool HasWaterCliffsV32(MapDocument document) =>
        HasWaterCliffsV32(document.Layers) || document.Checkpoints.Any(c => HasWaterCliffsV32(c.Snapshot.Layers));

    /// <summary>A file of an older schema may not carry any R12 construct.</summary>
    private static void ValidateWaterCliffsSchemaV32(SceneDto scene, int schema)
    {
        if (scene.Layers?.Any(l => l?.Sea is not null && l.Type != "BaseColor") == true)
            throw new MapProjectCorruptedException("Стиль моря бывает только у слоя фона.");
        if (schema < MapProjectFormat.WikiSchemaVersion && (scene.Entities is { Count: > 0 }
            || scene.Notes?.Any(n => n is not null && (n.LinkedMap is not null || n.Category is not null || n.PlayerVisible is not null)) == true))
            throw new MapProjectCorruptedException("Карточки сущностей и свойства заметок требуют схему v36.");
        if (schema < MapProjectFormat.IntentionalSchemaVersion && scene.Layers?.Any(l => l is not null && (
            l.Rivers?.Any(r => r is not null && r.GeographyIntentional) == true || l.Roads?.Any(r => r is not null && r.GeographyIntentional) == true
            || l.Objects?.Any(o => o?.GeographyIntentional == true) == true)) == true)
            throw new MapProjectCorruptedException("Отметка «намеренно» требует схему v35.");
        if (schema < MapProjectFormat.ReliefSchemaVersion && scene.Layers?.Any(l => l?.ReliefTiles is { Count: > 0 }) == true)
            throw new MapProjectCorruptedException("Рельеф суши требует схему v34.");
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
            if (layer.Sea is not null)
                throw new MapProjectCorruptedException("Стиль моря требует схему v32.");
        }
    }

    /// <summary>The cards of the world (R26): every card valid, ids distinct, at most 4096.</summary>
    private static System.Collections.Immutable.ImmutableArray<MapEntity> ReadEntitiesV36(SceneDto scene)
    {
        if (scene.Entities is not { Count: > 0 } list) return [];
        if (list.Count > 4096 || list.Any(e => e is null) || list.Select(e => e.Id).Distinct().Count() != list.Count)
            throw new MapProjectCorruptedException("Повреждены карточки сущностей.");
        try { foreach (var entity in list) entity.Validate(); }
        catch (ArgumentException ex) { throw new MapProjectCorruptedException("Повреждена карточка сущности.", ex); }
        return [.. list];
    }

    private static readonly IReadOnlyDictionary<SeaManner, string> SeaMannerNames = new Dictionary<SeaManner, string>
    {
        [SeaManner.PenLines] = "pen-lines",
        [SeaManner.Isobaths] = "isobaths",
        [SeaManner.Engraved] = "engraved",
        [SeaManner.DepthBands] = "depth-bands",
        [SeaManner.BandsAndLines] = "bands-and-lines",
    };

    private static SeaDto ToSeaDto(SeaStyle sea) => new()
    {
        Manner = SeaMannerNames[sea.Manner], Strength = sea.Strength, Scale = sea.Scale,
        Ink = sea.Ink, Shallow = sea.Shallow, Middle = sea.Middle, Deep = sea.Deep,
    };

    /// <summary>
    /// The sea style of a page. An unknown manner fails the map (it is not a look this Map can
    /// draw); a missing or unreadable number or colour takes the manner's own value.
    /// </summary>
    private static BaseColorLayer ReadSeaV32(BaseColorLayer page, SeaDto? dto)
    {
        if (dto is null) return page;
        var manner = SeaMannerNames.FirstOrDefault(p => p.Value == dto.Manner);
        if (manner.Value is null) throw new MapProjectCorruptedException($"Неизвестный стиль моря «{dto.Manner}».");
        var defaults = SeaStyle.For(manner.Key);
        return page with
        {
            Sea = new SeaStyle
            {
                Manner = manner.Key,
                Strength = dto.Strength ?? defaults.Strength, Scale = dto.Scale ?? defaults.Scale,
                Ink = dto.Ink ?? defaults.Ink, Shallow = dto.Shallow ?? defaults.Shallow,
                Middle = dto.Middle ?? defaults.Middle, Deep = dto.Deep ?? defaults.Deep,
            }.Bounded(),
        };
    }
}
