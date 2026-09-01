using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Persistence;

/// <summary>
/// A local composition package reuses the admitted Map ZIP container: manifest checksums,
/// bounded entries, asset SHA identity, provenance and atomic publication therefore have one
/// implementation instead of a weaker parallel archive format.
/// </summary>
public static class CompositionPackageStore
{
    public static void Export(
        CompositionPackage package,
        string targetPath,
        SaveMetadata metadata,
        byte[] previewPng)
    {
        ArgumentNullException.ThrowIfNull(package);
        var composition = package.Composition;
        var width = Math.Clamp((int)Math.Ceiling(composition.SourceWidth), 16, CanvasDimensionPolicy.Maximum);
        var height = Math.Clamp((int)Math.Ceiling(composition.SourceHeight), 16, CanvasDimensionPolicy.Maximum);
        var instance = CompositionTransfer.CreateInstance(package, width / 2d, height / 2d);
        var document = new MapDocument(
            DocumentId.New(), composition.Name, metadata.ModifiedUtc,
            new CanvasDefinition(width, height, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "Фон package", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(LayerId.New(), "Композиция", true, false, 1, [instance]),
            ],
            package.Assets);
        MapProjectStore.Save(document, targetPath, metadata, previewPng);
    }

    public static CompositionPackage Import(string sourcePath)
    {
        var opened = MapProjectStore.Open(sourcePath);
        var objects = opened.Document.Layers.OfType<ObjectLayer>()
            .SelectMany(layer => layer.Objects).ToList();
        if (objects.Count != 1 || objects[0].Composition is not { } composition)
            throw new MapProjectCorruptedException(
                "Файл не содержит ровно одну редактируемую композицию.");
        return new CompositionPackage(composition, opened.Document.Assets);
    }
}
