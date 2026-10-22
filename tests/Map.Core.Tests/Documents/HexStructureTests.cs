using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Documents;

public sealed class HexStructureTests
{
    [Fact]
    public void Poi_survive_terrain_rotation_region_fog_and_legacy_brush()
    {
        var original = HexCell.Of("starter/grass", feature: "starter/village");
        Assert.Single(original.PointsOfInterest);
        var cell = original.WithPointsOfInterest([new HexPoi("starter/village", "Город"), new HexPoi("starter/ruins", "Тайна", false)]);
        var changed = cell.WithTerrain("starter/forest", 1, 2).WithRotation(3).WithRegion(2).WithRevealed(true);
        Assert.Equal(cell.PointsOfInterest, changed.PointsOfInterest);
        Assert.Equal("Тайна", changed.WithFeature("starter/castle").PointsOfInterest[1].Name);
        Assert.Single(changed.WithFeature(null).PointsOfInterest);
        Assert.Equal(cell, original.WithPointsOfInterest(cell.PointsOfInterest.Select(p => p with { })));
    }

    [Fact]
    public void Region_hierarchy_rejects_cycles_and_missing_parents()
    {
        HexRegion[] regions = [new(1, "Королевство", "#FF123456"), new HexRegion(2, "Провинция", "#FF654321") { ParentRegion = 1 },
            new HexRegion(3, "Округ", "#FF123456") { ParentRegion = 2 }];
        Assert.False(HexRegionHierarchy.CanParent(regions, 1, 3));
        Assert.False(HexRegionHierarchy.CanParent(regions, 2, 2));
        Assert.False(HexRegionHierarchy.CanParent(regions, 2, 99));
        Assert.True(HexRegionHierarchy.CanParent(regions, 3, 0));
        Assert.Equal("Королевство → Провинция → Округ", HexRegionHierarchy.Label(regions, regions[2]));
    }

    [Fact]
    public void Player_export_has_no_hidden_poi_and_keeps_region_ancestors()
    {
        var document = HexMapFactory.Create("Мир", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"), HexOrientation.Flat, 40, 4, 4, "#FFFFFFFF");
        var layer = document.Layers.OfType<HexLayer>().Single();
        var hex = layer.Board.Layout.FromOffset(new HexOffset(1, 1));
        layer = layer with { FogEnabled = true,
            Regions = [new HexRegion(1,"Страна","#FF123456"), new HexRegion(2,"Край","#FF654321") { ParentRegion = 1 }],
            Board = layer.Board.With([new(hex, HexCell.Of("starter/grass", region:2, revealed:true).WithPointsOfInterest(
                [new HexPoi("starter/village", "Площадь"), new HexPoi("starter/ruins", "Секрет", false)]))]) };
        var safe = ExportPlayerVisibility.Sanitize(layer);
        Assert.Single(safe.Board[hex]!.PointsOfInterest);
        Assert.Equal(2, safe.Regions.Count);
        Assert.Equal(2, layer.Board[hex]!.PointsOfInterest.Count);
        var command = new HexLayerEditCommand("Точки", document.Layers.OfType<HexLayer>().Single(), layer);
        var after = command.Apply(document);
        Assert.Same(document.Layers.OfType<HexLayer>().Single().Board, command.Revert(after).Layers.OfType<HexLayer>().Single().Board);
    }
}
