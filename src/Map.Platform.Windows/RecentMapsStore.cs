using System.Text.Json;
using System.Text.Json.Serialization;

namespace Map.Platform.Windows;

/// <summary>One dashboard card. Availability is re-checked at read time, never trusted from disk.</summary>
public sealed class RecentMapEntry
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("documentId")] public string DocumentId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("modifiedUtc")] public string ModifiedUtc { get; set; } = "";

    [JsonIgnore] public bool IsAvailable { get; set; }
}

/// <summary>
/// Local bounded recent-maps index: one small JSON file, no database, no network. A torn index is
/// treated as empty rather than crashing the dashboard.
/// </summary>
public sealed class RecentMapsStore
{
    public const int Capacity = 10;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public RecentMapsStore(string? indexPath = null) =>
        _path = indexPath ?? AppPaths.RecentIndexPath;

    public IReadOnlyList<RecentMapEntry> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        List<RecentMapEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<RecentMapEntry>>(File.ReadAllBytes(_path), JsonOptions);
        }
        catch (JsonException)
        {
            return []; // a torn index must not take the dashboard down
        }

        if (entries is null)
        {
            return [];
        }

        var result = new List<RecentMapEntry>();
        foreach (var entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
            {
                continue;
            }

            entry.IsAvailable = File.Exists(entry.Path);
            result.Add(entry);
        }

        return result;
    }

    /// <summary>Puts the entry first, dedupes by path, trims to capacity, writes atomically enough.</summary>
    public void Touch(string path, Guid documentId, string title, DateTimeOffset modifiedUtc)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var entries = Load().ToList();

        entries.RemoveAll(e =>
            string.Equals(System.IO.Path.GetFullPath(e.Path), fullPath, StringComparison.OrdinalIgnoreCase));

        entries.Insert(0, new RecentMapEntry
        {
            Path = fullPath,
            DocumentId = documentId.ToString("D"),
            Title = title,
            ModifiedUtc = modifiedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        });

        Save(entries.Take(Capacity).ToList());
    }

    public void Remove(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var entries = Load().ToList();
        entries.RemoveAll(e =>
            string.Equals(System.IO.Path.GetFullPath(e.Path), fullPath, StringComparison.OrdinalIgnoreCase));
        Save(entries);
    }

    private void Save(List<RecentMapEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions));
        }
        catch (IOException)
        {
            // Losing a recent entry is an inconvenience; failing an open/save over it is not a trade.
        }
    }
}
