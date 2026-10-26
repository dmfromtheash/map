using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// R07b: a label that names a region sits inside the region's figure - a closed shape or a
/// placement area - along the figure's widest axis, away from its edge, and follows every edit of
/// the outline. Runs after every edit in <see cref="EditingSession.Execute"/>, like
/// <see cref="TextOnPath.Follow"/>; a map without region labels pays one scan of its objects.
/// </summary>
public static class RegionLabels
{
    /// <summary>Where a region's name goes: the middle, the turn of the text and the room along it.</summary>
    public readonly record struct Placement(VectorPoint Centre, double AngleDegrees, double Span);

    /// <summary>The steepest a region name is turned; a longer slant reads badly.</summary>
    public const double MaxAngle = 40;

    public static bool HasRegionLabels(MapDocument document) =>
        document.Layers.OfType<ObjectLayer>().Any(l => l.Objects.Any(o => o.Text?.Region is not null));

    /// <summary>The closed outline of the figure a label names, in world coordinates; null when it is gone.</summary>
    public static IReadOnlyList<VectorPoint>? Outline(MapDocument document, TextRegionBinding binding)
    {
        if (binding.Target == TextRegionTarget.PlacementArea)
        {
            foreach (var layer in document.Layers.OfType<ObjectLayer>())
            foreach (var area in layer.PlacementAreas)
                if (area.Id == binding.TargetId)
                    return area.Nodes.Length >= 3 ? [.. area.Nodes.Select(n => new VectorPoint(n.X, n.Y))] : null;
            return null;
        }

        if (DocumentRevisionIndex.For(document).FindObject(new ObjectId(binding.TargetId)) is not { Shape: { } shape } owner) return null;
        var region = shape.Regions[0];
        if (region.Primitive == ShapePrimitiveKind.Ellipse)
        {
            // An ellipse fills its object's box; turned with the object.
            var points = new List<VectorPoint>(48);
            var radians = owner.RotationDegrees * Math.PI / 180;
            var (cx, cy) = (owner.X + (owner.Width / 2), owner.Y + (owner.Height / 2));
            for (var i = 0; i < 48; i++)
            {
                var t = i * Math.PI * 2 / 48;
                var (x, y) = (Math.Cos(t) * owner.Width / 2, Math.Sin(t) * owner.Height / 2);
                points.Add(new VectorPoint(cx + (x * Math.Cos(radians)) - (y * Math.Sin(radians)), cy + (x * Math.Sin(radians)) + (y * Math.Cos(radians))));
            }
            return points;
        }

        var outline = VectorGeometry.Flatten(owner, region.Nodes, closed: true);
        return outline.Count >= 3 ? outline : null;
    }

    /// <summary>
    /// The widest axis of a figure (principal axis of its evenly resampled outline, kept within
    /// ±<see cref="MaxAngle"/>; level for a roundish figure) and, across it, the longest stretch
    /// inside the figure near its middle: the label's centre, turn and room.
    /// </summary>
    public static Placement? Place(IReadOnlyList<VectorPoint> polygon)
    {
        if (polygon.Count < 3) return null;
        var samples = Resample(polygon, 128);
        double mx = 0, my = 0;
        foreach (var p in samples) { mx += p.X; my += p.Y; }
        mx /= samples.Count; my /= samples.Count;
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var p in samples)
        {
            var (dx, dy) = (p.X - mx, p.Y - my);
            sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
        }

        var angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        var spread = Math.Sqrt(((sxx - syy) * (sxx - syy)) + (4 * sxy * sxy));
        var major = (sxx + syy + spread) / 2;
        var minor = Math.Max(1e-9, (sxx + syy - spread) / 2);
        var degrees = angle * 180 / Math.PI;
        if (degrees > 90) degrees -= 180;
        if (degrees < -90) degrees += 180;
        // A roundish figure reads best with a level name; a long one follows its length.
        if (major / minor < 1.6 || !double.IsFinite(degrees)) degrees = 0;
        degrees = Math.Clamp(degrees, -MaxAngle, MaxAngle);

        // Into the label's frame: the axis becomes horizontal.
        var radians = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
        var local = polygon.Select(p => new VectorPoint(((p.X - mx) * cos) + ((p.Y - my) * sin), (-(p.X - mx) * sin) + ((p.Y - my) * cos))).ToArray();
        var top = local.Min(p => p.Y);
        var bottom = local.Max(p => p.Y);
        if (bottom - top < 1e-6) return null;
        var middle = (top + bottom) / 2;
        var half = (bottom - top) / 2;

        var best = (Score: double.NegativeInfinity, X: 0.0, Y: 0.0, Span: 0.0);
        const int Lines = 41;
        for (var line = 1; line < Lines; line++)
        {
            var y = top + ((bottom - top) * line / Lines);
            var crossings = new List<double>();
            for (var i = 0; i < local.Length; i++)
            {
                var a = local[i];
                var b = local[(i + 1) % local.Length];
                if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))
                    crossings.Add(a.X + ((y - a.Y) / (b.Y - a.Y) * (b.X - a.X)));
            }

            crossings.Sort();
            for (var i = 0; i + 1 < crossings.Count; i += 2)
            {
                var span = crossings[i + 1] - crossings[i];
                // The longest stretch wins, but one near the middle of the figure is preferred.
                var score = span * (1 - (0.5 * Math.Abs(y - middle) / half));
                if (score > best.Score) best = (score, (crossings[i] + crossings[i + 1]) / 2, y, span);
            }
        }

        if (double.IsNegativeInfinity(best.Score)) return null;
        var centre = new VectorPoint(mx + (best.X * cos) - (best.Y * sin), my + (best.X * sin) + (best.Y * cos));
        return new Placement(centre, Math.Round(degrees, 2), best.Span);
    }

    /// <summary>The label set inside its figure (with the user's shift) and named after its card; the same instance when nothing changed.</summary>
    public static SceneObject Layout(MapDocument document, SceneObject label)
    {
        if (label.Text is not { Region: { } binding } text) return label;
        var content = binding.EntityId is { } entityId && document.Entities.FirstOrDefault(e => e.Id == entityId) is { } entity ? entity.Name : text.Content;
        var outline = Outline(document, binding);
        var placement = outline is null ? null : Place(outline);
        if (placement is not { } place)
        {
            if (binding.Detached && content == text.Content) return label;
            return label with { Text = text with { Content = content, Region = binding with { Detached = true } } };
        }

        var (width, height) = Size(text with { Content = content });
        var x = Round(place.Centre.X + binding.OffsetX - (width / 2));
        var y = Round(place.Centre.Y + binding.OffsetY - (height / 2));
        if (!binding.Detached && content == text.Content && Near(label.X, x) && Near(label.Y, y)
            && Near(label.Width, width) && Near(label.Height, height) && Near(label.RotationDegrees, place.AngleDegrees)) return label;
        return new SceneObject(label.Id, label.Kind, label.Name, x, y, width, height, place.AngleDegrees,
            label.FillColor, label.StrokeColor, label.StrokeWidth, label.Visible, label.Locked)
        {
            Sublayer = label.Sublayer,
            GroupId = label.GroupId,
            GroupName = label.GroupName,
            FillGradient = label.FillGradient,
            ObjectOpacity = label.ObjectOpacity,
            GenerationReason = label.GenerationReason,
            GeographyIntentional = label.GeographyIntentional,
            Text = text with { Content = content, Region = binding with { Detached = false } },
        }.KeepingMeaningOf(label);
    }

    /// <summary>The box a label needs: its widest line and its lines, from the font size and spacing.</summary>
    public static (double Width, double Height) Size(TextStyle text)
    {
        var lines = (text.Content.Length == 0 ? " " : text.Content).Split('\n');
        var longest = lines.Max(l => l.Length);
        var width = Math.Max(text.FontSize, (longest * text.FontSize * 0.62) + (Math.Max(0, longest - 1) * text.LetterSpacing)) + (text.OutlineWidth * 2) + 8;
        var height = (lines.Length * text.FontSize * Math.Max(1, text.LineHeight)) + (text.OutlineWidth * 2) + 6;
        return (Round(width), Round(height));
    }

    /// <summary>
    /// The edited document with every region label laid out again. A label the user moved keeps
    /// the move as its offset from the figure's middle, so the next outline edit carries it along.
    /// </summary>
    public static MapDocument Follow(MapDocument before, MapDocument after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (ReferenceEquals(before, after) || !HasRegionLabels(after)) return after;
        Dictionary<ObjectId, SceneObject>? previous = null;
        List<MapLayer>? layers = null;
        for (var index = 0; index < after.Layers.Count; index++)
        {
            if (after.Layers[index] is not ObjectLayer layer) continue;
            SceneObject[]? rewritten = null;
            for (var i = 0; i < layer.Objects.Count; i++)
            {
                var item = layer.Objects[i];
                if (item.Text?.Region is not { } binding) continue;
                previous ??= before.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Where(o => o.Text?.Region is not null).ToDictionary(o => o.Id);
                var source = item;
                if (previous.TryGetValue(item.Id, out var was) && !ReferenceEquals(was, item) && was.Text?.Region is { } wasBinding
                    && wasBinding.TargetId == binding.TargetId && wasBinding.OffsetX == binding.OffsetX && wasBinding.OffsetY == binding.OffsetY
                    && (!Near(was.X, item.X) || !Near(was.Y, item.Y)) && Near(was.Width, item.Width) && Near(was.Height, item.Height))
                {
                    // Moved by hand: the move becomes the label's own shift.
                    source = item with { Text = item.Text with { Region = binding with { OffsetX = Round(binding.OffsetX + item.X - was.X), OffsetY = Round(binding.OffsetY + item.Y - was.Y) } } };
                }

                var laid = Layout(after, source);
                if (ReferenceEquals(laid, item)) continue;
                rewritten ??= [.. layer.Objects];
                rewritten[i] = laid;
            }

            if (rewritten is null) continue;
            layers ??= [.. after.Layers];
            layers[index] = layer with { Objects = rewritten };
        }

        return layers is null ? after : after.WithLayers(layers);
    }

    private static List<VectorPoint> Resample(IReadOnlyList<VectorPoint> polygon, int count)
    {
        var lengths = new double[polygon.Count];
        var total = 0.0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var (a, b) = (polygon[i], polygon[(i + 1) % polygon.Count]);
            lengths[i] = Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
            total += lengths[i];
        }

        if (total < 1e-9) return [.. polygon];
        var result = new List<VectorPoint>(count);
        var step = total / count;
        var (segment, into) = (0, 0.0);
        for (var k = 0; k < count; k++)
        {
            var target = k * step;
            while (segment < polygon.Count - 1 && into + lengths[segment] < target) { into += lengths[segment]; segment++; }
            var t = lengths[segment] < 1e-9 ? 0 : (target - into) / lengths[segment];
            var (a, b) = (polygon[segment], polygon[(segment + 1) % polygon.Count]);
            result.Add(new VectorPoint(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t)));
        }

        return result;
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < .01;

    private static double Round(double value) => Math.Round(value, 2);
}

/// <summary>
/// An edit plus the region labels it made follow (<see cref="RegionLabels.Follow"/>): redo applies
/// the edit and puts the laid-out labels back; undo restores them as the edit left them and then
/// reverts the edit.
/// </summary>
public sealed class RegionLabelFollowCommand : IEditCommand
{
    private readonly IEditCommand _inner;
    private readonly IReadOnlyList<(LayerId Layer, SceneObject Before, SceneObject After)> _labels;

    public RegionLabelFollowCommand(IEditCommand inner, MapDocument edited, MapDocument followed)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        var labels = new List<(LayerId, SceneObject, SceneObject)>();
        foreach (var layer in followed.Layers.OfType<ObjectLayer>())
        {
            if (edited.FindLayer(layer.Id) is not ObjectLayer previous || ReferenceEquals(previous.Objects, layer.Objects)) continue;
            var before = previous.Objects.Where(o => o.Text?.Region is not null).ToDictionary(o => o.Id);
            foreach (var item in layer.Objects)
                if (item.Text?.Region is not null && before.TryGetValue(item.Id, out var was) && !ReferenceEquals(was, item))
                    labels.Add((layer.Id, was, item));
        }

        _labels = labels;
    }

    public string Name => _inner.Name;
    public long? MeasuredWorkingBytes => _inner.MeasuredWorkingBytes;
    public MapDocument Apply(MapDocument document) => Replace(_inner.Apply(document), after: true);
    public MapDocument Revert(MapDocument document) => _inner.Revert(Replace(document, after: false));

    private MapDocument Replace(MapDocument document, bool after)
    {
        var result = document;
        foreach (var group in _labels.GroupBy(label => label.Layer))
        {
            var map = group.ToDictionary(label => label.Before.Id, label => after ? label.After : label.Before);
            result = result.WithLayer(group.Key, layer => layer is ObjectLayer objects
                ? objects with { Objects = [.. objects.Objects.Select(item => map.TryGetValue(item.Id, out var swap) ? swap : item)] }
                : layer);
        }

        return result;
    }
}

/// <summary>Replaces the label settings of the map (R07b) as one undo step.</summary>
public sealed class SetLabelSettingsCommand(LabelSettings before, LabelSettings after, string name = "Подписи по масштабу") : IEditCommand
{
    public string Name { get; } = name;

    public MapDocument Apply(MapDocument document)
    {
        after.Validate();
        return document with { Labels = after };
    }

    public MapDocument Revert(MapDocument document) => document with { Labels = before };
}
