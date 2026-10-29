using System.Text.Json.Serialization;

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
