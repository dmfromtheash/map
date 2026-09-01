using System;
using System.IO;
using System.Text.Json;
using Map.Presentation.ViewModels;

namespace Map.App.Services;

/// <summary>Small atomic app-local preferences file; never enters a map document.</summary>
public sealed class EditorPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly string _path;

    public EditorPreferencesStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Editor preferences path must be absolute.", nameof(path));
        }
        _path = Path.GetFullPath(path);
    }

    public EditorLocalPreferences Load()
    {
        try
        {
            if (!File.Exists(_path)) return new EditorLocalPreferences();
            var bytes = File.ReadAllBytes(_path);
            if (bytes.Length is <= 0 or > 32 * 1024) return new EditorLocalPreferences();
            var file = JsonSerializer.Deserialize<PreferencesFile>(bytes, JsonOptions);
            return file is { Version: 1 }
                && Enum.TryParse<PlacementAutoSelectPolicy>(file.AutoSelect, true, out var policy)
                    ? new EditorLocalPreferences(policy, file.PrimaryAutosaveEnabled)
                    : new EditorLocalPreferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new EditorLocalPreferences();
        }
    }

    public void Save(EditorLocalPreferences preferences)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PreferencesFile
        {
            Version = 1,
            AutoSelect = preferences.AutoSelect.ToString(),
            PrimaryAutosaveEnabled = preferences.PrimaryAutosaveEnabled,
        }, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_path))
            {
                File.Replace(temp, _path, _path + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _path);
            }
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed class PreferencesFile
    {
        public int Version { get; set; }
        public string AutoSelect { get; set; } = nameof(PlacementAutoSelectPolicy.Auto);
        public bool PrimaryAutosaveEnabled { get; set; } = true;
    }
}
