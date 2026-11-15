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
        bool replaceStampForeground = false,
        CanvasDefinition? revisedCanvas = null)
    {
        Id = source.Id;
        Title = source.Title;
        CreatedUtc = source.CreatedUtc;
        Canvas = revisedCanvas ?? source.Canvas;
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
        Pointcrawl = source.Pointcrawl;
        Planet = source.Planet;
        Isometric = source.Isometric;
        Variants = source.Variants;
        Handout = source.Handout;
    }

    /// <summary>
    /// A checkpoint restored as a live document. The snapshot can only be captured from a
    /// document, so its layers, assets and notes already passed the whole-document validation
    /// (and the layer list is that document's own, sharing its derived indexes); only the
    /// supplied checkpoint catalog is new and is checked here.
    /// </summary>
    internal static MapDocument FromSnapshot(MapCheckpointSnapshot snapshot, DocumentId id,
        IReadOnlyList<MapCheckpoint>? checkpoints) => new(snapshot, id, checkpoints ?? []);

    private MapDocument(MapCheckpointSnapshot snapshot, DocumentId id, IReadOnlyList<MapCheckpoint> checkpoints)
    {
        Id = id;
        Title = snapshot.Title;
        CreatedUtc = snapshot.CreatedUtc;
        Canvas = snapshot.Canvas;
        Layers = snapshot.Layers;
        Assets = snapshot.Assets;
        Notes = snapshot.Notes;
        Checkpoints = new ReadOnlyCollection<MapCheckpoint>([.. checkpoints]);
        ValidateCheckpoints(Checkpoints);
        StampForeground = snapshot.StampForeground;
        Category = snapshot.Category;
        Lighting = snapshot.Lighting;
        Style = snapshot.Style;
        Entities = snapshot.Entities;
        Labels = snapshot.Labels;
        Fog = snapshot.Fog;
        Transitions = snapshot.Transitions;
        Timeline = snapshot.Timeline;
        Pointcrawl = snapshot.Pointcrawl;
        Planet = snapshot.Planet;
        Isometric = snapshot.Isometric;
        Variants = snapshot.Variants;
        Handout = snapshot.Handout;
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
        var objectIds = new HashSet<ObjectId>(orderedLayers.OfType<ObjectLayer>().Sum(l => l.Objects.Count));
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

                    // Only lines and shapes own vector ids; a stamp allocates nothing here.
                    if (sceneObject.Line is not null || sceneObject.Shape is not null)
                    {
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
        ValidateLayerContents(Layers, Assets, static _ => true);
        ValidateCheckpoints(Checkpoints);
        // Prepare at load/public construction, before the snapshot becomes editable.
        foreach (var layer in Layers.OfType<ObjectLayer>())
        {
            _ = AssetUsageIndex.For(layer);
            _ = PlacementOwnershipIndex.For(layer);
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

    /// <summary>The places and ways of the pointcrawl over the map (R32, schema v41).</summary>
    public MapPointcrawl Pointcrawl { get; init; } = new();

    /// <summary>The map is a whole planet, two to one (R39, schema v42).</summary>
    public MapPlanet Planet { get; init; } = new();

    /// <summary>The side and heights of the isometric view (R38, plan 2.3, schema v47).</summary>
    public MapIsometric Isometric { get; init; } = new();

    /// <summary>The named variants of the map - «День», «Ночь», «Политическая» and the owner's own (R09e, schema v50).</summary>
    public MapVariants Variants { get; init; } = new();

    /// <summary>How the map is handed to the players as an old sheet (R09f, schema v51); applied only by the handout export.</summary>
    public MapHandout Handout { get; init; }

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
    private static Dictionary<PlacementArea, Exception?>? CheckAreaMembersInParallel(IEnumerable<ObjectLayer> layers)
    {
        var areas = layers.SelectMany(layer => layer.PlacementAreas)
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

    /// <summary>
    /// Placement path/area, river and road invariants. Ids are collected from every object layer
    /// (they are unique across the document); the member, slot, asset and geometry checks run
    /// only on the layers <paramref name="check"/> selects, the others having been proven when
    /// they entered a snapshot.
    /// </summary>
    private static void ValidateLayerContents(IReadOnlyList<MapLayer> layers, IReadOnlyList<MapAssetReference> assets,
        Func<ObjectLayer, bool> check)
    {
        var pathIds = new HashSet<Guid>();
        var ownedIds = new HashSet<ObjectId>();
        var riverIds = new HashSet<Guid>();
        var checkedLayers = new HashSet<ObjectLayer>(layers.OfType<ObjectLayer>().Where(check), ReferenceEqualityComparer.Instance);
        var areaChecks = CheckAreaMembersInParallel(checkedLayers);
        foreach (var layer in layers.OfType<ObjectLayer>())
        {
            if (!checkedLayers.Contains(layer))
            {
                foreach (var path in layer.PlacementPaths)
                    if (!pathIds.Add(path.Id)) throw new ArgumentException("Duplicate placement path id.");
                foreach (var area in layer.PlacementAreas)
                    if (!pathIds.Add(area.Id)) throw new ArgumentException("Null or duplicate placement area id.");
                foreach (var river in layer.Rivers)
                    if (!riverIds.Add(river.Id)) throw new ArgumentException("Duplicate river id.");
                foreach (var road in layer.Roads)
                    if (!riverIds.Add(road.Id)) throw new ArgumentException("Duplicate road id.");
                continue;
            }

            if (!layer.PlacementPaths.IsEmpty || !layer.PlacementAreas.IsEmpty)
                ValidatePlacements(layer, assets, pathIds, ownedIds, areaChecks);
            foreach (var river in layer.Rivers)
            {
                if (river is null) throw new ArgumentException("Null river.");
                river.Validate();
                if (!riverIds.Add(river.Id)) throw new ArgumentException("Duplicate river id.");
            }
            foreach (var road in layer.Roads)
            {
                if (road is null) throw new ArgumentException("Null road.");
                road.Validate();
                if (!riverIds.Add(road.Id)) throw new ArgumentException("Duplicate road id.");
            }
        }
    }

    private static void ValidatePlacements(ObjectLayer layer, IReadOnlyList<MapAssetReference> assets,
        HashSet<Guid> pathIds, HashSet<ObjectId> ownedIds, Dictionary<PlacementArea, Exception?>? areaChecks)
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
            if (path.Variants.Any(v => !assets.Any(a => a.Sha256 == v.Sha256)
                || v.MaskSha256 is { } mask && !assets.Any(a => a.Sha256 == mask)))
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
                    || item.Stamp.MaskAssetSha256 is { } mask && !assets.Any(a => a.Sha256 == mask))
                    throw new ArgumentException("Invalid or multiply owned placement area member.");
                var anchor = composition?.At(member.Slot).Point ?? Editing.PlacementAreaPlanner.Anchor(area, member.Slot);
                if (Math.Abs(item.X + item.Width / 2 - anchor.X) > .000001
                    || Math.Abs(item.Y + item.Height / 2 - anchor.Y) > .000001 || item.Sublayer != area.Sublayer)
                    throw new ArgumentException("A placement area member is outside its saved slot or sublayer.");
            }
            if (area.Variants.Any(v => !assets.Any(a => a.Sha256 == v.Sha256)
                || v.MaskSha256 is { } mask && !assets.Any(a => a.Sha256 == mask)))
                throw new ArgumentException("A saved area must retain every resolved variant and mask.");
            Editing.PlacementAreaDrawOrder.Warm(area, area.Members.Select(m => objectsById[m.ObjectId]));
        }
    }

    private static void ValidateCheckpoints(IReadOnlyList<MapCheckpoint> checkpoints)
    {
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
    }

    /// <summary>New snapshot with another layer list; the map-wide light, style, atlas place and the rest stay.</summary>
    /// <remarks>
    /// Same invariants as the public constructor, checked in proportion to what changed (the way
    /// a ProseMirror step checks only the replaced range and Roslyn reuses unchanged green nodes):
    /// the same immutable list is a trusted copy; otherwise, when the source revision index is
    /// prepared, only new layers, replaced object storage and changed paths/areas/rivers/roads are
    /// validated against the source's identity indexes, and the derived indexes follow the delta.
    /// A cold source keeps the full whole-document validation.
    /// </remarks>
    public MapDocument WithLayers(IReadOnlyList<MapLayer> layers) => WithCanvasAndLayers(Canvas, layers);

    /// <summary>New snapshot with another canvas definition; layers and everything else stay.</summary>
    public MapDocument WithCanvas(CanvasDefinition canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        return new MapDocument(this, revisedCanvas: canvas);
    }

    /// <summary><see cref="WithLayers"/> that also replaces the canvas definition (hex import, board reshape).</summary>
    internal MapDocument WithCanvasAndLayers(CanvasDefinition canvas, IReadOnlyList<MapLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(layers);
        // The document's own list never aliases a caller list and was validated on entry.
        if (ReferenceEquals(layers, Layers)) return new MapDocument(this, revisedCanvas: canvas);
        if (DocumentRevisionIndex.TryGetPrepared(this, out var index)) return WithLayerDelta(canvas, layers, index);
        return WholeDocumentRevision(canvas, layers);
    }

    /// <summary>Most objects a layer-list revision validates one by one against the source index.</summary>
    private const int MaxDeltaObjects = 4_096;

    private MapDocument WholeDocumentRevision(CanvasDefinition canvas, IReadOnlyList<MapLayer> layers) =>
        new(Id, Title, CreatedUtc, canvas, layers, Assets, Notes, Checkpoints, StampForeground, Category)
        {
            Atlas = Atlas,
            Lighting = Lighting,
            Style = Style,
            Entities = Entities,
            Labels = Labels,
            Fog = Fog,
            Transitions = Transitions,
            Timeline = Timeline,
            Pointcrawl = Pointcrawl,
            Planet = Planet,
            Isometric = Isometric,
            Variants = Variants,
            Handout = Handout,
        };

    private MapDocument WithLayerDelta(CanvasDefinition canvas, IReadOnlyList<MapLayer> layers, DocumentRevisionIndex index)
    {
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
        var sourceById = new Dictionary<LayerId, MapLayer>(Layers.Count);
        foreach (var layer in Layers) sourceById.Add(layer.Id, layer);
        var layerIds = new HashSet<LayerId>();
        var gridIds = new HashSet<GridId>();
        var keptObjects = new HashSet<LayerId>();
        var revised = new HashSet<ObjectLayer>(ReferenceEqualityComparer.Instance);
        var entering = new List<SceneObject>();
        foreach (var layer in orderedLayers)
        {
            if (layer is null || !layerIds.Add(layer.Id))
            {
                throw new ArgumentException($"Duplicate layer id {layer?.Id}.", nameof(layers));
            }

            if (layer is GridLayer gridLayer && gridLayer.Grids.Any(grid => !gridIds.Add(grid.Id)))
            {
                throw new ArgumentException("Grid ids are unique across the document.", nameof(layers));
            }

            if (layer is not ObjectLayer objects) continue;
            var before = sourceById.GetValueOrDefault(layer.Id) as ObjectLayer;
            if (before is not null && ReferenceEquals(before.Objects, objects.Objects))
            {
                keptObjects.Add(layer.Id);
                if (!ReferenceEquals(before, objects) && !(before.PlacementPaths == objects.PlacementPaths
                    && before.PlacementAreas == objects.PlacementAreas && before.Rivers == objects.Rivers
                    && before.Roads == objects.Roads))
                    revised.Add(objects);
                continue;
            }

            revised.Add(objects);
            entering.AddRange(objects.Objects);
        }

        // A large replaced payload (a whole rebuilt layer) is cheaper to prove with one plain
        // whole-document pass than object by object through the revision overlays.
        if (entering.Count > MaxDeltaObjects) return WholeDocumentRevision(canvas, layers);
        index.ValidateLayerDelta(keptObjects, entering);
        ValidateLayerContents(orderedLayers, Assets, revised.Contains);
        var target = new MapDocument(this, revisedLayers: orderedLayers, revisedCanvas: canvas);
        foreach (var layer in target.Layers)
        {
            if (layer is not ObjectLayer objects || !revised.Contains(objects)) continue;
            if (!keptObjects.Contains(objects.Id) && sourceById.GetValueOrDefault(objects.Id) is ObjectLayer replaced)
                ObjectLayerChanges.RegisterReplacement(replaced, objects);
            _ = AssetUsageIndex.For(objects);
            _ = PlacementOwnershipIndex.For(objects);
        }

        var leaving = new List<SceneObject>();
        foreach (var layer in Layers)
            if (layer is ObjectLayer objects && !keptObjects.Contains(layer.Id)) leaving.AddRange(objects.Objects);
        DocumentRevisionIndex.RegisterLayerDelta(this, target, keptObjects);
        ObjectNameIndex.Register(this, target, leaving, entering);
        return target;
    }

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
            if (Layers[index] is ObjectLayer source && layers[index] is ObjectLayer target
                && !ReferenceEquals(source, target)) PlacementOwnershipIndex.Share(source, target);
        }

        return new MapDocument(this, revisedLayers: layers);
    }
}
