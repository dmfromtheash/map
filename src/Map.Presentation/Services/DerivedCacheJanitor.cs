using System.Diagnostics;
using System.Globalization;

namespace Map.Presentation.Services;

public sealed record DerivedCacheJanitorOptions
{
    // Decimal GB for disk space: 5 GB ceiling and 10 GB free-space reserve.
    public long MaximumBytes { get; init; } = 5_000_000_000;
    public decimal FreeSpaceFraction { get; init; } = 0.10m;
    public long MinimumFreeBytes { get; init; } = 10_000_000_000;
    public int CurrentVersion { get; init; } = 1;
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan TemporaryFileGrace { get; init; } = TimeSpan.FromHours(1);
}

public sealed record DerivedCacheJanitorReport
{
    public bool LockAcquired { get; internal init; }
    public bool ScanComplete { get; internal init; }
    public bool CleanupTriggered { get; internal init; }
    public long AvailableFreeBytes { get; internal init; }
    public long CeilingBytes { get; internal init; }
    public long TargetBytes { get; internal init; }
    public long BytesBefore { get; internal init; }
    public long FilesDeleted { get; internal init; }
    public long BytesDeleted { get; internal init; }
    public long FilesKept { get; internal init; }
    public long BytesKept { get; internal init; }
    public int VersionFoldersDeleted { get; internal init; }
    public bool ReserveSatisfied { get; internal init; }
    public bool CeilingSatisfied => ScanComplete && BytesKept <= CeilingBytes && ReserveSatisfied;
    public IReadOnlyList<string> Errors { get; internal init; } = Array.Empty<string>();
}

/// <summary>
/// Explicit background maintenance for one derived-cache root, not a writer/admission controller.
/// Only canonical store filenames in v1..vCurrentVersion are owned. No recursive deletion.
/// The store touches the header's last-write time on a disk hit; use the newest pair timestamp
/// as LRU, rather than filesystem access times (often disabled) or another side file.
/// </summary>
public sealed class DerivedCacheJanitor
{
    public const string LockFileName = ".derived-janitor.lock";
    private readonly string _root;
    private readonly DerivedCacheJanitorOptions _options;
    private readonly Func<long> _availableFreeBytes;

    /// <param name="availableFreeBytes">Test seam; production uses the containing DriveInfo volume.</param>
    public DerivedCacheJanitor(string root, DerivedCacheJanitorOptions? options = null,
        Func<long>? availableFreeBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _options = options ?? new();
        Validate(_options);
        _availableFreeBytes = availableFreeBytes ?? ReadAvailableFreeBytes;
    }

    /// <summary>
    /// Free space already excludes the cache. The reserve therefore permits at most
    /// currentCacheBytes + free - reserve bytes to remain, clamped to zero.
    /// A zero result means no cache allocation is allowed, even if cleanup cannot restore reserve.
    /// </summary>
    public static long CalculateCeiling(DerivedCacheJanitorOptions options, long availableFreeBytes,
        long currentCacheBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        ArgumentOutOfRangeException.ThrowIfNegative(availableFreeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(currentCacheBytes);
        return (long)Math.Min(options.MaximumBytes, Math.Min(
            decimal.Floor(availableFreeBytes * options.FreeSpaceFraction),
            Math.Max(0m, (decimal)currentCacheBytes + availableFreeBytes - options.MinimumFreeBytes)));
    }

    // Task.Run also moves the initial directory/drive queries and lock wait off the caller thread.
    public Task<DerivedCacheJanitorReport> CleanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Clean(cancellationToken), cancellationToken);

    private DerivedCacheJanitorReport Clean(CancellationToken cancellationToken)
    {
        var errors = new HashSet<string>(StringComparer.Ordinal);
        var complete = true;
        long deletedFiles = 0, deletedBytes = 0;
        var removedFolders = 0;
        FileStream? cleanerLock = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckPath(_root, directory: true);
            var watch = Stopwatch.StartNew();
            var lockPath = Path.Combine(_root, LockFileName);
            while (cleanerLock is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckPath(_root, directory: true);
                CheckOptionalFile(lockPath);
                try
                {
                    cleanerLock ??= new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    CheckPath(lockPath, directory: false);
                }
                catch (IOException ex)
                {
                    if (cleanerLock is not null) throw;
                    if (watch.Elapsed >= _options.LockTimeout)
                        return new() { Errors = new[] { "Cleaner lock timeout: " + ex.Message } };
                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(25));
                }
            }

            var before = Scan();
            var folders = before.Folders;
            // Obsolete versions, stale interrupted writes and stale singletons are maintenance,
            // independent of pressure. Young temporary files protect their entire SHA group.
            foreach (var entry in before.Entries.Where(e => e.Version < _options.CurrentVersion || e.Maintenance))
                Delete(entry);
            foreach (var folder in folders.Where(f => f.Version < _options.CurrentVersion))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    CheckPath(folder.Path, directory: true);
                    if (Directory.EnumerateFileSystemEntries(folder.Path).Any()) continue;
                    Directory.Delete(folder.Path, recursive: false);
                    removedFolders++;
                }
                catch (Exception ex) when (IsFileError(ex)) { Error(folder.Path, ex); }
            }

            var current = Scan();
            var free = _availableFreeBytes();
            var ceiling = CalculateCeiling(_options, free, current.Bytes);
            var target = (long)decimal.Floor(ceiling * 0.70m);
            var triggered = current.Bytes > ceiling * 0.90m;
            var remaining = current.Bytes;
            if (triggered)
            {
                foreach (var entry in current.Entries.OrderBy(e => e.Used).ThenBy(e => e.Files[0].Path, StringComparer.Ordinal))
                {
                    if (remaining <= target) break;
                    remaining -= Delete(entry);
                }
            }

            var kept = Scan();
            return new()
            {
                LockAcquired = true, ScanComplete = complete, CleanupTriggered = triggered,
                AvailableFreeBytes = free, CeilingBytes = ceiling, TargetBytes = target,
                BytesBefore = before.Bytes, FilesDeleted = deletedFiles, BytesDeleted = deletedBytes,
                FilesKept = kept.Files, BytesKept = kept.Bytes, VersionFoldersDeleted = removedFolders,
                ReserveSatisfied = _availableFreeBytes() >= _options.MinimumFreeBytes,
                Errors = errors.ToArray()
            };
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            Error(_root, ex);
            return new() { LockAcquired = cleanerLock is not null, FilesDeleted = deletedFiles,
                BytesDeleted = deletedBytes, VersionFoldersDeleted = removedFolders, Errors = errors.ToArray() };
        }
        finally
        {
            // Keep the lock inode: unlinking it lets another cleaner lock a different inode.
            cleanerLock?.Dispose();
        }

        void Error(string path, Exception ex) => errors.Add(path + ": " + ex.Message);

        Snapshot Scan()
        {
            var entries = new List<Entry>();
            var folders = new List<VersionFolder>();
            CheckPath(_root, directory: true);
            foreach (var directory in Directory.EnumerateFileSystemEntries(_root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryVersion(Path.GetFileName(directory), out var version) || version > _options.CurrentVersion) continue;
                try
                {
                    CheckPath(directory, directory: true);
                    folders.Add(new(directory, version));
                    var files = new List<CacheFile>();
                    var blocked = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = Path.GetFileName(path);
                        if (!OwnedName(name, out var temporary)) continue;
                        try
                        {
                            CheckPath(path, directory: false);
                            var info = new FileInfo(path);
                            files.Add(new(path, name[..64], info.Length, info.LastWriteTimeUtc, temporary));
                        }
                        catch (Exception ex) when (IsFileError(ex))
                        { blocked.Add(name[..64]); complete = false; Error(path, ex); }
                    }
                    var staleBefore = DateTime.UtcNow - _options.TemporaryFileGrace;
                    foreach (var group in files.GroupBy(f => f.Sha, StringComparer.Ordinal))
                    {
                        var active = blocked.Contains(group.Key) || group.Any(f => f.Temporary && f.Written > staleBefore);
                        foreach (var temporary in group.Where(f => f.Temporary))
                            entries.Add(new(version, new[] { temporary }, !active, true));
                        var pair = group.Where(f => !f.Temporary).OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
                        if (pair.Length > 0)
                            entries.Add(new(version, pair, !active && (pair.Length == 2 || pair.All(f => f.Written <= staleBefore)),
                                pair.Length != 2));
                    }
                }
                catch (Exception ex) when (IsFileError(ex)) { complete = false; Error(directory, ex); }
            }
            return new(entries, folders);
        }

        long Delete(Entry entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.Eligible) return 0;
            var handles = new List<FileStream>();
            var staged = new List<(CacheFile File, string Staged)>();
            long removed = 0;
            try
            {
                // Open ALL members before changing ANY names. Read/write sharing is denied;
                // delete sharing permits our reversible rename on Windows while handles stay open.
                foreach (var file in entry.Files)
                {
                    CheckPath(file.Path, directory: false);
                    if ((File.GetAttributes(file.Path) & FileAttributes.ReadOnly) != 0)
                        throw new IOException("Read-only cache file.");
                    handles.Add(new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Delete));
                    var info = new FileInfo(file.Path);
                    if (info.Length != file.Bytes || info.LastWriteTimeUtc != file.Written)
                        throw new IOException("Cache file changed since scan.");
                }
                // An active publisher reuses this exact temporary naming scheme. Recheck after
                // acquiring both handles; a janitor lock does not lock StampDerivedAssetStore.
                var parent = Path.GetDirectoryName(entry.Files[0].Path)!;
                CheckPath(parent, directory: true);
                foreach (var path in Directory.EnumerateFileSystemEntries(parent))
                {
                    var name = Path.GetFileName(path);
                    if (!OwnedName(name, out var temp) || !temp || name[..64] != entry.Files[0].Sha) continue;
                    CheckPath(path, directory: false);
                    if (File.GetLastWriteTimeUtc(path) > DateTime.UtcNow - _options.TemporaryFileGrace)
                        throw new IOException("Active cache write; SHA group kept.");
                }
                // JSON sorts before pixels. Remove publication header first, then pixels.
                // If staging fails, restore names before releasing handles. No cancellation in
                // this short transaction: cancellation between the two renames would split a pair.
                foreach (var file in entry.Files)
                {
                    CheckPath(file.Path, directory: false);
                    var temporary = Path.Combine(parent, file.Sha + "." + Guid.NewGuid().ToString("N") + ".tmp");
                    File.Move(file.Path, temporary, overwrite: false);
                    staged.Add((file, temporary));
                }
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                Error(entry.Files[0].Path, ex);
                foreach (var item in staged.AsEnumerable().Reverse())
                {
                    try
                    {
                        CheckPath(item.Staged, directory: false);
                        File.Move(item.Staged, item.File.Path, overwrite: false);
                    }
                    catch (Exception rollback) when (IsFileError(rollback))
                    { complete = false; Error(item.File.Path, rollback); }
                }
                foreach (var handle in handles) handle.Dispose();
                return 0;
            }

            try
            {
                foreach (var item in staged)
                {
                    try
                    {
                        CheckPath(item.Staged, directory: false);
                        File.Delete(item.Staged);
                        deletedFiles++; deletedBytes += item.File.Bytes; removed += item.File.Bytes;
                    }
                    catch (Exception ex) when (IsFileError(ex)) { Error(item.Staged, ex); }
                }
                return removed;
            }
            finally { foreach (var handle in handles) handle.Dispose(); }
        }
    }

    private long ReadAvailableFreeBytes()
    {
        // The longest mount prefix also handles /Volumes/... on macOS, not just C:\ on Windows.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var drive = DriveInfo.GetDrives().Where(d => _root.Equals(Path.TrimEndingDirectorySeparator(d.Name), comparison)
                || _root.StartsWith(Path.EndsInDirectorySeparator(d.Name) ? d.Name : d.Name + Path.DirectorySeparatorChar, comparison))
            .MaxBy(d => d.Name.Length) ?? throw new IOException("Cache volume not found.");
        return drive.AvailableFreeSpace;
    }

    private static void CheckOptionalFile(string path)
    {
        try { CheckPath(path, directory: false); }
        catch (FileNotFoundException) { }
    }

    private static void CheckPath(string path, bool directory)
    {
        // Check from filesystem root downward, including ancestors of the configured cache root.
        // Never enumerate a directory through a known symlink/junction. Recheck before mutations.
        var chain = new Stack<string>();
        for (var current = path; current is not null; current = Path.GetDirectoryName(current)) chain.Push(current);
        while (chain.TryPop(out var current))
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse point skipped: " + current);
            var expectDirectory = current != path || directory;
            if (((attributes & FileAttributes.Directory) != 0) != expectDirectory)
                throw new IOException("Unexpected cache path type: " + current);
        }
    }

    private static bool OwnedName(string name, out bool temporary)
    {
        temporary = false;
        if (name.Length is not (69 or 71 or 101) || !LowerHex(name.AsSpan(0, 64))) return false;
        if (name.Length == 69) return name.EndsWith(".json", StringComparison.Ordinal);
        if (name.Length == 71) return name.EndsWith(".pixels", StringComparison.Ordinal);
        temporary = name[64] == '.' && LowerHex(name.AsSpan(65, 32)) && name.EndsWith(".tmp", StringComparison.Ordinal);
        return temporary;
    }

    private static bool LowerHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text) if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    private static bool TryVersion(string name, out int version)
    {
        version = 0;
        return name.Length > 1 && name[0] == 'v' && name[1] is >= '1' and <= '9'
            && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    private static void Validate(DerivedCacheJanitorOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MinimumFreeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.CurrentVersion);
        if (options.FreeSpaceFraction is < 0 or > 1 || options.LockTimeout < TimeSpan.Zero
            || options.TemporaryFileGrace < TimeSpan.Zero || options.TemporaryFileGrace > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException;
    private sealed record CacheFile(string Path, string Sha, long Bytes, DateTime Written, bool Temporary);
    private sealed record VersionFolder(string Path, int Version);
    private sealed record Entry(int Version, CacheFile[] Files, bool Eligible, bool Maintenance)
    {
        public DateTime Used => Files.Max(f => f.Written);
    }
    private sealed record Snapshot(List<Entry> Entries, List<VersionFolder> Folders)
    {
        public long Bytes => Entries.Sum(e => e.Files.Sum(f => f.Bytes));
        public long Files => Entries.Sum(e => (long)e.Files.Length);
    }
}
