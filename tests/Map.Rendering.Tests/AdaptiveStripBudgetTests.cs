using Map.Core.Documents;

namespace Map.Rendering.Tests;

[Collection("OverviewExactLayers")]
public sealed class AdaptiveStripBudgetTests
{
    [Theory]
    [InlineData(6, 0)]
    [InlineData(14, 17)]
    [InlineData(32, 31)]
    public void Extra_strip_workers_preserve_every_pixel_of_the_legacy_partition_layout(int workers, int rotation)
    {
        var partitions = SceneRenderer.ExactLayerStripPartitions;
        var concurrency = SceneRenderer.ExactLayerStripConcurrencyOverride;
        try
        {
            SceneRenderer.ExactLayerStripPartitions = 4;
            var document = ColdLibraryWholeMapTests.WholeMapView.Document(200_000);
            if (rotation != 0)
            {
                var layer = (ObjectLayer)document.Layers[1];
                var objects = layer.Objects.Select(o => new SceneObject(o.Id, o.Kind, o.Name, o.X, o.Y, o.Width, o.Height,
                    rotation, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = o.Stamp }).ToArray();
                document = document.WithLayers([document.Layers[0], new ObjectLayer(layer.Id, layer.Name, true, false, 1, objects)]);
            }
            byte[] Render(int count)
            {
                SceneRenderer.ExactLayerStripConcurrencyOverride = count;
                using var view = new ColdLibraryWholeMapTests.WholeMapView(200_000, ready: true, document: document);
                view.Settle();
                return view.Pixels();
            }
            var reference = Render(1);
            var beforeRasters = Interlocked.Read(ref SceneRenderer.ExactLayerStripRasters);
            var beforeBands = Interlocked.Read(ref SceneRenderer.ExactLayerSubBandRasters);
            var parallel = Render(workers);
            var rasters = Interlocked.Read(ref SceneRenderer.ExactLayerStripRasters) - beforeRasters;
            var bands = Interlocked.Read(ref SceneRenderer.ExactLayerSubBandRasters) - beforeBands;
            Assert.True(rasters > 0 && bands > rasters * 4, "the extra workers did not receive additional bands");
            Assert.Equal(reference, parallel);
        }
        finally
        {
            SceneRenderer.ExactLayerStripPartitions = partitions;
            SceneRenderer.ExactLayerStripConcurrencyOverride = concurrency;
        }
    }
}
