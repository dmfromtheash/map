using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Map.Core.Documents;
using Map.Persistence.Container;
using Map.Persistence.Recovery;

namespace Map.Persistence;

/// <summary>
/// Reads and writes .mapproj. Save follows the docs/05 contract: serialize the immutable
/// snapshot to a unique sibling temp, read the temp back and validate entries and checksums,
/// flush, and only then replace the target atomically. A failure before the replace leaves the
/// last correct version untouched and is reported as an exception, never as success.
/// </summary>
public static partial class MapProjectStore
{
    // Large sparse scenes must spend the fixed JSON budget on data, not presentation whitespace.
    // The container is machine-owned and the reader accepts either representation.
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static int SchemaFor(MapDocument document) =>
        document.Layers.OfType<BaseColorLayer>().Any(l => l.TextureId is not null)
        || document.Checkpoints.Any(c => c.Snapshot.Layers.OfType<BaseColorLayer>().Any(l => l.TextureId is not null))
            ? MapProjectFormat.BaseMaterialSchemaVersion :
        document.Layers.OfType<LandLayer>().Any(l => l.PaintTiles.TileCount > 0)
        || document.Checkpoints.Any(c => c.Snapshot.Layers.OfType<LandLayer>().Any(l => l.PaintTiles.TileCount > 0))
            ? MapProjectFormat.LandPaintSchemaVersion :
        document.Layers.OfType<ObjectLayer>().Any(l => !l.Roads.IsEmpty)
        || document.Checkpoints.Any(c => c.Snapshot.Layers.OfType<ObjectLayer>().Any(l => !l.Roads.IsEmpty))
            ? MapProjectFormat.RoadsSchemaVersion :
        document.Layers.OfType<ObjectLayer>().Any(l => !l.Rivers.IsEmpty)
        || document.Checkpoints.Any(c => c.Snapshot.Layers.OfType<ObjectLayer>().Any(l => !l.Rivers.IsEmpty))
            ? MapProjectFormat.RiversSchemaVersion :
        document.Layers.OfType<ObjectLayer>().Any(l => !l.PlacementAreas.IsEmpty)
        || document.Checkpoints.Any(c => c.Snapshot.Layers.OfType<ObjectLayer>().Any(l => !l.PlacementAreas.IsEmpty))
            ? MapProjectFormat.EditableAreasSchemaVersion :
        document.Layers.OfType<ObjectLayer>().Any(l => !l.PlacementPaths.IsEmpty)
        || document.Checkpoints.Any(c => c.Snapshot.Layers.OfType<ObjectLayer>().Any(l => !l.PlacementPaths.IsEmpty))
            ? MapProjectFormat.EditablePathsSchemaVersion : MapProjectFormat.SchemaVersion;

    /// <summary>
    /// Validates one proposed checkpoint against the exact bytes the persistence writer would
    /// add. The document is not mutated and no file is touched on refusal.
    /// </summary>
    public static void ValidateCheckpointAddition(MapDocument document, MapCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(checkpoint);

        if (document.Checkpoints.Count >= MapCheckpointLimits.MaxCount)
        {
            throw new MapProjectResourceLimitException(
                $"Достигнут лимит: не больше {MapCheckpointLimits.MaxCount} контрольных точек.");
        }

        if (document.Checkpoints.Any(item => item.Id == checkpoint.Id))
        {
            throw new ArgumentException("Контрольная точка с таким идентификатором уже существует.",
                nameof(checkpoint));
        }

        var candidate = document.WithCheckpoints([.. document.Checkpoints, checkpoint]);
        var state = new ContainerWriteState();
        _ = WriteCheckpoints(archive: null, candidate, state);
    }

    /// <summary>
    /// Checks whether new document assets fit the same payload and ZIP-entry envelope enforced by
    /// the reader. The editor calls this before committing an undoable command, so it cannot create
    /// a state the next save is guaranteed to reject.
    /// </summary>
    public static bool CanAdmitAdditionalAssets(
        MapDocument document,
        IEnumerable<MapAssetReference> candidates,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(candidates);

        var projected = document.Assets.ToDictionary(
            asset => asset.Sha256, StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            projected.TryAdd(candidate.Sha256, candidate);
        }

        return TryValidateAssetEnvelope(
            document,
            projected.Values,
            MapProjectFormat.MaxJsonPayloadBytes + MapProjectFormat.MaxPreviewBytes,
            out reason);
    }

    /// <summary>
    /// Cheap admission before reading/cloning catalog payloads. Exact thumbnail bytes and entry
    /// count are checked again by <see cref="CanAdmitAdditionalAssets"/> after resolution.
    /// </summary>
    public static bool CanAdmitEmbeddedBytes(
        MapDocument document,
        long additionalBytes,
        int additionalAssets,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegative(additionalBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(additionalAssets);

        try
        {
            var existingBytes = document.Assets.Aggregate(
                0L,
                static (total, asset) => checked(
                    total
                    + (asset.EmbeddedContent?.LongLength ?? 0)
                    + (asset.FallbackThumbnailPng?.LongLength ?? 0)));
            var projectedAssetBytes = checked(existingBytes + additionalBytes);
            if (projectedAssetBytes > MapProjectFormat.MaxAssetPayloadBytes)
            {
                reason = $"Ассеты превысят лимит проекта {MapProjectFormat.MaxAssetPayloadBytes} байт.";
                return false;
            }

            var tileBytes = EnumerateTileEntries(document).Aggregate(
                0L,
                static (total, entry) => checked(total + entry.Bytes.LongLength));
            if (checked(
                    tileBytes
                    + projectedAssetBytes
                    + MapProjectFormat.MaxJsonPayloadBytes
                    + MapProjectFormat.MaxPreviewBytes)
                > MapProjectFormat.MaxTotalUncompressedBytes)
            {
                reason = $"Проект превысит общий лимит {MapProjectFormat.MaxTotalUncompressedBytes} байт.";
                return false;
            }

            if (checked(document.Assets.Count + additionalAssets) > MapProjectJsonBudget.MaxAssets)
            {
                reason = $"В проекте будет больше {MapProjectJsonBudget.MaxAssets} ассетов.";
                return false;
            }

            var entries = checked(
                BaseContainerEntryCount(document)
                + AssetEntryCount(document.Assets)
                + additionalAssets);
            if (entries > MapProjectFormat.MaxEntries)
            {
                reason = $"Проект превысит лимит {MapProjectFormat.MaxEntries} файлов контейнера.";
                return false;
            }

            reason = "";
            return true;
        }
        catch (OverflowException)
        {
            reason = "Размер или число ассетов переполняет допустимый бюджет проекта.";
            return false;
        }
    }

    public static void Save(
        MapDocument document,
        string targetPath,
        SaveMetadata metadata,
        byte[] previewPng,
        RecoveryJournal? journal = null,
        SaveFaultInjection? faults = null,
        SaveTimings? timings = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(previewPng);

        if (previewPng.Length == 0)
        {
            throw new ArgumentException(
                "A preview image is required; persistence stores the caller's bytes verbatim.",
                nameof(previewPng));
        }

        if (previewPng.LongLength > MapProjectFormat.MaxPreviewBytes)
        {
            throw new MapProjectResourceLimitException(
                $"Preview exceeds the {MapProjectFormat.MaxPreviewBytes}-byte project budget.");
        }

        // Reject known payload/count violations before opening a temp or serializing objects.
        // Exact JSON and checkpoint totals are still admitted by ContainerWriteState.
        if (!TryValidateAssetEnvelope(document, document.Assets, previewPng.LongLength, out var reason))
            throw new MapProjectResourceLimitException(reason);

        var fullTarget = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullTarget)
            ?? throw new ArgumentException("Target path has no directory.", nameof(targetPath));

        // Sibling temp so the final replace stays on one volume - that is what makes it atomic.
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(fullTarget)}.tmp-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(directory);

            // Fingerprint of the previous version is best effort: a locked target must not stop
            // the user's work from reaching a durable temp (Stage 1 lesson).
            RecoveryRecord? record = null;
            if (journal is not null)
            {
                var fingerprintStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                var baseHash = File.Exists(fullTarget) ? TryFingerprint(fullTarget) : "";
                if (timings is not null) timings.PreviousFingerprintMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(fingerprintStarted).TotalMilliseconds;
                record = journal.Begin(
                    document.Id.Value, fullTarget, tempPath, metadata.SavedRevision, baseHash);
            }

            using (var stream = new FileStream(
                       tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.None))
            {
                var writeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                WriteContainer(stream, document, metadata, previewPng);
                if (timings is not null) timings.WriteContainerMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(writeStarted).TotalMilliseconds;
                var flushStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                stream.Flush(flushToDisk: true);
                if (timings is not null) timings.FlushMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(flushStarted).TotalMilliseconds;
            }

            // A save that cannot be read back by our own reader is a failed save, not a success.
            var readBackStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            ValidateReadBack(tempPath, document);
            if (timings is not null) timings.ReadBackMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(readBackStarted).TotalMilliseconds;

            // The temp is now durable and proven: from this point a crash leaves a recovery
            // candidate the next launch can find.
            if (journal is not null && record is not null)
            {
                journal.Advance(document.Id.Value, record, RecoveryState.TempDurable);
            }

            faults?.AfterDurableTempBeforeReplace?.Invoke();

            var publishStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            if (File.Exists(fullTarget))
            {
                // Win32 ReplaceFile: on return the target is either wholly old or wholly new.
                // The previous correct version survives as one bounded sibling .bak (docs/05).
                File.Replace(tempPath, fullTarget, fullTarget + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, fullTarget);
            }
            if (timings is not null) timings.PublishMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(publishStarted).TotalMilliseconds;

            if (journal is not null && record is not null)
            {
                journal.Advance(document.Id.Value, record, RecoveryState.Replaced);
                journal.Clear(document.Id.Value);
            }
        }
        catch (MapProjectResourceLimitException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not MapProjectSaveException)
        {
            // The temp and the journal are deliberately left in place: after a durable write the
            // temp may hold the only copy of the user's work, and the journal is what tells the
            // next launch whether that temp is a verified candidate or a half-written fragment.
            throw new MapProjectSaveException(
                $"Save of {fullTarget} did not complete. The last correct version was not replaced.",
                fullTarget,
                File.Exists(tempPath) ? tempPath : null,
                ex);
        }
    }

    private static string TryFingerprint(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static bool TryValidateAssetEnvelope(
        MapDocument document,
        IEnumerable<MapAssetReference> assets,
        long reservedOtherPayloadBytes,
        out string reason)
    {
        try
        {
            var assetList = assets.ToList();
            if (assetList.Count > MapProjectJsonBudget.MaxAssets)
            {
                reason = $"В проекте больше {MapProjectJsonBudget.MaxAssets} ассетов.";
                return false;
            }

            long payloadBytes = 0;
            foreach (var asset in assetList)
            {
                foreach (var payload in new[] { asset.EmbeddedContent, asset.FallbackThumbnailPng })
                {
                    if (payload is null)
                    {
                        continue;
                    }

                    if (payload.LongLength > MapProjectFormat.MaxAssetEntryBytes)
                    {
                        reason = $"Ассет {asset.Sha256} превышает лимит одного файла проекта.";
                        return false;
                    }

                    payloadBytes = checked(payloadBytes + payload.LongLength);
                }
            }

            if (payloadBytes > MapProjectFormat.MaxAssetPayloadBytes)
            {
                reason = $"Ассеты превышают лимит проекта {MapProjectFormat.MaxAssetPayloadBytes} байт.";
                return false;
            }

            var tileBytes = EnumerateTileEntries(document).Aggregate(
                0L,
                static (total, entry) => checked(total + entry.Bytes.LongLength));
            if (tileBytes > MapProjectFormat.MaxTilePayloadBytes)
            {
                reason = $"Тайлы превышают лимит проекта {MapProjectFormat.MaxTilePayloadBytes} байт.";
                return false;
            }

            if (checked(payloadBytes + tileBytes + reservedOtherPayloadBytes)
                > MapProjectFormat.MaxTotalUncompressedBytes)
            {
                reason = $"Проект превысит общий лимит {MapProjectFormat.MaxTotalUncompressedBytes} байт.";
                return false;
            }

            var entries = checked(BaseContainerEntryCount(document) + AssetEntryCount(assetList));
            if (entries > MapProjectFormat.MaxEntries)
            {
                reason = $"Проект содержит больше {MapProjectFormat.MaxEntries} файлов контейнера.";
                return false;
            }

            reason = "";
            return true;
        }
        catch (OverflowException)
        {
            reason = "Размер или число ассетов переполняет допустимый бюджет проекта.";
            return false;
        }
    }

    private static long BaseContainerEntryCount(MapDocument document) => checked(
        MapProjectFormat.RequiredEntries.Count
        + EnumerateTileEntries(document).LongCount()
        + document.Layers.OfType<ObjectLayer>().Sum(layer =>
            (long)Math.Ceiling(layer.Objects.Count / (double)MapProjectFormat.ObjectChunkTargetObjects)));

    private static long AssetEntryCount(IEnumerable<MapAssetReference> assets) => assets.Sum(
        static asset => (asset.EmbeddedContent is null ? 0L : 1L)
            + (asset.FallbackThumbnailPng is null ? 0L : 1L));

    public static OpenedProject Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var (manifest, scene, objectStorages, assetManifest, previewPng, tiles, assets, checkpoints) =
            ReadAndValidate(Path.GetFullPath(path));

        return new OpenedProject
        {
            Document = ToDomain(
                manifest, scene, objectStorages, assetManifest, tiles, assets, checkpoints),
            AppVersion = manifest.AppVersion,
            ModifiedUtc = ParseTimestamp(manifest.ModifiedUtc, "modifiedUtc"),
            PreviewPng = previewPng,
        };
    }

    // ---------------------------------------------------------------- write path

    private static void WriteContainer(
        Stream destination,
        MapDocument document,
        SaveMetadata metadata,
        byte[] previewPng)
    {
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        var state = new ContainerWriteState();

        // Object entries are written first. Their bounded descriptors make scene.json and the
        // manifest possible without ever materializing a whole-scene DTO graph or UTF-8 buffer.
        var objectChunks = WriteObjectChunks(archive, document, entryRoot: "", state);
        var sceneBytes = SerializeJson(
            ToSceneDto(document, objectChunks), MapProjectFormat.SceneEntry);
        var assetManifestBytes = SerializeJson(
            ToAssetManifestDto(document), MapProjectFormat.AssetManifestEntry);
        var checkpointCatalogSha256 = WriteCheckpoints(archive, document, state);

        var manifest = new ManifestDto
        {
            SchemaVersion = SchemaFor(document),
            AppVersion = metadata.AppVersion,
            DocumentId = document.Id.ToString(),
            Title = document.Title,
            CreatedUtc = FormatTimestamp(document.CreatedUtc),
            ModifiedUtc = FormatTimestamp(metadata.ModifiedUtc),
            Canvas = new CanvasDto
            {
                Width = document.Canvas.Width,
                Height = document.Canvas.Height,
                Unit = document.Canvas.Unit,
                Background = document.Canvas.Background,
            },
            SceneSha256 = Sha256Hex(sceneBytes),
            AssetManifestSha256 = Sha256Hex(assetManifestBytes),
            CheckpointCatalogSha256 = checkpointCatalogSha256,
        };
        var manifestBytes = SerializeJson(manifest, MapProjectFormat.ManifestEntry);

        WriteBytesEntry(archive, MapProjectFormat.ManifestEntry, manifestBytes, state, json: true);
        WriteBytesEntry(archive, MapProjectFormat.SceneEntry, sceneBytes, state, json: true);
        WriteBytesEntry(archive, MapProjectFormat.AssetManifestEntry, assetManifestBytes, state, json: true);
        WriteBytesEntry(archive, MapProjectFormat.PreviewEntry, previewPng, state);
        foreach (var (name, bytes) in EnumerateTileEntries(document).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            WriteBytesEntry(archive, name, bytes, state);
        }
        foreach (var (name, bytes) in EnumerateAssetEntries(document).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            WriteBytesEntry(archive, name, bytes, state);
        }
    }

    private static string? WriteCheckpoints(
        ZipArchive? archive,
        MapDocument document,
        ContainerWriteState state)
    {
        if (document.Checkpoints.Count == 0)
        {
            return null;
        }
        if (document.Checkpoints.Count > MapCheckpointLimits.MaxCount)
        {
            throw new MapProjectResourceLimitException(
                $"Достигнут лимит: не больше {MapCheckpointLimits.MaxCount} контрольных точек.");
        }

        var catalog = new CheckpointCatalogDto();
        foreach (var checkpoint in document.Checkpoints)
        {
            var root = CheckpointRoot(checkpoint.Id);
            var snapshot = checkpoint.Snapshot.Materialize(document.Id);
            var objectChunks = WriteObjectChunks(archive, snapshot, root, state);
            var sceneEntry = root + "scene.json";
            var assetManifestEntry = root + "asset-manifest.json";
            var snapshotManifestEntry = root + "manifest.json";
            var previewEntry = root + "preview.png";
            var sceneBytes = SerializeJson(ToSceneDto(snapshot, objectChunks), sceneEntry);
            var assetManifestBytes = SerializeJson(ToAssetManifestDto(snapshot, root), assetManifestEntry);
            var previewBytes = checkpoint.PreviewPng.ToArray();

            if (previewBytes.LongLength > MapProjectFormat.MaxCheckpointPreviewBytes)
            {
                throw new MapProjectResourceLimitException(
                    $"Preview контрольной точки «{checkpoint.Name}» превышает "
                    + $"{MapProjectFormat.MaxCheckpointPreviewBytes} байт.");
            }
            ValidatePngHeader(
                previewBytes,
                previewEntry,
                MapProjectFormat.MaxCheckpointPreviewWidth,
                MapProjectFormat.MaxCheckpointPreviewHeight,
                (long)MapProjectFormat.MaxCheckpointPreviewWidth * MapProjectFormat.MaxCheckpointPreviewHeight);

            var snapshotManifest = new CheckpointSnapshotManifestDto
            {
                SchemaVersion = SchemaFor(snapshot),
                DocumentId = document.Id.ToString(),
                Title = snapshot.Title,
                CreatedUtc = FormatTimestamp(snapshot.CreatedUtc),
                Canvas = new CanvasDto
                {
                    Width = snapshot.Canvas.Width,
                    Height = snapshot.Canvas.Height,
                    Unit = snapshot.Canvas.Unit,
                    Background = snapshot.Canvas.Background,
                },
                SceneSha256 = Sha256Hex(sceneBytes),
                AssetManifestSha256 = Sha256Hex(assetManifestBytes),
                PreviewSha256 = Sha256Hex(previewBytes),
            };
            var snapshotManifestBytes = SerializeJson(snapshotManifest, snapshotManifestEntry);

            WriteBytesEntry(archive, snapshotManifestEntry, snapshotManifestBytes, state, json: true);
            WriteBytesEntry(archive, sceneEntry, sceneBytes, state, json: true);
            WriteBytesEntry(archive, assetManifestEntry, assetManifestBytes, state, json: true);
            WriteBytesEntry(archive, previewEntry, previewBytes, state);
            foreach (var (name, bytes) in EnumerateTileEntries(snapshot, root).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                WriteBytesEntry(archive, name, bytes, state);
            }
            foreach (var (name, bytes) in EnumerateAssetEntries(snapshot, root).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                WriteBytesEntry(archive, name, bytes, state);
            }

            catalog.Checkpoints.Add(new CheckpointEntryDto
            {
                Id = checkpoint.Id.ToString(),
                Name = checkpoint.Name,
                CreatedUtc = FormatTimestamp(checkpoint.CreatedUtc),
                SnapshotManifestSha256 = Sha256Hex(snapshotManifestBytes),
            });
        }

        var catalogBytes = SerializeJson(catalog, MapProjectFormat.CheckpointCatalogEntry);
        WriteBytesEntry(archive, MapProjectFormat.CheckpointCatalogEntry, catalogBytes, state, json: true);
        if (state.CheckpointBytes > MapCheckpointLimits.MaxTotalStoredBytes)
        {
            throw new MapProjectResourceLimitException(
                $"Контрольные точки занимают {state.CheckpointBytes} байт — больше лимита "
                + $"{MapCheckpointLimits.MaxTotalStoredBytes} байт. Старые точки не удалены.");
        }
        return Sha256Hex(catalogBytes);
    }

    private static void ValidateJsonPayload(string name, byte[] bytes)
    {
        if (bytes.LongLength > MapProjectFormat.MaxJsonEntryBytes)
        {
            throw new MapProjectResourceLimitException(
                $"{name} is {bytes.LongLength} bytes and exceeds the "
                + $"{MapProjectFormat.MaxJsonEntryBytes}-byte JSON budget.");
        }

        MapProjectJsonBudget.Validate(bytes, name);
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] data)
    {
        // Encoded PNG/JPEG/WebP saved less than 1% under DEFLATE in the fixed mixed scene,
        // while costing 90 ms versus 2 ms. Raw raster tiles and JSON still benefit from Optimal.
        var encodedImage = name == MapProjectFormat.PreviewEntry
            || name.StartsWith(MapProjectFormat.AssetPrefix, StringComparison.Ordinal)
            || name.Contains("/assets/", StringComparison.Ordinal)
            || name.EndsWith("/preview.png", StringComparison.Ordinal);
        var entry = archive.CreateEntry(name, encodedImage ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
        entry.LastWriteTime = MapProjectFormat.DeterministicEntryTime;
        using var stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }

    /// <summary>
    /// Runs the temp through the full read path and confirms it holds the snapshot that was asked
    /// for. Object payloads are compared in canonical persisted form so a count-only read-back can
    /// never hide reordering, an id change, or a semantic conversion defect.
    /// </summary>
    private static void ValidateReadBack(string tempPath, MapDocument expected)
    {
        var (manifest, scene, objectStorages, assetManifest, _, tiles, assets, checkpoints) =
            ReadAndValidate(tempPath);
        var reloaded = ToDomain(
            manifest, scene, objectStorages, assetManifest, tiles, assets, checkpoints);

        if (reloaded.Id != expected.Id)
        {
            throw new MapProjectCorruptedException("Read-back document id does not match the saved snapshot.");
        }

        if (reloaded.Layers.Count != expected.Layers.Count
            || reloaded.Layers.Zip(expected.Layers).Any(p =>
                p.First.Id != p.Second.Id
                || p.First.GetType() != p.Second.GetType()
                || ObjectCount(p.First) != ObjectCount(p.Second)
                || !ObjectSequencesMatch(p.First, p.Second)
                || TileCount(p.First) != TileCount(p.Second)
                || (p.First.Mask?.Tiles.TileCount ?? 0) != (p.Second.Mask?.Tiles.TileCount ?? 0))
            || reloaded.Assets.Count != expected.Assets.Count
            || (reloaded.StampForeground?.Tiles.TileCount ?? -1)
                != (expected.StampForeground?.Tiles.TileCount ?? -1)
            || reloaded.Checkpoints.Count != expected.Checkpoints.Count
            || reloaded.Checkpoints.Zip(expected.Checkpoints).Any(pair =>
                pair.First.Id != pair.Second.Id
                || pair.First.Name != pair.Second.Name
                || pair.First.CreatedUtc != pair.Second.CreatedUtc
                || (pair.First.Snapshot.StampForeground?.Tiles.TileCount ?? -1)
                    != (pair.Second.Snapshot.StampForeground?.Tiles.TileCount ?? -1)
                || !ObjectSequencesMatch(
                    pair.First.Snapshot.Layers, pair.Second.Snapshot.Layers)
                || !pair.First.PreviewPng.Span.SequenceEqual(pair.Second.PreviewPng.Span)))
        {
            throw new MapProjectCorruptedException("Read-back layer/object shape does not match the saved snapshot.");
        }
    }

    // ---------------------------------------------------------------- read path

    private static (
        ManifestDto Manifest,
        SceneDto Scene,
        IReadOnlyDictionary<string, SceneObjectList> ObjectStorages,
        AssetManifestDto AssetManifest,
        byte[] PreviewPng,
        IReadOnlyDictionary<string, byte[]> Tiles,
        IReadOnlyDictionary<string, byte[]> Assets,
        IReadOnlyList<MapCheckpoint> Checkpoints) ReadAndValidate(
            string fullPath,
            int expectedSchemaVersion = MapProjectFormat.SchemaVersion,
            bool allowUnknownObjectKinds = false)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        MapProjectZipAdmission.Validate(stream);

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new MapProjectCorruptedException(
                $"{Path.GetFileName(fullPath)} is not a readable ZIP container.", ex);
        }

        using (archive)
        {
            ValidateContainerShape(archive, legacyScenes: expectedSchemaVersion < 4);

            foreach (var required in MapProjectFormat.RequiredEntries)
            {
                if (archive.GetEntry(required) is null)
                {
                    throw new MapProjectCorruptedException($"Required entry {required} is missing.");
                }
            }

            ValidatePreviewHeader(archive);

            var manifestBytes = ReadEntry(archive, MapProjectFormat.ManifestEntry);
            var manifest = ParseJson<ManifestDto>(manifestBytes, MapProjectFormat.ManifestEntry);

            if (manifest.Format != MapProjectFormat.FormatId)
            {
                throw new MapProjectCorruptedException(
                    $"The container declares format \"{manifest.Format}\"; this is not a map project.");
            }

            if (manifest.SchemaVersion != expectedSchemaVersion
                && !(expectedSchemaVersion == MapProjectFormat.SchemaVersion
                    && MapProjectFormat.IsExtendedV4Schema(manifest.SchemaVersion)))
            {
                throw new UnsupportedSchemaVersionException(manifest.SchemaVersion);
            }

            var sceneBytes = ReadEntry(archive, MapProjectFormat.SceneEntry);
            if (!string.Equals(Sha256Hex(sceneBytes), manifest.SceneSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new MapProjectCorruptedException(
                    $"{MapProjectFormat.SceneEntry} does not match the checksum recorded in the manifest.");
            }

            var assetManifestBytes = ReadEntry(archive, MapProjectFormat.AssetManifestEntry);
            if (!string.Equals(
                    Sha256Hex(assetManifestBytes), manifest.AssetManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new MapProjectCorruptedException(
                    $"{MapProjectFormat.AssetManifestEntry} does not match the checksum recorded in the manifest.");
            }

            var scene = ParseJson<SceneDto>(sceneBytes, MapProjectFormat.SceneEntry);
            ValidatePathSchema(scene, manifest.SchemaVersion);
            if (expectedSchemaVersion is >= 2 and <= 3)
            {
                ValidateV2SceneShape(sceneBytes, MapProjectFormat.SceneEntry);
            }
            else if (expectedSchemaVersion == MapProjectFormat.SchemaVersion)
            {
                ValidateV4SceneShape(sceneBytes, MapProjectFormat.SceneEntry);
            }
            if (!string.Equals(scene.DocumentId, manifest.DocumentId, StringComparison.OrdinalIgnoreCase))
            {
                throw new MapProjectCorruptedException(
                    "manifest.json and scene.json disagree about the document id.");
            }

            var assetManifest = ParseJson<AssetManifestDto>(
                assetManifestBytes, MapProjectFormat.AssetManifestEntry);
            if (expectedSchemaVersion >= 2)
            {
                ValidateRequiredArray(
                    assetManifestBytes, MapProjectFormat.AssetManifestEntry, "assets");
            }

            var objectStorages = expectedSchemaVersion == MapProjectFormat.SchemaVersion
                ? ReadAndValidateObjectChunks(archive, scene, entryRoot: "", allowUnknownObjectKinds)
                : new Dictionary<string, SceneObjectList>(StringComparer.Ordinal);
            var tiles = ReadAndValidateTiles(archive, scene);
            var assets = ReadAndValidateAssets(archive, assetManifest);
            var checkpoints = ReadAndValidateCheckpoints(
                archive,
                manifest,
                allowLegacyLayerTypes: expectedSchemaVersion == MapProjectFormat.LegacySchemaVersion,
                allowUnknownObjectKinds,
                expectedSchemaVersion);
            return (
                manifest,
                scene,
                objectStorages,
                assetManifest,
                ReadEntry(archive, MapProjectFormat.PreviewEntry),
                tiles,
                assets,
                checkpoints);
        }
    }

    /// <summary>
    /// Structural sanity of the archive before any content is read. This is resilience against a
    /// truncated, mis-copied or mislabeled file - the reader must fail with a clear error instead
    /// of hanging on decompression or writing outside the container (docs/05 read limits).
    /// </summary>
    private static void ValidateContainerShape(ZipArchive archive, bool legacyScenes = false, bool migrationEnvelope = true)
    {
        if (archive.Entries.Count > MapProjectFormat.MaxEntries)
        {
            throw new MapProjectResourceLimitException(
                $"The container holds {archive.Entries.Count} entries; " +
                $"a map project has at most {MapProjectFormat.MaxEntries}.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var budget = new MapProjectReadBudget(legacyScenes, migrationEnvelope);

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;

            if (string.IsNullOrWhiteSpace(name)
                || name.Contains('\\')
                || name.StartsWith('/')
                || name.Contains(':')
                || name.Split('/').Any(segment => segment is "" or "." or ".."))
            {
                throw new MapProjectCorruptedException($"Entry path \"{name}\" is not canonical.");
            }

            if (!seen.Add(name))
            {
                throw new MapProjectCorruptedException($"Entry {name} occurs more than once.");
            }

            budget.Add(name, entry.Length, entry.CompressedLength);
        }
    }

    /// <summary>
    /// Checks the stored preview really is a PNG with sane dimensions - from the signature and
    /// IHDR header only, without decoding pixels (persistence has no imaging dependency).
    /// </summary>
    private static void ValidatePreviewHeader(ZipArchive archive) => ValidatePreviewHeader(
        archive,
        MapProjectFormat.PreviewEntry,
        MapProjectFormat.MaxPreviewDimension,
        MapProjectFormat.MaxPreviewDimension,
        MapProjectFormat.MaxPreviewPixels);

    private static void ValidatePreviewHeader(
        ZipArchive archive,
        string entryName,
        int maxWidth,
        int maxHeight,
        long maxPixels)
    {
        var preview = new byte[24];
        try
        {
            using var stream = archive.GetEntry(entryName)!.Open();
            stream.ReadExactly(preview);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new MapProjectCorruptedException(
                $"{entryName} is not a PNG or has a truncated header.", ex);
        }

        ValidatePngHeader(preview, entryName, maxWidth, maxHeight, maxPixels);
    }

    private static void ValidatePngHeader(
        ReadOnlySpan<byte> preview,
        string entryName,
        int maxWidth,
        int maxHeight,
        long maxPixels)
    {
        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        ReadOnlySpan<byte> ihdr = "IHDR"u8;
        if (preview.Length < 24
            || !preview.Slice(0, 8).SequenceEqual(pngSignature)
            || !preview.Slice(12, 4).SequenceEqual(ihdr))
        {
            throw new MapProjectCorruptedException(
                $"{entryName} is not a PNG image.");
        }

        // Bytes 16..23 are the IHDR width and height, big-endian.
        var width = BinaryPrimitives.ReadUInt32BigEndian(preview.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(preview.Slice(20, 4));

        var pixels = (long)width * height;
        if (width is 0 || width > maxWidth
            || height is 0 || height > maxHeight
            || pixels > maxPixels)
        {
            throw new MapProjectResourceLimitException(
                $"{entryName} declares {width}x{height}, outside this build's " +
                "supported preview size. The file was not modified.");
        }
    }

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        try
        {
            var entry = archive.GetEntry(name)!;
            if (entry.Length > int.MaxValue)
            {
                throw new MapProjectCorruptedException(
                    $"Entry {name} is too large for one in-memory payload.");
            }

            var bytes = GC.AllocateUninitializedArray<byte>((int)entry.Length);
            using var stream = entry.Open();
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw new MapProjectCorruptedException(
                    $"Entry {name} decompressed beyond its declared length.");
            }

            return bytes;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new MapProjectCorruptedException($"Entry {name} cannot be decompressed.", ex);
        }
    }

    private static T ParseJson<T>(byte[] bytes, string entryName)
    {
        try
        {
            MapProjectJsonBudget.Validate(bytes, entryName);
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new MapProjectCorruptedException($"{entryName} is empty.");
        }
        catch (JsonException ex)
        {
            throw new MapProjectCorruptedException($"{entryName} is not valid JSON.", ex);
        }
    }

    private static IReadOnlyDictionary<string, byte[]> ReadAndValidateTiles(
        ZipArchive archive, SceneDto scene, string entryRoot = "")
    {
        if (scene.Layers is null || scene.Layers.Any(HasInvalidLayerDto))
        {
            throw new MapProjectCorruptedException(
                "scene.json has null layers, a null layer, or a null element in a required collection.");
        }

        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var rasterPrefix = entryRoot + MapProjectFormat.RasterPrefix;
        var maskPrefix = entryRoot + MapProjectFormat.MaskPrefix;

        foreach (var layer in scene.Layers)
        {
            var layerId = RequireCanonicalLayerId(layer.Id);
            ReadTileList(
                layer.Tiles,
                rasterPrefix,
                layerId,
                bytesPerPixel: layer.Type == "Land" ? 1 : 4);
            ReadTileList(layer.Mask?.Tiles, maskPrefix, layerId, bytesPerPixel: 1);
            if (layer.LandPaintTiles is not null && layer.Type != "Land")
                throw new MapProjectCorruptedException("Only land can carry material paint tiles.");
            ReadTileList(layer.LandPaintTiles, entryRoot + MapProjectFormat.LandPaintPrefix, layerId, bytesPerPixel: 4);
        }
        if (scene.StampForeground is not null && scene.StampForeground.Tiles is null)
        {
            throw new MapProjectCorruptedException(
                "scene.json has a null tile collection for the stamp foreground surface.");
        }
        ReadTileList(scene.StampForeground?.Tiles, rasterPrefix, "foreground", bytesPerPixel: 4);

        foreach (var entry in archive.Entries)
        {
            if ((entry.FullName.StartsWith(rasterPrefix, StringComparison.Ordinal)
                    || entry.FullName.StartsWith(maskPrefix, StringComparison.Ordinal))
                && !referenced.Contains(entry.FullName))
            {
                throw new MapProjectCorruptedException(
                    $"Tile entry {entry.FullName} is not referenced by scene.json.");
            }
        }

        return result;

        void ReadTileList(
            List<TileDto>? tiles, string prefix, string layerId, int bytesPerPixel)
        {
            if (tiles is null)
            {
                return;
            }

            var keys = new HashSet<(int X, int Y)>();
            foreach (var tile in tiles)
            {
                if (tile is null)
                {
                    throw new MapProjectCorruptedException(
                        $"scene.json has a null tile in layer {layerId}.");
                }

                if (!keys.Add((tile.X, tile.Y)))
                {
                    throw new MapProjectTileCorruptedException(
                        prefix, layerId, tile.X, tile.Y, "is listed more than once");
                }

                var name = TileEntryName(prefix, layerId, tile.X, tile.Y);
                if (!referenced.Add(name))
                {
                    throw new MapProjectTileCorruptedException(
                        name, layerId, tile.X, tile.Y, "is referenced more than once");
                }

                var entry = archive.GetEntry(name) ?? throw new MapProjectTileCorruptedException(
                        name, layerId, tile.X, tile.Y, "is missing");
                var bytes = ReadEntry(archive, name);
                var expectedLength = TileMap.TileSize * TileMap.TileSize * bytesPerPixel;
                if (bytes.Length != expectedLength)
                {
                    throw new MapProjectTileCorruptedException(
                        name, layerId, tile.X, tile.Y,
                        $"has {bytes.Length} bytes instead of {expectedLength}");
                }

                if (!string.Equals(Sha256Hex(bytes), tile.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new MapProjectTileCorruptedException(
                        name, layerId, tile.X, tile.Y, "does not match its SHA-256 checksum");
                }

                result.Add(name, bytes);
            }
        }
    }

    private static IReadOnlyDictionary<string, byte[]> ReadAndValidateAssets(
        ZipArchive archive, AssetManifestDto manifest, string entryRoot = "")
    {
        if (manifest.Assets is null || manifest.Assets.Any(asset => asset is null))
        {
            throw new MapProjectCorruptedException("asset-manifest.json has a null assets array or entry.");
        }

        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assetPrefix = entryRoot + MapProjectFormat.AssetPrefix;

        foreach (var asset in manifest.Assets)
        {
            var hash = RequireSha256(asset.Sha256, "asset sha256");
            if (!ProjectAssetFormatPolicy.TryGetDeclaredFormat(
                    asset.MediaType, asset.Extension, out var declaredFormat))
            {
                throw new MapProjectCorruptedException(
                    $"Asset {hash} has inconsistent or unsupported media type and extension.");
            }

            if (!hashes.Add(hash))
            {
                throw new MapProjectCorruptedException($"Asset {hash} occurs more than once.");
            }

            if (asset.Storage == nameof(ProjectAssetStorage.Embedded))
            {
                var expected = entryRoot + AssetContentEntry(hash, asset.Extension);
                if (!string.Equals(asset.ContentEntry, expected, StringComparison.Ordinal))
                {
                    throw new MapProjectCorruptedException(
                        $"Embedded asset {hash} has a non-canonical content entry.");
                }

                ReadAssetEntry(expected, hash);
                if (!string.Equals(Sha256Hex(result[expected]), hash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new MapProjectCorruptedException(
                        $"Embedded asset {hash} does not match its content hash.");
                }

                if (!ProjectAssetFormatPolicy.HasExpectedHeader(result[expected], declaredFormat))
                {
                    throw new MapProjectCorruptedException(
                        $"Embedded asset {hash} payload does not match its declared image format.");
                }
            }
            else if (asset.Storage == nameof(ProjectAssetStorage.Linked))
            {
                if (asset.ContentEntry is not null)
                {
                    throw new MapProjectCorruptedException(
                        $"Linked asset {hash} must not declare an embedded content entry.");
                }
            }
            else
            {
                throw new MapProjectCorruptedException(
                    $"Asset {hash} has unsupported storage mode \"{asset.Storage}\".");
            }

            if ((asset.ThumbnailEntry is null) != (asset.ThumbnailSha256 is null))
            {
                throw new MapProjectCorruptedException(
                    $"Asset {hash} must declare its thumbnail entry and checksum together.");
            }

            if (asset.ThumbnailEntry is { } thumbnailEntry)
            {
                var expected = entryRoot + AssetThumbnailEntry(hash);
                if (!string.Equals(thumbnailEntry, expected, StringComparison.Ordinal))
                {
                    throw new MapProjectCorruptedException(
                        $"Asset {hash} has a non-canonical thumbnail entry.");
                }

                ReadAssetEntry(expected, hash);
                var thumbnailHash = RequireSha256(asset.ThumbnailSha256, "thumbnail sha256");
                if (!string.Equals(
                        Sha256Hex(result[expected]), thumbnailHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new MapProjectCorruptedException(
                        $"Asset {hash} fallback thumbnail does not match its checksum.");
                }

                if (!ProjectAssetFormatPolicy.HasExpectedHeader(
                        result[expected], ProjectAssetImageFormat.Png))
                {
                    throw new MapProjectCorruptedException(
                        $"Asset {hash} fallback thumbnail is not a PNG payload.");
                }
            }
        }

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.StartsWith(assetPrefix, StringComparison.Ordinal)
                && !referenced.Contains(entry.FullName))
            {
                throw new MapProjectCorruptedException(
                    $"Asset entry {entry.FullName} is not referenced by asset-manifest.json.");
            }
        }

        return result;

        void ReadAssetEntry(string name, string hash)
        {
            if (!referenced.Add(name))
            {
                throw new MapProjectCorruptedException($"Asset entry {name} is referenced more than once.");
            }

            if (archive.GetEntry(name) is null)
            {
                throw new MapProjectCorruptedException($"Asset {hash} entry {name} is missing.");
            }

            result[name] = ReadEntry(archive, name);
        }
    }

    private static IReadOnlyList<MapCheckpoint> ReadAndValidateCheckpoints(
        ZipArchive archive,
        ManifestDto mainManifest,
        bool allowLegacyLayerTypes,
        bool allowUnknownObjectKinds,
        int schemaVersion)
    {
        var checkpointArchiveEntries = archive.Entries
            .Where(entry => entry.FullName.StartsWith(
                MapProjectFormat.CheckpointPrefix, StringComparison.Ordinal))
            .ToList();

        if (mainManifest.CheckpointCatalogSha256 is null)
        {
            if (checkpointArchiveEntries.Count > 0)
            {
                throw new MapProjectCorruptedException(
                    "The container has checkpoint entries but manifest.json does not declare their catalog.");
            }

            return [];
        }

        var catalogEntry = archive.GetEntry(MapProjectFormat.CheckpointCatalogEntry)
            ?? throw new MapProjectCorruptedException(
                $"Declared entry {MapProjectFormat.CheckpointCatalogEntry} is missing.");
        var catalogBytes = ReadEntry(archive, catalogEntry.FullName);
        var expectedCatalogHash = RequireSha256(
            mainManifest.CheckpointCatalogSha256, "checkpoint catalog sha256");
        if (!string.Equals(Sha256Hex(catalogBytes), expectedCatalogHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new MapProjectCorruptedException(
                $"{MapProjectFormat.CheckpointCatalogEntry} does not match its manifest checksum.");
        }

        var catalog = ParseJson<CheckpointCatalogDto>(
            catalogBytes, MapProjectFormat.CheckpointCatalogEntry);
        if (!allowLegacyLayerTypes)
        {
            ValidateRequiredArray(
                catalogBytes, MapProjectFormat.CheckpointCatalogEntry, "checkpoints");
        }
        if (catalog.Checkpoints is null || catalog.Checkpoints.Any(item => item is null))
        {
            throw new MapProjectCorruptedException(
                $"{MapProjectFormat.CheckpointCatalogEntry} has a null checkpoints array or entry.");
        }
        if (catalog.Checkpoints.Count > MapCheckpointLimits.MaxCount)
        {
            throw new MapProjectResourceLimitException(
                $"The container has more than {MapCheckpointLimits.MaxCount} checkpoints. "
                + "The file was not modified.");
        }

        var checkpoints = new List<MapCheckpoint>(catalog.Checkpoints.Count);
        var seenIds = new HashSet<CheckpointId>();
        var expectedMetadataEntries = new HashSet<string>(StringComparer.Ordinal)
        {
            MapProjectFormat.CheckpointCatalogEntry,
        };
        var roots = new List<string>(catalog.Checkpoints.Count);

        foreach (var item in catalog.Checkpoints)
        {
            try
            {
                var idText = RequireString(item.Id, "checkpoint id");
                var id = CheckpointId.Parse(idText);
                if (!string.Equals(idText, id.ToString(), StringComparison.Ordinal)
                    || !seenIds.Add(id))
                {
                    throw new MapProjectCorruptedException(
                        $"Checkpoint id {idText} is non-canonical or occurs more than once.");
                }

                var name = RequireString(item.Name, "checkpoint name");
                if (!string.Equals(name, name.Trim(), StringComparison.Ordinal)
                    || name.Length > MapCheckpointLimits.MaxNameLength)
                {
                    throw new MapProjectCorruptedException(
                        $"Checkpoint {id} has an invalid name.");
                }

                var root = CheckpointRoot(id);
                roots.Add(root);
                var snapshotManifestEntry = root + "manifest.json";
                var sceneEntry = root + "scene.json";
                var assetManifestEntry = root + "asset-manifest.json";
                var previewEntry = root + "preview.png";
                foreach (var entryName in new[]
                {
                    snapshotManifestEntry,
                    sceneEntry,
                    assetManifestEntry,
                    previewEntry,
                })
                {
                    if (!expectedMetadataEntries.Add(entryName))
                    {
                        throw new MapProjectCorruptedException(
                            $"Checkpoint entry {entryName} is declared more than once.");
                    }
                    if (archive.GetEntry(entryName) is null)
                    {
                        throw new MapProjectCorruptedException(
                            $"Checkpoint {id} entry {entryName} is missing.");
                    }
                }

                var snapshotManifestBytes = ReadEntry(archive, snapshotManifestEntry);
                var expectedSnapshotHash = RequireSha256(
                    item.SnapshotManifestSha256, "checkpoint snapshot manifest sha256");
                if (!string.Equals(Sha256Hex(snapshotManifestBytes), expectedSnapshotHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new MapProjectCorruptedException(
                        $"Checkpoint {id} snapshot manifest does not match its checksum.");
                }

                var snapshotManifest = ParseJson<CheckpointSnapshotManifestDto>(
                    snapshotManifestBytes, snapshotManifestEntry);
                if (snapshotManifest.SchemaVersion >= MapProjectFormat.EditablePathsSchemaVersion
                    && mainManifest.SchemaVersion < snapshotManifest.SchemaVersion)
                    throw new MapProjectCorruptedException("A construction checkpoint requires a matching container schema.");
                if (schemaVersion == MapProjectFormat.SchemaVersion
                    && snapshotManifest.SchemaVersion != MapProjectFormat.SchemaVersion
                    && !MapProjectFormat.IsExtendedV4Schema(snapshotManifest.SchemaVersion))
                {
                    throw new MapProjectCorruptedException(
                        $"Checkpoint {id} snapshot manifest does not declare a supported object-chunk schema.");
                }
                if (snapshotManifest.Canvas is null)
                {
                    throw new MapProjectCorruptedException(
                        $"Checkpoint {id} snapshot manifest has no canvas object.");
                }
                if (!string.Equals(snapshotManifest.DocumentId, mainManifest.DocumentId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new MapProjectCorruptedException(
                        $"Checkpoint {id} belongs to a different document id.");
                }

                var sceneBytes = ReadEntry(archive, sceneEntry);
                if (!string.Equals(Sha256Hex(sceneBytes),
                        RequireSha256(snapshotManifest.SceneSha256, "checkpoint scene sha256"),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new MapProjectCorruptedException(
                        $"Checkpoint {id} scene does not match its checksum.");
                }
                var scene = ParseJson<SceneDto>(sceneBytes, sceneEntry);
                ValidatePathSchema(scene, snapshotManifest.SchemaVersion);
                if (schemaVersion is >= 2 and <= 3)
                {
                    ValidateV2SceneShape(sceneBytes, sceneEntry);
                }
                else if (schemaVersion == MapProjectFormat.SchemaVersion)
                {
                    ValidateV4SceneShape(sceneBytes, sceneEntry);
                }
                if (!string.Equals(scene.DocumentId, snapshotManifest.DocumentId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new MapProjectCorruptedException(
                        $"Checkpoint {id} manifest and scene disagree about the document id.");
                }

                var assetManifestBytes = ReadEntry(archive, assetManifestEntry);
                if (!string.Equals(Sha256Hex(assetManifestBytes),
                        RequireSha256(snapshotManifest.AssetManifestSha256,
                            "checkpoint asset manifest sha256"),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new MapProjectCorruptedException(
                        $"Checkpoint {id} asset manifest does not match its checksum.");
                }
                var assetManifest = ParseJson<AssetManifestDto>(
                    assetManifestBytes, assetManifestEntry);
                if (!allowLegacyLayerTypes)
                {
                    ValidateRequiredArray(assetManifestBytes, assetManifestEntry, "assets");
                }

                var previewBytes = ReadEntry(archive, previewEntry);
                if (!string.Equals(Sha256Hex(previewBytes),
                        RequireSha256(snapshotManifest.PreviewSha256, "checkpoint preview sha256"),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new MapProjectCorruptedException(
                        $"Checkpoint {id} preview does not match its checksum.");
                }
                ValidatePreviewHeader(
                    archive,
                    previewEntry,
                    MapProjectFormat.MaxCheckpointPreviewWidth,
                    MapProjectFormat.MaxCheckpointPreviewHeight,
                    (long)MapProjectFormat.MaxCheckpointPreviewWidth
                        * MapProjectFormat.MaxCheckpointPreviewHeight);

                var objectStorages = schemaVersion == MapProjectFormat.SchemaVersion
                    ? ReadAndValidateObjectChunks(archive, scene, root, allowUnknownObjectKinds)
                    : new Dictionary<string, SceneObjectList>(StringComparer.Ordinal);
                var tiles = ReadAndValidateTiles(archive, scene, root);
                var assets = ReadAndValidateAssets(archive, assetManifest, root);
                var domainManifest = new ManifestDto
                {
                    DocumentId = snapshotManifest.DocumentId,
                    Title = snapshotManifest.Title,
                    CreatedUtc = snapshotManifest.CreatedUtc,
                    Canvas = snapshotManifest.Canvas,
                };
                var snapshotDocument = ToDomain(
                    domainManifest, scene, objectStorages, assetManifest, tiles, assets,
                    checkpoints: null,
                    entryRoot: root,
                    allowLegacyLayerTypes,
                    allowUnknownObjectKinds);
                checkpoints.Add(new MapCheckpoint(
                    id,
                    name,
                    ParseTimestamp(item.CreatedUtc, "checkpoint createdUtc"),
                    MapCheckpointSnapshot.Capture(snapshotDocument),
                    previewBytes));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException
                or InvalidOperationException)
            {
                throw new MapProjectCorruptedException(
                    "The checkpoint catalog contains values the document model rejects: " + ex.Message,
                    ex);
            }
        }

        foreach (var entry in checkpointArchiveEntries)
        {
            if (expectedMetadataEntries.Contains(entry.FullName))
            {
                continue;
            }

            var isExpectedPayload = roots.Any(root =>
                entry.FullName.StartsWith(root + MapProjectFormat.RasterPrefix, StringComparison.Ordinal)
                || entry.FullName.StartsWith(root + MapProjectFormat.MaskPrefix, StringComparison.Ordinal)
                || entry.FullName.StartsWith(root + MapProjectFormat.AssetPrefix, StringComparison.Ordinal)
                || entry.FullName.StartsWith(root + MapProjectFormat.ObjectPrefix, StringComparison.Ordinal));
            if (!isExpectedPayload)
            {
                throw new MapProjectCorruptedException(
                    $"Checkpoint entry {entry.FullName} is not declared by the catalog.");
            }
        }

        return checkpoints;
    }

    // ---------------------------------------------------------------- mapping

    private static void ValidateRequiredArray(byte[] bytes, string entryName, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(propertyName, out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                throw new MapProjectCorruptedException(
                    $"schema v2 {entryName} requires an explicit {propertyName} array.");
            }
        }
        catch (JsonException ex)
        {
            throw new MapProjectCorruptedException($"{entryName} is not valid JSON.", ex);
        }
    }

    private static void ValidateV2SceneShape(byte[] bytes, string entryName)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("layers", out var layers)
                || layers.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("notes", out var notes)
                || notes.ValueKind != JsonValueKind.Array)
            {
                throw new MapProjectCorruptedException(
                    $"schema v2 {entryName} requires explicit layers and notes arrays.");
            }

            foreach (var layer in layers.EnumerateArray())
            {
                if (layer.ValueKind != JsonValueKind.Object
                    || !layer.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || !layer.TryGetProperty("objects", out var objects)
                    || objects.ValueKind != JsonValueKind.Array)
                {
                    throw new MapProjectCorruptedException(
                        $"schema v2 {entryName} requires every layer type and objects collection.");
                }

                var typeName = type.GetString();
                var required = typeName switch
                {
                    "Brush" or "Land" => "tiles",
                    "Grid" => "grids",
                    _ => null,
                };
                if (required is not null
                    && (!layer.TryGetProperty(required, out var collection)
                        || collection.ValueKind != JsonValueKind.Array))
                {
                    throw new MapProjectCorruptedException(
                        $"schema v2 {entryName} requires an explicit {required} array on {typeName} layers.");
                }

                if (layer.TryGetProperty("mask", out var mask)
                    && mask.ValueKind != JsonValueKind.Null
                    && (mask.ValueKind != JsonValueKind.Object
                        || !mask.TryGetProperty("tiles", out var maskTiles)
                        || maskTiles.ValueKind != JsonValueKind.Array))
                {
                    throw new MapProjectCorruptedException(
                        $"schema v2 {entryName} requires an explicit mask tiles array.");
                }

                ValidateCompositionCollections(objects, entryName);
            }
        }
        catch (JsonException ex)
        {
            throw new MapProjectCorruptedException($"{entryName} is not valid JSON.", ex);
        }
    }

    private static void ValidateV4SceneShape(byte[] bytes, string entryName)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("layers", out var layers)
                || layers.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("notes", out var notes)
                || notes.ValueKind != JsonValueKind.Array)
            {
                throw new MapProjectCorruptedException(
                    $"schema v4 {entryName} requires explicit layers and notes arrays.");
            }

            foreach (var layer in layers.EnumerateArray())
            {
                if (layer.ValueKind != JsonValueKind.Object
                    || !layer.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || layer.TryGetProperty("objects", out _))
                {
                    throw new MapProjectCorruptedException(
                        $"schema v4 {entryName} requires every layer type and forbids object arrays.");
                }

                var typeName = type.GetString();
                var hasChunks = layer.TryGetProperty("objectChunks", out var chunks);
                if (typeName == "Object"
                    ? !hasChunks || chunks.ValueKind != JsonValueKind.Array
                    : hasChunks)
                {
                    throw new MapProjectCorruptedException(
                        $"schema v4 {entryName} has invalid object-chunk metadata on {typeName} layer.");
                }

                var required = typeName switch
                {
                    "Brush" or "Land" => "tiles",
                    "Grid" => "grids",
                    _ => null,
                };
                if (required is not null
                    && (!layer.TryGetProperty(required, out var collection)
                        || collection.ValueKind != JsonValueKind.Array))
                {
                    throw new MapProjectCorruptedException(
                        $"schema v4 {entryName} requires an explicit {required} array on {typeName} layers.");
                }

                if (layer.TryGetProperty("mask", out var mask)
                    && mask.ValueKind != JsonValueKind.Null
                    && (mask.ValueKind != JsonValueKind.Object
                        || !mask.TryGetProperty("tiles", out var maskTiles)
                        || maskTiles.ValueKind != JsonValueKind.Array))
                {
                    throw new MapProjectCorruptedException(
                        $"schema v4 {entryName} requires an explicit mask tiles array.");
                }
            }
        }
        catch (JsonException ex)
        {
            throw new MapProjectCorruptedException($"{entryName} is not valid JSON.", ex);
        }
    }

    private static bool ObjectSequencesMatch(
        IReadOnlyList<MapLayer> first,
        IReadOnlyList<MapLayer> second) =>
        first.Count == second.Count
        && first.Zip(second).All(pair =>
            pair.First.Id == pair.Second.Id && ObjectSequencesMatch(pair.First, pair.Second));

    private static bool ObjectSequencesMatch(MapLayer first, MapLayer second)
    {
        if (first is not ObjectLayer firstObjects || second is not ObjectLayer secondObjects)
        {
            return first is not ObjectLayer && second is not ObjectLayer;
        }

        if (firstObjects.Objects.Count != secondObjects.Objects.Count)
        {
            return false;
        }

        for (var index = 0; index < firstObjects.Objects.Count; index++)
        {
            var expected = firstObjects.Objects[index];
            var actual = secondObjects.Objects[index];
            if (expected.Id != actual.Id
                || !JsonSerializer.SerializeToUtf8Bytes(ToObjectDto(expected), JsonOptions).AsSpan()
                    .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(ToObjectDto(actual), JsonOptions)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ObjectIdentitySequencesMatch(MapLayer first, MapLayer second)
    {
        if (first is not ObjectLayer firstObjects || second is not ObjectLayer secondObjects)
        {
            return first is not ObjectLayer && second is not ObjectLayer;
        }

        return firstObjects.Objects.Count == secondObjects.Objects.Count
            && firstObjects.Objects.Select(item => item.Id)
                .SequenceEqual(secondObjects.Objects.Select(item => item.Id));
    }

    private static void ValidateCompositionCollections(JsonElement objects, string entryName)
    {
        foreach (var item in objects.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("composition", out var composition)
                || composition.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (composition.ValueKind != JsonValueKind.Object)
            {
                throw new MapProjectCorruptedException(
                    $"schema v2 {entryName} has a malformed composition.");
            }
            foreach (var collectionName in new[] { "items", "assets", "grids", "filters" })
            {
                if (!composition.TryGetProperty(collectionName, out var collection)
                    || collection.ValueKind != JsonValueKind.Array)
                {
                    throw new MapProjectCorruptedException(
                        $"schema v2 {entryName} requires composition {collectionName} arrays.");
                }
            }
            ValidateCompositionCollections(composition.GetProperty("items"), entryName);
        }
    }

    private static AssetManifestDto ToAssetManifestDto(
        MapDocument document, string entryRoot = "") => new()
    {
        Assets = [.. document.Assets.OrderBy(asset => asset.Sha256).Select(asset => new AssetEntryDto
        {
            Sha256 = asset.Sha256,
            DisplayName = asset.DisplayName,
            MediaType = asset.MediaType,
            Extension = asset.Extension,
            Storage = asset.Storage.ToString(),
            ImportedUtc = FormatTimestamp(asset.ImportedUtc),
            Provenance = asset.Provenance,
            License = asset.License,
            CatalogId = asset.CatalogId,
            RelativePath = asset.RelativePath,
            ContentEntry = asset.Storage == ProjectAssetStorage.Embedded
                ? entryRoot + AssetContentEntry(asset.Sha256, asset.Extension)
                : null,
            ThumbnailEntry = asset.FallbackThumbnailPng is null
                ? null
                : entryRoot + AssetThumbnailEntry(asset.Sha256),
            ThumbnailSha256 = asset.FallbackThumbnailPng is null
                ? null
                : Sha256Hex(asset.FallbackThumbnailPng),
        })],
    };

    private static SceneDto ToSceneDto(
        MapDocument document,
        IReadOnlyDictionary<string, IReadOnlyList<ObjectChunkDescriptorDto>> objectChunks) => new()
    {
        DocumentId = document.Id.ToString(),
        Layers = [.. document.Layers.Select(layer => ToLayerDto(layer, objectChunks))],
        Notes = [.. document.Notes.Select(ToNoteDto)],
        StampForeground = document.StampForeground is { } foreground
            ? new StampForegroundDto { Tiles = ToTileDtos(foreground.Tiles) }
            : null,
    };

    private static LayerDto ToLayerDto(
        MapLayer layer,
        IReadOnlyDictionary<string, IReadOnlyList<ObjectChunkDescriptorDto>> objectChunks)
    {
        var dto = new LayerDto
        {
            PlacementAreas = layer is ObjectLayer areaLayer && !areaLayer.PlacementAreas.IsEmpty ? [.. areaLayer.PlacementAreas] : null,
            PlacementPaths = layer is ObjectLayer pathLayer && !pathLayer.PlacementPaths.IsEmpty
                ? [.. pathLayer.PlacementPaths] : null,
            Rivers = layer is ObjectLayer riverLayer && !riverLayer.Rivers.IsEmpty ? [.. riverLayer.Rivers] : null,
            Roads = layer is ObjectLayer roadLayer && !roadLayer.Roads.IsEmpty ? [.. roadLayer.Roads] : null,
            SurfaceOrder = layer is ObjectLayer orderLayer && !orderLayer.SurfaceOrder.IsDefaultOrEmpty && !orderLayer.Roads.IsEmpty
                ? [.. orderLayer.SurfaceOrder] : null,
            Id = layer.Id.ToString(),
            Type = layer switch
            {
                BaseColorLayer => "BaseColor",
                ObjectLayer => "Object",
                BrushLayer => "Brush",
                LandLayer => "Land",
                GridLayer => "Grid",
                FilterLayer => "Filter",
                _ => throw new InvalidOperationException($"Unknown layer type {layer.GetType().Name}."),
            },
            Name = layer.Name,
            Visible = layer.Visible,
            Locked = layer.Locked,
            Opacity = layer.Opacity,
            Blend = layer.Blend,
            Color = (layer as BaseColorLayer)?.Color,
            BaseMaterial = layer is BaseColorLayer { TextureId: { } baseTexture } textured
                ? new BaseMaterialDto { TextureId = baseTexture, Opacity = textured.TextureOpacity, Scale = textured.TextureScale }
                : null,
            Objects = null,
            ObjectChunks = layer is ObjectLayer
                ? [.. objectChunks[layer.Id.ToString()]]
                : null,
            Tiles = layer is IRasterContentLayer raster
                ? ToTileDtos(raster.ContentTiles)
                : null,
            Mask = layer.Mask is { } mask
                ? new MaskDto { Enabled = mask.Enabled, Tiles = ToTileDtos(mask.Tiles) }
                : null,
            LandStyle = layer is LandLayer land ? ToLandStyleDto(land.EdgeStyle) : null,
            LandPaintTiles = layer is LandLayer { PaintTiles.TileCount: > 0 } painted
                ? ToTileDtos(painted.PaintTiles) : null,
            Level = layer is LandLayer { Level: not 0 } leveled ? leveled.Level : null,
            Grids = layer is GridLayer gridLayer
                ? [.. gridLayer.Grids.Select(ToGridDto)]
                : null,
            Filter = layer is FilterLayer filterLayer ? ToFilterDto(filterLayer.Settings) : null,
            Effects = layer is ObjectLayer effectsLayer ? new LayerEffectsDto
            {
                ClippingEnabled = effectsLayer.Effects.ClippingEnabled,
                ShadowColor = effectsLayer.Effects.ShadowColor,
                OuterShadowOpacity = effectsLayer.Effects.OuterShadowOpacity,
                InnerShadowOpacity = effectsLayer.Effects.InnerShadowOpacity,
                ShadowOffsetX = effectsLayer.Effects.ShadowOffsetX,
                ShadowOffsetY = effectsLayer.Effects.ShadowOffsetY,
                ShadowBlur = effectsLayer.Effects.ShadowBlur,
            } : null,
            Sublayers = layer is ObjectLayer sublayerLayer
                ? [.. sublayerLayer.SublayerStates.Select(state => new ObjectSublayerStateDto
                {
                    Sublayer = state.Sublayer, Visible = state.Visible, Locked = state.Locked,
                })]
                : null,
        };
        return dto;
    }

    private static SceneObjectDto ToObjectDto(SceneObject sceneObject) => new()
    {
        Id = sceneObject.Id.ToString(),
        Kind = sceneObject.Kind.ToString(),
        Name = sceneObject.Name,
        X = sceneObject.X,
        Y = sceneObject.Y,
        Width = sceneObject.Width,
        Height = sceneObject.Height,
        RotationDegrees = sceneObject.RotationDegrees,
        FillColor = sceneObject.FillColor,
        FillGradient = ToGradientDto(sceneObject.FillGradient),
        StrokeColor = sceneObject.StrokeColor,
        StrokeWidth = sceneObject.StrokeWidth,
        Visible = sceneObject.Visible,
        Locked = sceneObject.Locked,
        Sublayer = sceneObject.Sublayer,
        GroupId = sceneObject.GroupId?.ToString("D"),
        GroupName = sceneObject.GroupName,
        ObjectOpacity = sceneObject.ObjectOpacity,
        Stamp = sceneObject.Stamp is { } stamp
            ? new StampStyleDto
            {
                AssetSha256 = stamp.AssetSha256,
                ReferenceWidth = stamp.ReferenceWidth,
                ReferenceHeight = stamp.ReferenceHeight,
                FlipHorizontal = stamp.FlipHorizontal,
                FlipVertical = stamp.FlipVertical,
                MaskAssetSha256 = stamp.MaskAssetSha256,
                ClipMaskEnabled = stamp.ClipMaskEnabled ? true : null,
                Blend = stamp.Blend,
                Blur = stamp.Blur,
                ShadowOffsetX = stamp.ShadowOffsetX,
                ShadowOffsetY = stamp.ShadowOffsetY,
                ShadowBlur = stamp.ShadowBlur,
                ShadowOpacity = stamp.ShadowOpacity,
                ShadowPlacement = stamp.ShadowPlacement.ToString(),
                ShadowColor = stamp.ShadowColor,
                Hue = stamp.Hue,
                Saturation = stamp.Saturation,
                Brightness = stamp.Brightness,
                Contrast = stamp.Contrast,
            }
            : null,
        Line = sceneObject.Line is { } line ? ToLineDto(line) : null,
        Shape = sceneObject.Shape is { } shape ? ToShapeDto(shape) : null,
        Room = sceneObject.Room is { } room ? new RoomStyleDto
        {
            CornerRadius = room.CornerRadius,
            CapAssetSha256 = RequireOptionalSha256(room.CapAssetSha256, "room cap asset hash"),
            CapScale = room.CapScale,
            CapAngle = room.CapAngleDegrees,
            CapShadow = room.CapShadow,
        } : null,
        Text = sceneObject.Text is { } text ? ToTextDto(text) : null,
        Composition = sceneObject.Composition is { } composition ? ToCompositionDto(composition) : null,
        PlaceholderReason = sceneObject.PlaceholderReason,
        OpaquePayload = sceneObject.OpaquePayload is { } opaque
            ? new OpaqueSceneObjectPayloadDto
            {
                OriginalKind = opaque.OriginalKind,
                RawJson = opaque.RawJson,
            }
            : null,
    };

    private static NoteDto ToNoteDto(MapNote note) => new()
    {
        Id = note.Id.ToString(),
        Title = note.Title,
        Markdown = note.Markdown,
        AnchorKind = note.AnchorKind.ToString(),
        WorldX = note.WorldX,
        WorldY = note.WorldY,
        ObjectId = note.ObjectId?.ToString(),
    };

    private static GridDefinitionDto ToGridDto(GridDefinition grid) => new()
    {
        Id = grid.Id.ToString(),
        Geometry = grid.Geometry.ToString(),
        CellSize = grid.CellSize,
        Subdivisions = grid.Subdivisions,
        LineWidth = grid.LineWidth,
        Color = grid.Color,
        Opacity = grid.Opacity,
        OffsetX = grid.OffsetX,
        OffsetY = grid.OffsetY,
        RotationDegrees = grid.RotationDegrees,
        SnapEnabled = grid.SnapEnabled,
        IncludeInExport = grid.IncludeInExport,
    };

    private static TextStyleDto ToTextDto(TextStyle text) => new()
    {
        Content = text.Content,
        RequestedFontFamily = text.RequestedFontFamily,
        FallbackFontFamily = text.FallbackFontFamily,
        FontWeight = text.FontWeight,
        FontSize = text.FontSize,
        FillColor = text.FillColor,
        OutlineColor = text.OutlineColor,
        OutlineWidth = text.OutlineWidth,
        ShadowOffsetX = text.ShadowOffsetX,
        ShadowOffsetY = text.ShadowOffsetY,
        ShadowBlur = text.ShadowBlur,
        ShadowOpacity = text.ShadowOpacity,
        Curve = text.Curve,
        LetterSpacing = text.LetterSpacing,
        LineHeight = text.LineHeight,
        Alignment = text.Alignment.ToString(),
        FlipHorizontal = text.FlipHorizontal,
        FlipVertical = text.FlipVertical,
        Blur = text.Blur,
        Blend = text.Blend,
    };

    private static FilterSettingsDto ToFilterDto(FilterSettings filter) => new()
    {
        Brightness = filter.Brightness,
        Contrast = filter.Contrast,
        Saturation = filter.Saturation,
        HueDegrees = filter.HueDegrees,
        Temperature = filter.Temperature,
        TextureAssetSha256 = RequireOptionalSha256(
            filter.TextureAssetSha256, "filter texture asset hash"),
        TextureOpacity = filter.TextureOpacity,
        TextureScale = filter.TextureScale,
        GradientMap = filter.GradientMap == new FilterGradientMap() ? null : new FilterGradientMapDto
        {
            Shadow = filter.GradientMap.ShadowColor,
            Midtone = filter.GradientMap.MidtoneColor,
            Highlight = filter.GradientMap.HighlightColor,
            Strength = filter.GradientMap.Strength,
        },
        PostProcess = filter.PostProcess == new FilterPostProcess() ? null : new FilterPostProcessDto
        {
            Saturation = filter.PostProcess.Saturation,
            Brightness = filter.PostProcess.Brightness,
            Contrast = filter.PostProcess.Contrast,
            Sharpness = filter.PostProcess.Sharpness,
        },
        ColorStrength = filter.ColorStrength == 1 ? null : filter.ColorStrength,
        ColorPresetId = filter.ColorPresetId,
    };

    private static EditableCompositionDto ToCompositionDto(EditableComposition composition) => new()
    {
        Id = composition.Id.ToString(),
        SourceDocumentId = composition.SourceDocumentId.ToString(),
        Name = composition.Name,
        SourceWidth = composition.SourceWidth,
        SourceHeight = composition.SourceHeight,
        AnchorX = composition.AnchorX,
        AnchorY = composition.AnchorY,
        ReferenceGridScale = composition.ReferenceGridScale,
        Items = [.. composition.Items.Select(ToObjectDto)],
        Assets = [.. composition.Assets.Select(asset => new CompositionAssetDto
        {
            Sha256 = asset.Sha256, DisplayName = asset.DisplayName,
            Provenance = asset.Provenance, License = asset.License,
        })],
        IncludeBackground = composition.IncludeBackground,
        BackgroundColor = composition.BackgroundColor,
        IncludeGrids = composition.IncludeGrids,
        Grids = [.. composition.Grids.Select(ToGridDto)],
        IncludeFilters = composition.IncludeFilters,
        Filters = [.. composition.Filters.Select(ToFilterDto)],
    };

    private static MapDocument ToDomain(
        ManifestDto manifest,
        SceneDto scene,
        IReadOnlyDictionary<string, SceneObjectList> objectStorages,
        AssetManifestDto assetManifest,
        IReadOnlyDictionary<string, byte[]> tileEntries,
        IReadOnlyDictionary<string, byte[]> assetEntries,
        IReadOnlyList<MapCheckpoint>? checkpoints = null,
        string entryRoot = "",
        bool allowLegacyLayerTypes = false,
        bool allowUnknownObjectKinds = false)
    {
        // Explicit JSON null can overwrite any DTO default, so every object/array the mapping
        // walks is checked up front - a damaged file must produce a clear error, never an NRE.
        if (manifest.Canvas is null)
        {
            throw new MapProjectCorruptedException("manifest.json has no canvas object.");
        }

        if (scene.Layers is null || scene.Notes is null
            || scene.Notes.Any(static note => note is null)
            || scene.Layers.Any(HasInvalidLayerDto))
        {
            throw new MapProjectCorruptedException(
                "scene.json has a null object or a null element in a required collection.");
        }

        try
        {
            var legacyStage2 = allowLegacyLayerTypes && scene.Layers.All(layer => layer.Type is null);
            if (allowLegacyLayerTypes
                && !legacyStage2
                && scene.Layers.Any(layer => layer.Type is null))
            {
                throw new MapProjectCorruptedException(
                    "schema v1 mixes historical untyped and explicit layer shapes.");
            }
            if (!allowLegacyLayerTypes && scene.Layers.Any(layer => layer.Type is null))
            {
                throw new MapProjectCorruptedException(
                    "schema v2 requires an explicit type on every layer.");
            }

            var layers = scene.Layers.Select(layer => ToLayer(
                layer, objectStorages, tileEntries, entryRoot, allowUnknownObjectKinds)).ToList();
            if (legacyStage2)
            {
                layers.Insert(0, new BaseColorLayer(
                    LegacyBaseLayerId(manifest.DocumentId),
                    "Цвет фона", visible: true, locked: false, opacity: 1.0,
                    RequireString(manifest.Canvas.Background, "canvas background")));
            }

            return new MapDocument(
                DocumentId.Parse(RequireString(manifest.DocumentId, "document id")),
                RequireString(manifest.Title, "title"),
                ParseTimestamp(manifest.CreatedUtc, "createdUtc"),
                new CanvasDefinition(
                    manifest.Canvas.Width,
                    manifest.Canvas.Height,
                    RequireString(manifest.Canvas.Unit, "canvas unit"),
                    RequireString(manifest.Canvas.Background, "canvas background")),
                layers,
                assetManifest.Assets.Select(asset => ToAsset(asset, assetEntries)).ToList(),
                scene.Notes.Select(ToNote).ToList(),
                checkpoints,
                scene.StampForeground is { } foreground
                    ? new StampForegroundSurface(ToTileMap(
                        foreground.Tiles,
                        entryRoot + MapProjectFormat.RasterPrefix,
                        "foreground",
                        4,
                        tileEntries))
                    : null);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            // A container that parses as JSON but violates the domain's own rules is a damaged
            // project, not an application bug worth an unhandled crash.
            throw new MapProjectCorruptedException(
                "The container holds values the document model rejects: " + ex.Message, ex);
        }
    }

    private static MapLayer ToLayer(
        LayerDto layer,
        IReadOnlyDictionary<string, SceneObjectList> objectStorages,
        IReadOnlyDictionary<string, byte[]> tileEntries,
        string entryRoot,
        bool allowUnknownObjectKinds)
    {
        var id = LayerId.Parse(RequireString(layer.Id, "layer id"));
        var name = RequireString(layer.Name, "layer name");
        var mask = layer.Mask is { } maskDto
            ? new LayerMask(
                ToTileMap(maskDto.Tiles, entryRoot + MapProjectFormat.MaskPrefix, id, 1, tileEntries),
                maskDto.Enabled)
            : null;

        MapLayer result = layer.Type switch
        {
            null or "Object" => new ObjectLayer(
                id, name, layer.Visible, layer.Locked, layer.Opacity,
                objectStorages.TryGetValue(id.ToString(), out var chunkedObjects)
                    ? chunkedObjects
                    : layer.Objects is not null
                        ? SceneObjectList.From(
                            [.. layer.Objects.Select(item => ToObject(item, allowUnknownObjectKinds))])
                        : throw new MapProjectCorruptedException(
                            $"Object layer {id} has neither legacy objects nor v4 chunks."),
                mask)
            {
                Effects = ToLayerEffects(layer.Effects),
                SublayerStates = ToSublayerStates(layer.Sublayers),
                PlacementPaths = layer.PlacementPaths is null ? [] : [.. layer.PlacementPaths],
                PlacementAreas = layer.PlacementAreas is null ? [] : [.. layer.PlacementAreas],
                Rivers = layer.Rivers is null ? [] : [.. layer.Rivers],
                Roads = layer.Roads is null ? [] : [.. layer.Roads],
                SurfaceOrder = layer.SurfaceOrder is null ? [] : [.. layer.SurfaceOrder],
            },
            "BaseColor" => ToBaseMaterial(new BaseColorLayer(
                id, name, layer.Visible, layer.Locked, layer.Opacity,
                RequireString(layer.Color, "base color")), layer.BaseMaterial),
            "Brush" => new BrushLayer(
                id, name, layer.Visible, layer.Locked, layer.Opacity,
                ToTileMap(layer.Tiles ?? [], entryRoot + MapProjectFormat.RasterPrefix, id, 4, tileEntries), mask),
            "Land" => new LandLayer(
                id, name, layer.Visible, layer.Locked, layer.Opacity,
                ToTileMap(layer.Tiles ?? [], entryRoot + MapProjectFormat.RasterPrefix, id, 1, tileEntries),
                ToLandStyle(layer.LandStyle), mask)
            {
                Level = TerrainOrder.Clamp(layer.Level ?? 0),
                PaintTiles = ToTileMap(layer.LandPaintTiles ?? [], entryRoot + MapProjectFormat.LandPaintPrefix, id, 4, tileEntries),
            },
            "Grid" => new GridLayer(
                id, name, layer.Visible, layer.Locked, layer.Opacity,
                [.. (layer.Grids ?? []).Select(ToGrid)], mask),
            "Filter" => new FilterLayer(
                id, name, layer.Visible, layer.Locked, layer.Opacity,
                ToFilter(layer.Filter!), mask),
            _ => throw new MapProjectCorruptedException(
                $"scene.json holds layer type \"{layer.Type}\" this version does not define."),
        };
        return result with { Blend = RequireString(layer.Blend, "layer blend") };
    }

    private static LayerEffects ToLayerEffects(LayerEffectsDto? dto) => dto is null
        ? new LayerEffects()
        : new LayerEffects
        {
            ClippingEnabled = dto.ClippingEnabled,
            ShadowColor = RequireAppearanceColor(dto.ShadowColor, "layer shadow color"),
            OuterShadowOpacity = RequireFinite(dto.OuterShadowOpacity, "layer outer shadow opacity", 0, 1),
            InnerShadowOpacity = RequireFinite(dto.InnerShadowOpacity, "layer inner shadow opacity", 0, 1),
            ShadowOffsetX = RequireFinite(dto.ShadowOffsetX, "layer shadow offset X", -512, 512),
            ShadowOffsetY = RequireFinite(dto.ShadowOffsetY, "layer shadow offset Y", -512, 512),
            ShadowBlur = RequireFinite(dto.ShadowBlur, "layer shadow blur", 0, 256),
        };

    private static IReadOnlyList<ObjectSublayerState> ToSublayerStates(
        IReadOnlyList<ObjectSublayerStateDto>? values)
    {
        if (values is null) return [];
        if (values.Count > 21 || values.Select(item => item.Sublayer).Distinct().Count() != values.Count
            || values.Any(item => item.Sublayer is < -10 or > 10))
            throw new MapProjectCorruptedException("Object sublayer state is outside the supported range.");
        return [.. values.Where(item => !item.Visible || item.Locked)
            .Select(item => new ObjectSublayerState(item.Sublayer, item.Visible, item.Locked))];
    }

    private static SceneObject ToObject(SceneObjectDto dto, bool allowUnknownObjectKinds = false) => new(
        ObjectId.Parse(RequireString(dto.Id, "object id")),
        ParseKind(dto.Kind, allowUnknownObjectKinds),
        RequireString(dto.Name, "object name"),
        dto.X,
        dto.Y,
        dto.Width,
        dto.Height,
        dto.RotationDegrees,
        RequireString(dto.FillColor, "object fill color"),
        RequireString(dto.StrokeColor, "object stroke color"),
        dto.StrokeWidth,
        dto.Visible,
        dto.Locked)
    {
        Sublayer = dto.Sublayer is >= -10 and <= 10
            ? dto.Sublayer
            : throw new MapProjectCorruptedException(
                $"scene.json holds an out-of-range object sublayer value \"{dto.Sublayer}\"."),
        GroupId = dto.GroupId is null ? null : Guid.ParseExact(dto.GroupId, "D"),
        GroupName = dto.GroupName,
        ObjectOpacity = RequireFinite(dto.ObjectOpacity, "object opacity", min: 0, max: 1),
        FillGradient = ToGradient(dto.FillGradient, "object fill gradient"),
        Stamp = dto.Stamp is { } stamp
            ? new StampStyle(RequireString(stamp.AssetSha256, "stamp asset hash"))
            {
                ReferenceWidth = RequirePositiveOrNull(stamp.ReferenceWidth, "stamp reference width"),
                ReferenceHeight = RequirePositiveOrNull(stamp.ReferenceHeight, "stamp reference height"),
                FlipHorizontal = stamp.FlipHorizontal,
                FlipVertical = stamp.FlipVertical,
                MaskAssetSha256 = RequireOptionalSha256(
                    stamp.MaskAssetSha256, "stamp mask asset hash"),
                ClipMaskEnabled = RequireClipMaskEnabled(
                    stamp.ClipMaskEnabled, stamp.MaskAssetSha256, stamp.AssetSha256),
                Blend = RequireBlend(stamp.Blend, "stamp blend"),
                Blur = RequireFinite(
                    stamp.Blur, "stamp blur", min: 0, max: AppearanceLimits.MaxBlur),
                ShadowOffsetX = RequireFinite(
                    stamp.ShadowOffsetX, "stamp shadow offset X",
                    -AppearanceLimits.MaxShadowOffset, AppearanceLimits.MaxShadowOffset),
                ShadowOffsetY = RequireFinite(
                    stamp.ShadowOffsetY, "stamp shadow offset Y",
                    -AppearanceLimits.MaxShadowOffset, AppearanceLimits.MaxShadowOffset),
                ShadowBlur = RequireFinite(
                    stamp.ShadowBlur, "stamp shadow blur", min: 0, max: AppearanceLimits.MaxShadowBlur),
                ShadowOpacity = RequireFinite(stamp.ShadowOpacity, "stamp shadow opacity", min: 0, max: 1),
                ShadowPlacement = stamp.ShadowPlacement is null
                    ? StampShadowPlacement.Object
                    : ParseDefined<StampShadowPlacement>(stamp.ShadowPlacement, "stamp shadow placement"),
                ShadowColor = RequireAppearanceColor(
                    stamp.ShadowColor ?? AppearanceColor.OpaqueBlack, "stamp shadow color"),
                Hue = RequireFinite(stamp.Hue, "stamp hue", min: -180, max: 180),
                Saturation = RequireFinite(stamp.Saturation, "stamp saturation", min: -1, max: 1),
                Brightness = RequireFinite(stamp.Brightness, "stamp brightness", min: -1, max: 1),
                Contrast = RequireFinite(stamp.Contrast, "stamp contrast", min: -1, max: 1),
            }
            : null,
        Line = dto.Line is { } line ? ToLine(line) : null,
        Shape = dto.Shape is { } shape ? ToShape(shape) : null,
        Room = dto.Room is { } room ? new RoomStyle
        {
            CornerRadius = RequireFinite(room.CornerRadius, "room corner radius", 0, RoomStyle.MaxCornerRadius),
            CapAssetSha256 = RequireOptionalSha256(room.CapAssetSha256, "room cap asset hash"),
            CapScale = RequireFinite(room.CapScale, "room cap scale", 0.01, RoomStyle.MaxCapScale),
            CapAngleDegrees = RequireFinite(room.CapAngle, "room cap angle", -360, 360),
            CapShadow = room.CapShadow,
        } : null,
        Text = dto.Text is { } text ? ToText(text) : null,
        Composition = dto.Composition is { } composition
            ? ToComposition(composition, allowUnknownObjectKinds)
            : null,
        PlaceholderReason = allowUnknownObjectKinds && !IsDefinedKind(dto.Kind)
            ? "Неизвестный тип объекта сохранён как заполнитель."
            : dto.PlaceholderReason,
        OpaquePayload = ToOpaquePayload(dto, allowUnknownObjectKinds),
    };

    private static OpaqueSceneObjectPayload? ToOpaquePayload(
        SceneObjectDto dto,
        bool allowUnknownObjectKinds)
    {
        string? originalKind = null;
        string? rawJson = null;
        if (allowUnknownObjectKinds && !IsDefinedKind(dto.Kind))
        {
            originalKind = RequireString(dto.Kind, "unknown object kind");
            rawJson = JsonSerializer.Serialize(dto, JsonOptions);
        }
        else if (dto.OpaquePayload is { } opaque)
        {
            if (!string.Equals(dto.Kind, nameof(SceneObjectKind.Placeholder), StringComparison.Ordinal))
            {
                throw new MapProjectCorruptedException(
                    "Only a Placeholder object may carry an opaque payload.");
            }
            originalKind = RequireString(opaque.OriginalKind, "opaque object original kind");
            rawJson = RequireString(opaque.RawJson, "opaque object raw JSON");
        }

        if (rawJson is null) return null;
        if (Encoding.UTF8.GetByteCount(rawJson) > OpaqueSceneObjectPayload.MaxUtf8Bytes)
        {
            throw new MapProjectResourceLimitException(
                "Opaque object payload exceeds the bounded migration envelope.");
        }
        return new OpaqueSceneObjectPayload(originalKind!, rawJson);
    }

    private static MapNote ToNote(NoteDto note) => new(
        NoteId.Parse(RequireString(note.Id, "note id")),
        RequireString(note.Title, "note title"), note.Markdown ?? throw new MapProjectCorruptedException("A note has no markdown."),
        ParseDefined<NoteAnchorKind>(note.AnchorKind, "note anchor kind"),
        note.WorldX, note.WorldY,
        note.ObjectId is null ? null : ObjectId.Parse(note.ObjectId));

    private static GridDefinition ToGrid(GridDefinitionDto grid) => new(
        GridId.Parse(RequireString(grid.Id, "grid id")),
        ParseDefined<GridGeometry>(grid.Geometry, "grid geometry"), grid.CellSize)
    {
        Subdivisions = grid.Subdivisions,
        LineWidth = grid.LineWidth,
        Color = RequireString(grid.Color, "grid color"),
        Opacity = grid.Opacity,
        OffsetX = grid.OffsetX,
        OffsetY = grid.OffsetY,
        RotationDegrees = grid.RotationDegrees,
        SnapEnabled = grid.SnapEnabled,
        IncludeInExport = grid.IncludeInExport,
    };

    private static TextStyle ToText(TextStyleDto text) => new()
    {
        Content = text.Content ?? "",
        RequestedFontFamily = RequireString(text.RequestedFontFamily, "requested font family"),
        FallbackFontFamily = RequireString(text.FallbackFontFamily, "fallback font family"),
        FontWeight = text.FontWeight,
        FontSize = text.FontSize,
        FillColor = RequireString(text.FillColor, "text fill color"),
        OutlineColor = RequireString(text.OutlineColor, "text outline color"),
        OutlineWidth = text.OutlineWidth,
        ShadowOffsetX = text.ShadowOffsetX,
        ShadowOffsetY = text.ShadowOffsetY,
        ShadowBlur = text.ShadowBlur,
        ShadowOpacity = text.ShadowOpacity,
        Curve = text.Curve,
        LetterSpacing = text.LetterSpacing,
        LineHeight = text.LineHeight,
        Alignment = ParseDefined<TextAlignment>(text.Alignment, "text alignment"),
        FlipHorizontal = text.FlipHorizontal,
        FlipVertical = text.FlipVertical,
        Blur = RequireFinite(
            text.Blur, "text blur", min: 0, max: AppearanceLimits.MaxBlur),
        Blend = RequireBlend(text.Blend, "text blend"),
    };

    private static FilterSettings ToFilter(FilterSettingsDto filter) => new()
    {
        Brightness = filter.Brightness,
        Contrast = filter.Contrast,
        Saturation = filter.Saturation,
        HueDegrees = filter.HueDegrees,
        Temperature = filter.Temperature,
        TextureAssetSha256 = RequireOptionalSha256(
            filter.TextureAssetSha256, "filter texture asset hash"),
        TextureOpacity = filter.TextureOpacity,
        TextureScale = filter.TextureScale,
        GradientMap = filter.GradientMap is not { } map ? new() : new FilterGradientMap
        {
            ShadowColor = RequireFilterColor(map.Shadow, "filter gradient shadow colour"),
            MidtoneColor = RequireFilterColor(map.Midtone, "filter gradient midtone colour"),
            HighlightColor = RequireFilterColor(map.Highlight, "filter gradient highlight colour"),
            Strength = RequireFinite(map.Strength, "filter gradient strength", 0, 1),
        },
        PostProcess = filter.PostProcess is not { } post ? new() : new FilterPostProcess
        {
            Saturation = RequireFinite(post.Saturation, "filter saturation", -1, 1),
            Brightness = RequireFinite(post.Brightness, "filter brightness", -1, 1),
            Contrast = RequireFinite(post.Contrast, "filter contrast", -1, 1),
            Sharpness = RequireFinite(post.Sharpness, "filter sharpness", 0, 1),
        },
        ColorStrength = RequireFinite(filter.ColorStrength ?? 1, "filter colour strength", 0, 1),
        ColorPresetId = filter.ColorPresetId is { Length: > 0 and <= 64 } presetId ? presetId : null,
    };

    private static string RequireFilterColor(string value, string field) =>
        FilterColorEffects.IsColor(value)
            ? value
            : throw new MapProjectCorruptedException($"scene.json holds an invalid {field} \"{value}\".");

    private static EditableComposition ToComposition(
        EditableCompositionDto composition,
        bool allowUnknownObjectKinds = false) => new(
        CompositionId.Parse(RequireString(composition.Id, "composition id")),
        DocumentId.Parse(RequireString(composition.SourceDocumentId, "composition source document id")),
        RequireString(composition.Name, "composition name"),
        composition.SourceWidth, composition.SourceHeight,
        [.. composition.Items.Select(item => ToObject(item, allowUnknownObjectKinds))])
    {
        AnchorX = composition.AnchorX,
        AnchorY = composition.AnchorY,
        ReferenceGridScale = composition.ReferenceGridScale,
        Assets = [.. composition.Assets.Select(asset => new CompositionAssetProvenance(
            RequireSha256(asset.Sha256, "composition asset hash"),
            RequireString(asset.DisplayName, "composition asset name"),
            asset.Provenance ?? "", asset.License ?? ""))],
        IncludeBackground = composition.IncludeBackground,
        BackgroundColor = composition.BackgroundColor,
        IncludeGrids = composition.IncludeGrids,
        Grids = [.. composition.Grids.Select(ToGrid)],
        IncludeFilters = composition.IncludeFilters,
        Filters = [.. composition.Filters.Select(ToFilter)],
    };

    private static LineStyleDto ToLineDto(LineStyle line) => new()
    {
        Nodes = [.. line.Nodes.Select(ToNodeDto)],
        Stroke = ToStrokeDto(line.Stroke),
        Portals = [.. line.Portals.Select(ToPortalDto)],
        Closed = line.Closed,
    };

    private static ShapeStyleDto ToShapeDto(ShapeStyle shape) => new()
    {
        Regions = [.. shape.Regions.Select(region => new ShapeRegionDto
        {
            Id = region.Id.ToString(),
            Mode = region.Mode.ToString(),
            Primitive = region.Primitive.ToString(),
            Nodes = [.. region.Nodes.Select(ToNodeDto)],
        })],
        Fill = new ShapeFillDto
        {
            Color = shape.Fill.Color,
            Gradient = ToGradientDto(shape.Fill.Gradient),
            TextureAssetSha256 = RequireOptionalSha256(
                shape.Fill.TextureAssetSha256, "shape fill texture asset hash"),
            TextureScale = shape.Fill.TextureScale,
            TextureRotation = shape.Fill.TextureRotation,
            TextureOffsetX = shape.Fill.TextureOffsetX,
            TextureOffsetY = shape.Fill.TextureOffsetY,
            Opacity = shape.Fill.Opacity,
        },
        Stroke = ToStrokeDto(shape.Stroke),
        Portals = [.. shape.Portals.Select(ToPortalDto)],
    };

    private static VectorNodeDto ToNodeDto(VectorNode node) => new()
    {
        Id = node.Id.ToString(),
        X = node.X,
        Y = node.Y,
        Kind = node.Kind.ToString(),
        ControlInX = node.ControlInX,
        ControlInY = node.ControlInY,
        ControlOutX = node.ControlOutX,
        ControlOutY = node.ControlOutY,
    };

    private static LinePortalDto ToPortalDto(LinePortal portal) => new()
    {
        Id = portal.Id.ToString(),
        Position = portal.Position,
        Width = portal.Width,
    };

    private static VectorStrokeDto ToStrokeDto(VectorStrokeStyle stroke) => new()
    {
        Color = stroke.Color,
        Width = stroke.Width,
        Opacity = stroke.Opacity,
        Blend = stroke.Blend,
        Blur = stroke.Blur,
        Pattern = stroke.Pattern.ToString(),
        Offset = stroke.Offset,
        EndCap = stroke.EndCap.ToString(),
        NodeCap = stroke.NodeCap.ToString(),
        ShadowOffsetX = stroke.ShadowOffsetX,
        ShadowOffsetY = stroke.ShadowOffsetY,
        ShadowBlur = stroke.ShadowBlur,
        ShadowOpacity = stroke.ShadowOpacity,
        AssetSha256 = RequireOptionalSha256(stroke.AssetSha256, "vector stroke asset hash"),
        AssetSpacing = stroke.AssetSpacing,
        Hue = stroke.Hue,
        Saturation = stroke.Saturation,
        BlocksLight = stroke.BlocksLight,
    };

    private static LineStyle ToLine(LineStyleDto line) => new(
        [.. line.Nodes.Select(ToNode)], ToStroke(line.Stroke),
        [.. line.Portals.Select(ToPortal)], line.Closed);

    private static ShapeStyle ToShape(ShapeStyleDto shape) => new(
        [.. shape.Regions.Select(region => new ShapeRegion(
            ShapeRegionId.Parse(RequireString(region.Id, "shape region id")),
            ParseDefined<ShapeCombineMode>(region.Mode, "shape combine mode"),
            ParseDefined<ShapePrimitiveKind>(region.Primitive, "shape primitive"),
            [.. region.Nodes.Select(ToNode)]))],
        new ShapeFillStyle
        {
            Color = RequireString(shape.Fill.Color, "shape fill color"),
            Gradient = ToGradient(shape.Fill.Gradient, "shape fill gradient"),
            TextureAssetSha256 = RequireOptionalSha256(
                shape.Fill.TextureAssetSha256, "shape fill texture asset hash"),
            TextureScale = shape.Fill.TextureScale,
            TextureRotation = shape.Fill.TextureRotation,
            TextureOffsetX = shape.Fill.TextureOffsetX,
            TextureOffsetY = shape.Fill.TextureOffsetY,
            Opacity = shape.Fill.Opacity,
        },
        ToStroke(shape.Stroke), [.. shape.Portals.Select(ToPortal)]);

    private static VectorNode ToNode(VectorNodeDto node) => new(
        VectorNodeId.Parse(RequireString(node.Id, "vector node id")), node.X, node.Y,
        ParseDefined<VectorNodeKind>(node.Kind, "vector node kind"),
        node.ControlInX, node.ControlInY, node.ControlOutX, node.ControlOutY);

    private static LinePortal ToPortal(LinePortalDto portal) => new(
        PortalId.Parse(RequireString(portal.Id, "portal id")), portal.Position, portal.Width);

    private static VectorStrokeStyle ToStroke(VectorStrokeDto stroke) => new()
    {
        Color = RequireString(stroke.Color, "vector stroke color"),
        Width = stroke.Width,
        Opacity = stroke.Opacity,
        Blend = RequireBlend(stroke.Blend, "vector stroke blend"),
        Blur = RequireFinite(
            stroke.Blur, "vector stroke blur", min: 0, max: AppearanceLimits.MaxBlur),
        Pattern = ParseDefined<StrokePattern>(stroke.Pattern, "stroke pattern"),
        Offset = stroke.Offset,
        EndCap = ParseDefined<VectorCap>(stroke.EndCap, "line end cap"),
        NodeCap = ParseDefined<VectorCap>(stroke.NodeCap, "line node cap"),
        ShadowOffsetX = stroke.ShadowOffsetX,
        ShadowOffsetY = stroke.ShadowOffsetY,
        ShadowBlur = stroke.ShadowBlur,
        ShadowOpacity = stroke.ShadowOpacity,
        AssetSha256 = RequireOptionalSha256(stroke.AssetSha256, "vector stroke asset hash"),
        AssetSpacing = stroke.AssetSpacing,
        Hue = stroke.Hue,
        Saturation = stroke.Saturation,
        BlocksLight = stroke.BlocksLight,
    };

    private static bool HasInvalidLayerDto(LayerDto? layer) => layer is null
        || layer.Tiles?.Any(static tile => tile is null) == true
        || layer.LandPaintTiles?.Any(static tile => tile is null) == true
        || layer.Mask is { Tiles: null }
        || layer.Mask?.Tiles.Any(static tile => tile is null) == true
        || layer.Grids is null && layer.Type == "Grid"
        || layer.Grids?.Any(static grid => grid is null) == true
        || layer.Filter is null && layer.Type == "Filter"
        || layer.Objects?.Any(HasInvalidVectorDto) == true
        || layer.ObjectChunks?.Any(static chunk => chunk is null) == true;

    private static bool HasInvalidVectorDto(SceneObjectDto? item)
    {
        if (item is null)
        {
            return true;
        }

        if (item.Line is { } line
            && (line.Nodes is null
                || line.Stroke is null
                || line.Portals is null
                || line.Nodes.Any(static node => node is null)
                || line.Portals.Any(static portal => portal is null)))
        {
            return true;
        }

        if (item.Shape is { } shape
            && (shape.Regions is null
                || shape.Fill is null
                || shape.Stroke is null
                || shape.Portals is null
                || shape.Regions.Any(region =>
                    region is null
                    || region.Nodes is null
                    || region.Nodes.Any(static node => node is null))
                || shape.Portals.Any(static portal => portal is null)))
        {
            return true;
        }

        return item.Composition is { } composition
            && (composition.Items is null
                || composition.Assets is null
                || composition.Grids is null
                || composition.Filters is null
                || composition.Items.Any(HasInvalidVectorDto)
                || composition.Assets.Any(static asset => asset is null)
                || composition.Grids.Any(static grid => grid is null)
                || composition.Filters.Any(static filter => filter is null));
    }

    private static List<TileDto> ToTileDtos(TileMap tiles) =>
        [.. tiles.Keys.OrderBy(k => k.Y).ThenBy(k => k.X).Select(key => new TileDto
        {
            X = key.X,
            Y = key.Y,
            Sha256 = Sha256Hex(tiles.TryGet(key)!),
        })];

    private static LandStyleDto ToLandStyleDto(LandEdgeStyle style) => new()
    {
        FillColor = style.FillColor,
        OutlineColor = style.OutlineColor,
        OutlineWidth = style.OutlineWidth,
        InnerShadowOpacity = style.InnerShadowOpacity,
        OuterShadowOpacity = style.OuterShadowOpacity,
        ShadowOffsetX = style.ShadowOffsetX,
        ShadowOffsetY = style.ShadowOffsetY,
        ShadowBlur = style.ShadowBlur,
        RippleColor = style.RippleColor,
        RippleWidth = style.RippleWidth,
        RippleSpacing = style.RippleSpacing,
        RippleCount = style.RippleCount,
        // Written only when a ground material is actually chosen, so a map without one keeps the
        // exact bytes it had before materials existed.
        FillTextureId = style.FillTextureId,
        FillTextureOpacity = style.FillTextureId is null ? null : style.FillTextureOpacity,
        FillTextureScale = style.FillTextureId is null ? null : style.FillTextureScale,
    };

    private static LandEdgeStyle ToLandStyle(LandStyleDto? style)
    {
        if (style is null)
        {
            throw new MapProjectCorruptedException("A land layer has no landStyle object.");
        }

        return new LandEdgeStyle
        {
            FillColor = RequireString(style.FillColor, "land fill color"),
            OutlineColor = RequireString(style.OutlineColor, "land outline color"),
            OutlineWidth = style.OutlineWidth,
            InnerShadowOpacity = style.InnerShadowOpacity,
            OuterShadowOpacity = style.OuterShadowOpacity,
            ShadowOffsetX = RequireFinite(style.ShadowOffsetX, "land shadow offset X", -512, 512),
            ShadowOffsetY = RequireFinite(style.ShadowOffsetY, "land shadow offset Y", -512, 512),
            ShadowBlur = style.ShadowBlur,
            RippleColor = RequireString(style.RippleColor, "land ripple color"),
            RippleWidth = style.RippleWidth,
            RippleSpacing = style.RippleSpacing,
            RippleCount = style.RippleCount,
            // An unknown material simply falls back to a plain fill: a file from a newer Map, or
            // one edited by hand, still opens and still looks like a map.
            FillTextureId = MapFillTextures.Find(style.FillTextureId)?.Id,
            FillTextureOpacity = style.FillTextureOpacity is { } textureOpacity && MapFillTextures.IsValidOpacity(textureOpacity)
                ? textureOpacity : new LandEdgeStyle().FillTextureOpacity,
            FillTextureScale = style.FillTextureScale is { } textureScale && MapFillTextures.IsValidScale(textureScale)
                ? textureScale : new LandEdgeStyle().FillTextureScale,
        };
    }

    private static IEnumerable<(string Name, byte[] Bytes)> EnumerateTileEntries(
        MapDocument document, string entryRoot = "")
    {
        foreach (var layer in document.Layers)
        {
            if (layer is IRasterContentLayer raster)
            {
                foreach (var key in raster.ContentTiles.Keys)
                {
                    yield return (
                        TileEntryName(entryRoot + MapProjectFormat.RasterPrefix, layer.Id.ToString(), key.X, key.Y),
                        raster.ContentTiles.TryGet(key)!);
                }
            }

            if (layer.Mask is { } mask)
            {
                foreach (var key in mask.Tiles.Keys)
                {
                    yield return (
                        TileEntryName(entryRoot + MapProjectFormat.MaskPrefix, layer.Id.ToString(), key.X, key.Y),
                        mask.Tiles.TryGet(key)!);
                }
            }
            if (layer is LandLayer paintedLand)
            {
                foreach (var key in paintedLand.PaintTiles.Keys)
                    yield return (TileEntryName(entryRoot + MapProjectFormat.LandPaintPrefix,
                        layer.Id.ToString(), key.X, key.Y), paintedLand.PaintTiles.TryGet(key)!);
            }
        }

        if (document.StampForeground is { } foreground)
        {
            foreach (var key in foreground.Tiles.Keys)
            {
                yield return (
                    TileEntryName(
                        entryRoot + MapProjectFormat.RasterPrefix,
                        "foreground",
                        key.X,
                        key.Y),
                    foreground.Tiles.TryGet(key)!);
            }
        }
    }

    private static IEnumerable<(string Name, byte[] Bytes)> EnumerateAssetEntries(
        MapDocument document, string entryRoot = "")
    {
        foreach (var asset in document.Assets)
        {
            if (asset.Storage == ProjectAssetStorage.Embedded)
            {
                yield return (
                    entryRoot + AssetContentEntry(asset.Sha256, asset.Extension),
                    asset.EmbeddedContent
                        ?? throw new InvalidOperationException($"Embedded asset {asset.Sha256} has no bytes."));
            }

            if (asset.FallbackThumbnailPng is { } thumbnail)
            {
                yield return (entryRoot + AssetThumbnailEntry(asset.Sha256), thumbnail);
            }
        }
    }

    private static MapAssetReference ToAsset(
        AssetEntryDto asset, IReadOnlyDictionary<string, byte[]> entries)
    {
        var hash = RequireSha256(asset.Sha256, "asset sha256");
        if (!Enum.TryParse<ProjectAssetStorage>(asset.Storage, out var storage)
            || !Enum.IsDefined(storage))
        {
            throw new MapProjectCorruptedException(
                $"Asset {hash} has unsupported storage mode \"{asset.Storage}\".");
        }

        if (asset.RelativePath is not null
            && !ProjectLinkedAssetPath.IsSafeRelative(asset.RelativePath))
        {
            throw new MapProjectCorruptedException(
                $"Asset {hash} has a linked path that is not a safe project-relative path.");
        }

        byte[]? embeddedContent = null;
        if (storage == ProjectAssetStorage.Embedded)
        {
            if (asset.ContentEntry is not { } content || !entries.TryGetValue(content, out embeddedContent))
            {
                throw new MapProjectCorruptedException(
                    $"Embedded asset {hash} has no validated content payload.");
            }
        }
        else if (asset.ContentEntry is not null)
        {
            throw new MapProjectCorruptedException(
                $"Linked asset {hash} must not carry embedded content.");
        }

        byte[]? fallbackThumbnail = null;
        if (asset.ThumbnailEntry is { } thumbnail
            && !entries.TryGetValue(thumbnail, out fallbackThumbnail))
        {
            throw new MapProjectCorruptedException(
                $"Asset {hash} has no validated fallback thumbnail payload.");
        }

        return MapAssetReference.FromValidatedPayloads(
            hash,
            RequireString(asset.DisplayName, "asset display name"),
            RequireString(asset.MediaType, "asset media type"),
            RequireExtension(asset.Extension),
            storage,
            ParseTimestamp(asset.ImportedUtc, "asset importedUtc"),
            RequireString(asset.Provenance, "asset provenance"),
            RequireString(asset.License, "asset license"),
            embeddedContent: embeddedContent,
            catalogId: asset.CatalogId,
            relativePath: asset.RelativePath,
            fallbackThumbnailPng: fallbackThumbnail);
    }

    private static TileMap ToTileMap(
        IEnumerable<TileDto> tiles,
        string prefix,
        LayerId layerId,
        int bytesPerPixel,
        IReadOnlyDictionary<string, byte[]> entries) =>
        ToTileMap(tiles, prefix, layerId.ToString(), bytesPerPixel, entries);

    private static TileMap ToTileMap(
        IEnumerable<TileDto> tiles,
        string prefix,
        string ownerId,
        int bytesPerPixel,
        IReadOnlyDictionary<string, byte[]> entries)
    {
        var map = TileMap.Empty(bytesPerPixel);
        return map.WithMany(tiles.Select(tile => new KeyValuePair<TileKey, byte[]?>(
            new TileKey(tile.X, tile.Y),
            entries[TileEntryName(prefix, ownerId, tile.X, tile.Y)])));
    }

    private static string TileEntryName(string prefix, string layerId, int x, int y) =>
        $"{prefix}{layerId}/{x}_{y}.bin";

    private static string CheckpointRoot(CheckpointId id) =>
        $"{MapProjectFormat.CheckpointPrefix}{id}/";

    private static string AssetContentEntry(string sha256, string extension) =>
        $"{MapProjectFormat.AssetPrefix}{sha256}.{RequireExtension(extension)}";

    private static string AssetThumbnailEntry(string sha256) =>
        $"{MapProjectFormat.AssetPrefix}thumbnails/{sha256}.png";

    private static string RequireExtension(string? extension)
    {
        var value = RequireString(extension, "asset extension").TrimStart('.').ToLowerInvariant();
        return value is "png" or "jpg" or "jpeg" or "webp"
            ? value
            : throw new MapProjectCorruptedException($"Asset extension \"{extension}\" is not supported.");
    }

    private static string RequireSha256(string? value, string what)
    {
        var hash = RequireString(value, what).ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new MapProjectCorruptedException($"The container has an invalid {what}.");
        }

        return hash;
    }

    private static string? RequireOptionalSha256(string? value, string what) =>
        value is null ? null : RequireSha256(value, what);

    private static bool RequireClipMaskEnabled(bool? enabled, string? maskSha256, string? visualSha256)
    {
        if (maskSha256 is not null
            && string.Equals(maskSha256, visualSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new MapProjectCorruptedException(
                "scene.json uses the stamp visual content as its own clip mask.");
        }
        if (enabled is true && maskSha256 is null)
        {
            throw new MapProjectCorruptedException(
                "scene.json enables a stamp clip mask without a mask asset identity.");
        }
        return enabled ?? false;
    }

    private static string RequireCanonicalLayerId(string? value)
    {
        try
        {
            return LayerId.Parse(RequireString(value, "layer id")).ToString();
        }
        catch (FormatException ex)
        {
            throw new MapProjectCorruptedException("scene.json has an invalid layer id.", ex);
        }
    }

    private static int ObjectCount(MapLayer layer) =>
        layer is ObjectLayer objectLayer ? objectLayer.Objects.Count : 0;

    private static int TileCount(MapLayer layer) =>
        (layer is IRasterContentLayer raster ? raster.ContentTiles.TileCount : 0)
        + (layer is LandLayer land ? land.PaintTiles.TileCount : 0);

    private static LayerId LegacyBaseLayerId(string documentId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("map/base-color/" + documentId));
        return new LayerId(new Guid(hash.AsSpan(0, 16)));
    }

    private static string RequireString(string? value, string what) =>
        value ?? throw new MapProjectCorruptedException($"The container has a null {what}.");

    /// <summary>Object-style blend keys come from the one supported list, exactly.</summary>
    private static string RequireBlend(string? value, string what) =>
        BlendModes.IsSupported(value)
            ? value!
            : throw new MapProjectCorruptedException(
                $"scene.json holds {what} \"{value}\" this version does not define.");

    private static double RequireFinite(
        double value, string what, double min = double.MinValue, double max = double.MaxValue)
    {
        if (!double.IsFinite(value) || value < min || value > max)
        {
            throw new MapProjectCorruptedException(
                $"scene.json holds an out-of-range {what} value \"{value}\".");
        }

        return value;
    }

    /// <summary>
    /// A gradient is written only when the object has one, so every project without gradients
    /// keeps byte-identical scene content.
    /// </summary>
    private static GradientDto? ToGradientDto(GradientFill? gradient) => gradient is null ? null : new GradientDto
    {
        Kind = gradient.Kind.ToString(),
        Stops = [.. gradient.Stops.Select(stop => new GradientStopDto
        {
            Offset = stop.Offset,
            Color = stop.Color,
        })],
        AngleDegrees = gradient.AngleDegrees,
        CenterX = gradient.CenterX,
        CenterY = gradient.CenterY,
        Radius = gradient.Radius,
    };

    /// <summary>
    /// Every stored gradient value is checked here rather than trusted: an absent gradient is a
    /// valid document, but a present one with a bad stop count, offset or colour is corruption and
    /// is reported as such instead of reaching the renderer.
    /// </summary>
    private static GradientFill? ToGradient(GradientDto? dto, string what)
    {
        if (dto is null)
        {
            return null;
        }

        if (dto.Stops is not { } stops
            || stops.Count is < GradientFill.MinStops or > GradientFill.MaxStops)
        {
            throw new MapProjectCorruptedException(
                $"scene.json holds a {what} with {dto.Stops?.Count ?? 0} colour stops.");
        }

        return new GradientFill(
            ParseDefined<GradientKind>(dto.Kind, what + " kind"),
            [.. stops.Select(stop => new GradientStopStyle(
                RequireFinite(stop.Offset, what + " stop offset", min: 0, max: 1),
                RequireAppearanceColor(stop.Color, what + " stop colour")))],
            RequireFinite(dto.AngleDegrees, what + " angle", min: -360, max: 360),
            RequireFinite(dto.CenterX, what + " centre x", min: 0, max: 1),
            RequireFinite(dto.CenterY, what + " centre y", min: 0, max: 1),
            RequireFinite(dto.Radius, what + " radius", min: double.Epsilon, max: 4));
    }

    private static string RequireAppearanceColor(string? value, string what)
    {
        var normalized = AppearanceColor.Normalize(value);
        if (normalized is null)
        {
            throw new MapProjectCorruptedException(
                $"scene.json holds invalid {what} \"{value}\".");
        }

        return normalized;
    }

    /// <summary>An optional positive size: absent stays absent; present must be a real size.</summary>
    private static double? RequirePositiveOrNull(double? value, string what) =>
        value is null
            ? null
            : RequireFinite(value.Value, what, min: double.Epsilon);

    /// <summary>
    /// Strict kind parse: only names this version defines. TryParse alone would accept numeric
    /// strings outside the enum (the Stage 1 Fix 02 lesson applied to scene content).
    /// </summary>
    private static bool IsDefinedKind(string? kind) =>
        kind is not null
        && Enum.TryParse<SceneObjectKind>(kind, ignoreCase: false, out var parsed)
        && Enum.IsDefined(parsed);

    private static SceneObjectKind ParseKind(string? kind, bool allowUnknownObjectKinds = false)
    {
        if (IsDefinedKind(kind))
        {
            return Enum.Parse<SceneObjectKind>(kind!, ignoreCase: false);
        }

        if (allowUnknownObjectKinds && !string.IsNullOrWhiteSpace(kind))
        {
            return SceneObjectKind.Placeholder;
        }

        throw new MapProjectCorruptedException(
            $"scene.json holds an object kind \"{kind}\" this version does not define.");
    }

    private static T ParseDefined<T>(string? value, string what) where T : struct, Enum
    {
        if (value is not null && Enum.TryParse<T>(value, ignoreCase: false, out var parsed)
            && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new MapProjectCorruptedException(
            $"scene.json holds {what} \"{value}\" this version does not define.");
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string? value, string fieldName)
    {
        if (!DateTimeOffset.TryParseExact(
                value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            throw new MapProjectCorruptedException($"Manifest field {fieldName} is not a valid timestamp.");
        }

        return parsed;
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
