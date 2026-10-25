using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed partial class StyleRenderingTests
{
    public static IEnumerable<object[]> PainterPresets => new[]
    {
        MapStylePreset.WatercolorBigShapes, MapStylePreset.UkiyoeSosakuHanga,
        MapStylePreset.PrintLetterpress, MapStylePreset.PrintTintedLithograph,
    }.Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(PainterPresets))]
    public void Painter_tiles_and_screen_export_agree_at_half_full_and_double_scale(MapStylePreset preset)
    {
        using var mountain = Mountain();
        var doc = PainterScene(preset);
        foreach (var scale in new[] { .5f, 1f, 2f })
        {
            using var screen = Draw(doc, mountain, View, scale: scale);
            using var whole = Draw(doc, mountain, View, export: true, scale: scale);
            Assert.Equal(0, MaxDiff(screen, whole));
            using var left = Draw(doc, mountain, new(0, 0, 256, 384), export: true, scale: scale);
            using var right = Draw(doc, mountain, new(256, 0, 512, 384), export: true, scale: scale);
            long sum = 0; var seam = 0; var off = 0;
            for (var y = 0; y < whole.Height; y++)
            for (var x = 0; x < whole.Width; x++)
            {
                var a = whole.GetPixel(x, y);
                var b = x < left.Width ? left.GetPixel(x, y) : right.GetPixel(x - left.Width, y);
                var d = Math.Max(Math.Abs(a.Red-b.Red),Math.Max(Math.Abs(a.Green-b.Green),Math.Abs(a.Blue-b.Blue)));
                sum += d;
                if (d > 4) { off++; if (Math.Abs(x-left.Width) <= 2) seam++; }
            }
            Assert.True(sum / (double)(whole.Width*whole.Height) < .1, $"{preset}/{scale}: mean max channel {sum / (double)(whole.Width*whole.Height)}");
            Assert.True(off < whole.Width*whole.Height/500, $"{preset}/{scale}: {off} pixels differ");
            Assert.True(seam <= 3, $"{preset}/{scale}: {seam} seam pixels");
        }
    }

    [Theory]
    [MemberData(nameof(PainterPresets))]
    public void Every_exposed_painter_effect_changes_pixels_and_off_equals_zero(MapStylePreset preset)
    {
        using var mountain = Mountain();
        var doc = PainterScene(preset);
        using var normal = Draw(doc, mountain, View);
        foreach (var kind in MapStyle.EffectsOf(preset))
        {
            var effect = doc.Style!.Effect(kind);
            using var off = Draw(doc with { Style = doc.Style.WithEffect(kind, effect with { Enabled = false }) }, mountain, View);
            using var zero = Draw(doc with { Style = doc.Style.WithEffect(kind, effect with { Strength = 0 }) }, mountain, View);
            Assert.Equal(0, MaxDiff(off, zero));
            Assert.True(MaxDiff(normal, off) > 0, $"{preset}/{kind} has no visible effect");
        }
    }

    [Fact]
    public void Shape_maps_are_cached_and_up_and_top_are_shifted_before_placement()
    {
        using var source = Mountain();
        var type = typeof(Map.Rendering.Styles.StyleRenderer).Assembly.GetType("Map.Rendering.Styles.PainterFormMaps")!;
        var get = type.GetMethod("Get", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var maps = get.Invoke(null, [source, false])!;
        Assert.Same(maps, get.Invoke(null, [source, false]));
        var features = (SKImage)maps.GetType().GetProperty("Features", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(maps)!;
        using var fields = SKBitmap.FromImage(features);
        // At the top of this cone the down-shifted silhouette is covered, while above it is empty.
        var top = fields.GetPixel(384, 94);
        var foot = fields.GetPixel(384, 670);
        Assert.True(top.Green > foot.Green, $"up at top/foot: {top}/{foot}");
        Assert.True(top.Blue > foot.Blue, $"top at top/foot: {top}/{foot}");
    }

    private static MapDocument PainterScene(MapStylePreset preset)
    {
        var scene = Scene(MapStyle.For(preset));
        var tiles = TileMap.Empty(1);
        for (var tx = 0; tx < 2; tx++)
        for (var ty = 0; ty < 2; ty++)
        {
            var bytes = new byte[TileMap.TileSize*TileMap.TileSize];
            for (var y = 0; y < TileMap.TileSize; y++)
            for (var x = 0; x < TileMap.TileSize; x++)
            {
                var dx = (x+tx*256-256)/220.0; var dy = (y+ty*256-192)/165.0;
                bytes[y*256+x] = dx*dx+dy*dy < 1 ? (byte)255 : (byte)0;
            }
            tiles = tiles.With(new TileKey(tx,ty),bytes);
        }
        return scene with { Layers = [scene.Layers[0],new LandLayer(LayerId.New(),"Land",true,false,1,tiles,new LandEdgeStyle()),scene.Layers[1]] };
    }

    [Fact]
    public void A_foreground_peak_keeps_its_up_field_when_a_larger_mountain_overlaps_it()
    {
        using var source = Mountain();
        var doc = Scene(null);
        SceneObject Stamp(double x,double y,double size) => new(ObjectId.New(),SceneObjectKind.Stamp,"Mountain",
            x,y,size,size,0,"#FFFFFFFF","#00000000",0,true,false) { Stamp = new StampStyle(Sha) };
        var front = Stamp(100,60,200);
        var rear = Stamp(0,-270,400);
        var build = typeof(Map.Rendering.Styles.StyleRenderer).GetMethod("BuildPainterBuffers",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        SKColor Field(params SceneObject[] objects)
        {
            var map = doc with { Layers = [doc.Layers[0],((ObjectLayer)doc.Layers[1]) with { Objects = objects }] };
            Action<SKCanvas,MapDocument,RenderOptions> draw = (canvas,document,options) => SceneRenderer.Render(canvas,document,options);
            using var buffers = (IDisposable)build.Invoke(null,[map,new RenderOptions { Stamps = new Images(source) },
                null,SKMatrix.Identity,512,384,1f,View,draw])!;
            var image = (SKImage)buffers.GetType().GetProperty("Features")!.GetValue(buffers)!;
            using var pixels = SKBitmap.FromImage(image);
            return pixels.GetPixel(200,110);
        }
        var alone = Field(front); var overlap = Field(rear,front);
        Assert.True(alone.Green > 250);
        Assert.InRange(Math.Abs(alone.Green-overlap.Green),0,1);
        Assert.InRange(Math.Abs(alone.Blue-overlap.Blue),0,1);
    }
}
