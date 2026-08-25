namespace Map.Spike.Core.Documents;

/// <summary>
/// Persistence-facing factory. Kept separate so <see cref="SpikeDocument"/> does not expose a
/// public constructor that could rebuild a document with an arbitrary revision from UI code.
/// </summary>
public static class DocumentRehydrator
{
    public static SpikeDocument Rehydrate(
        Guid documentId,
        string title,
        int canvasWidth,
        int canvasHeight,
        int schemaVersion,
        DateTimeOffset createdUtc,
        DateTimeOffset modifiedUtc,
        long revision,
        ObjectLayer objects,
        RasterLayer brush,
        RasterLayer decor,
        MaskLayer mask) =>
        SpikeDocument.Rehydrate(
            documentId, title, canvasWidth, canvasHeight, schemaVersion,
            createdUtc, modifiedUtc, revision, objects, brush, decor, mask);
}
