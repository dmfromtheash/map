using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>Which manipulator of the selection gizmo a drag grabbed.</summary>
public enum HandleKind
{
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
    Rotate,
}

/// <summary>
/// Pure math of the on-canvas selection gizmo: where the eight resize handles and the rotation
/// handle sit in world space, and what object a drag of each handle proposes. Everything honours
/// the object's rotation; every resize keeps the opposite (anchor) side pinned in place.
/// </summary>
public static class SelectionHandles
{
    /// <summary>Smallest width/height a handle drag can produce.</summary>
    public const double MinSize = 8.0;

    /// <summary>World positions of all nine handles for the given rotation-handle offset.</summary>
    public static IReadOnlyList<(HandleKind Kind, double X, double Y)> Compute(
        SceneObject sceneObject, double rotationHandleOffset)
    {
        var halfW = sceneObject.Width / 2;
        var halfH = sceneObject.Height / 2;

        (HandleKind Kind, double LX, double LY)[] local =
        [
            (HandleKind.TopLeft, -halfW, -halfH),
            (HandleKind.Top, 0, -halfH),
            (HandleKind.TopRight, halfW, -halfH),
            (HandleKind.Right, halfW, 0),
            (HandleKind.BottomRight, halfW, halfH),
            (HandleKind.Bottom, 0, halfH),
            (HandleKind.BottomLeft, -halfW, halfH),
            (HandleKind.Left, -halfW, 0),
            (HandleKind.Rotate, 0, -halfH - rotationHandleOffset),
        ];

        var result = new (HandleKind, double, double)[local.Length];
        for (var i = 0; i < local.Length; i++)
        {
            var world = LocalToWorld(sceneObject, local[i].LX, local[i].LY);
            result[i] = (local[i].Kind, world.X, world.Y);
        }

        return result;
    }

    /// <summary>The handle within <paramref name="radius"/> of the world point, or null.</summary>
    public static HandleKind? HitTest(
        SceneObject sceneObject, double worldX, double worldY, double radius, double rotationHandleOffset)
    {
        foreach (var (kind, x, y) in Compute(sceneObject, rotationHandleOffset))
        {
            var dx = worldX - x;
            var dy = worldY - y;
            if ((dx * dx) + (dy * dy) <= radius * radius)
            {
                return kind;
            }
        }

        return null;
    }

    /// <summary>
    /// The object a resize drag proposes: the cursor carries the grabbed side/corner, the opposite
    /// side/corner stays pinned, rotation is unchanged. Works in the object's rotated frame.
    /// </summary>
    public static SceneObject Resize(SceneObject before, HandleKind handle, double worldX, double worldY)
    {
        if (handle == HandleKind.Rotate)
        {
            throw new ArgumentException("The rotation handle does not resize.", nameof(handle));
        }

        var halfW = before.Width / 2;
        var halfH = before.Height / 2;
        var cursor = WorldToLocal(before, worldX, worldY);

        // Which edge each axis of the handle moves: +1 for the right/bottom edge, -1 for the
        // left/top edge, 0 when the axis does not move. The anchor is the opposite edge, and the
        // signed extent clamps at MinSize - dragging past the anchor never inverts the object.
        var signX = handle switch
        {
            HandleKind.TopRight or HandleKind.Right or HandleKind.BottomRight => 1,
            HandleKind.TopLeft or HandleKind.Left or HandleKind.BottomLeft => -1,
            _ => 0,
        };
        var signY = handle switch
        {
            HandleKind.BottomLeft or HandleKind.Bottom or HandleKind.BottomRight => 1,
            HandleKind.TopLeft or HandleKind.Top or HandleKind.TopRight => -1,
            _ => 0,
        };

        double newWidth = before.Width, centreLocalX = 0;
        if (signX != 0)
        {
            var anchorX = -signX * halfW;
            newWidth = Math.Max(MinSize, (cursor.X - anchorX) * signX);
            var movedX = anchorX + (signX * newWidth);
            centreLocalX = (anchorX + movedX) / 2;
        }

        double newHeight = before.Height, centreLocalY = 0;
        if (signY != 0)
        {
            var anchorY = -signY * halfH;
            newHeight = Math.Max(MinSize, (cursor.Y - anchorY) * signY);
            var movedY = anchorY + (signY * newHeight);
            centreLocalY = (anchorY + movedY) / 2;
        }

        // The new centre in world space keeps the anchor stationary under the unchanged rotation.
        var newCentre = LocalToWorld(before, centreLocalX, centreLocalY);

        return new SceneObject(
            before.Id, before.Kind, before.Name,
            newCentre.X - (newWidth / 2), newCentre.Y - (newHeight / 2),
            newWidth, newHeight, before.RotationDegrees,
            before.FillColor, before.StrokeColor, before.StrokeWidth,
            before.Visible, before.Locked)
        {
            Sublayer = before.Sublayer,
            GroupId = before.GroupId,
            GroupName = before.GroupName,
            Stamp = before.Stamp,
            Line = before.Line,
            Shape = before.Shape,
            FillGradient = before.FillGradient,
            Text = before.Text,
            Composition = before.Composition,
            PlaceholderReason = before.PlaceholderReason,
            OpaquePayload = before.OpaquePayload,
            ObjectOpacity = before.ObjectOpacity,
        }.KeepingMeaningOf(before);
    }

    /// <summary>
    /// The object a rotation drag proposes: the delta between the start and current cursor angles
    /// around the object's centre, added to the starting rotation. Position and size stay fixed.
    /// </summary>
    public static SceneObject Rotate(
        SceneObject before, double startWorldX, double startWorldY, double worldX, double worldY)
    {
        var centreX = before.X + (before.Width / 2);
        var centreY = before.Y + (before.Height / 2);

        var startAngle = Math.Atan2(startWorldY - centreY, startWorldX - centreX);
        var currentAngle = Math.Atan2(worldY - centreY, worldX - centreX);
        var deltaDegrees = (currentAngle - startAngle) * 180.0 / Math.PI;

        var rotation = (before.RotationDegrees + deltaDegrees) % 360.0;
        if (rotation > 180)
        {
            rotation -= 360;
        }
        else if (rotation <= -180)
        {
            rotation += 360;
        }

        return new SceneObject(
            before.Id, before.Kind, before.Name,
            before.X, before.Y, before.Width, before.Height,
            Math.Round(rotation, 2),
            before.FillColor, before.StrokeColor, before.StrokeWidth,
            before.Visible, before.Locked)
        {
            Sublayer = before.Sublayer,
            GroupId = before.GroupId,
            GroupName = before.GroupName,
            Stamp = before.Stamp,
            Line = before.Line,
            Shape = before.Shape,
            FillGradient = before.FillGradient,
            Text = before.Text,
            Composition = before.Composition,
            PlaceholderReason = before.PlaceholderReason,
            OpaquePayload = before.OpaquePayload,
            ObjectOpacity = before.ObjectOpacity,
        }.KeepingMeaningOf(before);
    }

    private static (double X, double Y) LocalToWorld(SceneObject o, double localX, double localY)
    {
        var centreX = o.X + (o.Width / 2);
        var centreY = o.Y + (o.Height / 2);
        var radians = o.RotationDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return (
            centreX + (localX * cos) - (localY * sin),
            centreY + (localX * sin) + (localY * cos));
    }

    private static (double X, double Y) WorldToLocal(SceneObject o, double worldX, double worldY)
    {
        var centreX = o.X + (o.Width / 2);
        var centreY = o.Y + (o.Height / 2);
        var radians = -o.RotationDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var dx = worldX - centreX;
        var dy = worldY - centreY;
        return ((dx * cos) - (dy * sin), (dx * sin) + (dy * cos));
    }
}
