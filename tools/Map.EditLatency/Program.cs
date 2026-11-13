using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

// No UI, raster decode or renderer runs in the timed actions. Same harness for both revisions.
var root = Path.GetFullPath(args[0]);
var count = int.Parse(args[1]);
var suite = args.Length > 2 ? args[2] : "actions";
Directory.CreateDirectory(Path.Combine(root, "tmp", "catalog-" + count + "-" + suite));
await using var catalog = new AssetCatalog(Path.Combine(root, "tmp", "catalog-" + count + "-" + suite));
var sourceDirectory = new DirectoryInfo(AppContext.BaseDirectory);
while (sourceDirectory is not null && !File.Exists(Path.Combine(sourceDirectory.FullName, "Map.slnx"))) sourceDirectory = sourceDirectory.Parent;
var source = args.Length > 3 ? Path.GetFullPath(args[3]) : sourceDirectory?.FullName ?? Path.Combine(root, "repo");
catalog.ImportSet([Path.Combine(source, "tests", "fixtures", "assets", "owner-mountain-01.png")],
    "Гора", new(), DateTimeOffset.UnixEpoch);
await catalog.FlushMetadataAsync();
var record = catalog.Records[0];
var refs = Enumerable.Range(0, 100).Select(i => new MapAssetReference(
    i == 0 ? record.Sha256 : i.ToString("x64"), "Ассет " + i, "image/png", ".png",
    ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "cpu-probe", "user-owned",
    catalogId: i == 0 ? record.Sha256 : i.ToString("x64"))).ToArray();
Console.WriteLine(JsonSerializer.Serialize(new { type = "environment", count, suite,
    framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
    arch = RuntimeInformation.ProcessArchitecture.ToString(), serverGC = GCSettings.IsServerGC,
    latencyMode = GCSettings.LatencyMode.ToString(), cpuCount = Environment.ProcessorCount }));
var measured = count != 16; // 16 is an untabulated JIT exercise in its own process.

MapDocument Build()
{
    // Same constructors, seed, distribution and last-object anchor as NativeMillionAssetsSmoke.cs:27-58.
    var random = new Random(12345);
    const int size = 131072;
    var objects = new SceneObject[count];
    for (var i = 0; i < count; i++)
    {
        var width = 64 * (.5 + random.NextDouble() * 1.5);
        objects[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Ассет " + i,
            random.NextDouble() * (size - width), random.NextDouble() * (size - width),
            width, width * record.Height / record.Width, random.Next(-180, 181),
            "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(refs[i % 100].Sha256) };
    }
    var last = objects[^1];
    objects[^1] = new SceneObject(last.Id, last.Kind, last.Name, size / 2d - last.Width / 2,
        size / 2d - last.Height / 2, last.Width, last.Height, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        { Stamp = last.Stamp };
    return new MapDocument(DocumentId.New(), "CPU edit probe", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(size, size, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
         new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects)]).WithAssets(refs);
}

void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
ObjectLayer Layer(MapDocument d) => d.Layers.OfType<ObjectLayer>().Single();
EditorViewModel Editor(MapDocument d) => new(d, null, false,
    new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog(),
    assetCatalog: catalog, availableFontFamilies: ["Arial"]);

void Measure(string name, int iteration, Action action, EditorViewModel? editor = null)
{
    var details = editor?.LastEditTimings;
    details?.Clear();
    var allocations = GC.GetAllocatedBytesForCurrentThread();
    var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
    var start = Stopwatch.GetTimestamp();
    action();
    var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    var bytes = GC.GetAllocatedBytesForCurrentThread() - allocations;
    if (measured) Console.WriteLine(JsonSerializer.Serialize(new { type = "sample", count, suite, name, iteration, ms, bytes,
        gc = new[] { GC.CollectionCount(0) - collections[0], GC.CollectionCount(1) - collections[1], GC.CollectionCount(2) - collections[2] },
        steps = details?.Select(s => new { step = s.Step, ms = s.Ms }).ToArray() }));
}

// Warm JIT using a small *different* document. The measured document keeps cold derived caches.
// Tiered compilation is disabled by run.sh so code does not tier during samples.
var warmObjects = Enumerable.Range(0, 16).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
    "warm " + i, i * 64, 0, 32, 32, 0, "#FFFFFFFF", "#00000000", 0, true, false)
    { Stamp = new StampStyle(record.Sha256) }).ToArray();
var warmDoc = new MapDocument(DocumentId.New(), "warm", DateTimeOffset.UnixEpoch,
    new CanvasDefinition(131072, 131072, "px", "#FFFFFFFF"),
    [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
     new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, warmObjects)]).WithAssets(refs);
var warmEditor = Editor(warmDoc);
EditorViewModel.CollectEditTimings = true;
_ = warmEditor.LastEditTimings;
var warmRouter = new CanvasInputRouter(warmEditor);
warmEditor.Session.Viewport.Set(0, 0, 1);
warmEditor.ActiveTool = EditorTool.Select;
warmRouter.Press(CanvasPointerButton.Left, 16, 16);
warmRouter.Release(16, 16);
_ = ObjectTreeRows.Build(warmDoc, "", ObjectTypeFilter.All, null, []);
_ = ObjectTreeRows.Build(warmDoc, "", ObjectTypeFilter.Stamps, null, []);
warmEditor.SelectAssetSet(catalog.Sets[0].Id);
warmEditor.ArmStampPlacement();
Check(warmRouter.Press(CanvasPointerButton.Left, 2000, 2000).StampPlaced, "warm placement refused");
warmRouter.Release(2000, 2000);
warmEditor.Undo();
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

if (suite == "fixes")
{
    var doc = Build();
    var layer = Layer(doc);
    _ = DocumentRevisionIndex.For(doc);
    var selected = new[] { layer.Objects[^1].Id };
    for (var i = 0; i <= 5; i++)
    {
        Measure("closed-selected", i, () => {
            var rows = ObjectTreeRows.Build(doc, "", ObjectTypeFilter.Stamps, null, selected);
            Check(rows.Count == Math.Min(count, 100) && rows.Count(r => r.Selected) == 1, "closed selection/count");
        });
        var addition = warmObjects[0] with { Id = ObjectId.New() };
        var changed = new PlaceAssetObjectsCommand(layer.Id, refs[0], [addition]).Apply(doc);
        Measure("follow-new-revision", i, () => {
            Check(ReferenceEquals(changed, TextOnPath.Follow(doc, changed)), "path changed");
            Check(ReferenceEquals(changed, RegionLabels.Follow(doc, changed)), "region changed");
            Check(ReferenceEquals(changed, MapDecorations.Refresh(changed)), "decoration changed");
        });
    }
    var single = layer with { Objects = layer.Objects.Select(o => o with { Stamp = new StampStyle(refs[0].Sha256) }).ToArray() };
    var singleDoc = doc.WithLayer(layer.Id, _ => single);
    _ = DocumentRevisionIndex.For(singleDoc);
    for (var i = 0; i <= 5; i++)
    {
        Measure("single-kind-closed", i, () => {
            var row = ObjectTreeRows.Build(singleDoc, "", ObjectTypeFilter.Stamps, null, selected).Single();
            Check(row.ChildCount == count && row.Selected, "single kind");
        });
        var changed = new AddObjectCommand(layer.Id, warmObjects[0] with { Id = ObjectId.New() }).Apply(singleDoc);
        Measure("single-kind-append-row", i, () => {
            var row = ObjectTreeRows.Build(changed, "", ObjectTypeFilter.Stamps, null, []).Single();
            Check(row.ChildCount == count + 1, "single kind delta");
        });
    }
}
else if (suite == "indexes")
{
    MapDocument doc = null!;
    Measure("build-document", 0, () => doc = Build());
    var layer = Layer(doc);
    Measure("identity-slots", 0, () => Check(((SceneObjectList)layer.Objects).IndexOfId(layer.Objects[^1].Id) == count - 1, "slot"));
    Measure("revision-after-identity", 0, () => Check(DocumentRevisionIndex.For(doc).ObjectCount == count, "revision"));
    Measure("spatial-root", 0, () => { _ = ObjectSpatialIndex.For(layer); });
    Measure("names-root", 0, () => { _ = EntityNameGenerator.Objects(doc, SceneObjectKind.Stamp, 1, "Гора"); });
    Measure("names-occupied-suffix", 0, () => { _ = EntityNameGenerator.Objects(doc, SceneObjectKind.Stamp, 2, "Ассет"); });
    Measure("names-occupied-suffix", 1, () => { _ = EntityNameGenerator.Objects(doc, SceneObjectKind.Stamp, 2, "Ассет"); });
    for (var i = 0; i <= 5; i++)
    {
        Measure("tree-all", i, () => { _ = ObjectTreeRows.Build(doc, "", ObjectTypeFilter.All, null, []); });
        Measure("tree-assets", i, () => { _ = ObjectTreeRows.Build(doc, "", ObjectTypeFilter.Stamps, null, []); });
        Measure("tree-open-kind", i, () => { _ = ObjectTreeRows.Build(doc, "", ObjectTypeFilter.Stamps, null, [], openAssetKey: refs[0].Sha256); });
    }
    for (var i = 0; i <= 5; i++)
    {
        var added = warmObjects[0] with { Id = ObjectId.New(), Name = "component probe" };
        var changed = new PlaceAssetObjectsCommand(layer.Id, refs[0], [added]).Apply(doc);
        Measure("text-path-follow", i, () => { Check(ReferenceEquals(changed, TextOnPath.Follow(doc, changed)), "unexpected text change"); });
        Measure("region-follow", i, () => { Check(ReferenceEquals(changed, RegionLabels.Follow(doc, changed)), "unexpected region change"); });
        Measure("decoration-refresh-new-list", i, () => { Check(ReferenceEquals(changed, MapDecorations.Refresh(changed)), "unexpected decoration change"); });
    }
}
else if (suite == "edges")
{
    var doc = Build();
    _ = DocumentRevisionIndex.For(doc);
    var layerId = Layer(doc).Id;
    var session = new EditingSession(doc);
    var novel = new MapAssetReference(new string('f', 64), "Новый ассет", "image/png", ".png",
        ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "cpu-probe", "user-owned", catalogId: new string('f', 64));
    var stamp = warmObjects[0] with { Id = ObjectId.New(), Stamp = new StampStyle(novel.Sha256) };
    Measure("execute-new-asset", 0, () => session.Execute(new PlaceAssetObjectsCommand(layerId, novel, [stamp])));
    Measure("undo-new-asset", 0, () => Check(session.Undo(), "new asset undo"));
    Check(Layer(session.Document).Objects.Count == count && session.Document.Assets.Count == 100, "new asset rollback");
    var revision = doc;
    for (var i = 1; i <= 258; i++)
    {
        var item = warmObjects[0] with { Id = ObjectId.New(), Name = "rollover " + i };
        var command = new PlaceAssetObjectsCommand(layerId, refs[0], [item]);
        if (i is 1 or 256 or 257 or 258)
        {
            Measure("rollover-append", i, () => revision = command.Apply(revision));
            Measure("rollover-lookup", i, () => Check(DocumentRevisionIndex.For(revision).FindObject(item.Id) is not null, "rollover lookup"));
        }
        else { revision = command.Apply(revision); _ = DocumentRevisionIndex.For(revision); }
    }
    Check(Layer(revision).Objects.Count == count + 258, "rollover count");
}
else
{
    MapDocument doc = null!;
    Measure("build-document", 0, () => doc = Build());
    EditorViewModel editor = null!;
    Measure("construct-editor", 0, () => editor = Editor(doc));
    var router = new CanvasInputRouter(editor);
    editor.Session.Viewport.Set(0, 0, 1);
    if (suite == "opened")
    {
        Measure("open-spatial", 0, () => { _ = ObjectSpatialIndex.For(Layer(doc)); });
        Measure("open-assets-panel", 0, () => { _ = ObjectTreeRows.Build(doc, "", ObjectTypeFilter.Stamps, null, []); });
    }
    if (suite != "place-first")
    {
        editor.ActiveTool = EditorTool.Select;
        var expected = Layer(doc).Objects[^1].Id;
        for (var i = 0; i <= 5; i++)
        {
            editor.DeselectObject();
            Measure("select-click", i, () => {
                router.Press(CanvasPointerButton.Left, 65536, 65536);
                router.Release(65536, 65536);
            }, editor);
            Check(editor.Session.SelectedObjectId == expected, "selection target mismatch");
            Check(editor.Session.UndoDepth == 0, "selection created undo");
        }
    }
    editor.SelectAssetSet(catalog.Sets[0].Id);
    editor.StampPlacementMode = StampPlacementMode.One;
    editor.PlacementAutoSelect = PlacementAutoSelectPolicy.Off;
    Measure("arm-stamp", 0, () => Check(editor.ArmStampPlacement(), "arm refused"));
    for (var i = 0; i <= 5; i++)
    {
        Measure("place-click", i, () => {
            var result = router.Press(CanvasPointerButton.Left, 70000, 70000);
            router.Release(70000, 70000);
            Check(result.StampPlaced, "placement refused: " + editor.StatusDetail);
        }, editor);
        Check(Layer(editor.Session.Document).Objects.Count == count + 1 && editor.Session.UndoDepth == 1, "place count/history");
        Measure("spatial-after-place", i, () => { _ = ObjectSpatialIndex.For(Layer(editor.Session.Document)).QueryPoint(70000, 70000); });
        Measure("tree-assets-after-place", i, () => { _ = ObjectTreeRows.Build(editor.Session.Document, "", ObjectTypeFilter.Stamps, null, editor.Session.SelectedObjectIds); });
        Measure("undo", i, () => editor.Undo(), editor);
        Check(Layer(editor.Session.Document).Objects.Count == count && editor.Session.UndoDepth == 0 && editor.Session.RedoDepth == 1, "undo count/history");
        Measure("spatial-after-undo", i, () => { _ = ObjectSpatialIndex.For(Layer(editor.Session.Document)).QueryPoint(70000, 70000); });
        Measure("tree-assets-after-undo", i, () => { _ = ObjectTreeRows.Build(editor.Session.Document, "", ObjectTypeFilter.Stamps, null, editor.Session.SelectedObjectIds); });
    }
    Check(Layer(doc).Objects.Count == count, "source snapshot mutated");
}
await catalog.FlushMetadataAsync();
Console.WriteLine(JsonSerializer.Serialize(new { type = "checks", count, suite, passed = true,
    workingSetBytes = Environment.WorkingSet, heapBytes = GC.GetTotalMemory(false) }));
