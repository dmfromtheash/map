using System.Text.Json;
using Map.Core.Editing;

namespace Map.Assets;

public sealed record LocalBrushPreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? TextureSha256 { get; init; }
    public string TipKind { get; init; } = "RoundSoft";
    public double Radius { get; init; } = 24;
    public double Softness { get; init; } = .5;
    public double Opacity { get; init; } = 1;
    public string Color { get; init; } = "#FF000000";
    public double Spacing { get; init; } = .25;
    public double Scatter { get; init; }
    public double AngleDegrees { get; init; }
    public bool RandomFlipHorizontal { get; init; }
    public bool RandomFlipVertical { get; init; }
    public double SizeJitter { get; init; }
    public double OpacityJitter { get; init; }
    public double TextureScale { get; init; } = 1;
    public double TextureRotationDegrees { get; init; }
    public double TextureOffsetX { get; init; }
    public double TextureOffsetY { get; init; }
    public double TextureOpacity { get; init; } = 1;
    public double HueDegrees { get; init; }
    public double Saturation { get; init; }
    public double Brightness { get; init; }
    public double Contrast { get; init; }
    public bool PressureAffectsSize { get; init; }
    public bool PressureAffectsOpacity { get; init; }
    public bool PressureAffectsSoftness { get; init; }
}

/// <summary>Bounded local brush presets. Texture identity is referenced by SHA; bytes are never copied.</summary>
public sealed class BrushPresetStore
{
    private const int MaxPresets = 100;
    private const int MaxBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly string _path;

    public BrushPresetStore(string catalogRoot) =>
        _path = Path.Combine(Path.GetFullPath(catalogRoot), "brush-presets.json");

    public IReadOnlyList<LocalBrushPreset> Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length is <= 0 or > MaxBytes) return [];
            return Sanitize(JsonSerializer.Deserialize<List<LocalBrushPreset>>(
                File.ReadAllBytes(_path), Options));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public IReadOnlyList<LocalBrushPreset> Save(IReadOnlyList<LocalBrushPreset> presets)
    {
        var safe = Sanitize(presets);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(safe, Options);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes); stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
            return safe;
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static IReadOnlyList<LocalBrushPreset> Sanitize(IEnumerable<LocalBrushPreset>? values) =>
        [.. (values ?? []).Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Id)
                && !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First())
            .Take(MaxPresets).Select(item => item with
            {
                Name = item.Name.Trim()[..Math.Min(80, item.Name.Trim().Length)],
                Radius = Math.Clamp(item.Radius, 1, 256),
                Softness = Math.Clamp(item.Softness, 0, 1),
                Opacity = Math.Clamp(item.Opacity, 0, 1),
                Spacing = Math.Clamp(item.Spacing, .05, 1),
                Scatter = Math.Clamp(item.Scatter, 0, 1),
                SizeJitter = Math.Clamp(item.SizeJitter, 0, 1),
                OpacityJitter = Math.Clamp(item.OpacityJitter, 0, 1),
                TextureScale = Math.Clamp(item.TextureScale, .05, 64),
                TextureOpacity = Math.Clamp(item.TextureOpacity, 0, 1),
                HueDegrees = Math.Clamp(item.HueDegrees, -180, 180),
                Saturation = Math.Clamp(item.Saturation, -1, 1),
                Brightness = Math.Clamp(item.Brightness, -1, 1),
                Contrast = Math.Clamp(item.Contrast, -1, 1),
            })];
}
