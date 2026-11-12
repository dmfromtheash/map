using System.Text.Json;

namespace Map.Assets;

/// <summary>
/// Libraries that live on an external drive. A drive carries <c>MapLibrary\map-library.json</c>
/// at its root; the file names the catalog folders beside it (one ordinary catalog per pack).
/// The drive letter is never stored anywhere: every scan looks at the drives that are ready now,
/// so a drive that comes back as another letter is found again and its cards keep their ids.
/// </summary>
public static class AssetDriveLibraries
{
    public const string FolderName = "MapLibrary";
    public const string MarkerFileName = "map-library.json";
    public const string Format = "map-drive-library";

    /// <summary>Tests and checks list library folders here (separated by ';') instead of scanning drives.</summary>
    public const string RootsVariable = "MAP_DRIVE_LIBRARY_ROOTS";

    /// <summary>Set to 1 to never scan drives (isolated profiles must not pick up the owner's drive).</summary>
    public const string DisableVariable = "MAP_DRIVE_LIBRARY_DISABLED";

    private const long MaxMarkerBytes = 256 * 1024;

    public sealed record DriveLibrary(string Root, string Id, string Name, IReadOnlyList<string> CatalogRoots);

    private sealed record Marker(string? Format, int Version, string? Id, string? Name, List<string>? Catalogs);

    /// <summary>Every drive library reachable right now, in a stable order.</summary>
    public static IReadOnlyList<DriveLibrary> Discover() => Discover(Environment.GetEnvironmentVariable, DriveRoots);

    internal static IReadOnlyList<DriveLibrary> Discover(Func<string, string?> environment, Func<IEnumerable<string>> drives)
    {
        if (environment(DisableVariable) == "1") return [];
        IEnumerable<string> folders = environment(RootsVariable) is { Length: > 0 } listed
            ? listed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : drives().Select(drive => Path.Combine(drive, FolderName));
        var found = new List<DriveLibrary>();
        foreach (var folder in folders)
        {
            if (TryRead(folder) is { } library) found.Add(library);
        }

        return [.. found.DistinctBy(library => library.Id, StringComparer.Ordinal)];
    }

    /// <summary>All catalog folders of the given libraries, ready for <see cref="AssetCatalog.SetMounts"/>.</summary>
    public static IReadOnlyList<string> CatalogRoots(IEnumerable<DriveLibrary> libraries) =>
        [.. libraries.SelectMany(library => library.CatalogRoots)];

    public static DriveLibrary? TryRead(string folder)
    {
        try
        {
            var marker = Path.Combine(folder, MarkerFileName);
            var file = new FileInfo(marker);
            if (!file.Exists || file.Length is <= 0 or > MaxMarkerBytes) return null;
            var parsed = JsonSerializer.Deserialize<Marker>(File.ReadAllBytes(marker),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is null || parsed.Format != Format || parsed.Version != 1
                || string.IsNullOrWhiteSpace(parsed.Id) || parsed.Catalogs is null) return null;
            var root = Path.GetFullPath(folder);
            var catalogs = new List<string>();
            foreach (var name in parsed.Catalogs)
            {
                // Catalog folders are plain names inside the library folder: never another place on disk.
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || name is "." or "..") continue;
                var catalog = Path.Combine(root, name);
                if (File.Exists(Path.Combine(catalog, "catalog.json"))) catalogs.Add(catalog);
            }

            return new DriveLibrary(root, parsed.Id, string.IsNullOrWhiteSpace(parsed.Name) ? parsed.Id : parsed.Name, catalogs);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static IEnumerable<string> DriveRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            try { ready = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable; }
            catch (IOException) { ready = false; }
            if (ready) yield return drive.RootDirectory.FullName;
        }
    }
}
