using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

public enum HexTileSetCheckKind { Neighbors, Rotations, Overlaps }

public sealed record HexTileSetCheck(HexUserTile First, HexUserTile Second, int FirstRotation, int SecondRotation,
    int Direction, HexTileSetCheckKind Kind, IReadOnlyList<string> Problems)
{
    public string Description => $"{First.Name} ({FirstRotation * 60}°) / {Second.Name} ({SecondRotation * 60}°), грань {Direction + 1}";
}

/// <summary>Контрольный лист вычисляется по номеру, без миллиона элементов UI и картинок в памяти.</summary>
public static class HexTileSetInspection
{
    public static int Count(HexUserTileSet set, HexTileSetCheckKind kind) => kind switch
    {
        HexTileSetCheckKind.Rotations => set.Manifest.Tiles.Count * 6,
        HexTileSetCheckKind.Overlaps => set.Manifest.Tiles.Count * set.Manifest.Tiles.Count * 6,
        _ => set.Manifest.Tiles.Count * set.Manifest.Tiles.Count * 6 * 36,
    };

    public static HexTileSetCheck At(HexUserTileSet set, HexTileSetCheckKind kind, int index, HexOrientation orientation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= Count(set, kind)) throw new ArgumentOutOfRangeException(nameof(index));
        var n = set.Manifest.Tiles.Count;
        int a, b, ar = 0, br = 0, direction = 0;
        if (kind == HexTileSetCheckKind.Rotations) { ar = index % 6; a = b = index / 6; }
        else
        {
            if (kind == HexTileSetCheckKind.Neighbors) { br = index % 6; index /= 6; ar = index % 6; index /= 6; }
            direction = index % 6; index /= 6; b = index % n; a = index / n;
        }
        var first = set.Manifest.Tiles[a]; var second = set.Manifest.Tiles[b];
        var problems = new List<string>();
        if (ar != 0 && !first.Rotatable || br != 0 && !second.Rotatable) problems.Add("Поворот запрещён: у плитки есть верх.");
        if (ar != 0 && first.Overhang > 0 || br != 0 && second.Overhang > 0) problems.Add("Нависающая плитка рисуется без поворота.");
        if (kind == HexTileSetCheckKind.Neighbors)
        {
            // Directions go counterclockwise, picture rotation is clockwise.
            var left = first.Edges[(direction + ar) % 6]; var right = second.Edges[(direction + 3 + br) % 6];
            if (left != "*" && right != "*" && left != right) problems.Add($"Край не совпадает: «{left}» / «{right}».");
        }
        var row = orientation == HexOrientation.Pointy ? .75 : 1;
        if (first.Overhang > row || second.Overhang > row) problems.Add("Нависание выходит за ряд: проверьте перекрытие дальнего ряда.");
        return new(first, second, ar, br, direction, kind, problems);
    }

    public static SKImage Preview(HexUserTileSet set, HexTileSetCheck check, HexOrientation orientation)
    {
        using var surface = SKSurface.Create(new SKImageInfo(600, 420));
        surface.Canvas.Clear(SKColors.Transparent);
        var layout = new HexLayout(orientation, 70, 300, 260);
        var center = new HexCoord(0, 0);
        var cells = check.Kind == HexTileSetCheckKind.Rotations ? new[] { center } : new[] { center, center.Neighbor(check.Direction) };
        // Same back-to-front order as the board; no new artwork or transition painting.
        foreach (var hex in cells.OrderBy(h => layout.Center(h).Y).ThenBy(h => layout.Center(h).X))
        {
            var first = hex == center;
            var tile = first ? check.First : check.Second;
            var turn = first ? check.FirstRotation : check.SecondRotation;
            HexLayerRenderer.DrawTile(surface.Canvas, layout, hex, set.TileId(tile), 0, rotation: tile.Rotatable ? turn : 0);
        }
        return surface.Snapshot();
    }
}
