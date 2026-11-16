global using Map.Tests;

namespace Map.Tests;

// xUnit v2 decides skips at discovery, before the test body runs. Keep manual diagnostics
// available without counting an absent local output directory/library as a passing test.
public sealed class OptInFactAttribute : Xunit.FactAttribute
{
    public OptInFactAttribute(string purpose, params string[] variables)
    {
        var missing = variables.Where(variable => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable))).ToArray();
        if (missing.Length > 0)
            Skip = "defect: opt-in diagnostic needs local configuration for " + purpose + "; set " + string.Join(", ", missing);
    }
}
