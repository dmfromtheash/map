using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// The coast and cliffs of new land follow the owner's mountains: the slate-blue rock with his ink,
/// a pen coastline and a shadow cast away from the north-west light.
/// </summary>
public sealed class PaintedCoastRenderingTests
{
    [Fact]
    public void New_land_takes_the_mountain_cliff_and_an_ink_coast_lit_from_the_north_west()
    {
        var style = LandEdgeStyle.SoftWatercolor();
        Assert.Equal(LandCliffStyle.OwnerMountains, style.Cliff);
        Assert.Equal(LandCliffStyle.OwnerMountainsId, LandCliffStyle.Looks[0].Id);
        Assert.Equal("#FF1F2A36", style.OutlineColor);
        Assert.True(style.ShadowOffsetX > 0 && style.ShadowOffsetY > 0, "the shadow falls to the south-east");
        // Older maps keep their cliff: the plain default is still the ink-and-sepia look.
        Assert.Equal(LandCliffStyle.Looks.Single(l => l.Id == "ink-sepia").Style, new LandCliffStyle());
    }

    [Fact]
    public void The_mountain_cliff_is_drawn_in_the_slate_blue_of_the_mountains()
    {
        int Bluish(SKBitmap bitmap)
        {
            var count = 0;
            for (var y = 0; y < bitmap.Height; y += 2)
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Blue > c.Red + 25) count++;
            }
            return count;
        }
        using var owner = LandCliffRenderingTests.RenderSheet(LandCliffStyle.Looks.Single(l => l.Id == LandCliffStyle.OwnerMountainsId), 1);
        using var sepia = LandCliffRenderingTests.RenderSheet(LandCliffStyle.Looks.Single(l => l.Id == "ink-sepia"), 1);
        Assert.True(Bluish(owner) > 1500, $"blue rock pixels: {Bluish(owner)}");
        Assert.True(Bluish(sepia) < 100, $"sepia sheet blue pixels: {Bluish(sepia)}");
    }
}
