using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>
/// The two things that decide where an object is drawn have to survive save and reopen: which
/// layer owns it, and its own level inside that layer. A control that changes a value the file
/// then forgets is not a finished feature.
/// </summary>
public sealed class ObjectLayerAndLevelRoundTripTests
{
    [Fact]
    public void The_owner_layer_and_the_object_level_survive_save_and_reopen()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("object-layer-and-level.mapproj");

        var lower = new LayerId(Guid.NewGuid());
        var upper = new LayerId(Guid.NewGuid());
        var document = new MapDocument(
            DocumentId.New(), "уровни", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1920, 1080, "px", "#FF1E2430"),
            [
                new BaseColorLayer(new LayerId(Guid.NewGuid()), "Фон", true, false, 1.0, "#FF1E2430"),
                new ObjectLayer(lower, "Нижние объекты", true, false, 1.0,
                [
                    Rectangle("под водой", -7),
                    Rectangle("по умолчанию", 0),
                ]),
                new ObjectLayer(upper, "Верхние объекты", true, false, 1.0,
                [
                    Rectangle("над всем", 9),
                ]),
            ]);

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path).Document;

        var reopenedLower = (ObjectLayer)reopened.FindLayer(lower)!;
        var reopenedUpper = (ObjectLayer)reopened.FindLayer(upper)!;

        // Ownership: each object came back on the layer it was saved from.
        Assert.Equal(["под водой", "по умолчанию"], reopenedLower.Objects.Select(item => item.Name));
        Assert.Equal(["над всем"], reopenedUpper.Objects.Select(item => item.Name));
        Assert.Equal("Нижние объекты", reopenedLower.Name);
        Assert.Equal("Верхние объекты", reopenedUpper.Name);

        // Level: the exact numbers, including the negative one and the untouched default.
        Assert.Equal(-7, reopenedLower.Objects[0].Sublayer);
        Assert.Equal(0, reopenedLower.Objects[1].Sublayer);
        Assert.Equal(9, reopenedUpper.Objects[0].Sublayer);

        // Order inside the layer is what breaks ties between equal levels, so it is part of the
        // result too: the renderer must answer the same before and after a reopen.
        Assert.Equal(
            SceneGeometry.InDrawOrder((ObjectLayer)document.FindLayer(lower)!).Select(item => item.Name),
            SceneGeometry.InDrawOrder(reopenedLower).Select(item => item.Name));
    }

    private static SceneObject Rectangle(string name, int sublayer) => new(
        ObjectId.New(), SceneObjectKind.Rectangle, name,
        10, 10, 100, 80, 0, "#FF3E5C76", "#FF10131A", 2, true, false)
    {
        Sublayer = sublayer,
    };
}
