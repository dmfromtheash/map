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
    public const int DefaultMaxOutputEdge = CanvasDimensionPolicy.Maximum;

    public int TileEdge { get; init; } = DefaultTileEdge;
    public int TileCacheCapacity { get; init; } = 128;
    public int MaxSingleEdge { get; init; } = DefaultMaxSingleEdge;
    public int MaxOutputEdge { get; init; } = DefaultMaxOutputEdge;
    public int MaxTileSurfaceEdge { get; init; } = DefaultMaxSingleEdge;
    public Action<ExportSurfaceAllocation>? SurfaceAllocated { get; init; }
    public Action<ExportFaultPoint>? FaultInjector { get; init; }
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
