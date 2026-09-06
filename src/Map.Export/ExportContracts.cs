using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;

namespace Map.Export;

public enum ExportImageFormat
{
    Png,
    Jpeg,
    Webp,
}

public enum ExportAreaMode
{
    WholeCanvas,
    Region,
}

public enum ExportOutputMode
{
    SingleImage,
    TilePackage,
}

public enum ExportPhase
{
    Preparing,
    Rendering,
    EncodingAndWriting,
    Finalizing,
    Completed,
}

public enum ExportSurfacePurpose
{
    Preview,
    SingleImage,
    TileWithBleed,
    TileCrop,
}

public enum ExportFaultPoint
{
    AfterRender,
    AfterTileWritten,
    BeforeFinalize,
    AfterPngBand,
}

public sealed record ExportRequest(
    ExportImageFormat Format,
    ExportAreaMode AreaMode,
    CanvasRegion? Region,
    int OutputWidth,
    int OutputHeight,
    ExportOutputMode OutputMode,
    bool TransparentBackground,
    bool IncludeGrid,
    int Quality = 90);

public sealed record ExportPlan(
    ExportImageFormat Format,
    CanvasRegion SourceRegion,
    int OutputWidth,
    int OutputHeight,
    ExportOutputMode OutputMode,
    bool TransparentBackground,
    bool IncludeGrid,
    int Quality,
    int TileEdge,
    int Columns,
    int Rows,
    int EffectBleedPixels,
    long EstimatedRawWorkingBytes)
{
    public int TileCount => checked(Columns * Rows);
    public bool IncludesBackground => !TransparentBackground;
    public bool UsesStreamingPng => Format == ExportImageFormat.Png && OutputMode == ExportOutputMode.SingleImage;
    public int RasterTileHeight => UsesStreamingPng ? Math.Min(TileEdge, 512) : TileEdge;

    // A complete sibling result and one temporary row of raw tiles coexist until publication.
    // This is a conservative incompressible-file bound, not a prediction of the compressed size.
    public long RequiredFreeDiskBytes
    {
        get
        {
            var raw = checked((long)OutputWidth * OutputHeight * 4 + OutputHeight);
            return UsesStreamingPng
                ? checked(raw + raw / 1000 + (raw / 65536 + 1) * 12
                    + (long)OutputWidth * Math.Min(RasterTileHeight, OutputHeight) * 4 + 16L * 1024 * 1024)
                : checked((long)OutputWidth * OutputHeight * 8 + 16L * 1024 * 1024);
        }
    }
}

public sealed record ExportProgress(ExportPhase Phase, double Fraction, string Message);

public sealed record ExportPreview(byte[] PngBytes, int Width, int Height);

public sealed record ExportResult(string DestinationPath, ExportOutputMode OutputMode, int FileCount);

public sealed record ExportSurfaceAllocation(
    ExportSurfacePurpose Purpose, int Width, int Height, int? TileIndex = null);

public sealed class ExportValidationException : Exception
{
    public ExportValidationException(string message)
        : base(message)
    {
    }

    public ExportValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ExportOperationException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class ExportEngineSettings
{
    public const int DefaultTileEdge = 2_048;
    public const int DefaultMaxSingleEdge = 16_384;
    // The largest document can be re-rendered at the existing maximum export scale of 800%.
    // Resource admission still checks the actual output and scratch space before any rendering.
    public const int DefaultMaxOutputEdge = CanvasDimensionPolicy.Maximum * 8;

    public int TileEdge { get; init; } = DefaultTileEdge;
    public int TileCacheCapacity { get; init; } = 128;
    public int MaxSingleEdge { get; init; } = DefaultMaxSingleEdge;
    public int MaxOutputEdge { get; init; } = DefaultMaxOutputEdge;
    public int MaxTileSurfaceEdge { get; init; } = DefaultMaxSingleEdge;
    public Action<ExportSurfaceAllocation>? SurfaceAllocated { get; init; }
    public Action<ExportFaultPoint>? FaultInjector { get; init; }
    public Func<long> AvailableMemoryBytes { get; init; } = ExportResourceAdmission.AvailableMemory;
    public Func<string, long> AvailableDiskBytes { get; init; } = path =>
        new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
}

public interface IImageExporter
{
    ExportPlan Plan(MapDocument document, ExportRequest request);

    Task<ExportPreview> RenderPreviewAsync(
        MapDocument snapshot,
        ExportPlan plan,
        IStampImageSource? stamps,
        CancellationToken cancellationToken = default);

    Task<ExportResult> ExportAsync(
        MapDocument snapshot,
        ExportPlan plan,
        string destinationPath,
        IStampImageSource? stamps,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
