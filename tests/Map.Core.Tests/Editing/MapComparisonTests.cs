using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>R30: two versions compared by meaning - added settlements, moved labels, changed roads, revealed fog - each with its place.</summary>
public sealed class MapComparisonTests
{
    private static readonly string House = new('h', 64), Tree = new('t', 64);

    [Fact]
    public void Settlements_labels_roads_notes_and_fog_are_counted_with_their_places()
    {
        var town = Stamp(House, 100, 100);
        var forest = Stamp(Tree, 400, 100);
        var label = Label("Ильград", 100, 160);
        var road = RoadEditor.NewRoad([new(0, 10), new(300, 10)], 16, RoadStyle.Country(), "Тракт");
        var before = Doc([town, forest, label], [road]);

        var newTown = Stamp(House, 700, 700);
        var movedLabel = label with { Text = label.Text! with { Content = "Ильград-на-Сельве" } };
        var bent = road with { Nodes = [road.Nodes[0], road.Nodes[1] with { Y = 120 }] };
        var stroke = MapFogEditor.Begin(new MapFog { Enabled = true });
        MapFogEditor.Dab(stroke, 500, 500, 100, true, 2048, 2048);
        var after = Doc([town, newTown, movedLabel], [bent]).WithNotes([new MapNote(NoteId.New(), "Засада", "", NoteAnchorKind.World, 50, 50)])
            with { Fog = MapFogEditor.Finish(stroke) };

        var diff = MapComparison.Compare(before, after, o => o.Stamp?.AssetSha256 == House);
        Assert.Equal(new MapChangeGroup(MapComparison.Settlements, 1, 0, 0), diff.Groups.Single(g => g.Group == MapComparison.Settlements));
        Assert.Equal(new MapChangeGroup(MapComparison.Objects, 0, 1, 0), diff.Groups.Single(g => g.Group == MapComparison.Objects));
        Assert.Equal(new MapChangeGroup(MapComparison.Labels, 0, 0, 1), diff.Groups.Single(g => g.Group == MapComparison.Labels));
        Assert.Equal(new MapChangeGroup(MapComparison.Roads, 0, 0, 1), diff.Groups.Single(g => g.Group == MapComparison.Roads));
        Assert.Equal(1, diff.Groups.Single(g => g.Group == MapComparison.Notes).Added);
        Assert.True(diff.FogCellsRevealed > 0);
        var added = diff.Changes.Single(c => c.Kind == MapChangeKind.Added && c.Group == MapComparison.Settlements);
        Assert.True(added.Bounds.Left >= 690 && added.Bounds.Right <= 760);
        Assert.Equal(MapComparison.Settlements, diff.Groups[0].Group);
        Assert.Contains(diff.Summary(), line => line.StartsWith("Поселения: +1", StringComparison.Ordinal));

        Assert.True(MapComparison.Compare(before, before).IsEmpty);
        Assert.Equal(["Различий нет."], MapComparison.Compare(before, before).Summary());
    }

    [Fact]
    public void Note_visibility_link_and_category_are_changes()
    {
        // R30b: each of them alone makes a note «changed».
        var note = new MapNote(NoteId.New(), "Засада", "", NoteAnchorKind.World, 50, 50);
        var before = Doc([], []).WithNotes([note]);
        foreach (var edited in new[] { note with { PlayerVisible = true }, note with { LinkedMap = Guid.NewGuid() }, note with { Category = NoteCategory.Quest } })
        {
            var diff = MapComparison.Compare(before, before.WithNotes([edited]));
            Assert.Equal(1, diff.Groups.Single(g => g.Group == MapComparison.Notes).Changed);
        }
    }

    [Fact]
    public void A_card_shown_to_players_is_a_change()
    {
        var card = new MapEntity { Name = "Ильград", Kind = EntityKind.Place };
        var before = Doc([], []) with { Entities = [card] };
        var diff = MapComparison.Compare(before, before with { Entities = [card with { PlayerVisible = true }] });
        Assert.Equal(1, diff.Groups.Single(g => g.Group == MapComparison.Cards).Changed);
    }

    [Fact]
    public void A_waterfall_a_sharp_bend_or_a_wider_river_is_a_change()
    {
        var river = new River { Name = "Сельва", Channels = [RiverEditor.NewChannel([new(0, 0), new(100, 50), new(200, 0)], 8)] };
        var before = WithRiver(river);
        var channel = river.Channels[0];
        River Node(Func<RiverNode, RiverNode> edit) => river with { Channels = [channel with { Nodes = [channel.Nodes[0], edit(channel.Nodes[1]), channel.Nodes[2]] }] };
        foreach (var edited in new[] { Node(n => n with { Feature = RiverNodeFeature.Waterfall }), Node(n => n with { Sharp = true }), Node(n => n with { Tension = 2 }), river with { WidthScale = 2 } })
            Assert.Equal(1, MapComparison.Compare(before, WithRiver(edited)).Groups.Single(g => g.Group == MapComparison.Rivers).Changed);
        Assert.True(MapComparison.Compare(before, WithRiver(river with { Channels = [channel] })).IsEmpty);
    }

    [Fact]
    public void Moving_the_opened_fog_with_the_same_number_of_cells_is_a_change()
    {
        MapFog Opened(double x)
        {
            var stroke = MapFogEditor.Begin(new MapFog { Enabled = true });
            MapFogEditor.Dab(stroke, x, 500, 100, true, 2048, 2048);
            return MapFogEditor.Finish(stroke);
        }
        var (left, right) = (Opened(400), Opened(1200));
        Assert.Equal(left.RevealedCells, right.RevealedCells);
        var diff = MapComparison.Compare(Doc([], []) with { Fog = left }, Doc([], []) with { Fog = right });
        Assert.False(diff.IsEmpty);
        Assert.Equal(left.RevealedCells, diff.FogCellsRevealed);
        Assert.Equal(left.RevealedCells, diff.FogCellsHidden);
        Assert.Contains(diff.Summary(), line => line.StartsWith("Закрыто туманом", StringComparison.Ordinal));
        Assert.True(MapComparison.Compare(Doc([], []) with { Fog = left }, Doc([], []) with { Fog = Opened(400) }).IsEmpty);
    }

    private static MapDocument WithRiver(River river)
    {
        var doc = Doc([], []);
        return doc.WithLayers([.. doc.Layers.Select(l => l is ObjectLayer o ? o with { Rivers = [river] } : l)]);
    }

    private static SceneObject Stamp(string sha, double x, double y) =>
        new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "s", x, y, 40, 40, 0, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(sha) };

    private static SceneObject Label(string content, double x, double y) =>
        new SceneObject(ObjectId.New(), SceneObjectKind.Text, content, x, y, 140, 40, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = content } };

    private static MapDocument Doc(SceneObject[] objects, Road[] roads) =>
        new(new DocumentId(Guid.Parse("11111111-1111-1111-1111-111111111111")), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(2048, 2048, "px", "#FFFFFFFF"),
            [new BaseColorLayer(new LayerId(Guid.Parse("22222222-2222-2222-2222-222222222222")), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(new LayerId(Guid.Parse("33333333-3333-3333-3333-333333333333")), "Объекты", true, false, 1, objects) { Roads = [.. roads] }]);
}
