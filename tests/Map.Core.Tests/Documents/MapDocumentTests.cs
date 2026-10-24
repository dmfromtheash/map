using Map.Core.Documents;

namespace Map.Core.Tests.Documents;

public class MapDocumentTests
{
    private static BaseColorLayer Base() => new(
        LayerId.New(), "background", true, false, 1.0, "#FFFFFFFF");

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
        var layer = new ObjectLayer(
            LayerId.New(), "layer", visible: true, locked: false, opacity: 1.0, objects);

        var document = new MapDocument(
            DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [Base(), layer]);

        Assert.Equal(["first", "second", "third"], layer.Objects.Select(o => o.Name));
    }

    [Fact]
    public void Land_is_canonical_below_objects_while_other_relative_order_is_preserved()
    {
        var firstObjects = new ObjectLayer(LayerId.New(), "objects one", true, false, 1, []);
        var brush = BrushLayer.CreateEmpty(LayerId.New(), "brush");
        var secondObjects = new ObjectLayer(LayerId.New(), "objects two", true, false, 1, []);
        var firstLand = LandLayer.CreateEmpty(LayerId.New(), "land one");
        var secondLand = LandLayer.CreateEmpty(LayerId.New(), "land two");

        var document = new MapDocument(
            DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(64, 64, "px", "#FFFFFFFF"),
            [Base(), firstObjects, brush, firstLand, secondObjects, secondLand]);

        Assert.Equal(
            ["background", "land one", "land two", "objects one", "brush", "objects two"],
            document.Layers.Select(layer => layer.Name));
    }

    [Fact]
    public void The_stored_collections_do_not_alias_the_caller_lists()
    {
        var objects = new List<SceneObject> { Object() };
        var layer = new ObjectLayer(LayerId.New(), "layer", true, false, 1.0, objects);

        var layers = new List<MapLayer> { Base(), layer };
        var document = new MapDocument(
            DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(64, 64, "px", "#FFFFFFFF"),
            layers);

        objects.Add(Object("smuggled"));
        layers.Clear();

        Assert.Equal(2, document.Layers.Count);
        Assert.Single(((ObjectLayer)document.Layers[1]).Objects);
    }

    [Fact]
    public void Duplicate_layer_or_object_ids_are_rejected()
    {
        var layerId = LayerId.New();
        var canvas = new CanvasDefinition(64, 64, "px", "#FFFFFFFF");

        MapLayer[] duplicateLayers =
        [
            Base(),
            new ObjectLayer(layerId, "a", true, false, 1.0, []),
            new ObjectLayer(layerId, "b", true, false, 1.0, []),
        ];

        Assert.Throws<ArgumentException>(() =>
            new MapDocument(DocumentId.New(), "map", DateTimeOffset.UnixEpoch, canvas, duplicateLayers));

        var sharedObject = Object();
        MapLayer[] duplicateObjects =
        [
            Base(),
            new ObjectLayer(LayerId.New(), "a", true, false, 1.0, [sharedObject]),
            new ObjectLayer(LayerId.New(), "b", true, false, 1.0, [sharedObject]),
        ];

        Assert.Throws<ArgumentException>(() =>
            new MapDocument(DocumentId.New(), "map", DateTimeOffset.UnixEpoch, canvas, duplicateObjects));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Layer_opacity_outside_the_unit_range_is_rejected(double opacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ObjectLayer(LayerId.New(), "layer", true, false, opacity, []));
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

    [Fact]
    public void Resizing_the_canvas_keeps_the_style_and_light_and_moves_the_lights()
    {
        var style = MapStyle.For(MapStylePreset.InkPenHatching) with { Strength = 0.6 };
        var torch = new MapLightSource(Guid.NewGuid(), 100, 50, "#ff9a42", 200, 1, true);
        var document = new MapDocument(
            DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [Base()]) { Style = style, Lighting = new MapLighting { Sources = [torch] } };
        // Grow from the bottom-right corner: the content moves right and down by the growth.
        var plan = Map.Core.Editing.CanvasTransformService.PlanResize(document, 1224, 868, Map.Core.Editing.CanvasAnchor.BottomRight);
        var resized = Map.Core.Editing.CanvasTransformService.ApplyResize(document, plan);
        Assert.Same(style, resized.Style);
        var moved = Assert.Single(resized.Lighting!.Sources);
        Assert.Equal(100 + plan.OffsetX, moved.X);
        Assert.Equal(50 + plan.OffsetY, moved.Y);
    }

    [Fact]
    public void Adding_or_removing_a_layer_keeps_the_light_of_the_map()
    {
        var lighting = new MapLighting { TimeOfDay = 0.5 };
        var document = new MapDocument(
            DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [Base()]) { Lighting = lighting };
        var extra = new ObjectLayer(LayerId.New(), "layer", visible: true, locked: false, opacity: 1.0, [Object()]);

        var added = new Map.Core.Editing.AddLayerCommand(extra).Apply(document);
        Assert.Same(lighting, added.Lighting);
        Assert.Same(lighting, document.WithLayers([document.Layers[0]]).Lighting);
    }
}
