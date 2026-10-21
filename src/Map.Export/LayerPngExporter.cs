using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Rendering;

namespace Map.Export;

public sealed record LayerPngResult(string Path, int Files, int Width, int Height);

/// <summary>
/// One PNG per selected visible layer, in document order. Every plan retains the same source
/// rectangle and output dimensions; no layer is cropped to its content. Renders sequentially
/// through the existing streaming image engine, then publishes a new folder atomically.
/// </summary>
public static class LayerPngExporter
{
    public static async Task<LayerPngResult> WriteAsync(IImageExporter exporter, MapDocument snapshot,
        ExportRequest request, string path, IStampImageSource? stamps = null,
        IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var pngRequest = request with { Format = ExportImageFormat.Png, OutputMode = ExportOutputMode.SingleImage };
        var common = exporter.Plan(snapshot, pngRequest);
        var scene = common.CreateScene(snapshot);
        var layers = scene.Layers.ToArray();
        if (layers.Length == 0) throw new ExportValidationException("Нет видимых слоёв для экспорта.");
        var fullPath = System.IO.Path.GetFullPath(path);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new IOException("Папка результата уже существует. Выберите новое имя; существующие данные не изменены.");
        var parent = System.IO.Path.GetDirectoryName(fullPath);
        if (parent is null || !Directory.Exists(parent)) throw new DirectoryNotFoundException("Родительская папка не существует.");
        var temporary = System.IO.Path.Combine(parent, ".layers-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(temporary);
            for (var index = 0; index < layers.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var layer = layers[index];
                var plan = exporter.Plan(snapshot, pngRequest with
                {
                    VisibleLayerIds = ImmutableHashSet.Create(layer.Id),
                    // The base colour has its own file. Other layers retain their alpha channel.
                    TransparentBackground = layer is not BaseColorLayer,
                });
                var filename = $"{index + 1:D3}-{SafeLayerName(layer.Name)}.png";
                progress?.Report(new(ExportPhase.Rendering, index / (double)layers.Length, $"Слой {index + 1}/{layers.Length}: {layer.Name}"));
                await exporter.ExportAsync(snapshot, plan, System.IO.Path.Combine(temporary, filename), stamps,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporary, fullPath);
            progress?.Report(new(ExportPhase.Completed, 1, "Слои сохранены."));
            return new(fullPath, layers.Length, common.OutputWidth, common.OutputHeight);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    private static string SafeLayerName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => char.IsControl(c) || invalid.Contains(c) ? '_' : c).Take(100).ToArray()).Trim().TrimEnd('.');
        return safe.Length == 0 ? "Слой" : safe;
    }
}
