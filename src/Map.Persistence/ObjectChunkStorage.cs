using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Persistence.Container;

namespace Map.Persistence;

public static partial class MapProjectStore
{
    private sealed class ContainerWriteState
    {
        private readonly MapProjectReadBudget _budget = new();

        public int EntryCount { get; private set; }
        public long CheckpointBytes => _budget.CheckpointPayloadBytes;

        public void Add(string name, long length)
        {
            EntryCount = checked(EntryCount + 1);
            if (EntryCount > MapProjectFormat.MaxEntries)
            {
                throw new MapProjectResourceLimitException(
                    $"Проект содержит больше {MapProjectFormat.MaxEntries} файлов контейнера.");
            }

            // Write-time admission uses the same uncompressed/category envelope as the reader.
            // Using length as the compressed size only disables a ratio check which is meaningful
            // after ZipArchive has closed; the full read-back performs that check before publish.
            _budget.Add(name, length, length);
        }
    }

    private sealed class HashingWriteStream(Stream destination) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public string CompleteHash() => Convert.ToHexStringLower(_hash.GetHashAndReset());
        public override void Flush() => destination.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            destination.Write(buffer);
            BytesWritten = checked(BytesWritten + buffer.Length);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ObjectChunkDescriptorDto>> WriteObjectChunks(
        ZipArchive? archive,
        MapDocument document,
        string entryRoot,
        ContainerWriteState state)
    {
        var result = new Dictionary<string, IReadOnlyList<ObjectChunkDescriptorDto>>(StringComparer.Ordinal);
        foreach (var layer in document.Layers.OfType<ObjectLayer>())
        {
            var layerId = layer.Id.ToString();
            var descriptors = new List<ObjectChunkDescriptorDto>();
            var objectIndex = 0;
            var ordinal = 0;
            while (objectIndex < layer.Objects.Count)
            {
                var entryName = ObjectChunkEntry(entryRoot, layerId, ordinal);
                var entry = archive?.CreateEntry(entryName, CompressionLevel.Optimal);
                if (entry is not null)
                {
                    entry.LastWriteTime = MapProjectFormat.DeterministicEntryTime;
                }

                using var destination = entry?.Open() ?? Stream.Null;
                using var hashing = new HashingWriteStream(destination);
                using var writer = new Utf8JsonWriter(hashing, new JsonWriterOptions
                {
                    Indented = false,
                    SkipValidation = false,
                });

                writer.WriteStartArray();
                var chunkCount = 0;
                long projectedBytes = 2; // '[' + ']'
                WorldBounds? chunkBounds = null;
                while (objectIndex < layer.Objects.Count
                       && chunkCount < MapProjectFormat.ObjectChunkTargetObjects)
                {
                    var sceneObject = layer.Objects[objectIndex];
                    var objectBytes = JsonSerializer.SerializeToUtf8Bytes(ToObjectDto(sceneObject), JsonOptions);
                    var nextBytes = checked(projectedBytes + objectBytes.LongLength + (chunkCount == 0 ? 0 : 1));
                    if (nextBytes > MapProjectFormat.ObjectChunkTargetBytes)
                    {
                        if (chunkCount == 0)
                        {
                            throw new MapProjectResourceLimitException(
                                $"Object {sceneObject.Id} needs {nextBytes} bytes and cannot fit the "
                                + $"{MapProjectFormat.ObjectChunkTargetBytes}-byte object-chunk budget.");
                        }
                        break;
                    }

                    writer.WriteRawValue(objectBytes, skipInputValidation: false);
                    projectedBytes = nextBytes;
                    chunkBounds = Union(chunkBounds, SceneGeometry.BoundsOf(sceneObject));
                    chunkCount++;
                    objectIndex++;
                }

                writer.WriteEndArray();
                writer.Flush();
                if (hashing.BytesWritten != projectedBytes)
                {
                    throw new InvalidOperationException("Object chunk byte accounting diverged from the JSON writer.");
                }

                state.Add(entryName, hashing.BytesWritten);
                var bounds = chunkBounds
                    ?? throw new InvalidOperationException("An object chunk cannot be empty.");
                descriptors.Add(new ObjectChunkDescriptorDto
                {
                    Ordinal = ordinal,
                    Count = chunkCount,
                    ByteLength = hashing.BytesWritten,
                    Sha256 = hashing.CompleteHash(),
                    Bounds = ToChunkBounds(bounds),
                    Entry = entryName,
                    RepresentationVersion = MapProjectFormat.ObjectChunkRepresentationVersion,
                });
                ordinal++;
            }

            result.Add(layerId, descriptors);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, SceneObjectList> ReadAndValidateObjectChunks(
        ZipArchive archive,
        SceneDto scene,
        string entryRoot,
        bool allowUnknownObjectKinds)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var normalizedLayers = new Dictionary<string, List<ObjectChunkDescriptorDto>>(StringComparer.Ordinal);

        foreach (var layer in scene.Layers)
        {
            var layerId = RequireCanonicalLayerId(layer.Id);
            if (layer.Type == "Object")
            {
                if (layer.Objects is not null || layer.ObjectChunks is null)
                {
                    throw new MapProjectCorruptedException(
                        "schema v4 Object layers require objectChunks and must not contain an objects array.");
                }

                for (var index = 0; index < layer.ObjectChunks.Count; index++)
                {
                    var descriptor = layer.ObjectChunks[index]
                        ?? throw new MapProjectCorruptedException("scene.json has a null object-chunk descriptor.");
                    ValidateObjectChunkDescriptor(archive, descriptor, entryRoot, layerId, index, referenced);
                }
                normalizedLayers.Add(layerId, layer.ObjectChunks);
            }
            else if (layer.Objects is not null || layer.ObjectChunks is not null)
            {
                throw new MapProjectCorruptedException(
                    "schema v4 non-Object layers must not contain object arrays or object-chunk descriptors.");
            }
        }

        var objectPrefix = entryRoot + MapProjectFormat.ObjectPrefix;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.StartsWith(objectPrefix, StringComparison.Ordinal)
                && !referenced.Contains(entry.FullName))
            {
                throw new MapProjectCorruptedException(
                    $"Object-chunk entry {entry.FullName} is not declared by scene.json.");
            }
        }

        var result = new Dictionary<string, SceneObjectList>(StringComparer.Ordinal);
        foreach (var (layerId, descriptors) in normalizedLayers)
        {
            var storage = SceneObjectList.Empty;
            foreach (var descriptor in descriptors)
            {
                var bytes = ReadEntry(archive, descriptor.Entry);
                if (!string.Equals(Sha256Hex(bytes), descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new MapProjectCorruptedException(
                        $"Object chunk {descriptor.Entry} does not match its SHA-256 checksum.");
                }

                MapProjectJsonBudget.Validate(bytes, descriptor.Entry);
                var count = CountTopLevelArrayItems(bytes, descriptor.Entry);
                if (count != descriptor.Count)
                {
                    throw new MapProjectCorruptedException(
                        $"Object chunk {descriptor.Entry} contains {count} objects instead of {descriptor.Count}.");
                }

                var dtos = ParseJson<List<SceneObjectDto>>(bytes, descriptor.Entry);
                if (dtos.Count != descriptor.Count || dtos.Any(HasInvalidVectorDto))
                {
                    throw new MapProjectCorruptedException(
                        $"Object chunk {descriptor.Entry} has a null or malformed object payload.");
                }

                var objects = new List<SceneObject>(dtos.Count);
                WorldBounds? actualBounds = null;
                foreach (var dto in dtos)
                {
                    var sceneObject = ToObject(dto, allowUnknownObjectKinds);
                    objects.Add(sceneObject);
                    actualBounds = Union(actualBounds, SceneGeometry.BoundsOf(sceneObject));
                }
                if (actualBounds is null || !ChunkBoundsContain(descriptor.Bounds!, actualBounds.Value))
                {
                    throw new MapProjectCorruptedException(
                        $"Object chunk {descriptor.Entry} extends outside its declared world bounds.");
                }
                storage = storage.AppendRange(objects);
            }
            result.Add(layerId, storage);
        }

        return result;
    }

    private static void ValidateObjectChunkDescriptor(
        ZipArchive archive,
        ObjectChunkDescriptorDto descriptor,
        string entryRoot,
        string layerId,
        int expectedOrdinal,
        HashSet<string> referenced)
    {
        if (descriptor.Ordinal != expectedOrdinal)
        {
            throw new MapProjectCorruptedException(
                $"Object layer {layerId} has a duplicate or missing chunk ordinal at {expectedOrdinal}.");
        }
        if (descriptor.Count is <= 0 or > MapProjectFormat.ObjectChunkTargetObjects)
        {
            throw new MapProjectResourceLimitException(
                $"Object chunk {expectedOrdinal} declares a count outside the "
                + $"{MapProjectFormat.ObjectChunkTargetObjects}-object chunk budget. The file was not modified.");
        }
        if (descriptor.ByteLength is < 2 or > MapProjectFormat.ObjectChunkTargetBytes)
        {
            throw new MapProjectResourceLimitException(
                $"Object chunk {expectedOrdinal} declares bytes outside the "
                + $"{MapProjectFormat.ObjectChunkTargetBytes}-byte chunk budget. The file was not modified.");
        }
        if (descriptor.RepresentationVersion != MapProjectFormat.ObjectChunkRepresentationVersion)
        {
            throw new MapProjectCorruptedException(
                $"Object chunk {expectedOrdinal} uses an unsupported representation version.");
        }

        _ = RequireSha256(descriptor.Sha256, "object chunk sha256");
        ValidateChunkBounds(descriptor.Bounds, descriptor.Entry);
        var expectedEntry = ObjectChunkEntry(entryRoot, layerId, expectedOrdinal);
        if (!string.Equals(descriptor.Entry, expectedEntry, StringComparison.Ordinal))
        {
            throw new MapProjectCorruptedException(
                $"Object chunk {expectedOrdinal} has a non-canonical archive entry name.");
        }
        if (!referenced.Add(expectedEntry))
        {
            throw new MapProjectCorruptedException($"Object chunk {expectedEntry} is referenced more than once.");
        }

        var entry = archive.GetEntry(expectedEntry)
            ?? throw new MapProjectCorruptedException($"Object chunk {expectedEntry} is missing.");
        if (entry.Length != descriptor.ByteLength)
        {
            throw new MapProjectCorruptedException(
                $"Object chunk {expectedEntry} declares {descriptor.ByteLength} bytes but stores {entry.Length}.");
        }
    }

    private static int CountTopLevelArrayItems(ReadOnlySpan<byte> bytes, string entryName)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                throw new MapProjectCorruptedException($"Object chunk {entryName} is not a JSON array.");
            }

            var count = 0;
            var ended = false;
            while (reader.Read())
            {
                if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.StartObject)
                {
                    count = checked(count + 1);
                }
                else if (reader.CurrentDepth == 1 && reader.TokenType is
                         JsonTokenType.StartArray
                         or JsonTokenType.String
                         or JsonTokenType.Number
                         or JsonTokenType.True
                         or JsonTokenType.False
                         or JsonTokenType.Null)
                {
                    throw new MapProjectCorruptedException(
                        $"Object chunk {entryName} contains a non-object array item.");
                }
                else if (reader.CurrentDepth == 0 && reader.TokenType == JsonTokenType.EndArray)
                {
                    ended = true;
                    if (reader.Read())
                    {
                        throw new MapProjectCorruptedException(
                            $"Object chunk {entryName} has trailing JSON content.");
                    }
                    break;
                }
            }
            if (!ended)
            {
                throw new MapProjectCorruptedException($"Object chunk {entryName} has no array terminator.");
            }
            return count;
        }
        catch (JsonException ex)
        {
            throw new MapProjectCorruptedException($"Object chunk {entryName} is not valid JSON.", ex);
        }
        catch (OverflowException)
        {
            throw new MapProjectResourceLimitException(
                $"Object chunk {entryName} has too many items. The file was not modified.");
        }
    }

    private static void WriteBytesEntry(
        ZipArchive? archive,
        string name,
        byte[] bytes,
        ContainerWriteState state,
        bool json = false)
    {
        if (json)
        {
            ValidateJsonPayload(name, bytes);
        }
        state.Add(name, bytes.LongLength);
        if (archive is not null)
        {
            WriteEntry(archive, name, bytes);
        }
    }

    private static string ObjectChunkEntry(string entryRoot, string layerId, int ordinal) =>
        entryRoot + MapProjectFormat.ObjectPrefix + layerId + "/"
        + ordinal.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".json";

    private static ObjectChunkBoundsDto ToChunkBounds(WorldBounds bounds) => new()
    {
        Left = bounds.Left,
        Top = bounds.Top,
        Right = bounds.Right,
        Bottom = bounds.Bottom,
    };

    private static WorldBounds Union(WorldBounds? current, WorldBounds next) => current is null
        ? next
        : new WorldBounds(
            Math.Min(current.Value.Left, next.Left),
            Math.Min(current.Value.Top, next.Top),
            Math.Max(current.Value.Right, next.Right),
            Math.Max(current.Value.Bottom, next.Bottom));

    private static void ValidateChunkBounds(ObjectChunkBoundsDto? bounds, string entryName)
    {
        if (bounds is null
            || !double.IsFinite(bounds.Left)
            || !double.IsFinite(bounds.Top)
            || !double.IsFinite(bounds.Right)
            || !double.IsFinite(bounds.Bottom)
            || bounds.Left > bounds.Right
            || bounds.Top > bounds.Bottom)
        {
            throw new MapProjectCorruptedException(
                $"Object chunk {entryName} has invalid world bounds.");
        }
    }

    // Bounds may conservatively overestimate content after an optional appearance field falls back
    // to a smaller neutral default. Underestimation is never accepted because it could cull data.
    private static bool ChunkBoundsContain(ObjectChunkBoundsDto expected, WorldBounds actual) =>
        expected.Left <= actual.Left
        && expected.Top <= actual.Top
        && expected.Right >= actual.Right
        && expected.Bottom >= actual.Bottom;

    private static byte[] SerializeJson<T>(T value, string entryName)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        ValidateJsonPayload(entryName, bytes);
        return bytes;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ObjectChunkDescriptorDto>>
        WriteMigratedObjectChunks(
            ProjectMigrationPayload payload,
            JsonObject scene,
            string entryRoot,
            string sceneEntry)
    {
        var result = new Dictionary<string, IReadOnlyList<ObjectChunkDescriptorDto>>(StringComparer.Ordinal);
        var layers = scene["layers"] as JsonArray
            ?? throw new MapProjectCorruptedException($"{sceneEntry} has no layers array.");

        foreach (var node in layers)
        {
            var layer = node as JsonObject
                ?? throw new MapProjectCorruptedException($"{sceneEntry} has an invalid layer.");
            var type = layer["type"]?.GetValue<string>()
                ?? throw new MapProjectCorruptedException($"{sceneEntry} has a layer without a type.");
            var layerId = RequireCanonicalLayerId(layer["id"]?.GetValue<string>());
            var objects = layer["objects"] as JsonArray
                ?? throw new MapProjectCorruptedException($"{sceneEntry} has a layer without an objects array.");
            layer.Remove("objects");

            if (type != "Object")
            {
                if (objects.Count != 0)
                {
                    throw new MapProjectCorruptedException(
                        $"{sceneEntry} has objects on a non-Object layer.");
                }
                continue;
            }

            var descriptors = new List<ObjectChunkDescriptorDto>();
            var objectIndex = 0;
            var ordinal = 0;
            while (objectIndex < objects.Count)
            {
                var entryName = ObjectChunkEntry(entryRoot, layerId, ordinal);
                var buffer = new ArrayBufferWriter<byte>();
                using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
                writer.WriteStartArray();
                long projectedBytes = 2;
                var chunkCount = 0;
                WorldBounds? bounds = null;
                while (objectIndex < objects.Count
                       && chunkCount < MapProjectFormat.ObjectChunkTargetObjects)
                {
                    var objectNode = objects[objectIndex]
                        ?? throw new MapProjectCorruptedException($"{sceneEntry} has a null scene object.");
                    var objectBytes = JsonSerializer.SerializeToUtf8Bytes(objectNode, JsonOptions);
                    var nextBytes = checked(projectedBytes + objectBytes.LongLength + (chunkCount == 0 ? 0 : 1));
                    if (nextBytes > MapProjectFormat.ObjectChunkTargetBytes)
                    {
                        if (chunkCount == 0)
                        {
                            throw new MapProjectResourceLimitException(
                                $"An object in {sceneEntry} cannot fit the v4 object-chunk byte budget.");
                        }
                        break;
                    }

                    var dto = JsonSerializer.Deserialize<SceneObjectDto>(objectBytes, JsonOptions)
                        ?? throw new MapProjectCorruptedException($"{sceneEntry} has an empty scene object.");
                    var domain = ToObject(dto, allowUnknownObjectKinds: true);
                    writer.WriteRawValue(objectBytes, skipInputValidation: false);
                    projectedBytes = nextBytes;
                    bounds = Union(bounds, SceneGeometry.BoundsOf(domain));
                    objectIndex++;
                    chunkCount++;
                }
                writer.WriteEndArray();
                writer.Flush();
                var bytes = buffer.WrittenSpan.ToArray();
                ValidateJsonPayload(entryName, bytes);
                payload.SetEntry(entryName, bytes);
                descriptors.Add(new ObjectChunkDescriptorDto
                {
                    Ordinal = ordinal,
                    Count = chunkCount,
                    ByteLength = bytes.LongLength,
                    Sha256 = Sha256Hex(bytes),
                    Bounds = ToChunkBounds(bounds
                        ?? throw new InvalidOperationException("A migrated object chunk cannot be empty.")),
                    Entry = entryName,
                    RepresentationVersion = MapProjectFormat.ObjectChunkRepresentationVersion,
                });
                ordinal++;
            }

            layer["objectChunks"] = JsonSerializer.SerializeToNode(descriptors, JsonOptions);
            result.Add(layerId, descriptors);
        }

        return result;
    }
}
