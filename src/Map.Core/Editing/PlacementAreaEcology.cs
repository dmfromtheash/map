using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// Optional catalog-driven post-filter. It never moves a surviving slot. The command stores its
/// verdict in the existing RuleSlots, so opening a map needs neither the catalog nor this policy.
/// </summary>
public sealed class PlacementAreaEcology(MapDocument document, PlacementArea area, bool forest)
{
    private readonly PlacementAreaRuleCheck _water = new(document, new() { AvoidWater = true });

    public bool Blocks(PlacementAreaSlot slot, SceneObject item)
    {
        var foot = PlacementAreaRuleCheck.Foot(item);
        if (!forest) return !HasCliff(foot.X, foot.Y, area.Request.BaseWidth);
        if (_water.Blocks(item)) return true;
        var edge = EdgeDistance(foot.X, foot.Y);
        var centre = Math.Clamp(edge / (area.Request.BaseWidth * 2), 0, 1);
        var probability = .22 + .43 * centre;
        var reach = area.Request.BaseWidth * 3;
        // Existing rivers, lakes and sea only. No synthetic humidity or elevation field.
        if (NearWater(foot.X, foot.Y, reach)) probability = Math.Min(.98, probability + .30);
        var draw = PlacementAreaPlanner.Unit(PlacementAreaPlanner.Mix(PlacementAreaPlanner.Random(area, slot) ^ 0x52423131UL));
        return draw >= probability;
    }

    private bool NearWater(double x, double y, double reach)
    {
        for (var i = 0; i < 12; i++)
        {
            var angle = i * Math.Tau / 12;
            for (var r = 1; r <= 3; r++)
                if (_water.IsWater(x + Math.Cos(angle) * reach * r / 3, y + Math.Sin(angle) * reach * r / 3)) return true;
        }
        return false;
    }

    /// <summary>Across a rock's reach the painted heights differ by a third of a tier: the same «steep» as the geography check.</summary>
    public const double SteepRise = 0.33;

    private readonly TerrainProbe _terrain = new(document);

    private bool HasCliff(double x, double y, double reach)
    {
        var land = TerrainOrder.LandAt(document, x, y);
        if (land is null) return false;
        if (land.EdgeStyle.Cliff.Height > 0)
            for (var i = 0; i < 12; i++)
            {
                var angle = i * Math.Tau / 12;
                var neighbour = TerrainOrder.LandAt(document, x + Math.Cos(angle) * reach, y + Math.Sin(angle) * reach);
                if (land.Level > (neighbour?.Level ?? 0)) return true;
            }
        return IsSteep(land, x, y, reach);
    }

    // R11c: a steep place of the land's own height field (R22), with no tier edge near. Only heights the owner
    // painted count: opposite points of the same land, so a tier step with its cliff switched off stays flat.
    private bool IsSteep(LandLayer land, double x, double y, double reach)
    {
        if (land.ReliefTiles.TileCount == 0) return false;
        for (var i = 0; i < 6; i++)
        {
            var angle = i * Math.Tau / 12;
            double dx = Math.Cos(angle) * reach, dy = Math.Sin(angle) * reach;
            var (near, low) = _terrain.At(x - dx, y - dy);
            var (far, high) = _terrain.At(x + dx, y + dy);
            if (ReferenceEquals(near, land) && ReferenceEquals(far, land) && Math.Abs(high - low) >= SteepRise) return true;
        }
        return false;
    }

    private double EdgeDistance(double x, double y)
    {
        var distance = double.MaxValue;
        for (var i = 0; i < area.Nodes.Length; i++)
        {
            var a = area.Nodes[i]; var b = area.Nodes[(i + 1) % area.Nodes.Length];
            var dx = b.X - a.X; var dy = b.Y - a.Y; var length2 = dx * dx + dy * dy;
            var t = length2 == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length2, 0, 1);
            distance = Math.Min(distance, Math.Sqrt(Math.Pow(x - a.X - t * dx, 2) + Math.Pow(y - a.Y - t * dy, 2)));
        }
        return distance;
    }
}
