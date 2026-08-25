using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class Stage8CompositionTests
{
    [Fact]
    public void Cross_document_paste_remaps_ids_preserves_relative_geometry_and_defaults_exclude_layers()
    {
        var sourceItem = new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle, "part",
            100, 200, 80, 40, 0, "#80112233", "#FF000000", 0, true, false);
        var source = Document(sourceItem,
            new GridLayer(LayerId.New(), "grid", true, false, 1,
                [new GridDefinition(GridId.New(), GridGeometry.Square, 100)]),
            new FilterLayer(LayerId.New(), "filter", true, false, 1, new FilterSettings { Brightness = 0.2 }));
        var package = CompositionTransfer.Copy(source, [sourceItem.Id],
            new CompositionCaptureOptions { ReferenceGridScale = 100 });
        var target = Document(); var targetLayer = target.Layers.OfType<ObjectLayer>().Single();
        var instance = CompositionTransfer.CreateInstance(package, 500, 400, 200);
        var command = new PasteCompositionCommand(targetLayer.Id, instance, package.Assets);
        var pasted = command.Apply(target);
        var result = pasted.Layers.OfType<ObjectLayer>().Single().Objects.Single();

        Assert.NotEqual(sourceItem.Id, result.Id);
        Assert.NotEqual(sourceItem.Id, result.Composition!.Items.Single().Id);
        Assert.Equal(0, result.Composition.Items.Single().X, 8);
        Assert.Equal(160, result.Width, 8); // reference scale 100 -> 200
        Assert.False(result.Composition.IncludeBackground);
        Assert.False(result.Composition.IncludeGrids);
        Assert.False(result.Composition.IncludeFilters);
        Assert.Empty(result.Composition.Grids); Assert.Empty(result.Composition.Filters);
        Assert.Empty(command.Revert(pasted).Layers.OfType<ObjectLayer>().Single().Objects);
    }

    [Fact]
    public void Missing_pasted_asset_becomes_visible_placeholder_and_asset_dedupe_is_by_hash()
    {
        var hash = new string('a', 64);
        var stamp = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "missing",
            0, 0, 64, 64, 0, "#00000000", "#00000000", 0, true, false)
        { Stamp = new StampStyle(hash) };
        var package = CompositionTransfer.Copy(Document(stamp), [stamp.Id]);
        var instance = CompositionTransfer.CreateInstance(package, 100, 100);
        Assert.Equal(SceneObjectKind.Placeholder, instance.Composition!.Items.Single().Kind);
        Assert.Contains(hash, instance.Composition.Items.Single().PlaceholderReason);
    }

    [Fact]
    public void Editable_composition_replacement_is_one_reversible_command()
    {
        var a = Rectangle(10, 20); var b = Rectangle(90, 20);
        var document = Document(a, b); var layer = document.Layers.OfType<ObjectLayer>().Single();
        var package = CompositionTransfer.Copy(document, [a.Id, b.Id]);
        var instance = CompositionTransfer.CreateInstance(package, 100, 50);
        var session = new EditingSession(document);
        session.Execute(new ReplaceObjectsWithCompositionCommand(document, layer.Id, [a.Id, b.Id], instance));
        Assert.Single(session.Document.Layers.OfType<ObjectLayer>().Single().Objects);
        Assert.True(session.Undo());
        Assert.Equal([a.Id, b.Id], session.Document.Layers.OfType<ObjectLayer>().Single().Objects.Select(item => item.Id));
    }

    private static SceneObject Rectangle(double x, double y) => new(ObjectId.New(), SceneObjectKind.Rectangle,
        "part", x, y, 50, 30, 0, "#8080A0C0", "#FF000000", 0, true, false);

    private static MapDocument Document(params object[] content)
    {
        var objects = content.OfType<SceneObject>().ToList();
        var layers = new List<MapLayer>
        {
            new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
            new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects),
        };
        layers.AddRange(content.OfType<MapLayer>());
        return new MapDocument(DocumentId.New(), "composition", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(65_536, 65_536, "px", "#FFFFFFFF"), layers);
    }
}
