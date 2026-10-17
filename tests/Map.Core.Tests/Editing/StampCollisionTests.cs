using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>«Без наложений» of the scatter: collision boxes, seeded replay and the untouched old path.</summary>
public sealed class StampCollisionTests
{
    private static readonly StampVariantSource Variant = new("a", 100, 60);

    [Fact]
    public void Turned_boxes_overlap_only_when_they_really_intersect()
    {
        var a = new StampCollisionBox(0, 0, 10, 2, 0);
        Assert.True(StampCollision.Overlaps(a, a with { CenterX = 15 }));
        Assert.False(StampCollision.Overlaps(a, a with { CenterX = 21 }));
        // A thin bar turned 90° next to the tip of another: their axis-aligned boxes would touch, the bars do not.
        Assert.False(StampCollision.Overlaps(a, new StampCollisionBox(0, 7, 10, 2, 90) with { CenterX = 13 }));
        Assert.True(StampCollision.Overlaps(a, new StampCollisionBox(0, 0, 10, 2, 90)));
        // Touching edges do not count.
        Assert.False(StampCollision.Overlaps(a, a with { CenterX = 20 }));
        Assert.Equal(15, StampCollision.Box(0, 0, 100, 50, 0, 0.3).HalfWidth, 6);
        Assert.Equal(StampCollision.DefaultFraction, StampCollision.ClampFraction(null));
        Assert.Equal(1, StampCollision.ClampFraction(4));
    }

    [Fact]
    public void Switched_off_the_plan_is_exactly_the_old_one()
    {
        var request = new StampPlacementRequest { AreaRadius = 200, Density = 120, Seed = 5, RandomFlip = true, SizeJitter = 0.2 };
        var withExisting = request with { ExistingCollisions = [new StampCollisionBox(0, 0, 500, 500, 0)] };
        Assert.Equal(StampPlacement.PlanArea(request, [Variant]), StampPlacement.PlanArea(withExisting, [Variant]));
    }

    [Fact]
    public void Switched_on_it_only_removes_stamps_and_none_of_the_kept_boxes_intersect()
    {
        var request = new StampPlacementRequest
        {
            AreaRadius = 250, Density = 200, Seed = 9, BaseWidth = 90,
            RandomAngleStep = StampRandomAngleStep.Degrees45, RotationJitterDegrees = 15, SizeJitter = 0.25,
        };
        var all = StampPlacement.PlanArea(request, [Variant]);
        var spaced = StampPlacement.PlanArea(request with { AvoidOverlap = true }, [Variant]);

        Assert.True(spaced.Count < all.Count && spaced.Count > 5, $"{spaced.Count} of {all.Count}");
        // A subsequence of the full plan: same stamps, same order, nothing moved.
        var cursor = 0;
        foreach (var kept in spaced)
        {
            while (cursor < all.Count && all[cursor] != kept) cursor++;
            Assert.True(cursor < all.Count, "a kept stamp is not one of the original plan");
            cursor++;
        }
        var boxes = spaced.Select(item => StampCollision.For(item, StampCollision.DefaultFraction)).ToArray();
        for (var i = 0; i < boxes.Length; i++)
        for (var j = i + 1; j < boxes.Length; j++)
            Assert.False(StampCollision.Overlaps(boxes[i], boxes[j]), $"stamps {i} and {j} overlap");
        Assert.Equal(spaced, StampPlacement.PlanArea(request with { AvoidOverlap = true }, [Variant]));
    }

    [Fact]
    public void Stamps_already_on_the_layer_and_the_passport_fraction_are_respected()
    {
        var request = new StampPlacementRequest { CenterX = 0, CenterY = 0, AreaRadius = 150, Density = 150, Seed = 3, AvoidOverlap = true };
        var blocker = new StampCollisionBox(0, 0, 60, 60, 0);
        var plan = StampPlacement.PlanArea(request with { ExistingCollisions = [blocker] }, [Variant]);
        Assert.NotEmpty(plan);
        Assert.All(plan, item => Assert.False(StampCollision.Overlaps(blocker, StampCollision.For(item, StampCollision.DefaultFraction))));

        var tight = StampPlacement.PlanArea(request, [Variant with { CollisionFraction = 0.1 }]);
        var loose = StampPlacement.PlanArea(request, [Variant with { CollisionFraction = 0.8 }]);
        Assert.True(tight.Count > loose.Count, $"tight {tight.Count}, loose {loose.Count}");
    }

    [Fact]
    public void Preview_is_the_first_part_of_the_committed_plan()
    {
        var request = new StampPlacementRequest { AreaRadius = 300, Density = 300, Seed = 21, AvoidOverlap = true };
        var full = StampPlacement.PlanArea(request, [Variant]);
        var preview = StampPlacement.PlanAreaPreview(request, [Variant], 12);
        Assert.Equal(12, preview.Count);
        Assert.Equal(full.Take(12), preview);
    }

    [Fact]
    public void Tool_rotation_turns_the_boxes_too()
    {
        // Tall narrow stamps in a row: upright they fit side by side, turned 90° they collide.
        var narrow = new StampVariantSource("n", 20, 100);
        var request = new StampPlacementRequest { AreaRadius = 120, Density = 400, Seed = 2, BaseWidth = 20, AvoidOverlap = true };
        var upright = StampPlacement.PlanArea(request, [narrow]);
        var turned = StampPlacement.PlanArea(request with { CollisionRotationDegrees = 90 }, [narrow]);
        Assert.NotEqual(upright.Count, turned.Count);
    }
}
