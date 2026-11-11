using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Export;

public sealed record HandoutResult(string Path, int Width, int Height);

/// <summary>
/// The map as a handout (R09f): the whole canvas rendered through the normal image export at
/// <see cref="HandoutSheet.MapLongSide"/> on the long side, then laid into the map's handout sheet. The result is written
/// next to the destination first and moved over it at the end, so a failure leaves no half-written file.
/// </summary>
public static class HandoutExporter
{
    public static async Task<HandoutResult> WriteAsync(IImageExporter exporter, MapDocument snapshot, MapHandout handout,
        string path, IStampImageSource? stamps = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (handout == MapHandout.None) throw new ExportValidationException("У карты нет хендаута.");
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (parent is null || !Directory.Exists(parent)) throw new DirectoryNotFoundException("Папка для хендаута не существует.");

        // A sheet drawn by Sol takes the map on white paper, without the sea's tint: only the strokes soak in.
        var document = HandoutSheet.NeedsPaperMap(handout)
            ? snapshot.WithLayers([.. snapshot.Layers.Select(l => l is BaseColorLayer page ? page with { Color = "#FFFFFFFF", Sea = null } : l)])
            : snapshot;
        var (width, height) = HandoutSheet.MapSize(document.Canvas.Width, document.Canvas.Height);
        var request = new ExportRequest(ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null, width, height,
            ExportOutputMode.SingleImage, TransparentBackground: false, IncludeGrid: false);
        var plan = exporter.Plan(document, request);
        var mapFile = Path.Combine(parent, ".handout-map-" + Guid.NewGuid().ToString("N") + ".png");
        var sheetFile = Path.Combine(parent, ".handout-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await exporter.ExportAsync(document, plan, mapFile, stamps, cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var (w, h) = await Task.Run(() =>
            {
                using var bitmap = SKBitmap.Decode(mapFile) ?? throw new ExportOperationException("Карта для хендаута не прочиталась.");
                using var map = SKImage.FromBitmap(bitmap);
                using var sheet = HandoutSheet.Compose(handout, map);
                var jpeg = Path.GetExtension(fullPath).ToLowerInvariant() is ".jpg" or ".jpeg";
                using var data = sheet.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, jpeg ? 92 : 100)
                    ?? throw new ExportOperationException("Хендаут не закодировался.");
                using (var file = File.Create(sheetFile)) data.SaveTo(file);
                return (sheet.Width, sheet.Height);
            }, cancellationToken).ConfigureAwait(false);
            File.Move(sheetFile, fullPath, overwrite: true);
            return new HandoutResult(fullPath, w, h);
        }
        finally
        {
            if (File.Exists(mapFile)) File.Delete(mapFile);
            if (File.Exists(sheetFile)) File.Delete(sheetFile);
        }
    }
}
