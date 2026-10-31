using Map.Core.Documents;
using Map.Rendering.Lighting;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>The Свет section (DEC-057): neutral day, darker night, lights, grounded shadows, tiles = whole.</summary>
public sealed class LightingRenderingTests
{
    private const string Sha = "1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public void Disabled_lighting_draws_exactly_the_unlit_map()
    {
        using var mountain = Mountain();
        var unlit = Scene(null);
        var off = Scene(new MapLighting { Enabled = false });
        using var a = Draw(unlit, mountain, new WorldBounds(0, 0, 512, 384));
        using var b = Draw(off, mountain, new WorldBounds(0, 0, 512, 384));
        Assert.Equal(0, MaxDiff(a, b));
    }

    [Fact]
    public void Day_with_every_effect_off_keeps_the_painting()
    {
        using var mountain = Mountain();
        var lighting = new MapLighting
        {
            TimeOfDay = 0,
            Relief = new(false, 1), Shadows = new(false, 1), Fog = new(false, 1), Lights = new(false, 1), Color = new(true, 1),
        };
        using var a = Draw(Scene(null), mountain, new WorldBounds(0, 0, 512, 384));
        using var b = Draw(Scene(lighting), mountain, new WorldBounds(0, 0, 512, 384));
        Assert.True(MaxDiff(a, b) <= 2, $"day neutral differs by {MaxDiff(a, b)}");
    }

    [Fact]
    public void Night_is_darker_and_a_light_lifts_the_ground_around_it()
    {
        using var mountain = Mountain();
        var view = new WorldBounds(0, 0, 512, 384);
        using var day = Draw(Scene(new MapLighting { TimeOfDay = 0 }), mountain, view);
        using var night = Draw(Scene(new MapLighting { TimeOfDay = 1 }), mountain, view);
        Assert.True(Luma(night.GetPixel(40, 340)) < Luma(day.GetPixel(40, 340)) - 40);

        var lit = new MapLighting
        {
            TimeOfDay = 1,
            Sources = [new MapLightSource(Guid.NewGuid(), 60, 330, "#ffb866", 120, 1, false)],
        };
        using var torch = Draw(Scene(lit), mountain, view);
        Assert.True(Luma(torch.GetPixel(70, 330)) > Luma(night.GetPixel(70, 330)) + 30);

        // R14: the dim zone lights a ring beyond the bright circle; the circle itself stays as it was.
        var zoned = lit with { Sources = [lit.Sources[0] with { DimZone = 1 }] };
        using var dim = Draw(Scene(zoned), mountain, view);
        Assert.True(Luma(dim.GetPixel(220, 330)) > Luma(torch.GetPixel(220, 330)) + 8,
            $"the dim zone reaches past the radius: {Luma(dim.GetPixel(220, 330)):0.0} vs {Luma(torch.GetPixel(220, 330)):0.0}");
        Assert.True(Math.Abs(Luma(dim.GetPixel(70, 330)) - Luma(torch.GetPixel(70, 330))) < 25, "the bright circle keeps its light");
    }

    [Fact]
    public void Evening_shadow_starts_at_the_foot_of_the_mountain()
    {
        using var mountain = Mountain();
        var view = new WorldBounds(0, 0, 512, 384);
        var evening = new MapLighting { TimeOfDay = 0.5, Fog = new(false, 0), Lights = new(false, 0) };
        var noShadow = evening with { Shadows = new(false, 1) };
        // The export waits for the shape maps; the canvas shows a stamp unlit until they are built.
        using var lit = Draw(Scene(evening), mountain, view, export: true);
        using var flat = Draw(Scene(noShadow), mountain, view, export: true);
        // Ground just right of the far (east) flank, the evening sun low in the west: shaded, touching the flank.
        Assert.True(Luma(lit.GetPixel(310, 230)) < Luma(flat.GetPixel(310, 230)) - 6,
            $"{Luma(lit.GetPixel(310, 230))} vs {Luma(flat.GetPixel(310, 230))}");
        // Nothing hangs under the foot apart from the mountain.
        Assert.True(Math.Abs(Luma(lit.GetPixel(220, 360)) - Luma(flat.GetPixel(220, 360))) <= 3);
        // Far above the mountain nothing changes.
        Assert.True(Math.Abs(Luma(lit.GetPixel(20, 20)) - Luma(flat.GetPixel(20, 20))) <= 2);
    }

    [Fact]
    public void A_torch_flickers_only_on_an_animated_canvas_and_asks_for_frames_only_then()
    {
        using var mountain = Mountain();
        var view = new WorldBounds(0, 0, 512, 384);
        // A fixed id: the flicker phase comes from the id, and with a random one about one run in
        // forty the two moments below happened to be equally bright (CI 2026-10-30). This id's
        // brightness differs by about 9 % between them.
        var torch = new MapLightSource(Guid.Parse("11111111-2222-3333-4444-555555555555"), 60, 330, "#ff9a42", 120, 1.1, Flicker: true);
        var night = Scene(new MapLighting { TimeOfDay = 1, Sources = [torch] });
        var day = Scene(new MapLighting { TimeOfDay = 0, Sources = [torch] });

        // The mountain's light maps are built in the background for the canvas; an export waits for
        // them. Built once first, so both steady frames see the same maps (alone, the first frame
        // had none and the test failed whatever the light did).
        using (Draw(night, mountain, view, export: true)) { }
        var asked = 0;
        using var steadyA = Draw(night, mountain, view, seconds: null, ask: () => asked++);
        using var steadyB = Draw(night, mountain, view, seconds: null, ask: () => asked++);
        Assert.Equal(0, MaxDiff(steadyA, steadyB));
        Assert.Equal(0, asked);

        using var early = Draw(night, mountain, view, seconds: 0.10, ask: () => asked++);
        using var late = Draw(night, mountain, view, seconds: 0.47, ask: () => asked++);
        Assert.True(Math.Abs(Luma(early.GetPixel(70, 330)) - Luma(late.GetPixel(70, 330))) >= 1, "the torch does not flicker");
        Assert.Equal(2, asked);

        // By day the torches are out: no animation frames.
        using var noon = Draw(day, mountain, view, seconds: 0.10, ask: () => asked++);
        Assert.Equal(2, asked);
    }

    [Fact]
    public void Export_tiles_match_the_whole_frame()
    {
        using var mountain = Mountain();
        var lighting = new MapLighting { TimeOfDay = 0.7, Sources = [new MapLightSource(Guid.NewGuid(), 240, 300, "#ffb866", 140, 1, false)] };
        var document = Scene(lighting);
        using var whole = Draw(document, mountain, new WorldBounds(0, 0, 512, 384), export: true);
        using var left = Draw(document, mountain, new WorldBounds(0, 0, 256, 384), export: true);
        using var right = Draw(document, mountain, new WorldBounds(256, 0, 512, 384), export: true);
        var worst = 0;
        for (var y = 0; y < 384; y += 3)
        for (var x = 0; x < 512; x += 3)
        {
            var tile = x < 256 ? left.GetPixel(x, y) : right.GetPixel(x - 256, y);
            var full = whole.GetPixel(x, y);
            worst = Math.Max(worst, Math.Max(Math.Abs(tile.Red - full.Red), Math.Max(Math.Abs(tile.Green - full.Green), Math.Abs(tile.Blue - full.Blue))));
        }
        Assert.True(worst <= 8, $"tile seam {worst}");
    }

    private static MapDocument Scene(MapLighting? lighting)
    {
        var stamp = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Mountain", 100, 80, 240, 240, 0,
            "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(Sha) };
        return new MapDocument(DocumentId.New(), "Light", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 384, "px", "#FFB6AE86"),
            [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFB6AE86"),
             new ObjectLayer(LayerId.New(), "Mountains", true, false, 1, [stamp])]) { Lighting = lighting };
    }

    /// <summary>A grey-blue cone with a dark left face: enough of a mountain for the light pass.</summary>
    private static SKImage Mountain()
    {
        using var surface = SKSurface.Create(new SKImageInfo(256, 256))!;
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        using var path = new SKPath();
        path.MoveTo(128, 10); path.LineTo(250, 250); path.LineTo(6, 250); path.Close();
        using var fill = new SKPaint { Color = new SKColor(0xdd, 0xe6, 0xee), IsAntialias = true };
        c.DrawPath(path, fill);
        using var left = new SKPath();
        left.MoveTo(128, 10); left.LineTo(128, 250); left.LineTo(6, 250); left.Close();
        using var shade = new SKPaint { Color = new SKColor(0x6f, 0x81, 0x99), IsAntialias = true };
        c.DrawPath(left, shade);
        return surface.Snapshot().ToRasterImage();
    }

    private static SKBitmap Draw(MapDocument document, SKImage mountain, WorldBounds view, bool export = false,
        double? seconds = null, Action? ask = null)
    {
        var info = new SKImageInfo((int)(view.Right - view.Left), (int)(view.Bottom - view.Top));
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Translate((float)-view.Left, (float)-view.Top);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions
        {
            Stamps = new Images(mountain), IsExport = export, VisibleWorld = view,
            LightAnimationSeconds = seconds, RequestAmbientFrame = ask,
        });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    private static int MaxDiff(SKBitmap a, SKBitmap b)
    {
        var worst = 0;
        for (var y = 0; y < a.Height; y++)
        for (var x = 0; x < a.Width; x++)
        {
            var p = a.GetPixel(x, y); var q = b.GetPixel(x, y);
            worst = Math.Max(worst, Math.Max(Math.Abs(p.Red - q.Red), Math.Max(Math.Abs(p.Green - q.Green), Math.Abs(p.Blue - q.Blue))));
        }
        return worst;
    }

    private static double Luma(SKColor c) => 0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue;

    private sealed class Images(SKImage image) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => image;
    }
}
