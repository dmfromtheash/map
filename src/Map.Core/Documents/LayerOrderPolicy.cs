namespace Map.Core.Documents;

/// <summary>
/// Canonical cross-type ordering rules for the editable layer stack.
/// Land is terrain beneath scene objects: every land layer therefore stays below every object
/// layer. Relative order inside the Land group, inside the Object group, and among all non-Land
/// layers is preserved.
/// </summary>
public static class LayerOrderPolicy
{
    /// <summary>
    /// Moves only Land layers that occur above the first Object layer. Existing files therefore
    /// acquire the same safe composition as new edits without changing the relative order of any
    /// non-Land layers.
    /// </summary>
    public static IReadOnlyList<MapLayer> Normalize(IReadOnlyList<MapLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        var firstObject = -1;
        for (var index = 0; index < layers.Count; index++)
        {
            if (layers[index] is ObjectLayer)
            {
                firstObject = index;
                break;
            }
        }

        if (firstObject < 0 || !layers.Skip(firstObject + 1).Any(layer => layer is LandLayer))
        {
            return layers;
        }

        var misplacedLand = layers.Skip(firstObject + 1).Where(layer => layer is LandLayer).ToList();
        var normalized = new List<MapLayer>(layers.Count);
        for (var index = 0; index < layers.Count; index++)
        {
            if (index <= firstObject || layers[index] is not LandLayer)
            {
                normalized.Add(layers[index]);
            }
        }
        var insertion = normalized.FindIndex(layer => layer is ObjectLayer);
        normalized.InsertRange(insertion, misplacedLand);
        return normalized;
    }

    /// <summary>
    /// A user may correct an old invalid boundary, but cannot move Land above Object or Object
    /// below Land again.
    /// </summary>
    public static bool CanMove(MapDocument document, LayerId layerId, bool up)
    {
        ArgumentNullException.ThrowIfNull(document);
        var layers = document.Layers;
        var index = layers.ToList().FindIndex(layer => layer.Id == layerId);
        if (index < 0 || layers[index] is BaseColorLayer)
        {
            return false;
        }

        var target = index + (up ? 1 : -1);
        if (target < 1 || target >= layers.Count)
        {
            return false;
        }

        return !(up && layers[index] is LandLayer && layers[target] is ObjectLayer)
            && !(!up && layers[index] is ObjectLayer && layers[target] is LandLayer);
    }
}
