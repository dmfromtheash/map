using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// Owner, 2026-11-12: zoomed out, some styles hid the canvas edge — «будто всё становится одним бесконечным стилем».
/// A styled frame is painted only inside the map; the workspace around it stays as the caller left it.
/// </summary>
public sealed partial class StyleRenderingTests
{
    // The 512×384 map seen from farther away: 128 px of workspace on each side.
    private static readonly WorldBounds ZoomedOut = new(-128, -128, 640, 512);

    public static TheoryData<MapStylePreset> AllPresets()
    {
        var data = new TheoryData<MapStylePreset>();
        foreach (var preset in Enum.GetValues<MapStylePreset>()) data.Add(preset);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllPresets))]
    public void A_style_paints_nothing_outside_the_map(MapStylePreset preset)
    {
        AssertOnlyTheMapIsPainted(Scene(MapStyle.For(preset)), preset.ToString());
        // A lit map at night goes through the light pass inside the style.
        AssertOnlyTheMapIsPainted(Scene(MapStyle.For(preset), lighting: new MapLighting { TimeOfDay = 1 }), preset + " + night");
    }

    [Fact]
    public void Light_without_a_style_paints_nothing_outside_the_map() =>
        AssertOnlyTheMapIsPainted(Scene(null, lighting: new MapLighting { TimeOfDay = 1 }), "night");

    private static void AssertOnlyTheMapIsPainted(MapDocument document, string what)
    {
        using var mountain = Mountain();
        using var frame = Draw(document, mountain, ZoomedOut);
        var preset = what;
        // Device (128,128)..(640,512) is the map; everything else is workspace and must stay untouched (transparent).
        var outside = 0;
        for (var y = 0; y < frame.Height; y++)
        for (var x = 0; x < frame.Width; x++)
        {
            var inMap = x >= 127 && x <= 640 && y >= 127 && y <= 512;
            if (!inMap && frame.GetPixel(x, y).Alpha != 0) outside++;
        }
        Assert.True(outside == 0, $"{preset}: {outside} workspace pixels painted");
        // The map itself is still painted to its edges.
        Assert.Equal(255, frame.GetPixel(130, 130).Alpha);
        Assert.Equal(255, frame.GetPixel(637, 509).Alpha);
    }
}
