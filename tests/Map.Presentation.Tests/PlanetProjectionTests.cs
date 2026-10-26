using Map.Core.Documents;
using Map.Export;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>R39: every projection takes its centre to the map's centre, its edges where they belong, and samples the planet across the seam.</summary>
public sealed class PlanetProjectionTests
{
    [Theory]
    [InlineData(PlanetProjection.Mercator)]
    [InlineData(PlanetProjection.Robinson)]
    [InlineData(PlanetProjection.Mollweide)]
    [InlineData(PlanetProjection.WinkelTripel)]
    [InlineData(PlanetProjection.Orthographic)]
    public void The_centre_of_the_output_is_longitude_and_latitude_zero(PlanetProjection projection)
    {
        var geo = PlanetProjections.Inverse(projection, .5, .5)!.Value;
        Assert.Equal(0, geo.Longitude, 6);
        Assert.Equal(0, geo.Latitude, 6);
    }

    [Fact]
    public void Edges_poles_and_outside_points_are_where_they_belong()
    {
        Assert.Equal(-Math.PI, PlanetProjections.Inverse(PlanetProjection.Mollweide, 0.0001, .5)!.Value.Longitude, 2);
        Assert.Null(PlanetProjections.Inverse(PlanetProjection.Mollweide, .01, .01));
        Assert.Equal(Math.PI / 2, PlanetProjections.Inverse(PlanetProjection.Robinson, .5, 0.0001)!.Value.Latitude, 2);
        Assert.Null(PlanetProjections.Inverse(PlanetProjection.Orthographic, .02, .02));
        var east = PlanetProjections.Inverse(PlanetProjection.WinkelTripel, .75, .5)!.Value;
        Assert.InRange(east.Longitude, Math.PI / 4, 3 * Math.PI / 4);
        // A gore: the middle of the first of twelve at the equator is longitude −165°.
        Assert.Equal(-165 * Math.PI / 180, PlanetProjections.Inverse(PlanetProjection.Gores, 1 / 24.0, .5)!.Value.Longitude, 6);
        Assert.Null(PlanetProjections.Inverse(PlanetProjection.Gores, 0.0001, .05));
        // The icosahedral net: the north apex is the pole, every inside point lands on the sphere.
        var pole = PlanetProjections.Inverse(PlanetProjection.Icosahedral, .5 / 5.5, 0.001)!.Value;
        Assert.InRange(pole.Latitude, 1.4, Math.PI / 2);
        Assert.Null(PlanetProjections.Inverse(PlanetProjection.Icosahedral, .999, .01));
    }

    [Fact]
    public void A_projection_samples_the_planet_and_leaves_the_outside_clear()
    {
        // West half red, east half blue.
        using var planet = new SKBitmap(new SKImageInfo(360, 180, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(planet))
        {
            canvas.Clear(SKColors.Blue);
            using var red = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(0, 0, 180, 180, red);
        }
        using var mollweide = PlanetProjections.Project(planet, PlanetProjection.Mollweide, 400);
        Assert.Equal((400, 200), (mollweide.Width, mollweide.Height));
        Assert.True(mollweide.GetPixel(100, 100).Red > 200);
        Assert.True(mollweide.GetPixel(300, 100).Blue > 200);
        Assert.Equal(0, mollweide.GetPixel(2, 2).Alpha);

        // Turned 180°, the globe's centre is on the seam (longitude ±180): red on one side, blue on the other.
        using var globe = PlanetProjections.Project(planet, PlanetProjection.Orthographic, 200, 180, 0);
        Assert.True(globe.GetPixel(90, 100).Blue > 200);
        Assert.True(globe.GetPixel(110, 100).Red > 200);

        Assert.True(MapPlanet.Fits(new CanvasDefinition(2048, 1024, "px", "#FFFFFFFF")));
        Assert.False(MapPlanet.Fits(new CanvasDefinition(2048, 1500, "px", "#FFFFFFFF")));
        var canvasDef = new CanvasDefinition(2048, 1024, "px", "#FFFFFFFF");
        Assert.Equal((0.0, 0.0), MapPlanet.ToGeo(canvasDef, 1024, 512));
    }
}
