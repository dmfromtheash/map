using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>Continuous per-stamp variation of a scatter: bounded, seeded, and silent when off.</summary>
public sealed class StampJitterTests
{
    private static readonly StampVariantSource Variant = new("a", 100, 50);

    [Fact]
    public void Jitter_off_leaves_the_plan_exactly_as_before()
    {
        var request = new StampPlacementRequest { AreaRadius = 200, Density = 60, Seed = 7, RandomAngleStep = StampRandomAngleStep.Degrees30, RandomFlip = true };
        var plan = StampPlacement.PlanArea(request, [Variant]);
        Assert.All(plan, item =>
        {
            Assert.Equal(1, item.OpacityFactor);
            Assert.Equal(0, item.HueShift);
            Assert.Equal(96, item.Width, 6);
            Assert.Equal(0, item.RotationDegrees % 30, 6);
        });
    }

    [Fact]
    public void Jitter_varies_within_its_ranges_keeps_centres_and_replays_from_the_seed()
    {
        var request = new StampPlacementRequest
        {
            AreaRadius = 300, Density = 80, Seed = 11, BaseWidth = 100,
            RotationJitterDegrees = 20, SizeJitter = .3, OpacityJitter = .5,
            HueJitterDegrees = 30, SaturationJitter = .2, BrightnessJitter = .1,
        };
        var plain = StampPlacement.PlanArea(request with { RotationJitterDegrees = 0, SizeJitter = 0, OpacityJitter = 0, HueJitterDegrees = 0, SaturationJitter = 0, BrightnessJitter = 0 }, [Variant]);
        var jittered = StampPlacement.PlanArea(request, [Variant]);
        var again = StampPlacement.PlanArea(request, [Variant]);

        Assert.Equal(plain.Count, jittered.Count);
        Assert.Equal(jittered, again);
        var varied = 0;
        for (var i = 0; i < plain.Count; i++)
        {
            var a = plain[i]; var b = jittered[i];
            // Same centre, a size within ±30 %, a rotation within ±20°, colour offsets within range.
            Assert.Equal(a.X + a.Width / 2, b.X + b.Width / 2, 6);
            Assert.Equal(a.Y + a.Height / 2, b.Y + b.Height / 2, 6);
            Assert.InRange(b.Width, 70 - 1e-6, 130 + 1e-6);
            Assert.Equal(b.Width / b.Height, a.Width / a.Height, 6);
            var delta = ((b.RotationDegrees - a.RotationDegrees + 540) % 360) - 180;
            Assert.InRange(delta, -20 - 1e-6, 20 + 1e-6);
            Assert.InRange(b.OpacityFactor, .5 - 1e-6, 1);
            Assert.InRange(b.HueShift, -30, 30);
            Assert.InRange(b.SaturationShift, -.2, .2);
            Assert.InRange(b.BrightnessShift, -.1, .1);
            if (Math.Abs(b.Width - a.Width) > 1 || Math.Abs(delta) > 1) varied++;
        }

        Assert.True(varied > plain.Count / 2, "the jitter should change most stamps");
    }
}
