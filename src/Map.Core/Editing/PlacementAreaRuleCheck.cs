using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing
{
    /// <summary>
    /// The map queries behind <see cref="PlacementAreaRules"/>. Prepared feature grids restrict
    /// each point probe to nearby rivers and roads; exact geometry predicates are unchanged.
    /// </summary>
    public sealed class PlacementAreaRuleCheck
    {
        private readonly MapDocument _document;
        private readonly PlacementAreaRules _rules;
        private readonly bool _hasLand;
        private readonly FeatureSpatialIndex<River>[] _rivers;
        private readonly FeatureSpatialIndex<Road>[] _roads;

        public PlacementAreaRuleCheck(MapDocument document, PlacementAreaRules rules)
        {
            _document = document;
            _rules = rules;
            _hasLand = document.Layers.OfType<LandLayer>().Any(l => l.Visible);
            var layers = document.Layers.OfType<ObjectLayer>().Where(l => l.Visible).ToArray();
            _rivers = rules.AvoidWater || rules.OnlyOnWater
                ? [.. layers.Select(l => l.PlacementRivers)] : [];
            _roads = rules.AvoidRoads ? [.. layers.Select(l => l.PlacementRoads)] : [];
        }

        /// <summary>
        /// The point that decides for a placed asset: the middle of its lower part, where a tree stands
        /// or a reed grows out of the water - not the top of its crown.
        /// </summary>
        public static VectorPoint Foot(SceneObject item) => new(item.X + item.Width / 2, item.Y + item.Height * .8);

        public bool Blocks(SceneObject item)
        {
            var foot = Foot(item);
            if (!Allows(foot.X, foot.Y)) return true;
            // «Не на воду» / «не на дорогу» look at the whole base of the asset, not one point:
            // a tree whose foot is just off the road must not stand with half its trunk on it.
            if (!_rules.AvoidWater && !_rules.AvoidRoads) return false;
            var left = item.X + item.Width * .2; var right = item.X + item.Width * .8;
            var top = item.Y + item.Height * .55; var bottom = item.Y + item.Height * .95;
            foreach (var (x, y) in new[] { (left, bottom), (right, bottom), (left, top), (right, top), (foot.X, top), (foot.X, bottom) })
                if (!AllowsAvoidance(x, y)) return true;
            return false;
        }

        private bool AllowsAvoidance(double x, double y)
        {
            if (_rules.AvoidWater && IsWater(x, y)) return false;
            if (_rules.AvoidRoads)
                if (OnRoad(x, y)) return false;
            return true;
        }

        public bool Allows(double x, double y)
        {
            if (_rules.AvoidWater || _rules.OnlyOnWater)
            {
                var water = IsWater(x, y);
                if (_rules.AvoidWater && water || _rules.OnlyOnWater && !water) return false;
            }

            if (_rules.OnlyOnLand)
            {
                var land = TerrainOrder.LandAt(_document, x, y);
                if (land is null && _hasLand || _rules.LandLayerId is { } id && land?.Id != id) return false;
            }

            if (_rules.AvoidRoads)
                if (OnRoad(x, y)) return false;
            return true;
        }

        private bool OnRoad(double x, double y)
        {
            var bounds = new WorldBounds(x - _rules.RoadMargin, y - _rules.RoadMargin, x + _rules.RoadMargin, y + _rules.RoadMargin);
            foreach (var index in _roads)
                if (index.Any(bounds, (x, y, _rules.RoadMargin), static (road, point) =>
                    RoadGeometry.TryClosest(RoadGeometry.Evaluate(road), point.x, point.y, point.RoadMargin,
                        out _, out _, out _, out _, out _))) return true;
            return false;
        }

        /// <summary>A river, a lake, or the open sea - the page where no land lies (on maps that have land).</summary>
        public bool IsWater(double x, double y)
        {
            foreach (var index in _rivers)
                if (index.Any(new WorldBounds(x - 1, y - 1, x + 1, y + 1), (x, y), static (candidate, point) =>
                {
                    var river = RiverGeometry.Evaluate(candidate);
                    if (RiverGeometry.TryClosest(river, point.x, point.y, 0, out _, out _, out _, out _, out _)) return true;
                    foreach (var channel in river.Channels.Values)
                        if (RiverGeometry.InsideLake(channel, point.x, point.y)) return true;
                    return false;
                })) return true;

            return _hasLand && TerrainOrder.LandAt(_document, x, y) is null;
        }
    }
}
