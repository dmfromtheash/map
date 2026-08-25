using System;

namespace Map.App.Services;

/// <summary>
/// Explicit switch for controls that exist only to drive automated smokes - a font-substitution
/// probe, for instance. They are never part of the interface a real user sees; the UIA driver
/// turns them on with MAP_UI_TEST_MODE=1 before starting the process.
/// </summary>
public static class TestMode
{
    public const string Variable = "MAP_UI_TEST_MODE";

    private static readonly bool Value =
        string.Equals(Environment.GetEnvironmentVariable(Variable), "1", StringComparison.Ordinal);

    public static bool Enabled => Value;
}
