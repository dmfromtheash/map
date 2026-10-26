using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;
using System.Runtime.CompilerServices;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Map.Rendering;

/// <summary>Supplies decoded asset images to the renderer. Stage 4's catalog implements it.</summary>
public interface IStampImageSource
{
    /// <summary>The image for the content hash, or null when the asset is missing/not resolved.</summary>
    SKImage? TryGetImage(string sha256);
    SKImage? TryGetImageForPixelSize(string sha256, double width, double height) => TryGetImage(sha256);
    /// <summary>
    /// True when every image these stamps sample at their pixel sizes is already decoded. Otherwise
    /// the missing ones start decoding off the render thread, <paramref name="ready"/> is called as
    /// each finishes, and the caller keeps drawing what it already has. Called on the render thread.
    /// </summary>
    bool PrepareForPixelSizes(IEnumerable<(string Sha256, double Width, double Height)> stamps, Action ready) => true;
}

/// <summary>The live, uncommitted part of a raster gesture, drawn on top of its layer.</summary>
public sealed class StrokePreview
{
    public required LayerId LayerId { get; init; }

    /// <summary>Per-tile coverage 0..255 from the in-progress <c>RasterStrokeSession</c>.</summary>
    public required IReadOnlyDictionary<TileKey, byte[]> Coverage { get; init; }

    /// <summary>
    /// Change counter per tile of <see cref="Coverage"/>, when the source tracks one: a tile whose
    /// array and counter are unchanged since the last frame has the same pixels, so its preview
    /// image is drawn again instead of being rebuilt. Without it every tile is rebuilt per frame.
    /// </summary>
    public IReadOnlyDictionary<TileKey, int>? Versions { get; init; }

    public string Color { get; init; } = "#FF000000";
    public double Opacity { get; init; } = 1.0;
    public bool Eraser { get; init; }
    public BrushSettings? PaintSettings { get; init; }

    /// <summary>Mask strokes preview as the red mask overlay, not as paint.</summary>
    public bool IsMask { get; init; }

    /// <summary>The preview belongs to the document-owned, map-aligned stamp foreground.</summary>
    public bool IsStampForeground { get; init; }
    public bool IsLandPaint { get; init; }

    /// <summary>A stroke of the «маска обрыва» brush (R12): a tint over the land, not new ground or paint.</summary>
    public bool IsCliffMask { get; init; }
}

/// <summary>Per-frame counters. Optional: the editor sets it only when the HUD or a test needs it.</summary>
public sealed class RenderStats
{
    public int LayersDrawn { get; set; }
    public int ObjectCandidates { get; set; }
    public int ObjectsDrawn { get; set; }
    public int TilesDrawn { get; set; }
    public int MaskTilesDrawn { get; set; }
    public int MipmappedTiles { get; set; }
    public int MissingAssets { get; set; }
    /// <summary>Object layers drawn from their kept gesture image (<see cref="LayerImageCache"/>) in this frame.</summary>
    public int LayerImagesKept { get; set; }
    /// <summary>Terrain images completed from a partial background build (a redrawn part laid into the previous image).</summary>
    public int TerrainPatches { get; set; }
    /// <summary>Diagnostics: why a layer large enough for dense tiles was drawn object by object in this frame (null when it was not).</summary>
    public string? DenseSkip { get; set; }
    public int ExactObjects { get; set; }
    public int OverviewObjects { get; set; }
    public int OverviewChunks { get; set; }
    public int OverviewBatches { get; set; }
    public int DrawCalls { get; set; }
    public int StampAtlasBatches { get; set; }
    public int StampOccludedObjects { get; set; }
    public int ObjectTilesDrawn { get; set; }
    public double DetailPrepareMilliseconds { get; set; }
    public double DetailRasterMilliseconds { get; set; }
    /// <summary>Exact object tiles left for a later frame by the settled-frame budget (R05).</summary>
    public int DetailTilesPending { get; set; }
    public int GpuDetailTiles { get; set; }
    public long ObjectCacheHits { get; set; }
    public long ObjectCacheMisses { get; set; }
    public long ObjectCacheInvalidations { get; set; }
    public int StampInstances { get; set; }
    public int RepeatedAssetInstances { get; set; }
    public int TextObjects { get; set; }
    public int VectorObjects { get; set; }
    public int CompositionObjects { get; set; }
    public int EffectObjects { get; set; }
    /// <summary>Where the scene pass spent its CPU time: the terrain pass, the object layers, and inside those the stamp paths.</summary>
    public double TerrainMilliseconds { get; set; }
    public double LandMilliseconds { get; set; }
    public double WaterMilliseconds { get; set; }
    public double RiverMilliseconds { get; set; }
    /// <summary>River passes: halo, bank relief, water fill, material, coast wash, shallows, water lines, outline.</summary>
    public double[] RiverPassMilliseconds { get; } = new double[8];
    public double RoadMilliseconds { get; set; }
    public double ObjectsMilliseconds { get; set; }
    public double StampBatchMilliseconds { get; set; }
    /// <summary>Frames that drew the kept terrain image resampled because the camera was moving.</summary>
    public int TerrainReused { get; set; }
    public int TerrainRefinementsPending { get; set; }
    /// <summary>Hex chunks that stood in this frame because their image was not drawn yet (they follow in the next frames).</summary>
    public int HexChunksPending { get; set; }
    public double TerrainRefinementBuildMilliseconds { get; set; }
    /// <summary>Frames of a moving camera whose kept terrain image did not cover the view, so the plain passes ran for the rest; the last such case described.</summary>
    public int TerrainUncovered { get; set; }
    public string TerrainUncoveredDetail { get; set; } = "";
    /// <summary>Layers whose kept detail tiles of a neighbouring zoom bucket were drawn resampled while the camera moved.</summary>
    public int TilesReused { get; set; }
    public double StampSingleMilliseconds { get; set; }
    public int StampMipBlends { get; set; }
    public double StampMipBlendMilliseconds { get; set; }
    private readonly HashSet<string> _seenAssets = new(StringComparer.Ordinal);

    public void Reset()
    {
        LayersDrawn = 0;
        ObjectCandidates = 0;
        ObjectsDrawn = 0;
        TilesDrawn = 0;
        MaskTilesDrawn = 0;
        MipmappedTiles = 0;
        MissingAssets = 0;
        LayerImagesKept = 0;
        TerrainPatches = 0;
        DenseSkip = null;
        ExactObjects = 0;
        OverviewObjects = 0;
        OverviewChunks = 0;
        OverviewBatches = 0;
        DrawCalls = 0;
        StampAtlasBatches = 0;
        StampOccludedObjects = 0;
        ObjectTilesDrawn = 0;
        DetailPrepareMilliseconds = 0;
        DetailRasterMilliseconds = 0;
        DetailTilesPending = 0;
        GpuDetailTiles = 0;
        ObjectCacheHits = 0;
        ObjectCacheMisses = 0;
        ObjectCacheInvalidations = 0;
        StampInstances = 0;
        RepeatedAssetInstances = 0;
        TextObjects = 0;
        VectorObjects = 0;
        CompositionObjects = 0;
        EffectObjects = 0;
        TerrainMilliseconds = 0;
        LandMilliseconds = 0;
        WaterMilliseconds = 0;
        RiverMilliseconds = 0;
        Array.Clear(RiverPassMilliseconds);
        RoadMilliseconds = 0;
        ObjectsMilliseconds = 0;
        StampBatchMilliseconds = 0;
        TerrainReused = 0;
        TerrainUncovered = 0; TerrainUncoveredDetail = "";
        TerrainRefinementsPending = 0;
        HexChunksPending = 0;
        TerrainRefinementBuildMilliseconds = 0;
        TilesReused = 0;
        StampSingleMilliseconds = 0;
        StampMipBlends = 0;
        StampMipBlendMilliseconds = 0;
        _seenAssets.Clear();
    }

    internal void RecordExactObject(SceneObject sceneObject)
    {
        switch (sceneObject.Kind)
        {
            case SceneObjectKind.Stamp:
                StampInstances++;
                if (sceneObject.Stamp is { } stamp && !_seenAssets.Add(stamp.AssetSha256))
                {
                    RepeatedAssetInstances++;
                }
                break;
            case SceneObjectKind.Text:
                TextObjects++;
                break;
            case SceneObjectKind.Line or SceneObjectKind.Shape:
                VectorObjects++;
                break;
            case SceneObjectKind.Composition:
                CompositionObjects++;
                break;
        }

        if (sceneObject.Stamp is { Blur: > 0 } or { ShadowOpacity: > 0 }
            || sceneObject.Text is { Blur: > 0 } or { ShadowOpacity: > 0 } or { OutlineWidth: > 0 }
            || sceneObject.Line?.Stroke is { Blur: > 0 } or { ShadowOpacity: > 0 }
            || sceneObject.Shape?.Stroke is { Blur: > 0 } or { ShadowOpacity: > 0 })
        {
            EffectObjects++;
        }
    }
}

/// <summary>
/// Editor-only rendering fidelity. Export always evaluates the exact document regardless of this
/// value; overview is a bounded screen-space representation for far zoom only.
/// </summary>
/// <summary>The brush footprint shown under the pointer: a circle, or the cells of a rough land dab.</summary>
public sealed record BrushCursorPreview(double WorldX, double WorldY, double Radius, double RoughCellSize, bool Subtract);

public enum RenderLodTier
{
    Exact,
    Interactive,
    Overview,
}

/// <summary>Options of one scene pass. Selection overlay is editor chrome and is opt-in only.</summary>
public sealed class RenderOptions
{
    /// <summary>Borrowed only during a native frame. Cached detail images remain raster-owned.</summary>
    public GRContext? GpuContext { get; init; }
    /// <summary>Draws the map without its Свет section (DEC-057) even when the document has one.</summary>
    public bool DisableLighting { get; init; }

    /// <summary>
    /// The clock of flickering torches and fires, in seconds, while the canvas may animate (its
    /// window in front, touched lately); null keeps every light steady - the export, thumbnails,
    /// an idle canvas. With it, a lit frame with a flickering light in view asks for the next one
    /// through <see cref="RequestAmbientFrame"/>.
    /// </summary>
    public double? LightAnimationSeconds { get; init; }
    /// <summary>Object to draw the selection gizmo around, or null. Never set on export paths.</summary>
    public ObjectId? SelectedObjectId { get; init; }

    /// <summary>Additional selected objects (group selection) drawn with a thin ring, no handles.</summary>
    public IReadOnlyCollection<ObjectId>? CoSelectedObjectIds { get; init; }

    /// <summary>
    /// Shared frame of a multi-selection. When set, the handles belong to this frame: every
    /// selected object (the primary included) gets only a thin ring and no per-object gizmo.
    /// Editor chrome - never set on export or thumbnail passes.
    /// </summary>
    public WorldBounds? SelectionFrame { get; init; }

    /// <summary>Live rubber-band rectangle of an in-progress marquee drag. Editor chrome only.</summary>
    public WorldBounds? Marquee { get; init; }

    /// <summary>
    /// Current view scale, used to keep the gizmo's ring, handles and rotation stem a constant
    /// on-screen size regardless of zoom. Irrelevant when no selection is drawn.
    /// </summary>
    public double ViewScale { get; init; } = 1.0;

    /// <summary>
    /// World rectangle actually on screen. When set, objects come from the layer's spatial index
    /// and raster layers upload only the intersecting tiles - the reason a 131 072-unit map draws
    /// in constant time regardless of how much content lives outside the viewport.
    /// </summary>
    public WorldBounds? VisibleWorld { get => _visibleWorld; init => _visibleWorld = value; }
    private WorldBounds? _visibleWorld;

    /// <summary>Draws the map without its style (R09, DEC-059) even when the document has one.</summary>
    public bool DisableStyle { get; init; }

    /// <summary>
    /// Additional style raster density. Normally 1, including PDF canvases whose RasterDpi
    /// already supplies the requested device matrix; custom point-space canvases may ask for more.
    /// </summary>
    public double StyleRasterScale { get; init; } = 1;

    /// <summary>
    /// The options of the style's map pass: the same frame, the view widened by the reach of the
    /// medium's filters (so blurs meet across export tiles and the canvas edge).
    /// </summary>
    internal RenderOptions ForStyleScene(WorldBounds? visible, bool exactTerrain = false)
    {
        var copy = (RenderOptions)MemberwiseClone();
        copy._visibleWorld = visible;
        // A style's overscan is a different raster than the viewport terrain cache. The print
        // pass already needs exact land coverage; finish its scene here instead of repeatedly
        // invalidating an async viewport refinement with a larger off-screen view.
        if (exactTerrain) copy._terrain = null;
        return copy;
    }

    /// <summary>
    /// The options of the names drawn sharp over the style: no kept images or statistics of the
    /// map pass are touched, no live gesture state is drawn twice.
    /// </summary>
    internal RenderOptions ForStyleNames()
    {
        var copy = (RenderOptions)MemberwiseClone();
        copy._layerImages = null;
        copy._stats = null;
        copy._terrain = null;
        // A river or road being dragged belongs to the painted map: the names pass has none to resolve it against.
        copy._riverOverrides = null;
        copy._roadOverrides = null;
        copy._liveSuppressed = true;
        return copy;
    }

    /// <summary>
    /// Bounded live object substitutions for an editor gesture. They are evaluated over the
    /// immutable source revision without constructing a 50K-sized preview document.
    /// </summary>
    public IReadOnlyList<SceneObject>? ObjectOverrides { get; init; }

    /// <summary>
    /// Bounded transient objects for an armed placement. They are drawn only by editor callers,
    /// after all document layers, and never participate in indexes, exports or thumbnails.
    /// </summary>
    public IReadOnlyList<SceneObject>? PlacementPreviewObjects { get; init; }

    /// <summary>Owning object layer for a preview shadow whose placement is "behind layer".</summary>
    public LayerId? PlacementPreviewLayerId { get; init; }

    public WorldBounds? PlacementArea { get; init; }

    public bool PlacementInvalid { get; init; }

    /// <summary>
    /// Live river substitutions for an editor gesture, matched by id. A river whose id is not on
    /// any layer is a draft and is drawn on <see cref="RiverPreviewLayerId"/> only.
    /// </summary>
    public IReadOnlyList<River>? RiverOverrides { get => _riverOverrides; init => _riverOverrides = value; }
    private IReadOnlyList<River>? _riverOverrides;

    public LayerId? RiverPreviewLayerId { get; init; }

    /// <summary>Live road substitutions for an editor gesture, matched by id; a draft is drawn on <see cref="RoadPreviewLayerId"/> only.</summary>
    public IReadOnlyList<Road>? RoadOverrides { get => _roadOverrides; init => _roadOverrides = value; }
    private IReadOnlyList<Road>? _roadOverrides;

    public LayerId? RoadPreviewLayerId { get; init; }

    public TileImageCache? TileCache { get; init; }

    /// <summary>Kept pictures of hex chunks (hex category); null draws every hex directly.</summary>
    public HexChunkCache? HexChunks { get; init; }

    /// <summary>The hexes the hex tool would touch, outlined under the pointer.</summary>
    public HexCursorPreview? HexCursor { get; init; }

    /// <summary>Hexes the hex tool just changed, easing into their new state.</summary>
    public HexSettles? HexSettles { get; init; }

    /// <summary>The hex world moves while it is looked at (water glints, wind, clouds); never in an export.</summary>
    public bool HexAmbient { get; init; }

    /// <summary>A hex drag is running: changed hex chunks are drawn directly, their kept images follow at release.</summary>
    public bool HexGestureLive { get; init; }

    /// <summary>The selection, paste, path, route and open key of the hex workshop tools.</summary>
    public HexOverlay? HexOverlay { get; init; }

    /// <summary>
    /// The players' view of a hex map: the fog of war is opaque, only player notes of revealed hexes
    /// show, and no tool overlays are drawn (the player window and the player export).
    /// </summary>
    public HexPresentation HexPresentation { get; init; }

    public bool HexPlayerView { get; init; }

    /// <summary>
    /// R27: how much of the fog of war of a painted map the game master sees over the map, 0…1
    /// (0 - none). The players' view (<see cref="HexPlayerView"/>) always draws it opaque.
    /// </summary>
    public double FogVeil { get; init; }

    internal double FogOpacity => HexPlayerView ? 1 : FogVeil;

    /// <summary>The fog of a stroke in progress, drawn instead of the document's until it ends.</summary>
    public MapFog? FogOverride { get; init; }

    /// <summary>The level of the world shown (hex layers of other levels are left out); null shows every level.</summary>
    public int? HexLevel { get; init; }

    /// <summary>With <see cref="HexLevel"/>: this level is also shown, faint, under it (the surface over a dungeon).</summary>
    public int? HexGhostLevel { get; init; }

    /// <summary>
    /// Reuse of the land coverage rivers were already carved out of. Absent (export, tests) the
    /// image is simply built for the frame, exactly as before.
    /// </summary>
    public CarvedLandCache? CarvedLand { get; init; }

    /// <summary>
    /// Reuse of the complete look of a piece of land for an unchanged view. With it, a frame
    /// drawn during a stroke, an armed placement or a drag shows the same material and coast as
    /// the idle frame at the cost of one image. Absent (export, tests) the land is drawn in full.
    /// </summary>
    public RenderImageCache? StyledLand { get; init; }

    /// <summary>
    /// Reuse of the whole terrain of a level run - land and water together - for an unchanged
    /// view. Strokes, placement and drags then cost one image for the entire ground; a change
    /// to any land, river or road, a pan or a zoom rebuilds it.
    /// </summary>
    public RenderImageCache? Terrain { get => _terrain; init => _terrain = value; }
    private RenderImageCache? _terrain;

    /// <summary>
    /// Reuse of whole object layers during a stroke or an armed placement, when the frame
    /// repeats the view and the layer is not part of the gesture. Absent (export, tests) every
    /// layer is drawn directly.
    /// </summary>
    public LayerImageCache? LayerImages { get => _layerImages; init => _layerImages = value; }
    private LayerImageCache? _layerImages;

    /// <summary>Optional editor worker for refining an unchanged terrain after a camera move.</summary>
    public TerrainRefinement? TerrainRefinement { get; init; }
    public Action? RequestRefinedFrame { get; init; }

    /// <summary>
    /// Asks for a frame a little later (about 24 a second): what only the living hex world needs.
    /// A slow glint or a passing cloud shade reads as smooth at that rate, at a third of the work.
    /// </summary>
    public Action? RequestAmbientFrame { get; init; }

    /// <summary>Render-context-local cache used only by the editor overview tier.</summary>
    public ObjectOverviewCache? ObjectCache { get; init; }

    /// <summary>
    /// Where the per-zoom-bucket stamp atlases of dense tiles live: a few large images with a
    /// budget of their own, so one atlas cannot evict a screen of tiles. Falls back to
    /// <see cref="ObjectCache"/> when not set.
    /// </summary>
    public ObjectOverviewCache? AtlasCache { get; init; }

    public RenderLodTier LodTier { get; init; } = RenderLodTier.Exact;

    public IStampImageSource? Stamps { get; init; }

    public StrokePreview? Preview { get => _liveSuppressed ? null : _preview; init => _preview = value; }
    private StrokePreview? _preview;

    /// <summary>Images of the stroke preview's tiles kept between the frames of one gesture.</summary>
    public StrokePreviewCache? PreviewCache { get; init; }

    /// <summary>While a mask is being edited its hidden area is tinted red on this layer only.</summary>
    public LayerId? MaskEditLayerId { get => _liveSuppressed ? null : _maskEditLayerId; init => _maskEditLayerId = value; }
    private LayerId? _maskEditLayerId;

    /// <summary>While the «маска обрыва» brush is armed, the painted wall and beach of this land are tinted (R12).</summary>
    public LayerId? CliffMaskLayerId { get => _liveSuppressed ? null : _cliffMaskLayerId; init => _cliffMaskLayerId = value; }
    private LayerId? _cliffMaskLayerId;
    private bool _liveSuppressed;

    /// <summary>
    /// The same options with the live gesture state (stroke preview, mask-edit tint) hidden: what
    /// a kept image of the terrain is built from, so a frame drawn in the middle of a stroke can
    /// reuse the idle terrain and lay the stroke over it instead of rebuilding every coast.
    /// </summary>
    internal RenderOptions WithoutLiveState()
    {
        if (_liveSuppressed || (_preview is null && _maskEditLayerId is null && _cliffMaskLayerId is null)) return this;
        var copy = (RenderOptions)MemberwiseClone();
        copy._liveSuppressed = true;
        return copy;
    }

    public RenderStats? Stats { get => _stats; init => _stats = value; }
    private RenderStats? _stats;

    /// <summary>
    /// A gesture is moving objects or the pointer: per-object effects (blur, shadow, outline of
    /// vector shapes) are left out until it ends. The terrain no longer follows this flag - see
    /// <see cref="FastTerrain"/> - so a stroke or an armed placement keeps the land's material.
    /// </summary>
    public bool FastPreview { get; init; }

    /// <summary>
    /// The view itself is moving (a pan) or the geometry of water or roads is being dragged:
    /// the terrain draws its plain fills and skips the coast, bank and material passes that
    /// would otherwise be rebuilt for every frame. Idle, strokes, placement, selection and
    /// export evaluate the complete terrain.
    /// </summary>
    public bool FastTerrain { get; init; }

    /// <summary>Export paths opt in so editor-only grids can be omitted independently.</summary>
    public bool IsExport { get; init; }

    /// <summary>The brush under the pointer, drawn over everything as its true footprint; null hides it.</summary>
    public BrushCursorPreview? BrushCursor { get; init; }

    /// <summary>
    /// Draws every coast with the filter passes instead of the distance field (CoastDistanceField),
    /// for comparing the two in tests and measurements without touching global state.
    /// </summary>
    public bool CoastFilters { get; init; }

    /// <summary>
    /// The mandatory document base is normally drawn. Transparent PNG/WebP export explicitly
    /// opts out without changing the document or inventing a second background colour.
    /// </summary>
    public bool IncludeBaseColor { get; init; } = true;

    /// <summary>
    /// Export-level grid master switch. Per-grid <c>IncludeInExport</c> is still evaluated when
    /// this is true; editor visibility remains independent.
    /// </summary>
    public bool IncludeExportGrids { get; init; } = true;

    /// <summary>
    /// Derived editor chrome for Stage 12B. It is drawn after the document clip and never enters
    /// persistence, thumbnails or export unless a caller explicitly supplies it.
    /// </summary>
    public CanvasBoundaryOverlay? BoundaryOverlay { get; init; }
}

public sealed record CanvasBoundaryOverlay(
    WorldBounds? OldCanvas,
    WorldBounds? NewCanvas,
    WorldBounds? Region);

/// <summary>
/// The single scene evaluator: the editor canvas and the thumbnail exporter both go through it,
/// so what the user sees and what gets stored cannot drift apart. It never mutates the document.
/// </summary>
public static partial class SceneRenderer
{
    private static readonly LayerId StampForegroundLayerId =
        new(new Guid("2e304ada-4a3e-4f2e-896c-c92977b52150"));

    private static readonly SKColor SelectionColor = new(255, 214, 10, 255);
    // Spatial revisions retain untouched tiles; each raster is capped at 512 screen pixels.
    private const double OverviewChunkSize = 8_192;

    /// <summary>
    /// How much finer than the document a stamp is sampled in the editor. One world unit is one
    /// document pixel; a stamp W units wide draws from a mip of at most W × 16 source pixels,
    /// which is the editor's deepest zoom (DEC-046; DEC-035 capped it at four). A high-resolution
    /// asset therefore keeps its own detail all the way in, while far views still draw from a
    /// small step of its detail ladder. Export is never capped: it evaluates the full source for
    /// the requested output size.
    /// </summary>
    public const double StampDetailHeadroom = 16;

    /// <summary>
    /// Above this zoom the terrain magnifies the same pixels instead of recomputing coastlines
    /// at ever larger radii (DEC-035). Stamps have their own, deeper headroom above.
    /// </summary>
    public const double TerrainDetailHeadroom = 4;

    /// <summary>
    /// What the terrain reads from an object layer: its visibility, blending, effects and mask.
    /// The objects on the layer are not part of it, so placing or moving a stamp keeps every
    /// kept terrain image; the rivers and roads themselves are separate parts of the key.
    /// </summary>
    private sealed record TerrainOwnerKey(LayerId Id, bool Visible, double Opacity, string Blend, LayerEffects Effects, LayerMask? Mask);

    private static object OwnerKey(MapLayer layer) => layer is ObjectLayer owner
        ? new TerrainOwnerKey(owner.Id, owner.Visible, owner.Opacity, owner.Blend, owner.Effects, owner.Mask)
        : layer;

    /// <summary>Source pixels a stamp of this world size may draw from at this device scale.</summary>
    private static double DetailPixels(double worldSize, double pixelScale, RenderOptions options) =>
        worldSize * (options.IsExport ? pixelScale : Math.Min(pixelScale, StampDetailHeadroom));


    private const double OverviewExactScreenSize = 4;
    private static readonly ConditionalWeakTable<ObjectLayer, ObjectOverviewPlan> OverviewPlans = [];
    private static readonly ConditionalWeakTable<ObjectLayer, DetailTileRevisions> DetailRevisions = [];

    /// <summary>The revisions of a layer's objects by the identity of its object list (see <see cref="RevisionsFor"/>).</summary>
    private static readonly ConditionalWeakTable<object, ObjectRevisions> RevisionsByObjects = [];

    private sealed record ObjectRevisions(IReadOnlyList<ObjectSublayerState> Sublayers, DetailTileRevisions Revisions);

    /// <summary>
    /// The tile revisions of an object layer. A new record of the layer that keeps the very same
    /// object list and sublayer visibility - a mask, an opacity, a name, a lock changed - shares the
    /// revisions of the record before it, so its dense tiles stay valid: the tiles hold only the
    /// objects, the layer's group applies everything else. Each such change rebuilt every tile of the
    /// view in the frame (30 tiles, 350-430 ms with 50 000 of the owner's mountains: a mask stroke's
    /// press and release, its undo).
    /// </summary>
    private static DetailTileRevisions RevisionsFor(ObjectLayer layer)
    {
        if (DetailRevisions.TryGetValue(layer, out var known)) return known;
        if (RevisionsByObjects.TryGetValue(layer.Objects, out var shared)
            && shared.Sublayers.SequenceEqual(layer.SublayerStates))
        {
            DetailRevisions.AddOrUpdate(layer, shared.Revisions);
            return shared.Revisions;
        }
        var revisions = DetailRevisions.GetValue(layer, static l => new(l));
        RevisionsByObjects.AddOrUpdate(layer.Objects, new ObjectRevisions(layer.SublayerStates, revisions));
        return revisions;
    }
    private static long _detailRevision;
    private static readonly ConditionalWeakTable<byte[], MaskCoverageState> MaskCoverageStates = [];
    private static readonly ConditionalWeakTable<byte[], LandCoverageState> LandCoverageStates = [];

    /// <summary>Draws the document in world coordinates. The caller sets the world transform.</summary>
    public static void Render(SKCanvas canvas, MapDocument document, RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        options.Stats?.Reset();
        options.PreviewCache?.Sync(options.Preview);

        // The style (R09) repaints the map without its names, lit or not, and the names go on top
        // sharp; the Свет section (DEC-057) lights the finished map; the editing chrome stays on top.
        var styled = Styles.StyleRenderer.Applies(document, options);
        if (styled || Lighting.LightingRenderer.Applies(document, options))
        {
            if (styled)
                Styles.StyleRenderer.Render(canvas, document, options,
                    (target, bare, sceneOptions) =>
                    {
                        if (Lighting.LightingRenderer.Applies(bare, sceneOptions))
                            Lighting.LightingRenderer.Render(target, bare, sceneOptions, t => RenderScene(t, bare, sceneOptions, chrome: false));
                        else
                            RenderScene(target, bare, sceneOptions, chrome: false);
                    },
                    (target, names, namesOptions) => RenderScene(target, names, namesOptions, chrome: false));
            else
            {
                if (options.GpuContext is not null && !options.IsExport) Styles.StyleRenderer.DropKept();
                Lighting.LightingRenderer.Render(canvas, document, options, target => RenderScene(target, document, options, chrome: false));
            }
            var bounds = new WorldBounds(0, 0, document.Canvas.Width, document.Canvas.Height);
            canvas.Save();
            canvas.ClipRect(ToRect(bounds));
            FogRenderer.Draw(canvas, document, options.VisibleWorld ?? bounds, options.FogOpacity, options.FogOverride);
            DrawPlacementPreview(canvas, document, options);
            DrawSelectionChrome(canvas, document, options);
            canvas.Restore();
            DrawBoundaryOverlay(canvas, options.BoundaryOverlay, options.ViewScale);
            return;
        }
        // The canvas without light or style gives back the video memory of its kept frames.
        if (options.GpuContext is not null && !options.IsExport) { Lighting.LightingRenderer.DropKept(); Styles.StyleRenderer.DropKept(); }
        RenderScene(canvas, document, options, chrome: true);
    }

    private static void RenderScene(SKCanvas canvas, MapDocument document, RenderOptions options, bool chrome)
    {
        var canvasBounds = new WorldBounds(0, 0, document.Canvas.Width, document.Canvas.Height);
        var requestedView = options.VisibleWorld ?? canvasBounds;
        if (!TryIntersect(requestedView, canvasBounds, out var view))
        {
            if (chrome) DrawBoundaryOverlay(canvas, options.BoundaryOverlay, options.ViewScale);
            return;
        }

        // One factual document boundary for viewport, live preview, thumbnail and export. Layer
        // implementations may retain and cull hidden data, but no content can paint through it.
        canvas.Save();
        canvas.ClipRect(ToRect(canvasBounds));

        // The terrain - every piece of land and every river or lake - is one pass ordered by
        // level (TerrainOrder), drawn where the first visible land layer sits in the stack, or
        // where the first object layer sits when the map has no land. The stack still keeps
        // land below objects; inside the terrain the levels decide what covers what.
        var terrainAnchor = TerrainAnchor(document, options);
        // A stroke or a placement ghost repeats the same view frame after frame and changes no
        // object layer: those layers are drawn from kept images (DrawKeptObjectLayer). Between
        // gestures the images are dropped, so they hold video memory only while they serve.
        // The same holds for any frame that repeats the previous frame's view (LayerImageCache.Steady);
        // a frame that only moves the camera at the same scale shifts a wider kept image (TryDrawPanned).
        var motion = options.LayerImages is not null && !options.IsExport ? options.LayerImages.Motion(canvas) : FrameMotion.Changed;
        var eligible = options.LayerImages is not null && options.Stats is not null && !options.IsExport
            && options.ObjectOverrides is not { Count: > 0 };
        // A wheel notch changes the scale: the kept image stands in, scaled, within its limits.
        var panning = eligible && motion is FrameMotion.Panned or FrameMotion.Changed;
        var keepLayers = eligible && !panning
            && (options.Preview is not null || options.PlacementPreviewObjects is { Count: > 0 } || motion == FrameMotion.Still);
        // Kept images hold video memory only while they serve: a moving camera keeps only its pan
        // images, a frame with neither a gesture nor a still view releases everything.
        if (!keepLayers && !panning) options.LayerImages?.Clear();
        else if (panning) options.LayerImages!.ClearStill();
        for (var layerIndex = 0; layerIndex < document.Layers.Count; layerIndex++)
        {
            var layer = document.Layers[layerIndex];
            if (options.IsExport && layer is BaseColorLayer && !options.IncludeBaseColor)
            {
                continue;
            }

            if (options.IsExport && layer is GridLayer && !options.IncludeExportGrids)
            {
                continue;
            }

            // Grid export is an explicit per-grid policy. Editor visibility must not silently
            // replace it: a hidden editor grid with IncludeInExport=true still belongs in export.
            var hiddenOnlyInEditor = options.IsExport && layer is GridLayer;
            if ((!layer.Visible && !hiddenOnlyInEditor) || layer.Opacity <= 0)
            {
                continue;
            }

            // One level of a hex world at a time; another level only as the faint ghost asked for,
            // or the level just left while the new one fades in over it.
            if (layer is HexLayer { Level: var hexLevel } && options.HexLevel is { } shownLevel
                && hexLevel != shownLevel && hexLevel != options.HexGhostLevel
                && !(options.HexChunks?.LevelChange(shownLevel, options.HexChunks.AnimationNow) is { } leaving && leaving.From == hexLevel))
            {
                continue;
            }

            if (layerIndex == terrainAnchor)
            {
                var terrainStarted = Stopwatch.GetTimestamp();
                DrawTerrain(canvas, document, view, options);
                if (options.Stats is { } terrainStats)
                    terrainStats.TerrainMilliseconds += Stopwatch.GetElapsedTime(terrainStarted).TotalMilliseconds;
            }
            if (layer is LandLayer)
            {
                continue;
            }

            if (options.Stats is { } stats)
            {
                stats.LayersDrawn++;
            }

            var clippingSource = layerIndex > 0 ? document.Layers[layerIndex - 1] : null;
            if ((panning || keepLayers && motion == FrameMotion.Still) && layer is ObjectLayer panned && CanKeepLayerImage(panned, options)
                && DrawPannedObjectLayer(canvas, document, panned, clippingSource, options, reuseOnly: !panning))
                continue;
            if (keepLayers && layer is ObjectLayer objects && CanKeepLayerImage(objects, options)
                && DrawKeptObjectLayer(canvas, document, objects, clippingSource, view, options))
                continue;
            if (keepLayers && layer is ObjectLayer masked && options.MaskEditLayerId == masked.Id && CanKeepMaskedContent(masked, options))
            {
                DrawLayer(canvas, document, layer, clippingSource, view, options, drawRivers: false,
                    contentOverride: target => DrawKeptObjectContent(target, document, masked, view, options), maskOverlay: true);
                continue;
            }
            DrawLayer(canvas, document, layer, clippingSource, view, options, drawRivers: false);
        }

        if (chrome)
        {
            FogRenderer.Draw(canvas, document, view, options.FogOpacity, options.FogOverride);
            DrawPlacementPreview(canvas, document, options);
            DrawSelectionChrome(canvas, document, options);
        }
        canvas.Restore();
        if (chrome) DrawBoundaryOverlay(canvas, options.BoundaryOverlay, options.ViewScale);
    }

    /// <summary>
    /// An object layer's pixels can be kept apart from the frame when they do not depend on what
    /// is drawn under them (Normal blending for the layer and every object, no clipping to the
    /// layer below, no clip masks against the stamp foreground) and the gesture does not draw
    /// into the layer (its stroke, its mask being edited, the ghost's "behind layer" shadow).
    /// </summary>
    private static bool CanKeepLayerImage(ObjectLayer layer, RenderOptions options) =>
        // An empty layer draws nothing: an image of the view for it is only memory (40 MiB during a pan).
        layer.Objects.Count > 0 && layer.Blend == "Normal" && !layer.Effects.ClippingEnabled
        && options.Preview?.LayerId != layer.Id && options.MaskEditLayerId != layer.Id
        && !(options.PlacementPreviewLayerId == layer.Id && options.PlacementPreviewObjects is { Count: > 0 } ghost
            && ghost.Any(o => o.Stamp is { ShadowPlacement: StampShadowPlacement.Layer, ShadowOpacity: > 0 }))
        && KeptLayerSupport.GetValue(layer, static l => new(l)).Supported;

    /// <summary>
    /// The objects of a layer whose mask is being edited can be kept apart from the mask: the
    /// stroke changes only the mask, which the layer's group applies over its objects afterwards.
    /// The objects themselves must not depend on the destination, as for a whole kept layer. The
    /// stroke preview of an object layer is always its mask's: objects take no raster strokes.
    /// </summary>
    private static bool CanKeepMaskedContent(ObjectLayer layer, RenderOptions options) =>
        layer.Objects.Count > 0 && options.LayerImages is not null && options.Stats is not null && !options.IsExport
        && options.ObjectOverrides is not { Count: > 0 }
        && !(options.PlacementPreviewLayerId == layer.Id && options.PlacementPreviewObjects is { Count: > 0 })
        && KeptLayerSupport.GetValue(layer, static l => new(l)).Supported;

    /// <summary>
    /// The objects of <paramref name="layer"/> only (no mask, opacity or effects: the caller's
    /// group applies those) from their kept image, slot 1 of <see cref="LayerImageCache"/>. A mask
    /// stroke over 1 500 mountains redrew every stamp in every frame of the stroke (45 FPS).
    /// </summary>
    private static void DrawKeptObjectContent(SKCanvas canvas, MapDocument document, ObjectLayer layer, WorldBounds view,
        RenderOptions options)
    {
        var stats = options.Stats!;
        var missingBefore = stats.MissingAssets;
        // R10: the depth order of the layer and its areas' underlays are drawn with the objects.
        object[] parts = [layer.Objects, layer.SublayerStates, layer.Effects.DepthSort, layer.PlacementAreas, options.LodTier, options.FastPreview, options.FastTerrain, options.ViewScale,
            (object?)options.Stamps ?? DBNull.Value, (object?)document.StampForeground ?? DBNull.Value, document.Assets];
        var built = false;
        void Draw(SKCanvas target)
        {
            built = true;
            var started = Stopwatch.GetTimestamp();
            DrawObjects(target, document, layer, view, options);
            stats.ObjectsMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        if (!options.LayerImages!.TryDraw(canvas, layer.Id, parts, options.GpuContext, Draw,
                () => stats.MissingAssets == missingBefore, slot: 1))
        {
            Draw(canvas);
            return;
        }
        if (!built) stats.LayerImagesKept++;
    }

    /// <summary>Per layer revision: how many objects would read the destination (non-Normal blend, clip mask, unknown kinds).</summary>
    private sealed class LayerImageSupport
    {
        private readonly int _unsupported;
        public bool Supported => _unsupported == 0;
        public LayerImageSupport(ObjectLayer layer)
        {
            if (ObjectLayerChanges.TryGet(layer, out var delta)
                && KeptLayerSupport.TryGetValue(delta.Source, out var previous))
            {
                _unsupported = previous._unsupported - delta.Removed.Count(i => !Independent(delta.Source.Objects[i]))
                    + delta.Added.Count(i => !Independent(layer.Objects[i]));
            }
            else
            {
                _unsupported = layer.Objects.Count(o => !Independent(o));
            }
        }

        private static bool Independent(SceneObject o) => o.Kind switch
        {
            SceneObjectKind.Stamp => o.Stamp is { Blend: "Normal", ClipMaskEnabled: false },
            SceneObjectKind.Text => o.Text is null or { Blend: "Normal" },
            _ => false,
        };
    }

    private static readonly ConditionalWeakTable<ObjectLayer, LayerImageSupport> KeptLayerSupport = [];

    private static bool DrawKeptObjectLayer(SKCanvas canvas, MapDocument document, ObjectLayer layer, MapLayer? clippingSource,
        WorldBounds view, RenderOptions options)
    {
        var stats = options.Stats!;
        var missingBefore = stats.MissingAssets;
        var pendingBefore = stats.DetailTilesPending;
        var built = false;
        // Everything the layer's pixels are drawn from besides the layer itself: the detail
        // level and per-object effects of the frame, the stamp images, the foreground surface.
        // A fast frame (the camera moving) draws dense tiles of a nearby zoom bucket resampled:
        // its image must not serve the exact frame that follows.
        object[] parts = [layer, options.LodTier, options.FastPreview, options.FastTerrain, options.ViewScale, (object?)options.Stamps ?? DBNull.Value,
            (object?)document.StampForeground ?? DBNull.Value, document.Assets];
        var appended = AppendedObjects(layer, view, options, out var previous);
        var drawn = options.LayerImages!.TryDraw(canvas, layer.Id, parts, options.GpuContext,
            target => { built = true; DrawLayer(target, document, layer, clippingSource, view, options, drawRivers: false); },
            () => stats.MissingAssets == missingBefore && stats.DetailTilesPending == pendingBefore,
            previous,
            appended is null ? null : target =>
            {
                built = true;
                foreach (var sceneObject in appended)
                {
                    if (!sceneObject.Visible || !layer.StateFor(sceneObject.Sublayer).Visible) continue;
                    DrawObject(target, sceneObject, document.StampForeground, options);
                    stats.ObjectsDrawn++;
                }
            });
        if (drawn && !built) stats.LayerImagesKept++;
        if (drawn) stats.LayersDrawn++;
        return drawn;
    }

    /// <summary>
    /// An object layer in a frame of a pan, from a kept image of the view widened by a quarter of
    /// its size on every side (LayerImageCache.TryDrawPanned): the layer is drawn over that wider
    /// area once, and the following frames of the pan shift it.
    /// </summary>
    private static bool DrawPannedObjectLayer(SKCanvas canvas, MapDocument document, ObjectLayer layer, MapLayer? clippingSource,
        RenderOptions options, bool reuseOnly = false)
    {
        var stats = options.Stats!;
        var missingBefore = stats.MissingAssets;
        var pendingBefore = stats.DetailTilesPending;
        var built = false;
        // No scale in the key: within its limits the image of another scale stands in, resampled.
        // The fast flag is in it: the image a wheel zoom built from resampled tiles of a nearby
        // bucket served every still frame after the zoom (the exact bucket was built only by the
        // next edit, 0.4-0.6 s inside a stroke on 50 000 objects; found in the live check 2026-10-18).
        object[] parts = [layer, options.LodTier, options.FastPreview, options.FastTerrain, (object?)options.Stamps ?? DBNull.Value,
            (object?)document.StampForeground ?? DBNull.Value, document.Assets];
        var device = canvas.DeviceClipBounds;
        // Dense layers already reuse world-aligned tiles. Building a widened pan image visits
        // up to three times as many tiles, exceeding the cache's 16 MiB pressure budget and
        // evicting the very tiles the next refinement needs in order to finish.
        var margin = layer.Objects.Count >= DenseTileMinimumObjects && options.ObjectCache is not null
            && options.RequestRefinedFrame is not null ? 0 : Math.Max(device.Width, device.Height) / 4;
        var drawn = options.LayerImages!.TryDrawPanned(canvas, layer.Id, parts, options.GpuContext, margin,
            (target, world) =>
            {
                built = true;
                DrawLayer(target, document, layer, clippingSource, new WorldBounds(world.Left, world.Top, world.Right, world.Bottom),
                    options, drawRivers: false);
            },
            () => stats.MissingAssets == missingBefore && stats.DetailTilesPending == pendingBefore, reuseOnly, allowScale: !reuseOnly);
        if (drawn && !built) stats.LayerImagesKept++;
        if (drawn) stats.LayersDrawn++;
        return drawn;
    }

    /// <summary>
    /// The objects a placement appended to <paramref name="layer"/>, when drawing just them over
    /// the image of the previous revision gives the same pixels as drawing the whole layer: the
    /// layer has no group of its own (full opacity, no mask, no layer shadows), every object sits
    /// on sublayer 0 so the new ones are drawn last, nothing was removed, the new objects cast no
    /// "behind layer" shadow (that prepass runs under all objects) and the tier is not the
    /// overview's point rendering. Null otherwise.
    /// </summary>
    private static IReadOnlyList<SceneObject>? AppendedObjects(ObjectLayer layer, WorldBounds view, RenderOptions options,
        out ObjectLayer? previous)
    {
        previous = null;
        if (options.LodTier == RenderLodTier.Overview || layer.Opacity < 1
            || layer.Mask is { Enabled: true } mask && HasEffectiveMask(mask, view)
            || layer.Effects is { OuterShadowOpacity: > 0 } or { InnerShadowOpacity: > 0 } or { DepthSort: true }
            || !ObjectLayerChanges.TryGet(layer, out var delta) || delta.Removed.Count > 0 || delta.Added.Count == 0
            || delta.Added.Any(index => index < delta.Source.Objects.Count)
            || !RevisionsFor(layer).AllOnFirstSublayer)
            return null;
        var added = delta.Added.Select(index => layer.Objects[index]).ToArray();
        if (added.Any(o => o.Stamp is { ShadowPlacement: StampShadowPlacement.Layer, ShadowOpacity: > 0 })) return null;
        // R10 blots go under every object, not over the kept picture.
        if (added.Any(AreaBeautyRenderer.HasMass)) return null;
        previous = delta.Source;
        return added;
    }

    /// <summary>The stack index the terrain pass is drawn at: the first visible land with coverage, else the first visible object layer.</summary>
    private static int TerrainAnchor(MapDocument document, RenderOptions options)
    {
        var firstObjects = -1;
        for (var index = 0; index < document.Layers.Count; index++)
        {
            switch (document.Layers[index])
            {
                case LandLayer { Visible: true, Opacity: > 0 } land when land.GeometryTiles.TileCount > 0 || HasLandShapePreview(land, options):
                    return index;
                case ObjectLayer { Visible: true, Opacity: > 0 } when firstObjects < 0:
                    firstObjects = index;
                    break;
            }
        }
        return firstObjects;
    }

    /// <summary>
    /// Land and water level by level. Each piece of land is drawn through the ordinary layer
    /// path (mask, opacity, blend); the water of a level follows it, carved into and clipped to
    /// the land at that level or below, and covered by the land of the levels above.
    /// </summary>
    /// <summary>One road or river of an object layer, in that layer's surface order, with its terrain level.</summary>
    private readonly record struct Surface(ObjectLayer Owner, int Order, River? River, Road? Road)
    {
        public int Level => River?.Level ?? Road!.Level;
    }

    /// <summary>
    /// The visible roads and water of every object layer, in stack order and, inside a layer, in
    /// its own surface order - with the editor's live substitutions applied. A draft road takes
    /// the default place of a road, under the water of its layer, so the preview and the result agree.
    /// </summary>
    private static List<Surface> Surfaces(MapDocument document, WorldBounds view, RenderOptions options)
    {
        var result = new List<Surface>();
        foreach (var layer in document.Layers.OfType<ObjectLayer>())
        {
            if (!layer.Visible || layer.Opacity <= 0) continue;
            result.AddRange(LayerSurfaces(layer, view, options));
        }
        return result;
    }

    private static IEnumerable<Surface> LayerSurfaces(ObjectLayer layer, WorldBounds view, RenderOptions options)
    {
        var rivers = RiverRenderer.Resolve(layer, options);
        var roads = RoadRenderer.Resolve(layer, options);
        if (rivers.Count == 0 && roads.Count == 0) yield break;
        var resolved = layer with { Rivers = [.. rivers], Roads = [.. roads] };
        var order = 0;
        foreach (var entry in LayerSurfaceOrder.Resolve(resolved))
        {
            if (entry.Kind == SurfaceKind.Water)
            {
                var river = rivers.First(r => r.Id == entry.Id);
                if (river.Visible && river.Style.Opacity > 0 && RiverGeometry.Evaluate(river).Bounds.Intersects(view))
                    yield return new Surface(layer, order, river, null);
            }
            else
            {
                var road = roads.First(r => r.Id == entry.Id);
                if (road.Visible && road.Style.Opacity > 0 && RoadGeometry.Evaluate(road).Bounds.Intersects(view))
                    yield return new Surface(layer, order, null, road);
            }
            order++;
        }
    }

    /// <summary>
    /// Draws one owner's surfaces of one level in their order: consecutive rivers as one body of
    /// water (so shared looks share an outline), each road as itself. A/B/A stays A/B/A.
    /// </summary>
    private static void DrawSurfaceRun(SKCanvas target, IReadOnlyList<Surface> run, WorldBounds view, RenderOptions options,
        Action<SKCanvas, SKPaint>? landCoverage, IReadOnlyList<River> neighbours)
    {
        var index = 0;
        while (index < run.Count)
        {
            if (run[index].Road is { } road)
            {
                var roadStarted = Stopwatch.GetTimestamp();
                RoadRenderer.DrawRoads(target, [road], view, options);
                if (options.Stats is { } roadStats) roadStats.RoadMilliseconds += Stopwatch.GetElapsedTime(roadStarted).TotalMilliseconds;
                index++;
                continue;
            }
            var water = new List<River>();
            while (index < run.Count && run[index].River is { } river) { water.Add(river); index++; }
            var riverStarted = Stopwatch.GetTimestamp();
            RiverRenderer.DrawRivers(target, water, view, options, landCoverage, neighbours);
            if (options.Stats is { } riverStats) riverStats.RiverMilliseconds += Stopwatch.GetElapsedTime(riverStarted).TotalMilliseconds;
        }
        // Bridges, fords and ferries where this run's roads cross the owner's rivers, above both (R06).
        if (run.Count > 0 && run[0].Owner.Rivers.Length > 0)
        {
            var runRoads = new List<Road>();
            foreach (var surface in run) if (surface.Road is { } road) runRoads.Add(road);
            if (runRoads.Count > 0)
            {
                var rivers = RiverRenderer.Resolve(run[0].Owner, options);
                var crossings = RoadCrossings.Detect(runRoads, rivers);
                if (crossings.Count > 0)
                {
                    // Water at a higher level than the road flows over it: no bridge there.
                    var kept = new List<RoadCrossing>(crossings.Count);
                    foreach (var crossing in crossings)
                    {
                        var roadLevel = runRoads.First(r => r.Id == crossing.RoadId).Level;
                        var riverLevel = rivers.First(r => r.Id == crossing.RiverId).Level;
                        if (riverLevel <= roadLevel) kept.Add(crossing);
                    }
                    if (kept.Count > 0) RoadCrossingRenderer.Draw(target, kept, options.ViewScale, options.FastPreview);
                }
            }
        }
    }

    private static void DrawTerrain(SKCanvas canvas, MapDocument document, WorldBounds view, RenderOptions options)
    {
        var lands = TerrainOrder.Lands(document)
            .Where(land => land.Visible && land.Opacity > 0
                && (land.GeometryTiles.TileCount > 0 || HasLandShapePreview(land, options)))
            .ToList();
        var surfaces = Surfaces(document, view, options);
        if (lands.Count == 0 && surfaces.Count == 0) return;

        // The whole terrain of the view is kept between frames when nothing in it can change
        // from one frame to the next: no live land stroke, no mask editing, no pan or geometry
        // drag, and only Normal blending (a multiplied layer must meet what lies beneath it in
        // the frame, not in a kept image). An edit to any land, river or road, or a change of
        // view, produces new inputs and rebuilds the image.
        // A live stroke on a land (its shape or its material) or a mask being edited does not
        // change the document: the kept terrain is still exact, and the stroke is composited
        // over it (DrawTerrainLiveOverlay) instead of rebuilding every coast and river per frame.
        // The overview tier (far zoom) is kept as well: its simplified land and water are as
        // deterministic per view and scale as the exact ones, and without the kept image every
        // wheel notch of a zoom-out redrew all the water of the map (40-50 ms per frame).
        var keepable = options.Terrain is not null && !options.IsExport
            && lands.All(land => land.Blend == "Normal")
            && surfaces.All(surface => surface.Owner.Blend == "Normal"
                && (!surface.Owner.Effects.ClippingEnabled || options.ObjectOverrides is null));
        var cacheable = keepable && !options.FastTerrain;
        // The identity of the kept image does not depend on the view: every visible land and
        // every visible river and road of the map is part of it, whether or not it is on screen,
        // so a zoomed-in frame can still recognise the image built for the whole map.
        List<object> TerrainParts()
        {
            var parts = new List<object>();
            if (ActiveSea(document, options) is { } sea) parts.Add(sea);
            foreach (var land in lands) parts.Add(land);
            foreach (var surface in Surfaces(document, new WorldBounds(0, 0, document.Canvas.Width, document.Canvas.Height), options))
            {
                parts.Add(OwnerKey(surface.Owner));
                parts.Add((object?)surface.River ?? surface.Road!);
                if (surface.Owner.Effects.ClippingEnabled && LowerLayer(document, surface.Owner) is { } lower) parts.Add(lower);
            }
            return parts;
        }
        // The camera is moving: the terrain last finished for these very layers, rivers and
        // roads is drawn resampled into the new view - the same land, coast and material, only
        // scaled - and the plain fills cover whatever the kept image no longer reaches. The
        // exact terrain is rebuilt once the camera settles (MapCanvasControl schedules that
        // frame). Nothing about the terrain changed, or the inputs would not match.
        var terrainParts = keepable ? TerrainParts() : null;
        var deviceScale = Math.Max(1e-4, Math.Sqrt(canvas.TotalMatrix.ScaleX * canvas.TotalMatrix.ScaleX + canvas.TotalMatrix.SkewY * canvas.TotalMatrix.SkewY));
        // Above the editor's detail headroom, magnify the same terrain pixels, just as
        // stamps do. Keep display scaling, but do not recalculate a coastline's blur and
        // morphology at ever larger radii for magnification alone. Export remains exact.
        var terrainScale = options.IsExport ? deviceScale
            : deviceScale * Math.Min(1, TerrainDetailHeadroom / Math.Max(1e-4, options.ViewScale));
        if (cacheable && options.TerrainRefinement is { } refinement)
        {
            if (options.Stats is { } refinementStats) refinementStats.TerrainRefinementBuildMilliseconds = refinement.LastBuildMilliseconds;
            var exact = options.Terrain!.TryGetExact(terrainParts!, view, terrainScale, options.GpuContext);
            if (exact is null && refinement.Take(terrainParts!, view, terrainScale, out var tag) is { } ready)
            {
                var adopted = tag is TerrainPatch patch ? ComposeTerrainPatch(ready, patch, view, terrainScale, options) : ready;
                if (adopted is null) refinement.PreferFullBuild = true;
                else exact = options.Terrain.Get(terrainParts!, view, terrainScale, options.GpuContext, (_, _) => adopted);
            }
            if (exact is not null)
            {
                canvas.DrawImage(exact, RenderImageCache.Placement(exact, view, terrainScale),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                DrawTerrainLiveOverlay(canvas, document, lands, view, options);
                return;
            }
        }
        if (keepable && (options.FastTerrain || options.TerrainRefinement is not null)
            && options.Terrain!.TryGetBest(terrainParts!, view, terrainScale,
                options.GpuContext, out var keptView, out var keptScale) is { } kept)
        {
            var placement = RenderImageCache.Placement(kept, keptView, keptScale);
            // The placement is a single-precision rectangle: at world coordinates in the thousands
            // its edges differ from the double view by up to a few 1e-4, which a 1e-6 slack read
            // as "not covered" and made every frame of a deep zoom rerun the plain passes (35-60 ms).
            // A thousandth of a world unit is far below a screen pixel at any editor scale.
            const double slack = 1e-3;
            var covered = placement.Left <= view.Left + slack && placement.Top <= view.Top + slack
                && placement.Right >= view.Right - slack && placement.Bottom >= view.Bottom - slack;
            var refining = false;
            // A kept image that covers only part of the view still stands in while the exact
            // terrain is prepared in the background: the uncovered part gets the plain passes. A
            // view that no kept image covers - back to a scale visited before, a fit to the canvas -
            // used to rebuild every coast in the frame (0.7-1.3 s on a 4096×3072 map of tiers).
            if (!options.FastTerrain && options.RequestRefinedFrame is { } requestFrame
                && options.RiverOverrides is null && options.RoadOverrides is null
                && surfaces.All(surface => !surface.Owner.Effects.ClippingEnabled))
            {
                // Prepare terrain from immutable inputs with private CPU resources. Even a
                // clipped GPU replay can stall on morphology filters and shader compilation;
                // the render thread only uploads the complete result. No borrowed context,
                // asset source or frame cache crosses threads.
                refining = options.TerrainRefinement!.Queue(terrainParts!, view, terrainScale,
                    BackgroundTerrain(document, lands, surfaces, view, terrainScale, options), requestFrame);
            }
            if (refining && options.Stats is { } pendingStats) pendingStats.TerrainRefinementsPending++;
            if ((options.FastTerrain || refining) && !covered)
            {
                if (options.Stats is { } uncoveredStats)
                {
                    uncoveredStats.TerrainUncovered++;
                    uncoveredStats.TerrainUncoveredDetail = FormattableString.Invariant($"view=({view.Left:0.#},{view.Top:0.#},{view.Right:0.#},{view.Bottom:0.#}) kept=({placement.Left:0.#},{placement.Top:0.#},{placement.Right:0.#},{placement.Bottom:0.#}) keptScale={keptScale:0.###} wanted={terrainScale:0.###} count={options.Terrain.Count}");
                }
                // Only the newly exposed area is missing. Drawing the fallback underneath the
                // kept image would apply translucent ground/water twice in their overlap. The
                // plain passes in any case: the exact terrain of this view is on its way.
                var uncoveredOptions = options.FastTerrain ? options : new RenderOptions
                {
                    ViewScale = options.ViewScale, VisibleWorld = view, LodTier = options.LodTier,
                    TileCache = options.TileCache, GpuContext = options.GpuContext, FastTerrain = true, Stats = options.Stats,
                };
                canvas.Save();
                canvas.ClipRect(placement, SKClipOperation.Difference);
                DrawTerrainPasses(canvas, document, lands, surfaces, view, uncoveredOptions);
                canvas.Restore();
            }
            if (options.FastTerrain || refining)
            {
                canvas.DrawImage(kept, placement, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (options.Stats is { } keptStats) keptStats.TerrainReused++;
                DrawTerrainLiveOverlay(canvas, document, lands, view, options);
                return;
            }
        }
        // The document changed under this very view - a stroke was released, ground was added or
        // taken away - and no image exists for the new inputs. Instead of holding the frame for
        // every coast and river pass (0.4-0.6 s on a regional map), the previous terrain of the
        // same view is drawn with the changed ground laid over it in its plain look - the look the
        // live stroke already had - and the exact terrain is prepared in the background; the frame
        // that adopts it is requested when it is done.
        if (cacheable && options.TerrainRefinement is { } staleRefinement && options.RequestRefinedFrame is { } staleFrame
            && options.RiverOverrides is null && options.RoadOverrides is null
            && surfaces.All(surface => !surface.Owner.Effects.ClippingEnabled)
            && options.Terrain!.TryGetStaleNear(view, terrainScale, options.GpuContext, out var staleParts,
                out var staleView, out var staleScale) is { } previous
            && OnlyLandsDiffer(terrainParts!, staleParts, out var changedLands, allowSame: true)
            && QueueStaleTerrain(staleRefinement, terrainParts!, view, terrainScale, staleParts, staleView, staleScale,
                changedLands, document, lands, surfaces, options, staleFrame))
        {
            // The previous terrain, of this view or - after a view change - of the nearest one,
            // resampled; whatever it does not cover gets the plain passes.
            var stalePlacement = RenderImageCache.Placement(previous, staleView, staleScale);
            // The stand-in does not change until the exact terrain arrives (1.5-3 s): it is drawn
            // once and kept while the view stands still. Laying the changed land over the previous
            // image in every frame cost 10-90 ms a frame after each stroke on the owner's test map.
            DrawTerrainStandIn(canvas, options, StaleTerrainSlot,
                [.. terrainParts!, previous, staleView, staleScale, terrainScale, options.ViewScale, options.LodTier], target =>
            {
                const double staleSlack = 1e-3;
                if (stalePlacement.Left > view.Left + staleSlack || stalePlacement.Top > view.Top + staleSlack
                    || stalePlacement.Right < view.Right - staleSlack || stalePlacement.Bottom < view.Bottom - staleSlack)
                {
                    var plainOptions = new RenderOptions
                    {
                        ViewScale = options.ViewScale, VisibleWorld = view, LodTier = options.LodTier,
                        TileCache = options.TileCache, GpuContext = options.GpuContext, FastTerrain = true, Stats = options.Stats,
                    };
                    target.Save();
                    target.ClipRect(stalePlacement, SKClipOperation.Difference);
                    DrawTerrainPasses(target, document, lands, surfaces, view, plainOptions);
                    target.Restore();
                }
                target.DrawImage(previous, stalePlacement, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                foreach (var (current, before) in changedLands) DrawLandChange(target, document, current, before, view, options);
            });
            if (options.Stats is { } staleStats) { staleStats.TerrainRefinementsPending++; staleStats.TerrainReused++; }
            DrawTerrainLiveOverlay(canvas, document, lands, view, options);
            return;
        }
        // Nothing of the terrain has been built in this view's cache yet - a map was just opened,
        // or memory pressure dropped every kept image. There is no earlier exact frame the user
        // could see jump, so the first frame draws the plain terrain (the same fast passes a pan
        // uses) and the exact one is prepared in the background, instead of holding the first
        // frame of the editor for the whole coast, bank and material work (0.7 s on the 4096×3072
        // regional map). After that, an edit still rebuilds its exact terrain in the same frame.
        if (cacheable && options.Terrain!.Count == 0 && options.TerrainRefinement is { } first
            && options.RequestRefinedFrame is { } firstFrame
            && options.RiverOverrides is null && options.RoadOverrides is null
            && surfaces.All(surface => !surface.Owner.Effects.ClippingEnabled)
            && first.Queue(terrainParts!, view, terrainScale,
                BackgroundTerrain(document, lands, surfaces, view, terrainScale, options), firstFrame))
        {
            var plain = new RenderOptions
            {
                ViewScale = options.ViewScale, VisibleWorld = view, LodTier = options.LodTier,
                TileCache = options.TileCache, GpuContext = options.GpuContext, FastTerrain = true, Stats = options.Stats,
            };
            // Kept as well while the first exact terrain is prepared (18-27 ms a frame for the
            // first seconds of an opened map otherwise).
            DrawTerrainStandIn(canvas, options, StaleTerrainSlot, [.. terrainParts!, terrainScale, options.ViewScale, options.LodTier],
                target => DrawTerrainPasses(target, document, lands, surfaces, view, plain));
            if (options.Stats is { } firstStats) firstStats.TerrainRefinementsPending++;
            DrawTerrainLiveOverlay(canvas, document, lands, view, options);
            return;
        }
        if (cacheable)
        {
            var parts = terrainParts!;
            var idle = options.WithoutLiveState();
            var image = options.Terrain!.Get(parts, view, terrainScale, options.GpuContext, (width, height) =>
            {
                using var surface = RenderImageCache.CreateSurface(options.GpuContext, width, height, view, terrainScale);
                if (surface is null) return null;
                DrawTerrainPasses(surface.Canvas, document, lands, surfaces, view, idle);
                surface.Canvas.Flush();
                return surface.Snapshot();
            });
            if (image is not null)
            {
                canvas.DrawImage(image, RenderImageCache.Placement(image, view, terrainScale),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                DrawTerrainLiveOverlay(canvas, document, lands, view, options);
                return;
            }
        }

        DrawTerrainPasses(canvas, document, lands, surfaces, view, options);
    }

    /// <summary>
    /// A partial terrain build: <see cref="RenderView"/> was drawn in the background, and the pixels
    /// <see cref="Copy"/> (in the pixels of the view's image) replace those of the image built for
    /// <see cref="BaseParts"/> - the terrain before the edit.
    /// </summary>
    private sealed record TerrainPatch(object[] BaseParts, WorldBounds RenderView, SKRectI Copy, SKPointI Offset);

    /// <summary>
    /// Queues the exact terrain after an edit. When the previous image is of this very view and
    /// scale and the edit changed only some tiles of its lands, only the part of the view those
    /// tiles can affect is drawn: every land, coast, cliff and river effect reaches no farther from
    /// its cause than <see cref="TerrainEffectReach"/>. A stroke rebuilt the whole view on the CPU
    /// (1.5-3 s on the owner's test map, 2560×1369), during which the plain look stood in.
    /// </summary>
    private static bool QueueStaleTerrain(TerrainRefinement refinement, IReadOnlyList<object> parts, WorldBounds view,
        double scale, IReadOnlyList<object> staleParts, WorldBounds staleView, double staleScale,
        List<(LandLayer Current, LandLayer Before)> changed, MapDocument document, List<LandLayer> lands,
        List<Surface> surfaces, RenderOptions options, Action ready)
    {
        if (!refinement.PreferFullBuild && staleView == view && staleScale == scale
            && TerrainPatchRegion(changed, lands, surfaces.Any(surface => surface.River is not null), view, scale,
                ActiveSea(document, options)?.Reach ?? 0) is { } region)
        {
            var (renderView, copy, offset) = region;
            return refinement.Queue(parts, view, scale,
                BackgroundTerrain(document, lands, surfaces, renderView, scale, options), ready,
                new TerrainPatch([.. staleParts], renderView, copy, offset));
        }
        refinement.PreferFullBuild = false;
        return refinement.Queue(parts, view, scale, BackgroundTerrain(document, lands, surfaces, view, scale, options), ready);
    }

    /// <summary>
    /// The part of the view an edit of these lands can change, or null when the whole view must be
    /// rebuilt: a change other than coverage or paint tiles (style, level, visibility, mask, name),
    /// or more than about half of the view. The copied pixels are the changed tiles widened by the
    /// effect reach; they are drawn with the reach once more around them, so a blur or a distance
    /// near their edge sees everything it would see in the whole view.
    /// </summary>
    private static (WorldBounds RenderView, SKRectI Copy, SKPointI Offset)? TerrainPatchRegion(
        List<(LandLayer Current, LandLayer Before)> changed, List<LandLayer> lands, bool rivers, WorldBounds view, double scale,
        double seaReach = 0)
    {
        if (changed.Count == 0) return null;
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        void Add(TileKey key)
        {
            var rect = TileRect(key);
            left = Math.Min(left, rect.Left); top = Math.Min(top, rect.Top);
            right = Math.Max(right, rect.Right); bottom = Math.Max(bottom, rect.Bottom);
        }
        foreach (var (current, before) in changed)
        {
            if (!Equals(before with { GeometryTiles = current.GeometryTiles, PaintTiles = current.PaintTiles }, current)) return null;
            foreach (var key in current.GeometryTiles.Keys.Union(before.GeometryTiles.Keys))
                if (!ReferenceEquals(current.GeometryTiles.TryGet(key), before.GeometryTiles.TryGet(key))) Add(key);
            foreach (var key in current.PaintTiles.Keys.Union(before.PaintTiles.Keys))
                if (!ReferenceEquals(current.PaintTiles.TryGet(key), before.PaintTiles.TryGet(key))) Add(key);
        }
        if (left == double.MaxValue)
        {
            // New records of the same tiles: nothing to redraw, the previous image is the result.
            var pixel = new WorldBounds(view.Left, view.Top, view.Left + (1 / scale), view.Top + (1 / scale));
            return (pixel, SKRectI.Empty, SKPointI.Empty);
        }
        // The sea style reads the distance to the nearest shore: an edit changes it that far out.
        var reach = Math.Max(TerrainEffectReach(lands, rivers, scale), seaReach + (2 / Math.Max(1e-4, scale)));
        var (width, height) = RenderImageCache.SizeOf(view, scale);
        // The copied pixels, in the pixels of the view's image.
        var x0 = Math.Clamp((int)Math.Floor((left - reach - view.Left) * scale), 0, width);
        var y0 = Math.Clamp((int)Math.Floor((top - reach - view.Top) * scale), 0, height);
        var x1 = Math.Clamp((int)Math.Ceiling((right + reach - view.Left) * scale), 0, width);
        var y1 = Math.Clamp((int)Math.Ceiling((bottom + reach - view.Top) * scale), 0, height);
        if (x1 <= x0 || y1 <= y0 || (long)(x1 - x0) * (y1 - y0) > (long)width * height * 55 / 100) return null;
        // Drawn with the reach once more around them, on the same pixel grid.
        var margin = (int)Math.Ceiling(reach * scale) + 2;
        var rx0 = x0 - margin; var ry0 = y0 - margin; var rx1 = x1 + margin; var ry1 = y1 + margin;
        var renderView = new WorldBounds(view.Left + (rx0 / scale), view.Top + (ry0 / scale),
            view.Left + (rx1 / scale), view.Top + (ry1 / scale));
        return (renderView, new SKRectI(x0, y0, x1, y1), new SKPointI(rx0, ry0));
    }

    /// <summary>
    /// How far, in world units, a change of land coverage can change the terrain around it: the
    /// widest coast effect of any land (outline, shadow, surf), the tallest cliff below a raised
    /// edge with its lean, a river mouth that reads the coast (when the map has water), and two
    /// device pixels of sampling.
    /// </summary>
    private static double TerrainEffectReach(List<LandLayer> lands, bool rivers, double scale)
    {
        var lowest = Math.Min(0, lands.Count > 0 ? lands.Min(land => land.Level) : 0);
        var reach = 0.0;
        foreach (var land in lands)
        {
            var style = land.EdgeStyle;
            var cliff = style.Cliff.Reach(style.Cliff.Steps(land.Level, lowest));
            reach = Math.Max(reach, LandEdgeReach(style) + (cliff * (1 + Math.Abs(style.Cliff.Lean))));
        }
        return reach + (rivers ? RiverCoastRenderer.MaximumMouthRadius : 0) + (2 / Math.Max(1e-4, scale)) + 2;
    }

    /// <summary>
    /// The terrain image after an edit: the image before it with the redrawn part laid over it,
    /// pixel for pixel. Null when the image before it is no longer kept (the whole view is then
    /// rebuilt on the next request). Takes ownership of <paramref name="part"/>.
    /// </summary>
    private static SKImage? ComposeTerrainPatch(SKImage part, TerrainPatch patch, WorldBounds view, double scale, RenderOptions options)
    {
        using (part)
        {
            if (options.Terrain!.TryGetExact(patch.BaseParts, view, scale, options.GpuContext) is not { } baseImage) return null;
            var info = new SKImageInfo(baseImage.Width, baseImage.Height, SKColorType.RgbaF16, SKAlphaType.Premul);
            using var surface = (options.GpuContext is { } context ? SKSurface.Create(context, false, info) : null) ?? SKSurface.Create(info);
            if (surface is null) return null;
            var canvas = surface.Canvas;
            using var replace = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawImage(baseImage, 0, 0, replace);
            var copy = patch.Copy;
            var source = new SKRect(copy.Left - patch.Offset.X, copy.Top - patch.Offset.Y,
                copy.Right - patch.Offset.X, copy.Bottom - patch.Offset.Y);
            if (!copy.IsEmpty)
                canvas.DrawImage(part, source, new SKRect(copy.Left, copy.Top, copy.Right, copy.Bottom),
                    new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), replace);
            canvas.Flush();
            if (options.Stats is { } stats) stats.TerrainPatches++;
            return surface.Snapshot();
        }
    }

    /// <summary>The slot of <see cref="LayerImageCache"/> that keeps the terrain's stand-in while the exact terrain is prepared.</summary>
    private static readonly LayerId StaleTerrainSlot = new(new Guid("b6a2f0d4-5c1e-4f7a-9d3b-2e8c7a1f6d40"));

    /// <summary>
    /// Draws a stand-in for the terrain (the previous image with the change laid over it, or the
    /// plain passes) from a kept device-pixel image while the view stands still; directly otherwise.
    /// </summary>
    private static void DrawTerrainStandIn(SKCanvas canvas, RenderOptions options, LayerId slot, object[] parts, Action<SKCanvas> draw)
    {
        // Only a stand-in the previous frame already showed is kept: a slider drag makes a new one
        // every frame, and building an image for it each time cost more than drawing it.
        var repeated = _lastStandInParts is { } last && last.AsSpan().SequenceEqual(parts);
        _lastStandInParts = parts;
        if (repeated && options.LayerImages is { LastMotion: FrameMotion.Still } images && options.Stats is not null && !options.IsExport
            && images.TryDraw(canvas, slot, parts, options.GpuContext, draw, static () => true, slot: 2))
            return;
        draw(canvas);
    }

    [ThreadStatic] private static object[]? _lastStandInParts;

    /// <summary>
    /// True when the two input lists of a terrain image differ only in land layers (an edit of
    /// coverage, paint or style), pairing each changed land with the one it replaces. Water,
    /// roads and layer settings must be the same, or the previous image cannot stand in.
    /// </summary>
    private static bool OnlyLandsDiffer(IReadOnlyList<object> current, IReadOnlyList<object> previous,
        out List<(LandLayer Current, LandLayer Before)> changed, bool allowSame = false)
    {
        changed = [];
        var aligned = current.Count == previous.Count;
        for (var i = 0; i < current.Count && aligned; i++)
        {
            if (Equals(current[i], previous[i])) continue;
            if (current[i] is LandLayer now && previous[i] is LandLayer then && now.Id == then.Id) changed.Add((now, then));
            else aligned = false;
        }
        if (aligned) return allowSame || changed.Count > 0;

        // The lands moved in the terrain order (a change of level), or one was added («Новая
        // земля» with its first stroke) or removed (its undo): water and roads must still be the
        // same, in the same order, and the lands are paired by identity. A level-only change draws
        // nothing over the previous image; an added land is its whole ground in the plain look, a
        // removed one is its ground given back to the sea. Each of these was a whole terrain
        // rebuild in the frame (0.76-0.88 s on the 4096×3072 test map of the owner's art).
        changed = [];
        var others = 0;
        var before = new Dictionary<LayerId, LandLayer>();
        foreach (var part in previous)
        {
            if (part is LandLayer land) before[land.Id] = land;
        }
        foreach (var part in current)
        {
            if (part is not LandLayer) others++;
        }
        if (others != previous.Count - before.Count) return false;
        int ci = 0, pi = 0;
        while (true)
        {
            while (ci < current.Count && current[ci] is LandLayer) ci++;
            while (pi < previous.Count && previous[pi] is LandLayer) pi++;
            if (ci >= current.Count || pi >= previous.Count) break;
            if (!Equals(current[ci], previous[pi])) return false;
            ci++; pi++;
        }
        foreach (var part in current)
        {
            if (part is not LandLayer now) continue;
            if (before.Remove(now.Id, out var then))
            {
                if (!Equals(now, then)) changed.Add((now, then));
            }
            else changed.Add((now, Bare(now)));
        }
        foreach (var gone in before.Values) changed.Add((Bare(gone), gone));
        return allowSame || changed.Count > 0;

        static LandLayer Bare(LandLayer land) => land with { GeometryTiles = TileMap.Empty(1), PaintTiles = TileMap.Empty(4) };
    }

    /// <summary>
    /// What changed between two states of one land, in the plain look of the live stroke: the
    /// added ground in the fill colour with the material, the removed ground in the sea colour.
    /// Drawn over the previous terrain image while the exact coast is prepared in the background.
    /// </summary>
    private static void DrawLandChange(SKCanvas canvas, MapDocument document, LandLayer current, LandLayer before,
        WorldBounds view, RenderOptions options)
    {
        var style = current.EdgeStyle;
        var sameShape = ReferenceEquals(current.GeometryTiles, before.GeometryTiles);
        using var group = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(current.Opacity)) };
        if (!sameShape || !ReferenceEquals(current.PaintTiles, before.PaintTiles) || !Equals(current.EdgeStyle, before.EdgeStyle))
        {
            canvas.SaveLayer(ToRect(view), group);
            using (var tint = SKColorFilter.CreateBlendMode(ParseColor(style.FillColor), SKBlendMode.SrcIn))
            using (var fill = new SKPaint { ColorFilter = tint })
                DrawLandCoverage(canvas, [current], view, options, fill);
            if (MapTextureLibrary.Image(style.FillTextureId) is not null && style.FillTextureOpacity > 0)
            {
                canvas.SaveLayer(ToRect(view), null);
                using (var plain = new SKPaint()) DrawLandCoverage(canvas, [current], view, options, plain);
                using (var shape = new SKPaint { BlendMode = SKBlendMode.SrcIn })
                {
                    canvas.SaveLayer(ToRect(view), shape);
                    MapTextureLibrary.DrawFill(canvas, style.FillTextureId, style.FillTextureScale, style.FillTextureOpacity, ToRect(view));
                    canvas.Restore();
                }
                canvas.Restore();
            }
            if (current.PaintTiles.TileCount > 0)
            {
                canvas.SaveLayer(ToRect(view), null);
                DrawTiles(canvas, current.Id, current.PaintTiles, view, options, TileImageKind.Content);
                using (var keepPaint = new SKPaint { BlendMode = SKBlendMode.DstIn })
                {
                    canvas.SaveLayer(ToRect(view), keepPaint);
                    using (var plain = new SKPaint()) DrawLandCoverage(canvas, [current], view, options, plain);
                    canvas.Restore();
                }
                canvas.Restore();
            }
            // Only the added ground when the shape changed: the finished look of the unchanged
            // ground stays as the previous image shows it. A change of paint or style redraws the
            // whole piece in its plain look.
            if (!sameShape && ReferenceEquals(current.PaintTiles, before.PaintTiles) && Equals(current.EdgeStyle, before.EdgeStyle))
            {
                using var cut = new SKPaint { BlendMode = SKBlendMode.DstOut };
                DrawLandCoverage(canvas, [before], view, options, cut);
            }
            if (current.Mask is { Enabled: true } mask && HasEffectiveMask(mask, view)) ApplyMask(canvas, current.Id, mask, view, options);
            canvas.Restore();
        }
        if (!sameShape)
        {
            canvas.SaveLayer(ToRect(view), null);
            using (var tint = SKColorFilter.CreateBlendMode(ParseColor(document.BaseColor.Color), SKBlendMode.SrcIn))
            using (var sea = new SKPaint { ColorFilter = tint })
                DrawLandCoverage(canvas, [before], view, options, sea);
            using (var keep = new SKPaint { BlendMode = SKBlendMode.DstOut })
                DrawLandCoverage(canvas, [current], view, options, keep);
            canvas.Restore();
        }
    }

    /// <summary>
    /// The live part of a raster gesture on the terrain, over the kept terrain image: the new
    /// ground of a land-shape stroke in the land's colour and material (or the sea colour where
    /// land is being taken away), the material of a land-paint stroke inside the land's coverage
    /// (or the bare land where paint is being erased), and the red tint of a land mask being
    /// edited. The exact coast of the new edge follows on release, when the document changes and
    /// the terrain is rebuilt once; during the gesture this overlay is all that is drawn per frame.
    /// </summary>
    private static void DrawTerrainLiveOverlay(SKCanvas canvas, MapDocument document, List<LandLayer> lands,
        WorldBounds view, RenderOptions options)
    {
        DrawCliffMaskOverlay(canvas, lands, view, options);
        if (options.Preview is { IsMask: false, IsCliffMask: false } preview
            && lands.FirstOrDefault(land => land.Id == preview.LayerId) is { } land)
        {
            // The stroke joins the land's own group: its opacity and mask apply to the new ground
            // exactly as they will once the stroke is committed and the land drawn in full.
            using var group = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(land.Opacity)) };
            canvas.SaveLayer(ToRect(view), group);
            if (preview.IsLandPaint)
            {
                if (preview.Eraser)
                    DrawLandShapePreview(canvas, land, new StrokePreview
                    { LayerId = land.Id, Coverage = preview.Coverage, Opacity = preview.Opacity }, view, options);
                else DrawStrokePreview(canvas, preview, view, options);
                using var keep = new SKPaint { BlendMode = SKBlendMode.DstIn };
                canvas.SaveLayer(ToRect(view), keep);
                using (var plain = new SKPaint()) DrawLandCoverage(canvas, [land], view, options, plain);
                canvas.Restore();
            }
            else if (preview.Eraser)
            {
                DrawStrokePreview(canvas, new StrokePreview
                { LayerId = land.Id, Coverage = preview.Coverage, Opacity = preview.Opacity, Color = document.BaseColor.Color }, view, options);
            }
            else DrawLandShapePreview(canvas, land, preview, view, options);
            if (land.Mask is { Enabled: true } mask && HasEffectiveMask(mask, view)) ApplyMask(canvas, land.Id, mask, view, options);
            canvas.Restore();
        }
        if (options.MaskEditLayerId is { } editing && lands.FirstOrDefault(land => land.Id == editing) is { Mask: not null } masked)
            DrawMaskOverlay(canvas, masked, view, options);
    }

    /// <summary>
    /// Prepares the exact terrain of a view from immutable inputs with private CPU resources. Even
    /// a clipped GPU replay can stall on morphology filters and shader compilation; the render
    /// thread only uploads the complete result. No borrowed context, asset source or frame cache
    /// crosses threads.
    /// </summary>
    private static Func<SKImage?> BackgroundTerrain(MapDocument document, List<LandLayer> lands, List<Surface> surfaces,
        WorldBounds view, double terrainScale, RenderOptions options)
    {
        var worldScale = options.ViewScale;
        var tier = options.LodTier;
        return () =>
        {
            using var tiles = new TileImageCache(1024, 64L * 1024 * 1024);
            tiles.BeginFrame();
            try
            {
                var (width, height) = RenderImageCache.SizeOf(view, terrainScale);
                using var surface = RenderImageCache.CreateSurface(null, width, height, view, terrainScale);
                if (surface is null) return null;
                var backgroundOptions = new RenderOptions
                { ViewScale = worldScale, VisibleWorld = view, LodTier = tier, TileCache = tiles };
                DrawTerrainPasses(surface.Canvas, document, lands, surfaces, view, backgroundOptions);
                return surface.Snapshot();
            }
            finally { tiles.EndFrame(); }
        };
    }

    private static void DrawTerrainPasses(SKCanvas canvas, MapDocument document, List<LandLayer> lands,
        List<Surface> surfaces, WorldBounds view, RenderOptions options)
    {
        DrawSea(canvas, document, lands, view, options);
        var levels = lands.Select(land => land.Level).Concat(surfaces.Select(surface => surface.Level))
            .Distinct().OrderBy(level => level).ToList();
        foreach (var level in levels)
        {
            foreach (var land in lands)
            {
                if (land.Level != level) continue;
                if (options.Stats is { } stats) stats.LayersDrawn++;
                var landStarted = Stopwatch.GetTimestamp();
                DrawLayer(canvas, document, land, null, view, options);
                if (options.Stats is { } landStats) landStats.LandMilliseconds += Stopwatch.GetElapsedTime(landStarted).TotalMilliseconds;
            }

            var atLevel = surfaces.Where(surface => surface.Level == level).ToList();
            if (atLevel.Count == 0) continue;
            var below = lands.Where(land => land.Level <= level).ToList();
            var water = atLevel.Where(surface => surface.River is not null).ToList();
            foreach (var group in atLevel.GroupBy(surface => surface.Owner))
            {
                var owner = group.Key;
                var run = group.OrderBy(surface => surface.Order).ToList();
                // Only unmasked, opaque neighbours can safely remove a shared bank. Hidden
                // portions of another layer must never erase the outline of this layer.
                var neighbours = SeamCompatible(owner)
                    ? water.Where(surface => surface.Owner.Id != owner.Id && SeamCompatible(surface.Owner))
                        .Select(surface => surface.River!).ToArray()
                    : [];
                var waterStarted = Stopwatch.GetTimestamp();
                DrawLayer(canvas, document, owner, LowerLayer(document, owner), view, options,
                    contentOverride: target => DrawSurfaceRun(target, run, view, options,
                        below.Count > 0 ? (coverage, paint) => DrawLandCoverage(coverage, below, view, options, paint) : null,
                        neighbours));
                if (options.Stats is { } waterStats) waterStats.WaterMilliseconds += Stopwatch.GetElapsedTime(waterStarted).TotalMilliseconds;
            }
        }

        static bool SeamCompatible(ObjectLayer layer) => layer.Opacity == 1 && layer.Blend == "Normal"
            && layer.Mask is not { Enabled: true } && !layer.Effects.ClippingEnabled;
    }

    private static MapLayer? LowerLayer(MapDocument document, MapLayer layer)
    {
        for (var i = 1; i < document.Layers.Count; i++)
            if (document.Layers[i].Id == layer.Id) return document.Layers[i - 1];
        return null;
    }

    private static bool TryIntersect(WorldBounds first, WorldBounds second, out WorldBounds result)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        result = new WorldBounds(left, top, right, bottom);
        return right > left && bottom > top;
    }

    private static void DrawBoundaryOverlay(
        SKCanvas canvas, CanvasBoundaryOverlay? overlay, double viewScale)
    {
        if (overlay is null)
        {
            return;
        }

        var px = (float)(1 / Math.Max(0.001, viewScale));
        if (overlay.OldCanvas is { } oldBounds && overlay.NewCanvas is { } newBounds)
        {
            using var crop = new SKPaint
            {
                Color = new SKColor(224, 82, 82, 72),
                Style = SKPaintStyle.Fill,
                IsAntialias = false,
            };
            using var expand = new SKPaint
            {
                Color = new SKColor(70, 190, 126, 64),
                Style = SKPaintStyle.Fill,
                IsAntialias = false,
            };
            DrawDifference(canvas, oldBounds, newBounds, crop);
            DrawDifference(canvas, newBounds, oldBounds, expand);

            using var oldLine = new SKPaint
            {
                Color = new SKColor(255, 183, 77, 235),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2 * px,
                PathEffect = SKPathEffect.CreateDash([7 * px, 5 * px], 0),
                IsAntialias = true,
            };
            using var newLine = new SKPaint
            {
                Color = new SKColor(74, 198, 255, 255),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2 * px,
                IsAntialias = true,
            };
            canvas.DrawRect(ToRect(oldBounds), oldLine);
            canvas.DrawRect(ToRect(newBounds), newLine);
        }

        if (overlay.Region is { } region)
        {
            using var fill = new SKPaint
            {
                Color = new SKColor(74, 198, 255, 42),
                Style = SKPaintStyle.Fill,
                IsAntialias = false,
            };
            using var line = new SKPaint
            {
                Color = new SKColor(74, 198, 255, 255),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2 * px,
                IsAntialias = true,
            };
            canvas.DrawRect(ToRect(region), fill);
            canvas.DrawRect(ToRect(region), line);
        }
    }

    private static void DrawDifference(
        SKCanvas canvas, WorldBounds outer, WorldBounds inner, SKPaint paint)
    {
        if (!TryIntersect(outer, inner, out var overlap))
        {
            canvas.DrawRect(ToRect(outer), paint);
            return;
        }

        Draw(outer.Left, outer.Top, outer.Right, overlap.Top);
        Draw(outer.Left, overlap.Bottom, outer.Right, outer.Bottom);
        Draw(outer.Left, overlap.Top, overlap.Left, overlap.Bottom);
        Draw(overlap.Right, overlap.Top, outer.Right, overlap.Bottom);

        void Draw(double left, double top, double right, double bottom)
        {
            if (right > left && bottom > top)
            {
                canvas.DrawRect(ToRect(new WorldBounds(left, top, right, bottom)), paint);
            }
        }
    }

    private static void DrawLayer(
        SKCanvas canvas, MapDocument document, MapLayer layer, MapLayer? clippingSource,
        WorldBounds view, RenderOptions options, bool drawRivers = true, Action<SKCanvas>? contentOverride = null,
        bool maskOverlay = false)
    {
        void DrawContent(bool includePreview)
        {
            if (contentOverride is not null) contentOverride(canvas);
            else DrawLayerContent(canvas, document, layer, view, options, includePreview, drawRivers);
        }
        var mask = layer.Mask is { Enabled: true } m
            && HasEffectiveMask(m, view)
                ? m
                : null;
        var maskEdit = options.MaskEditLayerId == layer.Id && layer.Mask is not null;
        var effects = (layer as ObjectLayer)?.Effects;
        var clipsToLower = effects?.ClippingEnabled == true
            && clippingSource is { Visible: true, Opacity: > 0 };

        // The live eraser preview subtracts alpha (DstOut). Without a group of its own it does
        // that to the frame itself, not to this layer: the window loses its own pixels and the
        // desktop shows through the hole for as long as the button is held.
        var erasesInPreview = options.Preview is { Eraser: true } erasing
            && erasing.LayerId == layer.Id;
        var needsGroup = mask is not null || clipsToLower || erasesInPreview
            || layer.Opacity < 1.0 || layer.Blend != "Normal";
        using var colorPass = layer is FilterLayer { Settings: var colorSettings } && contentOverride is null
            ? FilterColorPass(colorSettings, options)
            : null;

        if (colorPass is not null)
        {
            // The group starts as the recoloured copy of everything drawn below it; opacity and
            // the mask then decide how much of that copy replaces the original.
            using var groupPaint = new SKPaint
            {
                Color = SKColors.White.WithAlpha((byte)Math.Clamp(Math.Round(layer.Opacity * 255), 0, 255)),
                BlendMode = ParseBlend(layer.Blend),
            };
            canvas.SaveLayer(new SKCanvasSaveLayerRec { Bounds = ToRect(view), Paint = groupPaint, Backdrop = colorPass });
        }
        else if (needsGroup)
        {
            using var groupPaint = new SKPaint
            {
                Color = SKColors.White.WithAlpha((byte)Math.Clamp(Math.Round(layer.Opacity * 255), 0, 255)),
                BlendMode = ParseBlend(layer.Blend),
            };

            canvas.SaveLayer(ToRect(view), groupPaint);
        }
        else
        {
            canvas.Save();
        }

        if (effects is { OuterShadowOpacity: > 0 } && options.LodTier != RenderLodTier.Overview)
        {
            using var shadowPaint = new SKPaint
            {
                ImageFilter = SKImageFilter.CreateDropShadowOnly(
                    (float)effects.ShadowOffsetX, (float)effects.ShadowOffsetY,
                    (float)effects.ShadowBlur, (float)effects.ShadowBlur,
                    ParseColor(effects.ShadowColor).WithAlpha(Alpha(effects.OuterShadowOpacity))),
            };
            canvas.SaveLayer(ToRect(view), shadowPaint);
            DrawContent(includePreview: false);
            canvas.Restore();
        }

        DrawContent(includePreview: true);

        if (effects is { InnerShadowOpacity: > 0 } && options.LodTier != RenderLodTier.Overview)
        {
            canvas.SaveLayer(ToRect(view), null);
            using (var shadowPaint = new SKPaint
            {
                ImageFilter = SKImageFilter.CreateDropShadowOnly(
                    (float)effects.ShadowOffsetX, (float)effects.ShadowOffsetY,
                    (float)effects.ShadowBlur, (float)effects.ShadowBlur,
                    ParseColor(effects.ShadowColor).WithAlpha(Alpha(effects.InnerShadowOpacity))),
            })
            {
                canvas.SaveLayer(ToRect(view), shadowPaint);
                DrawContent(includePreview: false);
                canvas.Restore();
            }
            using (var inside = new SKPaint { BlendMode = SKBlendMode.DstIn })
            {
                canvas.SaveLayer(ToRect(view), inside);
                DrawContent(includePreview: false);
                canvas.Restore();
            }
            canvas.Restore();
        }

        if (mask is not null)
        {
            ApplyMask(canvas, layer.Id, mask, view, options);
        }

        if (clipsToLower && clippingSource is not null)
        {
            using var clipPaint = new SKPaint { BlendMode = SKBlendMode.DstIn };
            canvas.SaveLayer(ToRect(view), clipPaint);
            DrawClippingCoverage(canvas, document, clippingSource, view, options);
            if (clippingSource is not LandLayer && clippingSource.Mask is { Enabled: true } lowerMask && HasEffectiveMask(lowerMask, view))
                ApplyMask(canvas, clippingSource.Id, lowerMask, view, options);
            canvas.Restore();
        }

        // A surface run drawn through contentOverride is one piece of the layer and shows no
        // overlay; the kept objects of a layer whose mask is being edited are the whole layer and do.
        if (maskEdit && (contentOverride is null || maskOverlay))
        {
            DrawMaskOverlay(canvas, layer, view, options);
        }

        canvas.Restore();
    }

    private static void DrawLayerContent(
        SKCanvas canvas, MapDocument document, MapLayer layer, WorldBounds view,
        RenderOptions options, bool includePreview, bool drawRivers = true)
    {
        switch (layer)
        {
            case BaseColorLayer baseColor:
                using (var page = new SKPaint { Color = ParseColor(baseColor.Color), IsAntialias = false })
                {
                    canvas.DrawRect(0, 0, document.Canvas.Width, document.Canvas.Height, page);
                }
                // The open sea carries the same water material as rivers and lakes, so the water
                // of a map reads as one surface wherever it is.
                if (!options.FastTerrain && baseColor.TextureId is { } pageMaterial)
                    MapTextureLibrary.DrawFill(canvas, pageMaterial, baseColor.TextureScale, baseColor.TextureOpacity,
                        new SKRect(0, 0, document.Canvas.Width, document.Canvas.Height));

                break;

            case ObjectLayer objectLayer:
                // Rivers are terrain and are normally drawn by the terrain pass (DrawTerrain),
                // level by level with the land. Only a shape pass of this layer alone - its
                // shadow, or the clip it lends to the layer above - still includes its water.
                if (drawRivers)
                {
                    var run = LayerSurfaces(objectLayer, view, options).OrderBy(surface => surface.Level).ThenBy(surface => surface.Order).ToList();
                    if (run.Count > 0)
                        DrawSurfaceRun(canvas, run, view, options,
                            document.Layers.OfType<LandLayer>().Any(l => l.Visible && l.Opacity > 0 && l.GeometryTiles.TileCount > 0)
                                ? (target, paint) => DrawAllLandCoverage(target, document, view, options, paint)
                                : null, []);
                }
                if (options.PlacementPreviewLayerId == objectLayer.Id
                    && options.PlacementPreviewObjects is { Count: > 0 } placementPreview)
                {
                    foreach (var previewObject in placementPreview)
                    {
                        DrawLayerStampShadow(
                            canvas, previewObject, document.StampForeground, options);
                    }
                }
                var objectsStarted = Stopwatch.GetTimestamp();
                DrawObjects(canvas, document, objectLayer, view, options);
                if (options.Stats is { } objectStats)
                    objectStats.ObjectsMilliseconds += Stopwatch.GetElapsedTime(objectsStarted).TotalMilliseconds;
                break;

            case LandLayer landLayer:
                DrawLand(canvas, document, landLayer, view, options);
                break;

            case GridLayer gridLayer:
                DrawGridLayer(canvas, gridLayer, view, options);
                break;

            case FilterLayer filterLayer:
                DrawFilterOverlay(canvas, filterLayer.Settings, view, options);
                break;

            case HexLayer hexLayer:
            {
                // A switch of level: the new one fades in over the one just left (220 ms).
                var levelChange = options.HexLevel is { } current && !options.IsExport
                    ? options.HexChunks?.LevelChange(current, options.HexChunks.AnimationNow)
                    : null;
                if (options.HexLevel is { } shown && hexLayer.Level != shown)
                {
                    // Another level of the world, shown faint under the one being edited; the one
                    // just left fades out. Its pictures are kept like the edited level's.
                    var leavingAlpha = levelChange is { } gone && gone.From == hexLayer.Level ? 1 - HexChunkCache.Ease(levelChange.Value.T) : 0;
                    var ghostAlpha = hexLayer.Level == options.HexGhostLevel ? 64 / 255.0 : 0;
                    var alpha = Math.Max(leavingAlpha, ghostAlpha);
                    if (alpha <= 0.004) break;
                    using var faint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(255 * alpha)) };
                    canvas.SaveLayer(faint);
                    HexLayerRenderer.Draw(canvas, hexLayer, view, options.ViewScale, options.IsExport ? null : options.HexChunks,
                        options.GpuContext, null, options.IsExport, options.RequestRefinedFrame, null, false, options.HexPlayerView || options.HexPresentation == HexPresentation.PlayerKnowledge, null);
                    canvas.Restore();
                    break;
                }

                if (levelChange is { } arriving)
                {
                    using var fade = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(255 * HexChunkCache.Ease(arriving.T))) };
                    canvas.SaveLayer(fade);
                    HexLayerRenderer.Draw(canvas, hexLayer, view, options);
                    canvas.Restore();
                    options.RequestRefinedFrame?.Invoke();
                    break;
                }

                HexLayerRenderer.Draw(canvas, hexLayer, view, options);
                break;
            }

            case IRasterContentLayer raster:
                DrawTiles(canvas, layer.Id, raster.ContentTiles, view, options, TileImageKind.Content);
                break;
        }

        if (includePreview && options.Preview is { } preview && preview.LayerId == layer.Id && !preview.IsMask && !preview.IsLandPaint && !preview.IsCliffMask)
        {
            if (layer is LandLayer previewLand && !preview.Eraser && !options.FastTerrain)
                DrawLandShapePreview(canvas, previewLand, preview, view, options);
            else
                DrawStrokePreview(canvas, preview, view, options);
        }
    }

    private static bool HasLandShapePreview(LandLayer land, RenderOptions options) =>
        options.Preview is { IsMask: false, IsLandPaint: false, IsCliffMask: false } preview && preview.LayerId == land.Id;

    /// <summary>
    /// The live part of a land-shape stroke: the new ground in the land's colour with its
    /// material mixed in by the same formula the finished land uses, so the piece being drawn
    /// already looks like the land it is about to join. Coast styling of the new edge follows
    /// on release, when the coverage is one piece again.
    /// </summary>
    private static void DrawLandShapePreview(
        SKCanvas canvas, LandLayer land, StrokePreview preview, WorldBounds view, RenderOptions options)
    {
        var style = land.EdgeStyle;
        // Geometry uses coverage and opacity only; paint colour adjustments do not change it.
        var shapePreview = new StrokePreview
        {
            LayerId = land.Id, Coverage = preview.Coverage,
            Color = style.FillColor, Opacity = preview.Opacity,
        };
        DrawStrokePreview(canvas, shapePreview, view, options);
        if (MapTextureLibrary.Image(style.FillTextureId) is null || style.FillTextureOpacity <= 0)
        {
            return;
        }

        // Match DrawStyled: preserve the coloured fill, then composite the material over it
        // through a separate coverage mask. SrcIn must replace only that mask, not the fill.
        canvas.SaveLayer(ToRect(view), null);
        DrawStrokePreview(canvas, new StrokePreview
        {
            LayerId = land.Id, Coverage = preview.Coverage,
            Color = "#FFFFFFFF", Opacity = preview.Opacity,
        }, view, options);
        using (var shape = new SKPaint { BlendMode = SKBlendMode.SrcIn })
        {
            canvas.SaveLayer(ToRect(view), shape);
            MapTextureLibrary.DrawFill(canvas, style.FillTextureId, style.FillTextureScale,
                style.FillTextureOpacity, ToRect(view));
            canvas.Restore();
        }
        canvas.Restore();
    }

    private static void DrawClippingCoverage(SKCanvas canvas, MapDocument document, MapLayer source,
        WorldBounds view, RenderOptions options)
    {
        // Use original terrain coverage: carving the very river being clipped would create a
        // circular dependency and remove its own clipping surface.
        if (source is LandLayer land)
        {
            using var paint = new SKPaint();
            DrawLandCoverage(canvas, [land], view, options, paint);
        }
        else DrawLayerContent(canvas, document, source, view, options, includePreview: false);
    }

    /// <summary>
    /// Union of every visible Land layer's coverage inside the view, drawn with the caller's
    /// paint. Rivers use it (DstIn) to stay on land; it never evaluates edge styling.
    /// </summary>
    private static void DrawAllLandCoverage(SKCanvas canvas, MapDocument document, WorldBounds view, RenderOptions options, SKPaint paint) =>
        DrawLandCoverage(canvas, document.Layers.OfType<LandLayer>().Where(l => l.Visible && l.Opacity > 0), view, options, paint);

    /// <summary>Coverage of the given Land layers inside the view, drawn with the caller's paint.</summary>
    /// <summary>
    /// Far enough out that a land tile is a few screen pixels, the coverage is drawn from coarse
    /// blocks of 16×16 tiles reduced sixteenfold (one 256² image per 4096-unit block) instead of
    /// one image per tile: a filled 131072² map would otherwise ask for a quarter of a million
    /// tile images per frame and thrash every cache.
    /// </summary>
    private static bool UseCoarseCoverage(RenderOptions options) => !options.IsExport && TileMap.TileSize * options.ViewScale <= 24;

    private static void DrawLandCoverage(SKCanvas canvas, IEnumerable<LandLayer> layers, WorldBounds view, RenderOptions options, SKPaint paint)
    {
        var sampling = options.ViewScale < 0.9
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        foreach (var land in layers)
        {
            var mask = land.Mask is { Enabled: true } m && HasEffectiveMask(m, view) ? m : null;
            var grouped = land.Opacity < 1 || mask is not null;
            if (grouped)
            {
                canvas.SaveLayer(ToRect(view), paint);
                using var opacity = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(land.Opacity)) };
                canvas.SaveLayer(ToRect(view), opacity);
            }
            if (UseCoarseCoverage(options))
                CoarseCoverage.Draw(canvas, land.Id, land.GeometryTiles, view, sampling, grouped ? null : paint, BuildCoverageImage);
            else
            foreach (var key in land.GeometryTiles.KeysIntersecting(view.Left, view.Top, view.Right, view.Bottom))
            {
                var coverage = land.GeometryTiles.TryGet(key)!;
                var image = options.TileCache is not null
                    ? options.TileCache.Get(land.Id, key, TileImageKind.LandCoverage, coverage)
                    : BuildCoverageImage(coverage, TileImageKind.LandCoverage);
                canvas.DrawImage(image, TileRect(key), sampling, grouped ? null : paint);
                if (options.TileCache is null) image.Dispose();
            }
            if (mask is not null)
                ApplyMask(canvas, land.Id, mask, view, options);
            if (grouped) { canvas.Restore(); canvas.Restore(); }
        }
    }

    /// <summary>
    /// Rivers that carve this land, from every object layer, with live editor substitutions:
    /// only water at this land's level or above lies on it; water below passes underneath.
    /// </summary>
    private static List<(ObjectLayer Owner, River River)> LandCuttingRivers(MapDocument document, LandLayer land, WorldBounds view, RenderOptions options)
    {
        var result = new List<(ObjectLayer Owner, River River)>();
        foreach (var layer in document.Layers.OfType<ObjectLayer>().Where(l => l.Visible && l.Opacity > 0))
            foreach (var river in RiverRenderer.Resolve(layer, options))
                if (river.Visible && river.Style.CutsLand && TerrainOrder.Covers(river, land)
                    && RiverGeometry.Evaluate(river).Bounds.Intersects(view)
                    // The cut is bounded by the mouth's radial mask. The wider fade of water
                    // over the sea does not carve land and must not make a dry view run filters.
                    && RiverRenderer.BodyTouches(river, Expand(view, RiverCoastRenderer.MaximumMouthRadius
                        + LandEdgeReach(land.EdgeStyle) + river.Style.BankWidth + 2 / Math.Max(.01,options.ViewScale)), options.ViewScale))
                    result.Add((layer, river));
        return result;
    }

    private static double LandEdgeReach(LandEdgeStyle style) => 3 + Math.Max(style.OutlineWidth,
        Math.Max(style.ShadowBlur * 4 + Math.Max(Math.Abs(style.ShadowOffsetX),Math.Abs(style.ShadowOffsetY)),
            RippleReach(style) + style.RippleWidth * 3));

    /// <summary>Distance of surf line <paramref name="index"/> (from 1) in units of the style's spacing:
    /// every line a quarter of a spacing farther from the previous one than that was from its own.</summary>
    public static double RippleDistance(int index) => index * (1 + 0.25 * (index - 1));

    /// <summary>Opacity factor of surf line <paramref name="index"/> (from 1): the outer lines fade.</summary>
    public static double RippleStrength(int index) => Math.Max(0.3, 1 - 0.28 * (index - 1));

    /// <summary>How far beyond the land its outermost surf line reaches, in world units.</summary>
    public static double RippleReach(LandEdgeStyle style) =>
        style.RippleSpacing * RippleDistance(Math.Clamp(style.RippleCount, 0, 8)) + style.RippleWidth;

    private static bool SolidLandInterior(LandLayer layer, WorldBounds view)
    {
        for (var y=(int)Math.Floor(view.Top/TileMap.TileSize);y<=(int)Math.Floor(view.Bottom/TileMap.TileSize);y++)
            for (var x=(int)Math.Floor(view.Left/TileMap.TileSize);x<=(int)Math.Floor(view.Right/TileMap.TileSize);x++)
            {
                if (layer.GeometryTiles.TryGet(new(x,y)) is not { } pixels) return false;
                if (IsFullyCovered(pixels)) continue;
                // A partial tile can have its coastline far outside this view and every filter.
                // Prove the queried pixels, rather than rejecting the entire 256-pixel tile.
                var left = Math.Clamp((int)Math.Floor(view.Left-x*TileMap.TileSize),0,TileMap.TileSize);
                var right = Math.Clamp((int)Math.Ceiling(view.Right-x*TileMap.TileSize)+1,0,TileMap.TileSize);
                var top = Math.Clamp((int)Math.Floor(view.Top-y*TileMap.TileSize),0,TileMap.TileSize);
                var bottom = Math.Clamp((int)Math.Ceiling(view.Bottom-y*TileMap.TileSize)+1,0,TileMap.TileSize);
                for (var row=top;row<bottom;row++)
                    if (pixels.AsSpan(row*TileMap.TileSize+left,right-left).IndexOfAnyExcept((byte)255)>=0) return false;
            }
        return true;
    }

    private static void DrawLand(
        SKCanvas output, MapDocument document, LandLayer layer, WorldBounds view, RenderOptions options)
    {
        if (layer.GeometryTiles.TileCount == 0)
        {
            return;
        }

        // The passes below draw on "canvas": the frame itself, or - for the kept image of the
        // finished land - an offscreen surface standing in for it.
        var canvas = output;
        var style = layer.EdgeStyle;
        // A sea style draws the water lines itself; the land's ring surf would draw them twice.
        if (style.RippleCount > 0 && ActiveSea(document, options) is not null) style = style with { RippleCount = 0 };
        var sampling = options.ViewScale < 0.9
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        // A piece of land above the ground or sea beneath it drops as a cliff below its southern
        // edges (LandCliffField, LandCliffShader): the wall of land just above the view reaches into it, so the
        // view is extended upwards by the tallest wall this piece can have over what lies below.
        var earlier = TerrainOrder.Lands(document).TakeWhile(candidate => !ReferenceEquals(candidate, layer))
            .Where(candidate => candidate is { Visible: true, Opacity: > 0 } && candidate.GeometryTiles.TileCount > 0)
            .ToList();
        var lowestFloor = Math.Min(0, earlier.Count > 0 ? earlier.Min(candidate => candidate.Level) : 0);
        var cliffReach = options.FastTerrain ? 0 : style.Cliff.Reach(style.Cliff.Steps(layer.Level, lowestFloor));
        var reachTop = view.Top - cliffReach;
        var keys = layer.GeometryTiles
            .KeysIntersecting(view.Left, reachTop, view.Right, view.Bottom)
            .ToList();
        if (keys.Count == 0)
        {
            return;
        }

        // Land drawn before this piece in terrain order, inside the view. Where this piece lies
        // over another, the two are one landmass: no coast is drawn between them and this edge
        // dissolves into the land beneath instead of standing on it as a hard cut.
        var beneath = earlier
            .Where(candidate => candidate.GeometryTiles.KeysIntersecting(view.Left, reachTop, view.Right, view.Bottom).Any())
            .ToList();
        // A raised piece is not one landmass with the lower ground it stands on: it keeps its own
        // rim there, stands on it as a hard edge and drops to it as a cliff. Only land at its own
        // level shares its coast and dissolves into it; the land below is its ground.
        var lowers = cliffReach > 0 ? beneath.Where(lower => lower.Level >= layer.Level).ToList() : beneath;
        var ground = cliffReach > 0 ? beneath.Where(lower => lower.Level < layer.Level).ToList() : [];

        // Stage 19: rivers carve the coverage before any edge styling, so the coast wraps their
        // banks and the sea enters their mouths. The carved coverage is rasterized at device
        // resolution and then feeds every pass exactly like the tile images would. It is kept in
        // the carved-land cache, so an unchanged coast is not rebuilt on the next frame.
        SKImage? carved = null;
        var carvedOwned = false;
        var cutters = options.LodTier == RenderLodTier.Overview && !options.IsExport ? [] : LandCuttingRivers(document, layer, view, options);
        // A fully opaque interior has no coast, ripple or inner shadow. Prove coverage beyond
        // every filter's reach before skipping those costly morphology passes. Materials and
        // paint still draw normally; partial tiles, cuts, translucent fill and overlaps do not qualify.
        var solidInterior = !options.FastTerrain && cutters.Count == 0 && beneath.Count == 0
            && ParseColor(style.FillColor).Alpha == 255
            && SolidLandInterior(layer, Expand(view,LandEdgeReach(style)+2/Math.Max(.01,options.ViewScale)));
        if (cutters.Count > 0)
        {
            var matrix = canvas.TotalMatrix;
            var scale = Math.Max(1e-4, Math.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY));
            // A soft edge is still land while it has coverage. The 50% contour lies inside it
            // and would start a rounded sea opening before the visible ground actually ends.
            bool InsideLand(double x, double y)
            {
                var key = new TileKey((int)Math.Floor(x / TileMap.TileSize), (int)Math.Floor(y / TileMap.TileSize));
                if (layer.GeometryTiles.TryGet(key) is not { } tile) return false;
                var px = (int)Math.Clamp(x - (key.X * TileMap.TileSize), 0, TileMap.TileSize - 1);
                var py = (int)Math.Clamp(y - (key.Y * TileMap.TileSize), 0, TileMap.TileSize - 1);
                return tile[(py * TileMap.TileSize) + px] > 0;
            }

            SKImage? Build(int width, int height)
            {
                var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var surface = (options.GpuContext is { } gpu
                    ? SKSurface.Create(gpu, false, info) : null) ?? SKSurface.Create(info);
                if (surface is null) return null;
                var target = surface.Canvas;
                target.Clear(SKColors.Transparent);
                target.Scale((float)scale);
                target.Translate((float)-view.Left, (float)-view.Top);
                foreach (var key in keys)
                {
                    var coverage = layer.GeometryTiles.TryGet(key)!;
                    var image = options.TileCache is not null
                        ? options.TileCache.Get(layer.Id, key, TileImageKind.LandCoverage, coverage)
                        : BuildCoverageImage(coverage, TileImageKind.LandCoverage);
                    target.DrawImage(image, TileRect(key), sampling, null);
                    if (options.TileCache is null) image.Dispose();
                }
                foreach (var (owner, river) in cutters)
                {
                    using var erase = new SKPaint { BlendMode = SKBlendMode.DstOut,
                        Color = SKColors.White.WithAlpha(Alpha(owner.Opacity)) };
                    target.SaveLayer(ToRect(view), erase);
                    RiverRenderer.CutBody(target, river, options.ViewScale, SKBlendMode.SrcOver);
                    RiverCoastRenderer.SmoothMouths(target, river, scale, InsideLand,
                        (coverage, paint) =>
                        {
                            foreach (var key in keys)
                            {
                                var bytes = layer.GeometryTiles.TryGet(key)!;
                                var image = options.TileCache is not null
                                    ? options.TileCache.Get(layer.Id, key, TileImageKind.LandCoverage, bytes)
                                    : BuildCoverageImage(bytes, TileImageKind.LandCoverage);
                                coverage.DrawImage(image, TileRect(key), sampling, paint);
                                if (options.TileCache is null) image.Dispose();
                            }
                        }, water => RiverRenderer.CutBody(water, river, options.ViewScale, SKBlendMode.SrcOver));
                    if (owner.Mask is { Enabled: true } mask && HasEffectiveMask(mask, view))
                        ApplyMask(target, owner.Id, mask, view, options);
                    if (owner.Effects.ClippingEnabled && LowerLayer(document, owner) is { Visible: true, Opacity: > 0 } lower)
                    {
                        using var clip = new SKPaint { BlendMode = SKBlendMode.DstIn };
                        target.SaveLayer(ToRect(view), clip);
                        DrawClippingCoverage(target, document, lower, view, options);
                        if (lower is not LandLayer && lower.Mask is { Enabled: true } lowerMask && HasEffectiveMask(lowerMask, view))
                            ApplyMask(target, lower.Id, lowerMask, view, options);
                        target.Restore();
                    }
                    target.Restore();
                }
                var snapshot = surface.Snapshot();
                if (!snapshot.IsTextureBacked) return snapshot;
                // This coverage cache is also consumed by CPU exports; retain portable bytes.
                using (snapshot) return snapshot.ToRasterImage();
            }

            // Nothing about the coast changed between two frames that share this key, so the
            // carved raster is reused instead of being rebuilt. The cache owns what it hands back.
            if (options.CarvedLand is { } reuse)
                carved = reuse.Get(layer.Id, layer.GeometryTiles, view, scale, cutters.Select(c => c.River).ToArray(),
                    Build, out _, out _, cutters.SelectMany(c => c.Owner.Effects.ClippingEnabled
                        && LowerLayer(document, c.Owner) is { } lower ? new[] { OwnerKey(c.Owner), lower } : [OwnerKey(c.Owner)]).ToArray());
            else { carved = Build(
                (int)Math.Clamp(Math.Ceiling((view.Right - view.Left) * scale) + 2, 1, 16384),
                (int)Math.Clamp(Math.Ceiling((view.Bottom - view.Top) * scale) + 2, 1, 16384)); carvedOwned = true; }
        }
        using var carvedImage = carvedOwned ? carved : null;
        var deviceScale = Math.Max(1e-4, Math.Sqrt(output.TotalMatrix.ScaleX * output.TotalMatrix.ScaleX + output.TotalMatrix.SkewY * output.TotalMatrix.SkewY));
        var carvedRect = new SKRect((float)view.Left, (float)view.Top,
            (float)(view.Left + (carved?.Width ?? 0) / deviceScale),
            (float)(view.Top + (carved?.Height ?? 0) / deviceScale));

        // At overview scale the edge effects are below a screen pixel, but each one replays the
        // complete tile set (rings do it several times). Keep the exact style at edit/export zoom
        // and use one tinted coverage pass for the far map overview.
        var simplifiedOverview = !options.IsExport && options.LodTier == RenderLodTier.Overview;
        if (simplifiedOverview)
        {
            // The far view kept the land flat: raised pieces lost their cliff walls (D6). The wall
            // is drawn from the same kept field images as up close, under the coverage (R05).
            if (cliffReach > 0 && !solidInterior) DrawCliffs();
            DrawOverviewCoverage();
            if (options.Stats is { } overviewStats)
            {
                overviewStats.TilesDrawn += keys.Count;
                overviewStats.MipmappedTiles += keys.Count;
            }
            return;
        }

        // The live material stroke is composed inside the passes below (between the material
        // and the inner shading), so a frame that carries one always draws the land in full.
        var paintPreview = options.Preview is { IsLandPaint: true } live && live.LayerId == layer.Id ? live : null;

        // With land beneath, this piece's coast is the coast of the whole landmass: every edge
        // pass reads the union of this coverage and the land below (withLowers), and is then
        // erased inside the zone the lower land's own coast already occupies - the reach of
        // that very pass. Equal styles give exactly the coast of the union; the seam is gone.
        var blends = lowers.Count > 0 && !options.FastTerrain;
        var withLowers = false;
        // Surf lines follow the coast of the land as drawn, not the banks the rivers carve into
        // it: grown from the carved coverage they ran up every channel and had to be cut away
        // at each mouth, which left the lines ending abruptly on both sides of an estuary.
        var uncarved = false;
        // Magnified, the coverage raster shows its world pixels as steps along the coast. A blur
        // of about one world unit, brought back to a crisp edge by an alpha curve, turns them
        // into one smooth line; a soft coast keeps a (slightly steeper) soft ramp.
        var smoothEdges = !options.FastTerrain && deviceScale > 1.5;

        // State of the distance-field coast (TryBuildCoastField), and the bounds and tile set the
        // coverage pass currently draws: the view, or the padded view while the field is rasterized.
        float[]? coastField = null, surfField = null, lowersField = null;
        byte[]? coastAlpha = null;
        var fieldWidth = 0; var fieldHeight = 0;
        var fieldRect = SKRect.Empty;
        // A view larger than the field budget (a big single image) is covered by field tiles: each
        // computed over its own square plus the reach of the farthest effect, so every pixel sees
        // the same coast it would see in one field, and drawn only over its own square (R12).
        List<(SKImage? Outer, SKImage? Inner, SKRect Rect, SKRect Core)>? fieldTiles = null;
        var coverageBounds = ToRect(view);
        IReadOnlyList<TileKey> drawKeys = keys;

        // The finished look of this piece for this very view is kept between frames. A stroke,
        // an armed placement or a drag changes nothing about the land and does not move the
        // view, so those frames draw the kept image and look exactly like the idle frame. A
        // pan draws the plain fill (FastTerrain) and never enters the cache.
        if (options.StyledLand is { } styled && paintPreview is null && !options.IsExport && !options.FastTerrain)
        {
            var parts = new List<object> { layer.Id, layer.GeometryTiles, layer.PaintTiles, layer.EdgeStyle, layer.Level, layer.CliffWallTiles, layer.CliffBeachTiles, layer.ReliefTiles, ActiveSea(document, options) is not null };
            parts.AddRange(beneath);
            foreach (var (owner, river) in cutters)
            {
                parts.Add(OwnerKey(owner)); parts.Add(river);
                if (owner.Effects.ClippingEnabled && LowerLayer(document, owner) is { } lower) parts.Add(lower);
            }
            var image = styled.Get(parts, view, deviceScale, options.GpuContext, (width, height) =>
            {
                using var surface = RenderImageCache.CreateSurface(options.GpuContext, width, height, view, deviceScale);
                if (surface is null) return null;
                canvas = surface.Canvas;
                try { DrawStyled(); canvas.Flush(); }
                finally { canvas = output; }
                return surface.Snapshot();
            });
            if (image is not null)
            {
                output.DrawImage(image, RenderImageCache.Placement(image, view, deviceScale),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                CountTiles();
                return;
            }
        }

        DrawStyled();
        CountTiles();
        return;

        void CountTiles()
        {
            if (options.Stats is { } stats)
            {
                stats.TilesDrawn += keys.Count;
                if (options.ViewScale < 0.9)
                {
                    stats.MipmappedTiles += keys.Count;
                }
            }
        }

        // Every pass of the finished land, in order, on whatever canvas is current: the frame
        // itself, or the kept image that stands in for it.
        void DrawStyled()
        {
            if (blends)
            {
                canvas.SaveLayer(ToRect(view), null);
            }

            // One piece of land alone in the view: its coast becomes a distance field and the
            // outline, the surf and both shadows are one lookup per pixel (CoastDistanceField).
            // Land that overlaps other land keeps the filter passes, whose erase steps share one
            // coast between the pieces.
            var useField = CoastDistanceField.Enabled && !options.CoastFilters && !options.FastTerrain && !solidInterior
                && options.LodTier != RenderLodTier.Overview && TryBuildCoastField();

            // Over the lower ground a raised piece has no surf - what lies there is land, not
            // water - so its outer effects are drawn apart, cut away over that ground, and only
            // the rim line and the shadow are laid there again (RimOverGround).
            var rimOverGround = ground.Count > 0 && !options.FastTerrain && !solidInterior;
            if (rimOverGround) canvas.SaveLayer(ToRect(view), null);

            if (useField && fieldTiles is not null)
            {
                foreach (var tile in fieldTiles)
                {
                    if (tile.Outer is not { } outer) continue;
                    canvas.Save();
                    canvas.ClipRect(tile.Core);
                    canvas.DrawImage(outer, tile.Rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    canvas.Restore();
                    outer.Dispose();
                }
            }
            else if (useField)
            {
                using var outer = CoastDistanceField.OuterEffects(
                    coastField!, surfField, fieldWidth, fieldHeight, style, deviceScale, lowersField);
                if (outer is not null)
                {
                    canvas.DrawImage(outer, fieldRect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                }
            }
            else if (!options.FastTerrain && !solidInterior)
            {
                withLowers = blends;
                if (style.OuterShadowOpacity > 0 && style.ShadowBlur > 0)
                {
                    using var filter = SKImageFilter.CreateDropShadowOnly(
                        (float)style.ShadowOffsetX, (float)style.ShadowOffsetY,
                        (float)style.ShadowBlur, (float)style.ShadowBlur,
                        SKColors.Black.WithAlpha(Alpha(style.OuterShadowOpacity)));
                    DrawFilteredCoverage(filter, color: null, SKBlendMode.SrcOver);
                    EraseLowerZone(style.ShadowBlur * 3 + Math.Max(Math.Abs(style.ShadowOffsetX), Math.Abs(style.ShadowOffsetY)));
                }

                // Surf lines as a drawn atlas has them: each line a little farther from the one
                // before and a little fainter, so the water calms away from the shore instead of
                // repeating the coast as evenly spaced parallel tracks.
                var ripples = Math.Clamp(style.RippleCount, 0, 8);
                for (var ripple = ripples; ripple >= 1; ripple--)
                {
                    var radius = Math.Max(0, style.RippleSpacing * RippleDistance(ripple));
                    var width = Math.Max(0.5, style.RippleWidth);
                    var color = ParseColor(style.RippleColor);
                    color = color.WithAlpha((byte)Math.Round(color.Alpha * RippleStrength(ripple)));
                    DrawCoverageRing(radius, width, color);
                    EraseLowerZone(radius + width * 1.5);
                }

                if (style.OutlineWidth > 0)
                {
                    DrawExpandedCoverage(style.OutlineWidth, ParseColor(style.OutlineColor));
                    EraseLowerZone(style.OutlineWidth);
                }
                withLowers = false;
            }

            if (rimOverGround)
            {
                using (var erase = new SKPaint { BlendMode = SKBlendMode.DstOut })
                {
                    canvas.SaveLayer(ToRect(view), erase);
                    using (var plain = new SKPaint()) DrawLandCoverage(canvas, ground, view, options, plain);
                    canvas.Restore();
                }
                canvas.Restore();
                RimOverGround();
            }

            // The cliff below the raised edges: over the surf and shadows at its foot, under the
            // fill, which covers the top of the wall exactly along the edge.
            if (cliffReach > 0 && !solidInterior) DrawCliffs();

            using (var tint = SKColorFilter.CreateBlendMode(ParseColor(style.FillColor), SKBlendMode.SrcIn))
            using (var fill = new SKPaint { ColorFilter = tint, IsAntialias = true })
            {
                DrawCoverageImages(fill);
            }

            // The ground material, mixed into the fill colour and kept inside the coverage. The
            // coverage is redrawn into a layer only to provide the shape; its own colour is discarded
            // by SrcIn, so the land keeps exactly the colour the user chose, with grain on top.
            if (!options.FastTerrain
                && MapTextureLibrary.Image(style.FillTextureId) is not null && style.FillTextureOpacity > 0)
            {
                canvas.SaveLayer(ToRect(view), null);
                DrawCoverageImages(null);
                using (var shape = new SKPaint { BlendMode = SKBlendMode.SrcIn })
                {
                    canvas.SaveLayer(ToRect(view), shape);
                    MapTextureLibrary.DrawFill(canvas, style.FillTextureId, style.FillTextureScale,
                        style.FillTextureOpacity, ToRect(view));
                    canvas.Restore();
                }
                canvas.Restore();
            }

            // Paint lives inside the same carved coverage as the base material. Composing paint and
            // its live eraser on an isolated surface preserves the base and coast below the paint.
            if (layer.PaintTiles.TileCount > 0 || paintPreview is not null)
            {
                canvas.SaveLayer(ToRect(view), null);
                DrawTiles(canvas, layer.Id, layer.PaintTiles, view, options, TileImageKind.Content);
                if (paintPreview is not null) DrawStrokePreview(canvas, paintPreview, view, options);
                using (var keepPaint = new SKPaint { BlendMode = SKBlendMode.DstIn })
                {
                    canvas.SaveLayer(ToRect(view), keepPaint);
                    DrawCoverageImages(null);
                    canvas.Restore();
                }
                canvas.Restore();
            }

            // The heights of the land (R22): shade, contours and hachures, kept inside the coverage.
            if (layer.ReliefTiles.TileCount > 0 && !options.FastTerrain)
            {
                canvas.SaveLayer(ToRect(view), null);
                DrawRelief(canvas, layer, style, view, deviceScale);
                using (var keepRelief = new SKPaint { BlendMode = SKBlendMode.DstIn })
                {
                    canvas.SaveLayer(ToRect(view), keepRelief);
                    DrawCoverageImages(null);
                    canvas.Restore();
                }
                canvas.Restore();
            }

            if (useField && fieldTiles is not null)
            {
                foreach (var tile in fieldTiles)
                {
                    if (tile.Inner is not { } inner) continue;
                    canvas.Save();
                    canvas.ClipRect(tile.Core);
                    canvas.DrawImage(inner, tile.Rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    canvas.Restore();
                    inner.Dispose();
                }
            }
            else if (useField)
            {
                using var inner = CoastDistanceField.InnerShadow(
                    coastField!, coastAlpha!, fieldWidth, fieldHeight, style, deviceScale);
                if (inner is not null)
                {
                    canvas.DrawImage(inner, fieldRect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                }
            }
            else if (!options.FastTerrain && !solidInterior && style.InnerShadowOpacity > 0 && style.ShadowBlur > 0)
            {
                // An inner shadow is coverage minus its shifted, blurred copy. Drawing the blurred
                // coverage itself darkened the entire interior and made the very edge lighter.
                using var filter = SKImageFilter.CreateDropShadowOnly(
                    (float)style.ShadowOffsetX, (float)style.ShadowOffsetY,
                    (float)style.ShadowBlur, (float)style.ShadowBlur,
                    SKColors.Black);
                using var strength = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(style.InnerShadowOpacity)) };
                canvas.SaveLayer(ToRect(view), strength);
                withLowers = blends;
                using (var black = SKColorFilter.CreateBlendMode(SKColors.Black, SKBlendMode.SrcIn))
                using (var fillShadow = new SKPaint { ColorFilter = black }) DrawCoverageImages(fillShadow);
                DrawFilteredCoverage(filter, color: null, SKBlendMode.DstOut);
                withLowers = false;
                using (var keep = new SKPaint { BlendMode = SKBlendMode.DstIn })
                {
                    canvas.SaveLayer(ToRect(view), keep);
                    DrawCoverageImages(null);
                    canvas.Restore();
                }
                canvas.Restore();
            }

            if (blends)
            {
                // The edge of this piece dissolves into the land beneath over about the shadow's
                // reach, and stays crisp against the sea. The mask is 1 - lower * (1 - blur(this)):
                // one over the sea, the blurred edge of this piece over other land.
                var feather = (float)Math.Max(6, style.ShadowBlur * 1.5);
                using (var keep = new SKPaint { BlendMode = SKBlendMode.DstIn })
                {
                    canvas.SaveLayer(ToRect(view), keep);
                    using (var white = new SKPaint { Color = SKColors.White })
                    {
                        canvas.DrawRect(ToRect(view), white);
                    }
                    using (var subtract = new SKPaint { BlendMode = SKBlendMode.DstOut })
                    {
                        canvas.SaveLayer(ToRect(view), subtract);
                        using (var plain = new SKPaint())
                        {
                            DrawLandCoverage(canvas, lowers, view, options, plain);
                        }
                        using (var soften = SKImageFilter.CreateBlur(feather, feather))
                        using (var cut = new SKPaint { BlendMode = SKBlendMode.DstOut, ImageFilter = soften })
                        {
                            canvas.SaveLayer(ToRect(view), cut);
                            DrawCoverageImages(null);
                            canvas.Restore();
                        }
                        canvas.Restore();
                    }
                    canvas.Restore();
                }
                canvas.Restore();
            }
        }

        // The edge of a raised piece where it stands on lower ground: its outline and its shadow,
        // kept inside that ground's coverage.
        void RimOverGround()
        {
            var shadow = style.OuterShadowOpacity > 0 && style.ShadowBlur > 0;
            if (!shadow && style.OutlineWidth <= 0) return;
            canvas.SaveLayer(ToRect(view), null);
            if (shadow)
            {
                using var filter = SKImageFilter.CreateDropShadowOnly(
                    (float)style.ShadowOffsetX, (float)style.ShadowOffsetY,
                    (float)style.ShadowBlur, (float)style.ShadowBlur,
                    SKColors.Black.WithAlpha(Alpha(style.OuterShadowOpacity)));
                DrawFilteredCoverage(filter, color: null, SKBlendMode.SrcOver);
            }
            if (style.OutlineWidth > 0) DrawExpandedCoverage(style.OutlineWidth, ParseColor(style.OutlineColor));
            using (var keep = new SKPaint { BlendMode = SKBlendMode.DstIn })
            {
                canvas.SaveLayer(ToRect(view), keep);
                using (var plain = new SKPaint()) DrawLandCoverage(canvas, ground, view, options, plain);
                canvas.Restore();
            }
            canvas.Restore();
        }

        // The cliff of this piece over the view: one field per square of the map at the detail of
        // the current zoom (a texel about a device pixel), each drawn by the wall shader over the
        // bounds of its wall. Fields are kept between frames and only an edit of this land or of
        // the land below recomputes them; a slider or a zoom only redraws.
        void DrawCliffs()
        {
            var cliff = style.Cliff;
            if (!LandCliffShader.Available || cliff.Height * deviceScale < 1.2) return;
            var lod = Math.Clamp((int)Math.Floor(Math.Log2(1 / Math.Max(1e-6, deviceScale))), 0, 6);
            var size = LandCliffField.Size * (double)(1 << lod);
            var pixel = 1 / deviceScale;
            var rimWidth = style.OutlineWidth > 0 ? style.OutlineWidth : 0;
            using var paint = new SKPaint { IsAntialias = false };
            for (var fy = (int)Math.Floor(view.Top / size); fy * size < view.Bottom; fy++)
                for (var fx = (int)Math.Floor(view.Left / size); fx * size < view.Right; fx++)
                {
                    // Only squares a wall of this land can reach: some of its coverage lies above
                    // them within reach (or beside them, by the lean).
                    var side = Math.Abs(cliff.Lean) * cliffReach + 2;
                    if (!layer.GeometryTiles.KeysIntersecting(fx * size - side, fy * size - cliffReach - 2,
                            (fx + 1) * size + side, (fy + 1) * size).Any()) continue;
                    var field = LandCliffField.Get(layer, beneath, cliff.Lean, cliffReach, lod, fx, fy);
                    if (field.Wall is not { } wall || !wall.Intersects(view)) continue;
                    var drawStarted = Stopwatch.GetTimestamp();
                    // At a texel no larger than a device pixel the kept wall image of the square
                    // stands in for the shader; closer in, the shader draws every pixel crisply.
                    if (deviceScale * field.Texel <= 1.001)
                    {
                        using var kept = field.WallImage(cliff, style.OutlineColor, rimWidth);
                        if (kept is not null)
                        {
                            canvas.DrawImage(kept, new SKRect((float)field.Left, (float)field.Top,
                                (float)(field.Left + field.WorldSize), (float)(field.Top + field.WorldSize)),
                                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                            LandCliffField.DrawMilliseconds += Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds;
                            continue;
                        }
                    }
                    using var fieldShader = field.ToShader(out var image);
                    using (image)
                    using (var shader = LandCliffShader.Create(fieldShader, cliff, style.OutlineColor, rimWidth, pixel))
                    {
                        if (shader is null) return;
                        paint.Shader = shader;
                        using var area = field.WallArea();
                        canvas.DrawPath(area, paint);
                        paint.Shader = null;
                    }
                    LandCliffField.DrawMilliseconds += Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds;
                }
        }

        // The coast as a signed distance field at device resolution, over the view plus the reach
        // of the farthest effect, so an effect near the edge of the view still sees the land
        // just outside it. False when the view is too large for the field (export at full size).
        bool TryBuildCoastField()
        {
            var hasEffects = (style.OuterShadowOpacity > 0 && style.ShadowBlur > 0)
                || (style.InnerShadowOpacity > 0 && style.ShadowBlur > 0)
                || style.OutlineWidth > 0 || (style.RippleCount > 0 && style.RippleWidth > 0);
            if (!hasEffects) return false;
            var reach = Math.Max(CoastDistanceField.OuterReach(style),
                style.InnerShadowOpacity > 0 ? (3 * style.ShadowBlur) + Math.Max(Math.Abs(style.ShadowOffsetX), Math.Abs(style.ShadowOffsetY)) : 0);
            var pad = (int)Math.Ceiling(reach * deviceScale) + 2;
            var width = (int)Math.Ceiling((view.Right - view.Left) * deviceScale) + (2 * pad);
            var height = (int)Math.Ceiling((view.Bottom - view.Top) * deviceScale) + (2 * pad);
            if (width <= 0 || height <= 0) return false;
            if ((long)width * height > CoastDistanceField.Budget) return TryBuildCoastFieldTiles(pad);

            var padded = new WorldBounds(view.Left - (pad / deviceScale), view.Top - (pad / deviceScale),
                view.Right + (pad / deviceScale), view.Bottom + (pad / deviceScale));
            // This piece alone masks its own inner shadow; over other land the coast and every
            // effect come from the union, cut back by the lower land's own coast zone.
            var alpha = RasterizeCoverage(padded, width, height, surf: false, CoverageSet.ThisPiece);
            if (alpha is null) return false;
            coastAlpha = alpha;
            var union = blends ? RasterizeCoverage(padded, width, height, surf: false, CoverageSet.WithLowers) : alpha;
            if (union is null) return false;
            coastField = CoastDistanceField.SignedDistance(union, width, height);
            if (blends)
            {
                var lowersAlpha = RasterizeCoverage(padded, width, height, surf: false, CoverageSet.LowersOnly);
                if (lowersAlpha is null) return false;
                lowersField = CoastDistanceField.SignedDistance(lowersAlpha, width, height);
            }
            if (cutters.Count > 0 && style.RippleCount > 0 && style.RippleWidth > 0)
            {
                var surfAlpha = RasterizeCoverage(padded, width, height, surf: true, blends ? CoverageSet.WithLowers : CoverageSet.ThisPiece);
                surfField = surfAlpha is null ? null : CoastDistanceField.SignedDistance(surfAlpha, width, height);
            }
            fieldWidth = width; fieldHeight = height;
            fieldRect = new SKRect((float)padded.Left, (float)padded.Top,
                (float)(padded.Left + (width / deviceScale)), (float)(padded.Top + (height / deviceScale)));
            return true;
        }

        // The same field over a grid of squares of the view, each square with the full reach around
        // it. The finished outer and inner effects of every square are kept as images; the floats
        // of one square are dropped before the next is computed, so the memory stays that of the
        // images however large the view is.
        bool TryBuildCoastFieldTiles(int pad)
        {
            var core = CoastDistanceField.TileCore(pad);
            if (core <= 0) return false;
            var coreWorld = core / deviceScale;
            var tiles = new List<(SKImage? Outer, SKImage? Inner, SKRect Rect, SKRect Core)>();
            for (var top = view.Top; top < view.Bottom; top += coreWorld)
                for (var left = view.Left; left < view.Right; left += coreWorld)
                {
                    var square = new WorldBounds(left, top, Math.Min(left + coreWorld, view.Right), Math.Min(top + coreWorld, view.Bottom));
                    var width = (int)Math.Ceiling((square.Right - square.Left) * deviceScale) + (2 * pad);
                    var height = (int)Math.Ceiling((square.Bottom - square.Top) * deviceScale) + (2 * pad);
                    var padded = new WorldBounds(square.Left - (pad / deviceScale), square.Top - (pad / deviceScale),
                        square.Right + (pad / deviceScale), square.Bottom + (pad / deviceScale));
                    var alpha = RasterizeCoverage(padded, width, height, surf: false, CoverageSet.ThisPiece);
                    if (alpha is null) { Release(tiles); return false; }
                    var union = blends ? RasterizeCoverage(padded, width, height, surf: false, CoverageSet.WithLowers) : alpha;
                    if (union is null) { Release(tiles); return false; }
                    var field = CoastDistanceField.SignedDistance(union, width, height);
                    float[]? lowers = null, surf = null;
                    if (blends)
                    {
                        var lowersAlpha = RasterizeCoverage(padded, width, height, surf: false, CoverageSet.LowersOnly);
                        if (lowersAlpha is null) { Release(tiles); return false; }
                        lowers = CoastDistanceField.SignedDistance(lowersAlpha, width, height);
                    }
                    if (cutters.Count > 0 && style.RippleCount > 0 && style.RippleWidth > 0)
                    {
                        var surfAlpha = RasterizeCoverage(padded, width, height, surf: true, blends ? CoverageSet.WithLowers : CoverageSet.ThisPiece);
                        surf = surfAlpha is null ? null : CoastDistanceField.SignedDistance(surfAlpha, width, height);
                    }
                    var rect = new SKRect((float)padded.Left, (float)padded.Top,
                        (float)(padded.Left + (width / deviceScale)), (float)(padded.Top + (height / deviceScale)));
                    tiles.Add((CoastDistanceField.OuterEffects(field, surf, width, height, style, deviceScale, lowers),
                        CoastDistanceField.InnerShadow(field, alpha, width, height, style, deviceScale),
                        rect, ToRect(square)));
                }
            fieldTiles = tiles;
            return true;

            static void Release(List<(SKImage? Outer, SKImage? Inner, SKRect Rect, SKRect Core)> made)
            {
                foreach (var (outer, inner, _, _) in made) { outer?.Dispose(); inner?.Dispose(); }
            }
        }

        // The coverage drawn by the ordinary coverage pass into a CPU raster of the padded view;
        // only its alpha is kept. The surf raster follows the uncarved coast, as the ring pass did.
        byte[]? RasterizeCoverage(WorldBounds padded, int width, int height, bool surf, CoverageSet set)
        {
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null) return null;
            var target = surface.Canvas;
            target.Clear(SKColors.Transparent);
            target.Scale((float)deviceScale);
            target.Translate((float)-padded.Left, (float)-padded.Top);
            var previousCanvas = canvas; var previousBounds = coverageBounds; var previousKeys = drawKeys;
            var previousLowers = withLowers;
            canvas = target; coverageBounds = ToRect(padded);
            drawKeys = layer.GeometryTiles.KeysIntersecting(padded.Left, padded.Top, padded.Right, padded.Bottom).ToList();
            uncarved = surf;
            withLowers = set == CoverageSet.WithLowers;
            try
            {
                if (set == CoverageSet.LowersOnly)
                {
                    using var plain = new SKPaint();
                    DrawLandCoverage(canvas, lowers, padded, options, plain);
                }
                else DrawCoverageImages(null);
            }
            finally
            {
                canvas = previousCanvas; coverageBounds = previousBounds; drawKeys = previousKeys;
                uncarved = false; withLowers = previousLowers;
            }
            using var bitmap = new SKBitmap(info);
            if (!surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0)) return null;
            var pixels = bitmap.GetPixelSpan();
            var alpha = new byte[width * height];
            for (var i = 0; i < alpha.Length; i++) alpha[i] = pixels[(i * 4) + 3];
            return alpha;
        }

        void DrawFilteredCoverage(SKImageFilter filter, SKColor? color, SKBlendMode blend)
        {
            using var tint = color is { } value
                ? SKColorFilter.CreateBlendMode(value, SKBlendMode.SrcIn)
                : null;
            using var paint = new SKPaint
            {
                ImageFilter = filter,
                ColorFilter = tint,
                BlendMode = blend,
                IsAntialias = true,
            };
            canvas.SaveLayer(ToRect(view), paint);
            DrawCoverageImages(null);
            canvas.Restore();
        }

        void DrawCoverageImages(SKPaint? paint)
        {
            if (solidInterior)
            {
                // Adjacent opaque tile images can expose a subpixel join when magnified.
                // The proven solid interior is one continuous coverage, including its material mask.
                using var solid = paint?.Clone() ?? new SKPaint();
                solid.Color=SKColors.White.WithAlpha(paint?.Color.Alpha??255);solid.IsAntialias=false;
                canvas.DrawRect(ToRect(view),solid);return;
            }
            if (smoothEdges)
            {
                using var layerPaint = paint?.Clone() ?? new SKPaint();
                using var blur = SKImageFilter.CreateBlur(0.9f, 0.9f);
                using var edge = SKColorFilter.CreateTable(SmoothEdgeAlpha, IdentityTable, IdentityTable, IdentityTable);
                using var smooth = SKImageFilter.CreateColorFilter(edge, blur);
                layerPaint.ImageFilter = smooth;
                canvas.SaveLayer(SKRect.Inflate(coverageBounds, 2, 2), layerPaint);
                smoothEdges = false;
                try { DrawCoverageImages(null); }
                finally { smoothEdges = true; }
                canvas.Restore();
                return;
            }
            if (withLowers)
            {
                using var plain = paint is null ? new SKPaint() : null;
                DrawLandCoverage(canvas, lowers, view, options, paint ?? plain!);
            }
            if (carved is not null && !uncarved)
            {
                canvas.DrawImage(carved, carvedRect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
                return;
            }
            foreach (var key in drawKeys)
            {
                var coverage = layer.GeometryTiles.TryGet(key)!;
                var image = options.TileCache is not null
                    ? options.TileCache.Get(layer.Id, key, TileImageKind.LandCoverage, coverage)
                    : BuildCoverageImage(coverage, TileImageKind.LandCoverage);
                canvas.DrawImage(image, TileRect(key), sampling, paint);
                if (options.TileCache is null)
                {
                    image.Dispose();
                }
            }
        }

        // Removes what was just drawn inside the zone a lower piece's own coast covers: that
        // piece drew its coast there already, and this piece must not draw it a second time.
        void EraseLowerZone(double reach)
        {
            if (!blends) return;
            using var grow = reach > 0 ? SKImageFilter.CreateDilate((float)reach, (float)reach) : null;
            using var erase = new SKPaint { BlendMode = SKBlendMode.DstOut, ImageFilter = grow };
            var pad = (float)Math.Max(0, reach) + 1;
            canvas.SaveLayer(SKRect.Inflate(ToRect(view), pad, pad), erase);
            using var plain = new SKPaint();
            DrawLandCoverage(canvas, lowers, view, options, plain);
            canvas.Restore();
        }

        void DrawOverviewCoverage()
        {
            if (UseCoarseCoverage(options))
            {
                using var coarseTint = SKColorFilter.CreateBlendMode(ParseColor(style.FillColor), SKBlendMode.SrcIn);
                using var coarsePaint = new SKPaint { ColorFilter = coarseTint, IsAntialias = false };
                CoarseCoverage.Draw(canvas, layer.Id, layer.GeometryTiles, view, sampling, coarsePaint, BuildCoverageImage);
                return;
            }
            using var fullTilePath = new SKPath();
            var partial = new List<(TileKey Key, byte[] Coverage)>();
            foreach (var key in keys)
            {
                var coverage = layer.GeometryTiles.TryGet(key)!;
                if (IsFullyCovered(coverage))
                {
                    fullTilePath.AddRect(TileRect(key));
                }
                else
                {
                    partial.Add((key, coverage));
                }
            }

            using (var fullPaint = new SKPaint
            {
                Color = ParseColor(style.FillColor),
                IsAntialias = false,
                Style = SKPaintStyle.Fill,
            })
            {
                canvas.DrawPath(fullTilePath, fullPaint);
            }

            if (partial.Count == 0)
            {
                return;
            }

            using var tint = SKColorFilter.CreateBlendMode(
                ParseColor(style.FillColor), SKBlendMode.SrcIn);
            using var partialPaint = new SKPaint { ColorFilter = tint, IsAntialias = false };
            foreach (var (key, coverage) in partial)
            {
                var image = options.TileCache is not null
                    ? options.TileCache.Get(layer.Id, key, TileImageKind.LandCoverage, coverage)
                    : BuildCoverageImage(coverage, TileImageKind.LandCoverage);
                canvas.DrawImage(image, TileRect(key), sampling, partialPaint);
                if (options.TileCache is null)
                {
                    image.Dispose();
                }
            }
        }

        void DrawCoverageRing(double radius, double width, SKColor color)
        {
            // A ring is a line of surf, not a hard band: a light blur on the finished ring hides
            // the pixel steps of the coverage and the square corners a box dilation leaves.
            using var soften = SKImageFilter.CreateBlur((float)(width * 0.3), (float)(width * 0.3));
            using var ringPaint = new SKPaint { ImageFilter = soften };
            canvas.SaveLayer(ToRect(view), ringPaint);
            uncarved = true;
            try
            {
                DrawExpandedCoverage(radius + width, color);
                using var erase = new SKPaint { BlendMode = SKBlendMode.DstOut };
                DrawExpandedCoverageCore(radius, null, erase);
                // The land itself, carved rivers included, never carries a surf line: the
                // ring lies in the sea, passing in front of a river mouth as a drawn coast does.
                DrawExpandedCoverageCore(0, null, erase);
            }
            finally { uncarved = false; }
            canvas.Restore();
        }

        void DrawExpandedCoverage(double radius, SKColor color) =>
            DrawExpandedCoverageCore(radius, color, paint: null);

        // The coverage grown by a radius, as one morphological dilation of the whole coverage
        // rather than eight shifted copies: a copy per direction gave every ring an eight-fold
        // shape at corners and, once a river mouth softened the coverage, a star of streaks. The
        // dilation follows the coast exactly, soft edge included.
        void DrawExpandedCoverageCore(double radius, SKColor? color, SKPaint? paint)
        {
            using var tint = paint is null && color is { } value
                ? SKColorFilter.CreateBlendMode(value, SKBlendMode.SrcIn)
                : null;
            using var grow = RoundGrowth(radius);
            using var layer = new SKPaint
            {
                ColorFilter = paint?.ColorFilter ?? tint,
                BlendMode = paint?.BlendMode ?? SKBlendMode.SrcOver,
                ImageFilter = grow,
                IsAntialias = true,
            };
            var reach = (float)(Math.Max(0, radius) * 2.5) + 1;
            canvas.SaveLayer(SKRect.Inflate(ToRect(view), reach, reach), layer);
            DrawCoverageImages(null);
            canvas.Restore();
        }
    }

    /// <summary>Which land the coverage raster of the distance field holds.</summary>
    private enum CoverageSet { ThisPiece, WithLowers, LowersOnly }

    private static byte Alpha(double opacity) =>
        (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);

    private static readonly byte[] RoundGrowthAlpha = BuildRoundGrowthAlpha();
    private static readonly byte[] IdentityTable = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];
    private static readonly byte[] SmoothEdgeAlpha = [.. Enumerable.Range(0, 256).Select(i =>
    {
        var t = Math.Clamp((i / 255.0 - 0.22) / 0.56, 0, 1);
        return (byte)Math.Round(t * t * (3 - 2 * t) * 255);
    })];

    /// <summary>
    /// Grows a coverage by <paramref name="radius"/> world units with round corners. A blur of
    /// r / 1.5 reaches 6.7 % at distance r from a straight edge; the alpha curve turns that level
    /// into the new edge. A dilation alone grows by a square, so every surf line and outline had
    /// square corners at a cape.
    /// </summary>
    private static SKImageFilter? RoundGrowth(double radius)
    {
        if (radius <= 0) return null;
        // The blur's softness grows with its radius, so only the last few units are grown
        // round; the rest is an exact dilation. The corner keeps a round end of that size and
        // the line stays as crisp as the coast it follows.
        var round = Math.Min(radius, 3);
        using var dilate = radius > round ? SKImageFilter.CreateDilate((float)(radius - round), (float)(radius - round)) : null;
        var sigma = (float)Math.Max(round / 1.5, 0.35);
        using var blur = SKImageFilter.CreateBlur(sigma, sigma, dilate);
        using var edge = SKColorFilter.CreateTable(RoundGrowthAlpha, IdentityTable, IdentityTable, IdentityTable);
        return SKImageFilter.CreateColorFilter(edge, blur);
    }

    private static byte[] BuildRoundGrowthAlpha()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var t = Math.Clamp((i / 255.0 - 0.035) / (0.10 - 0.035), 0, 1);
            table[i] = (byte)Math.Round(t * t * (3 - 2 * t) * 255);
        }
        return table;
    }

    private static void DrawObjects(
        SKCanvas canvas,
        MapDocument document,
        ObjectLayer layer,
        WorldBounds view,
        RenderOptions options)
    {
        // A screen holding a dense forest - many stamps in view at a readable size - is composited
        // into cached tiles: pan and revisits reuse them, and each tile is drawn as one atlas mesh.
        // A screen with a few hundred stamps draws them directly, which is faster than a tile
        // and needs no atlas at all, whatever the size of the layer behind it.
        if (!options.IsExport && options.ViewScale >= .03
            && options.ObjectCache is not null && options.VisibleWorld is not null
            && options.ObjectOverrides is not { Count: > 0 } && layer.Objects.Count >= DenseTileMinimumObjects
            && ObjectSpatialIndex.For(layer).MayContainAtLeast(view, DenseTileMinimumVisible)
            && RevisionsFor(layer).CanCache
            && document.Assets.All(asset => options.Stamps?.TryGetImage(asset.Sha256) is not null))
        {
            DrawDetailedStampTiles(canvas, document, layer, view, options);
            return;
        }
        if (options.Stats is { } skipped && !options.IsExport && layer.Objects.Count >= DenseTileMinimumObjects)
            skipped.DenseSkip = options.ViewScale < .03 ? "far" : options.ObjectCache is null ? "no-cache" : options.VisibleWorld is null ? "no-view"
                : options.ObjectOverrides is { Count: > 0 } ? "overrides" : !ObjectSpatialIndex.For(layer).MayContainAtLeast(view, DenseTileMinimumVisible) ? "sparse"
                : !RevisionsFor(layer).CanCache ? "uncacheable" : "asset-missing";

        if (!options.IsExport
            && options.LodTier == RenderLodTier.Overview
            && layer.SublayerStates.All(state => state.Visible)
            && options.ObjectCache is not null
            && options.VisibleWorld is not null)
        {
            DrawOverviewObjects(canvas, document, layer, view, options);
            return;
        }

        // A layer shadow may originate just outside the viewport. The expansion is the strict
        // maximum offset plus a conservative three-sigma blur reach; it remains a local query.
        var queryView = Expand(view,
            AppearanceLimits.MaxShadowOffset + (AppearanceLimits.MaxShadowBlur * 3));
        IReadOnlyList<SceneObject> candidates = options.VisibleWorld is null
            ? layer.Objects
            : ObjectSpatialIndex.For(layer).Query(queryView);
        if (layer.SublayerStates.Any(state => !state.Visible))
            candidates = [.. candidates.Where(item => layer.StateFor(item.Sublayer).Visible)];

        IReadOnlyList<SceneObject> ordered;
        if (options.ObjectOverrides is not { Count: > 0 } overrideList)
        {
            // Sublayer bands, and with «Ближние поверх дальних» (R10) the lower edge inside a band.
            ordered = SceneGeometry.InDrawOrder(layer, candidates);
        }
        else
        {
            var overrides = overrideList.ToDictionary(sceneObject => sceneObject.Id);
            var applied = new HashSet<ObjectId>();
            var index = DocumentRevisionIndex.For(document);
            var resolved = new List<(SceneObject Object, int Position)>(
                candidates.Count + overrides.Count);
            foreach (var candidate in candidates)
            {
                if (!index.TryFind(candidate.Id, out var location))
                {
                    continue;
                }

                SceneObject current;
                if (overrides.TryGetValue(candidate.Id, out var replacement))
                {
                    current = replacement;
                    applied.Add(candidate.Id);
                }
                else
                {
                    current = candidate;
                }

                if (options.VisibleWorld is null || SceneGeometry.BoundsOf(current).Intersects(queryView))
                {
                    resolved.Add((current, location.ObjectIndex));
                }
            }

            // A moved object whose old bounds were outside the viewport was not a base candidate.
            foreach (var replacement in overrideList)
            {
                if (!applied.Contains(replacement.Id)
                    && index.TryFind(replacement.Id, out var location)
                    && location.LayerId == layer.Id
                    && (options.VisibleWorld is null
                        || SceneGeometry.BoundsOf(replacement).Intersects(queryView)))
                {
                    resolved.Add((replacement, location.ObjectIndex));
                }
            }

            var depthSort = layer.Effects.DepthSort;
            resolved.Sort((left, right) =>
            {
                var sublayer = left.Object.Sublayer.CompareTo(right.Object.Sublayer);
                if (sublayer != 0) return sublayer;
                var depth = depthSort ? SceneGeometry.DepthKey(left.Object).CompareTo(SceneGeometry.DepthKey(right.Object)) : 0;
                return depth != 0 ? depth : left.Position.CompareTo(right.Position);
            });
            ordered = [.. resolved.Select(entry => entry.Object)];
        }

        if (options.Stats is { } stats)
        {
            stats.ObjectCandidates += ordered.Count;
        }

        // Exact/interactive rendering evaluates "behind layer" as one bounded prepass. Overview
        // intentionally keeps its existing point representation and omits expensive effects.
        if (options.LodTier != RenderLodTier.Overview)
        {
            foreach (var sceneObject in ordered)
            {
                DrawLayerStampShadow(canvas, sceneObject, document.StampForeground, options);
            }
            // R10 «Подложка массива»: the soft blots under all objects of the run, before any of them.
            AreaBeautyRenderer.DrawMasses(canvas, ordered, options);
        }

        var hiddenLabels = LabelPriority.Hidden(document, options.ViewScale);
        for (var objectIndex = 0; objectIndex < ordered.Count; objectIndex++)
        {
            var sceneObject = ordered[objectIndex];
            if (!sceneObject.Visible || GivesWay(sceneObject, hiddenLabels, options))
            {
                continue;
            }

            var objectStarted = Stopwatch.GetTimestamp();
            if (!options.IsExport && TryDrawStampBatch(canvas, ordered, objectIndex, options) is > 0 and var batched)
            {
                objectIndex += batched - 1;
                if (options.Stats is { } batchStats)
                    batchStats.StampBatchMilliseconds += Stopwatch.GetElapsedTime(objectStarted).TotalMilliseconds;
                continue;
            }
            DrawObject(canvas, sceneObject, document.StampForeground, options);
            if (options.Stats is { } s)
            {
                if (sceneObject.Kind == SceneObjectKind.Stamp)
                    s.StampSingleMilliseconds += Stopwatch.GetElapsedTime(objectStarted).TotalMilliseconds;
                s.ObjectsDrawn++;
                s.ExactObjects++;
                s.DrawCalls++;
                s.RecordExactObject(sceneObject);
            }
        }
    }

    // Dense neutral stamp layers are composited into screen-sized tiles. Pan reuses their exact
    // source-over result; a document delta invalidates only intersecting tiles. Complex paints
    // keep the direct evaluator, since their result can depend on the destination behind them.
    private sealed class DetailTileRevisions
    {
        private readonly DetailTileRevisions? _parent;
        private readonly WorldBounds[] _changed;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(int X, int Y, int Zoom), ulong> _versions = new();
        private readonly int _unsupported;
        private readonly int _nonzeroSublayers;
        private readonly SceneObject[]? _appended;
        public bool CanCache => _unsupported == 0;
        public bool AllOnFirstSublayer => _nonzeroSublayers == 0;
        public DetailTileRevisions(ObjectLayer layer)
        {
            if (ObjectLayerChanges.TryGet(layer, out var delta))
            {
                var previous = RevisionsFor(delta.Source);
                _parent = previous;
                _unsupported = previous._unsupported - delta.Removed.Count(i => !Neutral(delta.Source.Objects[i]))
                    + delta.Added.Count(i => !Neutral(layer.Objects[i]));
                _nonzeroSublayers = previous._nonzeroSublayers - delta.Removed.Count(i => delta.Source.Objects[i].Sublayer != 0)
                    + delta.Added.Count(i => layer.Objects[i].Sublayer != 0);
                if (delta.Removed.Count == 0 && _nonzeroSublayers == 0
                    && delta.Added.All(i => i >= delta.Source.Objects.Count))
                    _appended = delta.Added.Select(i => layer.Objects[i]).ToArray();
                _changed = delta.Removed.Select(i => SceneGeometry.BoundsOf(delta.Source.Objects[i]))
                    .Concat(delta.Added.Select(i => SceneGeometry.BoundsOf(layer.Objects[i]))).ToArray();
            }
            else
            {
                _unsupported = layer.Objects.Count(o => !Neutral(o));
                _nonzeroSublayers = layer.Objects.Count(o => o.Sublayer != 0);
                _changed = [];
            }
        }
        public bool TryAppendBase(ObjectOverviewCache cache, LayerId layer, int x, int y, int zoom,
            WorldBounds bounds, out ObjectOverviewCacheResult basis, out IReadOnlyList<SceneObject> tail)
        {
            var segments = new List<SceneObject[]>();
            var current = this;
            for (var depth = 0; depth < 64 && current._appended is { } appended && current._parent is { } parent; depth++)
            {
                segments.Add(appended);
                // The plain detail key only: a depth-sorted layer (R10) is kept under its own key, so an append
                // that would break its order by Y never finds a base here and the tile is rebuilt instead.
                if (cache.TryGet(layer, x, y, 10000 + zoom, parent.Version(x, y, zoom, bounds), out basis))
                {
                    segments.Reverse();
                    tail = segments.SelectMany(s => s).Where(o => o.Visible && SceneGeometry.BoundsOf(o).Intersects(bounds)).ToArray();
                    return true;
                }
                current = parent;
            }
            basis = default; tail = [];
            return false;
        }
        public ulong Version(int x, int y, int zoom, WorldBounds bounds) => _versions.GetOrAdd((x, y, zoom), key =>
        {
            if (_parent is not null && !_changed.Any(b => b.Intersects(bounds))) return _parent.Version(x, y, zoom, bounds);
            return unchecked((ulong)System.Threading.Interlocked.Increment(ref _detailRevision));
        });
        private static bool Neutral(SceneObject o) => o.Kind == SceneObjectKind.Stamp
            && o.Stamp is { Blur: 0, ShadowOpacity: 0, ClipMaskEnabled: false, Blend: "Normal" };
    }

    private static void DrawDetailedStampTiles(SKCanvas canvas, MapDocument document, ObjectLayer layer, WorldBounds view,
        RenderOptions options, int? prepareBucket = null)
    {
        options.ObjectCache!.BindGpuContext(options.GpuContext?.Handle ?? IntPtr.Zero);
        options.AtlasCache?.BindGpuContext(options.GpuContext?.Handle ?? IntPtr.Zero);
        var bucket = prepareBucket ?? (int)Math.Ceiling(Math.Log2(options.ViewScale) * 4);
        var scale = Math.Pow(2, bucket / 4d);
        var worldSize = 512 / scale;
        var revisions = RevisionsFor(layer);
        var cache = options.ObjectCache!;
        var hits = cache.Hits; var misses = cache.Misses;
        // While the camera moves, the tiles of the nearest zoom bucket that are all still kept
        // are drawn resampled instead of building this bucket for every notch of the wheel;
        // the settled frame builds the exact bucket. Like the terrain: same objects, only scaled.
        if (prepareBucket is null && options.FastTerrain)
        {
            if (TryDrawKeptTiles(canvas, layer, view, bucket, revisions, cache, options)) return;
            // Twelve buckets either way: a wheel zoom from a whole-map view to 160 % crosses eight,
            // and the bucket the zoom started from is the one that is complete.
            for (var delta = 1; delta <= 12; delta++)
            {
                if (TryDrawKeptTiles(canvas, layer, view, bucket - delta, revisions, cache, options)
                    || TryDrawKeptTiles(canvas, layer, view, bucket + delta, revisions, cache, options))
                {
                    if (options.Stats is { } keptStats) keptStats.TilesReused++;
                    return;
                }
            }
        }
        using var batchScratch = new StampBatchScratch();
        // Keep a small raster overview of the whole page, using the same stamp evaluator. Unlike
        // a neighbouring zoom level it also covers tiles first revealed by a pan. Preparing it
        // only seeds the cache (normally one 514-square tile); it never paints under exact tiles,
        // which would composite translucent stamps twice. Everything stays on the Skia thread.
        var canRefine = prepareBucket is null && !options.IsExport && options.RequestRefinedFrame is not null;
        var page = new WorldBounds(0, 0, document.Canvas.Width, document.Canvas.Height);
        var fallbackBucket = canRefine ? NearestKeptBucket(layer, view, bucket, revisions, cache) : null;
        if (canRefine && fallbackBucket is null)
        {
            var overviewBucket = (int)Math.Floor(Math.Log2(512d / Math.Max(document.Canvas.Width, document.Canvas.Height)) * 4);
            if (overviewBucket < bucket)
            {
                DrawDetailedStampTiles(canvas, document, layer, page, options, overviewBucket);
                fallbackBucket = overviewBucket;
            }
        }
        var budgetTiles = canRefine && cache.BeginDetailView(layer.Id, DetailZoomKey(layer, bucket), view, worldSize, 514L * 514 * 4);
        var budgetStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var builtThisFrame = 0;
        var deferredTiles = 0;
        for (var y = (int)Math.Floor(view.Top / worldSize); y <= (int)Math.Floor(view.Bottom / worldSize); y++)
        for (var x = (int)Math.Floor(view.Left / worldSize); x <= (int)Math.Floor(view.Right / worldSize); x++)
        {
            var bounds = new WorldBounds(x * worldSize, y * worldSize, (x + 1) * worldSize, (y + 1) * worldSize);
            var revision = revisions.Version(x, y, bucket, Expand(bounds, 1 / scale));
            var exactKept = cache.TryGet(layer.Id, x, y, DetailZoomKey(layer, bucket), revision, out _);
            // A tile also waits while the images it samples at this zoom are still being decoded
            // off the render thread; the neighbouring level stands in for it meanwhile.
            if (!exactKept && fallbackBucket is { } fallback
                && (budgetTiles && builtThisFrame > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(budgetStarted).TotalMilliseconds > DetailBudgetMilliseconds
                    || !StampImagesReady(layer, bounds, scale, options)))
            {
                canvas.Save();
                canvas.ClipRect(ToRect(bounds));
                // A tile may straddle the document boundary; only its part inside the page needs
                // coverage. If anything was evicted, build exactly rather than leave a hole.
                var covered = TryIntersect(bounds, page, out var inside)
                    && TryDrawKeptTiles(canvas, layer, inside, fallback, revisions, cache, options);
                canvas.Restore();
                if (covered)
                {
                    deferredTiles++;
                    if (options.Stats is { } pending) pending.DetailTilesPending++;
                    options.RequestRefinedFrame?.Invoke();
                    continue;
                }
            }
            if (!exactKept) builtThisFrame++;
            var objectCount = 0; var culledCount = 0;
            var drawnBefore = options.Stats?.ObjectsDrawn ?? 0;
            var cached = cache.GetOrCreate(layer.Id, x, y, DetailZoomKey(layer, bucket), revision, () =>
            {
                // Preserve the established platform pixel layout on both paths: changing it
                // alters Skia's premultiplied rounding when translucent tiles are appended.
                var tileInfo = new SKImageInfo(514, 514);
                using var surface = (options.GpuContext is { } gpu
                    ? SKSurface.Create(gpu, true, tileInfo) : null) ?? SKSurface.Create(tileInfo)!;
                var target = surface.Canvas;
                target.Clear(SKColors.Transparent);
                // Appended objects go on top of the kept tile; under «Ближние поверх дальних» they may belong below.
                ObjectOverviewCacheResult basis = default!; IReadOnlyList<SceneObject> appended = [];
                var incremental = !layer.Effects.DepthSort && revisions.TryAppendBase(cache, layer.Id, x, y, bucket, Expand(bounds, 1 / scale),
                    out basis, out appended) && !appended.Any(AreaBeautyRenderer.HasMass);
                if (incremental)
                {
                    using var copy = new SKPaint { BlendMode = SKBlendMode.Src };
                    target.DrawImage(basis.Image, 0, 0, copy);
                }
                target.Translate(1, 1);
                target.Scale((float)scale);
                target.Translate((float)-bounds.Left, (float)-bounds.Top);
                var prepareStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                var candidates = incremental ? appended : ObjectSpatialIndex.For(layer).Query(Expand(bounds, 1 / scale));
                if (layer.SublayerStates.Any(s => !s.Visible)) candidates = candidates.Where(o => layer.StateFor(o.Sublayer).Visible).ToArray();
                var drawOrder = SceneGeometry.InDrawOrder(layer, candidates);
                var ordered = StampOcclusion.Cull(drawOrder, Expand(bounds, 1 / scale), scale, options.Stamps);
                objectCount = (incremental ? basis.ObjectCount : 0) + ordered.Count(o => o.Visible);
                culledCount = (incremental ? basis.CulledCount : 0) + candidates.Count - ordered.Count;
                if (options.Stats is { } preparation)
                    preparation.DetailPrepareMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(prepareStarted).TotalMilliseconds;
                var rasterStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                AreaBeautyRenderer.DrawMasses(target, drawOrder, options);
                if (!TryDrawDenseAtlas(target, ordered, layer.Id, bucket, cache, options, batchScratch))
                for (var i = 0; i < ordered.Count; i++)
                {
                    if (!ordered[i].Visible) continue;
                    var batch = TryDrawStampBatch(target, ordered, i, options, batchScratch);
                    if (batch > 0) i += batch - 1;
                    else DrawObject(target, ordered[i], document.StampForeground, options);
                }
                var snapshot = surface.Snapshot();
                // Keep the finished texture on the context that built it. Reading every new
                // tile back to the CPU and uploading it again stalled each cold zoom level.
                // BindGpuContext drops these entries before another context can use them.
                if (snapshot.IsTextureBacked)
                {
                    if (options.Stats is { } gpuStats) gpuStats.GpuDetailTiles++;
                }
                if (options.Stats is { } raster)
                    raster.DetailRasterMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(rasterStarted).TotalMilliseconds;
                return snapshot;
            }, () => (objectCount, culledCount), fallback: prepareBucket is not null);
            // One pixel of bleed avoids seams from neighboring tiles under fractional pan/zoom.
            if (prepareBucket is null)
            {
                canvas.DrawImage(cached.Image, new SKRect(1, 1, 513, 513),
                    new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom),
                    new SKSamplingOptions(SKFilterMode.Linear));
                if (options.Stats is { } stats)
                {
                    stats.DrawCalls++; stats.ObjectTilesDrawn++;
                    stats.ObjectsDrawn = drawnBefore + cached.ObjectCount;
                    stats.StampOccludedObjects += cached.CulledCount;
                }
            }
        }
        if (canRefine && deferredTiles == 0) cache.CompleteDetailView(layer.Id);
        if (options.Stats is { } finalStats)
        { finalStats.ObjectCacheHits += cache.Hits - hits; finalStats.ObjectCacheMisses += cache.Misses - misses; }
    }

    /// <summary>The cache slot of exact object tiles: a depth-sorted layer (R10) keeps its own tiles.</summary>
    private static int DetailZoomKey(ObjectLayer layer, int bucket) => (layer.Effects.DepthSort ? 40000 : 10000) + bucket;

    /// <summary>Milliseconds of exact object tiles one settled frame builds before deferring the rest.</summary>
    private const double DetailBudgetMilliseconds = 24;

    /// <summary>Whether a tile's stamps can be drawn without decoding on this thread (see <see cref="IStampImageSource.PrepareForPixelSizes"/>).</summary>
    private static bool StampImagesReady(ObjectLayer layer, WorldBounds bounds, double scale, RenderOptions options) =>
        options.Stamps is not { } stamps || options.RequestRefinedFrame is not { } ready || stamps.PrepareForPixelSizes(
            ObjectSpatialIndex.For(layer).Query(Expand(bounds, 1 / scale))
                .Where(o => o.Visible && o.Stamp is not null)
                .Select(o => (o.Stamp!.AssetSha256, DetailPixels(o.Width, scale, options), DetailPixels(o.Height, scale, options))),
            ready);

    /// <summary>The nearest zoom bucket whose kept tiles cover the whole view, or null when none does.</summary>
    private static int? NearestKeptBucket(ObjectLayer layer, WorldBounds view, int bucket,
        DetailTileRevisions revisions, ObjectOverviewCache cache)
    {
        for (var delta = 1; delta <= 12; delta++)
        {
            if (HasKeptTiles(layer, view, bucket - delta, revisions, cache)) return bucket - delta;
            if (HasKeptTiles(layer, view, bucket + delta, revisions, cache)) return bucket + delta;
        }
        return null;
    }

    private static bool HasKeptTiles(ObjectLayer layer, WorldBounds view, int bucket,
        DetailTileRevisions revisions, ObjectOverviewCache cache)
    {
        var scale = Math.Pow(2, bucket / 4d);
        var worldSize = 512 / scale;
        var count = 0;
        for (var y = (int)Math.Floor(view.Top / worldSize); y <= (int)Math.Floor(view.Bottom / worldSize); y++)
        for (var x = (int)Math.Floor(view.Left / worldSize); x <= (int)Math.Floor(view.Right / worldSize); x++)
        {
            if (++count > 64) return false;
            var bounds = new WorldBounds(x * worldSize, y * worldSize, (x + 1) * worldSize, (y + 1) * worldSize);
            if (!cache.TryGet(layer.Id, x, y, DetailZoomKey(layer, bucket), revisions.Version(x, y, bucket, Expand(bounds, 1 / scale)), out _)) return false;
        }
        return true;
    }

    /// <summary>Draws the view from kept tiles of one zoom bucket, or nothing when any tile of it is missing.</summary>
    private static bool TryDrawKeptTiles(SKCanvas canvas, ObjectLayer layer, WorldBounds view, int bucket,
        DetailTileRevisions revisions, ObjectOverviewCache cache, RenderOptions options)
    {
        var scale = Math.Pow(2, bucket / 4d);
        var worldSize = 512 / scale;
        var tiles = new List<(WorldBounds Bounds, ObjectOverviewCacheResult Tile)>();
        for (var y = (int)Math.Floor(view.Top / worldSize); y <= (int)Math.Floor(view.Bottom / worldSize); y++)
        for (var x = (int)Math.Floor(view.Left / worldSize); x <= (int)Math.Floor(view.Right / worldSize); x++)
        {
            if (tiles.Count > 64) return false;
            var bounds = new WorldBounds(x * worldSize, y * worldSize, (x + 1) * worldSize, (y + 1) * worldSize);
            var revision = revisions.Version(x, y, bucket, Expand(bounds, 1 / scale));
            if (!cache.TryGet(layer.Id, x, y, DetailZoomKey(layer, bucket), revision, out var kept)) return false;
            tiles.Add((bounds, kept));
        }
        var drawn = 0;
        foreach (var (bounds, tile) in tiles)
        {
            canvas.DrawImage(tile.Image, new SKRect(1, 1, 513, 513),
                new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            drawn += tile.ObjectCount;
        }
        if (options.Stats is { } stats) { stats.DrawCalls += tiles.Count; stats.ObjectTilesDrawn += tiles.Count; stats.ObjectsDrawn += drawn; }
        return true;
    }

    /// <summary>Consecutive, compatible stamps share one submission without reordering the scene.</summary>
    private static int TryDrawStampBatch(SKCanvas canvas, IReadOnlyList<SceneObject> objects, int start,
        RenderOptions options, StampBatchScratch? scratch = null)
    {
        var first = objects[start];
        if (!Compatible(first) || options.Stamps?.TryGetImage(first.Stamp!.AssetSha256) is not { } image) return 0;
        var canvasMatrix = canvas.TotalMatrix;
        var pixelScale = Math.Sqrt(canvasMatrix.ScaleX * canvasMatrix.ScaleX + canvasMatrix.SkewY * canvasMatrix.SkewY);
        // Skia's raster atlas shader omits mipmap sampling. Keep the independent DrawImage
        // evaluator for minified CPU stamps; the native GPU atlas honours the sampling contract.
        if (options.GpuContext is null
            && (first.Width * pixelScale < image.Width || first.Height * pixelScale < image.Height)) return 0;
        var count = 0;
        while (start + count < objects.Count && count < 8192)
        {
            var item = objects[start + count];
            if (!Compatible(item) || item.Stamp!.AssetSha256 != first.Stamp!.AssetSha256
                // A batch has one sampled image. Mixing dimensions chooses the largest
                // member's mip for earlier stamps, changing their pixels on an append/rebuild.
                || item.Width != first.Width || item.Height != first.Height
                || Math.Abs(item.Width / image.Width - item.Height / image.Height) > 0.000001) break;
            count++;
        }
        // On the native GPU use the same sampling/transform evaluator for one stamp and a
        // consecutive run. Otherwise an appended cache tile changes its pixels when the same
        // stamps are later rebuilt as an atlas batch (notably at fractional detail scales).
        if (count == 0 || (count == 1 && options.GpuContext is null)) return 0;
        if (options.Stamps is { } stamps)
        {
            image = stamps.TryGetImageForPixelSize(first.Stamp!.AssetSha256,
                DetailPixels(first.Width, pixelScale, options), DetailPixels(first.Height, pixelScale, options)) ?? image;
        }
        // Odd-sized source images round their mip width and height independently. DrawAtlas
        // has only a uniform scale; DrawImage preserves the exact rectangle in this case.
        if (Math.Abs(first.Width / image.Width - first.Height / image.Height) > 0.000001) return 0;
        var (transforms, sources, colors) = scratch?.Get(count)
            ?? (new SKRotationScaleMatrix[count], new SKRect[count], new SKColor[count]);
        for (var i = 0; i < count; i++)
        {
            var item = objects[start + i];
            var angle = item.RotationDegrees * Math.PI / 180;
            var scale = item.Width / image.Width;
            var cos = scale * Math.Cos(angle); var sin = scale * Math.Sin(angle);
            transforms[i] = new((float)cos, (float)sin,
                (float)(item.X + item.Width / 2 - cos * image.Width / 2 + sin * image.Height / 2),
                (float)(item.Y + item.Height / 2 - sin * image.Width / 2 - cos * image.Height / 2));
            sources[i] = new(0, 0, image.Width, image.Height);
            colors[i] = SKColors.White.WithAlpha(Alpha(item.ObjectOpacity));
        }
        using var ownedPaint = scratch is null
            ? new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.SrcOver } : null;
        var paint = scratch?.Paint ?? ownedPaint!;
        canvas.DrawAtlas(image, sources, transforms, colors, SKBlendMode.Modulate,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        if (options.Stats is { } stats)
        {
            stats.DrawCalls++; stats.StampAtlasBatches++; stats.ObjectsDrawn += count; stats.ExactObjects += count;
            for (var i = 0; i < count; i++) stats.RecordExactObject(objects[start + i]);
        }
        return count;

        static bool Compatible(SceneObject item) => item.Visible && item.Kind == SceneObjectKind.Stamp
            && item.Stamp is { FlipHorizontal: false, FlipVertical: false, ClipMaskEnabled: false,
                ShadowOpacity: 0, Blur: 0, Hue: 0, Saturation: 0, Brightness: 0, Contrast: 0, Blend: "Normal",
                Tint: null, GroundShadow: null };
    }

    private static void DrawTiles(
        SKCanvas canvas, LayerId layerId, TileMap tiles, WorldBounds view,
        RenderOptions options, TileImageKind kind)
    {
        if (tiles.TileCount == 0)
        {
            return;
        }

        // Below 1:1 the tiles are minified; mip sampling keeps a fully zoomed-out map from
        // shimmering and is what makes the deep-zoom pass cheap.
        var minified = options.ViewScale < 0.9;
        var sampling = minified
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        using var paint = new SKPaint { IsAntialias = false };
        var cache = options.TileCache;

        foreach (var key in tiles.KeysIntersecting(view.Left, view.Top, view.Right, view.Bottom))
        {
            var pixels = tiles.TryGet(key);
            if (pixels is null)
            {
                continue;
            }

            var image = cache is not null
                ? cache.Get(layerId, key, kind, pixels)
                : SKImage.FromPixelCopy(
                    new SKImageInfo(TileMap.TileSize, TileMap.TileSize, SKColorType.Rgba8888, SKAlphaType.Premul),
                    pixels);

            canvas.DrawImage(image, TileRect(key), sampling, paint);

            if (cache is null)
            {
                image.Dispose();
            }

            if (options.Stats is { } stats)
            {
                if (kind == TileImageKind.Content)
                {
                    stats.TilesDrawn++;
                }
                else
                {
                    stats.MaskTilesDrawn++;
                }

                if (minified)
                {
                    stats.MipmappedTiles++;
                }
            }
        }
    }

    /// <summary>
    /// Multiplies the group by the mask's coverage. Only the tiles the mask actually has are
    /// composited, so an area with no mask tile keeps its pixels - "missing tile = fully visible".
    /// </summary>
    private static void ApplyMask(
        SKCanvas canvas, LayerId layerId, LayerMask mask, WorldBounds view, RenderOptions options)
    {
        using var paint = new SKPaint { BlendMode = SKBlendMode.DstIn, IsAntialias = false };
        var cache = options.TileCache;
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        foreach (var key in mask.Tiles.KeysIntersecting(view.Left, view.Top, view.Right, view.Bottom))
        {
            var coverage = mask.Tiles.TryGet(key);
            if (coverage is null || !MaskHasHiddenPixels(coverage))
            {
                continue;
            }

            var image = cache is not null
                ? cache.Get(layerId, key, TileImageKind.MaskAlpha, coverage)
                : BuildCoverageImage(coverage, TileImageKind.MaskAlpha);

            canvas.DrawImage(image, TileRect(key), sampling, paint);

            if (cache is null)
            {
                image.Dispose();
            }

            if (options.Stats is { } stats)
            {
                stats.MaskTilesDrawn++;
            }
        }
    }

    /// <summary>
    /// The cliff mask of the land the brush works on: «здесь стена» in terracotta, «здесь пляж» in
    /// sand, both light enough to see the coast through, and the live stroke over them.
    /// </summary>
    private static void DrawCliffMaskOverlay(SKCanvas canvas, List<LandLayer> lands, WorldBounds view, RenderOptions options)
    {
        if (options.CliffMaskLayerId is not { } id || lands.FirstOrDefault(l => l.Id == id) is not { } land) return;
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        foreach (var (tiles, color) in new[] { (land.CliffWallTiles, CliffWallTint), (land.CliffBeachTiles, CliffBeachTint) })
            foreach (var key in tiles.KeysIntersecting(view.Left, view.Top, view.Right, view.Bottom))
                if (tiles.TryGet(key) is { } coverage)
                {
                    using var image = TintedCoverage(coverage, color);
                    canvas.DrawImage(image, TileRect(key), sampling);
                }
        if (options.Preview is { IsCliffMask: true } preview && preview.LayerId == land.Id)
            DrawStrokePreview(canvas, preview, view, options);
    }

    public static readonly SKColor CliffWallTint = new(0xB8, 0x5C, 0x38, 0x70);
    public static readonly SKColor CliffBeachTint = new(0xE6, 0xCC, 0x7A, 0x80);

    private static SKImage TintedCoverage(byte[] coverage, SKColor color)
    {
        var pixels = new byte[TileMap.TileSize * TileMap.TileSize * 4];
        for (var i = 0; i < coverage.Length; i++)
        {
            var c = coverage[i];
            if (c == 0) continue;
            var a = c / 255.0 * color.Alpha / 255.0;
            var o = i * 4;
            pixels[o] = (byte)Math.Round(color.Red * a); pixels[o + 1] = (byte)Math.Round(color.Green * a);
            pixels[o + 2] = (byte)Math.Round(color.Blue * a); pixels[o + 3] = (byte)Math.Round(255 * a);
        }
        var info = new SKImageInfo(TileMap.TileSize, TileMap.TileSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        return SKImage.FromPixelCopy(info, pixels, info.RowBytes)!;
    }

    private static void DrawMaskOverlay(
        SKCanvas canvas, MapLayer layer, WorldBounds view, RenderOptions options)
    {
        var mask = layer.Mask!;
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha(110), IsAntialias = false };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        var cache = options.TileCache;

        foreach (var key in mask.Tiles.KeysIntersecting(view.Left, view.Top, view.Right, view.Bottom))
        {
            var coverage = mask.Tiles.TryGet(key);
            if (coverage is null)
            {
                continue;
            }

            var image = cache is not null
                ? cache.Get(layer.Id, key, TileImageKind.MaskOverlay, coverage)
                : BuildCoverageImage(coverage, TileImageKind.MaskOverlay);

            canvas.DrawImage(image, TileRect(key), sampling, paint);

            if (cache is null)
            {
                image.Dispose();
            }
        }

        // The live part of the mask gesture, tinted the same red.
        if (options.Preview is { IsMask: true } preview && preview.LayerId == layer.Id)
        {
            DrawStrokePreview(canvas, preview, view, options);
        }
    }

    private static void DrawStrokePreview(
        SKCanvas canvas, StrokePreview preview, WorldBounds view, RenderOptions options)
    {
        var (r, g, b, a) = preview.IsMask
            ? ((byte)255, (byte)0, (byte)0, (byte)110)
            : ParseChannels(preview.Color);

        var alpha = Math.Clamp(preview.Opacity, 0, 1) * (a / 255.0);
        using var paint = new SKPaint
        {
            IsAntialias = false,
            BlendMode = preview.Eraser ? SKBlendMode.DstOut : SKBlendMode.SrcOver,
        };

        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        foreach (var (key, coverage) in preview.Coverage)
        {
            var rect = TileRect(key);
            if (rect.Right < view.Left || rect.Left > view.Right
                || rect.Bottom < view.Top || rect.Top > view.Bottom)
            {
                continue;
            }

            // Only the tiles the dabs since the last frame changed are rebuilt; the rest of the
            // stroke is drawn from the images kept in the preview cache. Without a cache (export,
            // tests) every tile is built for the frame and dropped.
            SKImage BuildImage()
            {
                // One pooled buffer per tile build: a fresh 256 KiB array per tile per frame went
                // to the large-object heap and cost a full collection every few frames of a stroke.
                var pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(TileMap.TileSize * TileMap.TileSize * 4);
                try
                {
                if (!preview.IsMask && !preview.Eraser && preview.PaintSettings is { } settings)
                    RasterStrokeSession.RenderPreviewTile(settings, key, coverage, pixels);
                else
                {
                    Array.Clear(pixels, 0, TileMap.TileSize * TileMap.TileSize * 4);
                    Span<byte> lutR = stackalloc byte[256], lutG = stackalloc byte[256], lutB = stackalloc byte[256], lutA = stackalloc byte[256];
                    for (var c = 1; c < 256; c++)
                    {
                        var value = c / 255.0 * alpha;
                        lutR[c] = (byte)Math.Round(r * value); lutG[c] = (byte)Math.Round(g * value);
                        lutB[c] = (byte)Math.Round(b * value); lutA[c] = (byte)Math.Round(255 * value);
                    }
                    for (var i = 0; i < coverage.Length; i++)
                    {
                        var c = coverage[i];
                        if (c == 0) continue;
                        var offset = i * 4;
                        pixels[offset] = lutR[c]; pixels[offset + 1] = lutG[c]; pixels[offset + 2] = lutB[c]; pixels[offset + 3] = lutA[c];
                    }
                }

                return SKImage.FromPixelCopy(
                    new SKImageInfo(TileMap.TileSize, TileMap.TileSize, SKColorType.Rgba8888, SKAlphaType.Premul),
                    pixels.AsSpan(0, TileMap.TileSize * TileMap.TileSize * 4));
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(pixels); }
            }

            if (options.PreviewCache is { } cache && preview.Versions is { } versions && versions.TryGetValue(key, out var version))
            {
                canvas.DrawImage(cache.Get(preview, key, coverage, version, BuildImage), rect, sampling, paint);
                continue;
            }

            using var image = BuildImage();
            canvas.DrawImage(image, rect, sampling, paint);
        }
    }

    private static SKImage BuildCoverageImage(byte[] coverage, TileImageKind kind)
    {
        var pixels = new byte[TileMap.TileSize * TileMap.TileSize * 4];
        for (var i = 0; i < coverage.Length; i++)
        {
            var offset = i * 4;
            if (kind is TileImageKind.MaskAlpha or TileImageKind.LandCoverage)
            {
                var v = coverage[i];
                pixels[offset] = v;
                pixels[offset + 1] = v;
                pixels[offset + 2] = v;
                pixels[offset + 3] = v;
            }
            else
            {
                var hidden = (byte)(255 - coverage[i]);
                pixels[offset] = hidden;
                pixels[offset + 3] = hidden;
            }
        }

        return SKImage.FromPixelCopy(
            new SKImageInfo(TileMap.TileSize, TileMap.TileSize, SKColorType.Rgba8888, SKAlphaType.Premul),
            pixels);
    }

    private static void DrawObject(
        SKCanvas canvas,
        SceneObject sceneObject,
        StampForegroundSurface? stampForeground,
        RenderOptions options,
        bool prepareGpuEffect = true)
    {
        if (prepareGpuEffect && options.GpuContext is not null
            && sceneObject.Stamp is { Blend: "Normal", ClipMaskEnabled: false } effect
            && (effect.Blur > 0 || effect.ShadowOpacity > 0 && effect.ShadowBlur > 0)
            && TryDrawRasterStampEffect(canvas, sceneObject, stampForeground, options)) return;
        canvas.Save();
        ApplyRotation(canvas, sceneObject);

        var rect = new SKRect(
            (float)sceneObject.X,
            (float)sceneObject.Y,
            (float)(sceneObject.X + sceneObject.Width),
            (float)(sceneObject.Y + sceneObject.Height));

        var objectAlpha = (byte)Math.Clamp(Math.Round(sceneObject.ObjectOpacity * 255), 0, 255);

        if (sceneObject.Kind == SceneObjectKind.Stamp && sceneObject.Stamp is { } stamp)
        {
            DrawStamp(canvas, sceneObject, stamp, rect, objectAlpha, stampForeground, options);
            canvas.Restore();
            return;
        }

        if (sceneObject.Kind == SceneObjectKind.Line && sceneObject.Line is { } line)
        {
            DrawVectorStroke(
                canvas, sceneObject,
                VectorGeometry.Flatten(sceneObject, line.Nodes, line.Closed),
                line.Closed, line.Portals, line.Stroke, objectAlpha, options);
            canvas.Restore();
            return;
        }

        if (sceneObject.Kind == SceneObjectKind.Shape && sceneObject.Shape is { } shape)
        {
            DrawVectorShape(canvas, sceneObject, shape, objectAlpha, options);
            canvas.Restore();
            return;
        }

        if (sceneObject.Kind == SceneObjectKind.Text && sceneObject.Text is { } text)
        {
            DrawText(canvas, sceneObject, text, rect, objectAlpha, options);
            canvas.Restore();
            return;
        }

        if (sceneObject.Kind == SceneObjectKind.Composition && sceneObject.Composition is { } composition)
        {
            DrawComposition(canvas, sceneObject, composition, objectAlpha, options);
            canvas.Restore();
            return;
        }

        if (sceneObject.Kind == SceneObjectKind.Decoration && sceneObject.Decoration is { } decoration)
        {
            DecorationRenderer.Draw(canvas, decoration, rect, objectAlpha, options);
            canvas.Restore();
            return;
        }

        if (sceneObject.Kind == SceneObjectKind.Placeholder)
        {
            DrawPlaceholder(canvas, rect, objectAlpha);
            canvas.Restore();
            return;
        }

        using (var fillGradient = CreateGradientShader(sceneObject.FillGradient, rect, objectAlpha))
        using (var fill = new SKPaint
        {
            Color = ParseColor(sceneObject.FillColor).WithScaledAlpha(objectAlpha),
            Shader = fillGradient,
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        })
        {
            DrawShape(canvas, sceneObject.Kind, rect, fill);
        }

        if (sceneObject.StrokeWidth > 0)
        {
            using var stroke = new SKPaint
            {
                Color = ParseColor(sceneObject.StrokeColor).WithScaledAlpha(objectAlpha),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)sceneObject.StrokeWidth,
            };

            DrawShape(canvas, sceneObject.Kind, rect, stroke);
        }

        canvas.Restore();
    }

    /// <summary>
    /// When a layer's stamps are composited into cached tiles instead of drawn one by one: the
    /// layer is large and the view holds many of them. Below that the direct path is cheaper
    /// (a few hundred atlas draws a frame) and needs no tile or atlas memory.
    /// </summary>
    private const int DenseTileMinimumObjects = 2000;
    private const int DenseTileMinimumVisible = 1200;

    /// <summary>One raster atlas per zoom bucket holds every (asset, mip) a dense tile needs; the largest allowed.</summary>
    private const int DenseAtlasMaxSide = 4096;

    /// <summary>
    /// Every neutral stamp of a dense tile in one mesh: an atlas of the mips the tile needs (one
    /// sprite per asset and world size, packed on shelves) is sampled bilinearly, and the
    /// objects become quads in their original order, so overlap and opacity are what the map
    /// says. Texture switches are the only thing that disappears; a tile of ten thousand trees
    /// is a handful of draw calls. The atlas is kept per zoom bucket in the atlas cache.
    /// </summary>
    private static bool TryDrawDenseAtlas(SKCanvas canvas, IReadOnlyList<SceneObject> objects,
        LayerId layer, int bucket, ObjectOverviewCache cache, RenderOptions options, StampBatchScratch scratch)
    {
        if (options.GpuContext is null || options.Stamps is not { } stamps || objects.Count == 0) return false;
        var matrix = canvas.TotalMatrix;
        var scale = Math.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY);
        var sources = new Dictionary<(string Sha, double Width, double Height), SKImage>();
        var anyShadow = false;
        foreach (var item in objects)
        {
            if (!item.Visible) continue;
            // R10 tint and contact shadow stay in the mesh: a vertex colour and one more quad.
            if (item.Stamp is not { FlipHorizontal: false, FlipVertical: false, ClipMaskEnabled: false,
                ShadowOpacity: 0, Blur: 0, Hue: 0, Saturation: 0, Brightness: 0, Contrast: 0, Blend: "Normal" } stamp)
                return false;
            if (stamp.GroundShadow is { IsValid: true, Opacity: > 0 }) anyShadow = true;
            var key = (stamp.AssetSha256, item.Width, item.Height);
            if (sources.ContainsKey(key)) continue;
            if (stamps.TryGetImage(stamp.AssetSha256) is not { } original) return false;
            var image = stamps.TryGetImageForPixelSize(stamp.AssetSha256,
                DetailPixels(item.Width, scale, options), DetailPixels(item.Height, scale, options)) ?? original;
            if (image.Width > DenseAtlasMaxSide - 8 || image.Height > DenseAtlasMaxSide - 8) return false;
            sources.Add(key, image);
        }
        if (sources.Count == 0) return true;
        var shadowKey = (AreaBeautyRenderer.ShadowSpriteKey, -1d, -1d);
        if (anyShadow) sources.Add(shadowKey, AreaBeautyRenderer.ShadowSprite);

        // One sprite per sampled image, not per object size: stamps of one asset at different
        // sizes mostly sample the same mip, and every quad stretches its sprite to its own
        // rectangle anyway. Keyed by size, a map of freely scaled mountains had a sprite per
        // object, never fit the atlas and fell back to one draw per stamp (R05, 2026-10-23).
        // Shelf packing: tallest sprites first, two transparent pixels around each so bilinear
        // sampling never reads a neighbour. The atlas doubles until everything fits.
        var sprites = sources.Values.DistinctBy(image => image.UniqueId)
            .OrderByDescending(image => image.Height).ThenBy(image => image.UniqueId).ToArray();
        var area = sprites.Sum(image => (long)(image.Width + 4) * (image.Height + 4));
        var side = 256;
        while ((long)side * side < area * 5 / 4 && side < DenseAtlasMaxSide) side *= 2;
        var slots = new Dictionary<uint, int>();
        var rects = new SKRect[sprites.Length];
        int atlasHeight;
        while (true)
        {
            var x = 2; var y = 2; var shelf = 0; var fits = true;
            for (var index = 0; index < sprites.Length; index++)
            {
                var image = sprites[index];
                if (x + image.Width + 2 > side) { x = 2; y += shelf + 4; shelf = 0; }
                if (y + image.Height + 2 > side) { fits = false; break; }
                rects[index] = new SKRect(x, y, x + image.Width, y + image.Height);
                x += image.Width + 4; shelf = Math.Max(shelf, image.Height);
            }
            if (fits) { atlasHeight = Math.Min(side, y + shelf + 2); break; }
            if (side >= DenseAtlasMaxSide) return false;
            side *= 2;
        }
        for (var index = 0; index < sprites.Length; index++) slots.Add(sprites[index].UniqueId, index);
        var hash = 14695981039346656037UL;
        foreach (var image in sprites)
            hash = unchecked((hash ^ image.UniqueId) * 1099511628211UL);
        var atlasCache = options.AtlasCache ?? cache;
        var atlas = atlasCache.GetOrCreate(layer, 0, 0, 20000 + bucket, hash, () =>
        {
            var info = new SKImageInfo(side, atlasHeight);
            using var surface = (options.GpuContext is { } gpu
                ? SKSurface.Create(gpu, false, info) : null) ?? SKSurface.Create(info)!;
            surface.Canvas.Clear(SKColors.Transparent);
            for (var index = 0; index < sprites.Length; index++)
                StampMipSampling.CopyClamped(surface.Canvas, sprites[index], rects[index]);
            return surface.Snapshot();
        }).Image;
        using var shader = atlas.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear));
        using var paint = new SKPaint { BlendMode = SKBlendMode.SrcOver, Shader = shader };
        // Keep the original order, including overlap and opacity. Only texture switches disappear.
        // With contact shadows every object has two quads, its shadow first: half as many per mesh.
        var perObject = anyShadow ? 2 : 1;
        var shadowTexture = anyShadow ? rects[slots[AreaBeautyRenderer.ShadowSprite.UniqueId]] : default;
        for (var start = 0; start < objects.Count;)
        {
            var count = Math.Min(8192 / perObject, objects.Count - start);
            var positions = new SKPoint[count * 4 * perObject]; var texture = new SKPoint[count * 4 * perObject];
            var colors = new SKColor[count * 4 * perObject]; var indices = new ushort[count * 6 * perObject];
            for (var index = 0; index < count; index++)
            {
                var item = objects[start + index];
                if (!item.Visible) continue;
                var image = sources[(item.Stamp!.AssetSha256, item.Width, item.Height)];
                var angle = item.RotationDegrees * Math.PI / 180;
                // Mips round each source dimension independently. Stretch that sampled image
                // to the exact saved rectangle instead of rejecting nearly every real PNG
                // whose reduced aspect ratio differs by a fraction of a source pixel.
                var cos = Math.Cos(angle); var sin = Math.Sin(angle);
                var dx = cos * item.Width; var dy = sin * item.Width;
                var hx = -sin * item.Height; var hy = cos * item.Height;
                var tx = item.X + item.Width / 2 - (dx + hx) / 2;
                var ty = item.Y + item.Height / 2 - (dy + hy) / 2;
                var quad = index * perObject;
                if (item.Stamp.GroundShadow is { IsValid: true, Opacity: > 0 } ground)
                {
                    // The same local rectangle as the single-stamp path, turned with the object.
                    var local = AreaBeautyRenderer.ShadowRect(new SKRect(0, 0, (float)item.Width, (float)item.Height), ground);
                    SKPoint Corner(float lx, float ly) => new((float)(tx + cos * lx - sin * ly), (float)(ty + sin * lx + cos * ly));
                    var shade = SKColors.White.WithAlpha((byte)Math.Clamp(Math.Round(ground.Opacity * Alpha(item.ObjectOpacity)), 0, 255));
                    Quad(quad, Corner(local.Left, local.Top), Corner(local.Right, local.Top), Corner(local.Right, local.Bottom),
                        Corner(local.Left, local.Bottom), shadowTexture, shade);
                }
                if (anyShadow) quad++;
                var rect = rects[slots[image.UniqueId]];
                Quad(quad, new((float)tx, (float)ty), new((float)(tx + dx), (float)(ty + dy)),
                    new((float)(tx + dx + hx), (float)(ty + dy + hy)), new((float)(tx + hx), (float)(ty + hy)), rect,
                    (item.Stamp.Tint is null ? SKColors.White : AreaBeautyRenderer.Tint(item.Stamp)).WithAlpha(Alpha(item.ObjectOpacity)));
                if (options.Stats is { } stats)
                { stats.ObjectsDrawn++; stats.ExactObjects++; stats.RecordExactObject(item); }
            }
            void Quad(int quad, SKPoint a, SKPoint b, SKPoint c, SKPoint d, SKRect source, SKColor color)
            {
                var vertex = quad * 4;
                positions[vertex] = a; positions[vertex + 1] = b; positions[vertex + 2] = c; positions[vertex + 3] = d;
                texture[vertex] = new(source.Left, source.Top); texture[vertex + 1] = new(source.Right, source.Top);
                texture[vertex + 2] = new(source.Right, source.Bottom); texture[vertex + 3] = new(source.Left, source.Bottom);
                for (var corner = 0; corner < 4; corner++) colors[vertex + corner] = color;
                indices[quad * 6] = (ushort)vertex; indices[quad * 6 + 1] = (ushort)(vertex + 1); indices[quad * 6 + 2] = (ushort)(vertex + 2);
                indices[quad * 6 + 3] = (ushort)vertex; indices[quad * 6 + 4] = (ushort)(vertex + 2); indices[quad * 6 + 5] = (ushort)(vertex + 3);
            }
            using var mesh = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, texture, colors, indices);
            canvas.DrawVertices(mesh, SKBlendMode.Modulate, paint);
            if (options.Stats is { } statsAfter) { statsAfter.DrawCalls++; statsAfter.StampAtlasBatches++; }
            start += count;
        }
        return true;
    }

    private sealed class StampBatchScratch : IDisposable
    {
        private readonly Dictionary<int, (SKRotationScaleMatrix[], SKRect[], SKColor[])> _small = [];
        public SKPaint Paint { get; } = new() { IsAntialias = true, BlendMode = SKBlendMode.SrcOver };
        public (SKRotationScaleMatrix[], SKRect[], SKColor[]) Get(int count)
        {
            if (_small.TryGetValue(count, out var buffers)) return buffers;
            buffers = (new SKRotationScaleMatrix[count], new SKRect[count], new SKColor[count]);
            // A frame with thousands of alternating sources mostly submits one/two stamps.
            // Reuse those exact-sized arrays; large runs remain bounded by the batch limit.
            if (count <= 32) _small.Add(count, buffers);
            return buffers;
        }
        public void Dispose() => Paint.Dispose();
    }

    private static bool TryDrawRasterStampEffect(SKCanvas canvas, SceneObject sceneObject,
        StampForegroundSurface? stampForeground, RenderOptions options)
    {
        // Skia's GPU blur uses a different kernel approximation. Prepare only this bounded
        // source-over stamp with the CPU evaluator, then composite its device pixels on the GPU.
        // The dense neutral stamp layer and the independent export evaluator stay accelerated
        // and unchanged respectively. No document or source image is modified.
        var matrix = canvas.TotalMatrix;
        var device = matrix.MapRect(ToRect(SceneGeometry.BoundsOf(sceneObject)));
        var clip = canvas.DeviceClipBounds;
        var left = (int)Math.Floor(Math.Max(device.Left - 2, clip.Left));
        var top = (int)Math.Floor(Math.Max(device.Top - 2, clip.Top));
        var right = (int)Math.Ceiling(Math.Min(device.Right + 2, clip.Right));
        var bottom = (int)Math.Ceiling(Math.Min(device.Bottom + 2, clip.Bottom));
        var width = right - left; var height = bottom - top;
        if (width <= 0 || height <= 0) return true;
        if ((long)width * height > 4L * 1024 * 1024) return false;
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        if (surface is null) return false;
        var target = surface.Canvas;
        target.Clear(SKColors.Transparent);
        target.Translate(-left, -top);
        target.Concat(matrix);
        DrawObject(target, sceneObject, stampForeground, new RenderOptions
        {
            Stamps = options.Stamps, Stats = options.Stats, ViewScale = options.ViewScale,
            VisibleWorld = options.VisibleWorld, LodTier = options.LodTier, IsExport = options.IsExport,
        }, prepareGpuEffect: false);
        using var image = surface.Snapshot();
        canvas.Save();
        canvas.ResetMatrix();
        canvas.DrawImage(image, left, top, new SKSamplingOptions(SKFilterMode.Nearest));
        canvas.Restore();
        return true;
    }

    private static void DrawGridLayer(
        SKCanvas canvas, GridLayer layer, WorldBounds view, RenderOptions options)
    {
        foreach (var grid in layer.Grids)
        {
            if (options.IsExport && !grid.IncludeInExport) continue;
            DrawGrid(canvas, grid, view);
        }
    }

    private static void DrawComposition(
        SKCanvas canvas, SceneObject owner, EditableComposition composition,
        byte objectAlpha, RenderOptions options)
    {
        var ownerRect = new SKRect((float)owner.X, (float)owner.Y,
            (float)(owner.X + owner.Width), (float)(owner.Y + owner.Height));
        using var ownerPaint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(objectAlpha),
            BlendMode = SKBlendMode.SrcOver,
        };
        canvas.SaveLayer(ownerRect, ownerPaint);
        canvas.ClipRect(ownerRect);
        canvas.Translate((float)owner.X, (float)owner.Y);
        canvas.Scale((float)(owner.Width / composition.SourceWidth),
            (float)(owner.Height / composition.SourceHeight));
        if (composition.IncludeBackground && composition.BackgroundColor is { } background)
        {
            using var paint = new SKPaint
            {
                Color = ParseColor(background).WithScaledAlpha(objectAlpha),
                Style = SKPaintStyle.Fill,
            };
            canvas.DrawRect(0, 0, (float)composition.SourceWidth, (float)composition.SourceHeight, paint);
        }
        foreach (var item in composition.Items.Where(item => item.Visible)
                     .OrderBy(item => item.Sublayer))
            DrawObject(canvas, item, stampForeground: null, options);
        if (composition.IncludeGrids && (!options.IsExport || options.IncludeExportGrids))
        {
            var localView = new WorldBounds(0, 0, composition.SourceWidth, composition.SourceHeight);
            foreach (var grid in composition.Grids)
            {
                if (!options.IsExport || grid.IncludeInExport)
                {
                    DrawGrid(canvas, grid, localView);
                }
            }
        }
        if (composition.IncludeFilters)
        {
            var localView = new WorldBounds(0, 0, composition.SourceWidth, composition.SourceHeight);
            foreach (var filter in composition.Filters) DrawFilterOverlay(canvas, filter, localView, options);
        }
        canvas.Restore();
    }

    private static void DrawPlaceholder(SKCanvas canvas, SKRect rect, byte objectAlpha)
    {
        using var fill = new SKPaint
        {
            Color = new SKColor(255, 0, 255, (byte)Math.Min(180, (int)objectAlpha)),
            Style = SKPaintStyle.Fill,
        };
        using var stroke = new SKPaint
        {
            Color = new SKColor(60, 0, 60, objectAlpha),
            StrokeWidth = 3,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
        };
        canvas.DrawRect(rect, fill); canvas.DrawRect(rect, stroke);
        canvas.DrawLine(rect.Left, rect.Top, rect.Right, rect.Bottom, stroke);
        canvas.DrawLine(rect.Right, rect.Top, rect.Left, rect.Bottom, stroke);
    }

    /// <summary>
    /// The gradient map and post-processing of a filter layer as one image filter over the scene
    /// below it, or null when they change nothing. Numbers come from <see cref="FilterColorEffects"/>
    /// so the canvas, the export and the tests share one formula. Sharpening is skipped while the
    /// view moves or is far out (it is a detail effect); idle frames and exports always apply it.
    /// </summary>
    private static SKImageFilter? FilterColorPass(FilterSettings settings, RenderOptions options)
    {
        if (!FilterColorEffects.IsActive(settings)) return null;
        SKColorFilter? color = null;
        var gradientWeight = FilterColorEffects.GradientWeight(settings);
        if (gradientWeight > 0)
        {
            var (red, green, blue) = FilterColorEffects.GradientLut(settings.GradientMap);
            var alpha = new byte[256];
            for (var i = 0; i < alpha.Length; i++) alpha[i] = (byte)i;
            float r = (float)FilterColorEffects.LumaR, g = (float)FilterColorEffects.LumaG, b = (float)FilterColorEffects.LumaB;
            using var luma = SKColorFilter.CreateColorMatrix(
            [
                r, g, b, 0, 0,
                r, g, b, 0, 0,
                r, g, b, 0, 0,
                0, 0, 0, 1, 0,
            ]);
            using var table = SKColorFilter.CreateTable(alpha, red, green, blue);
            var mapped = SKColorFilter.CreateCompose(table, luma);
            // CreateLerp at weight 1 hands back the first filter (checked in FilterColorRenderingTests),
            // so a full-strength gradient is used as is.
            if (gradientWeight >= 1) color = mapped;
            else
            {
                using var identity = SKColorFilter.CreateColorMatrix(
                [
                    1, 0, 0, 0, 0,
                    0, 1, 0, 0, 0,
                    0, 0, 1, 0, 0,
                    0, 0, 0, 1, 0,
                ]);
                color = SKColorFilter.CreateLerp((float)gradientWeight, identity, mapped);
                mapped.Dispose();
            }
        }
        if (FilterColorEffects.HasPost(settings))
        {
            using var post = SKColorFilter.CreateColorMatrix(
                FilterColorEffects.PostMatrix(settings.PostProcess, settings.ColorStrength));
            var combined = color is null ? null : SKColorFilter.CreateCompose(post, color);
            color?.Dispose();
            color = combined ?? SKColorFilter.CreateColorMatrix(
                FilterColorEffects.PostMatrix(settings.PostProcess, settings.ColorStrength));
        }
        SKImageFilter? pass = null;
        if (color is not null)
        {
            pass = SKImageFilter.CreateColorFilter(color);
            color.Dispose();
        }
        var sharpen = FilterColorEffects.SharpenWeight(settings);
        if (sharpen > 0 && (options.IsExport || options.LodTier is not (RenderLodTier.Interactive or RenderLodTier.Overview)))
        {
            var n = (float)-sharpen;
            var centre = (float)(1 + (8 * sharpen));
            var kernel = new[] { n, n, n, n, centre, n, n, n, n };
            var sharpened = SKImageFilter.CreateMatrixConvolution(
                new SKSizeI(3, 3), kernel, 1, 0, new SKPointI(1, 1), SKShaderTileMode.Clamp, false, pass);
            pass?.Dispose();
            pass = sharpened;
        }
        return pass;
    }

    private static void DrawFilterOverlay(
        SKCanvas canvas, FilterSettings filter, WorldBounds view, RenderOptions options)
    {
        var rect = ToRect(view);
        static byte Alpha(double value, double scale = 0.45) =>
            (byte)Math.Clamp(Math.Round(Math.Abs(value) * scale * 255), 0, 255);
        void Overlay(SKColor color, SKBlendMode blend)
        {
            if (color.Alpha == 0) return;
            using var paint = new SKPaint { Color = color, BlendMode = blend, Style = SKPaintStyle.Fill };
            canvas.DrawRect(rect, paint);
        }

        if (!options.IsExport
            && options.LodTier is RenderLodTier.Interactive or RenderLodTier.Overview)
        {
            // One bounded screen-space wash keeps the direction of the filter legible while the
            // viewport is moving/far away. Exact idle and every export retain the full sequence.
            var strength = Math.Clamp(
                Math.Abs(filter.Brightness) + Math.Abs(filter.Contrast)
                + Math.Abs(filter.Saturation) + Math.Abs(filter.HueDegrees / 180)
                + Math.Abs(filter.Temperature),
                0,
                1);
            var red = (byte)Math.Clamp(Math.Round(
                128 + (filter.Temperature * 100) + (filter.Saturation * 45)), 0, 255);
            var green = (byte)Math.Clamp(Math.Round(
                128 + (filter.Brightness * 80)), 0, 255);
            var blue = (byte)Math.Clamp(Math.Round(
                128 - (filter.Temperature * 100) - (filter.Saturation * 20)), 0, 255);
            Overlay(new SKColor(red, green, blue, Alpha(strength, 0.25)), SKBlendMode.SoftLight);
            return;
        }

        Overlay(filter.Brightness >= 0
            ? SKColors.White.WithAlpha(Alpha(filter.Brightness))
            : SKColors.Black.WithAlpha(Alpha(filter.Brightness)), SKBlendMode.SrcOver);
        Overlay(filter.Contrast >= 0
            ? SKColors.Gray.WithAlpha(Alpha(filter.Contrast, 0.35))
            : SKColors.White.WithAlpha(Alpha(filter.Contrast, 0.25)),
            filter.Contrast >= 0 ? SKBlendMode.Overlay : SKBlendMode.SoftLight);
        if (filter.Saturation != 0)
        {
            var color = filter.Saturation > 0 ? new SKColor(255, 90, 50) : SKColors.Gray;
            Overlay(color.WithAlpha(Alpha(filter.Saturation, 0.35)), SKBlendMode.Color);
        }
        if (filter.HueDegrees != 0)
        {
            var hueColor = SKColor.FromHsv((float)((filter.HueDegrees % 360 + 360) % 360), 80, 85);
            Overlay(hueColor.WithAlpha(Alpha(filter.HueDegrees / 180, 0.35)), SKBlendMode.Hue);
        }
        if (filter.Temperature != 0)
        {
            var temperature = filter.Temperature > 0 ? new SKColor(255, 126, 40) : new SKColor(45, 130, 255);
            Overlay(temperature.WithAlpha(Alpha(filter.Temperature, 0.3)), SKBlendMode.SoftLight);
        }

        if (filter.TextureAssetSha256 is not { } hash) return;
        var image = options.Stamps?.TryGetImage(hash);
        if (image is null) { if (options.Stats is { } stats) stats.MissingAssets++; return; }
        var scale = (float)Math.Max(0.05, filter.TextureScale);
        using var shader = image.ToShader(
            SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKMatrix.CreateScale(scale, scale));
        using var texturePaint = new SKPaint
        {
            Color = SKColors.White.WithAlpha((byte)Math.Clamp(Math.Round(filter.TextureOpacity * 255), 0, 255)),
            BlendMode = SKBlendMode.SoftLight,
            IsAntialias = false,
            Shader = shader,
        };
        canvas.DrawRect(rect, texturePaint);
    }

    private static void DrawGrid(SKCanvas canvas, GridDefinition grid, WorldBounds view)
    {
        using var paint = new SKPaint
        {
            Color = ParseColor(grid.Color).WithScaledAlpha((byte)Math.Round(grid.Opacity * 255)),
            StrokeWidth = (float)grid.LineWidth,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
        };
        foreach (var segment in GridGeometryContract.VisibleSegments(grid, view))
        {
            canvas.DrawLine(
                (float)segment.Start.X, (float)segment.Start.Y,
                (float)segment.End.X, (float)segment.End.Y, paint);
        }
    }

    private static void DrawText(
        SKCanvas canvas, SceneObject owner, TextStyle style, SKRect rect, byte objectAlpha,
        RenderOptions options)
    {
        var resolution = TextFontResolver.Resolve(style.RequestedFontFamily, style.FallbackFontFamily);
        var typeface = TextFontResolver.GetTypeface(resolution, style.FontWeight);
        using var font = new SKFont(typeface, (float)Math.Max(1, style.FontSize));
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = ParseColor(style.FillColor).WithScaledAlpha(objectAlpha),
            BlendMode = ParseBlend(style.Blend),
            Style = SKPaintStyle.Fill,
            ImageFilter = !options.FastPreview && style.Blur > 0
                ? SKImageFilter.CreateBlur((float)style.Blur, (float)style.Blur)
                : null,
        };
        if (style.Path is { Baseline.Length: >= 2 } along)
        {
            DrawTextAlongPath(canvas, style, along, rect, font, paint, objectAlpha, options);
            return;
        }

        var centreX = rect.MidX; var centreY = rect.MidY;
        canvas.Save();
        canvas.ClipRect(rect);
        canvas.Scale(style.FlipHorizontal ? -1 : 1, style.FlipVertical ? -1 : 1, centreX, centreY);
        var lines = style.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lineHeight = Math.Max(1, style.FontSize * style.LineHeight);
        var y = rect.Top + (float)style.FontSize;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++, y += (float)lineHeight)
        {
            var line = lines[lineIndex];
            var width = font.MeasureText(line) + (float)(Math.Max(0, line.Length - 1) * style.LetterSpacing);
            var x = style.Alignment switch
            {
                TextAlignment.Center => rect.MidX - width / 2,
                TextAlignment.Right => rect.Right - width,
                _ => rect.Left,
            };
            if (!options.FastPreview && style.ShadowOpacity > 0)
            {
                using var shadow = new SKPaint
                {
                    Color = SKColors.Black.WithAlpha((byte)Math.Clamp(Math.Round(style.ShadowOpacity * objectAlpha), 0, 255)),
                    ImageFilter = SKImageFilter.CreateBlur((float)style.ShadowBlur, (float)style.ShadowBlur),
                    IsAntialias = true,
                    BlendMode = ParseBlend(style.Blend),
                    Style = SKPaintStyle.Fill,
                };
                DrawTextRun(canvas, line, x + (float)style.ShadowOffsetX, y + (float)style.ShadowOffsetY,
                    lineIndex, style, font, shadow);
            }
            if (!options.FastPreview && style.OutlineWidth > 0)
            {
                using var outline = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = (float)style.OutlineWidth,
                    Color = ParseColor(style.OutlineColor).WithScaledAlpha(objectAlpha),
                    IsAntialias = true,
                    BlendMode = ParseBlend(style.Blend),
                };
                DrawTextRun(canvas, line, x, y, lineIndex, style, font, outline);
            }
            DrawTextRun(canvas, line, x, y, lineIndex, style, font, paint);
        }
        canvas.Restore();
    }

    /// <summary>
    /// A label bound to a river, road or hex path (R07): every glyph sits on the saved baseline,
    /// turned to its direction, the run centred on the baseline's middle; text longer than the
    /// baseline runs on straight along its end directions.
    /// </summary>
    private static void DrawTextAlongPath(
        SKCanvas canvas, TextStyle style, TextPathBinding along, SKRect rect, SKFont font, SKPaint paint,
        byte objectAlpha, RenderOptions options)
    {
        var line = TextOnPath.LineOf(style);
        if (line.Length == 0) return;
        var points = along.Baseline;
        var distances = new double[points.Length];
        for (var i = 1; i < points.Length; i++)
        {
            var dx = points[i].X - points[i - 1].X; var dy = points[i].Y - points[i - 1].Y;
            distances[i] = distances[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }

        var length = distances[^1];
        if (length <= 0) return;
        // Cartographic conventions (R07 «Красота», research of labels 2026-10-22): water names lean
        // in italic; a name on a long line opens its letters out along it, up to about half a letter.
        var water = along.Target == TextPathTarget.River;
        var skew = font.SkewX;
        if (water && Math.Abs(skew) < 1e-3) font.SkewX = -0.2f;
        var glyphs = new string[line.Length];
        var advances = new float[line.Length];
        var total = 0f;
        for (var i = 0; i < line.Length; i++)
        {
            glyphs[i] = line[i].ToString();
            advances[i] = font.MeasureText(glyphs[i]) + (i < line.Length - 1 ? (float)style.LetterSpacing : 0);
            total += advances[i];
        }
        if (line.Length > 1 && length > total * 1.6)
        {
            var spread = (float)Math.Min((length * 0.62 - total) / (line.Length - 1), style.FontSize * 0.55);
            if (spread > 0)
            {
                for (var i = 0; i < line.Length - 1; i++) advances[i] += spread;
                total += spread * (line.Length - 1);
            }
        }

        (float X, float Y, float Angle) At(double s)
        {
            int segment;
            if (s <= 0) segment = 1;
            else if (s >= length) segment = points.Length - 1;
            else
            {
                segment = Array.BinarySearch(distances, s);
                segment = segment >= 0 ? Math.Max(1, segment) : ~segment;
            }

            var a = points[segment - 1]; var b = points[segment];
            var span = Math.Max(1e-9, distances[segment] - distances[segment - 1]);
            var t = (s - distances[segment - 1]) / span;
            var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
            return ((float)(rect.Left + a.X + (b.X - a.X) * t), (float)(rect.Top + a.Y + (b.Y - a.Y) * t), (float)angle);
        }

        // Each glyph turns with the chord across its own width, and never more than 25° from its
        // neighbour (QGIS / MapLibre keep a maximum angle between letters): a sharp kink in the
        // line bends the name gently instead of breaking it.
        var placed = new (float X, float Y, float Angle)[glyphs.Length];
        {
            var cursor = length / 2 - total / 2;
            const double maxTurn = 25 * Math.PI / 180;
            for (var i = 0; i < glyphs.Length; i++)
            {
                var middle = cursor + advances[i] / 2;
                var (x, y, _) = At(middle);
                var (ax, ay, _) = At(middle - advances[i] * 0.6);
                var (bx, by, _) = At(middle + advances[i] * 0.6);
                var angle = Math.Atan2(by - ay, bx - ax);
                if (i > 0)
                {
                    var delta = Math.IEEERemainder(angle - placed[i - 1].Angle, 2 * Math.PI);
                    angle = placed[i - 1].Angle + Math.Clamp(delta, -maxTurn, maxTurn);
                }
                placed[i] = (x, y, (float)angle);
                cursor += advances[i];
            }
        }

        void Run(SKPaint runPaint, float offsetX, float offsetY)
        {
            for (var i = 0; i < glyphs.Length; i++)
            {
                var (x, y, angle) = placed[i];
                canvas.Save();
                canvas.Translate(x + offsetX, y + offsetY);
                canvas.RotateRadians(angle);
                canvas.DrawText(glyphs[i], -(advances[i] - spreadOf(i)) / 2, 0, SKTextAlign.Left, font, runPaint);
                canvas.Restore();
            }
        }
        float spreadOf(int i) => i < glyphs.Length - 1 ? advances[i] - font.MeasureText(glyphs[i]) - (float)style.LetterSpacing : 0;

        if (!options.FastPreview && style.ShadowOpacity > 0)
        {
            using var shadow = new SKPaint
            {
                Color = SKColors.Black.WithAlpha((byte)Math.Clamp(Math.Round(style.ShadowOpacity * objectAlpha), 0, 255)),
                ImageFilter = SKImageFilter.CreateBlur((float)style.ShadowBlur, (float)style.ShadowBlur),
                IsAntialias = true,
                BlendMode = ParseBlend(style.Blend),
                Style = SKPaintStyle.Fill,
            };
            Run(shadow, (float)style.ShadowOffsetX, (float)style.ShadowOffsetY);
        }

        if (!options.FastPreview && style.OutlineWidth > 0)
        {
            // A soft halo of the paper rather than a hard outline: the name lifts off the line
            // without a cut-out edge (Tolkien-style atlases, QGIS «buffer» with blur).
            using var outline = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)(style.OutlineWidth * 1.4),
                StrokeJoin = SKStrokeJoin.Round,
                Color = ParseColor(style.OutlineColor).WithScaledAlpha(objectAlpha),
                IsAntialias = true,
                BlendMode = ParseBlend(style.Blend),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)Math.Max(0.4, style.OutlineWidth * 0.45)),
            };
            Run(outline, 0, 0);
        }

        Run(paint, 0, 0);
        font.SkewX = skew;
    }

    private static void DrawTextRun(
        SKCanvas canvas, string text, float x, float y, int lineIndex,
        TextStyle style, SKFont font, SKPaint paint)
    {
        if (style.LetterSpacing == 0 && style.Curve == 0)
        {
            canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint); return;
        }
        var cursor = x;
        for (var index = 0; index < text.Length; index++)
        {
            var glyph = text[index].ToString();
            var curveY = (float)(style.Curve * Math.Sin((index + lineIndex * 0.5) * Math.PI / Math.Max(2, text.Length - 1)));
            canvas.DrawText(glyph, cursor, y - curveY, SKTextAlign.Left, font, paint);
            cursor += font.MeasureText(glyph) + (float)style.LetterSpacing;
        }
    }

    private static void DrawVectorShape(
        SKCanvas canvas, SceneObject owner, ShapeStyle shape, byte objectAlpha, RenderOptions options)
    {
        // The stroke's blend/blur act as the shape's whole-object blend and blur (docs/02 §8):
        // fill and stroke composite against the backdrop as one image, so a Multiply shape does
        // not multiply its own fill under its stroke twice.
        var overallBlend = ParseBlend(shape.Stroke.Blend);
        var wrapped = overallBlend != SKBlendMode.SrcOver
            || !options.FastPreview && shape.Stroke.Blur > 0;
        var strokeStyle = shape.Stroke;
        if (wrapped)
        {
            using var overallPaint = new SKPaint
            {
                BlendMode = overallBlend,
                ImageFilter = !options.FastPreview && shape.Stroke.Blur > 0
                    ? SKImageFilter.CreateBlur((float)shape.Stroke.Blur, (float)shape.Stroke.Blur)
                    : null,
            };
            canvas.SaveLayer(ToRect(SceneGeometry.BoundsOf(owner)), overallPaint);
            overallPaint.ImageFilter?.Dispose();
            strokeStyle = strokeStyle with { Blend = "Normal", Blur = 0 };
        }

        canvas.SaveLayer(ToRect(SceneGeometry.BoundsOf(owner)), null);
        var fillAlpha = (byte)Math.Clamp(Math.Round(objectAlpha * shape.Fill.Opacity), 0, 255);
        foreach (var region in shape.Regions)
        {
            using var path = BuildRegionPath(owner, region);

            // A subtract region punches a hole, so it is always flat black through DstOut - a
            // gradient there would describe the hole, not the fill, and nothing would read it.
            using var gradient = region.Mode == ShapeCombineMode.Subtract
                ? null
                : CreateGradientShader(
                    shape.Fill.Gradient, ToRect(SceneGeometry.BoundsOf(owner)), fillAlpha);
            using var fill = new SKPaint
            {
                Color = region.Mode == ShapeCombineMode.Subtract
                    ? SKColors.Black
                    : ParseColor(shape.Fill.Color).WithScaledAlpha(fillAlpha),
                Shader = gradient,
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                BlendMode = region.Mode == ShapeCombineMode.Subtract ? SKBlendMode.DstOut : SKBlendMode.SrcOver,
            };
            canvas.DrawPath(path, fill);

            if (region.Mode == ShapeCombineMode.Add
                && shape.Fill.TextureAssetSha256 is { } hash
                && options.Stamps?.TryGetImage(hash) is { } texture)
            {
                canvas.Save();
                canvas.ClipPath(path, SKClipOperation.Intersect, antialias: true);
                canvas.Translate(
                    (float)(owner.X + owner.Width / 2 + shape.Fill.TextureOffsetX),
                    (float)(owner.Y + owner.Height / 2 + shape.Fill.TextureOffsetY));
                canvas.RotateDegrees((float)shape.Fill.TextureRotation);
                canvas.Scale((float)Math.Max(0.01, shape.Fill.TextureScale));
                canvas.Translate((float)-(owner.X + owner.Width / 2), (float)-(owner.Y + owner.Height / 2));
                using var texturePaint = new SKPaint
                {
                    Color = SKColors.White.WithAlpha((byte)Math.Clamp(
                        Math.Round(objectAlpha * shape.Fill.Opacity), 0, 255)),
                    IsAntialias = true,
                };
                canvas.DrawImage(texture, new SKRect(
                    (float)owner.X, (float)owner.Y,
                    (float)(owner.X + owner.Width), (float)(owner.Y + owner.Height)),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), texturePaint);
                canvas.Restore();
            }
        }
        canvas.Restore();

        foreach (var region in shape.Regions)
        {
            var points = RegionPoints(owner, region);
            DrawVectorStroke(canvas, owner, points, closed: true, shape.Portals, strokeStyle, objectAlpha, options);
        }

        // «Комната»: the third layer - a cap on every corner, over floor and wall.
        if (owner.Room is { CapAssetSha256: { } capHash } room)
        {
            DrawRoomCaps(canvas, owner, shape, room, capHash, strokeStyle, objectAlpha, options);
        }

        if (wrapped)
        {
            canvas.Restore();
        }
    }

    /// <summary>
    /// The caps of a room: the cap picture centred on each (rounded) corner, turned along the wall
    /// plus the room's cap angle, as wide as the wall times the cap scale, with the wall's shadow.
    /// </summary>
    private static void DrawRoomCaps(
        SKCanvas canvas, SceneObject owner, ShapeStyle shape, RoomStyle room, string capHash,
        VectorStrokeStyle wall, byte objectAlpha, RenderOptions options)
    {
        var image = options.Stamps?.TryGetImage(capHash);
        if (image is null)
        {
            if (options.Stats is { } stats) stats.MissingAssets++;
            return;
        }
        var width = (float)Math.Max(1, wall.Width * room.CapScale);
        var height = width * image.Height / Math.Max(1f, image.Width);
        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha((byte)Math.Clamp(Math.Round(objectAlpha * wall.Opacity), 0, 255)),
            IsAntialias = true,
            ImageFilter = room.CapShadow && !options.FastPreview
                ? SKImageFilter.CreateDropShadow(
                    (float)wall.ShadowOffsetX, (float)wall.ShadowOffsetY,
                    (float)Math.Max(0.5, wall.ShadowBlur / 2), (float)Math.Max(0.5, wall.ShadowBlur / 2),
                    SKColors.Black.WithAlpha((byte)Math.Round(255 * (wall.ShadowOpacity > 0 ? wall.ShadowOpacity : 0.35))))
                : null,
        };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        foreach (var region in shape.Regions)
        {
            foreach (var cap in RoomGeometry.Caps(owner, region))
            {
                canvas.Save();
                canvas.Translate((float)cap.Centre.X, (float)cap.Centre.Y);
                canvas.RotateDegrees((float)cap.AngleDegrees);
                canvas.DrawImage(image, new SKRect(-width / 2, -height / 2, width / 2, height / 2), sampling, paint);
                canvas.Restore();
            }
        }
        paint.ImageFilter?.Dispose();
    }

    private static SKPath BuildRegionPath(SceneObject owner, ShapeRegion region)
    {
        var path = new SKPath();
        if (region.Primitive == ShapePrimitiveKind.Ellipse)
        {
            var a = region.Nodes[0]; var b = region.Nodes[1];
            path.AddOval(new SKRect(
                (float)(owner.X + Math.Min(a.X, b.X) * owner.Width),
                (float)(owner.Y + Math.Min(a.Y, b.Y) * owner.Height),
                (float)(owner.X + Math.Max(a.X, b.X) * owner.Width),
                (float)(owner.Y + Math.Max(a.Y, b.Y) * owner.Height)));
            return path;
        }

        // Same outline as the stroke: the flattened nodes, or a room's rounded corners.
        var points = VectorGeometry.RegionOutline(owner, region);
        path.MoveTo((float)points[0].X, (float)points[0].Y);
        foreach (var point in points.Skip(1)) path.LineTo((float)point.X, (float)point.Y);
        path.Close();
        return path;
    }

    private static IReadOnlyList<VectorPoint> RegionPoints(SceneObject owner, ShapeRegion region) =>
        VectorGeometry.RegionOutline(owner, region);

    private static void DrawVectorStroke(
        SKCanvas canvas, SceneObject owner, IReadOnlyList<VectorPoint> points, bool closed,
        IReadOnlyList<LinePortal> portals, VectorStrokeStyle style, byte objectAlpha, RenderOptions options)
    {
        if (style.Width <= 0 || points.Count < 2)
        {
            return;
        }

        var pieces = VisibleSegments(points, portals);
        if (pieces.Count == 0) return;

        if (style.AssetSha256 is { } hash && options.Stamps?.TryGetImage(hash) is { } image)
        {
            DrawAssetStroke(canvas, [.. pieces.Select(piece => Offset(piece.A, piece.B, style.Offset))],
                image, style, objectAlpha, options);
        }
        else
        {
            using var paint = new SKPaint
            {
                Color = ParseColor(style.Color).WithScaledAlpha((byte)Math.Clamp(
                    Math.Round(objectAlpha * style.Opacity), 0, 255)),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)style.Width,
                StrokeCap = ToStrokeCap(style.EndCap),
                StrokeJoin = SKStrokeJoin.Round,
                BlendMode = ParseBlend(style.Blend),
                ImageFilter = options.FastPreview ? null : BuildStrokeFilter(style),
                PathEffect = BuildStrokeDash(style),
            };

            using var path = new SKPath();
            foreach (var piece in pieces)
            {
                var (a, b) = Offset(piece.A, piece.B, style.Offset);
                path.MoveTo((float)a.X, (float)a.Y);
                path.LineTo((float)b.X, (float)b.Y);
            }
            canvas.DrawPath(path, paint);
            paint.ImageFilter?.Dispose();
            paint.PathEffect?.Dispose();
        }

        if (style.NodeCap != VectorCap.Butt)
        {
            using var cap = new SKPaint
            {
                Color = ParseColor(style.Color).WithScaledAlpha((byte)Math.Clamp(
                    Math.Round(objectAlpha * style.Opacity), 0, 255)),
                IsAntialias = true,
            };
            foreach (var point in points.Take(closed ? points.Count - 1 : points.Count))
            {
                if (style.NodeCap == VectorCap.Round)
                    canvas.DrawCircle((float)point.X, (float)point.Y, (float)(style.Width / 2), cap);
                else
                    canvas.DrawRect((float)(point.X - style.Width / 2), (float)(point.Y - style.Width / 2),
                        (float)style.Width, (float)style.Width, cap);
            }
        }
    }

    private static List<(VectorPoint A, VectorPoint B)> VisibleSegments(
        IReadOnlyList<VectorPoint> points, IReadOnlyList<LinePortal> portals)
    {
        var lengths = points.Zip(points.Skip(1)).Select(pair => Distance(pair.First, pair.Second)).ToList();
        var total = lengths.Sum();
        if (total <= 0) return [];
        var gaps = portals.Select(portal => (
            Start: Math.Max(0, portal.Position * total - portal.Width / 2),
            End: Math.Min(total, portal.Position * total + portal.Width / 2))).OrderBy(g => g.Start).ToList();
        var result = new List<(VectorPoint A, VectorPoint B)>();
        var cursor = 0.0;
        for (var index = 0; index < lengths.Count; index++)
        {
            var length = lengths[index];
            if (length <= 0) continue;
            var visible = new List<(double Start, double End)> { (cursor, cursor + length) };
            foreach (var gap in gaps)
            {
                visible = [.. visible.SelectMany(part => Subtract(part, gap))];
            }
            foreach (var part in visible)
            {
                result.Add((
                    Lerp(points[index], points[index + 1], (part.Start - cursor) / length),
                    Lerp(points[index], points[index + 1], (part.End - cursor) / length)));
            }
            cursor += length;
        }
        return result;
    }

    private static IEnumerable<(double Start, double End)> Subtract(
        (double Start, double End) part, (double Start, double End) gap)
    {
        if (gap.End <= part.Start || gap.Start >= part.End) { yield return part; yield break; }
        if (gap.Start > part.Start) yield return (part.Start, Math.Min(gap.Start, part.End));
        if (gap.End < part.End) yield return (Math.Max(gap.End, part.Start), part.End);
    }

    private static void DrawAssetStroke(
        SKCanvas canvas, IReadOnlyList<(VectorPoint A, VectorPoint B)> pieces,
        SKImage image, VectorStrokeStyle style, byte objectAlpha, RenderOptions options)
    {
        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha((byte)Math.Clamp(Math.Round(objectAlpha * style.Opacity), 0, 255)),
            IsAntialias = true,
            BlendMode = ParseBlend(style.Blend),
            ColorFilter = BuildColorFilter(new StampStyle(style.AssetSha256!)
            {
                Hue = style.Hue,
                Saturation = style.Saturation,
            }),
            ImageFilter = options.FastPreview ? null : BuildStrokeFilter(style),
        };
        var spacing = Math.Max(1, style.Width * Math.Max(0.1, style.AssetSpacing));
        var drawn = 0;
        foreach (var piece in pieces)
        {
            var length = Distance(piece.A, piece.B);
            var count = Math.Max(1, (int)Math.Floor(length / spacing));
            for (var index = 0; index <= count && drawn < 4096; index++, drawn++)
            {
                var point = Lerp(piece.A, piece.B, index / (double)Math.Max(1, count));
                var angle = Math.Atan2(piece.B.Y - piece.A.Y, piece.B.X - piece.A.X) * 180 / Math.PI;
                canvas.Save();
                canvas.RotateDegrees((float)angle, (float)point.X, (float)point.Y);
                canvas.DrawImage(image, new SKRect(
                    (float)(point.X - style.Width / 2), (float)(point.Y - style.Width / 2),
                    (float)(point.X + style.Width / 2), (float)(point.Y + style.Width / 2)),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
                canvas.Restore();
            }
        }
        paint.ImageFilter?.Dispose();
        paint.ColorFilter?.Dispose();
    }

    /// <summary>Blur first, drop shadow around it - the same chain the stamp path uses.</summary>
    private static SKImageFilter? BuildStrokeFilter(VectorStrokeStyle style)
    {
        SKImageFilter? filter = null;
        if (style.Blur > 0)
        {
            filter = SKImageFilter.CreateBlur((float)style.Blur, (float)style.Blur);
        }

        if (style.ShadowOpacity > 0)
        {
            filter = SKImageFilter.CreateDropShadow(
                (float)style.ShadowOffsetX, (float)style.ShadowOffsetY,
                (float)style.ShadowBlur, (float)style.ShadowBlur,
                SKColors.Black.WithAlpha(Alpha(style.ShadowOpacity)), filter);
        }

        return filter;
    }

    /// <summary>Dash intervals scale with the stroke width so the rhythm survives any zoom.</summary>
    private static SKPathEffect? BuildStrokeDash(VectorStrokeStyle style)
    {
        var width = (float)Math.Max(1, style.Width);
        return style.Pattern switch
        {
            StrokePattern.Dotted => SKPathEffect.CreateDash([width * 0.5f, width * 1.5f], 0),
            StrokePattern.Dashed => SKPathEffect.CreateDash([width * 3f, width * 2f], 0),
            _ => null,
        };
    }

    private static (VectorPoint A, VectorPoint B) Offset(VectorPoint a, VectorPoint b, double amount)
    {
        var length = Distance(a, b);
        if (length <= 0 || amount == 0) return (a, b);
        var dx = -(b.Y - a.Y) / length * amount;
        var dy = (b.X - a.X) / length * amount;
        return (new VectorPoint(a.X + dx, a.Y + dy), new VectorPoint(b.X + dx, b.Y + dy));
    }

    private static SKStrokeCap ToStrokeCap(VectorCap cap) => cap switch
    {
        VectorCap.Round => SKStrokeCap.Round,
        VectorCap.Square => SKStrokeCap.Square,
        _ => SKStrokeCap.Butt,
    };

    private static double Distance(VectorPoint a, VectorPoint b) =>
        Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
    private static VectorPoint Lerp(VectorPoint a, VectorPoint b, double t) =>
        new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));

    private static void DrawStamp(
        SKCanvas canvas, SceneObject sceneObject, StampStyle stamp, SKRect rect,
        byte objectAlpha, StampForegroundSurface? stampForeground, RenderOptions options)
    {
        var image = options.Stamps?.TryGetImage(stamp.AssetSha256);
        if (image is null)
        {
            // A linked asset that is not on this machine must be visible, not silently absent.
            if (options.Stats is { } stats)
            {
                stats.MissingAssets++;
            }

            using var placeholderFill = new SKPaint
            {
                Color = new SKColor(255, 90, 90, (byte)(objectAlpha / 4)),
                IsAntialias = true,
            };
            using var placeholderDash = SKPathEffect.CreateDash([8, 6], 0);
            using var placeholderEdge = new SKPaint
            {
                Color = new SKColor(255, 90, 90, objectAlpha),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1f, rect.Width / 64),
                PathEffect = placeholderDash,
            };

            canvas.DrawRect(rect, placeholderFill);
            canvas.DrawRect(rect, placeholderEdge);
            canvas.DrawLine(rect.Left, rect.Top, rect.Right, rect.Bottom, placeholderEdge);
            canvas.DrawLine(rect.Right, rect.Top, rect.Left, rect.Bottom, placeholderEdge);
            return;
        }

        var mask = ResolveClipMask(stamp, stampForeground, options, countMissing: true);
        if (stamp.ShadowOpacity > 0 && stamp.ShadowPlacement == StampShadowPlacement.Object)
        {
            if (mask is null)
            {
                DrawStampShadow(canvas, sceneObject, stamp, rect, objectAlpha, image);
            }
            else
            {
                DrawClippedStampShadow(
                    canvas, sceneObject, stamp, rect, objectAlpha, image, mask, stampForeground!, options);
            }
        }

        // R10 «Тень под объектом»: the contact shadow lies under the stamp, inside its rectangle.
        if (stamp.GroundShadow is { } ground) AreaBeautyRenderer.DrawGroundShadow(canvas, rect, ground, objectAlpha);

        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(objectAlpha),
            IsAntialias = true,
            BlendMode = ParseBlend(stamp.Blend),
            ColorFilter = AreaBeautyRenderer.Compose(BuildColorFilter(stamp), stamp),
        };

        SKImageFilter? filter = null;
        if (stamp.Blur > 0)
        {
            filter = SKImageFilter.CreateBlur((float)stamp.Blur, (float)stamp.Blur);
        }

        paint.ImageFilter = filter;

        if (mask is null)
        {
            // Clip Mask off (and unresolved legacy/missing mask) keeps the exact pre-Stage-15 path.
            var matrix = canvas.TotalMatrix;
            var viewScale = Math.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY);
            image = options.Stamps?.TryGetImageForPixelSize(stamp.AssetSha256,
                DetailPixels(rect.Width, viewScale, options), DetailPixels(rect.Height, viewScale, options)) ?? image;
            DrawStampImage(canvas, image, rect, stamp, paint, options);
        }
        else
        {
            DrawClippedStampComposition(
                canvas, sceneObject, stamp, rect, image, mask, stampForeground!, options, paint);
        }

        filter?.Dispose();
        paint.ColorFilter?.Dispose();
    }

    private static SKImage? ResolveClipMask(
        StampStyle stamp,
        StampForegroundSurface? foreground,
        RenderOptions options,
        bool countMissing)
    {
        if (!stamp.ClipMaskEnabled || stamp.MaskAssetSha256 is not { } maskSha256)
        {
            return null;
        }

        var hasForeground = foreground is { IsEmpty: false }
            || options.Preview is { IsStampForeground: true, Coverage.Count: > 0 };
        if (!hasForeground)
        {
            return null;
        }

        var mask = options.Stamps?.TryGetImage(maskSha256);
        if (mask is null && countMissing && options.Stats is { } stats)
        {
            stats.MissingAssets++;
        }
        return mask;
    }

    private static void DrawClippedStampShadow(
        SKCanvas canvas,
        SceneObject sceneObject,
        StampStyle stamp,
        SKRect rect,
        byte objectAlpha,
        SKImage visual,
        SKImage mask,
        StampForegroundSurface foreground,
        RenderOptions options)
    {
        var color = ParseColor(stamp.ShadowColor);
        var combinedAlpha = (byte)Math.Clamp(Math.Round(
            color.Alpha * stamp.ShadowOpacity * (objectAlpha / 255.0)), 0, 255);
        if (combinedAlpha == 0) return;

        using var filter = SKImageFilter.CreateDropShadowOnly(
            (float)stamp.ShadowOffsetX, (float)stamp.ShadowOffsetY,
            (float)stamp.ShadowBlur, (float)stamp.ShadowBlur,
            color.WithAlpha(combinedAlpha));
        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            BlendMode = ParseBlend(stamp.Blend),
            ImageFilter = filter,
        };
        DrawClippedStampComposition(
            canvas, sceneObject, stamp, rect, visual, mask, foreground, options, paint);
    }

    private static void DrawClippedStampComposition(
        SKCanvas canvas,
        SceneObject sceneObject,
        StampStyle stamp,
        SKRect rect,
        SKImage visual,
        SKImage mask,
        StampForegroundSurface foreground,
        RenderOptions options,
        SKPaint finalPaint)
    {
        // The bounded outer layer is the composed stamp. Object opacity, colour adjustment, blur
        // and blend are applied exactly once when this layer is restored.
        var reach = (float)Math.Max(
            stamp.Blur * 3,
            Math.Max(Math.Abs(stamp.ShadowOffsetX), Math.Abs(stamp.ShadowOffsetY))
                + (stamp.ShadowBlur * 3));
        var bounds = rect;
        bounds.Inflate(reach, reach);
        canvas.SaveLayer(bounds, finalPaint);

        using (var basePaint = new SKPaint { Color = SKColors.White, IsAntialias = true })
        {
            DrawStampImage(canvas, visual, rect, stamp, basePaint, options);
        }

        // Overlay = Foreground(map coordinates) × Mask(stamp coordinates) × original visual alpha.
        canvas.SaveLayer(rect, null);
        canvas.Save();
        if (sceneObject.RotationDegrees != 0)
        {
            canvas.RotateDegrees(
                (float)-sceneObject.RotationDegrees, rect.MidX, rect.MidY);
        }
        var boundsInMap = SceneGeometry.BoundsOf(sceneObject);
        DrawTiles(
            canvas,
            StampForegroundLayerId,
            foreground.Tiles,
            boundsInMap,
            options,
            TileImageKind.Content);
        if (options.Preview is { IsStampForeground: true } foregroundPreview)
        {
            DrawStrokePreview(canvas, foregroundPreview, boundsInMap, options);
        }
        canvas.Restore();

        using (var maskFilter = SKColorFilter.CreateColorMatrix(
        [
            1, 0, 0, 0, 0,
            0, 1, 0, 0, 0,
            0, 0, 1, 0, 0,
            0.2126f, 0.7152f, 0.0722f, 0, 0,
        ]))
        using (var maskPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            BlendMode = SKBlendMode.DstIn,
            ColorFilter = maskFilter,
        })
        {
            DrawStampImage(canvas, mask, rect, stamp, maskPaint, options);
        }
        using (var alphaPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            BlendMode = SKBlendMode.DstIn,
        })
        {
            DrawStampImage(canvas, visual, rect, stamp, alphaPaint, options);
        }
        canvas.Restore();
        canvas.Restore();
    }

    private static void DrawStampImage(
        SKCanvas canvas, SKImage image, SKRect rect, StampStyle stamp, SKPaint paint, RenderOptions options)
    {
        canvas.Save();
        if (stamp.FlipHorizontal || stamp.FlipVertical)
        {
            canvas.Translate(rect.MidX, rect.MidY);
            canvas.Scale(stamp.FlipHorizontal ? -1 : 1, stamp.FlipVertical ? -1 : 1);
            canvas.Translate(-rect.MidX, -rect.MidY);
        }
        // The cached mip level of the stamp, filtered by the GPU: the same sampling in the live
        // frame, in an appended tile and in a rebuilt one.
        canvas.DrawImage(
            image,
            rect,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
            paint);
        canvas.Restore();
    }

    private static void DrawLayerStampShadow(
        SKCanvas canvas,
        SceneObject sceneObject,
        StampForegroundSurface? stampForeground,
        RenderOptions options)
    {
        if (!sceneObject.Visible || sceneObject.ObjectOpacity <= 0
            || sceneObject.Stamp is not { ShadowPlacement: StampShadowPlacement.Layer, ShadowOpacity: > 0 } stamp
            || options.Stamps?.TryGetImage(stamp.AssetSha256) is not { } image)
        {
            return;
        }

        canvas.Save();
        ApplyRotation(canvas, sceneObject);
        var rect = new SKRect(
            (float)sceneObject.X, (float)sceneObject.Y,
            (float)(sceneObject.X + sceneObject.Width), (float)(sceneObject.Y + sceneObject.Height));
        var objectAlpha = Alpha(sceneObject.ObjectOpacity);
        var mask = ResolveClipMask(stamp, stampForeground, options, countMissing: false);
        if (mask is null)
        {
            DrawStampShadow(canvas, sceneObject, stamp, rect, objectAlpha, image);
        }
        else
        {
            DrawClippedStampShadow(
                canvas, sceneObject, stamp, rect, objectAlpha, image, mask, stampForeground!, options);
        }
        canvas.Restore();
    }

    private static void DrawStampShadow(
        SKCanvas canvas, SceneObject sceneObject, StampStyle stamp, SKRect rect,
        byte objectAlpha, SKImage image)
    {
        var color = ParseColor(stamp.ShadowColor);
        var combinedAlpha = (byte)Math.Clamp(Math.Round(
            color.Alpha * stamp.ShadowOpacity * (objectAlpha / 255.0)), 0, 255);
        if (combinedAlpha == 0) return;

        using var filter = SKImageFilter.CreateDropShadowOnly(
            (float)stamp.ShadowOffsetX, (float)stamp.ShadowOffsetY,
            (float)stamp.ShadowBlur, (float)stamp.ShadowBlur,
            color.WithAlpha(combinedAlpha));
        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            BlendMode = ParseBlend(stamp.Blend),
            ImageFilter = filter,
        };

        canvas.Save();
        if (stamp.FlipHorizontal || stamp.FlipVertical)
        {
            canvas.Translate(rect.MidX, rect.MidY);
            canvas.Scale(stamp.FlipHorizontal ? -1 : 1, stamp.FlipVertical ? -1 : 1);
            canvas.Translate(-rect.MidX, -rect.MidY);
        }
        canvas.DrawImage(image, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        canvas.Restore();
    }

    /// <summary>Hue / saturation / brightness / contrast as one colour matrix, or null when neutral.</summary>
    private static SKColorFilter? BuildColorFilter(StampStyle stamp)
    {
        if (stamp is { Hue: 0, Saturation: 0, Brightness: 0, Contrast: 0 })
        {
            return null;
        }

        var matrix = Identity();

        if (stamp.Hue != 0)
        {
            var radians = stamp.Hue * Math.PI / 180.0;
            var cos = (float)Math.Cos(radians);
            var sin = (float)Math.Sin(radians);
            matrix = Multiply(
            [
                0.213f + (cos * 0.787f) - (sin * 0.213f), 0.715f - (cos * 0.715f) - (sin * 0.715f),
                0.072f - (cos * 0.072f) + (sin * 0.928f), 0, 0,
                0.213f - (cos * 0.213f) + (sin * 0.143f), 0.715f + (cos * 0.285f) + (sin * 0.140f),
                0.072f - (cos * 0.072f) - (sin * 0.283f), 0, 0,
                0.213f - (cos * 0.213f) - (sin * 0.787f), 0.715f - (cos * 0.715f) + (sin * 0.715f),
                0.072f + (cos * 0.928f) + (sin * 0.072f), 0, 0,
                0, 0, 0, 1, 0,
            ], matrix);
        }

        if (stamp.Saturation != 0)
        {
            var s = (float)(1 + Math.Clamp(stamp.Saturation, -1, 1));
            var inv = 1 - s;
            var r = 0.213f * inv;
            var g = 0.715f * inv;
            var b = 0.072f * inv;
            matrix = Multiply(
            [
                r + s, g, b, 0, 0,
                r, g + s, b, 0, 0,
                r, g, b + s, 0, 0,
                0, 0, 0, 1, 0,
            ], matrix);
        }

        if (stamp.Contrast != 0)
        {
            var c = (float)(1 + Math.Clamp(stamp.Contrast, -1, 1));
            var t = 127.5f * (1 - c);
            matrix = Multiply([c, 0, 0, 0, t, 0, c, 0, 0, t, 0, 0, c, 0, t, 0, 0, 0, 1, 0], matrix);
        }

        if (stamp.Brightness != 0)
        {
            var brightness = (float)Math.Clamp(stamp.Brightness, -1, 1);
            // The UI expresses brightness as 0..200 % with 100 % neutral, so use the matching
            // 0..2 channel multiplier. The previous translation was additionally multiplied by
            // 255 even though Skia uses normalized colour space, clipping one slider step to
            // solid black or white.
            var scale = 1 + brightness;
            matrix = Multiply(
                [scale, 0, 0, 0, 0,
                 0, scale, 0, 0, 0,
                 0, 0, scale, 0, 0,
                 0, 0, 0, 1, 0],
                matrix);
        }

        return SKColorFilter.CreateColorMatrix(matrix);
    }

    private static float[] Identity() =>
        [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0];

    private static float[] Multiply(float[] a, float[] b)
    {
        var result = new float[20];
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 5; col++)
            {
                var sum = 0f;
                for (var k = 0; k < 4; k++)
                {
                    sum += a[(row * 5) + k] * b[(k * 5) + col];
                }

                if (col == 4)
                {
                    sum += a[(row * 5) + 4];
                }

                result[(row * 5) + col] = sum;
            }
        }

        return result;
    }

    private static SKBlendMode ParseBlend(string blend) => blend switch
    {
        "Normal" => SKBlendMode.SrcOver,
        "Multiply" => SKBlendMode.Multiply,
        "Screen" => SKBlendMode.Screen,
        "Overlay" => SKBlendMode.Overlay,
        "Darken" => SKBlendMode.Darken,
        "Lighten" => SKBlendMode.Lighten,
        "SoftLight" => SKBlendMode.SoftLight,
        "HardLight" => SKBlendMode.HardLight,
        "Difference" => SKBlendMode.Difference,
        "Add" => SKBlendMode.Plus,
        "ColorBurn" => SKBlendMode.ColorBurn,
        "ColorDodge" => SKBlendMode.ColorDodge,
        "Exclusion" => SKBlendMode.Exclusion,
        "Hue" => SKBlendMode.Hue,
        "Saturation" => SKBlendMode.Saturation,
        "Color" => SKBlendMode.Color,
        "Luminosity" => SKBlendMode.Luminosity,
        _ => throw new InvalidOperationException($"Unsupported persisted blend mode: {blend}."),
    };

    /// <summary>Compatibility view over the domain's sole persisted-key contract.</summary>
    public static IReadOnlyList<string> BlendModes => Map.Core.Documents.BlendModes.Supported;

    private static void DrawSelectionChrome(SKCanvas canvas, MapDocument document, RenderOptions options)
    {
        var viewScale = Math.Max(options.ViewScale, 0.01);
        var frameOwnsHandles = options.SelectionFrame is not null;

        if (options.CoSelectedObjectIds is { Count: > 0 } coSelected)
        {
            foreach (var id in coSelected)
            {
                // With a shared frame the primary is just another member and gets a ring too.
                if ((frameOwnsHandles || id != options.SelectedObjectId)
                    && FindRenderObject(document, options, id) is { } member)
                {
                    DrawRing(canvas, member, viewScale);
                }
            }
        }

        if (options.SelectionFrame is { } frame)
        {
            DrawSelectionFrame(canvas, frame, viewScale);
        }
        else if (options.SelectedObjectId is { } selectedId)
        {
            var selected = FindRenderObject(document, options, selectedId);

            if (selected is not null)
            {
                DrawSelection(canvas, selected, viewScale);
            }
        }

        if (options.Marquee is { } marquee)
        {
            DrawMarquee(canvas, marquee, viewScale);
        }

        if (options.BrushCursor is { } brush)
        {
            DrawBrushCursor(canvas, brush, viewScale);
        }
    }

    /// <summary>
    /// The footprint the next dab would leave, under the pointer: a thin light ring with a dark
    /// shadow line, readable over sea and land alike. A rough land brush shows its actual cells,
    /// so the jagged shore is seen before it is drawn; the plain brush shows its circle.
    /// </summary>
    private static void DrawBrushCursor(SKCanvas canvas, BrushCursorPreview brush, double viewScale)
    {
        if (brush.Radius <= 0 || viewScale <= 0) return;
        var px = (float)(1.0 / viewScale);
        var light = brush.Subtract ? new SKColor(255, 170, 150, 230) : new SKColor(255, 255, 255, 220);
        var dark = new SKColor(0, 0, 0, 110);
        if (brush.RoughCellSize <= 0)
        {
            using var shadow = new SKPaint { Color = dark, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 * px };
            using var ring = new SKPaint { Color = light, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f * px };
            canvas.DrawCircle((float)brush.WorldX, (float)brush.WorldY, (float)brush.Radius, shadow);
            canvas.DrawCircle((float)brush.WorldX, (float)brush.WorldY, (float)brush.Radius, ring);
            return;
        }

        // The cells, sampled into a small mask at about screen resolution (at most 192 px
        // across), then drawn as a faint fill with an outline of one dilated minus one eroded
        // copy. The same cell field decides the dab, so the outline is the coast to come.
        var reach = brush.Radius + (brush.RoughCellSize * RoughCells.Reach);
        var worldSize = reach * 2;
        var pixels = (int)Math.Clamp(Math.Ceiling(worldSize * viewScale), 24, 192);
        var step = worldSize / pixels;
        var left = brush.WorldX - reach;
        var top = brush.WorldY - reach;
        var mask = new byte[pixels * pixels * 4];
        var radiusSquared = brush.Radius * brush.Radius;
        for (var y = 0; y < pixels; y++)
        {
            var worldY = top + ((y + 0.5) * step);
            for (var x = 0; x < pixels; x++)
            {
                var worldX = left + ((x + 0.5) * step);
                var (siteX, siteY) = RoughCells.NearestSite(worldX, worldY, brush.RoughCellSize);
                var dx = siteX - brush.WorldX; var dy = siteY - brush.WorldY;
                if ((dx * dx) + (dy * dy) > radiusSquared) continue;
                var offset = ((y * pixels) + x) * 4;
                mask[offset] = mask[offset + 1] = mask[offset + 2] = mask[offset + 3] = 255;
            }
        }

        var info = new SKImageInfo(pixels, pixels, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var data = SKData.CreateCopy(mask);
        using var image = SKImage.FromPixels(info, data, info.RowBytes);
        if (image is null) return;
        var rect = new SKRect((float)left, (float)top, (float)(left + worldSize), (float)(top + worldSize));
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        using (var tint = SKColorFilter.CreateBlendMode(light.WithAlpha(40), SKBlendMode.SrcIn))
        using (var fill = new SKPaint { ColorFilter = tint })
        {
            canvas.DrawImage(image, rect, sampling, fill);
        }

        // Outline in mask pixels: the mask grown by one pixel, minus the mask shrunk by one.
        var one = (float)(1.0 / step * px); // one screen pixel, in mask pixels
        using var grow = SKImageFilter.CreateDilate(one * 1.5f, one * 1.5f);
        using var shrink = SKImageFilter.CreateErode(one * 0.5f, one * 0.5f);
        using (var outer = new SKPaint { ImageFilter = grow, ColorFilter = SKColorFilter.CreateBlendMode(dark, SKBlendMode.SrcIn) })
        using (var inner = new SKPaint { ImageFilter = shrink, BlendMode = SKBlendMode.DstOut })
        {
            canvas.SaveLayer(SKRect.Inflate(rect, 2 * px, 2 * px), null);
            canvas.DrawImage(image, rect, sampling, outer);
            canvas.DrawImage(image, rect, sampling, inner);
            canvas.Restore();
        }
        using (var lightEdge = new SKPaint { ImageFilter = grow, ColorFilter = SKColorFilter.CreateBlendMode(light, SKBlendMode.SrcIn) })
        using (var cut = new SKPaint { BlendMode = SKBlendMode.DstOut })
        {
            canvas.SaveLayer(SKRect.Inflate(rect, 2 * px, 2 * px), null);
            canvas.DrawImage(image, rect, sampling, lightEdge);
            canvas.DrawImage(image, rect, sampling, cut);
            canvas.Restore();
        }
    }

    private static void DrawPlacementPreview(
        SKCanvas canvas, MapDocument document, RenderOptions options)
    {
        if (options.PlacementPreviewObjects is not { Count: > 0 } preview)
        {
            return;
        }

        foreach (var sceneObject in preview)
        {
            DrawObject(canvas, sceneObject, document.StampForeground, options);
        }

        var scale = Math.Max(options.ViewScale, 0.01);
        var accent = options.PlacementInvalid
            ? new SKColor(239, 83, 80, 235)
            : new SKColor(98, 196, 255, 235);
        using var outline = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)(1.5 / scale),
            Color = accent,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(
                [(float)(7 / scale), (float)(5 / scale)], 0),
        };

        if (options.PlacementArea is { } area)
        {
            canvas.DrawOval(ToRect(area), outline);
        }
        else
        {
            // One clear outline is enough for a primitive/stamp; a scatter already has its area
            // ring and outlining sixty-four samples would turn the ghost into visual noise.
            canvas.DrawRect(ToRect(SceneGeometry.BoundsOf(preview[0])), outline);
        }
    }

    private static bool HasEffectiveMask(LayerMask mask, WorldBounds view)
    {
        foreach (var key in mask.Tiles.KeysIntersecting(view.Left, view.Top, view.Right, view.Bottom))
        {
            var coverage = mask.Tiles.TryGet(key);
            if (coverage is not null && MaskHasHiddenPixels(coverage))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MaskHasHiddenPixels(byte[] coverage) =>
        MaskCoverageStates.GetValue(
            coverage,
            static pixels => new MaskCoverageState(pixels.Any(value => value != 255)))
        .HasHiddenPixels;

    private sealed record MaskCoverageState(bool HasHiddenPixels);

    private static bool IsFullyCovered(byte[] coverage) =>
        LandCoverageStates.GetValue(
            coverage,
            static pixels => new LandCoverageState(pixels.All(value => value == 255)))
        .FullyCovered;

    private sealed record LandCoverageState(bool FullyCovered);

    private static void DrawOverviewObjects(
        SKCanvas canvas,
        MapDocument document,
        ObjectLayer layer,
        WorldBounds view,
        RenderOptions options)
    {
        var cache = options.ObjectCache!;
        var plan = OverviewPlans.GetValue(layer, static current => new ObjectOverviewPlan(current));
        var colors = new Dictionary<string, SKColor>(StringComparer.Ordinal);
        var colorSignature = new HashCode();
        foreach (var asset in document.Assets)
        {
            if (options.Stamps?.TryGetImage(asset.Sha256) is not { } image) continue;
            var color = StampOcclusion.Color(image);
            colors[asset.Sha256] = color;
            colorSignature.Add(asset.Sha256); colorSignature.Add(color);
        }
        var tintRevision = colorSignature.ToHashCode();
        var zoomBucket = OverviewZoomBucket(options.ViewScale);
        var bucketScale = Math.Pow(2, zoomBucket / 4.0);
        var minX = (int)Math.Floor(view.Left / OverviewChunkSize);
        var maxX = (int)Math.Floor(view.Right / OverviewChunkSize);
        var minY = (int)Math.Floor(view.Top / OverviewChunkSize);
        var maxY = (int)Math.Floor(view.Bottom / OverviewChunkSize);
        var cacheHitsBefore = cache.Hits;
        var cacheMissesBefore = cache.Misses;
        var cacheInvalidationsBefore = cache.Invalidations;
        var exact = new Dictionary<ObjectId, SceneObject>();
        var overviewObjects = 0;
        var overviewChunks = 0;
        var overviewBatches = 0;
        var overviewMissing = 0;

        for (var chunkY = minY; chunkY <= maxY; chunkY++)
        {
            for (var chunkX = minX; chunkX <= maxX; chunkX++)
            {
                if (!plan.TryGetVariant(chunkX, chunkY, zoomBucket, bucketScale, colors, tintRevision, out var variant))
                {
                    continue;
                }

                overviewChunks++;
                overviewObjects += variant.OverviewObjectCount;
                overviewBatches += variant.BatchCount;
                overviewMissing += variant.MissingCount;
                var cached = cache.GetOrCreate(
                    layer.Id,
                    chunkX,
                    chunkY,
                    zoomBucket,
                    variant.ContentHash,
                    variant.BuildImage);
                canvas.DrawImage(
                    cached.Image,
                    variant.Bounds,
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                foreach (var sceneObject in variant.ExactObjects)
                {
                    if (SceneGeometry.BoundsOf(sceneObject).Intersects(view)) exact.TryAdd(sceneObject.Id, sceneObject);
                }
            }
        }

        // Far zoom keeps spatial density cheap, but anything the user is manipulating remains an
        // exact object over the aggregate representation. The immutable base picture stays hot.
        if (options.SelectedObjectId is { } selected
            && DocumentRevisionIndex.For(document).TryFind(selected, out var primary)
            && primary.LayerId == layer.Id)
        {
            exact[primary.Object.Id] = primary.Object;
        }
        if (options.CoSelectedObjectIds is { Count: > 0 } selectedObjects)
        {
            foreach (var id in selectedObjects)
            {
                if (DocumentRevisionIndex.For(document).TryFind(id, out var selectedObject)
                    && selectedObject.LayerId == layer.Id)
                {
                    exact[selectedObject.Object.Id] = selectedObject.Object;
                }
            }
        }
        if (options.ObjectOverrides is { Count: > 0 } overrides)
        {
            foreach (var replacement in overrides)
            {
                if (SceneGeometry.BoundsOf(replacement).Intersects(view))
                {
                    exact[replacement.Id] = replacement;
                }
            }
        }

        var hiddenLabels = LabelPriority.Hidden(document, options.ViewScale);
        foreach (var sceneObject in exact.Values.OrderBy(item => item.Sublayer))
        {
            if (GivesWay(sceneObject, hiddenLabels, options)) continue;
            if (sceneObject.Stamp is { } missing && !colors.ContainsKey(missing.AssetSha256)
                && Math.Max(sceneObject.Width, sceneObject.Height) * bucketScale < OverviewExactScreenSize)
                overviewMissing--;
            DrawObject(canvas, sceneObject, document.StampForeground, options);
            options.Stats?.RecordExactObject(sceneObject);
        }

        if (options.Stats is { } stats)
        {
            stats.ObjectCandidates += overviewObjects + exact.Count;
            stats.ObjectsDrawn += overviewObjects + exact.Count;
            stats.OverviewObjects += overviewObjects;
            stats.MissingAssets += Math.Max(0, overviewMissing);
            stats.OverviewChunks += overviewChunks;
            stats.OverviewBatches += overviewBatches;
            stats.ExactObjects += exact.Count;
            stats.DrawCalls += overviewChunks + exact.Count;
            stats.ObjectCacheHits += cache.Hits - cacheHitsBefore;
            stats.ObjectCacheMisses += cache.Misses - cacheMissesBefore;
            stats.ObjectCacheInvalidations += cache.Invalidations - cacheInvalidationsBefore;
        }
    }

    /// <summary>
    /// R07b: a label hidden at this scale or under a more important one (<see cref="LabelPriority"/>);
    /// the selected label always shows, so the user can still see what they edit.
    /// </summary>
    private static bool GivesWay(SceneObject sceneObject, IReadOnlySet<ObjectId> hiddenLabels, RenderOptions options) =>
        hiddenLabels.Count > 0 && sceneObject.Kind == SceneObjectKind.Text && hiddenLabels.Contains(sceneObject.Id)
        && sceneObject.Id != options.SelectedObjectId && options.CoSelectedObjectIds?.Contains(sceneObject.Id) != true;

    private static int OverviewZoomBucket(double scale) =>
        (int)Math.Floor(Math.Log2(
            Math.Max(scale, 1.0 / CanvasDimensionPolicy.Maximum)) * 4);

    private sealed class ObjectOverviewPlan
    {
        private readonly ImmutableDictionary<(int X, int Y), OverviewChunk> _chunks;
        private readonly bool _compacted;

        public ObjectOverviewPlan(ObjectLayer layer)
        {
            if (ObjectLayerChanges.TryGet(layer, out var delta))
            {
                var previous = OverviewPlans.GetValue(delta.Source, static l => new(l));
                _compacted = previous._compacted || delta.OrderCompacted;
                var chunks = previous._chunks.ToBuilder();
                var changes = new Dictionary<(int, int), (List<ObjectId> Remove, List<(long, SceneObject)> Add)>();
                foreach (var index in delta.Removed) Collect(delta.Source.Objects[index], index, false);
                foreach (var index in delta.Added) Collect(layer.Objects[index], index, true);
                foreach (var (key, change) in changes)
                {
                    var objects = chunks.TryGetValue(key, out var oldChunk)
                        ? oldChunk.Objects.ToBuilder() : ImmutableDictionary.CreateBuilder<ObjectId, (long Order, SceneObject Item)>();
                    foreach (var index in change.Remove) objects.Remove(index);
                    foreach (var (order, item) in change.Add) objects[item.Id] = (order, item);
                    if (_compacted)
                        foreach (var id in objects.Keys.ToArray())
                            objects[id] = (((SceneObjectList)layer.Objects).IndexOfId(id), objects[id].Item);
                    if (objects.Count == 0) chunks.Remove(key);
                    else chunks[key] = new OverviewChunk(key.Item1, key.Item2, objects.ToImmutable());
                }
                _chunks = chunks.ToImmutable();

                void Collect(SceneObject item, int index, bool add)
                {
                    if (!item.Visible) return;
                    var key = KeyOf(item);
                    if (!changes.TryGetValue(key, out var change)) changes[key] = change = ([], []);
                    if (add) change.Add.Add((index, item));
                    else change.Remove.Add(item.Id);
                }
            }
            else
            {
                var chunks = new Dictionary<(int, int), ImmutableDictionary<ObjectId, (long Order, SceneObject Item)>.Builder>();
                for (var index = 0; index < layer.Objects.Count; index++)
                {
                    var item = layer.Objects[index];
                    if (!item.Visible) continue;
                    var key = KeyOf(item);
                    if (!chunks.TryGetValue(key, out var objects)) chunks[key] = objects = ImmutableDictionary.CreateBuilder<ObjectId, (long Order, SceneObject Item)>();
                    objects.Add(item.Id, (index, item));
                }
                _chunks = chunks.ToImmutableDictionary(p => p.Key,
                    p => new OverviewChunk(p.Key.Item1, p.Key.Item2, p.Value.ToImmutable()));
            }
        }

        private static (int, int) KeyOf(SceneObject item) =>
            ((int)Math.Floor((item.X + item.Width / 2) / OverviewChunkSize),
             (int)Math.Floor((item.Y + item.Height / 2) / OverviewChunkSize));

        public bool TryGetVariant(int chunkX, int chunkY, int zoomBucket, double bucketScale,
            IReadOnlyDictionary<string, SKColor> colors, int tintRevision, out OverviewVariant variant)
        {
            if (!_chunks.TryGetValue((chunkX, chunkY), out var chunk)) { variant = null!; return false; }
            variant = chunk.GetVariant(zoomBucket, bucketScale, colors, tintRevision);
            return true;
        }
    }

    private sealed class OverviewChunk(int x, int y, ImmutableDictionary<ObjectId, (long Order, SceneObject Item)> objects)
    {
        private readonly Dictionary<(int Zoom, int Tints), OverviewVariant> _variants = [];

        public ImmutableDictionary<ObjectId, (long Order, SceneObject Item)> Objects { get; } = objects;

        public OverviewVariant GetVariant(int zoomBucket, double bucketScale,
            IReadOnlyDictionary<string, SKColor> colors, int tintRevision)
        {
            lock (_variants) return Build();

            OverviewVariant Build()
            {
            if (_variants.TryGetValue((zoomBucket, tintRevision), out var existing))
            {
                return existing;
            }

            var batches = new List<(SKColor Color, List<SKPoint> Points)>();
            var exact = new List<SceneObject>();
            var hash = 14_695_981_039_346_656_037UL;
            var overviewCount = 0;
            var missingCount = 0;
            foreach (var sceneObject in Objects.Values.OrderBy(o => o.Item.Sublayer).ThenBy(o => o.Order).Select(o => o.Item))
            {
                hash = HashObject(hash, sceneObject);
                if (Math.Max(sceneObject.Width, sceneObject.Height) * bucketScale >= OverviewExactScreenSize
                    || sceneObject.Stamp is { } detailStamp &&
                    (colors.ContainsKey(detailStamp.AssetSha256) && detailStamp is not
                        { Blur: 0, ShadowOpacity: 0, ClipMaskEnabled: false, Blend: "Normal", Hue: 0,
                          Saturation: 0, Brightness: 0, Contrast: 0 }))
                {
                    exact.Add(sceneObject);
                    continue;
                }

                overviewCount++;
                if (sceneObject.Stamp is { } absent && !colors.ContainsKey(absent.AssetSha256)) missingCount++;
                var color = sceneObject.Stamp is { } stamp && colors.TryGetValue(stamp.AssetSha256, out var tint)
                    ? tint.WithScaledAlpha(Alpha(sceneObject.ObjectOpacity)) : OverviewColor(sceneObject);
                hash = (hash ^ (uint)color.GetHashCode()) * 1_099_511_628_211UL;
                if (batches.Count == 0 || batches[^1].Color != color) batches.Add((color, []));
                var points = batches[^1].Points;
                points.Add(new SKPoint(
                    (float)(sceneObject.X + (sceneObject.Width / 2)),
                    (float)(sceneObject.Y + (sceneObject.Height / 2))));
            }

            var immutableBatches = batches
                .Select(pair => new OverviewBatch(pair.Color, [.. pair.Points]))
                .ToArray();
            var bounds = new SKRect(
                (float)(x * OverviewChunkSize),
                (float)(y * OverviewChunkSize),
                (float)((x + 1) * OverviewChunkSize),
                (float)((y + 1) * OverviewChunkSize));
            var variant = new OverviewVariant(
                hash,
                bounds,
                bucketScale,
                immutableBatches,
                exact,
                overviewCount,
                missingCount);
            if (_variants.Count >= 4) _variants.Clear();
            _variants.Add((zoomBucket, tintRevision), variant);
            return variant;
            }
        }
    }

    private sealed class OverviewVariant(
        ulong contentHash,
        SKRect bounds,
        double bucketScale,
        IReadOnlyList<OverviewBatch> batches,
        IReadOnlyList<SceneObject> exactObjects,
        int overviewObjectCount,
        int missingCount)
    {
        public ulong ContentHash { get; } = contentHash;
        public IReadOnlyList<SceneObject> ExactObjects { get; } = exactObjects;
        public int OverviewObjectCount { get; } = overviewObjectCount;
        public int MissingCount { get; } = missingCount;
        public int BatchCount => batches.Count;
        public SKRect Bounds => bounds;

        public SKImage BuildImage()
        {
            var imageScale = Math.Min(bucketScale, 512.0 / Math.Max(bounds.Width, bounds.Height));
            var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * imageScale));
            var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * imageScale));
            using var surface = SKSurface.Create(new SKImageInfo(
                width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("Could not allocate an overview chunk surface.");
            var recording = surface.Canvas;
            recording.Clear(SKColors.Transparent);
            recording.Scale((float)imageScale);
            recording.Translate(-bounds.Left, -bounds.Top);
            foreach (var batch in batches)
            {
                using var paint = new SKPaint
                {
                    Color = batch.Color,
                    IsAntialias = false,
                    StrokeWidth = (float)(1.25 / bucketScale),
                    StrokeCap = SKStrokeCap.Square,
                };
                recording.DrawPoints(SKPointMode.Points, batch.Points, paint);
            }
            recording.Flush();
            return surface.Snapshot();
        }
    }

    private sealed record OverviewBatch(SKColor Color, SKPoint[] Points);

    private static ulong HashObject(ulong hash, SceneObject sceneObject)
    {
        static ulong Add(ulong current, ulong value) => (current ^ value) * 1_099_511_628_211UL;

        var bytes = sceneObject.Id.Value.ToByteArray();
        hash = Add(hash, BitConverter.ToUInt64(bytes, 0));
        hash = Add(hash, BitConverter.ToUInt64(bytes, 8));
        hash = Add(hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(sceneObject.X)));
        hash = Add(hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(sceneObject.Y)));
        hash = Add(hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(sceneObject.Width)));
        hash = Add(hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(sceneObject.Height)));
        hash = Add(hash, unchecked((ulong)sceneObject.Sublayer));
        hash = Add(hash, (ulong)sceneObject.Kind);
        hash = Add(hash, unchecked((ulong)sceneObject.GetHashCode()));
        hash = Add(hash, unchecked((ulong)OverviewColor(sceneObject).GetHashCode()));
        hash = Add(hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(sceneObject.ObjectOpacity)));
        return hash;
    }

    private static SKColor OverviewColor(SceneObject sceneObject)
    {
        var color = ParseColor(sceneObject.Kind switch
        {
            SceneObjectKind.Stamp => "#D9FF5A5A",
            SceneObjectKind.Text => sceneObject.Text?.FillColor ?? sceneObject.FillColor,
            SceneObjectKind.Line => sceneObject.Line?.Stroke.Color ?? sceneObject.StrokeColor,
            SceneObjectKind.Shape => sceneObject.Shape?.Fill.Color ?? sceneObject.FillColor,
            SceneObjectKind.Composition => "#D979638E",
            _ => sceneObject.FillColor,
        });
        return color.WithScaledAlpha(Alpha(sceneObject.ObjectOpacity));
    }

    private static SceneObject? FindRenderObject(
        MapDocument document, RenderOptions options, ObjectId id) =>
        options.ObjectOverrides?.FirstOrDefault(sceneObject => sceneObject.Id == id)
        ?? DocumentRevisionIndex.For(document).FindObject(id);

    /// <summary>
    /// The shared frame of a multi-selection: an axis-aligned ring, eight resize handles and the
    /// rotation handle on a stem, all at constant on-screen size. Geometry must match
    /// <c>MultiSelectionGeometry.ComputeFrameHandles</c>, which the input side hit-tests.
    /// </summary>
    private static void DrawSelectionFrame(SKCanvas canvas, WorldBounds frame, double viewScale)
    {
        var px = (float)(1.0 / viewScale);
        var left = (float)frame.Left;
        var top = (float)frame.Top;
        var right = (float)frame.Right;
        var bottom = (float)frame.Bottom;
        var centreX = (left + right) / 2;
        var centreY = (top + bottom) / 2;

        using (var ring = new SKPaint
        {
            Color = SelectionColor,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2 * px,
        })
        {
            canvas.DrawRect(left, top, right - left, bottom - top, ring);
            canvas.DrawLine(centreX, top, centreX, top - (28 * px), ring);
        }

        using var handleFill = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var handleEdge = new SKPaint
        {
            Color = SelectionColor,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f * px,
        };

        var half = 4 * px;
        foreach (var (x, y) in new (float X, float Y)[]
                 {
                     (left, top), (centreX, top), (right, top), (right, centreY),
                     (right, bottom), (centreX, bottom), (left, bottom), (left, centreY),
                 })
        {
            var rect = new SKRect(x - half, y - half, x + half, y + half);
            canvas.DrawRect(rect, handleFill);
            canvas.DrawRect(rect, handleEdge);
        }

        canvas.DrawCircle(centreX, top - (28 * px), 5 * px, handleFill);
        canvas.DrawCircle(centreX, top - (28 * px), 5 * px, handleEdge);
    }

    /// <summary>The live rubber band: a translucent fill and a dashed constant-width border.</summary>
    private static void DrawMarquee(SKCanvas canvas, WorldBounds marquee, double viewScale)
    {
        var px = (float)(1.0 / viewScale);
        var rect = new SKRect(
            (float)marquee.Left, (float)marquee.Top, (float)marquee.Right, (float)marquee.Bottom);

        using var fill = new SKPaint
        {
            Color = SelectionColor.WithAlpha(28),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        canvas.DrawRect(rect, fill);

        using var dash = SKPathEffect.CreateDash([6 * px, 4 * px], 0);
        using var border = new SKPaint
        {
            Color = SelectionColor,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f * px,
            PathEffect = dash,
        };
        canvas.DrawRect(rect, border);
    }

    private static void DrawRing(SKCanvas canvas, SceneObject sceneObject, double viewScale)
    {
        var px = (float)(1.0 / viewScale);
        canvas.Save();
        ApplyRotation(canvas, sceneObject);

        using var ring = new SKPaint
        {
            Color = SelectionColor.WithAlpha(150),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f * px,
        };

        canvas.DrawRect(
            (float)sceneObject.X, (float)sceneObject.Y,
            (float)sceneObject.Width, (float)sceneObject.Height, ring);
        canvas.Restore();
    }

    /// <summary>
    /// The selection gizmo: ring, eight square resize handles and the round rotation handle on a
    /// stem above the top edge. All sizes are divided by the view scale so the gizmo keeps a
    /// constant on-screen size; positions must match <c>SelectionHandles.Compute</c>, which the
    /// input side uses for hit-testing.
    /// </summary>
    private static void DrawSelection(SKCanvas canvas, SceneObject selected, double viewScale)
    {
        var px = (float)(1.0 / viewScale);

        canvas.Save();
        ApplyRotation(canvas, selected);

        var left = (float)selected.X;
        var top = (float)selected.Y;
        var right = (float)(selected.X + selected.Width);
        var bottom = (float)(selected.Y + selected.Height);
        var centreX = (left + right) / 2;

        using (var ring = new SKPaint
        {
            Color = SelectionColor,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2 * px,
        })
        {
            canvas.DrawRect(left, top, right - left, bottom - top, ring);

            // Stem to the rotation handle (28 screen px above the top edge, matching the view model).
            canvas.DrawLine(centreX, top, centreX, top - (28 * px), ring);
        }

        using var handleFill = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var handleEdge = new SKPaint
        {
            Color = SelectionColor,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f * px,
        };

        var half = 4 * px;
        foreach (var (x, y) in new (float X, float Y)[]
                 {
                     (left, top), (centreX, top), (right, top), (right, (top + bottom) / 2),
                     (right, bottom), (centreX, bottom), (left, bottom), (left, (top + bottom) / 2),
                 })
        {
            var rect = new SKRect(x - half, y - half, x + half, y + half);
            canvas.DrawRect(rect, handleFill);
            canvas.DrawRect(rect, handleEdge);
        }

        // Round rotation handle.
        canvas.DrawCircle(centreX, top - (28 * px), 5 * px, handleFill);
        canvas.DrawCircle(centreX, top - (28 * px), 5 * px, handleEdge);

        canvas.Restore();
    }

    private static void DrawShape(SKCanvas canvas, SceneObjectKind kind, SKRect rect, SKPaint paint)
    {
        if (kind == SceneObjectKind.Ellipse)
        {
            canvas.DrawOval(rect, paint);
        }
        else
        {
            canvas.DrawRect(rect, paint);
        }
    }

    private static void ApplyRotation(SKCanvas canvas, SceneObject sceneObject)
    {
        if (sceneObject.RotationDegrees != 0)
        {
            canvas.RotateDegrees(
                (float)sceneObject.RotationDegrees,
                (float)(sceneObject.X + (sceneObject.Width / 2)),
                (float)(sceneObject.Y + (sceneObject.Height / 2)));
        }
    }

    private static SKRect TileRect(TileKey key) => new(
        key.X * TileMap.TileSize,
        key.Y * TileMap.TileSize,
        (key.X + 1) * TileMap.TileSize,
        (key.Y + 1) * TileMap.TileSize);

    private static SKRect ToRect(WorldBounds bounds) => new(
        (float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom);

    private static WorldBounds Expand(WorldBounds bounds, double padding) => new(
        bounds.Left - padding, bounds.Top - padding,
        bounds.Right + padding, bounds.Bottom + padding);

    internal static SKColor WithScaledAlpha(this SKColor color, byte layerAlpha) =>
        layerAlpha == 255 ? color : color.WithAlpha((byte)(color.Alpha * layerAlpha / 255));

    private static (byte R, byte G, byte B, byte A) ParseChannels(string color)
    {
        var c = ParseColor(color);
        return (c.Red, c.Green, c.Blue, c.Alpha);
    }

    /// <summary>Parses "#AARRGGBB" (or "#RRGGBB" as opaque). The domain stores colours as strings.</summary>
    /// <summary>
    /// Builds the Skia shader for a document gradient inside one object's bounds, or null when the
    /// object has no gradient - in which case the paint keeps its flat colour and nothing changes.
    /// <para>
    /// The object's own alpha is folded into every stop rather than left on the paint, because a
    /// paint colour is ignored once a shader is set; doing it here keeps a gradient object fading
    /// exactly like a flat one under layer opacity and preview.
    /// </para>
    /// </summary>
    internal static SKShader? CreateGradientShader(GradientFill? gradient, SKRect rect, byte alpha)
    {
        if (gradient is null || rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        var colors = new SKColor[gradient.Stops.Count];
        var positions = new float[gradient.Stops.Count];
        for (var index = 0; index < gradient.Stops.Count; index++)
        {
            colors[index] = ParseColor(gradient.Stops[index].Color).WithScaledAlpha(alpha);
            positions[index] = (float)gradient.Stops[index].Offset;
        }

        if (gradient.Kind == GradientKind.Radial)
        {
            return SKShader.CreateRadialGradient(
                new SKPoint(
                    rect.Left + (float)(gradient.CenterX * rect.Width),
                    rect.Top + (float)(gradient.CenterY * rect.Height)),
                (float)Math.Max(0.001, gradient.Radius * Math.Max(rect.Width, rect.Height)),
                colors, positions, SKShaderTileMode.Clamp);
        }

        // The ramp runs through the centre and is long enough to cover the whole rect at any
        // angle, so 0..1 always spans the object instead of clipping at a diagonal.
        var angle = gradient.AngleDegrees * Math.PI / 180;
        var half = ((Math.Abs(Math.Cos(angle)) * rect.Width)
            + (Math.Abs(Math.Sin(angle)) * rect.Height)) / 2;
        var dx = (float)(Math.Cos(angle) * half);
        var dy = (float)(Math.Sin(angle) * half);
        return SKShader.CreateLinearGradient(
            new SKPoint(rect.MidX - dx, rect.MidY - dy),
            new SKPoint(rect.MidX + dx, rect.MidY + dy),
            colors, positions, SKShaderTileMode.Clamp);
    }

    internal static SKColor ParseColor(string color)
    {
        var span = color.AsSpan().TrimStart('#');
        var value = uint.Parse(span, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);

        if (span.Length == 6)
        {
            value |= 0xFF000000;
        }

        return new SKColor(
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF),
            (byte)((value >> 24) & 0xFF));
    }
}
