using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Map.Export;

/// <summary>Derived, disposable PNG tiles for native viewing. Level zero preserves every source pixel.</summary>
public sealed record PngTilePyramid(string DirectoryPath, int Width, int Height, int TileEdge, int Levels)
{
    private const int Version = 2;
    private const int Edge = 1024;
    private static readonly SemaphoreSlim BuildGate = new(1, 1);

    public (int Width, int Height) LevelSize(int level)
    {
        if (level < 0 || level >= Levels) throw new ArgumentOutOfRangeException(nameof(level));
        var divisor = 1 << level;
        return ((Width + divisor - 1) / divisor, (Height + divisor - 1) / divisor);
    }

    public string TilePath(int level, int column, int row)
    {
        var size = LevelSize(level);
        if (column < 0 || row < 0 || column >= (size.Width + TileEdge - 1) / TileEdge
            || row >= (size.Height + TileEdge - 1) / TileEdge) throw new ArgumentOutOfRangeException(nameof(column));
        return Path.Combine(DirectoryPath, level.ToString(), $"{column}_{row}.png");
    }

    public static async Task<PngTilePyramid> OpenAsync(string sourcePath, string cacheRoot,
        IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default,
        ExportEngineSettings? settings = null)
    {
        progress?.Report(new(ExportPhase.Preparing, 0, "Подготовка просмотра…"));
        await BuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => OpenCore(sourcePath, cacheRoot, progress, cancellationToken,
                settings ?? new ExportEngineSettings()), cancellationToken).ConfigureAwait(false);
        }
        finally { BuildGate.Release(); }
    }

    private static PngTilePyramid OpenCore(string sourcePath, string cacheRoot,
        IProgress<ExportProgress>? progress, CancellationToken token, ExportEngineSettings settings)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        cacheRoot = Path.GetFullPath(cacheRoot);
        using var source = new PngPixelReader(sourcePath);
        var sourceFile = new FileInfo(sourcePath);
        var identity = $"{Version}\n{sourcePath}\n{sourceFile.Length}\n{sourceFile.LastWriteTimeUtc.Ticks}";
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var finalPath = Path.Combine(cacheRoot, key);
        var levels = 1;
        var width = source.Width; var height = source.Height;
        long columns = (width + Edge - 1) / Edge;
        while (Math.Max(width, height) > Edge)
        {
            width = (width + 1) / 2; height = (height + 1) / 2;
            columns += (width + Edge - 1) / Edge;
            levels++;
        }
        var result = new PngTilePyramid(finalPath, source.Width, source.Height, Edge, levels);
        var manifestPath = Path.Combine(finalPath, "manifest.json");
        if (File.Exists(manifestPath) && new FileInfo(manifestPath).Length < 16384)
        {
            Manifest? manifest;
            try { manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath)); }
            catch (JsonException ex) { throw new InvalidDataException("Данные просмотра повреждены: " + finalPath, ex); }
            if (manifest == new Manifest(Version, key, source.Width, source.Height, Edge, levels))
            {
                // A manifest is published last, but the user or a disk failure can remove tiles later.
                for (var level = 0; level < levels; level++)
                {
                    var size = result.LevelSize(level);
                    for (var y = 0; y < size.Height; y += Edge)
                    for (var x = 0; x < size.Width; x += Edge)
                    {
                        token.ThrowIfCancellationRequested();
                        result.CheckTile(level, x / Edge, y / Edge);
                    }
                }
                progress?.Report(new(ExportPhase.Completed, 1, "Данные просмотра готовы."));
                return result;
            }
            throw new InvalidDataException("Данные просмотра повреждены. Выберите другую папку кэша или удалите эти данные просмотра.");
        }
        // Per-column PNG/zlib buffers are bounded; no full-width band is kept in memory.
        var memoryNeeded = columns * 512 * 1024 + 16L * 1024 * 1024;
        var available = settings.AvailableMemoryBytes();
        if (memoryNeeded > available - available / 4)
            throw new ExportValidationException("Недостаточно свободной памяти для подготовки просмотра с резервом. Освободите память и повторите.");
        Directory.CreateDirectory(cacheRoot);
        var diskEstimate = Math.Min(checked(sourceFile.Length * 2 + 64L * 1024 * 1024),
            checked((long)source.Width * source.Height * 6 + 64L * 1024 * 1024));
        if (settings.AvailableDiskBytes(cacheRoot) < diskEstimate)
            throw new ExportValidationException($"Для данных просмотра нужно ориентировочно {diskEstimate / (1024d * 1024 * 1024):N1} ГиБ свободного места рядом с кэшем.");
        var temporaryPath = Path.Combine(cacheRoot, key + ".building-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryPath);
        try
        {
            using (var encoder = new LevelEncoder(temporaryPath, source.Width, source.Height, 0))
            {
                var rows = 0;
                source.ReadRows(row =>
                {
                    token.ThrowIfCancellationRequested();
                    if (rows % Edge == 0 && settings.AvailableDiskBytes(cacheRoot) < 64L * 1024 * 1024)
                        throw new IOException("Место для данных просмотра закончилось. Исходный PNG не изменён.");
                    encoder.AddRow(row);
                    rows++;
                    if (rows % 64 == 0 || rows == source.Height)
                        progress?.Report(new(ExportPhase.EncodingAndWriting, .98 * rows / source.Height,
                            $"Подготовка чёткого зума: {rows:N0} из {source.Height:N0} строк. PNG остаётся без изменений."));
                }, token);
                encoder.Complete();
            }
            token.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(temporaryPath, "manifest.json"),
                JsonSerializer.Serialize(new Manifest(Version, key, source.Width, source.Height, Edge, levels)));
            Directory.Move(temporaryPath, finalPath);
            progress?.Report(new(ExportPhase.Completed, 1, "Карта готова к просмотру с приближением."));
            return result;
        }
        finally
        {
            // Only this invocation's generated GUID directory can be removed; never source/user files.
            try { if (Directory.Exists(temporaryPath)) Directory.Delete(temporaryPath, recursive: true); }
            catch (IOException) { /* An incomplete directory has no published manifest and is never used. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Validate dimensions before a UI codec is allowed to allocate any tile pixels.</summary>
    public string CheckTile(int level, int column, int row)
    {
        var path = TilePath(level, column, row);
        var size = LevelSize(level);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 5L * 1024 * 1024)
            throw new InvalidDataException("Не найден или повреждён участок просмотра: " + path);
        using var header = new PngPixelReader(path);
        if (header.Width != Math.Min(TileEdge, size.Width - column * TileEdge)
            || header.Height != Math.Min(TileEdge, size.Height - row * TileEdge))
            throw new InvalidDataException("Размер участка просмотра повреждён: " + path);
        return path;
    }

    private sealed record Manifest(int Version, string SourceKey, int Width, int Height, int TileEdge, int Levels);

    private sealed class LevelEncoder : IDisposable
    {
        private readonly string _directory;
        private readonly int _width, _height;
        private readonly LevelEncoder? _next;
        private readonly byte[] _previous, _reduced;
        private readonly List<(FileStream File, StreamingPngWriter Png, int Bytes)> _writers = [];
        private int _rows;

        public LevelEncoder(string root, int width, int height, int level)
        {
            _directory = Path.Combine(root, level.ToString());
            Directory.CreateDirectory(_directory);
            _width = width; _height = height;
            if (Math.Max(width, height) > Edge)
            {
                _next = new(root, (width + 1) / 2, (height + 1) / 2, level + 1);
                _previous = new byte[width * 4]; _reduced = new byte[((width + 1) / 2) * 4];
            }
            else { _previous = []; _reduced = []; }
        }

        public void AddRow(ReadOnlySpan<byte> row)
        {
            if (_rows >= _height || row.Length != _width * 4) throw new InvalidDataException("Размер строки просмотра не совпал.");
            if (_rows % Edge == 0)
            {
                for (var x = 0; x < _width; x += Edge)
                {
                    var file = new FileStream(Path.Combine(_directory, $"{x / Edge}_{_rows / Edge}.png"),
                        FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan);
                    try
                    {
                        var w = Math.Min(Edge, _width - x);
                        _writers.Add((file, new StreamingPngWriter(file, w, Math.Min(Edge, _height - _rows)), w * 4));
                    }
                    catch { file.Dispose(); throw; }
                }
            }
            var offset = 0;
            foreach (var writer in _writers)
            {
                writer.Png.WriteRow(row.Slice(offset, writer.Bytes)); offset += writer.Bytes;
            }
            if (_next is not null)
            {
                if (_rows % 2 == 0) row.CopyTo(_previous);
                if (_rows % 2 == 1 || _rows == _height - 1)
                {
                    Reduce(_previous, row, _reduced);
                    _next.AddRow(_reduced);
                }
            }
            _rows++;
            if (_rows % Edge == 0 || _rows == _height)
            {
                foreach (var writer in _writers) writer.Png.Complete();
                CloseWriters();
            }
        }

        public void Complete()
        {
            if (_rows != _height || _writers.Count != 0) throw new InvalidDataException("Не все строки просмотра записаны.");
            _next?.Complete();
        }

        private static void Reduce(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, Span<byte> output)
        {
            var a = MemoryMarshal.Cast<byte, uint>(first);
            var b = MemoryMarshal.Cast<byte, uint>(second);
            var result = MemoryMarshal.Cast<byte, uint>(output);
            for (var x = 0; x < result.Length; x++)
            {
                var i = x * 2; var j = Math.Min(i + 1, a.Length - 1);
                var p = a[i]; var q = a[j]; var r = b[i]; var s = b[j];
                if (((p & q & r & s) >> 24) == 255)
                {
                    var rb = ((p & 0x00ff00ff) + (q & 0x00ff00ff) + (r & 0x00ff00ff) + (s & 0x00ff00ff) + 0x00020002) >> 2;
                    var ga = (((p >> 8) & 0x00ff00ff) + ((q >> 8) & 0x00ff00ff) + ((r >> 8) & 0x00ff00ff) + ((s >> 8) & 0x00ff00ff) + 0x00020002) >> 2;
                    result[x] = (rb & 0x00ff00ff) | ((ga & 0x00ff00ff) << 8);
                }
                else
                {
                    var pa = p >> 24; var qa = q >> 24; var ra = r >> 24; var sa = s >> 24;
                    var alpha = pa + qa + ra + sa;
                    uint value = ((alpha + 2) / 4) << 24;
                    if (alpha != 0)
                        for (var shift = 0; shift < 24; shift += 8)
                            value |= (((((p >> shift) & 255) * pa + ((q >> shift) & 255) * qa
                                + ((r >> shift) & 255) * ra + ((s >> shift) & 255) * sa + alpha / 2) / alpha) << shift);
                    result[x] = value;
                }
            }
        }

        private void CloseWriters()
        {
            Exception? failure = null;
            foreach (var writer in _writers)
            {
                try { writer.Png.Dispose(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { failure ??= ex; }
                try { writer.File.Dispose(); } catch (IOException ex) { failure ??= ex; }
            }
            _writers.Clear();
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        public void Dispose() { try { CloseWriters(); } finally { _next?.Dispose(); } }
    }
}
