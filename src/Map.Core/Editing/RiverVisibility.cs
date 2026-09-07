using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// Why a river or lake is not on screen, decided from the document alone. The renderer clips
/// water to the land coverage when <see cref="RiverStyle.ClipToLand"/> is set, which is what
/// makes a river end exactly at the coast. The same rule silently swallows a whole lake drawn
/// over the sea, so creation asks this first and says what happened instead of reporting success
/// for an invisible object.
/// </summary>
public static class RiverVisibility
{
    /// <summary>True when the map has land coverage the water could be clipped against.</summary>
    public static bool HasLandCoverage(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Layers.OfType<LandLayer>().Any(l => l.Visible && l.Opacity > 0 && l.GeometryTiles.TileCount > 0);
    }

    /// <summary>Land coverage at a world point, 0..255, across every visible Land layer.</summary>
    public static byte LandCoverageAt(MapDocument document, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!double.IsFinite(x) || !double.IsFinite(y)) return 0;
        var px = (int)Math.Floor(x); var py = (int)Math.Floor(y);
        byte best = 0;
        foreach (var land in document.Layers.OfType<LandLayer>().Where(l => l.Visible && l.Opacity > 0))
        {
            var key = new TileKey((int)Math.Floor(px / (double)TileMap.TileSize), (int)Math.Floor(py / (double)TileMap.TileSize));
            if (land.GeometryTiles.TryGet(key) is not { } tile) continue;
            var ox = px - key.X * TileMap.TileSize; var oy = py - key.Y * TileMap.TileSize;
            if (ox < 0 || oy < 0 || ox >= TileMap.TileSize || oy >= TileMap.TileSize) continue;
            var value = tile[oy * TileMap.TileSize + ox];
            if (value > best) best = value;
        }
        return best;
    }

    /// <summary>
    /// True when this river would draw nothing at all: it clips to land, the map has land, and no
    /// sampled point of any channel lies over coverage. Sampling walks the evaluated centreline,
    /// which is the same geometry the renderer fills, plus the interior of every lake.
    /// </summary>
    public static bool IsHiddenByLandClip(MapDocument document, River river)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(river);
        if (!river.Style.ClipToLand || !HasLandCoverage(document)) return false;
        var set = RiverGeometry.Evaluate(river);
        foreach (var geometry in set.Channels.Values)
        {
            foreach (var sample in geometry.Samples)
            {
                if (LandCoverageAt(document, sample.X, sample.Y) > 0) return false;
                // A wide channel can straddle a coast the centreline misses; probe both banks.
                var nx = -sample.TangentY * sample.HalfWidth; var ny = sample.TangentX * sample.HalfWidth;
                if (LandCoverageAt(document, sample.X + nx, sample.Y + ny) > 0) return false;
                if (LandCoverageAt(document, sample.X - nx, sample.Y - ny) > 0) return false;
            }
            if (!geometry.Closed) continue;
            var bounds = Bounds(geometry);
            for (var step = 1; step <= 8; step++)
                for (var other = 1; other <= 8; other++)
                {
                    var x = bounds.Left + (bounds.Right - bounds.Left) * step / 9.0;
                    var y = bounds.Top + (bounds.Bottom - bounds.Top) * other / 9.0;
                    if (RiverGeometry.InsideLake(geometry, x, y) && LandCoverageAt(document, x, y) > 0) return false;
                }
        }
        return true;
    }

    private static WorldBounds Bounds(RiverChannelGeometry geometry)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var sample in geometry.Samples)
        {
            left = Math.Min(left, sample.X); right = Math.Max(right, sample.X);
            top = Math.Min(top, sample.Y); bottom = Math.Max(bottom, sample.Y);
        }
        return new WorldBounds(left, top, right, bottom);
    }
}
