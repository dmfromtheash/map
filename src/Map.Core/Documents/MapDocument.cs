using System.Collections.ObjectModel;

namespace Map.Core.Documents;

/// <summary>
/// The single source of truth for a map (docs/04). Immutable snapshot: every edit produces a new
/// instance, so persistence and rendering can hold a snapshot that never changes under them.
/// Session state - selection, viewport, tool - is deliberately NOT part of the document.
///
/// Layer invariants: exactly one <see cref="BaseColorLayer"/>, always at index 0; layer and
/// object ids are unique across the document.
/// </summary>
public sealed record MapDocument
{
    public MapDocument(
        DocumentId id,
        string title,
        DateTimeOffset createdUtc,
        CanvasDefinition canvas,
        IReadOnlyList<MapLayer> layers,
        IReadOnlyList<MapAssetReference>? assets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(layers);
        assets ??= [];

        if (layers.Count == 0 || layers[0] is not BaseColorLayer)
        {
            throw new ArgumentException(
                "A document starts with the mandatory base colour layer at index 0.", nameof(layers));
        }

        if (layers.Skip(1).Any(l => l is BaseColorLayer))
        {
            throw new ArgumentException("Only one base colour layer may exist.", nameof(layers));
        }

        var layerIds = new HashSet<LayerId>();
        var objectIds = new HashSet<ObjectId>();
        var nodeIds = new HashSet<VectorNodeId>();
        var portalIds = new HashSet<PortalId>();
        var regionIds = new HashSet<ShapeRegionId>();
        foreach (var layer in layers)
        {
            if (!layerIds.Add(layer.Id))
            {
                throw new ArgumentException($"Duplicate layer id {layer.Id}.", nameof(layers));
            }

            if (layer is ObjectLayer objectLayer)
            {
                foreach (var sceneObject in objectLayer.Objects)
                {
                    if (!objectIds.Add(sceneObject.Id))
                    {
                        throw new ArgumentException(
                            $"Duplicate object id {sceneObject.Id}.", nameof(layers));
                    }

                    var nodes = sceneObject.Line?.Nodes
                        ?? sceneObject.Shape?.Regions.SelectMany(region => region.Nodes).ToList()
                        ?? [];
                    var portals = sceneObject.Line?.Portals ?? sceneObject.Shape?.Portals ?? [];
                    if (nodes.Any(node => !nodeIds.Add(node.Id))
                        || portals.Any(portal => !portalIds.Add(portal.Id))
                        || sceneObject.Shape?.Regions.Any(region => !regionIds.Add(region.Id)) == true)
                    {
                        throw new ArgumentException("Vector node, portal and region ids are unique across the document.", nameof(layers));
                    }
                }
            }
        }

        Id = id;
        Title = title;
        CreatedUtc = createdUtc;
        Canvas = canvas;
        Layers = new ReadOnlyCollection<MapLayer>([.. layers]);
        Assets = new ReadOnlyCollection<MapAssetReference>([.. assets]);

        if (Assets.Select(asset => asset.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != Assets.Count)
        {
            throw new ArgumentException("Asset hashes are unique within a document.", nameof(assets));
        }
    }

    public DocumentId Id { get; init; }
    public string Title { get; init; }

    /// <summary>When the document was first created. Supplied by the caller; Core has no clock.</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    public CanvasDefinition Canvas { get; init; }

    /// <summary>Layers in composition order, base colour first. Never aliases a caller list.</summary>
    public IReadOnlyList<MapLayer> Layers { get; init; }

    public IReadOnlyList<MapAssetReference> Assets { get; init; }

    /// <summary>The mandatory background layer.</summary>
    public BaseColorLayer BaseColor => (BaseColorLayer)Layers[0];

    public MapLayer? FindLayer(LayerId id) => Layers.FirstOrDefault(l => l.Id == id);

    public MapDocument WithLayers(IReadOnlyList<MapLayer> layers) =>
        new(Id, Title, CreatedUtc, Canvas, layers, Assets);

    public MapDocument WithAssets(IReadOnlyList<MapAssetReference> assets) =>
        new(Id, Title, CreatedUtc, Canvas, Layers, assets);

    /// <summary>New snapshot with one layer replaced in place.</summary>
    public MapDocument WithLayer(LayerId id, Func<MapLayer, MapLayer> rewrite)
    {
        var found = false;
        var layers = new List<MapLayer>(Layers.Count);
        foreach (var layer in Layers)
        {
            if (layer.Id == id)
            {
                found = true;
                layers.Add(rewrite(layer));
            }
            else
            {
                layers.Add(layer);
            }
        }

        if (!found)
        {
            throw new InvalidOperationException($"Layer {id} is not part of this document.");
        }

        return WithLayers(layers);
    }
}
