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
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        var scale = Math.Min(
            PreviewMaxWidth / (double)plan.OutputWidth,
            PreviewMaxHeight / (double)plan.OutputHeight);
        scale = Math.Min(1, scale);
        var width = Math.Max(1, (int)Math.Round(plan.OutputWidth * scale));
        var height = Math.Max(1, (int)Math.Round(plan.OutputHeight * scale));
        using var tileCache = new TileImageCache(_settings.TileCacheCapacity);
        var bytes = RenderEncoded(
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
        return new ExportPreview(bytes, width, height);
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
        Report(progress, ExportPhase.Rendering, 0.08, "Рендеринг изображения…");
        var bytes = RenderEncoded(
            snapshot,
            plan,
            new PixelRect(0, 0, plan.OutputWidth, plan.OutputHeight),
            plan.OutputWidth,
            plan.OutputHeight,
            bleed: 0,
            plan.Format,
            plan.Quality,
            stamps,
            tileCache,
            ExportSurfacePurpose.SingleImage,
            tileIndex: null,
            cancellationToken);
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
                    var bytes = RenderEncoded(
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

    private byte[] RenderEncoded(
        MapDocument snapshot,
        ExportPlan plan,
        PixelRect pixelRect,
        int globalOutputWidth,
        int globalOutputHeight,
        int bleed,
        ExportImageFormat format,
        int quality,
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
            using var image = surface.Snapshot();
            return Encode(image, format, quality);
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

        using var croppedImage = SKImage.FromBitmap(crop);
        return Encode(croppedImage, format, quality);
    }

    private static byte[] Encode(SKImage image, ExportImageFormat format, int quality)
    {
        using var data = image.Encode(ToSkiaFormat(format), quality)
            ?? throw new InvalidOperationException($"Кодировщик {FormatName(format)} недоступен.");
        return data.ToArray();
    }

    private static async Task WriteAndValidateAsync(
        string path,
        byte[] bytes,
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
