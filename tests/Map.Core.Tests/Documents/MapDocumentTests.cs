using Map.Core.Documents;

namespace Map.Core.Tests.Documents;

public class MapDocumentTests
{
    private static SceneObject Object(string name = "object") => new(
        ObjectId.New(), SceneObjectKind.Rectangle, name,
        x: 10, y: 20, width: 30, height: 40, rotationDegrees: 15,
        fillColor: "#FF3E5C76", strokeColor: "#FF1B263B", strokeWidth: 2,
        visible: true, locked: false);

    [Fact]
    public void Ids_round_trip_through_their_canonical_string_form()
    {
        var documentId = DocumentId.New();
        var layerId = LayerId.New();
        var objectId = ObjectId.New();

        Assert.Equal(documentId, DocumentId.Parse(documentId.ToString()));
        Assert.Equal(layerId, LayerId.Parse(layerId.ToString()));
        Assert.Equal(objectId, ObjectId.Parse(objectId.ToString()));

        // Canonical "D" form: lowercase, hyphenated, no braces.
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", documentId.ToString());
    }

    [Fact]
    public void Layers_and_objects_keep_their_order()
    {
        var objects = new[] { Object("first"), Object("second"), Object("third") };
        var layer = new Layer(LayerId.New(), "layer", visible: true, locked: false, opacity: 1.0, objects);

        var document = new MapDocument(
            DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [layer]);

        Assert.Equal(["first", "second", "third"], document.Layers[0].Objects.Select(o => o.Name));
    }

    [Fact]
    public void The_stored_collections_do_not_alias_the_caller_lists()
    {
        var objects = new List<SceneObject> { Object() };
        var layer = new Layer(LayerId.New(), "layer", true, false, 1.0, objects);

        var layers = new List<Layer> { layer };
        var document = new MapDocument(
            DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(64, 64, "px", "#FFFFFFFF"),
            layers);

        objects.Add(Object("smuggled"));
        layers.Clear();

        Assert.Single(document.Layers);
        Assert.Single(document.Layers[0].Objects);
    }

    [Fact]
    public void Duplicate_layer_or_object_ids_are_rejected()
    {
        var layerId = LayerId.New();
        var canvas = new CanvasDefinition(64, 64, "px", "#FFFFFFFF");

        var duplicateLayers = new[]
        {
            new Layer(layerId, "a", true, false, 1.0, []),
            new Layer(layerId, "b", true, false, 1.0, []),
        };

        Assert.Throws<ArgumentException>(() =>
            new MapDocument(DocumentId.New(), "map", DateTimeOffset.UnixEpoch, canvas, duplicateLayers));

        var sharedObject = Object();
        var duplicateObjects = new[]
        {
            new Layer(LayerId.New(), "a", true, false, 1.0, [sharedObject]),
            new Layer(LayerId.New(), "b", true, false, 1.0, [sharedObject]),
        };

        Assert.Throws<ArgumentException>(() =>
            new MapDocument(DocumentId.New(), "map", DateTimeOffset.UnixEpoch, canvas, duplicateObjects));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Layer_opacity_outside_the_unit_range_is_rejected(double opacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Layer(LayerId.New(), "layer", true, false, opacity, []));
    }

    [Theory]
    [InlineData(0, 64)]
    [InlineData(64, 0)]
    [InlineData(-1, 64)]
    public void Canvas_dimensions_must_be_positive(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CanvasDefinition(width, height, "px", "#FFFFFFFF"));
    }

    [Fact]
    public void With_mutation_produces_a_new_object_and_leaves_the_original_alone()
    {
        var original = Object("original");
        var moved = original with { };

        Assert.Equal(original, moved);
        Assert.NotSame(original, moved);
    }
}
