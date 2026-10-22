using System.Text.RegularExpressions;
using Map.Core.Documents;

namespace Map.Persistence;

public static partial class MapProjectStore
{
    // v23 → v24: stable set references only. PNG stay in the offline local library.
    // Missing sets never prevent opening; the renderer uses its existing missing-tile marker.
    private static List<string>? WriteTileSetsV24(HexLayer layer)
    {
        var sets = layer.Board.Cells.SelectMany(p => new[] { p.Value.Terrain }.Concat(p.Value.PointsOfInterest.Select(t => t.Type)))
            .Where(id => id.StartsWith("user/", StringComparison.Ordinal))
            .Select(id => id.Split('/')).Where(parts => parts.Length == 3).Select(parts => parts[1])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return sets.Count == 0 ? null : sets;
    }

    private static bool NeedsTileSetsV24(HexLayer layer) => WriteTileSetsV24(layer) is not null;

    private static void ReadTileSetsV24(HexLayer layer, List<string>? sets)
    {
        if (sets is null) return; // v4…v23 and legacy arbitrary tile ids keep their meaning.
        if (sets.Count is < 1 or > 4096 || sets.Any(id => id is null || id.Length is < 1 or > 48
            || !Regex.IsMatch(id, "^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant))
            || !sets.SequenceEqual(WriteTileSetsV24(layer) ?? [], StringComparer.Ordinal))
            throw new MapProjectCorruptedException("Повреждены ссылки на гекс-комплекты (v24).");
    }
}
