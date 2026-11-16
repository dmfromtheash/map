using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

public static partial class SceneRenderer
{
    /// <summary>During a drag the document stays unchanged. Copy its kept layer outside the
    /// old/new object bounds, then evaluate just those bounds with the live substitutions.
    /// Null means this layer has no substitutions; false asks for the ordinary evaluator.</summary>
    private static bool? DrawLocalObjectPreview(SKCanvas canvas, MapDocument document, ObjectLayer layer,
        MapLayer? clippingSource, WorldBounds view, RenderOptions options, RenderOptions baseOptions,
        bool panning, bool keepLayers)
    {
        var index = DocumentRevisionIndex.For(document);
        var bounds = new List<WorldBounds>();
        var repairable = true;
        foreach (var proposed in options.ObjectOverrides!)
        {
            if (!index.TryFind(proposed.Id, out var before) || before.LayerId != layer.Id) continue;
            bounds.Add(SceneGeometry.BoundsOf(before.Object));
            bounds.Add(SceneGeometry.BoundsOf(proposed));
            repairable &= LayerImageSupport.RepairableStamp(proposed);
        }
        if (bounds.Count == 0) return null;
        if (!repairable || !keepLayers && !panning || options.LodTier == RenderLodTier.Overview
            || layer.Opacity != 1 || layer.Mask is { Enabled: true } || layer.Effects != new LayerEffects()
            || !layer.PlacementAreas.IsEmpty || !CanKeepLayerImage(layer, baseOptions)
            || !KeptLayerSupport.GetValue(layer, static l => new(l)).Repairable)
            return false;
        var matrix = canvas.TotalMatrix;
        if (!matrix.TryInvert(out var inverse)) return false;
        var device = canvas.DeviceClipBounds;
        using var excluded = new SKRegion();
        foreach (var dirty in bounds)
        {
            var rect = matrix.MapRect(ToRect(dirty));
            var patch = new SKRectI(Math.Max(device.Left, (int)Math.Floor(rect.Left) - 2),
                Math.Max(device.Top, (int)Math.Floor(rect.Top) - 2),
                Math.Min(device.Right, (int)Math.Ceiling(rect.Right) + 2),
                Math.Min(device.Bottom, (int)Math.Ceiling(rect.Bottom) + 2));
            if (patch.Width > 0 && patch.Height > 0) excluded.Op(patch, SKRegionOperation.Union);
        }
        var kept = DrawPannedObjectLayer(canvas, document, layer, clippingSource, baseOptions,
            reuseOnly: !panning, exclude: excluded)
            || keepLayers && DrawKeptObjectLayer(canvas, document, layer, clippingSource, view, baseOptions, excluded);
        if (!kept) return false;
        using var rectangles = excluded.CreateRectIterator();
        while (rectangles.Next(out var patch))
        {
            canvas.Save();
            canvas.ResetMatrix();
            canvas.ClipRect(patch);
            canvas.SetMatrix(matrix);
            var world = inverse.MapRect(patch);
            DrawLayer(canvas, document, layer, clippingSource, new(world.Left, world.Top, world.Right, world.Bottom),
                options.ForLayerRepair(), drawRivers: false);
            canvas.Restore();
        }
        return true;
    }

    /// <summary>A local edit of independent stamps can repair the old device raster in place.
    /// Group effects keep their existing evaluator. Dot overviews repair whole changed chunks.</summary>
    private static SKRect[]? LayerEditDamage(ObjectLayer layer, ObjectLayer? basis, RenderOptions options)
    {
        if (basis is null || ReferenceEquals(layer, basis) || options.ObjectOverrides is { Count: > 0 }
            || layer.Opacity != 1 || layer.Mask is { Enabled: true }
            || layer.Effects != new LayerEffects() || !layer.PlacementAreas.IsEmpty
            || !KeptLayerSupport.GetValue(layer, static l => new(l)).Repairable
            || !KeptLayerSupport.GetValue(basis, static l => new(l)).Repairable)
            return null;
        var overview = options.LodTier == RenderLodTier.Overview;
        if (overview)
        {
            var side = Math.Max(KeptLayerSupport.GetValue(layer, static l => new(l)).MaximumSide,
                KeptLayerSupport.GetValue(basis, static l => new(l)).MaximumSide);
            var scale = Math.Pow(2, OverviewZoomBucket(options.ViewScale) / 4.0);
            // Exact overview objects have their own background layer and dirty-chunk handoff.
            // Keep that path in charge when even one stamp is large enough to enter it.
            if (options.ObjectCache is null || side * scale >= OverviewExactScreenSize) return null;
        }
        // Both directions matter: Undo can restore the source revision rather than creating
        // a reverse delta. Coalesced input follows at most the existing 64-revision bridge.
        var changed = Forward(layer, basis) ?? Forward(basis, layer);
        if (changed is null) return null;
        return changed.Select(ToRect).ToArray();

        List<WorldBounds>? Forward(ObjectLayer target, ObjectLayer source)
        {
            var bounds = new List<WorldBounds>();
            for (var depth = 0; depth < 64 && ObjectLayerChanges.TryGet(target, out var delta); depth++)
            {
                if ((target with { Objects = delta.Source.Objects }) != delta.Source) return null;
                foreach (var index in delta.Removed) Add(delta.Source.Objects[index]);
                foreach (var index in delta.Added) Add(target.Objects[index]);
                if (ReferenceEquals(delta.Source, source)) return bounds;
                target = delta.Source;
            }
            return null;

            void Add(SceneObject item)
            {
                bounds.Add(SceneGeometry.BoundsOf(item));
                if (!overview) return;
                var x = Math.Floor((item.X + item.Width / 2) / OverviewChunkSize);
                var y = Math.Floor((item.Y + item.Height / 2) / OverviewChunkSize);
                // A tinted stamp uses a painted chunk, whose conservative margin is shared
                // by old and new variants. The same bounds also cover the neutral dot case.
                var reach = AppearanceLimits.MaxShadowOffset + AppearanceLimits.MaxShadowBlur * 3;
                bounds.Add(new WorldBounds(x * OverviewChunkSize - reach, y * OverviewChunkSize - reach,
                    (x + 1) * OverviewChunkSize + reach, (y + 1) * OverviewChunkSize + reach));
            }
        }
    }
}
