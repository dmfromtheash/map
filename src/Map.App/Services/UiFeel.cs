using System;
using System.Runtime.InteropServices;

namespace Map.App.Services;

/// <summary>
/// The two switches of the interface's "feel" (2026-10-17): sounds and motion (unfolding
/// ornaments, the springy press, the brass cursor). Both follow the app settings; an automated
/// UI run keeps them off so its screenshots and timings stay deterministic, unless MAP_UI_FEEL=1
/// asks for them explicitly.
/// </summary>
public static class UiFeel
{
    private static readonly bool ForcedInTests =
        string.Equals(Environment.GetEnvironmentVariable("MAP_UI_FEEL"), "1", StringComparison.Ordinal);

    private static bool _sounds = true;
    private static bool _effects = true;

    public static bool SoundsEnabled => _sounds && (!TestMode.Enabled || ForcedInTests);
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

    /// <summary>The owner's choice for sounds, independent of the test-run gate.</summary>
    public static bool SoundsSetting => _sounds;

    /// <summary>Raised after a switch changes, so every sound button and live page can follow.</summary>
    public static event Action? Changed;

    /// <summary>Saves the sound choice in the app settings; set once by the main window.</summary>
    public static Action<bool>? PersistSounds { get; set; }

    public static void Apply(bool sounds, bool effects)
    {
        if (_sounds == sounds && _effects == effects) return;
        _sounds = sounds;
        _effects = effects;
        Changed?.Invoke();
    }

    /// <summary>The one switch at the top of the window: all interface sounds on or off.</summary>
    public static void SetSounds(bool on)
    {
        Apply(on, _effects);
        PersistSounds?.Invoke(on);
    }
}
