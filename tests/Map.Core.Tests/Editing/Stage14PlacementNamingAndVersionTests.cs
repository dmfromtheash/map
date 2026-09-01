using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public class Stage14PlacementNamingAndVersionTests
{
    [Fact]
    public void Stage14_scatter_preview_is_a_bounded_exact_prefix_of_commit_plan()
    {
        var request = new StampPlacementRequest
        {
            CenterX = 400,
            CenterY = 300,
            AreaRadius = 240,
            Density = 300,
            Seed = 1401,
            RandomizeVariant = true,
            RandomAngle = true,
            RandomFlip = true,
            BaseWidth = 48,
        };
        StampVariantSource[] variants =
        [
            new("a".PadLeft(64, 'a'), 64, 32),
            new("b".PadLeft(64, 'b'), 40, 80),
        ];

        var preview = StampPlacement.PlanAreaPreview(request, variants, maximumItems: 24);
        var committed = StampPlacement.PlanArea(request, variants);

        Assert.Equal(24, preview.Count);
        Assert.True(committed.Count > preview.Count);
        Assert.Equal(preview, committed.Take(preview.Count));
    }

    [Fact]
    public void Stage14_names_use_next_free_number_and_remain_stable_through_undo_redo()
    {
        var document = Document();
        var layer = document.Layers.OfType<ObjectLayer>().Single();
        var first = Rectangle("Прямоугольник 1", 10);
        var third = Rectangle("Прямоугольник 3", 30);
        var session = new EditingSession(document.WithLayers(
            [document.BaseColor, layer with { Objects = [first, third] }]));

        var name = EntityNameGenerator.Object(session.Document, SceneObjectKind.Rectangle);
        var added = Rectangle(name, 50);
        session.Execute(new AddObjectCommand(layer.Id, added));
        session.Undo();
        session.Redo();

        Assert.Equal("Прямоугольник 2", name);
        Assert.Equal(name, DocumentEditor.FindObject(session.Document, added.Id)!.Name);
        Assert.Equal("Объекты 2", EntityNameGenerator.Layer(session.Document, typeof(ObjectLayer)));
        Assert.Equal("Группа · 2 объектов", EntityNameGenerator.Group(2));
    }

    [Fact]
    public void Stage14_version_rename_is_one_undoable_command_and_preserves_snapshot_preview_order()
    {
        var document = Document();
        var preview = new byte[] { 137, 80, 78, 71 };
        var first = new MapCheckpoint(
            CheckpointId.New(), "Старое пользовательское имя", DateTimeOffset.UnixEpoch,
            MapCheckpointSnapshot.Capture(document), preview);
        var second = new MapCheckpoint(
            CheckpointId.New(), "Версия 01 · 01.09.2026 14:05", DateTimeOffset.UnixEpoch.AddMinutes(1),
            MapCheckpointSnapshot.Capture(document), preview);
        var session = new EditingSession(document.WithCheckpoints([first, second]));

        session.Execute(RenameCheckpointCommand.Create(session.Document, first.Id, "До эксперимента"));
        Assert.Equal(["До эксперимента", second.Name], session.Document.Checkpoints.Select(v => v.Name));
        Assert.Equal(first.PreviewPng.ToArray(), session.Document.Checkpoints[0].PreviewPng.ToArray());
        session.Undo();
        Assert.Equal("Старое пользовательское имя", session.Document.Checkpoints[0].Name);
        session.Redo();
        Assert.Equal("До эксперимента", session.Document.Checkpoints[0].Name);
    }

    private static MapDocument Document()
    {
        var objects = new ObjectLayer(LayerId.New(), "Объекты 1", true, false, 1, []);
        return new MapDocument(
            DocumentId.New(), "Stage 14", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FF20242B"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF20242B"), objects]);
    }

    private static SceneObject Rectangle(string name, double x) => new(
        ObjectId.New(), SceneObjectKind.Rectangle, name,
        x, 10, 20, 20, 0, "#FFFFFFFF", "#FF000000", 1, true, false);
}
