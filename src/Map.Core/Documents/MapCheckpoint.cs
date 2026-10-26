using System.Collections.ObjectModel;

namespace Map.Core.Documents;

/// <summary>Named bounds for durable checkpoints. Nothing is evicted automatically.</summary>
public static class MapCheckpointLimits
{
    public const int MaxCount = 20;
    public const long MaxTotalStoredBytes = 64L * 1024 * 1024;
    public const int MaxPreviewBytes = 1 * 1024 * 1024;
    public const int PreviewWidth = 320;
    public const int PreviewHeight = 180;
    public const int MaxNameLength = 120;
}

/// <summary>
/// Immutable map state captured by a checkpoint. The checkpoint catalog is deliberately absent,
/// so snapshots can never recursively contain checkpoints or copies of sibling checkpoints.
/// </summary>
public sealed class MapCheckpointSnapshot
{
    private MapCheckpointSnapshot(MapDocument document)
    {
        Title = document.Title;
        CreatedUtc = document.CreatedUtc;
        Canvas = document.Canvas;
        Layers = new ReadOnlyCollection<MapLayer>([.. document.Layers]);
        Assets = new ReadOnlyCollection<MapAssetReference>([.. document.Assets]);
        Notes = new ReadOnlyCollection<MapNote>([.. document.Notes]);
        StampForeground = document.StampForeground;
        Category = document.Category;
        Lighting = document.Lighting;
        Style = document.Style;
        Entities = document.Entities;
        Labels = document.Labels;
        Fog = document.Fog;
        Transitions = document.Transitions;
        Timeline = document.Timeline;
        Pointcrawl = document.Pointcrawl;
    }

    public string Title { get; }
    public DateTimeOffset CreatedUtc { get; }
    public CanvasDefinition Canvas { get; }
    public IReadOnlyList<MapLayer> Layers { get; }
    public IReadOnlyList<MapAssetReference> Assets { get; }
    public IReadOnlyList<MapNote> Notes { get; }
    public StampForegroundSurface? StampForeground { get; }
    public MapCategoryId Category { get; }

    /// <summary>The light and time of day of the version (Свет), or null when it had none.</summary>
    public MapLighting? Lighting { get; }

    /// <summary>The style of the version (Стили), or null when it had none.</summary>
    public MapStyle? Style { get; }

    /// <summary>The cards of the world of the version (R26).</summary>
    public System.Collections.Immutable.ImmutableArray<MapEntity> Entities { get; }

    /// <summary>The label settings of the version (R07b).</summary>
    public LabelSettings Labels { get; }

    /// <summary>The fog of war of the version (R27).</summary>
    public MapFog Fog { get; }

    /// <summary>The transitions of the version (R29).</summary>
    public System.Collections.Immutable.ImmutableArray<MapTransition> Transitions { get; }

    /// <summary>The timeline of the version (R31).</summary>
    public MapTimeline Timeline { get; }

    /// <summary>The pointcrawl of the version (R32).</summary>
    public MapPointcrawl Pointcrawl { get; }

    public static MapCheckpointSnapshot Capture(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new MapCheckpointSnapshot(document);
    }

    /// <summary>
    /// Restores only map state. The live document id and its checkpoint catalog are supplied by
    /// the caller and therefore cannot be replaced by historical values.
    /// </summary>
    public MapDocument Materialize(
        DocumentId currentDocumentId,
        IReadOnlyList<MapCheckpoint>? checkpoints = null) => new(
            currentDocumentId,
            Title,
            CreatedUtc,
            Canvas,
            Layers,
            Assets,
            Notes,
            checkpoints,
            StampForeground,
            Category) { Lighting = Lighting, Style = Style, Entities = Entities, Labels = Labels, Fog = Fog, Transitions = Transitions, Timeline = Timeline, Pointcrawl = Pointcrawl };
}

/// <summary>One named durable checkpoint with its bounded preview and immutable map snapshot.</summary>
public sealed class MapCheckpoint
{
    private readonly byte[] _previewPng;

    public MapCheckpoint(
        CheckpointId id,
        string name,
        DateTimeOffset createdUtc,
        MapCheckpointSnapshot snapshot,
        ReadOnlySpan<byte> previewPng)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(snapshot);

        var trimmed = name.Trim();
        if (trimmed.Length > MapCheckpointLimits.MaxNameLength)
        {
            throw new ArgumentException(
                $"Checkpoint name is longer than {MapCheckpointLimits.MaxNameLength} characters.",
                nameof(name));
        }

        if (previewPng.Length == 0)
        {
            throw new ArgumentException("A checkpoint preview is required.", nameof(previewPng));
        }

        if (previewPng.Length > MapCheckpointLimits.MaxPreviewBytes)
        {
            throw new ArgumentException(
                $"Checkpoint preview exceeds {MapCheckpointLimits.MaxPreviewBytes} bytes.",
                nameof(previewPng));
        }

        Id = id;
        Name = trimmed;
        CreatedUtc = createdUtc;
        Snapshot = snapshot;
        _previewPng = previewPng.ToArray();
    }

    public CheckpointId Id { get; }
    public string Name { get; }
    public DateTimeOffset CreatedUtc { get; }
    public MapCheckpointSnapshot Snapshot { get; }
    public ReadOnlyMemory<byte> PreviewPng => _previewPng;

    public MapCheckpoint WithName(string name) => new(
        Id, name, CreatedUtc, Snapshot, _previewPng);
}
