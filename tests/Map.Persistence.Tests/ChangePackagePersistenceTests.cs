using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>R42: a .mapchanges file keeps both halves of the package and refuses a file that is not one.</summary>
public sealed class ChangePackagePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-changes-" + Guid.NewGuid().ToString("N"));
    public ChangePackagePersistenceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Package_roundtrip_keeps_before_after_and_header()
    {
        var version = ProjectFixtures.Document("Мир");
        var layer = version.Layers.OfType<ObjectLayer>().First();
        var label = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Застава", 10, 20, 140, 40, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = "Застава" } };
        var current = version.WithLayers([.. version.Layers.Select(l => l.Id == layer.Id ? layer with { Objects = [.. layer.Objects, label] } : l)]);
        var package = ChangePackage.Create(version, current, "Сессия 3", DateTimeOffset.UnixEpoch);
        var path = Path.Combine(_root, "s3" + ChangePackageStore.Extension);

        ChangePackageStore.Save(package, path, "test");
        var opened = ChangePackageStore.Open(path);

        Assert.Equal((package.SourceMap, package.SourceTitle, package.BaseName), (opened.SourceMap, opened.SourceTitle, opened.BaseName));
        Assert.Equal(1, opened.Count);
        Assert.Equal("Застава", ChangePackage.Objects(opened.After).Single().Text!.Content);
        Assert.Empty(ChangePackage.Objects(opened.Before));
        Assert.All(ChangePackageMerge.Check(version, opened), c => Assert.Equal(ChangeOutcome.Clean, c.Outcome));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void Note_card_and_river_node_fields_survive_the_package_file()
    {
        // R42b: what R30b compares also travels through the .mapchanges file.
        var note = new MapNote(NoteId.New(), "Засада", "", NoteAnchorKind.World, 50, 50);
        var card = new MapEntity { Name = "Ильград", Kind = EntityKind.Place };
        var river = new River { Name = "Сельва", Channels = [RiverEditor.NewChannel([new(10, 10), new(100, 50), new(200, 20)], 8)] };
        var version = ProjectFixtures.Document("Мир");
        var layer = version.Layers.OfType<ObjectLayer>().First();
        version = version.WithLayers([.. version.Layers.Select(l => l.Id == layer.Id ? layer with { Rivers = [river] } : l)]).WithNotes([note]) with { Entities = [card] };
        var channel = river.Channels[0];
        var falls = river with { Channels = [channel with { Nodes = [channel.Nodes[0], channel.Nodes[1] with { Feature = RiverNodeFeature.Rapids }, channel.Nodes[2]] }] };
        var mapId = Guid.NewGuid();
        var current = version.WithLayers([.. version.Layers.Select(l => l.Id == layer.Id ? layer with { Rivers = [falls] } : l)])
            .WithNotes([note with { PlayerVisible = true, LinkedMap = mapId, Category = NoteCategory.Secret }]) with { Entities = [card with { PlayerVisible = true }] };
        var path = Path.Combine(_root, "fields" + ChangePackageStore.Extension);

        ChangePackageStore.Save(ChangePackage.Create(version, current, "v", DateTimeOffset.UnixEpoch), path, "test");
        var opened = ChangePackageStore.Open(path);
        Assert.Equal(3, opened.Count);
        var merged = ChangePackageMerge.Apply(version, opened, new HashSet<Guid>());
        var taken = merged.Notes.Single();
        Assert.Equal((true, mapId, NoteCategory.Secret), (taken.PlayerVisible, taken.LinkedMap, taken.Category));
        Assert.True(merged.Entities.Single().PlayerVisible);
        Assert.Equal(RiverNodeFeature.Rapids, ChangePackage.Rivers(merged).Single().Channels[0].Nodes[1].Feature);
    }

    [Fact]
    public void A_project_file_is_not_a_package()
    {
        var path = Path.Combine(_root, "map.mapproj");
        MapProjectStore.Save(ProjectFixtures.Document("Мир"), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Throws<MapProjectCorruptedException>(() => ChangePackageStore.Open(path));
    }
}
