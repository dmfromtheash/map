using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing;

public static class ControlledAreaGeneration
{
    public static IEditCommand Create(MapDocument document, LayerId layerId, Guid areaId, CancellationToken token,
        bool? forestEcology = null, PlacementArea? parameters = null)
    {
        token.ThrowIfCancellationRequested();
        var before = (ObjectLayer)document.FindLayer(layerId)!;
        var area = parameters ?? before.PlacementAreas.Single(a => a.Id == areaId);
        var recipe = new AreaGenerationRecipe(area.Seed, area.Density, area.LayoutVersion, area.Pattern,
            area.SizeVariationPercent, area.JoinEdges);
        var command = new PlacementAreaCommand(layerId, areaId,
            area with { GenerationRecipe = recipe, Rules = (area.Rules ?? new()) with { Revision = (area.Rules?.Revision ?? 0) + 1 } }, "Перегенерация области", cancellationToken: token, forestEcology: forestEcology);
        var generated = (ObjectLayer)command.Apply(document).FindLayer(layerId)!;
        var next = generated.PlacementAreas.Single(a => a.Id == areaId);
        var generatedIds = next.Members.Where(m => area.GenerationProtection?.Contains(m.Slot) != true).Select(m => m.ObjectId).ToHashSet();
        // The command already preserved protected members; keep their draw order as well.
        var after = generated with { Objects = generated.Objects.Select(o => generatedIds.Contains(o.Id)
            ? o with { GenerationReason = "Расстановка внутри выбранной области по её плотности, рисунку и ограничениям местности." } : o).ToImmutableArray() };
        token.ThrowIfCancellationRequested();
        return UpdateLayerCommand.Replace(before, after, "Применить генерацию области");
    }
}
