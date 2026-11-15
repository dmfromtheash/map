using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Rendering.Tests;

[CollectionDefinition(nameof(KeptLayerSupportCounters), DisableParallelization = true)]
public sealed class KeptLayerSupportCounters;

/// <summary>
/// Live place-10 trace (PC, 2M objects, 2026-11-15): the first frame after several placements counted every object
/// of the layer (LayerImageSupport, 192 ms) because only the last drawn revision was known. Revisions between two
/// frames are now derived through their deltas.
/// </summary>
[Collection(nameof(KeptLayerSupportCounters))]
public sealed class KeptLayerSupportDeltaTests
{
    private static readonly string Hash = new('b', 64);

    private static SceneObject Stamp(int i) => new(ObjectId.New(), SceneObjectKind.Stamp, "Ассет " + i,
        i % 1000 * 40, i / 1000 * 40, 32, 24, 0, "#FFFFFFFF", "#00000000", 0, true, false)
    { Stamp = new StampStyle(Hash) };

    [Fact]
    public void Ten_placements_between_two_frames_do_not_count_the_whole_layer_again()
    {
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, Enumerable.Range(0, 200_000).Select(Stamp).ToArray());
        var document = new MapDocument(DocumentId.New(), "Support", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(65536, 65536, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), layer]);
        Assert.True(SceneRenderer.KeptLayerSupported(document.Layers.OfType<ObjectLayer>().Single())); // the frame before the edits
        var before = SceneRenderer.KeptLayerSupportFullCounts;

        for (var k = 0; k < 10; k++)
            document = DocumentEditor.AppendObjects(document, layer.Id, [Stamp(300_000 + k)]);

        Assert.True(SceneRenderer.KeptLayerSupported(document.Layers.OfType<ObjectLayer>().Single())); // the next frame
        Assert.Equal(before, SceneRenderer.KeptLayerSupportFullCounts);
    }
}
