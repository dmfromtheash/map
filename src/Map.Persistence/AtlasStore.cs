using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Map.Core.Documents;

namespace Map.Persistence;

public sealed record AtlasIssue(string Code, Guid Map, string Message, string Repair);
public sealed record AtlasSave(MapDocument Document, string Path, SaveMetadata Metadata, byte[] Preview);
public sealed record AtlasLinkedResult(MapDocument Parent, MapDocument Child, string ManifestPath);

/// <summary>
/// R25: manifest v1 and redo journal. All payloads are durable before the journal is published.
/// Recovery repeats the same replacements, then removes the journal. No half-atlas is accepted.
/// Map files remain ordinary .mapproj files. Paths may stay only inside the atlas directory.
/// </summary>
public static class AtlasStore
{
    public const string Extension = ".mapatlas";
    private const int MaxJsonBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Pending(string Format, int Version, string Directory, ImmutableArray<PendingFile> Files);
    private sealed record PendingFile(string Stage, string Target, string Sha256);

    private sealed class Gate : IDisposable
    {
        private readonly Mutex _mutex;
        public Gate(string path)
        {
            _mutex = new Mutex(false, "Local\\Map.Atlas." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))));
            try { if (!_mutex.WaitOne(TimeSpan.FromSeconds(15))) throw new IOException("Атлас сейчас сохраняется в другом окне. Повторите действие."); }
            catch (AbandonedMutexException) { }
        }
        public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
    }

    public static string ManifestPath(MapDocument document, string mapPath) => document.Atlas is { } locator
        ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(mapPath))!, locator.File))
        : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(mapPath))!, document.Id + Extension);

    public static MapAtlas Read(string path)
    {
        using var gate = new Gate(path); RecoverCore(path);
        var atlas = ReadJson<MapAtlas>(path); atlas.Validate(); return atlas;
    }

    public static void Save(MapAtlas atlas, string path)
    {
        atlas.Validate(); using var gate = new Gate(path); RecoverCore(path);
        WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(atlas, Json));
    }

    public static string ResolveMap(string manifestPath, AtlasMap map) => ResolveInside(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, map.File, ".mapproj");

    public static string ResolveReference(string manifestPath, string mapPath, string reference, string? extension = null)
    {
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference) || reference.Contains(':'))
            throw new InvalidDataException("Ссылка ведёт за пределы папки атласа.");
        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(mapPath))!, reference));
        return ResolveInside(root, Path.GetRelativePath(root, target), extension);
    }

    private static string ResolveInside(string root, string relative, string? extension = null)
    {
        root = Path.GetFullPath(root);
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("Ссылка ведёт за пределы папки атласа.");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || extension is not null && !full.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ссылка ведёт за пределы папки атласа или имеет неверный формат.");
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Символическая ссылка в атласе не поддерживается.");
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return full;
    }

    private static string Relative(string manifest, string target) => Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(manifest))!, Path.GetFullPath(target));
    private static AtlasLocator Locator(Guid id, string manifest, string map) => new(id, Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(map))!, Path.GetFullPath(manifest)));

    public static AtlasLinkedResult SaveLinked(AtlasSave parent, AtlasSave child, AtlasTransform transform, string kind, Action<int>? afterPublish = null)
    {
        var path = ManifestPath(parent.Document, parent.Path);
        using var gate = new Gate(path); RecoverCore(path);
        var atlas = File.Exists(path) ? Read(path) : new MapAtlas(parent.Document.Atlas?.Id ?? Guid.NewGuid(), [], []);
        if (parent.Document.Atlas is { } locator && locator.Id != atlas.Id)
            throw new InvalidDataException("Идентификатор атласа не совпадает с картой.");
        if (child.Document.Atlas is { } childLocator && childLocator.Id != atlas.Id)
            throw new InvalidDataException("Подкарта уже принадлежит другому атласу.");
        var maps = atlas.Maps;
        foreach (var save in new[] { parent, child })
        {
            var map = new AtlasMap(save.Document.Id.Value, Relative(path, save.Path), save.Document.Title);
            _ = ResolveMap(path, map);
            if (maps.FirstOrDefault(m => m.Id == map.Id) is { } previous && !string.Equals(previous.File, map.File, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Такая карта уже есть в атласе по другому пути. Копия сохраняет прежний ID.");
            maps = [.. maps.Where(m => m.Id != map.Id), map];
        }
        atlas = atlas with { Maps = maps, Links = [.. atlas.Links, new AtlasLink(parent.Document.Id.Value, child.Document.Id.Value, transform, kind)] };
        atlas.Validate();
        var parentDoc = parent.Document with { Atlas = Locator(atlas.Id, path, parent.Path) };
        var childDoc = child.Document with { Atlas = Locator(atlas.Id, path, child.Path) };
        Commit(path, atlas, [parent with { Document = parentDoc }, child with { Document = childDoc }], afterPublish);
        return new(parentDoc, childDoc, path);
    }

    /// <summary>Save As changes the canonical path, keeping one manifest entry and the same ID.</summary>
    public static void SaveMap(AtlasSave map)
    {
        var manifest = ManifestPath(map.Document, map.Path);
        using var gate = new Gate(manifest); RecoverCore(manifest);
        if (map.Document.Atlas is null || !File.Exists(manifest))
        { MapProjectStore.Save(map.Document, map.Path, map.Metadata, map.Preview); return; }
        var atlas = Read(manifest);
        if (atlas.Id != map.Document.Atlas.Id || !atlas.Maps.Any(m => m.Id == map.Document.Id.Value))
            throw new InvalidDataException("Карта не зарегистрирована в указанном атласе.");
        var entry = new AtlasMap(map.Document.Id.Value, Relative(manifest, map.Path), map.Document.Title);
        _ = ResolveMap(manifest, entry);
        atlas = atlas with { Maps = [.. atlas.Maps.Select(m => m.Id == entry.Id ? entry : m)] }; atlas.Validate();
        Commit(manifest, atlas, [map], null);
    }

    private static void Commit(string manifest, MapAtlas atlas, AtlasSave[] saves, Action<int>? afterPublish)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
        Directory.CreateDirectory(root);
        var name = ".atlas-txn-" + Guid.NewGuid().ToString("N");
        var stageRoot = Path.Combine(root, name); Directory.CreateDirectory(stageRoot);
        var ready = false;
        try
        {
            var files = ImmutableArray.CreateBuilder<PendingFile>();
            for (var i = 0; i < saves.Length; i++)
            {
                var save = saves[i]; var target = Relative(manifest, save.Path);
                _ = ResolveInside(root, target, ".mapproj");
                var stage = Path.Combine(stageRoot, i + ".mapproj");
                MapProjectStore.Save(save.Document, stage, save.Metadata, save.Preview);
                files.Add(new(Path.GetRelativePath(root, stage), target, Hash(stage)));
            }
            var manifestStage = Path.Combine(stageRoot, "atlas.json");
            WriteAtomic(manifestStage, JsonSerializer.SerializeToUtf8Bytes(atlas, Json));
            files.Add(new(Path.GetRelativePath(root, manifestStage), Path.GetFileName(manifest), Hash(manifestStage)));
            var pending = new Pending("map-atlas-operation", 1, name, files.ToImmutable());
            WriteAtomic(manifest + ".pending", JsonSerializer.SerializeToUtf8Bytes(pending, Json)); ready = true;
            Publish(manifest, pending, afterPublish);
        }
        finally
        {
            // Once published, the journal owns these payloads until recovery completes.
            if (!ready && Directory.Exists(stageRoot)) Directory.Delete(stageRoot, true);
        }
    }

    public static void Recover(string manifest) { using var gate = new Gate(manifest); RecoverCore(manifest); }
    private static void RecoverCore(string manifest)
    {
        if (File.Exists(manifest + ".pending")) Publish(manifest, ReadJson<Pending>(manifest + ".pending"), null);
    }

    /// <summary>Called before opening a map, including a parent not yet stamped with its locator.</summary>
    public static void RecoverForMap(string path)
    {
        try { RecoverForMapCore(path); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
        { throw new MapProjectCorruptedException("Не удалось прочитать журнал атласа. Сохранённые копии оставлены для восстановления.", ex); }
    }

    private static void RecoverForMapCore(string path)
    {
        path = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(path);
        for (var depth = 0; folder is not null && depth < 32; depth++, folder = Path.GetDirectoryName(folder))
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var pendingPath in Directory.EnumerateFiles(folder, "*" + Extension + ".pending", SearchOption.TopDirectoryOnly))
            {
                var manifest = pendingPath[..^".pending".Length];
                Pending pending;
                try { pending = ReadJson<Pending>(pendingPath); }
                catch (FileNotFoundException) { continue; }
                ValidatePending(pending);
                if (pending.Files.Any(f => string.Equals(ResolveInside(folder, f.Target), path, StringComparison.OrdinalIgnoreCase)))
                {
                    using var gate = new Gate(manifest);
                    if (File.Exists(pendingPath)) Publish(manifest, ReadJson<Pending>(pendingPath), null);
                }
            }
        }
    }

    private static void Publish(string manifest, Pending pending, Action<int>? afterPublish)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
        ValidatePending(pending);
        var stageRoot = ResolveInside(root, pending.Directory);
        var payloads = pending.Files.Select(f => (File: f, Stage: ResolveInside(root, f.Stage), Target: ResolveInside(root, f.Target))).ToArray();
        // Verify every payload BEFORE replacing anything. Stages cannot alias targets or escape the journal directory.
        foreach (var item in payloads)
        {
            if (!item.Stage.StartsWith(stageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || item.Target.StartsWith(stageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Hash(item.Stage) != item.File.Sha256
                || !(item.Target.EndsWith(".mapproj", StringComparison.OrdinalIgnoreCase) || string.Equals(item.Target, Path.GetFullPath(manifest), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Копия операции атласа повреждена. Журнал оставлен для восстановления.");
        }
        for (var i = 0; i < payloads.Length; i++)
        {
            var item = payloads[i]; CopyAtomic(item.Stage, item.Target); afterPublish?.Invoke(i + 1);
        }
        File.Delete(manifest + ".pending"); Directory.Delete(stageRoot, true);
    }

    private static void ValidatePending(Pending pending)
    {
        if (pending.Format != "map-atlas-operation" || pending.Version != 1 || pending.Files.IsDefault
            || pending.Files.Length is < 2 or > 3 || pending.Directory is null || !pending.Directory.StartsWith(".atlas-txn-", StringComparison.Ordinal)
            || pending.Directory.Length != 43 || !Guid.TryParseExact(pending.Directory[11..], "N", out _)
            || pending.Files.Any(f => f is null || string.IsNullOrWhiteSpace(f.Stage) || string.IsNullOrWhiteSpace(f.Target) || f.Sha256 is not { Length: 64 })
            || pending.Files.Select(f => f.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pending.Files.Length)
            throw new InvalidDataException("Повреждён журнал атласа. Сохранённые копии оставлены для восстановления.");
    }

    private static T ReadJson<T>(string path)
    {
        if (new FileInfo(path).Length > MaxJsonBytes) throw new InvalidDataException("Файл атласа слишком большой.");
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException("Файл атласа пуст.");
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void CopyAtomic(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".atlas-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); }
            File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void WriteAtomic(string path, byte[] bytes)
    {
        if (bytes.Length > MaxJsonBytes) throw new InvalidDataException("Файл атласа слишком большой.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static ImmutableArray<AtlasIssue> Inspect(string path)
    {
        var issues = ImmutableArray.CreateBuilder<AtlasIssue>(); var atlas = Read(path);
        foreach (var map in atlas.Maps)
        {
            try
            {
                var file = ResolveMap(path, map);
                if (!File.Exists(file)) { issues.Add(new("missing-map", map.Id, "Не найдена подкарта: " + map.Title, "Найти файл карты или убрать её связь из атласа.")); continue; }
                var document = MapProjectStore.Open(file).Document;
                if (document.Id.Value != map.Id || document.Atlas?.Id != atlas.Id)
                    issues.Add(new("identity", map.Id, "Идентификатор карты или атласа не совпадает: " + map.Title, "Выбрать исходную карту с правильным ID."));
                foreach (var asset in document.Assets.Where(a => a.Storage == ProjectAssetStorage.Linked))
                {
                    try
                    {
                        var assetFile = ResolveReference(path, file, asset.RelativePath ?? "");
                        if (!File.Exists(assetFile) || !string.Equals(Hash(assetFile), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                            issues.Add(new("missing-asset", map.Id, "Не найден ассет или изменилось содержимое: " + asset.DisplayName + " (" + map.Title + ")", "Вернуть исходный ассет по прежнему пути или заново связать его в библиотеке."));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException)
                    { issues.Add(new("external-asset", map.Id, "Внешняя ссылка ассета: " + asset.DisplayName, "Поместить ассет рядом с картой и заново связать его.")); }
                }
                foreach (var linked in document.Layers.OfType<HexLayer>().SelectMany(l => l.Notes.Values).Select(n => n.ChildMap).Where(s => !string.IsNullOrWhiteSpace(s)))
                {
                    try
                    {
                        var child = ResolveReference(path, file, linked!, ".mapproj");
                        if (!atlas.Maps.Any(m => string.Equals(ResolveMap(path, m), child, StringComparison.OrdinalIgnoreCase)))
                            issues.Add(new("unregistered-link", map.Id, "Подкарта не зарегистрирована в атласе: " + linked, "Открыть подкарту из ключа гекса для регистрации или удалить старую ссылку."));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException)
                    { issues.Add(new("external-link", map.Id, "Ссылка гекса ведёт наружу: " + linked, "Убрать ссылку в ключе гекса или выбрать файл внутри атласа.")); }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or MapProjectException or IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
            { issues.Add(new("unreadable-map", map.Id, "Не удалось проверить «" + map.Title + "». Файл повреждён, недоступен или ссылка ведёт за пределы папки атласа.", "Проверить путь внутри папки атласа, вернуть файл из резервной копии или убрать связь.")); }
        }
        return issues.ToImmutable();
    }
}
