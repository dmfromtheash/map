using System.IO.Compression;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>R08: decorations and the map scale take schema v17 and reopen unchanged; maps without them keep the older schema.</summary>
public sealed class DecorationPersistenceTests
{
    [Fact]
    public void Decorations_and_the_map_scale_reopen_exactly_in_v17()
    {
        using var workspace = new TempWorkspace();
        var document = MapDecorations.Refresh(WithDecorations(new MapScale(0.25, "км")));
        var path = workspace.File("decor.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(17, ReadJson(path, "manifest.json")["schemaVersion"]!.GetValue<int>());

        var reopened = MapProjectStore.Open(path).Document;
        Assert.Equal(new MapScale(0.25, "км"), reopened.Canvas.Scale);
        var saved = Objects(document);
        var loaded = Objects(reopened);
        Assert.Equal(saved.Count, loaded.Count);
        for (var i = 0; i < saved.Count; i++)
        {
            Assert.Equal(SceneObjectKind.Decoration, loaded[i].Kind);
            Assert.Equal(saved[i].Decoration, loaded[i].Decoration);
            Assert.Equal(saved[i].X, loaded[i].X);
        }

        var legend = loaded.Single(o => o.Decoration!.Kind == DecorationKind.Legend).Decoration!;
        Assert.Contains(legend.Entries, e => e.Kind == LegendEntryKind.River);
        Assert.Equal(new MapScale(0.25, "км"), loaded.Single(o => o.Decoration!.Kind == DecorationKind.ScaleBar).Decoration!.Scale);
        // Refreshing a reopened map changes nothing: the stored derived state is current.
        Assert.Same(reopened, MapDecorations.Refresh(reopened));
    }

    [Fact]
    public void A_scale_alone_requires_v17_and_a_plain_map_keeps_its_schema()
    {
        using var workspace = new TempWorkspace();
        var plain = Plain(null);
        var plainPath = workspace.File("plain.mapproj");
        MapProjectStore.Save(plain, plainPath, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.True(ReadJson(plainPath, "manifest.json")["schemaVersion"]!.GetValue<int>() < 17);
        Assert.Null(ReadJson(plainPath, "manifest.json")["canvas"]!["scale"]);

        var scaled = Plain(new MapScale(1.5, "миль"));
        var scaledPath = workspace.File("scaled.mapproj");
        MapProjectStore.Save(scaled, scaledPath, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(17, ReadJson(scaledPath, "manifest.json")["schemaVersion"]!.GetValue<int>());
        Assert.Equal(new MapScale(1.5, "миль"), MapProjectStore.Open(scaledPath).Document.Canvas.Scale);
    }

    private static List<SceneObject> Objects(MapDocument document) =>
        document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).ToList();

    private static MapDocument Plain(MapScale? scale) => new(DocumentId.New(), "plain", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(400, 400, "px", "#FFFFFFFF") { Scale = scale },
        [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF")]);

    internal static MapDocument WithDecorations(MapScale? scale)
    {
        var river = new River { Name = "Река", Style = RiverStyle.Illustrated(), Channels = [RiverEditor.NewChannel([new(50, 200), new(350, 220)], 20)] };
        SceneObject Decoration(DecorationKind kind, double x, double y, double w, double h, DecorationStyle? style = null) =>
            new(ObjectId.New(), SceneObjectKind.Decoration, DecorationStyle.DefaultName(kind), x, y, w, h, 0,
                "#00000000", "#00000000", 0, true, false) { Decoration = style ?? new DecorationStyle { Kind = kind } };
        var layer = new ObjectLayer(LayerId.New(), "Оформление", true, false, 1,
        [
            Decoration(DecorationKind.ScaleBar, 20, 340, 200, 40, new DecorationStyle { Kind = DecorationKind.ScaleBar, Variant = 1 }),
            Decoration(DecorationKind.Compass, 300, 20, 80, 80, new DecorationStyle { Kind = DecorationKind.Compass, Variant = 2, InkColor = "#FF102030" }),
            Decoration(DecorationKind.Cartouche, 100, 20, 180, 60, new DecorationStyle { Kind = DecorationKind.Cartouche, Title = "Северные земли", Subtitle = "1024 г." }),
            Decoration(DecorationKind.Legend, 260, 200, 120, 160),
        ]) { Rivers = [river] };
        return new MapDocument(DocumentId.New(), "decor", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(400, 400, "px", "#FFFFFFFF") { Scale = scale },
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), layer]);
    }

    private static JsonNode ReadJson(string path, string entry)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry(entry)!.Open();
        return JsonNode.Parse(stream)!;
    }
}
