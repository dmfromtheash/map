using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Map.Core.Resources;

namespace Map.Presentation.Services;

public sealed record DerivedCacheJanitorOptions
{
    // Binary GiB like Windows Explorer's «ГБ»: 5 GiB ceiling and 10 GiB free-space reserve (PreparedDataBudget).
    public long MaximumBytes { get; init; } = PreparedDataBudget.DefaultTotalBytes;
    public decimal FreeSpaceFraction { get; init; } = PreparedDataBudget.DefaultFreeSpaceFraction;
    public long MinimumFreeBytes { get; init; } = PreparedDataBudget.DefaultMinimumFreeBytes;
    public int CurrentVersion { get; init; } = 1;
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan TemporaryFileGrace { get; init; } = TimeSpan.FromHours(1);
    /// <summary>
    /// When set, the ceiling is this share of the shared prepared-data budget (its policy replaces MaximumBytes,
    /// FreeSpaceFraction and MinimumFreeBytes) and the kept bytes are reported back to it after each cleanup.
    /// </summary>
    public PreparedDataBudget? Budget { get; init; }
    public PreparedDataShare Share { get; init; } = PreparedDataBudget.DerivedAssets;
    /// <summary>Eviction class of an entry; without it every entry is <see cref="DerivedCacheEntryClass.Live"/> (plain LRU).</summary>
    public Func<DerivedCacheEntry, DerivedCacheEntryClass>? Classify { get; init; }
}

/// <summary>Eviction order under the ceiling: lower classes go first, LRU inside a class.</summary>
public enum DerivedCacheEntryClass
{
    /// <summary>No catalog lists the content any more (removed from a library, or never listed).</summary>
    Unreferenced = 0,
    /// <summary>The content's drive library has been unplugged for longer than the aging period.</summary>
    UnmountedStale = 1,
    /// <summary>Listed by an attached catalog, or by a library unplugged only recently.</summary>
    Live = 2,
}

/// <summary>One cached content as the janitor sees it. The source path is read from the header only when asked for.</summary>
public sealed class DerivedCacheEntry
{
    private readonly string? _headerPath;
    private string? _originalPath;
    private bool _read;

    internal DerivedCacheEntry(string sha, DateTime lastUsedUtc, string? headerPath)
    { Sha = sha; LastUsedUtc = lastUsedUtc; _headerPath = headerPath; }

    public string Sha { get; }
    public DateTime LastUsedUtc { get; }

    /// <summary>The original the preparation was made from (header «Original.Path»), or null when unreadable.</summary>
    public string? OriginalPath
    {
        get
        {
            if (_read) return _originalPath;
            _read = true;
            if (_headerPath is null) return null;
            try
            {
                using var file = new FileStream(_headerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length > 16 * 1024) return null;
                using var json = JsonDocument.Parse(file);
                if (json.RootElement.TryGetProperty("Original", out var original) && original.ValueKind == JsonValueKind.Object
                    && original.TryGetProperty("Path", out var path) && path.ValueKind == JsonValueKind.String)
                    _originalPath = path.GetString();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            return _originalPath;
        }
    }
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
    /// <summary>Whole entries (header + pixels) removed for the ceiling, per eviction class.</summary>
    public int EvictedUnreferenced { get; internal init; }
    public int EvictedUnmounted { get; internal init; }
    public int EvictedLive { get; internal init; }
    /// <summary>Purge only: entries selected and removed, and selected entries kept because a reader held them.</summary>
    public int PurgedEntries { get; internal init; }
    public int PurgeSkipped { get; internal init; }
    public IReadOnlyList<string> SkippedShas { get; internal init; } = Array.Empty<string>();
    public bool CeilingSatisfied => ScanComplete && BytesKept <= CeilingBytes && ReserveSatisfied;
    public IReadOnlyList<string> Errors { get; internal init; } = Array.Empty<string>();
}

/// <summary>
/// Background maintenance for one derived-cache root, not a writer/admission controller.
/// Only canonical store filenames in v1..vCurrentVersion are owned. No recursive deletion.
/// The store touches the header's last-write time on a disk hit; use the newest pair timestamp
/// as LRU, rather than filesystem access times (often disabled) or another side file.
/// Deleting renames both members while handles deny other readers and writers, then deletes the renamed files:
/// a reader either opens the old pair before the rename (and then the pair is skipped) or finds no pair and rebuilds.
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

    public string Root => _root;

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
        if (options.Budget is { } budget) return budget.CeilingFor(options.Share, availableFreeBytes, currentCacheBytes);
        return PreparedDataBudget.CalculateTotal(options.MaximumBytes, options.FreeSpaceFraction, options.MinimumFreeBytes,
            availableFreeBytes, currentCacheBytes);
    }

    // Task.Run also moves the initial directory/drive queries and lock wait off the caller thread.
    public Task<DerivedCacheJanitorReport> CleanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Clean(cancellationToken), cancellationToken);

    /// <summary>Synchronous form for a caller already on its own background thread (the store's maintenance).</summary>
    internal DerivedCacheJanitorReport Clean(CancellationToken cancellationToken, Action? beforeWork = null) =>
        Run(cancellationToken, (pass, before) =>
        {
            // Obsolete versions, stale interrupted writes and stale singletons are maintenance,
            // independent of pressure. Young temporary files protect their entire SHA group.
            foreach (var entry in before.Entries.Where(e => e.Version < _options.CurrentVersion || e.Maintenance))
                pass.Delete(entry);
            var removedFolders = 0;
            foreach (var folder in before.Folders.Where(f => f.Version < _options.CurrentVersion))
            {
                pass.Checkpoint();
                try
                {
                    CheckPath(folder.Path, directory: true);
                    if (Directory.EnumerateFileSystemEntries(folder.Path).Any()) continue;
                    Directory.Delete(folder.Path, recursive: false);
                    removedFolders++;
                }
                catch (Exception ex) when (IsFileError(ex)) { pass.Error(folder.Path, ex); }
            }

            var current = pass.Scan();
            var free = _availableFreeBytes();
            var ceiling = CalculateCeiling(_options, free, current.Bytes);
            var target = (long)decimal.Floor(ceiling * 0.70m);
            var triggered = current.Bytes > ceiling * 0.90m;
            var remaining = current.Bytes;
            var evicted = new int[3];
            if (triggered)
            {
                // Removed and long-unplugged content first, then least recently used. Only the ordering
                // reads headers, and only while the ceiling is exceeded.
                var ordered = current.Entries
                    .Select(e => (Entry: e, Class: e.Maintenance || _options.Classify is null ? DerivedCacheEntryClass.Live
                        : _options.Classify(pass.Describe(e))))
                    .OrderBy(e => e.Class).ThenBy(e => e.Entry.Used).ThenBy(e => e.Entry.Files[0].Path, StringComparer.Ordinal);
                foreach (var (entry, cls) in ordered)
                {
                    if (remaining <= target) break;
                    var removed = pass.Delete(entry);
                    if (removed > 0) evicted[(int)cls]++;
                    remaining -= removed;
                }
            }

            var kept = pass.Scan();
            _options.Budget?.ReportUsage(_options.Share, kept.Bytes);
            return new()
            {
                LockAcquired = true, ScanComplete = pass.Complete, CleanupTriggered = triggered,
                AvailableFreeBytes = free, CeilingBytes = ceiling, TargetBytes = target,
                BytesBefore = before.Bytes, FilesDeleted = pass.DeletedFiles, BytesDeleted = pass.DeletedBytes,
                FilesKept = kept.Files, BytesKept = kept.Bytes, VersionFoldersDeleted = removedFolders,
                ReserveSatisfied = _availableFreeBytes() >= MinimumFree,
                EvictedUnreferenced = evicted[0], EvictedUnmounted = evicted[1], EvictedLive = evicted[2],
                Errors = pass.Errors
            };
        }, beforeWork);

    /// <summary>
    /// Removes exactly the selected entries (header + pixels of every owned version), whatever the ceiling.
    /// Nothing else is touched. An entry a reader holds open, or one being written, is kept and named in
    /// <see cref="DerivedCacheJanitorReport.SkippedShas"/> so the caller can try again later.
    /// </summary>
    public Task<DerivedCacheJanitorReport> PurgeAsync(Func<DerivedCacheEntry, bool> select,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(select);
        return Task.Run(() => Purge(select, cancellationToken), cancellationToken);
    }

    internal DerivedCacheJanitorReport Purge(Func<DerivedCacheEntry, bool> select, CancellationToken cancellationToken) =>
        Run(cancellationToken, (pass, before) =>
        {
            var purged = 0;
            var skipped = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in before.Entries.Where(e => !e.Files[0].Temporary))
            {
                if (!select(pass.Describe(entry))) continue;
                if (pass.Delete(entry) > 0) purged++;
                else skipped.Add(entry.Sha);
            }
            var kept = pass.Scan();
            _options.Budget?.ReportUsage(_options.Share, kept.Bytes);
            return new()
            {
                LockAcquired = true, ScanComplete = pass.Complete, BytesBefore = before.Bytes,
                FilesDeleted = pass.DeletedFiles, BytesDeleted = pass.DeletedBytes, FilesKept = kept.Files,
                BytesKept = kept.Bytes, PurgedEntries = purged, PurgeSkipped = skipped.Count,
                SkippedShas = skipped.ToArray(), ReserveSatisfied = true, CeilingBytes = long.MaxValue,
                Errors = pass.Errors
            };
        });

    private long MinimumFree => _options.Budget?.MinimumFreeBytes ?? _options.MinimumFreeBytes;

    private DerivedCacheJanitorReport Run(CancellationToken cancellationToken,
        Func<Pass, Snapshot, DerivedCacheJanitorReport> body, Action? beforeWork = null)
    {
        var pass = new Pass(this, cancellationToken, beforeWork);
        FileStream? cleanerLock = null;
        try
        {
            pass.Checkpoint();
            CheckPath(_root, directory: true);
            var watch = Stopwatch.StartNew();
            var lockPath = Path.Combine(_root, LockFileName);
            while (cleanerLock is null)
            {
                pass.Checkpoint();
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
            return body(pass, pass.Scan());
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            pass.Error(_root, ex);
            return new() { LockAcquired = cleanerLock is not null, FilesDeleted = pass.DeletedFiles,
                BytesDeleted = pass.DeletedBytes, Errors = pass.Errors };
        }
        finally
        {
            // Keep the lock inode: unlinking it lets another cleaner lock a different inode.
            cleanerLock?.Dispose();
        }
    }

    private sealed class Pass(DerivedCacheJanitor owner, CancellationToken cancellationToken, Action? beforeWork)
    {
        private readonly HashSet<string> _errors = new(StringComparer.Ordinal);
        public bool Complete = true;
        public long DeletedFiles, DeletedBytes;
        public string[] Errors => _errors.ToArray();
        private DerivedCacheJanitorOptions Options => owner._options;

        public void Checkpoint()
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeWork?.Invoke();
        }

        public void Error(string path, Exception ex) => _errors.Add(path + ": " + ex.Message);

        public DerivedCacheEntry Describe(Entry entry) => new(entry.Sha, entry.Used,
            entry.Files.FirstOrDefault(f => f.Path.EndsWith(".json", StringComparison.Ordinal))?.Path);

        public Snapshot Scan()
        {
            var entries = new List<Entry>();
            var folders = new List<VersionFolder>();
            Checkpoint();
            CheckPath(owner._root, directory: true);
            foreach (var directory in Directory.EnumerateFileSystemEntries(owner._root))
            {
                Checkpoint();
                if (!TryVersion(Path.GetFileName(directory), out var version) || version > Options.CurrentVersion) continue;
                try
                {
                    CheckPath(directory, directory: true);
                    folders.Add(new(directory, version));
                    var files = new List<CacheFile>();
                    var blocked = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        Checkpoint();
                        var name = Path.GetFileName(path);
                        if (!OwnedName(name, out var temporary)) continue;
                        try
                        {
                            CheckPath(path, directory: false);
                            var info = new FileInfo(path);
                            files.Add(new(path, name[..64], info.Length, info.LastWriteTimeUtc, temporary));
                        }
                        catch (Exception ex) when (IsFileError(ex))
                        { blocked.Add(name[..64]); Complete = false; Error(path, ex); }
                    }
                    var staleBefore = DateTime.UtcNow - Options.TemporaryFileGrace;
                    foreach (var group in files.GroupBy(f => f.Sha, StringComparer.Ordinal))
                    {
                        Checkpoint();
                        var active = blocked.Contains(group.Key) || group.Any(f => f.Temporary && f.Written > staleBefore);
                        foreach (var temporary in group.Where(f => f.Temporary))
                            entries.Add(new(version, new[] { temporary }, !active, true));
                        var pair = group.Where(f => !f.Temporary).OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
                        if (pair.Length > 0)
                            entries.Add(new(version, pair, !active && (pair.Length == 2 || pair.All(f => f.Written <= staleBefore)),
                                pair.Length != 2));
                    }
                }
                catch (Exception ex) when (IsFileError(ex)) { Complete = false; Error(directory, ex); }
            }
            return new(entries, folders);
        }

        public long Delete(Entry entry)
        {
            Checkpoint();
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
                    if (File.GetLastWriteTimeUtc(path) > DateTime.UtcNow - Options.TemporaryFileGrace)
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
                    { Complete = false; Error(item.File.Path, rollback); }
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
                        DeletedFiles++; DeletedBytes += item.File.Bytes; removed += item.File.Bytes;
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
        if (name.Length is not (69 or 70 or 71 or 101) || !LowerHex(name.AsSpan(0, 64))) return false;
        if (name.Length == 69) return name.EndsWith(".json", StringComparison.Ordinal);
        // Portable trust record: same entry as its .json/.pixels, removed together with them.
        if (name.Length == 70) return name.EndsWith(".trust", StringComparison.Ordinal);
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
        ArgumentNullException.ThrowIfNull(options.Share);
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
        public string Sha => Files[0].Sha;
    }
    private sealed record Snapshot(List<Entry> Entries, List<VersionFolder> Folders)
    {
        public long Bytes => Entries.Sum(e => e.Files.Sum(f => f.Bytes));
        public long Files => Entries.Sum(e => (long)e.Files.Length);
    }
}
