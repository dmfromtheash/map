using Map.Core.Documents;

namespace Map.Rendering.Tests;

public sealed class WheelImagePlanTests
{
    [Fact]
    public void Cell_slices_request_the_same_images_as_one_full_query_at_edges_and_with_oversized_stamps()
    {
        var objects = new List<SceneObject>();
        foreach (var position in new[] { -2048d, -1, 0, Math.BitDecrement(1024d), 1024, 2048, 4096 })
        foreach (var width in new[] { 0.001, 64d, 2048, 100_000 })
        foreach (var rotation in new[] { 0d, 37 })
            objects.Add(new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора", position, position,
                width, width / 2, rotation, "#FFFFFFFF", "#00000000", 0, true, false)
                { Stamp = new StampStyle(objects.Count.ToString("x64")) });
        objects.Add(new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора", 0, 0, 64, 64,
            0, "#FFFFFFFF", "#00000000", 0, false, false) { Stamp = new StampStyle(new string('f', 64)) });
        objects.Add(objects[1] with { Id = ObjectId.New(), Stamp = null });
        var layer = new ObjectLayer(LayerId.New(), "Горы", true, false, 1, objects.ToArray());
        var index = ObjectSpatialIndex.For(layer);
        Assert.True(index.OversizedCount > 0);
        foreach (var area in new[] { new WorldBounds(-1, -1, 4096, 4096), new WorldBounds(-1024, -1024, 2048, 2048),
            new WorldBounds(-26.9, 996.2, 4122.9, 2074.9) })
        {
            const double scale = .037162722343835;
            var expected = index.Query(area).Where(o => o.Visible && o.Stamp is not null)
                .Select(o => (o.Stamp!.AssetSha256, o.Width * scale, o.Height * scale)).ToHashSet();
            var plan = new Plan();
            SceneRenderer.AddDetailImageRequests(layer, area, scale, plan, boundedQueries: true);
            Assert.NotEmpty(expected);
            Assert.True(expected.SetEquals(plan.Requests), "cell slicing changed the exact image-key set");
        }
    }

    private sealed class Plan : IStampPreparationPlan
    {
        public HashSet<(string, double, double)> Requests { get; } = [];
        public void Add(IEnumerable<(string Sha256, double Width, double Height)> stamps) => Requests.UnionWith(stamps);
        public bool Prepare(Action ready) => false;
        public long Bytes => 0;
        public long SourceRevision => 0;
    }
}
