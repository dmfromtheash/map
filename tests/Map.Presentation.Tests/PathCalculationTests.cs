using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>
/// R20b: placement along a path goes through the background pipeline. The continuations of the calculation
/// are queued on a manual context and run only when the test pumps them, so «the map changed meanwhile»
/// is deterministic.
/// </summary>
public sealed class PathCalculationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-r20b-" + Guid.NewGuid().ToString("N"));
    private readonly List<AssetCatalog> _catalogs = [];

    [Fact]
    public void Cancel_during_the_calculation_leaves_the_document_and_history_untouched()
        => OnManualContext(context =>
        {
            var editor = Editor(); var before = editor.Session.Document;
            Draw(editor, (100, 100), (7000, 100), (7000, 7000));
            Assert.True(editor.FinishEditablePath(), editor.StatusDetail);
            Assert.True(editor.PathCalculationBusy);
            Assert.NotEmpty(editor.PlacementPreviewObjects); // the quick samples stay on the canvas
            Assert.True(editor.HandleEscape());
            context.Pump(editor.PendingPathCalculation);
            Assert.Same(before, editor.Session.Document);
            Assert.Equal(0, editor.Session.UndoDepth);
            Assert.Empty(editor.EditablePaths);
            Assert.False(editor.PathCalculationBusy);
        });

    [Fact]
    public void A_map_changed_during_the_calculation_is_never_overwritten_and_recalculate_applies_once()
        => OnManualContext(context =>
        {
            var editor = Editor();
            Draw(editor, (100, 100), (3000, 100));
            editor.FinishEditablePath(); context.Pump(editor.PendingPathCalculation);
            var path = editor.SelectedEditablePath!; var seed = path.Seed;
            editor.SetEditablePathSeed(seed + 1);
            Assert.True(editor.PathCalculationBusy);
            // Another edit lands before the calculation finishes.
            var layer = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
            var other = new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle, "Чужой", 50, 5000, 40, 40, 0,
                "#FF000000", "#00000000", 0, true, false);
            editor.Session.Execute(new AddObjectCommand(layer.Id, other));
            var edited = editor.Session.Document;
            context.Pump(editor.PendingPathCalculation);
            Assert.Same(edited, editor.Session.Document);
            Assert.True(editor.PathCalculationConflict);
            Assert.Equal(seed, editor.SelectedEditablePath!.Seed);
            Assert.Contains("Пересчитать", editor.StatusDetail, StringComparison.Ordinal);
            var depth = editor.Session.UndoDepth;
            editor.RecalculatePath(); context.Pump(editor.PendingPathCalculation);
            Assert.False(editor.PathCalculationConflict);
            Assert.Equal(seed + 1, editor.SelectedEditablePath!.Seed);
            Assert.Equal(depth + 1, editor.Session.UndoDepth);
            Assert.Same(other, DocumentEditor.FindObject(editor.Session.Document, other.Id));
            editor.Undo(); Assert.Same(edited, editor.Session.Document);
        });

    [Fact]
    public void Applied_change_is_one_undo_and_detached_objects_stay_as_they_are()
        => OnManualContext(context =>
        {
            var editor = Editor();
            Draw(editor, (100, 100), (2000, 100), (2000, 2000));
            editor.FinishEditablePath(); context.Pump(editor.PendingPathCalculation);
            Assert.Equal(1, editor.Session.UndoDepth);
            var member = editor.SelectedEditablePath!.Members[1];
            editor.Session.SelectedObjectId = member.ObjectId; editor.DetachSelectedPathObject();
            var detached = DocumentEditor.FindObject(editor.Session.Document, member.ObjectId)!;
            var before = editor.Session.Document; var depth = editor.Session.UndoDepth;
            editor.SetEditablePathSpacing(editor.SelectedEditablePath!.Spacing / 2);
            editor.SetEditablePathSeed(4242); // the second change builds on the first one still being calculated
            context.Pump(editor.PendingPathCalculation);
            Assert.Equal(depth + 1, editor.Session.UndoDepth);
            Assert.Equal(4242, editor.SelectedEditablePath!.Seed);
            Assert.Same(detached, DocumentEditor.FindObject(editor.Session.Document, member.ObjectId));
            Assert.DoesNotContain(editor.SelectedEditablePath.Members, m => m.ObjectId == member.ObjectId);
            editor.Undo(); Assert.Same(before, editor.Session.Document);
        });

    private static void Draw(EditorViewModel editor, params (double X, double Y)[] points)
    {
        editor.StartEditablePath();
        foreach (var (x, y) in points) Assert.True(editor.AddEditablePathNode(x, y), editor.StatusDetail);
    }

    private static void OnManualContext(Action<ManualContext> body)
    {
        var previous = SynchronizationContext.Current; var context = new ManualContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try { body(context); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class ManualContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _queue = [];
        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public void Pump(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!task.IsCompleted)
            {
                if (_queue.TryTake(out var item, 20)) item.Item1(item.Item2);
                Assert.True(DateTime.UtcNow < deadline, "The path calculation did not finish.");
            }
            task.GetAwaiter().GetResult();
        }
    }

    private EditorViewModel Editor()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "own.png");
        using (var surface = SKSurface.Create(new SKImageInfo(32, 32))!)
        {
            surface.Canvas.Clear(SKColors.ForestGreen);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        var catalog = new AssetCatalog(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        _catalogs.Add(catalog);
        catalog.ImportSet([path], "Дерево", new(), DateTimeOffset.UnixEpoch);
        var document = new MapDocument(DocumentId.New(), "Paths", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(8192, 8192, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), assetCatalog: catalog);
        editor.SelectAssetSet(catalog.Sets[0].Id);
        editor.PlacementAutoSelect = PlacementAutoSelectPolicy.Off;
        return editor;
    }

    public void Dispose()
    {
        foreach (var catalog in _catalogs) catalog.FlushMetadataAsync().GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
