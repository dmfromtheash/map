using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>Bounds every step to the frozen selection and excludes protected cells and paths.</summary>
public static class ControlledHexGeneration
{
    /// <param name="progress">Hears 0..1 over all the steps, from the chunks each step works through (R21).</param>
    public static HexLayer Run(HexLayer snapshot, HexGenerationRecipe recipe, CancellationToken cancellationToken = default,
        Action<double>? progress = null)
    {
        var layer = snapshot;
        bool Allowed(HexCoord h) => !snapshot.GenerationProtection.Contains(h)
            && (recipe.Selection.IsEmpty || recipe.Selection.Contains(h));
        var stepIndex = 0;
        foreach (var step in recipe.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = stepIndex++;
            var generated = HexWorldGenerator.Run(layer, step, recipe.Settings, recipe.Palette, cancellationToken,
                progress is null ? null : fraction => progress((first + Math.Clamp(fraction, 0, 1)) / recipe.Steps.Length));
            var changes = new List<KeyValuePair<HexCoord, HexCell?>>();
            // R20b/D5: the generator hands out few shared cell instances, and a reason is one of a few strings.
            // Re-stamping every hex (copy + hash into the shared table) cost +50 % of «Суша и моря» on 1024²;
            // the same instance with the same reason gets the same result, so it is looked up once.
            // A step that gives every hex its own instance (states: a region each) gains nothing, so the table stops growing.
            var stamped = new Dictionary<(HexCell Cell, string Reason), HexCell>(StampComparer.Instance);
            HashSet<HexCoord>? riverHexes = null;
            HashSet<HexCoord> Rivers() => riverHexes ??= [.. generated.Paths.Where(p => p.Kind == HexPathKind.River).SelectMany(p => p.Hexes)];
            var protectedOrPartial = !snapshot.GenerationProtection.IsEmpty || !recipe.Selection.IsEmpty;
            // Both boards share one geometry, so they are compared chunk by chunk: a chunk the step left as it was
            // is the same object and is skipped whole; inside a chunk the old cell is read without a lookup per hex.
            if (generated.Board.Columns != layer.Board.Columns || generated.Board.Rows != layer.Board.Rows
                || generated.Board.Layout != layer.Board.Layout)
                throw new InvalidOperationException("Генератор изменил размер доски гексов.");
            foreach (var key in generated.Board.ChunkKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = generated.Board.Chunk(key)!; var old = layer.Board.Chunk(key);
                if (ReferenceEquals(chunk, old)) continue;
                for (var row = 0; row < HexBoard.ChunkSize; row++)
                    for (var column = 0; column < HexBoard.ChunkSize; column++)
                    {
                        if (chunk[column, row] is not { } cell || Equals(cell, old?[column, row])) continue;
                        var h = generated.Board.CoordOf(key, column, row);
                        if (protectedOrPartial && !Allowed(h)) continue;
                        var reason = Reason(step, generated, h, cell, recipe.Palette, Rivers);
                        if (!stamped.TryGetValue((cell, reason), out var result))
                        {
                            result = cell.WithGenerationReason(reason);
                            if (stamped.Count < 4096) stamped[(cell, reason)] = result;
                        }
                        changes.Add(new(h, result));
                    }
            }
            // Whole paths crossing the protected/outside region are retained, never trimmed silently.
            static IEnumerable<HexCoord> Touched(HexPath p) => p.Style == HexRouteStyle.Edges
                ? p.Vertices.SelectMany(v => v.Hex.Neighbors().Append(v.Hex)) : p.Hexes;
            // Without protection or a selection every hex is allowed: no path needs walking.
            var candidates = generated.Paths.Where(p => !protectedOrPartial || Touched(p).All(Allowed)).ToDictionary(p => p.Id);
            var sourceIds = layer.Paths.Select(p => p.Id).ToHashSet();
            var paths = layer.Paths.Where(p => protectedOrPartial && Touched(p).Any(h => !Allowed(h)) || candidates.ContainsKey(p.Id))
                .Select(p => candidates.GetValueOrDefault(p.Id, p)).ToImmutableList();
            paths = paths.AddRange(generated.Paths.Where(p => candidates.ContainsKey(p.Id) && !sourceIds.Contains(p.Id))
                .Select(p => p with { GenerationReason = p.Kind == HexPathKind.River ? "Река от возвышенности вниз к воде или низине."
                        : "Дорога соединяет поселения по доступной местности." }));
            layer = layer with { Board = layer.Board.With(changes), Paths = paths };
            // A state is a region: its entry comes with the hexes it was given.
            if (step == HexGenerationStep.States) layer = layer with { Regions = generated.Regions };
        }
        cancellationToken.ThrowIfCancellationRequested();
        return layer with { GenerationRecipe = recipe };
    }

    /// <summary>Same cell instance and same reason string (both by reference).</summary>
    private sealed class StampComparer : IEqualityComparer<(HexCell Cell, string Reason)>
    {
        public static readonly StampComparer Instance = new();
        public bool Equals((HexCell Cell, string Reason) x, (HexCell Cell, string Reason) y)
            => ReferenceEquals(x.Cell, y.Cell) && ReferenceEquals(x.Reason, y.Reason);
        public int GetHashCode((HexCell Cell, string Reason) value)
            => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Cell),
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Reason));
    }

    private static string Reason(HexGenerationStep step, HexLayer layer, HexCoord h, HexCell cell, HexGeneratorPalette palette,
        Func<HashSet<HexCoord>> riverHexes)
    {
        if (step == HexGenerationStep.Settlements && cell.Feature is not null)
        {
            var water = h.Neighbors().Any(n => palette.IsWater(layer.Board[n]?.Terrain));
            var river = riverHexes().Contains(h);
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
            HexGenerationStep.States => "Земля государства, до столицы которого путь короче всего.",
            _ => "Поля вокруг поселения.",
        };
    }
}
