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
            SceneObjectKind.Ellipse =>
                ((localX * localX) / (halfW * halfW)) + ((localY * localY) / (halfH * halfH)) <= 1.0,
            _ => Math.Abs(localX) <= halfW && Math.Abs(localY) <= halfH,
        };
    }

    /// <summary>
    /// Topmost visible object under the point, or null. Hidden objects are not pickable; locked
    /// objects ARE pickable (the user must be able to see why nothing moves) but not editable.
    /// </summary>
    public static SceneObject? HitTest(MapDocument document, double worldX, double worldY)
    {
        for (var l = document.Layers.Count - 1; l >= 0; l--)
        {
            var layer = document.Layers[l];
            if (!layer.Visible)
            {
                continue;
            }

            for (var o = layer.Objects.Count - 1; o >= 0; o--)
            {
                var candidate = layer.Objects[o];
                if (candidate.Visible && Contains(candidate, worldX, worldY))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
