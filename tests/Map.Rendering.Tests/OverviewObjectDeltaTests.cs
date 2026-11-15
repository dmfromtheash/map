using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class OverviewObjectDeltaTests
{
    [Fact]
    public void Long_structural_edit_sequence_preserves_draw_order_pixels_and_previous_snapshots()
    {
        var random = new Random(73);
        var list = SceneObjectList.From(Enumerable.Range(0, 1400).Select(i => Item(i)).ToArray());
        var chunk = Cold(list);
        var first = chunk;
        var firstIds = chunk.Objects.Select(o => o.Item.Id).ToArray();
        var colors = new Dictionary<string, SKColor> { [Sha(0)] = SKColors.Red, [Sha(1)] = SKColors.Blue };
        for (var step = 0; step < 1800; step++)
        {
            var at = random.Next(list.Count);
            var old = list[at];
            var source = list;
            if (step % 3 == 0)
            {
                list = list.RemoveRange(at, 1);
                chunk = chunk.Change([(at, old)], [], source, list);
            }
            else if (step % 3 == 1)
            {
                var added = Item(step + 5000);
                list = list.InsertRange(at, [added]);
                chunk = chunk.Change([], [(at, added)], source, list);
            }
            else
            {
                var changed = SceneObjectClone.At(old, random.Next(500), old.Y) with
                    { Sublayer = random.Next(4), Stamp = new StampStyle(Sha(step % 2)) };
                list = list.ReplaceAt(at, changed);
                chunk = chunk.Change([(at, old)], [(at, changed)], source, list);
            }
            if (step % 41 != 0 && step != 1799) continue;
            var expected = Cold(list);
            Assert.Equal(expected.Objects.Select(o => o.Item), chunk.Objects.Select(o => o.Item));
            var palette = new SceneRenderer.OverviewPalette(colors.ToImmutableDictionary());
            Assert.Equal(expected.GetTintRevision(palette), chunk.GetTintRevision(palette));
            var actualVariant = chunk.GetVariant(-28, 1d / 128, colors, 0);
            var referenceVariant = expected.GetVariant(-28, 1d / 128, colors, 0);
            Assert.Equal(referenceVariant.ContentHash, actualVariant.ContentHash);
            Assert.Equal(referenceVariant.ExactObjects, actualVariant.ExactObjects);
            using var a = actualVariant.BuildImage(null, new RenderOptions());
            using var b = referenceVariant.BuildImage(null, new RenderOptions());
            using var actualPixels = SKBitmap.FromImage(a);
            using var expectedPixels = SKBitmap.FromImage(b);
            Assert.Equal(expectedPixels.Bytes, actualPixels.Bytes);
        }
        Assert.Equal(firstIds, first.Objects.Select(o => o.Item.Id));

        static string Sha(int i) => new(i == 0 ? 'a' : 'b', 64);
        static SceneObject Item(int i) => new(ObjectId.New(), SceneObjectKind.Stamp, "Гора",
            i % 31 * 8, i % 43 * 8, i % 7 == 0 ? 1024 : 16, 16, 0,
            "#FF445566", "#00000000", 0, true, false) { Sublayer = i % 4, Stamp = new StampStyle(Sha(i % 2)) };
        static SceneRenderer.OverviewChunk Cold(SceneObjectList items) => new(0, 0,
            [.. items.Select((o, i) => (Order: (long)i, Item: o)).OrderBy(o => o.Item.Sublayer).ThenBy(o => o.Order)]);
    }
}
