using Map.Core.Documents;
using Map.Persistence.Container;

namespace Map.Persistence;

/// <summary>Light and time of day in the manifest (schema v28, DEC-057).</summary>
public static partial class MapProjectStore
{
    private static LightingDto? ToLightingDto(MapLighting? lighting)
    {
        if (lighting is null) return null;
        static LightingEffectDto Effect(LightingEffect e) => new() { Enabled = e.Enabled, Strength = e.Strength };
        return new LightingDto
        {
            Enabled = lighting.Enabled,
            TimeOfDay = lighting.TimeOfDay,
            Relief = Effect(lighting.Relief), Shadows = Effect(lighting.Shadows), Fog = Effect(lighting.Fog),
            Lights = Effect(lighting.Lights), Color = Effect(lighting.Color),
            Sources = [.. lighting.Sources.Select(s => new LightSourceDto
            {
                Id = s.Id, X = s.X, Y = s.Y, Color = s.Color, Radius = s.Radius, Intensity = s.Intensity, Flicker = s.Flicker,
                Dim = s.DimZone > 0 ? s.DimZone : null,
            })],
        };
    }

    private static bool HasLightZonesV33(MapLighting? lighting) => lighting?.Sources.Any(s => s.DimZone > 0) == true;

    private static MapLighting? ReadLightingV28(ManifestDto manifest)
    {
        if (manifest.Lighting is not { } dto) return null;
        if (manifest.SchemaVersion < MapProjectFormat.LightingSchemaVersion)
            throw new MapProjectCorruptedException("Свет карты записан в файле старой схемы (нужна v28).");
        if (dto.Sources is { Count: > MapLighting.MaxSources })
            throw new MapProjectCorruptedException("Слишком много источников света в файле карты.");
        if (manifest.SchemaVersion < MapProjectFormat.LightZonesSchemaVersion && dto.Sources?.Any(s => s?.Dim is not null) == true)
            throw new MapProjectCorruptedException("Тусклая зона огня требует схему v33.");
        var defaults = new MapLighting();
        static LightingEffect Effect(LightingEffectDto? e, LightingEffect fallback) =>
            e is null ? fallback : new LightingEffect(e.Enabled, e.Strength);
        return new MapLighting
        {
            Enabled = dto.Enabled,
            TimeOfDay = dto.TimeOfDay,
            Relief = Effect(dto.Relief, defaults.Relief), Shadows = Effect(dto.Shadows, defaults.Shadows),
            Fog = Effect(dto.Fog, defaults.Fog), Lights = Effect(dto.Lights, defaults.Lights), Color = Effect(dto.Color, defaults.Color),
            Sources = [.. (dto.Sources ?? []).Select(s => new MapLightSource(s.Id, s.X, s.Y, s.Color, s.Radius, s.Intensity, s.Flicker) { DimZone = s.Dim ?? 0 })],
        }.Normalized();
    }
}
