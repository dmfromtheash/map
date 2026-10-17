using System.Text.Json;

namespace Map.Platform.Windows;

public enum LocalThemeMode { System, Dark, Light }
public enum CanvasPerformanceMode { Balanced, Quality, Performance }

/// <summary>Application-local settings. They never enter a .mapproj or a map history.</summary>
public sealed record AppSettings
{
    public const double MinUiScale = 0.8;
    public const double MaxUiScale = 2.0;
    public const int MinAutosaveSeconds = 10;
    public const int MaxAutosaveSeconds = 3600;
    public const int MinRecoveryItems = 1;
    public const int MaxRecoveryItems = 50;

    public LocalThemeMode Theme { get; init; } = LocalThemeMode.System;
    public string Language { get; init; } = "ru";
    public double UiScale { get; init; } = 1;
    public int AutosaveIntervalSeconds { get; init; } = 60;
    public int RecoveryItemBudget { get; init; } = 12;
    public CanvasPerformanceMode PerformanceMode { get; init; } = CanvasPerformanceMode.Balanced;
    public string? LibraryRoot { get; init; }
    public string? CacheRoot { get; init; }
    public string DefaultExportFormat { get; init; } = "Png";
    public int DefaultExportScalePercent { get; init; } = 100;
    public bool DefaultExportIncludeGrid { get; init; }
    /// <summary>Short synthesised sounds when the pointer reaches or presses a command.</summary>
    public bool InterfaceSounds { get; init; } = true;
    /// <summary>Interface motion: drifting embers, unfolding ornaments, springy press, brass cursor.</summary>
    public bool InterfaceEffects { get; init; } = true;

    public static AppSettings Sanitize(AppSettings? value)
    {
        value ??= new AppSettings();
        return value with
        {
            Theme = Enum.IsDefined(value.Theme) ? value.Theme : LocalThemeMode.System,
            Language = "ru",
            UiScale = double.IsFinite(value.UiScale)
                ? Math.Clamp(value.UiScale, MinUiScale, MaxUiScale) : 1,
            AutosaveIntervalSeconds = Math.Clamp(
                value.AutosaveIntervalSeconds, MinAutosaveSeconds, MaxAutosaveSeconds),
            RecoveryItemBudget = Math.Clamp(
                value.RecoveryItemBudget, MinRecoveryItems, MaxRecoveryItems),
            PerformanceMode = Enum.IsDefined(value.PerformanceMode)
                ? value.PerformanceMode : CanvasPerformanceMode.Balanced,
            LibraryRoot = SafeRoot(value.LibraryRoot),
            CacheRoot = SafeRoot(value.CacheRoot),
            DefaultExportFormat = value.DefaultExportFormat is "Png" or "Jpeg" or "Webp"
                ? value.DefaultExportFormat : "Png",
            DefaultExportScalePercent = Math.Clamp(value.DefaultExportScalePercent, 10, 800),
        };
    }

    private static string? SafeRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar);
            var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar);
            return Path.IsPathFullyQualified(full)
                && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                ? full : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

/// <summary>Bounded atomic JSON store for <see cref="AppSettings"/>.</summary>
public sealed class AppSettingsStore
{
    private const int MaxBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly string _path;

    public AppSettingsStore(string? path = null)
    {
        _path = System.IO.Path.GetFullPath(path ?? AppPaths.AppSettingsPath);
    }

    public string Path => _path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            var info = new FileInfo(_path);
            if (info.Length is <= 0 or > MaxBytes) return new AppSettings();
            return AppSettings.Sanitize(JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllBytes(_path), JsonOptions));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public AppSettings Save(AppSettings settings)
    {
        var sanitized = AppSettings.Sanitize(settings);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(sanitized, JsonOptions);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_path)) File.Replace(temp, _path, _path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temp, _path);
            return sanitized;
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public AppSettings Reset() => Save(new AppSettings());
}
