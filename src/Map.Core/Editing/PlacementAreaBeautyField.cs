using System.Globalization;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// The fields behind <see cref="PlacementAreaBeauty"/> and the density brush (R10). Everything is a
/// pure function of the world position and the area's seed: no raster is built and nothing depends
/// on neighbouring decisions, so a filter only removes or returns a slot and a finished object only
/// changes its own size and colour. Spacing (who conflicts with whom) is decided on the unfinished
/// candidates and therefore never depends on the border, the noise or the brush.
/// </summary>
internal sealed class PlacementAreaBeautyField
{
    private readonly PlacementArea _area;
    private readonly PlacementAreaBeauty _beauty;
    private readonly PlacementAreaDensityMask? _mask;
    private readonly double[] _edges;
    private readonly ulong _seed;

    private PlacementAreaBeautyField(PlacementArea area)
    {
        _area = area;
        _beauty = area.Beauty ?? new PlacementAreaBeauty();
        _mask = area.DensityMask;
        _seed = PlacementAreaPlanner.Mix((uint)area.Seed ^ 0x5bd1e995UL);
        _edges = new double[area.Nodes.Length * 4];
        for (var i = 0; i < area.Nodes.Length; i++)
        {
            var a = area.Nodes[i]; var b = area.Nodes[(i + 1) % area.Nodes.Length];
            _edges[i * 4] = a.X; _edges[i * 4 + 1] = a.Y; _edges[i * 4 + 2] = b.X; _edges[i * 4 + 3] = b.Y;
        }
    }

    /// <summary>Null when the area has nothing to add: then it fills and draws as before R10 «Красота».</summary>
    public static PlacementAreaBeautyField? For(PlacementArea area) =>
        area.LayoutVersion >= 3 && (area.Beauty is { Any: true } || area.DensityMask is not null) ? new(area) : null;

    public bool ChangesDensity => _beauty.SoftEdge || _beauty.Clumps && _beauty.ClumpStrength > 0 || _mask is not null;

    /// <summary>The multiplier of the area's density at a world point: soft edge × clumps × brush.</summary>
    public double DensityFactor(double x, double y)
    {
        var factor = 1d;
        if (_beauty.SoftEdge) factor *= Edge(x, y);
        if (_beauty.Clumps && _beauty.ClumpStrength > 0)
        {
            var strength = _beauty.ClumpStrength / 100;
            var n = SmoothStep(.28, .72, Fbm(x / _beauty.ClumpSize, y / _beauty.ClumpSize, 0x2c1b3c6dUL));
            factor *= Math.Max(0, 1 + strength * (2 * n - 1));
        }
        if (_mask is not null) factor *= _mask.Factor(x, y);
        return factor;
    }

    /// <summary>0 on the border, 1 at <see cref="PlacementAreaBeauty.EdgeWidth"/> inside and deeper.</summary>
    public double Edge(double x, double y)
    {
        var best = double.MaxValue;
        for (var i = 0; i < _edges.Length; i += 4)
        {
            var ax = _edges[i]; var ay = _edges[i + 1]; var dx = _edges[i + 2] - ax; var dy = _edges[i + 3] - ay;
            var length = dx * dx + dy * dy;
            var t = length <= 0 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / length, 0, 1);
            var ex = ax + t * dx - x; var ey = ay + t * dy - y;
            best = Math.Min(best, ex * ex + ey * ey);
        }
        return SmoothStep(0, _beauty.EdgeWidth, Math.Sqrt(best));
    }

    /// <summary>
    /// «Рощи одного вида»: the variant of a slot comes from the nearest of a jittered lattice of
    /// patch centres; a few slots on every patch keep their own random kind, so borders mingle.
    /// </summary>
    public int? PatchVariant(double x, double y, PlacementAreaSlot slot, int count)
    {
        if (!_beauty.SpeciesPatches || count < 2) return null;
        if (PlacementAreaPlanner.Unit(PlacementAreaPlanner.Mix(PlacementAreaPlanner.Random(_area, slot) ^ 0x51f7UL)) < .14) return null;
        var size = Math.Max(10, _beauty.ClumpSize * .85);
        var fx = x / size; var fy = y / size;
        var cx = (int)Math.Floor(fx); var cy = (int)Math.Floor(fy);
        var best = double.MaxValue; var kind = 0;
        for (var j = cy - 1; j <= cy + 1; j++)
        for (var i = cx - 1; i <= cx + 1; i++)
        {
            var h = Hash(i, j, 0x6a09e667UL);
            var px = i + .15 + .7 * PlacementAreaPlanner.Unit(h); var py = j + .15 + .7 * PlacementAreaPlanner.Unit(PlacementAreaPlanner.Mix(h));
            var d = (px - fx) * (px - fx) + (py - fy) * (py - fy);
            if (d < best) { best = d; kind = (int)(PlacementAreaPlanner.Mix(h ^ 0x3c6ef372UL) % (ulong)count); }
        }
        return kind;
    }

    /// <summary>The finished object: smaller near the border, its colour varied, its contact shadow.</summary>
    public SceneObject Finish(SceneObject item, PlacementAreaSlot slot)
    {
        if (item.Stamp is not { } stamp) return item;
        var cx = item.X + item.Width / 2; var cy = item.Y + item.Height / 2;
        if (_beauty.SoftEdge)
        {
            var scale = .6 + .4 * Edge(cx, cy);
            if (scale < .999)
            {
                var width = item.Width * scale; var height = item.Height * scale;
                item = SceneObjectClone.At(item, cx - width / 2, cy - height / 2, width, height);
                stamp = stamp with { ReferenceWidth = width / (_area.ScalePercent / 100), ReferenceHeight = height / (_area.ScalePercent / 100) };
            }
        }
        if (_beauty.ColorVariation && _beauty.ColorVariationPercent > 0) stamp = stamp with { Tint = Tint(cx, cy, slot) };
        var shadow = _beauty.GroundShadow ? _beauty.ShadowOpacity : 0;
        var mass = _beauty.Underlay ? _beauty.UnderlayOpacity : 0;
        if (shadow > 0 || mass > 0) stamp = stamp with { GroundShadow = Shadow(stamp, shadow, mass) };
        return item with { Stamp = stamp };
    }

    private string Tint(double x, double y, PlacementAreaSlot slot)
    {
        var p = _beauty.ColorVariationPercent / 100;
        var size = Math.Max(10, _beauty.ClumpSize * .6);
        var own = PlacementAreaPlanner.Unit(PlacementAreaPlanner.Mix(PlacementAreaPlanner.Random(_area, slot) ^ 0x7e57UL)) * 2 - 1;
        var light = Math.Clamp((Fbm(x / size, y / size, 0x1f83d9abUL) * 2 - 1) * 1.6 * .7 + own * .3, -1, 1);
        var warm = Math.Clamp((Fbm(x / size, y / size, 0x5be0cd19UL) * 2 - 1) * 1.6, -1, 1);
        // Multiplying can only darken, so the neutral point is 1 - p: ±p around it, warmth ±p/2.
        var m = 1 - p + p * light;
        static byte Channel(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return string.Create(CultureInfo.InvariantCulture,
            $"#FF{Channel(m + p * .5 * warm):X2}{Channel(m):X2}{Channel(m - p * .5 * warm):X2}");
    }

    private StampGroundShadow Shadow(StampStyle stamp, double opacity, double mass)
    {
        var content = _area.ContentBounds.GetValueOrDefault(stamp.AssetSha256, StampContentBounds.Full);
        var footX = (content.Left + content.Right) / 2;
        var footY = content.Bottom;
        if (stamp.FlipHorizontal) footX = 1 - footX;
        if (stamp.FlipVertical) footY = 1 - content.Top;
        return new(opacity, Math.Clamp(footX, 0, 1), Math.Clamp(footY, 0, 1), Math.Clamp(content.Width * .9, .05, 1), mass);
    }

    private ulong Hash(int x, int y, ulong salt) => PlacementAreaPlanner.Mix(
        PlacementAreaPlanner.Mix(unchecked((ulong)(uint)x) ^ (salt * 0x9e3779b97f4a7c15UL))
        ^ PlacementAreaPlanner.Mix(unchecked((ulong)(uint)y) + 0x632be59bd9b4e019UL) ^ _seed);

    private double Value(double x, double y, ulong salt)
    {
        var ix = (int)Math.Floor(x); var iy = (int)Math.Floor(y);
        var tx = Fade(x - ix); var ty = Fade(y - iy);
        double At(int i, int j) => PlacementAreaPlanner.Unit(Hash(i, j, salt));
        var top = At(ix, iy) + (At(ix + 1, iy) - At(ix, iy)) * tx;
        var bottom = At(ix, iy + 1) + (At(ix + 1, iy + 1) - At(ix, iy + 1)) * tx;
        return top + (bottom - top) * ty;
    }

    /// <summary>Two octaves of value noise, 0..1, about one feature per unit.</summary>
    private double Fbm(double x, double y, ulong salt)
    {
        // Turned lattices: plain value noise shows its axis-aligned cells as straight edges of
        // groves once the contrast is raised.
        var u = x * .8 - y * .6; var v = x * .6 + y * .8;
        var p = x * .28 + y * .96; var q = -x * .96 + y * .28;
        return .65 * Value(u, v, salt) + .35 * Value(p * 2.03 + 17.1, q * 2.03 - 9.7, salt ^ 0xa54ff53aUL);
    }

    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    private static double SmoothStep(double edge0, double edge1, double x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
