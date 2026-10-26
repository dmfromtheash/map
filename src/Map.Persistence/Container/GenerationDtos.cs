using Map.Core.Documents;

namespace Map.Persistence.Container;

internal sealed class HexGenerationDto
{
    public HexGenerationRecipe? Recipe { get; set; }
    public List<HexCoord> Protection { get; set; } = [];
    public List<string> Reasons { get; set; } = [];
    // Axial q, r, reason palette index. Separate from the historical cell strides.
    public List<int> Cells { get; set; } = [];
}
