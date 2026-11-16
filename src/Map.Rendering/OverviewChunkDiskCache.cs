using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>Finished overview chunk rasters kept across program runs. Called on background workers only.</summary>
public interface IOverviewChunkStore
{
    /// <summary>The raster stored under the key, or null (none, unreadable or damaged: a damaged file is deleted).</summary>
    SKImage? TryLoad(ReadOnlySpan<byte> key, int width, int height);

    /// <summary>Keeps premultiplied RGBA pixels (width * height * 4 bytes) under the key; failures are silent.</summary>
    void Save(ReadOnlySpan<byte> key, int width, int height, byte[] pixels);
}

/// <summary>
/// The disk tier of <see cref="ObjectOverviewCache"/>: <c>&lt;root&gt;/v1/&lt;key&gt;.chunk</c>, one file per chunk
/// raster. The key (<see cref="SceneRenderer.OverviewChunkDiskKey"/>) is a SHA-256 over everything the raster is
/// drawn from, so a file is valid for whichever map and run asks for it, and an edit changes the keys of only the
/// chunks it touches. A file holds raw premultiplied RGBA - a 512-pixel chunk is one read and one copy, no decode -
/// behind a header that repeats the key and the size, and ends with a checksum; a file that fails any check is
/// deleted and never drawn. The folder is listed once (one directory scan), so a key that is not there costs no file
/// access. Writes are queued to one background writer of their own (a first open does not wait for the disk), each a
/// temporary file moved over the name (atomic). The folder is kept under its byte budget by deleting the least
/// recently used files (a hit refreshes the file's write time at most once an hour).
/// Model: the overview colours of <c>StampDerivedAssetStore.Colors</c> (versioned folder, integrity, atomic save).
/// </summary>
public sealed class OverviewChunkDiskCache : IOverviewChunkStore
{
    public const string FolderName = "overview-chunks";
    private const uint Magic = 0x4B43564F; // "OVCK"
    private const int Format = 1;
    internal const int HeaderBytes = 64;
    private const int KeyBytes = 32;
    private const string Extension = ".chunk";
    /// <summary>Pixels waiting for the writer; beyond it a picture is not kept (the next run rasters it again).</summary>
    private const long PendingLimit = 256L * 1024 * 1024;
    private readonly string _folder;
    private readonly long _byteBudget;
    private readonly object _gate = new();
    private Dictionary<string, long>? _present; // file name -> length, after the first scan
    private long _bytes;
    private long _loads, _misses, _saves, _damaged, _evicted, _dropped;
    private readonly Queue<(byte[] Key, int Width, int Height, byte[] Pixels)> _pending = new();
    private long _pendingBytes;
    private bool _writing;

    /// <summary>Test hook: called on the thread of every file operation, before it runs.</summary>
    internal Action<string>? BeforeIo { get; set; }

    /// <param name="root">The folder of this tier (usually <c>&lt;derived root&gt;/overview-chunks</c>).</param>
    /// <param name="byteBudget">Files beyond it are deleted, the least recently used first.</param>
    public OverviewChunkDiskCache(string root, long byteBudget = 1024L * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteBudget);
        _folder = Path.Combine(root, "v" + Format);
        _byteBudget = byteBudget;
    }

    public string Folder => _folder;

    public (long Loads, long Misses, long Saves, long Damaged, long Evicted) Statistics
    {
        get { lock (_gate) return (_loads, _misses, _saves, _damaged, _evicted); }
    }

    /// <summary>Bytes of chunk files in the folder (after the first scan, kept up to date by this instance).</summary>
    public long Bytes
    {
        get { EnsureIndex(); lock (_gate) return _bytes; }
    }

    internal string PathOf(ReadOnlySpan<byte> key) => Path.Combine(_folder, NameOf(key));
    private static string NameOf(ReadOnlySpan<byte> key) => Convert.ToHexStringLower(key) + Extension;

    // One listing of the folder; afterwards only this instance's own writes and deletions change it.
    private void EnsureIndex()
    {
        lock (_gate) if (_present is not null) return;
        var present = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            BeforeIo?.Invoke("scan");
            var directory = new DirectoryInfo(_folder);
            if (directory.Exists)
            {
                var staleTemporary = DateTime.UtcNow - TimeSpan.FromHours(1);
                foreach (var file in directory.EnumerateFiles())
                {
                    if (file.Name.EndsWith(Extension, StringComparison.Ordinal)) present[file.Name] = file.Length;
                    else if (file.Name.EndsWith(".tmp", StringComparison.Ordinal) && file.LastWriteTimeUtc < staleTemporary)
                        try { file.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        lock (_gate)
        {
            if (_present is not null) return;
            _present = present;
            _bytes = present.Values.Sum();
        }
    }

    public SKImage? TryLoad(ReadOnlySpan<byte> key, int width, int height)
    {
        if (key.Length != KeyBytes || width <= 0 || height <= 0) return null;
        EnsureIndex();
        var name = NameOf(key);
        lock (_gate)
            if (!_present!.ContainsKey(name)) { _misses++; return null; }
        var path = Path.Combine(_folder, name);
        var expected = HeaderBytes + (long)width * height * 4 + 8;
        try
        {
            BeforeIo?.Invoke("load");
            byte[] bytes;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan))
            {
                if (file.Length != expected) return Damaged(name);
                bytes = new byte[expected];
                file.ReadExactly(bytes);
            }
            if (!Valid(bytes, key, width, height)) return Damaged(name);
            var image = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul),
                bytes.AsSpan(HeaderBytes, width * height * 4), width * 4);
            if (image is null) return Damaged(name);
            // Recency for the budget: at most once an hour per file, so a frame of hits is not a burst of writes.
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromHours(1))
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            lock (_gate) _loads++;
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone meanwhile (another program's cleanup) or locked: rastered as if never kept.
            lock (_gate)
            {
                _misses++;
                if (ex is FileNotFoundException or DirectoryNotFoundException) Forget(name);
            }
            return null;
        }
    }

    // Under _gate.
    private void Forget(string name)
    {
        if (_present!.Remove(name, out var length)) _bytes -= length;
    }

    private SKImage? Damaged(string name)
    {
        lock (_gate) { _damaged++; Forget(name); }
        try { BeforeIo?.Invoke("delete"); File.Delete(Path.Combine(_folder, name)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static bool Valid(byte[] bytes, ReadOnlySpan<byte> key, int width, int height)
    {
        var payload = (long)width * height * 4;
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic
            && BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) == Format
            && BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) == width
            && BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)) == height
            && BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)) == payload
            && bytes.AsSpan(24, KeyBytes).SequenceEqual(key)
            && BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8)) == Checksum(bytes.AsSpan(0, bytes.Length - 8));
    }

    /// <summary>Queues the picture for the background writer and returns at once.</summary>
    public void Save(ReadOnlySpan<byte> key, int width, int height, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (key.Length != KeyBytes || width <= 0 || height <= 0 || pixels.LongLength != (long)width * height * 4) return;
        var copy = key.ToArray();
        lock (_gate)
        {
            if (_pendingBytes + pixels.Length > PendingLimit) { _dropped++; return; }
            _pending.Enqueue((copy, width, height, pixels));
            _pendingBytes += pixels.Length;
            if (_writing) return;
            _writing = true;
        }
        var writer = new Thread(WriteQueued) { IsBackground = true, Name = "Map overview chunk writer", Priority = ThreadPriority.BelowNormal };
        writer.Start();
    }

    /// <summary>Waits until every queued picture is written (shutdown, tests). False when the time ran out.</summary>
    public bool Flush(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_gate)
        {
            while (_writing)
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0 || !Monitor.Wait(_gate, TimeSpan.FromMilliseconds(left))) return !_writing;
            }
            return true;
        }
    }

    private void WriteQueued()
    {
        Map.Core.Resources.BackgroundWork.EnterOwnedThread();
        EnsureIndex();
        while (true)
        {
            (byte[] Key, int Width, int Height, byte[] Pixels) item;
            lock (_gate)
            {
                if (!_pending.TryDequeue(out item)) { _writing = false; Monitor.PulseAll(_gate); return; }
            }
            try { Write(item.Key, item.Width, item.Height, item.Pixels); }
            finally { lock (_gate) _pendingBytes -= item.Pixels.Length; }
        }
    }

    private void Write(byte[] key, int width, int height, byte[] pixels)
    {
        var bytes = new byte[HeaderBytes + pixels.Length + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), Format);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), height);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), pixels.LongLength);
        key.CopyTo(bytes.AsSpan(24));
        pixels.CopyTo(bytes, HeaderBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8), Checksum(bytes.AsSpan(0, bytes.Length - 8)));
        var name = NameOf(key);
        var path = Path.Combine(_folder, name);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            BeforeIo?.Invoke("save");
            Directory.CreateDirectory(_folder);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            bool over;
            lock (_gate)
            {
                _saves++;
                Forget(name);
                _present![name] = bytes.Length;
                _bytes += bytes.Length;
                over = _bytes > _byteBudget;
            }
            if (over) KeepBudget();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    // Writer thread: down to 90 % of the budget, the least recently used files first.
    private void KeepBudget()
    {
        List<FileInfo> files;
        try
        {
            BeforeIo?.Invoke("scan");
            files = [.. new DirectoryInfo(_folder).EnumerateFiles("*" + Extension)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        var total = files.Sum(file => file.Length);
        var target = _byteBudget * 9 / 10;
        long evicted = 0;
        var removed = new List<string>();
        foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc))
        {
            if (total <= target) break;
            try { file.Delete(); total -= file.Length; evicted++; removed.Add(file.Name); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        lock (_gate)
        {
            foreach (var name in removed) Forget(name);
            _evicted += evicted;
        }
    }

    /// <summary>A fast 64-bit checksum over the file (damage detection, not security).</summary>
    internal static ulong Checksum(ReadOnlySpan<byte> bytes)
    {
        const ulong prime = 0x9E3779B185EBCA87UL;
        ulong a = 0x243F6A8885A308D3UL, b = 0x13198A2E03707344UL, c = 0xA4093822299F31D0UL, d = 0x082EFA98EC4E6C89UL;
        var words = MemoryMarshal.Cast<byte, ulong>(bytes);
        var i = 0;
        for (; i + 4 <= words.Length; i += 4)
        {
            a = ulong.RotateLeft(a ^ words[i] * prime, 31) * prime;
            b = ulong.RotateLeft(b ^ words[i + 1] * prime, 31) * prime;
            c = ulong.RotateLeft(c ^ words[i + 2] * prime, 31) * prime;
            d = ulong.RotateLeft(d ^ words[i + 3] * prime, 31) * prime;
        }
        var hash = ulong.RotateLeft(a, 1) + ulong.RotateLeft(b, 7) + ulong.RotateLeft(c, 12) + ulong.RotateLeft(d, 18);
        for (; i < words.Length; i++) hash = ulong.RotateLeft(hash ^ words[i] * prime, 27) * prime;
        for (var j = words.Length * 8; j < bytes.Length; j++) hash = ulong.RotateLeft(hash ^ bytes[j] * prime, 11) * prime;
        return (hash ^ (ulong)bytes.Length) * prime;
    }
}
