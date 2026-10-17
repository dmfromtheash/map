using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>«Комната» draws floor, wall and caps; an ordinary shape draws exactly as before.</summary>
public sealed class RoomRenderingTests
{
    private const string Floor = "#FFC9BFA8", Wall = "#FF3A342E";
    private static readonly string CapHash = new('e', 64);

    [Fact]
    public void Floor_wall_and_caps_are_three_layers_in_order()
    {
        using var images = new Images();
        using var bitmap = Render(Room(new RoomStyle { CornerRadius = 0, CapAssetSha256 = CapHash, CapScale = 2, CapShadow = false }), images);

        Assert.Equal(SKColor.Parse(Floor), bitmap.GetPixel(150, 150));       // floor inside
        Assert.Equal(SKColor.Parse(Wall), bitmap.GetPixel(150, 100));        // wall on the edge
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(100, 100));    // red cap over the corner
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(200, 200));
        Assert.Equal(new SKColor(255, 255, 255), bitmap.GetPixel(20, 20));   // background outside
    }

    [Fact]
    public void Rounded_corner_leaves_the_square_corner_empty_and_a_missing_cap_is_counted()
    {
        var stats = new RenderStats();
        using var bitmap = Render(Room(new RoomStyle { CornerRadius = 40, CapAssetSha256 = CapHash }), images: null, stats);
        Assert.Equal(new SKColor(255, 255, 255), bitmap.GetPixel(103, 103));
        Assert.Equal(SKColor.Parse(Floor), bitmap.GetPixel(150, 150));
        Assert.True(stats.MissingAssets > 0);
    }

    [Fact]
    public void An_ordinary_shape_renders_pixel_for_pixel_as_before()
    {
        var plain = Room(null);
        using var a = Render(plain, null);
        using var b = Render(plain with { Room = null }, null);
        Assert.Equal(a.Bytes, b.Bytes);
        Assert.Equal(SKColor.Parse(Wall), a.GetPixel(100, 150));
    }

    private static SceneObject Room(RoomStyle? room) =>
        VectorGeometry.CreateShape(ObjectId.New(), "Комната", ShapePrimitiveKind.Polygon, new WorldBounds(100, 100, 200, 200),
            new ShapeFillStyle { Color = Floor, Opacity = 1 },
            new VectorStrokeStyle { Color = Wall, Width = 10, NodeCap = VectorCap.Butt }) with { Room = room };

    private static SKBitmap Render(SceneObject room, IStampImageSource? images, RenderStats? stats = null)
    {
        var document = new MapDocument(DocumentId.New(), "room", DateTimeOffset.UnixEpoch, new CanvasDefinition(300, 300, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "bg", true, false, 1, "#FFFFFFFF"), new ObjectLayer(LayerId.New(), "rooms", true, false, 1, [room])]);
        var info = new SKImageInfo(300, 300);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true, Stamps = images, Stats = stats });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    private sealed class Images : IStampImageSource, IDisposable
    {
        private readonly SKImage _red;
        public Images()
        {
            using var surface = SKSurface.Create(new SKImageInfo(16, 16))!;
            surface.Canvas.Clear(new SKColor(255, 0, 0));
            _red = surface.Snapshot();
        }
        public SKImage? TryGetImage(string sha256) => sha256 == CapHash ? _red : null;
        public void Dispose() => _red.Dispose();
    }
}
