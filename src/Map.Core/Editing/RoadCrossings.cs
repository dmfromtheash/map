using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// One place where a road branch meets a river channel: position, both directions and both widths
/// there, the crossing's ordinal along the branch for that channel, and the kind in effect.
/// Derived from the geometry every time, so it follows every node the user moves (R06, T3).
/// </summary>
public sealed record RoadCrossing(
    Guid RoadId, Guid BranchId, Guid RiverId, Guid ChannelId, int Ordinal,
    double X, double Y,
    double RoadTangentX, double RoadTangentY, double RoadHalfWidth,
    double RiverTangentX, double RiverTangentY, double RiverHalfWidth,
    double Distance, RoadCrossingKind Kind)
{
    /// <summary>The length of the crossing measured along the road: the river's width widened by the angle.</summary>
    public double SpanAlongRoad
    {
        get
        {
            // sin of the angle between the road and the river: 1 when they cross squarely.
            var cross = Math.Abs(RoadTangentX * RiverTangentY - RoadTangentY * RiverTangentX);
            var sine = Math.Max(0.3, cross);
            return 2 * RiverHalfWidth / sine;
        }
    }
}

/// <summary>Finds the crossings of roads over rivers from their evaluated geometries.</summary>
public static class RoadCrossings
{
    /// <summary>Every crossing of the given roads over the given rivers; lakes (closed channels) are not crossed.</summary>
    public static IReadOnlyList<RoadCrossing> Detect(IReadOnlyList<Road> roads, IReadOnlyList<River> rivers)
    {
        var result = new List<RoadCrossing>();
        if (roads.Count == 0 || rivers.Count == 0) return result;
        var riverSets = new List<RiverGeometrySet>();
        foreach (var river in rivers) if (river.Visible) riverSets.Add(RiverGeometry.Evaluate(river));
        foreach (var road in roads)
        {
            if (!road.Visible) continue;
            var set = RoadGeometry.Evaluate(road);
            foreach (var riverSet in riverSets)
            {
                if (!set.Bounds.Intersects(riverSet.Bounds)) continue;
                foreach (var branch in set.Branches.Values)
                {
                    if (branch.Samples.Length < 2) continue;
                    foreach (var channel in riverSet.Channels.Values)
                    {
                        if (channel.Closed || channel.Samples.Length < 2) continue;
                        var hits = new List<RoadCrossing>();
                        FindHits(road, branch, riverSet.River, channel, hits);
                        if (hits.Count == 0) continue;
                        hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                        for (var ordinal = 0; ordinal < hits.Count; ordinal++)
                        {
                            var hit = hits[ordinal];
                            result.Add(hit with { Ordinal = ordinal, Kind = KindOf(road, hit.BranchId, hit.ChannelId, ordinal) });
                        }
                    }
                }
            }
        }
        return result;
    }

    /// <summary>The kind stored for this crossing, or the default bridge.</summary>
    public static RoadCrossingKind KindOf(Road road, Guid branchId, Guid channelId, int ordinal)
    {
        foreach (var item in road.CrossingOverrides)
            if (item.BranchId == branchId && item.ChannelId == channelId && item.Ordinal == ordinal) return item.Kind;
        return RoadCrossingKind.Bridge;
    }

    private static void FindHits(Road road, RoadBranchGeometry branch, River river, RiverChannelGeometry channel, List<RoadCrossing> hits)
    {
        var rs = branch.Samples;
        var cs = channel.Samples;
        var channelLeft = double.MaxValue; var channelTop = double.MaxValue; var channelRight = double.MinValue; var channelBottom = double.MinValue;
        foreach (var s in cs)
        {
            channelLeft = Math.Min(channelLeft, s.X); channelRight = Math.Max(channelRight, s.X);
            channelTop = Math.Min(channelTop, s.Y); channelBottom = Math.Max(channelBottom, s.Y);
        }
        double lastDistance = double.NegativeInfinity;
        for (var i = 0; i + 1 < rs.Length; i++)
        {
            var a = rs[i]; var b = rs[i + 1];
            var left = Math.Min(a.X, b.X); var right = Math.Max(a.X, b.X);
            var top = Math.Min(a.Y, b.Y); var bottom = Math.Max(a.Y, b.Y);
            if (right < channelLeft || left > channelRight || bottom < channelTop || top > channelBottom) continue;
            for (var j = 0; j + 1 < cs.Length; j++)
            {
                var c = cs[j]; var d = cs[j + 1];
                if (Math.Max(c.X, d.X) < left || Math.Min(c.X, d.X) > right || Math.Max(c.Y, d.Y) < top || Math.Min(c.Y, d.Y) > bottom) continue;
                if (!Intersect(a.X, a.Y, b.X, b.Y, c.X, c.Y, d.X, d.Y, out var t, out var u)) continue;
                var distance = a.Distance + t * (b.Distance - a.Distance);
                // The same point reached at a shared sample of two segments counts once.
                if (distance - lastDistance < 1e-6) continue;
                lastDistance = distance;
                var x = a.X + t * (b.X - a.X); var y = a.Y + t * (b.Y - a.Y);
                var (rtx, rty) = Unit(a.TangentX + t * (b.TangentX - a.TangentX), a.TangentY + t * (b.TangentY - a.TangentY), b.X - a.X, b.Y - a.Y);
                var (ctx, cty) = Unit(c.TangentX + u * (d.TangentX - c.TangentX), c.TangentY + u * (d.TangentY - c.TangentY), d.X - c.X, d.Y - c.Y);
                hits.Add(new RoadCrossing(road.Id, branch.Branch.Id, river.Id, channel.Channel.Id, 0, x, y,
                    rtx, rty, a.HalfWidth + t * (b.HalfWidth - a.HalfWidth),
                    ctx, cty, c.HalfWidth + u * (d.HalfWidth - c.HalfWidth),
                    distance, RoadCrossingKind.Bridge));
            }
        }
    }

    private static (double X, double Y) Unit(double x, double y, double fallbackX, double fallbackY)
    {
        var length = Math.Sqrt(x * x + y * y);
        if (length < 1e-9) { x = fallbackX; y = fallbackY; length = Math.Sqrt(x * x + y * y); }
        return length < 1e-9 ? (1, 0) : (x / length, y / length);
    }

    /// <summary>Segment intersection; t along the first segment, u along the second, both in [0, 1].</summary>
    private static bool Intersect(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy, out double t, out double u)
    {
        var rx = bx - ax; var ry = by - ay; var sx = dx - cx; var sy = dy - cy;
        var denominator = rx * sy - ry * sx;
        t = u = 0;
        if (Math.Abs(denominator) < 1e-12) return false;
        var qx = cx - ax; var qy = cy - ay;
        t = (qx * sy - qy * sx) / denominator;
        u = (qx * ry - qy * rx) / denominator;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1;
    }
}
