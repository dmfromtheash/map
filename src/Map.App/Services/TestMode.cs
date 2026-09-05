using System;
using System.IO;

namespace Map.App.Services;

/// <summary>
/// Explicit switch for controls that exist only to drive automated smokes - a font-substitution
/// probe, for instance. They are never part of the interface a real user sees; the UIA driver
/// turns them on with MAP_UI_TEST_MODE=1 before starting the process.
/// </summary>
public static class TestMode
{
    public const string Variable = "MAP_UI_TEST_MODE";
    public const string ProfileRootVariable = "MAP_UI_TEST_PROFILE_ROOT";
    public const string RecoveryRootVariable = "MAP_UI_TEST_RECOVERY_ROOT";
    public const string ShortcutSettingsPathVariable = "MAP_UI_TEST_SHORTCUT_SETTINGS_PATH";

    private static readonly bool Value =
        string.Equals(Environment.GetEnvironmentVariable(Variable), "1", StringComparison.Ordinal);

    public static bool Enabled => Value;
    public static bool StartupSmoke => Enabled && string.Equals(
        Environment.GetEnvironmentVariable("MAP_UI_TEST_STARTUP_SMOKE"), "1", StringComparison.Ordinal);

    public static TimeSpan AutosaveInterval => TimeSpan.FromSeconds(1);

    /// <summary>
    /// Existing UI drivers use test mode without a recovery root. They must never write test
    /// documents into the user's real recovery root; the Stage 9 driver opts in with an explicit
    /// isolated root.
    /// </summary>
    public static bool AutosaveEnabled => !Enabled || RecoveryRootOverride is not null;

    public static string? ProfileRootOverride => Enabled
        ? Environment.GetEnvironmentVariable(ProfileRootVariable)
        : null;

    public static string? RecoveryRootOverride
    {
        get
        {
            if (!Enabled)
            {
                return null;
            }

            var value = Environment.GetEnvironmentVariable(RecoveryRootVariable);
            return string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
                ? null
                : Path.GetFullPath(value);
        }
    }

    public static string? ShortcutSettingsPathOverride
    {
        get
        {
            if (!Enabled)
            {
                return null;
            }

            var value = Environment.GetEnvironmentVariable(ShortcutSettingsPathVariable);
            return string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
                ? null
                : Path.GetFullPath(value);
        }
    }
}
