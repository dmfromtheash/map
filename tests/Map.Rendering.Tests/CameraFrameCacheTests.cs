using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class CameraFrameCacheTests
{
    private static SKImage Mountain()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        return SKImage.FromEncodedData(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"));
    }

    [Fact]
    public void Detail_and_overview_cover_a_pan_without_double_blending_transparency()
    {
        using var mountain = Mountain();
        using var cache = new CameraFrameCache();
        var page = new SKRect(0, 0, 128, 128);
        using var overview = SKSurface.Create(new SKImageInfo(128, 128));
        overview.Canvas.Clear(SKColors.Transparent);
        using var alpha = new SKPaint { Color = SKColors.White.WithAlpha(128) };
        overview.Canvas.DrawImage(mountain, page, new SKSamplingOptions(SKFilterMode.Linear), alpha);
        cache.SetIdentity(new object(), 0);
        Assert.True(cache.Capture(overview, overview.Canvas, page));
        using var detail = SKSurface.Create(new SKImageInfo(64, 64));
        detail.Canvas.Clear(SKColors.Transparent);
        detail.Canvas.Translate(-32, -32);
        detail.Canvas.DrawImage(mountain, page, new SKSamplingOptions(SKFilterMode.Linear), alpha);
        Assert.True(cache.Capture(detail, detail.Canvas, page));
        Assert.Equal(2, cache.Count);

        using var result = SKSurface.Create(new SKImageInfo(128, 128));
        result.Canvas.Clear(SKColors.Transparent);
        result.Canvas.Translate(-3, -5);
        Assert.True(cache.TryDraw(result.Canvas, new SKRect(3, 5, 131, 133), page));
        using var expected = SKSurface.Create(new SKImageInfo(128, 128));
        expected.Canvas.Clear(SKColors.Transparent);
        expected.Canvas.Translate(-3, -5);
        expected.Canvas.DrawImage(mountain, page, new SKSamplingOptions(SKFilterMode.Linear), alpha);
        using var actualPixels = SKBitmap.FromImage(result.Snapshot());
        using var expectedPixels = SKBitmap.FromImage(expected.Snapshot());
        Assert.Equal(expectedPixels.Bytes, actualPixels.Bytes);
    }

    [Fact]
    public void Uncovered_view_returns_false_without_drawing_and_identity_changes_drop_frames()
    {
        using var mountain = Mountain();
        using var cache = new CameraFrameCache();
        using var source = SKSurface.Create(new SKImageInfo(64, 64));
        var document = new object();
        cache.SetIdentity(document, 1, "night");
        source.Canvas.DrawImage(mountain, new SKRect(0, 0, 64, 64));
        Assert.True(cache.Capture(source, source.Canvas, new SKRect(0, 0, 128, 128)));
        using var result = SKSurface.Create(new SKImageInfo(128, 128));
        result.Canvas.Clear(SKColors.Transparent);
        Assert.False(cache.TryDraw(result.Canvas, new SKRect(0, 0, 128, 128), new SKRect(0, 0, 128, 128)));
        using var pixels = SKBitmap.FromImage(result.Snapshot());
        Assert.All(pixels.Bytes, value => Assert.Equal(0, value));
        cache.SetIdentity(document, 1, "night");
        Assert.Equal(1, cache.Count);
        cache.SetIdentity(document, 1, "watercolor");
        Assert.Equal(0, cache.Count);
        Assert.True(cache.Capture(source, source.Canvas, new SKRect(0, 0, 64, 64)));
        cache.SetIdentity(document, 2, "watercolor");
        Assert.Equal(0, cache.Count);
        Assert.True(cache.Capture(source, source.Canvas, new SKRect(0, 0, 64, 64)));
        cache.SetIdentity(new object(), 2, "watercolor");
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void A_restored_missing_asset_invalidates_camera_pixels_without_a_document_edit()
    {
        using var mountain = Mountain();
        using var cache = new CameraFrameCache();
        using var source = SKSurface.Create(new SKImageInfo(64, 64));
        var document = new object();
        var page = new SKRect(0, 0, 64, 64);
        cache.SetIdentity(document, 1, 0L);
        source.Canvas.Clear(SKColors.Transparent);
        Assert.True(cache.Capture(source, source.Canvas, page));
        cache.SetIdentity(document, 1, 1L);
        using var target = SKSurface.Create(new SKImageInfo(64, 64));
        Assert.False(cache.TryDraw(target.Canvas, page, page));
        source.Canvas.DrawImage(mountain, page);
        Assert.True(cache.Capture(source, source.Canvas, page));
        Assert.True(cache.TryDraw(target.Canvas, page, page));
        using var expected = SKBitmap.FromImage(source.Snapshot());
        using var actual = SKBitmap.FromImage(target.Snapshot());
        Assert.Equal(expected.Bytes, actual.Bytes);
    }

    [Fact]
    public void Oversized_capture_is_rejected_before_allocating_an_image()
    {
        using var cache = new CameraFrameCache(1024);
        using var source = SKSurface.Create(new SKImageInfo(64, 64));
        Assert.False(cache.Capture(source, source.Canvas, new SKRect(0, 0, 64, 64)));
        Assert.Equal(0, cache.Bytes);
        Assert.Equal(1, cache.CapturesRejectedByBudget);
    }

    [Theory]
    [InlineData(2560, 1600)]
    [InlineData(3840, 2160)]
    public void Large_windows_retain_a_frame_and_charge_the_actual_color_format(int width, int height)
    {
        using var cache = new CameraFrameCache();
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888));
        surface.Canvas.Clear(SKColors.Transparent);
        Assert.True(cache.Capture(surface, surface.Canvas, new SKRect(0, 0, width, height)));
        Assert.Equal((long)width * height * 4, cache.Bytes);
        Assert.Equal(0, cache.CapturesRejectedByBudget);
    }

    [Theory]
    [InlineData(-128)] // partly visible page at 25%
    [InlineData(512)] // the whole page is outside the viewport
    public void Panning_past_the_page_preserves_the_parent_painted_surround(int offset)
    {
        using var mountain = Mountain();
        using var cache = new CameraFrameCache();
        var page = new SKRect(0, 0, 256, 256);
        using var source = SKSurface.Create(new SKImageInfo(64, 64));
        source.Canvas.Scale(.25f);
        source.Canvas.Clear(SKColors.Transparent);
        source.Canvas.DrawImage(mountain, page);
        Assert.True(cache.Capture(source, source.Canvas, page));
        using var actual = SKSurface.Create(new SKImageInfo(128, 128));
        using var expected = SKSurface.Create(new SKImageInfo(128, 128));
        foreach (var surface in new[] { actual, expected })
        {
            // The actual editor paints its bitmap surround before the canvas custom operation.
            surface.Canvas.Clear(SKColors.White);
            surface.Canvas.DrawImage(mountain, new SKRect(0, 0, 128, 128));
            surface.Canvas.Scale(.25f);
            surface.Canvas.Translate(-offset, 0);
        }
        Assert.True(cache.TryDraw(actual.Canvas, new SKRect(offset, 0, offset + 512, 512), page));
        expected.Canvas.ClipRect(page);
        expected.Canvas.DrawImage(mountain, page);
        using var a = SKBitmap.FromImage(actual.Snapshot());
        using var b = SKBitmap.FromImage(expected.Snapshot());
        Assert.Equal(b.Bytes, a.Bytes);
    }
}
