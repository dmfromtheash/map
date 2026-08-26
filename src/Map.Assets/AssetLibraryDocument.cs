using System.Text.Json;
using System.Text.Json.Serialization;

namespace Map.Assets;

/// <summary>
/// The versioned on-disk shape of the local library. Schema v1 was a bare JSON array of
/// <see cref="AssetRecord"/>; v2 is this object, which additionally owns the collections, the
/// variants and the logical sets. Content is still keyed by SHA-256 and never duplicated.
/// </summary>
public sealed class AssetLibraryDocument
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>Recent is bounded on purpose: it is a convenience, not a history of the user.</summary>
    public const int MaxRecent = 200;

    /// <summary>How many superseded index copies we are willing to keep next to the live one.</summary>
    public const int MaxBackups = 5;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<AssetRecord> Records { get; set; } = [];

    public List<AssetCollection> Collections { get; set; } = [];

    public List<AssetVariant> Variants { get; set; } = [];

    public List<AssetSet> Sets { get; set; } = [];

    public List<AssetRecentEntry> Recent { get; set; } = [];

    /// <summary>Last variant the user chose inside each set; catalog state, never map state.</summary>
    public Dictionary<string, string> CurrentVariantBySet { get; set; } = [];

    [JsonIgnore]
    public AssetLibraryStatus Status { get; set; } = AssetLibraryStatus.Ready;

    /// <summary>Human-readable reason whenever <see cref="Status"/> is not Ready.</summary>
    [JsonIgnore]
    public string Diagnostic { get; set; } = "";
}

/// <summary>
/// Loads, migrates and atomically stores <see cref="AssetLibraryDocument"/>. A damaged index is
/// reported, never parsed loosely and never replaced: the only surviving copy stays on disk.
/// </summary>
public static class AssetLibraryStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions LegacyOptions = new() { WriteIndented = true };

    public static AssetLibraryDocument Load(string indexPath, Func<DateTimeOffset> clock)
    {
        if (!File.Exists(indexPath))
        {
            return new AssetLibraryDocument();
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(indexPath);
        }
        catch (IOException ex)
        {
            return Damaged("Индекс каталога не читается: " + ex.Message);
        }

        if (bytes.Length == 0)
        {
            return Damaged("Индекс каталога пуст.");
        }

        JsonValueKind kind;
        try
        {
            using var probe = JsonDocument.Parse(bytes);
            kind = probe.RootElement.ValueKind;
        }
        catch (JsonException ex)
        {
            return Damaged("Индекс каталога повреждён: " + ex.Message);
        }

        return kind switch
        {
            JsonValueKind.Array => Migrate(indexPath, bytes, clock),
            JsonValueKind.Object => LoadV2(bytes),
            _ => Damaged("Индекс каталога имеет неизвестную структуру."),
        };
    }

    private static AssetLibraryDocument LoadV2(byte[] bytes)
    {
        AssetLibraryDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<AssetLibraryDocument>(bytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Damaged("Индекс каталога повреждён: " + ex.Message);
        }

        if (document is null)
        {
            return Damaged("Индекс каталога пуст.");
        }

        if (document.SchemaVersion > AssetLibraryDocument.CurrentSchemaVersion)
        {
            return Damaged(
                $"Индекс каталога сохранён более новой версией программы (schema {document.SchemaVersion}).");
        }

        return Validate(document) is { } reason ? Damaged("Индекс каталога противоречив: " + reason) : document;
    }

    /// <summary>
    /// Schema v1 to v2. Every old field is carried over unchanged and each record becomes a
    /// singleton set whose ids are derived from the content hash, so the conversion is stable.
    /// </summary>
    private static AssetLibraryDocument Migrate(string indexPath, byte[] bytes, Func<DateTimeOffset> clock)
    {
        List<AssetRecord>? legacy;
        try
        {
            legacy = JsonSerializer.Deserialize<List<AssetRecord>>(bytes, LegacyOptions);
        }
        catch (JsonException ex)
        {
            return Damaged("Старый индекс каталога повреждён: " + ex.Message);
        }

        if (legacy is null)
        {
            return Damaged("Старый индекс каталога пуст.");
        }

        var document = new AssetLibraryDocument { Status = AssetLibraryStatus.Migrated };
        foreach (var record in legacy)
        {
            if (document.Records.Any(existing =>
                string.Equals(existing.Sha256, record.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            document.Records.Add(record);
            AddSingleton(document, record);
        }

        if (Validate(document) is { } reason)
        {
            return Damaged("Старый индекс каталога не прошёл проверку: " + reason);
        }

        // The old copy is preserved under its own name BEFORE the new one is published, so an
        // interrupted migration always leaves at least one readable index behind.
        var backup = BackupPath(indexPath, clock());
        if (!File.Exists(backup))
        {
            File.Copy(indexPath, backup, overwrite: false);
        }

        Save(indexPath, document);
        TrimBackups(indexPath);
        document.Status = AssetLibraryStatus.Migrated;
        document.Diagnostic =
            $"Каталог обновлён до схемы v{AssetLibraryDocument.CurrentSchemaVersion}. "
            + $"Прежняя копия сохранена: {Path.GetFileName(backup)}.";
        return document;
    }

    private static string BackupPath(string indexPath, DateTimeOffset now)
    {
        var directory = Path.GetDirectoryName(indexPath)!;
        var stem = Path.GetFileNameWithoutExtension(indexPath);
        return Path.Combine(directory, $"{stem}.v1.{now.UtcDateTime:yyyyMMddHHmmssfff}.json");
    }

    /// <summary>Appends the singleton set and variant a plain record maps to.</summary>
    public static void AddSingleton(AssetLibraryDocument document, AssetRecord record)
    {
        var variantId = AssetIdentity.VariantIdForContent(record.Sha256);
        var setId = AssetIdentity.SetIdForContent(record.Sha256);
        if (document.Variants.All(item => item.Id != variantId))
        {
            document.Variants.Add(new AssetVariant
            {
                Id = variantId,
                Sha256 = record.Sha256,
                Label = record.DisplayName,
            });
        }

        EnsureCollection(document, record.Pack);
        if (document.Sets.Any(item => item.Id == setId))
        {
            return;
        }

        document.Sets.Add(new AssetSet
        {
            Id = setId,
            Name = record.DisplayName,
            VariantIds = [variantId],
            CoverVariantId = variantId,
            CollectionId = string.IsNullOrWhiteSpace(record.Pack) ? null : AssetIdentity.CollectionId(record.Pack),
            Tags = [.. record.Tags],
            Favorite = record.Favorite,
            ImportedUtc = record.ImportedUtc,
        });
    }

    public static void EnsureCollection(AssetLibraryDocument document, string? pack)
    {
        if (string.IsNullOrWhiteSpace(pack))
        {
            return;
        }

        var id = AssetIdentity.CollectionId(pack);
        if (document.Collections.Any(item => item.Id == id))
        {
            return;
        }

        document.Collections.Add(new AssetCollection
        {
            Id = id,
            Name = pack.Trim(),
            Order = document.Collections.Count,
        });
    }

    /// <summary>
    /// Structural checks a healthy document must satisfy. They exist so a half-written or hand
    /// edited file is reported as damaged instead of silently loading a broken library.
    /// </summary>
    public static string? Validate(AssetLibraryDocument document)
    {
        if (document.Records.Any(record => string.IsNullOrWhiteSpace(record.Sha256)))
        {
            return "запись без SHA-256";
        }

        if (document.Records.Select(record => record.Sha256.ToLowerInvariant()).Distinct().Count()
            != document.Records.Count)
        {
            return "повторяющийся SHA-256";
        }

        if (document.Variants.Select(variant => variant.Id).Distinct().Count() != document.Variants.Count)
        {
            return "повторяющийся идентификатор варианта";
        }

        if (document.Sets.Select(set => set.Id).Distinct().Count() != document.Sets.Count)
        {
            return "повторяющийся идентификатор набора";
        }

        var variantIds = document.Variants.Select(variant => variant.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var set in document.Sets)
        {
            if (set.VariantIds.Count == 0)
            {
                return $"набор «{set.Name}» без вариантов";
            }

            if (set.VariantIds.Any(id => !variantIds.Contains(id)))
            {
                return $"набор «{set.Name}» ссылается на отсутствующий вариант";
            }

            if (!set.VariantIds.Contains(set.CoverVariantId))
            {
                return $"обложка набора «{set.Name}» не входит в него";
            }
        }

        return null;
    }

    /// <summary>Unique temp file, read-back validation, atomic replace, bounded backup.</summary>
    public static void Save(string indexPath, AssetLibraryDocument document)
    {
        document.SchemaVersion = AssetLibraryDocument.CurrentSchemaVersion;
        if (document.Recent.Count > AssetLibraryDocument.MaxRecent)
        {
            document.Recent = [.. document.Recent
                .OrderByDescending(entry => entry.UsedUtc)
                .Take(AssetLibraryDocument.MaxRecent)];
        }

        var directory = Path.GetDirectoryName(indexPath)!;
        Directory.CreateDirectory(directory);
        var temp = indexPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions));

        // The candidate is read back before it is allowed to replace the live index: a truncated
        // or unreadable temp file must never become the only copy the user has.
        try
        {
            var verified = JsonSerializer.Deserialize<AssetLibraryDocument>(File.ReadAllBytes(temp), JsonOptions)
                ?? throw new InvalidDataException("Новый индекс каталога не читается.");
            if (Validate(verified) is { } reason)
            {
                throw new InvalidDataException("Новый индекс каталога не прошёл проверку: " + reason);
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        if (File.Exists(indexPath))
        {
            File.Replace(temp, indexPath, indexPath + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, indexPath);
        }
    }

    private static void TrimBackups(string indexPath)
    {
        var directory = Path.GetDirectoryName(indexPath)!;
        var pattern = Path.GetFileNameWithoutExtension(indexPath) + ".v1.*.json";
        foreach (var stale in Directory.GetFiles(directory, pattern)
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .Skip(AssetLibraryDocument.MaxBackups))
        {
            TryDelete(stale);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A locked leftover is never a reason to fail the operation the user asked for.
        }
    }

    private static AssetLibraryDocument Damaged(string diagnostic) => new()
    {
        Status = AssetLibraryStatus.Damaged,
        Diagnostic = diagnostic,
    };
}
