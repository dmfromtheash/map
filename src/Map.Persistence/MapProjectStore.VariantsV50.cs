using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Container;

namespace Map.Persistence;

/// <summary>The named variants of a map in its scene (R09e, schema v50).</summary>
public static partial class MapProjectStore
{
    /// <summary>The variants as saved: the chosen one with the look the map has now.</summary>
    private static MapVariantsDto? ToVariantsDto(MapDocument document)
    {
        if (document.Variants.IsDefault) return null;
        var variants = MapVariantEditing.Current(document);
        return new MapVariantsDto
        {
            Active = variants.Active,
            Items = [.. variants.Items.Select(v => new MapVariantDto
            {
                Id = v.Id, Name = v.Name,
                Hidden = [.. (v.Hidden.IsDefault ? [] : v.Hidden).Select(id => id.ToString())],
                Style = ToStyleDto(v.Style), Lighting = ToLightingDto(v.Lighting), Political = v.PoliticalMode,
            })],
        };
    }

    private static MapVariants ReadVariantsV50(SceneDto scene, int schema)
    {
        if (scene.Variants is not { } dto) return new MapVariants();
        if (schema < MapProjectFormat.VariantsSchemaVersion)
            throw new MapProjectCorruptedException("Варианты карты требуют схему v50.");
        if (dto.Items is not { } items || items.Count > MapVariants.MaxCount)
            throw new MapProjectCorruptedException($"Вариантов карты в файле больше {MapVariants.MaxCount} или нет списка.");
        try
        {
            var variants = new MapVariants
            {
                Active = dto.Active,
                Items = [.. items.Select(v => new MapVariant(v.Id, v.Name ?? "")
                {
                    Hidden = [.. (v.Hidden ?? []).Select(LayerId.Parse)],
                    // The style and the light of a variant follow the rules of the map's own (v29, v28 and later).
                    Style = ReadStyleV29(new ManifestDto { SchemaVersion = schema, Style = v.Style }),
                    Lighting = ReadLightingV28(new ManifestDto { SchemaVersion = schema, Lighting = v.Lighting }) is { } light
                        ? light with { Sources = [] } : null,
                    PoliticalMode = v.Political,
                })],
            };
            variants.Validate();
            return variants;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new MapProjectCorruptedException("Варианты карты в файле повреждены: " + ex.Message, ex);
        }
    }
}
