using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// Pure geometry of a multi-object selection. The shared frame is the axis-aligned union of the
/// members' world bounds; scaling is uniform around the frame centre so every member's centre and
/// size change proportionally; rotation turns member centres around the frame centre and adds the
/// same delta to each member's own angle. Nothing here touches the document.
/// </summary>
public static class MultiSelectionGeometry
{
    /// <summary>Union of the members' world bounds, or null for an empty list.</summary>
    public static WorldBounds? CombinedBounds(IReadOnlyList<SceneObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        WorldBounds? union = null;
        foreach (var sceneObject in objects)
        {
            var bounds = SceneGeometry.BoundsOf(sceneObject);
            union = union is { } existing
                ? new WorldBounds(
                    Math.Min(existing.Left, bounds.Left),
                    Math.Min(existing.Top, bounds.Top),
                    Math.Max(existing.Right, bounds.Right),
                    Math.Max(existing.Bottom, bounds.Bottom))
                : bounds;
        }

        return union;
    }

    /// <summary>Every member moved by the same world delta.</summary>
    public static IReadOnlyList<SceneObject> Translate(
        IReadOnlyList<SceneObject> objects, double deltaX, double deltaY)
    {
        ArgumentNullException.ThrowIfNull(objects);
        return [.. objects.Select(o => WithGeometry(
            o, o.X + deltaX, o.Y + deltaY, o.Width, o.Height, o.RotationDegrees))];
    }

    /// <summary>
    /// Uniform scale around the shared centre: each member's centre moves proportionally and its
    /// width/height scale by the same factor, keeping its own rotation.
    /// </summary>
    public static IReadOnlyList<SceneObject> ScaleAroundCentre(
        IReadOnlyList<SceneObject> objects, double factor, double centreX, double centreY)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        return [.. objects.Select(o =>
        {
            var cx = centreX + (factor * (o.X + (o.Width / 2) - centreX));
            var cy = centreY + (factor * (o.Y + (o.Height / 2) - centreY));
            var width = o.Width * factor;
            var height = o.Height * factor;
            return WithGeometry(o, cx - (width / 2), cy - (height / 2), width, height, o.RotationDegrees);
        })];
    }

    /// <summary>
    /// The largest lower bound on a scale factor that keeps every member at or above the shared
    /// minimum size, so a shrink can never collapse the smallest object.
    /// </summary>
    public static double ClampScaleFactor(IReadOnlyList<SceneObject> objects, double factor)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var smallest = objects.Count == 0
            ? double.MaxValue
            : objects.Min(o => Math.Min(o.Width, o.Height));
        if (smallest <= 0 || double.IsInfinity(smallest) || smallest == double.MaxValue)
        {
            return factor;
        }

        return Math.Max(factor, SelectionHandles.MinSize / smallest);
    }

    /// <summary>
    /// Rotates member centres around the shared centre (clockwise on screen, Y down) and adds the
    /// same delta to each member's own rotation, normalized to (-180, 180].
    /// </summary>
    public static IReadOnlyList<SceneObject> RotateAroundCentre(
        IReadOnlyList<SceneObject> objects, double deltaDegrees, double centreX, double centreY)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var radians = deltaDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return [.. objects.Select(o =>
        {
            var dx = o.X + (o.Width / 2) - centreX;
            var dy = o.Y + (o.Height / 2) - centreY;
            var cx = centreX + (dx * cos) - (dy * sin);
            var cy = centreY + (dx * sin) + (dy * cos);
            return WithGeometry(
                o, cx - (o.Width / 2), cy - (o.Height / 2), o.Width, o.Height,
                NormalizeAngle(o.RotationDegrees + deltaDegrees));
        })];
    }

    // ---------------------------------------------------------------- shared frame

    /// <summary>
    /// World positions of the shared frame's nine handles. The frame is axis-aligned, so unlike
    /// the single-object gizmo no rotation is applied; the rotate handle sits above the top edge.
    /// </summary>
    public static IReadOnlyList<(HandleKind Kind, double X, double Y)> ComputeFrameHandles(
        WorldBounds frame, double rotationHandleOffset)
    {
        var centreX = (frame.Left + frame.Right) / 2;
        var centreY = (frame.Top + frame.Bottom) / 2;
        return
        [
            (HandleKind.TopLeft, frame.Left, frame.Top),
            (HandleKind.Top, centreX, frame.Top),
            (HandleKind.TopRight, frame.Right, frame.Top),
            (HandleKind.Right, frame.Right, centreY),
            (HandleKind.BottomRight, frame.Right, frame.Bottom),
            (HandleKind.Bottom, centreX, frame.Bottom),
            (HandleKind.BottomLeft, frame.Left, frame.Bottom),
            (HandleKind.Left, frame.Left, centreY),
            (HandleKind.Rotate, centreX, frame.Top - rotationHandleOffset),
        ];
    }

    /// <summary>The frame handle within the grab radius of the world point, or null.</summary>
    public static HandleKind? HitTestFrame(
        WorldBounds frame, double worldX, double worldY, double radius, double rotationHandleOffset)
    {
        foreach (var (kind, x, y) in ComputeFrameHandles(frame, rotationHandleOffset))
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
    /// The uniform factor a resize drag proposes: the cursor's distance from the frame centre
    /// divided by the grabbed handle's starting distance. Centre-relative by specification.
    /// </summary>
    public static double ResizeFactor(WorldBounds frame, HandleKind handle, double worldX, double worldY)
    {
        if (handle == HandleKind.Rotate)
        {
            throw new ArgumentException("The rotation handle does not resize.", nameof(handle));
        }

        var centreX = (frame.Left + frame.Right) / 2;
        var centreY = (frame.Top + frame.Bottom) / 2;
        var (_, startX, startY) = ComputeFrameHandles(frame, 0).First(h => h.Kind == handle);
        var startDistance = Math.Sqrt(
            Math.Pow(startX - centreX, 2) + Math.Pow(startY - centreY, 2));
        if (startDistance <= 0)
        {
            return 1.0;
        }

        var cursorDistance = Math.Sqrt(
            Math.Pow(worldX - centreX, 2) + Math.Pow(worldY - centreY, 2));
        return Math.Max(cursorDistance / startDistance, 1e-6);
    }

    // ---------------------------------------------------------------- marquee

    /// <summary>Normalized marquee rectangle from any two drag corners.</summary>
    public static WorldBounds MarqueeBounds(double x1, double y1, double x2, double y2) => new(
        Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

    /// <summary>
    /// Objects the marquee catches, in document order: visible and unlocked objects on visible,
    /// unlocked object layers whose world bounds intersect the rectangle. Hidden and locked
    /// content stays reachable through the object tree, never through the frame.
    /// </summary>
    public static IReadOnlyList<SceneObject> ObjectsInMarquee(MapDocument document, WorldBounds marquee)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new List<SceneObject>();
        foreach (var layer in document.Layers.OfType<ObjectLayer>())
        {
            if (!layer.Visible || layer.Locked)
            {
                continue;
            }

            foreach (var sceneObject in ObjectSpatialIndex.For(layer).Query(marquee))
            {
                if (sceneObject.Visible && !sceneObject.Locked
                    && layer.StateFor(sceneObject.Sublayer) is { Visible: true, Locked: false }
                    && SceneGeometry.BoundsOf(sceneObject).Intersects(marquee))
                {
                    result.Add(sceneObject);
                }
            }
        }

        return result;
    }

    // ---------------------------------------------------------------- helpers

    private static double NormalizeAngle(double angle)
    {
        var normalized = angle % 360;
        if (normalized <= -180)
        {
            normalized += 360;
        }

        if (normalized > 180)
        {
            normalized -= 360;
        }

        return normalized;
    }

    private static SceneObject WithGeometry(
        SceneObject source, double x, double y, double width, double height, double rotation) =>
        new(
            source.Id, source.Kind, source.Name,
            x, y, width, height, rotation,
            source.FillColor, source.StrokeColor, source.StrokeWidth,
            source.Visible, source.Locked)
        {
            Sublayer = source.Sublayer,
            GroupId = source.GroupId,
            GroupName = source.GroupName,
            Stamp = source.Stamp,
            Line = source.Line,
            Shape = source.Shape,
            FillGradient = source.FillGradient,
            Text = source.Text,
            Composition = source.Composition,
            Decoration = source.Decoration,
            PlaceholderReason = source.PlaceholderReason,
            OpaquePayload = source.OpaquePayload,
            ObjectOpacity = source.ObjectOpacity,
        };
}
