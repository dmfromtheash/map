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
/// Reads and writes .mapproj v1. Save follows the docs/05 contract: serialize the immutable
/// snapshot to a unique sibling temp, read the temp back and validate entries and checksums,
/// flush, and only then replace the target atomically. A failure before the replace leaves the
/// last correct version untouched and is reported as an exception, never as success.
/// </summary>
public static class MapProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Save(
        MapDocument document,
        string targetPath,
        SaveMetadata metadata,
        byte[] previewPng,
        RecoveryJournal? journal = null,
        SaveFaultInjection? faults = null)
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
                var baseHash = File.Exists(fullTarget) ? TryFingerprint(fullTarget) : "";
                record = journal.Begin(
                    document.Id.Value, fullTarget, tempPath, metadata.SavedRevision, baseHash);
            }

            using (var stream = new FileStream(
                       tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.None))
            {
                WriteContainer(stream, document, metadata, previewPng);
                stream.Flush(flushToDisk: true);
            }

            // A save that cannot be read back by our own reader is a failed save, not a success.
            ValidateReadBack(tempPath, document);

            // The temp is now durable and proven: from this point a crash leaves a recovery
            // candidate the next launch can find.
            if (journal is not null && record is not null)
            {
                journal.Advance(document.Id.Value, record, RecoveryState.TempDurable);
            }

            faults?.AfterDurableTempBeforeReplace?.Invoke();

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

            if (journal is not null && record is not null)
            {
                journal.Advance(document.Id.Value, record, RecoveryState.Replaced);
                journal.Clear(document.Id.Value);
            }
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

    public static OpenedProject Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var (manifest, scene, assetManifest, previewPng, tiles, assets) =
            ReadAndValidate(Path.GetFullPath(path));

        return new OpenedProject
        {
            Document = ToDomain(manifest, scene, assetManifest, tiles, assets),
            AppVersion = manifest.AppVersion,
            ModifiedUtc = ParseTimestamp(manifest.ModifiedUtc, "modifiedUtc"),
            PreviewPng = previewPng,
        };
    }

    // ---------------------------------------------------------------- write path

    private static void WriteContainer(
        Stream destination, MapDocument document, SaveMetadata metadata, byte[] previewPng)
    {
        var sceneBytes = JsonSerializer.SerializeToUtf8Bytes(ToSceneDto(document), JsonOptions);
        var assetManifestBytes = JsonSerializer.SerializeToUtf8Bytes(ToAssetManifestDto(document), JsonOptions);

        var manifest = new ManifestDto
        {
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
        };

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntry(archive, MapProjectFormat.ManifestEntry, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
        WriteEntry(archive, MapProjectFormat.SceneEntry, sceneBytes);
        WriteEntry(archive, MapProjectFormat.AssetManifestEntry, assetManifestBytes);
        WriteEntry(archive, MapProjectFormat.PreviewEntry, previewPng);
        foreach (var (name, bytes) in EnumerateTileEntries(document).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            WriteEntry(archive, name, bytes);
        }
        foreach (var (name, bytes) in EnumerateAssetEntries(document).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            WriteEntry(archive, name, bytes);
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] data)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = MapProjectFormat.DeterministicEntryTime;
        using var stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }

    /// <summary>
    /// Runs the temp through the full read path and confirms it holds the snapshot that was asked
    /// for. Identity and shape are enough here: content equality is already guaranteed by the
    /// checksums the read path verifies.
    /// </summary>
    private static void ValidateReadBack(string tempPath, MapDocument expected)
    {
        var (manifest, scene, assetManifest, _, tiles, assets) = ReadAndValidate(tempPath);
        var reloaded = ToDomain(manifest, scene, assetManifest, tiles, assets);

        if (reloaded.Id != expected.Id)
        {
            throw new MapProjectCorruptedException("Read-back document id does not match the saved snapshot.");
        }

        if (reloaded.Layers.Count != expected.Layers.Count
            || reloaded.Layers.Zip(expected.Layers).Any(p =>
                p.First.GetType() != p.Second.GetType()
                || ObjectCount(p.First) != ObjectCount(p.Second)
                || TileCount(p.First) != TileCount(p.Second)
                || (p.First.Mask?.Tiles.TileCount ?? 0) != (p.Second.Mask?.Tiles.TileCount ?? 0))
            || reloaded.Assets.Count != expected.Assets.Count)
        {
            throw new MapProjectCorruptedException("Read-back layer/object shape does not match the saved snapshot.");
        }
    }

    // ---------------------------------------------------------------- read path

    private static (
        ManifestDto Manifest,
        SceneDto Scene,
        AssetManifestDto AssetManifest,
        byte[] PreviewPng,
        IReadOnlyDictionary<string, byte[]> Tiles,
        IReadOnlyDictionary<string, byte[]> Assets) ReadAndValidate(string fullPath)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);

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
            ValidateContainerShape(archive);

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

            if (manifest.SchemaVersion != MapProjectFormat.SchemaVersion)
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
            if (!string.Equals(scene.DocumentId, manifest.DocumentId, StringComparison.OrdinalIgnoreCase))
            {
                throw new MapProjectCorruptedException(
                    "manifest.json and scene.json disagree about the document id.");
            }

            var assetManifest = ParseJson<AssetManifestDto>(
                assetManifestBytes, MapProjectFormat.AssetManifestEntry);

            var tiles = ReadAndValidateTiles(archive, scene);
            var assets = ReadAndValidateAssets(archive, assetManifest);
            return (
                manifest,
                scene,
                assetManifest,
                ReadEntry(archive, MapProjectFormat.PreviewEntry),
                tiles,
                assets);
        }
    }

    /// <summary>
    /// Structural sanity of the archive before any content is read. This is resilience against a
    /// truncated, mis-copied or mislabeled file - the reader must fail with a clear error instead
    /// of hanging on decompression or writing outside the container (docs/05 read limits).
    /// </summary>
    private static void ValidateContainerShape(ZipArchive archive)
    {
        if (archive.Entries.Count > MapProjectFormat.MaxEntries)
        {
            throw new MapProjectCorruptedException(
                $"The container holds {archive.Entries.Count} entries; " +
                $"a map project has at most {MapProjectFormat.MaxEntries}.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalUncompressed = 0;

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

            totalUncompressed += entry.Length;

            var perEntryLimit = name == MapProjectFormat.PreviewEntry
                ? MapProjectFormat.MaxPreviewBytes
                : name.StartsWith(MapProjectFormat.RasterPrefix, StringComparison.Ordinal)
                    || name.StartsWith(MapProjectFormat.MaskPrefix, StringComparison.Ordinal)
                    ? MapProjectFormat.MaxTileEntryBytes
                    : name.StartsWith(MapProjectFormat.AssetPrefix, StringComparison.Ordinal)
                        ? MapProjectFormat.MaxAssetEntryBytes
                    : MapProjectFormat.MaxJsonEntryBytes;

            if (entry.Length > perEntryLimit)
            {
                throw new MapProjectCorruptedException(
                    $"Entry {name} declares {entry.Length} uncompressed bytes, above the {perEntryLimit} limit.");
            }
        }

        if (totalUncompressed > MapProjectFormat.MaxTotalUncompressedBytes)
        {
            throw new MapProjectCorruptedException(
                $"The container declares {totalUncompressed} uncompressed bytes in total, " +
                $"above the {MapProjectFormat.MaxTotalUncompressedBytes} limit.");
        }
    }

    /// <summary>
    /// Checks the stored preview really is a PNG with sane dimensions - from the signature and
    /// IHDR header only, without decoding pixels (persistence has no imaging dependency).
    /// </summary>
    private static void ValidatePreviewHeader(ZipArchive archive)
    {
        var preview = ReadEntry(archive, MapProjectFormat.PreviewEntry);

        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (preview.Length < 24 || !preview.AsSpan(0, 8).SequenceEqual(pngSignature))
        {
            throw new MapProjectCorruptedException(
                $"{MapProjectFormat.PreviewEntry} is not a PNG image.");
        }

        // Bytes 16..23 are the IHDR width and height, big-endian.
        var width = BinaryPrimitives.ReadUInt32BigEndian(preview.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(preview.AsSpan(20, 4));

        if (width is 0 or > MapProjectFormat.MaxPreviewDimension
            || height is 0 or > MapProjectFormat.MaxPreviewDimension)
        {
            throw new MapProjectCorruptedException(
                $"{MapProjectFormat.PreviewEntry} declares {width}x{height}, outside the supported preview size.");
        }
    }

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        try
        {
            using var stream = archive.GetEntry(name)!.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new MapProjectCorruptedException($"Entry {name} cannot be decompressed.", ex);
        }
    }

    private static T ParseJson<T>(byte[] bytes, string entryName)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new MapProjectCorruptedException($"{entryName} is empty.");
        }
        catch (JsonException ex)
        {
            throw new MapProjectCorruptedException($"{entryName} is not valid JSON.", ex);
        }
    }

    private static IReadOnlyDictionary<string, byte[]> ReadAndValidateTiles(
        ZipArchive archive, SceneDto scene)
    {
        if (scene.Layers is null
            || scene.Layers.Any(layer => layer is null || layer.Objects is null || layer.Mask is { Tiles: null }))
        {
            throw new MapProjectCorruptedException(
                "scene.json has null layers, layer, objects array or mask tiles array.");
        }

        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var layer in scene.Layers)
        {
            var layerId = RequireCanonicalLayerId(layer.Id);
            ReadTileList(
                layer.Tiles,
                MapProjectFormat.RasterPrefix,
                layerId,
                bytesPerPixel: layer.Type == "Land" ? 1 : 4);
            ReadTileList(layer.Mask?.Tiles, MapProjectFormat.MaskPrefix, layerId, bytesPerPixel: 1);
        }

        foreach (var entry in archive.Entries)
        {
            if ((entry.FullName.StartsWith(MapProjectFormat.RasterPrefix, StringComparison.Ordinal)
                    || entry.FullName.StartsWith(MapProjectFormat.MaskPrefix, StringComparison.Ordinal))
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
        ZipArchive archive, AssetManifestDto manifest)
    {
        if (manifest.Assets is null || manifest.Assets.Any(asset => asset is null))
        {
            throw new MapProjectCorruptedException("asset-manifest.json has a null assets array or entry.");
        }

        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in manifest.Assets)
        {
            var hash = RequireSha256(asset.Sha256, "asset sha256");
            if (!hashes.Add(hash))
            {
                throw new MapProjectCorruptedException($"Asset {hash} occurs more than once.");
            }

            if (asset.Storage == nameof(ProjectAssetStorage.Embedded))
            {
                var expected = AssetContentEntry(hash, asset.Extension);
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
            }
            else if (asset.Storage != nameof(ProjectAssetStorage.Linked))
            {
                throw new MapProjectCorruptedException(
                    $"Asset {hash} has unsupported storage mode \"{asset.Storage}\".");
            }

            if (asset.ThumbnailEntry is { } thumbnailEntry)
            {
                var expected = AssetThumbnailEntry(hash);
                if (!string.Equals(thumbnailEntry, expected, StringComparison.Ordinal))
                {
                    throw new MapProjectCorruptedException(
                        $"Asset {hash} has a non-canonical thumbnail entry.");
                }

                ReadAssetEntry(expected, hash);
                if (!string.Equals(
                        Sha256Hex(result[expected]), asset.ThumbnailSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new MapProjectCorruptedException(
                        $"Asset {hash} fallback thumbnail does not match its checksum.");
                }
            }
        }

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.StartsWith(MapProjectFormat.AssetPrefix, StringComparison.Ordinal)
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

    // ---------------------------------------------------------------- mapping

    private static AssetManifestDto ToAssetManifestDto(MapDocument document) => new()
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
                ? AssetContentEntry(asset.Sha256, asset.Extension)
                : null,
            ThumbnailEntry = asset.FallbackThumbnailPng is null
                ? null
                : AssetThumbnailEntry(asset.Sha256),
            ThumbnailSha256 = asset.FallbackThumbnailPng is null
                ? null
                : Sha256Hex(asset.FallbackThumbnailPng),
        })],
    };

    private static SceneDto ToSceneDto(MapDocument document) => new()
    {
        DocumentId = document.Id.ToString(),
        Layers = [.. document.Layers.Select(ToLayerDto)],
        Notes = [.. document.Notes.Select(ToNoteDto)],
    };

    private static LayerDto ToLayerDto(MapLayer layer)
    {
        var dto = new LayerDto
        {
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
            Objects = layer is ObjectLayer objectLayer
                ? [.. objectLayer.Objects.Select(ToObjectDto)]
                : [],
            Tiles = layer is IRasterContentLayer raster
                ? ToTileDtos(raster.ContentTiles)
                : null,
            Mask = layer.Mask is { } mask
                ? new MaskDto { Enabled = mask.Enabled, Tiles = ToTileDtos(mask.Tiles) }
                : null,
            LandStyle = layer is LandLayer land ? ToLandStyleDto(land.EdgeStyle) : null,
            Grids = layer is GridLayer gridLayer
                ? [.. gridLayer.Grids.Select(ToGridDto)]
                : null,
            Filter = layer is FilterLayer filterLayer ? ToFilterDto(filterLayer.Settings) : null,
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
        StrokeColor = sceneObject.StrokeColor,
        StrokeWidth = sceneObject.StrokeWidth,
        Visible = sceneObject.Visible,
        Locked = sceneObject.Locked,
        Sublayer = sceneObject.Sublayer,
        GroupId = sceneObject.GroupId?.ToString("D"),
        ObjectOpacity = sceneObject.ObjectOpacity,
        Stamp = sceneObject.Stamp is { } stamp
            ? new StampStyleDto
            {
                AssetSha256 = stamp.AssetSha256,
                FlipHorizontal = stamp.FlipHorizontal,
                FlipVertical = stamp.FlipVertical,
                Blend = stamp.Blend,
                Blur = stamp.Blur,
                ShadowOffsetX = stamp.ShadowOffsetX,
                ShadowOffsetY = stamp.ShadowOffsetY,
                ShadowBlur = stamp.ShadowBlur,
                ShadowOpacity = stamp.ShadowOpacity,
                Hue = stamp.Hue,
                Saturation = stamp.Saturation,
                Brightness = stamp.Brightness,
                Contrast = stamp.Contrast,
            }
            : null,
        Line = sceneObject.Line is { } line ? ToLineDto(line) : null,
        Shape = sceneObject.Shape is { } shape ? ToShapeDto(shape) : null,
        Text = sceneObject.Text is { } text ? ToTextDto(text) : null,
        Composition = sceneObject.Composition is { } composition ? ToCompositionDto(composition) : null,
        PlaceholderReason = sceneObject.PlaceholderReason,
    };

    private static NoteDto ToNoteDto(MapNote note) => new()
    {
        Id = note.Id.ToString(), Title = note.Title, Markdown = note.Markdown,
        AnchorKind = note.AnchorKind.ToString(), WorldX = note.WorldX, WorldY = note.WorldY,
        ObjectId = note.ObjectId?.ToString(),
    };

    private static GridDefinitionDto ToGridDto(GridDefinition grid) => new()
    {
        Id = grid.Id.ToString(), Geometry = grid.Geometry.ToString(), CellSize = grid.CellSize,
        Subdivisions = grid.Subdivisions, LineWidth = grid.LineWidth, Color = grid.Color,
        Opacity = grid.Opacity, OffsetX = grid.OffsetX, OffsetY = grid.OffsetY,
        RotationDegrees = grid.RotationDegrees, SnapEnabled = grid.SnapEnabled,
        IncludeInExport = grid.IncludeInExport,
    };

    private static TextStyleDto ToTextDto(TextStyle text) => new()
    {
        Content = text.Content, RequestedFontFamily = text.RequestedFontFamily,
        FallbackFontFamily = text.FallbackFontFamily, FontWeight = text.FontWeight,
        FontSize = text.FontSize, FillColor = text.FillColor, OutlineColor = text.OutlineColor,
        OutlineWidth = text.OutlineWidth, ShadowOffsetX = text.ShadowOffsetX,
        ShadowOffsetY = text.ShadowOffsetY, ShadowBlur = text.ShadowBlur,
        ShadowOpacity = text.ShadowOpacity, Curve = text.Curve,
        LetterSpacing = text.LetterSpacing, LineHeight = text.LineHeight,
        Alignment = text.Alignment.ToString(), FlipHorizontal = text.FlipHorizontal,
        FlipVertical = text.FlipVertical, Blur = text.Blur, Blend = text.Blend,
    };

    private static FilterSettingsDto ToFilterDto(FilterSettings filter) => new()
    {
        Brightness = filter.Brightness, Contrast = filter.Contrast,
        Saturation = filter.Saturation, HueDegrees = filter.HueDegrees,
        Temperature = filter.Temperature, TextureAssetSha256 = filter.TextureAssetSha256,
        TextureOpacity = filter.TextureOpacity, TextureScale = filter.TextureScale,
    };

    private static EditableCompositionDto ToCompositionDto(EditableComposition composition) => new()
    {
        Id = composition.Id.ToString(), SourceDocumentId = composition.SourceDocumentId.ToString(),
        Name = composition.Name, SourceWidth = composition.SourceWidth, SourceHeight = composition.SourceHeight,
        AnchorX = composition.AnchorX, AnchorY = composition.AnchorY,
        ReferenceGridScale = composition.ReferenceGridScale,
        Items = [.. composition.Items.Select(ToObjectDto)],
        Assets = [.. composition.Assets.Select(asset => new CompositionAssetDto
        {
            Sha256 = asset.Sha256, DisplayName = asset.DisplayName,
            Provenance = asset.Provenance, License = asset.License,
        })],
        IncludeBackground = composition.IncludeBackground, BackgroundColor = composition.BackgroundColor,
        IncludeGrids = composition.IncludeGrids, Grids = [.. composition.Grids.Select(ToGridDto)],
        IncludeFilters = composition.IncludeFilters, Filters = [.. composition.Filters.Select(ToFilterDto)],
    };

    private static MapDocument ToDomain(
        ManifestDto manifest,
        SceneDto scene,
        AssetManifestDto assetManifest,
        IReadOnlyDictionary<string, byte[]> tileEntries,
        IReadOnlyDictionary<string, byte[]> assetEntries)
    {
        // Explicit JSON null can overwrite any DTO default, so every object/array the mapping
        // walks is checked up front - a damaged file must produce a clear error, never an NRE.
        if (manifest.Canvas is null)
        {
            throw new MapProjectCorruptedException("manifest.json has no canvas object.");
        }

        if (scene.Layers is null || scene.Notes is null
            || scene.Layers.Any(l => l is null || l.Objects is null || l.Mask is { Tiles: null }
                || l.Grids is null && l.Type == "Grid"
                || l.Filter is null && l.Type == "Filter"
                || l.Objects.Any(HasInvalidVectorDto)))
        {
            throw new MapProjectCorruptedException(
                "scene.json has a null layers array, layer, objects array or mask tiles array.");
        }

        try
        {
            var legacyStage2 = scene.Layers.All(layer => layer.Type is null);
            var layers = scene.Layers.Select(layer => ToLayer(layer, tileEntries)).ToList();
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
                scene.Notes.Select(ToNote).ToList());
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
        LayerDto layer, IReadOnlyDictionary<string, byte[]> tileEntries)
    {
        var id = LayerId.Parse(RequireString(layer.Id, "layer id"));
        var name = RequireString(layer.Name, "layer name");
        var mask = layer.Mask is { } maskDto
            ? new LayerMask(
                ToTileMap(maskDto.Tiles, MapProjectFormat.MaskPrefix, id, 1, tileEntries),
                maskDto.Enabled)
            : null;

        MapLayer result = layer.Type switch
        {
            null or "Object" => new ObjectLayer(
                id, name, layer.Visible, layer.Locked, layer.Opacity,
                [.. layer.Objects.Select(ToObject)], mask),
            "BaseColor" => new BaseColorLayer(
                id, name, layer.Visible, layer.Locked, layer.Opacity,
                RequireString(layer.Color, "base color")),
            "Brush" => new BrushLayer(
                id, name, layer.Visible, layer.Locked, layer.Opacity,
                ToTileMap(layer.Tiles ?? [], MapProjectFormat.RasterPrefix, id, 4, tileEntries), mask),
            "Land" => new LandLayer(
                id, name, layer.Visible, layer.Locked, layer.Opacity,
                ToTileMap(layer.Tiles ?? [], MapProjectFormat.RasterPrefix, id, 1, tileEntries),
                ToLandStyle(layer.LandStyle), mask),
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

    private static SceneObject ToObject(SceneObjectDto dto) => new(
        ObjectId.Parse(RequireString(dto.Id, "object id")),
        ParseKind(dto.Kind),
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
        Sublayer = dto.Sublayer,
        GroupId = dto.GroupId is null ? null : Guid.ParseExact(dto.GroupId, "D"),
        ObjectOpacity = dto.ObjectOpacity,
        Stamp = dto.Stamp is { } stamp
            ? new StampStyle(RequireString(stamp.AssetSha256, "stamp asset hash"))
            {
                FlipHorizontal = stamp.FlipHorizontal,
                FlipVertical = stamp.FlipVertical,
                Blend = stamp.Blend,
                Blur = stamp.Blur,
                ShadowOffsetX = stamp.ShadowOffsetX,
                ShadowOffsetY = stamp.ShadowOffsetY,
                ShadowBlur = stamp.ShadowBlur,
                ShadowOpacity = stamp.ShadowOpacity,
                Hue = stamp.Hue,
                Saturation = stamp.Saturation,
                Brightness = stamp.Brightness,
                Contrast = stamp.Contrast,
            }
            : null,
        Line = dto.Line is { } line ? ToLine(line) : null,
        Shape = dto.Shape is { } shape ? ToShape(shape) : null,
        Text = dto.Text is { } text ? ToText(text) : null,
        Composition = dto.Composition is { } composition ? ToComposition(composition) : null,
        PlaceholderReason = dto.PlaceholderReason,
    };

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
        Subdivisions = grid.Subdivisions, LineWidth = grid.LineWidth,
        Color = RequireString(grid.Color, "grid color"), Opacity = grid.Opacity,
        OffsetX = grid.OffsetX, OffsetY = grid.OffsetY,
        RotationDegrees = grid.RotationDegrees, SnapEnabled = grid.SnapEnabled,
        IncludeInExport = grid.IncludeInExport,
    };

    private static TextStyle ToText(TextStyleDto text) => new()
    {
        Content = text.Content ?? "", RequestedFontFamily = RequireString(text.RequestedFontFamily, "requested font family"),
        FallbackFontFamily = RequireString(text.FallbackFontFamily, "fallback font family"),
        FontWeight = text.FontWeight, FontSize = text.FontSize,
        FillColor = RequireString(text.FillColor, "text fill color"),
        OutlineColor = RequireString(text.OutlineColor, "text outline color"),
        OutlineWidth = text.OutlineWidth, ShadowOffsetX = text.ShadowOffsetX,
        ShadowOffsetY = text.ShadowOffsetY, ShadowBlur = text.ShadowBlur,
        ShadowOpacity = text.ShadowOpacity, Curve = text.Curve,
        LetterSpacing = text.LetterSpacing, LineHeight = text.LineHeight,
        Alignment = ParseDefined<TextAlignment>(text.Alignment, "text alignment"),
        FlipHorizontal = text.FlipHorizontal, FlipVertical = text.FlipVertical,
        Blur = text.Blur, Blend = RequireString(text.Blend, "text blend"),
    };

    private static FilterSettings ToFilter(FilterSettingsDto filter) => new()
    {
        Brightness = filter.Brightness, Contrast = filter.Contrast,
        Saturation = filter.Saturation, HueDegrees = filter.HueDegrees,
        Temperature = filter.Temperature, TextureAssetSha256 = filter.TextureAssetSha256,
        TextureOpacity = filter.TextureOpacity, TextureScale = filter.TextureScale,
    };

    private static EditableComposition ToComposition(EditableCompositionDto composition) => new(
        CompositionId.Parse(RequireString(composition.Id, "composition id")),
        DocumentId.Parse(RequireString(composition.SourceDocumentId, "composition source document id")),
        RequireString(composition.Name, "composition name"),
        composition.SourceWidth, composition.SourceHeight,
        [.. composition.Items.Select(ToObject)])
    {
        AnchorX = composition.AnchorX, AnchorY = composition.AnchorY,
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
            TextureAssetSha256 = shape.Fill.TextureAssetSha256,
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
        Id = node.Id.ToString(), X = node.X, Y = node.Y, Kind = node.Kind.ToString(),
        ControlInX = node.ControlInX, ControlInY = node.ControlInY,
        ControlOutX = node.ControlOutX, ControlOutY = node.ControlOutY,
    };

    private static LinePortalDto ToPortalDto(LinePortal portal) => new()
    {
        Id = portal.Id.ToString(), Position = portal.Position, Width = portal.Width,
    };

    private static VectorStrokeDto ToStrokeDto(VectorStrokeStyle stroke) => new()
    {
        Color = stroke.Color, Width = stroke.Width, Opacity = stroke.Opacity, Blend = stroke.Blend,
        Offset = stroke.Offset, EndCap = stroke.EndCap.ToString(), NodeCap = stroke.NodeCap.ToString(),
        ShadowOffsetX = stroke.ShadowOffsetX, ShadowOffsetY = stroke.ShadowOffsetY,
        ShadowBlur = stroke.ShadowBlur, ShadowOpacity = stroke.ShadowOpacity,
        AssetSha256 = stroke.AssetSha256, AssetSpacing = stroke.AssetSpacing,
        Hue = stroke.Hue, Saturation = stroke.Saturation,
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
            TextureAssetSha256 = shape.Fill.TextureAssetSha256,
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
        Width = stroke.Width, Opacity = stroke.Opacity, Blend = RequireString(stroke.Blend, "vector stroke blend"),
        Offset = stroke.Offset,
        EndCap = ParseDefined<VectorCap>(stroke.EndCap, "line end cap"),
        NodeCap = ParseDefined<VectorCap>(stroke.NodeCap, "line node cap"),
        ShadowOffsetX = stroke.ShadowOffsetX, ShadowOffsetY = stroke.ShadowOffsetY,
        ShadowBlur = stroke.ShadowBlur, ShadowOpacity = stroke.ShadowOpacity,
        AssetSha256 = stroke.AssetSha256, AssetSpacing = stroke.AssetSpacing,
        Hue = stroke.Hue, Saturation = stroke.Saturation,
    };

    private static bool HasInvalidVectorDto(SceneObjectDto? item) => item is null
        || item.Line is { Nodes: null } or { Stroke: null } or { Portals: null }
        || item.Shape is { Regions: null } or { Fill: null } or { Stroke: null } or { Portals: null }
        || item.Shape?.Regions.Any(region => region is null || region.Nodes is null) == true
        || item.Composition is { Items: null } or { Assets: null } or { Grids: null } or { Filters: null }
        || item.Composition?.Items.Any(HasInvalidVectorDto) == true;

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
        ShadowBlur = style.ShadowBlur,
        RippleColor = style.RippleColor,
        RippleWidth = style.RippleWidth,
        RippleSpacing = style.RippleSpacing,
        RippleCount = style.RippleCount,
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
            ShadowBlur = style.ShadowBlur,
            RippleColor = RequireString(style.RippleColor, "land ripple color"),
            RippleWidth = style.RippleWidth,
            RippleSpacing = style.RippleSpacing,
            RippleCount = style.RippleCount,
        };
    }

    private static IEnumerable<(string Name, byte[] Bytes)> EnumerateTileEntries(MapDocument document)
    {
        foreach (var layer in document.Layers)
        {
            if (layer is IRasterContentLayer raster)
            {
                foreach (var key in raster.ContentTiles.Keys)
                {
                    yield return (
                        TileEntryName(MapProjectFormat.RasterPrefix, layer.Id.ToString(), key.X, key.Y),
                        raster.ContentTiles.TryGet(key)!);
                }
            }

            if (layer.Mask is { } mask)
            {
                foreach (var key in mask.Tiles.Keys)
                {
                    yield return (
                        TileEntryName(MapProjectFormat.MaskPrefix, layer.Id.ToString(), key.X, key.Y),
                        mask.Tiles.TryGet(key)!);
                }
            }
        }
    }

    private static IEnumerable<(string Name, byte[] Bytes)> EnumerateAssetEntries(MapDocument document)
    {
        foreach (var asset in document.Assets)
        {
            if (asset.Storage == ProjectAssetStorage.Embedded)
            {
                yield return (
                    AssetContentEntry(asset.Sha256, asset.Extension),
                    asset.EmbeddedContent
                        ?? throw new InvalidOperationException($"Embedded asset {asset.Sha256} has no bytes."));
            }

            if (asset.FallbackThumbnailPng is { } thumbnail)
            {
                yield return (AssetThumbnailEntry(asset.Sha256), thumbnail);
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

        return new MapAssetReference(
            hash,
            RequireString(asset.DisplayName, "asset display name"),
            RequireString(asset.MediaType, "asset media type"),
            RequireExtension(asset.Extension),
            storage,
            ParseTimestamp(asset.ImportedUtc, "asset importedUtc"),
            RequireString(asset.Provenance, "asset provenance"),
            RequireString(asset.License, "asset license"),
            embeddedContent: asset.ContentEntry is { } content ? entries[content] : null,
            catalogId: asset.CatalogId,
            relativePath: asset.RelativePath,
            fallbackThumbnailPng: asset.ThumbnailEntry is { } thumbnail ? entries[thumbnail] : null);
    }

    private static TileMap ToTileMap(
        IEnumerable<TileDto> tiles,
        string prefix,
        LayerId layerId,
        int bytesPerPixel,
        IReadOnlyDictionary<string, byte[]> entries)
    {
        var map = TileMap.Empty(bytesPerPixel);
        return map.WithMany(tiles.Select(tile => new KeyValuePair<TileKey, byte[]?>(
            new TileKey(tile.X, tile.Y),
            entries[TileEntryName(prefix, layerId.ToString(), tile.X, tile.Y)])));
    }

    private static string TileEntryName(string prefix, string layerId, int x, int y) =>
        $"{prefix}{layerId}/{x}_{y}.bin";

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
        layer is IRasterContentLayer raster ? raster.ContentTiles.TileCount : 0;

    private static LayerId LegacyBaseLayerId(string documentId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("map/base-color/" + documentId));
        return new LayerId(new Guid(hash.AsSpan(0, 16)));
    }

    private static string RequireString(string? value, string what) =>
        value ?? throw new MapProjectCorruptedException($"The container has a null {what}.");

    /// <summary>
    /// Strict kind parse: only names this version defines. TryParse alone would accept numeric
    /// strings outside the enum (the Stage 1 Fix 02 lesson applied to scene content).
    /// </summary>
    private static SceneObjectKind ParseKind(string? kind)
    {
        if (kind is not null
            && Enum.TryParse<SceneObjectKind>(kind, ignoreCase: false, out var parsed)
            && Enum.IsDefined(parsed))
        {
            return parsed;
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
