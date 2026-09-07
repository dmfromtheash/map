using System.Numerics;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>Affine sampling of shared 32x32 silhouettes; no image buffers or UI dependency.</summary>
internal sealed class PlacementAreaSilhouette
{
    private readonly StampContentMask _mask;
    private readonly double _ax, _ay, _bx, _by, _x, _y, _det, _protectedY;
    private PlacementAreaSilhouette(StampContentMask mask, SceneObject item, WorldBounds visible)
    {
        _mask = mask; Bottom = visible.Bottom; CenterX = (visible.Left + visible.Right) / 2; Id = item.Id.Value;
        var angle = item.RotationDegrees * Math.PI / 180; var cos = Math.Cos(angle); var sin = Math.Sin(angle);
        var flipX = item.Stamp!.FlipHorizontal ? -1 : 1; var flipY = item.Stamp.FlipVertical ? -1 : 1;
        _ax = item.Width * cos * flipX; _ay = item.Width * sin * flipX;
        _bx = -item.Height * sin * flipY; _by = item.Height * cos * flipY;
        _x = item.X + item.Width / 2 - (_ax + _bx) / 2; _y = item.Y + item.Height / 2 - (_ay + _by) / 2;
        _det = _ax * _by - _ay * _bx;
        _protectedY = visible.Top + (visible.Bottom - visible.Top) * .30;
    }
    public double Bottom { get; }
    private double CenterX { get; }
    private Guid Id { get; }
    public static PlacementAreaSilhouette? Create(PlacementArea area, SceneObject item, WorldBounds visible) =>
        area.LayoutVersion >= 4 && area.JoinEdges && item.Stamp is { Blur: 0, ShadowOpacity: 0, ClipMaskEnabled: false } stamp
        && item.StrokeWidth == 0 && area.ContentMasks.TryGetValue(stamp.AssetSha256, out var mask)
            ? new(mask, item, visible) : null;

    public static bool Conflicts(PlacementAreaSilhouette a, PlacementAreaSilhouette b)
    {
        var order = a.Bottom.CompareTo(b.Bottom);
        if (order == 0) order = a.CenterX.CompareTo(b.CenterX);
        if (order == 0) order = a.Id.CompareTo(b.Id);
        var (covered, upper) = order <= 0 ? Coverage(a, b) : Coverage(b, a);
        // Bases and lower crowns may interlock, but the peak/canopy at the back stays legible.
        return covered > .60 || upper > .32;
    }
    private static (double Covered, double Upper) Coverage(PlacementAreaSilhouette back, PlacementAreaSilhouette front)
    {
        var total = 0; var covered = 0; var upper = 0; var upperCovered = 0;
        // Transform the rear image's normalized grid directly into the front image's grid.
        var ax = (back._ax * front._by - back._ay * front._bx) / front._det;
        var bx = (back._bx * front._by - back._by * front._bx) / front._det;
        var ay = (back._ay * front._ax - back._ax * front._ay) / front._det;
        var by = (back._by * front._ax - back._bx * front._ay) / front._det;
        var dx = back._x - front._x; var dy = back._y - front._y;
        var x = (dx * front._by - dy * front._bx) / front._det;
        var y = (dy * front._ax - dx * front._ay) / front._det;
        const int edge = StampContentMask.Edge;
        for (var row = 0; row < edge; row++)
        {
            var bits = back._mask.Rows[row]; var v = (row + .5) / edge;
            while (bits != 0)
            {
                var column = BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
                var u = (column + .5) / edge;
                var px = (int)Math.Floor((ax * u + bx * v + x) * edge);
                var py = (int)Math.Floor((ay * u + by * v + y) * edge);
                var hit = (uint)px < edge && (uint)py < edge && (front._mask.Rows[py] & (1u << px)) != 0;
                total++; if (hit) covered++;
                if (back._ay * u + back._by * v + back._y <= back._protectedY)
                { upper++; if (hit) upperCovered++; }
            }
        }
        return ((double)covered / total, upper == 0 ? 0 : (double)upperCovered / upper);
    }
}
