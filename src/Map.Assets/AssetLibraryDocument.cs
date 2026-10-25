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
    public const int CurrentSchemaVersion = 3;

    /// <summary>Recent is bounded on purpose: it is a convenience, not a history of the user.</summary>
    public const int MaxRecent = 200;

    /// <summary>How many superseded index copies we are willing to keep next to the live one.</summary>
    public const int MaxBackups = 5;

    public const int MaxCollections = 10_000;

    /// <summary>Bounds the recursive UI tree builder even for a valid acyclic catalog.</summary>
    public const int MaxCollectionDepth = 128;

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
        Converters = { new JsonStringEnumConverter<AssetRole>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
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
            using var stream = new FileStream(
                indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > AssetLibraryJsonBudget.MaxEncodedBytes)
            {
                return Damaged(
                    $"Индекс каталога превышает startup budget {AssetLibraryJsonBudget.MaxEncodedBytes} байт.");
            }

            bytes = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                return Damaged("Индекс каталога вырос во время чтения и не прошёл bounded read.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
            kind = AssetLibraryJsonBudget.Validate(bytes);
        }
        catch (JsonException ex)
        {
            return Damaged("Индекс каталога повреждён: " + ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return Damaged("Индекс каталога превышает допустимый бюджет: " + ex.Message);
        }

        return kind switch
        {
            JsonValueKind.Array => Migrate(indexPath, bytes, clock),
            JsonValueKind.Object => LoadVersioned(indexPath, bytes, clock),
            _ => Damaged("Индекс каталога имеет неизвестную структуру."),
        };
    }

    private static AssetLibraryDocument LoadVersioned(string indexPath, byte[] bytes, Func<DateTimeOffset> clock)
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

        if (document.SchemaVersion < 2) return Damaged("Неизвестная версия объектного каталога.");
        if (Validate(document) is { } reason) return Damaged("Индекс каталога противоречив: " + reason);
        if (document.SchemaVersion < AssetLibraryDocument.CurrentSchemaVersion)
        {
            var backup = BackupPath(indexPath, clock(), document.SchemaVersion);
            RegionalArtPack.AssignDefaultRoles(document);
            if (!File.Exists(backup)) File.Copy(indexPath, backup, overwrite: false);
            Save(indexPath, document);
            TrimBackups(indexPath);
            document.Status = AssetLibraryStatus.Migrated;
            document.Diagnostic = $"Каталог обновлён до схемы v3. Прежняя копия сохранена: {Path.GetFileName(backup)}.";
        }
        return document;
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

        if (legacy.Any(static record => record is null))
        {
            return Damaged("Старый индекс каталога содержит пустую запись.");
        }

        var document = new AssetLibraryDocument { Status = AssetLibraryStatus.Migrated };
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var collectionIds = new HashSet<string>(StringComparer.Ordinal);
        var variantIds = new HashSet<string>(StringComparer.Ordinal);
        var setIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in legacy)
        {
            if (!IsSha256(record.Sha256))
            {
                return Damaged("Старый индекс каталога содержит некорректный SHA-256.");
            }

            if (!hashes.Add(record.Sha256))
            {
                continue;
            }

            document.Records.Add(record);
            var variantId = AssetIdentity.VariantIdForContent(record.Sha256);
            if (variantIds.Add(variantId))
            {
                document.Variants.Add(new AssetVariant
                {
                    Id = variantId,
                    Sha256 = record.Sha256,
                    Label = record.DisplayName,
                });
            }

            string? collectionId = null;
            if (!string.IsNullOrWhiteSpace(record.Pack))
            {
                collectionId = AssetIdentity.CollectionId(record.Pack);
                if (collectionIds.Add(collectionId))
                {
                    document.Collections.Add(new AssetCollection
                    {
                        Id = collectionId,
                        Name = record.Pack.Trim(),
                        Order = document.Collections.Count,
                    });
                }
            }

            var setId = AssetIdentity.SetIdForContent(record.Sha256);
            if (setIds.Add(setId))
            {
                document.Sets.Add(new AssetSet
                {
                    Id = setId,
                    Name = record.DisplayName,
                    VariantIds = [variantId],
                    CoverVariantId = variantId,
                    CollectionId = collectionId,
                    Tags = record.Tags is null ? [] : [.. record.Tags],
                    Favorite = record.Favorite,
                    ImportedUtc = record.ImportedUtc,
                });
            }
        }

        RegionalArtPack.AssignDefaultRoles(document);
        if (Validate(document) is { } reason)
        {
            return Damaged("Старый индекс каталога не прошёл проверку: " + reason);
        }

        // The old copy is preserved under its own name BEFORE the replacement is committed, so an
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

    private static string BackupPath(string indexPath, DateTimeOffset now, int version = 1)
    {
        var directory = Path.GetDirectoryName(indexPath)!;
        var stem = Path.GetFileNameWithoutExtension(indexPath);
        return Path.Combine(directory, $"{stem}.v{version}.{now.UtcDateTime:yyyyMMddHHmmssfff}.json");
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
            // A hand-edited or truncated v1 file may carry "tags": null, and migration must not
            // fall over on it: the record still has an identity worth keeping.
            Tags = record.Tags is null ? [] : [.. record.Tags],
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
    /// Every consistency rule a healthy schema v2 document must satisfy. The point is not to be
    /// strict for its own sake: a file that parses but contradicts itself would otherwise reach
    /// the collection tree walk and the placement path as if it were a real library. Each rule
    /// costs one pass over one list, so the whole check stays linear in the size of the index.
    /// </summary>
    public static string? Validate(AssetLibraryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // JSON is free to write "records": null, and the deserializer will do exactly that, so the
        // lists have to be proven present before anything is allowed to enumerate them.
        List<AssetRecord>? records = document.Records;
        List<AssetCollection>? collections = document.Collections;
        List<AssetVariant>? variants = document.Variants;
        List<AssetSet>? sets = document.Sets;
        List<AssetRecentEntry>? recent = document.Recent;
        Dictionary<string, string>? current = document.CurrentVariantBySet;
        if (records is null || collections is null || variants is null || sets is null
            || recent is null || current is null)
        {
            return "в индексе отсутствует один из обязательных списков";
        }

        if (collections.Count > AssetLibraryDocument.MaxCollections)
        {
            return $"в индексе больше {AssetLibraryDocument.MaxCollections} коллекций";
        }


        if (records.Any(static record => record is null)
            || collections.Any(static collection => collection is null)
            || variants.Any(static variant => variant is null)
            || sets.Any(static set => set is null)
            || recent.Any(static entry => entry is null))
        {
            return "один из обязательных списков содержит пустой элемент";
        }

        var contentHashes = new HashSet<string>(records.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (!IsSha256(record.Sha256))
            {
                return "запись с некорректным SHA-256";
            }

            if (!contentHashes.Add(record.Sha256))
            {
                return "повторяющийся SHA-256";
            }

            if (string.IsNullOrWhiteSpace(record.DisplayName)
                || string.IsNullOrWhiteSpace(record.OriginalFileName)
                || string.IsNullOrWhiteSpace(record.MediaType)
                || string.IsNullOrWhiteSpace(record.Extension)
                || record.Pack is null)
            {
                return "запись ассета без одного из обязательных текстовых полей";
            }

            if (!HasSupportedFormat(record.MediaType, record.Extension))
            {
                return $"ассет «{record.DisplayName}» содержит несогласованный формат изображения";
            }

            try
            {
                _ = AssetImagePolicy.Validate(record.Width, record.Height, record.ByteLength);
            }
            catch (InvalidDataException)
            {
                return $"ассет «{record.DisplayName}» содержит недопустимые параметры изображения";
            }

            if (record.Provenance is null)
            {
                return $"у ассета «{record.DisplayName}» отсутствуют сведения о происхождении";
            }

            if (record.Provenance.Source is null
                || record.Provenance.Author is null
                || record.Provenance.LicenseId is null
                || record.Provenance.LicenseUrl is null)
            {
                return $"у ассета «{record.DisplayName}» повреждены сведения о происхождении";
            }

            if (record.Tags is null || record.Tags.Any(static tag => tag is null))
            {
                return $"у ассета «{record.DisplayName}» отсутствует тег или список тегов";
            }

            if (record.StoredRelativePath is { } stored
                && !CatalogRelativePath.IsValid(stored, "originals"))
            {
                return $"ассет «{record.DisplayName}» содержит небезопасный путь оригинала";
            }

            if (!CatalogRelativePath.IsValid(record.ThumbnailRelativePath, "thumbnails"))
            {
                return $"ассет «{record.DisplayName}» содержит небезопасный путь миниатюры";
            }

            if (record.LinkedPath is { } linked && !IsUsableLinkedPath(linked))
            {
                return $"ассет «{record.DisplayName}» содержит некорректный linked path";
            }
        }

        var variantIds = new HashSet<string>(variants.Count, StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            if (!double.IsFinite(variant.AnchorX) || !double.IsFinite(variant.AnchorY)
                || variant.AnchorX is < 0 or > 1 || variant.AnchorY is < 0 or > 1
                || !double.IsFinite(variant.DefaultScale) || variant.DefaultScale <= 0)
                return "некорректная опора или условный масштаб варианта";
            if (string.IsNullOrWhiteSpace(variant.Id) || string.IsNullOrWhiteSpace(variant.Label))
            {
                return "вариант без идентификатора или названия";
            }

            if (!variantIds.Add(variant.Id))
            {
                return "повторяющийся идентификатор варианта";
            }

            if (string.IsNullOrWhiteSpace(variant.Sha256) || !contentHashes.Contains(variant.Sha256))
            {
                return $"вариант «{variant.Label}» ссылается на отсутствующее содержимое";
            }

            if (variant.MaskSha256 is { } maskSha)
            {
                if (string.Equals(maskSha, variant.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return $"вариант «{variant.Label}» использует изображение как собственную маску";
                }
                if (!IsSha256(maskSha) || !contentHashes.Contains(maskSha))
                {
                    return $"вариант «{variant.Label}» ссылается на отсутствующую маску";
                }

                var visual = records.First(record =>
                    string.Equals(record.Sha256, variant.Sha256, StringComparison.OrdinalIgnoreCase));
                var mask = records.First(record =>
                    string.Equals(record.Sha256, maskSha, StringComparison.OrdinalIgnoreCase));
                if (visual.Width != mask.Width || visual.Height != mask.Height)
                {
                    return $"маска варианта «{variant.Label}» имеет другой размер";
                }
            }
        }

        var collectionIds = new HashSet<string>(collections.Count, StringComparer.Ordinal);
        foreach (var collection in collections)
        {
            if (string.IsNullOrWhiteSpace(collection.Id) || string.IsNullOrWhiteSpace(collection.Name))
            {
                return "коллекция без идентификатора или названия";
            }

            if (!collectionIds.Add(collection.Id))
            {
                return "повторяющийся идентификатор коллекции";
            }
        }

        foreach (var collection in collections)
        {
            if (collection.ParentId is { } parent && !collectionIds.Contains(parent))
            {
                return $"родительская коллекция «{collection.Name}» отсутствует";
            }
        }

        if (FindCollectionCycle(collections) is { } cycle)
        {
            return $"цикл в дереве коллекций: «{cycle}»";
        }

        if (FindExcessiveCollectionDepth(collections) is { } tooDeep)
        {
            return $"дерево коллекций глубже {AssetLibraryDocument.MaxCollectionDepth} уровней: «{tooDeep}»";
        }

        var setIds = new HashSet<string>(sets.Count, StringComparer.Ordinal);
        var setOfVariant = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var set in sets)
        {
            if (set.Placement is { } placement && ValidatePlacement(placement) is { } placementReason)
                return $"паспорт набора «{set.Name}»: {placementReason}";
            if (string.IsNullOrWhiteSpace(set.Id) || string.IsNullOrWhiteSpace(set.Name))
            {
                return "набор без идентификатора или названия";
            }

            if (!setIds.Add(set.Id))
            {
                return "повторяющийся идентификатор набора";
            }

            if (set.VariantIds is not { Count: > 0 })
            {
                return $"набор «{set.Name}» без вариантов";
            }

            var members = new HashSet<string>(set.VariantIds.Count, StringComparer.Ordinal);
            foreach (var variantId in set.VariantIds)
            {
                if (variantId is null || !variantIds.Contains(variantId))
                {
                    return $"набор «{set.Name}» ссылается на отсутствующий вариант";
                }

                if (!members.Add(variantId))
                {
                    return $"набор «{set.Name}» содержит повторяющийся вариант";
                }

                // A variant belongs to one card. Two cards claiming it would make "which set did
                // this stamp come from" ambiguous, and the whole set-aware tool rests on that.
                if (!setOfVariant.TryAdd(variantId, set.Id))
                {
                    return $"вариант входит сразу в два набора, один из них — «{set.Name}»";
                }
            }

            if (string.IsNullOrWhiteSpace(set.CoverVariantId) || !members.Contains(set.CoverVariantId))
            {
                return $"обложка набора «{set.Name}» не входит в него";
            }

            if (set.CollectionId is { } collectionId && !collectionIds.Contains(collectionId))
            {
                return $"набор «{set.Name}» ссылается на отсутствующую коллекцию";
            }

            if (set.Tags is null || set.Tags.Any(static tag => tag is null))
            {
                return $"у набора «{set.Name}» отсутствует тег или список тегов";
            }
        }

        foreach (var entry in recent)
        {
            if (string.IsNullOrWhiteSpace(entry.SetId) || !setIds.Contains(entry.SetId))
            {
                return "недавний элемент ссылается на отсутствующий набор";
            }

            if (!setOfVariant.TryGetValue(entry.VariantId ?? "", out var owner)
                || !string.Equals(owner, entry.SetId, StringComparison.Ordinal))
            {
                return "недавний элемент ссылается на вариант вне своего набора";
            }
        }

        foreach (var (setId, variantId) in current)
        {
            if (!setIds.Contains(setId))
            {
                return "текущий вариант записан для отсутствующего набора";
            }

            if (!setOfVariant.TryGetValue(variantId ?? "", out var owner)
                || !string.Equals(owner, setId, StringComparison.Ordinal))
            {
                return "текущий вариант не входит в свой набор";
            }
        }

        return null;
    }

    private static bool IsUsableLinkedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Any(char.IsControl)
            || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return root is { Length: 3 }
                && root[1] == ':'
                && fullPath[root.Length..].Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .All(Map.Core.Documents.SafePathSegment.IsSafe);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(Uri.IsHexDigit);

    private static bool HasSupportedFormat(string mediaType, string extension) =>
        mediaType.ToLowerInvariant() switch
        {
            "image/png" => string.Equals(extension, "png", StringComparison.OrdinalIgnoreCase),
            "image/jpeg" => extension.Equals("jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals("jpeg", StringComparison.OrdinalIgnoreCase),
            "image/webp" => string.Equals(extension, "webp", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    /// <summary>
    /// Names one collection caught in a parent ring, or null when the graph is a forest. The walk
    /// is iterative and settles every node once, so a ring cannot become unbounded recursion.
    /// </summary>
    private static string? FindCollectionCycle(List<AssetCollection> collections)
    {
        const int Visiting = 1;
        const int Settled = 2;
        var parents = new Dictionary<string, string?>(collections.Count, StringComparer.Ordinal);
        var names = new Dictionary<string, string>(collections.Count, StringComparer.Ordinal);
        foreach (var collection in collections)
        {
            parents[collection.Id] = collection.ParentId;
            names[collection.Id] = collection.Name;
        }

        var state = new Dictionary<string, int>(collections.Count, StringComparer.Ordinal);
        var path = new List<string>();
        foreach (var collection in collections)
        {
            if (state.GetValueOrDefault(collection.Id) == Settled)
            {
                continue;
            }

            path.Clear();
            var node = collection.Id;
            while (true)
            {
                var seen = state.GetValueOrDefault(node);
                if (seen == Settled)
                {
                    break;
                }

                if (seen == Visiting)
                {
                    return names.GetValueOrDefault(node, node);
                }

                state[node] = Visiting;
                path.Add(node);
                if (parents.TryGetValue(node, out var parent) && parent is not null)
                {
                    node = parent;
                    continue;
                }

                break;
            }

            foreach (var settled in path)
            {
                state[settled] = Settled;
            }
        }

        return null;
    }

    private static string? FindExcessiveCollectionDepth(List<AssetCollection> collections)
    {
        var byId = collections.ToDictionary(collection => collection.Id, StringComparer.Ordinal);
        foreach (var collection in collections)
        {
            var depth = 1;
            var current = collection;
            while (current.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent))
            {
                current = parent;
                depth++;
                if (depth > AssetLibraryDocument.MaxCollectionDepth)
                {
                    return collection.Name;
                }
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

    public static string? ValidatePlacement(AssetPlacementDefaults placement)
    {
        if (placement.Role is { } role && !Enum.IsDefined(role)) return "неизвестная роль";
        if (placement.AllowedRotations is null || placement.AllowedRotations.Count > 360
            || placement.AllowedRotations.Any(a => !double.IsFinite(a) || a is < -180 or > 180))
            return "допустимые повороты — до 360 углов от -180 до 180 градусов";
        if (placement.HexEdges is null || placement.HexEdges.Count is not (0 or 6)
            || placement.HexEdges.Any(e => string.IsNullOrWhiteSpace(e) || e.Length > 128))
            return "края гекса — шесть непустых названий по часовой стрелке или пустой список";
        if (placement.CollisionFraction is { } fraction && (!double.IsFinite(fraction) || fraction is < .05 or > 1)
            || !double.IsFinite(placement.RandomSizePercent) || placement.RandomSizePercent is < 0 or > 100)
            return "некорректные параметры расстановки";
        return null;
    }

    private static void TrimBackups(string indexPath)
    {
        var directory = Path.GetDirectoryName(indexPath)!;
        var pattern = Path.GetFileNameWithoutExtension(indexPath) + ".v?.*.json";
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
