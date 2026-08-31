namespace Map.Core.Documents;

/// <summary>
/// Immutable object-order storage. A point replacement copies one bounded chunk and the small
/// chunk directory; insert/remove rebuild only boundary chunks and share every untouched chunk.
/// Enumeration and the public index contract preserve the exact serialized/draw order.
/// </summary>
public sealed class SceneObjectList : IReadOnlyList<SceneObject>
{
    public const int ChunkSize = 256;

    private static readonly SceneObjectList EmptyInstance = new([], 0);
    private readonly SceneObject[][] _chunks;
    private readonly int[] _starts;

    private SceneObjectList(SceneObject[][] chunks, int count)
    {
        _chunks = chunks;
        Count = count;
        _starts = new int[chunks.Length];
        var start = 0;
        for (var index = 0; index < chunks.Length; index++)
        {
            _starts[index] = start;
            start += chunks[index].Length;
        }

        if (start != count)
        {
            throw new ArgumentException("Scene object chunks do not match their declared count.", nameof(chunks));
        }
    }

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
        return new SceneObjectList(chunks, Count);
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

        return new SceneObjectList(chunks, Count);
    }

    public SceneObjectList InsertRange(int index, IReadOnlyList<SceneObject> inserted) =>
        Splice(index, removeCount: 0, inserted);

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
        return nextCount == 0 ? Empty : new SceneObjectList([.. chunks], nextCount);
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
