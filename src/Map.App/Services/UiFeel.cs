using System;
using System.Runtime.InteropServices;

namespace Map.App.Services;

/// <summary>
/// The interface's "feel" (2026-10-17): motion (unfolding ornaments, the springy press, the brass
/// cursor) and the create hints. Motion follows the app settings; an automated UI run keeps it
/// off so its screenshots and timings stay deterministic, unless MAP_UI_FEEL=1 asks for it
/// explicitly. Interface sound was removed by the owner (2026-11-15).
/// </summary>
public static class UiFeel
{
    private static readonly bool ForcedInTests =
        string.Equals(Environment.GetEnvironmentVariable("MAP_UI_FEEL"), "1", StringComparison.Ordinal);

    private static bool _effects = true;

    public static bool EffectsEnabled => _effects && !SystemReducesMotion && (!TestMode.Enabled || ForcedInTests);

    /// <summary>
    /// R41: Windows «Показывать анимацию в Windows» is off - the person asked the whole system for
    /// less motion, so the interface keeps still whatever its own switch says. Read on each check,
    /// so turning it back on in Windows works without a restart.
    /// </summary>
    public static bool SystemReducesMotion
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try { return SystemParametersInfo(GetClientAreaAnimation, 0, out var on, 0) && !on; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
        }
    }

    private const uint GetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);

    /// <summary>Raised after a switch changes, so every live page can follow.</summary>
    public static event Action? Changed;

    public static void Apply(bool effects)
    {
        if (_effects == effects) return;
        _effects = effects;
        Changed?.Invoke();
    }

    private static bool _hints = true;

    /// <summary>
    /// «Нажми сюда, чтобы создать…» above every create button with a live picture of what it makes
    /// (owner, 2026-10-31: «должна быть функция отключения всех этих подсказок»). The picture moves
    /// only while <see cref="EffectsEnabled"/>; switched off, the plain button stays.
    /// </summary>
    public static bool HintsEnabled => _hints;

    /// <summary>Saves the hint choice in the app settings; set once by the main window.</summary>
    public static Action<bool>? PersistHints { get; set; }

    /// <summary>Settings or the «×» of a hint: every create hint follows at once.</summary>
    public static void SetHints(bool on, bool persist)
    {
        if (_hints != on) { _hints = on; Changed?.Invoke(); }
        if (persist) PersistHints?.Invoke(on);
    }
}
