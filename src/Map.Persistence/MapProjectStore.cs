using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
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

        var (manifest, scene, previewPng) = ReadAndValidate(Path.GetFullPath(path));

        return new OpenedProject
        {
            Document = ToDomain(manifest, scene),
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
        var assetManifestBytes = JsonSerializer.SerializeToUtf8Bytes(new AssetManifestDto(), JsonOptions);

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
        var (manifest, scene, _) = ReadAndValidate(tempPath);
        var reloaded = ToDomain(manifest, scene);

        if (reloaded.Id != expected.Id)
        {
            throw new MapProjectCorruptedException("Read-back document id does not match the saved snapshot.");
        }

        if (reloaded.Layers.Count != expected.Layers.Count
            || reloaded.Layers.Zip(expected.Layers).Any(p => p.First.Objects.Count != p.Second.Objects.Count))
        {
            throw new MapProjectCorruptedException("Read-back layer/object shape does not match the saved snapshot.");
        }
    }

    // ---------------------------------------------------------------- read path

    private static (ManifestDto Manifest, SceneDto Scene, byte[] PreviewPng) ReadAndValidate(string fullPath)
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

            // The empty catalog must still be a well-formed document.
            _ = ParseJson<AssetManifestDto>(assetManifestBytes, MapProjectFormat.AssetManifestEntry);

            return (manifest, scene, ReadEntry(archive, MapProjectFormat.PreviewEntry));
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

    // ---------------------------------------------------------------- mapping

    private static SceneDto ToSceneDto(MapDocument document) => new()
    {
        DocumentId = document.Id.ToString(),
        Layers = document.Layers.Select(layer => new LayerDto
        {
            Id = layer.Id.ToString(),
            Name = layer.Name,
            Visible = layer.Visible,
            Locked = layer.Locked,
            Opacity = layer.Opacity,
            Objects = layer.Objects.Select(sceneObject => new SceneObjectDto
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
            }).ToList(),
        }).ToList(),
    };

    private static MapDocument ToDomain(ManifestDto manifest, SceneDto scene)
    {
        // Explicit JSON null can overwrite any DTO default, so every object/array the mapping
        // walks is checked up front - a damaged file must produce a clear error, never an NRE.
        if (manifest.Canvas is null)
        {
            throw new MapProjectCorruptedException("manifest.json has no canvas object.");
        }

        if (scene.Layers is null || scene.Layers.Any(l => l is null || l.Objects is null))
        {
            throw new MapProjectCorruptedException("scene.json has a null layers array, layer or objects array.");
        }

        try
        {
            var layers = scene.Layers.Select(layer => new Layer(
                LayerId.Parse(RequireString(layer.Id, "layer id")),
                RequireString(layer.Name, "layer name"),
                layer.Visible,
                layer.Locked,
                layer.Opacity,
                layer.Objects.Select(sceneObject => new SceneObject(
                    ObjectId.Parse(RequireString(sceneObject.Id, "object id")),
                    ParseKind(sceneObject.Kind),
                    RequireString(sceneObject.Name, "object name"),
                    sceneObject.X,
                    sceneObject.Y,
                    sceneObject.Width,
                    sceneObject.Height,
                    sceneObject.RotationDegrees,
                    sceneObject.FillColor,
                    sceneObject.StrokeColor,
                    sceneObject.StrokeWidth,
                    sceneObject.Visible,
                    sceneObject.Locked)).ToList())).ToList();

            return new MapDocument(
                DocumentId.Parse(RequireString(manifest.DocumentId, "document id")),
                RequireString(manifest.Title, "title"),
                ParseTimestamp(manifest.CreatedUtc, "createdUtc"),
                new CanvasDefinition(
                    manifest.Canvas.Width,
                    manifest.Canvas.Height,
                    RequireString(manifest.Canvas.Unit, "canvas unit"),
                    RequireString(manifest.Canvas.Background, "canvas background")),
                layers);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // A container that parses as JSON but violates the domain's own rules is a damaged
            // project, not an application bug worth an unhandled crash.
            throw new MapProjectCorruptedException(
                "The container holds values the document model rejects: " + ex.Message, ex);
        }
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
