using System;
using System.Reflection;

namespace Map.App;

/// <summary>Provides the product version embedded by the shared build configuration.</summary>
internal static class AppVersion
{
    public static string Current { get; } = Resolve(typeof(AppVersion).Assembly);

    /// <summary>Short Git commit of the build (the SDK appends it to the informational version), or "".</summary>
    public static string Commit { get; } = ResolveCommit(typeof(AppVersion).Assembly);

    /// <summary>Version with the commit for the window title: "0.3.0 (18975bd)".</summary>
    public static string Display { get; } = Commit.Length == 0 ? Current : $"{Current} ({Commit})";

    internal static string ResolveCommit(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var metadataSeparator = informational?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        if (metadataSeparator < 0) return "";
        var commit = informational![(metadataSeparator + 1)..];
        return commit.Length > 7 ? commit[..7] : commit;
    }

    internal static string Resolve(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var metadataSeparator = informational.IndexOf('+', StringComparison.Ordinal);
            return metadataSeparator >= 0 ? informational[..metadataSeparator] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
