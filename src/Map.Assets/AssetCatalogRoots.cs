namespace Map.Assets;

/// <summary>
/// Where a catalog is actually opened. The native UI driver must be able to exercise import,
/// migration and damaged-index paths without ever touching the owner's real library, and the
/// composition root that constructs the catalog is outside this slice. The redirect therefore
/// lives here and follows the same shape the recovery sandbox already uses: it applies only when
/// the UI test switch is on AND an explicit fully-qualified sandbox root is supplied.
/// </summary>
public static class AssetCatalogRoots
{
    public const string TestModeVariable = "MAP_UI_TEST_MODE";
    public const string SandboxRootVariable = "MAP_UI_TEST_ASSET_ROOT";

    /// <summary>The sandbox root, or null whenever the redirect is not unambiguously requested.</summary>
    public static string? SandboxRoot
    {
        get
        {
            if (!string.Equals(
                Environment.GetEnvironmentVariable(TestModeVariable), "1", StringComparison.Ordinal))
            {
                return null;
            }

            var value = Environment.GetEnvironmentVariable(SandboxRootVariable);
            return string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
                ? null
                : Path.GetFullPath(value);
        }
    }

    public static string Resolve(string requestedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedRoot);
        return SandboxRoot ?? Path.GetFullPath(requestedRoot);
    }
}
