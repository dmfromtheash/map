using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    private HexGeneratorSettings _hexGenerator = new() { Seed = Environment.TickCount & 0xFFFF };
    private readonly BackgroundGeneration _generation = new();
    private Func<Task>? _recalculateGeneration;
    private long _generationUiJob;
    public Task PendingGeneration { get; private set; } = Task.CompletedTask;
    public bool GenerationBusy => _generation.IsBusy;
    public bool GenerationHasPreview => _generation.Preview is not null;
    public bool GenerationConflict => _generation.Conflicted || GenerationHasPreview && !_generation.IsCurrent(Session);
    public bool GenerationSelectionOnly { get; set; }
    public bool GenerationProtectionErase { get; set; }
    public bool HexCanRerollGenerator => ActiveHexLayer?.GenerationRecipe is not null && !GenerationBusy;
    public MapDocument? GenerationPreview => GenerationConflict ? null : _generation.Preview;

    public HexGeneratorSettings HexGeneratorSettings
    {
        get => _hexGenerator;
        set { ArgumentNullException.ThrowIfNull(value); _hexGenerator = value; Raise(nameof(HexGeneratorSettings)); }
    }

    public static string HexGenerationStepName(HexGenerationStep step) => step switch
    {
        HexGenerationStep.Land => "Суша и моря", HexGenerationStep.Relief => "Холмы и горы",
        HexGenerationStep.Climate => "Климат и леса", HexGenerationStep.Rivers => "Реки",
        HexGenerationStep.Settlements => "Поселения", _ => "Дороги",
    };

    public void RunHexGeneratorStep(HexGenerationStep step) => StartHexGeneration([step]);
    public void RunHexGeneratorAll() => StartHexGeneration([.. Enum.GetValues<HexGenerationStep>()]);

    private void StartHexGeneration(ImmutableArray<HexGenerationStep> steps)
    {
        if (ActiveHexLayer is not { } layer) return;
        if (GenerationSelectionOnly && _hexSelection.Count == 0)
        { StatusDetail = "Сначала выделите гексы на вкладке «Правка»."; return; }
        var recipe = new HexGenerationRecipe { Settings = _hexGenerator, Steps = steps,
            Selection = GenerationSelectionOnly ? _hexSelection.ToImmutableHashSet() : [] };
        _recalculateGeneration = () => { StartHexGeneration(steps); return PendingGeneration; };
        PendingGeneration = CalculateGenerationAsync((document, token) =>
        {
            var snapshot = (HexLayer)document.FindLayer(layer.Id)!;
            if (!_workingMemory.TryReserve(EditCost.BaseBytes + (long)snapshot.Board.Columns * snapshot.Board.Rows * 192,
                out var reservation, out var reason)) throw new InvalidOperationException(reason);
            using var held = reservation;
            var after = ControlledHexGeneration.Run(snapshot, recipe, token);
            return new HexLayerEditCommand("Применить генерацию", snapshot, after,
                EditCost.BaseBytes + (long)after.Board.CellCount * 48);
        });
    }

    private async Task CalculateGenerationAsync(Func<MapDocument, CancellationToken, IEditCommand> calculate)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var uiJob = ++_generationUiJob;
        var work = _generation.CalculateAsync(Session, calculate);
        StatusDetail = "Расчёт в фоне. Карту можно редактировать; расчёт можно отменить.";
        RaiseGenerationState();
        try
        {
            await work;
            if (uiJob != _generationUiJob) return;
            StatusDetail = GenerationConflict ? "Карта изменилась во время расчёта. Нажмите «Пересчитать»."
                : GenerationHasPreview ? $"Предпросмотр готов за {watch.Elapsed.TotalSeconds:0.0} с. Нажмите «Применить»."
                : "Генерация отменена. Документ не изменён.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException)
        { if (uiJob == _generationUiJob) { _generation.Cancel(); StatusDetail = "Расчёт не завершён: " + ex.Message; } }
        finally { if (uiJob == _generationUiJob) RaiseGenerationState(); }
    }

    public void CancelGeneration() { ++_generationUiJob; _generation.Cancel(); StatusDetail = "Генерация отменена. Документ не изменён."; RaiseGenerationState(); }
    public void RecalculateGeneration() { if (_recalculateGeneration is not null) _ = _recalculateGeneration(); }

    public bool ApplyGeneration()
    {
        var result = _generation.TryApply(Session);
        if (result == GenerationApplyResult.Applied) AfterEdit();
        StatusDetail = result switch
        {
            GenerationApplyResult.Applied => "Генерация применена. Одна отмена возвращает карту до применения.",
            GenerationApplyResult.Conflict => "Карта изменилась. Результат не применён. Нажмите «Пересчитать».",
            GenerationApplyResult.ResourceRefused => Session.LastResourceError ?? "Недостаточно памяти для применения.",
            _ => "Сначала дождитесь предпросмотра.",
        };
        RaiseGenerationState(); return result == GenerationApplyResult.Applied;
    }

    private void RaiseGenerationState()
    {
        Raise(nameof(GenerationBusy)); Raise(nameof(GenerationHasPreview)); Raise(nameof(GenerationConflict));
        Raise(nameof(HexCanRerollGenerator));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void RerollHexGeneratorStep()
    {
        if (ActiveHexLayer?.GenerationRecipe is not { } recipe) return;
        HexGeneratorSettings = recipe.Settings with { Seed = recipe.Settings.Seed + 1 };
        StartHexGeneration(recipe.Steps);
    }
    public void NewHexGeneratorSeed() => HexGeneratorSettings = _hexGenerator with { Seed = Random.Shared.Next(1, 1_000_000) };

    public void ProtectHexSelection(bool protect)
    {
        if (ActiveHexLayer is not { } layer || _hexSelection.Count == 0) return;
        ExecuteHexLayerEdit("Защита гексов", layer, layer with { GenerationProtection = protect
            ? layer.GenerationProtection.Union(_hexSelection) : layer.GenerationProtection.Except(_hexSelection) });
    }
}
