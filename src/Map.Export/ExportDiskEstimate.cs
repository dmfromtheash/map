namespace Map.Export;

/// <summary>Peak additional bytes; the existing destination is never counted as reusable space.</summary>
internal sealed record ExportDiskEstimate(long ResultBytes, long PngBytes, long PyramidBytes,
    long RasterBytes, bool HasPyramid)
{
    internal const long ReserveBytes = 64L * 1024 * 1024;

    public long CombinedBytes => checked((HasPyramid
        ? Math.Max(PngBytes + RasterBytes, PngBytes + PyramidBytes + ResultBytes)
        : ResultBytes + RasterBytes) + ReserveBytes);
    public long DestinationBytes => checked(ResultBytes + ReserveBytes);
    public long TemporaryBytes => checked((HasPyramid
        ? PngBytes + Math.Max(RasterBytes, PyramidBytes) : RasterBytes) + ReserveBytes);

    public static ExportDiskEstimate Create(ExportPlan plan, bool incompressible = false)
    {
        var pixels = checked((long)plan.OutputWidth * plan.OutputHeight);
        var png = CompressedBytes(checked(pixels * (plan.ExpectsOpaquePng ? 3 : 4) + plan.OutputHeight), incompressible);
        var raster = checked((long)plan.OutputWidth * Math.Min(plan.RasterTileHeight, plan.OutputHeight) * 4);
        var pyramidMode = plan.OutputMode is ExportOutputMode.MbTiles or ExportOutputMode.ViewerFolder;
        if (!pyramidMode) return new(png, png, 0, raster, false);

        long pyramid = 0, tiles = 0;
        var width = plan.OutputWidth;
        var height = plan.OutputHeight;
        while (true)
        {
            var edge = PngTilePyramid.Edge;
            var count = checked(((width + edge - 1L) / edge) * ((height + edge - 1L) / edge));
            tiles += count;
            // LevelEncoder always writes RGBA, including the opaque route. Count odd/thin
            // levels exactly (4/3 is only the limiting ratio for a large square).
            pyramid += checked(CompressedBytes((long)width * height * 4 + height, incompressible) + count * 4096);
            if (Math.Max(width, height) <= edge) break;
            width = (width + 1) / 2;
            height = (height + 1) / 2;
        }
        // Do not depend on WebP savings or deduplication: PNG fallback is lossless too.
        // Allow SQLite pages/indexes, file allocation slack and the viewer page.
        var result = checked(pyramid + pyramid / 10 + tiles * 4096);
        return new(result, png, pyramid, raster, true);
    }

    private static long CompressedBytes(long raw, bool incompressible)
    {
        // Admission budgets half the RGB/RGBA scanline bytes: 1.5/2 bytes per pixel.
        // Owner measurements: RGB 0.61 B/px at 131072² and 1.06 B/px at 16384²;
        // 1.5 B/px leaves at least 41% over those samples, not a guarantee for noise.
        // Keep a separate incompressible ceiling (deflate expansion + IDAT framing).
        // Estimates never change compression or pixels; a full disk aborts and cleans scratch.
        var bytes = incompressible ? raw : (raw + 1) / 2;
        return checked(bytes + bytes / 1000 + (bytes / 65536 + 1) * 12 + 64);
    }
}
