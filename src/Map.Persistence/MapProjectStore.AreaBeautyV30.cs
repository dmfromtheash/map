using System.Globalization;
using Map.Core.Documents;
using Map.Persistence.Container;

namespace Map.Persistence;

/// <summary>
/// v29 → v30 (R10 «Красота», written in its branch as v27 and renumbered on merge): an area's <c>Beauty</c> and <c>DensityMask</c> (written
/// with the area record), <c>depthSort</c> of an object layer's effects, and <c>tint</c> /
/// <c>groundShadow</c> of a placed asset. All are optional and absent on every older map, which
/// therefore opens unchanged; a map that uses none of them keeps its older schema number.
/// </summary>
public static partial class MapProjectStore
{
    private static bool HasAreaBeautyV30(IEnumerable<MapLayer> layers) =>
        layers.OfType<ObjectLayer>().Any(l => l.Effects.DepthSort
            || l.PlacementAreas.Any(a => a.NeedsBeautySchema)
            || l.Objects.Any(o => o.Stamp is { Tint: not null } or { GroundShadow: not null }));

    private static void ValidateAreaBeautySchemaV30(SceneDto scene, int schema)
    {
        if (schema >= MapProjectFormat.AreaBeautySchemaVersion) return;
        if (scene.Layers?.Any(l => l?.Effects?.DepthSort == true
                || l?.PlacementAreas?.Any(a => a is not null && a.NeedsBeautySchema) == true) == true)
            throw new MapProjectCorruptedException("«Красота» областей и порядок «ближние поверх дальних» требуют схему v30.");
    }

    private static GroundShadowDto? ToGroundShadowDtoV30(StampGroundShadow? shadow) => shadow is null ? null : new()
    {
        Opacity = shadow.Opacity, FootX = shadow.FootX, FootY = shadow.FootY, Width = shadow.Width, Mass = shadow.Mass,
    };

    private static StampGroundShadow? ReadGroundShadowV30(GroundShadowDto? dto)
    {
        if (dto is null) return null;
        var shadow = new StampGroundShadow(dto.Opacity, dto.FootX, dto.FootY, dto.Width, dto.Mass);
        return shadow.IsValid ? shadow : throw new MapProjectCorruptedException("Повреждена тень под ассетом (v30).");
    }

    private static string? ReadTintV30(string? tint)
    {
        if (tint is null) return null;
        if (tint.Length == 9 && tint[0] == '#'
            && uint.TryParse(tint.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) return tint;
        throw new MapProjectCorruptedException("Повреждён оттенок ассета (v30).");
    }
}
