using Map.Core.Documents;
using Map.Persistence.Container;

namespace Map.Persistence;

/// <summary>The style of the map in the manifest (schema v29, DEC-059).</summary>
public static partial class MapProjectStore
{
    private static readonly IReadOnlyDictionary<MapStylePreset, string> StylePresetNames = new Dictionary<MapStylePreset, string>
    {
        [MapStylePreset.InkWetBrush] = "ink-wet-brush",
        [MapStylePreset.InkPenHatching] = "ink-pen-hatching",
        [MapStylePreset.InkPenStipple] = "ink-pen-stipple",
    };

    private static MapStyleDto? ToStyleDto(MapStyle? style)
    {
        if (style is null) return null;
        static LightingEffectDto Effect(StyleEffect e) => new() { Enabled = e.Enabled, Strength = e.Strength };
        return new MapStyleDto
        {
            Enabled = style.Enabled,
            Preset = StylePresetNames[style.Preset],
            Strength = style.Strength,
            Strokes = Effect(style.Strokes), Bleed = Effect(style.Bleed), Edges = Effect(style.Edges),
            DryBrush = Effect(style.DryBrush), Mist = Effect(style.Mist), Pen = Effect(style.Pen),
            Paper = Effect(style.Paper), Contour = Effect(style.Contour),
        };
    }

    private static MapStyle? ReadStyleV29(ManifestDto manifest)
    {
        if (manifest.Style is not { } dto) return null;
        if (manifest.SchemaVersion < MapProjectFormat.StyleSchemaVersion)
            throw new MapProjectCorruptedException("Стиль карты записан в файле старой схемы (нужна v29).");
        var preset = StylePresetNames.FirstOrDefault(p => p.Value == dto.Preset);
        if (preset.Value is null)
            throw new MapProjectCorruptedException($"Неизвестный стиль карты «{dto.Preset}».");
        // An effect missing from the file takes the preset's own value.
        var defaults = MapStyle.For(preset.Key);
        static StyleEffect Effect(LightingEffectDto? e, StyleEffect fallback) =>
            e is null ? fallback : new StyleEffect(e.Enabled, e.Strength);
        return new MapStyle
        {
            Enabled = dto.Enabled,
            Preset = preset.Key,
            Strength = dto.Strength,
            Strokes = Effect(dto.Strokes, defaults.Strokes), Bleed = Effect(dto.Bleed, defaults.Bleed),
            Edges = Effect(dto.Edges, defaults.Edges), DryBrush = Effect(dto.DryBrush, defaults.DryBrush),
            Mist = Effect(dto.Mist, defaults.Mist), Pen = Effect(dto.Pen, defaults.Pen),
            Paper = Effect(dto.Paper, defaults.Paper), Contour = Effect(dto.Contour, defaults.Contour),
        }.Normalized();
    }
}
