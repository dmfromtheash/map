using Map.Core.Documents;

namespace Map.Core.Tests.Editing;

public sealed class TileMapBlockRevisionTests
{
    [Fact]
    public void Only_changed_blocks_get_new_tokens_including_negative_coordinates()
    {
        var pixels = new byte[65536];
        var map = TileMap.Empty(1).With(new TileKey(0, 0), pixels)
            .With(new TileKey(16, 0), pixels).With(new TileKey(-1, -1), pixels);
        var a = map.BlockRevision(0, 0);
        var b = map.BlockRevision(1, 0);
        var negative = map.BlockRevision(-1, -1);
        Assert.NotNull(a); Assert.NotNull(b); Assert.NotNull(negative);
        Assert.Null(map.BlockRevision(0, -1));
        var changed = map.With(new TileKey(1, 0), pixels);
        Assert.NotSame(a, changed.BlockRevision(0, 0));
        Assert.Same(b, changed.BlockRevision(1, 0));
        Assert.Same(negative, changed.BlockRevision(-1, -1));
        Assert.Same(changed, changed.With(new TileKey(1, 0), pixels));
        Assert.Same(a, map.BlockRevision(0, 0)); // Old immutable snapshot / undo.
        Assert.NotSame(negative, map.With(new TileKey(-1, -1), new byte[65536]).BlockRevision(-1, -1));
    }

    [Fact]
    public void Batched_add_replace_remove_and_last_tile_removal_keep_correct_empty_proofs()
    {
        var pixels = new byte[65536];
        var map = TileMap.Empty(1).With(new TileKey(16, 0), pixels);
        var untouched = map.BlockRevision(1, 0);
        KeyValuePair<TileKey, byte[]?> Change(int x, byte[]? value) => new(new TileKey(x, 0), value);
        var changed = map.WithMany([Change(0, pixels), Change(1, pixels), Change(0, null), Change(1, new byte[65536])]);
        Assert.Same(untouched, changed.BlockRevision(1, 0));
        Assert.NotNull(changed.BlockRevision(0, 0));
        Assert.Equal(2, changed.TileCount);
        Assert.Null(changed.With(new TileKey(1, 0), null).BlockRevision(0, 0));
        Assert.Null(changed.WithMany([Change(1, null), Change(1, null)]).BlockRevision(0, 0));
        Assert.Same(untouched, map.WithMany([Change(16, pixels), Change(0, null)]).BlockRevision(1, 0));
    }
}
