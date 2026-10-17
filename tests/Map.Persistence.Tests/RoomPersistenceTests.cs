using System.IO.Compression;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class RoomPersistenceTests
{
    [Fact]
    public void Room_survives_reopen_and_an_ordinary_shape_writes_no_room_field()
    {
        using var workspace = new TempWorkspace();
        var room = new RoomStyle { CornerRadius = 22, CapScale = 3, CapAngleDegrees = -15, CapShadow = false };
        var roomShape = VectorGeometry.CreateShape(ObjectId.New(), "Комната 1", ShapePrimitiveKind.Polygon,
                new WorldBounds(10, 10, 210, 150), new ShapeFillStyle { Color = "#FFC9BFA8" },
                new VectorStrokeStyle { Color = "#FF3A342E", Width = 14, BlocksLight = true })
            with { Room = room };
        roomShape = Map.Core.Editing.VectorObjectEditor.AddPortal(roomShape, 0.3, 40);
        var plain = VectorGeometry.CreateShape(ObjectId.New(), "Фигура", ShapePrimitiveKind.Rectangle, new WorldBounds(300, 10, 400, 80));
        var document = new MapDocument(DocumentId.New(), "rooms", DateTimeOffset.UnixEpoch, new CanvasDefinition(512, 256, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "bg", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Комнаты", true, false, 1, [roomShape, plain])]);
        var path = workspace.File("rooms.mapproj");

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var objects = MapProjectStore.Open(path).Document.Layers.OfType<ObjectLayer>().Single().Objects;

        Assert.Equal(room, objects[0].Room);
        Assert.True(objects[0].Shape!.Stroke.BlocksLight);
        Assert.Single(objects[0].Shape!.Portals);
        Assert.Null(objects[1].Room);
        using var zip = ZipFile.OpenRead(path);
        // Objects may live in streamed chunks: count over every JSON part of the container.
        var json = string.Concat(zip.Entries.Where(e => e.FullName.EndsWith(".json", StringComparison.Ordinal))
            .Select(e => new StreamReader(e.Open()).ReadToEnd()));
        Assert.Equal(1, CountOf(json, "\"room\""));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal)) count++;
        return count;
    }
}
