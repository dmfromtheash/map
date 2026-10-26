namespace Map.Core.Documents;

/// <summary>
/// The map is a whole planet (R39): the canvas, two to one, is the equirectangular world - the
/// left edge meets the right edge at the antimeridian, the top is the north pole and the bottom
/// the south. «Бесшовный меридиан» shows, beyond each side edge while drawing, the other side of
/// the world, so a coast crossing the seam can be matched.
/// </summary>
public sealed record MapPlanet
{
    public bool Enabled { get; init; }
    public bool SeamlessMeridian { get; init; } = true;

    public bool IsDefault => !Enabled && SeamlessMeridian;

    /// <summary>A canvas fits a planet when it is two to one (within a pixel).</summary>
    public static bool Fits(CanvasDefinition canvas) => Fits(canvas.Width, canvas.Height);

    public static bool Fits(int width, int height) => Math.Abs(width - (2.0 * height)) <= 1;

    /// <summary>Longitude (−180…180) and latitude (−90…90) of a point of the canvas.</summary>
    public static (double Longitude, double Latitude) ToGeo(CanvasDefinition canvas, double x, double y) =>
        ((x / canvas.Width * 360) - 180, 90 - (y / canvas.Height * 180));

    public static (double X, double Y) FromGeo(CanvasDefinition canvas, double longitude, double latitude) =>
        ((longitude + 180) / 360 * canvas.Width, (90 - latitude) / 180 * canvas.Height);
}
