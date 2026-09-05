namespace Map.Assets;

/// <summary>
/// Where a catalog is actually opened. The native UI driver must be able to exercise import,
/// migration and damaged-index paths without ever touching the owner's real library, and the
/// composition root that constructs the catalog is outside this slice. The redirect therefore
/// lives here and follows the same shape as the recovery test root: it applies only when
/// the UI test switch is on AND an explicit fully-qualified test root is supplied.
/// </summary>
public static class AssetCatalogRoots
{
    public const string TestModeVariable = "MAP_UI_TEST_MODE";
    public const string TestRootVariable = "MAP_UI_TEST_ASSET_ROOT";

    /// <summary>The test root, or null whenever the redirect is not unambiguously requested.</summary>
    public static string? TestRoot
    {
        get
        {
            if (!string.Equals(
                Environment.GetEnvironmentVariable(TestModeVariable), "1", StringComparison.Ordinal))
            {
                return null;
            }

            var value = Environment.GetEnvironmentVariable(TestRootVariable);
            return string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
                ? null
                : Path.GetFullPath(value);
        }
    }

    public static string Resolve(string requestedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedRoot);
        return TestRoot ?? Path.GetFullPath(requestedRoot);
    }
}
