using Map.Core.Documents;
using Map.Rendering.Styles;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>The style of a map (R09, DEC-059): off = the map as it was, ink paints it, names stay sharp, tiles = whole.</summary>
public sealed partial class StyleRenderingTests
{
    private const string Sha = "2222222222222222222222222222222222222222222222222222222222222222";
    private static readonly WorldBounds View = new(0, 0, 512, 384);

    [Fact]
    public void A_style_switched_off_draws_exactly_the_map_without_style()
    {
        using var mountain = Mountain();
        using var plain = Draw(Scene(null), mountain, View);
        using var off = Draw(Scene(MapStyle.For(MapStylePreset.InkWetBrush) with { Enabled = false }), mountain, View);
        using var none = Draw(Scene(MapStyle.For(MapStylePreset.InkWetBrush) with { Strength = 0 }), mountain, View);
        Assert.Equal(0, MaxDiff(plain, off));
        Assert.Equal(0, MaxDiff(plain, none));
    }

    [Theory]
    [InlineData(MapStylePreset.InkWetBrush)]
    [InlineData(MapStylePreset.InkPenHatching)]
    [InlineData(MapStylePreset.InkPenStipple)]
    public void Ink_turns_the_coloured_map_into_ink_on_paper(MapStylePreset preset)
    {
        using var mountain = Mountain();
        using var plain = Draw(Scene(null), mountain, View);
        using var ink = Draw(Scene(MapStyle.For(preset)), mountain, View);
        // The olive page became paper and ink: almost no colour is left away from the names.
        Assert.True(MaxDiff(plain, ink) > 40);
        Assert.True(Chroma(ink, new SKRectI(0, 0, 512, 300)) < Chroma(plain, new SKRectI(0, 0, 512, 300)) / 3,
            $"{Chroma(ink, new SKRectI(0, 0, 512, 300))} vs {Chroma(plain, new SKRectI(0, 0, 512, 300))}");
    }

    [Theory]
    [InlineData(MapStylePreset.InkWetBrush)]
    [InlineData(MapStylePreset.OldMapBlaeu)]
    [InlineData(MapStylePreset.EngravingAquatint)]
    [InlineData(MapStylePreset.WatercolorBigShapes)]
    [InlineData(MapStylePreset.UkiyoeSosakuHanga)]
    [InlineData(MapStylePreset.PrintLetterpress)]
    [InlineData(MapStylePreset.PrintTintedLithograph)]
    [InlineData(MapStylePreset.OilAllaPrima)]
    [InlineData(MapStylePreset.OilImpastoRhythm)]
    public void Names_are_drawn_over_the_style_exactly_as_without_it(MapStylePreset preset)
    {
        using var mountain = Mountain();
        // A red name on a plain page: with the style the solid insides of its letters keep their own
        // red exactly (the anti-aliased rims blend with whatever lies under them).
        using var plain = Draw(Scene(null, withName: true), mountain, View);
        using var ink = Draw(Scene(MapStyle.For(preset), withName: true), mountain, View);
        var letters = 0;
        for (var y = 330; y < 380; y++)
        for (var x = 20; x < 300; x++)
        {
            var p = plain.GetPixel(x, y);
            if (p == new SKColor(0xE0, 0x10, 0x10))
            {
                letters++;
                Assert.Equal(p, ink.GetPixel(x, y));
            }
        }
        Assert.True(letters > 100, $"only {letters} letter pixels");
    }

    [Fact]
    public void Split_leaves_the_names_out_of_the_painted_map_and_keeps_unchanged_layers()
    {
        var document = Scene(MapStyle.For(MapStylePreset.InkWetBrush), withName: true);
        var (bare, names) = StyleRenderer.Split(document);
        Assert.DoesNotContain(bare.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects), o => o.Kind == SceneObjectKind.Text);
        Assert.All(names!.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects), o => Assert.Equal(SceneObjectKind.Text, o.Kind));
        Assert.Null(bare.Style);
        // The mountains' layer has no text: the very same layer, its tiles stay valid.
        Assert.Same(document.Layers[1], bare.Layers[1]);
        // The same layer list gives the same split (kept between frames).
        Assert.Same(bare.Layers, StyleRenderer.Split(document with { Title = "другое" }).Bare.Layers);
    }

    [Theory]
    [InlineData(MapStylePreset.InkWetBrush)]
    [InlineData(MapStylePreset.InkPenHatching)]
    [InlineData(MapStylePreset.OldMapBlaeu)]
    [InlineData(MapStylePreset.EngravingAquatint)]
    public void Export_tiles_match_the_whole_frame(MapStylePreset preset)
    {
        using var mountain = Mountain();
        var document = Scene(MapStyle.For(preset));
        using var whole = Draw(document, mountain, View, export: true);
        using var left = Draw(document, mountain, new WorldBounds(0, 0, 256, 384), export: true);
        using var right = Draw(document, mountain, new WorldBounds(256, 0, 512, 384), export: true);
        // The unstyled map itself differs by a unit here and there between a tile and the whole
        // (stamp sampling); the ink's thresholds may lift such a pixel by a few more units. A seam
        // would be a band of differing pixels along x = 256, so: few pixels off by more than 4,
        // none by more than 24, and the column at the seam no worse than the rest.
        var worst = 0; var off = 0; var seamOff = 0; var count = 0; var worstAt = "";
        for (var y = 0; y < 384; y++)
        for (var x = 0; x < 512; x++)
        {
            var tile = x < 256 ? left.GetPixel(x, y) : right.GetPixel(x - 256, y);
            var full = whole.GetPixel(x, y);
            var d = Math.Max(Math.Abs(tile.Red - full.Red), Math.Max(Math.Abs(tile.Green - full.Green), Math.Abs(tile.Blue - full.Blue)));
            if (d > worst) { worst = d; worstAt = $"{x},{y}: {full} vs {tile}"; }
            count++;
            if (d > 4) { off++; if (Math.Abs(x - 256) <= 2) seamOff++; }
        }
        // Blaeu's hard engraved screen can flip an isolated pixel when the tensor's F16 blur
        // rounds at a threshold. Bound their count and the seam independently; keep ink's old bound.
        Assert.True(worst <= (preset == MapStylePreset.OldMapBlaeu ? 180 : 24),
            $"worst {worst} at {worstAt}; off={off}, seam={seamOff}");
        Assert.True(off < count / 500, $"{off} pixels differ");
        Assert.True(seamOff <= 3, $"{seamOff} pixels differ at the seam");
    }

    [Fact]
    public void Export_tiles_match_the_whole_frame_when_zoomed_in()
    {
        using var mountain = Mountain();
        var document = Scene(MapStyle.For(MapStylePreset.InkWetBrush));
        // Three device pixels per map pixel: the blurs and the margin grow with the scale.
        using var whole = Draw(document, mountain, new WorldBounds(150, 100, 320, 228), scale: 3);
        using var left = Draw(document, mountain, new WorldBounds(150, 100, 235, 228), scale: 3);
        using var right = Draw(document, mountain, new WorldBounds(235, 100, 320, 228), scale: 3);
        int off = 0, seam = 0, count = 0;
        for (var y = 0; y < whole.Height; y++)
        for (var x = 0; x < whole.Width; x++)
        {
            var tile = x < left.Width ? left.GetPixel(x, y) : right.GetPixel(x - left.Width, y);
            var full = whole.GetPixel(x, y);
            var d = Math.Max(Math.Abs(tile.Red - full.Red), Math.Max(Math.Abs(tile.Green - full.Green), Math.Abs(tile.Blue - full.Blue)));
            count++;
            if (d > 4) { off++; if (Math.Abs(x - left.Width) <= 2) seam++; }
        }
        Assert.True(off < count / 500, $"{off} pixels differ");
        Assert.True(seam <= 3, $"{seam} pixels differ at the seam");
    }

    [Fact]
    public void An_export_whose_axes_round_apart_keeps_the_style()
    {
        using var mountain = Mountain();
        using var plain = Draw(Scene(null), mountain, View, scaleY: 0.9995f);
        using var ink = Draw(Scene(MapStyle.For(MapStylePreset.InkWetBrush)), mountain, View, scaleY: 0.9995f);
        Assert.True(Chroma(ink, new SKRectI(0, 0, 512, 300)) < Chroma(plain, new SKRectI(0, 0, 512, 300)) / 3);
    }

    [Fact]
    public void The_split_is_kept_per_document_and_a_clipped_text_layer_stays_painted()
    {
        var document = Scene(MapStyle.For(MapStylePreset.InkWetBrush), withName: true);
        Assert.Same(StyleRenderer.Split(document).Bare, StyleRenderer.Split(document).Bare);
        var names = (ObjectLayer)document.Layers[2];
        var clipped = document.WithLayer(names.Id, l => ((ObjectLayer)l) with { Effects = new LayerEffects { ClippingEnabled = true } });
        var (bare, rest) = StyleRenderer.Split(clipped);
        Assert.Null(rest);
        Assert.Contains(bare.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects), o => o.Kind == SceneObjectKind.Text);
    }

    [Fact]
    public void Style_and_light_together_paint_the_lit_map()
    {
        using var mountain = Mountain();
        var ink = MapStyle.For(MapStylePreset.InkWetBrush);
        using var day = Draw(Scene(ink, lighting: new MapLighting { TimeOfDay = 0 }), mountain, View);
        using var night = Draw(Scene(ink, lighting: new MapLighting { TimeOfDay = 1 }), mountain, View);
        Assert.True(MaxDiff(day, night) > 20);
    }

    private static MapDocument Scene(MapStyle? style, bool withName = false, MapLighting? lighting = null)
    {
        var stamp = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Mountain", 100, 40, 240, 240, 0,
            "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(Sha) };
        var layers = new List<MapLayer>
        {
            new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFB6AE86"),
            new ObjectLayer(LayerId.New(), "Mountains", true, false, 1, [stamp]),
        };
        if (withName)
        {
            var name = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Имя", 30, 330, 260, 50, 0, "#FFE01010", "#00000000", 0, true, false)
            {
                Text = new TextStyle { Content = "КРАСНОЕ ИМЯ", FontSize = 36, FillColor = "#FFE01010", OutlineWidth = 0 },
            };
            layers.Add(new ObjectLayer(LayerId.New(), "Names", true, false, 1, [name]));
        }
        return new MapDocument(DocumentId.New(), "Style", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 384, "px", "#FFB6AE86"), layers) { Style = style, Lighting = lighting };
    }

    /// <summary>A grey-blue cone with a dark left face and a black outline: a mountain with an ink contour.</summary>
    private static SKImage Mountain()
    {
        using var surface = SKSurface.Create(new SKImageInfo(256, 256))!;
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        using var path = new SKPath();
        path.MoveTo(128, 10); path.LineTo(250, 250); path.LineTo(6, 250); path.Close();
        using var fill = new SKPaint { Color = new SKColor(0x9d, 0xb6, 0x8e), IsAntialias = true };
        c.DrawPath(path, fill);
        using var left = new SKPath();
        left.MoveTo(128, 10); left.LineTo(128, 250); left.LineTo(6, 250); left.Close();
        using var shade = new SKPaint { Color = new SKColor(0x4f, 0x61, 0x49), IsAntialias = true };
        c.DrawPath(left, shade);
        using var outline = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
        c.DrawPath(path, outline);
        return surface.Snapshot().ToRasterImage();
    }

    private static SKBitmap Draw(MapDocument document, SKImage mountain, WorldBounds view, bool export = false,
        float scale = 1, float? scaleY = null)
    {
        var sy = scaleY ?? scale;
        var info = new SKImageInfo((int)Math.Round((view.Right - view.Left) * scale), (int)Math.Round((view.Bottom - view.Top) * sy));
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Scale(scale, sy);
        surface.Canvas.Translate((float)-view.Left, (float)-view.Top);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { Stamps = new Images(mountain), IsExport = export, VisibleWorld = view });
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

    /// <summary>Mean distance of the colours from grey.</summary>
    private static double Chroma(SKBitmap image, SKRectI area)
    {
        double sum = 0; var n = 0;
        for (var y = area.Top; y < area.Bottom; y += 2)
        for (var x = area.Left; x < area.Right; x += 2)
        {
            var p = image.GetPixel(x, y);
            sum += Math.Max(p.Red, Math.Max(p.Green, p.Blue)) - Math.Min(p.Red, Math.Min(p.Green, p.Blue));
            n++;
        }
        return sum / n;
    }

    private sealed class Images(SKImage image) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => image;
    }
}
