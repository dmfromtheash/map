using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>One evaluated point of a channel centreline: position, half width and unit tangent.</summary>
public readonly record struct RiverSample(double X, double Y, double HalfWidth, double TangentX, double TangentY, double Distance);

/// <summary>Evaluated centreline of one channel, from the source (or source junction) to its mouth or free end.</summary>
public sealed class RiverChannelGeometry
{
    internal RiverChannelGeometry(RiverChannel channel, RiverSample[] samples, int[] nodeSampleIndex,
        bool joinsParent, double parentHalfWidthAtMouth, double parentTangentX, double parentTangentY,
        bool leavesParent, double parentHalfWidthAtSource, bool tapered)
    {
        Channel = channel; Samples = samples; NodeSampleIndex = nodeSampleIndex; JoinsParent = joinsParent;
        ParentHalfWidthAtMouth = parentHalfWidthAtMouth; ParentTangentX = parentTangentX; ParentTangentY = parentTangentY;
        LeavesParent = leavesParent; ParentHalfWidthAtSource = parentHalfWidthAtSource; Tapered = tapered;
    }

    public RiverChannel Channel { get; }
    public RiverSample[] Samples { get; }

    /// <summary>
    /// Sample index sitting exactly on each node, in node order. A lake carries one more entry:
    /// the closing sample back on the first node. A channel with a source junction starts its
    /// samples before <c>NodeSampleIndex[0]</c>.
    /// </summary>
    public int[] NodeSampleIndex { get; }
    public bool JoinsParent { get; }
    public double ParentHalfWidthAtMouth { get; }
    public double ParentTangentX { get; }
    public double ParentTangentY { get; }
    public bool LeavesParent { get; }
    public double ParentHalfWidthAtSource { get; }

    /// <summary>Whether the free source of this channel starts as a thread.</summary>
    public bool Tapered { get; }
    public bool Closed => Channel.Closed;
    public double Length => Samples.Length == 0 ? 0 : Samples[^1].Distance;

    /// <summary>The evaluated end point: the junction for a tributary, the last node otherwise.</summary>
    public VectorPoint End => new(Samples[^1].X, Samples[^1].Y);

    /// <summary>The evaluated start point: the source junction for a distributary, the first node otherwise.</summary>
    public VectorPoint Start => new(Samples[0].X, Samples[0].Y);
}

/// <summary>Every channel of a river evaluated once, parents before the channels attached to them.</summary>
public sealed class RiverGeometrySet
{
    internal RiverGeometrySet(River river, IReadOnlyDictionary<Guid, RiverChannelGeometry> channels, WorldBounds bounds)
    { River = river; Channels = channels; Bounds = bounds; }

    public River River { get; }
    public IReadOnlyDictionary<Guid, RiverChannelGeometry> Channels { get; }

    /// <summary>Union of the channel outlines including the bank stroke and its halo.</summary>
    public WorldBounds Bounds { get; }
}

/// <summary>
/// Pure river geometry shared by editing, hit-testing, rendering and export: one deterministic
/// evaluation of the saved nodes, so the viewport, a tile and a single image cannot disagree.
/// </summary>
public static class RiverGeometry
{
    private static readonly ConditionalWeakTable<River, RiverGeometrySet> Cache = [];

    /// <summary>Fraction of the first segment over which a free source grows from a thread to its node width.</summary>
    public const double SourceTaperFraction = 0.35;

    /// <summary>Evaluates the river once per immutable instance; the result is shared and never mutated.</summary>
    public static RiverGeometrySet Evaluate(River river)
    {
        ArgumentNullException.ThrowIfNull(river);
        return Cache.GetValue(river, static r => Build(r, 0));
    }

    /// <summary>
    /// Evaluation with a floor on the drawn width, used by far-zoom rendering so a river stays a
    /// visible thread instead of vanishing. Not cached: the floor depends on the view.
    /// </summary>
    public static RiverGeometrySet Evaluate(River river, double minimumWidth) =>
        minimumWidth <= 0 ? Evaluate(river) : Build(river, minimumWidth);

    private static RiverGeometrySet Build(River river, double minimumWidth)
    {
        var result = new Dictionary<Guid, RiverChannelGeometry>();
        var pending = river.Channels.ToList();
        var guard = 0;
        while (pending.Count > 0 && guard++ <= river.Channels.Length)
        {
            foreach (var channel in pending.ToArray())
            {
                if (channel.Mouth is { } mouth && !result.ContainsKey(mouth.ParentChannelId)) continue;
                if (channel.Source is { } source && !result.ContainsKey(source.ParentChannelId)) continue;
                result[channel.Id] = BuildChannel(river, channel, result, minimumWidth);
                pending.Remove(channel);
            }
        }
        // A cyclic or dangling junction is rejected by validation; evaluate the leftovers as free channels.
        foreach (var channel in pending)
            result[channel.Id] = BuildChannel(river, channel with { Mouth = null, Source = null }, result, minimumWidth);

        var pad = river.Style.BankWidth + river.Style.BankShadowBlur * 3 + river.Style.BankReliefWidth * 1.8 + 1;
        var bounds = new WorldBounds(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
        foreach (var geometry in result.Values)
        {
            foreach (var sample in geometry.Samples)
            {
                var reach = sample.HalfWidth * (1 + river.Style.EdgeRoughness * 0.4) + pad;
                bounds = new WorldBounds(Math.Min(bounds.Left, sample.X - reach), Math.Min(bounds.Top, sample.Y - reach),
                    Math.Max(bounds.Right, sample.X + reach), Math.Max(bounds.Bottom, sample.Y + reach));
            }
            foreach (var (point, half) in new[] { (geometry.End, geometry.ParentHalfWidthAtMouth), (geometry.Start, geometry.ParentHalfWidthAtSource) })
            {
                var reach = half + pad;
                bounds = new WorldBounds(Math.Min(bounds.Left, point.X - reach), Math.Min(bounds.Top, point.Y - reach),
                    Math.Max(bounds.Right, point.X + reach), Math.Max(bounds.Bottom, point.Y + reach));
            }
        }
        return new RiverGeometrySet(river, result, bounds);
    }

    private static RiverChannelGeometry BuildChannel(River river, RiverChannel channel,
        IReadOnlyDictionary<Guid, RiverChannelGeometry> parents, double minimumWidth)
    {
        var nodes = channel.Nodes;
        var points = new List<(double X, double Y, double Width, bool Sharp, double Tension)>(nodes.Length + 2);
        var leaves = false; double sourceHalf = 0;
        (double X, double Y)? before = null;
        if (!channel.Closed && channel.Source is { } source && parents.TryGetValue(source.ParentChannelId, out var sourceParent)
            && TryJunction(sourceParent, source, out var origin))
        {
            leaves = true; sourceHalf = sourceParent.Closed ? nodes[0].Width * river.WidthScale : origin.HalfWidth;
            // A distributary departs along the parent flow; a lake outflow departs away from the lake.
            points.Add((origin.X, origin.Y, nodes[0].Width * river.WidthScale, false, 1));
            var reach = Math.Max(sourceHalf * 2, 8);
            var (dx, dy) = sourceParent.Closed ? Inward(sourceParent, origin) : (origin.TangentX, origin.TangentY);
            before = (origin.X - dx * reach, origin.Y - dy * reach);
        }
        var firstNodeIndex = points.Count;
        foreach (var node in nodes) points.Add((node.X, node.Y, node.Width * river.WidthScale, node.Sharp, node.EffectiveTension));
        if (channel.Closed) points.Add(points[firstNodeIndex]);

        var joins = false; double parentHalf = 0, parentTx = 0, parentTy = 0;
        (double X, double Y)? beyond = null;
        if (!channel.Closed && channel.Mouth is { } mouth && parents.TryGetValue(mouth.ParentChannelId, out var parent)
            && TryJunction(parent, mouth, out var junction))
        {
            var last = points[^1];
            joins = true; parentHalf = parent.Closed ? last.Width : junction.HalfWidth;
            // The tributary ends at the junction point and keeps its last node width; the curve is
            // steered along the parent flow so the mouth enters at an acute downstream angle. Into
            // a lake it heads straight for the shore instead.
            var (dx, dy) = parent.Closed ? Inward(parent, junction) : (junction.TangentX, junction.TangentY);
            parentTx = dx; parentTy = dy;
            points.Add((junction.X, junction.Y, last.Width, false, 1));
            var reach = Math.Max(parentHalf * 2, 8);
            beyond = (junction.X + dx * reach, junction.Y + dy * reach);
        }

        var samples = new List<RiverSample>();
        var pointIndex = new int[points.Count];
        var count = points.Count;
        for (var i = 0; i < count - 1; i++)
        {
            (double, double) p0;
            if (i == 0) p0 = before ?? (channel.Closed ? (points[count - 2].X, points[count - 2].Y) : (points[0].X * 2 - points[1].X, points[0].Y * 2 - points[1].Y));
            else p0 = (points[i - 1].X, points[i - 1].Y);
            var p1 = (points[i].X, points[i].Y);
            var p2 = (points[i + 1].X, points[i + 1].Y);
            (double, double) p3;
            if (i + 2 < count) p3 = (points[i + 2].X, points[i + 2].Y);
            else if (channel.Closed) p3 = (points[1].X, points[1].Y);
            else p3 = beyond ?? (points[i + 1].X * 2 - points[i].X, points[i + 1].Y * 2 - points[i].Y);
            var chord = Math.Sqrt((p2.Item1 - p1.Item1) * (p2.Item1 - p1.Item1) + (p2.Item2 - p1.Item2) * (p2.Item2 - p1.Item2));
            var steps = Math.Clamp((int)Math.Ceiling(chord / 5), 6, 64);
            // The node's own tension scales the tangent the curve leaves and enters it with, so
            // the bend is edited in place instead of by inserting more nodes. Sharp reads 0.
            var t1 = points[i].Tension; var t2 = points[i + 1].Tension;
            var m1 = (0.5 * t1 * (p2.Item1 - p0.Item1), 0.5 * t1 * (p2.Item2 - p0.Item2));
            var m2 = (0.5 * t2 * (p3.Item1 - p1.Item1), 0.5 * t2 * (p3.Item2 - p1.Item2));
            pointIndex[i] = samples.Count;
            for (var s = 0; s < steps; s++)
            {
                var t = s / (double)steps;
                var (x, y) = Hermite(p1, m1, p2, m2, t);
                samples.Add(new RiverSample(x, y, 0, 0, 0, 0));
            }
        }
        pointIndex[count - 1] = samples.Count;
        samples.Add(new RiverSample(points[^1].X, points[^1].Y, 0, 0, 0, 0));

        // Distance, then width by distance inside each segment, then tangents.
        var array = samples.ToArray();
        for (var i = 1; i < array.Length; i++)
        {
            var d = array[i - 1].Distance + Math.Sqrt(
                (array[i].X - array[i - 1].X) * (array[i].X - array[i - 1].X) + (array[i].Y - array[i - 1].Y) * (array[i].Y - array[i - 1].Y));
            array[i] = array[i] with { Distance = d };
        }
        var taper = river.Style.TaperSource && !channel.Closed && !leaves && !HasTributaryAtStart(river, channel);
        for (var segment = 0; segment < count - 1; segment++)
        {
            var from = pointIndex[segment]; var to = pointIndex[segment + 1];
            var start = array[from].Distance; var length = Math.Max(1e-9, array[to].Distance - start);
            var w1 = points[segment].Width; var w2 = points[segment + 1].Width;
            for (var i = from; i <= to; i++)
            {
                var local = (array[i].Distance - start) / length;
                var width = w1 + (w2 - w1) * local;
                if (taper && segment == 0)
                {
                    var ramp = Math.Min(1, local / SourceTaperFraction);
                    width *= 0.2 + 0.8 * ramp;
                }
                var half = channel.Closed ? 0 : Math.Max(width, minimumWidth) / 2;
                array[i] = array[i] with { HalfWidth = half };
            }
        }
        for (var i = 0; i < array.Length; i++)
        {
            var a = array[Math.Max(0, i - 1)]; var b = array[Math.Min(array.Length - 1, i + 1)];
            if (channel.Closed) { a = array[(i - 1 + array.Length - 1) % (array.Length - 1)]; b = array[(i + 1) % (array.Length - 1)]; }
            var tx = b.X - a.X; var ty = b.Y - a.Y; var len = Math.Sqrt(tx * tx + ty * ty);
            if (len < 1e-9) { tx = 1; ty = 0; } else { tx /= len; ty /= len; }
            array[i] = array[i] with { TangentX = tx, TangentY = ty };
        }
        // Node sample indices exclude the derived source point; a lake keeps its closing entry.
        var nodeIndex = channel.Closed
            ? pointIndex[firstNodeIndex..]
            : pointIndex[firstNodeIndex..(firstNodeIndex + nodes.Length)];
        return new RiverChannelGeometry(channel, array, nodeIndex, joins, parentHalf, parentTx, parentTy, leaves, sourceHalf, taper);
    }

    /// <summary>Unit direction from a point on a lake outline towards the inside of the lake.</summary>
    private static (double X, double Y) Inward(RiverChannelGeometry lake, RiverSample at)
    {
        var probe = Math.Max(2, lake.Length * 0.01);
        var (nx, ny) = (-at.TangentY, at.TangentX);
        return InsideLake(lake, at.X + nx * probe, at.Y + ny * probe) ? (nx, ny) : (-nx, -ny);
    }

    /// <summary>A source that already receives a tributary at its first node does not thin out.</summary>
    private static bool HasTributaryAtStart(River river, RiverChannel channel) =>
        river.Channels.Any(c => c.Mouth is { } m && m.ParentChannelId == channel.Id && m.ParentNodeId == channel.Nodes[0].Id && m.Fraction < 0.05);

    private static (double X, double Y) Hermite((double, double) p1, (double, double) m1, (double, double) p2, (double, double) m2, double t)
    {
        var t2 = t * t; var t3 = t2 * t;
        var h00 = 2 * t3 - 3 * t2 + 1; var h10 = t3 - 2 * t2 + t; var h01 = -2 * t3 + 3 * t2; var h11 = t3 - t2;
        return (h00 * p1.Item1 + h10 * m1.Item1 + h01 * p2.Item1 + h11 * m2.Item1,
            h00 * p1.Item2 + h10 * m1.Item2 + h01 * p2.Item2 + h11 * m2.Item2);
    }

    /// <summary>Index of the parent node segment a junction addresses, or -1 when the node is absent.</summary>
    public static int SegmentOf(RiverChannel parent, Guid nodeId)
    {
        var last = parent.Closed ? parent.Nodes.Length - 1 : parent.Nodes.Length - 2;
        for (var i = 0; i <= last; i++) if (parent.Nodes[i].Id == nodeId) return i;
        return -1;
    }

    /// <summary>The junction point on a parent channel, with the parent half width and flow tangent there.</summary>
    public static bool TryJunction(RiverChannelGeometry parent, RiverJunction junction, out RiverSample point)
    {
        point = default;
        var segment = SegmentOf(parent.Channel, junction.ParentNodeId);
        if (segment < 0) return false;
        return TryPointOnSegment(parent, segment, junction.Fraction, out point);
    }

    private static bool TryPointOnSegment(RiverChannelGeometry geometry, int segment, double fraction, out RiverSample point)
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
            var tx = a.TangentX + (b.TangentX - a.TangentX) * t; var ty = a.TangentY + (b.TangentY - a.TangentY) * t;
            var tl = Math.Sqrt(tx * tx + ty * ty); if (tl < 1e-9) { tx = a.TangentX; ty = a.TangentY; } else { tx /= tl; ty /= tl; }
            point = new RiverSample(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.HalfWidth + (b.HalfWidth - a.HalfWidth) * t, tx, ty, target);
            return true;
        }
        point = samples[to];
        return true;
    }

    /// <summary>
    /// Closed outline of one channel: the left bank forward, a cap, the right bank backward. A
    /// tributary is extended past the junction to a point under the parent water, a distributary
    /// starts under it, so the union of the outlines has no seam and no gap at any entry angle.
    /// A lake's outline is its centreline loop. Roughness wobbles the banks deterministically.
    /// </summary>
    public static IReadOnlyList<VectorPoint> Outline(RiverChannelGeometry geometry, double roughness, int bankManner = 0)
    {
        var samples = geometry.Samples;
        var seed = geometry.Channel.Id.GetHashCode();
        if (geometry.Closed)
        {
            var loop = new List<VectorPoint>(samples.Length);
            var perimeter = Math.Max(1, samples[^1].Distance);
            var amplitude = roughness * Math.Clamp(perimeter * 0.012, 0.5, 40);
            for (var i = 0; i < samples.Length - 1; i++)
            {
                var s = samples[i];
                var wobble = roughness > 0 ? Noise(seed, s.Distance / Math.Max(4, amplitude * 2.5)) * amplitude : 0;
                loop.Add(new VectorPoint(s.X - s.TangentY * wobble, s.Y + s.TangentX * wobble));
            }
            return loop;
        }
        var left = new List<VectorPoint>(samples.Length + 16);
        var right = new List<VectorPoint>(samples.Length + 16);
        foreach (var s in samples)
        {
            var nx = -s.TangentY; var ny = s.TangentX;
            var wl = BankWobble(seed, s, left: true, roughness, bankManner);
            var wr = BankWobble(seed, s, left: false, roughness, bankManner);
            left.Add(new VectorPoint(s.X + nx * (s.HalfWidth + wl), s.Y + ny * (s.HalfWidth + wl)));
            right.Add(new VectorPoint(s.X - nx * (s.HalfWidth + wr), s.Y - ny * (s.HalfWidth + wr)));
        }
        var result = new List<VectorPoint>(left.Count * 2 + 32);
        var first = samples[0];
        if (geometry.LeavesParent)
        {
            var reach = Math.Max(geometry.ParentHalfWidthAtSource * 0.9, first.HalfWidth * 1.2);
            result.Add(new VectorPoint(first.X - first.TangentX * reach, first.Y - first.TangentY * reach));
        }
        else result.AddRange(Cap(first, backwards: true));
        result.AddRange(left);
        var last = samples[^1];
        if (geometry.JoinsParent)
        {
            // Buried extension: a spike along the tributary direction that ends well inside the parent.
            var reach = Math.Max(geometry.ParentHalfWidthAtMouth * 0.9, last.HalfWidth * 1.2);
            result.Add(new VectorPoint(last.X + last.TangentX * reach, last.Y + last.TangentY * reach));
        }
        else result.AddRange(Cap(last, backwards: false));
        for (var i = right.Count - 1; i >= 0; i--) result.Add(right[i]);
        return result;
    }

    /// <summary>
    /// How far one bank of a channel stands out from its even half width at a sample: the small
    /// hand-drawn wobble, and with a natural bank manner (owner 2026-10-22, «ближе к настоящим
    /// рекам») a slow swell of each bank on its own - reaches and narrows, never two parallel banks.
    /// Shared by the outline and the drawn bank line, so the ink lies exactly on the water's edge.
    /// </summary>
    public static double BankWobble(int seed, RiverSample s, bool left, double roughness, int bankManner)
    {
        var period = Math.Max(4, s.HalfWidth * 1.6);
        var amp = roughness * Math.Max(0.3, s.HalfWidth * 0.35);
        var wobble = roughness <= 0 ? 0 : left
            ? Noise(seed, s.Distance / period) * amp
            : Noise(seed ^ 0x5bd1e995, s.Distance / period + 37.1) * amp;
        if (bankManner <= 0 || s.HalfWidth <= 0) return wobble;
        // Reaches and narrows: about ±20 % of the width over six to ten widths, each bank apart
        // (the engraving keeps them calmer).
        // The roughness slider drives how ragged a natural bank is (owner 2026-10-22: «ещё более
        // коряво по краям»): bigger reaches, bays and spurs a few widths long, and a jagged tremor.
        var rough = Math.Clamp(roughness, 0, 1);
        var swellPeriod = Math.Max(8, s.HalfWidth * 2 * 7.5);
        var swellAmp = s.HalfWidth * (0.15 + 0.35 * rough);
        var swell = Noise(seed ^ (left ? 0x2C1B3C6D : 0x297A2D39), s.Distance / swellPeriod + (left ? 3.7 : 11.9)) * swellAmp;
        var bays = Noise(seed ^ (left ? 0x510E527F : 0x1F83D9AB), s.Distance / Math.Max(3, s.HalfWidth * 2.2) + (left ? 5.1 : 2.3))
            * s.HalfWidth * 0.3 * rough;
        // Fine tremor of the pen; ragged with the roughness, but never more than a fifth of the width.
        var tremorPeriod = Math.Max(1.2, s.HalfWidth * (0.9 - 0.4 * rough));
        var tremor = Noise(seed ^ (left ? 0x6A09E667 : 0x3C6EF372), s.Distance / tremorPeriod) * s.HalfWidth * (0.04 + 0.12 * rough);
        return wobble + swell + bays + tremor;
    }

    /// <summary>Smooth deterministic noise in -1..1 from a seed and a continuous coordinate.</summary>
    public static double Noise(int seed, double t)
    {
        var i = (long)Math.Floor(t); var f = t - i;
        var a = Hash(seed, i); var b = Hash(seed, i + 1);
        var u = f * f * (3 - 2 * f);
        return a + (b - a) * u;
    }

    private static double Hash(int seed, long i)
    {
        unchecked
        {
            var h = (ulong)i * 0x9E3779B97F4A7C15UL ^ ((ulong)(uint)seed * 0xBF58476D1CE4E5B9UL);
            h ^= h >> 31; h *= 0x94D049BB133111EBUL; h ^= h >> 29;
            return (h & 0xFFFFFF) / (double)0xFFFFFF * 2 - 1;
        }
    }

    private static IEnumerable<VectorPoint> Cap(RiverSample s, bool backwards)
    {
        var nx = -s.TangentY; var ny = s.TangentX;
        var dx = backwards ? -s.TangentX : s.TangentX; var dy = backwards ? -s.TangentY : s.TangentY;
        for (var i = 0; i <= 8; i++)
        {
            var angle = Math.PI * i / 8;
            var sideSign = backwards ? -Math.Cos(angle) : Math.Cos(angle);
            var forward = Math.Sin(angle);
            yield return new VectorPoint(s.X + (nx * sideSign + dx * forward) * s.HalfWidth, s.Y + (ny * sideSign + dy * forward) * s.HalfWidth);
        }
    }

    /// <summary>
    /// Distance from a point to the nearest channel centreline, with the node segment it falls on
    /// (-1 for the derived stretch before a distributary's first node; a lake reports its edge).
    /// </summary>
    public static bool TryClosest(RiverGeometrySet set, double x, double y, double tolerance,
        out Guid channelId, out int segment, out double fraction, out double distance, out double halfWidth,
        Func<RiverChannel, bool>? filter = null)
    {
        channelId = Guid.Empty; segment = int.MinValue; fraction = 0; distance = double.MaxValue; halfWidth = 0;
        // Ranked by how far the point lies OUTSIDE the drawn body, not by its distance to the
        // centreline. A click well inside a wide river is a hit on that river even when a narrow
        // channel happens to run its centreline closer to the cursor.
        var best = double.MaxValue;
        foreach (var geometry in set.Channels.Values)
        {
            if (filter is not null && !filter(geometry.Channel)) continue;
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
                best = outside; distance = d; halfWidth = h; channelId = geometry.Channel.Id;
                var seg = NodeSegmentOf(geometry, i);
                segment = seg;
                if (seg < 0) { fraction = 0; continue; }
                var from = geometry.NodeSampleIndex[seg]; var to = geometry.NodeSampleIndex[Math.Min(seg + 1, geometry.NodeSampleIndex.Length - 1)];
                var start = samples[from].Distance; var span = Math.Max(1e-9, samples[to].Distance - start);
                var at = a.Distance + (b.Distance - a.Distance) * t;
                fraction = Math.Clamp((at - start) / span, 0, 1);
            }
        }
        return segment != int.MinValue;
    }

    /// <summary>Index of the node segment that owns a sample interval; -1 before the first node.</summary>
    private static int NodeSegmentOf(RiverChannelGeometry geometry, int sampleIndex)
    {
        var index = geometry.NodeSampleIndex;
        for (var n = index.Length - 2; n >= 0; n--)
            if (sampleIndex >= index[n]) return n;
        // A tributary's stretch from its last node to the junction belongs to the last node.
        if (index.Length > 0 && sampleIndex >= index[^1]) return index.Length - 1;
        return -1;
    }

    /// <summary>Point-in-lake test for a closed channel.</summary>
    public static bool InsideLake(RiverChannelGeometry geometry, double x, double y)
    {
        if (!geometry.Closed) return false;
        var s = geometry.Samples; var inside = false;
        for (int i = 0, j = s.Length - 2; i < s.Length - 1; j = i++)
            if ((s[i].Y > y) != (s[j].Y > y) && x < (s[j].X - s[i].X) * (y - s[i].Y) / (s[j].Y - s[i].Y) + s[i].X)
                inside = !inside;
        return inside;
    }

    /// <summary>
    /// True when the water the user can see meets the rectangle: any point of the drawn body -
    /// the centreline expanded by its own half width - falls inside it, or the rectangle lies
    /// within a lake. A rubber band that crosses the visible surface of a wide river without
    /// reaching its centreline is still a selection of that river.
    /// </summary>
    public static bool IntersectsRect(RiverGeometrySet set, WorldBounds rect)
    {
        ArgumentNullException.ThrowIfNull(set);
        foreach (var geometry in set.Channels.Values)
        {
            foreach (var sample in geometry.Samples)
            {
                var dx = Math.Max(0, Math.Max(rect.Left - sample.X, sample.X - rect.Right));
                var dy = Math.Max(0, Math.Max(rect.Top - sample.Y, sample.Y - rect.Bottom));
                if (dx * dx + dy * dy <= sample.HalfWidth * sample.HalfWidth) return true;
            }
            if (!geometry.Closed) continue;
            // A band drawn entirely inside a lake never touches its outline, but it is still on it.
            var midX = (rect.Left + rect.Right) / 2; var midY = (rect.Top + rect.Bottom) / 2;
            if (InsideLake(geometry, rect.Left, rect.Top) || InsideLake(geometry, rect.Right, rect.Top)
                || InsideLake(geometry, rect.Right, rect.Bottom) || InsideLake(geometry, rect.Left, rect.Bottom)
                || InsideLake(geometry, midX, midY)) return true;
        }
        return false;
    }

    /// <summary>Point-in-river test with an optional extra tolerance, in world units.</summary>
    public static bool Contains(River river, double x, double y, double tolerance = 0)
    {
        var set = Evaluate(river);
        return TryClosest(set, x, y, tolerance, out _, out _, out _, out _, out _)
            || set.Channels.Values.Any(g => InsideLake(g, x, y));
    }

    /// <summary>Interpolated width at a point of a channel, used when a node is inserted on a segment.</summary>
    public static double WidthAt(RiverChannelGeometry geometry, int segment, double fraction) =>
        TryPointOnSegment(geometry, segment, fraction, out var point) ? point.HalfWidth * 2 : geometry.Samples[^1].HalfWidth * 2;

    /// <summary>Position of a point on a node segment, for placing an inserted node.</summary>
    public static VectorPoint PointAt(RiverChannelGeometry geometry, int segment, double fraction) =>
        TryPointOnSegment(geometry, segment, fraction, out var point) ? new VectorPoint(point.X, point.Y) : geometry.End;

    /// <summary>Linear width profile from the source to the mouth by cumulative chord length.</summary>
    public static ImmutableArray<RiverNode> WidthProfile(ImmutableArray<RiverNode> nodes, double sourceWidth, double mouthWidth)
    {
        if (nodes.Length < 2) return nodes;
        var lengths = new double[nodes.Length];
        for (var i = 1; i < nodes.Length; i++)
            lengths[i] = lengths[i - 1] + Math.Sqrt(Math.Pow(nodes[i].X - nodes[i - 1].X, 2) + Math.Pow(nodes[i].Y - nodes[i - 1].Y, 2));
        var total = Math.Max(1e-9, lengths[^1]);
        return [.. nodes.Select((n, i) => n with { Width = Math.Round(sourceWidth + (mouthWidth - sourceWidth) * lengths[i] / total, 2) })];
    }

    /// <summary>Ramer–Douglas–Peucker simplification of a freehand stroke into node points.</summary>
    public static IReadOnlyList<VectorPoint> Simplify(IReadOnlyList<VectorPoint> points, double tolerance)
    {
        if (points.Count <= 2) return points;
        var indices = SimplifyIndices(points, tolerance);
        var kept = new List<VectorPoint>(indices.Count);
        foreach (var index in indices) kept.Add(points[index]);
        return kept;
    }

    /// <summary>
    /// The indices Ramer–Douglas–Peucker keeps for the given world tolerance: no dropped point
    /// lies further than <paramref name="tolerance"/> from the kept course. Exposed so an existing
    /// channel can be simplified without losing the widths and ids of the nodes that survive.
    /// </summary>
    public static IReadOnlyList<int> SimplifyIndices(IReadOnlyList<VectorPoint> points, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count <= 2) return [.. Enumerable.Range(0, points.Count)];
        var keep = new bool[points.Count]; keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>(); stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            var best = -1; var bestDistance = tolerance;
            var ax = points[a].X; var ay = points[a].Y; var dx = points[b].X - ax; var dy = points[b].Y - ay; var l2 = dx * dx + dy * dy;
            for (var i = a + 1; i < b; i++)
            {
                var t = l2 < 1e-12 ? 0 : Math.Clamp(((points[i].X - ax) * dx + (points[i].Y - ay) * dy) / l2, 0, 1);
                var d = Math.Sqrt(Math.Pow(points[i].X - ax - dx * t, 2) + Math.Pow(points[i].Y - ay - dy * t, 2));
                if (d > bestDistance) { bestDistance = d; best = i; }
            }
            if (best >= 0) { keep[best] = true; stack.Push((a, best)); stack.Push((best, b)); }
        }
        var result = new List<int>();
        for (var i = 0; i < points.Count; i++) if (keep[i]) result.Add(i);
        return result;
    }
}
