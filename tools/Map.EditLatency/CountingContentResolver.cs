using Map.Assets;
using Map.Presentation.ViewModels;

internal sealed class CountingContentResolver(IAssetContentResolver inner) : IAssetContentResolver
{
    private int _reads;
    public int Reads => Volatile.Read(ref _reads);
    public AssetContentResolution Resolve(AssetRecord record)
    { Interlocked.Increment(ref _reads); return inner.Resolve(record); }
    public AssetAvailability Check(AssetRecord record) => inner.Check(record);
}
