using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Map.Persistence;

namespace Map.Persistence.Tests.Support;

/// <summary>Schema-v4-aware corruption fixture edits with every enclosing checksum repaired.</summary>
internal static class ProjectJsonTestMutations
{
    public static void RewriteScene(string path, Action<JsonObject> change)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var scene = ReadObject(archive, MapProjectFormat.SceneEntry);
        change(scene);
        StoreSceneAndManifest(archive, scene);
    }

    public static void RewriteFirstObjectChunk(string path, Func<string, string> rewrite)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var scene = ReadObject(archive, MapProjectFormat.SceneEntry);
        var descriptor = Descriptors(scene).First();
        RewriteChunk(archive, descriptor, rewrite);
        StoreSceneAndManifest(archive, scene);
    }

    public static void RewriteFirstObjectChunk(string path, Action<JsonArray> change)
    {
        RewriteFirstObjectChunk(path, json =>
        {
            var array = JsonNode.Parse(json)!.AsArray();
            change(array);
            return array.ToJsonString();
        });
    }

    public static void RewriteAllObjectChunks(string path, Func<string, string> rewrite)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var scene = ReadObject(archive, MapProjectFormat.SceneEntry);
        foreach (var descriptor in Descriptors(scene))
        {
            RewriteChunk(archive, descriptor, rewrite);
        }
        StoreSceneAndManifest(archive, scene);
    }

    private static IEnumerable<JsonObject> Descriptors(JsonObject scene) =>
        scene["layers"]!.AsArray()
            .SelectMany(layer => layer!["objectChunks"] is JsonArray chunks
                ? chunks.Select(item => item!.AsObject())
                : []);

    private static void RewriteChunk(
        ZipArchive archive,
        JsonObject descriptor,
        Func<string, string> rewrite)
    {
        var name = descriptor["entry"]!.GetValue<string>();
        var json = Encoding.UTF8.GetString(ReadEntry(archive, name));
        var bytes = Encoding.UTF8.GetBytes(rewrite(json));
        using (var parsed = JsonDocument.Parse(bytes))
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("An object-chunk test mutation must retain the JSON array root.");
            }
        }

        Replace(archive, name, bytes);
        descriptor["byteLength"] = bytes.LongLength;
        descriptor["sha256"] = Sha256(bytes);
    }

    private static void StoreSceneAndManifest(ZipArchive archive, JsonObject scene)
    {
        var sceneBytes = JsonSerializer.SerializeToUtf8Bytes(scene);
        Replace(archive, MapProjectFormat.SceneEntry, sceneBytes);
        var manifest = ReadObject(archive, MapProjectFormat.ManifestEntry);
        manifest["sceneSha256"] = Sha256(sceneBytes);
        Replace(
            archive,
            MapProjectFormat.ManifestEntry,
            JsonSerializer.SerializeToUtf8Bytes(manifest));
    }

    private static JsonObject ReadObject(ZipArchive archive, string name) =>
        JsonNode.Parse(ReadEntry(archive, name))!.AsObject();

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void Replace(ZipArchive archive, string name, byte[] bytes)
    {
        archive.GetEntry(name)!.Delete();
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = MapProjectFormat.DeterministicEntryTime;
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
