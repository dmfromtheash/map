using System.Security.Cryptography;
using SkiaSharp;

namespace Map.Assets;

/// <summary>
/// Local metadata-first library. Opening, searching, sorting and grouping read JSON only;
/// originals are decoded solely when the user asks for a thumbnail or the renderer asks for one
/// image. Content identity is the SHA-256; a logical <see cref="AssetSet"/> orders the variants
/// that name it.
/// </summary>
public sealed class AssetCatalog
{
    public const int MaxImageDimension = 32_768;
    public const long MaxEncodedBytes = 512L * 1024 * 1024;

    private readonly string _root;
    private readonly string _indexPath;
    private readonly Func<DateTimeOffset> _clock;
    private AssetLibraryDocument _document;
    private Dictionary<string, AssetRecord> _bySha = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, AssetVariant> _byVariantId = new(StringComparer.Ordinal);
    private Dictionary<string, AssetSet> _bySetId = new(StringComparer.Ordinal);

    public AssetCatalog(string root)
        : this(root, null)
    {
    }

    public AssetCatalog(string root, Func<DateTimeOffset>? clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = AssetCatalogRoots.Resolve(root);
        _indexPath = Path.Combine(_root, "catalog.json");
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _document = AssetLibraryStore.Load(_indexPath, _clock);
        Reindex();
    }

    public string Root => _root;

    public AssetLibraryStatus Status => _document.Status;

    /// <summary>Why the library is not Ready; empty when it is.</summary>
    public string Diagnostic => _document.Diagnostic;

    public int SchemaVersion => _document.SchemaVersion;

    public IReadOnlyList<AssetRecord> Records => _document.Records;

    public IReadOnlyList<AssetSet> Sets => _document.Sets;

    public IReadOnlyList<AssetVariant> Variants => _document.Variants;

    public IReadOnlyList<AssetCollection> Collections => _document.Collections;

    public IReadOnlyList<AssetRecentEntry> Recent => _document.Recent;

    // ---------------------------------------------------------------- lookup

    public AssetRecord? Find(string sha256) =>
        sha256 is not null && _bySha.TryGetValue(sha256, out var record) ? record : null;

    public AssetVariant? FindVariant(string variantId) =>
        variantId is not null && _byVariantId.TryGetValue(variantId, out var variant) ? variant : null;

    public AssetSet? FindSet(string setId) =>
        setId is not null && _bySetId.TryGetValue(setId, out var set) ? set : null;

    /// <summary>The set a placed stamp belongs to, found back by content hash.</summary>
    public AssetSet? FindSetForContent(string sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return null;
        }

        var variantIds = _document.Variants
            .Where(variant => string.Equals(variant.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            .Select(variant => variant.Id)
            .ToHashSet(StringComparer.Ordinal);
        return variantIds.Count == 0
            ? null
            : _document.Sets.FirstOrDefault(set => set.VariantIds.Any(variantIds.Contains));
    }

    public AssetRecord? RecordForVariant(string variantId) =>
        FindVariant(variantId) is { } variant ? Find(variant.Sha256) : null;

    // ---------------------------------------------------------------- import

    public AssetRecord Import(string sourcePath, AssetImportOptions options, DateTimeOffset importedUtc)
    {
        RequireWritable();
        var record = ImportContent(sourcePath, options, importedUtc);
        AssetLibraryStore.AddSingleton(_document, record);
        SaveIndex();
        return record;
    }

    /// <summary>
    /// One import of many files becomes ONE ordered logical set. The bytes are still shared by
    /// hash, so re-importing a file that is already in the library adds no second copy.
    /// </summary>
    public AssetSet ImportSet(
        IReadOnlyList<string> sourcePaths,
        string setName,
        AssetImportOptions options,
        DateTimeOffset importedUtc)
    {
        RequireWritable();
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(setName);
        if (sourcePaths.Count == 0)
        {
            throw new ArgumentException("Набор содержит хотя бы один файл.", nameof(sourcePaths));
        }

        var variantIds = new List<string>(sourcePaths.Count);
        var hashes = new List<string>(sourcePaths.Count);
        foreach (var path in sourcePaths)
        {
            var record = ImportContent(path, options, importedUtc);
            var variantId = AssetIdentity.VariantIdForContent(record.Sha256);
            if (variantIds.Contains(variantId, StringComparer.Ordinal))
            {
                continue;
            }

            if (_document.Variants.All(item => item.Id != variantId))
            {
                _document.Variants.Add(new AssetVariant
                {
                    Id = variantId,
                    Sha256 = record.Sha256,
                    Label = record.DisplayName,
                });
            }

            variantIds.Add(variantId);
            hashes.Add(record.Sha256);
        }

        if (variantIds.Count == 1)
        {
            // A "set" of one file is exactly a singleton card; do not invent a second identity.
            var single = Find(_document.Variants.Single(item => item.Id == variantIds[0]).Sha256)!;
            AssetLibraryStore.AddSingleton(_document, single);
            SaveIndex();
            Reindex();
            return _bySetId[AssetIdentity.SetIdForContent(single.Sha256)];
        }

        AssetLibraryStore.EnsureCollection(_document, options.Pack);
        var id = UniqueSetId(AssetIdentity.SetIdForVariants(hashes), variantIds);
        if (_bySetId.TryGetValue(id, out var existing)
            && existing.VariantIds.SequenceEqual(variantIds, StringComparer.Ordinal))
        {
            SaveIndex();
            return existing;
        }

        var set = new AssetSet
        {
            Id = id,
            Name = setName.Trim(),
            VariantIds = variantIds,
            CoverVariantId = variantIds[0],
            CollectionId = string.IsNullOrWhiteSpace(options.Pack)
                ? null
                : AssetIdentity.CollectionId(options.Pack),
            Tags = [.. options.Tags.Distinct(StringComparer.OrdinalIgnoreCase)],
            ImportedUtc = importedUtc,
        };
        _document.Sets.Add(set);
        SaveIndex();
        return set;
    }

    private string UniqueSetId(string candidate, IReadOnlyList<string> variantIds)
    {
        if (!_bySetId.TryGetValue(candidate, out var clash)
            || clash.VariantIds.SequenceEqual(variantIds, StringComparer.Ordinal))
        {
            return candidate;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var next = $"{candidate}-{suffix}";
            if (!_bySetId.TryGetValue(next, out var other)
                || other.VariantIds.SequenceEqual(variantIds, StringComparer.Ordinal))
            {
                return next;
            }
        }

        throw new InvalidOperationException("Не удалось выделить идентификатор набора.");
    }

    /// <summary>Validates, stores and thumbnails one file. Identical bytes reuse the record.</summary>
    private AssetRecord ImportContent(string sourcePath, AssetImportOptions options, DateTimeOffset importedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(options);
        var fullSource = Path.GetFullPath(sourcePath);
        var file = new FileInfo(fullSource);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Asset image was not found.", fullSource);
        }

        if (file.Length is <= 0 or > MaxEncodedBytes)
        {
            throw new InvalidDataException($"Asset size {file.Length} is outside the supported range.");
        }

        byte[] bytes = File.ReadAllBytes(fullSource);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (_bySha.TryGetValue(sha256, out var existing))
        {
            return existing;
        }

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("The file is not a supported PNG, JPEG or WebP image.");
        var (extension, mediaType) = Format(codec.EncodedFormat);
        if (codec.Info.Width is <= 0 or > MaxImageDimension
            || codec.Info.Height is <= 0 or > MaxImageDimension)
        {
            throw new InvalidDataException(
                $"Image dimensions {codec.Info.Width}x{codec.Info.Height} are outside the supported range.");
        }

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "originals"));
        Directory.CreateDirectory(Path.Combine(_root, "thumbnails"));

        string? storedRelative = null;
        if (!options.Linked)
        {
            storedRelative = $"originals/{sha256}.{extension}";
            var storedPath = ResolveCatalogRelative(storedRelative);
            if (!File.Exists(storedPath))
            {
                File.WriteAllBytes(storedPath, bytes);
            }
        }

        var thumbnailRelative = $"thumbnails/{sha256}.png";
        var thumbnailPath = ResolveCatalogRelative(thumbnailRelative);
        if (!File.Exists(thumbnailPath))
        {
            File.WriteAllBytes(thumbnailPath, BuildThumbnail(bytes));
        }

        var record = new AssetRecord
        {
            Sha256 = sha256,
            DisplayName = Path.GetFileNameWithoutExtension(fullSource),
            OriginalFileName = Path.GetFileName(fullSource),
            MediaType = mediaType,
            Extension = extension,
            Width = codec.Info.Width,
            Height = codec.Info.Height,
            HasAlpha = codec.Info.AlphaType != SKAlphaType.Opaque,
            ByteLength = bytes.LongLength,
            ImportedUtc = importedUtc,
            Pack = options.Pack,
            Tags = [.. options.Tags.Distinct(StringComparer.OrdinalIgnoreCase)],
            Favorite = false,
            Provenance = options.Provenance,
            StoredRelativePath = storedRelative,
            LinkedPath = options.Linked ? fullSource : null,
            ThumbnailRelativePath = thumbnailRelative,
        };

        _document.Records.Add(record);
        _bySha[sha256] = record;
        return record;
    }

    // ---------------------------------------------------------------- query

    /// <summary>Flat record search kept for callers that address content rather than cards.</summary>
    public IReadOnlyList<AssetRecord> Search(
        string? query = null, string? pack = null, string? tag = null, bool favoritesOnly = false)
    {
        IEnumerable<AssetRecord> result = _document.Records;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var text = query.Trim();
            result = result.Where(record =>
                record.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || record.OriginalFileName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || record.Tags.Any(value => value.Contains(text, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(pack))
        {
            result = result.Where(record => string.Equals(record.Pack, pack, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            result = result.Where(record => record.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        }

        if (favoritesOnly)
        {
            result = result.Where(record => record.Favorite);
        }

        return [.. result.OrderByDescending(record => record.ImportedUtc).ThenBy(record => record.DisplayName)];
    }

    /// <summary>
    /// The library query the grid uses. Returns logical cards with badge count and cover metadata;
    /// no original is opened, so this stays metadata-only at ten thousand sets.
    /// </summary>
    public AssetQueryResult Query(AssetQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var recentOrder = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var entry in _document.Recent)
        {
            if (!recentOrder.TryGetValue(entry.SetId, out var known) || entry.UsedUtc > known)
            {
                recentOrder[entry.SetId] = entry.UsedUtc;
            }
        }

        var text = string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim();
        var used = query.UsedContentHashes is null
            ? null
            : new HashSet<string>(query.UsedContentHashes, StringComparer.OrdinalIgnoreCase);

        var matched = new List<AssetSetSummary>(_document.Sets.Count);
        foreach (var set in _document.Sets)
        {
            if (query.FavoritesOnly && !set.Favorite)
            {
                continue;
            }

            if (query.RecentOnly && !recentOrder.ContainsKey(set.Id))
            {
                continue;
            }

            if (query.CollectionId is { } collection
                && !string.Equals(set.CollectionId, collection, StringComparison.Ordinal))
            {
                continue;
            }

            if (query.UntaggedOnly && set.Tags.Count > 0)
            {
                continue;
            }

            if (query.Tag is { } tag && !set.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (query.ImportedFromUtc is { } from && set.ImportedUtc < from)
            {
                continue;
            }

            if (query.ImportedToUtc is { } to && set.ImportedUtc > to)
            {
                continue;
            }

            if (Summarize(set) is not { } summary)
            {
                continue;
            }

            if (query.MediaType is { } mediaType
                && !string.Equals(summary.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (used is not null && !set.VariantIds.Any(id =>
                _byVariantId.TryGetValue(id, out var variant) && used.Contains(variant.Sha256)))
            {
                continue;
            }

            if (text is not null && !Matches(set, summary, text))
            {
                continue;
            }

            matched.Add(summary);
        }

        var sorted = Sort(matched, query, recentOrder);
        return new AssetQueryResult(sorted, GroupBy(sorted, query.Group), sorted.Count);
    }

    private bool Matches(AssetSet set, AssetSetSummary summary, string text)
    {
        if (set.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || set.Tags.Any(tag => tag.Contains(text, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (var variantId in set.VariantIds)
        {
            if (!_byVariantId.TryGetValue(variantId, out var variant))
            {
                continue;
            }

            if (variant.Label.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (_bySha.TryGetValue(variant.Sha256, out var record)
                && record.OriginalFileName.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return summary.CoverSha256.StartsWith(text, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<AssetSetSummary> Sort(
        List<AssetSetSummary> matched, AssetQuery query, Dictionary<string, DateTimeOffset> recentOrder)
    {
        if (query.RecentOnly)
        {
            return [.. matched.OrderByDescending(item => recentOrder[item.SetId]).ThenBy(item => item.Name)];
        }

        return query.Sort switch
        {
            AssetSortOrder.NameAscending =>
                [.. matched.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(item => item.SetId, StringComparer.Ordinal)],
            AssetSortOrder.NameDescending =>
                [.. matched.OrderByDescending(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(item => item.SetId, StringComparer.Ordinal)],
            AssetSortOrder.ImportedOldest =>
                [.. matched.OrderBy(item => item.ImportedUtc).ThenBy(item => item.SetId, StringComparer.Ordinal)],
            _ =>
                [.. matched.OrderByDescending(item => item.ImportedUtc)
                    .ThenBy(item => item.SetId, StringComparer.Ordinal)],
        };
    }

    private IReadOnlyList<AssetSetGroup> GroupBy(IReadOnlyList<AssetSetSummary> sets, AssetGrouping grouping)
    {
        switch (grouping)
        {
            case AssetGrouping.Collection:
                return [.. sets
                    .GroupBy(item => item.CollectionId ?? "")
                    .Select(group => new AssetSetGroup(
                        group.Key,
                        group.Key.Length == 0 ? "Без коллекции" : CollectionName(group.Key),
                        [.. group]))
                    .OrderBy(group => group.Title, StringComparer.CurrentCultureIgnoreCase)];

            case AssetGrouping.Tag:
                var byTag = new Dictionary<string, List<AssetSetSummary>>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in sets)
                {
                    if (item.Tags.Count == 0)
                    {
                        Add(byTag, "", item);
                        continue;
                    }

                    foreach (var tag in item.Tags)
                    {
                        Add(byTag, tag, item);
                    }
                }

                return [.. byTag
                    .Select(pair => new AssetSetGroup(
                        pair.Key, pair.Key.Length == 0 ? "Без тегов" : pair.Key, pair.Value))
                    .OrderBy(group => group.Title, StringComparer.CurrentCultureIgnoreCase)];

            default:
                return [];
        }

        static void Add(Dictionary<string, List<AssetSetSummary>> map, string key, AssetSetSummary item)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }

            list.Add(item);
        }
    }

    private string CollectionName(string id) =>
        _document.Collections.FirstOrDefault(item => item.Id == id)?.Name ?? id;

    public AssetSetSummary? Summarize(AssetSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (!_byVariantId.TryGetValue(set.CoverVariantId, out var cover))
        {
            return null;
        }

        _bySha.TryGetValue(cover.Sha256, out var record);
        return new AssetSetSummary
        {
            SetId = set.Id,
            Name = set.Name,
            VariantCount = set.VariantIds.Count,
            CoverVariantId = cover.Id,
            CoverSha256 = cover.Sha256,
            CoverThumbnailRelativePath = record?.ThumbnailRelativePath ?? "",
            Favorite = set.Favorite,
            CollectionId = set.CollectionId,
            Tags = set.Tags,
            ImportedUtc = set.ImportedUtc,
            CoverWidth = record?.Width ?? 0,
            CoverHeight = record?.Height ?? 0,
            MediaType = record?.MediaType ?? "",
        };
    }

    public AssetSetSummary? SummarizeSet(string setId) =>
        FindSet(setId) is { } set ? Summarize(set) : null;

    /// <summary>Collection tree with the number of logical cards in each node and its subtree.</summary>
    public IReadOnlyList<AssetCollectionNode> CollectionTree()
    {
        var direct = _document.Sets
            .Where(set => set.CollectionId is not null)
            .GroupBy(set => set.CollectionId!)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var byParent = _document.Collections
            .GroupBy(collection => collection.ParentId ?? "")
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Order).ToList(),
                StringComparer.Ordinal);

        // Validation already refuses a document whose parents form a ring, so this set is belt
        // and braces: the tree walk must be incapable of unbounded recursion on any input.
        var walking = new HashSet<string>(StringComparer.Ordinal);
        return Build("");

        List<AssetCollectionNode> Build(string parentId)
        {
            if (!byParent.TryGetValue(parentId, out var children))
            {
                return [];
            }

            var nodes = new List<AssetCollectionNode>(children.Count);
            foreach (var collection in children)
            {
                if (!walking.Add(collection.Id))
                {
                    continue;
                }

                var sub = Build(collection.Id);
                var own = direct.GetValueOrDefault(collection.Id);
                nodes.Add(new AssetCollectionNode(
                    collection.Id, collection.Name, own, own + sub.Sum(node => node.TotalCount), sub));
            }

            return nodes;
        }
    }

    // ---------------------------------------------------------------- mutation

    public void SetFavorite(string sha256, bool favorite)
    {
        RequireWritable();
        var index = _document.Records.FindIndex(record =>
            string.Equals(record.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new KeyNotFoundException($"Asset {sha256} is not in the catalog.");
        }

        _document.Records[index] = _document.Records[index] with { Favorite = favorite };
        var singleton = AssetIdentity.SetIdForContent(sha256);
        var setIndex = _document.Sets.FindIndex(set => set.Id == singleton);
        if (setIndex >= 0)
        {
            _document.Sets[setIndex] = _document.Sets[setIndex] with { Favorite = favorite };
        }

        SaveIndex();
    }

    /// <summary>Favorite belongs to the logical card, so the whole set is marked at once.</summary>
    public void SetSetFavorite(string setId, bool favorite)
    {
        RequireWritable();
        var index = RequireSetIndex(setId);
        _document.Sets[index] = _document.Sets[index] with { Favorite = favorite };
        if (_document.Sets[index].VariantIds.Count == 1
            && _byVariantId.TryGetValue(_document.Sets[index].CoverVariantId, out var variant))
        {
            var recordIndex = _document.Records.FindIndex(record =>
                string.Equals(record.Sha256, variant.Sha256, StringComparison.OrdinalIgnoreCase));
            if (recordIndex >= 0)
            {
                _document.Records[recordIndex] = _document.Records[recordIndex] with { Favorite = favorite };
            }
        }

        SaveIndex();
    }

    public void RenameSet(string setId, string name)
    {
        RequireWritable();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var index = RequireSetIndex(setId);
        _document.Sets[index] = _document.Sets[index] with { Name = name.Trim() };
        SaveIndex();
    }

    public void ReorderSetVariants(string setId, IReadOnlyList<string> orderedVariantIds)
    {
        RequireWritable();
        ArgumentNullException.ThrowIfNull(orderedVariantIds);
        var index = RequireSetIndex(setId);
        var set = _document.Sets[index];
        if (orderedVariantIds.Count != set.VariantIds.Count
            || !orderedVariantIds.OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(set.VariantIds.OrderBy(id => id, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new ArgumentException("Новый порядок должен содержать те же варианты.", nameof(orderedVariantIds));
        }

        _document.Sets[index] = set with { VariantIds = [.. orderedVariantIds] };
        SaveIndex();
    }

    public void SetCoverVariant(string setId, string variantId)
    {
        RequireWritable();
        var index = RequireSetIndex(setId);
        var set = _document.Sets[index];
        if (!set.VariantIds.Contains(variantId, StringComparer.Ordinal))
        {
            throw new ArgumentException("Обложка должна быть вариантом этого набора.", nameof(variantId));
        }

        _document.Sets[index] = set with { CoverVariantId = variantId };
        SaveIndex();
    }

    /// <summary>
    /// Detaches a variant from its set. The content, the original file and the variant record all
    /// survive: removing a card membership is not a delete.
    /// </summary>
    public void RemoveVariantFromSet(string setId, string variantId)
    {
        RequireWritable();
        var index = RequireSetIndex(setId);
        var set = _document.Sets[index];
        if (set.VariantIds.Count <= 1)
        {
            throw new InvalidOperationException("Последний вариант нельзя исключить из набора.");
        }

        if (!set.VariantIds.Contains(variantId, StringComparer.Ordinal))
        {
            return;
        }

        var remaining = set.VariantIds.Where(id => !string.Equals(id, variantId, StringComparison.Ordinal)).ToList();
        _document.Sets[index] = set with
        {
            VariantIds = remaining,
            CoverVariantId = string.Equals(set.CoverVariantId, variantId, StringComparison.Ordinal)
                ? remaining[0]
                : set.CoverVariantId,
        };
        SaveIndex();
    }

    public void SetTags(string setId, IReadOnlyList<string> tags)
    {
        RequireWritable();
        ArgumentNullException.ThrowIfNull(tags);
        var index = RequireSetIndex(setId);
        _document.Sets[index] = _document.Sets[index] with
        {
            Tags = [.. tags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)],
        };
        SaveIndex();
    }

    /// <summary>Records one selection in the bounded recent list and remembers the variant.</summary>
    public void TouchRecent(string setId, string variantId, DateTimeOffset usedUtc)
    {
        if (Status == AssetLibraryStatus.Damaged || !_bySetId.ContainsKey(setId))
        {
            return;
        }

        _document.Recent.RemoveAll(entry => string.Equals(entry.SetId, setId, StringComparison.Ordinal));
        _document.Recent.Insert(0, new AssetRecentEntry
        {
            SetId = setId,
            VariantId = variantId,
            UsedUtc = usedUtc,
        });
        _document.CurrentVariantBySet[setId] = variantId;
        SaveIndex();
    }

    public string? CurrentVariantOf(string setId) =>
        _document.CurrentVariantBySet.TryGetValue(setId, out var variantId) ? variantId : null;

    /// <summary>Replaces the flat record list; used by fixtures and by profile generators.</summary>
    public void ReplaceIndex(IReadOnlyList<AssetRecord> records)
    {
        RequireWritable();
        ArgumentNullException.ThrowIfNull(records);
        _document.Records = [.. records
            .GroupBy(record => record.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
        _document.Variants.Clear();
        _document.Sets.Clear();
        _document.Collections.Clear();
        _document.Recent.Clear();
        _document.CurrentVariantBySet.Clear();
        foreach (var record in _document.Records)
        {
            AssetLibraryStore.AddSingleton(_document, record);
        }

        SaveIndex();
    }

    /// <summary>Replaces the whole library document; used by set-shaped profile generators.</summary>
    public void ReplaceLibrary(AssetLibraryDocument document)
    {
        RequireWritable();
        ArgumentNullException.ThrowIfNull(document);
        if (AssetLibraryStore.Validate(document) is { } reason)
        {
            throw new ArgumentException("Библиотека противоречива: " + reason, nameof(document));
        }

        _document = document;
        _document.Status = AssetLibraryStatus.Ready;
        SaveIndex();
    }

    // ---------------------------------------------------------------- resolution

    public AssetResolution Resolve(AssetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var path = record.StoredRelativePath is { } stored
            ? ResolveCatalogRelative(stored)
            : record.LinkedPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new AssetResolution(AssetResolutionStatus.Missing, path);
        }

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        return string.Equals(actual, record.Sha256, StringComparison.OrdinalIgnoreCase)
            ? new AssetResolution(AssetResolutionStatus.Available, path)
            : new AssetResolution(AssetResolutionStatus.HashMismatch, path);
    }

    public string ThumbnailPath(AssetRecord record) => ResolveCatalogRelative(record.ThumbnailRelativePath);

    public string? ThumbnailPathForVariant(string variantId) =>
        RecordForVariant(variantId) is { } record ? ResolveCatalogRelative(record.ThumbnailRelativePath) : null;

    // ---------------------------------------------------------------- internals

    private int RequireSetIndex(string setId)
    {
        var index = _document.Sets.FindIndex(set => string.Equals(set.Id, setId, StringComparison.Ordinal));
        return index >= 0 ? index : throw new KeyNotFoundException($"Набор {setId} отсутствует в каталоге.");
    }

    /// <summary>A damaged index is read-only: the user's only copy is never overwritten.</summary>
    private void RequireWritable()
    {
        if (Status == AssetLibraryStatus.Damaged)
        {
            throw new InvalidOperationException(
                "Индекс каталога повреждён и защищён от записи. " + Diagnostic);
        }
    }

    private void SaveIndex()
    {
        RequireWritable();
        AssetLibraryStore.Save(_indexPath, _document);
        Reindex();
    }

    private void Reindex()
    {
        _bySha = _document.Records
            .GroupBy(record => record.Sha256, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        _byVariantId = _document.Variants
            .GroupBy(variant => variant.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        _bySetId = _document.Sets
            .GroupBy(set => set.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    private string ResolveCatalogRelative(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Catalog entry points outside the catalog root.");
        }

        return path;
    }

    private static (string Extension, string MediaType) Format(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Png => ("png", "image/png"),
        SKEncodedImageFormat.Jpeg => ("jpg", "image/jpeg"),
        SKEncodedImageFormat.Webp => ("webp", "image/webp"),
        _ => throw new InvalidDataException($"Image format {format} is not supported."),
    };

    private static byte[] BuildThumbnail(byte[] encoded)
    {
        using var bitmap = SKBitmap.Decode(encoded)
            ?? throw new InvalidDataException("Image pixels cannot be decoded.");
        var info = new SKImageInfo(128, 128, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("Cannot create thumbnail surface.");
        surface.Canvas.Clear(SKColors.Transparent);
        var scale = Math.Min(128.0 / bitmap.Width, 128.0 / bitmap.Height);
        var width = (float)(bitmap.Width * scale);
        var height = (float)(bitmap.Height * scale);
        var rect = new SKRect((128 - width) / 2, (128 - height) / 2, (128 + width) / 2, (128 + height) / 2);
        using var source = SKImage.FromBitmap(bitmap);
        surface.Canvas.DrawImage(
            source, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
