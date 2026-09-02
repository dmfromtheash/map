using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;
using System.Runtime.CompilerServices;

namespace Map.Rendering;

/// <summary>Supplies decoded asset images to the renderer. Stage 4's catalog implements it.</summary>
public interface IStampImageSource
{
    /// <summary>The image for the content hash, or null when the asset is missing/not resolved.</summary>
    SKImage? TryGetImage(string sha256);
}

/// <summary>The live, uncommitted part of a raster gesture, drawn on top of its layer.</summary>
public sealed class StrokePreview
{
    public required LayerId LayerId { get; init; }

    /// <summary>Per-tile coverage 0..255 from the in-progress <c>RasterStrokeSession</c>.</summary>
    public required IReadOnlyDictionary<TileKey, byte[]> Coverage { get; init; }

    public string Color { get; init; } = "#FF000000";
    public double Opacity { get; init; } = 1.0;
    public bool Eraser { get; init; }
    public BrushSettings? PaintSettings { get; init; }

    /// <summary>Mask strokes preview as the red mask overlay, not as paint.</summary>
    public bool IsMask { get; init; }

    /// <summary>The preview belongs to the document-owned, map-aligned stamp foreground.</summary>
    public bool IsStampForeground { get; init; }
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
    public int ExactObjects { get; set; }
    public int OverviewObjects { get; set; }
    public int OverviewChunks { get; set; }
    public int OverviewBatches { get; set; }
    public int DrawCalls { get; set; }
    public long ObjectCacheHits { get; set; }
    public long ObjectCacheMisses { get; set; }
    public long ObjectCacheInvalidations { get; set; }
    public int StampInstances { get; set; }
    public int RepeatedAssetInstances { get; set; }
    public int TextObjects { get; set; }
    public int VectorObjects { get; set; }
    public int CompositionObjects { get; set; }
    public int EffectObjects { get; set; }
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
        ExactObjects = 0;
        OverviewObjects = 0;
        OverviewChunks = 0;
        OverviewBatches = 0;
        DrawCalls = 0;
        ObjectCacheHits = 0;
        ObjectCacheMisses = 0;
        ObjectCacheInvalidations = 0;
        StampInstances = 0;
        RepeatedAssetInstances = 0;
        TextObjects = 0;
        VectorObjects = 0;
        CompositionObjects = 0;
        EffectObjects = 0;
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
public enum RenderLodTier
{
    Exact,
    Interactive,
    Overview,
}

/// <summary>Options of one scene pass. Selection overlay is editor chrome and is opt-in only.</summary>
public sealed class RenderOptions
{
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
    public WorldBounds? VisibleWorld { get; init; }

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

    public TileImageCache? TileCache { get; init; }

    /// <summary>Render-context-local cache used only by the editor overview tier.</summary>
    public ObjectOverviewCache? ObjectCache { get; init; }

    public RenderLodTier LodTier { get; init; } = RenderLodTier.Exact;

    public IStampImageSource? Stamps { get; init; }

    public StrokePreview? Preview { get; init; }

    /// <summary>While a mask is being edited its hidden area is tinted red on this layer only.</summary>
    public LayerId? MaskEditLayerId { get; init; }

    public RenderStats? Stats { get; init; }

    /// <summary>During pan/gesture draw source land fill only; idle/export evaluates all effects.</summary>
    public bool FastPreview { get; init; }

    /// <summary>Export paths opt in so editor-only grids can be omitted independently.</summary>
    public bool IsExport { get; init; }

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
public static class SceneRenderer
{
    private static readonly LayerId StampForegroundLayerId =
        new(new Guid("2e304ada-4a3e-4f2e-896c-c92977b52150"));

    private static readonly SKColor SelectionColor = new(255, 214, 10, 255);
    private const double OverviewChunkSize = 8_192;
    private const double OverviewExactScreenSize = 4;
    private static readonly ConditionalWeakTable<ObjectLayer, ObjectOverviewPlan> OverviewPlans = [];
    private static readonly ConditionalWeakTable<byte[], MaskCoverageState> MaskCoverageStates = [];

    /// <summary>Draws the document in world coordinates. The caller sets the world transform.</summary>
    public static void Render(SKCanvas canvas, MapDocument document, RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        options.Stats?.Reset();

        var canvasBounds = new WorldBounds(0, 0, document.Canvas.Width, document.Canvas.Height);
        var requestedView = options.VisibleWorld ?? canvasBounds;
        if (!TryIntersect(requestedView, canvasBounds, out var view))
        {
            DrawBoundaryOverlay(canvas, options.BoundaryOverlay, options.ViewScale);
            return;
        }

        // One factual document boundary for viewport, live preview, thumbnail and export. Layer
        // implementations may retain and cull hidden data, but no content can paint through it.
        canvas.Save();
        canvas.ClipRect(ToRect(canvasBounds));

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

            if (options.Stats is { } stats)
            {
                stats.LayersDrawn++;
            }

            var clippingSource = layerIndex > 0 ? document.Layers[layerIndex - 1] : null;
            DrawLayer(canvas, document, layer, clippingSource, view, options);
        }

        DrawPlacementPreview(canvas, document, options);
        DrawSelectionChrome(canvas, document, options);
        canvas.Restore();
        DrawBoundaryOverlay(canvas, options.BoundaryOverlay, options.ViewScale);
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
        WorldBounds view, RenderOptions options)
    {
        var mask = layer.Mask is { Enabled: true } m
            && HasEffectiveMask(m, view)
                ? m
                : null;
        var maskEdit = options.MaskEditLayerId == layer.Id && layer.Mask is not null;
        var effects = (layer as ObjectLayer)?.Effects;
        var clipsToLower = effects?.ClippingEnabled == true
            && clippingSource is { Visible: true, Opacity: > 0 };
        var needsGroup = mask is not null || clipsToLower || layer.Opacity < 1.0 || layer.Blend != "Normal";

        if (needsGroup)
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
            DrawLayerContent(canvas, document, layer, view, options, includePreview: false);
            canvas.Restore();
        }

        DrawLayerContent(canvas, document, layer, view, options, includePreview: true);

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
                DrawLayerContent(canvas, document, layer, view, options, includePreview: false);
                canvas.Restore();
            }
            using (var inside = new SKPaint { BlendMode = SKBlendMode.DstIn })
            {
                canvas.SaveLayer(ToRect(view), inside);
                DrawLayerContent(canvas, document, layer, view, options, includePreview: false);
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
            DrawLayerContent(canvas, document, clippingSource, view, options, includePreview: false);
            if (clippingSource.Mask is { Enabled: true } lowerMask && HasEffectiveMask(lowerMask, view))
                ApplyMask(canvas, clippingSource.Id, lowerMask, view, options);
            canvas.Restore();
        }

        if (maskEdit)
        {
            DrawMaskOverlay(canvas, layer, view, options);
        }

        canvas.Restore();
    }

    private static void DrawLayerContent(
        SKCanvas canvas, MapDocument document, MapLayer layer, WorldBounds view,
        RenderOptions options, bool includePreview)
    {
        switch (layer)
        {
            case BaseColorLayer baseColor:
                using (var page = new SKPaint { Color = ParseColor(baseColor.Color), IsAntialias = false })
                {
                    canvas.DrawRect(0, 0, document.Canvas.Width, document.Canvas.Height, page);
                }

                break;

            case ObjectLayer objectLayer:
                if (options.PlacementPreviewLayerId == objectLayer.Id
                    && options.PlacementPreviewObjects is { Count: > 0 } placementPreview)
                {
                    foreach (var previewObject in placementPreview)
                    {
                        DrawLayerStampShadow(
                            canvas, previewObject, document.StampForeground, options);
                    }
                }
                DrawObjects(canvas, document, objectLayer, view, options);
                break;

            case LandLayer landLayer:
                DrawLand(canvas, landLayer, view, options);
                break;

            case GridLayer gridLayer:
                DrawGridLayer(canvas, gridLayer, view, options);
                break;

            case FilterLayer filterLayer:
                DrawFilterOverlay(canvas, filterLayer.Settings, view, options);
                break;

            case IRasterContentLayer raster:
                DrawTiles(canvas, layer.Id, raster.ContentTiles, view, options, TileImageKind.Content);
                break;
        }

        if (includePreview && options.Preview is { } preview && preview.LayerId == layer.Id && !preview.IsMask)
        {
            DrawStrokePreview(canvas, preview, view, options);
        }
    }

    private static void DrawLand(
        SKCanvas canvas, LandLayer layer, WorldBounds view, RenderOptions options)
    {
        if (layer.GeometryTiles.TileCount == 0)
        {
            return;
        }

        var style = layer.EdgeStyle;
        var sampling = options.ViewScale < 0.9
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        var keys = layer.GeometryTiles
            .KeysIntersecting(view.Left, view.Top, view.Right, view.Bottom)
            .ToList();
        if (keys.Count == 0)
        {
            return;
        }

        if (!options.FastPreview)
        {
            if (style.OuterShadowOpacity > 0 && style.ShadowBlur > 0)
            {
                using var filter = SKImageFilter.CreateDropShadowOnly(
                    (float)style.ShadowOffsetX, (float)style.ShadowOffsetY,
                    (float)style.ShadowBlur, (float)style.ShadowBlur,
                    SKColors.Black.WithAlpha(Alpha(style.OuterShadowOpacity)));
                DrawFilteredCoverage(filter, color: null, SKBlendMode.SrcOver);
            }

            for (var ripple = Math.Clamp(style.RippleCount, 0, 8); ripple >= 1; ripple--)
            {
                var radius = Math.Max(0, style.RippleSpacing * ripple);
                DrawCoverageRing(
                    radius,
                    Math.Max(0.5, style.RippleWidth),
                    ParseColor(style.RippleColor));
            }

            if (style.OutlineWidth > 0)
            {
                DrawExpandedCoverage(style.OutlineWidth, ParseColor(style.OutlineColor));
            }
        }

        using (var tint = SKColorFilter.CreateBlendMode(ParseColor(style.FillColor), SKBlendMode.SrcIn))
        using (var fill = new SKPaint { ColorFilter = tint, IsAntialias = true })
        {
            DrawCoverageImages(fill);
        }

        if (!options.FastPreview && style.InnerShadowOpacity > 0 && style.ShadowBlur > 0)
        {
            using var filter = SKImageFilter.CreateDropShadowOnly(
                (float)style.ShadowOffsetX, (float)style.ShadowOffsetY,
                (float)style.ShadowBlur, (float)style.ShadowBlur,
                SKColors.Black.WithAlpha(Alpha(style.InnerShadowOpacity)));
            DrawFilteredCoverage(filter, color: null, SKBlendMode.SrcATop);
        }

        if (options.Stats is { } stats)
        {
            stats.TilesDrawn += keys.Count;
            if (options.ViewScale < 0.9)
            {
                stats.MipmappedTiles += keys.Count;
            }
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
            foreach (var key in keys)
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

        void DrawCoverageRing(double radius, double width, SKColor color)
        {
            canvas.SaveLayer(ToRect(view), null);
            DrawExpandedCoverage(radius + width, color);
            using var erase = new SKPaint { BlendMode = SKBlendMode.DstOut };
            DrawExpandedCoverageCore(radius, null, erase);
            canvas.Restore();
        }

        void DrawExpandedCoverage(double radius, SKColor color) =>
            DrawExpandedCoverageCore(radius, color, paint: null);

        void DrawExpandedCoverageCore(double radius, SKColor? color, SKPaint? paint)
        {
            using var tint = paint is null && color is { } value
                ? SKColorFilter.CreateBlendMode(value, SKBlendMode.SrcIn)
                : null;
            using var ownedPaint = paint is null
                ? new SKPaint { ColorFilter = tint, IsAntialias = true }
                : null;
            var drawPaint = paint ?? ownedPaint;
            DrawCoverageImages(drawPaint);
            if (radius <= 0)
            {
                return;
            }

            const int directionCount = 8;
            for (var direction = 0; direction < directionCount; direction++)
            {
                var angle = (Math.PI * 2 * direction) / directionCount;
                canvas.Save();
                canvas.Translate(
                    (float)(Math.Cos(angle) * radius),
                    (float)(Math.Sin(angle) * radius));
                DrawCoverageImages(drawPaint);
                canvas.Restore();
            }
        }
    }

    private static byte Alpha(double opacity) =>
        (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);

    private static void DrawObjects(
        SKCanvas canvas,
        MapDocument document,
        ObjectLayer layer,
        WorldBounds view,
        RenderOptions options)
    {
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
            ordered = candidates.Count > 1 && candidates.Any(o => o.Sublayer != 0)
                ? [.. candidates.OrderBy(o => o.Sublayer)]
                : candidates;
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

            resolved.Sort(static (left, right) =>
            {
                var sublayer = left.Object.Sublayer.CompareTo(right.Object.Sublayer);
                return sublayer != 0 ? sublayer : left.Position.CompareTo(right.Position);
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
        }

        foreach (var sceneObject in ordered)
        {
            if (!sceneObject.Visible)
            {
                continue;
            }

            DrawObject(canvas, sceneObject, document.StampForeground, options);
            if (options.Stats is { } s)
            {
                s.ObjectsDrawn++;
                s.ExactObjects++;
                s.DrawCalls++;
                s.RecordExactObject(sceneObject);
            }
        }
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

            // Preview tiles change every mouse move, so they are built fresh, never cached.
            var pixels = !preview.IsMask && !preview.Eraser && preview.PaintSettings is { } settings
                ? RasterStrokeSession.RenderPreviewTile(settings, key, coverage)
                : new byte[TileMap.TileSize * TileMap.TileSize * 4];
            if (preview.IsMask || preview.Eraser || preview.PaintSettings is null)
            {
                for (var i = 0; i < coverage.Length; i++)
                {
                    if (coverage[i] == 0) continue;

                    var value = coverage[i] / 255.0 * alpha;
                    var offset = i * 4;
                    pixels[offset] = (byte)Math.Round(r * value);
                    pixels[offset + 1] = (byte)Math.Round(g * value);
                    pixels[offset + 2] = (byte)Math.Round(b * value);
                    pixels[offset + 3] = (byte)Math.Round(255 * value);
                }
            }

            using var image = SKImage.FromPixelCopy(
                new SKImageInfo(TileMap.TileSize, TileMap.TileSize, SKColorType.Rgba8888, SKAlphaType.Premul),
                pixels);

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
        RenderOptions options)
    {
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

        if (wrapped)
        {
            canvas.Restore();
        }
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

        var points = VectorGeometry.Flatten(owner, region.Nodes, closed: true);
        path.MoveTo((float)points[0].X, (float)points[0].Y);
        foreach (var point in points.Skip(1)) path.LineTo((float)point.X, (float)point.Y);
        path.Close();
        return path;
    }

    private static IReadOnlyList<VectorPoint> RegionPoints(SceneObject owner, ShapeRegion region)
    {
        if (region.Primitive != ShapePrimitiveKind.Ellipse)
        {
            return VectorGeometry.Flatten(owner, region.Nodes, closed: true);
        }

        var a = region.Nodes[0]; var b = region.Nodes[1];
        var cx = (a.X + b.X) / 2; var cy = (a.Y + b.Y) / 2;
        var rx = Math.Abs(b.X - a.X) / 2; var ry = Math.Abs(b.Y - a.Y) / 2;
        return [.. Enumerable.Range(0, 33).Select(index =>
        {
            var angle = index * Math.PI * 2 / 32;
            return new VectorPoint(
                owner.X + ((cx + Math.Cos(angle) * rx) * owner.Width),
                owner.Y + ((cy + Math.Sin(angle) * ry) * owner.Height));
        })];
    }

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
            using var placeholderEdge = new SKPaint
            {
                Color = new SKColor(255, 90, 90, objectAlpha),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1f, rect.Width / 64),
                PathEffect = SKPathEffect.CreateDash([8, 6], 0),
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

        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(objectAlpha),
            IsAntialias = true,
            BlendMode = ParseBlend(stamp.Blend),
            ColorFilter = BuildColorFilter(stamp),
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
            DrawStampImage(canvas, image, rect, stamp, paint);
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
            DrawStampImage(canvas, visual, rect, stamp, basePaint);
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
            DrawStampImage(canvas, mask, rect, stamp, maskPaint);
        }
        using (var alphaPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            BlendMode = SKBlendMode.DstIn,
        })
        {
            DrawStampImage(canvas, visual, rect, stamp, alphaPaint);
        }
        canvas.Restore();
        canvas.Restore();
    }

    private static void DrawStampImage(
        SKCanvas canvas, SKImage image, SKRect rect, StampStyle stamp, SKPaint paint)
    {
        canvas.Save();
        if (stamp.FlipHorizontal || stamp.FlipVertical)
        {
            canvas.Translate(rect.MidX, rect.MidY);
            canvas.Scale(stamp.FlipHorizontal ? -1 : 1, stamp.FlipVertical ? -1 : 1);
            canvas.Translate(-rect.MidX, -rect.MidY);
        }
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
            var offset = (float)(Math.Clamp(stamp.Brightness, -1, 1) * 255);
            matrix = Multiply(
                [1, 0, 0, 0, offset, 0, 1, 0, 0, offset, 0, 0, 1, 0, offset, 0, 0, 0, 1, 0], matrix);
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

    private static void DrawOverviewObjects(
        SKCanvas canvas,
        MapDocument document,
        ObjectLayer layer,
        WorldBounds view,
        RenderOptions options)
    {
        var cache = options.ObjectCache!;
        var plan = OverviewPlans.GetValue(layer, static current => new ObjectOverviewPlan(current));
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

        for (var chunkY = minY; chunkY <= maxY; chunkY++)
        {
            for (var chunkX = minX; chunkX <= maxX; chunkX++)
            {
                if (!plan.TryGetVariant(chunkX, chunkY, zoomBucket, bucketScale, out var variant))
                {
                    continue;
                }

                overviewChunks++;
                overviewObjects += variant.OverviewObjectCount;
                overviewBatches += variant.BatchCount;
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
                    exact.TryAdd(sceneObject.Id, sceneObject);
                }
            }
        }

        // Far zoom keeps spatial density cheap, but anything the user is manipulating remains an
        // exact object over the aggregate representation. The immutable base picture stays hot.
        if (options.SelectedObjectId is { } selected && plan.TryFind(selected, out var primary))
        {
            exact[primary.Id] = primary;
        }
        if (options.CoSelectedObjectIds is { Count: > 0 } selectedObjects)
        {
            foreach (var id in selectedObjects)
            {
                if (plan.TryFind(id, out var selectedObject))
                {
                    exact[selectedObject.Id] = selectedObject;
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

        foreach (var sceneObject in exact.Values.OrderBy(item => item.Sublayer))
        {
            DrawObject(canvas, sceneObject, document.StampForeground, options);
            options.Stats?.RecordExactObject(sceneObject);
        }

        if (options.Stats is { } stats)
        {
            stats.ObjectCandidates += overviewObjects + exact.Count;
            stats.ObjectsDrawn += overviewObjects + exact.Count;
            stats.OverviewObjects += overviewObjects;
            stats.OverviewChunks += overviewChunks;
            stats.OverviewBatches += overviewBatches;
            stats.ExactObjects += exact.Count;
            stats.DrawCalls += overviewChunks + exact.Count;
            stats.ObjectCacheHits += cache.Hits - cacheHitsBefore;
            stats.ObjectCacheMisses += cache.Misses - cacheMissesBefore;
            stats.ObjectCacheInvalidations += cache.Invalidations - cacheInvalidationsBefore;
        }
    }

    private static int OverviewZoomBucket(double scale) =>
        (int)Math.Floor(Math.Log2(
            Math.Max(scale, 1.0 / CanvasDimensionPolicy.Maximum)) * 4);

    private sealed class ObjectOverviewPlan
    {
        private readonly Dictionary<(int X, int Y), OverviewChunk> _chunks = [];
        private readonly Dictionary<ObjectId, SceneObject> _objects = [];

        public ObjectOverviewPlan(ObjectLayer layer)
        {
            foreach (var sceneObject in layer.Objects)
            {
                if (!sceneObject.Visible)
                {
                    continue;
                }

                _objects[sceneObject.Id] = sceneObject;
                var centreX = sceneObject.X + (sceneObject.Width / 2);
                var centreY = sceneObject.Y + (sceneObject.Height / 2);
                var key = (
                    (int)Math.Floor(centreX / OverviewChunkSize),
                    (int)Math.Floor(centreY / OverviewChunkSize));
                if (!_chunks.TryGetValue(key, out var chunk))
                {
                    chunk = new OverviewChunk(key.Item1, key.Item2);
                    _chunks.Add(key, chunk);
                }
                chunk.Objects.Add(sceneObject);
            }
        }

        public bool TryFind(ObjectId id, out SceneObject sceneObject) =>
            _objects.TryGetValue(id, out sceneObject!);

        public bool TryGetVariant(
            int chunkX,
            int chunkY,
            int zoomBucket,
            double bucketScale,
            out OverviewVariant variant)
        {
            if (!_chunks.TryGetValue((chunkX, chunkY), out var chunk))
            {
                variant = null!;
                return false;
            }

            variant = chunk.GetVariant(zoomBucket, bucketScale);
            return true;
        }
    }

    private sealed class OverviewChunk(int x, int y)
    {
        private readonly Dictionary<int, OverviewVariant> _variants = [];

        public List<SceneObject> Objects { get; } = [];

        public OverviewVariant GetVariant(int zoomBucket, double bucketScale)
        {
            if (_variants.TryGetValue(zoomBucket, out var existing))
            {
                return existing;
            }

            var batches = new Dictionary<(int Sublayer, SKColor Color), List<SKPoint>>();
            var exact = new List<SceneObject>();
            var hash = 14_695_981_039_346_656_037UL;
            var overviewCount = 0;
            foreach (var sceneObject in Objects)
            {
                hash = HashObject(hash, sceneObject);
                if (Math.Max(sceneObject.Width, sceneObject.Height) * bucketScale
                    >= OverviewExactScreenSize)
                {
                    exact.Add(sceneObject);
                    continue;
                }

                overviewCount++;
                var key = (sceneObject.Sublayer, OverviewColor(sceneObject));
                if (!batches.TryGetValue(key, out var points))
                {
                    points = [];
                    batches.Add(key, points);
                }
                points.Add(new SKPoint(
                    (float)(sceneObject.X + (sceneObject.Width / 2)),
                    (float)(sceneObject.Y + (sceneObject.Height / 2))));
            }

            var immutableBatches = batches
                .OrderBy(pair => pair.Key.Sublayer)
                .Select(pair => new OverviewBatch(
                    pair.Key.Color, [.. pair.Value]))
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
                overviewCount);
            _variants.Add(zoomBucket, variant);
            return variant;
        }
    }

    private sealed class OverviewVariant(
        ulong contentHash,
        SKRect bounds,
        double bucketScale,
        IReadOnlyList<OverviewBatch> batches,
        IReadOnlyList<SceneObject> exactObjects,
        int overviewObjectCount)
    {
        public ulong ContentHash { get; } = contentHash;
        public IReadOnlyList<SceneObject> ExactObjects { get; } = exactObjects;
        public int OverviewObjectCount { get; } = overviewObjectCount;
        public int BatchCount => batches.Count;
        public SKRect Bounds => bounds;

        public SKImage BuildImage()
        {
            var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * bucketScale));
            var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * bucketScale));
            using var surface = SKSurface.Create(new SKImageInfo(
                width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("Could not allocate an overview chunk surface.");
            var recording = surface.Canvas;
            recording.Clear(SKColors.Transparent);
            recording.Scale((float)bucketScale);
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

            // Stem to the rotation handle (28 screen px above the top edge, matching the VM).
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

    private static SKColor WithScaledAlpha(this SKColor color, byte layerAlpha) =>
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
