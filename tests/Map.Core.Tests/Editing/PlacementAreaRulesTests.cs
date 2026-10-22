using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>
/// R10: area rules only remove places - the assets that stay keep exactly their objects - and
/// switching a rule off brings back the removed ones where they were. Each change is one Undo step.
/// </summary>
public sealed class PlacementAreaRulesTests
{
    private static readonly string Sha = new('a', 64);

    [Fact]
    public void Avoid_water_removes_only_assets_on_the_river_and_switching_off_restores_them()
    {
        var session = Session(out var layer, withRiver: true, withRoad: false);
        var area = Area();
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "create"));
        var plain = Objects(session.Document);
        Assert.Contains(plain.Values, o => OnRiver(o));

        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, Get(session.Document) with { Rules = new() { AvoidWater = true } }, "rules"));
        Assert.Null(session.LastResourceError);
        var ruled = Objects(session.Document);
        var stored = Get(session.Document);
        stored.ValidateMembers();
        Assert.NotEmpty(stored.RuleBlocked);
        Assert.DoesNotContain(ruled.Values, o => OnRiver(o));
        // Every asset that stays is the very same object at the very same place.
        foreach (var (id, item) in ruled) Assert.Equal(plain[id], item);
        // The whole base is checked, so at least every asset standing on the river is gone.
        Assert.True(plain.Count - ruled.Count >= plain.Values.Count(o => OnRiver(o)));

        session.Undo();
        Assert.Equal(plain.Count, Objects(session.Document).Count);
        session.Redo();
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, Get(session.Document) with { Rules = null }, "off"));
        var restored = Objects(session.Document);
        Assert.Equal(plain.Count, restored.Count);
        foreach (var (id, item) in plain) Assert.Equal(item, restored[id]);
        Assert.Null(Get(session.Document).RuleSlots);
    }

    [Fact]
    public void Only_on_water_keeps_just_the_assets_on_the_river()
    {
        var session = Session(out var layer, withRiver: true, withRoad: false);
        var area = Area() with { Rules = new() { OnlyOnWater = true } };
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "create"));
        var kept = Objects(session.Document);
        Assert.NotEmpty(kept);
        Assert.All(kept.Values, o => Assert.True(OnRiver(o)));
        Get(session.Document).ValidateMembers();
    }

    [Fact]
    public void Avoid_roads_keeps_the_margin_free_and_a_refresh_follows_a_moved_road()
    {
        var session = Session(out var layer, withRiver: false, withRoad: true);
        var area = Area() with { Rules = new() { AvoidRoads = true, RoadMargin = 20 } };
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "create"));
        Assert.DoesNotContain(Objects(session.Document).Values, o => NearRoad(o, 1000, 20));

        // The road moves; «Пересчитать по карте» bumps the rule revision and fills the area again.
        var road = ((ObjectLayer)session.Document.FindLayer(layer.Id)!).Roads[0];
        var moved = RoadEditor.NewRoad([new(0, 600), new(2048, 600)], 16, RoadStyle.Country(), "Дорога") with { Id = road.Id };
        var current = (ObjectLayer)session.Document.FindLayer(layer.Id)!;
        session.Execute(UpdateLayerCommand.Replace(current, current with { Roads = [moved] }, "move road"));
        var stale = Get(session.Document);
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, stale with { Rules = stale.Rules! with { Revision = 1 } }, "refresh"));
        var refreshed = Objects(session.Document);
        Assert.DoesNotContain(refreshed.Values, o => NearRoad(o, 600, 20));
        Assert.Contains(refreshed.Values, o => NearRoad(o, 1000, 20));
        Get(session.Document).ValidateMembers();
    }

    [Fact]
    public void Depth_sort_draws_lower_objects_over_higher_ones_and_undo_restores_the_order()
    {
        var session = Session(out var layer, withRiver: false, withRoad: false);
        var area = Area();
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "create"));
        var plain = ((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects.Select(o => o.Id).ToArray();
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, Get(session.Document) with { DepthSort = true }, "depth"));
        Assert.Null(session.LastResourceError);
        var sorted = ((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects;
        Assert.Equal(plain.Length, sorted.Count);
        for (var i = 1; i < sorted.Count; i++) Assert.True(sorted[i - 1].Y + sorted[i - 1].Height <= sorted[i].Y + sorted[i].Height + 1e-6);
        Assert.True(Get(session.Document).NeedsRulesSchema);
        Get(session.Document).ValidateMembers();
        session.Undo();
        Assert.Equal(plain, ((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects.Select(o => o.Id));
    }

    [Fact]
    public void Rules_validate_contradictions_and_report_whether_any_is_on()
    {
        Assert.False(new PlacementAreaRules().Any);
        Assert.True(new PlacementAreaRules { OnlyOnLand = true }.Any);
        Assert.Throws<ArgumentException>(() => new PlacementAreaRules { AvoidWater = true, OnlyOnWater = true }.Validate());
        Assert.Throws<ArgumentException>(() => new PlacementAreaRules { RoadMargin = -1 }.Validate());
        Assert.False(Area().NeedsRulesSchema);
        Assert.True((Area() with { Rules = new() { AvoidRoads = true } }).NeedsRulesSchema);
    }

    // A river runs straight across the area at y = 1000; its body is measured by the river geometry itself.
    private static readonly River TestRiver = new() { Name = "Река", Channels = [RiverEditor.NewChannel([new(0, 1000), new(2048, 1000)], 80)] };

    private static bool OnRiver(SceneObject o) { var f = PlacementAreaRuleCheck.Foot(o); return RiverGeometry.Contains(TestRiver, f.X, f.Y); }

    private static bool NearRoad(SceneObject o, double y, double margin)
    {
        var f = PlacementAreaRuleCheck.Foot(o);
        return RoadGeometry.Contains(RoadEditor.NewRoad([new(0, y), new(2048, y)], 16, RoadStyle.Country(), "Дорога"), f.X, f.Y, margin - 1);
    }

    private static Dictionary<ObjectId, SceneObject> Objects(MapDocument document) =>
        document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Where(o => o.Kind == SceneObjectKind.Stamp).ToDictionary(o => o.Id);

    private static EditingSession Session(out ObjectLayer layer, bool withRiver, bool withRoad)
    {
        layer = new(LayerId.New(), "Objects", true, false, 1, []);
        if (withRiver)
            layer = layer with { Rivers = [TestRiver] };
        if (withRoad)
            layer = layer with { Roads = [RoadEditor.NewRoad([new(0, 1000), new(2048, 1000)], 16, RoadStyle.Country(), "Дорога")] };
        var assets = new[] { new MapAssetReference(Sha, "tree", "image/png", "png", ProjectAssetStorage.Linked,
            DateTimeOffset.UnixEpoch, "test", "test", catalogId: Sha) };
        return new(new MapDocument(DocumentId.New(), "Areas", DateTimeOffset.UnixEpoch, new CanvasDefinition(2048, 2048, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFFFFFFF"), layer], assets));
    }

    private static PlacementArea Area() => new()
    {
        Name = "Trees", LayoutVersion = 1, Density = 4, Seed = 18,
        Nodes = [new(Guid.NewGuid(), 10, 10), new(Guid.NewGuid(), 1900, 10), new(Guid.NewGuid(), 1900, 1900), new(Guid.NewGuid(), 10, 1900)],
        Variants = [new(Sha, 32, 32)], Style = new(Sha), Request = new() { BaseWidth = 24 },
    };

    private static PlacementArea Get(MapDocument document) => document.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementAreas).Single();
}
