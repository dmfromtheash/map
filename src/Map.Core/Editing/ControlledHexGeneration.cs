using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>Bounds every step to the frozen selection and excludes protected cells and paths.</summary>
public static class ControlledHexGeneration
{
    public static HexLayer Run(HexLayer snapshot, HexGenerationRecipe recipe, CancellationToken cancellationToken = default)
    {
        var layer = snapshot;
        bool Allowed(HexCoord h) => !snapshot.GenerationProtection.Contains(h)
            && (recipe.Selection.IsEmpty || recipe.Selection.Contains(h));
        foreach (var step in recipe.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generated = HexWorldGenerator.Run(layer, step, recipe.Settings, recipe.Palette, cancellationToken);
            var changes = new List<KeyValuePair<HexCoord, HexCell?>>();
            foreach (var (h, cell) in generated.Board.Cells)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Allowed(h) || Equals(cell, layer.Board[h])) continue;
                changes.Add(new(h, cell.WithGenerationReason(Reason(step, generated, h, cell, recipe.Palette))));
            }
            // Whole paths crossing the protected/outside region are retained, never trimmed silently.
            static IEnumerable<HexCoord> Touched(HexPath p) => p.Style == HexRouteStyle.Edges
                ? p.Vertices.SelectMany(v => v.Hex.Neighbors().Append(v.Hex)) : p.Hexes;
            var candidates = generated.Paths.Where(p => Touched(p).All(Allowed)).ToDictionary(p => p.Id);
            var sourceIds = layer.Paths.Select(p => p.Id).ToHashSet();
            var paths = layer.Paths.Where(p => Touched(p).Any(h => !Allowed(h)) || candidates.ContainsKey(p.Id))
                .Select(p => candidates.GetValueOrDefault(p.Id, p)).ToImmutableList();
            paths = paths.AddRange(generated.Paths.Where(p => candidates.ContainsKey(p.Id) && !sourceIds.Contains(p.Id))
                .Select(p => p with { GenerationReason = p.Kind == HexPathKind.River ? "Река от возвышенности вниз к воде или низине."
                        : "Дорога соединяет поселения по доступной местности." }));
            layer = layer with { Board = layer.Board.With(changes), Paths = paths };
        }
        cancellationToken.ThrowIfCancellationRequested();
        return layer with { GenerationRecipe = recipe };
    }

    private static string Reason(HexGenerationStep step, HexLayer layer, HexCoord h, HexCell cell, HexGeneratorPalette palette)
    {
        if (step == HexGenerationStep.Settlements && cell.Feature is not null)
        {
            var water = h.Neighbors().Any(n => palette.IsWater(layer.Board[n]?.Terrain));
            var river = layer.Paths.Any(p => p.Kind == HexPathKind.River && p.Hexes.Contains(h));
            return cell.Feature == palette.Mine ? "Месторождение в горной местности."
                : river ? "Поселение на пригодной земле у реки."
                : water ? "Поселение на пригодной земле рядом с водой." : "Поселение на пригодной земле с учётом расстояния до соседей.";
        }
        return step switch
        {
            HexGenerationStep.Land => "Суша или вода по высоте, форме края и доле суши.",
            HexGenerationStep.Relief => "Рельеф по высоте суши и полю горных хребтов.",
            HexGenerationStep.Climate => "Биом по широте, высоте, температуре и влажности.",
            HexGenerationStep.Rivers => "Озеро в низине на пути стока реки.",
            _ => "Поля вокруг поселения.",
        };
    }
}
