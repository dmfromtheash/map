namespace Map.Core.Documents;

/// <summary>Pure geometry over scene objects. No rendering here - just math the whole app shares.</summary>
public static class SceneGeometry
{
    /// <summary>Exact containment test in world space, honouring rotation around the centre.</summary>
    public static bool Contains(SceneObject sceneObject, double worldX, double worldY)
    {
        var centerX = sceneObject.X + (sceneObject.Width / 2);
        var centerY = sceneObject.Y + (sceneObject.Height / 2);

        // Un-rotate the point into the object's local axis-aligned space.
        var radians = -sceneObject.RotationDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        var dx = worldX - centerX;
        var dy = worldY - centerY;
        var localX = (dx * cos) - (dy * sin);
        var localY = (dx * sin) + (dy * cos);

        var halfW = sceneObject.Width / 2;
        var halfH = sceneObject.Height / 2;

        return sceneObject.Kind switch
        {
            SceneObjectKind.Line => VectorGeometry.ContainsLine(sceneObject, worldX, worldY),
            SceneObjectKind.Shape => VectorGeometry.ContainsShape(sceneObject, worldX, worldY),
            SceneObjectKind.Ellipse =>
                ((localX * localX) / (halfW * halfW)) + ((localY * localY) / (halfH * halfH)) <= 1.0,
            _ => Math.Abs(localX) <= halfW && Math.Abs(localY) <= halfH,
        };
    }

    /// <summary>
    /// World-space bounding box including rotation and everything that can spill past the shape:
    /// stroke half-width, blur and drop shadow of a stamp. Culling that ignored the overhang used
    /// to clip strokes at the viewport edge (Stage 1 Fix 01), so the padding stays here, in the
    /// one place both the renderer and the spatial index read it from.
    /// </summary>
    public static WorldBounds BoundsOf(SceneObject sceneObject)
    {
        var halfW = sceneObject.Width / 2;
        var halfH = sceneObject.Height / 2;
        var centerX = sceneObject.X + halfW;
        var centerY = sceneObject.Y + halfH;

        var radians = sceneObject.RotationDegrees * Math.PI / 180.0;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));

        var extentX = (halfW * cos) + (halfH * sin);
        var extentY = (halfW * sin) + (halfH * cos);

        var pad = sceneObject.StrokeWidth / 2;
        var shadowX = 0.0;
        var shadowY = 0.0;
        if (sceneObject.Stamp is { } stamp)
        {
            pad += stamp.Blur * 3;
            if (stamp.ShadowOpacity > 0)
            {
                shadowX = Math.Abs(stamp.ShadowOffsetX) + (stamp.ShadowBlur * 3);
                shadowY = Math.Abs(stamp.ShadowOffsetY) + (stamp.ShadowBlur * 3);
            }
        }
        else if (sceneObject.Line?.Stroke is { } lineStroke)
        {
            pad += (lineStroke.Width / 2) + Math.Abs(lineStroke.Offset) + (lineStroke.Blur * 3);
            if (lineStroke.ShadowOpacity > 0)
            {
                shadowX = Math.Abs(lineStroke.ShadowOffsetX) + (lineStroke.ShadowBlur * 3);
                shadowY = Math.Abs(lineStroke.ShadowOffsetY) + (lineStroke.ShadowBlur * 3);
            }
        }
        else if (sceneObject.Shape?.Stroke is { } shapeStroke)
        {
            pad += (shapeStroke.Width / 2) + Math.Abs(shapeStroke.Offset) + (shapeStroke.Blur * 3);
            if (shapeStroke.ShadowOpacity > 0)
            {
                shadowX = Math.Abs(shapeStroke.ShadowOffsetX) + (shapeStroke.ShadowBlur * 3);
                shadowY = Math.Abs(shapeStroke.ShadowOffsetY) + (shapeStroke.ShadowBlur * 3);
            }
        }
        else if (sceneObject.Text is { } text)
        {
            pad += text.Blur * 3;
            if (text.ShadowOpacity > 0)
            {
                shadowX = Math.Abs(text.ShadowOffsetX) + (text.ShadowBlur * 3);
                shadowY = Math.Abs(text.ShadowOffsetY) + (text.ShadowBlur * 3);
            }
        }

        return new WorldBounds(
            centerX - extentX - pad - shadowX,
            centerY - extentY - pad - shadowY,
            centerX + extentX + pad + shadowX,
            centerY + extentY + pad + shadowY);
    }

    /// <summary>
    /// Objects of one layer in draw order: sublayer band first (low draws below), list order
    /// inside a band. Stable, so an edit that does not touch the sublayer keeps the z-order.
    /// </summary>
    public static IReadOnlyList<SceneObject> InDrawOrder(ObjectLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return layer.Objects.All(o => o.Sublayer == 0)
            ? layer.Objects
            : [.. layer.Objects.OrderBy(o => o.Sublayer)];
    }

    /// <summary>
    /// Topmost visible object under the point, or null. Hidden objects are not pickable; locked
    /// objects ARE pickable (the user must be able to see why nothing moves) but not editable.
    /// Only visible object layers take part.
    /// </summary>
    public static SceneObject? HitTest(MapDocument document, double worldX, double worldY)
    {
        ArgumentNullException.ThrowIfNull(document);

        // The canvas surround is not document space. Content intentionally retained by a crop
        // may live there, but it must not be selectable until a later expand makes it visible.
        if (worldX < 0 || worldY < 0
            || worldX >= document.Canvas.Width || worldY >= document.Canvas.Height)
        {
            return null;
        }

        for (var l = document.Layers.Count - 1; l >= 0; l--)
        {
            if (document.Layers[l] is not ObjectLayer layer || !layer.Visible)
            {
                continue;
            }

            // Broad phase returns source-list order. The topmost exact hit is the greatest
            // sublayer, with the latest source position winning ties: exactly InDrawOrder(...)
            // followed by reverse traversal, without sorting or scanning the whole layer.
            SceneObject? best = null;
            var bestSublayer = int.MinValue;
            foreach (var candidate in ObjectSpatialIndex.For(layer).QueryPoint(worldX, worldY))
            {
                if (candidate.Visible
                    && candidate.Sublayer >= bestSublayer
                    && Contains(candidate, worldX, worldY))
                {
                    best = candidate;
                    bestSublayer = candidate.Sublayer;
                }
            }

            if (best is not null)
            {
                return best;
            }
        }

        return null;
    }

    /// <summary>Every object of the document that shares the group id, across layers.</summary>
    public static IReadOnlyList<SceneObject> GroupMembers(MapDocument document, Guid groupId) =>
        DocumentRevisionIndex.For(document).GroupMembers(groupId);
}

/// <summary>Axis-aligned world rectangle used by culling and the spatial index.</summary>
public readonly record struct WorldBounds(double Left, double Top, double Right, double Bottom)
{
    public bool Intersects(WorldBounds other) =>
        Left <= other.Right && Right >= other.Left && Top <= other.Bottom && Bottom >= other.Top;
}
