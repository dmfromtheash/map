using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class RiverRoadResolutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sparse_resolution_matches_original_order_duplicates_and_preview_ownership(bool owner)
    {
        var river = new River(); var road = new Road();
        var river2 = river with { Id = Guid.NewGuid() }; var road2 = road with { Id = Guid.NewGuid() };
        var layer = new ObjectLayer(LayerId.New(), "features", true, false, 1, [])
            { Rivers = [river, river2, river], Roads = [road, road2, road] };
        River[] rivers = [river2 with { Name = "first" }, river2 with { Name = "ignored" }, new River()];
        Road[] roads = [road2 with { Name = "first" }, road2 with { Name = "ignored" }, new Road()];
        rivers = [.. rivers, rivers[^1] with { Name = "duplicate extra" }];
        roads = [.. roads, roads[^1] with { Name = "duplicate extra" }];
        var options = new RenderOptions { RiverOverrides = rivers, RoadOverrides = roads,
            RiverPreviewLayerId = owner ? layer.Id : LayerId.New(), RoadPreviewLayerId = owner ? layer.Id : LayerId.New() };
        var expectedRivers = layer.Rivers.Select(r => rivers.FirstOrDefault(o => o.Id == r.Id) ?? r)
            .Concat(owner ? rivers.Where(o => layer.Rivers.All(r => r.Id != o.Id)) : []).ToArray();
        var expectedRoads = layer.Roads.Select(r => roads.FirstOrDefault(o => o.Id == r.Id) ?? r)
            .Concat(owner ? roads.Where(o => layer.Roads.All(r => r.Id != o.Id)) : []).ToArray();
        var resolvedRivers = RiverRenderer.Resolve(layer with { Name = "copy" }, options);
        var resolvedRoads = RoadRenderer.Resolve(layer with { Name = "copy" }, options);
        Assert.Equal(expectedRivers.Length, resolvedRivers.Count); Assert.Equal(expectedRoads.Length, resolvedRoads.Count);
        for (var i = 0; i < resolvedRivers.Count; i++) Assert.Same(expectedRivers[i], resolvedRivers[i]);
        for (var i = 0; i < resolvedRoads.Count; i++) Assert.Same(expectedRoads[i], resolvedRoads[i]);
        Assert.Equal(expectedRivers, resolvedRivers.ToArray()); Assert.Equal(expectedRoads, resolvedRoads.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => resolvedRivers[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => resolvedRoads[resolvedRoads.Count]);
    }

    [Fact]
    public void Each_draft_has_its_own_substitution_and_keeps_unchanged_geometry()
    {
        var river = new River { Channels = [RiverEditor.NewChannel([new(10, 20), new(80, 20)], 12)] };
        var road = RoadEditor.NewRoad([new(10, 60), new(80, 60)], 12, seed: 7);
        var layer = new ObjectLayer(LayerId.New(), "features", true, false, 1, [])
            { Rivers = [river, river with { Id = Guid.NewGuid() }], Roads = [road, road with { Id = Guid.NewGuid() }] };
        var riverGeometry = RiverGeometry.Evaluate(river); var roadGeometry = RoadGeometry.Evaluate(road);
        for (var i = 1; i <= 3; i++)
        {
            var changedRiver = RiverEditor.Translate(layer.Rivers[1], i, 0);
            var changedRoad = RoadEditor.Translate(layer.Roads[1], i, 0);
            var options = new RenderOptions { RiverOverrides = [changedRiver], RoadOverrides = [changedRoad],
                RiverPreviewLayerId = layer.Id, RoadPreviewLayerId = layer.Id };
            var rivers = RiverRenderer.Resolve(layer, options); var roads = RoadRenderer.Resolve(layer, options);
            Assert.Same(changedRiver, rivers[1]); Assert.Same(changedRoad, roads[1]);
            Assert.Same(riverGeometry, RiverGeometry.Evaluate(rivers[0]));
            Assert.Same(roadGeometry, RoadGeometry.Evaluate(roads[0]));
        }
    }

    [Fact]
    public void Live_draft_pixels_equal_the_materialized_document_in_explicit_surface_order()
    {
        var scene = Stage20RoadRenderingTests.Settlement();
        var layer = (ObjectLayer)scene.Document.FindLayer(scene.ObjectsId)!;
        var river = RiverEditor.Translate(layer.Rivers[0], 7, 0);
        var road = RoadEditor.Translate(layer.Roads[0], 0, 9);
        var ordered = layer with { SurfaceOrder = [layer.Roads[0].Id, layer.Rivers[0].Id, layer.Roads[1].Id] };
        var document = scene.Document.WithLayer(layer.Id, _ => ordered);
        var materialized = document.WithLayer(layer.Id, _ => ordered with
            { Rivers = ordered.Rivers.SetItem(0, river), Roads = ordered.Roads.SetItem(0, road) });
        var options = new RenderOptions { ViewScale = 1, VisibleWorld = new(0, 0, 1024, 768) };
        using var preview = SKSurface.Create(new SKImageInfo(1024, 768));
        using var baked = SKSurface.Create(new SKImageInfo(1024, 768));
        preview.Canvas.Clear(SKColors.Transparent); baked.Canvas.Clear(SKColors.Transparent);
        SceneRenderer.Render(preview.Canvas, document, new RenderOptions { ViewScale = 1, VisibleWorld = options.VisibleWorld,
            RiverOverrides = [river], RoadOverrides = [road],
            RiverPreviewLayerId = layer.Id, RoadPreviewLayerId = layer.Id });
        SceneRenderer.Render(baked.Canvas, materialized, options);
        using var a = preview.Snapshot(); using var b = baked.Snapshot();
        using var pa = a.PeekPixels(); using var pb = b.PeekPixels();
        Assert.True(pa.GetPixelSpan().SequenceEqual(pb.GetPixelSpan()));
    }
}
