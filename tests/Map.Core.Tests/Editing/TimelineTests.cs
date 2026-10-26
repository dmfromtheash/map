using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>
/// R31: a year shows only the things of its era, a border blends between its eras, the political
/// look and legend; the era (and «намеренно») survive a move.
/// </summary>
public sealed class TimelineTests
{
    [Fact]
    public void A_year_shows_only_its_things_and_roads()
    {
        var town = Label("Ильград", 100, 100) with { Era = new Era(812, 1040) };
        var always = Label("Горы", 300, 100);
        var road = RoadEditor.NewRoad([new(0, 10), new(300, 10)], 16, RoadStyle.Country(), "Тракт") with { Era = new Era(900, null) };
        var document = Doc([town, always], [road]);
        Assert.Equal(2, Objects(TimelineView.At(document, 1000, false)).Count);
        Assert.Single(Roads(TimelineView.At(document, 1000, false)));
        Assert.Equal(["Горы"], Objects(TimelineView.At(document, 1100, false)).Select(o => o.Name));
        Assert.Empty(Roads(TimelineView.At(document, 850, false)));
        Assert.Same(document, TimelineView.Shown(document));
        Assert.Same(TimelineView.At(document, 1000, false), TimelineView.At(document, 1000, false));
        Assert.False(new Era(10, 5).IsValid);
        Assert.Equal("812–1040", new Era(812, 1040).ToString());

        // A river that dried up in 950 flows in 900, not in 1000.
        var river = new River { Name = "Старица", Channels = [RiverEditor.NewChannel([new(10, 400), new(300, 420)], 8)], Era = new Era(null, 950) };
        var withRiver = document.WithLayers([.. document.Layers.Select(l => l is ObjectLayer o ? o with { Rivers = [river] } : l)]);
        Assert.Single(TimelineView.At(withRiver, 900, false).Layers.OfType<ObjectLayer>().SelectMany(l => l.Rivers));
        Assert.Empty(TimelineView.At(withRiver, 1000, false).Layers.OfType<ObjectLayer>().SelectMany(l => l.Rivers));
        Assert.Throws<ArgumentException>(() => (river with { Era = new Era(10, 5) }).Validate());
    }

    [Fact]
    public void A_border_blends_between_its_eras_and_the_political_look_has_a_legend()
    {
        var before = Square(0, "Королевство") with { Era = new Era(null, 900), MorphKey = "kingdom", Political = PoliticalRole.State };
        var after = Square(400, "Королевство") with { Era = new Era(1000, null), MorphKey = "kingdom", Political = PoliticalRole.State };
        var disputed = Square(1200, "Пограничье") with { Political = PoliticalRole.Disputed };
        var document = Doc([before, after, disputed], []);

        var mid = Objects(TimelineView.At(document, 950, false)).Where(o => o.Shape is not null && o.Name == "Королевство").ToArray();
        var blended = Assert.Single(mid);
        Assert.InRange(blended.X + (blended.Width / 2), 300, 500); // halfway between 100 and 500
        Assert.Single(Objects(TimelineView.At(document, 850, false)).Where(o => o.Name == "Королевство"));

        var political = TimelineView.At(document, 1200, true);
        var state = Objects(political).Single(o => o.Name == "Королевство");
        Assert.Equal(.42, state.Shape!.Fill.Opacity, 2);
        Assert.Equal(StrokePattern.Dashed, Objects(political).Single(o => o.Name == "Пограничье").Shape!.Stroke.Pattern);
        Assert.Equal([("Королевство", PoliticalRole.State), ("Пограничье", PoliticalRole.Disputed)],
            TimelineView.PoliticalLegend(political).Select(e => (e.Name, e.Role)));
    }

    [Fact]
    public void An_era_and_intent_survive_a_move()
    {
        var town = Label("Ильград", 100, 100) with { Era = new Era(812, 1040), GeographyIntentional = true, MorphKey = "x" };
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [town]);
        var session = new EditingSession(new MapDocument(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(2048, 2048, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), layer]));
        session.Execute(new TransformObjectCommand(town, SceneObjectClone.At(town, 300, 300)));
        var moved = Objects(session.Document).Single();
        Assert.Equal(300, moved.X);
        Assert.Equal(new Era(812, 1040), moved.Era);
        Assert.True(moved.GeographyIntentional);
        Assert.Equal("x", moved.MorphKey);
    }

    private static List<SceneObject> Objects(MapDocument d) => [.. d.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)];
    private static List<Road> Roads(MapDocument d) => [.. d.Layers.OfType<ObjectLayer>().SelectMany(l => l.Roads)];

    private static SceneObject Label(string content, double x, double y) =>
        new SceneObject(ObjectId.New(), SceneObjectKind.Text, content, x, y, 140, 40, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = content } };

    private static SceneObject Square(double x, string name) =>
        VectorGeometry.CreateFreeShape(ObjectId.New(), name, [new(x, 0), new(x + 200, 0), new(x + 200, 200), new(x, 200)], new ShapeFillStyle { Color = "#FF3D6E8A" });

    private static MapDocument Doc(SceneObject[] objects, Road[] roads) =>
        new(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(2048, 2048, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects) { Roads = [.. roads] }]);
}
