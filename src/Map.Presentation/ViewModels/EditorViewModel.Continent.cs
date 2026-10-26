using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R23: the continent generator of the Land tool. Each step is calculated in the background on a
/// snapshot (the R20 pipeline): a preview first, «Применить» makes it one undo step, a change of
/// the map meanwhile asks for a recalculation.
/// </summary>
public sealed partial class EditorViewModel
{
    private ContinentSettings _continent = new() { Seed = Environment.TickCount & 0xFFFF };

    public ContinentSettings ContinentSettings
    {
        get => _continent;
        set { ArgumentNullException.ThrowIfNull(value); _continent = value; Raise(nameof(ContinentSettings)); }
    }

    public static string ContinentStepName(ContinentStep step) => step switch
    {
        ContinentStep.Heights => "Высоты",
        ContinentStep.Erosion => "Эрозия",
        ContinentStep.Climate => "Климат",
        ContinentStep.Rivers => "Реки",
        _ => "Поселения и дороги",
    };

    public void NewContinentSeed() => ContinentSettings = _continent with { Seed = Random.Shared.Next(1, 1_000_000) };

    public void RunContinentStep(ContinentStep step) => StartContinent([step]);

    public void RunContinentAll() => StartContinent([.. Enum.GetValues<ContinentStep>()]);

    private void StartContinent(IReadOnlyList<ContinentStep> steps)
    {
        if (ActiveLayer is not LandLayer { Locked: false } land || land.GeometryTiles.TileCount == 0)
        { StatusDetail = "Выберите слой суши с нарисованной землёй: материк строится из неё."; return; }
        var settings = _continent;
        var landId = land.Id;
        _recalculateGeneration = () => { StartContinent(steps); return PendingGeneration; };
        PendingGeneration = CalculateGenerationAsync((document, token) =>
        {
            var area = ((LandLayer)document.FindLayer(landId)!).GeometryTiles.TileCount * (long)TileMap.TileSize * TileMap.TileSize;
            if (!_workingMemory.TryReserve(EditCost.BaseBytes + area * 6, out var reservation, out var reason))
                throw new InvalidOperationException(reason);
            using var held = reservation;
            Volatile.Write(ref _generationProgress, 0);
            var after = document;
            for (var k = 0; k < steps.Count; k++)
            {
                var first = k;
                after = ContinentGenerator.Run(after, landId, steps[k], settings, token,
                    p => Volatile.Write(ref _generationProgress, (first + Math.Clamp(p, 0, 1)) / steps.Count));
            }
            var name = steps.Count == 1 ? "Материк: " + ContinentStepName(steps[0]).ToLowerInvariant() : "Материк целиком";
            return new GeneratedDocumentCommand(name, document, after, EditCost.BaseBytes + area * 5);
        });
    }

    /// <summary>A generated snapshot of the whole map: applies on the exact document it was made from.</summary>
    private sealed class GeneratedDocumentCommand(string name, MapDocument before, MapDocument after, long bytes) : IEditCommand
    {
        public string Name { get; } = name;
        public long? MeasuredWorkingBytes => bytes;
        public MapDocument Apply(MapDocument document) => ReferenceEquals(document, before) ? after
            : throw new InvalidOperationException("Карта изменилась: требуется пересчёт генерации.");
        public MapDocument Revert(MapDocument document) => before;
    }
}
