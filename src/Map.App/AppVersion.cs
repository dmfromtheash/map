using System;
using System.Reflection;

namespace Map.App;

/// <summary>Provides the product version embedded by the shared build configuration.</summary>
internal static class AppVersion
{
    public static string Current { get; } = Resolve(typeof(AppVersion).Assembly);

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
