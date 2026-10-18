using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>
/// The world generator of the hex workshop: one step at a time (land, relief, climate, rivers,
/// settlements, roads), each one undo step, each built on the board as it is now - paint between
/// the steps and the next step takes the edit into account; the last step can be dealt again.
/// </summary>
public sealed partial class EditorViewModel
{
    private HexGeneratorSettings _hexGenerator = new() { Seed = Environment.TickCount & 0xFFFF };
    private (HexGenerationStep Step, long UndoDepth)? _hexLastGenerated;

    public HexGeneratorSettings HexGeneratorSettings
    {
        get => _hexGenerator;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_hexGenerator == value) return;
            _hexGenerator = value;
            Raise(nameof(HexGeneratorSettings));
        }
    }

    public static string HexGenerationStepName(HexGenerationStep step) => step switch
    {
        HexGenerationStep.Land => "Суша и моря",
        HexGenerationStep.Relief => "Холмы и горы",
        HexGenerationStep.Climate => "Климат и леса",
        HexGenerationStep.Rivers => "Реки",
        HexGenerationStep.Settlements => "Поселения",
        _ => "Дороги",
    };

    /// <summary>Runs one generator step on the active hex layer as one undo step.</summary>
    public void RunHexGeneratorStep(HexGenerationStep step)
    {
        if (ActiveHexLayer is not { } layer)
        {
            StatusDetail = "На этом уровне нет слоя гексов.";
            return;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var after = HexWorldGenerator.Run(layer, step, _hexGenerator);
        if (ReferenceEquals(after, layer) || (after.Board == layer.Board && after.Paths == layer.Paths))
        {
            StatusDetail = step switch
            {
                HexGenerationStep.Roads => "Дорог не проложено: нужно хотя бы два поселения.",
                HexGenerationStep.Rivers => "Рек не появилось: нужны холмы или горы (шаг «Холмы и горы»).",
                _ => $"Шаг «{HexGenerationStepName(step)}» ничего не изменил.",
            };
            return;
        }

        ExecuteHexLayerEdit("Генератор: " + HexGenerationStepName(step), layer, after,
            EditCost.BaseBytes + (long)after.Board.CellCount * 48);
        _hexLastGenerated = (step, Session.UndoDepth);
        Raise(nameof(HexPaths));
        Raise(nameof(HexBoardSummary));
        Raise(nameof(HexCanRerollGenerator));
        StatusDetail = $"Генератор: «{HexGenerationStepName(step)}» готов за {watch.Elapsed.TotalSeconds:0.0} с. Ctrl+Z отменяет шаг.";
    }

    /// <summary>All steps in order (each still its own undo step).</summary>
    public void RunHexGeneratorAll()
    {
        foreach (var step in Enum.GetValues<HexGenerationStep>()) RunHexGeneratorStep(step);
        StatusDetail = "Мир создан по шагам: каждый шаг можно отменить или перебросить.";
    }

    /// <summary>True right after a generator step, while it is still the last thing done.</summary>
    public bool HexCanRerollGenerator => _hexLastGenerated is { } last && last.UndoDepth == Session.UndoDepth;

    /// <summary>Takes the last generator step back and deals it again with a new seed.</summary>
    public void RerollHexGeneratorStep()
    {
        if (_hexLastGenerated is not { } last || last.UndoDepth != Session.UndoDepth)
        {
            StatusDetail = "Перебросить можно только последний шаг генератора, пока после него ничего не менялось.";
            return;
        }

        Undo();
        _hexGenerator = _hexGenerator with { Seed = _hexGenerator.Seed + 1 };
        Raise(nameof(HexGeneratorSettings));
        RunHexGeneratorStep(last.Step);
    }

    /// <summary>A new random seed for the next steps.</summary>
    public void NewHexGeneratorSeed()
    {
        HexGeneratorSettings = _hexGenerator with { Seed = Random.Shared.Next(1, 1_000_000) };
    }
}
