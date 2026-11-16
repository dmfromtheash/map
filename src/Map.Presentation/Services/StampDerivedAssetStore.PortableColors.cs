using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Map.Assets;
using SkiaSharp;

namespace Map.Presentation.Services;

public sealed partial class StampDerivedAssetStore
{
    // Library-side, expendable index. Old programs ignore it. It contains colours, never pixel decisions.
    // Unlike the machine palette, its source fingerprint is relative and survives copying a library.
    private const uint PortableColorsMagic = 0x50434F4C;
    private const int PortableColorsEntryBytes = 68;
    private sealed record PortableColor(uint Color, string Sources);
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyDictionary<string, PortableColor>>>> _libraryColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<SKColor?>> _colorRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> _colorTasks = [];
    private readonly ConcurrentDictionary<string, byte> _colorIndexInvalidated = new(StringComparer.OrdinalIgnoreCase);

    private static string PortableColorsPath(string root) => Path.Combine(root, "derived", "v" + Version, "overview-colors.bin");

    private string PortableColorStamp(AssetRecord record, FileStamp original, FileStamp working) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { original, working }
            .Select(stamp => new { Path = Path.GetRelativePath(_catalog.RootFor(record), stamp.Path).Replace('\\', '/'), stamp.Length }))));

    // Worker only. A missing/damaged index falls back to the existing per-asset route.
    private IReadOnlyDictionary<string, PortableColor> ReadPortableColors(string root)
    {
        var result = new Dictionary<string, PortableColor>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = PortableColorsPath(root);
            if (new FileInfo(path) is not { Exists: true, Length: >= 48 and <= 64 * 1024 * 1024 }) return result;
            CountIo(ref _portableColorIndexReads);
            var bytes = File.ReadAllBytes(path);
            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != PortableColorsMagic
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) != 1
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) != Version
                || count < 0 || 48L + count * (long)PortableColorsEntryBytes != bytes.Length
                || !SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - 32))) return result;
            for (var i = 0; i < count; i++)
            {
                var offset = 16 + i * PortableColorsEntryBytes;
                result[Convert.ToHexStringLower(bytes, offset, 32)] = new(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 32)),
                    Convert.ToHexStringLower(bytes, offset + 36, 32));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { result.Clear(); }
        return result;
    }

    internal Task<SKColor?>[] PrepareColors(string[] assets)
    {
        var tasks = assets.Select(sha => PrepareColor(sha, notify: false)).ToArray();
        if (tasks.Any(task => !task.IsCompleted))
            _ = Task.WhenAll(tasks).ContinueWith(_ => { if (!_disposed) OverviewColorChanged(); }, TaskScheduler.Default);
        return tasks;
    }

    internal Task<SKColor?> PrepareColor(string sha) => PrepareColor(sha, notify: true);

    private Task<SKColor?> PrepareColor(string sha, bool notify)
    {
        if (KnownColor(sha) is { } known) return Task.FromResult<SKColor?>(known);
        lock (_requestsGate)
        {
            if (_disposed || _catalog.Find(sha) is null) return Task.FromResult<SKColor?>(null);
            // Preserve the synchronous verdict of a completed failed header during its backoff.
            // Starting an index task here would turn a known miss into a transparent pending colour.
            if (_requests.TryGetValue(sha, out var completedHeader) && completedHeader.Task.IsCompletedSuccessfully
                && completedHeader.Task.Result is null && !HeaderRetryDue(sha)) return Task.FromResult<SKColor?>(null);
            if (_colorRequests.TryGetValue(sha, out var existing))
            {
                if (!existing.IsCompleted || existing.IsCompletedSuccessfully && (existing.Result is not null || !HeaderRetryDue(sha))) return existing;
                _colorRequests.TryRemove(sha, out _);
            }
            var generation = Generation;
            var task = Task.Run(async () =>
            {
                if (_disposed) return (SKColor?)null;
                if (_catalog.Find(sha) is { } record && !_colorIndexInvalidated.ContainsKey(sha))
                {
                    var root = _catalog.RootFor(record);
                    var colors = await _libraryColors.GetOrAdd(root, path => new(() => Task.Run(() => ReadPortableColors(path)))).Value.ConfigureAwait(false);
                    if (colors.TryGetValue(sha, out var entry))
                    {
                        try
                        {
                            var source = _catalog.SourcePath(record);
                            if (source is not null)
                            {
                                Watch(record, source);
                                if (AssetCatalog.DetailLadderDepth(record.Width, record.Height) > 0)
                                    Watch(record, Path.Combine(root, "ladder", sha.ToLowerInvariant(), "1.png"));
                            }
                            var original = FileStamp.Read(source);
                            var working = FileStamp.Read(_catalog.TryDetailLevelPath(record, 1, out var step) ? step : source);
                            if (original is not null && working is not null && PortableColorStamp(record, original, working) == entry.Sources)
                            {
                                lock (_requestsGate)
                                {
                                    if (_disposed || Generation != generation || _colorIndexInvalidated.ContainsKey(sha)) return null;
                                    var sources = ColorSourceStamp(original, working);
                                    _knownColors[sha] = new(entry.Color, sources, false);
                                }
                                // Same provisional trust policy as a portable header. Only proved colours enter
                                // the machine palette, and rejection goes through ordinary source invalidation.
                                QueueTrustVerification(sha);
                                Interlocked.Increment(ref _portableColorIndexHits);
                                // A whole palette is published once; thousands of per-colour revisions used
                                // to discard/raster the same million-object overview repeatedly during open.
                                if (notify) OverviewColorChanged();
                                return new SKColor(entry.Color);
                            }
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    }
                }
                Task<Header?> request;
                lock (_requestsGate)
                {
                    // Forget/mount changes may retire the colour lookup while its library index loads.
                    // Do not let that old lookup enqueue fresh headers after the generation was cleared.
                    if (_disposed || Generation != generation) return null;
                    request = Request(sha);
                }
                var header = await request.ConfigureAwait(false);
                return header is null ? (SKColor?)null : new SKColor(header.Color);
            });
            _colorRequests[sha] = task;
            _colorTasks.Add(task);
            _ = task.ContinueWith(completed => { lock (_requestsGate) _colorTasks.Remove(completed); }, TaskScheduler.Default);
            return task;
        }
    }

    private bool HeaderRetryDue(string sha) => !_failedHeaders.TryGetValue(sha, out var failure)
        || _retryClock.GetUtcNow().ToUnixTimeMilliseconds() >= failure.RetryAt;

    /// <summary>Explicit library-preparation operation, never called by rendering. Creates a compact palette
    /// beside existing portable files, so an empty machine cache need not read every asset header for fit.</summary>
    public async Task<int> WritePortableColorIndexAsync(IEnumerable<string> assets, bool write = true, CancellationToken cancellation = default)
    {
        var total = 0;
        var groups = assets.Distinct(StringComparer.OrdinalIgnoreCase).Select(sha => _catalog.Find(sha))
            .Where(record => record is not null).Cast<AssetRecord>().GroupBy(_catalog.RootFor);
        foreach (var group in groups)
        {
            var entries = new List<(string Sha, uint Color, string Sources)>();
            foreach (var record in group)
            {
                cancellation.ThrowIfCancellationRequested();
                // This index carries provisional colours, not a machine-local proof. Do not hash/decode
                // originals here: the normal reader verifies sources after the first visible work drains.
                var header = TryReadPortableHeader(record, verify: false);
                if (header?.PortablePath is null) continue;
                entries.Add((record.Sha256, header.Color, PortableColorStamp(record, header.Original, header.Working)));
            }
            total += entries.Count;
            if (!write) continue;
            var bytes = new byte[48 + entries.Count * PortableColorsEntryBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, PortableColorsMagic);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), Version);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                var offset = 16 + i * PortableColorsEntryBytes;
                Convert.FromHexString(entries[i].Sha).CopyTo(bytes, offset);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 32), entries[i].Color);
                Convert.FromHexString(entries[i].Sources).CopyTo(bytes, offset + 36);
            }
            SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes, bytes.Length - 32);
            var path = PortableColorsPath(group.Key);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            // As for portable pixels: preparation must never follow a library output reparse point.
            foreach (var directory in new[] { Path.Combine(group.Key, "derived"), Path.GetDirectoryName(path)! })
                if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Portable output must not follow a directory reparse point.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellation).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return total;
    }
}
