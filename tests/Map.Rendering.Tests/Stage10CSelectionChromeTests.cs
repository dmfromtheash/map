using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// Stage 10C: a multi-selection draws one shared frame that owns the handles, thin rings on the
/// members, and an optional marquee - and none of that chrome exists on an export/thumbnail pass.
/// </summary>
public sealed class Stage10CSelectionChromeTests
{
    private static readonly SKColor Background = new(255, 255, 255, 255);

    private static (MapDocument Document, SceneObject A, SceneObject B) TwoObjects()
    {
        var a = new SceneObject(
            ObjectId.New(), SceneObjectKind.Rectangle, "A", 40, 40, 40, 30, 0,
            "#FFFFFFFF", "#00000000", 0, true, false);
        var b = new SceneObject(
            ObjectId.New(), SceneObjectKind.Rectangle, "B", 120, 100, 40, 30, 0,
            "#FFFFFFFF", "#00000000", 0, true, false);
        var document = new MapDocument(
            DocumentId.New(), "two", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(200, 200, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(LayerId.New(), "objects", true, false, 1, [a, b]),
            ]);
        return (document, a, b);
    }

    private static SKBitmap Render(MapDocument document, RenderOptions options)
    {
        var surface = SKSurface.Create(new SKImageInfo(200, 200))!;
        surface.Canvas.Clear(Background);
        SceneRenderer.Render(surface.Canvas, document, options);
        using var snapshot = surface.Snapshot();
        var bitmap = SKBitmap.FromImage(snapshot);
        surface.Dispose();
        return bitmap;
    }

    private static bool IsSelectionColour(SKColor pixel) =>
        pixel.Red > 200 && pixel.Green > 150 && pixel.Blue < 160;

    /// <summary>Anti-aliased thin lines need a small window, not a single exact pixel.</summary>
    private static bool AnySelectionColourNear(SKBitmap bitmap, int x, int y)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (IsSelectionColour(bitmap.GetPixel(x + dx, y + dy)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    [Fact]
    public void Shared_frame_and_rotation_handle_belong_to_the_whole_selection()
    {
        var (document, a, b) = TwoObjects();
        var frame = new WorldBounds(40, 40, 160, 130);

        using var bitmap = Render(document, new RenderOptions
        {
            SelectedObjectId = b.Id,
            CoSelectedObjectIds = [a.Id, b.Id],
            SelectionFrame = frame,
        });

        // Frame top edge at x=110: clear of both objects' edges and of every frame handle.
        Assert.True(AnySelectionColourNear(bitmap, 110, 40),
            "The shared frame's top edge is not drawn.");

        // The rotation handle sits above the FRAME centre, not above the primary object.
        var handle = bitmap.GetPixel(100, 40 - 28);
        Assert.True(IsSelectionColour(handle) || handle.Red > 230 && handle.Green > 230 && handle.Blue > 230,
            "The rotation handle does not belong to the shared frame.");
    }

    [Fact]
    public void Every_member_gets_a_thin_ring_when_the_frame_owns_the_handles()
    {
        var (document, a, b) = TwoObjects();

        using var bitmap = Render(document, new RenderOptions
        {
            SelectedObjectId = b.Id,
            CoSelectedObjectIds = [a.Id, b.Id],
            SelectionFrame = new WorldBounds(40, 40, 160, 130),
        });

        // A's own top edge (member ring) and B's own top edge (primary also ringed, no gizmo).
        Assert.True(AnySelectionColourNear(bitmap, 60, 40), "Member A has no ring.");
        Assert.True(AnySelectionColourNear(bitmap, 140, 100), "Member B has no ring.");
    }

    [Fact]
    public void Marquee_rectangle_is_drawn_when_requested()
    {
        var (document, _, _) = TwoObjects();

        using var bitmap = Render(document, new RenderOptions
        {
            Marquee = new WorldBounds(20, 20, 180, 180),
        });

        var found = false;
        for (var x = 20; x <= 180 && !found; x++)
        {
            found = !bitmap.GetPixel(x, 20).Equals(Background);
        }

        Assert.True(found, "No marquee pixels drawn along its top edge.");
    }

    [Fact]
    public void An_export_pass_without_chrome_options_stays_clean()
    {
        var (document, _, _) = TwoObjects();

        using var bitmap = Render(document, new RenderOptions { IsExport = true });

        // Where the frame edge and rotation handle would be: pure background.
        Assert.Equal(Background, bitmap.GetPixel(100, 40));
        Assert.Equal(Background, bitmap.GetPixel(100, 12));
        Assert.Equal(Background, bitmap.GetPixel(30, 20));
    }
}
