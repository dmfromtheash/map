using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Map.Core.Documents;

namespace Map.Persistence;

public static partial class MapProjectStore
{
    // Immutable storage chunks are their own revision tokens, including across undo/checkpoints.
    // Keep compressed bytes, not a second copy of a million-object JSON graph. Weak keys never
    // retain documents; the LRU bounds payloads even while a long undo history keeps keys alive.
    private const long ObjectCacheBudget = 128L * 1024 * 1024;
    private static readonly ConditionalWeakTable<SceneObject[], ObjectCacheSlot> ObjectCache = new();
    private static readonly LinkedList<ObjectCacheSlot> ObjectCacheLru = new();
    private static readonly object ObjectCacheGate = new();
    private static long _objectCacheBytes;
    [ThreadStatic] private static long _serializedObjects;
    internal static long SerializedObjectsOnThisThread => _serializedObjects;

    private sealed class ObjectCacheSlot
    {
        public SerializedObjects? Payload;
        public LinkedListNode<ObjectCacheSlot>? Node;
    }

    private sealed record SerializedObjects(byte[] Compressed, int[] Lengths, WorldBounds Bounds)
    {
        public long Cost => Compressed.LongLength + Lengths.LongLength * sizeof(int) + 128;
    }

    private static SerializedObjects GetSerializedObjects(SceneObject[] objects)
    {
        var slot = ObjectCache.GetValue(objects, static _ => new ObjectCacheSlot());
        lock (ObjectCacheGate)
        {
            if (slot.Payload is { } cached)
            {
                ObjectCacheLru.Remove(slot.Node!);
                ObjectCacheLru.AddLast(slot.Node!);
                return cached;
            }
        }

        var lengths = new int[objects.Length];
        WorldBounds? bounds = null;
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var i = 0; i < objects.Length; i++)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(ToObjectDto(objects[i]), JsonOptions);
                _serializedObjects++;
                if (bytes.LongLength + 2 > MapProjectFormat.ObjectChunkTargetBytes)
                    throw new MapProjectResourceLimitException($"Object {objects[i].Id} cannot fit the object-chunk byte budget.");
                // One surrounding array level belongs to the persisted object chunk. Cache only
                // reader-admissible JSON; this work is paid once per changed storage chunk.
                MapProjectJsonBudget.Validate(bytes, "object chunk", maxDepth: 63);
                lengths[i] = bytes.Length;
                if (i > 0) deflate.WriteByte((byte)',');
                deflate.Write(bytes);
                bounds = Union(bounds, SceneGeometry.BoundsOf(objects[i]));
            }
        }
        var result = new SerializedObjects(compressed.ToArray(), lengths, bounds!.Value);
        lock (ObjectCacheGate)
        {
            // Another save may have populated the same immutable key while this one serialized.
            if (slot.Payload is { } cached) return cached;
            if (result.Cost > ObjectCacheBudget) return result;
            while (_objectCacheBytes + result.Cost > ObjectCacheBudget)
            {
                var oldest = ObjectCacheLru.First!;
                _objectCacheBytes -= oldest.Value.Payload!.Cost;
                oldest.Value.Payload = null;
                oldest.Value.Node = null;
                ObjectCacheLru.RemoveFirst();
            }
            slot.Payload = result;
            slot.Node = ObjectCacheLru.AddLast(slot);
            _objectCacheBytes += result.Cost;
        }
        return result;
    }

    /// <summary>Walks cached JSON without materializing an uncompressed storage chunk.</summary>
    private sealed class ObjectByteCursor(IReadOnlyList<SceneObject[]> chunks) : IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        private int _chunk;
        private int _offset;
        private DeflateStream? _stream;
        private SerializedObjects? _payload;
        public bool HasMore => _chunk < chunks.Count;

        public int WriteNext(Utf8JsonWriter writer, int maxCount, long availableBytes, List<int> lengths, out int byteLength, out WorldBounds bounds)
        {
            _payload ??= GetSerializedObjects(chunks[_chunk]);
            var count = 0;
            var length = 0;
            while (_offset + count < _payload.Lengths.Length && count < maxCount)
            {
                var next = checked(length + _payload.Lengths[_offset + count] + (count == 0 ? 0 : 1));
                if (next > availableBytes) break;
                length = next;
                count++;
            }
            bounds = _payload.Bounds;
            byteLength = length;
            if (count == 0) return 0;
            if (_offset != 0 || count != _payload.Lengths.Length)
            {
                WorldBounds? partial = null;
                for (var i = 0; i < count; i++) partial = Union(partial, SceneGeometry.BoundsOf(chunks[_chunk][_offset + i]));
                bounds = partial!.Value;
            }
            _stream ??= new DeflateStream(new MemoryStream(_payload.Compressed, writable: false), CompressionMode.Decompress);
            for (var i = 0; i < count; i++)
            {
                if (_offset + i > 0)
                {
                    _stream.ReadExactly(_buffer.AsSpan(0, 1));
                    if (_buffer[0] != ',') throw new InvalidOperationException("Cached object separator is missing.");
                }
                var size = _payload.Lengths[_offset + i];
                lengths.Add(size);
                if (size > _buffer.Length)
                {
                    ArrayPool<byte>.Shared.Return(_buffer);
                    _buffer = ArrayPool<byte>.Shared.Rent(size);
                }
                _stream.ReadExactly(_buffer.AsSpan(0, size));
                // Preserve the original writer calls/buffer boundaries too: feeding the same JSON
                // to Deflate in different runs can produce a different compressed ZIP bitstream.
                writer.WriteRawValue(_buffer.AsSpan(0, size), skipInputValidation: true);
            }
            _offset += count;
            if (_offset == _payload.Lengths.Length)
            {
                _stream.Dispose();
                _stream = null;
                _payload = null;
                _offset = 0;
                _chunk++;
            }
            return count;
        }

        public void Dispose()
        {
            _stream?.Dispose();
            ArrayPool<byte>.Shared.Return(_buffer);
        }
    }
}
