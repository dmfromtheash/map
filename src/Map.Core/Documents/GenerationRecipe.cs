using System.Collections.Immutable;
using Map.Core.Editing;

namespace Map.Core.Documents;

public sealed record AreaGenerationRecipe(int Seed, double Density, int LayoutVersion, PlacementAreaPattern Pattern,
    double SizeVariationPercent, bool JoinEdges, int AlgorithmVersion = 1);

/// <summary>Reproducible inputs, kept with the generated layer rather than in editor settings.</summary>
public sealed record HexGenerationRecipe
{
    public int AlgorithmVersion { get; init; } = 1;
    public HexGeneratorSettings Settings { get; init; } = new();
    public HexGeneratorPalette Palette { get; init; } = new();
    public ImmutableArray<HexGenerationStep> Steps { get; init; } = [];
    // Empty means the whole board. Coordinates use the board's axial layout.
    public ImmutableHashSet<HexCoord> Selection { get; init; } = [];
}
