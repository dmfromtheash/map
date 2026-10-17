using System.IO.Compression;
using System.Text.Json;

namespace Map.Presentation.Services;

/// <summary>A map file found in the library folder (or one of its folders).</summary>
public sealed record LibraryMapFile(string Path, string Title, DateTimeOffset ModifiedUtc, DateTimeOffset CreatedUtc,
    long SizeBytes, string Folder);

/// <summary>A folder of the library: its own directory one level below the library root.</summary>
public sealed record LibraryFolder(string Path, string Name, int MapCount, DateTimeOffset ModifiedUtc);

/// <summary>An exported picture of a map kept in the library.</summary>
public sealed record LibraryExport(string Path, string Name, DateTimeOffset ModifiedUtc, long SizeBytes);

/// <summary>What a map file says about itself without being opened: its manifest and preview.</summary>
public sealed record LibraryMapSummary(string? Title, DateTimeOffset? CreatedUtc, int CanvasWidth, int CanvasHeight, byte[]? PreviewPng);

/// <summary>One scan of the library folder.</summary>
public sealed record LibrarySnapshot(
    IReadOnlyList<LibraryMapFile> Maps, IReadOnlyList<LibraryFolder> Folders, IReadOnlyList<LibraryExport> Exports)
{
    public static LibrarySnapshot Empty { get; } = new([], [], []);
}

/// <summary>
/// The place where the user's maps live, like Inkarnate's "Maps": every *.mapproj in the library
/// folder and in its folders is shown, whether or not it was opened recently. Folders are plain
/// directories one level deep; exported pictures are the images in the "Экспорт" folder; the
/// favourites ("Избранное") are kept in a small file in the profile. Only the file system is
/// read - nothing here opens or changes a map.
/// </summary>
public sealed class MapLibrary
{
    public const string ExportsFolderName = "Экспорт";
    private static readonly string[] ExportPatterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"];
    private readonly string _favoritesPath;
    private readonly object _gate = new();
    private HashSet<string>? _favorites;

    public MapLibrary(string root, string favoritesPath)
    {
        Root = root;
        _favoritesPath = favoritesPath;
    }

    public string Root { get; }
    public string ExportsFolder => System.IO.Path.Combine(Root, ExportsFolderName);

    /// <summary>Creates the library folder if it is missing; false when the disk refuses.</summary>
    public bool EnsureExists()
    {
        try { Directory.CreateDirectory(Root); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public LibrarySnapshot Scan()
    {
        if (!Directory.Exists(Root)) return LibrarySnapshot.Empty;
        var maps = new List<LibraryMapFile>();
        var folders = new List<LibraryFolder>();
        var exports = new List<LibraryExport>();
        try
        {
            maps.AddRange(MapsIn(Root, ""));
            foreach (var directory in SafeDirectories(Root))
            {
                var name = System.IO.Path.GetFileName(directory);
                if (name.StartsWith('.')) continue;
                if (string.Equals(name, ExportsFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var pattern in ExportPatterns)
                        foreach (var file in SafeFiles(directory, pattern))
                        {
                            var info = new FileInfo(file);
                            exports.Add(new LibraryExport(file, System.IO.Path.GetFileNameWithoutExtension(file),
                                info.LastWriteTimeUtc, info.Length));
                        }
                    continue;
                }
                var inFolder = MapsIn(directory, name).ToList();
                maps.AddRange(inFolder);
                folders.Add(new LibraryFolder(directory, name, inFolder.Count, Directory.GetLastWriteTimeUtc(directory)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read shows what was read so far, never an error page.
        }
        return new LibrarySnapshot(maps, folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            exports.OrderByDescending(e => e.ModifiedUtc).ToList());
    }

    private static IEnumerable<LibraryMapFile> MapsIn(string directory, string folder)
    {
        foreach (var file in SafeFiles(directory, "*.mapproj"))
        {
            var info = new FileInfo(file);
            yield return new LibraryMapFile(file, System.IO.Path.GetFileNameWithoutExtension(file),
                info.LastWriteTimeUtc, info.CreationTimeUtc, info.Length, folder);
        }
    }

    private static string[] SafeFiles(string directory, string pattern)
    {
        try { return Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string[] SafeDirectories(string directory)
    {
        try { return Directory.GetDirectories(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Creates a new folder with a free name based on <paramref name="name"/>; null when it fails.</summary>
    public string? CreateFolder(string name)
    {
        var clean = string.Concat((name ?? "").Trim().Where(c => !System.IO.Path.GetInvalidFileNameChars().Contains(c)));
        if (clean.Length == 0) clean = "Новая папка";
        if (!EnsureExists()) return null;
        var candidate = System.IO.Path.Combine(Root, clean);
        for (var n = 2; Directory.Exists(candidate) || File.Exists(candidate); n++)
            candidate = System.IO.Path.Combine(Root, $"{clean} {n}");
        try { Directory.CreateDirectory(candidate); return candidate; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Moves a map file into a library folder ("" = the library root). Returns the new path, or
    /// null when the target already holds a file of that name or the disk refuses.
    /// </summary>
    public string? MoveToFolder(string path, string folder)
    {
        var target = string.IsNullOrEmpty(folder) ? Root : System.IO.Path.Combine(Root, folder);
        var destination = System.IO.Path.Combine(target, System.IO.Path.GetFileName(path));
        if (string.Equals(System.IO.Path.GetFullPath(destination), System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            return path;
        if (File.Exists(destination)) return null;
        try
        {
            Directory.CreateDirectory(target);
            File.Move(path, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (IsFavorite(path)) { SetFavorite(path, false); SetFavorite(destination, true); }
        return destination;
    }

    public bool IsFavorite(string path)
    {
        lock (_gate) return Favorites().Contains(Normalize(path));
    }

    public int FavoriteCount(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            var favorites = Favorites();
            return paths.Count(path => favorites.Contains(Normalize(path)));
        }
    }

    public void SetFavorite(string path, bool favorite)
    {
        lock (_gate)
        {
            var favorites = Favorites();
            var changed = favorite ? favorites.Add(Normalize(path)) : favorites.Remove(Normalize(path));
            if (!changed) return;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_favoritesPath)!);
                var temporary = _favoritesPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new { favorites = favorites.Order().ToArray() }));
                File.Move(temporary, _favoritesPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The mark stays for this session; the next start reads what reached the disk.
            }
        }
    }

    private HashSet<string> Favorites()
    {
        if (_favorites is not null) return _favorites;
        _favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_favoritesPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_favoritesPath));
                if (document.RootElement.TryGetProperty("favorites", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var item in list.EnumerateArray())
                        if (item.GetString() is { Length: > 0 } value) _favorites.Add(value);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged list starts empty; the maps themselves are untouched.
        }
        return _favorites;
    }

    private static readonly Dictionary<(string Path, DateTime Written), LibraryMapSummary> Summaries = [];

    /// <summary>
    /// Title, creation time, canvas size and preview read from the project archive's manifest and
    /// preview entries only (a few kilobytes, never the scene). Cached per file and write time.
    /// </summary>
    public static LibraryMapSummary ReadSummary(string path)
    {
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return new(null, null, 0, 0, null); }
        lock (Summaries) if (Summaries.TryGetValue((path, written), out var cached)) return cached;
        var summary = new LibraryMapSummary(null, null, 0, 0, null);
        try
        {
            using var archive = ZipFile.OpenRead(path);
            string? title = null; DateTimeOffset? created = null; int width = 0, height = 0;
            if (archive.GetEntry("manifest.json") is { Length: < 1 << 20 } manifest)
            {
                using var stream = manifest.Open();
                using var json = JsonDocument.Parse(stream);
                var root = json.RootElement;
                if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) title = t.GetString();
                if (root.TryGetProperty("createdUtc", out var c) && c.TryGetDateTimeOffset(out var when)) created = when;
                if (root.TryGetProperty("canvas", out var canvas) && canvas.ValueKind == JsonValueKind.Object)
                {
                    if (canvas.TryGetProperty("width", out var w) && w.TryGetInt32(out var wv)) width = wv;
                    if (canvas.TryGetProperty("height", out var h) && h.TryGetInt32(out var hv)) height = hv;
                }
            }
            byte[]? preview = null;
            if (archive.GetEntry("preview.png") is { Length: > 0 and < 8 << 20 } image)
            {
                using var stream = image.Open();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                preview = copy.ToArray();
            }
            summary = new LibraryMapSummary(title, created, width, height, preview);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            // Not a readable project archive: the card shows the file name and no preview.
        }
        lock (Summaries) Summaries[(path, written)] = summary;
        return summary;
    }

    private static string Normalize(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
}
