using System.Text.Json;
using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Export;

public sealed class ImageExportEngine : IImageExporter
{
    public const int PreviewMaxWidth = 960;
    public const int PreviewMaxHeight = 540;

    private readonly ExportEngineSettings _settings;

    public ImageExportEngine(ExportEngineSettings? settings = null)
    {
        _settings = settings ?? new ExportEngineSettings();
    }

    public ExportPlan Plan(MapDocument document, ExportRequest request) =>
        ExportPlanner.Create(document, request, _settings);

    public Task<ExportPreview> RenderPreviewAsync(
        MapDocument snapshot,
        ExportPlan plan,
        IStampImageSource? stamps,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = await Map.Core.Resources.WorkingMemoryBudget.Shared.EnterHeavyAsync(cancellationToken).ConfigureAwait(false);

        var scale = Math.Min(
            PreviewMaxWidth / (double)plan.OutputWidth,
            PreviewMaxHeight / (double)plan.OutputHeight);
        scale = Math.Min(1, scale);
        var width = Math.Max(1, (int)Math.Round(plan.OutputWidth * scale));
        var height = Math.Max(1, (int)Math.Round(plan.OutputHeight * scale));
        using var tileCache = new TileImageCache(_settings.TileCacheCapacity);
        using var bytes = RenderEncoded(
            snapshot,
            plan,
            new PixelRect(0, 0, width, height),
            width,
            height,
            bleed: 0,
            ExportImageFormat.Png,
            quality: 100,
            stamps,
            tileCache,
            ExportSurfacePurpose.Preview,
            tileIndex: null,
            cancellationToken);
        return new ExportPreview(bytes.ToArray(), width, height);
    }, cancellationToken);

    public Task<ExportResult> ExportAsync(
        MapDocument snapshot,
        ExportPlan plan,
        string destinationPath,
        IStampImageSource? stamps,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        ExportResourceAdmission.Check(plan, destinationPath, _settings);
        // The slot is still exclusive across windows, but the reservation is the size of THIS
        // export. A small map used to be refused because the gate always asked for the working set
        // of the largest one.
        var required = Math.Max(Map.Core.Resources.WorkingMemoryBudget.MiB, plan.EstimatedRawWorkingBytes);
        var leased = Math.Min(required, Map.Core.Resources.WorkingMemoryBudget.HeavyReserveBytes);
        using var operation = await Map.Core.Resources.WorkingMemoryBudget.Shared
            .EnterHeavyAsync(cancellationToken, leased).ConfigureAwait(false);
        if (!Map.Core.Resources.WorkingMemoryBudget.Shared.TryReserve(
            Math.Max(0, required - leased), out var memoryReservation, out var memoryReason, measured: true))
            throw new ExportValidationException(memoryReason!);
        using var heldMemory = memoryReservation;
        Report(progress, ExportPhase.Preparing, 0.01, "Подготовка снимка карты…");

        try
        {
            using var tileCache = new TileImageCache(_settings.TileCacheCapacity);
            return plan.OutputMode == ExportOutputMode.SingleImage
                ? await ExportSingleAsync(
                    snapshot, plan, destinationPath, stamps, tileCache, progress, cancellationToken)
                    .ConfigureAwait(false)
                : await ExportTilesAsync(
                    snapshot, plan, destinationPath, stamps, tileCache, progress, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ExportOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or OutOfMemoryException or InvalidOperationException
            or ArgumentException)
        {
            throw new ExportOperationException(
                "Экспорт не завершён. Карта и прежний результат сохранены. "
                + "Попробуйте меньший размер, пакет тайлов или другую папку.", ex);
        }
    }, cancellationToken);

    private async Task<ExportResult> ExportSingleAsync(
        MapDocument snapshot,
        ExportPlan plan,
        string destinationPath,
        IStampImageSource? stamps,
        TileImageCache tileCache,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        ValidateFileExtension(fullPath, plan.Format);
        if (plan.UsesStreamingPng)
            return ExportStreamingPng(snapshot, plan, fullPath, stamps, tileCache, progress, cancellationToken);
        Report(progress, ExportPhase.Rendering, 0.08, "Рендеринг изображения…");
        using var bytes = RenderSingleEncoded(snapshot, plan, stamps, tileCache, cancellationToken);
        _settings.FaultInjector?.Invoke(ExportFaultPoint.AfterRender);
        cancellationToken.ThrowIfCancellationRequested();

        Report(progress, ExportPhase.EncodingAndWriting, 0.76, "Проверка и запись файла…");
        var tempPath = UniqueSiblingFile(fullPath);
        try
        {
            await WriteAndValidateAsync(
                tempPath, bytes, plan.Format, plan.OutputWidth, plan.OutputHeight, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _settings.FaultInjector?.Invoke(ExportFaultPoint.BeforeFinalize);
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, ExportPhase.Finalizing, 0.96, "Завершение записи готового файла…");
            FinalizeFile(tempPath, fullPath);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        Report(progress, ExportPhase.Completed, 1, "Экспорт завершён.");
        return new ExportResult(fullPath, ExportOutputMode.SingleImage, 1);
    }

    private ExportResult ExportStreamingPng(
        MapDocument snapshot, ExportPlan plan, string fullPath, IStampImageSource? stamps,
        TileImageCache tileCache, IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        var tempPath = UniqueSiblingFile(fullPath);
        try
        {
            using (var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.SequentialScan))
            using (var png = new StreamingPngWriter(destination, plan.OutputWidth, plan.OutputHeight))
            {
                var row = new byte[checked(plan.OutputWidth * 4)];
                var tileIndex = 0;
                for (var y = 0; y < plan.OutputHeight; y += plan.RasterTileHeight)
                {
                    var height = Math.Min(plan.RasterTileHeight, plan.OutputHeight - y);
                    var band = new List<(FileStream Stream, int RowBytes)>();
                    try
                    {
                        for (var x = 0; x < plan.OutputWidth; x += plan.TileEdge)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            Report(progress, ExportPhase.Rendering,
                                .02 + .88 * (y + height * .7 * x / plan.OutputWidth) / plan.OutputHeight,
                                $"Отрисовка в полном разрешении: часть {tileIndex + 1:N0} из {plan.TileCount:N0}…");
                            var tile = new PixelRect(x, y, Math.Min(plan.TileEdge, plan.OutputWidth - x), height);
                            using var image = RenderImage(snapshot, plan, tile, plan.OutputWidth, plan.OutputHeight,
                                plan.EffectBleedPixels, stamps, tileCache, ExportSurfacePurpose.TileWithBleed,
                                tileIndex++, cancellationToken);
                            var info = new SKImageInfo(tile.Width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                            using var pixels = new SKBitmap(info);
                            if (!image.ReadPixels(info, pixels.GetPixels(), pixels.RowBytes, 0, 0))
                                throw new InvalidDataException("Не удалось прочитать пиксели PNG.");
                            // One tile-column file per band. Buffered sequential reads assemble rows;
                            // no full-width/full-height bitmap or memory-mapped full band is allocated.
                            var scratch = new FileStream(tempPath + $".{band.Count:D4}.rgba",
                                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
                                FileOptions.DeleteOnClose | FileOptions.SequentialScan);
                            band.Add((scratch, checked(tile.Width * 4)));
                            scratch.Write(pixels.GetPixelSpan());
                            scratch.Position = 0;
                        }
                        for (var line = 0; line < height; line++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var offset = 0;
                            foreach (var tile in band)
                            {
                                tile.Stream.ReadExactly(row.AsSpan(offset, tile.RowBytes));
                                offset += tile.RowBytes;
                            }
                            png.WriteRow(row);
                            if (line % 64 == 0 || line == height - 1)
                                Report(progress, ExportPhase.EncodingAndWriting,
                                    .02 + .88 * (y + height * .7 + (line + 1) * .3) / plan.OutputHeight,
                                    $"Запись PNG: {y + line + 1:N0} из {plan.OutputHeight:N0} строк…");
                        }
                        _settings.FaultInjector?.Invoke(ExportFaultPoint.AfterPngBand);
                    }
                    finally
                    {
                        foreach (var tile in band) tile.Stream.Dispose();
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                _settings.FaultInjector?.Invoke(ExportFaultPoint.AfterRender);
                cancellationToken.ThrowIfCancellationRequested();
                png.Complete();
                destination.Flush(flushToDisk: true);
            }
            Report(progress, ExportPhase.Finalizing, .92, "Проверка целостности записанного PNG…");
            StreamingPngWriter.ValidateFile(tempPath, plan.OutputWidth, plan.OutputHeight, cancellationToken);
            _settings.FaultInjector?.Invoke(ExportFaultPoint.BeforeFinalize);
            cancellationToken.ThrowIfCancellationRequested();
            FinalizeFile(tempPath, fullPath);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
        Report(progress, ExportPhase.Completed, 1, "PNG в выбранном разрешении сохранён.");
        return new ExportResult(fullPath, ExportOutputMode.SingleImage, 1);
    }

    private async Task<ExportResult> ExportTilesAsync(
        MapDocument snapshot,
        ExportPlan plan,
        string destinationPath,
        IStampImageSource? stamps,
        TileImageCache tileCache,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        {
            throw new ExportOperationException(
                "Папка результата уже существует. Выберите новое имя; существующие данные не изменены.");
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            throw new ExportOperationException(
                "Родительская папка результата не существует. Выберите другую папку.");
        }

        var tempPath = Path.Combine(parent, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var entries = new List<TilePackageEntry>(plan.TileCount);
        try
        {
            Directory.CreateDirectory(tempPath);
            var tileIndex = 0;
            for (var row = 0; row < plan.Rows; row++)
            {
                for (var column = 0; column < plan.Columns; column++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var x = checked(column * plan.TileEdge);
                    var y = checked(row * plan.TileEdge);
                    var width = Math.Min(plan.TileEdge, plan.OutputWidth - x);
                    var height = Math.Min(plan.TileEdge, plan.OutputHeight - y);
                    var tile = new PixelRect(x, y, width, height);
                    var start = 0.05 + (0.86 * tileIndex / plan.TileCount);
                    Report(progress, ExportPhase.Rendering, start,
                        $"Рендеринг тайла {tileIndex + 1} из {plan.TileCount}…");
                    using var bytes = RenderEncoded(
                        snapshot,
                        plan,
                        tile,
                        plan.OutputWidth,
                        plan.OutputHeight,
                        plan.EffectBleedPixels,
                        plan.Format,
                        plan.Quality,
                        stamps,
                        tileCache,
                        ExportSurfacePurpose.TileWithBleed,
                        tileIndex,
                        cancellationToken);

                    var fileName = $"tile-y{row:D4}-x{column:D4}.{Extension(plan.Format)}";
                    var tilePath = Path.Combine(tempPath, fileName);
                    Report(progress, ExportPhase.EncodingAndWriting, start + (0.42 / plan.TileCount),
                        $"Запись тайла {tileIndex + 1} из {plan.TileCount}…");
                    await WriteAndValidateAsync(
                        tilePath, bytes, plan.Format, width, height, cancellationToken)
                        .ConfigureAwait(false);
                    entries.Add(new TilePackageEntry(fileName, x, y, width, height));
                    _settings.FaultInjector?.Invoke(ExportFaultPoint.AfterTileWritten);
                    cancellationToken.ThrowIfCancellationRequested();
                    tileIndex++;
                    Report(progress, ExportPhase.EncodingAndWriting,
                        0.05 + (0.86 * tileIndex / plan.TileCount),
                        $"Готово тайлов: {tileIndex} из {plan.TileCount}.");
                }
            }

            var manifest = new TilePackageManifest(
                1,
                new TilePackageRegion(
                    plan.SourceRegion.X,
                    plan.SourceRegion.Y,
                    plan.SourceRegion.Width,
                    plan.SourceRegion.Height),
                plan.OutputWidth,
                plan.OutputHeight,
                plan.TileEdge,
                plan.TileEdge,
                plan.Columns,
                plan.Rows,
                FormatName(plan.Format),
                plan.Quality,
                plan.TransparentBackground,
                plan.IncludesBackground,
                plan.IncludeGrid,
                entries);
            var manifestPath = Path.Combine(tempPath, "manifest.json");
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                manifest,
                new JsonSerializerOptions { WriteIndented = true });
            await WriteDurableAsync(manifestPath, manifestBytes, cancellationToken).ConfigureAwait(false);
            ValidateManifest(manifestPath, plan);

            _settings.FaultInjector?.Invoke(ExportFaultPoint.BeforeFinalize);
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, ExportPhase.Finalizing, 0.97, "Завершение записи пакета тайлов…");
            Directory.Move(tempPath, fullPath);
        }
        catch
        {
            TryDeleteDirectory(tempPath);
            throw;
        }

        Report(progress, ExportPhase.Completed, 1, "Пакет тайлов готов.");
        return new ExportResult(fullPath, ExportOutputMode.TilePackage, plan.TileCount + 1);
    }

    private SKData RenderSingleEncoded(MapDocument snapshot, ExportPlan plan,
        IStampImageSource? stamps, TileImageCache tileCache, CancellationToken cancellationToken)
    {
        // Both outputs evaluate the same globally aligned tiles and effect margins. Only an
        // explicitly requested single file owns this final-sized encoder surface; packages do not.
        var info = new SKImageInfo(plan.OutputWidth, plan.OutputHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        _settings.SurfaceAllocated?.Invoke(new(ExportSurfacePurpose.SingleImage, info.Width, info.Height, null));
        using var output = SKSurface.Create(info) ?? throw new OutOfMemoryException("Не удалось создать поверхность итогового изображения.");
        output.Canvas.Clear(SKColors.Transparent);
        using var copy = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = false };
        var index = 0;
        for (var y = 0; y < plan.OutputHeight; y += plan.TileEdge)
        for (var x = 0; x < plan.OutputWidth; x += plan.TileEdge)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tile = new PixelRect(x, y, Math.Min(plan.TileEdge, plan.OutputWidth - x), Math.Min(plan.TileEdge, plan.OutputHeight - y));
            using var image = RenderImage(snapshot, plan, tile, plan.OutputWidth, plan.OutputHeight,
                plan.EffectBleedPixels, stamps, tileCache, ExportSurfacePurpose.TileWithBleed, index++, cancellationToken);
            output.Canvas.DrawImage(image, x, y, new SKSamplingOptions(SKFilterMode.Nearest), copy);
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var result = output.Snapshot();
        return Encode(result, plan.Format, plan.Quality);
    }

    private SKData RenderEncoded(MapDocument snapshot, ExportPlan plan, PixelRect pixelRect,
        int globalOutputWidth, int globalOutputHeight, int bleed, ExportImageFormat format, int quality,
        IStampImageSource? stamps, TileImageCache tileCache, ExportSurfacePurpose purpose,
        int? tileIndex, CancellationToken cancellationToken)
    {
        using var image = RenderImage(snapshot, plan, pixelRect, globalOutputWidth, globalOutputHeight,
            bleed, stamps, tileCache, purpose, tileIndex, cancellationToken);
        return Encode(image, format, quality);
    }

    private SKImage RenderImage(
        MapDocument snapshot,
        ExportPlan plan,
        PixelRect pixelRect,
        int globalOutputWidth,
        int globalOutputHeight,
        int bleed,
        IStampImageSource? stamps,
        TileImageCache tileCache,
        ExportSurfacePurpose purpose,
        int? tileIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var width = checked(pixelRect.Width + (2 * bleed));
        var height = checked(pixelRect.Height + (2 * bleed));
        _settings.SurfaceAllocated?.Invoke(new ExportSurfaceAllocation(purpose, width, height, tileIndex));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new OutOfMemoryException($"Не удалось создать поверхность {width}×{height} px.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var scaleX = globalOutputWidth / (double)plan.SourceRegion.Width;
        var scaleY = globalOutputHeight / (double)plan.SourceRegion.Height;
        var expandedLeft = pixelRect.X - bleed;
        var expandedTop = pixelRect.Y - bleed;
        var visible = new WorldBounds(
            plan.SourceRegion.X + (expandedLeft / scaleX),
            plan.SourceRegion.Y + (expandedTop / scaleY),
            plan.SourceRegion.X + ((expandedLeft + width) / scaleX),
            plan.SourceRegion.Y + ((expandedTop + height) / scaleY));

        canvas.Save();
        canvas.Translate(-expandedLeft, -expandedTop);
        canvas.Scale((float)scaleX, (float)scaleY);
        canvas.Translate(-plan.SourceRegion.X, -plan.SourceRegion.Y);
        SceneRenderer.Render(canvas, snapshot, new RenderOptions
        {
            IsExport = true,
            IncludeBaseColor = !plan.TransparentBackground,
            IncludeExportGrids = plan.IncludeGrid,
            VisibleWorld = visible,
            ViewScale = Math.Max(scaleX, scaleY),
            TileCache = tileCache,
            Stamps = stamps,
        });
        canvas.Restore();
        canvas.Flush();
        cancellationToken.ThrowIfCancellationRequested();

        if (bleed == 0)
        {
            return surface.Snapshot();
        }

        _settings.SurfaceAllocated?.Invoke(new ExportSurfaceAllocation(
            ExportSurfacePurpose.TileCrop, pixelRect.Width, pixelRect.Height, tileIndex));
        var cropInfo = new SKImageInfo(
            pixelRect.Width, pixelRect.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var crop = new SKBitmap(cropInfo);
        if (!surface.ReadPixels(cropInfo, crop.GetPixels(), crop.RowBytes, bleed, bleed))
        {
            throw new InvalidOperationException("Не удалось прочитать центральную область тайла.");
        }

        return SKImage.FromBitmap(crop);
    }

    private static SKData Encode(SKImage image, ExportImageFormat format, int quality)
    {
        var data = image.Encode(ToSkiaFormat(format), quality)
            ?? throw new InvalidOperationException($"Кодировщик {FormatName(format)} недоступен.");
        return data;
    }

    private static async Task WriteAndValidateAsync(
        string path,
        SKData bytes,
        ExportImageFormat format,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        await WriteDurableAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var data = SKData.Create(path)
            ?? throw new InvalidDataException("Не удалось повторно прочитать экспортированный файл.");
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("Экспортированный файл не декодируется.");
        if (codec.EncodedFormat != ToSkiaFormat(format)
            || codec.Info.Width != width || codec.Info.Height != height)
        {
            throw new InvalidDataException(
                "Read-back экспортированного файла не совпал с выбранным форматом или размером.");
        }
    }

    private static async Task WriteDurableAsync(string path, SKData bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var source = bytes.AsStream();
        await source.CopyToAsync(stream, 64 * 1024, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task WriteDurableAsync(
        string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void ValidateManifest(string path, ExportPlan plan)
    {
        var manifest = JsonSerializer.Deserialize<TilePackageManifest>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("Manifest пакета не декодируется.");
        if (manifest.SchemaVersion != 1
            || manifest.OutputWidth != plan.OutputWidth
            || manifest.OutputHeight != plan.OutputHeight
            || manifest.Columns != plan.Columns
            || manifest.Rows != plan.Rows
            || manifest.Tiles.Count != plan.TileCount)
        {
            throw new InvalidDataException("Read-back manifest пакета не совпал с планом экспорта.");
        }
    }

    private static string UniqueSiblingFile(string destination)
    {
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new ExportOperationException(
                "Папка результата не существует. Выберите другую папку.");
        }

        return Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp.{ExtensionFromPath(destination)}");
    }

    private static void FinalizeFile(string tempPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(tempPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, destinationPath);
        }
    }

    private static void ValidateFileExtension(string path, ExportImageFormat format)
    {
        var extension = Path.GetExtension(path);
        var valid = format switch
        {
            ExportImageFormat.Png => extension.Equals(".png", StringComparison.OrdinalIgnoreCase),
            ExportImageFormat.Jpeg => extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase),
            ExportImageFormat.Webp => extension.Equals(".webp", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (!valid)
        {
            throw new ExportValidationException(
                $"Расширение файла должно соответствовать формату {FormatName(format)}.");
        }
    }

    public static string Extension(ExportImageFormat format) => format switch
    {
        ExportImageFormat.Png => "png",
        ExportImageFormat.Jpeg => "jpg",
        ExportImageFormat.Webp => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string FormatName(ExportImageFormat format) => format switch
    {
        ExportImageFormat.Png => "PNG",
        ExportImageFormat.Jpeg => "JPEG",
        ExportImageFormat.Webp => "WebP",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static SKEncodedImageFormat ToSkiaFormat(ExportImageFormat format) => format switch
    {
        ExportImageFormat.Png => SKEncodedImageFormat.Png,
        ExportImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
        ExportImageFormat.Webp => SKEncodedImageFormat.Webp,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static string ExtensionFromPath(string path) =>
        Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    private static void Report(
        IProgress<ExportProgress>? progress, ExportPhase phase, double fraction, string message)
    {
        try
        {
            progress?.Report(new ExportProgress(phase, Math.Clamp(fraction, 0, 1), message));
        }
        catch
        {
            // UI progress is observational. It can never turn a valid completed file into failure.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private readonly record struct PixelRect(int X, int Y, int Width, int Height);
}
