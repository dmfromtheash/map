using System.Security.Cryptography;
using SkiaSharp;

namespace Map.Assets;

/// <summary>
/// Local metadata-first library. Opening, searching, sorting and grouping read JSON only;
/// originals are decoded solely when the user asks for a thumbnail or the renderer asks for one
/// image. Content identity is the SHA-256; a logical <see cref="AssetSet"/> orders the variants
/// that name it.
/// </summary>
public sealed class AssetCatalog : IAsyncDisposable
{
    public const int MaxImageDimension = AssetImagePolicy.MaxDimension;
    public const long MaxEncodedBytes = AssetImagePolicy.MaxEncodedBytes;

    private readonly string _root;
    private readonly string _indexPath;
    private readonly Func<DateTimeOffset> _clock;
    private AssetLibraryDocument _document;
    private readonly AssetMetadataWriter _metadataWriter;
    private AssetLibraryDocument _metadataBase = new();
    public long MetadataRevision { get; private set; }
    public long ContentRevision { get; private set; }
    public string? MetadataSaveError => _metadataWriter.Error;
    public async Task FlushMetadataAsync()
    {
        try { await _metadataWriter.FlushAsync(); }
        catch
        {
            if (_metadataWriter.LastSaved is { } saved)
            {
                _document = CopyDocument(saved);
                Reindex(); CaptureMetadataBase(); MetadataRevision++;
            }
            throw;
        }
    }
    public ValueTask DisposeAsync() => new(FlushMetadataAsync());
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
        _metadataWriter = new AssetMetadataWriter(_indexPath, CopyDocument(_document));
        Reindex();
        CaptureMetadataBase();
    }

    public string Root => _root;

    private AssetCatalog(AssetCatalog source, AssetLibraryDocument snapshot)
    {
        _root = source._root;
        _indexPath = source._indexPath;
        _clock = source._clock;
        _metadataWriter = source._metadataWriter;
        _document = snapshot;
        Reindex();
    }

    public Task<AssetQueryResult> QueryAsync(AssetQuery query, CancellationToken cancellationToken)
    {
        var snapshot = MetadataSnapshot();
        return Task.Run(() => new AssetCatalog(this, snapshot).Query(query, cancellationToken), cancellationToken);
    }

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

    public AssetRecord? MaskRecordForVariant(string variantId) =>
        FindVariant(variantId)?.MaskSha256 is { } maskSha ? Find(maskSha) : null;

    // ---------------------------------------------------------------- import

    public AssetRecord Import(string sourcePath, AssetImportOptions options, DateTimeOffset importedUtc)
    {
        using var mutation = BeginMutation();
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
        using var mutation = BeginMutation();
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(setName);
        if (sourcePaths.Count == 0)
        {
            throw new ArgumentException("Набор содержит хотя бы один файл.", nameof(sourcePaths));
        }

        var records = sourcePaths.Select(path => ImportContent(path, options, importedUtc))
            .DistinctBy(record => record.Sha256, StringComparer.OrdinalIgnoreCase).ToArray();
        if (records.Length == 1)
        {
            AssetLibraryStore.AddSingleton(_document, records[0]);
            SaveIndex();
            return _bySetId[AssetIdentity.SetIdForContent(records[0].Sha256)];
        }

        var hashes = records.Select(record => record.Sha256).ToArray();
        var collection = string.IsNullOrWhiteSpace(options.Pack) ? null : AssetIdentity.CollectionId(options.Pack);
        var existing = _document.Sets.FirstOrDefault(set => set.Name == setName.Trim()
            && set.CollectionId == collection
            && set.VariantIds.Select(id => _byVariantId[id].Sha256).SequenceEqual(hashes, StringComparer.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SaveIndex();
            return existing;
        }

        var candidate = AssetIdentity.SetIdForVariants(hashes);
        var id = candidate;
        for (var suffix = 2; _bySetId.ContainsKey(id); suffix++) id = $"{candidate}-{suffix}";
        var variantIds = records.Select(record => AssetIdentity.VariantIdForSet(id, record.Sha256)).ToList();
        for (var index = 0; index < records.Length; index++)
            _document.Variants.Add(new AssetVariant { Id = variantIds[index], Sha256 = records[index].Sha256, Label = records[index].DisplayName });
        AssetLibraryStore.EnsureCollection(_document, options.Pack);
        var result = new AssetSet
        {
            Id = id, Name = setName.Trim(), VariantIds = variantIds, CoverVariantId = variantIds[0],
            CollectionId = collection, Tags = [.. options.Tags.Distinct(StringComparer.OrdinalIgnoreCase)], ImportedUtc = importedUtc,
        };
        _document.Sets.Add(result);
        SaveIndex();
        return result;
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

        var isSvg = string.Equals(file.Extension, ".svg", StringComparison.OrdinalIgnoreCase);
        if (isSvg && options.Linked)
        {
            throw new InvalidDataException(
                "SVG импортируется только как локально растрированный PNG, linked-режим недоступен.");
        }

        byte[] bytes = isSvg
            ? SafeSvgRasterizer.RasterizeFile(fullSource)
            : AssetImagePolicy.ReadEncodedFile(fullSource);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (_bySha.TryGetValue(sha256, out var existing))
        {
            return existing;
        }

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("The file is not a supported PNG, JPEG or WebP image.");
        var metadata = AssetImagePolicy.Inspect(codec, bytes.LongLength);
        var (extension, mediaType) = Format(codec.EncodedFormat);

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "originals"));
        Directory.CreateDirectory(Path.Combine(_root, "thumbnails"));

        string? storedRelative = null;
        if (!options.Linked)
        {
            storedRelative = $"originals/{sha256}.{extension}";
            var storedPath = ResolveCatalogRelative(storedRelative, "originals");
            if (!File.Exists(storedPath))
            {
                File.WriteAllBytes(storedPath, bytes);
            }
        }

        var thumbnailRelative = $"thumbnails/{sha256}.png";
        var thumbnailPath = ResolveCatalogRelative(thumbnailRelative, "thumbnails");
        if (!File.Exists(thumbnailPath))
        {
            File.WriteAllBytes(thumbnailPath, BuildThumbnail(bytes));
        }

        if (storedRelative is not null && DetailLadderDepth(metadata.Width, metadata.Height) > 0)
        {
            BuildDetailLadder(bytes, Path.Combine(_root, "ladder", sha256));
        }

        var record = new AssetRecord
        {
            Sha256 = sha256,
            DisplayName = Path.GetFileNameWithoutExtension(fullSource),
            OriginalFileName = Path.GetFileName(fullSource),
            MediaType = mediaType,
            Extension = extension,
            Width = metadata.Width,
            Height = metadata.Height,
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
    public AssetQueryResult Query(AssetQuery query, CancellationToken cancellationToken = default)
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
        var collections = query.CollectionId is { } root ? DescendantCollections(root) : null;
        var tagGroups = query.Tags.Where(t => !string.IsNullOrWhiteSpace(t))
            .GroupBy(t => t.Contains('/') ? t[..t.IndexOf('/')] : "", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.ToArray()).ToArray();
        foreach (var set in _document.Sets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MatchesSource(set, query.Source)) continue;
            if (set.Archived && !query.IncludeArchived) continue;
            if (query.FavoritesOnly && !set.Favorite)
            {
                continue;
            }

            if (query.RecentOnly && !recentOrder.ContainsKey(set.Id))
            {
                continue;
            }

            if (collections is not null && (set.CollectionId is null || !collections.Contains(set.CollectionId)))
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

            if (tagGroups.Any(group => !group.Any(selected => set.Tags.Any(tag =>
                tag.Equals(selected, StringComparison.OrdinalIgnoreCase)
                || tag.StartsWith(selected + "/", StringComparison.OrdinalIgnoreCase))))) continue;

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

    private string CollectionName(string id)
    {
        var path = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? current = id;
        while (current is not null && seen.Add(current)
            && _document.Collections.FirstOrDefault(item => item.Id == current) is { } collection)
        { path.Add(collection.Name); current = collection.ParentId; }
        path.Reverse();
        return path.Count == 0 ? id : string.Join(" / ", path);
    }

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
            Archived = set.Archived,
        };
    }

    public AssetSetSummary? SummarizeSet(string setId) =>
        FindSet(setId) is { } set ? Summarize(set) : null;

    /// <summary>Collection tree with the number of logical cards in each node and its subtree.</summary>
    public IReadOnlyList<AssetCollectionNode> CollectionTree(AssetSourceFilter source = AssetSourceFilter.All)
    {
        var direct = _document.Sets
            .Where(set => set.CollectionId is not null && !set.Archived && MatchesSource(set, source))
            .GroupBy(set => set.CollectionId!)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var byParent = _document.Collections
            .GroupBy(collection => collection.ParentId ?? "")
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Order).ToList(),
                StringComparer.Ordinal);

        // Validation refuses both parent rings and trees deeper than MaxCollectionDepth. This set
        // remains a final guard against accidental repeated nodes in a transient in-memory model.
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
                if (source != AssetSourceFilter.All && own == 0 && sub.Count == 0) continue;
                nodes.Add(new AssetCollectionNode(
                    collection.Id, collection.Name, own, own + sub.Sum(node => node.TotalCount), sub));
            }

            return nodes;
        }
    }

    // ---------------------------------------------------------------- mutation

    public int CountSets(AssetSourceFilter source) => _document.Sets.Count(set => !set.Archived && MatchesSource(set, source));

    internal void NestBuiltInCollection(string pack, string parent, string title)
    {
        using var mutation = BeginMutation();
        AssetLibraryStore.EnsureCollection(_document, parent);
        var id = AssetIdentity.CollectionId(pack);
        var index = _document.Collections.FindIndex(c => c.Id == id);
        if (index < 0) throw new InvalidOperationException("Imported collection is missing.");
        _document.Collections[index] = _document.Collections[index] with
        { Name = title, ParentId = AssetIdentity.CollectionId(parent) };
        SaveIndex();
    }

    private bool MatchesSource(AssetSet set, AssetSourceFilter source)
    {
        if (source == AssetSourceFilter.All) return true;
        return set.VariantIds.Any(id => _byVariantId.TryGetValue(id, out var variant)
            && _bySha.TryGetValue(variant.Sha256, out var record)
            && (source == AssetSourceFilter.BuiltIn) == (record.Provenance.Source is
                // Recognise metadata from former packs without installing or recreating them.
                "map-original-generated-starter" or "map-original-ai-atlas-20260908"));
    }

    private HashSet<string> DescendantCollections(string root)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { root };
        var children = _document.Collections.ToLookup(c => c.ParentId ?? "");
        var pending = new Queue<string>(); pending.Enqueue(root);
        while (pending.TryDequeue(out var parent))
            foreach (var child in children[parent])
                if (result.Add(child.Id)) pending.Enqueue(child.Id);
        return result;
    }

    /// <summary>
    /// Validates a local image completely before changing the library, then commits the mask
    /// identity and any new content record through the catalog's one atomic index replacement.
    /// </summary>
    public AssetVariant AssignVariantMask(
        string variantId, string sourcePath, DateTimeOffset importedUtc)
    {
        using var mutation = BeginMutation();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!_byVariantId.TryGetValue(variantId, out var variant)
            || Find(variant.Sha256) is not { } visual)
        {
            throw new KeyNotFoundException($"Вариант {variantId} отсутствует в каталоге.");
        }

        var fullSource = Path.GetFullPath(sourcePath);
        var bytes = AssetImagePolicy.ReadEncodedFile(fullSource);
        var maskSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (string.Equals(maskSha256, variant.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Маска должна быть отдельным content asset, а не теми же байтами, что и изображение варианта.");
        }
        using (var data = SKData.CreateCopy(bytes))
        using (var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("Маска не является поддерживаемым PNG, JPEG или WebP."))
        {
            var metadata = AssetImagePolicy.Inspect(codec, bytes.LongLength);
            if (metadata.Width != visual.Width || metadata.Height != visual.Height)
            {
                throw new InvalidDataException(
                    $"Размер маски {metadata.Width}×{metadata.Height} не совпадает с вариантом "
                    + $"{visual.Width}×{visual.Height}.");
            }
        }

        var mask = ImportContent(fullSource, new AssetImportOptions
        {
            Pack = visual.Pack,
            Tags = [.. visual.Tags, "clip-mask"],
            Provenance = new AssetProvenance
            {
                Source = "user-imported-mask",
                LicenseStatus = AssetLicenseStatus.Unknown,
            },
        }, importedUtc);
        var index = _document.Variants.FindIndex(item =>
            string.Equals(item.Id, variantId, StringComparison.Ordinal));
        _document.Variants[index] = variant with { MaskSha256 = mask.Sha256 };
        SaveIndex();
        return _byVariantId[variantId];
    }

    public AssetVariant RemoveVariantMask(string variantId)
    {
        using var mutation = BeginMutation();
        if (!_byVariantId.TryGetValue(variantId, out var variant))
        {
            throw new KeyNotFoundException($"Вариант {variantId} отсутствует в каталоге.");
        }

        if (variant.MaskSha256 is null)
        {
            return variant;
        }

        var index = _document.Variants.FindIndex(item =>
            string.Equals(item.Id, variantId, StringComparison.Ordinal));
        _document.Variants[index] = variant with { MaskSha256 = null };
        SaveIndex();
        return _byVariantId[variantId];
    }

    public void SetFavorite(string sha256, bool favorite)
    {
        using var mutation = BeginMutation();
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
        using var mutation = BeginMutation();
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

    /// <summary>
    /// Stores the placement passport of a card. A passport equal to the built-in defaults is
    /// written as absent, so an untouched card keeps the exact older catalog shape.
    /// </summary>
    public void SetPlacementDefaults(string setId, AssetPlacementDefaults? defaults)
    {
        using var mutation = BeginMutation();
        var index = RequireSetIndex(setId);
        if (defaults is not null && ((defaults.CollisionFraction is { } fraction && !double.IsFinite(fraction))
            || !double.IsFinite(defaults.RandomSizePercent)))
            throw new ArgumentOutOfRangeException(nameof(defaults), "Значения паспорта должны быть числами.");
        var normalized = defaults is null ? null : defaults with
        {
            AllowedRotations = defaults.AllowedRotations?.ToArray()!,
            HexEdges = defaults.HexEdges?.ToArray()!,
            CollisionFraction = defaults.CollisionFraction is { } value ? Math.Clamp(value, 0.05, 1) : null,
            RandomSizePercent = Math.Clamp(defaults.RandomSizePercent, 0, 100),
        };
        if (normalized is not null && AssetLibraryStore.ValidatePlacement(normalized) is { } reason)
            throw new ArgumentException(reason, nameof(defaults));
        _document.Sets[index] = _document.Sets[index] with
        {
            Placement = normalized == new AssetPlacementDefaults() ? null : normalized,
        };
        SaveIndex();
    }

    /// <summary>Moves a card into the archive or back; nothing is deleted either way.</summary>
    public void SetSetArchived(string setId, bool archived)
    {
        using var mutation = BeginMutation();
        var index = RequireSetIndex(setId);
        if (_document.Sets[index].Archived == archived) return;
        _document.Sets[index] = _document.Sets[index] with { Archived = archived };
        SaveIndex();
    }

    public void RenameSet(string setId, string name)
    {
        using var mutation = BeginMutation();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var index = RequireSetIndex(setId);
        _document.Sets[index] = _document.Sets[index] with { Name = name.Trim() };
        SaveIndex();
    }

    public void ReorderSetVariants(string setId, IReadOnlyList<string> orderedVariantIds)
    {
        using var mutation = BeginMutation();
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
        using var mutation = BeginMutation();
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
        using var mutation = BeginMutation();
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
        using var mutation = BeginMutation();
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
        if (Status == AssetLibraryStatus.Damaged || FindSet(setId) is not { } set
            || !set.VariantIds.Contains(variantId, StringComparer.Ordinal))
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
        if (_document.Recent.Count > AssetLibraryDocument.MaxRecent)
            _document.Recent.RemoveRange(AssetLibraryDocument.MaxRecent, _document.Recent.Count - AssetLibraryDocument.MaxRecent);
        MetadataRevision++;
        _metadataWriter.Enqueue(MetadataSnapshot());
    }

    public string? CurrentVariantOf(string setId) =>
        _document.CurrentVariantBySet.TryGetValue(setId, out var variantId) ? variantId : null;

    public void RememberVariant(string setId, string variantId)
    {
        if (Status == AssetLibraryStatus.Damaged || FindSet(setId) is not { } set
            || !set.VariantIds.Contains(variantId, StringComparer.Ordinal)
            || CurrentVariantOf(setId) == variantId) return;
        _document.CurrentVariantBySet[setId] = variantId;
        _metadataWriter.Enqueue(MetadataSnapshot());
    }

    /// <summary>Replaces the flat record list; used by fixtures and by profile generators.</summary>
    public void ReplaceIndex(IReadOnlyList<AssetRecord> records)
    {
        using var mutation = BeginMutation();
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

    /// <summary>Changes only one variant's label and placement defaults, preserving all other owner metadata.</summary>
    public void SetVariantPresentation(string variantId, string label, double anchorX, double anchorY, double defaultScale)
    {
        using var mutation = BeginMutation();
        var index = _document.Variants.FindIndex(v => v.Id == variantId);
        if (index < 0) throw new ArgumentException("Вариант не найден.", nameof(variantId));
        if (string.IsNullOrWhiteSpace(label) || !double.IsFinite(anchorX) || !double.IsFinite(anchorY)
            || !double.IsFinite(defaultScale) || defaultScale <= 0) throw new ArgumentException("Неверные параметры варианта.");
        _document.Variants[index] = _document.Variants[index] with
        { Label = label.Trim(), AnchorX = Math.Clamp(anchorX, 0, 1), AnchorY = Math.Clamp(anchorY, 0, 1), DefaultScale = defaultScale };
        SaveIndex();
    }

    /// <summary>Replaces the whole library document; used by set-shaped profile generators.</summary>
    public void ReplaceLibrary(AssetLibraryDocument document)
    {
        using var mutation = BeginMutation();
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
        var payload = ReadVerified(record);
        return new AssetResolution(payload.Status, payload.Path);
    }

    /// <summary>
    /// Reads one bounded original and verifies the SHA-256 of those exact bytes. Callers that
    /// embed or decode content must use the returned payload instead of reopening its mutable path.
    /// </summary>
    public AssetPayloadResolution ReadVerified(AssetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var path = record.StoredRelativePath is { } stored
            ? ResolveCatalogRelative(stored, "originals")
            : record.LinkedPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return new AssetPayloadResolution(AssetResolutionStatus.Missing, path, null);
        }

        try
        {
            var content = AssetImagePolicy.ReadEncodedFile(path);
            var metadata = AssetImagePolicy.Inspect(content, record.MediaType, record.Extension);
            var actual = Convert.ToHexStringLower(SHA256.HashData(content));
            return string.Equals(actual, record.Sha256, StringComparison.OrdinalIgnoreCase)
                && metadata.Width == record.Width
                && metadata.Height == record.Height
                && content.LongLength == record.ByteLength
                ? new AssetPayloadResolution(AssetResolutionStatus.Available, path, content)
                : new AssetPayloadResolution(AssetResolutionStatus.HashMismatch, path, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AssetPayloadResolution(AssetResolutionStatus.Missing, path, null);
        }
    }

    /// <summary>
    /// Repairs a missing/mismatched catalog source only when the chosen bytes reproduce the
    /// record's exact SHA, dimensions and format. A refusal changes neither bytes nor metadata.
    /// </summary>
    public AssetRecord RepairSource(AssetRecord record, string replacementPath)
    {
        using var mutation = BeginMutation();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementPath);
        if (!_bySha.ContainsKey(record.Sha256))
            throw new KeyNotFoundException("Ассет отсутствует в текущем каталоге.");

        var fullReplacement = Path.GetFullPath(replacementPath);
        var bytes = string.Equals(Path.GetExtension(fullReplacement), ".svg", StringComparison.OrdinalIgnoreCase)
            ? SafeSvgRasterizer.RasterizeFile(fullReplacement)
            : AssetImagePolicy.ReadEncodedFile(fullReplacement);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("Выбранный исходник не является поддержанным изображением.");
        var metadata = AssetImagePolicy.Inspect(codec, bytes.LongLength);
        var (extension, mediaType) = Format(codec.EncodedFormat);
        if (!string.Equals(hash, record.Sha256, StringComparison.OrdinalIgnoreCase)
            || metadata.Width != record.Width || metadata.Height != record.Height
            || bytes.LongLength != record.ByteLength
            || !string.Equals(extension, record.Extension, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(mediaType, record.MediaType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Выбранный файл не совпадает с ожидаемыми SHA-256, форматом и размерами ассета.");
        }

        AssetRecord repaired;
        if (record.StoredRelativePath is { } stored)
        {
            var destination = ResolveCatalogRelative(stored, "originals");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            AtomicWrite(destination, bytes);
            repaired = record;
        }
        else
        {
            repaired = record with { LinkedPath = fullReplacement };
            var index = _document.Records.FindIndex(item =>
                string.Equals(item.Sha256, record.Sha256, StringComparison.OrdinalIgnoreCase));
            _document.Records[index] = repaired;
        }

        var thumbnail = ResolveCatalogRelative(record.ThumbnailRelativePath, "thumbnails");
        Directory.CreateDirectory(Path.GetDirectoryName(thumbnail)!);
        AtomicWrite(thumbnail, BuildThumbnail(bytes));
        SaveIndex();
        return repaired;
    }

    /// <summary>
    /// Removes one catalog identity and its cards after a UI confirmation. Stored original bytes
    /// are deliberately retained, so catalog removal is recoverable by a later re-import.
    /// </summary>
    public void RemoveRecord(string sha256)
    {
        using var mutation = BeginMutation();
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        var variantIds = _document.Variants
            .Where(item => string.Equals(item.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        _document.Records.RemoveAll(item =>
            string.Equals(item.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
        _document.Variants.RemoveAll(item => variantIds.Contains(item.Id));
        for (var index = _document.Sets.Count - 1; index >= 0; index--)
        {
            var set = _document.Sets[index];
            var remaining = set.VariantIds.Where(id => !variantIds.Contains(id)).ToList();
            if (remaining.Count == 0)
            {
                _document.Recent.RemoveAll(item => item.SetId == set.Id);
                _document.CurrentVariantBySet.Remove(set.Id);
                _document.Sets.RemoveAt(index);
            }
            else if (remaining.Count != set.VariantIds.Count)
            {
                _document.Sets[index] = set with
                {
                    VariantIds = remaining,
                    CoverVariantId = remaining.Contains(set.CoverVariantId, StringComparer.Ordinal)
                        ? set.CoverVariantId : remaining[0],
                };
                // Recent uses and the remembered variant of a set that keeps other variants must not
                // point at the removed one: the index validator rejects such a save, so the removal
                // used to fail outright once the asset had been placed.
                _document.Recent.RemoveAll(item => item.SetId == set.Id && variantIds.Contains(item.VariantId));
                if (_document.CurrentVariantBySet.TryGetValue(set.Id, out var current) && variantIds.Contains(current))
                {
                    _document.CurrentVariantBySet[set.Id] = remaining[0];
                }
            }
        }
        SaveIndex();
    }

    public string ThumbnailPath(AssetRecord record) =>
        ResolveCatalogRelative(record.ThumbnailRelativePath, "thumbnails");

    /// <summary>
    /// Resolves an optional preview without letting malformed catalog metadata block a verified
    /// original. A damaged index is rejected during load; this also protects transient records
    /// supplied directly by tests or callers.
    /// </summary>
    public bool TryThumbnailPath(AssetRecord record, out string? path)
    {
        ArgumentNullException.ThrowIfNull(record);
        return CatalogRelativePath.TryResolve(
            _root, record.ThumbnailRelativePath, "thumbnails", out path);
    }

    public string? ThumbnailPathForVariant(string variantId) =>
        RecordForVariant(variantId) is { } record && TryThumbnailPath(record, out var path) ? path : null;

    // ---------------------------------------------------------------- internals

    private int RequireSetIndex(string setId)
    {
        var index = _document.Sets.FindIndex(set => string.Equals(set.Id, setId, StringComparison.Ordinal));
        return index >= 0 ? index : throw new KeyNotFoundException($"Набор {setId} отсутствует в каталоге.");
    }

    private static void AtomicWrite(string destination, byte[] bytes)
    {
        var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
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

    private CatalogMutation? _activeMutation;

    // Mutators own a private candidate. A failed validation, import or publication restores the
    // last visible document and every lookup; source bytes are content-addressed and never deleted.
    private CatalogMutation BeginMutation()
    {
        RequireWritable();
        return new CatalogMutation(this);
    }

    private static AssetLibraryDocument CopyDocument(AssetLibraryDocument document) => new()
    {
        SchemaVersion = document.SchemaVersion, Status = document.Status, Diagnostic = document.Diagnostic,
        Records = [.. document.Records], Variants = [.. document.Variants], Collections = [.. document.Collections],
        Sets = [.. document.Sets], Recent = [.. document.Recent], CurrentVariantBySet = new(document.CurrentVariantBySet),
    };

    private sealed class CatalogMutation : IDisposable
    {
        private readonly AssetCatalog _owner;
        private readonly AssetLibraryDocument _before;
        private readonly CatalogMutation? _parent;
        public bool Published { get; set; }
        public CatalogMutation(AssetCatalog owner)
        {
            _owner = owner; _before = owner._document; _parent = owner._activeMutation;
            owner._document = CopyDocument(_before); owner._activeMutation = this;
            owner.Reindex();
        }
        public void Dispose()
        {
            if (!Published) { _owner._document = _before; _owner.Reindex(); }
            _owner._activeMutation = _parent;
        }
    }

    private void SaveIndex()
    {
        RequireWritable();
        // Do not publish candidate indexes/revisions or replace the shared metadata base until
        // the serial writer has validated, read back and atomically published this candidate.
        var candidate = CopyDocument(_document);
        _metadataWriter.Enqueue(candidate);
        _metadataWriter.FlushAsync().GetAwaiter().GetResult();
        Reindex();
        CaptureMetadataBase();
        MetadataRevision++;
        ContentRevision++;
        if (_activeMutation is { } mutation) mutation.Published = true;
    }
    private void CaptureMetadataBase() => _metadataBase = new AssetLibraryDocument
    {
        Records = [.. _document.Records.Select(record => record with { Tags = record.Tags.ToArray() })],
        Variants = [.. _document.Variants],
        Collections = [.. _document.Collections],
        Sets = [.. _document.Sets.Select(set => set with
            { VariantIds = set.VariantIds.ToArray(), Tags = set.Tags.ToArray() })],
    };

    private AssetLibraryDocument MetadataSnapshot() => new()
    {
        Records = _metadataBase.Records, Variants = _metadataBase.Variants,
        Collections = _metadataBase.Collections, Sets = _metadataBase.Sets,
        Recent = [.. _document.Recent], CurrentVariantBySet = new(_document.CurrentVariantBySet),
    };

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

    private string ResolveCatalogRelative(string relative, string requiredTopLevel)
    {
        if (!CatalogRelativePath.TryResolve(_root, relative, requiredTopLevel, out var path))
        {
            throw new InvalidDataException(
                $"Catalog entry is not a safe {requiredTopLevel} path inside the catalog root.");
        }

        return path!;
    }

    private static (string Extension, string MediaType) Format(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Png => ("png", "image/png"),
        SKEncodedImageFormat.Jpeg => ("jpg", "image/jpeg"),
        SKEncodedImageFormat.Webp => ("webp", "image/webp"),
        _ => throw new InvalidDataException($"Image format {format} is not supported."),
    };

    /// <summary>
    /// Side of the square preview written next to every original. A gallery tile is drawn up to
    /// about 180 px wide on an ordinary window, so a 128 px preview was always upscaled and soft.
    /// </summary>
    public const int ThumbnailSize = 256;

    /// <summary>
    /// Names the original behind one preview file, so a caller that found the preview too small
    /// can rebuild it in the background without touching the index. False when the preview does
    /// not belong to this catalog or the original is not stored locally.
    /// </summary>
    public bool TryDescribeThumbnail(string thumbnailPath, out string originalPath)
    {
        originalPath = "";
        if (string.IsNullOrEmpty(thumbnailPath)) return false;
        var name = Path.GetFileNameWithoutExtension(thumbnailPath);
        if (!_bySha.TryGetValue(name, out var record)) return false;
        if (!TryThumbnailPath(record, out var expected)
            || !string.Equals(Path.GetFullPath(expected!), Path.GetFullPath(thumbnailPath), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (record.StoredRelativePath is { } stored
            && CatalogRelativePath.TryResolve(_root, stored, "originals", out var storedPath)
            && File.Exists(storedPath))
        {
            originalPath = storedPath!;
            return true;
        }

        if (record.LinkedPath is { } linked && File.Exists(linked))
        {
            originalPath = linked;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Rewrites one preview from its original at the current <see cref="ThumbnailSize"/>. Pure
    /// file work: safe off the UI thread, and the index is not involved because the preview path
    /// is derived from the content hash and never changes.
    /// </summary>
    public static void RebuildThumbnailFile(string originalPath, string thumbnailPath)
    {
        var bytes = AssetImagePolicy.ReadEncodedFile(originalPath);
        AtomicWrite(thumbnailPath, BuildThumbnail(bytes));
    }

    private static byte[] BuildThumbnail(byte[] encoded)
    {
        const int size = ThumbnailSize;
        using var bitmap = AssetImagePolicy.DecodeThumbnailSource(encoded, size);
        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("Cannot create thumbnail surface.");
        surface.Canvas.Clear(SKColors.Transparent);
        var scale = Math.Min((double)size / bitmap.Width, (double)size / bitmap.Height);
        var width = (float)(bitmap.Width * scale);
        var height = (float)(bitmap.Height * scale);
        var rect = new SKRect((size - width) / 2, (size - height) / 2, (size + width) / 2, (size + height) / 2);
        using var source = SKImage.FromBitmap(bitmap);
        surface.Canvas.DrawImage(
            source, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    // ---------------------------------------------------------------- detail ladder

    /// <summary>
    /// A stored original longer than this on its long side also gets a ladder of halved copies
    /// (<c>ladder/&lt;sha&gt;/&lt;level&gt;.png</c>). A far view then decodes the small step it
    /// needs instead of the whole high-resolution master, which is only read when the view is
    /// close enough to show its pixels. The steps are derived files like thumbnails: the
    /// identity and the original bytes of the asset do not change.
    /// </summary>
    public const int DetailLadderThreshold = 2048;

    /// <summary>The ladder stops at the first step whose long side is at most this.</summary>
    public const int DetailLadderSmallestSide = 512;

    /// <summary>How many halved steps an image of this size gets; zero for ordinary sizes.</summary>
    public static int DetailLadderDepth(int width, int height)
    {
        var side = Math.Max(width, height);
        if (side <= DetailLadderThreshold) return 0;
        var depth = 0;
        while ((side >> depth) > DetailLadderSmallestSide) depth++;
        return depth;
    }

    /// <summary>The file of one halved step (1 = half size), when it exists in this catalog.</summary>
    public bool TryDetailLevelPath(AssetRecord record, int level, out string? path) =>
        TryDetailLevelPath(_root, record, level, out path);

    /// <summary>
    /// The same for a catalog known only by its root: an export or a save works from frozen
    /// catalog records on a worker and must not touch the live catalog.
    /// </summary>
    public static bool TryDetailLevelPath(string root, AssetRecord record, int level, out string? path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(record);
        path = null;
        if (level < 1 || level > DetailLadderDepth(record.Width, record.Height)) return false;
        return CatalogRelativePath.TryResolve(root, $"ladder/{record.Sha256.ToLowerInvariant()}/{level}.png", "ladder", out path)
            && File.Exists(path);
    }

    /// <summary>
    /// Writes every missing step of the ladder for one original. Each step is resampled from
    /// the one above it with a Mitchell cubic filter, so ink lines stay crisp and paper grain
    /// does not alias. Pure file work: safe off the UI thread.
    /// </summary>
    public static void BuildDetailLadder(byte[] encoded, string directory)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        using var decoded = SKBitmap.Decode(encoded)
            ?? throw new InvalidDataException("The original cannot be decoded for its detail ladder.");
        var depth = DetailLadderDepth(decoded.Width, decoded.Height);
        if (depth == 0) return;
        Directory.CreateDirectory(directory);
        var source = SKImage.FromBitmap(decoded);
        try
        {
            for (var level = 1; level <= depth; level++)
            {
                var width = Math.Max(1, decoded.Width >> level);
                var height = Math.Max(1, decoded.Height >> level);
                var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
                using (var pixels = bitmap.PeekPixels())
                {
                    if (!source.ScalePixels(pixels, new SKSamplingOptions(SKCubicResampler.Mitchell)))
                    {
                        bitmap.Dispose();
                        throw new InvalidDataException("A detail ladder step could not be resampled.");
                    }
                }
                bitmap.SetImmutable();
                var next = SKImage.FromBitmap(bitmap);
                bitmap.Dispose();
                source.Dispose();
                source = next;
                var path = Path.Combine(directory, level.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png");
                if (File.Exists(path)) continue;
                using var png = source.Encode(SKEncodedImageFormat.Png, 100);
                AtomicWrite(path, png.ToArray());
            }
        }
        finally
        {
            source.Dispose();
        }
    }
}
