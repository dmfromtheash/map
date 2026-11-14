namespace Map.Assets;

/// <summary>Catalog records and their file roots from one immutable catalog publication.</summary>
public sealed class AssetCatalogReadSnapshot
{
    private readonly Func<AssetRecord, string> _rootFor;
    private readonly Func<IReadOnlyList<AssetRecord>> _records;
    internal AssetCatalogReadSnapshot(string root, Func<IReadOnlyList<AssetRecord>> records, Func<AssetRecord, string> rootFor)
    {
        Root = root;
        _records = records;
        _rootFor = rootFor;
    }

    public string Root { get; }
    public IReadOnlyList<AssetRecord> Records => _records();
    public string RootFor(AssetRecord record) => _rootFor(record);
}
