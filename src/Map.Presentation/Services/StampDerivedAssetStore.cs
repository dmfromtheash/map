using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;
using Map.Assets;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Services;

/// <summary>
/// Disposable preparation queue, persistent content-addressed results. No file operations,
/// decode, resampling or waits are performed by Request/TryReady on the canvas thread.
/// One worker bounds native decode memory and avoids competing with the frame for all cores.
/// </summary>
public sealed class StampDerivedAssetStore : IDisposable
{
    // Bump when the working-image choice, overview evaluator or mip sampling changes.
    private const int Version = 1;
    private readonly AssetCatalog _catalog;
    private readonly string _root;
    private readonly long _diskBudget;
    private readonly object _requestsGate = new();
    private readonly object _diskGate = new();
    private readonly ConcurrentQueue<(string Sha, TaskCompletionSource<Header?> Completion)> _completedHeaders = new();
    private readonly int _metadataCapacity;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Header?>> _requests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<(string Sha, TaskCompletionSource<Header?> Completion, TaskCompletionSource<SKColor?> Preview)> _queue =
        Channel.CreateUnbounded<(string, TaskCompletionSource<Header?>, TaskCompletionSource<SKColor?>)>(new() { SingleReader = true });
    private readonly Channel<(string Sha, TaskCompletionSource<Header?> Completion)> _buildQueue =
        Channel.CreateUnbounded<(string, TaskCompletionSource<Header?>)>(new() { SingleReader = true });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<SKColor?>> _previews = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _watchedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _invalidated = new(StringComparer.OrdinalIgnoreCase);
    public Action? SourceChanged { get; set; }
    public string[] TakeInvalidated()
    {
        var keys = _invalidated.Keys.ToArray();
        foreach (var key in keys) _invalidated.TryRemove(key, out _);
        return keys;
    }
    private volatile bool _disposed;
    private long _generation;
    public long Generation => Interlocked.Read(ref _generation);
    public long DiskHits => Interlocked.Read(ref _diskHits);
    public long Builds => Interlocked.Read(ref _builds);
    public long DiskBytes => Interlocked.Read(ref _diskBytes);
    public long DiskEvictions => Interlocked.Read(ref _diskEvictions);
    public int MetadataEntries => _requests.Count;
    public int PendingMips => _mips.Count;
    private long _diskBytes, _diskEvictions;
    private long _diskHits, _builds;
    private readonly ConcurrentDictionary<(string Sha, int Level), MipRequest> _mips = new();
    private long _requestSequence, _discardedMips;
    public long DiscardedMips => Interlocked.Read(ref _discardedMips);
    private sealed class MipRequest(CancellationTokenSource stop, long order)
    {
        public CancellationTokenSource Stop { get; } = stop;
        public long Order { get; } = order;
        public bool Started { get; set; }
        public Task<Mip> Task { get; set; } = null!;
    }
    private readonly SemaphoreSlim _mipSlots = new(8);
    private readonly SemaphoreSlim _mipReader = new(1);
    private readonly CancellationTokenSource _stop = new();
    public sealed record Mip(byte[]? Pixels, int Width, int Height, bool Reserved, bool Cancelled = false);
    public Task Completion { get; }

    public sealed record FileStamp(string Path, long Length, long WrittenTicks)
    {
        public static FileStamp? Read(string? path)
        {
            if (path is null) return null;
            var info = new FileInfo(path);
            return info.Exists ? new(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        public bool Matches() => this == Read(Path);
    }

    public sealed record Header(int Version, string Sha, int Width, int Height, uint Color,
        FileStamp Original, FileStamp Working, int FirstMip, int MipCount, string PixelHash);

    public StampDerivedAssetStore(AssetCatalog catalog, string root, long diskBudgetBytes = 2L * 1024 * 1024 * 1024,
        int metadataCapacity = 8192)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(diskBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(metadataCapacity);
        _catalog = catalog;
        _root = Path.GetFullPath(root);
        _diskBudget = diskBudgetBytes;
        _metadataCapacity = metadataCapacity;
        Completion = Task.WhenAll(Task.Run(async () => { TrimDisk(); await Work().ConfigureAwait(false); }), Task.Run(BuildWork));
    }

    public Task<Header?> Request(string sha)
    {
        if (_disposed || sha.Length != 64 || !sha.All(Uri.IsHexDigit)) return Task.FromResult<Header?>(null);
        lock (_requestsGate)
        {
            if (_requests.TryGetValue(sha, out var existing)) return existing.Task;
            if (_catalog.Find(sha) is null) return Task.FromResult<Header?>(null);
            var completion = new TaskCompletionSource<Header?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests[sha] = completion;
            var preview = new TaskCompletionSource<SKColor?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _previews[sha] = preview;
            if (!_queue.Writer.TryWrite((sha, completion, preview)))
            { completion.TrySetResult(null); preview.TrySetResult(null); }
            return completion.Task;
        }
    }

    public Header? TryReady(string sha) => _requests.TryGetValue(sha, out var task)
        && task.Task.IsCompletedSuccessfully ? task.Task.Result : null;

    public Task<SKColor?> Preview(string sha)
    {
        Request(sha);
        return _previews.TryGetValue(sha, out var preview) ? preview.Task : Task.FromResult<SKColor?>(null);
    }

    public SKColor? TryPreview(string sha) => _previews.TryGetValue(sha, out var preview)
        && preview.Task.IsCompletedSuccessfully ? preview.Task.Result : null;

    public void Forget()
    {
        lock (_requestsGate)
        {
            Interlocked.Increment(ref _generation);
            _requests.Clear(); _previews.Clear(); _completedHeaders.Clear();
            foreach (var request in _mips.Values) Retire(request);
            _mips.Clear();
        }
    }

    private void Retire(MipRequest request)
    {
        request.Stop.Cancel();
        _ = request.Task.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && task.Result.Reserved) _mipSlots.Release();
            request.Stop.Dispose();
        }, TaskScheduler.Default);
    }

    private void CompleteHeader(string sha, TaskCompletionSource<Header?> completion, Header? header)
    {
        lock (_requestsGate)
        {
            if (_requests.TryGetValue(sha, out var active) && ReferenceEquals(active, completion))
            {
                _completedHeaders.Enqueue((sha, completion));
                while (_requests.Count > _metadataCapacity && _completedHeaders.TryDequeue(out var oldest))
                    if (_requests.TryGetValue(oldest.Sha, out var current) && ReferenceEquals(current, oldest.Completion))
                    { _requests.TryRemove(oldest.Sha, out _); _previews.TryRemove(oldest.Sha, out _); }
            }
        }
        completion.TrySetResult(header);
    }

    public Task<Mip> RequestMip(Header header, int level)
    {
        if (_disposed) return Task.FromResult(new Mip(null, 0, 0, false, Cancelled: true));
        var key = (header.Sha.ToLowerInvariant(), level);
        lock (_requestsGate)
        {
            if (_mips.TryGetValue(key, out var held)) return held.Task;
            if (_mips.Count >= 4096)
            {
                // A rapid wheel gesture must not grow a task per asset per abandoned zoom.
                var oldest = _mips.Where(pair => !pair.Value.Started).MinBy(pair => pair.Value.Order);
                if (oldest.Value is not null && _mips.TryRemove(oldest.Key, out var removed))
                {
                    Retire(removed); Interlocked.Increment(ref _discardedMips);
                }
            }
            var request = new MipRequest(CancellationTokenSource.CreateLinkedTokenSource(_stop.Token), ++_requestSequence);
            request.Task = Task.Run(() => ReadRequestedMip(header, level, request));
            _mips[key] = request;
            return request.Task;
        }
    }

    public void CancelUnwantedMips(IReadOnlySet<(string Sha, int Level)> wanted)
    {
        lock (_requestsGate)
            foreach (var pair in _mips)
                if (!pair.Value.Started && !pair.Value.Stop.IsCancellationRequested && !wanted.Contains(pair.Key))
                { pair.Value.Stop.Cancel(); Interlocked.Increment(ref _discardedMips); }
    }

    private async Task<Mip> ReadRequestedMip(Header header, int level, MipRequest request)
    {
        var reserved = false;
        try
        {
            await _mipSlots.WaitAsync(request.Stop.Token).ConfigureAwait(false);
            reserved = true;
            await _mipReader.WaitAsync(request.Stop.Token).ConfigureAwait(false);
            try
            {
                lock (_requestsGate) { request.Stop.Token.ThrowIfCancellationRequested(); request.Started = true; }
                byte[]? pixels; int width, height;
                try { pixels = ReadMip(header, level, out width, out height); }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    // A crash, interrupted write or eviction loses only derived data. Rebuild
                    // from verified owner bytes here, never fall back to frame-thread decoding.
                    var repaired = LoadOrBuild(header.Sha);
                    if (repaired is null) return new Mip(null, 0, 0, reserved);
                    pixels = ReadMip(repaired, level, out width, out height);
                }
                return new Mip(pixels, width, height, reserved);
            }
            finally { _mipReader.Release(); }
        }
        catch (OperationCanceledException)
        {
            if (reserved) _mipSlots.Release();
            return new Mip(null, 0, 0, false, Cancelled: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { return new Mip(null, 0, 0, reserved); }
    }

    public bool TryTakeMip(string sha, int level, out Mip? mip)
    {
        lock (_requestsGate)
        {
            mip = null;
            var key = (sha.ToLowerInvariant(), level);
            if (!_mips.TryGetValue(key, out var request) || !request.Task.IsCompletedSuccessfully) return false;
            if (!_mips.TryRemove(key, out request)) return false;
            mip = request.Task.Result;
            if (mip.Reserved) _mipSlots.Release();
            request.Stop.Dispose();
            return true;
        }
    }

    public IEnumerable<(string Sha, int Level)> ReadyMips => _mips
        .Where(pair => pair.Value.Task.IsCompletedSuccessfully).Select(pair => pair.Key);

    private async Task Work()
    {
        await foreach (var (sha, completion, previewCompletion) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Header? held = null; SKColor? preview = null;
            try
            {
                if (!_disposed && _catalog.Find(sha) is { } record)
                {
                    held = TryReadHeader(sha, record);
                    if (held is not null) { preview = new SKColor(held.Color); Interlocked.Increment(ref _diskHits); }
                    else
                    {
                        using var bitmap = SKBitmap.Decode(_catalog.ThumbnailPath(record));
                        if (bitmap is not null)
                        {
                            using var image = SKImage.FromBitmap(bitmap);
                            preview = SceneRenderer.StampOverviewColor(image);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { }
            previewCompletion.TrySetResult(preview);
            if (_disposed || held is not null) CompleteHeader(sha, completion, held);
            else if (!_buildQueue.Writer.TryWrite((sha, completion))) CompleteHeader(sha, completion, null);
        }
        _buildQueue.Writer.TryComplete();
    }

    private async Task BuildWork()
    {
        await foreach (var (sha, completion) in _buildQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Header? result = null;
            try { if (!_disposed) result = LoadOrBuild(sha); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or ArgumentException or OverflowException) { }
            CompleteHeader(sha, completion, result);
        }
    }

    private string Stem(string sha) => Path.Combine(_root, "v" + Version, sha.ToLowerInvariant());

    // Watching happens on workers, never inside a canvas getter. Catalog roots cover original
    // and ladder files with one handle per library; external linked files use their directory.
    private void Watch(AssetRecord record, string path)
    {
        _watchedFiles[path] = record.Sha256;
        var root = Path.GetFullPath(_catalog.RootFor(record));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            root = Path.GetDirectoryName(path)!;
        lock (_watchers)
        {
            if (_disposed || _watchers.ContainsKey(root)) return;
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, args) => FileChanged(args.FullPath);
            watcher.Created += (_, args) => FileChanged(args.FullPath);
            watcher.Deleted += (_, args) => FileChanged(args.FullPath);
            watcher.Renamed += (_, args) => { FileChanged(args.OldFullPath); FileChanged(args.FullPath); };
            watcher.Error += (_, _) =>
            {
                // A disconnected drive or overflow loses the notification history. Drop the
                // broken watcher so the next catalog reconnect/request can register it again.
                lock (_watchers)
                    if (_watchers.TryGetValue(root, out var current) && ReferenceEquals(current, watcher)) _watchers.Remove(root);
                watcher.Dispose();
                foreach (var path in _watchedFiles.Keys) FileChanged(path);
            };
            _watchers[root] = watcher;
            watcher.EnableRaisingEvents = true;
        }
    }

    private void FileChanged(string path)
    {
        if (_disposed || !_watchedFiles.TryGetValue(path, out var sha)) return;
        lock (_requestsGate)
        {
            Interlocked.Increment(ref _generation);
            _requests.TryRemove(sha, out _); _previews.TryRemove(sha, out _);
            foreach (var key in _mips.Keys.Where(key => string.Equals(key.Sha, sha, StringComparison.OrdinalIgnoreCase)))
                if (_mips.TryRemove(key, out var request)) Retire(request);
            _invalidated[sha] = 0;
        }
        SourceChanged?.Invoke();
    }

    private Header? TryReadHeader(string sha, AssetRecord record)
    {
        var stem = Stem(sha);
        try
        {
            if (_catalog.SourcePath(record) is { } source) Watch(record, source);
            if (_catalog.TryDetailLevelPath(record, 1, out var step)) Watch(record, step);
            var info = new FileInfo(stem + ".json");
            if (info.Exists && info.Length <= 16 * 1024)
            {
                var held = JsonSerializer.Deserialize<Header>(File.ReadAllBytes(info.FullName));
                if (held is { Version: Version, Width: > 0 and <= 32768, Height: > 0 and <= 32768,
                        MipCount: > 0 and <= 16, FirstMip: >= 0 and <= 15, PixelHash.Length: 64 }
                    && string.Equals(held.Sha, sha, StringComparison.OrdinalIgnoreCase)
                    && held.Original is not null && held.Working is not null
                    && string.Equals(held.Original.Path, _catalog.SourcePath(record), StringComparison.OrdinalIgnoreCase)
                    && held.Original.Matches() && held.Working.Matches()
                    && File.Exists(stem + ".pixels"))
                {
                    File.SetLastWriteTimeUtc(info.FullName, DateTime.UtcNow);
                    return held;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        return null;
    }

    private Header? LoadOrBuild(string sha)
    {
        var record = _catalog.Find(sha);
        if (record is null) return null;
        var stem = Stem(sha);
        // ReadVerified returns the same bytes it checked. Do not resolve and reopen the source.
        var original = FileStamp.Read(_catalog.SourcePath(record));
        if (original is null) return null;
        var payload = _catalog.ReadVerified(record);
        if (payload is not { Status: AssetResolutionStatus.Available, Content: { } encoded, Path: { } path }) return null;
        if (!original.Matches() || !string.Equals(original.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return null;
        var working = original;
        if (_catalog.TryDetailLevelPath(record, 1, out var step))
        {
            working = FileStamp.Read(step);
            if (working is null) return null;
            encoded = AssetImagePolicy.ReadEncodedFile(step);
        }
        using var image = AssetImageCache.DecodeOwned(encoded);
        if (image is null || !original.Matches() || !working.Matches()) return null;
        var color = SceneRenderer.StampOverviewColor(image);
        using var raw = new MemoryStream();
        using var writer = new BinaryWriter(raw, System.Text.Encoding.UTF8, leaveOpen: true);
        // Raw premultiplied bytes avoid the unpremultiply/re-premultiply rounding of PNG.
        // Every halving is the same Linear path used by AssetImageCache.GetForPixelSize.
        var first = 0;
        while (Math.Max(image.Width >> first, image.Height >> first) > 256) first++;
        var count = 0;
        SKImage current = image;
        try
        {
            var last = Math.Min(15, (int)Math.Floor(Math.Log2(Math.Min(image.Width, image.Height))));
            for (var level = 0; level <= last; level++)
            {
                var width = Math.Max(1, image.Width >> level);
                var height = Math.Max(1, image.Height >> level);
                if (level > 0)
                {
                    using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
                    using var pixels = bitmap.PeekPixels();
                    if (!current.ScalePixels(pixels, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))) return null;
                    bitmap.SetImmutable();
                    var next = SKImage.FromBitmap(bitmap);
                    if (!ReferenceEquals(current, image)) current.Dispose();
                    current = next;
                }
                if (level >= first)
                {
                    using var pixels = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
                    if (!current.ReadPixels(pixels.Info, pixels.GetPixels(), pixels.RowBytes, 0, 0)) return null;
                    var bytes = pixels.Bytes;
                    writer.Write(level); writer.Write(width); writer.Write(height); writer.Write(bytes.Length); writer.Write(bytes);
                    count++;
                }
                if (width == 1 && height == 1) break;
            }
        }
        finally { if (!ReferenceEquals(current, image)) current.Dispose(); }
        var content = raw.ToArray();
        var header = new Header(Version, sha, image.Width, image.Height, (uint)color, original, working,
            first, count, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)));
        if (!original.Matches() || !working.Matches()) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        var temporary = stem + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var previousBytes = FileBytes(stem + ".pixels") + FileBytes(stem + ".json");
        try
        {
            using (var file = File.Create(temporary))
            using (var compressed = new BrotliStream(file, CompressionLevel.Fastest)) compressed.Write(content);
            File.Move(temporary, stem + ".pixels", overwrite: true);
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(header));
            File.Move(temporary, stem + ".json", overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Interlocked.Add(ref _diskBytes, FileBytes(stem + ".pixels") + FileBytes(stem + ".json") - previousBytes);
        if (Interlocked.Increment(ref _builds) % 64 == 0 || DiskBytes > _diskBudget) TrimDisk();
        return header;
    }

    private static long FileBytes(string path)
    {
        try { return new FileInfo(path) is { Exists: true } file ? file.Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private void TrimDisk()
    {
        lock (_diskGate)
        {
            var folder = Path.Combine(_root, "v" + Version);
            if (!Directory.Exists(folder)) return;
            try
            {
                var files = new DirectoryInfo(folder).GetFiles();
                foreach (var file in files.Where(f => f.Name.EndsWith(".tmp", StringComparison.Ordinal)
                    && f.Name.Length == 101 && f.Name[..64].All(Uri.IsHexDigit)
                    && f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)))
                    try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                var entries = files.Where(f => f.Name.Length is 69 or 71 && f.Name[..64].All(Uri.IsHexDigit)
                    && (f.Extension == ".json" || f.Extension == ".pixels"))
                    .GroupBy(f => f.Name[..64], StringComparer.OrdinalIgnoreCase)
                    .Select(group => new { Files = group.ToArray(), Used = group.Max(f => f.LastWriteTimeUtc), Bytes = group.Sum(f => f.Length) })
                    .OrderBy(entry => entry.Used).ToArray();
                var bytes = entries.Sum(entry => entry.Bytes);
                foreach (var entry in entries)
                {
                    if (bytes <= _diskBudget) break;
                    var removed = false;
                    foreach (var file in entry.Files)
                        try { var length = file.Length; file.Delete(); bytes -= length; removed = true; }
                        catch (IOException) { } catch (UnauthorizedAccessException) { }
                    if (removed) Interlocked.Increment(ref _diskEvictions);
                }
                Interlocked.Exchange(ref _diskBytes, bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Background/test reader. Never called by a canvas getter.</summary>
    public byte[]? ReadMip(Header header, int level, out int width, out int height)
    {
        width = height = 0;
        using var file = File.OpenRead(Stem(header.Sha) + ".pixels");
        using var compressed = new BrotliStream(file, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = compressed.Read(buffer)) > 0)
        {
            if (raw.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("Oversized derived mip data.");
            raw.Write(buffer, 0, read);
        }
        var bytes = raw.ToArray();
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != header.PixelHash)
            throw new InvalidDataException("Derived mip checksum mismatch.");
        using var reader = new BinaryReader(new MemoryStream(bytes));
        for (var i = 0; i < header.MipCount; i++)
        {
            var current = reader.ReadInt32(); var w = reader.ReadInt32(); var h = reader.ReadInt32(); var length = reader.ReadInt32();
            if (w is < 1 or > 256 || h is < 1 or > 256 || length != w * h * 4) throw new InvalidDataException("Invalid derived mip.");
            var pixels = reader.ReadBytes(length);
            if (pixels.Length != length) throw new InvalidDataException("Truncated derived mip.");
            if (current == level) { width = w; height = h; return pixels; }
        }
        return null;
    }

    public void Dispose()
    {
        _disposed = true; _stop.Cancel(); _queue.Writer.TryComplete();
        Forget();
        lock (_watchers) { foreach (var watcher in _watchers.Values) watcher.Dispose(); _watchers.Clear(); }
        _watchedFiles.Clear(); SourceChanged = null;
    }
}
