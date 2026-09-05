using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Map.Core.Documents;

namespace Map.Persistence;

public enum ProjectFileStatus
{
    Ready,
    MigrationRequired,
    UnsupportedVersion,
    NotMapProject,
    Corrupted,
    ResourceLimit,
    NotFound,
    AccessDenied,
    IoFailure,
}

/// <summary>Safe, typed result of read-only project-file inspection.</summary>
public sealed record ProjectFileInspection
{
    public required ProjectFileStatus Status { get; init; }
    public required string Code { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    public int? SourceSchema { get; init; }
    public int? TargetSchema { get; init; }
    public int? MaxSupportedSchema { get; init; }
    public string? CanonicalEntryName { get; init; }
    public string? TileLayerId { get; init; }
    public int? TileX { get; init; }
    public int? TileY { get; init; }
    public bool SourceWasModified => false;
    public OpenedProject? Project { get; init; }
    internal Exception? InternalException { get; init; }
}

public sealed record ProjectMigrationResult
{
    public required OpenedProject Project { get; init; }
    public required int SourceSchema { get; init; }
    public required int TargetSchema { get; init; }
    public bool SourceWasModified => false;
}

public sealed class MigrationFaultInjection
{
    public Action? AfterTempWriteBeforeReadBack { get; init; }
    public Action? AfterReadBackBeforeFinalize { get; init; }
    public Action? BeforeCreateNewFinalize { get; init; }
}

/// <summary>In-memory bounded container passed between adjacent migration steps.</summary>
public sealed class ProjectMigrationPayload
{
    private readonly SortedDictionary<string, byte[]> _entries;

    internal ProjectMigrationPayload(IEnumerable<KeyValuePair<string, byte[]>> entries) =>
        _entries = new(entries.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal), StringComparer.Ordinal);

    public int SchemaVersion { get; set; }
    public IReadOnlyDictionary<string, byte[]> Entries => _entries;

    public byte[] GetEntry(string name) => _entries.TryGetValue(name, out var bytes)
        ? bytes
        : throw new MapProjectCorruptedException($"Required entry {name} is missing.");

    public void SetEntry(string name, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bytes);
        _entries[name] = bytes;
    }
}

public interface IProjectMigrationStep
{
    int SourceVersion { get; }
    int TargetVersion { get; }
    void Migrate(ProjectMigrationPayload payload);
}

/// <summary>Validates and executes only continuous vN to vN+1 migration chains.</summary>
public sealed class ProjectMigrationRegistry
{
    private readonly IReadOnlyDictionary<int, IProjectMigrationStep> _steps;
    private readonly int _currentVersion;

    public ProjectMigrationRegistry(
        IEnumerable<IProjectMigrationStep> steps,
        int currentVersion = MapProjectFormat.SchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (currentVersion < MapProjectFormat.LegacySchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(currentVersion));
        }

        _currentVersion = currentVersion;
        var ordered = steps.OrderBy(step => step.SourceVersion).ToList();
        foreach (var step in ordered)
        {
            if (step.TargetVersion != step.SourceVersion + 1)
            {
                throw new ArgumentException(
                    $"Migration {step.SourceVersion} to {step.TargetVersion} is not an adjacent forward step.",
                    nameof(steps));
            }
            if (step.SourceVersion < MapProjectFormat.LegacySchemaVersion
                || step.TargetVersion > currentVersion)
            {
                throw new ArgumentException("Migration step is outside the supported schema range.", nameof(steps));
            }
        }

        if (ordered.Select(step => step.SourceVersion).Distinct().Count() != ordered.Count
            || ordered.Select(step => step.TargetVersion).Distinct().Count() != ordered.Count)
        {
            throw new ArgumentException("Migration registry contains a duplicate step.", nameof(steps));
        }

        for (var index = 1; index < ordered.Count; index++)
        {
            if (ordered[index - 1].TargetVersion != ordered[index].SourceVersion)
            {
                throw new ArgumentException("Migration registry contains a version gap.", nameof(steps));
            }
        }

        _steps = ordered.ToDictionary(step => step.SourceVersion);
    }

    internal void Migrate(ProjectMigrationPayload payload, int targetVersion)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (targetVersion > _currentVersion || targetVersion < payload.SchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(targetVersion));
        }

        while (payload.SchemaVersion < targetVersion)
        {
            if (!_steps.TryGetValue(payload.SchemaVersion, out var step))
            {
                throw new InvalidOperationException(
                    $"No migration step starts at schema {payload.SchemaVersion}.");
            }

            step.Migrate(payload);
            if (payload.SchemaVersion != step.TargetVersion)
            {
                throw new InvalidOperationException(
                    $"Migration step did not produce schema {step.TargetVersion}.");
            }
        }
    }
}

public static partial class MapProjectStore
{
    private sealed record ProjectHeader(string Format, int SchemaVersion);

    private static readonly ProjectMigrationRegistry MigrationRegistry = new(
        [new V1ToV2MigrationStep(), new V2ToV3MigrationStep(), new V3ToV4MigrationStep()]);

    /// <summary>Classifies a path without opening it for write or materializing its document graph.</summary>
    public static ProjectFileInspection Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var fullPath = Path.GetFullPath(path);
            var header = ReadHeader(fullPath);
            if (!string.Equals(header.Format, MapProjectFormat.FormatId, StringComparison.Ordinal)
                || header.SchemaVersion < MapProjectFormat.LegacySchemaVersion)
            {
                return Inspection(
                    ProjectFileStatus.NotMapProject,
                    "FILE_NOT_MAP_PROJECT",
                    "Это не проект Map",
                    "Файл не содержит поддерживаемый заголовок проекта Map.");
            }

            if (header.SchemaVersion < MapProjectFormat.SchemaVersion)
            {
                return Inspection(
                    ProjectFileStatus.MigrationRequired,
                    "FILE_MIGRATION_REQUIRED",
                    "Нужно обновить копию проекта",
                    $"Исходный файл останется без изменений. Создайте новую копию формата v{MapProjectFormat.SchemaVersion}.",
                    sourceSchema: header.SchemaVersion,
                    targetSchema: MapProjectFormat.SchemaVersion);
            }

            if (header.SchemaVersion > MapProjectFormat.SchemaVersion)
            {
                return Inspection(
                    ProjectFileStatus.UnsupportedVersion,
                    "FILE_UNSUPPORTED_VERSION",
                    "Проект создан более новой версией Map",
                    "Эта версия приложения не поддерживает схему проекта.",
                    sourceSchema: header.SchemaVersion,
                    maxSupportedSchema: MapProjectFormat.SchemaVersion);
            }

            if (header.SchemaVersion != MapProjectFormat.SchemaVersion)
            {
                return Inspection(
                    ProjectFileStatus.NotMapProject,
                    "FILE_SCHEMA_INVALID",
                    "Это не проект Map",
                    "Версия схемы файла указана неверно.");
            }

            return Inspection(
                ProjectFileStatus.Ready,
                "FILE_READY",
                "Проект готов к открытию",
                "Проверка файла завершена.",
                sourceSchema: header.SchemaVersion,
                project: Open(fullPath));
        }
        catch (FileNotFoundException ex)
        {
            return Failure(ProjectFileStatus.NotFound, "FILE_NOT_FOUND", "Файл не найден",
                "Выбранный файл больше недоступен.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            return Failure(ProjectFileStatus.NotFound, "FILE_NOT_FOUND", "Файл не найден",
                "Папка выбранного файла больше недоступна.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failure(ProjectFileStatus.AccessDenied, "FILE_ACCESS_DENIED", "Нет доступа к файлу",
                "Проверьте права доступа или закройте приложение, удерживающее файл.", ex);
        }
        catch (MapProjectResourceLimitException ex)
        {
            return Failure(ProjectFileStatus.ResourceLimit, "FILE_RESOURCE_LIMIT",
                "Проект превышает безопасный лимит", "Файл не изменён. Уменьшите размер проекта или его вложений.", ex);
        }
        catch (UnsupportedSchemaVersionException ex)
        {
            return Inspection(ProjectFileStatus.UnsupportedVersion, "FILE_UNSUPPORTED_VERSION",
                "Проект создан более новой версией Map", "Эта версия приложения не поддерживает схему проекта.",
                sourceSchema: ex.SchemaVersion, maxSupportedSchema: MapProjectFormat.SchemaVersion,
                exception: ex);
        }
        catch (MapProjectTileCorruptedException ex)
        {
            return Inspection(
                ProjectFileStatus.Corrupted,
                "FILE_TILE_CORRUPTED",
                "Проект повреждён",
                "Один из тайлов проекта не прошёл проверку.",
                exception: ex) with
            {
                CanonicalEntryName = ex.EntryName,
                TileLayerId = ex.LayerId,
                TileX = ex.TileX,
                TileY = ex.TileY,
            };
        }
        catch (MapProjectCorruptedException ex) when (
            ex.Message.Contains("canonical ZIP end record", StringComparison.Ordinal)
            || ex.Message.Contains("ZIP end record length", StringComparison.Ordinal))
        {
            return Failure(ProjectFileStatus.NotMapProject, "FILE_NOT_MAP_PROJECT", "Это не проект Map",
                "Файл не является читаемым контейнером проекта Map.", ex);
        }
        catch (MapProjectCorruptedException ex)
        {
            return Failure(ProjectFileStatus.Corrupted, "FILE_CORRUPTED", "Проект повреждён",
                "Структура или контрольные суммы проекта не прошли проверку.", ex);
        }
        catch (MapProjectException ex)
        {
            return Failure(ProjectFileStatus.Corrupted, "FILE_CORRUPTED", "Проект повреждён",
                "Проект не прошёл безопасную проверку файла.", ex);
        }
        catch (InvalidDataException ex)
        {
            return Failure(ProjectFileStatus.NotMapProject, "FILE_NOT_MAP_PROJECT", "Это не проект Map",
                "Файл не является читаемым контейнером проекта Map.", ex);
        }
        catch (JsonException ex)
        {
            return Failure(ProjectFileStatus.NotMapProject, "FILE_NOT_MAP_PROJECT", "Это не проект Map",
                "Заголовок файла не является корректным проектом Map.", ex);
        }
        catch (IOException ex)
        {
            return Failure(ProjectFileStatus.IoFailure, "FILE_IO_FAILURE", "Не удалось прочитать файл",
                "Повторите попытку после проверки диска и доступности файла.", ex);
        }
    }

    /// <summary>Creates and validates a new v2 copy; source and existing destination are never replaced.</summary>
    public static ProjectMigrationResult MigrateCopy(
        string sourcePath,
        string destinationPath,
        MigrationFaultInjection? faults = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Migration destination must differ from its source.", nameof(destinationPath));
        }
        if (File.Exists(destination))
        {
            throw new IOException("Migration destination already exists.");
        }

        var inspection = Inspect(source);
        if (inspection.Status != ProjectFileStatus.MigrationRequired
            || inspection.SourceSchema is not { } sourceSchema)
        {
            throw new InvalidOperationException(
                $"Migration requires status MigrationRequired, not {inspection.Status}.");
        }

        var (manifest, scene, objectStorages, assetManifest, _, tiles, assets, checkpoints) = ReadAndValidate(
            source, sourceSchema, allowUnknownObjectKinds: true);
        var expected = ToDomain(
            manifest, scene, objectStorages, assetManifest, tiles, assets, checkpoints,
            allowLegacyLayerTypes: true,
            allowUnknownObjectKinds: true);

        var payload = ReadMigrationPayload(source, sourceSchema);
        MigrationRegistry.Migrate(payload, MapProjectFormat.SchemaVersion);

        var directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("Destination path has no directory.", nameof(destinationPath));
        var temp = Path.Combine(directory, $"{Path.GetFileName(destination)}.tmp-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(
                       temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.None))
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var (name, bytes) in payload.Entries)
                    {
                        WriteEntry(archive, name, bytes);
                    }
                }
                stream.Flush(flushToDisk: true);
            }

            faults?.AfterTempWriteBeforeReadBack?.Invoke();
            var verified = Open(temp);
            ValidateMigrationReadBack(expected, verified.Document);
            faults?.AfterReadBackBeforeFinalize?.Invoke();
            faults?.BeforeCreateNewFinalize?.Invoke();
            File.Move(temp, destination, overwrite: false);
            return new ProjectMigrationResult
            {
                Project = Open(destination),
                SourceSchema = sourceSchema,
                TargetSchema = MapProjectFormat.SchemaVersion,
            };
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (IOException)
            {
                // Best effort: never hide the primary migration outcome.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort: never hide the primary migration outcome.
            }
        }
    }

    private static ProjectHeader ReadHeader(string fullPath)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 22)
        {
            throw new InvalidDataException("The file is too short to be a project container.");
        }
        MapProjectZipAdmission.Validate(stream);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw;
        }

        using (archive)
        {
            ValidateContainerShape(archive);
            if (archive.GetEntry(MapProjectFormat.ManifestEntry) is null)
            {
                throw new MapProjectCorruptedException("Required entry manifest.json is missing.");
            }

            var bytes = ReadEntry(archive, MapProjectFormat.ManifestEntry);
            MapProjectJsonBudget.Validate(bytes, MapProjectFormat.ManifestEntry);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("format", out var formatElement)
                || formatElement.ValueKind != JsonValueKind.String
                || !document.RootElement.TryGetProperty("schemaVersion", out var schemaElement)
                || schemaElement.ValueKind != JsonValueKind.Number
                || !schemaElement.TryGetInt32(out var schemaVersion))
            {
                return new ProjectHeader("", 0);
            }

            return new ProjectHeader(formatElement.GetString() ?? "", schemaVersion);
        }
    }

    private static ProjectMigrationPayload ReadMigrationPayload(string fullPath, int schemaVersion)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        MapProjectZipAdmission.Validate(stream);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        ValidateContainerShape(archive);
        var entries = archive.Entries.Select(entry =>
            new KeyValuePair<string, byte[]>(entry.FullName, ReadEntry(archive, entry.FullName)));
        return new ProjectMigrationPayload(entries) { SchemaVersion = schemaVersion };
    }

    private static void ValidateMigrationReadBack(MapDocument expected, MapDocument actual)
    {
        if (expected.Id != actual.Id
            || expected.Title != actual.Title
            || expected.CreatedUtc != actual.CreatedUtc
            || expected.Canvas != actual.Canvas
            || expected.Layers.Count != actual.Layers.Count
            || expected.Layers.Zip(actual.Layers).Any(pair =>
                pair.First.Id != pair.Second.Id
                || pair.First.GetType() != pair.Second.GetType()
                || ObjectCount(pair.First) != ObjectCount(pair.Second)
                || !ObjectIdentitySequencesMatch(pair.First, pair.Second)
                || TileCount(pair.First) != TileCount(pair.Second))
            || expected.Assets.Count != actual.Assets.Count
            || expected.Notes.Count != actual.Notes.Count
            || expected.Checkpoints.Count != actual.Checkpoints.Count)
        {
            throw new MapProjectCorruptedException(
                "Migrated read-back does not match the source document semantics.");
        }
    }

    private static ProjectFileInspection Inspection(
        ProjectFileStatus status,
        string code,
        string title,
        string message,
        int? sourceSchema = null,
        int? targetSchema = null,
        int? maxSupportedSchema = null,
        OpenedProject? project = null,
        Exception? exception = null) => new()
    {
        Status = status,
        Code = code,
        Title = title,
        Message = message,
        SourceSchema = sourceSchema,
        TargetSchema = targetSchema,
        MaxSupportedSchema = maxSupportedSchema,
        Project = project,
        InternalException = exception,
    };

    private static ProjectFileInspection Failure(
        ProjectFileStatus status,
        string code,
        string title,
        string message,
        Exception exception) => Inspection(status, code, title, message, exception: exception);

    private sealed class V1ToV2MigrationStep : IProjectMigrationStep
    {
        public int SourceVersion => 1;
        public int TargetVersion => 2;

        public void Migrate(ProjectMigrationPayload payload)
        {
            if (payload.SchemaVersion != SourceVersion)
            {
                throw new InvalidOperationException("The v1 to v2 migration received a non-v1 payload.");
            }

            var manifest = ParseObject(payload.GetEntry(MapProjectFormat.ManifestEntry), MapProjectFormat.ManifestEntry);
            var documentId = RequiredString(manifest, "documentId", MapProjectFormat.ManifestEntry);
            var background = RequiredObject(manifest, "canvas", MapProjectFormat.ManifestEntry)["background"]?.GetValue<string>()
                ?? throw new MapProjectCorruptedException("manifest.json has no canvas background.");

            var sceneBytes = MigrateScene(
                payload.GetEntry(MapProjectFormat.SceneEntry), documentId, background, MapProjectFormat.SceneEntry);
            payload.SetEntry(MapProjectFormat.SceneEntry, sceneBytes);
            manifest["schemaVersion"] = TargetVersion;
            manifest["sceneSha256"] = Sha256Hex(sceneBytes);

            if (payload.Entries.TryGetValue(MapProjectFormat.CheckpointCatalogEntry, out var catalogBytes))
            {
                var catalog = ParseObject(catalogBytes, MapProjectFormat.CheckpointCatalogEntry);
                var checkpoints = catalog["checkpoints"] as JsonArray
                    ?? throw new MapProjectCorruptedException("Checkpoint catalog has no checkpoints array.");
                foreach (var node in checkpoints)
                {
                    var item = node as JsonObject
                        ?? throw new MapProjectCorruptedException("Checkpoint catalog has an invalid item.");
                    var id = RequiredString(item, "id", MapProjectFormat.CheckpointCatalogEntry);
                    var root = MapProjectFormat.CheckpointPrefix + id + "/";
                    var snapshotName = root + "manifest.json";
                    var checkpointSceneName = root + "scene.json";
                    var snapshot = ParseObject(payload.GetEntry(snapshotName), snapshotName);
                    var checkpointBackground = RequiredObject(snapshot, "canvas", snapshotName)["background"]?.GetValue<string>()
                        ?? throw new MapProjectCorruptedException($"{snapshotName} has no canvas background.");
                    var migratedCheckpointScene = MigrateScene(
                        payload.GetEntry(checkpointSceneName), documentId, checkpointBackground, checkpointSceneName);
                    payload.SetEntry(checkpointSceneName, migratedCheckpointScene);
                    snapshot["sceneSha256"] = Sha256Hex(migratedCheckpointScene);
                    var snapshotBytes = SerializeJson(snapshot, snapshotName);
                    payload.SetEntry(snapshotName, snapshotBytes);
                    item["snapshotManifestSha256"] = Sha256Hex(snapshotBytes);
                }

                var migratedCatalog = SerializeJson(catalog, MapProjectFormat.CheckpointCatalogEntry);
                payload.SetEntry(MapProjectFormat.CheckpointCatalogEntry, migratedCatalog);
                manifest["checkpointCatalogSha256"] = Sha256Hex(migratedCatalog);
            }

            payload.SetEntry(MapProjectFormat.ManifestEntry, SerializeJson(manifest, MapProjectFormat.ManifestEntry));
            payload.SchemaVersion = TargetVersion;
        }

        private static byte[] MigrateScene(
            byte[] source,
            string documentId,
            string background,
            string entryName)
        {
            var unknownPayloads = CollectUnknownObjectPayloads(source, entryName);
            var scene = ParseObject(source, entryName);
            var layers = scene["layers"] as JsonArray
                ?? throw new MapProjectCorruptedException($"{entryName} has no layers array.");
            scene["notes"] ??= new JsonArray();

            var layerObjects = layers.Select(node => node as JsonObject
                ?? throw new MapProjectCorruptedException($"{entryName} has an invalid layer.")).ToList();
            var allUntyped = layerObjects.All(layer => layer["type"] is null);
            var anyUntyped = layerObjects.Any(layer => layer["type"] is null);
            if (anyUntyped && !allUntyped)
            {
                throw new MapProjectCorruptedException(
                    $"{entryName} mixes historical untyped and explicit layer shapes.");
            }

            if (allUntyped)
            {
                foreach (var layer in layerObjects) layer["type"] = "Object";
                layers.Insert(0, NewBaseLayer(documentId, background));
                layerObjects.Insert(0, (JsonObject)layers[0]!);
            }

            foreach (var layer in layerObjects)
            {
                if (layer["type"] is not JsonValue typeValue
                    || !typeValue.TryGetValue<string>(out var type)
                    || type is not ("BaseColor" or "Object" or "Brush" or "Land" or "Grid" or "Filter"))
                {
                    throw new MapProjectCorruptedException($"{entryName} has an unsupported layer type.");
                }
                layer["objects"] ??= new JsonArray();
                if (layer["objects"] is JsonArray objects)
                    MigrateObjects(objects, entryName, unknownPayloads);
            }

            return SerializeJson(scene, entryName);
        }

        private static void MigrateObjects(
            JsonArray objects,
            string entryName,
            IReadOnlyDictionary<string, (string Kind, string Raw)> unknownPayloads)
        {
            foreach (var node in objects)
            {
                var item = node as JsonObject
                    ?? throw new MapProjectCorruptedException($"{entryName} has an invalid scene object.");
                var kind = RequiredString(item, "kind", entryName);
                if (!Enum.TryParse<SceneObjectKind>(kind, ignoreCase: false, out var parsed)
                    || !Enum.IsDefined(parsed))
                {
                    var id = RequiredString(item, "id", entryName);
                    if (!unknownPayloads.TryGetValue(id, out var original)
                        || !string.Equals(original.Kind, kind, StringComparison.Ordinal))
                    {
                        throw new MapProjectCorruptedException(
                            $"{entryName} cannot attach an unknown object payload to its stable id.");
                    }
                    var raw = original.Raw;
                    if (Encoding.UTF8.GetByteCount(raw) > OpaqueSceneObjectPayload.MaxUtf8Bytes)
                    {
                        throw new MapProjectResourceLimitException(
                            "Unknown object payload exceeds the bounded migration envelope.");
                    }
                    item["kind"] = nameof(SceneObjectKind.Placeholder);
                    item["placeholderReason"] = "Неизвестный тип объекта сохранён как заполнитель.";
                    item["opaquePayload"] = new JsonObject
                    {
                        ["originalKind"] = kind,
                        ["rawJson"] = raw,
                    };
                }

                if (item["composition"] is JsonObject composition
                    && composition["items"] is JsonArray nested)
                {
                    MigrateObjects(nested, entryName, unknownPayloads);
                }
            }
        }

        private static IReadOnlyDictionary<string, (string Kind, string Raw)> CollectUnknownObjectPayloads(
            byte[] source,
            string entryName)
        {
            var result = new Dictionary<string, (string Kind, string Raw)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var document = JsonDocument.Parse(source);
                if (!document.RootElement.TryGetProperty("layers", out var layers)
                    || layers.ValueKind != JsonValueKind.Array)
                {
                    throw new MapProjectCorruptedException($"{entryName} has no layers array.");
                }

                foreach (var layer in layers.EnumerateArray())
                {
                    if (layer.TryGetProperty("objects", out var objects)
                        && objects.ValueKind == JsonValueKind.Array)
                    {
                        Collect(objects);
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new MapProjectCorruptedException($"{entryName} is not valid JSON.", ex);
            }

            return result;

            void Collect(JsonElement objects)
            {
                foreach (var item in objects.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("kind", out var kindElement)
                        || kindElement.ValueKind != JsonValueKind.String
                        || !item.TryGetProperty("id", out var idElement)
                        || idElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var kind = kindElement.GetString()!;
                    if (!Enum.TryParse<SceneObjectKind>(kind, ignoreCase: false, out var parsed)
                        || !Enum.IsDefined(parsed))
                    {
                        var raw = item.GetRawText();
                        if (Encoding.UTF8.GetByteCount(raw) > OpaqueSceneObjectPayload.MaxUtf8Bytes)
                        {
                            throw new MapProjectResourceLimitException(
                                "Unknown object payload exceeds the bounded migration envelope.");
                        }
                        var id = idElement.GetString()!;
                        if (!result.TryAdd(id, (kind, raw)))
                        {
                            throw new MapProjectCorruptedException(
                                $"{entryName} has duplicate stable object ids.");
                        }
                    }

                    if (item.TryGetProperty("composition", out var composition)
                        && composition.ValueKind == JsonValueKind.Object
                        && composition.TryGetProperty("items", out var nested)
                        && nested.ValueKind == JsonValueKind.Array)
                    {
                        Collect(nested);
                    }
                }
            }
        }

        private static JsonObject NewBaseLayer(string documentId, string background) => new()
        {
            ["id"] = LegacyBaseLayerId(documentId).ToString(),
            ["type"] = "BaseColor",
            ["name"] = "Цвет фона",
            ["visible"] = true,
            ["locked"] = false,
            ["opacity"] = 1.0,
            ["blend"] = "Normal",
            ["color"] = background,
            ["objects"] = new JsonArray(),
        };

        private static JsonObject ParseObject(byte[] bytes, string entryName)
        {
            MapProjectJsonBudget.Validate(bytes, entryName);
            try
            {
                return JsonNode.Parse(bytes) as JsonObject
                    ?? throw new MapProjectCorruptedException($"{entryName} is not a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new MapProjectCorruptedException($"{entryName} is not valid JSON.", ex);
            }
        }

        private static JsonObject RequiredObject(JsonObject owner, string name, string entryName) =>
            owner[name] as JsonObject
            ?? throw new MapProjectCorruptedException($"{entryName} has no {name} object.");

        private static string RequiredString(JsonObject owner, string name, string entryName) =>
            owner[name]?.GetValue<string>()
            ?? throw new MapProjectCorruptedException($"{entryName} has no {name} string.");

        private static byte[] SerializeJson(JsonObject value, string entryName)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            ValidateJsonPayload(entryName, bytes);
            return bytes;
        }
    }

    /// <summary>
    /// v3 adds optional Object-layer effects and sublayer states. Existing scenes already express
    /// the exact default state by omission, so migration only advances canonical manifests and
    /// their checkpoint checksums; it never rewrites the source file or invents document values.
    /// </summary>
    private sealed class V2ToV3MigrationStep : IProjectMigrationStep
    {
        public int SourceVersion => 2;
        public int TargetVersion => 3;

        public void Migrate(ProjectMigrationPayload payload)
        {
            if (payload.SchemaVersion != SourceVersion)
                throw new InvalidOperationException("The v2 to v3 migration received a non-v2 payload.");

            var manifest = ParseObject(payload.GetEntry(MapProjectFormat.ManifestEntry), MapProjectFormat.ManifestEntry);
            manifest["schemaVersion"] = TargetVersion;

            if (payload.Entries.TryGetValue(MapProjectFormat.CheckpointCatalogEntry, out var catalogBytes))
            {
                var catalog = ParseObject(catalogBytes, MapProjectFormat.CheckpointCatalogEntry);
                var checkpoints = catalog["checkpoints"] as JsonArray
                    ?? throw new MapProjectCorruptedException("Checkpoint catalog has no checkpoints array.");
                foreach (var node in checkpoints)
                {
                    var item = node as JsonObject
                        ?? throw new MapProjectCorruptedException("Checkpoint catalog has an invalid item.");
                    var id = RequiredString(item, "id", MapProjectFormat.CheckpointCatalogEntry);
                    var snapshotName = MapProjectFormat.CheckpointPrefix + id + "/manifest.json";
                    var snapshot = ParseObject(payload.GetEntry(snapshotName), snapshotName);
                    snapshot["schemaVersion"] = TargetVersion;
                    var snapshotBytes = SerializeJson(snapshot, snapshotName);
                    payload.SetEntry(snapshotName, snapshotBytes);
                    item["snapshotManifestSha256"] = Sha256Hex(snapshotBytes);
                }

                var migratedCatalog = SerializeJson(catalog, MapProjectFormat.CheckpointCatalogEntry);
                payload.SetEntry(MapProjectFormat.CheckpointCatalogEntry, migratedCatalog);
                manifest["checkpointCatalogSha256"] = Sha256Hex(migratedCatalog);
            }

            payload.SetEntry(MapProjectFormat.ManifestEntry,
                SerializeJson(manifest, MapProjectFormat.ManifestEntry));
            payload.SchemaVersion = TargetVersion;
        }

        private static JsonObject ParseObject(byte[] bytes, string entryName)
        {
            MapProjectJsonBudget.Validate(bytes, entryName);
            try
            {
                return JsonNode.Parse(bytes) as JsonObject
                    ?? throw new MapProjectCorruptedException($"{entryName} is not a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new MapProjectCorruptedException($"{entryName} is not valid JSON.", ex);
            }
        }

        private static string RequiredString(JsonObject owner, string name, string entryName) =>
            owner[name]?.GetValue<string>()
            ?? throw new MapProjectCorruptedException($"{entryName} has no {name} string.");

        private static byte[] SerializeJson(JsonObject value, string entryName)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            ValidateJsonPayload(entryName, bytes);
            return bytes;
        }
    }

    /// <summary>
    /// v4 moves top-level Object-layer content out of scene.json into independently checksummed
    /// canonical JSON chunks. Migration only writes a separate destination container; v3 bytes
    /// and timestamps remain untouched and the caller publishes only after a full v4 read-back.
    /// </summary>
    private sealed class V3ToV4MigrationStep : IProjectMigrationStep
    {
        public int SourceVersion => 3;
        public int TargetVersion => 4;

        public void Migrate(ProjectMigrationPayload payload)
        {
            if (payload.SchemaVersion != SourceVersion)
            {
                throw new InvalidOperationException("The v3 to v4 migration received a non-v3 payload.");
            }
            if (payload.Entries.Keys.Any(name =>
                    name.StartsWith(MapProjectFormat.ObjectPrefix, StringComparison.Ordinal)
                    || name.StartsWith(MapProjectFormat.CheckpointPrefix, StringComparison.Ordinal)
                    && name.Contains("/" + MapProjectFormat.ObjectPrefix, StringComparison.Ordinal)))
            {
                throw new MapProjectCorruptedException(
                    "A schema v3 source unexpectedly contains reserved object-chunk entries.");
            }

            var manifest = ParseObject(
                payload.GetEntry(MapProjectFormat.ManifestEntry), MapProjectFormat.ManifestEntry);
            var scene = ParseObject(payload.GetEntry(MapProjectFormat.SceneEntry), MapProjectFormat.SceneEntry);
            _ = WriteMigratedObjectChunks(payload, scene, entryRoot: "", MapProjectFormat.SceneEntry);
            var sceneBytes = SerializeObject(scene, MapProjectFormat.SceneEntry);
            payload.SetEntry(MapProjectFormat.SceneEntry, sceneBytes);
            manifest["schemaVersion"] = TargetVersion;
            manifest["sceneSha256"] = Sha256Hex(sceneBytes);

            if (payload.Entries.TryGetValue(MapProjectFormat.CheckpointCatalogEntry, out var catalogBytes))
            {
                var catalog = ParseObject(catalogBytes, MapProjectFormat.CheckpointCatalogEntry);
                var checkpoints = catalog["checkpoints"] as JsonArray
                    ?? throw new MapProjectCorruptedException("Checkpoint catalog has no checkpoints array.");
                foreach (var node in checkpoints)
                {
                    var item = node as JsonObject
                        ?? throw new MapProjectCorruptedException("Checkpoint catalog has an invalid item.");
                    var id = RequiredString(item, "id", MapProjectFormat.CheckpointCatalogEntry);
                    var root = MapProjectFormat.CheckpointPrefix + id + "/";
                    var snapshotName = root + "manifest.json";
                    var checkpointSceneName = root + "scene.json";
                    var checkpointScene = ParseObject(
                        payload.GetEntry(checkpointSceneName), checkpointSceneName);
                    _ = WriteMigratedObjectChunks(payload, checkpointScene, root, checkpointSceneName);
                    var checkpointSceneBytes = SerializeObject(checkpointScene, checkpointSceneName);
                    payload.SetEntry(checkpointSceneName, checkpointSceneBytes);

                    var snapshot = ParseObject(payload.GetEntry(snapshotName), snapshotName);
                    snapshot["schemaVersion"] = TargetVersion;
                    snapshot["sceneSha256"] = Sha256Hex(checkpointSceneBytes);
                    var snapshotBytes = SerializeObject(snapshot, snapshotName);
                    payload.SetEntry(snapshotName, snapshotBytes);
                    item["snapshotManifestSha256"] = Sha256Hex(snapshotBytes);
                }

                var migratedCatalog = SerializeObject(catalog, MapProjectFormat.CheckpointCatalogEntry);
                payload.SetEntry(MapProjectFormat.CheckpointCatalogEntry, migratedCatalog);
                manifest["checkpointCatalogSha256"] = Sha256Hex(migratedCatalog);
            }

            payload.SetEntry(
                MapProjectFormat.ManifestEntry,
                SerializeObject(manifest, MapProjectFormat.ManifestEntry));
            payload.SchemaVersion = TargetVersion;
        }

        private static JsonObject ParseObject(byte[] bytes, string entryName)
        {
            MapProjectJsonBudget.Validate(bytes, entryName);
            try
            {
                return JsonNode.Parse(bytes) as JsonObject
                    ?? throw new MapProjectCorruptedException($"{entryName} is not a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new MapProjectCorruptedException($"{entryName} is not valid JSON.", ex);
            }
        }

        private static string RequiredString(JsonObject owner, string name, string entryName) =>
            owner[name]?.GetValue<string>()
            ?? throw new MapProjectCorruptedException($"{entryName} has no {name} string.");

        private static byte[] SerializeObject(JsonObject value, string entryName)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            ValidateJsonPayload(entryName, bytes);
            return bytes;
        }
    }
}
