using Map.Spike.Core.Geometry;

namespace Map.Spike.Core.Documents;

/// <summary>
/// Minimal versioned spike document. NOT the .mapproj v1 model from docs/05 - that is out of
/// Stage 1 scope. It only carries what the spike must prove: objects, one brush raster layer,
/// one decor raster layer (to show mask isolation), one mask, and save/dirty bookkeeping.
/// </summary>
public sealed class SpikeDocument
{
    public const int CurrentSchemaVersion = 1;

    public SpikeDocument(Guid documentId, string title, int canvasWidth, int canvasHeight)
    {
        DocumentId = documentId;
        Title = title;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;

        Objects = new ObjectLayer(LayerId.New(), "objects");
        Brush = new RasterLayer(LayerId.New(), "brush");
        Decor = new RasterLayer(LayerId.New(), "decor");
        Mask = new MaskLayer(LayerId.New(), Brush.Id, "brush-mask");
    }

    private SpikeDocument(
        Guid documentId, string title, int canvasWidth, int canvasHeight,
        ObjectLayer objects, RasterLayer brush, RasterLayer decor, MaskLayer mask)
    {
        DocumentId = documentId;
        Title = title;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        Objects = objects;
        Brush = brush;
        Decor = decor;
        Mask = mask;
    }

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid DocumentId { get; }
    public string Title { get; set; }
    public int CanvasWidth { get; }
    public int CanvasHeight { get; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; private set; } = DateTimeOffset.UtcNow;

    public ObjectLayer Objects { get; }
    public RasterLayer Brush { get; }
    public RasterLayer Decor { get; }
    public MaskLayer Mask { get; }

    /// <summary>Monotonic mutation counter. Renderers cache against it; they never write it.</summary>
    public long Revision { get; private set; }

    /// <summary>
    /// Last revision known to be durable on disk. Advanced ONLY after a successful atomic replace
    /// (docs/05 save contract). A failed save must leave this behind <see cref="Revision"/>.
    /// </summary>
    public long SavedRevision { get; private set; } = -1;

    public bool IsDirty => Revision != SavedRevision;

    public Rect2 CanvasBounds => new(0, 0, CanvasWidth, CanvasHeight);

    public void BumpRevision(DateTimeOffset? nowUtc = null)
    {
        Revision++;
        ModifiedUtc = nowUtc ?? DateTimeOffset.UtcNow;
    }

    public void MarkSaved(long revision) => SavedRevision = revision;

    internal static SpikeDocument Rehydrate(
        Guid documentId, string title, int canvasWidth, int canvasHeight, int schemaVersion,
        DateTimeOffset createdUtc, DateTimeOffset modifiedUtc, long revision,
        ObjectLayer objects, RasterLayer brush, RasterLayer decor, MaskLayer mask)
    {
        var doc = new SpikeDocument(documentId, title, canvasWidth, canvasHeight, objects, brush, decor, mask)
        {
            SchemaVersion = schemaVersion,
            CreatedUtc = createdUtc,
            ModifiedUtc = modifiedUtc,
            Revision = revision,
            SavedRevision = revision
        };
        return doc;
    }
}
