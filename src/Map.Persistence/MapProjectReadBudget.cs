namespace Map.Persistence;

/// <summary>
/// Metadata-only admission budget for a .mapproj. It runs before the first payload is inflated,
/// so an archive that cannot fit the supported interactive memory envelope fails predictably.
/// </summary>
public sealed class MapProjectReadBudget(bool legacyScenes = false, bool migrationEnvelope = true)
{
    public long TotalUncompressedBytes { get; private set; }
    public long AssetPayloadBytes { get; private set; }
    public long TilePayloadBytes { get; private set; }
    public long JsonPayloadBytes { get; private set; }
    public long CheckpointPayloadBytes { get; private set; }
    public long StructuralJsonPayloadBytes { get; private set; }
    public long ObjectChunkPayloadBytes { get; private set; }

    public void Add(string name, long uncompressedBytes, long compressedBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (uncompressedBytes < 0 || compressedBytes < 0)
        {
            throw new MapProjectCorruptedException($"Entry {name} declares a negative size.");
        }

        var isCheckpoint = name.StartsWith(MapProjectFormat.CheckpointPrefix, StringComparison.Ordinal);
        var isCheckpointTile = isCheckpoint
            && (name.Contains("/rasters/", StringComparison.Ordinal)
                || name.Contains("/masks/", StringComparison.Ordinal));
        var isCheckpointAsset = isCheckpoint && name.Contains("/assets/", StringComparison.Ordinal);
        var isCheckpointPreview = isCheckpoint && name.EndsWith("/preview.png", StringComparison.Ordinal);
        var isObjectChunk = name.StartsWith(MapProjectFormat.ObjectPrefix, StringComparison.Ordinal)
            || isCheckpoint && name.Contains("/" + MapProjectFormat.ObjectPrefix, StringComparison.Ordinal);
        var isTile = name.StartsWith(MapProjectFormat.RasterPrefix, StringComparison.Ordinal)
            || name.StartsWith(MapProjectFormat.MaskPrefix, StringComparison.Ordinal)
            || isCheckpointTile;
        var isAsset = name.StartsWith(MapProjectFormat.AssetPrefix, StringComparison.Ordinal)
            || isCheckpointAsset;
        var perEntryLimit = isCheckpointPreview
            ? MapProjectFormat.MaxCheckpointPreviewBytes
            : isObjectChunk
                ? MapProjectFormat.ObjectChunkTargetBytes
            : name == MapProjectFormat.PreviewEntry
            ? MapProjectFormat.MaxPreviewBytes
            : isTile
                ? MapProjectFormat.MaxTileEntryBytes
                : isAsset
                    ? MapProjectFormat.MaxAssetEntryBytes
                    : legacyScenes && (name == "scene.json" || isCheckpoint && name.EndsWith("/scene.json", StringComparison.Ordinal))
                        ? MapProjectFormat.MaxLegacySceneEntryBytes
                        : MapProjectFormat.MaxJsonEntryBytes;

        if (uncompressedBytes > perEntryLimit)
        {
            throw new MapProjectResourceLimitException(
                $"Entry {name} declares {uncompressedBytes} uncompressed bytes, above this " +
                $"build's {perEntryLimit}-byte safety limit. The file was not modified.");
        }

        var compressionAllowance = compressedBytes * MapProjectFormat.MaxCompressionRatio
            + MapProjectFormat.CompressionRatioSlackBytes;
        if (uncompressedBytes > compressionAllowance)
        {
            throw new MapProjectResourceLimitException(
                $"Entry {name} declares a compression ratio outside this build's safety limit. " +
                "The file was not modified.");
        }

        try
        {
            TotalUncompressedBytes = checked(TotalUncompressedBytes + uncompressedBytes);
            if (isCheckpoint)
            {
                CheckpointPayloadBytes = checked(CheckpointPayloadBytes + uncompressedBytes);
            }
            if (isAsset)
            {
                AssetPayloadBytes = checked(AssetPayloadBytes + uncompressedBytes);
            }
            else if (isTile)
            {
                TilePayloadBytes = checked(TilePayloadBytes + uncompressedBytes);
            }
            else if (name.EndsWith(".json", StringComparison.Ordinal))
            {
                JsonPayloadBytes = checked(JsonPayloadBytes + uncompressedBytes);
                if (isObjectChunk) ObjectChunkPayloadBytes = checked(ObjectChunkPayloadBytes + uncompressedBytes);
                else if (!legacyScenes || !(name == "scene.json" || isCheckpoint && name.EndsWith("/scene.json", StringComparison.Ordinal)))
                    StructuralJsonPayloadBytes = checked(StructuralJsonPayloadBytes + uncompressedBytes);
            }
        }
        catch (OverflowException ex)
        {
            throw new MapProjectCorruptedException("Container entry sizes overflow the read budget.", ex);
        }

        if (StructuralJsonPayloadBytes > MapProjectFormat.MaxStructuralJsonPayloadBytes
            || ObjectChunkPayloadBytes > MapProjectFormat.MaxObjectChunkPayloadBytes)
            throw new MapProjectResourceLimitException("Превышен отдельный бюджет структуры или объектных чанков. Файл не изменён.");

        if (AssetPayloadBytes > MapProjectFormat.MaxAssetPayloadBytes)
        {
            throw new MapProjectResourceLimitException(
                $"The container's asset payload exceeds the {MapProjectFormat.MaxAssetPayloadBytes}-byte budget.");
        }

        if (TilePayloadBytes > MapProjectFormat.MaxTilePayloadBytes)
        {
            throw new MapProjectResourceLimitException(
                $"The container's tile payload exceeds the {MapProjectFormat.MaxTilePayloadBytes}-byte budget.");
        }

        if (JsonPayloadBytes > MapProjectFormat.MaxJsonPayloadBytes)
        {
            throw new MapProjectResourceLimitException(
                $"The container's JSON payload exceeds the {MapProjectFormat.MaxJsonPayloadBytes}-byte budget.");
        }

        if (TotalUncompressedBytes > MapProjectFormat.MaxTotalUncompressedBytes)
        {
            throw new MapProjectResourceLimitException(
                $"The container declares {TotalUncompressedBytes} uncompressed bytes in total, " +
                $"above the {MapProjectFormat.MaxTotalUncompressedBytes} limit.");
        }

        if (legacyScenes && migrationEnvelope && TotalUncompressedBytes > MapProjectFormat.MaxMigrationPayloadBytes)
            throw new MapProjectResourceLimitException("Превышен совокупный бюджет обновления старого формата. Исходный файл не изменён.");

        if (CheckpointPayloadBytes > MapProjectFormat.MaxCheckpointPayloadBytes)
        {
            throw new MapProjectResourceLimitException(
                $"The container's checkpoints exceed the {MapProjectFormat.MaxCheckpointPayloadBytes}-byte budget.");
        }
    }
}
