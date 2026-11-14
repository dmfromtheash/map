using System.Runtime.CompilerServices;
using Map.Core.Documents;
using Map.Core.Resources;

[assembly: InternalsVisibleTo("Map.Rendering.Tests")]

namespace Map.Rendering;

/// <summary>
/// Portable, immutable field data. Blocks retain their original sampling origin, dimensions
/// and exact scale as well as the scale bucket: rounding these changes shoreline pixels.
/// Revision tokens do not retain document snapshots or their source arrays.
/// </summary>
internal sealed class TerrainFieldCache : IDisposable
{
    [ThreadStatic] internal static bool Bypass;
    internal static readonly TerrainFieldCache Shared = new(384L * WorkingMemoryBudget.MiB, WorkingMemoryBudget.Shared);
    private sealed class Token { public readonly long Id = Interlocked.Increment(ref _nextId); }
    private static long _nextId;
    private static readonly ConditionalWeakTable<object, Token> Tokens = new();
    internal static long Revision(object value) => Tokens.GetValue(value, _ => new Token()).Id;

    /// <summary>Only the immutable source groups intersecting a field, including its padding.</summary>
    internal static long[] TileRevisions(TileMap tiles, WorldBounds bounds)
    {
        const int size = TileMap.TileSize * TileMap.RevisionBlockSize;
        var revisions = new List<long>();
        var minX = (int)Math.Floor(bounds.Left / size); var maxX = (int)Math.Floor(bounds.Right / size);
        var minY = (int)Math.Floor(bounds.Top / size); var maxY = (int)Math.Floor(bounds.Bottom / size);
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
            if (tiles.BlockRevision(x, y) is { } revision)
            {
                revisions.Add(((long)x << 32) | (uint)y);
                revisions.Add(Revision(revision));
            }
        return revisions.ToArray();
    }

    internal static long[] CoverageRevisions(IEnumerable<LandLayer> lands, WorldBounds bounds)
    {
        var revisions = new List<long>();
        foreach (var land in lands)
        {
            var id = land.Id.Value.ToByteArray();
            revisions.Add(BitConverter.ToInt64(id, 0)); revisions.Add(BitConverter.ToInt64(id, 8));
            revisions.Add(BitConverter.DoubleToInt64Bits(land.Opacity));
            var geometry = TileRevisions(land.GeometryTiles, bounds);
            revisions.Add(geometry.Length); revisions.AddRange(geometry);
            var mask = land.Mask is { Enabled: true } m ? TileRevisions(m.Tiles, bounds) : [];
            revisions.Add(land.Mask is { Enabled: true } ? 1 : 0);
            revisions.Add(mask.Length); revisions.AddRange(mask);
        }
        return revisions.ToArray();
    }

    internal sealed record Key(string Kind, LayerId Layer, WorldBounds Block, double Scale, object Parameters, long[] Revisions)
    {
        public int Bucket => (int)Math.Floor(Math.Log2(Math.Max(1e-9, Scale)));
        public bool Equals(Key? other) => other is not null && Kind == other.Kind && Layer == other.Layer
            && Block == other.Block && Scale == other.Scale && Equals(Parameters, other.Parameters)
            && Revisions.AsSpan().SequenceEqual(other.Revisions);
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Kind); hash.Add(Layer); hash.Add(Block); hash.Add(Bucket); hash.Add(Scale); hash.Add(Parameters);
            foreach (var revision in Revisions) hash.Add(revision);
            return hash.ToHashCode();
        }
    }

    private sealed record Entry(Key Key, object Value, long Bytes);
    private readonly object _gate = new();
    private readonly CacheMemoryAccount _account;
    private readonly Dictionary<Key, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _order = new();
    private readonly Action _release;
    private bool _disposed;
    internal long Bytes { get; private set; }
    internal long Builds { get; private set; }
    internal long Hits { get; private set; }

    internal TerrainFieldCache(long bytes, WorkingMemoryBudget memory)
    {
        _account = memory.RegisterCache("land-terrain-fields", CacheRebuildCost.Expensive, bytes);
        _release = Clear;
        WorkingMemoryBudget.ReleaseDerivedCaches += _release;
    }

    internal T? Get<T>(Key key, long bytes, Func<T?> build) where T : class
    {
        if (Bypass) return build();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Trim();
            if (_entries.TryGetValue(key, out var entry))
            {
                _order.Remove(entry); _order.AddLast(entry); Hits++;
                return (T)entry.Value.Value;
            }
            // Single flight, including simultaneous CPU/export requests for the same block.
            var value = build();
            if (value is null) return null;
            Builds++;
            bytes += 640L + key.Revisions.LongLength * sizeof(long);
            if (bytes > Limit) return value; // An oversized transient never enters the cache.
            var node = _order.AddLast(new Entry(key, value, bytes));
            _entries.Add(key, node); Bytes += bytes;
            Trim();
            return value; // Managed immutable values remain valid if another request evicts them.
        }
    }

    private long Limit => Math.Min(_account.LimitBytes, _account.TargetBytes);
    private void Trim()
    {
        long bytes = 0; var count = 0;
        while (Bytes > Limit && _order.First is { } oldest)
        {
            _order.RemoveFirst(); _entries.Remove(oldest.Value.Key);
            Bytes -= oldest.Value.Bytes; bytes += oldest.Value.Bytes; count++;
        }
        _account.RecordTrim(bytes, count); _account.Report(Bytes);
    }

    internal void Clear()
    {
        lock (_gate) { _entries.Clear(); _order.Clear(); Bytes = 0; _account.Report(0); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Clear(); _disposed = true;
            WorkingMemoryBudget.ReleaseDerivedCaches -= _release;
            _account.Dispose();
        }
    }

    internal sealed class CoastField
    {
        internal byte[] Alpha { get; }
        private readonly Lazy<float[]> _distance;
        // Some overlapping lands use this alpha only as an inner-shadow mask. Charge the
        // possible distance bytes up front, but compute them only when an effect needs them.
        internal CoastField(byte[] alpha, int width, int height)
        {
            Alpha = alpha;
            _distance = new(() => CoastDistanceField.SignedDistance(alpha, width, height));
        }
        internal float[] Distance => _distance.Value;
    }
}
