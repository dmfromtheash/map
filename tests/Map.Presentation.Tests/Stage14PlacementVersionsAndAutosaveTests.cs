using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class Stage14PlacementVersionsAndAutosaveTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "map-stage14-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Stage14_arm_preview_leave_and_cancel_are_transient_then_commit_matches_snapped_preview()
    {
        var document = Document(withGrid: true);
        var editor = Editor(document, out _, out _, startDirty: false);
        editor.Session.Viewport.Resize(1000, 700);
        var beforeDocument = editor.Session.Document;
        var beforeRevision = editor.Session.Revision;
        var beforeUndo = editor.Session.UndoDepth;
        var beforeSelection = editor.Session.SelectedObjectIds.ToArray();

        editor.ArmPrimitivePlacement(SceneObjectKind.Ellipse);
        AssertTransient();
        editor.UpdatePlacementPreview(147, 164);
        var pending = Assert.IsType<PlacementSession>(editor.PendingPlacement);
        Assert.Equal(100, pending.WorldX);
        Assert.Equal(200, pending.WorldY);
        AssertTransient();
        editor.SetPlacementPointerVisible(false);
        Assert.Empty(editor.PlacementPreviewObjects);
        editor.SetPlacementPointerVisible(true);
        var preview = Assert.Single(editor.PlacementPreviewObjects);

        Assert.True(editor.CommitPendingPlacement());
        var committed = editor.Session.SelectedObject!;
        Assert.Equal(preview.X, committed.X);
        Assert.Equal(preview.Y, committed.Y);
        Assert.Equal(preview.Width, committed.Width);
        Assert.Equal(preview.Height, committed.Height);
        Assert.Equal("Эллипс 1", committed.Name);
        Assert.Equal(beforeRevision + 1, editor.Session.Revision);
        Assert.Equal(beforeUndo + 1, editor.Session.UndoDepth);
        Assert.Null(editor.PendingPlacement);

        void AssertTransient()
        {
            Assert.Same(beforeDocument, editor.Session.Document);
            Assert.Equal(beforeRevision, editor.Session.Revision);
            Assert.Equal(beforeUndo, editor.Session.UndoDepth);
            Assert.Equal(beforeSelection, editor.Session.SelectedObjectIds);
            Assert.False(editor.Session.IsDirty);
        }
    }

    [Theory]
    [InlineData(PlacementAutoSelectPolicy.Auto, StampPlacementMode.One, true)]
    [InlineData(PlacementAutoSelectPolicy.Auto, StampPlacementMode.Series, false)]
    [InlineData(PlacementAutoSelectPolicy.Always, StampPlacementMode.Series, true)]
    [InlineData(PlacementAutoSelectPolicy.Off, StampPlacementMode.One, false)]
    public void Stage14_one_series_and_all_auto_select_policies_are_explicit(
        PlacementAutoSelectPolicy policy, StampPlacementMode mode, bool selected)
    {
        var editor = Editor(Document(), out _, out _, startDirty: false);
        editor.Session.Viewport.Resize(800, 600);
        editor.PlacementAutoSelect = policy;
        editor.StampPlacementMode = mode;
        editor.ArmPrimitivePlacement(SceneObjectKind.Rectangle);
        editor.UpdatePlacementPreview(300, 250);

        Assert.True(editor.CommitPendingPlacement());

        Assert.Equal(selected, editor.Session.SelectedObjectId is not null);
        Assert.Equal(mode == StampPlacementMode.Series, editor.PendingPlacement is not null);
    }

    [Fact]
    public void Stage14_series_and_scatter_keep_one_command_per_gesture_and_never_reroll_preview()
    {
        var editor = AssetEditor(out var catalog);
        var set = catalog.FindSetForContent(catalog.Records[0].Sha256)!;
        editor.SelectAssetSet(set.Id);
        editor.StampPlacementMode = StampPlacementMode.Series;
        editor.UpdatePlacementPreview(240, 220);
        Assert.True(editor.CommitPendingPlacement());
        editor.UpdatePlacementPreview(360, 260);
        Assert.True(editor.CommitPendingPlacement());
        Assert.Equal(2, editor.Session.UndoDepth);
        Assert.Equal(2, Objects(editor).Count);
        Assert.NotNull(editor.PendingPlacement);
        Assert.True(editor.HandleEscape());
        Assert.Null(editor.PendingPlacement);
        Assert.Equal(2, Objects(editor).Count);

        editor.StampPlacementMode = StampPlacementMode.Area;
        editor.StampAreaRadius = 120;
        editor.StampDensity = 60;
        editor.RandomVariantAfterPlacement = true;
        editor.StampRandomAngle = true;
        editor.StampRandomFlip = true;
        editor.ArmStampPlacement();
        editor.UpdatePlacementPreview(500, 400);
        var pending = editor.PendingPlacement!;
        var preview = pending.PreviewObjects.ToArray();
        var beforeCount = Objects(editor).Count;
        var beforeUndo = editor.Session.UndoDepth;

        Assert.True(editor.CommitPendingPlacement());
        var added = Objects(editor).Skip(beforeCount).ToArray();
        Assert.Equal(pending.PlannedCount, added.Length);
        Assert.Equal(beforeUndo + 1, editor.Session.UndoDepth);
        Assert.All(preview.Zip(added), pair =>
        {
            Assert.Equal(pair.First.X, pair.Second.X);
            Assert.Equal(pair.First.Y, pair.Second.Y);
            Assert.Equal(pair.First.RotationDegrees, pair.Second.RotationDegrees);
            Assert.Equal(pair.First.Stamp!.AssetSha256, pair.Second.Stamp!.AssetSha256);
        });
    }

    [Fact]
    public void Stage14_cycling_variant_rebuilds_armed_ghost_and_commit_from_the_same_variant()
    {
        var editor = AssetEditor(out var catalog);
        var set = catalog.FindSetForContent(catalog.Records[0].Sha256)!;
        editor.SelectAssetSet(set.Id);
        editor.UpdatePlacementPreview(240, 180);
        var initial = Assert.Single(editor.PlacementPreviewObjects);

        editor.NextVariant();
        editor.UpdatePlacementPreview(320, 240);
        var cycled = Assert.Single(editor.PlacementPreviewObjects);

        Assert.NotEqual(initial.Stamp!.AssetSha256, cycled.Stamp!.AssetSha256);
        Assert.True(editor.CommitPendingPlacement());
        Assert.Equal(cycled.Stamp.AssetSha256, editor.Session.SelectedObject!.Stamp!.AssetSha256);
        Assert.Equal(cycled.X, editor.Session.SelectedObject.X);
        Assert.Equal(cycled.Y, editor.Session.SelectedObject.Y);
    }

    [Fact]
    public void Stage14_tool_switch_and_explicit_cancel_clear_placement_without_mutation()
    {
        var editor = Editor(Document(), out _, out _, startDirty: false);
        var source = editor.Session.Document;
        editor.ArmPrimitivePlacement(SceneObjectKind.Ellipse);
        editor.UpdatePlacementPreview(200, 180);

        editor.ActiveTool = EditorTool.Brush;
        Assert.Null(editor.PendingPlacement);
        Assert.Same(source, editor.Session.Document);
        Assert.False(editor.Session.IsDirty);

        editor.ArmPrimitivePlacement(SceneObjectKind.Rectangle);
        editor.UpdatePlacementPreview(300, 260);
        Assert.True(editor.CancelPlacement());
        Assert.Null(editor.PendingPlacement);
        Assert.Same(source, editor.Session.Document);
        Assert.Empty(editor.Session.SelectedObjectIds);
    }

    [Fact]
    public void Stage14_invalid_layer_refuses_commit_and_explicit_layer_command_continues()
    {
        var source = Document();
        var locked = source.Layers.OfType<ObjectLayer>().Single() with { Locked = true };
        var document = source.WithLayers([source.BaseColor, locked]);
        var editor = Editor(document, out _, out _, startDirty: false);
        editor.ArmPrimitivePlacement(SceneObjectKind.Rectangle);
        editor.UpdatePlacementPreview(200, 200);
        var before = editor.Session.Document;

        Assert.False(editor.CommitPendingPlacement());
        Assert.Same(before, editor.Session.Document);
        Assert.Contains("Создайте слой объектов", editor.PlacementRefusal);
        Assert.True(editor.CreateObjectLayerAndContinuePlacement());
        editor.UpdatePlacementPreview(200, 200);
        Assert.True(editor.CommitPendingPlacement());
        Assert.Equal(2, editor.Session.UndoDepth); // explicit layer + object
    }

    [Fact]
    public void Stage14_pointer_preview_on_50k_scene_raises_only_canvas_invalidation()
    {
        var objects = Enumerable.Range(0, 50_000).Select(index => new SceneObject(
            new ObjectId(new Guid(index + 1, 0, 0, new byte[8])),
            SceneObjectKind.Rectangle, $"Прямоугольник {index + 1}",
            index % 1000, index / 1000, 8, 8, 0,
            "#FFFFFFFF", "#FF000000", 0, true, false)).ToArray();
        var source = Document();
        var layer = source.Layers.OfType<ObjectLayer>().Single() with { Objects = objects };
        var largeDocument = source.WithLayers([source.BaseColor, layer]);
        var editor = Editor(largeDocument, out _, out _, startDirty: false);
        var sceneChanges = 0;
        var invalidations = 0;
        editor.SceneChanged += (_, _) => sceneChanges++;
        editor.SceneInvalidated += (_, e) =>
        {
            if (e.Has(EditorChangeReason.CanvasInvalidation)) invalidations++;
        };
        editor.ArmPrimitivePlacement(SceneObjectKind.Ellipse);
        sceneChanges = 0;
        invalidations = 0;

        for (var index = 0; index < 20; index++)
        {
            editor.UpdatePlacementPreview(100 + index, 200 + index);
        }

        Assert.Equal(0, sceneChanges);
        Assert.Equal(20, invalidations);
        Assert.Same(largeDocument, editor.Session.Document);
        Assert.Equal(50_000, Objects(editor).Count);
    }

    [Fact]
    public void Stage14_one_click_version_auto_names_and_optional_rename_are_undoable()
    {
        var now = new DateTimeOffset(2026, 9, 1, 14, 5, 0, TimeSpan.Zero);
        var editor = Editor(Document(), out _, out _, startDirty: false, clock: () => now);

        Assert.True(editor.CreateVersion());
        var version = Assert.Single(editor.Checkpoints);
        Assert.Equal($"Версия 01 · {now.ToLocalTime():dd.MM.yyyy HH:mm}", version.Name);
        Assert.True(version.PreviewPng.Length > 100);
        Assert.True(editor.RenameVersion(version.Id, "До эксперимента"));
        Assert.Equal("До эксперимента", editor.Checkpoints.Single().Name);
        editor.Undo();
        Assert.StartsWith("Версия 01", editor.Checkpoints.Single().Name, StringComparison.Ordinal);
        editor.Redo();
        Assert.Equal("До эксперимента", editor.Checkpoints.Single().Name);
    }

    [Fact]
    public async Task Stage14_untitled_uses_recovery_then_first_save_arms_debounced_primary_autosave()
    {
        var now = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var editor = Editor(Document(), out var store, out var picker, startDirty: true, clock: () => now);

        Assert.True(await editor.TryAutosaveAsync());
        Assert.Empty(store.Saved);
        Assert.Single(store.Autosaves);
        Assert.Null(editor.DocumentPath);

        picker.NextSavePath = @"C:\stage14\map.mapproj";
        Assert.True(await editor.SaveAsync());
        editor.AddObject(SceneObjectKind.Rectangle);
        now = now.AddSeconds(3);
        Assert.False(await editor.TryPrimaryAutosaveAsync());
        now = now.AddSeconds(2);
        Assert.True(await editor.TryPrimaryAutosaveAsync());
        Assert.False(editor.Session.IsDirty);
        Assert.Single(store.Saved[@"C:\stage14\map.mapproj"].Document
            .Layers.OfType<ObjectLayer>().Single().Objects);
    }

    [Fact]
    public async Task Stage14_edit_during_primary_save_stays_dirty_and_next_debounce_catches_up()
    {
        var now = DateTimeOffset.UnixEpoch;
        var editor = Editor(Document(), out var store, out var picker, startDirty: true, clock: () => now);
        picker.NextSavePath = @"C:\stage14\race.mapproj";
        Assert.True(await editor.SaveAsync());
        editor.AddObject(SceneObjectKind.Rectangle);
        now = now.AddSeconds(5);
        store.ManualSaveEntered = new ManualResetEventSlim(false);
        store.ManualSaveRelease = new ManualResetEventSlim(false);

        var saving = editor.TryPrimaryAutosaveAsync();
        Assert.True(store.ManualSaveEntered.Wait(TimeSpan.FromSeconds(5)));
        editor.AddObject(SceneObjectKind.Ellipse);
        store.ManualSaveRelease.Set();
        Assert.True(await saving);
        Assert.True(editor.Session.IsDirty);
        Assert.Single(store.Saved[@"C:\stage14\race.mapproj"].Document
            .Layers.OfType<ObjectLayer>().Single().Objects);

        store.ManualSaveEntered = null;
        store.ManualSaveRelease = null;
        now = now.AddSeconds(5);
        Assert.True(await editor.TryPrimaryAutosaveAsync());
        Assert.False(editor.Session.IsDirty);
        Assert.Equal(2, store.Saved[@"C:\stage14\race.mapproj"].Document
            .Layers.OfType<ObjectLayer>().Single().Objects.Count);
    }

    [Fact]
    public async Task Stage14_primary_error_keeps_previous_destination_and_recovery_remains_separate()
    {
        var now = DateTimeOffset.UnixEpoch;
        var editor = Editor(Document(), out var store, out var picker, startDirty: true, clock: () => now);
        picker.NextSavePath = @"C:\stage14\safe.mapproj";
        Assert.True(await editor.SaveAsync());
        var previous = store.Saved[@"C:\stage14\safe.mapproj"].Document;
        editor.AddObject(SceneObjectKind.Rectangle);
        now = now.AddSeconds(5);
        store.NextSaveFailures.Enqueue(new IOException("disk full"));

        Assert.False(await editor.TryPrimaryAutosaveAsync());
        Assert.Same(previous, store.Saved[@"C:\stage14\safe.mapproj"].Document);
        Assert.True(editor.Session.IsDirty);
        Assert.Equal(SaveState.SaveFailed, editor.SaveState);

        editor.PrimaryAutosaveEnabled = false;
        Assert.True(await editor.TryAutosaveAsync());
        Assert.True(editor.RecoveryAvailable);
        Assert.Single(store.Autosaves);
    }

    private EditorViewModel AssetEditor(out AssetCatalog catalog)
    {
        catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var first = Image("tree-a", SKColors.ForestGreen);
        var second = Image("tree-b", SKColors.DarkOliveGreen);
        var set = catalog.ImportSet(
            [first, second], "Дерево", new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        return new EditorViewModel(
            Document(), null, false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => DateTimeOffset.UnixEpoch,
            assetCatalog: catalog);
    }

    private EditorViewModel Editor(
        MapDocument document,
        out FakeProjectStore store,
        out FakeFilePicker picker,
        bool startDirty,
        Func<DateTimeOffset>? clock = null)
    {
        store = new FakeProjectStore();
        picker = new FakeFilePicker();
        return new EditorViewModel(
            document, null, startDirty, store, new FakeRecentMaps(), picker,
            new FakePrompts(), new FakeLog(), clock: clock);
    }

    private string Image(string name, SKColor color)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name + ".png");
        using var surface = SKSurface.Create(new SKImageInfo(32, 24))!;
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static List<SceneObject> Objects(EditorViewModel editor) =>
        editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects).ToList();

    private static MapDocument Document(bool withGrid = false)
    {
        var baseLayer = new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF20242B");
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        var layers = new List<MapLayer> { baseLayer, objects };
        if (withGrid)
        {
            layers.Add(new GridLayer(LayerId.New(), "Сетка", true, false, 1,
            [
                new GridDefinition(GridId.New(), GridGeometry.Square, 100)
                {
                    SnapEnabled = true,
                },
            ]));
        }
        return new MapDocument(
            DocumentId.New(), "Stage 14", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FF20242B"), layers);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
