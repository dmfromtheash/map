using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Map.Core.Documents;
using Map.Core.Editing;
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
            if (file is not { Version: 1 or 2 or 3 }
                || !Enum.TryParse<PlacementAutoSelectPolicy>(file.AutoSelect, true, out var policy)
                || !Enum.IsDefined(policy))
            {
                return new EditorLocalPreferences();
            }

            return new EditorLocalPreferences(
                policy, file.PrimaryAutosaveEnabled,
                file.Version >= 2 ? SanitizeStamp(file.Stamp, policy) : null,
                Finite(file.LibraryTileSize, 112, 64, 176), file.LibraryShowNames);
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
            Version = 3,
            AutoSelect = preferences.AutoSelect.ToString(),
            PrimaryAutosaveEnabled = preferences.PrimaryAutosaveEnabled,
            Stamp = SanitizeStamp(preferences.Stamp, preferences.AutoSelect),
            LibraryTileSize = Finite(preferences.LibraryTileSize, 112, 64, 176),
            LibraryShowNames = preferences.LibraryShowNames,
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
        public StampToolSettings? Stamp { get; set; }
        public double LibraryTileSize { get; set; } = 112;
        public bool LibraryShowNames { get; set; } = true;
    }

    private static StampToolSettings? SanitizeStamp(
        StampToolSettings? settings, PlacementAutoSelectPolicy fallbackAutoSelect)
    {
        if (settings is null) return null;
        var style = settings.Style;
        if (style is not null)
        {
            var maskSha256 = Sha256(style.MaskAssetSha256);
            style = style with
            {
                AssetSha256 = Bounded(style.AssetSha256, 64) ?? "",
                MaskAssetSha256 = maskSha256,
                ClipMaskEnabled = maskSha256 is not null && style.ClipMaskEnabled,
                Blend = BlendModes.IsSupported(style.Blend) ? style.Blend : BlendModes.Normal,
                Blur = Finite(style.Blur, 0, 0, AppearanceLimits.MaxBlur),
                ShadowOffsetX = Finite(style.ShadowOffsetX, 4,
                    -AppearanceLimits.MaxShadowOffset, AppearanceLimits.MaxShadowOffset),
                ShadowOffsetY = Finite(style.ShadowOffsetY, 4,
                    -AppearanceLimits.MaxShadowOffset, AppearanceLimits.MaxShadowOffset),
                ShadowBlur = Finite(style.ShadowBlur, 6, 0, AppearanceLimits.MaxShadowBlur),
                ShadowOpacity = Finite(style.ShadowOpacity, 0, 0, 1),
                ShadowPlacement = Enum.IsDefined(style.ShadowPlacement)
                    ? style.ShadowPlacement : StampShadowPlacement.Object,
                ShadowColor = AppearanceColor.Normalize(style.ShadowColor) ?? AppearanceColor.OpaqueBlack,
                Hue = Finite(style.Hue, 0, -180, 180),
                Saturation = Finite(style.Saturation, 0, -1, 1),
                Brightness = Finite(style.Brightness, 0, -1, 1),
                Contrast = Finite(style.Contrast, 0, -1, 1),
            };
        }

        var angle = Enum.IsDefined(settings.RandomAngleStep)
            && ((int)settings.RandomAngleStep == 0 || 360 % (int)settings.RandomAngleStep == 0)
                ? settings.RandomAngleStep : StampRandomAngleStep.Off;
        var rotation = Finite(settings.RotationDegrees, 0, -360_000, 360_000) % 360;
        if (rotation < 0) rotation += 360;
        return settings with
        {
            ScalePercent = Finite(settings.ScalePercent, 100, 10, 400),
            RotationDegrees = rotation,
            Sublayer = Math.Clamp(settings.Sublayer, -10, 10),
            ObjectOpacity = Finite(settings.ObjectOpacity, 1, 0, 1),
            Style = style,
            Mode = Enum.IsDefined(settings.Mode) ? settings.Mode : StampPlacementMode.One,
            AutoSelect = Enum.IsDefined(settings.AutoSelect) ? settings.AutoSelect : fallbackAutoSelect,
            AreaRadius = Finite(settings.AreaRadius, 160, 1, 1024),
            Density = Finite(settings.Density, 40, 0, 400),
            RandomAngleStep = angle,
            AssetSetId = Bounded(settings.AssetSetId, 160),
            VariantId = Bounded(settings.VariantId, 160),
        };
    }

    private static double Finite(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static string? Bounded(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maximumLength)];

    private static string? Sha256(string? value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit) ? normalized : null;
    }
}
