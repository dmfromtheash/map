using System.Collections.ObjectModel;

namespace Map.Core.Documents;

/// <summary>
/// The single source of truth for a map (docs/04). Immutable snapshot: every edit produces a new
/// instance, so persistence and (later) rendering can hold a snapshot that never changes under
/// them. Session state - selection, viewport, tool - is deliberately NOT part of the document.
/// </summary>
public sealed record MapDocument
{
    public MapDocument(
        DocumentId id,
        string title,
        DateTimeOffset createdUtc,
        CanvasDefinition canvas,
        IReadOnlyList<Layer> layers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(layers);

        var layerIds = new HashSet<LayerId>();
        var objectIds = new HashSet<ObjectId>();
        foreach (var layer in layers)
        {
            if (!layerIds.Add(layer.Id))
            {
                throw new ArgumentException($"Duplicate layer id {layer.Id}.", nameof(layers));
            }

            foreach (var sceneObject in layer.Objects)
            {
                if (!objectIds.Add(sceneObject.Id))
                {
                    throw new ArgumentException($"Duplicate object id {sceneObject.Id}.", nameof(layers));
                }
            }
        }

        Id = id;
        Title = title;
        CreatedUtc = createdUtc;
        Canvas = canvas;
        Layers = new ReadOnlyCollection<Layer>([.. layers]);
    }

    public DocumentId Id { get; }
    public string Title { get; }

    /// <summary>When the document was first created. Supplied by the caller; Core has no clock.</summary>
    public DateTimeOffset CreatedUtc { get; }

    public CanvasDefinition Canvas { get; }

    /// <summary>Layers in composition order. The stored copy cannot be mutated by the caller's list.</summary>
    public IReadOnlyList<Layer> Layers { get; }
}
