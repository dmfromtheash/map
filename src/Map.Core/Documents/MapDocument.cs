using System.Collections.Immutable;
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
        Category = source.Category;
        Atlas = source.Atlas;
        Lighting = source.Lighting;
        Style = source.Style;
        Entities = source.Entities;
        Labels = source.Labels;
        Fog = source.Fog;
        Transitions = source.Transitions;
        Timeline = source.Timeline;
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
        StampForegroundSurface? stampForeground = null,
        MapCategoryId category = default)
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
        Category = category;

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
        var areaChecks = CheckAreaMembersInParallel(Layers);
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
                if (areaChecks is null || !areaChecks.TryGetValue(area, out var failure)) area.ValidateMembers();
                else if (failure is InvalidOperationException) area.ValidateMembers();
                else if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
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
        foreach (var road in Layers.OfType<ObjectLayer>().SelectMany(l => l.Roads))
        {
            if (road is null) throw new ArgumentException("Null road.");
            road.Validate();
            if (!riverIds.Add(road.Id)) throw new ArgumentException("Duplicate road id.");
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
    public AtlasLocator? Atlas { get; init; }

    /// <summary>Light and time of day (DEC-057), or null when the map has never used the Свет section.</summary>
    public MapLighting? Lighting { get; init; }

    /// <summary>The style of the whole map (R09, DEC-059), or null when the map has never used the Стили section.</summary>
    public MapStyle? Style { get; init; }

    /// <summary>The cards of the world kept with this map (R26, schema v36): places, rivers, characters, factions.</summary>
    public ImmutableArray<MapEntity> Entities { get; init; } = [];

    /// <summary>How labels give way by scale and overlap, and keep placement areas clear (R07b, schema v37).</summary>
    public LabelSettings Labels { get; init; } = new();

    /// <summary>The fog of war of the painted map: what the players have seen (R27, schema v38).</summary>
    public MapFog Fog { get; init; } = new();

    /// <summary>Stairs, cave mouths, shafts and portals between levels and maps (R29, schema v39).</summary>
    public ImmutableArray<MapTransition> Transitions { get; init; } = [];

    /// <summary>The year shown and the political mode (R31, schema v40).</summary>
    public MapTimeline Timeline { get; init; } = new();
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

    /// <summary>
    /// The workshop this map belongs to (<see cref="MapCategoryId"/>). Fixed at creation: edits,
    /// versions, resize and clones keep it; nothing in Core changes it afterwards.
    /// </summary>
    public MapCategoryId Category { get; init; }

    /// <summary>The mandatory background layer.</summary>
    public BaseColorLayer BaseColor => (BaseColorLayer)Layers[0];

    public MapLayer? FindLayer(LayerId id) => Layers.FirstOrDefault(l => l.Id == id);

    /// <summary>
    /// Runs the full member check of every placement area at once when there are several.
    /// </summary>
    /// <remarks>
    /// Each check rebuilds the whole scatter of its area; on the regional map this was 0.4 s of
    /// every open, one area after another. The checks are pure and independent. The caller keeps
    /// its order: it rethrows a stored failure where the sequential check would have thrown, and
    /// repeats a check refused by the memory budget alone, since parallel reservations can refuse
    /// what one reservation at a time would allow. Returns null when there is nothing to share.
    /// </remarks>
    private static Dictionary<PlacementArea, Exception?>? CheckAreaMembersInParallel(IReadOnlyList<MapLayer> layers)
    {
        var areas = layers.OfType<ObjectLayer>().SelectMany(layer => layer.PlacementAreas)
            .Where(area => area is not null).Distinct<PlacementArea>(ReferenceEqualityComparer.Instance).ToArray();
        if (areas.Length < 2)
        {
            return null;
        }

        var failures = new Exception?[areas.Length];
        Parallel.For(0, areas.Length, index =>
        {
            try { areas[index].ValidateMembers(); }
            catch (Exception error) { failures[index] = error; }
        });
        var results = new Dictionary<PlacementArea, Exception?>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < areas.Length; index++)
        {
            results[areas[index]] = failures[index];
        }

        return results;
    }

    /// <summary>New snapshot with another layer list; the map-wide light, style, atlas place and the rest stay.</summary>
    public MapDocument WithLayers(IReadOnlyList<MapLayer> layers) =>
        new(Id, Title, CreatedUtc, Canvas, layers, Assets, Notes, Checkpoints, StampForeground, Category)
        {
            Atlas = Atlas,
            Lighting = Lighting,
            Style = Style,
            Entities = Entities,
            Labels = Labels,
            Fog = Fog,
            Transitions = Transitions,
            Timeline = Timeline,
        };

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
        MapLayer? original = null, rewritten = null;
        var layers = new List<MapLayer>(Layers.Count);
        foreach (var layer in Layers)
        {
            if (layer.Id == id)
            {
                found = true;
                original = layer;
                rewritten = rewrite(layer);
                layers.Add(rewritten);
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

        // A rewrite that keeps the layer's identity, kind and objects (a stroke on its tiles, a
        // mask, a style, a river or a road) is a replacement revision: nothing the whole-document
        // invariants guard - ids, groups, placement areas, ownership - can have changed, and the
        // full check rebuilt every placement area of the map on each stroke (a quarter of a
        // second on a regional map). The indexes of the unchanged layers carry over.
        if (rewritten!.Id == id && rewritten.GetType() == original!.GetType()
            && (original is not ObjectLayer before || rewritten is ObjectLayer after && ReferenceEquals(before.Objects, after.Objects)))
        {
            var target = WithRevisedLayers(layers);
            ObjectNameIndex.Share(this, target);
            DocumentRevisionIndex.ShareUnchangedLayers(this, target);
            return target;
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
