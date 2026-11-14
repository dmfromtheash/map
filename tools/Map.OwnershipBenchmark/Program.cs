using System.Collections.Immutable;
using System.Diagnostics;
using Map.Core.Documents;
using Map.Core.Editing;

// Headless, no assets are opened. Optional arguments: owner count, members per owner, samples.
var ownerCount = args.Length > 0 ? int.Parse(args[0]) : 1000;
var memberCount = args.Length > 1 ? int.Parse(args[1]) : 1000;
var samples = args.Length > 2 ? int.Parse(args[2]) : 11;
foreach (var paths in new[] { false, true }) Run(paths);

void Run(bool paths)
{
    var sha = new string('a', 64);
    var items = new List<SceneObject>(ownerCount * memberCount);
    var areas = new List<PlacementArea>(); var rows = new List<PlacementPath>();
    var setup = Stopwatch.StartNew();
    for (var i = 0; i < ownerCount; i++)
    {
        var x = (i % 32) * 2000d; var y = (i / 32) * 2000d;
        if (paths)
        {
            var row = new PlacementPath { Nodes = [new(Guid.NewGuid(), x, y), new(Guid.NewGuid(), x + memberCount, y)],
                Spacing = 1, Variants = [new(sha, 1, 1)], Request = new() { BaseWidth = 1 } };
            var members = ImmutableArray.CreateBuilder<PlacementPathMember>();
            for (var k = 0; k < memberCount; k++)
            {
                var item = PlacementPathPlanner.Item(row, 0, k); items.Add(item);
                members.Add(new(new(row.Nodes[0].Id, k), item.Id));
            }
            rows.Add(row with { Members = members.ToImmutable() });
        }
        else
        {
            // Legacy layout 0 has exactly Density candidates in a complete 100-unit cell.
            var area = new PlacementArea { Density = memberCount, Variants = [new(sha, 1, 1)],
                Request = new() { BaseWidth = 1 }, Nodes = [new(Guid.NewGuid(), x, y), new(Guid.NewGuid(), x + 99.5, y),
                    new(Guid.NewGuid(), x + 99.5, y + 99.5), new(Guid.NewGuid(), x, y + 99.5)] };
            var members = ImmutableArray.CreateBuilder<PlacementAreaMember>();
            for (var k = 0; k < memberCount; k++)
            {
                var slot = new PlacementAreaSlot((int)x / 100, (int)y / 100, k);
                var item = PlacementAreaPlanner.Item(area, slot); items.Add(item); members.Add(new(slot, item.Id));
            }
            areas.Add(area with { Cells = ImmutableDictionary<long, ImmutableArray<PlacementAreaMember>>.Empty
                .Add(members[0].Slot.Cell, members.ToImmutable()), MemberCount = memberCount });
        }
    }
    var layer = new ObjectLayer(LayerId.New(), "Objects", true, false, 1, items)
        { PlacementAreas = [.. areas], PlacementPaths = [.. rows] };
    var document = new MapDocument(DocumentId.New(), "Ownership benchmark", DateTimeOffset.UnixEpoch,
        new(131072, 131072, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFFFFFFF"), layer],
        [new MapAssetReference(sha, "tree", "image/png", "png", ProjectAssetStorage.Linked,
            DateTimeOffset.UnixEpoch, "test", "test", catalogId: sha)]);
    var itemToEdit = items[^1];
    // A generic path edit requires explicit detachment. Area edits detach in the same transaction.
    if (paths) document = new PlacementPathCommand(layer.Id, rows[^1].Id, null, "detach", detach: itemToEdit.Id).Apply(document);
    _ = DocumentRevisionIndex.For(document); _ = ObjectSpatialIndex.For(layer);
    Console.WriteLine($"{(paths ? "paths" : "areas")}: owners={ownerCount} members/owner={memberCount} objects={items.Count} setup_ms={setup.Elapsed.TotalMilliseconds:F3}");
    var session = new EditingSession(document);
    var measurements = new Dictionary<string, List<(double Ms, long Bytes)>>();
    for (var iteration = -1; iteration < samples; iteration++)
    {
        var moved = MultiSelectionGeometry.Translate([itemToEdit], 1, 0)[0];
        Measure("move", () =>
        {
            var edit = new ReplaceObjectsCommand([itemToEdit], [moved], "move");
            session.Execute(paths ? edit : new ManualAreaSelectionCommand(edit, [itemToEdit.Id]));
            if (session.LastResourceError is { } error) throw new InvalidOperationException(error);
        });
        if (DocumentEditor.FindObject(session.Document, itemToEdit.Id) != moved) throw new Exception("Move failed");
        Measure("delete", () => session.Execute(new DeleteObjectsCommand(session.Document, [itemToEdit.Id])));
        if (DocumentEditor.FindObject(session.Document, itemToEdit.Id) is not null) throw new Exception("Delete failed");
        Measure("undo_delete", () => { if (!session.Undo()) throw new Exception("Undo failed"); });
        if (DocumentEditor.FindObject(session.Document, itemToEdit.Id) != moved) throw new Exception("Undo changed object");
        Measure("undo_move", () => { if (!session.Undo()) throw new Exception("Undo failed"); });

        void Measure(string name, Action action)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
            action(); clock.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (iteration < 0) return;
            if (!measurements.TryGetValue(name, out var values)) measurements[name] = values = [];
            values.Add((clock.Elapsed.TotalMilliseconds, allocated));
        }
    }
    foreach (var (name, values) in measurements)
    {
        var times = values.Select(v => v.Ms).Order().ToArray();
        Console.WriteLine($"{name}: n={samples} median_ms={times[times.Length / 2]:F3} max_ms={times[^1]:F3} median_bytes={values.Select(v => v.Bytes).Order().ElementAt(values.Count / 2)}");
    }
}
