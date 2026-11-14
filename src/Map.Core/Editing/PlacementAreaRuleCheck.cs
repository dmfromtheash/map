using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Map.Core.Documents;

namespace Map.Core.Editing
{
    /// <summary>
    /// The map queries behind <see cref="PlacementAreaRules"/>. Immutable feature arrays share
    /// spatial lookups across commands and object-only edits; probes evaluate local candidates.
    /// </summary>
    public sealed class PlacementAreaRuleCheck
    {
        private readonly MapDocument _document;
        private readonly PlacementAreaRules _rules;
        private readonly bool _hasLand;
        private readonly ObjectLayer[] _layers;
        private static readonly ConditionalWeakTable<River[], Lazy<ObjectSpatialIndex.BoundsGrid<RiverGeometrySet>>> Rivers = new();
        private static readonly ConditionalWeakTable<Road[], Lazy<ObjectSpatialIndex.BoundsGrid<RoadGeometrySet>>> Roads = new();

        private static ObjectSpatialIndex.BoundsGrid<RiverGeometrySet> RiverIndex(ImmutableArray<River> rivers) =>
            Rivers.GetValue(ImmutableCollectionsMarshal.AsArray(rivers)!, static items => new(() => new(items
                .Where(r => r.Visible).Select(r => { var g = RiverGeometry.Evaluate(r); return (g, g.Bounds); })))).Value;

        private static ObjectSpatialIndex.BoundsGrid<RoadGeometrySet> RoadIndex(ImmutableArray<Road> roads) =>
            Roads.GetValue(ImmutableCollectionsMarshal.AsArray(roads)!, static items => new(() => new(items
                .Where(r => r.Visible).Select(r => { var g = RoadGeometry.Evaluate(r); return (g, g.Bounds); })))).Value;

        internal static void Prepare(ObjectLayer layer)
        {
            if (!layer.Rivers.IsDefaultOrEmpty) _ = RiverIndex(layer.Rivers);
            if (!layer.Roads.IsDefaultOrEmpty) _ = RoadIndex(layer.Roads);
        }

        internal static bool IsPrepared(ObjectLayer layer) =>
            (layer.Rivers.IsDefaultOrEmpty || Rivers.TryGetValue(ImmutableCollectionsMarshal.AsArray(layer.Rivers)!, out var rivers) && rivers.IsValueCreated)
            && (layer.Roads.IsDefaultOrEmpty || Roads.TryGetValue(ImmutableCollectionsMarshal.AsArray(layer.Roads)!, out var roads) && roads.IsValueCreated);

        public PlacementAreaRuleCheck(MapDocument document, PlacementAreaRules rules)
        {
            _document = document;
            _rules = rules;
            _hasLand = document.Layers.OfType<LandLayer>().Any(l => l.Visible);
            _layers = document.Layers.OfType<ObjectLayer>().Where(l => l.Visible).ToArray();
            foreach (var layer in _layers)
            {
                if ((rules.AvoidWater || rules.OnlyOnWater) && !layer.Rivers.IsDefaultOrEmpty) _ = RiverIndex(layer.Rivers);
                if (rules.AvoidRoads && !layer.Roads.IsDefaultOrEmpty) _ = RoadIndex(layer.Roads);
            }
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
            if (_rules.AvoidRoads && OnRoad(x, y)) return false;
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

            if (_rules.AvoidRoads && OnRoad(x, y)) return false;
            return true;
        }

        private bool OnRoad(double x, double y)
        {
            var probe = new WorldBounds(x - _rules.RoadMargin, y - _rules.RoadMargin, x + _rules.RoadMargin, y + _rules.RoadMargin);
            foreach (var layer in _layers)
                if (!layer.Roads.IsDefaultOrEmpty)
                    foreach (var road in RoadIndex(layer.Roads).Query(probe))
                        if (RoadGeometry.TryClosest(road, x, y, _rules.RoadMargin, out _, out _, out _, out _, out _)) return true;
            return false;
        }

        /// <summary>A river, a lake, or the open sea - the page where no land lies (on maps that have land).</summary>
        public bool IsWater(double x, double y)
        {
            // IsWater historically consults rivers only when a water rule was requested.
            if (_rules.AvoidWater || _rules.OnlyOnWater)
            {
                var probe = new WorldBounds(x - 1, y - 1, x + 1, y + 1);
                foreach (var layer in _layers)
                {
                    if (layer.Rivers.IsDefaultOrEmpty) continue;
                    foreach (var river in RiverIndex(layer.Rivers).Query(probe))
                    {
                        if (RiverGeometry.TryClosest(river, x, y, 0, out _, out _, out _, out _, out _)) return true;
                        if (river.Channels.Values.Any(g => RiverGeometry.InsideLake(g, x, y))) return true;
                    }
                }
            }

            return _hasLand && TerrainOrder.LandAt(_document, x, y) is null;
        }
    }
}
