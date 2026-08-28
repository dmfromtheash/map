using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Map.Core.Documents;

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
    public const long MaxEncodedBytes = 1L * 1024 * 1024;
    public const int MaxStringUtf8Bytes = 64 * 1024;
    public const int MaxJsonTokens = 512;
    public const int MaxTitleRunes = 512;

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

        byte[] bytes;
        try
        {
            using var stream = new FileStream(
                _path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaxEncodedBytes)
            {
                return [];
            }

            bytes = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                return [];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return []; // a torn index must not take the dashboard down
        }

        if (!HasBoundedJsonShape(bytes))
        {
            return [];
        }

        List<RecentMapEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<RecentMapEntry>>(bytes, JsonOptions);
        }
        catch (JsonException)
        {
            return [];
        }

        if (entries is null || entries.Count > Capacity)
        {
            return [];
        }

        var result = new List<RecentMapEntry>();
        foreach (var entry in entries)
        {
            if (entry is null
                || string.IsNullOrWhiteSpace(entry.Path)
                || entry.Path.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(entry.DocumentId)
                || !Guid.TryParse(entry.DocumentId, out _)
                || entry.Title is null
                || entry.Title.EnumerateRunes().Count() > MaxTitleRunes
                || entry.ModifiedUtc is null
                || !DateTimeOffset.TryParse(
                    entry.ModifiedUtc,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out _))
            {
                return [];
            }

            try
            {
                entry.Path = System.IO.Path.GetFullPath(entry.Path);
            }
            catch (Exception ex) when (
                ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return [];
            }

            entry.IsAvailable = IsSafeStartupProbe(entry.Path)
                && HasNoReparsePoint(entry.Path)
                && File.Exists(entry.Path);
            result.Add(entry);
        }

        return result;
    }

    private static bool IsSafeStartupProbe(string fullPath)
    {
        if (!System.IO.Path.IsPathFullyQualified(fullPath)
            || fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal)
            || !string.Equals(
                System.IO.Path.GetExtension(fullPath), ".mapproj", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var root = System.IO.Path.GetPathRoot(fullPath);
        if (root is null || root.Length != 3 || root[1] != ':')
        {
            return false;
        }

        return fullPath[root.Length..].Split(
            [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).All(PortablePathSegment.IsSafe);
    }

    /// <summary>
    /// A recent card is display-only startup state. Never follow a junction/symlink while deciding
    /// whether its target exists: a local-looking path may otherwise turn into an SMB/device probe.
    /// Missing descendants are safe to stop at because the final File.Exists will simply be false.
    /// </summary>
    private static bool HasNoReparsePoint(string fullPath)
    {
        var root = System.IO.Path.GetPathRoot(fullPath)!;
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasBoundedJsonShape(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var tokens = 0;
            var entries = 0;
            var rootStarted = false;
            while (reader.Read())
            {
                if (++tokens > MaxJsonTokens)
                {
                    return false;
                }

                if (!rootStarted)
                {
                    if (reader.TokenType != JsonTokenType.StartArray)
                    {
                        return false;
                    }

                    rootStarted = true;
                    continue;
                }

                if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String
                    && (reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length)
                        > MaxStringUtf8Bytes)
                {
                    return false;
                }

                if (reader.CurrentDepth == 1
                    && reader.TokenType is JsonTokenType.StartObject or JsonTokenType.Null
                    && ++entries > Capacity)
                {
                    return false;
                }
            }

            return rootStarted;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Puts the entry first, dedupes by path, trims to capacity, writes atomically enough.</summary>
    public void Touch(string path, Guid documentId, string title, DateTimeOffset modifiedUtc)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        if (fullPath.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(fullPath) > MaxStringUtf8Bytes)
        {
            return;
        }

        var entries = Load().ToList();

        entries.RemoveAll(e =>
            string.Equals(System.IO.Path.GetFullPath(e.Path), fullPath, StringComparison.OrdinalIgnoreCase));

        entries.Insert(0, new RecentMapEntry
        {
            Path = fullPath,
            DocumentId = documentId.ToString("D"),
            Title = TruncateTitle(title ?? ""),
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
        string? temp = null;
        try
        {
            var bounded = entries.Take(Capacity).ToList();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(bounded, JsonOptions);
            if (bytes.LongLength > MaxEncodedBytes || !HasBoundedJsonShape(bytes))
            {
                return;
            }

            var directory = System.IO.Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(_path))
            {
                File.Replace(temp, _path, _path + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a recent entry is an inconvenience; failing an open/save over it is not a trade.
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A locked temp is inert and must not take the dashboard down.
                }
            }
        }
    }

    private static string TruncateTitle(string title) => string.Concat(
        title.EnumerateRunes().Take(MaxTitleRunes).Select(static rune => rune.ToString()));
}
