using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Map.App.Services;

/// <summary>
/// Map's interface sounds - «Перо и пергамент» (owner's pick, 2026-10-17): a quill nib scratching
/// parchment over a quietly rustling sheet. No tones and nothing bright: every sound is band-limited
/// noise, shaped. The pointer reaching a command is a tiny nib touch; a press is a short stroke that
/// settles on the page; switching on is two quick strokes, off one longer stroke; a slider ticks with
/// nib touches whose pitch follows its value; a list opens and closes like a page turned. All of it
/// is synthesised here (the same formulas as the listening page the owner chose from), played
/// asynchronously through the Windows multimedia API, and silenced by one switch
/// (<see cref="UiFeel.SoundsEnabled"/>).
/// </summary>
public static class UiSound
{
    private const int SampleRate = 44100;
    private const int TickPitches = 7;

    // Quiet peaks per kind, the levels the owner listened to.
    private const double HoverPeak = 0.032, PressPeak = 0.065, OnPeak = 0.055, OffPeak = 0.045;
    private const double TickPeak = 0.03, OpenPeak = 0.04, ClosePeak = 0.035;

    private static readonly Lazy<Clip> Hover = Make(() => Scratch(201, 0.045, 2600, 90), HoverPeak);
    private static readonly Lazy<Clip> Press = Make(() => Mix(Scratch(203, 0.12, 2300, 70), Page(205, 0.10, 1300, 900, 0.01), 0.06, 0.7), PressPeak);
    private static readonly Lazy<Clip> On = Make(() => Mix(Mix(Buffer(0.22), Scratch(207, 0.07, 2200, 80), 0, 1), Scratch(209, 0.08, 2900, 90), 0.09, 1), OnPeak);
    private static readonly Lazy<Clip> Off = Make(() => Scratch(211, 0.16, 2400, 60), OffPeak);
    private static readonly Lazy<Clip> Open = Make(() => Mix(Page(215, 0.18, 800, 1900, 0.05), Scratch(217, 0.06, 2600, 80), 0.05, 0.6), OpenPeak);
    private static readonly Lazy<Clip> Close = Make(() => Mix(Scratch(219, 0.05, 2300, 70), Page(221, 0.13, 1700, 700, 0.01), 0.02, 1), ClosePeak);
    private static readonly Lazy<Clip>[] Ticks = CreateTicks();

    private static long _lastHover;
    private static long _lastTick;

    public static void PlayHover()
    {
        if (!UiFeel.SoundsEnabled) return;
        // A sweep across a column of commands would otherwise chatter.
        var now = Environment.TickCount64;
        if (now - _lastHover < 80) return;
        _lastHover = now;
        Play(Hover);
    }

    public static void PlayPress()
    {
        if (UiFeel.SoundsEnabled) Play(Press);
    }

    public static void PlayToggle(bool on)
    {
        if (UiFeel.SoundsEnabled) Play(on ? On : Off);
    }

    public static void PlayList(bool open)
    {
        if (UiFeel.SoundsEnabled) Play(open ? Open : Close);
    }

    /// <summary>A slider step; <paramref name="position"/> 0..1 picks the nib's pitch.</summary>
    public static void PlayTick(double position)
    {
        if (!UiFeel.SoundsEnabled) return;
        var now = Environment.TickCount64;
        if (now - _lastTick < 38) return;
        _lastTick = now;
        var index = (int)Math.Round(Math.Clamp(double.IsFinite(position) ? position : 0.5, 0, 1) * (TickPitches - 1));
        Play(Ticks[index]);
    }

    private static Lazy<Clip>[] CreateTicks()
    {
        var ticks = new Lazy<Clip>[TickPitches];
        for (var i = 0; i < TickPitches; i++)
        {
            var p = i / (double)(TickPitches - 1);
            var seed = 213 + i;
            ticks[i] = Make(() => Scratch(seed, 0.014, 2000 + p * 1400, 200), TickPeak);
        }
        return ticks;
    }

    private static Lazy<Clip> Make(Func<float[]> synth, double peak) => new(() => new Clip(ToWav(Level(synth(), peak))));

    /// <summary>Builds the clip on first use and plays it; audio is decoration and never throws.</summary>
    private static void Play(Lazy<Clip> clip)
    {
        try { clip.Value.Play(); }
        catch (Exception) { /* a missing device or a failed synthesis must never break a click */ }
    }

    /// <summary>A pinned WAV image: PlaySound reads it from memory while it plays.</summary>
    private sealed class Clip
    {
        private readonly GCHandle _handle;

        public Clip(byte[] wav) => _handle = GCHandle.Alloc(wav, GCHandleType.Pinned);

        public void Play()
        {
            if (!OperatingSystem.IsWindows()) return;
            PlaySound(_handle.AddrOfPinnedObject(), IntPtr.Zero, SndMemory | SndAsync | SndNoDefault);
        }
    }

    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = false)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    // ------------------------------------------------------------------ synthesis

    /// <summary>The listening page's generator: a 32-bit LCG giving values in -1..1.</summary>
    private sealed class Lcg(int seed)
    {
        private uint _state = seed == 0 ? 1u : unchecked((uint)seed);

        public double Next()
        {
            _state = unchecked(_state * 1664525u + 1013904223u);
            return _state / 4294967296.0 * 2 - 1;
        }
    }

    private static float[] Buffer(double seconds) => new float[(int)Math.Ceiling(SampleRate * seconds)];

    private static float[] Noise(double seconds, int seed)
    {
        var random = new Lcg(seed);
        var b = Buffer(seconds);
        for (var i = 0; i < b.Length; i++) b[i] = (float)random.Next();
        return b;
    }

    /// <summary>RBJ band-pass (constant 0 dB peak); the centre may move with time.</summary>
    private static float[] BandPass(float[] x, Func<double, double> centre, double q)
    {
        var y = new float[x.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var w = 2 * Math.PI * centre(i / (double)SampleRate) / SampleRate;
            var alpha = Math.Sin(w) / (2 * q);
            var cos = Math.Cos(w);
            var a0 = 1 + alpha;
            var v = (alpha * x[i] - alpha * x2 + 2 * cos * y1 - (1 - alpha) * y2) / a0;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = v;
            y[i] = (float)v;
        }
        return y;
    }

    /// <summary>Quiet paper: a slow brush through a band that slides from one centre to another.</summary>
    private static float[] Page(int seed, double seconds, double from, double to, double attack)
    {
        var y = BandPass(Noise(seconds, seed), t => from + (to - from) * Math.Min(1, t / seconds), 0.6);
        for (var i = 0; i < y.Length; i++)
        {
            var t = i / (double)SampleRate;
            y[i] *= (float)(Math.Min(1, t / attack) * Math.Pow(Math.Max(0, 1 - t / seconds), 1.3));
        }
        return y;
    }

    /// <summary>A quill nib on parchment: a narrow band of noise chopped into tiny scratchy grains.</summary>
    private static float[] Scratch(int seed, double seconds, double centre, double grainHz)
    {
        var random = new Lcg(seed);
        var n = BandPass(Noise(seconds, seed), _ => centre, 1.6);
        var y = new float[n.Length];
        double grain = 0;
        var next = 0;
        for (var i = 0; i < n.Length; i++)
        {
            if (i >= next)
            {
                grain = 0.3 + Math.Abs(random.Next()) * 0.7;
                next = i + (int)Math.Round(SampleRate / (grainHz * (0.6 + Math.Abs(random.Next()) * 0.8)));
            }
            var t = i / (double)SampleRate;
            y[i] = (float)(n[i] * grain * Math.Min(1, t / 0.006) * Math.Pow(Math.Max(0, 1 - t / seconds), 1.2));
        }
        return y;
    }

    private static float[] Mix(float[] into, float[] source, double at, double gain)
    {
        var offset = (int)Math.Round(at * SampleRate);
        for (var i = 0; i < source.Length && offset + i < into.Length; i++)
            into[offset + i] += (float)(source[i] * gain);
        return into;
    }

    /// <summary>Every kind of sound gets its own quiet peak, whatever its raw level.</summary>
    private static float[] Level(float[] x, double peak)
    {
        var max = 0.0;
        foreach (var v in x) max = Math.Max(max, Math.Abs(v));
        if (max > 0)
            for (var i = 0; i < x.Length; i++) x[i] = (float)(x[i] * peak / max);
        return x;
    }

    private static byte[] ToWav(float[] samples)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var dataBytes = samples.Length * 2;
        writer.Write("RIFF"u8); writer.Write(36 + dataBytes); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(SampleRate); writer.Write(SampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(dataBytes);
        foreach (var sample in samples)
            writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
        writer.Flush();
        return stream.ToArray();
    }
}
