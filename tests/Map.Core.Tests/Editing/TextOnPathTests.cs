using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>R07: labels follow rivers, roads and hex paths, never read upside down, slide when moved and undo cleanly.</summary>
public sealed class TextOnPathTests
{
    private static readonly LayerId Water = LayerId.New();
    private static readonly LayerId Labels = LayerId.New();

    private static River River(params VectorPoint[] points) => new()
    {
        Name = "Серебрянка", Style = RiverStyle.Illustrated(),
        Channels = [RiverEditor.NewChannel(points, 20)],
    };

    private static SceneObject Label(TextPathBinding binding, string content = "Серебрянка") =>
        new(ObjectId.New(), SceneObjectKind.Text, content, 0, 0, 1, 1, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        {
            Text = new TextStyle { Content = content, FontSize = 20, Path = binding },
        };

    private static MapDocument Document(River? river = null, Road? road = null, params SceneObject[] labels)
    {
        var water = new ObjectLayer(Water, "Вода", true, false, 1, [])
        {
            Rivers = river is null ? [] : [river], Roads = road is null ? [] : [road],
        };
        return new MapDocument(DocumentId.New(), "labels", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1000, 1000, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), water,
                new ObjectLayer(Labels, "Подписи", true, false, 1, labels)]);
    }

    private static SceneObject LabelOf(MapDocument document) =>
        document.Layers.OfType<ObjectLayer>().Single(l => l.Id == Labels).Objects.Single();

    private static VectorPoint WorldStart(SceneObject label) =>
        new(label.X + label.Text!.Path!.Baseline[0].X, label.Y + label.Text.Path.Baseline[0].Y);

    private static VectorPoint WorldEnd(SceneObject label) =>
        new(label.X + label.Text!.Path!.Baseline[^1].X, label.Y + label.Text.Path.Baseline[^1].Y);

    private static TextPathBinding OnRiver(River river) =>
        new() { Target = TextPathTarget.River, TargetId = river.Id, PartId = river.Channels[0].Id };

    /// <summary>Puts a laid-out label into the document as the session would after the first edit.</summary>
    private static EditingSession Session(River river, TextPathBinding binding)
    {
        var draft = Document(river);
        var label = TextOnPath.Layout(draft, Label(binding));
        var session = new EditingSession(draft);
        session.Execute(new AddObjectCommand(Labels, label));
        return session;
    }

    [Fact]
    public void A_label_sits_above_the_river_and_follows_a_moved_node_with_undo()
    {
        var river = River(new(100, 500), new(900, 500));
        var session = Session(river, OnRiver(river) with { Side = TextPathSide.Above, Gap = 6 });
        var label = LabelOf(session.Document);
        Assert.False(label.Text!.Path!.Detached);
        Assert.Equal(0, label.RotationDegrees);
        // Above a horizontal river: baseline at half width (10) + gap (6) over the axis.
        Assert.Equal(484, WorldStart(label).Y, 1);
        Assert.InRange(TextOnPath.BaselineCentre(label).X, 480, 520);

        var moved = river with { Channels = [river.Channels[0] with { Nodes = [river.Channels[0].Nodes[0] with { Y = 300 }, river.Channels[0].Nodes[1] with { Y = 300 }] }] };
        session.Execute(new RiverCommand(Water, river.Id, moved, "move"));
        var followed = LabelOf(session.Document);
        Assert.Equal(284, WorldStart(followed).Y, 1);

        Assert.True(session.Undo());
        Assert.Equal(484, WorldStart(LabelOf(session.Document)).Y, 1);
        Assert.True(session.Redo());
        Assert.Equal(284, WorldStart(LabelOf(session.Document)).Y, 1);
    }

    [Fact]
    public void A_river_drawn_right_to_left_still_reads_left_to_right_unless_upright_is_off()
    {
        var river = River(new(900, 500), new(100, 500));
        var upright = TextOnPath.Layout(Document(river), Label(OnRiver(river)));
        Assert.True(WorldStart(upright).X < WorldEnd(upright).X, "upright text runs left to right");
        // Above stays above for the reader: smaller Y than the axis.
        Assert.True(WorldStart(upright).Y < 500);

        var raw = TextOnPath.Layout(Document(river), Label(OnRiver(river) with { KeepUpright = false }));
        Assert.True(WorldStart(raw).X > WorldEnd(raw).X, "following the line's own direction reads right to left");
        Assert.True(TextOnPath.ReadsAgainstLine(Document(river), upright));
        Assert.False(TextOnPath.ReadsAgainstLine(Document(river), raw));

        var flipped = TextOnPath.Layout(Document(river), Label(OnRiver(river) with { KeepUpright = false, Reverse = true }));
        Assert.True(WorldStart(flipped).X < WorldEnd(flipped).X);
    }

    [Fact]
    public void A_steep_river_never_turns_the_text_upside_down()
    {
        // Every direction of a straight river, every 15 degrees: the baseline always runs left to right
        // or, when vertical, from bottom to top.
        for (var degrees = 0; degrees < 360; degrees += 15)
        {
            var a = degrees * Math.PI / 180;
            var river = River(new(500 - Math.Cos(a) * 400, 500 - Math.Sin(a) * 400), new(500 + Math.Cos(a) * 400, 500 + Math.Sin(a) * 400));
            var label = TextOnPath.Layout(Document(river), Label(OnRiver(river)));
            var start = WorldStart(label); var end = WorldEnd(label);
            var dx = end.X - start.X; var dy = end.Y - start.Y;
            Assert.True(dx > 1e-6 || Math.Abs(dx) <= 1e-6 && dy < 0, $"{degrees}°: dx={dx:0.###} dy={dy:0.###}");
        }
    }

    [Fact]
    public void Moving_a_bound_label_slides_it_along_its_river()
    {
        var river = River(new(100, 500), new(900, 500));
        var session = Session(river, OnRiver(river) with { Position = 0.5 });
        var label = LabelOf(session.Document);
        var shifted = SceneObjectClone.At(label, label.X + 200, label.Y + 120);
        session.Execute(new ReplaceObjectsCommand([label], [shifted], "move"));
        var slid = LabelOf(session.Document);
        Assert.InRange(slid.Text!.Path!.Position, 0.70, 0.80);
        // Back on the river: the vertical drag is undone by the line.
        Assert.Equal(WorldStart(label).Y, WorldStart(slid).Y, 1);
        Assert.True(session.Undo());
        Assert.Equal(0.5, LabelOf(session.Document).Text!.Path!.Position);
    }

    [Fact]
    public void A_road_label_follows_its_branch_and_a_deleted_river_detaches_the_label()
    {
        var road = RoadEditor.NewRoad([new(100, 200), new(900, 200)], 16);
        var binding = new TextPathBinding { Target = TextPathTarget.Road, TargetId = road.Id, PartId = road.Branches[0].Id, Side = TextPathSide.Below };
        var label = TextOnPath.Layout(Document(road: road), Label(binding, "Королевский тракт"));
        // Below: half width 8 + gap 6 + 0.8 of the font size under the axis.
        Assert.Equal(200 + 8 + 6 + 16, WorldStart(label).Y, 1);

        var river = River(new(100, 500), new(900, 500));
        var session = Session(river, OnRiver(river));
        var before = LabelOf(session.Document);
        session.Execute(new RiverCommand(Water, river.Id, null, "delete"));
        var detached = LabelOf(session.Document);
        Assert.True(detached.Text!.Path!.Detached);
        Assert.Equal(before.Text!.Path!.Baseline, detached.Text.Path.Baseline);
        Assert.True(session.Undo());
        Assert.False(LabelOf(session.Document).Text!.Path!.Detached);
    }

    [Fact]
    public void A_label_follows_a_hex_path_when_the_path_changes()
    {
        var hex = new HexLayer(LayerId.New(), "Гексы", true, false, 1, HexBoard.Create(HexOrientation.Flat, 40, 12, 8));
        var layout = hex.Board.Layout;
        HexCoord At(int c, int r) => layout.FromOffset(new HexOffset(c, r));
        var path = new HexPath(HexPathId.New(), HexPathKind.Road, HexRouteStyle.Centers, [.. HexPaths.Walk([.. Enumerable.Range(1, 8).Select(c => At(c, 2))])], [], name: "Тракт");
        hex = hex with { Paths = [path] };
        var document = Document().WithLayers([.. Document().Layers.Take(1), hex, new ObjectLayer(Labels, "Подписи", true, false, 1, [])]);
        var binding = new TextPathBinding { Target = TextPathTarget.HexPath, TargetId = path.Id.Value };
        var label = TextOnPath.Layout(document, Label(binding, "Тракт"));
        Assert.False(label.Text!.Path!.Detached);
        var session = new EditingSession(document);
        session.Execute(new AddObjectCommand(Labels, label));
        var startBefore = WorldStart(LabelOf(session.Document));

        var lower = new HexPath(path.Id, HexPathKind.Road, HexRouteStyle.Centers, [.. HexPaths.Walk([.. Enumerable.Range(1, 8).Select(c => At(c, 5))])], [], name: "Тракт");
        session.Execute(new SwapLayerCommand(hex, hex with { Paths = [lower] }));
        Assert.True(WorldStart(LabelOf(session.Document)).Y > startBefore.Y + 50, "the label moves down with the path");
        Assert.True(session.Undo());
        Assert.Equal(startBefore, WorldStart(LabelOf(session.Document)));
    }

    [Fact]
    public void Bind_nearest_picks_the_closest_line_and_its_position()
    {
        var river = River(new(100, 500), new(900, 500));
        var road = RoadEditor.NewRoad([new(100, 200), new(900, 200)], 16);
        var document = Document(river, road);
        var nearRiver = TextOnPath.BindNearest(document, 300, 470, 400);
        Assert.Equal(TextPathTarget.River, nearRiver!.Target);
        Assert.Equal(0.25, nearRiver.Position, 2);
        Assert.Equal(TextPathTarget.Road, TextOnPath.BindNearest(document, 700, 230, 400)!.Target);
        Assert.Null(TextOnPath.BindNearest(document, 500, 900, 100));
    }

    [Fact]
    public void Free_text_and_unrelated_edits_cost_nothing()
    {
        var river = River(new(100, 500), new(900, 500));
        var document = Document(river);
        var other = Document(river) with { };
        Assert.Same(document, TextOnPath.Follow(document, document));
        var free = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Текст", 0, 0, 100, 40, 0, "#FF000000", "#FFFFFFFF", 0, true, false) { Text = new TextStyle() };
        Assert.Same(free, TextOnPath.Layout(document, free));
        var session = new EditingSession(document);
        session.Execute(new AddObjectCommand(Labels, free));
        Assert.Equal(free, LabelOf(session.Document));
        Assert.NotNull(other);
    }

    [Fact]
    public void Place_names_are_deterministic_shaped_by_kind_and_never_repeat_a_shown_label()
    {
        var empty = Document();
        Assert.Equal(EntityNameGenerator.PlaceName(empty, LabelPreset.City, 42), EntityNameGenerator.PlaceName(empty, LabelPreset.City, 42));
        var names = Enumerable.Range(0, 40).Select(seed => EntityNameGenerator.PlaceName(empty, LabelPreset.City, seed)).ToList();
        Assert.True(names.Distinct().Count() >= 30, string.Join(", ", names));
        Assert.All(names, n => Assert.Matches("(град|бург|ск|поль|мост|гард)$", n));
        Assert.All(Enumerable.Range(0, 10).Select(s => EntityNameGenerator.PlaceName(empty, LabelPreset.Sea, s)),
            n => Assert.Matches("(море|залив|взморье)$", n));

        var taken = EntityNameGenerator.PlaceName(empty, LabelPreset.River, 7);
        var withLabel = Document(null, null, Label(new TextPathBinding(), taken));
        Assert.NotEqual(taken, EntityNameGenerator.PlaceName(withLabel, LabelPreset.River, 7));
    }

    [Fact]
    public void Presets_change_the_look_but_keep_words_font_and_binding()
    {
        var binding = new TextPathBinding { TargetId = Guid.NewGuid() };
        var text = new TextStyle { Content = "Альвария", RequestedFontFamily = "Georgia", Path = binding };
        var sizes = LabelPresets.All.Select(p => LabelPresets.Apply(text, p)).ToList();
        Assert.Equal(7, sizes.Count);
        Assert.All(sizes, s => { Assert.Equal("Альвария", s.Content); Assert.Equal("Georgia", s.RequestedFontFamily); Assert.Same(binding, s.Path); });
        Assert.True(LabelPresets.Apply(text, LabelPreset.Country).FontSize > LabelPresets.Apply(text, LabelPreset.Village).FontSize);
        Assert.Equal("Море", LabelPresets.Title(LabelPreset.Sea));
    }

    [Fact]
    public void Moving_another_object_leaves_bound_labels_and_the_command_untouched()
    {
        var river = River(new(100, 500), new(900, 500));
        var session = Session(river, OnRiver(river));
        var house = new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle, "Дом", 10, 10, 50, 40, 0, "#FF000000", "#FF000000", 0, true, false);
        session.Execute(new AddObjectCommand(Labels, house));
        var label = LabelOf2(session.Document);
        var before = session.Document;
        var moved = SceneObjectClone.At(house, 200, 100);
        var after = new ReplaceObjectsCommand([house], [moved], "move").Apply(before);
        Assert.Same(after, TextOnPath.Follow(before, after));
        session.Execute(new ReplaceObjectsCommand([house], [moved], "move"));
        Assert.Same(label, LabelOf2(session.Document));
    }

    private static SceneObject LabelOf2(MapDocument document) =>
        document.Layers.OfType<ObjectLayer>().Single(l => l.Id == Labels).Objects.Single(o => o.Text is not null);

    private sealed class SwapLayerCommand(MapLayer before, MapLayer after) : IEditCommand
    {
        public string Name => "swap";
        public MapDocument Apply(MapDocument document) => document.WithLayer(before.Id, _ => after);
        public MapDocument Revert(MapDocument document) => document.WithLayer(before.Id, _ => before);
    }
}
