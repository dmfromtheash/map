using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>
/// R07b: a region's name sits inside its figure along the widest axis and follows outline edits;
/// a hand move is kept as the label's shift; far away the less important labels hide first, and
/// of two crossing labels the less important one; a placement area leaves the ground under labels.
/// </summary>
public sealed class RegionLabelTests
{
    private static readonly string Sha = new('a', 64);

    [Fact]
    public void A_name_sits_inside_along_the_widest_axis_even_in_a_bent_figure()
    {
        // A long band turned by 20°: the name follows it.
        var radians = 20 * Math.PI / 180;
        VectorPoint Turn(double x, double y) => new(500 + (x * Math.Cos(radians)) - (y * Math.Sin(radians)), 500 + (x * Math.Sin(radians)) + (y * Math.Cos(radians)));
        var band = RegionLabels.Place([Turn(-400, -60), Turn(400, -60), Turn(400, 60), Turn(-400, 60)])!.Value;
        Assert.Equal(20, band.AngleDegrees, 0);
        Assert.InRange(band.Span, 700, 801);

        // A roundish figure gets a level name.
        Assert.Equal(0, RegionLabels.Place([new(0, 0), new(300, 0), new(300, 260), new(0, 260)])!.Value.AngleDegrees);

        // A «C»: the middle of its points is outside, the name still lands inside.
        VectorPoint[] c = [new(0, 0), new(600, 0), new(600, 120), new(140, 120), new(140, 480), new(600, 480), new(600, 600), new(0, 600)];
        var inside = RegionLabels.Place(c)!.Value;
        Assert.True(Inside(c, inside.Centre));
    }

    [Fact]
    public void A_region_label_follows_the_outline_keeps_a_hand_move_and_takes_the_card_name()
    {
        var (session, layer, shape, label) = Session();
        var card = new MapEntity { Name = "Долина Ветров", Kind = EntityKind.Region, Bindings = [new EntityBinding(null, EntityTargetKind.Object, shape.Id.Value, 0, 0)] };
        session.Execute(new SetEntitiesCommand([], [card], "card"));
        session.Execute(new AddObjectCommand(layer.Id, label with { Text = label.Text! with { Region = label.Text.Region! with { EntityId = card.Id } } }));
        var placed = Label(session.Document);
        Assert.Equal("Долина Ветров", placed.Text!.Content);
        Assert.True(Inside(RegionLabels.Outline(session.Document, placed.Text.Region!)!, Centre(placed)));

        // The figure moves 300 to the right: the name goes with it.
        var current = Shape(session.Document);
        session.Execute(new TransformObjectCommand(current, SceneObjectClone.At(current, current.X + 300, current.Y)));
        var followed = Label(session.Document);
        Assert.Equal(placed.X + 300, followed.X, 1);

        // Moved by hand 40 down: kept as the shift, and kept through the next outline edit.
        session.Execute(new TransformObjectCommand(followed, SceneObjectClone.At(followed, followed.X, followed.Y + 40)));
        Assert.Equal(40, Label(session.Document).Text!.Region!.OffsetY, 1);
        current = Shape(session.Document);
        session.Execute(new TransformObjectCommand(current, SceneObjectClone.At(current, current.X - 100, current.Y)));
        Assert.Equal(followed.X - 100, Label(session.Document).X, 1);
        Assert.Equal(followed.Y + 40, Label(session.Document).Y, 1);

        // The card is renamed: so is the label. Undo brings the old name back.
        session.Execute(new SetEntitiesCommand(session.Document.Entities, [card with { Name = "Долина Бурь" }], "rename"));
        Assert.Equal("Долина Бурь", Label(session.Document).Text!.Content);
        Assert.True(session.Undo());
        Assert.Equal("Долина Ветров", Label(session.Document).Text!.Content);

        // The figure is deleted: the label stays where it was, marked detached.
        var before = Label(session.Document);
        session.Execute(new DeleteObjectsCommand(session.Document, [Shape(session.Document).Id]));
        Assert.True(Label(session.Document).Text!.Region!.Detached);
        Assert.Equal(before.X, Label(session.Document).X);
    }

    [Fact]
    public void Far_away_less_important_labels_hide_first_and_overlaps_hide_the_lesser()
    {
        var minor = Text("Хутор", 100, 100, 1);
        var major = Text("Империя", 600, 600, 5);
        var crossing = Text("Мыс", 610, 605, 2);
        var document = Doc(minor, major, crossing) with { Labels = new LabelSettings { ByScale = true } };
        Assert.Empty(LabelPriority.Hidden(document, 1));
        var far = LabelPriority.Hidden(document, 0.2);
        Assert.Contains(minor.Id, far);
        Assert.DoesNotContain(major.Id, far);
        Assert.Contains(crossing.Id, LabelPriority.Hidden(document, 0.1));

        var overlaps = document with { Labels = new LabelSettings { ResolveOverlaps = true } };
        var hidden = LabelPriority.Hidden(overlaps, 1);
        Assert.Contains(crossing.Id, hidden);
        Assert.DoesNotContain(major.Id, hidden);
        Assert.DoesNotContain(minor.Id, hidden);
        Assert.Empty(LabelPriority.Hidden(Doc(minor, major, crossing), 0.01));
        Assert.Throws<ArgumentException>(() => new LabelSettings { MinScales = [1, 2] }.Validate());
    }

    [Fact]
    public void A_placement_area_leaves_the_ground_under_labels_when_asked()
    {
        var label = Text("Лес Теней", 700, 900, 4, size: 60);
        var bounds = SceneGeometry.BoundsOf(label);
        bool Under(MapDocument d) => d.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)
            .Any(o => o.Kind == SceneObjectKind.Stamp && SceneGeometry.BoundsOf(o).Intersects(bounds));
        Assert.True(Under(FillArea(label, clear: false)));
        Assert.False(Under(FillArea(label, clear: true)));
    }

    private static MapDocument FillArea(SceneObject label, bool clear)
    {
        var layer = new ObjectLayer(LayerId.New(), "Objects", true, false, 1, []);
        var labels = new ObjectLayer(LayerId.New(), "Подписи", true, false, 1, [label]);
        var asset = new MapAssetReference(Sha, "tree", "image/png", "png", ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "test", "test", catalogId: Sha);
        var document = new MapDocument(DocumentId.New(), "Areas", DateTimeOffset.UnixEpoch, new CanvasDefinition(2048, 2048, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFFFFFFF"), layer, labels], [asset])
        { Labels = new LabelSettings { ClearUnderLabels = clear } };
        var session = new EditingSession(document);
        var area = PlacementAreaPlanner.InitializeComposition(new PlacementArea
        {
            Name = "Trees", LayoutVersion = 4, Density = 80, Seed = 18, Pattern = PlacementAreaPattern.Masses,
            Nodes = [new(Guid.NewGuid(), 10, 10), new(Guid.NewGuid(), 1900, 10), new(Guid.NewGuid(), 1900, 1900), new(Guid.NewGuid(), 10, 1900)],
            Variants = [new(Sha, 32, 32)], Style = new(Sha), Request = new() { BaseWidth = 40 },
        });
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "create"));
        Assert.Null(session.LastResourceError);
        return session.Document;
    }

    private static (EditingSession Session, ObjectLayer Layer, SceneObject Shape, SceneObject Label) Session()
    {
        var shape = VectorGeometry.CreateFreeShape(ObjectId.New(), "Долина", [new(100, 300), new(900, 260), new(950, 520), new(120, 560)]);
        var layer = new ObjectLayer(LayerId.New(), "Objects", true, false, 1, [shape]);
        var document = new MapDocument(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(2048, 1024, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), layer]);
        var label = Text("Долина", 0, 0, 4) with
        {
            Text = Text("Долина", 0, 0, 4).Text! with { Region = new TextRegionBinding { Target = TextRegionTarget.Shape, TargetId = shape.Id.Value } },
        };
        return (new EditingSession(document), layer, shape, label);
    }

    private static SceneObject Text(string content, double x, double y, int importance, double size = 32) =>
        new SceneObject(ObjectId.New(), SceneObjectKind.Text, content, x, y, content.Length * size * .62, size * 1.3, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = content, FontSize = size, Importance = importance, Alignment = TextAlignment.Center } };

    private static MapDocument Doc(params SceneObject[] labels) =>
        new(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(2048, 2048, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), new ObjectLayer(LayerId.New(), "Подписи", true, false, 1, labels)]);

    private static SceneObject Label(MapDocument document) =>
        document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Single(o => o.Text?.Region is not null);

    private static SceneObject Shape(MapDocument document) =>
        document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Single(o => o.Kind == SceneObjectKind.Shape);

    private static VectorPoint Centre(SceneObject o) => new(o.X + (o.Width / 2), o.Y + (o.Height / 2));

    private static bool Inside(IReadOnlyList<VectorPoint> polygon, VectorPoint p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            if ((polygon[i].Y > p.Y) != (polygon[j].Y > p.Y)
                && p.X < ((polygon[j].X - polygon[i].X) * (p.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y)) + polygon[i].X)
                inside = !inside;
        return inside;
    }
}
