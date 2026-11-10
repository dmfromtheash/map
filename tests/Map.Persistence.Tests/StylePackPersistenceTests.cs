using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>R09d (DEC-094): a map in a style pack keeps every part of the pack through save and reopen.</summary>
public sealed class StylePackPersistenceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("map-pack-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("ink")]
    [InlineData("atlas")]
    public void A_packed_map_reopens_in_its_pack(string id)
    {
        var tiles = TileMap.Empty(1).With(new TileKey(0, 0), Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray());
        var plateau = new LandLayer(LayerId.New(), "Плато", true, false, 1, tiles, LandEdgeStyle.SoftWatercolor()) { Level = 1 };
        var road = RoadEditor.NewRoad([new(20, 300), new(300, 320)], 8, RoadStyle.Painted(), "Дорога", seed: 1);
        var river = new River { Name = "Река", Style = RiverStyle.Painted(), Channels = [RiverEditor.NewChannel([new(10, 10), new(200, 200)], 12)] };
        var name = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Имя", 10, 10, 200, 40, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = "Плато", FontSize = 30 } };
        var document = new MapDocument(DocumentId.New(), "Пакет", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", "#FF9DB7C2"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF9DB7C2") { Sea = SeaStyle.NewMapDefault }, plateau,
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [name]) { Rivers = [river], Roads = [road] }]);
        var pack = MapStylePack.Find(id)!;
        var packed = pack.ApplyTo(document);
        var path = Path.Combine(_root, id + ".mapproj");
        MapProjectStore.Save(packed, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        var opened = MapProjectStore.Open(path).Document;
        Assert.Equal(packed.Style, opened.Style);
        Assert.Equal(packed.Layers.OfType<LandLayer>().Single().EdgeStyle.Cliff, opened.Layers.OfType<LandLayer>().Single().EdgeStyle.Cliff);
        Assert.Equal(pack.Sea.Manner, opened.Layers.OfType<BaseColorLayer>().Single().Sea!.Manner);
        var objects = opened.Layers.OfType<ObjectLayer>().Single();
        Assert.Equal(pack.Road.Drawing, objects.Roads.Single().Style.Drawing);
        Assert.Equal(packed.Layers.OfType<ObjectLayer>().Single().Rivers.Single().Style, objects.Rivers.Single().Style);
        Assert.Equal(pack.NameColor, objects.Objects.Single().Text!.FillColor);
        Assert.Equal(pack.NameOutlineWidth, objects.Objects.Single().Text!.OutlineWidth);
    }

    [Fact]
    public void The_owners_own_pack_comes_back_from_its_file_whole()
    {
        var atlas = MapStylePack.Find("atlas")!;
        // The look of a map the owner tuned: the Atlas pack with a changed cliff colour and sea scale.
        var own = atlas with
        {
            Id = MapStylePack.OwnId, Title = "Мой пакет",
            Cliff = atlas.Cliff with { Color = "#FFAA8866", Scree = CliffScree.WallAndBench }, Sea = atlas.Sea with { Scale = 1.4 },
        };
        var path = Path.Combine(_root, "own.mapproj");
        MapProjectStore.Save(own.ToTemplate(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var back = MapStylePack.From(MapProjectStore.Open(path).Document, MapStylePack.OwnId, "Мой пакет");
        Assert.Equal(own.Style, back.Style);
        Assert.Equal(own.Cliff, back.Cliff);
        Assert.Equal(own.River, back.River);
        Assert.Equal(own.Road, back.Road);
        Assert.Equal(own.Sea, back.Sea);
        Assert.Equal((own.NameColor, own.NameOutline, own.NameOutlineWidth), (back.NameColor, back.NameOutline, back.NameOutlineWidth));
    }
}
