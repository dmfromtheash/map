using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>One evaluated point of a branch axis: position, half width, unit tangent and distance from the branch start.</summary>
public readonly record struct RoadSample(double X, double Y, double HalfWidth, double TangentX, double TangentY, double Distance);

/// <summary>
/// What a branch end meets: the shared node and the widest other branch there. A narrow branch
/// opens softly into a wider one over the last stretch before the node; the renderer buries its
/// end under the wider body so the fork has no seam.
/// </summary>
public sealed record RoadEndJoin(Guid NodeId, double OtherHalfWidth, bool OtherPassesThrough);

/// <summary>Evaluated axis of one branch, from its first vertex to its last (or around a loop).</summary>
public sealed class RoadBranchGeometry
{
    internal RoadBranchGeometry(RoadBranch branch, RoadStyle style, RoadSample[] samples, int[] nodeSampleIndex,
        RoadEndJoin? startJoin, RoadEndJoin? endJoin, double startTaper, double endTaper, double[] junctionDistances)
    {
        Branch = branch; Style = style; Samples = samples; NodeSampleIndex = nodeSampleIndex;
        StartJoin = startJoin; EndJoin = endJoin; StartTaper = startTaper; EndTaper = endTaper;
        JunctionDistances = junctionDistances;
    }

    /// <summary>Distances along the branch of every vertex whose node other branches share: where the edges go quiet.</summary>
    public double[] JunctionDistances { get; }

    public RoadBranch Branch { get; }

    /// <summary>The look this branch is drawn with: its own, or the road's.</summary>
    public RoadStyle Style { get; }
    public RoadSample[] Samples { get; }

    /// <summary>Sample index sitting exactly on each vertex, in vertex order; a loop carries one more entry, the closing one.</summary>
    public int[] NodeSampleIndex { get; }

    /// <summary>Null at a free start; otherwise the branch starts on a node other branches use.</summary>
    public RoadEndJoin? StartJoin { get; }
    public RoadEndJoin? EndJoin { get; }

    /// <summary>Length in world units over which a free end narrows; 0 for a plain cap.</summary>
    public double StartTaper { get; }
    public double EndTaper { get; }

    public bool Closed => Branch.Closed;
    public double Length => Samples.Length == 0 ? 0 : Samples[^1].Distance;
    public VectorPoint Start => new(Samples[0].X, Samples[0].Y);
    public VectorPoint End => new(Samples[^1].X, Samples[^1].Y);
}

/// <summary>One node of the evaluated network: where it is, how many branch ends and passes meet there, and the widest of them.</summary>
public sealed record RoadNodeInfo(Guid Id, double X, double Y, int Degree, double MaxHalfWidth);

/// <summary>Every branch of a road evaluated once, sharing one immutable road instance.</summary>
public sealed class RoadGeometrySet
{
    internal RoadGeometrySet(Road road, IReadOnlyDictionary<Guid, RoadBranchGeometry> branches,
        IReadOnlyDictionary<Guid, RoadNodeInfo> nodes, WorldBounds bounds)
    { Road = road; Branches = branches; Nodes = nodes; Bounds = bounds; }

    public Road Road { get; }
    public IReadOnlyDictionary<Guid, RoadBranchGeometry> Branches { get; }
    public IReadOnlyDictionary<Guid, RoadNodeInfo> Nodes { get; }

    /// <summary>Union of the drawn bodies including the shoulder and edge marks.</summary>
    public WorldBounds Bounds { get; }
}

/// <summary>One closed contour of a road body, plus the round joints that keep every corner sound.</summary>
public sealed record RoadOutline(IReadOnlyList<IReadOnlyList<VectorPoint>> Contours, IReadOnlyList<(VectorPoint Centre, double Radius)> Joints);

/// <summary>
/// Pure road geometry shared by editing, hit-testing, rendering and export: one deterministic
/// evaluation of the saved nodes and branches, so the viewport, a tile and a single image agree.
/// The axis is smooth first; width follows it smoothly; the edges vary independently and by a
/// bounded amount, and an inner edge never folds over at a tight bend.
/// </summary>
public static class RoadGeometry
{
    private static readonly ConditionalWeakTable<Road, RoadGeometrySet> Cache = [];

    public static RoadGeometrySet Evaluate(Road road)
    {
        ArgumentNullException.ThrowIfNull(road);
        return Cache.GetValue(road, static r => Build(r, 0));
    }

    /// <summary>Evaluation with a floor on the drawn width for far zoom; not cached, the floor depends on the view.</summary>
    public static RoadGeometrySet Evaluate(Road road, double minimumWidth) =>
        minimumWidth <= 0 ? Evaluate(road) : Build(road, minimumWidth);

    /// <summary>World reach of everything a road draws beyond its body: shoulder, tone patches, edge marks and their softness.</summary>
    public static double DecorReach(RoadStyle style) =>
        style.ShoulderWidth * 1.9 + style.EdgeWidth * 2 + 6;

    /// <summary>Everything one branch's geometry is computed from, so an unchanged branch is recognised across road revisions.</summary>
    private sealed record BranchSignature(double WidthScale, double MinimumWidth, RoadStyle Style,
        (double X, double Y, double Width, double Tension, bool Shared)[] Vertices, bool Closed, RoadEndJoin? Start, RoadEndJoin? End)
    {
        public static BranchSignature Of(Road road, RoadBranch branch, IReadOnlyDictionary<Guid, RoadNodeInfo> nodes, double minimumWidth)
        {
            var vertices = new (double, double, double, double, bool)[branch.Vertices.Length];
            for (var i = 0; i < vertices.Length; i++)
            {
                var vertex = branch.Vertices[i]; var node = nodes[vertex.NodeId];
                vertices[i] = (node.X, node.Y, vertex.Width, vertex.EffectiveTension, road.IsShared(vertex.NodeId));
            }
            RoadEndJoin? start = null, end = null;
            if (!branch.Closed && branch.Vertices.Length > 0)
            {
                start = Join(road, branch, branch.Vertices[0].NodeId, nodes);
                end = Join(road, branch, branch.Vertices[^1].NodeId, nodes);
            }
            return new BranchSignature(road.WidthScale, minimumWidth, road.EffectiveStyle(branch), vertices, branch.Closed, start, end);
        }

        public bool SameAs(BranchSignature other) =>
            WidthScale == other.WidthScale && MinimumWidth == other.MinimumWidth && Closed == other.Closed
            && Style == other.Style && Equals(Start, other.Start) && Equals(End, other.End)
            && Vertices.AsSpan().SequenceEqual(other.Vertices);
    }

    private sealed record BranchMemoEntry(BranchSignature Signature, RoadBranchGeometry Geometry);

    private sealed class BranchMemo
    {
        public BranchMemoEntry? Entry;
    }

    private static readonly ConditionalWeakTable<RoadBranch, BranchMemo> BranchMemos = [];

    private static RoadGeometrySet Build(Road road, double minimumWidth)
    {
        var nodes = new Dictionary<Guid, RoadNodeInfo>(road.Nodes.Length);
        foreach (var node in road.Nodes)
        {
            var degree = 0; var widest = 0.0;
            foreach (var branch in road.Branches)
                for (var i = 0; i < branch.Vertices.Length; i++)
                    if (branch.Vertices[i].NodeId == node.Id)
                    {
                        degree += branch.Closed || (i > 0 && i < branch.Vertices.Length - 1) ? 2 : 1;
                        widest = Math.Max(widest, branch.Vertices[i].Width * road.WidthScale / 2);
                    }
            nodes[node.Id] = new RoadNodeInfo(node.Id, node.X, node.Y, degree, widest);
        }

        // A branch is rebuilt only when something it is built from changed: its own vertices,
        // the positions of its nodes, what it meets at its ends, the width scale, the style or
        // the width floor. Moving one node therefore recomputes the branches through that node
        // and hands every other branch back as the very same result - no identical copies.
        var result = new Dictionary<Guid, RoadBranchGeometry>(road.Branches.Length);
        foreach (var branch in road.Branches)
        {
            var signature = BranchSignature.Of(road, branch, nodes, minimumWidth);
            var memo = BranchMemos.GetValue(branch, static _ => new BranchMemo());
            var kept = memo.Entry;
            if (kept is not null && kept.Signature.SameAs(signature)) { result[branch.Id] = kept.Geometry; continue; }
            var built = BuildBranch(road, branch, nodes, minimumWidth);
            memo.Entry = new BranchMemoEntry(signature, built);
            result[branch.Id] = built;
        }

        var bounds = new WorldBounds(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
        foreach (var geometry in result.Values)
        {
            var pad = DecorReach(geometry.Style);
            foreach (var sample in geometry.Samples)
            {
                var reach = sample.HalfWidth * (1.6 + geometry.Style.EdgeRoughness * 0.5) + pad;
                bounds = new WorldBounds(Math.Min(bounds.Left, sample.X - reach), Math.Min(bounds.Top, sample.Y - reach),
                    Math.Max(bounds.Right, sample.X + reach), Math.Max(bounds.Bottom, sample.Y + reach));
            }
        }
        if (bounds.Left > bounds.Right) bounds = new WorldBounds(0, 0, 0, 0);
        return new RoadGeometrySet(road, result, nodes, bounds);
    }

    private static RoadBranchGeometry BuildBranch(Road road, RoadBranch branch, IReadOnlyDictionary<Guid, RoadNodeInfo> nodes, double minimumWidth)
    {
        var style = road.EffectiveStyle(branch);
        var vertices = branch.Vertices;
        var count = branch.Closed ? vertices.Length + 1 : vertices.Length;
        var points = new (double X, double Y, double Width, double Tension)[count];
        for (var i = 0; i < count; i++)
        {
            var vertex = vertices[i % vertices.Length];
            var node = nodes[vertex.NodeId];
            points[i] = (node.X, node.Y, vertex.Width * road.WidthScale, vertex.EffectiveTension);
        }

        var samples = new List<RoadSample>(count * 24);
        var pointIndex = new int[count];
        for (var i = 0; i < count - 1; i++)
        {
            var p1 = (points[i].X, points[i].Y);
            var p2 = (points[i + 1].X, points[i + 1].Y);
            (double, double) p0 = i > 0 ? (points[i - 1].X, points[i - 1].Y)
                : branch.Closed ? (points[count - 2].X, points[count - 2].Y)
                : (p1.Item1 * 2 - p2.Item1, p1.Item2 * 2 - p2.Item2);
            (double, double) p3 = i + 2 < count ? (points[i + 2].X, points[i + 2].Y)
                : branch.Closed ? (points[1].X, points[1].Y)
                : (p2.Item1 * 2 - p1.Item1, p2.Item2 * 2 - p1.Item2);
            var chord = Math.Sqrt((p2.Item1 - p1.Item1) * (p2.Item1 - p1.Item1) + (p2.Item2 - p1.Item2) * (p2.Item2 - p1.Item2));
            var steps = Math.Clamp((int)Math.Ceiling(chord / 4), 6, 96);
            // The tangent at a vertex is scaled by that vertex's tension: 0 makes a corner, 1 the
            // plain Catmull-Rom curve. Shape is edited here, never by subdividing.
            var t1 = points[i].Tension; var t2 = points[i + 1].Tension;
            var m1 = (0.5 * t1 * (p2.Item1 - p0.Item1), 0.5 * t1 * (p2.Item2 - p0.Item2));
            var m2 = (0.5 * t2 * (p3.Item1 - p1.Item1), 0.5 * t2 * (p3.Item2 - p1.Item2));
            pointIndex[i] = samples.Count;
            for (var s = 0; s < steps; s++)
            {
                var t = s / (double)steps;
                var (x, y) = Hermite(p1, m1, p2, m2, t);
                samples.Add(new RoadSample(x, y, 0, 0, 0, 0));
            }
        }
        pointIndex[count - 1] = samples.Count;
        samples.Add(new RoadSample(points[^1].X, points[^1].Y, 0, 0, 0, 0));

        var array = samples.ToArray();
        for (var i = 1; i < array.Length; i++)
        {
            var d = array[i - 1].Distance + Math.Sqrt(
                (array[i].X - array[i - 1].X) * (array[i].X - array[i - 1].X) + (array[i].Y - array[i - 1].Y) * (array[i].Y - array[i - 1].Y));
            array[i] = array[i] with { Distance = d };
        }
        var length = array[^1].Distance;

        // Width by distance inside each segment, then the two end treatments: a soft opening into
        // a wider branch at a shared node, and a taper at a free end.
        for (var segment = 0; segment < count - 1; segment++)
        {
            var from = pointIndex[segment]; var to = pointIndex[segment + 1];
            var start = array[from].Distance; var span = Math.Max(1e-9, array[to].Distance - start);
            var w1 = points[segment].Width; var w2 = points[segment + 1].Width;
            for (var i = from; i <= to; i++)
            {
                var local = (array[i].Distance - start) / span;
                array[i] = array[i] with { HalfWidth = (w1 + (w2 - w1) * local) / 2 };
            }
        }

        RoadEndJoin? startJoin = null, endJoin = null;
        double startTaper = 0, endTaper = 0;
        if (!branch.Closed)
        {
            startJoin = Join(road, branch, vertices[0].NodeId, nodes);
            endJoin = Join(road, branch, vertices[^1].NodeId, nodes);
            if (startJoin is not null) Flare(array, startJoin.OtherHalfWidth, fromStart: true, length);
            if (endJoin is not null) Flare(array, endJoin.OtherHalfWidth, fromStart: false, length);
            if (startJoin is null && style.StartCap == RoadEndCap.Taper)
                startTaper = Taper(array, style, fromStart: true, length);
            if (endJoin is null && style.EndCap == RoadEndCap.Taper)
                endTaper = Taper(array, style, fromStart: false, length);
        }
        if (minimumWidth > 0)
            for (var i = 0; i < array.Length; i++)
                array[i] = array[i] with { HalfWidth = Math.Max(array[i].HalfWidth, minimumWidth / 2) };

        // Unit tangents by central difference; a loop wraps, a coincident pair keeps its neighbour's direction.
        var last = array.Length - 1;
        double px = 1, py = 0;
        for (var i = 0; i < array.Length; i++)
        {
            RoadSample a, b;
            if (branch.Closed) { a = array[(i - 1 + last) % last]; b = array[(i + 1) % last]; }
            else { a = array[Math.Max(0, i - 1)]; b = array[Math.Min(last, i + 1)]; }
            var tx = b.X - a.X; var ty = b.Y - a.Y; var len = Math.Sqrt(tx * tx + ty * ty);
            if (len < 1e-9) { tx = px; ty = py; } else { tx /= len; ty /= len; px = tx; py = ty; }
            array[i] = array[i] with { TangentX = tx, TangentY = ty };
        }
        var junctions = new List<double>();
        for (var n = 0; n < vertices.Length; n++)
            if (road.IsShared(vertices[n].NodeId)) junctions.Add(array[pointIndex[n]].Distance);
        return new RoadBranchGeometry(branch, style, array, pointIndex, startJoin, endJoin, startTaper, endTaper, [.. junctions]);
    }

    /// <summary>What an open branch end meets at its node: nothing, or the widest other branch there.</summary>
    private static RoadEndJoin? Join(Road road, RoadBranch branch, Guid nodeId, IReadOnlyDictionary<Guid, RoadNodeInfo> nodes)
    {
        var widest = 0.0; var passes = false; var any = false;
        foreach (var other in road.Branches)
        {
            if (other.Id == branch.Id) continue;
            var at = other.IndexOf(nodeId);
            if (at < 0) continue;
            any = true;
            widest = Math.Max(widest, other.Vertices[at].Width * road.WidthScale / 2);
            if (other.Closed || (at > 0 && at < other.Vertices.Length - 1)) passes = true;
        }
        // A branch that returns to its own interior node (a loop drawn as an open branch) also meets itself.
        var ownUses = 0;
        foreach (var vertex in branch.Vertices) if (vertex.NodeId == nodeId) ownUses++;
        if (ownUses > 1) { any = true; passes = true; widest = Math.Max(widest, nodes[nodeId].MaxHalfWidth); }
        return any ? new RoadEndJoin(nodeId, widest, passes) : null;
    }

    /// <summary>A narrower branch opens smoothly into the wider one it joins: no bottleneck and no flat plug across the road.</summary>
    private static void Flare(RoadSample[] array, double otherHalf, bool fromStart, double length)
    {
        var endHalf = fromStart ? array[0].HalfWidth : array[^1].HalfWidth;
        var target = otherHalf * 0.82;
        if (target <= endHalf * 1.02) return;
        var reach = Math.Clamp(otherHalf * 2.6 + endHalf * 1.5, endHalf * 3, length * 0.5);
        if (reach <= 1e-6) return;
        for (var i = 0; i < array.Length; i++)
        {
            var d = fromStart ? array[i].Distance : length - array[i].Distance;
            if (d >= reach) continue;
            var u = 1 - d / reach;
            var s = u * u * (3 - 2 * u);
            var half = array[i].HalfWidth;
            array[i] = array[i] with { HalfWidth = half + (target - half) * s * Math.Clamp((target - half) / Math.Max(1e-6, target), 0.35, 1) };
        }
    }

    /// <summary>A free end that narrows to a thread and dissolves; returns the length it used.</summary>
    private static double Taper(RoadSample[] array, RoadStyle style, bool fromStart, double length)
    {
        var endHalf = fromStart ? array[0].HalfWidth : array[^1].HalfWidth;
        var reach = style.TaperLength > 0 ? style.TaperLength : Math.Clamp(endHalf * 9, 12, 400);
        reach = Math.Min(reach, length * 0.6);
        if (reach <= 1e-6) return 0;
        for (var i = 0; i < array.Length; i++)
        {
            var d = fromStart ? array[i].Distance : length - array[i].Distance;
            if (d >= reach) continue;
            var u = d / reach;
            var s = u * u * (3 - 2 * u);
            array[i] = array[i] with { HalfWidth = array[i].HalfWidth * (0.12 + 0.88 * s) };
        }
        return reach;
    }

    private static (double X, double Y) Hermite((double, double) p1, (double, double) m1, (double, double) p2, (double, double) m2, double t)
    {
        var t2 = t * t; var t3 = t2 * t;
        var h00 = 2 * t3 - 3 * t2 + 1; var h10 = t3 - 2 * t2 + t; var h01 = -2 * t3 + 3 * t2; var h11 = t3 - t2;
        return (h00 * p1.Item1 + h10 * m1.Item1 + h01 * p2.Item1 + h11 * m2.Item1,
            h00 * p1.Item2 + h10 * m1.Item2 + h01 * p2.Item2 + h11 * m2.Item2);
    }

    /// <summary>
    /// The body outline of one branch, in world units. An open branch gives one contour: the left
    /// edge forward, the end, the right edge backward; a loop gives two, the outer and the inner
    /// ring. Every vertex also gets a round joint of the local half width, so a corner is never a
    /// notch and a tight bend never folds: the inner edge is held back to the radius of curvature
    /// and the joint fills what it gave up. Roughness is a slow, independent, bounded drift of each
    /// edge, seeded by the road and the branch, quiet again near a junction or a taper.
    /// <paramref name="extra"/> adds to the half width on each side - the shoulder uses it.
    /// </summary>
    public static RoadOutline Outline(RoadBranchGeometry geometry, int seed, Func<RoadSample, int, double>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var style = geometry.Style;
        var samples = geometry.Samples;
        var roughness = style.EdgeRoughness;
        var branchSeed = unchecked(seed * 31 + geometry.Branch.Id.GetHashCode());
        var length = geometry.Length;
        var last = samples.Length - 1;
        var left = new List<VectorPoint>(samples.Length + 16);
        var right = new List<VectorPoint>(samples.Length + 16);
        var joints = new List<(VectorPoint, double)>();
        for (var i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            var nx = -s.TangentY; var ny = s.TangentX;
            var half = s.HalfWidth;
            // Slow independent drift of each edge, capped so both offsets stay well positive.
            var period = Math.Max(14, half * 3.2);
            var amp = roughness * Math.Max(0.25, half * 0.36);
            var quiet = 1.0;
            if (!geometry.Closed)
            {
                var toStart = s.Distance; var toEnd = length - s.Distance;
                if (geometry.StartJoin is not null) quiet = Math.Min(quiet, Math.Clamp(toStart / Math.Max(1e-6, half * 2.5), 0, 1));
                if (geometry.EndJoin is not null) quiet = Math.Min(quiet, Math.Clamp(toEnd / Math.Max(1e-6, half * 2.5), 0, 1));
                if (geometry.StartTaper > 0) quiet = Math.Min(quiet, Math.Clamp(toStart / geometry.StartTaper, 0, 1));
                if (geometry.EndTaper > 0) quiet = Math.Min(quiet, Math.Clamp(toEnd / geometry.EndTaper, 0, 1));
            }
            // Near a shared node the edges settle down too, so the fillet of the fork meets a
            // calm edge and no wobble is left to poke out of the join.
            foreach (var junction in geometry.JunctionDistances)
            {
                var away = Math.Abs(s.Distance - junction);
                if (geometry.Closed) away = Math.Min(away, length - away);
                quiet = Math.Min(quiet, Math.Clamp(away / Math.Max(1e-6, half * 3.5), 0, 1));
            }
            var wl = roughness > 0 ? RiverGeometry.Noise(branchSeed, s.Distance / period) * amp * quiet : 0;
            var wr = roughness > 0 ? RiverGeometry.Noise(branchSeed ^ 0x5bd1e995, s.Distance / period + 41.3) * amp * quiet : 0;
            var hl = Math.Max(half * 0.35, half + wl + (extra?.Invoke(s, -1) ?? 0));
            var hr = Math.Max(half * 0.35, half + wr + (extra?.Invoke(s, 1) ?? 0));
            // The inner edge of a bend cannot reach past the centre of curvature: hold it back.
            RoadSample before, after;
            if (geometry.Closed) { before = samples[(i - 1 + last) % last]; after = samples[(i + 1) % last]; }
            else { before = samples[Math.Max(0, i - 1)]; after = samples[Math.Min(last, i + 1)]; }
            var ds = Math.Max(1e-6, after.Distance - before.Distance);
            if (geometry.Closed && after.Distance < before.Distance) ds = Math.Max(1e-6, length - before.Distance + after.Distance);
            var turn = (after.TangentX - before.TangentX) * nx + (after.TangentY - before.TangentY) * ny;
            var curvature = Math.Abs(turn) / ds;
            if (curvature > 1e-9)
            {
                var radius = 0.92 / curvature;
                if (turn > 0) hl = Math.Min(hl, Math.Max(radius, half * 0.08));
                else hr = Math.Min(hr, Math.Max(radius, half * 0.08));
            }
            left.Add(new VectorPoint(s.X + nx * hl, s.Y + ny * hl));
            right.Add(new VectorPoint(s.X - nx * hr, s.Y - ny * hr));
        }
        var jointExtra = extra is null ? 0 : (extra(samples[0], -1) + extra(samples[0], 1)) / 2;
        for (var n = 0; n < geometry.NodeSampleIndex.Length; n++)
        {
            var index = geometry.NodeSampleIndex[n];
            if (index < 0 || index >= samples.Length) continue;
            if (!geometry.Closed && n == 0 && geometry.StartJoin is null && style.StartCap == RoadEndCap.Flat) continue;
            if (!geometry.Closed && n == geometry.NodeSampleIndex.Length - 1 && geometry.EndJoin is null && style.EndCap == RoadEndCap.Flat) continue;
            var s = samples[index];
            var radius = s.HalfWidth + (extra is null ? 0 : (extra(s, -1) + extra(s, 1)) / 2);
            if (radius > 0.05) joints.Add((new VectorPoint(s.X, s.Y), radius));
        }
        _ = jointExtra;

        if (geometry.Closed)
        {
            // Two rings: whichever encloses the larger area is the outer one and keeps its
            // direction; the other is reversed, so the winding rule leaves the middle open.
            var leftArea = SignedArea(left); var rightArea = SignedArea(right);
            var outer = Math.Abs(leftArea) >= Math.Abs(rightArea) ? left : right;
            var inner = ReferenceEquals(outer, left) ? right : left;
            if (SignedArea(outer) < 0) outer.Reverse();
            if (SignedArea(inner) > 0) inner.Reverse();
            return new RoadOutline([outer, inner], joints);
        }

        var contour = new List<VectorPoint>(left.Count * 2 + 32);
        var first = samples[0]; var end = samples[^1];
        contour.AddRange(EndCap(geometry, first, geometry.StartJoin, style.StartCap, extra, backwards: true));
        contour.AddRange(left);
        contour.AddRange(EndCap(geometry, end, geometry.EndJoin, style.EndCap, extra, backwards: false));
        for (var i = right.Count - 1; i >= 0; i--) contour.Add(right[i]);
        if (SignedArea(contour) < 0) contour.Reverse();
        return new RoadOutline([contour], joints);
    }

    private static IEnumerable<VectorPoint> EndCap(RoadBranchGeometry geometry, RoadSample s, RoadEndJoin? join, RoadEndCap cap,
        Func<RoadSample, int, double>? extra, bool backwards)
    {
        var half = s.HalfWidth + (extra is null ? 0 : (extra(s, -1) + extra(s, 1)) / 2);
        var dx = backwards ? -s.TangentX : s.TangentX; var dy = backwards ? -s.TangentY : s.TangentY;
        if (join is not null)
        {
            // Into a wider branch: a flat end at the node and a buried spike that ends inside
            // the other body, so the union has no gap at any entry angle. Onto a narrower one the
            // round joint at the node is the honest end of a road that stops on a path.
            if (half <= join.OtherHalfWidth * 1.1)
            {
                var reach = Math.Max(join.OtherHalfWidth * 0.85, half * 0.6);
                yield return new VectorPoint(s.X + dx * reach, s.Y + dy * reach);
            }
            yield break;
        }
        if (cap == RoadEndCap.Flat || half < 0.3) yield break;
        var nx = -s.TangentY; var ny = s.TangentX;
        for (var i = 0; i <= 8; i++)
        {
            var angle = Math.PI * i / 8;
            var side = backwards ? -Math.Cos(angle) : Math.Cos(angle);
            var forward = Math.Sin(angle);
            yield return new VectorPoint(s.X + (nx * side + dx * forward) * half, s.Y + (ny * side + dy * forward) * half);
        }
    }

    private static double SignedArea(IReadOnlyList<VectorPoint> ring)
    {
        double area = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            area += (ring[j].X * ring[i].Y) - (ring[i].X * ring[j].Y);
        return area / 2;
    }

    /// <summary>
    /// The rounded wedges that fill the inner corners of every fork and sharp bend: where two
    /// branch directions leave a node at an angle, the ground between their edges is worn into
    /// a fillet tangent to both. Each wedge is a small closed polygon to be joined to the body;
    /// pure geometry, so the viewport, a tile and an export agree, and no boolean smoothing of
    /// the whole outline is ever needed.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<VectorPoint>> Fillets(RoadGeometrySet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var result = new List<IReadOnlyList<VectorPoint>>();
        foreach (var node in set.Nodes.Values)
        {
            // Every direction a branch leaves this node in: the branch, the sample at the node,
            // which way along the samples the arm runs, its half width there and how far it goes.
            var arms = new List<Arm>();
            foreach (var geometry in set.Branches.Values)
            {
                var vertices = geometry.Branch.Vertices;
                for (var n = 0; n < vertices.Length; n++)
                {
                    if (vertices[n].NodeId != node.Id) continue;
                    var index = geometry.NodeSampleIndex[n];
                    var samples = geometry.Samples;
                    var last = samples.Length - 1;
                    var half = samples[index].HalfWidth;
                    var interior = geometry.Closed || (n > 0 && n < vertices.Length - 1);
                    if (interior || n == 0)
                    {
                        var ahead = geometry.Closed ? (index + 3) % last : Math.Min(last, index + 3);
                        var reach = geometry.Closed ? geometry.Length / 2 : samples[last].Distance - samples[index].Distance;
                        Add(geometry, index, +1, samples[ahead].X - node.X, samples[ahead].Y - node.Y, half, reach);
                    }
                    if (interior || n == vertices.Length - 1)
                    {
                        var behind = geometry.Closed ? (index - 3 + last) % last : Math.Max(0, index - 3);
                        var reach = geometry.Closed ? geometry.Length / 2 : samples[index].Distance;
                        Add(geometry, index, -1, samples[behind].X - node.X, samples[behind].Y - node.Y, half, reach);
                    }
                }
            }
            if (arms.Count < 2) continue;
            arms.Sort((a, b) => Math.Atan2(a.Dy, a.Dx).CompareTo(Math.Atan2(b.Dy, b.Dx)));
            for (var i = 0; i < arms.Count; i++)
            {
                var a = arms[i]; var b = arms[(i + 1) % arms.Count];
                var cross = a.Dx * b.Dy - a.Dy * b.Dx;
                var dot = a.Dx * b.Dx + a.Dy * b.Dy;
                var gap = Math.Atan2(cross, dot);
                if (gap < 0) gap += Math.PI * 2;
                // Only a real corner is filled: not a straight pass, not a hairline.
                if (gap < 0.12 || gap > Math.PI * 0.85) continue;
                var wedge = Wedge(node, a, b);
                if (wedge is not null) result.Add(wedge);
            }

            void Add(RoadBranchGeometry geometry, int index, int direction, double dx, double dy, double half, double reach)
            {
                var length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1e-6 || half <= 0) return;
                arms.Add(new Arm(geometry, index, direction, dx / length, dy / length, half, reach));
            }
        }
        return result;
    }

    private readonly record struct Arm(RoadBranchGeometry Geometry, int Index, int Direction, double Dx, double Dy, double Half, double Reach);

    /// <summary>
    /// The fillet between two arms leaving a node counter-clockwise from <paramref name="a"/> to
    /// <paramref name="b"/>: a circle tangent to both edges, and the wedge between the real,
    /// sampled edges up to the tangent points - so the wedge follows a bending or flaring edge
    /// exactly and never pokes out of it.
    /// </summary>
    private static IReadOnlyList<VectorPoint>? Wedge(RoadNodeInfo node, Arm a, Arm b)
    {
        // Normals of each arm pointing into the gap between them.
        var e1 = (X: -a.Dy, Y: a.Dx);
        var e2 = (X: b.Dy, Y: -b.Dx);
        var radius = Math.Clamp((a.Half + b.Half) * 0.75, 1.5, 34);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            // The centre lies at (half + radius) from each arm's axis: a 2x2 linear system.
            var r1 = a.Half + radius; var r2 = b.Half + radius;
            var det = e1.X * e2.Y - e1.Y * e2.X;
            if (Math.Abs(det) < 1e-9) return null;
            var cx = (r1 * e2.Y - r2 * e1.Y) / det;
            var cy = (e1.X * r2 - e2.X * r1) / det;
            var along1 = cx * a.Dx + cy * a.Dy; var along2 = cx * b.Dx + cy * b.Dy;
            // The tangent points must lie on the arms, not behind the node and not past their end.
            if (along1 > 0 && along2 > 0 && along1 < a.Reach * 0.9 && along2 < b.Reach * 0.9)
            {
                var edge1 = EdgeUpTo(a, node, e1, along1);
                var edge2 = EdgeUpTo(b, node, e2, along2);
                if (edge1.Count == 0 || edge2.Count == 0) return null;
                var t1 = edge1[^1]; var t2 = edge2[^1];
                var start = Math.Atan2(t1.Y - node.Y - cy, t1.X - node.X - cx);
                var end = Math.Atan2(t2.Y - node.Y - cy, t2.X - node.X - cx);
                // Sweep the short way round, on the side facing the node.
                var sweep = end - start;
                while (sweep > Math.PI) sweep -= Math.PI * 2;
                while (sweep < -Math.PI) sweep += Math.PI * 2;
                var polygon = new List<VectorPoint> { new(node.X, node.Y) };
                polygon.AddRange(edge1);
                var steps = Math.Max(3, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 12)));
                var arcRadius1 = Math.Sqrt((t1.X - node.X - cx) * (t1.X - node.X - cx) + (t1.Y - node.Y - cy) * (t1.Y - node.Y - cy));
                var arcRadius2 = Math.Sqrt((t2.X - node.X - cx) * (t2.X - node.X - cx) + (t2.Y - node.Y - cy) * (t2.Y - node.Y - cy));
                for (var s = 1; s < steps; s++)
                {
                    var t = s / (double)steps;
                    var angle = start + sweep * t;
                    var r = arcRadius1 + (arcRadius2 - arcRadius1) * t;
                    polygon.Add(new VectorPoint(node.X + cx + Math.Cos(angle) * r, node.Y + cy + Math.Sin(angle) * r));
                }
                for (var i = edge2.Count - 1; i >= 0; i--) polygon.Add(edge2[i]);
                if (SignedArea(polygon) < 0) polygon.Reverse();
                return polygon;
            }
            radius *= 0.6;
            if (radius < 1) return null;
        }
        return null;
    }

    /// <summary>The sampled edge of an arm on the side of the gap, from the node out to the given distance along the arm.</summary>
    private static List<VectorPoint> EdgeUpTo(Arm arm, RoadNodeInfo node, (double X, double Y) inward, double along)
    {
        var samples = arm.Geometry.Samples;
        var last = samples.Length - 1;
        var points = new List<VectorPoint>();
        var index = arm.Index;
        for (var step = 0; step < samples.Length; step++)
        {
            var s = samples[index];
            var nx = -s.TangentY; var ny = s.TangentX;
            if (nx * inward.X + ny * inward.Y < 0) { nx = -nx; ny = -ny; }
            // A hair inside the edge, so the wedge is buried in both bodies and the arc crosses
            // the real edge instead of trying to touch it: no notch, no sliver.
            points.Add(new VectorPoint(s.X + nx * s.HalfWidth * 0.88, s.Y + ny * s.HalfWidth * 0.88));
            var travelled = (s.X - node.X) * arm.Dx + (s.Y - node.Y) * arm.Dy;
            if (travelled >= along) break;
            var next = index + arm.Direction;
            if (arm.Geometry.Closed) next = (next + last) % last;
            else if (next < 0 || next > last) break;
            index = next;
        }
        return points;
    }

    /// <summary>Unit tangent of a branch at one of its vertices.</summary>
    public static (double X, double Y) TangentAtVertex(RoadBranchGeometry geometry, int vertex)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var index = geometry.NodeSampleIndex[Math.Clamp(vertex, 0, geometry.NodeSampleIndex.Length - 1)];
        var s = geometry.Samples[Math.Clamp(index, 0, geometry.Samples.Length - 1)];
        return (s.TangentX, s.TangentY);
    }

    /// <summary>
    /// Distance from a point to the nearest branch axis, ranked by how far the point lies outside
    /// the drawn body: a click inside a wide road is a hit on it even when a narrow path runs its
    /// axis closer. Reports the vertex segment and the fraction along it.
    /// </summary>
    public static bool TryClosest(RoadGeometrySet set, double x, double y, double tolerance,
        out Guid branchId, out int segment, out double fraction, out double distance, out double halfWidth,
        Func<RoadBranch, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        branchId = Guid.Empty; segment = -1; fraction = 0; distance = double.MaxValue; halfWidth = 0;
        var best = double.MaxValue;
        foreach (var geometry in set.Branches.Values)
        {
            if (filter is not null && !filter(geometry.Branch)) continue;
            var samples = geometry.Samples;
            for (var i = 0; i < samples.Length - 1; i++)
            {
                var a = samples[i]; var b = samples[i + 1];
                var dx = b.X - a.X; var dy = b.Y - a.Y; var l2 = dx * dx + dy * dy;
                var t = l2 < 1e-12 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / l2, 0, 1);
                var px = a.X + dx * t; var py = a.Y + dy * t;
                var d = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
                var h = a.HalfWidth + (b.HalfWidth - a.HalfWidth) * t;
                var outside = d - h;
                if (outside > tolerance || outside >= best) continue;
                best = outside; distance = d; halfWidth = h; branchId = geometry.Branch.Id;
                var seg = SegmentOfSample(geometry, i);
                segment = seg;
                var from = geometry.NodeSampleIndex[seg]; var to = geometry.NodeSampleIndex[Math.Min(seg + 1, geometry.NodeSampleIndex.Length - 1)];
                var start = samples[from].Distance; var span = Math.Max(1e-9, samples[to].Distance - start);
                var at = a.Distance + (b.Distance - a.Distance) * t;
                fraction = Math.Clamp((at - start) / span, 0, 1);
            }
        }
        return branchId != Guid.Empty;
    }

    private static int SegmentOfSample(RoadBranchGeometry geometry, int sampleIndex)
    {
        var index = geometry.NodeSampleIndex;
        for (var n = index.Length - 2; n >= 0; n--)
            if (sampleIndex >= index[n]) return n;
        return 0;
    }

    /// <summary>Point-in-road test with an optional extra tolerance, in world units.</summary>
    public static bool Contains(Road road, double x, double y, double tolerance = 0) =>
        TryClosest(Evaluate(road), x, y, tolerance, out _, out _, out _, out _, out _);

    /// <summary>True when the drawn body of any branch meets the rectangle.</summary>
    public static bool IntersectsRect(RoadGeometrySet set, WorldBounds rect)
    {
        ArgumentNullException.ThrowIfNull(set);
        foreach (var geometry in set.Branches.Values)
            foreach (var sample in geometry.Samples)
            {
                var dx = Math.Max(0, Math.Max(rect.Left - sample.X, sample.X - rect.Right));
                var dy = Math.Max(0, Math.Max(rect.Top - sample.Y, sample.Y - rect.Bottom));
                if (dx * dx + dy * dy <= sample.HalfWidth * sample.HalfWidth) return true;
            }
        return false;
    }

    private static bool TryPointOnSegment(RoadBranchGeometry geometry, int segment, double fraction, out RoadSample point)
    {
        point = default;
        if (segment < 0 || segment + 1 >= geometry.NodeSampleIndex.Length) return false;
        var from = geometry.NodeSampleIndex[segment]; var to = geometry.NodeSampleIndex[segment + 1];
        var samples = geometry.Samples;
        var start = samples[from].Distance; var length = samples[to].Distance - start;
        var target = start + length * Math.Clamp(fraction, 0, 1);
        for (var i = from; i < to; i++)
        {
            if (samples[i + 1].Distance < target) continue;
            var span = Math.Max(1e-9, samples[i + 1].Distance - samples[i].Distance);
            var t = Math.Clamp((target - samples[i].Distance) / span, 0, 1);
            var a = samples[i]; var b = samples[i + 1];
            point = new RoadSample(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.HalfWidth + (b.HalfWidth - a.HalfWidth) * t,
                a.TangentX, a.TangentY, target);
            return true;
        }
        point = samples[to];
        return true;
    }

    /// <summary>Interpolated full width at a point of a segment, for a vertex inserted there.</summary>
    public static double WidthAt(RoadBranchGeometry geometry, int segment, double fraction) =>
        TryPointOnSegment(geometry, segment, fraction, out var point) ? point.HalfWidth * 2 : geometry.Samples[^1].HalfWidth * 2;

    public static VectorPoint PointAt(RoadBranchGeometry geometry, int segment, double fraction) =>
        TryPointOnSegment(geometry, segment, fraction, out var point) ? new VectorPoint(point.X, point.Y) : geometry.End;

    /// <summary>Linear width profile from the first vertex to the last by cumulative chord length.</summary>
    public static ImmutableArray<RoadVertex> WidthProfile(Road road, RoadBranch branch, double startWidth, double endWidth)
    {
        ArgumentNullException.ThrowIfNull(road);
        ArgumentNullException.ThrowIfNull(branch);
        var vertices = branch.Vertices;
        if (vertices.Length < 2) return vertices;
        var lengths = new double[vertices.Length];
        for (var i = 1; i < vertices.Length; i++)
        {
            var a = road.FindNode(vertices[i - 1].NodeId)!; var b = road.FindNode(vertices[i].NodeId)!;
            lengths[i] = lengths[i - 1] + Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        }
        var total = Math.Max(1e-9, lengths[^1]);
        return [.. vertices.Select((v, i) => v with { Width = Math.Round(startWidth + (endWidth - startWidth) * lengths[i] / total, 2) })];
    }
}
