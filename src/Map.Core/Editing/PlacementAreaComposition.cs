using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// Version 3: a stationary composition of bent, asymmetric groves with smaller fringe stamps.
/// Its origin is frozen on creation, so changing an edge or density cannot move the composition.
/// This evaluates a few numbers per candidate; there are no raster masks or per-map pixel caches.
/// </summary>
internal static class PlacementAreaComposition
{
    public static double MaximumScale(PlacementArea area) => 1 + area.SizeVariationPercent / 100;
    internal readonly record struct Sample(bool Eligible, double Scale, ulong Priority, double Activation, VectorPoint? Point = null);

    private static (double Width, double Height) Size(PlacementArea area, StampVariantSource variant)
    {
        var bounds = area.ContentBounds.GetValueOrDefault(variant.Sha256, StampContentBounds.Full);
        return (Math.Max(1, area.Request.BaseWidth * bounds.Width),
            Math.Max(1, area.Request.BaseWidth * variant.PixelHeight / variant.PixelWidth * bounds.Height));
    }

    public static int Passes(PlacementArea area)
    {
        var surface = area.Variants.Min(v => { var size = Size(area, v); return size.Width * size.Height; });
        // A fixed full-density layout, with candidate work proportional to the number of stamps
        // that can physically fit. Density chooses from this layout, not more failed probes.
        return (int)Math.Clamp(Math.Ceiling(24 * PlacementAreaGeometry.CellSize * PlacementAreaGeometry.CellSize
            / surface), 2, int.MaxValue);
    }

    public static Sample At(PlacementArea area, PlacementAreaSlot slot)
    {
        var point = PlacementAreaPlanner.Anchor(area, slot);
        var width = area.Variants.Average(v => Size(area, v).Width);
        var height = area.Variants.Average(v => Size(area, v).Height);
        var x = (point.X - area.CompositionOrigin.X) / width;
        var y = (point.Y - area.CompositionOrigin.Y) / height;
        var strength = area.Pattern == PlacementAreaPattern.Ridge
            ? Ridge(area, point, width, height) : Groves(area, x, y);
        strength = Math.Clamp(strength, 0, 1);
        var random = PlacementAreaPlanner.Random(area, slot);
        double Unit(uint salt) => PlacementAreaPlanner.Unit(PlacementAreaPlanner.Mix(random ^ salt));
        var eligible = strength > .04 + .10 * Unit(91);
        var scale = 1 + area.SizeVariationPercent / 100 * (1.65 * strength + .35 * Unit(93) - 1);
        var priority = (ulong)((.7 * (1 - strength) + .3 * Unit(94)) * (ulong.MaxValue / 2));
        // Grow outward from the cores, with a little irregularity at the advancing fringe.
        // A fixed full layout keeps every survivor's size and position when density changes.
        var activation = Math.Clamp(100 * (.84 * (1 - strength) + .16 * Unit(95)), 1, 100);
        return new(eligible, scale, priority, activation);
    }

    private static double Groves(PlacementArea area, double x, double y)
    {
        var mass = area.Pattern == PlacementAreaPattern.Masses;
        var stepX = mass ? 9.0 : 6.0;
        var stepY = mass ? 7.3 : 5.1;
        var cellX = (int)Math.Floor(x / stepX + .5);
        var cellY = (int)Math.Floor(y / stepY + .5);
        var strength = 0d;
        for (var cy = cellY - 1; cy <= cellY + 1; cy++)
            for (var cx = cellX - 1; cx <= cellX + 1; cx++)
            {
                var bits = PlacementAreaPlanner.Random(area, new(cx, cy, 713));
                double Draw(uint salt) => PlacementAreaPlanner.Unit(PlacementAreaPlanner.Mix(bits ^ salt));
                // Keep one grove inside the initial polygon. Surrounding groves vary in location,
                // direction, length and thickness; no rows or identical circular islands.
                var dx = x - cx * stepX - (cx == 0 && cy == 0 ? 0 : (Draw(1) - .5) * 2.4);
                var dy = y - cy * stepY - (cx == 0 && cy == 0 ? 0 : (Draw(2) - .5) * 2.0);
                var angle = (Draw(3) - .5) * 1.2;
                var u = dx * Math.Cos(angle) + dy * Math.Sin(angle);
                var v = -dx * Math.Sin(angle) + dy * Math.Cos(angle);
                v += .45 * Math.Sin(u * .85 + Draw(4) * Math.Tau);
                var a = mass ? 5.2 + Draw(5) : 2.5 + Draw(5) * .65;
                var b = mass ? 3.6 + Draw(6) : 1.75 + Draw(6) * .55;
                // An off-centre shoulder and a curved spine break the ellipse's symmetry.
                b *= 1 + .18 * Math.Sin(u * 1.8 + Draw(7) * Math.Tau);
                strength = Math.Max(strength, 1 - u * u / (a * a) - v * v / (b * b));
            }
        return strength;
    }

    private static double Ridge(PlacementArea area, VectorPoint point, double width, double height)
    {
        var bits = PlacementAreaPlanner.Mix((uint)area.Seed);
        double Draw(uint salt) => PlacementAreaPlanner.Unit(PlacementAreaPlanner.Mix(bits ^ salt));
        var angle = area.CompositionAngleDegrees * Math.PI / 180;
        var dx = point.X - area.CompositionOrigin.X; var dy = point.Y - area.CompositionOrigin.Y;
        var unit = Math.Sqrt(width * height);
        var u = (dx * Math.Cos(angle) + dy * Math.Sin(angle)) / unit;
        var v = (-dx * Math.Sin(angle) + dy * Math.Cos(angle)) / unit;
        var halfLength = Math.Max(3, area.CompositionLength / unit * .52);
        var phase = Draw(8) * Math.Tau;
        double Spine(double t) => .7 * Math.Sin(t * .45 + phase) + .35 * Math.Sin(t * .9 - phase);
        var thickness = 1.35 + .35 * Math.Cos(u * .7 + phase);
        var distance = Math.Abs(v - Spine(u)) / thickness;
        var strength = 1 - distance * distance - Math.Pow(u / halfLength, 8) * .35;
        // Shorter, narrower offshoots share the spine and taper into small foothills.
        for (var branch = -1; branch <= 1; branch += 2)
        {
            var root = halfLength * (branch * .28 + (Draw((uint)(21 + branch)) - .5) * .18);
            var length = Math.Min(halfLength * .55, 4.2);
            var along = (v - Spine(root)) * branch;
            if (along < 0 || along > length) continue;
            var center = root + along * (.6 + Draw((uint)(31 + branch)) * .6);
            var radius = .85 * (1 - along / length) + .4;
            var side = (u - center) / radius;
            strength = Math.Max(strength, (.82 - .38 * along / length) * (1 - side * side));
        }
        return strength;
    }

    public static VectorPoint Origin(IReadOnlyList<PlacementPathNode> nodes)
    {
        // Called only for a valid draft / explicit upgrade, never while editing a stored boundary.
        var twiceArea = 0d; var x = 0d; var y = 0d;
        for (var i = 0; i < nodes.Count; i++)
        {
            var a = nodes[i]; var b = nodes[(i + 1) % nodes.Count];
            var cross = a.X * b.Y - b.X * a.Y;
            twiceArea += cross; x += (a.X + b.X) * cross; y += (a.Y + b.Y) * cross;
        }
        var center = new VectorPoint(x / (3 * twiceArea), y / (3 * twiceArea));
        if (PlacementAreaGeometry.Contains(nodes, center.X, center.Y)) return center;
        // A concave polygon's centroid can be outside: choose the middle of its widest interior span.
        var cuts = nodes.Select(n => n.Y).Distinct().Order().ToArray();
        var bestWidth = -1d;
        for (var row = 0; row < cuts.Length - 1; row++)
        {
            var middle = (cuts[row] + cuts[row + 1]) / 2; var intersections = new List<double>();
            for (var i = 0; i < nodes.Count; i++)
            {
                var a = nodes[i]; var b = nodes[(i + 1) % nodes.Count];
                if ((a.Y > middle) != (b.Y > middle))
                    intersections.Add(a.X + (middle - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
            intersections.Sort();
            for (var i = 0; i + 1 < intersections.Count; i += 2)
                if (intersections[i + 1] - intersections[i] > bestWidth)
                { bestWidth = intersections[i + 1] - intersections[i]; center = new((intersections[i] + intersections[i + 1]) / 2, middle); }
        }
        return center;
    }
}
