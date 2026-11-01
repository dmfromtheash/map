using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

/// <summary>
/// The starting sizes of the drawing tools, from the size of the canvas (audit 2026-11-01, А1.2, DEC-085): a 24 px
/// land brush under a 64 px coast roughness left a chain of islets instead of a shore, and mountains, rivers and roads
/// kept the same world size on a 1 920 and a 16 384 canvas. The canvas, not the view, sets them: the same map starts
/// the same at any zoom, and the numbers stay what the panel shows. Maps keep everything already drawn; only new
/// strokes and objects use these. The owner chose «B · средне» from three variants shot on USER.
/// </summary>
public sealed record NewMapDefaults(
    double LandBrushRadius, double LandRoughness, double StampWidth, double AreaStampFactor, double RiverWidth, double RoadWidth)
{
    /// <summary>The coast cells must be well inside the brush, or a dab holds one cell centre or none.</summary>
    private const double BrushPerRoughness = 2.5;

    // Fractions of the canvas's long side; on 4096: brush 158, roughness 63, mountain 372, river 27, road 19.
    private const double Brush = 1 / 26.0, Stamp = 1 / 11.0, River = 1 / 150.0, Road = 1 / 220.0;

    /// <summary>An area's groves are measured in its stamps' widths: its mountains are half a single one.</summary>
    private const double Area = .5;

    public static NewMapDefaults For(CanvasDefinition canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var side = Math.Max(1, Math.Max(canvas.Width, canvas.Height));
        // The panel's brush slider ends at 256 px; the coast follows the brush it gets.
        var radius = Math.Clamp(Math.Round(side * Brush), 8, 256);
        return new(radius, Math.Max(4, Math.Round(radius / BrushPerRoughness)),
            Math.Clamp(Math.Round(side * Stamp), 32, 4096), Area,
            Math.Clamp(Math.Round(side * River), 4, 4096), Math.Clamp(Math.Round(side * Road), 3, 4096));
    }
}
