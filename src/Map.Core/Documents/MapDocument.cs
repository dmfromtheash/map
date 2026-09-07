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
    private MapDocument(
        MapDocument source,
        IReadOnlyList<MapLayer>? revisedLayers = null,
        IReadOnlyList<MapAssetReference>? revisedAssets = null,
        IReadOnlyList<MapNote>? revisedNotes = null,
        IReadOnlyList<MapCheckpoint>? revisedCheckpoints = null,
        StampForegroundSurface? revisedStampForeground = null,
        bool replaceStampForeground = false)
    {
        Id = source.Id;
        Title = source.Title;
        CreatedUtc = source.CreatedUtc;
        Canvas = source.Canvas;
        Layers = revisedLayers is null
            ? source.Layers
            : new ReadOnlyCollection<MapLayer>([.. LayerOrderPolicy.Normalize(revisedLayers)]);
        Assets = revisedAssets is null
            ? source.Assets
            : new ReadOnlyCollection<MapAssetReference>([.. revisedAssets]);
        Notes = revisedNotes is null
            ? source.Notes
            : new ReadOnlyCollection<MapNote>([.. revisedNotes]);
        Checkpoints = revisedCheckpoints is null
            ? source.Checkpoints
            : new ReadOnlyCollection<MapCheckpoint>([.. revisedCheckpoints]);
        StampForeground = replaceStampForeground ? revisedStampForeground : source.StampForeground;
    }

    public MapDocument(
        DocumentId id,
        string title,
        DateTimeOffset createdUtc,
        CanvasDefinition canvas,
        IReadOnlyList<MapLayer> layers,
        IReadOnlyList<MapAssetReference>? assets = null,
        IReadOnlyList<MapNote>? notes = null,
        IReadOnlyList<MapCheckpoint>? checkpoints = null,
        StampForegroundSurface? stampForeground = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(layers);
        assets ??= [];
        notes ??= [];
        checkpoints ??= [];

        if (layers.Count == 0 || layers[0] is not BaseColorLayer)
        {
            throw new ArgumentException(
                "A document starts with the mandatory base colour layer at index 0.", nameof(layers));
        }

        if (layers.Skip(1).Any(l => l is BaseColorLayer))
        {
            throw new ArgumentException("Only one base colour layer may exist.", nameof(layers));
        }

        var orderedLayers = LayerOrderPolicy.Normalize(layers);
        var layerIds = new HashSet<LayerId>();
        var objectIds = new HashSet<ObjectId>();
        var nodeIds = new HashSet<VectorNodeId>();
        var portalIds = new HashSet<PortalId>();
        var regionIds = new HashSet<ShapeRegionId>();
        var gridIds = new HashSet<GridId>();
        var compositionIds = new HashSet<CompositionId>();

        void ValidateComposition(SceneObject owner)
        {
            if (owner.Composition is not { } composition) return;
            if (!compositionIds.Add(composition.Id))
                throw new ArgumentException("Composition ids are unique across the document.", nameof(layers));
            foreach (var child in composition.Items)
            {
                if (!objectIds.Add(child.Id))
                    throw new ArgumentException("Composition internal object ids cannot collide.", nameof(layers));
                var childNodes = child.Line?.Nodes
                    ?? child.Shape?.Regions.SelectMany(region => region.Nodes).ToList() ?? [];
                var childPortals = child.Line?.Portals ?? child.Shape?.Portals ?? [];
                if (childNodes.Any(node => !nodeIds.Add(node.Id))
                    || childPortals.Any(portal => !portalIds.Add(portal.Id))
                    || child.Shape?.Regions.Any(region => !regionIds.Add(region.Id)) == true)
                    throw new ArgumentException("Composition vector ids cannot collide.", nameof(layers));
                ValidateComposition(child);
            }
        }
        foreach (var layer in orderedLayers)
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
                    ValidateComposition(sceneObject);
                }
            }

            if (layer is GridLayer gridLayer && gridLayer.Grids.Any(grid => !gridIds.Add(grid.Id)))
            {
                throw new ArgumentException("Grid ids are unique across the document.", nameof(layers));
            }
        }

        // Group naming invariants: a name only exists together with a group id, and every member
        // of one group carries exactly the same name (null included, so old files stay valid).
        var groupNames = new Dictionary<Guid, string?>();
        foreach (var sceneObject in orderedLayers.OfType<ObjectLayer>().SelectMany(l => l.Objects))
        {
            if (sceneObject.GroupId is not { } groupKey)
            {
                if (sceneObject.GroupName is not null)
                {
                    throw new ArgumentException(
                        $"Object {sceneObject.Id} carries a group name without a group id.", nameof(layers));
                }

                continue;
            }

            if (!groupNames.TryAdd(groupKey, sceneObject.GroupName)
                && !string.Equals(groupNames[groupKey], sceneObject.GroupName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Members of group {groupKey} disagree on the group name.", nameof(layers));
            }
        }

        Id = id;
        Title = title;
        CreatedUtc = createdUtc;
        Canvas = canvas;
        Layers = new ReadOnlyCollection<MapLayer>([.. orderedLayers]);
        Assets = new ReadOnlyCollection<MapAssetReference>([.. assets]);
        Notes = new ReadOnlyCollection<MapNote>([.. notes]);
        Checkpoints = new ReadOnlyCollection<MapCheckpoint>([.. checkpoints]);
        StampForeground = stampForeground;

        if (Assets.Select(asset => asset.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != Assets.Count)
        {
            throw new ArgumentException("Asset hashes are unique within a document.", nameof(assets));
        }
        if (Notes.Select(note => note.Id).Distinct().Count() != Notes.Count)
        {
            throw new ArgumentException("Note ids are unique within a document.", nameof(notes));
        }
        var pathIds = new HashSet<Guid>();
        var ownedIds = new HashSet<ObjectId>();
        foreach (var layer in Layers.OfType<ObjectLayer>().Where(l => !l.PlacementPaths.IsEmpty || !l.PlacementAreas.IsEmpty))
        {
            var objectsById = layer.Objects.ToDictionary(o => o.Id);
            foreach (var path in layer.PlacementPaths)
            {
                if (path is null) throw new ArgumentException("Null placement path.");
                path.ValidateMembers();
                if (!pathIds.Add(path.Id)) throw new ArgumentException("Duplicate placement path id.");
                var nodeIdsForPath = path.Nodes.Take(path.Nodes.Length - 1).Select(n => n.Id).ToHashSet();
                foreach (var member in path.Members)
                    if (!ownedIds.Add(member.ObjectId) || !nodeIdsForPath.Contains(member.Slot.NodeId)
                        || Editing.PlacementPathPlanner.ObjectIdFor(path.Id, member.Slot) != member.ObjectId
                        || !objectsById.TryGetValue(member.ObjectId, out var item) || item.Kind != SceneObjectKind.Stamp)
                        throw new ArgumentException("Invalid or multiply owned placement path member.");
                if (path.Variants.Any(v => !Assets.Any(a => a.Sha256 == v.Sha256)
                    || v.MaskSha256 is { } mask && !Assets.Any(a => a.Sha256 == mask)))
                    throw new ArgumentException("A saved path must retain every resolved variant and mask.");
            }
            foreach (var area in layer.PlacementAreas)
            {
                if (area is null || !pathIds.Add(area.Id)) throw new ArgumentException("Null or duplicate placement area id.");
                area.ValidateMembers();
                var composition = area.LayoutVersion >= 4 ? new Editing.PlacementAreaLayout(area) : null;
                foreach (var member in area.Members)
                {
                    if (!ownedIds.Add(member.ObjectId) || !objectsById.TryGetValue(member.ObjectId, out var item)
                        || item.Kind != SceneObjectKind.Stamp || item.Stamp is null
                        || !area.Variants.Any(v => v.Sha256 == item.Stamp.AssetSha256)
                        || item.Stamp.MaskAssetSha256 is { } mask && !Assets.Any(a => a.Sha256 == mask))
                        throw new ArgumentException("Invalid or multiply owned placement area member.");
                    var anchor = composition?.At(member.Slot).Point ?? Editing.PlacementAreaPlanner.Anchor(area, member.Slot);
                    if (Math.Abs(item.X + item.Width / 2 - anchor.X) > .000001
                        || Math.Abs(item.Y + item.Height / 2 - anchor.Y) > .000001 || item.Sublayer != area.Sublayer)
                        throw new ArgumentException("A placement area member is outside its saved slot or sublayer.");
                }
                if (area.Variants.Any(v => !Assets.Any(a => a.Sha256 == v.Sha256)
                    || v.MaskSha256 is { } mask && !Assets.Any(a => a.Sha256 == mask)))
                    throw new ArgumentException("A saved area must retain every resolved variant and mask.");
                Editing.PlacementAreaDrawOrder.Warm(area, area.Members.Select(m => objectsById[m.ObjectId]));
            }
        }
        var riverIds = new HashSet<Guid>();
        foreach (var river in Layers.OfType<ObjectLayer>().SelectMany(l => l.Rivers))
        {
            if (river is null) throw new ArgumentException("Null river.");
            river.Validate();
            if (!riverIds.Add(river.Id)) throw new ArgumentException("Duplicate river id.");
        }
        if (Checkpoints.Count > MapCheckpointLimits.MaxCount)
        {
            throw new ArgumentException(
                $"A document can contain at most {MapCheckpointLimits.MaxCount} checkpoints.",
                nameof(checkpoints));
        }
        if (Checkpoints.Select(checkpoint => checkpoint.Id).Distinct().Count() != Checkpoints.Count)
        {
            throw new ArgumentException(
                "Checkpoint ids are unique within a document.", nameof(checkpoints));
        }
    }

    public DocumentId Id { get; init; }
    public string Title { get; init; }

    /// <summary>When the document was first created. Supplied by the caller; Core has no clock.</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    public CanvasDefinition Canvas { get; init; }

    /// <summary>
    /// Layers in canonical composition order, base colour first and every Land layer below every
    /// Object layer. Never aliases a caller list.
    /// </summary>
    public IReadOnlyList<MapLayer> Layers { get; init; }

    public IReadOnlyList<MapAssetReference> Assets { get; init; }
    public IReadOnlyList<MapNote> Notes { get; init; }
    public IReadOnlyList<MapCheckpoint> Checkpoints { get; init; }

    /// <summary>Optional map-wide paint revealed only through enabled per-stamp clip masks.</summary>
    public StampForegroundSurface? StampForeground { get; init; }

    /// <summary>The mandatory background layer.</summary>
    public BaseColorLayer BaseColor => (BaseColorLayer)Layers[0];

    public MapLayer? FindLayer(LayerId id) => Layers.FirstOrDefault(l => l.Id == id);

    public MapDocument WithLayers(IReadOnlyList<MapLayer> layers) =>
        new(Id, Title, CreatedUtc, Canvas, layers, Assets, Notes, Checkpoints, StampForeground);

    public MapDocument WithAssets(IReadOnlyList<MapAssetReference> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (assets.Select(asset => asset.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != assets.Count)
        {
            throw new ArgumentException("Asset hashes are unique within a document.", nameof(assets));
        }

        var target = new MapDocument(this, revisedAssets: assets);
        ObjectNameIndex.Share(this, target);
        DocumentRevisionIndex.ShareUnchangedLayers(this, target);
        return target;
    }

    public MapDocument WithNotes(IReadOnlyList<MapNote> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (notes.Select(note => note.Id).Distinct().Count() != notes.Count)
        {
            throw new ArgumentException("Note ids are unique within a document.", nameof(notes));
        }

        return new MapDocument(this, revisedNotes: notes);
    }

    public MapDocument WithCheckpoints(IReadOnlyList<MapCheckpoint> checkpoints)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        if (checkpoints.Count > MapCheckpointLimits.MaxCount)
        {
            throw new ArgumentException(
                $"A document can contain at most {MapCheckpointLimits.MaxCount} checkpoints.",
                nameof(checkpoints));
        }
        if (checkpoints.Select(checkpoint => checkpoint.Id).Distinct().Count() != checkpoints.Count)
        {
            throw new ArgumentException(
                "Checkpoint ids are unique within a document.", nameof(checkpoints));
        }

        return new MapDocument(this, revisedCheckpoints: checkpoints);
    }

    public MapDocument WithStampForeground(StampForegroundSurface? surface) =>
        new(this, revisedStampForeground: surface, replaceStampForeground: true);

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

    /// <summary>
    /// Fast replacement-only revision boundary for Core edit commands. Layer count, identity and
    /// order cannot change here; load/public construction retains the full invariant validation.
    /// </summary>
    internal MapDocument WithRevisedLayers(IReadOnlyList<MapLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (layers.Count != Layers.Count)
        {
            throw new ArgumentException("A replacement revision cannot add or remove layers.", nameof(layers));
        }

        for (var index = 0; index < layers.Count; index++)
        {
            if (layers[index].Id != Layers[index].Id
                || layers[index].GetType() != Layers[index].GetType())
            {
                throw new ArgumentException(
                    "A replacement revision must preserve layer identity, kind and order.",
                    nameof(layers));
            }
        }

        return new MapDocument(this, revisedLayers: layers);
    }
}
