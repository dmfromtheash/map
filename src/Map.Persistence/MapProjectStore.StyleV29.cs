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
        [MapStylePreset.OldMapBlaeu] = "old-map-blaeu",
        [MapStylePreset.EngravingAquatint] = "engraving-aquatint",
        [MapStylePreset.WatercolorBigShapes] = "watercolor-big-shapes",
        [MapStylePreset.UkiyoeSosakuHanga] = "ukiyoe-sosaku-hanga",
        [MapStylePreset.PrintLetterpress] = "print-letterpress",
        [MapStylePreset.PrintTintedLithograph] = "print-tinted-lithograph",
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
            Sea = Effect(style.Sea), Coast = Effect(style.Coast), ColourWash = Effect(style.ColourWash),
            Aquatint = Effect(style.Aquatint), Plate = Effect(style.Plate), Border = Effect(style.Border),
            Granulation = Effect(style.Granulation), Crest = Effect(style.Crest), Gouge = Effect(style.Gouge),
            SeaCuts = Effect(style.SeaCuts), WoodGrain = Effect(style.WoodGrain), InkSqueeze = Effect(style.InkSqueeze),
            Starved = Effect(style.Starved), WaterLines = Effect(style.WaterLines), StoneCrayon = Effect(style.StoneCrayon),
            TintPlate = Effect(style.TintPlate), Scraped = Effect(style.Scraped),
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
            Sea = Effect(dto.Sea, defaults.Sea), Coast = Effect(dto.Coast, defaults.Coast),
            ColourWash = Effect(dto.ColourWash, defaults.ColourWash), Aquatint = Effect(dto.Aquatint, defaults.Aquatint),
            Plate = Effect(dto.Plate, defaults.Plate), Border = Effect(dto.Border, defaults.Border),
            Granulation = Effect(dto.Granulation, defaults.Granulation), Crest = Effect(dto.Crest, defaults.Crest),
            Gouge = Effect(dto.Gouge, defaults.Gouge), SeaCuts = Effect(dto.SeaCuts, defaults.SeaCuts),
            WoodGrain = Effect(dto.WoodGrain, defaults.WoodGrain), InkSqueeze = Effect(dto.InkSqueeze, defaults.InkSqueeze),
            Starved = Effect(dto.Starved, defaults.Starved), WaterLines = Effect(dto.WaterLines, defaults.WaterLines),
            StoneCrayon = Effect(dto.StoneCrayon, defaults.StoneCrayon), TintPlate = Effect(dto.TintPlate, defaults.TintPlate),
            Scraped = Effect(dto.Scraped, defaults.Scraped),
        }.Normalized();
    }
}
