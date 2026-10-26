using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Container;

namespace Map.Persistence;

public static partial class MapProjectStore
{
    private static bool HasGenerationV31(IEnumerable<MapLayer> layers) => layers.OfType<ObjectLayer>().Any(l =>
        l.PlacementAreas.Any(a => a.GenerationProtection is not null || a.GenerationRecipe is not null)
        || l.Objects.Any(o => o.GenerationReason is not null)) || layers.OfType<HexLayer>().Any(l =>
        l.GenerationRecipe is not null || !l.GenerationProtection.IsEmpty || l.Paths.Any(p => p.GenerationReason is not null)
        || l.Board.Cells.Any(c => c.Value.GenerationReason is not null));

    private static void ValidateGenerationSchemaV31(SceneDto scene, int schema)
    {
        if (schema < MapProjectFormat.GenerationSchemaVersion && scene.Layers?.Any(l =>
            l.Hex?.Generation is not null || l.Hex?.Paths?.Any(p => p.GenerationReason is not null) == true
            || l.PlacementAreas?.Any(a => a.GenerationProtection is not null || a.GenerationRecipe is not null) == true) == true)
            throw new MapProjectCorruptedException("Рецепт, защита и причины генерации требуют v31.");
    }

    private static HexGenerationDto? WriteGenerationV31(HexLayer layer)
    {
        var dto = new HexGenerationDto { Recipe = layer.GenerationRecipe,
            Protection = [.. layer.GenerationProtection.OrderBy(h => h.R).ThenBy(h => h.Q)] };
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (h, cell) in layer.Board.Cells)
        {
            if (cell.GenerationReason is not { } reason) continue;
            if (!indices.TryGetValue(reason, out var index))
            { index = dto.Reasons.Count; indices.Add(reason, index); dto.Reasons.Add(reason); }
            dto.Cells.Add(h.Q); dto.Cells.Add(h.R); dto.Cells.Add(index);
        }
        return dto.Recipe is null && dto.Protection.Count == 0 && dto.Cells.Count == 0 ? null : dto;
    }

    private static HexLayer ReadGenerationV31(HexLayer layer, HexGenerationDto? dto)
    {
        if (layer.Paths.Any(p => p.GenerationReason is { Length: > 240 }))
            throw new MapProjectCorruptedException("Слишком длинная причина генерации.");
        if (dto is null) return layer; // v30 and older: no protection, recipe or reasons.
        if (dto.Protection is null || dto.Cells is null || dto.Reasons is null || dto.Cells.Count % 3 != 0
            || dto.Protection.Any(h => !layer.Board.Contains(h)) || dto.Protection.Distinct().Count() != dto.Protection.Count
            || dto.Reasons.Count > 256 || dto.Reasons.Any(r => string.IsNullOrWhiteSpace(r) || r.Length > 240))
            throw new MapProjectCorruptedException("Повреждены данные генерации v31.");
        if (dto.Recipe is { } recipe)
        {
            var s = recipe.Settings;
            if (recipe.AlgorithmVersion != 1 || s is null || recipe.Palette is null || recipe.Selection is null
                || recipe.Steps.IsDefaultOrEmpty || recipe.Steps.Length > 6 || recipe.Steps.Any(s => !Enum.IsDefined(s))
                || recipe.Selection.Any(h => !layer.Board.Contains(h))
                || !double.IsFinite(s.LandShare) || !double.IsFinite(s.Fragmentation) || !double.IsFinite(s.Relief)
                || !double.IsFinite(s.Moisture) || !double.IsFinite(s.Temperature)
                || !double.IsFinite(s.RiversPerThousand) || !double.IsFinite(s.SettlementsPerThousand))
                throw new MapProjectCorruptedException("Повреждён рецепт генерации v31.");
        }
        var changes = new List<KeyValuePair<HexCoord, HexCell?>>();
        var seen = new HashSet<HexCoord>();
        for (var i = 0; i < dto.Cells.Count; i += 3)
        {
            var h = new HexCoord(dto.Cells[i], dto.Cells[i + 1]);
            var index = dto.Cells[i + 2];
            if (!layer.Board.Contains(h) || !seen.Add(h) || layer.Board[h] is not { } cell || index < 0 || index >= dto.Reasons.Count)
                throw new MapProjectCorruptedException("Повреждена причина генерации гекса.");
            changes.Add(new(h, cell.WithGenerationReason(dto.Reasons[index])));
        }
        return layer with { GenerationProtection = dto.Protection.ToImmutableHashSet(), GenerationRecipe = dto.Recipe,
            Board = layer.Board.With(changes) };
    }
}
