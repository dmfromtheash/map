using Map.Assets;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    private double _libraryTileSize = 112;
    private bool _libraryShowNames = true;
    private AssetSourceFilter _librarySource;
    private readonly HashSet<string> _libraryTags = new(StringComparer.OrdinalIgnoreCase);

    public double LibraryTileSize
    {
        get => _libraryTileSize;
        set
        {
            if (double.IsFinite(value) && SetField(ref _libraryTileSize, Math.Clamp(value, 64, 176)))
                PersistLocalPreferences();
        }
    }

    public bool LibraryShowNames
    {
        get => _libraryShowNames;
        set { if (SetField(ref _libraryShowNames, value)) PersistLocalPreferences(); }
    }

    public AssetSourceFilter LibrarySource
    {
        get => _librarySource;
        set
        {
            if (!SetField(ref _librarySource, value)) return;
            _libraryCollectionId = null;
            Raise(nameof(LibraryCollectionId));
            RefreshLibrary();
        }
    }

    public int LibrarySourceCount(AssetSourceFilter source) => _assetCatalog?.CountSets(source) ?? 0;
    public IReadOnlyCollection<string> LibraryTags => _libraryTags;

    public void SetLibraryTagSelected(string tag, bool selected)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        var changed = selected ? _libraryTags.Add(tag) : _libraryTags.Remove(tag);
        if (!changed) return;
        _libraryTag = _libraryTags.FirstOrDefault();
        Raise(nameof(LibraryTags)); Raise(nameof(LibraryTag));
        RefreshLibrary();
    }
}
