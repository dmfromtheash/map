namespace Map.Presentation.Services;

public sealed partial class MapLibrary
{
    public void ChangeFolder(string path, Action<string>? persist = null)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("Папка библиотеки не найдена.");
        // Check access before saving a preference. Existing maps are neither moved nor deleted.
        using (var entries = Directory.EnumerateFileSystemEntries(fullPath).GetEnumerator()) entries.MoveNext();
        persist?.Invoke(fullPath);
        Root = fullPath;
    }
}
