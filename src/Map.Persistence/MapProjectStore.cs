using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Map.Core.Documents;
using Map.Persistence.Container;

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

            using (var stream = new FileStream(
                       tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.None))
            {
                WriteContainer(stream, document, metadata, previewPng);
                stream.Flush(flushToDisk: true);
            }

            // A save that cannot be read back by our own reader is a failed save, not a success.
            ValidateReadBack(tempPath, document);

            faults?.AfterDurableTempBeforeReplace?.Invoke();

            if (File.Exists(fullTarget))
            {
                // Win32 ReplaceFile: on return the target is either wholly old or wholly new.
                File.Replace(tempPath, fullTarget, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, fullTarget);
            }
        }
        catch (Exception ex) when (ex is not MapProjectSaveException)
        {
            // The temp is deliberately left in place: after a durable write it may hold the only
            // copy of the user's work, and deleting user data on a failure path is never safe.
            throw new MapProjectSaveException(
                $"Save of {fullTarget} did not complete. The last correct version was not replaced.",
                fullTarget,
                File.Exists(tempPath) ? tempPath : null,
                ex);
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
            foreach (var required in MapProjectFormat.RequiredEntries)
            {
                if (archive.GetEntry(required) is null)
                {
                    throw new MapProjectCorruptedException($"Required entry {required} is missing.");
                }
            }

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
        try
        {
            var layers = scene.Layers.Select(layer => new Layer(
                LayerId.Parse(layer.Id),
                layer.Name,
                layer.Visible,
                layer.Locked,
                layer.Opacity,
                layer.Objects.Select(sceneObject => new SceneObject(
                    ObjectId.Parse(sceneObject.Id),
                    Enum.Parse<SceneObjectKind>(sceneObject.Kind),
                    sceneObject.Name,
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
                DocumentId.Parse(manifest.DocumentId),
                manifest.Title,
                ParseTimestamp(manifest.CreatedUtc, "createdUtc"),
                new CanvasDefinition(
                    manifest.Canvas.Width,
                    manifest.Canvas.Height,
                    manifest.Canvas.Unit,
                    manifest.Canvas.Background),
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

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value, string fieldName)
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
