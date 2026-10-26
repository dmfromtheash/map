using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>R38: a generated town in isometry - buildings stand up, the picture changes with the turn and the heights.</summary>
public sealed class IsometricViewTests
{
    [Fact]
    public void A_town_stands_up_and_turns()
    {
        var town = CityBuilder.NewCityMap("Ильград", 1600, new CitySettings { Name = "Ильград", Seed = 11, Radius = 600 }, new MapCategoryId("city"), DateTimeOffset.UnixEpoch);
        using var flat = IsometricRenderer.Render(town, new IsometricSettings { Width = 900, HeightScale = 0.01 });
        using var view = IsometricRenderer.Render(town, new IsometricSettings { Width = 900 });
        using var turned = IsometricRenderer.Render(town, new IsometricSettings { Width = 900, Turn = 1 });
        Assert.Equal(900, view.Width);
        Assert.True(view.Height is > 400 and < 700);
        // Standing houses show walls and shadows the flat ones do not.
        Assert.True(Difference(flat, view) > .05);
        Assert.True(Difference(view, turned) > .05);
        if (Environment.GetEnvironmentVariable("MAP_ISO_SHOT") is { Length: > 0 } shot)
            File.WriteAllBytes(shot, IsometricRenderer.RenderPng(town, new IsometricSettings { Width = 2000 }));
    }

    private static double Difference(SKBitmap a, SKBitmap b)
    {
        var differ = 0; var total = 0;
        for (var y = 0; y < Math.Min(a.Height, b.Height); y += 4)
        for (var x = 0; x < a.Width; x += 4)
        {
            total++;
            var (p, q) = (a.GetPixel(x, y), b.GetPixel(x, y));
            if (Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue) > 30) differ++;
        }
        return differ / (double)total;
    }
}
