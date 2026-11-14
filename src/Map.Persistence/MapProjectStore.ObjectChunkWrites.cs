using System.Text.Json;

namespace Map.Persistence;

public static partial class MapProjectStore
{
    // Stream-mode Utf8JsonWriter grows its backing buffer and sends the committed prefix to the
    // stream at each growth. Both the growth allocations and Deflate output depend on object
    // lengths. Remember that write schedule so repeat saves can reuse one bounded JSON buffer
    // while feeding Deflate exactly the same runs as the original writer, byte for byte.
    private const long WritePatternBudget = 32L * 1024 * 1024;
    private static readonly object WritePatternsGate = new();
    private static readonly Dictionary<ulong, List<ObjectWritePattern>> WritePatterns = [];
    private static readonly LinkedList<ObjectWritePattern> WritePatternsLru = new();
    private static long _writePatternBytes;

    private sealed class ObjectWritePattern(ulong key, int[] lengths, int[] writes)
    {
        public ulong Key { get; } = key;
        public int[] Lengths { get; } = lengths;
        public int[] Writes { get; } = writes;
        public LinkedListNode<ObjectWritePattern>? Node;
        public long Cost => (Lengths.LongLength + Writes.LongLength) * sizeof(int) + 128;
    }

    private static ObjectWritePattern? FindWritePattern(ulong key, List<int> lengths)
    {
        if (!WritePatterns.TryGetValue(key, out var patterns)) return null;
        foreach (var pattern in patterns)
        {
            if (pattern.Lengths.Length != lengths.Count) continue;
            var equal = true;
            for (var i = 0; i < lengths.Count; i++)
                if (pattern.Lengths[i] != lengths[i]) { equal = false; break; }
            if (!equal) continue;
            WritePatternsLru.Remove(pattern.Node!);
            WritePatternsLru.AddLast(pattern.Node!);
            return pattern;
        }
        return null;
    }

    private static void WriteCanonicalObjectChunk(HashingWriteStream destination, ReadOnlySpan<byte> json, List<int> lengths)
    {
        var key = 14695981039346656037UL;
        foreach (var length in lengths) key = unchecked((key ^ (uint)length) * 1099511628211UL);
        ObjectWritePattern? cached;
        lock (WritePatternsGate) cached = FindWritePattern(key, lengths);
        if (cached is not null)
        {
            var offset = 0;
            foreach (var size in cached.Writes)
            {
                if (size < 0) { destination.Flush(); continue; }
                destination.Write(json.Slice(offset, size));
                offset += size;
            }
            if (offset != json.Length) throw new InvalidOperationException("Cached JSON write schedule has the wrong length.");
            return;
        }

        var writes = new List<int>();
        destination.RecordedWrites = writes;
        try
        {
            using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            });
            writer.WriteStartArray();
            var offset = 1;
            foreach (var length in lengths)
            {
                writer.WriteRawValue(json.Slice(offset, length), skipInputValidation: true);
                offset += length + 1;
            }
            writer.WriteEndArray();
            writer.Flush();
        }
        finally { destination.RecordedWrites = null; }

        var pattern = new ObjectWritePattern(key, [.. lengths], [.. writes]);
        lock (WritePatternsGate)
        {
            if (FindWritePattern(key, lengths) is not null) return;
            while (_writePatternBytes + pattern.Cost > WritePatternBudget)
            {
                var oldest = WritePatternsLru.First!.Value;
                _writePatternBytes -= oldest.Cost;
                var bucket = WritePatterns[oldest.Key];
                bucket.Remove(oldest);
                if (bucket.Count == 0) WritePatterns.Remove(oldest.Key);
                WritePatternsLru.RemoveFirst();
            }
            if (!WritePatterns.TryGetValue(key, out var patterns)) WritePatterns.Add(key, patterns = []);
            patterns.Add(pattern);
            pattern.Node = WritePatternsLru.AddLast(pattern);
            _writePatternBytes += pattern.Cost;
        }
    }
}
