using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>R42: changes since a version carried to another copy - clean ones apply, clashing ones wait for a choice, one undo.</summary>
public sealed class ChangePackageTests
{
    [Fact]
    public void Clean_changes_apply_and_a_clash_keeps_ours_unless_theirs_is_chosen()
    {
        var keep = Label("Ильград", 100, 100);
        var moved = Label("Брод", 300, 300);
        var gone = Label("Руины", 500, 500);
        var road = RoadEditor.NewRoad([new(0, 10), new(300, 10)], 16, RoadStyle.Country(), "Тракт");
        var version = Doc([keep, moved, gone], [road]);

        // My copy since the version: moved «Брод», removed «Руины», added «Застава», bent the road.
        var added = Label("Застава", 700, 700);
        var bent = road with { Nodes = [road.Nodes[0], road.Nodes[1] with { Y = 120 }] };
        var mine = Doc([keep, Label("Брод", 320, 300, moved.Id), added], [bent]).WithNotes([new MapNote(NoteId.New(), "Засада", "", NoteAnchorKind.World, 50, 50)]);
        var package = ChangePackage.Create(version, mine, "Сессия 3", DateTimeOffset.UnixEpoch);
        Assert.Equal(5, package.Count);
        Assert.DoesNotContain(ChangePackage.Objects(package.After), o => o.Id == keep.Id);

        // The other copy also moved «Брод» its own way: that one clashes, the rest is clean.
        var theirs = Doc([keep, Label("Брод", 280, 300, moved.Id), gone], [road]);
        var plan = ChangePackageMerge.Check(theirs, package);
        Assert.Equal(ChangeOutcome.Conflict, plan.Single(c => c.Id == moved.Id.Value).Outcome);
        Assert.All(plan.Where(c => c.Id != moved.Id.Value), c => Assert.Equal(ChangeOutcome.Clean, c.Outcome));

        var merged = ChangePackageMerge.Apply(theirs, package, new HashSet<Guid>());
        var objects = ChangePackage.Objects(merged).ToList();
        Assert.Equal(280, objects.Single(o => o.Id == moved.Id).X);
        Assert.Contains(objects, o => o.Id == added.Id);
        Assert.DoesNotContain(objects, o => o.Id == gone.Id);
        Assert.Equal(120, ChangePackage.Roads(merged).Single().Nodes[1].Y);
        Assert.Single(merged.Notes);

        var forced = ChangePackageMerge.Apply(theirs, package, new HashSet<Guid> { moved.Id.Value });
        Assert.Equal(320, ChangePackage.Objects(forced).Single(o => o.Id == moved.Id).X);

        // Taken again: everything is already so.
        Assert.All(ChangePackageMerge.Check(merged, package).Where(c => c.Id != moved.Id.Value), c => Assert.Equal(ChangeOutcome.Same, c.Outcome));

        var command = new ApplyChangePackageCommand(theirs, merged);
        Assert.Equal(3, ChangePackage.Objects(command.Revert(command.Apply(theirs))).Count());
    }

    [Fact]
    public void Nothing_changed_makes_an_empty_package()
    {
        var version = Doc([Label("Ильград", 1, 1)], []);
        Assert.Equal(0, ChangePackage.Create(version, version, "v", DateTimeOffset.UnixEpoch).Count);
    }

    private static SceneObject Label(string content, double x, double y, ObjectId? id = null) =>
        new SceneObject(id ?? ObjectId.New(), SceneObjectKind.Text, content, x, y, 140, 40, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = content } };

    private static MapDocument Doc(SceneObject[] objects, Road[] roads) =>
        new(new DocumentId(Guid.Parse("11111111-1111-1111-1111-111111111111")), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(2048, 2048, "px", "#FFFFFFFF"),
            [new BaseColorLayer(new LayerId(Guid.Parse("22222222-2222-2222-2222-222222222222")), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(new LayerId(Guid.Parse("33333333-3333-3333-3333-333333333333")), "Объекты", true, false, 1, objects) { Roads = [.. roads] }]);
}
