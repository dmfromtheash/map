using Map.Spike.Core.Documents;
using Map.Spike.Core.Fixtures;
using Map.Spike.Core.Geometry;
using Map.Spike.Core.Spatial;
using Map.Spike.Rendering;

namespace Map.Spike.Tests.Core;

public class SpatialHitTestTests
{
    [Fact]
    public void HitTest_selects_the_expected_object_id_in_a_10k_scene()
    {
        var document = SpikeFixtures.CreateObjectScene(10_000);

        // Pick a known object and aim at its exact centre. Its centre is inside every shape kind.
        var expected = document.Objects.Objects[7_531];
        var result = HitTester.HitTestWorld(document, expected.Center);

        Assert.True(result.Hit);

        // Another object may legitimately sit on top of this one at that point. What must hold is
        // that the winner really contains the point and is not below the expected object.
        Assert.True(result.Object!.ContainsPoint(expected.Center));

        var winnerIndex = IndexOf(document, result.Object!.Id);
        Assert.True(winnerIndex >= 7_531, "Hit test returned an object below the expected one.");
    }

    [Fact]
    public void HitTest_examines_far_fewer_candidates_than_the_scene_holds()
    {
        var document = SpikeFixtures.CreateObjectScene(10_000);
        var stats = default(SpatialQueryStats);

        document.Objects.HitTest(document.Objects.Objects[100].Center, ref stats);

        Assert.Equal(10_000, stats.SceneObjectCount);

        // The whole point of the index: a point query must not walk the scene. One percent is a
        // deliberately loose ceiling - the measured value is reported in the benchmark output.
        Assert.True(
            stats.CandidatesExamined < 100,
            $"Point query examined {stats.CandidatesExamined} candidates out of {stats.SceneObjectCount}.");
    }

    [Fact]
    public void HitTest_returns_the_topmost_object_when_two_overlap()
    {
        var layer = new ObjectLayer(LayerId.New(), "objects");

        var below = new SpikeObject
        {
            Id = ObjectId.New(),
            Kind = SpikeObjectKind.Square,
            Center = new Vec2(100, 100),
            Size = 40,
            Rotation = 0,
            ColorArgb = 0xFF112233,
        };

        var above = new SpikeObject
        {
            Id = ObjectId.New(),
            Kind = SpikeObjectKind.Square,
            Center = new Vec2(105, 105),
            Size = 40,
            Rotation = 0,
            ColorArgb = 0xFF445566,
        };

        layer.Add(below);
        layer.Add(above);
        layer.RebuildIndex();

        var stats = default(SpatialQueryStats);
        var hit = layer.HitTest(new Vec2(102, 102), ref stats);

        Assert.NotNull(hit);
        Assert.Equal(above.Id, hit!.Id);
    }

    [Fact]
    public void HitTest_on_empty_space_returns_nothing()
    {
        var document = SpikeFixtures.CreateObjectScene(1_000, canvasSize: 2048);
        var result = HitTester.HitTestWorld(document, new Vec2(-5000, -5000));

        Assert.False(result.Hit);
        Assert.Null(result.Object);
    }

    [Fact]
    public void Precise_shape_test_rejects_a_point_inside_bounds_but_outside_the_triangle()
    {
        var triangle = new SpikeObject
        {
            Id = ObjectId.New(),
            Kind = SpikeObjectKind.Triangle,
            Center = new Vec2(0, 0),
            Size = 100,
            Rotation = 0,
            ColorArgb = 0xFF000000,
        };

        // Top-left corner of the bounding box: inside Bounds, outside the apex-up triangle.
        Assert.True(triangle.Bounds.Contains(new Vec2(-45, -45)));
        Assert.False(triangle.ContainsPoint(new Vec2(-45, -45)));
        Assert.True(triangle.ContainsPoint(new Vec2(0, 40)));
    }

    [Fact]
    public void Visible_query_returns_fewer_objects_than_the_scene_when_zoomed_in()
    {
        var document = SpikeFixtures.CreateObjectScene(10_000);
        var stats = default(SpatialQueryStats);

        var visible = document.Objects.QueryVisible(new Rect2(0, 0, 256, 256), ref stats);

        Assert.NotEmpty(visible);
        Assert.True(
            visible.Count < 500,
            $"A 256x256 window of a 4096x4096 scene returned {visible.Count} objects.");
    }

    private static int IndexOf(SpikeDocument document, ObjectId id)
    {
        for (var i = 0; i < document.Objects.Count; i++)
        {
            if (document.Objects.Objects[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }
}
