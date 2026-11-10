using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>R09d (DEC-094): a style pack sets the style, cliffs, rivers, roads, sea and names at once; one Ctrl+Z.</summary>
public sealed class StylePackTests
{
    private static MapDocument Map()
    {
        var tiles = TileMap.Empty(1).With(new TileKey(0, 0), Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray());
        var ground = new LandLayer(LayerId.New(), "Суша", true, false, 1, tiles, LandEdgeStyle.SoftWatercolor());
        var plateau = new LandLayer(LayerId.New(), "Плато", true, false, 1, tiles,
            LandEdgeStyle.SoftWatercolor() with { Cliff = LandCliffStyle.OwnerMountains with { Height = 52, Lean = .2 } }) { Level = 1 };
        var river = new River { Name = "Река", Style = RiverStyle.Painted(), Channels = [RiverEditor.NewChannel([new(10, 10), new(200, 200)], 12)] };
        var road = RoadEditor.NewRoad([new(20, 300), new(300, 320)], 8, RoadStyle.Painted() with { EndCap = RoadEndCap.Flat, BlocksLight = true }, "Дорога", seed: 1);
        var name = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Имя", 10, 10, 200, 40, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = "Северные горы", FontSize = 40 } };
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [name]) { Rivers = [river], Roads = [road] };
        return new MapDocument(DocumentId.New(), "Пакет", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", "#FF9DB7C2"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF9DB7C2") { Sea = SeaStyle.NewMapDefault }, ground, plateau, objects]);
    }

    private static EditorViewModel Editor(MapDocument document) => new(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
        new FakeFilePicker(), new FakePrompts(), new FakeLog());

    [Fact]
    public void There_are_the_four_approved_packs() =>
        Assert.Equal(["Тушь", "Пергамент", "Акварель", "Атлас"], MapStylePack.Presets.Select(p => p.Title));

    [Theory]
    [InlineData("ink")]
    [InlineData("parchment")]
    [InlineData("watercolor")]
    [InlineData("atlas")]
    public void A_pack_sets_the_whole_look_and_one_undo_takes_it_back(string id)
    {
        var pack = MapStylePack.Find(id)!;
        var editor = Editor(Map());
        var before = editor.Session.Document;
        var depth = editor.Session.UndoDepth;
        Assert.True(editor.ApplyStylePack(pack));
        Assert.Equal(depth + 1, editor.Session.UndoDepth);

        var after = editor.Session.Document;
        Assert.Equal(pack.Style.Preset, after.Style!.Preset);
        Assert.True(after.Style.Enabled);
        var plateau = after.Layers.OfType<LandLayer>().Single(l => l.Level == 1);
        Assert.Equal(pack.Cliff.Scree, plateau.EdgeStyle.Cliff.Scree);
        Assert.Equal(pack.Cliff.Foot, plateau.EdgeStyle.Cliff.Foot);
        // The land's own geometry stays: the height and lean of its cliff.
        Assert.Equal(52, plateau.EdgeStyle.Cliff.Height);
        Assert.Equal(.2, plateau.EdgeStyle.Cliff.Lean);
        // The ground material is the owner's: untouched.
        Assert.Equal(before.Layers.OfType<LandLayer>().First().EdgeStyle.FillTextureId, after.Layers.OfType<LandLayer>().First().EdgeStyle.FillTextureId);
        var objects = after.Layers.OfType<ObjectLayer>().Single();
        Assert.Equal(pack.River, objects.Rivers.Single().Style);
        var road = objects.Roads.Single();
        Assert.Equal(pack.Road.Drawing, road.Style.Drawing);
        Assert.Equal(RoadEndCap.Flat, road.Style.EndCap);
        Assert.True(road.Style.BlocksLight);
        Assert.Equal(pack.Sea.Manner, after.Layers.OfType<BaseColorLayer>().Single().Sea!.Manner);
        Assert.Equal(pack.NameColor, objects.Objects.Single().Text!.FillColor);
        // Shapes are the same: the river's nodes and the road's vertices.
        Assert.Equal(before.Layers.OfType<ObjectLayer>().Single().Rivers.Single().Channels, objects.Rivers.Single().Channels);

        editor.Undo();
        Assert.Equal(before.Style, editor.Session.Document.Style);
        Assert.Equal(before.Layers, editor.Session.Document.Layers);
    }
}
