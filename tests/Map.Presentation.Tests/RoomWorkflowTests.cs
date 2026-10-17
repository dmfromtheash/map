using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>«Комната» from the editor: create, convert, caps, doors, walls for VTT, undo.</summary>
public sealed class RoomWorkflowTests : IDisposable
{
    private readonly List<AssetCatalog> _catalogs = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-room-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void New_room_is_a_selected_wall_that_blocks_light_with_rounded_corners()
    {
        var editor = Editor(out _);
        Assert.True(editor.CreateRoom());
        var room = editor.Session.SelectedObject!;
        Assert.Equal("Комната 1", room.Name);
        Assert.Equal(EditorViewModel.RoomFloorColor, room.Shape!.Fill.Color);
        Assert.True(room.Shape.Stroke.BlocksLight);
        Assert.Equal(EditorViewModel.RoomWallWidth, room.Room!.CornerRadius);
        Assert.Single(VttGeometry.Collect(editor.Session.Document).Walls);
        Assert.Equal(1, editor.Session.UndoDepth);

        Assert.True(editor.AddRoomDoor());
        var walls = VttGeometry.Collect(editor.Session.Document);
        Assert.Single(walls.Portals);
        Assert.Equal(2, walls.Walls.Count);

        Assert.True(editor.SetSelectedRoom(r => r with { CornerRadius = 0 }));
        Assert.True(editor.SetSelectedRoomWallWidth(24));
        Assert.Equal(24, editor.Session.SelectedObject!.Shape!.Stroke.Width);
        Assert.False(editor.SetSelectedRoom(r => r with { CornerRadius = 0 }));
        Assert.True(editor.CreateRoom());
        Assert.Equal("Комната 2", editor.Session.SelectedObject!.Name);
        editor.Undo(); editor.Undo(); editor.Undo(); editor.Undo(); editor.Undo();
        Assert.Empty(editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects));
    }

    [Fact]
    public void A_shape_becomes_a_room_takes_cap_asset_and_goes_back_without_losing_its_look()
    {
        var editor = Editor(out var catalog);
        var shape = VectorGeometry.CreateShape(ObjectId.New(), "Зал", ShapePrimitiveKind.Polygon, new WorldBounds(100, 100, 300, 260),
            new ShapeFillStyle { Color = "#FF778899" }, new VectorStrokeStyle { Color = "#FF112233", Width = 3 });
        var layer = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
        editor.Session.Execute(new AddObjectCommand(layer.Id, shape));
        editor.SelectObject(shape.Id);

        Assert.True(editor.CanMakeSelectedRoom);
        Assert.True(editor.MakeSelectedRoom());
        var room = editor.Session.SelectedObject!;
        Assert.NotNull(room.Room);
        Assert.Equal("#FF778899", room.Shape!.Fill.Color);
        Assert.Equal(6, room.Shape.Stroke.Width);
        Assert.False(editor.CanMakeSelectedRoom);

        editor.SelectAssetSet(catalog.Sets[0].Id);
        editor.SelectObject(shape.Id);
        Assert.True(editor.UseAssetAsRoomCaps(editor.CurrentAssetRecord!));
        var capped = editor.Session.SelectedObject!;
        Assert.Equal(catalog.Records[0].Sha256, capped.Room!.CapAssetSha256);
        Assert.Contains(editor.Session.Document.Assets, a => a.Sha256 == catalog.Records[0].Sha256);

        editor.Undo();
        Assert.Null(editor.Session.SelectedObject!.Room!.CapAssetSha256);
        Assert.DoesNotContain(editor.Session.Document.Assets, a => a.Sha256 == catalog.Records[0].Sha256);
        Assert.True(editor.ClearSelectedRoom());
        Assert.Null(editor.Session.SelectedObject!.Room);
        Assert.Equal("#FF778899", editor.Session.SelectedObject.Shape!.Fill.Color);
    }

    private EditorViewModel Editor(out AssetCatalog catalog)
    {
        catalog = new AssetCatalog(Path.Combine(_root, "catalog-" + Guid.NewGuid().ToString("N")));
        _catalogs.Add(catalog);
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "Колонна.png");
        using (var surface = SKSurface.Create(new SKImageInfo(24, 24))!)
        {
            surface.Canvas.Clear(SKColors.SlateGray);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        catalog.ImportSet([path], "Колонна", new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        var document = new MapDocument(DocumentId.New(), "dungeon", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", "#FF20242B"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF20242B"), new ObjectLayer(LayerId.New(), "Подземелье", true, false, 1, [])]);
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => DateTimeOffset.UnixEpoch, assetCatalog: catalog);
        editor.Session.Viewport.Resize(1024, 768);
        editor.Session.Viewport.FitTo(1024, 768);
        return editor;
    }

    public void Dispose()
    {
        foreach (var catalog in _catalogs) catalog.FlushMetadataAsync().GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
