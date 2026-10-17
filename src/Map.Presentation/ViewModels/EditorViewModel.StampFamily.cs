using Map.Assets;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

public sealed record StampFamilyVariant(string SetId, string SetName, AssetVariant Variant);

public sealed partial class EditorViewModel
{
    private string? _stampFamilySelectedSet;
    private long _stampFamilyRevision = -1;
    private string _stampFamilyName = "";
    private IReadOnlyList<StampFamilyVariant> _stampFamilyVariants = [];

    private void RememberPlacedFamily()
    {
        if (_assetCatalog is null || _selectedSetId is null || CurrentVariant is not { } variant) return;
        _assetCatalog.TouchRecent(_selectedSetId,variant.Id,_clock());
        Raise(nameof(CompactRecentSets)); Raise(nameof(RecentCount));
    }

    // A selected saved group owns its frozen images. Resolve its current catalogue card by
    // content when its original local IDs belong to another library; never show the previous tool's family.
    private void SyncPlacementFamily(string? setId, string? variantId, IReadOnlyList<StampVariantSource> sources)
    {
        var set = setId is null ? null : _assetCatalog?.FindSet(setId);
        if (set is null && _assetCatalog is not null)
        {
            var hashes = sources.Select(v => v.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
            set = _assetCatalog.Sets.FirstOrDefault(s => s.VariantIds.Select(_assetCatalog.RecordForVariant)
                .OfType<AssetRecord>().Select(r => r.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(hashes));
        }
        SelectAssetSet(set?.Id, armPlacement: false);
        if (variantId is not null && _selectedSetVariants.ToList().FindIndex(v => v.Id == variantId) is var index && index >= 0)
        {
            _currentVariantIndex = index;
            BindStampToolToCurrentVariant(); RaiseVariantState();
        }
    }

    public IReadOnlyList<StampFamilyVariant> StampFamilyVariants
    {
        get { RefreshStampFamily(); return _stampFamilyVariants; }
    }

    public string StampFamilyName
    {
        get { RefreshStampFamily(); return _stampFamilyName; }
    }

    public int CurrentStampFamilyIndex => StampFamilyVariants.ToList().FindIndex(
        choice => choice.SetId == _selectedSetId && choice.Variant.Id == CurrentVariant?.Id);

    public string CurrentStampFamilyLabel => StampFamilyVariants.Count == 0
        ? "" : $"{CurrentStampFamilyIndex + 1} / {StampFamilyVariants.Count}";

    private void RefreshStampFamily()
    {
        var revision = _assetCatalog?.ContentRevision ?? -1;
        if (_stampFamilySelectedSet == _selectedSetId && _stampFamilyRevision == revision) return;
        _stampFamilySelectedSet = _selectedSetId;
        _stampFamilyRevision = revision;
        _stampFamilyName = "";
        if (_selectedSetId is null || _assetCatalog?.FindSet(_selectedSetId) is not { } selected)
        { _stampFamilyVariants = []; return; }

        IReadOnlyList<AssetSet> members = [selected];
        _stampFamilyName = selected.Name;
        // A real multi-variant set keeps its own ordered members. Packs imported one image
        // per card use their immediate collection as the palette, without changing the index
        // or merging persistent sets. Parent/sibling collections are never pulled into it.
        if (selected.VariantIds.Count == 1 && selected.CollectionId is { } collectionId)
        {
            var siblings = _assetCatalog.Sets.Where(s => s.CollectionId == collectionId).ToArray();
            if (siblings.Length > 1 && siblings.All(s => s.VariantIds.Count == 1))
            {
                members = siblings.OrderBy(s => s.ImportedUtc).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
                _stampFamilyName = _assetCatalog.Collections.FirstOrDefault(c => c.Id == collectionId)?.Name ?? selected.Name;
            }
        }
        var choices = members.SelectMany(set => set.VariantIds.Select(_assetCatalog.FindVariant)
            .OfType<AssetVariant>().Select(variant => new StampFamilyVariant(set.Id, set.Name, variant))).ToArray();
        // Keep the virtualized strip and its scroll position while selecting another member.
        if (!choices.SequenceEqual(_stampFamilyVariants)) _stampFamilyVariants = choices;
    }

    public bool SelectStampFamilyVariantAt(int index)
    {
        var choices = StampFamilyVariants;
        if (index < 0 || index >= choices.Count) return false;
        var choice = choices[index];
        return ChooseGalleryVariant(choice.SetId, choice.Variant.Id);
    }

    /// <summary>
    /// Alt+wheel of the asset tool: the next (or previous) variant of the current set or family;
    /// past its last variant it moves on to the next set of the library as the panel lists it
    /// (search and filters included), taking that set's first variant - or its last one when
    /// going back. A library of one set just cycles its own variants. Returns false when no set is chosen.
    /// </summary>
    public bool StepAssetSet(int direction)
    {
        if (direction == 0 || _selectedSetId is null) return false;
        direction = Math.Sign(direction);
        var family = StampFamilyVariants;
        var index = CurrentStampFamilyIndex;
        if (family.Count > 1 && index >= 0 && index + direction >= 0 && index + direction < family.Count)
        {
            var inside = family[index + direction];
            var moved = ChooseGalleryVariant(inside.SetId, inside.Variant.Id, armPlacement: false);
            if (moved) { RefreshArmedPlacement(); AnnounceAssetStep(); }
            return moved;
        }

        var familySets = family.Select(choice => choice.SetId).Append(_selectedSetId).ToHashSet(StringComparer.Ordinal);
        var sets = LibrarySets;
        var position = sets.ToList().FindIndex(set => set.SetId == _selectedSetId);
        for (var step = 1; step <= sets.Count; step++)
        {
            var candidate = sets[(((position < 0 ? (direction > 0 ? -1 : 0) : position) + (direction * step)) % sets.Count + sets.Count) % sets.Count];
            if (familySets.Contains(candidate.SetId)) continue;
            var variants = VariantsForSet(candidate.SetId);
            if (variants.Count == 0) continue;
            var target = direction > 0 ? variants[0] : variants[^1];
            if (!ChooseGalleryVariant(candidate.SetId, target.Id, armPlacement: false)) continue;
            // Going back lands on the last member of the previous family, so the wheel reverses exactly.
            if (direction < 0 && StampFamilyVariants is { Count: > 1 } previous && previous[^1] is var last
                && (last.SetId != _selectedSetId || last.Variant.Id != CurrentVariant?.Id))
                ChooseGalleryVariant(last.SetId, last.Variant.Id, armPlacement: false);
            RefreshArmedPlacement();
            AnnounceAssetStep();
            return true;
        }

        // Nothing else in the library: keep cycling the current family as before.
        if (family.Count > 1) { MoveStampFamilyVariant(direction); AnnounceAssetStep(); return true; }
        return false;
    }

    private void AnnounceAssetStep() =>
        StatusDetail = StampFamilyVariants.Count > 1
            ? $"Набор «{StampFamilyName}» · вариант {CurrentStampFamilyLabel}. Alt+колесо — дальше."
            : $"Набор «{SelectedSet?.Name}». Alt+колесо — следующий набор.";

    public void NextStampFamilyVariant() => MoveStampFamilyVariant(1);
    public void PreviousStampFamilyVariant() => MoveStampFamilyVariant(-1);

    private void MoveStampFamilyVariant(int direction)
    {
        var choices = StampFamilyVariants;
        if (choices.Count <= 1) { MoveVariant(direction); return; }
        if (_selectedSetVariants.Count > 1) { MoveVariant(direction); return; }
        var choice = choices[(CurrentStampFamilyIndex + direction + choices.Count) % choices.Count];
        if (ChooseGalleryVariant(choice.SetId, choice.Variant.Id, armPlacement: false))
            RefreshArmedPlacement();
    }
}
