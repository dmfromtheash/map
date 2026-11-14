namespace Map.Core.Documents;

/// <summary>
/// Immutable object-order storage. A point replacement copies one bounded chunk and the small
/// chunk directory; insert/remove rebuild only boundary chunks and share every untouched chunk.
/// Enumeration and the public index contract preserve the exact serialized/draw order.
/// </summary>
public sealed class SceneObjectList : IReadOnlyList<SceneObject>
{
    private IdentityTable? _identitySlots;
    private readonly Dictionary<SceneObject[], int> _chunkStarts = new(ReferenceEqualityComparer.Instance);

    /// <summary>Number of full identity tables built (every object visited). Diagnostics and tests only.</summary>
    internal static long IdentityRootBuilds => System.Threading.Interlocked.Read(ref _identityRootBuilds);
    private static long _identityRootBuilds;

    /// <summary>
    /// Object id → (chunk, offset). The root is one plain hash table built once per loaded list
    /// (about 15x cheaper than a persistent tree of a million entries) and shared, never
    /// mutated, by every derived list. A derived list adds the items of its new chunks to a
    /// small persistent overlay and drops the items of chunks it no longer holds; a root entry
    /// whose chunk is no longer part of the list is stale and ignored. Chunks are immutable
    /// once stored, so a slot whose chunk is current always names the right object.
    /// </summary>
    private sealed class IdentityTable(
        Dictionary<ObjectId, (SceneObject[] Chunk, int Offset)> root,
        System.Collections.Immutable.ImmutableDictionary<ObjectId, (SceneObject[] Chunk, int Offset)> overlay)
    {
        public Dictionary<ObjectId, (SceneObject[] Chunk, int Offset)> Root { get; } = root;
        public System.Collections.Immutable.ImmutableDictionary<ObjectId, (SceneObject[] Chunk, int Offset)> Overlay { get; } = overlay;
    }
    public const int ChunkSize = 256;

    private static readonly SceneObjectList EmptyInstance = new([], 0);
    private readonly SceneObject[][] _chunks;
    private readonly int[] _starts;

    private SceneObjectList(SceneObject[][] chunks, int count, SceneObjectList? source = null)
    {
        _chunks = chunks;
        Count = count;
        _starts = new int[chunks.Length];
        var start = 0;
        for (var index = 0; index < chunks.Length; index++)
        {
            _starts[index] = start;
            _chunkStarts[chunks[index]] = start;
            start += chunks[index].Length;
        }

        if (start != count)
        {
            throw new ArgumentException("Scene object chunks do not match their declared count.", nameof(chunks));
        }
        if (source?._identitySlots is { } slots)
        {
            var builder = slots.Overlay.ToBuilder();
            foreach (var chunk in source._chunks)
                if (!_chunkStarts.ContainsKey(chunk))
                    foreach (var item in chunk) builder.Remove(item.Id);
            foreach (var chunk in chunks)
                if (!source._chunkStarts.ContainsKey(chunk))
                    for (var i = 0; i < chunk.Length; i++) builder[chunk[i].Id] = (chunk, i);
            _identitySlots = new IdentityTable(slots.Root, builder.ToImmutable());
        }
    }

    /// <summary>True when id lookups of this list no longer visit every object.</summary>
    internal bool HasIdentityTable => _identitySlots is not null;

    public static SceneObjectList Empty => EmptyInstance;

    /// <summary>An immutable input may be shared; every other caller list is copied.</summary>
    public static SceneObjectList From(IReadOnlyList<SceneObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (objects is SceneObjectList persistent)
        {
            return persistent;
        }

        if (objects.Count == 0)
        {
            return Empty;
        }

        var chunks = new SceneObject[(objects.Count + ChunkSize - 1) / ChunkSize][];
        for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
        {
            var start = chunkIndex * ChunkSize;
            var length = Math.Min(ChunkSize, objects.Count - start);
            var chunk = new SceneObject[length];
            for (var offset = 0; offset < length; offset++)
            {
                chunk[offset] = objects[start + offset];
            }

            chunks[chunkIndex] = chunk;
        }

        return new SceneObjectList(chunks, objects.Count);
    }

    public int Count { get; }

    public int IndexOfId(ObjectId id)
    {
        var table = _identitySlots;
        if (table is null)
        {
            var root = new Dictionary<ObjectId, (SceneObject[], int)>(Count);
            foreach (var chunk in _chunks)
                for (var i = 0; i < chunk.Length; i++) root.Add(chunk[i].Id, (chunk, i));
            System.Threading.Interlocked.Increment(ref _identityRootBuilds);
            System.Threading.Interlocked.CompareExchange(ref _identitySlots,
                new IdentityTable(root, System.Collections.Immutable.ImmutableDictionary<ObjectId, (SceneObject[], int)>.Empty), null);
            table = _identitySlots;
        }
        // The overlay holds exactly the items of chunks newer than the root, all current.
        if (table.Overlay.TryGetValue(id, out var slot)) return _chunkStarts[slot.Chunk] + slot.Offset;
        return table.Root.TryGetValue(id, out slot) && _chunkStarts.TryGetValue(slot.Chunk, out var start)
            ? start + slot.Offset : -1;
    }

    internal SceneObjectList RemoveIds(IReadOnlySet<ObjectId> ids)
    {
        var positions = ids.Select(IndexOfId).Where(i => i >= 0).ToHashSet();
        var touched = positions.Select(i => Locate(i).Chunk).ToHashSet();
        var chunks = new List<SceneObject[]>();
        for (var c = 0; c < _chunks.Length; c++)
        {
            if (!touched.Contains(c)) { chunks.Add(_chunks[c]); continue; }
            var kept = _chunks[c].Where((_, i) => !positions.Contains(_starts[c] + i)).ToArray();
            if (kept.Length > 0) chunks.Add(kept);
        }
        return new SceneObjectList([.. chunks], Count - positions.Count, this);
    }

    public SceneObject this[int index]
    {
        get
        {
            var (chunk, offset) = Locate(index);
            return _chunks[chunk][offset];
        }
    }

    public SceneObjectList ReplaceAt(int index, SceneObject replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var (chunkIndex, offset) = Locate(index);
        if (ReferenceEquals(_chunks[chunkIndex][offset], replacement))
        {
            return this;
        }

        var chunks = (SceneObject[][])_chunks.Clone();
        var chunk = (SceneObject[])chunks[chunkIndex].Clone();
        chunk[offset] = replacement;
        chunks[chunkIndex] = chunk;
        return new SceneObjectList(chunks, Count, this);
    }

    /// <summary>Copies every touched chunk once even when a group edit replaces many members.</summary>
    public SceneObjectList ReplaceMany(IReadOnlyDictionary<int, SceneObject> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0)
        {
            return this;
        }

        var chunks = (SceneObject[][])_chunks.Clone();
        var copied = new Dictionary<int, SceneObject[]>();
        foreach (var (index, replacement) in replacements)
        {
            ArgumentNullException.ThrowIfNull(replacement);
            var (chunkIndex, offset) = Locate(index);
            if (!copied.TryGetValue(chunkIndex, out var chunk))
            {
                chunk = (SceneObject[])chunks[chunkIndex].Clone();
                copied.Add(chunkIndex, chunk);
                chunks[chunkIndex] = chunk;
            }

            chunk[offset] = replacement;
        }

        return new SceneObjectList(chunks, Count, this);
    }

    public SceneObjectList InsertRange(int index, IReadOnlyList<SceneObject> inserted) =>
        Splice(index, removeCount: 0, inserted);

    /// <summary>Batch insertions at original indices; only intersected chunks are rebuilt.</summary>
    internal SceneObjectList InsertMany(IReadOnlyDictionary<int, IReadOnlyList<SceneObject>> insertions)
    {
        if (insertions.Count == 0) return this;
        var ordered = insertions.OrderBy(kv => kv.Key).ToArray();
        if (ordered[0].Key < 0 || ordered[^1].Key > Count) throw new ArgumentOutOfRangeException(nameof(insertions));
        var chunks = new List<SceneObject[]>(); var at = 0; var added = 0;
        foreach (var (chunk, start) in _chunks.Zip(_starts))
        {
            var end = start + chunk.Length;
            if (at == ordered.Length || ordered[at].Key >= end) { chunks.Add(chunk); continue; }
            var buffer = new List<SceneObject>(); var offset = 0;
            while (at < ordered.Length && ordered[at].Key < end)
            {
                var insertion = ordered[at++];
                while (start + offset < insertion.Key) buffer.Add(chunk[offset++]);
                buffer.AddRange(insertion.Value); added = checked(added + insertion.Value.Count);
            }
            while (offset < chunk.Length) buffer.Add(chunk[offset++]);
            AddChunks(buffer);
        }
        if (at < ordered.Length)
        { var tail = ordered[at].Value; added = checked(added + tail.Count); AddChunks(tail); }
        return new SceneObjectList([.. chunks], checked(Count + added), this);

        void AddChunks(IReadOnlyList<SceneObject> buffer)
        {
            for (var start = 0; start < buffer.Count; start += ChunkSize)
            {
                var part = new SceneObject[Math.Min(ChunkSize, buffer.Count - start)];
                for (var i = 0; i < part.Length; i++) part[i] = buffer[start + i];
                chunks.Add(part);
            }
        }
    }

    /// <summary>
    /// Appends a placement/paste batch without flattening the already stored prefix. Only the
    /// last partial chunk, the new chunks and the small chunk directory are copied.
    /// </summary>
    public SceneObjectList AppendRange(IReadOnlyList<SceneObject> appended) =>
        Splice(Count, removeCount: 0, appended);

    public SceneObjectList RemoveRange(int index, int count) =>
        Splice(index, count, Array.Empty<SceneObject>());

    public SceneObjectList ReplaceRange(
        int index, int removeCount, IReadOnlyList<SceneObject> replacements) =>
        Splice(index, removeCount, replacements);

    private SceneObjectList Splice(
        int index, int removeCount, IReadOnlyList<SceneObject> inserted)
    {
        ArgumentNullException.ThrowIfNull(inserted);
        if (index < 0 || index > Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        if (removeCount < 0 || removeCount > Count - index)
        {
            throw new ArgumentOutOfRangeException(nameof(removeCount));
        }
        if (removeCount == 0 && inserted.Count == 0)
        {
            return this;
        }
        if (Count == 0)
        {
            return From(inserted);
        }

        var (startChunk, startOffset) = LocateBoundary(index);
        var (endChunk, endOffset) = LocateBoundary(index + removeCount);
        var chunks = new List<SceneObject[]>(_chunks.Length + (inserted.Count / ChunkSize) + 2);
        for (var chunkIndex = 0; chunkIndex < startChunk; chunkIndex++)
        {
            chunks.Add(_chunks[chunkIndex]);
        }

        var boundaryCount = startOffset + inserted.Count + (_chunks[endChunk].Length - endOffset);
        if (boundaryCount > 0)
        {
            var boundary = new SceneObject[boundaryCount];
            Array.Copy(_chunks[startChunk], 0, boundary, 0, startOffset);
            for (var offset = 0; offset < inserted.Count; offset++)
            {
                boundary[startOffset + offset] = inserted[offset];
            }
            Array.Copy(
                _chunks[endChunk],
                endOffset,
                boundary,
                startOffset + inserted.Count,
                _chunks[endChunk].Length - endOffset);

            for (var offset = 0; offset < boundary.Length; offset += ChunkSize)
            {
                var length = Math.Min(ChunkSize, boundary.Length - offset);
                var chunk = new SceneObject[length];
                Array.Copy(boundary, offset, chunk, 0, length);
                chunks.Add(chunk);
            }
        }

        for (var chunkIndex = endChunk + 1; chunkIndex < _chunks.Length; chunkIndex++)
        {
            chunks.Add(_chunks[chunkIndex]);
        }

        var nextCount = checked(Count - removeCount + inserted.Count);
        return nextCount == 0 ? Empty : new SceneObjectList([.. chunks], nextCount, this);
    }

    private (int Chunk, int Offset) Locate(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var chunk = Array.BinarySearch(_starts, index);
        if (chunk < 0)
        {
            chunk = ~chunk - 1;
        }

        return (chunk, index - _starts[chunk]);
    }

    private (int Chunk, int Offset) LocateBoundary(int index)
    {
        if (index == Count)
        {
            return (_chunks.Length - 1, _chunks[^1].Length);
        }

        return Locate(index);
    }

    public IEnumerator<SceneObject> GetEnumerator()
    {
        foreach (var chunk in _chunks)
        {
            for (var index = 0; index < chunk.Length; index++)
            {
                yield return chunk[index];
            }
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
