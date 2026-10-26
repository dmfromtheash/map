using System.IO.Compression;
using System.Text.Json;
using Map.Core.Editing;

namespace Map.Persistence;

/// <summary>
/// R42: a <c>.mapchanges</c> file - a ZIP with <c>package.json</c> (which map, from which version,
/// when) and the two small maps <c>before.mapproj</c> and <c>after.mapproj</c>, each written by
/// <see cref="MapProjectStore"/> so assets, checksums and limits are checked the same way.
/// </summary>
public static class ChangePackageStore
{
    public const string Extension = ".mapchanges";

    // 1×1 transparent PNG: the inner maps are never shown in the gallery.
    private static readonly byte[] Blank = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private sealed record Header(int Format, Guid SourceMap, string SourceTitle, string BaseName, DateTimeOffset CreatedUtc);

    public static void Save(ChangePackage package, string targetPath, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(package);
        var temp = Directory.CreateTempSubdirectory("mapchanges-");
        try
        {
            var before = Path.Combine(temp.FullName, "before" + MapProjectFormat.Extension);
            var after = Path.Combine(temp.FullName, "after" + MapProjectFormat.Extension);
            MapProjectStore.Save(package.Before, before, new SaveMetadata(appVersion, package.CreatedUtc), Blank);
            MapProjectStore.Save(package.After, after, new SaveMetadata(appVersion, package.CreatedUtc), Blank);
            var part = targetPath + ".part";
            using (var zip = ZipFile.Open(part, ZipArchiveMode.Create))
            {
                using (var stream = zip.CreateEntry("package.json").Open())
                    JsonSerializer.Serialize(stream, new Header(ChangePackage.Format, package.SourceMap, package.SourceTitle, package.BaseName, package.CreatedUtc));
                zip.CreateEntryFromFile(before, "before.mapproj", CompressionLevel.NoCompression);
                zip.CreateEntryFromFile(after, "after.mapproj", CompressionLevel.NoCompression);
            }
            File.Move(part, targetPath, overwrite: true);
        }
        finally { temp.Delete(recursive: true); }
    }

    public static ChangePackage Open(string sourcePath)
    {
        var temp = Directory.CreateTempSubdirectory("mapchanges-");
        try
        {
            Header header;
            using (var zip = ZipFile.OpenRead(sourcePath))
            {
                var json = zip.GetEntry("package.json") ?? throw new MapProjectCorruptedException("Это не пакет изменений: нет package.json.");
                if (json.Length > 64 * 1024) throw new MapProjectCorruptedException("Описание пакета слишком большое.");
                using (var stream = json.Open())
                    header = JsonSerializer.Deserialize<Header>(stream) ?? throw new MapProjectCorruptedException("Описание пакета пустое.");
                if (header.Format != ChangePackage.Format) throw new MapProjectCorruptedException($"Пакет другой версии ({header.Format}).");
                foreach (var name in new[] { "before.mapproj", "after.mapproj" })
                    (zip.GetEntry(name) ?? throw new MapProjectCorruptedException($"В пакете нет {name}.")).ExtractToFile(Path.Combine(temp.FullName, name));
            }
            var before = MapProjectStore.Open(Path.Combine(temp.FullName, "before.mapproj")).Document;
            var after = MapProjectStore.Open(Path.Combine(temp.FullName, "after.mapproj")).Document;
            return new ChangePackage(header.SourceMap, header.SourceTitle, header.BaseName, header.CreatedUtc, before, after);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new MapProjectCorruptedException("Файл пакета изменений повреждён: " + ex.Message);
        }
        finally { temp.Delete(recursive: true); }
    }
}
