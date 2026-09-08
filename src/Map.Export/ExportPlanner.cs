using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Export;

public static class ExportPlanner
{
    private const long AssetCacheBudget = 256L * 1024 * 1024;

    public static ExportPlan Create(
        MapDocument document,
        ExportRequest request,
        ExportEngineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        ValidateSettings(settings);

        var region = request.AreaMode switch
        {
            ExportAreaMode.WholeCanvas => new CanvasRegion(
                0, 0, document.Canvas.Width, document.Canvas.Height),
            ExportAreaMode.Region when request.Region is { } selected => selected,
            ExportAreaMode.Region => throw new ExportValidationException(
                "Задайте прямоугольный регион экспорта."),
            _ => throw new ExportValidationException("Неизвестная область экспорта."),
        };

        if (region.X < 0 || region.Y < 0
            || region.Right > document.Canvas.Width || region.Bottom > document.Canvas.Height)
        {
            throw new ExportValidationException(
                "Регион должен целиком находиться внутри фактической границы карты.");
        }

        if (request.OutputWidth <= 0 || request.OutputHeight <= 0)
        {
            throw new ExportValidationException("Ширина и высота результата должны быть больше нуля.");
        }

        if (request.OutputWidth > settings.MaxOutputEdge || request.OutputHeight > settings.MaxOutputEdge)
        {
            throw new ExportValidationException(
                $"Максимальный размер результата — {settings.MaxOutputEdge:N0} px по каждой стороне.");
        }

        if (request.OutputMode == ExportOutputMode.SingleImage
            && request.Format != ExportImageFormat.Png
            && (request.OutputWidth > settings.MaxSingleEdge || request.OutputHeight > settings.MaxSingleEdge))
        {
            throw new ExportValidationException(
                $"Один файл JPEG/WebP ограничен {settings.MaxSingleEdge:N0} px. "
                + "Выберите PNG для полного размера или «Пакет тайлов».");
        }

        if (request.Format == ExportImageFormat.Jpeg && request.TransparentBackground)
        {
            throw new ExportValidationException(
                "JPEG не поддерживает прозрачность. Фон карты должен быть включён.");
        }

        if (request.Format is ExportImageFormat.Jpeg or ExportImageFormat.Webp
            && request.Quality is < 1 or > 100)
        {
            throw new ExportValidationException("Качество JPEG/WebP должно быть от 1 до 100.");
        }

        try
        {
            var streamingPng = request.Format == ExportImageFormat.Png
                && request.OutputMode == ExportOutputMode.SingleImage;
            var tileHeight = streamingPng ? Math.Min(settings.TileEdge, 512) : settings.TileEdge;
            var columns = request.OutputMode == ExportOutputMode.TilePackage || streamingPng
                ? DivideRoundUp(request.OutputWidth, settings.TileEdge)
                : 1;
            var rows = request.OutputMode == ExportOutputMode.TilePackage || streamingPng
                ? DivideRoundUp(request.OutputHeight, tileHeight)
                : 1;
            _ = checked(columns * rows);

            var bleed = EffectBleedPixels(document, region, request.OutputWidth, request.OutputHeight);
            var surfaceWidth = checked(Math.Min(settings.TileEdge, request.OutputWidth) + (2 * bleed));
            var surfaceHeight = checked(Math.Min(tileHeight, request.OutputHeight) + (2 * bleed));

            if (surfaceWidth > settings.MaxTileSurfaceEdge || surfaceHeight > settings.MaxTileSurfaceEdge)
            {
                throw new ExportValidationException(
                    "Эффекты при выбранном масштабе требуют слишком большой рабочей поверхности. "
                    + "Уменьшите размер результата или эффекты blur/shadow.");
            }

            var surfaceBytes = checked((long)surfaceWidth * surfaceHeight * 4);
            var tileCacheBytes = checked((long)settings.TileCacheCapacity
                * TileMap.TileSize * TileMap.TileSize * 4);
            var singleOutputBytes = request.OutputMode == ExportOutputMode.SingleImage && !streamingPng
                ? checked((long)request.OutputWidth * request.OutputHeight * 8) : 0;
            var rowBuffers = streamingPng
                ? checked((long)request.OutputWidth * 8 + columns * 65536L + 1024 * 1024) : 0;
            var estimate = checked(AssetWorkingBytes(document) + tileCacheBytes + (surfaceBytes * 3)
                + singleOutputBytes + rowBuffers);

            return new ExportPlan(
                request.Format,
                region,
                request.OutputWidth,
                request.OutputHeight,
                request.OutputMode,
                request.TransparentBackground,
                request.IncludeGrid,
                request.Format == ExportImageFormat.Png ? 100 : request.Quality,
                settings.TileEdge,
                columns,
                rows,
                bleed,
                estimate);
        }
        catch (OverflowException ex)
        {
            throw new ExportValidationException(
                "Размер или оценка памяти вышли за поддерживаемый числовой диапазон.", ex);
        }
    }

    private static long AssetWorkingBytes(MapDocument document)
    {
        // A cache capacity is not an allocation. Inspect embedded headers without decoding or
        // copying payloads; small maps must not reserve a full 256 MiB cache they cannot fill.
        // Linked/unknown content retains the existing conservative bound.
        var bytes = 16L * 1024 * 1024;
        foreach (var asset in document.Assets)
        {
            if (asset.EmbeddedContent is not { } encoded) return AssetCacheBudget;
            using var stream = new MemoryStream(encoded, writable: false);
            using var codec = SKCodec.Create(stream);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0) return AssetCacheBudget;
            var pixels = (long)codec.Info.Width * codec.Info.Height;
            if (pixels >= AssetCacheBudget / 8 || encoded.LongLength >= AssetCacheBudget / 2) return AssetCacheBudget;
            bytes += pixels * 8 + encoded.LongLength * 2;
            if (bytes >= AssetCacheBudget) return AssetCacheBudget;
        }
        return bytes;
    }

    private static int EffectBleedPixels(
        MapDocument document, CanvasRegion region, int outputWidth, int outputHeight)
    {
        var world = 1.0;
        foreach (var layer in document.Layers.Where(layer => layer.Visible))
        {
            if (layer is LandLayer land)
            {
                var style = land.EdgeStyle;
                world = Math.Max(world, Math.Max(
                    Math.Max(style.OutlineWidth, style.RippleSpacing * style.RippleCount + style.RippleWidth),
                    style.ShadowBlur * 3));
            }

            if (layer is ObjectLayer objects)
            {
                foreach (var item in objects.Objects.Where(item => item.Visible))
                {
                    world = Math.Max(world, EffectBleedWorld(item));
                }

                // A river bank and its blurred halo reach past the water outline (Stage 19).
                foreach (var river in objects.Rivers)
                {
                    world = Math.Max(world, river.Style.BankWidth + (river.Style.BankShadowBlur * 3)
                        + river.Style.BankReliefWidth * 1.8 + 1);
                }
            }
        }

        if (!double.IsFinite(world) || world < 0)
        {
            throw new ExportValidationException("Параметры blur/shadow карты имеют недопустимое значение.");
        }

        var scale = Math.Max(outputWidth / (double)region.Width, outputHeight / (double)region.Height);
        var pixels = Math.Ceiling((world * scale) + 2);
        if (!double.IsFinite(pixels) || pixels > int.MaxValue)
        {
            throw new OverflowException("Effect bleed is outside the supported range.");
        }

        return Math.Max(1, (int)pixels);
    }

    private static double EffectBleedWorld(SceneObject item)
    {
        var result = item.StrokeWidth / 2;
        if (item.Stamp is { } stamp)
        {
            result = Math.Max(result, stamp.Blur * 3);
            if (stamp.ShadowOpacity > 0)
            {
                result = Math.Max(result,
                    Math.Max(Math.Abs(stamp.ShadowOffsetX), Math.Abs(stamp.ShadowOffsetY))
                    + (stamp.ShadowBlur * 3));
            }
        }

        if (item.Line?.Stroke is { } line)
        {
            result = Math.Max(result, (line.Width / 2) + Math.Abs(line.Offset) + (line.Blur * 3));
            if (line.ShadowOpacity > 0)
            {
                result = Math.Max(result,
                    Math.Max(Math.Abs(line.ShadowOffsetX), Math.Abs(line.ShadowOffsetY))
                    + (line.ShadowBlur * 3));
            }
        }

        if (item.Shape?.Stroke is { } shape)
        {
            result = Math.Max(result, (shape.Width / 2) + Math.Abs(shape.Offset) + (shape.Blur * 3));
            if (shape.ShadowOpacity > 0)
            {
                result = Math.Max(result,
                    Math.Max(Math.Abs(shape.ShadowOffsetX), Math.Abs(shape.ShadowOffsetY))
                    + (shape.ShadowBlur * 3));
            }
        }

        if (item.Text is { } text)
        {
            result = Math.Max(result, text.OutlineWidth + (text.Blur * 3));
            if (text.ShadowOpacity > 0)
            {
                result = Math.Max(result,
                    Math.Max(Math.Abs(text.ShadowOffsetX), Math.Abs(text.ShadowOffsetY))
                    + (text.ShadowBlur * 3));
            }
        }

        if (item.Composition is { } composition)
        {
            var childScale = Math.Max(
                item.Width / composition.SourceWidth,
                item.Height / composition.SourceHeight);
            foreach (var child in composition.Items.Where(child => child.Visible))
            {
                result = Math.Max(result, EffectBleedWorld(child) * childScale);
            }
        }

        return result;
    }

    private static int DivideRoundUp(int value, int divisor) =>
        checked((value + divisor - 1) / divisor);

    private static void ValidateSettings(ExportEngineSettings settings)
    {
        if (settings.TileEdge <= 0 || settings.TileCacheCapacity <= 0
            || settings.MaxSingleEdge <= 0 || settings.MaxOutputEdge <= 0
            || settings.MaxTileSurfaceEdge <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Export limits must be positive.");
        }
    }
}
