using Map.Assets;
using Map.Core.Documents;
using Map.Persistence;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    // Desktop preparation runs on a worker. Non-dispatcher clients can prepare during Arm
    // synchronously; either way the canvas commit only consumes immutable ready references.
    public bool AllowsBackgroundAssetPreparation { get; set; }
    private AssetRecord[] _preparingRecords = [];
    private Task<IReadOnlyDictionary<string, AssetContentResolution>>? _preparedAssets;
    private CancellationTokenSource? _assetPreparationCancellation;

    public Task PreparePlacementAssetsAsync(bool refresh = false)
    {
        if (_assetResolver is null || !TryStampInputs(_stampToolSettings, out _, out _, out var records, out _))
            return Task.CompletedTask;
        var needed = records.Values.ToArray();
        if (!refresh && _preparedAssets is not null && needed.Length == _preparingRecords.Length
            && needed.All(record => _preparingRecords.Any(previous => ReferenceEquals(previous, record))))
            return _preparedAssets;

        var previousTask = _preparedAssets;
        var previousRecords = refresh ? [] : _preparingRecords;
        _assetPreparationCancellation?.Cancel();
        _assetPreparationCancellation?.Dispose();
        _assetPreparationCancellation = new();
        var cancellation = _assetPreparationCancellation.Token;
        var resolver = _assetResolver;
        var existing = Session.Document.Assets.ToDictionary(asset => asset.Sha256, StringComparer.OrdinalIgnoreCase);
        var missing = needed.Where(record => !existing.ContainsKey(record.Sha256)).ToArray();
        long advertisedBytes;
        try { advertisedBytes = missing.Aggregate(0L, (sum, record) => checked(sum + record.ByteLength)); }
        catch (OverflowException) { advertisedBytes = long.MaxValue; }
        if (!MapProjectStore.CanAdmitEmbeddedBytes(Session.Document, advertisedBytes, missing.Length, out _))
        {
            _preparingRecords = needed;
            _preparedAssets = Task.FromResult<IReadOnlyDictionary<string, AssetContentResolution>>(
                new Dictionary<string, AssetContentResolution>(StringComparer.OrdinalIgnoreCase));
            return _preparedAssets;
        }
        _preparingRecords = needed;
        _preparedAssets = Task.Run(async () =>
        {
            // Serialize preparation when the user changes cards quickly. Old answers cannot
            // replace the active task, and unchanged records reuse their immutable payload.
            var previous = previousTask is null ? null : await previousTask.ConfigureAwait(false);
            var result = new Dictionary<string, AssetContentResolution>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in needed)
            {
                if (cancellation.IsCancellationRequested) break;
                if (existing.TryGetValue(record.Sha256, out var owned))
                    result[record.Sha256] = new(AssetAvailability.Available, owned);
                else if (previous is not null && previousRecords.Any(was => ReferenceEquals(was, record))
                    && previous.TryGetValue(record.Sha256, out var ready) && ready.IsAvailable)
                    result[record.Sha256] = ready;
                else
                {
                    try { result[record.Sha256] = resolver.Resolve(record); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                    { result[record.Sha256] = AssetContentResolution.Failed(AssetAvailability.Missing); }
                }
            }
            return (IReadOnlyDictionary<string, AssetContentResolution>)result;
        });
        return _preparedAssets;
    }

    private void PrepareArmedAssets()
    {
        var preparation = PreparePlacementAssetsAsync();
        if (!AllowsBackgroundAssetPreparation) preparation.GetAwaiter().GetResult();
    }

    private AssetContentResolution PreparedAsset(AssetRecord record)
    {
        if (_preparedAssets is { IsCompletedSuccessfully: true } ready
            && _preparingRecords.Any(prepared => ReferenceEquals(prepared, record))
            && ready.Result.TryGetValue(record.Sha256, out var resolution)) return resolution;
        return AssetContentResolution.Failed(AssetAvailability.Checking);
    }
}
