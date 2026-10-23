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
    public static string? TestRoot => TestRootFrom(Environment.GetEnvironmentVariable);

    public static string Resolve(string requestedRoot) => Resolve(requestedRoot, Environment.GetEnvironmentVariable);

    /// <summary>
    /// The same rule over a given set of variables. Tests use it instead of changing the process
    /// environment: a test that set these two variables made every catalog opened by a parallel
    /// test read its root instead (a damaged index once came back Ready in CI, R05 2026-10-23).
    /// </summary>
    internal static string Resolve(string requestedRoot, Func<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedRoot);
        return TestRootFrom(environment) ?? Path.GetFullPath(requestedRoot);
    }

    private static string? TestRootFrom(Func<string, string?> environment)
    {
        if (!string.Equals(environment(TestModeVariable), "1", StringComparison.Ordinal))
        {
            return null;
        }

        var value = environment(TestRootVariable);
        return string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
            ? null
            : Path.GetFullPath(value);
    }
}
