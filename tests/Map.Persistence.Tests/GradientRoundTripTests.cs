using System.IO.Compression;
using System.Text;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>
/// The gradient fill is new state in the project file, so it has to pass the same three gates the
/// rest of the format passes: it survives save/reopen exactly, a project without one is written
/// exactly as before, and a stored gradient that could not be drawn is corruption rather than data.
/// </summary>
public sealed partial class GradientRoundTripTests
{
    [System.Text.RegularExpressions.GeneratedRegex("\"sceneSha256\"\\s*:\\s*\"[0-9a-f]+\"")]
    private static partial System.Text.RegularExpressions.Regex SceneShaRegex();

    [Fact]
    public void A_shape_gradient_and_an_object_gradient_both_survive_save_and_reopen()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("gradient.mapproj");
        var document = GradientDocument();

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path).Document;

        ProjectFixtures.AssertSemanticallyEqual(document, reopened);

        var objects = ((ObjectLayer)reopened.Layers[1]).Objects;
        var shape = objects[0].Shape!.Fill.Gradient!;
        Assert.Equal(GradientKind.Linear, shape.Kind);
        Assert.Equal(135, shape.AngleDegrees);
        Assert.Equal(["#FF102030", "#80AABBCC", "#FFFFFFFF"], shape.Stops.Select(stop => stop.Color));
        Assert.Equal([0, 0.35, 1], shape.Stops.Select(stop => stop.Offset));

        var primitive = objects[1].FillGradient!;
        Assert.Equal(GradientKind.Radial, primitive.Kind);
        Assert.Equal(0.25, primitive.CenterX);
        Assert.Equal(0.75, primitive.CenterY);
        Assert.Equal(1.5, primitive.Radius);
    }

    [Fact]
    public void A_project_without_gradients_writes_no_gradient_at_all()
    {
        // The field is optional on purpose: every map made before gradients existed keeps exactly
        // the scene content it had, and opening one does not invent a gradient.
        using var workspace = new TempWorkspace();
        var path = workspace.File("flat.mapproj");

        MapProjectStore.Save(
            FlatDocument(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        Assert.DoesNotContain("gradient", ReadScene(path), StringComparison.OrdinalIgnoreCase);

        var objects = ((ObjectLayer)MapProjectStore.Open(path).Document.Layers[1]).Objects;
        Assert.Null(objects[0].Shape!.Fill.Gradient);
        Assert.Null(objects[1].FillGradient);
    }

    [Theory]
    [InlineData("\"offset\":0.35", "\"offset\":4")]
    [InlineData("\"kind\":\"Linear\"", "\"kind\":\"Conic\"")]
    [InlineData("\"color\":\"#80AABBCC\"", "\"color\":\"не цвет\"")]
    [InlineData("\"radius\":1.5", "\"radius\":0")]
    [InlineData(
        "\"stops\":[{\"offset\":0,\"color\":\"#FF102030\"},{\"offset\":0.35,\"color\":\"#80AABBCC\"}"
            + ",{\"offset\":1,\"color\":\"#FFFFFFFF\"}]",
        "\"stops\":[{\"offset\":0,\"color\":\"#FF102030\"}]")]
    public void A_stored_gradient_the_renderer_could_not_draw_is_corruption(string from, string to)
    {
        // One gradient value at a time is moved outside the contract, with the manifest checksum
        // repaired so the file is otherwise valid. Opening must fail with the typed error rather
        // than hand the renderer a shader it cannot build.
        using var workspace = new TempWorkspace();
        var path = workspace.File("broken.mapproj");
        MapProjectStore.Save(
            GradientDocument(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        RewriteScene(path, json =>
        {
            Assert.Contains(from, json, StringComparison.Ordinal);
            return json.Replace(from, to, StringComparison.Ordinal);
        });

        Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
    }

    // ------------------------------------------------------------------------ fixtures

    private static MapDocument GradientDocument()
    {
        var shape = VectorGeometry.CreateShape(
            ObjectId.New(), "Фигура 1", ShapePrimitiveKind.Rectangle,
            new WorldBounds(100, 100, 300, 260));
        shape = shape with
        {
            Shape = new ShapeStyle(
                shape.Shape!.Regions,
                shape.Shape.Fill with
                {
                    Gradient = new GradientFill(GradientKind.Linear,
                    [
                        new GradientStopStyle(0, "#FF102030"),
                        new GradientStopStyle(0.35, "#80AABBCC"),
                        new GradientStopStyle(1, "#FFFFFFFF"),
                    ], angleDegrees: 135),
                },
                shape.Shape.Stroke,
                shape.Shape.Portals),
        };

        var primitive = Rectangle() with
        {
            FillGradient = new GradientFill(GradientKind.Radial,
            [
                new GradientStopStyle(0, "#FFFFEE00"),
                new GradientStopStyle(1, "#00FFEE00"),
            ], centerX: 0.25, centerY: 0.75, radius: 1.5),
        };

        return Document(shape, primitive);
    }

    private static MapDocument FlatDocument() => Document(
        VectorGeometry.CreateShape(
            ObjectId.New(), "Фигура 1", ShapePrimitiveKind.Rectangle,
            new WorldBounds(100, 100, 300, 260)),
        Rectangle());

    private static SceneObject Rectangle() => new(
        ObjectId.New(), SceneObjectKind.Rectangle, "Прямоугольник 1",
        400, 100, 200, 160, 0, "#FF3355AA", "#FF102030", 2, true, false);

    private static MapDocument Document(params SceneObject[] objects) => new(
        DocumentId.New(), "gradient", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
        [
            new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
            new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects),
        ]);

    private static string ReadScene(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(
            archive.GetEntry(MapProjectFormat.SceneEntry)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Rewrites the first schema-v4 object chunk and repairs both its descriptor and the manifest
    /// checksum, so the only thing left wrong with the file is the gradient value under test.
    /// </summary>
    private static void RewriteScene(string path, Func<string, string> rewrite) =>
        ProjectJsonTestMutations.RewriteFirstObjectChunk(path, objects =>
        {
            var json = objects.ToJsonString();
            var rewritten = rewrite(json);
            var replacement = System.Text.Json.Nodes.JsonNode.Parse(rewritten)!.AsArray();
            objects.Clear();
            foreach (var item in replacement)
            {
                objects.Add(item?.DeepClone());
            }
        });
}
