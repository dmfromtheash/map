using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// The kept camera frame holds the scene without the selection and the brush; they are drawn over
/// it (SceneRenderer.DrawEditorOverlay), so a pan with a selection reuses the frame and the settled
/// frame stays pixel-identical.
/// </summary>
public sealed class CameraFrameOverlayTests
{
    private const string Sha = "2222222222222222222222222222222222222222222222222222222222222222";

    private static SKImage Mountain()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        return SKImage.FromEncodedData(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"));
    }

    private static (MapDocument Document, SceneObject A, SceneObject B) Scene(MapLighting? lighting)
    {
        var a = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора", 40, 50, 120, 120, 0,
            "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(Sha) };
        var b = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора", 170, 90, 90, 90, 0,
            "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(Sha) };
        var document = new MapDocument(DocumentId.New(), "overlay", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(300, 220, "px", "#FFB6AE86"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFB6AE86"),
             new ObjectLayer(LayerId.New(), "Горы", true, false, 1, [a, b])]) { Lighting = lighting };
        return (document, a, b);
    }

    private static IEnumerable<RenderOptions> Decorated(SceneObject a, SceneObject b, SKImage mountain, bool defer)
    {
        var images = new Images(mountain);
        var boundary = new CanvasBoundaryOverlay(new WorldBounds(0, 0, 300, 220), new WorldBounds(-10, -10, 290, 200), null);
        yield return new RenderOptions { Stamps = images, ViewScale = 1.5, SelectedObjectId = a.Id, DeferEditorOverlay = defer };
        yield return new RenderOptions
        {
            Stamps = images, ViewScale = 1.5, SelectedObjectId = a.Id, CoSelectedObjectIds = [a.Id, b.Id],
            SelectionFrame = new WorldBounds(40, 50, 260, 180), DeferEditorOverlay = defer,
        };
        yield return new RenderOptions
        {
            Stamps = images, ViewScale = 1.5, BrushCursor = new BrushCursorPreview(150, 110, 60, 0, false),
            Marquee = new WorldBounds(20, 20, 120, 90), BoundaryOverlay = boundary, DeferEditorOverlay = defer,
        };
    }

    private static SKBitmap Draw(MapDocument document, RenderOptions options, bool overlay, Action<SKSurface, SKCanvas>? between = null)
    {
        var info = new SKImageInfo(450, 330);
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(1.5f);
        SceneRenderer.Render(surface.Canvas, document, options);
        between?.Invoke(surface, surface.Canvas);
        if (overlay) SceneRenderer.DrawEditorOverlay(surface.Canvas, document, options);
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deferred_decorations_drawn_afterwards_give_the_same_pixels_and_stay_out_of_the_scene(bool lit)
    {
        using var mountain = Mountain();
        var (document, a, b) = Scene(lit ? new MapLighting { TimeOfDay = 0.6 } : null);
        // The export waits for the stamps' light maps; afterwards every canvas pass below sees them ready.
        using (Draw(document, new RenderOptions { Stamps = new Images(mountain), ViewScale = 1.5, IsExport = true }, overlay: false)) { }
        using var bare = Draw(document, new RenderOptions { Stamps = new Images(mountain), ViewScale = 1.5 }, overlay: false);
        var classic = Decorated(a, b, mountain, defer: false).ToList();
        var deferred = Decorated(a, b, mountain, defer: true).ToList();
        for (var i = 0; i < classic.Count; i++)
        {
            using var expected = Draw(document, classic[i], overlay: false);
            using var actual = Draw(document, deferred[i], overlay: true);
            Assert.Equal(expected.Bytes, actual.Bytes);
            // The deferred pass itself is the undecorated scene: that is what a camera frame keeps.
            using var scene = Draw(document, deferred[i], overlay: false);
            Assert.Equal(bare.Bytes, scene.Bytes);
            Assert.NotEqual(expected.Bytes, scene.Bytes);
        }
    }

    [Fact]
    public void A_kept_camera_frame_with_the_selection_drawn_over_it_equals_the_full_frame()
    {
        using var mountain = Mountain();
        var (document, a, _) = Scene(null);
        var page = new SKRect(0, 0, 300, 220);
        var options = new RenderOptions
        {
            Stamps = new Images(mountain), ViewScale = 1.5, SelectedObjectId = a.Id,
            BrushCursor = new BrushCursorPreview(150, 110, 40, 0, false), DeferEditorOverlay = true,
        };
        using var cache = new CameraFrameCache();
        cache.SetIdentity(document, 0, CameraFramePolicy.KeyOf(a.Id, [a.Id]));
        using var settled = Draw(document, options, overlay: true, between: (surface, canvas) => Assert.True(cache.Capture(surface, canvas, page)));
        Assert.Equal(1, cache.Count);

        // The next frame of the same view comes from the kept frame plus the decorations.
        var info = new SKImageInfo(450, 330);
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(1.5f);
        Assert.True(cache.TryDraw(surface.Canvas, new SKRect(0, 0, 300, 220), page));
        SceneRenderer.DrawEditorOverlay(surface.Canvas, document, options);
        using var image = surface.Snapshot();
        using var reused = SKBitmap.FromImage(image);
        using var classic = Draw(document, new RenderOptions
        {
            Stamps = new Images(mountain), ViewScale = 1.5, SelectedObjectId = a.Id,
            BrushCursor = new BrushCursorPreview(150, 110, 40, 0, false),
        }, overlay: false);
        Assert.Equal(classic.Bytes, settled.Bytes);
        Assert.Equal(classic.Bytes, reused.Bytes);
    }

    [Fact]
    public void Selection_and_brush_keep_camera_frames_but_scene_changes_do_not()
    {
        var idle = new CameraFramePolicy.Inputs(Enabled: true, GpuSurface: true, LiveObjectPreview: false, StrokePreview: false,
            CanvasWorkflowOpen: false, Marquee: false, FogStroke: false, HexGesture: false, HexCursor: false,
            SeamlessPlanet: false, SceneOverrides: false, Selection: false, BrushCursor: false);
        Assert.True(CameraFramePolicy.Eligible(idle));
        Assert.True(CameraFramePolicy.Eligible(idle with { Selection = true }));
        Assert.True(CameraFramePolicy.Eligible(idle with { BrushCursor = true }));
        Assert.True(CameraFramePolicy.Eligible(idle with { Selection = true, BrushCursor = true }));
        Assert.False(CameraFramePolicy.Eligible(idle with { Enabled = false }));
        Assert.False(CameraFramePolicy.Eligible(idle with { GpuSurface = false }));
        Assert.False(CameraFramePolicy.Eligible(idle with { Selection = true, LiveObjectPreview = true }));
        Assert.False(CameraFramePolicy.Eligible(idle with { BrushCursor = true, StrokePreview = true }));
        Assert.False(CameraFramePolicy.Eligible(idle with { Selection = true, SceneOverrides = true }));
        Assert.False(CameraFramePolicy.Eligible(idle with { CanvasWorkflowOpen = true }));
        Assert.False(CameraFramePolicy.Eligible(idle with { Marquee = true }));
        Assert.False(CameraFramePolicy.Eligible(idle with { FogStroke = true }));
        Assert.False(CameraFramePolicy.Eligible(idle with { HexGesture = true }));
        Assert.False(CameraFramePolicy.Eligible(idle with { HexCursor = true }));
        Assert.False(CameraFramePolicy.Eligible(idle with { SeamlessPlanet = true }));
    }

    [Fact]
    public void The_selection_key_is_a_value_and_a_changed_selection_drops_the_kept_frames()
    {
        ObjectId a = ObjectId.New(), b = ObjectId.New();
        var list = new List<ObjectId> { a, b };
        var before = CameraFramePolicy.KeyOf(b, list);
        Assert.Equal(before, CameraFramePolicy.KeyOf(b, [a, b]));
        list.RemoveAt(1);
        Assert.NotEqual(before, CameraFramePolicy.KeyOf(a, list));
        Assert.NotEqual(CameraFramePolicy.KeyOf(null, []), CameraFramePolicy.KeyOf(a, [a]));

        using var cache = new CameraFrameCache();
        using var surface = SKSurface.Create(new SKImageInfo(16, 16))!;
        var document = new object();
        cache.SetIdentity(document, 0, before);
        Assert.True(cache.Capture(surface, surface.Canvas, new SKRect(0, 0, 16, 16)));
        cache.SetIdentity(document, 0, CameraFramePolicy.KeyOf(b, [a, b]));
        Assert.Equal(1, cache.Count);
        cache.SetIdentity(document, 0, CameraFramePolicy.KeyOf(a, [a]));
        Assert.Equal(0, cache.Count);
    }

    private sealed class Images(SKImage image) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => image;
    }
}
