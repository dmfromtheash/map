using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// The two Stage 19 rendering corrections: a hidden river draws nothing, and the land coverage a
/// river was carved out of is reused instead of being rasterized again on every frame.
/// </summary>
public sealed class Stage19WaterRenderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Water_obeys_owner_opacity_and_mask_and_a_mask_edit_invalidates_carving(bool illustrated)
    {
        var (document, _, river) = Scene(true);
        var owner = document.Layers.OfType<ObjectLayer>().Single();
        if (illustrated)
        {
            owner = owner with { Rivers = [river with { Style = RiverStyle.Illustrated() }] };
            document = document.WithLayer(owner.Id, _ => owner);
        }
        using var cache = new CarvedLandCache();
        using var opaque = Render(document, cache);
        var fadedDocument = document.WithLayer(owner.Id, layer => layer with { Opacity = .25 });
        using var faded = Render(fadedDocument, cache);
        Assert.NotEqual(opaque.GetPixel(120, 105), faded.GetPixel(120, 105));
        var hiddenDocument = document.WithLayer(owner.Id, layer => layer.WithMask(new LayerMask(
            TileMap.Empty(1).With(new TileKey(0, 0), new byte[TileMap.TileSize * TileMap.TileSize]), true)));
        using var masked = Render(hiddenDocument, cache);
        using var absent = Render(document.WithLayer(owner.Id, layer => ((ObjectLayer)layer) with { Rivers = [] }));
        Assert.Equal(3, cache.Misses);
        Assert.Equal(absent.Pixels, masked.Pixels);
    }

    [Fact]
    public void Interleaved_water_styles_keep_their_stack_order()
    {
        var (document, landId, river) = Scene(true);
        var owner = document.Layers.OfType<ObjectLayer>().Single();
        var plain = river.Style with { CutsLand = false, ClipToLand = false, BankWidth = 0,
            BankShadowOpacity = 0, ShallowWidth = 0, HighlightOpacity = 0, MouthFade = 0, TextureId = null };
        var a = river with { Style = plain with { WaterColor = "#FFFF0000" } };
        var b = river with { Id = Guid.NewGuid(), Style = plain with { WaterColor = "#FF00FF00", BankWidth = 1 } };
        var c = river with { Id = Guid.NewGuid(), Style = plain with { WaterColor = "#FF0000FF" } };
        using var rendered = Render(document.WithLayer(owner.Id, layer => ((ObjectLayer)layer) with { Rivers = [a, b, c] }));
        Assert.Equal(SKColors.Blue, rendered.GetPixel(120, 105));
    }

    private static (MapDocument Document, LayerId LandId, River River) Scene(bool visible)
    {
        var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var land = new LandLayer(LayerId.New(), "Земля", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), coverage), new LandEdgeStyle { FillColor = "#FF3C8C4C" });
        var river = new River
        {
            Name = "Река",
            Visible = visible,
            Channels = [RiverEditor.NewChannel([new VectorPoint(10, 128), new VectorPoint(120, 100), new VectorPoint(246, 140)], 30)],
        };
        river.Validate();
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Rivers = [river] };
        var document = new MapDocument(DocumentId.New(), "Вода", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(256, 256, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), land, objects]);
        return (document, land.Id, river);
    }

    private static SKBitmap Render(MapDocument document, CarvedLandCache? cache = null)
    {
        var info = new SKImageInfo(256, 256);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { CarvedLand = cache });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    [Fact]
    public void A_hidden_river_draws_nothing_and_stops_carving_the_land()
    {
        using var shown = Render(Scene(visible: true).Document);
        using var hidden = Render(Scene(visible: false).Document);

        var onCourse = shown.GetPixel(120, 105);
        Assert.True(onCourse.Blue > onCourse.Red, "the visible river paints water over the land");
        var hiddenPixel = hidden.GetPixel(120, 105);
        Assert.True(hiddenPixel.Green > hiddenPixel.Blue, "the hidden river leaves plain land behind");
    }

    [Fact]
    public void The_carved_land_is_reused_while_nothing_changes_and_rebuilt_after_an_edit()
    {
        var (document, _, river) = Scene(visible: true);
        using var cache = new CarvedLandCache();
        using (var first = Render(document, cache)) { }
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0, cache.Hits);

        using (var second = Render(document, cache)) { }
        using (var third = Render(document, cache)) { }
        Assert.Equal(1, cache.Misses);
        Assert.Equal(2, cache.Hits);

        // Moving a node produces a new immutable river, so the entry cannot be reused and the
        // coast is rebuilt: the cache can never show a stale carve.
        var moved = RiverEditor.MoveNode(river, river.Channels[0].Id, 1, 130, 60);
        var layer = document.Layers.OfType<ObjectLayer>().Single();
        var edited = new RiverCommand(layer.Id, river.Id, moved, "move").Apply(document);
        using (var fourth = Render(edited, cache)) { }
        Assert.Equal(2, cache.Misses);
        Assert.Equal(2, cache.Hits);

        // Relief under memory pressure drops the images; the next frame simply builds them again.
        cache.Clear();
        Assert.Equal(0, cache.Count);
        using (var fifth = Render(edited, cache)) { }
        Assert.Equal(3, cache.Misses);
    }

    [Fact]
    public void Reuse_does_not_change_a_single_pixel_of_the_coast()
    {
        // The reuse is an optimisation, not a different picture: the frame a warm cache produces
        // has to be identical to the one built from scratch, carved mouth and all.
        var (document, _, _) = Scene(visible: true);
        using var cache = new CarvedLandCache();
        using var uncached = Render(document);
        using (var warm = Render(document, cache)) { }
        Assert.Equal(1, cache.Misses);
        using var cached = Render(document, cache);
        Assert.Equal(1, cache.Hits);

        for (var y = 0; y < 256; y += 1)
            for (var x = 0; x < 256; x += 1)
                if (uncached.GetPixel(x, y) != cached.GetPixel(x, y))
                    Assert.Fail($"reuse changed the picture at {x},{y}");
    }
}
