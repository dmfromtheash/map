using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests;

/// <summary>R08: the live legend follows the map, the scale bar follows the canvas scale, and both refresh inside the same Undo step.</summary>
public sealed class MapDecorationsTests
{
    [Fact]
    public void A_legend_lists_what_the_map_uses_and_follows_each_edit_within_one_undo_step()
    {
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [Legend()]);
        var document = new MapDocument(DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1000, 1000, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF4E86A3"), objects], [Asset(Sha, "Ель 3")]);
        var session = new EditingSession(document);
        Assert.Empty(LegendOf(session.Document).Entries);

        var tree = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Ель", 10, 10, 20, 20, 0,
            "#FFFFFFFF", "#FF000000", 0, true, false) { Stamp = new StampStyle(Sha) };
        session.Execute(new AddObjectCommand(objects.Id, tree));
        var legend = LegendOf(session.Document);
        Assert.Contains(legend.Entries, e => e.Kind == LegendEntryKind.Asset && e.Label == "Ель");
        Assert.Contains(legend.Entries, e => e.Kind == LegendEntryKind.Water);

        session.Undo();
        Assert.DoesNotContain(LegendOf(session.Document).Entries, e => e.Kind == LegendEntryKind.Asset);
        session.Redo();
        Assert.Contains(LegendOf(session.Document).Entries, e => e.Kind == LegendEntryKind.Asset);
    }

    [Fact]
    public void Asset_variants_share_one_legend_line_and_hidden_layers_are_left_out()
    {
        var sha1 = new string('a', 64);
        var sha2 = new string('b', 64);
        SceneObject Stamp(string sha) => new(ObjectId.New(), SceneObjectKind.Stamp, "Ель", 10, 10, 20, 20, 0,
            "#FFFFFFFF", "#FF000000", 0, true, false) { Stamp = new StampStyle(sha) };
        var visible = new ObjectLayer(LayerId.New(), "Лес", true, false, 1, [Stamp(sha1), Stamp(sha2)]);
        var hidden = new ObjectLayer(LayerId.New(), "Скрытый", false, false, 1, [Stamp(new string('c', 64))]);
        var document = new MapDocument(DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(100, 100, "px", "#FFFFFFFF"), [Base(), visible, hidden],
            [Asset(sha1, "Ель 1"), Asset(sha2, "Ель 2"), Asset(new string('c', 64), "Дуб")]);
        var entries = MapDecorations.BuildLegend(document).Where(e => e.Kind == LegendEntryKind.Asset).ToList();
        Assert.Single(entries);
        Assert.Equal("Ель", entries[0].Label);
        Assert.Equal(sha1, entries[0].AssetSha256);
    }

    [Fact]
    public void The_scale_bar_follows_the_map_scale_and_rounds_to_a_nice_length()
    {
        var bar = new SceneObject(ObjectId.New(), SceneObjectKind.Decoration, "Линейка", 0, 0, 400, 60, 0,
            "#00000000", "#00000000", 0, true, false) { Decoration = new DecorationStyle { Kind = DecorationKind.ScaleBar } };
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [bar]);
        var session = new EditingSession(new MapDocument(DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1000, 1000, "px", "#FFFFFFFF"), [Base(), layer]));
        session.Execute(new SetMapScaleCommand(null, new MapScale(0.5, "км")));
        Assert.Equal(new MapScale(0.5, "км"), session.Document.Canvas.Scale);
        var style = session.Document.Layers.OfType<ObjectLayer>().Single().Objects.Single().Decoration!;
        Assert.Equal(new MapScale(0.5, "км"), style.Scale);

        var layout = MapDecorations.ScaleBarLayout(style.Scale, 360);
        Assert.Equal(100, layout.TotalDistance); // 360 px × 0,5 = 180 км → 100 км
        Assert.Equal(200, layout.TotalPixels);
        Assert.Equal(5, layout.Segments);
        Assert.Equal("км", layout.Unit);

        session.Undo();
        Assert.Null(session.Document.Canvas.Scale);
        Assert.Null(session.Document.Layers.OfType<ObjectLayer>().Single().Objects.Single().Decoration!.Scale);
    }

    [Fact]
    public void A_canvas_resize_keeps_the_scale_and_distances_format_in_russian()
    {
        var canvas = new CanvasDefinition(100, 100, "px", "#FFFFFFFF") { Scale = new MapScale(0.125, "миль") };
        Assert.Equal(canvas.Scale, canvas.Resized(300, 200).Scale);
        Assert.Equal("12,5 миль", canvas.Scale!.Format(100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapScale(0, "км"));
        Assert.Equal("Ель", MapDecorations.BaseName("Ель (12)"));
    }

    private static readonly string Sha = new('d', 64);

    private static BaseColorLayer Base() => new(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF");

    private static SceneObject Legend() => new(ObjectId.New(), SceneObjectKind.Decoration, "Легенда", 0, 0, 200, 300, 0,
        "#00000000", "#00000000", 0, true, false) { Decoration = new DecorationStyle { Kind = DecorationKind.Legend } };

    private static DecorationStyle LegendOf(MapDocument document) =>
        document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Single(o => o.Kind == SceneObjectKind.Decoration).Decoration!;

    private static MapAssetReference Asset(string sha, string name) =>
        new(sha, name, "image/png", "png", ProjectAssetStorage.Embedded, DateTimeOffset.UnixEpoch, "test", "CC0-1.0",
            embeddedContent: [1, 2, 3]);
}
