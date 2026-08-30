using System.Text;
using System.Text.Json;

namespace Map.Platform.Windows;

public sealed record ShortcutSettingsLoadResult(
    IReadOnlyDictionary<string, string> Bindings,
    string? Warning = null);

public sealed record ShortcutSettingsSaveResult(bool Succeeded, string? Error = null);

/// <summary>
/// Bounded, atomic local settings storage. It deliberately knows only command ids supplied by the
/// presentation layer, so the platform adapter does not depend on UI or document assemblies.
/// </summary>
public sealed class ShortcutSettingsStore
{
    public const int SchemaVersion = 1;
    public const long MaxEncodedBytes = 64 * 1024;
    public const int MaxBindings = 128;
    public const int MaxStringUtf8Bytes = 256;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly string _path;

    public ShortcutSettingsStore(string? path = null) =>
        _path = System.IO.Path.GetFullPath(path ?? AppPaths.ShortcutSettingsPath);

    public string Path => _path;

    public ShortcutSettingsLoadResult Load(IReadOnlySet<string> knownCommandIds)
    {
        if (!File.Exists(_path))
        {
            return new ShortcutSettingsLoadResult(new Dictionary<string, string>());
        }

        byte[] bytes;
        try
        {
            using var stream = new FileStream(
                _path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaxEncodedBytes)
            {
                return Damaged();
            }

            bytes = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                return Damaged();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Damaged("Не удалось прочитать локальные сочетания. Используются стандартные.");
        }

        try
        {
            ReadOnlyMemory<byte> jsonBytes = bytes;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                jsonBytes = jsonBytes[3..];
            }

            using var document = JsonDocument.Parse(jsonBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var schemaVersion)
                || schemaVersion != SchemaVersion
                || !root.TryGetProperty("bindings", out var bindings)
                || bindings.ValueKind != JsonValueKind.Object)
            {
                return Damaged();
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var total = 0;
            var ignoredKnownValue = false;
            foreach (var property in bindings.EnumerateObject())
            {
                if (++total > MaxBindings)
                {
                    return Damaged();
                }

                // Unknown future ids, including values with a different JSON shape, are inert.
                if (!knownCommandIds.Contains(property.Name))
                {
                    continue;
                }

                if (Encoding.UTF8.GetByteCount(property.Name) > MaxStringUtf8Bytes
                    || property.Value.ValueKind != JsonValueKind.String)
                {
                    ignoredKnownValue = true;
                    continue;
                }

                var value = property.Value.GetString() ?? "";
                if (Encoding.UTF8.GetByteCount(value) > MaxStringUtf8Bytes)
                {
                    ignoredKnownValue = true;
                    continue;
                }

                result[property.Name] = value;
            }

            return new ShortcutSettingsLoadResult(
                result,
                ignoredKnownValue
                    ? "Часть локальных сочетаний повреждена и заменена стандартными."
                    : null);
        }
        catch (JsonException)
        {
            return Damaged();
        }
    }

    public ShortcutSettingsSaveResult Save(
        IReadOnlyDictionary<string, string> bindings,
        IReadOnlySet<string> knownCommandIds)
    {
        var filtered = bindings
            .Where(pair => knownCommandIds.Contains(pair.Key))
            .Take(MaxBindings + 1)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (filtered.Count > MaxBindings
            || filtered.Any(pair =>
                Encoding.UTF8.GetByteCount(pair.Key) > MaxStringUtf8Bytes
                || Encoding.UTF8.GetByteCount(pair.Value ?? "") > MaxStringUtf8Bytes))
        {
            return new ShortcutSettingsSaveResult(false, "Настройки превышают безопасный локальный лимит.");
        }

        var payload = new ShortcutSettingsFile
        {
            Version = SchemaVersion,
            Bindings = filtered,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (bytes.LongLength > MaxEncodedBytes)
        {
            return new ShortcutSettingsSaveResult(false, "Файл настроек превышает безопасный локальный лимит.");
        }

        string? temp = null;
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(
                       temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                // An explicitly confirmed save may replace damaged settings, but the sole old
                // bytes remain recoverable as .bak. Merely loading never writes anything.
                File.Replace(temp, _path, _path + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _path);
            }

            return new ShortcutSettingsSaveResult(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ShortcutSettingsSaveResult(false, $"Не удалось сохранить сочетания: {ex.Message}");
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An orphaned unique temp cannot become authoritative settings.
                }
            }
        }
    }

    private static ShortcutSettingsLoadResult Damaged(string? warning = null) => new(
        new Dictionary<string, string>(),
        warning ?? "Локальный файл сочетаний повреждён. Используются стандартные; исходный файл не изменён.");

    private sealed class ShortcutSettingsFile
    {
        public int Version { get; set; }
        public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
