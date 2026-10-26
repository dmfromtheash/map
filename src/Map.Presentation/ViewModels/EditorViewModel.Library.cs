using System.Globalization;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    // ================================================================ local library

    /// <summary>How the local library loaded; the UI shows migrated and damaged apart.</summary>
    public AssetLibraryStatus LibraryStatus =>
        _assetCatalog?.Status ?? AssetLibraryStatus.Ready;

    public string LibraryDiagnostic => _assetCatalog?.Diagnostic ?? "";

    public bool IsLibraryAvailable =>
        _assetCatalog is not null && _assetCatalog.Status != AssetLibraryStatus.Damaged;

    public IReadOnlyList<AssetSetSummary> LibrarySets => _librarySets;

    public IReadOnlyList<AssetSetGroup> LibraryGroups => _libraryGroups;

    public IReadOnlyList<AssetCollectionNode> LibraryCollections => _libraryCollections;

    /// <summary>The tiles a virtualized grid is currently allowed to realise.</summary>
    public AssetTileWindow VisibleLibraryTileWindow => _visibleTiles;

    public IReadOnlyList<AssetSetSummary> VisibleLibraryTiles => _visibleTiles.Tiles;

    public string LibrarySearchText
    {
        get => _librarySearchText;
        set
        {
            if (SetField(ref _librarySearchText, value ?? ""))
            {
                RefreshLibrary();
            }
        }
    }

    public string? LibraryCollectionId
    {
        get => _libraryCollectionId;
        set
        {
            if (SetField(ref _libraryCollectionId, value))
            {
                RefreshLibrary();
            }
        }
    }

    public string? LibraryTag
    {
        get => _libraryTag;
        set
        {
            if (_libraryTags.Count == (value is null ? 0 : 1) && _libraryTag == value) return;
            _libraryTags.Clear();
            if (value is not null) _libraryTags.Add(value);
            _libraryTag = value;
            Raise(nameof(LibraryTag)); Raise(nameof(LibraryTags));
            RefreshLibrary();
        }
    }

    public bool LibraryUntaggedOnly
    {
        get => _libraryUntaggedOnly;
        set
        {
            if (SetField(ref _libraryUntaggedOnly, value))
            {
                RefreshLibrary();
            }
        }
    }

    public bool LibraryFavoritesOnly
    {
        get => _libraryFavoritesOnly;
        set
        {
            if (SetField(ref _libraryFavoritesOnly, value))
            {
                RefreshLibrary();
            }
        }
    }

    public bool LibraryRecentOnly
    {
        get => _libraryRecentOnly;
        set
        {
            if (SetField(ref _libraryRecentOnly, value))
            {
                RefreshLibrary();
            }
        }
    }

    /// <summary>Restricts the library to content the open map actually places.</summary>
    public bool LibraryUsedInMapOnly
    {
        get => _libraryUsedInMapOnly;
        set
        {
            if (SetField(ref _libraryUsedInMapOnly, value))
            {
                RefreshLibrary();
            }
        }
    }

    /// <summary>«Архивные»: archived cards are listed too, marked as archived.</summary>
    public bool LibraryShowArchived
    {
        get => _libraryShowArchived;
        set
        {
            if (SetField(ref _libraryShowArchived, value))
            {
                RefreshLibrary();
            }
        }
    }

    /// <summary>True when the chosen card is in the archive.</summary>
    public bool SelectedSetArchived => _selectedSetId is { } id && _assetCatalog?.FindSet(id)?.Archived == true;

    /// <summary>
    /// Moves the chosen card into the archive or back. Nothing is deleted: maps keep their
    /// stamps, and «Архивные» shows the card again.
    /// </summary>
    public bool ToggleSelectedSetArchived()
    {
        if (_selectedSetId is not { } id || _assetCatalog is null)
        {
            StatusDetail = "Сначала выберите набор в библиотеке.";
            return false;
        }
        var archived = !SelectedSetArchived;
        if (!MutateSet(() => _assetCatalog.SetSetArchived(id, archived))) return false;
        Raise(nameof(SelectedSetArchived));
        StatusDetail = archived
            ? "Набор перенесён в архив: в каталоге он виден только с фильтром «Архивные»."
            : "Набор возвращён из архива.";
        return true;
    }

    public string? LibraryMediaType
    {
        get => _libraryMediaType;
        set
        {
            if (SetField(ref _libraryMediaType, value))
            {
                RefreshLibrary();
            }
        }
    }

    public DateTimeOffset? LibraryImportedFromUtc
    {
        get => _libraryImportedFromUtc;
        set
        {
            if (SetField(ref _libraryImportedFromUtc, value))
            {
                RefreshLibrary();
            }
        }
    }

    public DateTimeOffset? LibraryImportedToUtc
    {
        get => _libraryImportedToUtc;
        set
        {
            if (SetField(ref _libraryImportedToUtc, value))
            {
                RefreshLibrary();
            }
        }
    }

    public AssetSortOrder LibrarySort
    {
        get => _librarySort;
        set
        {
            if (SetField(ref _librarySort, value))
            {
                RefreshLibrary();
            }
        }
    }

    public AssetGrouping LibraryGrouping
    {
        get => _libraryGrouping;
        set
        {
            if (SetField(ref _libraryGrouping, value))
            {
                RefreshLibrary();
            }
        }
    }

    /// <summary>Re-runs the current query. Metadata only: no original image is opened here.</summary>
    public void RefreshLibrary()
    {
        _libraryQueryVersion++;
        if (_suppressLibraryRefresh)
        {
            return;
        }

        if (_assetCatalog is null || _assetCatalog.Status == AssetLibraryStatus.Damaged)
        {
            _librarySets = [];
            _libraryGroups = [];
            _libraryCollections = [];
            _libraryRows = [];
            _libraryTagChoices = [];
            _libraryMediaTypeChoices = [];
            _visibleTiles = AssetTileWindow.Empty;
            _visibleRowFirst = 0;
            _visibleRowCount = 0;
            ForgetAvailability();
            RaiseLibrary();
            return;
        }

        var result = _assetCatalog.Query(CurrentLibraryQuery());

        _librarySets = result.Sets;
        _libraryGroups = result.Groups;
        _libraryCollections = _assetCatalog.CollectionTree(LibrarySource);
        _libraryRows = BuildLibraryRows(result);
        RefreshFilterChoices();
        SyncSelectedLibraryRow();

        // A new result means the previous verdicts describe rows that may no longer be on screen,
        // and a file may have changed since. Nothing is carried over as "still fine".
        ForgetAvailability();
        _visibleTiles = SliceVisibleRows(_visibleRowFirst, _visibleRowCount);
        RaiseLibrary();
    }

    /// <summary>
    /// Turns one query result into the flat row sequence the grid virtualizes. Without grouping
    /// every row is a card; with grouping each header is followed by the cards it covers, so the
    /// same single list control shows both without ever materialising the whole library.
    /// </summary>
    private int _libraryQueryVersion;
    private readonly SemaphoreSlim _libraryQueryGate = new(1, 1);
    private AssetQuery CurrentLibraryQuery() => new AssetQuery
        {
            Text = _librarySearchText,
            CollectionId = _libraryCollectionId,
            Tags = _libraryTags.ToArray(),
            Source = LibrarySource,
            UntaggedOnly = _libraryUntaggedOnly,
            FavoritesOnly = _libraryFavoritesOnly,
            IncludeArchived = _libraryShowArchived,
            RecentOnly = _libraryRecentOnly,
            UsedContentHashes = _libraryUsedInMapOnly ? UsedContentHashes() : null,
            MediaType = _libraryMediaType,
            ImportedFromUtc = _libraryImportedFromUtc,
            ImportedToUtc = _libraryImportedToUtc,
            Sort = _librarySort,
            Group = _libraryGrouping,
        };

    public async Task SearchLibraryAsync(string text, CancellationToken cancellationToken)
    {
        if (_assetCatalog is null) return;
        SetField(ref _librarySearchText, text, nameof(LibrarySearchText));
        var version = ++_libraryQueryVersion;
        var query = CurrentLibraryQuery();
        await _libraryQueryGate.WaitAsync(cancellationToken);
        AssetQueryResult result;
        try { result = await _assetCatalog.QueryAsync(query, cancellationToken); }
        finally { _libraryQueryGate.Release(); }
        if (version != _libraryQueryVersion || cancellationToken.IsCancellationRequested) return;
        _librarySets = result.Sets;
        _libraryGroups = result.Groups;
        _libraryRows = BuildLibraryRows(result);
        SyncSelectedLibraryRow();
        _visibleTiles = SliceVisibleRows(_visibleRowFirst, _visibleRowCount);
        RaiseLibrary();
    }

    private List<AssetLibraryRow> BuildLibraryRows(AssetQueryResult result)
    {
        if (_libraryGrouping == AssetGrouping.None || result.Groups.Count == 0)
        {
            return [.. result.Sets.Select(card => AssetLibraryRow.ForCard("", card))];
        }

        var rows = new List<AssetLibraryRow>(result.Sets.Count + result.Groups.Count);
        foreach (var group in result.Groups)
        {
            rows.Add(AssetLibraryRow.Header(group.Key, group.Title, group.Items.Count));
            foreach (var card in group.Items)
            {
                rows.Add(AssetLibraryRow.ForCard(group.Key, card));
            }
        }

        return rows;
    }

    /// <summary>
    /// The choices the filter controls offer. They come from the whole catalog rather than from
    /// the current result, so choosing one tag does not make every other tag disappear.
    /// </summary>
    private void RefreshFilterChoices()
    {
        if (_assetCatalog is null)
        {
            return;
        }

        var tags = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var set in _assetCatalog.Sets)
        {
            foreach (var tag in set.Tags)
            {
                tags.Add(tag);
            }
        }

        var media = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in _assetCatalog.Records)
        {
            if (record.MediaType.Length > 0)
            {
                media.Add(record.MediaType);
            }
        }

        _libraryTagChoices = [.. tags];
        _libraryMediaTypeChoices = [.. media];
    }

    private void SyncSelectedLibraryRow()
    {
        _selectedLibraryRowIndex = -1;
        if (_selectedSetId is null)
        {
            return;
        }

        for (var index = 0; index < _libraryRows.Count; index++)
        {
            if (_libraryRows[index].Card is { } card
                && string.Equals(card.SetId, _selectedSetId, StringComparison.Ordinal))
            {
                _selectedLibraryRowIndex = index;
                return;
            }
        }
    }

    /// <summary>The rows a virtualized grid may realise, and the cards inside that span.</summary>
    public IReadOnlyList<AssetLibraryRow> LibraryRows => _libraryRows;

    /// <summary>Row of the selected card in the current grouping, or -1 when it is not shown.</summary>
    public int SelectedLibraryRowIndex => _selectedLibraryRowIndex;

    /// <summary>
    /// Makes the card on one row current. A header is not a card: selecting it must leave the
    /// tool holding whatever set it already had, so the answer is simply no.
    /// </summary>
    public bool SelectLibraryRow(int index)
    {
        if (index < 0 || index >= _libraryRows.Count || _libraryRows[index].Card is not { } card)
        {
            return false;
        }

        UseSinglePlacementForNewAsset();
        SelectAssetSet(card.SetId);
        return true;
    }

    /// <summary>
    /// Declares which slice of the result the grid can actually show. Only these rows ever ask
    /// for a thumbnail or a file check, so ten thousand cards never become ten thousand bitmaps.
    /// </summary>
    public void SetLibraryVisibleRange(int firstIndex, int count)
    {
        var window = SliceVisibleRows(firstIndex, count);
        if (window.FirstIndex == _visibleTiles.FirstIndex && window.Count == _visibleTiles.Count
            && _visibleRowFirst == Math.Max(0, firstIndex))
        {
            return;
        }

        _visibleRowFirst = window.FirstIndex;
        _visibleRowCount = Math.Max(0, Math.Min(count, _libraryRows.Count - window.FirstIndex));
        _visibleTiles = window;
        Raise(nameof(VisibleLibraryTiles));
        Raise(nameof(VisibleLibraryTileWindow));
    }

    /// <summary>
    /// Reports the rows the grid really realised. The cost of this is the number of realised
    /// containers, never the size of the result: a layout pass must not walk the whole library.
    /// </summary>
    public void ReportRealizedLibraryRows(IEnumerable<int> realizedRowIndexes)
    {
        ArgumentNullException.ThrowIfNull(realizedRowIndexes);
        var first = int.MaxValue;
        var last = -1;
        foreach (var index in realizedRowIndexes)
        {
            if (index < 0 || index >= _libraryRows.Count)
            {
                continue;
            }

            if (index < first)
            {
                first = index;
            }

            if (index > last)
            {
                last = index;
            }
        }

        if (last < 0)
        {
            SetLibraryVisibleRange(0, 0);
            return;
        }

        SetLibraryVisibleRange(first, last - first + 1);
    }

    private AssetTileWindow SliceVisibleRows(int firstIndex, int count)
    {
        if (_libraryRows.Count == 0 || count <= 0)
        {
            return AssetTileWindow.Empty;
        }

        var first = Math.Clamp(firstIndex, 0, Math.Max(0, _libraryRows.Count - 1));
        var take = Math.Clamp(count, 0, _libraryRows.Count - first);
        var cards = new List<AssetSetSummary>(take);
        for (var index = first; index < first + take; index++)
        {
            if (_libraryRows[index].Card is { } card)
            {
                cards.Add(card);
            }
        }

        return new AssetTileWindow(first, cards.Count, cards);
    }
    /// <summary>Absolute path of a card cover thumbnail, or null when the library has none.</summary>
    public string? LibraryThumbnailPath(AssetSetSummary summary)
    {
        if (_assetCatalog is null || summary is null
            || string.IsNullOrEmpty(summary.CoverThumbnailRelativePath))
        {
            return null;
        }

        return _assetCatalog.ThumbnailPathForVariant(summary.CoverVariantId);
    }

    public string? LibraryThumbnailPathForVariant(string variantId) =>
        _assetCatalog?.ThumbnailPathForVariant(variantId);

    /// <summary>
    /// The stored original behind one preview file, for a view that found the preview smaller
    /// than the current standard and wants to rebuild it off the UI thread. Metadata only.
    /// </summary>
    public bool TryDescribeLibraryThumbnail(string thumbnailPath, out string originalPath)
    {
        originalPath = "";
        return _assetCatalog is not null && _assetCatalog.TryDescribeThumbnail(thumbnailPath, out originalPath);
    }

    /// <summary>
    /// The catalog thumbnail of the exact asset a placed object draws, or null when the object is
    /// not asset-backed or the catalog cannot show it. Lists use this so a placed tree looks like
    /// that tree instead of like the tool that placed it.
    /// </summary>
    public string? ObjectThumbnailPath(SceneObject sceneObject)
    {
        if (_assetCatalog is null || sceneObject?.Stamp?.AssetSha256 is not { } sha)
        {
            return null;
        }

        return _assetCatalog.Find(sha) is { } record && _assetCatalog.TryThumbnailPath(record, out var path)
            ? path
            : null;
    }

    // ---------------------------------------------------------------- filter controls

    /// <summary>Every tag the catalog knows, so choosing one never hides the others.</summary>
    public IReadOnlyList<string> LibraryTagChoices => _libraryTagChoices;

    public IReadOnlyList<string> LibraryMediaTypeChoices => _libraryMediaTypeChoices;

    /// <summary>Import date bound as the user types it, in ISO <c>ГГГГ-ММ-ДД</c> form.</summary>
    public string LibraryImportedFromText
    {
        get => _libraryImportedFromText;
        set => SetImportedBound(value, from: true);
    }

    public string LibraryImportedToText
    {
        get => _libraryImportedToText;
        set => SetImportedBound(value, from: false);
    }

    /// <summary>True while one of the date fields holds something that is not a date.</summary>
    public bool HasLibraryDateError => _libraryDateError;

    public bool HasActiveLibraryFilters =>
        _librarySearchText.Length > 0
        || _libraryCollectionId is not null
        || _librarySource != AssetSourceFilter.All
        || _libraryTag is not null
        || _libraryMediaType is not null
        || _libraryUntaggedOnly
        || _libraryFavoritesOnly
        || _libraryRecentOnly
        || _libraryUsedInMapOnly
        || _libraryShowArchived
        || _libraryImportedFromText.Length > 0
        || _libraryImportedToText.Length > 0;

    /// <summary>
    /// Says in words which filters are on. Icon-only toggles alone would leave the user guessing
    /// why the library looks empty, so the panel can always show the reason.
    /// </summary>
    public string ActiveLibraryFilterSummary
    {
        get
        {
            var parts = new List<string>();
            if (_librarySearchText.Length > 0)
            {
                parts.Add($"поиск «{_librarySearchText}»");
            }

            if (_libraryCollectionId is not null)
            {
                parts.Add("коллекция " + CollectionTitle(_libraryCollectionId));
            }

            foreach (var tag in _libraryTags)
            {
                parts.Add($"тег «{tag}»");
            }

            if (_libraryUntaggedOnly)
            {
                parts.Add("без тегов");
            }

            if (_libraryFavoritesOnly)
            {
                parts.Add("избранное");
            }

            if (_libraryRecentOnly)
            {
                parts.Add("недавние");
            }

            if (_libraryUsedInMapOnly)
            {
                parts.Add("используется на карте");
            }

            if (_libraryShowArchived)
            {
                parts.Add("с архивными");
            }

            if (_libraryMediaType is { } media)
            {
                parts.Add("тип " + media);
            }

            if (_libraryImportedFromText.Length > 0)
            {
                parts.Add(_libraryImportedFromUtc is null
                    ? "с (дата не распознана)"
                    : "с " + _libraryImportedFromText);
            }

            if (_libraryImportedToText.Length > 0)
            {
                parts.Add(_libraryImportedToUtc is null
                    ? "по (дата не распознана)"
                    : "по " + _libraryImportedToText);
            }

            return parts.Count == 0 ? "Фильтры не заданы" : string.Join(" · ", parts);
        }
    }

    /// <summary>Drops every filter in one pass, so the grid is rebuilt once rather than ten times.</summary>
    public void ClearLibraryFilters()
    {
        _suppressLibraryRefresh = true;
        try
        {
            LibrarySearchText = "";
            LibraryCollectionId = null;
            LibrarySource = AssetSourceFilter.All;
            LibraryTag = null;
            LibraryMediaType = null;
            LibraryUntaggedOnly = false;
            LibraryFavoritesOnly = false;
            LibraryRecentOnly = false;
            LibraryUsedInMapOnly = false;
            LibraryShowArchived = false;
            LibraryImportedFromText = "";
            LibraryImportedToText = "";
        }
        finally
        {
            _suppressLibraryRefresh = false;
        }

        RefreshLibrary();
        RaiseFilterState();
        StatusDetail = "Фильтры библиотеки сброшены.";
    }

    private void SetImportedBound(string? value, bool from)
    {
        var text = (value ?? "").Trim();
        if (from ? text == _libraryImportedFromText : text == _libraryImportedToText)
        {
            return;
        }

        DateTimeOffset? bound = null;
        if (text.Length > 0 && DateTimeOffset.TryParseExact(
            text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            // "to" means the whole of that day, otherwise a card imported at noon would fall
            // outside a range whose upper bound is its own import date.
            bound = from ? parsed : parsed.AddDays(1).AddTicks(-1);
        }

        if (from)
        {
            _libraryImportedFromText = text;
            _libraryImportedFromUtc = bound;
        }
        else
        {
            _libraryImportedToText = text;
            _libraryImportedToUtc = bound;
        }

        _libraryDateError = IsInvalidDate(_libraryImportedFromText) || IsInvalidDate(_libraryImportedToText);
        Raise(from ? nameof(LibraryImportedFromText) : nameof(LibraryImportedToText));
        Raise(nameof(HasLibraryDateError));
        RefreshLibrary();
        RaiseFilterState();

        static bool IsInvalidDate(string text) => text.Length > 0 && !DateTimeOffset.TryParseExact(
            text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);
    }

    /// <summary>The plain name of a collection for a breadcrumb or a tooltip; the id when unknown.</summary>
    public string LibraryCollectionTitle(string id) => CollectionTitle(id).Trim('«', '»');

    private string CollectionTitle(string id)
    {
        string? found = null;
        Walk(_libraryCollections);
        return found ?? id;

        void Walk(IReadOnlyList<AssetCollectionNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (found is not null)
                {
                    return;
                }

                if (string.Equals(node.Id, id, StringComparison.Ordinal))
                {
                    found = "«" + node.Name + "»";
                    return;
                }

                Walk(node.Children);
            }
        }
    }

    private void RaiseFilterState()
    {
        Raise(nameof(HasActiveLibraryFilters));
        Raise(nameof(ActiveLibraryFilterSummary));
        Raise(nameof(LibraryTagChoices));
        Raise(nameof(LibraryMediaTypeChoices));
    }

    // ---------------------------------------------------------------- set management

    public bool CanRenameSelectedSet =>
        _assetCatalog is not null && IsLibraryAvailable && _selectedSetId is not null;

    /// <summary>Whether the current variant has anywhere to move in the set's manual order.</summary>
    public bool CanMoveCurrentVariant(int direction)
    {
        if (!CanRenameSelectedSet || direction == 0 || _selectedSetVariants.Count <= 1)
        {
            return false;
        }

        var target = _currentVariantIndex + Math.Sign(direction);
        return target >= 0 && target < _selectedSetVariants.Count;
    }

    public bool CanMakeCurrentVariantCover =>
        CanRenameSelectedSet && _selectedSetVariants.Count > 1
        && CurrentVariant is { } variant && SelectedSet is { } set
        && !string.Equals(set.CoverVariantId, variant.Id, StringComparison.Ordinal);

    /// <summary>Renames the card. The set id and every variant id stay exactly as they were.</summary>
    public bool RenameSelectedSet(string? name)
    {
        if (!CanRenameSelectedSet)
        {
            StatusDetail = "Сначала выберите набор в библиотеке.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            StatusDetail = "Имя набора не может быть пустым.";
            return false;
        }

        if (!MutateSet(() => _assetCatalog!.RenameSet(_selectedSetId!, name)))
        {
            return false;
        }

        StatusDetail = $"Набор переименован: «{name.Trim()}».";
        return true;
    }

    /// <summary>
    /// Moves the current variant one place in the set's own order. The variant stays current, so
    /// the position readout follows the thumbnail the user is looking at.
    /// </summary>
    public bool MoveCurrentVariant(int direction)
    {
        if (!CanMoveCurrentVariant(direction))
        {
            StatusDetail = _selectedSetId is null
                ? "Сначала выберите набор в библиотеке."
                : "Этот вариант уже на краю набора.";
            return false;
        }

        var order = _selectedSetVariants.Select(variant => variant.Id).ToList();
        var from = _currentVariantIndex;
        var to = from + Math.Sign(direction);
        (order[from], order[to]) = (order[to], order[from]);
        var moved = order[to];
        if (!MutateSet(() => _assetCatalog!.ReorderSetVariants(_selectedSetId!, order)))
        {
            return false;
        }

        SelectVariantById(moved);
        StatusDetail = $"Порядок изменён: вариант {CurrentVariantLabel}.";
        return true;
    }

    /// <summary>Promotes the current variant to the card's cover without touching the order.</summary>
    public bool MakeCurrentVariantCover()
    {
        if (!CanMakeCurrentVariantCover)
        {
            StatusDetail = _selectedSetId is null
                ? "Сначала выберите набор в библиотеке."
                : "Этот вариант уже является обложкой набора.";
            return false;
        }

        var variantId = CurrentVariant!.Id;
        if (!MutateSet(() => _assetCatalog!.SetCoverVariant(_selectedSetId!, variantId)))
        {
            return false;
        }

        SelectVariantById(variantId);
        StatusDetail = $"Обложкой набора стал вариант {CurrentVariantLabel}.";
        return true;
    }

    /// <summary>Runs one catalog mutation and rebuilds the panel, or reports why it failed.</summary>
    private bool MutateSet(Action mutation)
    {
        try
        {
            mutation();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException
            or ArgumentException or KeyNotFoundException)
        {
            StatusDetail = "Не удалось изменить набор: " + ex.Message;
            _log.Error("Asset set change failed", ex);
            return false;
        }

        var setId = _selectedSetId;
        RefreshLibrary();
        SelectAssetSet(setId);
        Raise(nameof(SelectedSet));
        RaiseSetManagementState();
        return true;
    }

    private void SelectVariantById(string variantId)
    {
        for (var index = 0; index < _selectedSetVariants.Count; index++)
        {
            if (string.Equals(_selectedSetVariants[index].Id, variantId, StringComparison.Ordinal))
            {
                _currentVariantIndex = index;
                break;
            }
        }

        RememberCurrentVariant();
        RaiseVariantState();
        RaiseSetManagementState();
    }

    private void RaiseSetManagementState()
    {
        Raise(nameof(CanRenameSelectedSet));
        Raise(nameof(CanMakeCurrentVariantCover));
    }

    // ---------------------------------------------------------------- availability

    /// <summary>What the library currently knows about one content file.</summary>
    public AssetAvailability AvailabilityOf(string? sha256) =>
        sha256 is not null && _availability.TryGetValue(sha256, out var state)
            ? state
            : AssetAvailability.Unknown;

    /// <summary>Bumped whenever a verdict lands, so the panel knows to redraw its badges.</summary>
    public int AvailabilityRevision => _availabilityRevision;

    public static string AvailabilityText(AssetAvailability availability) => availability switch
    {
        AssetAvailability.Available => "файл на месте",
        AssetAvailability.Missing => "файл отсутствует",
        AssetAvailability.HashMismatch => "файл не совпадает с сохранённым SHA-256",
        AssetAvailability.Unsupported => "формат не поддерживается",
        AssetAvailability.Checking => "проверка файла…",
        _ => "файл не проверен",
    };

    /// <summary>
    /// Verifies the content behind the rows that are actually on screen and behind the set the
    /// stamp tool holds - and nothing else. Hashing happens off the UI thread, and a verdict from
    /// a query the user has already moved on from is discarded rather than shown.
    /// </summary>
    public async Task RefreshVisibleAvailabilityAsync()
    {
        if (_assetCatalog is null || _assetResolver is null
            || _assetCatalog.Status == AssetLibraryStatus.Damaged)
        {
            return;
        }

        var pending = new List<AssetRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tile in _visibleTiles.Tiles)
        {
            Consider(tile.CoverSha256);
        }

        if (CurrentVariant is { } variant) Consider(variant.Sha256);

        if (pending.Count == 0)
        {
            return;
        }

        foreach (var record in pending)
        {
            _availability[record.Sha256] = AssetAvailability.Checking;
        }

        var generation = ++_availabilityGeneration;
        BumpAvailability();

        var resolver = _assetResolver;
        var verdicts = await Task.Run(
            () => pending.Select(record => (record.Sha256, State: resolver.Check(record))).ToList());

        // The query moved on while we were hashing: this answer describes rows nobody is looking
        // at any more, and writing it back would show a stale state.
        if (generation != _availabilityGeneration)
        {
            return;
        }

        foreach (var (sha256, state) in verdicts)
        {
            _availability[sha256] = state;
        }

        BumpAvailability();

        void Consider(string? sha256)
        {
            if (string.IsNullOrEmpty(sha256) || !seen.Add(sha256)
                || AvailabilityOf(sha256) != AssetAvailability.Unknown)
            {
                return;
            }

            if (_assetCatalog.Find(sha256) is { } record)
            {
                pending.Add(record);
            }
        }
    }

    private void ForgetAvailability()
    {
        if (_availability.Count == 0)
        {
            return;
        }

        _availability.Clear();
        _availabilityGeneration++;
        BumpAvailability();
    }

    private void BumpAvailability()
    {
        _availabilityRevision++;
        Raise(nameof(AvailabilityRevision));
    }

    /// <summary>
    /// Every content hash the open map really reaches: stamps, line and shape strokes, shape
    /// texture fills and nested composition items all come from the one walk the composition
    /// transfer already defines, and filter textures are added on top of it.
    /// </summary>
    private HashSet<string> UsedContentHashes()
    {
        var objects = Session.Document.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects);
        var hashes = new HashSet<string>(
            CompositionTransfer.UsedAssetHashes(objects), StringComparer.OrdinalIgnoreCase);
        foreach (var filter in Session.Document.Layers.OfType<FilterLayer>())
        {
            if (filter.Settings.TextureAssetSha256 is { Length: > 0 } texture)
            {
                hashes.Add(texture);
            }
        }

        return hashes;
    }

    /// <summary>How many entries the bounded recent list currently holds.</summary>
    public int RecentCount => _assetCatalog?.Recent.Count ?? 0;

    private long _compactMetadataRevision = -1;
    private long _compactContentRevision = -1;
    private IReadOnlyList<AssetSetSummary> _compactRecentSets = [];
    private IReadOnlyList<AssetSetSummary> _compactFavoriteSets = [];
    public IReadOnlyList<AssetSetSummary> CompactRecentSets { get { RefreshCompactSets(); return _compactRecentSets; } }
    public IReadOnlyList<AssetSetSummary> CompactFavoriteSets { get { RefreshCompactSets(); return _compactFavoriteSets; } }
    private void RefreshCompactSets()
    {
        if (_assetCatalog is null || _compactMetadataRevision == _assetCatalog.MetadataRevision) return;
        _compactMetadataRevision = _assetCatalog.MetadataRevision;
        _compactRecentSets = _assetCatalog.Recent.Select(e => _assetCatalog.SummarizeSet(e.SetId))
            .OfType<AssetSetSummary>().ToArray();
        if (_compactContentRevision == _assetCatalog.ContentRevision) return;
        _compactContentRevision = _assetCatalog.ContentRevision;
        _compactFavoriteSets = _assetCatalog.Sets.Where(s => s.Favorite)
            .Select(s => _assetCatalog.SummarizeSet(s.Id)).OfType<AssetSetSummary>().ToArray();
    }

    // ---------------------------------------------------------------- set and variant

    public string? SelectedSetId => _selectedSetId;

    public AssetSetSummary? SelectedSet =>
        _selectedSetId is null ? null : _assetCatalog?.SummarizeSet(_selectedSetId);

    public IReadOnlyList<AssetVariant> SelectedSetVariants => _selectedSetVariants;

    public int CurrentVariantIndex => _currentVariantIndex;

    public AssetVariant? CurrentVariant =>
        _currentVariantIndex >= 0 && _currentVariantIndex < _selectedSetVariants.Count
            ? _selectedSetVariants[_currentVariantIndex]
            : null;

    /// <summary>The tool's own position readout, for example "3 / 12".</summary>
    public string CurrentVariantLabel => _selectedSetVariants.Count == 0
        ? ""
        : $"{_currentVariantIndex + 1} / {_selectedSetVariants.Count}";

    public bool IsCurrentVariantTheCover =>
        SelectedSet is { } set && CurrentVariant is { } variant && set.CoverVariantId == variant.Id;

    public StampToolSettings StampToolSettings => _stampToolSettings;

    /// <summary>Reset future placement without editing any placed object or adding undo history.</summary>
    public void ResetStampToolSettings()
    {
        CancelEditableAreaWork();
        CancelAppearanceTransfer();
        CancelPlacement();
        CancelDrag();
        _selectedAreaId = _selectedPathId = null;
        _newAreaTemplate = null;
        _stampSettingsBeforeConstruction = null;
        _areaSettingsBeforeConstruction = null;
        _naturalAreaDensity = 70;
        _naturalAreaSizeVariation = 50;
        _naturalAreaPattern = default;
        _naturalAreaJoinEdges = true;
        _stampToolSettings = new StampToolSettings();
        BindStampToolToCurrentVariant();
        ActiveTool = EditorTool.Stamp;
        Session.ClearSelection();
        CanvasGestureCancelled?.Invoke(this, EventArgs.Empty);
        if (CurrentVariant is not null) ArmStampPlacement();
        RaiseStampToolState();
        RaiseAreaState();
        RaisePathState();
        Raise(nameof(SeriesSpacingPercent));
        PersistLocalPreferences();
        StatusDetail = "Базовые настройки восстановлены: одиночное размещение, масштаб 100%, поворот 0°, непрозрачность 100%.";
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public bool IsEditingFutureStamp =>
        ActiveTool == EditorTool.Stamp && (PendingPlacement?.Target == PlacementTargetKind.Stamp
            || SelectedEditablePath is not null || IsCreatingEditablePath
            || SelectedEditableArea is not null || IsCreatingEditableArea);

    public string StampPanelHeading =>
        ActiveTool == EditorTool.Stamp && SelectedEditableArea is not null ? "Параметры области — примените кнопкой ниже"
            : ActiveTool == EditorTool.Stamp && SelectedEditablePath is not null ? "Параметры пути — примените кнопкой ниже"
            : IsEditingFutureStamp ? "Ассет для размещения" : "Свойства ассета";

    public StampStyle? StampPanelStyle => IsEditingFutureStamp
        ? _stampToolSettings.Style
        : Session.SelectedObject?.Stamp;

    private AssetVariant? StampPanelVariant
    {
        get
        {
            if (IsEditingFutureStamp) return CurrentVariant;
            if (_assetCatalog is null || Session.SelectedObject?.Stamp is not { } stamp) return null;
            return _assetCatalog.Variants.FirstOrDefault(variant =>
                       string.Equals(variant.Sha256, stamp.AssetSha256, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(variant.MaskSha256, stamp.MaskAssetSha256,
                           StringComparison.OrdinalIgnoreCase))
                   ?? _assetCatalog.Variants.FirstOrDefault(variant =>
                       string.Equals(variant.Sha256, stamp.AssetSha256, StringComparison.OrdinalIgnoreCase));
        }
    }

    public string? StampPanelMaskSha256 =>
        StampPanelStyle?.MaskAssetSha256 ?? StampPanelVariant?.MaskSha256;

    public string? StampClipMaskThumbnailPath
    {
        get
        {
            if (_assetCatalog?.Find(StampPanelMaskSha256 ?? "") is not { } record) return null;
            return _assetCatalog.TryThumbnailPath(record, out var path) ? path : null;
        }
    }

    public string StampClipMaskStatus
    {
        get
        {
            if (StampPanelStyle is not { } style) return "Недоступно — ассет не выбран";
            var maskSha = StampPanelMaskSha256;
            if (maskSha is null) return "Недоступно — у варианта нет маски";
            if (!IsMaskAvailable(maskSha)) return "Маска отсутствует или повреждена";
            return style.ClipMaskEnabled ? "Включено" : "Выключено";
        }
    }

    public bool StampClipMaskCanToggle => StampPanelMaskSha256 is { } sha && IsMaskAvailable(sha);

    public bool StampPanelCanEditLibraryMask => StampPanelVariant is not null;

    public bool StampPanelVariantHasLibraryMask => StampPanelVariant?.MaskSha256 is not null;

    public string StampForegroundStatus => Session.Document.StampForeground switch
    {
        null => "Поверхность переднего плана не создана",
        { IsEmpty: true } => "Поверхность переднего плана пуста",
        _ => "Поверхность переднего плана готова",
    };

    /// <summary>True once the user has chosen an asset, so placement has something to place.</summary>
    public bool HasChosenAsset => _selectedSetId is not null;

    /// <summary>Makes one logical card current and restores the variant last used inside it.</summary>
    public void SelectAssetSet(string? setId) => SelectAssetSet(setId, armPlacement: true);

    /// <summary>
    /// A new image picked in the library or catalog is placed one at a time. A leftover «Область»
    /// or «Путь» with nothing drawn yet is dropped, so choosing trees never starts an area by itself.
    /// </summary>
    public void UseSinglePlacementForNewAsset()
    {
        if (StampPlacementMode is StampPlacementMode.FillArea or StampPlacementMode.Path
            && _areaDraft is null && _pathDraft is null)
            StampPlacementMode = StampPlacementMode.One;
    }

    private void SelectAssetSet(string? setId, bool armPlacement)
    {
        if (setId is null || _assetCatalog is null || _assetCatalog.FindSet(setId) is not { } set)
        {
            _selectedSetId = null;
            _selectedSetVariants = [];
            _currentVariantIndex = 0;
            _stampToolSettings = _stampToolSettings with
            {
                AssetSetId = null,
                VariantId = null,
                Style = null,
            };
            SyncSelectedLibraryRow();
            RaiseVariantState();
            RaiseStampToolState();
            return;
        }

        _selectedSetId = set.Id;
        _selectedSetVariants =
        [
            .. set.VariantIds.Select(_assetCatalog.FindVariant).Where(variant => variant is not null)
                .Select(variant => variant!),
        ];

        var remembered = _assetCatalog.CurrentVariantOf(set.Id);
        var index = remembered is null
            ? -1
            : _selectedSetVariants.ToList().FindIndex(variant => variant.Id == remembered);
        var coverIndex = _selectedSetVariants.ToList().FindIndex(variant => variant.Id == set.CoverVariantId);
        _currentVariantIndex = index >= 0 ? index : Math.Max(0, coverIndex);
        RememberCurrentVariant();
        BindStampToolToCurrentVariant();
        ApplyPlacementPassport(set);
        SyncSelectedLibraryRow();
        RaiseVariantState();
        RaiseStampToolState();
        PersistLocalPreferences();
        if (armPlacement) ArmStampPlacement();
    }

    // The passport values the tool last took from a card, and what the tool held before, so a
    // card without a passport gets the user's own values back when they were left untouched.
    private (string SetId, AssetPlacementDefaults Passport, double Rotation, double Size, bool Flip)? _passportApplied;
    private (double Rotation, double Size, bool Flip)? _beforePassport;

    /// <summary>
    /// Takes the starting values of the tool from the chosen card's passport, the way the
    /// reference editor seeds its rotation spread from "randomize angle by default". The user may
    /// change them afterwards; choosing the same card again does not undo that change.
    /// </summary>
    private void ApplyPlacementPassport(AssetSet set)
    {
        var passport = set.Placement;
        if (_passportApplied is { } same && same.SetId == set.Id && Equals(same.Passport, passport ?? new AssetPlacementDefaults()))
            return;
        var settings = _stampToolSettings;
        if (_passportApplied is { } applied && _beforePassport is { } before
            && settings.RotationJitterDegrees == applied.Rotation && settings.SizeJitterPercent == applied.Size
            && settings.RandomFlip == applied.Flip)
        {
            settings = settings with { RotationJitterDegrees = before.Rotation, SizeJitterPercent = before.Size, RandomFlip = before.Flip };
        }
        _passportApplied = null;
        _beforePassport = null;
        if (passport is not null)
        {
            _beforePassport = (settings.RotationJitterDegrees, settings.SizeJitterPercent, settings.RandomFlip);
            settings = settings with
            {
                RotationJitterDegrees = passport.RandomRotation ? 180 : 0,
                SizeJitterPercent = Math.Clamp(passport.RandomSizePercent, 0, 100),
                RandomFlip = passport.CanFlip && settings.RandomFlip,
            };
            if (settings.Style is { } style)
            {
                settings = settings with
                {
                    Style = style with
                    {
                        ShadowOpacity = passport.NoShadow ? 0 : style.ShadowOpacity,
                        FlipHorizontal = passport.CanFlip && style.FlipHorizontal,
                        FlipVertical = passport.CanFlip && style.FlipVertical,
                    },
                };
            }
            _passportApplied = (set.Id, passport, settings.RotationJitterDegrees, settings.SizeJitterPercent, settings.RandomFlip);
        }
        _stampToolSettings = settings;
        Raise(nameof(StampRotationJitterDegrees));
        Raise(nameof(StampSizeJitterPercent));
        Raise(nameof(StampRandomFlip));
        Raise(nameof(SelectedSetPlacement));
        Raise(nameof(SelectedSetRole));
        Raise(nameof(SelectedSetCanFlip));
    }

    /// <summary>The placement passport of the chosen card (built-in defaults when it has none).</summary>
    public AssetPlacementDefaults SelectedSetPlacement =>
        _selectedSetId is { } id && _assetCatalog?.FindSet(id)?.Placement is { } passport ? passport : new AssetPlacementDefaults();

    /// <summary>False when the chosen card's passport forbids mirroring it.</summary>
    public bool SelectedSetCanFlip => SelectedSetPlacement.CanFlip;

    public AssetRole SelectedSetRole => SelectedSetPlacement.Role ?? AssetRole.None;

    public bool SetSelectedSetRole(AssetRole role) =>
        SetSelectedSetPlacement(p => p with { Role = role });

    /// <summary>
    /// Edits the passport of the chosen card in the catalog; the tool takes the new values as its
    /// starting point at once. Returns false when nothing is chosen or the catalog refused.
    /// </summary>
    public bool SetSelectedSetPlacement(Func<AssetPlacementDefaults, AssetPlacementDefaults> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (_selectedSetId is not { } id || _assetCatalog is null)
        {
            StatusDetail = "Сначала выберите набор в библиотеке.";
            return false;
        }
        var updated = change(SelectedSetPlacement);
        if (updated == SelectedSetPlacement) return false;
        if (!MutateSet(() => _assetCatalog.SetPlacementDefaults(id, updated))) return false;
        StatusDetail = "Паспорт ассета сохранён: инструмент взял новые значения как стартовые.";
        return true;
    }

    public void NextVariant() => MoveVariant(1);

    public IReadOnlyList<AssetVariant> VariantsForSet(string setId) => _assetCatalog?.FindSet(setId) is { } set
        ? set.VariantIds.Select(_assetCatalog.FindVariant).OfType<AssetVariant>().ToArray() : [];

    public void PreviousVariant() => MoveVariant(-1);

    /// <summary>Validate a gallery action before changing the tool it will replace.</summary>
    public bool ChooseGalleryVariant(string setId, string? variantId, bool armPlacement = true)
    {
        if (_assetCatalog?.FindSet(setId) is not { } set || _assetResolver is null)
        { StatusDetail = "Набор больше не доступен в каталоге."; return false; }
        var candidates = variantId is not null ? new[] { variantId }
            : new[] { _assetCatalog.CurrentVariantOf(setId), set.CoverVariantId }
                .Concat(set.VariantIds).OfType<string>().Distinct().ToArray();
        foreach (var id in candidates)
        {
            if (!set.VariantIds.Contains(id) || _assetCatalog.FindVariant(id) is not { } variant
                || _assetCatalog.Find(variant.Sha256) is not { } record) continue;
            var embedded = Session.Document.Assets.Any(a => a.Sha256.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase));
            // A card the library already verified in the background is not read and hashed again
            // on the UI thread: the first pick of a 4096 px mountain spent ~300 ms there (R05,
            // 2026-10-23). Placing it still resolves and verifies the file before anything is added.
            if (!embedded && AvailabilityOf(record.Sha256) != AssetAvailability.Available
                && _assetResolver.Check(record) != AssetAvailability.Available) continue;
            SelectAssetSet(setId, armPlacement: false);
            SelectVariantAt(_selectedSetVariants.ToList().FindIndex(v => v.Id == id), armPlacement);
            return true;
        }
        StatusDetail = "Вариант недоступен: проверьте исходный файл или выберите другой.";
        return false;
    }

    public void SelectVariantAt(int index) => SelectVariantAt(index, armPlacement: true);

    private void SelectVariantAt(int index, bool armPlacement)
    {
        if (_selectedSetVariants.Count == 0)
        {
            return;
        }

        _newAreaTemplate = null; _selectedAreaId = null; CancelEditableAreaGesture();
        _currentVariantIndex = Math.Clamp(index, 0, _selectedSetVariants.Count - 1);
        _stampToolSettings = _stampToolSettings with { RandomizeVariant = false };
        RememberCurrentVariant();
        BindStampToolToCurrentVariant();
        RaiseVariantState();
        RaiseStampToolState();
        PersistLocalPreferences();
        if (armPlacement) ArmStampPlacement();
    }

    private void MoveVariant(int direction)
    {
        if (_selectedSetVariants.Count <= 1)
        {
            StatusDetail = _selectedSetVariants.Count == 0
                ? "Сначала выберите набор в библиотеке."
                : "В этом наборе один вариант.";
            return;
        }

        var count = _selectedSetVariants.Count;
        _currentVariantIndex = ((_currentVariantIndex + direction) % count + count) % count;
        RememberCurrentVariant();
        BindStampToolToCurrentVariant();
        StatusDetail = $"Вариант {CurrentVariantLabel}.";
        RaiseVariantState();
        RaiseStampToolState();
        PersistLocalPreferences();
        RefreshArmedPlacement();
    }

    private void RememberCurrentVariant()
    {
        if (_assetCatalog is null || _selectedSetId is null || CurrentVariant is not { } variant
            || _assetCatalog.Status == AssetLibraryStatus.Damaged)
        {
            return;
        }

        _assetCatalog.RememberVariant(_selectedSetId, variant.Id);
    }

    private void RestoreStampCatalogIdentity()
    {
        if (_assetCatalog is null || _stampToolSettings.AssetSetId is not { } setId
            || _assetCatalog.FindSet(setId) is not { } set)
        {
            return;
        }

        _selectedSetId = set.Id;
        _selectedSetVariants =
        [
            .. set.VariantIds.Select(_assetCatalog.FindVariant).Where(item => item is not null)
                .Select(item => item!),
        ];
        var wanted = _stampToolSettings.VariantId;
        var index = wanted is null ? -1 : _selectedSetVariants.ToList().FindIndex(item => item.Id == wanted);
        _currentVariantIndex = index >= 0 ? index : 0;
        BindStampToolToCurrentVariant();
        SyncSelectedLibraryRow();
        RaiseVariantState();
        RaiseStampToolState();
    }

    private void BindStampToolToCurrentVariant()
    {
        if (CurrentVariant is not { } variant || _assetCatalog?.Find(variant.Sha256) is not { } record)
        {
            return;
        }

        var referenceWidth = DefaultStampReferenceWidth();
        var referenceHeight = referenceWidth * record.Height / Math.Max(1.0, record.Width);
        var before = _stampToolSettings.Style;
        var resolvedMask = variant.MaskSha256;
        _stampToolSettings = _stampToolSettings with
        {
            AssetSetId = _selectedSetId,
            VariantId = variant.Id,
            Style = new StampStyle(variant.Sha256)
            {
                ReferenceWidth = referenceWidth,
                ReferenceHeight = referenceHeight,
                FlipHorizontal = before?.FlipHorizontal ?? false,
                FlipVertical = before?.FlipVertical ?? false,
                MaskAssetSha256 = resolvedMask,
                ClipMaskEnabled = resolvedMask is not null && (before?.ClipMaskEnabled ?? false),
                Blend = before?.Blend ?? BlendModes.Normal,
                Blur = before?.Blur ?? 0,
                ShadowOffsetX = before?.ShadowOffsetX ?? ShadowPreset.StandardOffsetX,
                ShadowOffsetY = before?.ShadowOffsetY ?? ShadowPreset.StandardOffsetY,
                ShadowBlur = before?.ShadowBlur ?? ShadowPreset.StandardBlur,
                ShadowOpacity = before?.ShadowOpacity ?? 0,
                ShadowPlacement = before?.ShadowPlacement ?? StampShadowPlacement.Object,
                ShadowColor = before?.ShadowColor ?? AppearanceColor.OpaqueBlack,
                Hue = before?.Hue ?? 0,
                Saturation = before?.Saturation ?? 0,
                Brightness = before?.Brightness ?? 0,
                Contrast = before?.Contrast ?? 0,
            },
        };
    }

    private void RefreshSelectedVariantsFromCatalog()
    {
        if (_assetCatalog is null || _selectedSetId is not { } setId
            || _assetCatalog.FindSet(setId) is not { } set)
        {
            return;
        }

        var currentId = CurrentVariant?.Id;
        _selectedSetVariants = [.. set.VariantIds.Select(_assetCatalog.FindVariant)
            .Where(variant => variant is not null).Select(variant => variant!)];
        var index = currentId is null ? -1 : _selectedSetVariants.ToList()
            .FindIndex(variant => variant.Id == currentId);
        _currentVariantIndex = index >= 0 ? index : Math.Clamp(_currentVariantIndex, 0,
            Math.Max(0, _selectedSetVariants.Count - 1));
    }

    public bool AssignCurrentVariantMask(string path)
    {
        if (_assetCatalog is null || StampPanelVariant is not { } variant)
        {
            StatusDetail = "Сначала выберите вариант ассета.";
            return false;
        }

        try
        {
            _assetCatalog.AssignVariantMask(variant.Id, path, _clock());
            RefreshSelectedVariantsFromCatalog();
            if (IsEditingFutureStamp && CurrentVariant?.Id == variant.Id)
            {
                BindStampToolToCurrentVariant();
                PersistLocalPreferences();
                RefreshArmedPlacement();
            }
            RaiseVariantState();
            RaiseStampToolState();
            StatusDetail = variant.MaskSha256 is null ? "Маска назначена." : "Маска заменена.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StatusDetail = "Не удалось назначить маску: " + ex.Message;
            _log.Error("Variant clip mask assignment failed", ex);
            return false;
        }
    }

    public bool RemoveCurrentVariantMask()
    {
        if (_assetCatalog is null || StampPanelVariant is not { } variant)
        {
            StatusDetail = "Сначала выберите вариант ассета.";
            return false;
        }

        try
        {
            _assetCatalog.RemoveVariantMask(variant.Id);
            RefreshSelectedVariantsFromCatalog();
            if (IsEditingFutureStamp && CurrentVariant?.Id == variant.Id)
            {
                BindStampToolToCurrentVariant();
                PersistLocalPreferences();
                RefreshArmedPlacement();
            }
            RaiseVariantState();
            RaiseStampToolState();
            StatusDetail = "Маска удалена из варианта. Уже размещённые объекты не изменены.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException)
        {
            StatusDetail = "Не удалось удалить маску: " + ex.Message;
            _log.Error("Variant clip mask removal failed", ex);
            return false;
        }
    }

    public void SetStampClipMaskEnabled(bool enabled)
    {
        if (StampPanelStyle is not { } style)
        {
            return;
        }

        if (IsEditingFutureStamp)
        {
            var maskSha = CurrentVariant?.MaskSha256;
            if (enabled && (maskSha is null || !IsMaskAvailable(maskSha)))
            {
                StatusDetail = maskSha is null
                    ? "Недоступно — у варианта нет маски."
                    : "Маска отсутствует или повреждена.";
                return;
            }
            UpdateStampToolSettings(settings => settings with
            {
                Style = style with
                {
                    MaskAssetSha256 = maskSha,
                    ClipMaskEnabled = enabled,
                },
            }, persist: true);
            return;
        }

        if (Session.SelectedObject is not { Stamp: { } selectedStyle } selected)
        {
            return;
        }
        if (!enabled)
        {
            EditStamp("Disable stamp clip mask", commit: true,
                stamp => stamp with { ClipMaskEnabled = false });
            return;
        }

        var resolvedMask = selectedStyle.MaskAssetSha256 ?? StampPanelVariant?.MaskSha256;
        if (resolvedMask is null)
        {
            StatusDetail = "Недоступно — у варианта нет маски.";
            return;
        }
        var maskReference = ResolveMaskForDocument(resolvedMask, out var refusal);
        if (maskReference is null)
        {
            StatusDetail = refusal ?? "Маска отсутствует или повреждена.";
            return;
        }

        Session.Execute(new UpdateStampStyleWithAssetCommand(
            selected,
            selectedStyle with { MaskAssetSha256 = resolvedMask, ClipMaskEnabled = true },
            maskReference));
        AfterEdit();
    }

    public void CreateStampForegroundSurface()
    {
        if (Session.Document.StampForeground is not null)
        {
            StatusDetail = StampForegroundStatus;
            return;
        }
        Session.Execute(new UpdateStampForegroundCommand(
            null, StampForegroundSurface.CreateEmpty(), "Create stamp foreground"));
        AfterEdit();
        RaiseStampToolState();
        StatusDetail = "Поверхность переднего плана создана. Выберите её редактирование и рисуйте кистью.";
    }

    public void EditStampForegroundSurface()
    {
        if (Session.Document.StampForeground is null)
        {
            StatusDetail = "Сначала создайте передний план ассета.";
            return;
        }
        ActiveTool = EditorTool.Brush;
        _editingStampForeground = true;
        Raise(nameof(IsEditingStampForeground));
        StatusDetail = "Рисуется передний план ассета: рисуйте обычной кистью на холсте.";
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    private bool IsMaskAvailable(string sha256)
    {
        if (Session.Document.Assets.Any(asset =>
                string.Equals(asset.Sha256, sha256, StringComparison.OrdinalIgnoreCase)))
        {
            return StampImages?.TryGetImage(sha256) is not null;
        }
        if (_assetCatalog?.Find(sha256) is not { } record) return false;
        try
        {
            return _assetCatalog.Resolve(record).Status == AssetResolutionStatus.Available;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private MapAssetReference? ResolveMaskForDocument(string sha256, out string? refusal)
    {
        var existing = Session.Document.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            refusal = StampImages?.TryGetImage(sha256) is null
                ? "Маска отсутствует или повреждена."
                : null;
            return refusal is null ? existing : null;
        }
        if (_assetCatalog?.Find(sha256) is not { } record)
        {
            refusal = "Маска отсутствует в Library.";
            return null;
        }
        var result = ResolveAssetForDocument(record, out _, out refusal);
        return result;
    }

    /// <summary>Favorite belongs to the logical card, not to one variant's bytes.</summary>
    public void ToggleSelectedSetFavorite()
    {
        if (_assetCatalog is null || SelectedSet is not { } set)
        {
            StatusDetail = "Сначала выберите набор в библиотеке.";
            return;
        }

        try
        {
            _assetCatalog.SetSetFavorite(set.SetId, !set.Favorite);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            StatusDetail = "Не удалось изменить избранное: " + ex.Message;
            return;
        }

        RefreshLibrary();
        Raise(nameof(SelectedSet));
    }

    /// <summary>Points the library at the set that owns a placed stamp, found back by hash.</summary>
    public void SyncLibraryToSelection(string? sha256)
    {
        if (_assetCatalog is null || string.IsNullOrWhiteSpace(sha256))
        {
            return;
        }

        if (_assetCatalog.FindSetForContent(sha256) is not { } set)
        {
            // The map is self-sufficient: a stamp whose set is gone still renders and exports.
            StatusDetail = "Этот ассет не найден в локальном каталоге; объект работает как раньше.";
            return;
        }

        SelectAssetSet(set.Id);
        var index = _selectedSetVariants.ToList().FindIndex(variant =>
            string.Equals(variant.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _currentVariantIndex = index;
            RaiseVariantState();
            RefreshArmedPlacement();
        }
    }

    // ---------------------------------------------------------------- area and density

    public StampPlacementMode StampPlacementMode
    {
        get => _stampToolSettings.Mode;
        set
        {
            if (_stampToolSettings.Mode != value && Enum.IsDefined(value))
            {
                var leavingPath = _selectedPathId is not null && value != StampPlacementMode.Path;
                var leavingArea = _selectedAreaId is not null && value != StampPlacementMode.FillArea;
                var hadDraft = _areaDraft is not null || _pathDraft is not null;
                CancelEditablePathGesture(); CancelEditableAreaGesture();
                CancelStampSeries();
                _stampToolSettings = _stampToolSettings with { Mode = value };
                if (value is not (StampPlacementMode.FillArea or StampPlacementMode.Path)) RestoreStampSettingsAfterConstruction();
                if (leavingPath) { _selectedPathId = null; ArmStampPlacement(); RaisePathState(); }
                if (leavingArea) { _selectedAreaId = null; _newAreaTemplate = null; ArmStampPlacement(); RaiseAreaState(); }
                if (hadDraft && PendingPlacement is null && _activeTool == EditorTool.Stamp) ArmStampPlacement();
                Raise();
                Raise(nameof(PlannedStampCount));
                Raise(nameof(StampAreaSummary));
                RefreshArmedPlacement();
                PersistLocalPreferences();
            }
        }
    }

    /// <summary>
    /// «После размещения: выделять / не выделять» of the asset tool. Not selecting is the default
    /// (policy Auto: assets are never selected, a single shape still is); selecting is Always.
    /// </summary>
    public bool SelectAfterPlacement
    {
        get => PlacementAutoSelect == PlacementAutoSelectPolicy.Always;
        set => PlacementAutoSelect = value ? PlacementAutoSelectPolicy.Always : PlacementAutoSelectPolicy.Auto;
    }

    public PlacementAutoSelectPolicy PlacementAutoSelect
    {
        get => _stampToolSettings.AutoSelect;
        set
        {
            if (_stampToolSettings.AutoSelect != value && Enum.IsDefined(value))
            {
                _stampToolSettings = _stampToolSettings with { AutoSelect = value };
                Raise();
                PersistLocalPreferences();
                if (_pendingPlacement is { } pending)
                {
                    PendingPlacement = pending with { AutoSelect = value };
                }
            }
        }
    }

    /// <summary>Gesture radius in world units.</summary>
    public double StampAreaRadius
    {
        get => _stampToolSettings.AreaRadius;
        set
        {
            var bounded = double.IsFinite(value)
                ? Math.Clamp(value, MinStampAreaRadius, MaxStampAreaRadius)
                : _stampToolSettings.AreaRadius;
            if (_stampToolSettings.AreaRadius != bounded)
            {
                _stampToolSettings = _stampToolSettings with { AreaRadius = bounded };
                Raise();
                Raise(nameof(PlannedStampCount));
                Raise(nameof(StampAreaInCells));
                Raise(nameof(StampAreaSummary));
                RefreshArmedPlacement();
            }
        }
    }

    public void SetStampAreaRadius(double value, bool persist)
    {
        StampAreaRadius = value;
        if (persist) PersistLocalPreferences();
    }

    /// <summary>Stamps per 10 000 square world units.</summary>
    public double StampDensity
    {
        get => _stampToolSettings.Density;
        set
        {
            var bounded = double.IsFinite(value)
                ? Math.Clamp(value, MinStampDensity, MaxStampDensity)
                : _stampToolSettings.Density;
            if (_stampToolSettings.Density != bounded)
            {
                _stampToolSettings = _stampToolSettings with { Density = bounded };
                Raise();
                Raise(nameof(PlannedStampCount));
                Raise(nameof(StampAreaSummary));
                RefreshArmedPlacement();
            }
        }
    }

    public void SetStampDensity(double value, bool persist)
    {
        StampDensity = value;
        if (persist) PersistLocalPreferences();
    }

    public bool RandomVariantAfterPlacement
    {
        get => _stampToolSettings.RandomizeVariant;
        set
        {
            if (_stampToolSettings.RandomizeVariant != value)
            {
                _stampToolSettings = _stampToolSettings with { RandomizeVariant = value };
                Raise();
                RefreshArmedPlacement();
                PersistLocalPreferences();
            }
        }
    }

    public StampRandomAngleStep StampRandomAngleStep
    {
        get => _stampToolSettings.RandomAngleStep;
        set
        {
            if (_stampToolSettings.RandomAngleStep != value && IsSupportedRandomAngleStep(value))
            {
                _stampToolSettings = _stampToolSettings with { RandomAngleStep = value };
                Raise();
                Raise(nameof(StampRandomAngle));
                RefreshArmedPlacement();
                PersistLocalPreferences();
            }
        }
    }

    public bool StampRandomAngle
    {
        get => StampRandomAngleStep != Map.Core.Editing.StampRandomAngleStep.Off;
        set => StampRandomAngleStep = value
            ? Map.Core.Editing.StampRandomAngleStep.Degrees15
            : Map.Core.Editing.StampRandomAngleStep.Off;
    }

    public bool StampRandomFlip
    {
        get => _stampToolSettings.RandomFlip;
        set
        {
            if (_stampToolSettings.RandomFlip != value)
            {
                _stampToolSettings = _stampToolSettings with { RandomFlip = value };
                Raise();
                RefreshArmedPlacement();
                PersistLocalPreferences();
            }
        }
    }

    /// <summary>«Без наложений» of the scatter mode; remembered with the other tool settings.</summary>
    public bool StampAvoidOverlap
    {
        get => _stampToolSettings.AvoidOverlap;
        set
        {
            if (_stampToolSettings.AvoidOverlap != value)
            {
                _stampToolSettings = _stampToolSettings with { AvoidOverlap = value };
                Raise();
                Raise(nameof(StampAreaSummary));
                RefreshArmedPlacement();
                PersistLocalPreferences();
            }
        }
    }

    /// <summary>Per-stamp variation of a series, path or scatter, as the panel's sliders hold it.</summary>
    public double StampRotationJitterDegrees
    {
        get => _stampToolSettings.RotationJitterDegrees;
        set => SetStampJitter(Math.Clamp(value, 0, 180), s => s.RotationJitterDegrees, (s, v) => s with { RotationJitterDegrees = v });
    }

    public double StampSizeJitterPercent
    {
        get => _stampToolSettings.SizeJitterPercent;
        set => SetStampJitter(Math.Clamp(value, 0, 100), s => s.SizeJitterPercent, (s, v) => s with { SizeJitterPercent = v });
    }

    public double StampOpacityJitterPercent
    {
        get => _stampToolSettings.OpacityJitterPercent;
        set => SetStampJitter(Math.Clamp(value, 0, 100), s => s.OpacityJitterPercent, (s, v) => s with { OpacityJitterPercent = v });
    }

    public double StampHueJitterDegrees
    {
        get => _stampToolSettings.HueJitterDegrees;
        set => SetStampJitter(Math.Clamp(value, 0, 180), s => s.HueJitterDegrees, (s, v) => s with { HueJitterDegrees = v });
    }

    public double StampSaturationJitterPercent
    {
        get => _stampToolSettings.SaturationJitterPercent;
        set => SetStampJitter(Math.Clamp(value, 0, 100), s => s.SaturationJitterPercent, (s, v) => s with { SaturationJitterPercent = v });
    }

    public double StampBrightnessJitterPercent
    {
        get => _stampToolSettings.BrightnessJitterPercent;
        set => SetStampJitter(Math.Clamp(value, 0, 100), s => s.BrightnessJitterPercent, (s, v) => s with { BrightnessJitterPercent = v });
    }

    private void SetStampJitter(double value, Func<StampToolSettings, double> read,
        Func<StampToolSettings, double, StampToolSettings> write)
    {
        if (!double.IsFinite(value) || Math.Abs(read(_stampToolSettings) - value) < 0.0001) return;
        _stampToolSettings = write(_stampToolSettings, value);
        Raise(nameof(StampRotationJitterDegrees)); Raise(nameof(StampSizeJitterPercent));
        Raise(nameof(StampOpacityJitterPercent)); Raise(nameof(StampHueJitterDegrees));
        Raise(nameof(StampSaturationJitterPercent)); Raise(nameof(StampBrightnessJitterPercent));
        Raise(nameof(StampToolChanges));
        RefreshArmedPlacement();
        PersistLocalPreferences();
    }

    /// <summary>How many stamps the current Area and Density would place.</summary>
    public int PlannedStampCount => StampPlacementMode is StampPlacementMode.One or StampPlacementMode.Series
        ? 1
        : StampPlacement.CountFor(StampDensity, StampAreaRadius);

    /// <summary>The same radius in cells of the active snap grid, or null without one.</summary>
    public double? StampAreaInCells =>
        ActiveSnapGrid() is { } grid ? StampPlacement.RadiusInCells(StampAreaRadius, grid.CellSize) : null;

    public string StampAreaSummary
    {
        get
        {
            var area = StampAreaInCells is { } cells
                ? $"Область {StampAreaRadius:0} ед. ({cells:0.##} кл.)"
                : $"Область {StampAreaRadius:0} ед.";
            return StampPlacementMode switch
            {
                StampPlacementMode.One => area + " · один ассет",
                StampPlacementMode.Series => area + " · серия по одному ассету",
                StampPlacementMode.FillArea => UsesNaturalAreaLayout ? $"Область · плотность {EditableAreaDensity:0}%"
                    : $"Область · плотность {EditableAreaDensity:0} на 10 000 ед²",
                _ => StampAvoidOverlap
                    ? $"{area} · плотность {StampDensity:0} на 10 000 ед² · до {PlannedStampCount} шт., без наложений"
                    : $"{area} · плотность {StampDensity:0} на 10 000 ед² · {PlannedStampCount} шт.",
            };
        }
    }

    public void AdjustStampArea(int direction, bool largerStep = false)
    {
        if (direction == 0)
        {
            return;
        }

        StampAreaRadius += Math.Sign(direction) * (largerStep ? 10 : 1);
        StatusDetail = StampAreaSummary;
    }

    public void AdjustStampDensity(int direction, bool largerStep = false)
    {
        if (direction == 0)
        {
            return;
        }

        if (ActiveTool == EditorTool.Stamp && StampPlacementMode == StampPlacementMode.FillArea)
        {
            SetEditableAreaDensity(Math.Clamp(EditableAreaDensity + Math.Sign(direction) * (largerStep ? 10 : 1),
                0, UsesNaturalAreaLayout ? 100 : int.MaxValue - 1));
            return;
        }
        StampDensity += Math.Sign(direction) * (largerStep ? 10 : 1);
        StatusDetail = StampAreaSummary;
    }
}
