using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>World-space centreline of a hex path, following the renderer's edge/centre/meander curves.</summary>
public static class HexPathGeometry
{
    public static IReadOnlyList<VectorPoint> Flatten(HexPath path, HexLayout layout)
    {
        if (path.Style == HexRouteStyle.Edges)
            return [.. path.Vertices.Select(v => { var (x, y) = layout.Position(v); return new VectorPoint(x, y); })];
        if (path.Hexes.Length < 2) return [];
        var points = new List<VectorPoint>();
        VectorPoint Centre(HexCoord hex) { var (x, y) = layout.Center(hex); return new(x, y); }
        static VectorPoint Mid(VectorPoint a, VectorPoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        static VectorPoint Unit(VectorPoint a, VectorPoint b)
        {
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            return length < 1e-9 ? new(1, 0) : new(dx / length, dy / length);
        }
        static VectorPoint Along(VectorPoint p, VectorPoint direction, double distance) =>
            new(p.X + direction.X * distance, p.Y + direction.Y * distance);
        void Cubic(VectorPoint a, VectorPoint b, VectorPoint c, VectorPoint d)
        {
            // At most a small fraction of one world pixel from the drawn cubic, even at large hex sizes.
            var steps = Math.Max(12, (int)Math.Ceiling(Math.Sqrt(layout.Size) * 2));
            for (var j = 1; j <= steps; j++)
            {
                var t = j / (double)steps; var s = 1 - t;
                points.Add(new(s * s * s * a.X + 3 * s * s * t * b.X + 3 * s * t * t * c.X + t * t * t * d.X,
                    s * s * s * a.Y + 3 * s * s * t * b.Y + 3 * s * t * t * c.Y + t * t * t * d.Y));
            }
        }
        for (var i = 0; i < path.Hexes.Length; i++)
        {
            var centre = Centre(path.Hexes[i]);
            var previous = i == 0 ? centre : Centre(path.Hexes[i - 1]);
            var next = i == path.Hexes.Length - 1 ? centre : Centre(path.Hexes[i + 1]);
            var entry = i == 0 ? centre : Mid(previous, centre);
            var exit = i == path.Hexes.Length - 1 ? centre : Mid(centre, next);
            var across = Unit(entry, exit);
            var tin = i == 0 ? across : Unit(previous, centre);
            var tout = i == path.Hexes.Length - 1 ? across : Unit(centre, next);
            if (i == 0) points.Add(entry);
            if (path.Style == HexRouteStyle.Meander)
            {
                var noise = HexEditing.Noise(path.Hexes[i], path.Seed * 31 + 7);
                var sideways = ((noise & 0xFFFF) / 65535.0 - 0.5) * 0.62 * layout.Size;
                var mid = new VectorPoint(centre.X - across.Y * sideways, centre.Y + across.X * sideways);
                var k = layout.Size * 0.32;
                Cubic(entry, Along(entry, tin, k), Along(mid, across, -k), mid);
                Cubic(mid, Along(mid, across, k), Along(exit, tout, -k), exit);
            }
            else
            {
                var k = layout.Size * 0.42;
                Cubic(entry, Along(entry, tin, k), Along(exit, tout, -k), exit);
            }
        }
        return points;
    }
}
