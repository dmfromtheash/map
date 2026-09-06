using System.Globalization;
using System.Diagnostics;
using System.Text.Json;

namespace Map.Export;

/// <summary>Retention for reproducible PNG viewing tiles, never source images or map documents.</summary>
public static class PngViewCache
{
    private const string EvictingSuffix = ".evicting-";
    public const long RetainedBytesLimit = 2L * 1024 * 1024 * 1024;
    public readonly record struct TrimResult(long RemovedBytes, long RetainedBytes, bool InUse);

    public static TrimResult TrimWhenIdle(string cacheRoot)
    {
        // Older Map builds do not hold usage leases. Conservatively defer their shared
        // profile cleanup while another Map process exists; never interrupt its viewer.
        var otherMap = false;
        foreach (var process in Process.GetProcessesByName("Map.App"))
        {
            using (process) otherMap |= process.Id != Environment.ProcessId;
        }
        return otherMap ? new(0, 0, true) : TrimUnused(cacheRoot);
    }

    // Shared file handles pin the root across processes, including while a pyramid is built.
    // The exclusive cleaner cannot run until all readers have released it. The persistent
    // marker is a sibling, outside the cache data, so it cannot be deleted during pruning.
    public static async Task<IDisposable> AcquireAsync(string cacheRoot, CancellationToken token = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        if (!SafeAncestors(root)) throw new IOException("Папка данных просмотра не должна быть ссылкой.");
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!SafeAncestors(root) || IsLink(root + ".usage"))
                throw new IOException("Папка данных просмотра не должна быть ссылкой.");
            try { return new FileStream(root + ".usage", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            { await Task.Delay(25, token).ConfigureAwait(false); }
        }
    }

    public static TrimResult TrimUnused(string cacheRoot, long limitBytes = RetainedBytesLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limitBytes);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        if (!Directory.Exists(root) || !SafeAncestors(root) || IsLink(root + ".usage")) return default;
        try
        {
            using var exclusive = new FileStream(root + ".usage", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var entries = new List<Entry>();
            foreach (var directory in Directory.EnumerateDirectories(root))
                if (Inspect(directory) is { } entry) entries.Add(entry);
            var total = entries.Sum(e => e.Bytes);
            var removed = 0L;
            foreach (var entry in entries.OrderByDescending(e => e.Retired).ThenBy(e => e.LastUsed))
            {
                if (!entry.Retired && total - removed <= limitBytes) break;
                // Delete only inspected files, then empty directories. An unexpected file
                // appearing concurrently is never swept up by a recursive delete.
                try
                {
                    if (!SafeAncestors(entry.Path)) continue;
                    var deleting = entry.Path;
                    if (!entry.Retired)
                    {
                        deleting += EvictingSuffix + Guid.NewGuid().ToString("N");
                        if (Path.GetDirectoryName(deleting) != root) continue;
                        // Retire the entry atomically before touching tiles. If a file is busy
                        // or cleanup is interrupted, the next open builds a complete cache;
                        // it never reads a partially evicted pyramid as a published result.
                        Directory.Move(entry.Path, deleting);
                    }
                    foreach (var file in entry.Files)
                    {
                        var target = Path.Combine(deleting, Path.GetRelativePath(entry.Path, file));
                        if (!SafeAncestors(Path.GetDirectoryName(target)!) || IsLink(target)) break;
                        var size = new FileInfo(target).Length;
                        File.Delete(target); removed += size;
                    }
                    foreach (var directory in Directory.EnumerateDirectories(deleting))
                        if (!IsLink(directory)) Directory.Delete(directory, recursive: false);
                    Directory.Delete(deleting, recursive: false);
                }
                catch (IOException) { /* A busy/changed entry is retried on the next idle cleanup. */ }
                catch (UnauthorizedAccessException) { }
            }
            return new(removed, Math.Max(0, total - removed), false);
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return new(0, 0, true); }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
    }

    internal static void Touch(string path)
    {
        try { Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Entry? Inspect(string path)
    {
        var name = Path.GetFileName(path);
        var retired = name.Length == 64 + EvictingSuffix.Length + 32
            && name.Substring(64, EvictingSuffix.Length) == EvictingSuffix
            && Guid.TryParseExact(name[(64 + EvictingSuffix.Length)..], "N", out _);
        var key = retired ? name[..64] : name;
        if (key.Length != 64 || key.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || IsLink(path)) return null;
        try
        {
            var manifestPath = Path.Combine(path, "manifest.json");
            var info = new FileInfo(manifestPath);
            if (!info.Exists || info.Length > 16384 || IsLink(manifestPath)) return null;
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath));
            if (manifest is not { Version: 2, TileEdge: 1024, Width: > 0, Height: > 0, Levels: >= 1 and <= 21 }
                || manifest.SourceKey != key) return null;
            var levels = 1;
            for (long size = Math.Max(manifest.Width, manifest.Height); size > 1024; size = (size + 1) / 2) levels++;
            if (levels != manifest.Levels) return null;
            var files = new List<string>();
            var bytes = info.Length;
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                if (IsLink(child)) return null;
                if (child == manifestPath) continue;
                if (!Directory.Exists(child) || !Number(Path.GetFileName(child), out var level) || level >= levels) return null;
                var width = ((long)manifest.Width + (1L << level) - 1) >> level;
                var height = ((long)manifest.Height + (1L << level) - 1) >> level;
                foreach (var tile in Directory.EnumerateFileSystemEntries(child))
                {
                    if (Directory.Exists(tile) || IsLink(tile) || Path.GetExtension(tile) != ".png") return null;
                    var parts = Path.GetFileNameWithoutExtension(tile).Split('_');
                    if (parts.Length != 2 || !Number(parts[0], out var column) || !Number(parts[1], out var row)
                        || column >= (width + 1023) / 1024 || row >= (height + 1023) / 1024) return null;
                    bytes += new FileInfo(tile).Length; files.Add(tile);
                }
            }
            files.Add(manifestPath); // Published metadata is removed only after its known tiles.
            return new(path, bytes, Directory.GetLastWriteTimeUtc(path), files, retired);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static bool Number(string value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number)
        && number >= 0 && value == number.ToString(CultureInfo.InvariantCulture);
    private static bool IsLink(string path) => (File.Exists(path) || Directory.Exists(path))
        && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool SafeAncestors(string path)
    {
        for (DirectoryInfo? part = new(path); part is not null; part = part.Parent)
            if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    private sealed record Manifest(int Version, string SourceKey, int Width, int Height, int TileEdge, int Levels);
    private sealed record Entry(string Path, long Bytes, DateTime LastUsed, List<string> Files, bool Retired);
}
