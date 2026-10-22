using System.Runtime.CompilerServices;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

public static partial class HexLayerRenderer
{
    private static readonly ConditionalWeakTable<HexLayer, HexLayer> PlayerLayers = new();

    private static void DrawPresentation(SKCanvas canvas, HexLayer layer, WorldBounds view,
        HexPresentation presentation, double scale, bool player)
    {
        var layout = layer.Board.Layout;
        var travel = presentation == HexPresentation.Travel
            ? new HexTravelPlanner(layer, id => HexStarterTiles.Find(id)?.TravelCost) : null;
        using var fill = new SKPaint { IsAntialias = true };
        using var ink = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#302B24") };
        using var halo = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        using var font = new SKFont(SKTypeface.Default, (float)Math.Clamp(layout.Size * .16, 6, 16));
        foreach (var key in layer.Board.ChunksIn(view))
        {
        if (layer.Board.Chunk(key) is not { } chunk) continue;
        for (var row = 0; row < HexBoard.ChunkSize; row++)
        for (var column = 0; column < HexBoard.ChunkSize; column++)
        {
            if (chunk[column, row] is not { } cell) continue;
            var hex = layer.Board.CoordOf(key, column, row);
            var (x, y) = layout.Center(hex);
            if (x < view.Left - layout.Size || x > view.Right + layout.Size || y < view.Top - layout.Size || y > view.Bottom + layout.Size) continue;
            string? label = null;
            if (presentation == HexPresentation.Politics && layer.Region(cell.Region) is { } region)
            {
                using var shape = new SKPath();
                for (var i = 0; i < 6; i++)
                {
                    var corner = layout.Corner(hex, i);
                    if (i == 0) shape.MoveTo((float)corner.X, (float)corner.Y); else shape.LineTo((float)corner.X, (float)corner.Y);
                }
                shape.Close();
                fill.Color = SKColor.Parse(region.Color).WithAlpha(110);
                canvas.DrawPath(shape, fill);
                label = region.Name;
            }
            else if (presentation == HexPresentation.Travel)
            {
                var costs = hex.Neighbors().Select(from => travel!.StepCost(from, hex)).Where(c => c.HasValue).ToArray();
                var cost = costs.Length == 0 ? (double?)null : costs.Min();
                label = cost is null ? "Непроходимо" : $"{cost * layer.Travel.DistancePerHex / layer.Travel.DistancePerDay:0.##} дн.";
            }
            else if (scale * layout.Size > 28)
                label = string.Join(" · ", cell.PointsOfInterest.Where(p => !player || p.PlayerVisible).Select(p => p.Name).Where(n => n.Length != 0));
            if (string.IsNullOrEmpty(label) || scale * layout.Size < 15) continue;
            if (label.Length > 28) label = label[..27] + "…";
            var width = font.MeasureText(label);
            var baseline = (float)(y + layout.Size * .55);
            canvas.DrawText(label, (float)x - width / 2, baseline, font, halo);
            canvas.DrawText(label, (float)x - width / 2, baseline, font, ink);
        }
        }
    }
}
