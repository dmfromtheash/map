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
    public void A_project_file_is_not_a_package()
    {
        var path = Path.Combine(_root, "map.mapproj");
        MapProjectStore.Save(ProjectFixtures.Document("Мир"), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Throws<MapProjectCorruptedException>(() => ChangePackageStore.Open(path));
    }
}
