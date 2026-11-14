using System.Collections.Immutable;

namespace Map.Core.Documents;

public sealed partial class SceneObjectList
{
    private readonly LegendChunkOrder _legendOrder;

    // Legend buckets need stable order keys, while the storage directory addresses chunks by
    // rank. Keep only these two persistent maps; edits assign keys to created chunks and share
    // every untouched entry, without a second directory tree or predecessor snapshot.
    private sealed class LegendChunkOrder
    {
        private readonly ImmutableDictionary<SceneObject[], string> _keys;
        private readonly ImmutableDictionary<string, SceneObject[]> _chunks;

        public LegendChunkOrder(SceneObjectList list, SceneObjectList? source, Rewrite? rewrite)
        {
            var keys = (source?._legendOrder._keys
                ?? ImmutableDictionary.Create<SceneObject[], string>(ReferenceEqualityComparer.Instance)).ToBuilder();
            var chunks = (source?._legendOrder._chunks
                ?? ImmutableDictionary.Create<string, SceneObject[]>(StringComparer.Ordinal)).ToBuilder();
            if (source is null)
            {
                var index = 0;
                foreach (var chunk in list._chunks)
                {
                    var key = new string([(char)(index / 65536 + 1), (char)(index % 65536), (char)32768]);
                    keys.Add(chunk, key);
                    chunks.Add(key, chunk);
                    index++;
                }
            }
            else if (rewrite is not null)
            {
                foreach (var chunk in rewrite.Dropped)
                {
                    chunks.Remove(keys[chunk]);
                    keys.Remove(chunk);
                }
                var created = rewrite.Created.Select(entry =>
                {
                    if (!list._chunks.TryStart(entry.Chunk, out var start))
                        throw new InvalidOperationException("Created legend chunk is absent from storage.");
                    return (Chunk: entry.Chunk, Rank: list._chunks.Locate(start).Chunk);
                }).OrderBy(entry => entry.Rank).ToArray();
                for (var first = 0; first < created.Length;)
                {
                    var last = first;
                    while (last + 1 < created.Length && created[last + 1].Rank == created[last].Rank + 1) last++;
                    var lower = created[first].Rank == 0 ? null : keys[list._chunks[created[first].Rank - 1]];
                    var upper = created[last].Rank + 1 == list._chunks.Count
                        ? null : keys[list._chunks[created[last].Rank + 1]];
                    for (var i = first; i <= last; i++)
                    {
                        lower = Between(lower, upper);
                        keys.Add(created[i].Chunk, lower);
                        chunks.Add(lower, created[i].Chunk);
                    }
                    first = last + 1;
                }
            }
            _keys = keys.ToImmutable();
            _chunks = chunks.ToImmutable();
        }

        public string KeyOf(SceneObject[] chunk) => _keys[chunk];
        public SceneObject[] ChunkAtKey(string key) => _chunks[key];

        // No key ends in zero: even a prefix boundary has room for another key.
        private static string Between(string? lower, string? upper)
        {
            var prefix = new System.Text.StringBuilder();
            for (var i = 0; ; i++)
            {
                var a = lower is not null && i < lower.Length ? lower[i] : 0;
                var b = upper is not null && i < upper.Length ? upper[i] : 65535;
                if (b - a > 1) return prefix.Append((char)(a + (b - a) / 2)).ToString();
                prefix.Append((char)a);
                if (a != b) upper = null;
            }
        }
    }
}
