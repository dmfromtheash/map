namespace Map.Core.Editing;

/// <summary>
/// A fixed, map-wide field of irregular cells for rough land edges. The plane is divided into a
/// grid of <c>cellSize</c> squares, each holding one centre point ("site") jittered by a hash of
/// the square's coordinates; every world point belongs to the cell of its nearest site (a
/// Voronoi partition of jittered grid points). The field depends only on position and cell
/// size - never on a stroke, a seed or a document - so two dabs, two strokes or two sessions
/// that touch the same coast agree on the very same cells and join without a seam.
/// </summary>
public static class RoughCells
{
    /// <summary>How far, in cell sizes, a cell can extend beyond its own centre.</summary>
    /// <remarks>
    /// A site lies within <see cref="Jitter"/>/2 of its square's centre, so the nearest site of
    /// any point is at most about 1.3 cell sizes away; 1.5 is the safe footprint.
    /// </remarks>
    public const double Reach = 1.5;

    /// <summary>Fraction of the square the site may move away from its centre.</summary>
    private const double Jitter = 0.85;

    /// <summary>The centre of the cell the world point belongs to.</summary>
    public static (double X, double Y) NearestSite(double worldX, double worldY, double cellSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSize);
        var cellX = (int)Math.Floor(worldX / cellSize);
        var cellY = (int)Math.Floor(worldY / cellSize);
        var bestDistance = double.MaxValue;
        double bestX = worldX, bestY = worldY;
        for (var j = cellY - 1; j <= cellY + 1; j++)
        {
            for (var i = cellX - 1; i <= cellX + 1; i++)
            {
                var (siteX, siteY) = Site(i, j, cellSize);
                var dx = siteX - worldX;
                var dy = siteY - worldY;
                var distance = (dx * dx) + (dy * dy);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestX = siteX;
                    bestY = siteY;
                }
            }
        }

        return (bestX, bestY);
    }

    /// <summary>True when the world point lies in a cell whose centre is inside the circle.</summary>
    public static bool InsideRoughCircle(double worldX, double worldY,
        double centerX, double centerY, double radius, double cellSize)
    {
        var (siteX, siteY) = NearestSite(worldX, worldY, cellSize);
        var dx = siteX - centerX;
        var dy = siteY - centerY;
        return (dx * dx) + (dy * dy) <= radius * radius;
    }

    /// <summary>The site of one grid square: its centre moved by a hash of the square's coordinates.</summary>
    public static (double X, double Y) Site(int cellX, int cellY, double cellSize)
    {
        var hash = Hash(cellX, cellY);
        var offsetX = (((hash & 0xFFFF) / 65535.0) - 0.5) * Jitter;
        var offsetY = ((((hash >> 16) & 0xFFFF) / 65535.0) - 0.5) * Jitter;
        return ((cellX + 0.5 + offsetX) * cellSize, (cellY + 0.5 + offsetY) * cellSize);
    }

    /// <summary>A well-mixed 32-bit hash of two integers; the same on every machine and run.</summary>
    private static uint Hash(int x, int y)
    {
        var h = unchecked((uint)x * 0x8DA6B343u) ^ unchecked((uint)y * 0xD8163841u);
        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;
        return h;
    }
}
