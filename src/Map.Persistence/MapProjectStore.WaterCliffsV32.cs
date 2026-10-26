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
        if (schema < MapProjectFormat.TransitionsSchemaVersion && scene.Transitions is { Count: > 0 })
            throw new MapProjectCorruptedException("Переходы требуют схему v39.");
        if (schema < MapProjectFormat.FogSchemaVersion && scene.Fog is not null)
            throw new MapProjectCorruptedException("Туман войны обычной карты требует схему v38.");
        if (schema < MapProjectFormat.RegionLabelsSchemaVersion && (scene.Labels is not null
            || scene.Layers?.Any(l => l?.Objects?.Any(o => o?.Text is { } t && (t.Region is not null || t.Importance is not null)) == true) == true))
            throw new MapProjectCorruptedException("Подписи областей и важность подписей требуют схему v37.");
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

    /// <summary>The transitions (R29): every one valid, ids distinct, at most 4096.</summary>
    private static System.Collections.Immutable.ImmutableArray<MapTransition> ReadTransitionsV39(SceneDto scene)
    {
        if (scene.Transitions is not { Count: > 0 } list) return [];
        if (list.Count > Map.Core.Editing.MapTransitions.MaxCount || list.Any(t => t is null) || list.Select(t => t.Id).Distinct().Count() != list.Count)
            throw new MapProjectCorruptedException("Повреждены переходы.");
        try { foreach (var transition in list) transition.Validate(); }
        catch (ArgumentException ex) { throw new MapProjectCorruptedException("Повреждён переход.", ex); }
        return [.. list];
    }

    private static FogDto? ToFogDto(MapFog fog) => fog.IsDefault ? null : new FogDto
    {
        Enabled = fog.Enabled,
        Tiles = [.. fog.Revealed.OrderBy(p => p.Key).Select(p => new FogTileDto
        {
            X = MapFog.Unkey(p.Key).X, Y = MapFog.Unkey(p.Key).Y,
            Rows = p.Value.Length == 0 ? "" : Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(p.Value.AsSpan())),
        })],
    };

    /// <summary>The fog of war (R27); none when absent. Every tile 0 or 64 rows, no tile twice.</summary>
    private static MapFog ReadFogV38(SceneDto scene)
    {
        if (scene.Fog is not { } dto) return new MapFog();
        if (dto.Tiles is null || dto.Tiles.Count > MapFog.MaxTiles) throw new MapProjectCorruptedException("Повреждена маска тумана войны.");
        var builder = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<long, System.Collections.Immutable.ImmutableArray<ulong>>();
        foreach (var tile in dto.Tiles)
        {
            if (tile is null || tile.X < 0 || tile.Y < 0 || builder.ContainsKey(MapFog.Key(tile.X, tile.Y)))
                throw new MapProjectCorruptedException("Повреждена маска тумана войны.");
            if (string.IsNullOrEmpty(tile.Rows)) { builder[MapFog.Key(tile.X, tile.Y)] = []; continue; }
            byte[] bytes;
            try { bytes = Convert.FromBase64String(tile.Rows); }
            catch (FormatException ex) { throw new MapProjectCorruptedException("Повреждена маска тумана войны.", ex); }
            if (bytes.Length != MapFog.TileCells * sizeof(ulong)) throw new MapProjectCorruptedException("Повреждена маска тумана войны.");
            builder[MapFog.Key(tile.X, tile.Y)] = [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(bytes)];
        }
        return new MapFog { Enabled = dto.Enabled, Revealed = builder.ToImmutable() };
    }

    /// <summary>The label settings of the map (R07b); defaults when absent.</summary>
    private static LabelSettings ReadLabelsV37(SceneDto scene)
    {
        if (scene.Labels is not { } labels) return new LabelSettings();
        try { labels.Validate(); }
        catch (ArgumentException ex) { throw new MapProjectCorruptedException("Повреждены настройки подписей.", ex); }
        return labels;
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
