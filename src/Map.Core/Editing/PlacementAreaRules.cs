using System.Text.Json.Serialization;
using Map.Core.Documents;

namespace Map.Core.Documents
{
    /// <summary>
    /// Where an area may put its assets (R10). Each rule has its own switch and every switch is off
    /// by default, so an area without rules - every area of an older map - fills exactly as before.
    /// A rule only removes slots; it never moves the ones that stay.
    /// </summary>
    public sealed record PlacementAreaRules
    {
        public const double MaxRoadMargin = 2000;

        /// <summary>«Не на воду»: no asset on a river, a lake or the open sea.</summary>
        public bool AvoidWater { get; init; }

        /// <summary>«Только на воду»: the reverse, for reeds, water lilies, rocks in the sea.</summary>
        public bool OnlyOnWater { get; init; }

        /// <summary>«Не на дорогу»: no asset on a road or within <see cref="RoadMargin"/> of it.</summary>
        public bool AvoidRoads { get; init; }

        /// <summary>Extra clearance beside a road, in canvas units.</summary>
        public double RoadMargin { get; init; } = 8;

        /// <summary>«Только на суше»: on land; with <see cref="LandLayerId"/> only on that piece of land.</summary>
        public bool OnlyOnLand { get; init; }

        public LayerId? LandLayerId { get; init; }

        /// <summary>Bumped by «Пересчитать по карте» after a river, road or land edit, to fill the area again.</summary>
        public int Revision { get; init; }

        [JsonIgnore] public bool Any => AvoidWater || OnlyOnWater || AvoidRoads || OnlyOnLand;

        public void Validate()
        {
            if (!double.IsFinite(RoadMargin) || RoadMargin < 0 || RoadMargin > MaxRoadMargin)
                throw new ArgumentException("Отступ от дороги — от 0 до 2000.");
            if (AvoidWater && OnlyOnWater) throw new ArgumentException("Нельзя одновременно «не на воду» и «только на воду».");
            if (OnlyOnLand && OnlyOnWater) throw new ArgumentException("Нельзя одновременно «только на суше» и «только на воду».");
        }
    }
}

namespace Map.Core.Editing
{
    /// <summary>
    /// The map queries behind <see cref="PlacementAreaRules"/>, prepared once per area command:
    /// river and road geometry is evaluated a single time, not per slot.
    /// </summary>
    public sealed class PlacementAreaRuleCheck
    {
        private readonly MapDocument _document;
        private readonly PlacementAreaRules _rules;
        private readonly bool _hasLand;
        private readonly RiverGeometrySet[] _rivers;
        private readonly RoadGeometrySet[] _roads;

        public PlacementAreaRuleCheck(MapDocument document, PlacementAreaRules rules)
        {
            _document = document;
            _rules = rules;
            _hasLand = document.Layers.OfType<LandLayer>().Any(l => l.Visible);
            var layers = document.Layers.OfType<ObjectLayer>().Where(l => l.Visible).ToArray();
            _rivers = rules.AvoidWater || rules.OnlyOnWater
                ? [.. layers.SelectMany(l => l.Rivers).Where(r => r.Visible).Select(r => RiverGeometry.Evaluate(r))] : [];
            _roads = rules.AvoidRoads ? [.. layers.SelectMany(l => l.Roads).Where(r => r.Visible).Select(r => RoadGeometry.Evaluate(r))] : [];
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
                foreach (var road in _roads)
                    if (road.Bounds.Intersects(new WorldBounds(x - _rules.RoadMargin, y - _rules.RoadMargin, x + _rules.RoadMargin, y + _rules.RoadMargin))
                        && RoadGeometry.TryClosest(road, x, y, _rules.RoadMargin, out _, out _, out _, out _, out _)) return false;
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
                foreach (var road in _roads)
                    if (road.Bounds.Intersects(new WorldBounds(x - _rules.RoadMargin, y - _rules.RoadMargin, x + _rules.RoadMargin, y + _rules.RoadMargin))
                        && RoadGeometry.TryClosest(road, x, y, _rules.RoadMargin, out _, out _, out _, out _, out _)) return false;
            return true;
        }

        /// <summary>A river, a lake, or the open sea - the page where no land lies (on maps that have land).</summary>
        private bool IsWater(double x, double y)
        {
            foreach (var river in _rivers)
            {
                if (!river.Bounds.Intersects(new WorldBounds(x - 1, y - 1, x + 1, y + 1))) continue;
                if (RiverGeometry.TryClosest(river, x, y, 0, out _, out _, out _, out _, out _)) return true;
                if (river.Channels.Values.Any(g => RiverGeometry.InsideLake(g, x, y))) return true;
            }

            return _hasLand && TerrainOrder.LandAt(_document, x, y) is null;
        }
    }
}
