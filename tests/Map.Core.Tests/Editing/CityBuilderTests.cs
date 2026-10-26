using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>R34/R35: a generated town has streets, blocks, houses facing their streets inside blocks, a wall with gates, landmarks and a key; the same settings repeat.</summary>
public sealed class CityBuilderTests
{
    [Fact]
    public void A_town_has_streets_blocks_houses_a_wall_and_a_key()
    {
        var settings = new CitySettings { Seed = 7, Radius = 900, Radials = 6, Rings = 3, Walls = true, Temples = 2, Guild = true };
        var plan = CityBuilder.Generate(1200, 1200, settings);
        Assert.Equal(9, plan.Streets.Count);
        Assert.True(plan.Blocks.Count >= 12, $"blocks {plan.Blocks.Count}");
        Assert.True(plan.Houses >= 150, $"houses {plan.Houses}");
        Assert.Contains(plan.Objects, o => o.Name == "Площадь");
        Assert.Equal(2, plan.Objects.Count(o => o.Name == "Храм"));
        Assert.Contains(plan.Objects, o => o.Name == "Гильдия");
        Assert.True(plan.Objects.Count(o => o.Name == "Стена") >= 6, "the wall is broken by gates");
        Assert.Contains(plan.Objects, o => o.Name == "Башня");
        Assert.Equal(6, plan.Key.Count(k => k.Kind == "Ворота"));
        Assert.Contains(plan.Objects, o => o.Kind == SceneObjectKind.Text && o.Text!.Content == "Город");

        // Houses stay off each other.
        var houses = plan.Objects.Where(o => o.Name == "Дом").Select(SceneGeometry.BoundsOf).ToList();
        var overlaps = 0;
        for (var i = 0; i < houses.Count; i++)
            for (var j = i + 1; j < houses.Count; j++)
                if (Shrink(houses[i]).Intersects(Shrink(houses[j]))) overlaps++;
        Assert.True(overlaps < houses.Count / 20, $"overlaps {overlaps}");

        var again = CityBuilder.Generate(1200, 1200, settings);
        Assert.Equal(plan.Houses, again.Houses);
        Assert.Equal(plan.Streets.Select(s => s.Nodes.Length), again.Streets.Select(s => s.Nodes.Length));
    }

    [Fact]
    public void Main_streets_follow_the_incoming_roads_and_a_port_gets_water()
    {
        var plan = CityBuilder.Generate(1000, 1000, new CitySettings { Seed = 3, Radius = 700, Radials = 4, EntryDirections = [0, Math.PI / 2], Port = true });
        var first = plan.Streets[0].Nodes[^1];
        Assert.True(first.X > 1500 && Math.Abs(first.Y - 1000) < 120, "the first main street goes east, along the incoming road");
        Assert.Contains(plan.Objects, o => o.Name == "Гавань");
        Assert.Contains(plan.Key, k => k.Kind == "Порт");
        var map = CityBuilder.NewCityMap("Ильград", 2400, new CitySettings { Seed = 1 }, new MapCategoryId("city"), DateTimeOffset.UnixEpoch);
        Assert.Equal("city", map.Category.Value);
        Assert.NotEmpty(map.Notes);
        Assert.Throws<ArgumentException>(() => CityBuilder.Generate(0, 0, new CitySettings { Radials = 1 }));
    }

    private static WorldBounds Shrink(WorldBounds b) => new(b.Left + 3, b.Top + 3, b.Right - 3, b.Bottom - 3);
}
