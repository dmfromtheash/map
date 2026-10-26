using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// Labels that follow a river channel, a road branch or a hex path (R07, S2/H15). The label keeps
/// its binding; its baseline and bounds are derived here from the target's evaluated axis, so a
/// label follows every node the user moves and never reads upside down. <see cref="Follow"/> is
/// the local recomputation the editing session runs after each edit: only labels whose target or
/// whose own object changed are laid out again.
/// </summary>
public static class TextOnPath
{
    /// <summary>Rough advance of one glyph relative to the font size; the renderer measures the real text.</summary>
    public const double GlyphAdvance = 0.58;

    private readonly record struct Axis(VectorPoint[] Points, double[] HalfWidths, double[] Distances)
    {
        public double Length => Distances.Length == 0 ? 0 : Distances[^1];
    }

    /// <summary>One line of the label as it is drawn along a path (line breaks become spaces).</summary>
    public static string LineOf(TextStyle text) =>
        text.Content.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim();

    public static double EstimatedWidth(TextStyle text)
    {
        var line = LineOf(text);
        return Math.Max(1, line.Length * text.FontSize * GlyphAdvance + Math.Max(0, line.Length - 1) * text.LetterSpacing);
    }

    /// <summary>True when the binding's target (and part) exists in the document.</summary>
    public static bool TargetExists(MapDocument document, TextPathBinding binding) => TryAxis(document, binding, out _);

    /// <summary>
    /// Lays the label out along its target: new baseline, bounds, no rotation. A label whose target
    /// is gone keeps its last baseline and is marked detached. Returns the same instance when nothing changes.
    /// </summary>
    public static SceneObject Layout(MapDocument document, SceneObject label)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(label);
        if (label.Text?.Path is not { } binding) return label;
        if (!TryAxis(document, binding, out var axis))
        {
            return binding.Detached ? label : WithText(label, label.Text with { Path = binding with { Detached = true } });
        }

        var text = label.Text;
        var width = EstimatedWidth(text);
        var size = Math.Max(1, text.FontSize);
        var length = axis.Length;
        var window = Math.Min(length, width * 1.15 + size);
        var centre = Math.Clamp(binding.Position, 0, 1) * length;
        centre = Math.Clamp(centre, window / 2, length - window / 2);
        var start = centre - window / 2;
        var count = Math.Clamp((int)Math.Ceiling(window / Math.Max(1, size / 6)) + 1, 2, TextPathBinding.MaxBaselinePoints);
        var points = new VectorPoint[count];
        var halves = new double[count];
        var tangents = new VectorPoint[count];
        var smoothing = size * 0.6;
        for (var i = 0; i < count; i++)
        {
            var s = start + window * i / (count - 1);
            (points[i], halves[i]) = Sample(axis, s);
            var behind = Sample(axis, Math.Max(0, s - smoothing)).Point;
            var ahead = Sample(axis, Math.Min(length, s + smoothing)).Point;
            tangents[i] = Unit(behind, ahead, i > 0 ? tangents[i - 1] : new VectorPoint(1, 0));
        }

        var reverse = binding.KeepUpright
            ? ReadsBackwards(points[0], points[^1], tangents)
            : binding.Reverse;
        if (reverse)
        {
            Array.Reverse(points); Array.Reverse(halves); Array.Reverse(tangents);
            for (var i = 0; i < count; i++) tangents[i] = new VectorPoint(-tangents[i].X, -tangents[i].Y);
        }

        var baseline = new VectorPoint[count];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (var i = 0; i < count; i++)
        {
            // "Up" for the reader is the left-hand normal of the reading direction (y grows downwards).
            var up = new VectorPoint(tangents[i].Y, -tangents[i].X);
            var offset = binding.Side switch
            {
                TextPathSide.On => -size * 0.35,
                TextPathSide.Below => -(halves[i] + binding.Gap + size * 0.8),
                _ => halves[i] + binding.Gap,
            };
            var p = new VectorPoint(points[i].X + up.X * offset, points[i].Y + up.Y * offset);
            baseline[i] = p;
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }

        // Glyphs rise a font size above the baseline; text longer than a short line runs on along its ends.
        var pad = size * 1.2 + Math.Max(0, width - window) / 2;
        var x = minX - pad; var y = minY - pad;
        var w = Math.Max(1, maxX - minX + 2 * pad); var h = Math.Max(1, maxY - minY + 2 * pad);
        var local = ImmutableArray.CreateBuilder<VectorPoint>(count);
        foreach (var p in baseline) local.Add(new VectorPoint(Round(p.X - x), Round(p.Y - y)));
        x = Round(x); y = Round(y); w = Round(w); h = Round(h);
        var laid = binding with { Baseline = local.MoveToImmutable(), Detached = false };
        if (laid.Equals(binding) && label.X == x && label.Y == y && label.Width == w && label.Height == h
            && label.RotationDegrees == 0)
        {
            return label;
        }

        return Rebuild(label, x, y, w, h, text with { Path = laid });
    }

    /// <summary>
    /// The binding that puts the label's middle as close as possible to a world point on the
    /// nearest river channel, road branch or hex path within <paramref name="tolerance"/>; null if none is that close.
    /// </summary>
    public static TextPathBinding? BindNearest(MapDocument document, double worldX, double worldY, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(document);
        TextPathBinding? best = null;
        var bestDistance = tolerance;
        foreach (var candidate in Candidates(document))
        {
            if (!TryAxis(document, candidate, out var axis) || axis.Length <= 0) continue;
            var (distance, along) = Project(axis, worldX, worldY);
            if (distance > bestDistance) continue;
            bestDistance = distance;
            best = candidate with { Position = Math.Round(along / axis.Length, 4) };
        }

        return best;
    }

    /// <summary>The position (0..1) along the binding's target closest to a world point; null if the target is gone.</summary>
    public static double? PositionNear(MapDocument document, TextPathBinding binding, double worldX, double worldY)
    {
        if (!TryAxis(document, binding, out var axis) || axis.Length <= 0) return null;
        return Math.Round(Project(axis, worldX, worldY).Along / axis.Length, 4);
    }

    /// <summary>True when the label currently reads against its line's own direction (end to start).</summary>
    public static bool ReadsAgainstLine(MapDocument document, SceneObject label)
    {
        if (label.Text?.Path is not { Baseline.Length: >= 2 } binding || !TryAxis(document, binding, out var axis) || axis.Length <= 0)
            return false;
        var at = Math.Clamp(binding.Position, 0, 1) * axis.Length;
        var a = Sample(axis, Math.Max(0, at - 1)).Point; var b = Sample(axis, Math.Min(axis.Length, at + 1)).Point;
        var first = binding.Baseline[0]; var last = binding.Baseline[^1];
        return (b.X - a.X) * (last.X - first.X) + (b.Y - a.Y) * (last.Y - first.Y) < 0;
    }

    /// <summary>World centre of the drawn baseline of a bound label.</summary>
    public static VectorPoint BaselineCentre(SceneObject label)
    {
        var line = label.Text?.Path?.Baseline ?? [];
        if (line.IsEmpty) return new VectorPoint(label.X + label.Width / 2, label.Y + label.Height / 2);
        var mid = line[line.Length / 2];
        return new VectorPoint(label.X + mid.X, label.Y + mid.Y);
    }

    /// <summary>
    /// Local recomputation after an edit: lays out again every bound label whose target changed
    /// between <paramref name="before"/> and <paramref name="after"/>, and slides along its line every
    /// bound label the edit itself moved. Returns <paramref name="after"/> when no label needs it.
    /// </summary>
    public static MapDocument Follow(MapDocument before, MapDocument after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (ReferenceEquals(before, after)) return after;
        var changedTargets = ChangedTargets(before, after);
        Dictionary<ObjectId, SceneObject>? previousLabels = null;
        List<MapLayer>? layers = null;
        for (var index = 0; index < after.Layers.Count; index++)
        {
            if (after.Layers[index] is not ObjectLayer layer) continue;
            var previousLayer = before.FindLayer(layer.Id) as ObjectLayer;
            var objectsChanged = previousLayer is null || !ReferenceEquals(previousLayer.Objects, layer.Objects);
            if (changedTargets.Count == 0 && !objectsChanged) continue;
            SceneObject[]? rewritten = null;
            for (var i = 0; i < layer.Objects.Count; i++)
            {
                var item = layer.Objects[i];
                if (item.Text?.Path is not { } binding) continue;
                SceneObject? previous = null;
                if (objectsChanged)
                {
                    previousLabels ??= BoundLabels(before);
                    previousLabels.TryGetValue(item.Id, out previous);
                }

                SceneObject laid;
                if (changedTargets.Contains(binding.TargetId))
                {
                    laid = Layout(after, item);
                }
                else if (previous is not null && !ReferenceEquals(previous, item)
                    && previous.Text?.Path is { } previousBinding
                    && (previous.X != item.X || previous.Y != item.Y) && previousBinding.Equals(binding))
                {
                    // Moved as a whole: slide the label along its line to where the user dropped it.
                    var centre = BaselineCentre(item);
                    var position = PositionNear(after, binding, centre.X, centre.Y);
                    laid = position is null ? item : Layout(after, WithText(item, item.Text with { Path = binding with { Position = position.Value } }));
                }
                else if (previous is null || !ReferenceEquals(previous, item))
                {
                    laid = Layout(after, item);
                }
                else
                {
                    continue;
                }

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

    private static Dictionary<ObjectId, SceneObject> BoundLabels(MapDocument document)
    {
        var result = new Dictionary<ObjectId, SceneObject>();
        foreach (var layer in document.Layers.OfType<ObjectLayer>())
        foreach (var item in layer.Objects)
        {
            if (item.Text?.Path is not null) result[item.Id] = item;
        }

        return result;
    }

    private static HashSet<Guid> ChangedTargets(MapDocument before, MapDocument after)
    {
        var changed = new HashSet<Guid>();
        void Compare<T>(IEnumerable<T> previous, IEnumerable<T> current, Func<T, Guid> id) where T : class
        {
            var old = previous.ToDictionary(id);
            foreach (var item in current)
            {
                if (!old.Remove(id(item), out var was) || !ReferenceEquals(was, item)) changed.Add(id(item));
            }

            foreach (var gone in old.Keys) changed.Add(gone);
        }

        foreach (var layer in after.Layers)
        {
            var previous = before.FindLayer(layer.Id);
            switch (layer)
            {
                case ObjectLayer objects:
                    var oldObjects = previous as ObjectLayer;
                    if (oldObjects is null || !SameItems(oldObjects.Rivers, objects.Rivers))
                        Compare(oldObjects?.Rivers ?? [], objects.Rivers, river => river.Id);
                    if (oldObjects is null || !SameItems(oldObjects.Roads, objects.Roads))
                        Compare(oldObjects?.Roads ?? [], objects.Roads, road => road.Id);
                    break;
                case HexLayer hex:
                    var oldHex = previous as HexLayer;
                    if (oldHex is not null && !Equals(oldHex.Board.Layout, hex.Board.Layout))
                        foreach (var path in hex.Paths) changed.Add(path.Id.Value);
                    if (oldHex is null || !ReferenceEquals(oldHex.Paths, hex.Paths))
                        Compare<HexPath>(oldHex?.Paths ?? [], hex.Paths, path => path.Id.Value);
                    break;
            }
        }

        // Targets on layers that disappeared entirely.
        foreach (var layer in before.Layers)
        {
            if (after.FindLayer(layer.Id) is not null) continue;
            if (layer is ObjectLayer objects)
            {
                foreach (var river in objects.Rivers) changed.Add(river.Id);
                foreach (var road in objects.Roads) changed.Add(road.Id);
            }
            else if (layer is HexLayer hex)
            {
                foreach (var path in hex.Paths) changed.Add(path.Id.Value);
            }
        }

        return changed;
    }

    private static bool SameItems<T>(ImmutableArray<T> a, ImmutableArray<T> b) where T : class
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }

    private static IEnumerable<TextPathBinding> Candidates(MapDocument document)
    {
        foreach (var layer in document.Layers)
        {
            if (!layer.Visible) continue;
            if (layer is ObjectLayer objects)
            {
                foreach (var river in objects.Rivers)
                foreach (var channel in river.Channels)
                {
                    if (!channel.Closed) yield return new TextPathBinding { Target = TextPathTarget.River, TargetId = river.Id, PartId = channel.Id };
                }

                foreach (var road in objects.Roads)
                foreach (var branch in road.Branches)
                {
                    if (!branch.Closed) yield return new TextPathBinding { Target = TextPathTarget.Road, TargetId = road.Id, PartId = branch.Id };
                }
            }
            else if (layer is HexLayer hex)
            {
                foreach (var path in hex.Paths) yield return new TextPathBinding { Target = TextPathTarget.HexPath, TargetId = path.Id.Value };
            }
        }
    }

    private static bool TryAxis(MapDocument document, TextPathBinding binding, out Axis axis)
    {
        axis = default;
        foreach (var layer in document.Layers)
        {
            switch (binding.Target)
            {
                case TextPathTarget.River when layer is ObjectLayer objects:
                    foreach (var river in objects.Rivers)
                    {
                        if (river.Id != binding.TargetId) continue;
                        var set = RiverGeometry.Evaluate(river);
                        var channel = binding.PartId is { } part && set.Channels.TryGetValue(part, out var c) ? c
                            : set.Channels.Values.Where(g => !g.Closed).OrderByDescending(g => g.Length).FirstOrDefault();
                        if (channel is null || channel.Samples.Length < 2) return false;
                        axis = FromSamples(channel.Samples.Select(s => (new VectorPoint(s.X, s.Y), s.HalfWidth)));
                        return axis.Length > 0;
                    }

                    break;
                case TextPathTarget.Road when layer is ObjectLayer objects:
                    foreach (var road in objects.Roads)
                    {
                        if (road.Id != binding.TargetId) continue;
                        var set = RoadGeometry.Evaluate(road);
                        var branch = binding.PartId is { } part && set.Branches.TryGetValue(part, out var b) ? b
                            : set.Branches.Values.Where(g => !g.Closed).OrderByDescending(g => g.Length).FirstOrDefault();
                        if (branch is null || branch.Samples.Length < 2) return false;
                        axis = FromSamples(branch.Samples.Select(s => (new VectorPoint(s.X, s.Y), s.HalfWidth)));
                        return axis.Length > 0;
                    }

                    break;
                case TextPathTarget.HexPath when layer is HexLayer hex:
                    foreach (var path in hex.Paths)
                    {
                        if (path.Id.Value != binding.TargetId) continue;
                        var points = HexPathGeometry.Flatten(path, hex.Board.Layout);
                        if (points.Count < 2) return false;
                        var half = hex.Board.Layout.Size * 0.06 * Math.Max(0.1, path.Width);
                        axis = FromSamples(points.Select(p => (p, half)));
                        return axis.Length > 0;
                    }

                    break;
            }
        }

        return false;
    }

    private static Axis FromSamples(IEnumerable<(VectorPoint Point, double Half)> samples)
    {
        var points = new List<VectorPoint>(); var halves = new List<double>(); var distances = new List<double>();
        foreach (var (point, half) in samples)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) continue;
            if (points.Count > 0)
            {
                var step = Distance(points[^1], point);
                if (step < 1e-6) continue;
                distances.Add(distances[^1] + step);
            }
            else
            {
                distances.Add(0);
            }

            points.Add(point); halves.Add(Math.Max(0, half));
        }

        return new Axis([.. points], [.. halves], [.. distances]);
    }

    private static (VectorPoint Point, double Half) Sample(Axis axis, double s)
    {
        var d = axis.Distances;
        if (s <= 0) return (axis.Points[0], axis.HalfWidths[0]);
        if (s >= d[^1]) return (axis.Points[^1], axis.HalfWidths[^1]);
        var hi = Array.BinarySearch(d, s);
        if (hi >= 0) return (axis.Points[hi], axis.HalfWidths[hi]);
        hi = ~hi; var lo = hi - 1;
        var t = (s - d[lo]) / Math.Max(1e-9, d[hi] - d[lo]);
        var a = axis.Points[lo]; var b = axis.Points[hi];
        return (new VectorPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t),
            axis.HalfWidths[lo] + (axis.HalfWidths[hi] - axis.HalfWidths[lo]) * t);
    }

    private static (double Distance, double Along) Project(Axis axis, double x, double y)
    {
        var best = double.MaxValue; var along = 0.0;
        for (var i = 1; i < axis.Points.Length; i++)
        {
            var a = axis.Points[i - 1]; var b = axis.Points[i];
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            var t = lengthSquared <= 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / lengthSquared, 0, 1);
            var px = a.X + dx * t; var py = a.Y + dy * t;
            var distance = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
            if (distance >= best) continue;
            best = distance;
            along = axis.Distances[i - 1] + (axis.Distances[i] - axis.Distances[i - 1]) * t;
        }

        return (best, along);
    }

    /// <summary>Text reads backwards when, weighted along its length, it runs right-to-left.</summary>
    private static bool ReadsBackwards(VectorPoint first, VectorPoint last, VectorPoint[] tangents)
    {
        var sum = 0.0;
        foreach (var t in tangents) sum += t.X;
        if (Math.Abs(sum) > 1e-6 * tangents.Length) return sum < 0;
        // A vertical line reads from bottom to top, like a map label on a north-south river.
        return last.Y > first.Y;
    }

    private static VectorPoint Unit(VectorPoint a, VectorPoint b, VectorPoint fallback)
    {
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return length < 1e-9 ? fallback : new VectorPoint(dx / length, dy / length);
    }

    private static double Distance(VectorPoint a, VectorPoint b) =>
        Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    private static double Round(double value) => Math.Round(value, 3);

    private static SceneObject WithText(SceneObject label, TextStyle text) =>
        Rebuild(label, label.X, label.Y, label.Width, label.Height, text);

    private static SceneObject Rebuild(SceneObject source, double x, double y, double width, double height, TextStyle text) =>
        new SceneObject(source.Id, source.Kind, source.Name, x, y, width, height, 0,
            source.FillColor, source.StrokeColor, source.StrokeWidth, source.Visible, source.Locked)
        {
            Sublayer = source.Sublayer,
            GroupId = source.GroupId,
            GroupName = source.GroupName,
            FillGradient = source.FillGradient,
            Text = text,
            ObjectOpacity = source.ObjectOpacity,
        }.KeepingMeaningOf(source);
}

/// <summary>
/// An edit plus the labels it made follow (<see cref="TextOnPath.Follow"/>): redo applies the edit
/// and puts the followed labels back; undo restores the labels as the edit left them and then
/// reverts the edit, so a label on another layer than its river returns with the river.
/// </summary>
public sealed class TextPathFollowCommand : IEditCommand
{
    private readonly IEditCommand _inner;
    private readonly IReadOnlyList<(LayerId Layer, SceneObject Before, SceneObject After)> _labels;

    public TextPathFollowCommand(IEditCommand inner, MapDocument edited, MapDocument followed)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        var labels = new List<(LayerId, SceneObject, SceneObject)>();
        foreach (var layer in followed.Layers.OfType<ObjectLayer>())
        {
            if (edited.FindLayer(layer.Id) is not ObjectLayer previous || ReferenceEquals(previous.Objects, layer.Objects)) continue;
            var before = previous.Objects.Where(item => item.Text?.Path is not null).ToDictionary(item => item.Id);
            foreach (var item in layer.Objects)
            {
                if (item.Text?.Path is not null && before.TryGetValue(item.Id, out var was) && !ReferenceEquals(was, item))
                    labels.Add((layer.Id, was, item));
            }
        }

        _labels = labels;
    }

    public string Name => _inner.Name;

    public long? MeasuredWorkingBytes => _inner.MeasuredWorkingBytes;

    public int LabelCount => _labels.Count;

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
