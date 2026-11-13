using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Documents;

/// <summary>Owner, 2026-11-13: older maps open in the current look; rivers, roads and land keep their geometry.</summary>
public sealed class CurrentLookUpgradeTests
{
    private static MapDocument OldMap()
    {
        var full = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var tiles = TileMap.Empty(1).With(new TileKey(0, 0), full);
        var land = new LandLayer(LayerId.New(), "Земля", true, false, 1, tiles,
            new LandEdgeStyle { FillTextureId = "meadow", ReliefSteepInk = 0.4, Cliff = LandCliffStyle.OwnerMountains with { Height = 3 } }) { Level = 1 };
        var channel = RiverEditor.NewChannel([new VectorPoint(10, 10), new VectorPoint(120, 90), new VectorPoint(200, 210)], 14);
        var river = new River { Name = "Старая", Channels = [channel], Style = RiverStyle.Illustrated(pen: true) with { Opacity = 0.8, CutsLand = false } };
        var road = RoadEditor.NewRoad([new VectorPoint(20, 200), new VectorPoint(240, 220)], 9, RoadStyle.Country() with
        { StartCap = RoadEndCap.Taper, BlocksLight = true }, "Тракт", seed: 4);
        road = road with { Branches = [.. road.Branches.Select(b => b with { Style = RoadStyle.PenRuts() })] };
        return new MapDocument(DocumentId.New(), "Старая карта", DateTimeOffset.UnixEpoch, new CanvasDefinition(256, 256, "px", "#FF8FB0BA"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF8FB0BA"), land,
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Rivers = [river], Roads = [road] }]);
    }

    [Fact]
    public void An_old_map_takes_the_current_look_and_keeps_its_geometry()
    {
        var old = OldMap();
        var now = CurrentLookUpgrade.Apply(old);

        var oldObjects = old.Layers.OfType<ObjectLayer>().Single();
        var objects = now.Layers.OfType<ObjectLayer>().Single();
        var river = objects.Rivers.Single();
        Assert.Equal(RiverStyle.Natural() with { Opacity = 0.8, CutsLand = false }, river.Style);
        // Geometry, name and widths are the very same.
        Assert.Equal("Старая", river.Name);
        Assert.Same(oldObjects.Rivers.Single().Channels[0], river.Channels[0]);

        var road = objects.Roads.Single();
        Assert.True(CurrentLookUpgrade.IsCurrent(road.Style));
        Assert.Equal(RoadEndCap.Taper, road.Style.StartCap);
        Assert.True(road.Style.BlocksLight);
        Assert.All(road.Branches, b => Assert.True(CurrentLookUpgrade.IsCurrent(b.Style!)));
        Assert.Equal(oldObjects.Roads.Single().Nodes, road.Nodes);
        Assert.Equal(oldObjects.Roads.Single().Branches.Select(b => b.Vertices.ToArray()), road.Branches.Select(b => b.Vertices.ToArray()));
        Assert.Equal("Тракт", road.Name);

        var land = now.Layers.OfType<LandLayer>().Single();
        var oldLand = old.Layers.OfType<LandLayer>().Single();
        Assert.Equal(MapFillTextures.OwnerSnow, land.EdgeStyle.FillTextureId);
        Assert.Equal(oldLand.EdgeStyle.Cliff, land.EdgeStyle.Cliff);
        Assert.Equal(0.4, land.EdgeStyle.ReliefSteepInk);
        Assert.Same(oldLand.GeometryTiles, land.GeometryTiles);
        Assert.Equal(1, land.Level);

        Assert.Equal(SeaStyle.NewMapDefault, now.Layers.OfType<BaseColorLayer>().Single().Sea);
    }

    [Fact]
    public void An_inherited_copy_nobody_edited_stays_in_sync_and_an_edited_one_stays_protected()
    {
        var road = RoadEditor.NewRoad([new VectorPoint(0, 10), new VectorPoint(100, 10)], 6, RoadStyle.Country(), "Тракт");
        var copy = road with { Inherited = new InheritedFrom(Guid.NewGuid(), road.Id, ScaleSync.Fingerprint(road)) };
        var edited = copy with { Name = "Свой тракт" };
        static Road Upgraded(Road road) => CurrentLookUpgrade.Apply(new MapDocument(DocumentId.New(), "Регион", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(128, 128, "px", "#FF1E2430"),
            [new BaseColorLayer(LayerId.New(), "Цвет фона", true, false, 1, "#FF1E2430"), new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Roads = [road] }]))
            .Layers.OfType<ObjectLayer>().Single().Roads.Single();
        // The untouched copy: new look, and its fingerprint still says «not edited by hand».
        var kept = Upgraded(copy);
        Assert.True(CurrentLookUpgrade.IsCurrent(kept.Style));
        Assert.Equal(ScaleSync.Fingerprint(kept), kept.Inherited!.Fingerprint);
        // The copy edited by hand keeps its old fingerprint: the sync still sees the edit.
        var own = Upgraded(edited);
        Assert.NotEqual(ScaleSync.Fingerprint(own), own.Inherited!.Fingerprint);
    }

    [Fact]
    public void A_map_in_the_current_look_is_left_untouched()
    {
        var current = CurrentLookUpgrade.Apply(OldMap());
        Assert.Same(current, CurrentLookUpgrade.Apply(current));
    }

    [Fact]
    public void Library_textures_and_the_owners_grounds_are_current()
    {
        Assert.True(CurrentLookUpgrade.IsCurrent(LandEdgeStyle.SoftWatercolor()));
        Assert.True(CurrentLookUpgrade.IsCurrent(LandEdgeStyle.SoftWatercolor() with { FillTextureId = MapFillTextures.AssetId(new string('a', 64)) }));
        Assert.True(CurrentLookUpgrade.IsCurrent(RiverStyle.Painted()));
        Assert.False(CurrentLookUpgrade.IsCurrent(new RiverStyle()));
        Assert.False(CurrentLookUpgrade.IsCurrent(RoadStyle.Painterly()));
    }

    [Fact]
    public void Style_packs_give_only_the_current_rivers_and_roads()
    {
        Assert.All(MapStylePack.Presets, pack =>
        {
            Assert.True(CurrentLookUpgrade.IsCurrent(pack.River), pack.Id);
            Assert.True(CurrentLookUpgrade.IsCurrent(pack.Road), pack.Id);
        });
    }

    [Fact]
    public void A_new_city_lies_on_current_land_with_painted_streets()
    {
        var map = CityBuilder.NewCityMap("Город", 1024, new CitySettings { Port = true }, new MapCategoryId("city"), DateTimeOffset.UnixEpoch);
        Assert.Same(map, CurrentLookUpgrade.Apply(map));
        var objects = map.Layers.OfType<ObjectLayer>().Single();
        Assert.NotEmpty(objects.Roads);
        Assert.Contains(objects.Rivers, r => r.Name == CityPlan.HarbourName);
        Assert.DoesNotContain(objects.Objects, o => o.Name == CityPlan.HarbourName);
    }
}
