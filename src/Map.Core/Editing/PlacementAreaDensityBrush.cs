using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>What one dab of the area density brush does (R10).</summary>
public enum PlacementAreaDensityBrushMode
{
    /// <summary>«Гуще»: toward twice the area's density.</summary>
    Thicker,

    /// <summary>«Реже»: toward an empty place.</summary>
    Thinner,

    /// <summary>«Как было»: back to the area's own density.</summary>
    Reset,
}

/// <summary>
/// Paints the 8-bit density raster of an area (R10). The raster covers the area's bounds with a
/// small margin, at most <see cref="PlacementAreaDensityMask.MaxSide"/> cells a side; a dab has a
/// smooth falloff to its edge, like the analogues' density brushes (Blender, Cinema 4D).
/// </summary>
public static class PlacementAreaDensityBrush
{
    /// <summary>A working copy of the area's raster that covers its current boundary.</summary>
    public static (double Left, double Top, double CellSize, int Width, int Height, byte[] Values) Begin(PlacementArea area)
    {
        ArgumentNullException.ThrowIfNull(area);
        var left = area.Nodes.Min(n => n.X); var top = area.Nodes.Min(n => n.Y);
        var right = area.Nodes.Max(n => n.X); var bottom = area.Nodes.Max(n => n.Y);
        var mask = area.DensityMask;
        if (mask is not null && mask.Left <= left && mask.Top <= top && mask.Right >= right && mask.Bottom >= bottom)
            return (mask.Left, mask.Top, mask.CellSize, mask.Width, mask.Height, mask.CopyValues());
        if (mask is not null)
        {
            // The boundary grew past the raster: cover both, keeping the cell size where it fits.
            left = Math.Min(left, mask.Left); top = Math.Min(top, mask.Top);
            right = Math.Max(right, mask.Right); bottom = Math.Max(bottom, mask.Bottom);
        }
        var side = Math.Max(right - left, bottom - top);
        var cell = Math.Max(mask?.CellSize ?? 8, Math.Ceiling((side + 32) / (PlacementAreaDensityMask.MaxSide - 4)));
        left = Math.Floor((left - cell * 2) / cell) * cell; top = Math.Floor((top - cell * 2) / cell) * cell;
        var width = Math.Min(PlacementAreaDensityMask.MaxSide, (int)Math.Ceiling((right + cell * 2 - left) / cell));
        var height = Math.Min(PlacementAreaDensityMask.MaxSide, (int)Math.Ceiling((bottom + cell * 2 - top) / cell));
        var values = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                values[y * width + x] = mask is null ? PlacementAreaDensityMask.Neutral
                    : (byte)Math.Round(Math.Clamp(mask.Factor(left + (x + .5) * cell, top + (y + .5) * cell), 0, 255d / 128) * 128);
        return (left, top, cell, width, height, values);
    }

    /// <summary>
    /// One dab at a world point. <paramref name="strength"/> 0..1 is how far one dab moves a value
    /// toward the target at the centre; the effect falls off smoothly to zero at the radius.
    /// Returns the touched world rectangle, or null when the dab missed the raster.
    /// </summary>
    public static WorldBounds? Dab(double left, double top, double cellSize, int width, int height, byte[] values,
        double x, double y, double radius, double strength, PlacementAreaDensityBrushMode mode)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(radius) || radius <= 0 || strength <= 0) return null;
        var target = mode switch
        {
            PlacementAreaDensityBrushMode.Thicker => 255,
            PlacementAreaDensityBrushMode.Thinner => 0,
            _ => (int)PlacementAreaDensityMask.Neutral,
        };
        var x0 = Math.Max(0, (int)Math.Floor((x - radius - left) / cellSize));
        var x1 = Math.Min(width - 1, (int)Math.Floor((x + radius - left) / cellSize));
        var y0 = Math.Max(0, (int)Math.Floor((y - radius - top) / cellSize));
        var y1 = Math.Min(height - 1, (int)Math.Floor((y + radius - top) / cellSize));
        if (x0 > x1 || y0 > y1) return null;
        var amount = Math.Clamp(strength, 0, 1);
        for (var cy = y0; cy <= y1; cy++)
        for (var cx = x0; cx <= x1; cx++)
        {
            var dx = left + (cx + .5) * cellSize - x; var dy = top + (cy + .5) * cellSize - y;
            var d = Math.Sqrt(dx * dx + dy * dy) / radius;
            if (d >= 1) continue;
            var falloff = 1 - d * d * (3 - 2 * d);
            var index = cy * width + cx; var value = values[index];
            var next = (int)Math.Round(value + (target - value) * amount * falloff);
            // A weak dab still makes progress toward the target instead of rounding back.
            if (next == value && value != target && amount * falloff > .05) next += Math.Sign(target - value);
            values[index] = (byte)Math.Clamp(next, 0, 255);
        }
        return new WorldBounds(left + x0 * cellSize, top + y0 * cellSize, left + (x1 + 1) * cellSize, top + (y1 + 1) * cellSize);
    }

    /// <summary>The raster to store: null when every value is neutral (nothing painted, nothing saved).</summary>
    public static PlacementAreaDensityMask? Finish(double left, double top, double cellSize, int width, int height, byte[] values) =>
        values.All(v => v == PlacementAreaDensityMask.Neutral) ? null
        : PlacementAreaDensityMask.Create(left, top, cellSize, width, height, values);
}
