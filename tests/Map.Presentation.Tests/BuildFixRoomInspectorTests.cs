using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class BuildFixRoomInspectorTests
{
    [Theory]
    [InlineData("X")]
    [InlineData("Y")]
    [InlineData("W")]
    [InlineData("H")]
    [InlineData("Rotation")]
    public void Geometry_edits_keep_room_walls_doors_undo_redo_and_save(string field)
    {
        var editor = new EditorViewModel(TestData.Document(), null, false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());
        Assert.True(editor.CreateRoom());
        Assert.True(editor.SetSelectedRoom(r => r with { CornerRadius = 17, CapScale = 2, CapAngleDegrees = 25, CapShadow = false }));
        Assert.True(editor.AddRoomDoor());
        var room = editor.Session.SelectedObject!;
        var depth = editor.Session.UndoDepth;
        var expected = (X: room.X, Y: room.Y, Width: room.Width, Height: room.Height, Rotation: room.RotationDegrees);
        switch (field)
        {
            case "X": editor.XText = "123"; expected.X = 123; break;
            case "Y": editor.YText = "234"; expected.Y = 234; break;
            case "W": editor.WidthText = "345"; expected.Width = 345; break;
            case "H": editor.HeightText = "456"; expected.Height = 456; break;
            case "Rotation": editor.RotationText = "45"; expected.Rotation = 45; break;
        }
        Assert.True(editor.CommitInspector(), editor.InspectorError);
        var edited = editor.Session.SelectedObject!;
        Assert.Same(room.Room, edited.Room);
        Assert.Same(room.Shape, edited.Shape);
        Assert.Equal(expected, (edited.X, edited.Y, edited.Width, edited.Height, edited.RotationDegrees));
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        var after = editor.Session.Document;
        editor.Undo(); Assert.Same(room, DocumentEditor.FindObject(editor.Session.Document, room.Id));
        editor.Redo(); Assert.Same(edited, DocumentEditor.FindObject(editor.Session.Document, room.Id));
        var walls = VttGeometry.Collect(after);
        Assert.NotEmpty(walls.Walls);
        Assert.Single(walls.Portals);

        var path = Path.Combine(Path.GetTempPath(), "map-room-inspector-" + Guid.NewGuid().ToString("N") + ".mapproj");
        try
        {
            var project = new DirectoryInfo(AppContext.BaseDirectory);
            while (project is not null && !File.Exists(Path.Combine(project.FullName, "Map.slnx"))) project = project.Parent;
            var preview = File.ReadAllBytes(Path.Combine(project!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"));
            MapProjectStore.Save(after, path, new SaveMetadata("buildfixes", DateTimeOffset.UnixEpoch), preview);
            var reopened = MapProjectStore.Open(path).Document;
            var saved = DocumentEditor.FindObject(reopened, room.Id)!;
            Assert.Equal(room.Room, saved.Room);
            Assert.Equal(room.Shape!.Stroke, saved.Shape!.Stroke);
            Assert.Equal(room.Shape.Portals.ToArray(), saved.Shape.Portals.ToArray());
            Assert.Equal(expected,
                (saved.X, saved.Y, saved.Width, saved.Height, saved.RotationDegrees));
            Assert.Equal(walls.Walls.Count, VttGeometry.Collect(reopened).Walls.Count);
            Assert.Single(VttGeometry.Collect(reopened).Portals);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
