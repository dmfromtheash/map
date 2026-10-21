using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Map.App.Services;

/// <summary>
/// Map's interface sounds - «Перо и пергамент», softened (owner, 2026-10-17: "рваный и грубый,
/// нужно мягче и плавнее"). Still no tones, only shaped noise, but low and round: the pointer
/// reaching a command is a faint breath of paper, a press a soft felt tap with a little paper
/// after it, switching on two soft taps rising, off one lower tap, a slider ticks with tiny taps
/// whose pitch follows its value, a list opens and closes like a page sliding. The former nib
/// "grains" (noise chopped into pieces at 2.3-2.9 kHz) were the roughness and are gone: every
/// sound now has a raised-cosine attack, a smooth decay that ends in silence, and a gentle
/// low-pass. All of it plays through one continuous output stream (<see cref="UiAudioOutput"/>),
/// so sounds overlap instead of cutting each other off, and one switch silences it
/// (<see cref="UiFeel.SoundsEnabled"/>).
/// </summary>
public static class UiSound
{
    private const int SampleRate = UiAudioOutput.SampleRate;
    private const int TickPitches = 7;

    // Quiet peaks per kind: a little below the former levels, the press the loudest.
    private const double HoverPeak = 0.02, PressPeak = 0.05, OnPeak = 0.045, OffPeak = 0.038;
    private const double TickPeak = 0.02, OpenPeak = 0.034, ClosePeak = 0.03;

    private static readonly Lazy<Clip> Hover = Make(() => Soft(201, 0.07, 1400, 0.9, 0.018), HoverPeak);
    private static readonly Lazy<Clip> Press = Make(() => Mix(Soft(203, 0.10, 720, 0.8, 0.005), Soft(205, 0.16, 1250, 0.7, 0.025), 0.012, 0.45), PressPeak);
    private static readonly Lazy<Clip> On = Make(() => Mix(Mix(Buffer(0.22), Soft(207, 0.09, 820, 0.9, 0.006), 0, 1), Soft(209, 0.10, 1150, 0.9, 0.006), 0.085, 0.9), OnPeak);
    private static readonly Lazy<Clip> Off = Make(() => Soft(211, 0.15, 650, 0.8, 0.008), OffPeak);
    private static readonly Lazy<Clip> Open = Make(() => Mix(Page(215, 0.22, 550, 1150, 0.05), Soft(217, 0.07, 1000, 0.9, 0.008), 0.03, 0.5), OpenPeak);
    private static readonly Lazy<Clip> Close = Make(() => Mix(Soft(219, 0.06, 950, 0.9, 0.006), Page(221, 0.16, 1050, 550, 0.02), 0.01, 1), ClosePeak);
    private static readonly Lazy<Clip>[] Ticks = CreateTicks();
    private static readonly Lazy<Clip>[] Places = CreatePlaces();
    private const double PlacePeak = 0.03;
    private const int PlacePitches = 5;
    private static long _lastPlace;

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

    /// <summary>A hex tile touching down on the map; <paramref name="pitch"/> 0..1 picks one of a few soft taps.</summary>
    public static void PlayPlace(double pitch)
    {
        if (!UiFeel.SoundsEnabled) return;
        // A brush stroke lands many tiles: at most about fourteen taps a second.
        var now = Environment.TickCount64;
        if (now - _lastPlace < 70) return;
        _lastPlace = now;
        var index = (int)Math.Round(Math.Clamp(double.IsFinite(pitch) ? pitch : 0.5, 0, 1) * (PlacePitches - 1));
        Play(Places[index]);
    }

    private static Lazy<Clip>[] CreatePlaces()
    {
        var places = new Lazy<Clip>[PlacePitches];
        for (var i = 0; i < PlacePitches; i++)
        {
            var p = i / (double)(PlacePitches - 1);
            var seed = 231 + i;
            // A low wooden "tok": a short body and a quieter, brighter click on top.
            places[i] = Make(() => Mix(Soft(seed, 0.07, 380 + p * 160, 0.85, 0.003), Soft(seed + 40, 0.03, 1100 + p * 300, 0.9, 0.002), 0, 0.35), PlacePeak);
        }

        return places;
    }

    private static Lazy<Clip>[] CreateTicks()
    {
        var ticks = new Lazy<Clip>[TickPitches];
        for (var i = 0; i < TickPitches; i++)
        {
            var p = i / (double)(TickPitches - 1);
            var seed = 213 + i;
            ticks[i] = Make(() => Soft(seed, 0.035, 850 + p * 650, 1.0, 0.004), TickPeak);
        }
        return ticks;
    }

    private static Lazy<Clip> Make(Func<float[]> synth, double peak) => new(() => new Clip(Level(Smooth(synth()), peak)));

    /// <summary>Builds the clip on first use and plays it; audio is decoration and never throws.</summary>
    private static void Play(Lazy<Clip> clip)
    {
        try { clip.Value.Play(); }
        catch (Exception) { /* a missing device or a failed synthesis must never break a click */ }
    }

    /// <summary>
    /// A synthesised sound. It is mixed into the one interface stream; PlaySound (one stream per
    /// sound) remains only as the fallback when that stream cannot be opened.
    /// </summary>
    private sealed class Clip(float[] samples)
    {
        private GCHandle _wav;

        public void Play()
        {
            if (!OperatingSystem.IsWindows() || UiAudioOutput.Play(samples)) return;
            if (!_wav.IsAllocated) _wav = GCHandle.Alloc(ToWav(samples), GCHandleType.Pinned);
            PlaySound(_wav.AddrOfPinnedObject(), IntPtr.Zero, SndMemory | SndAsync | SndNoDefault);
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
            y[i] *= (float)(Rise(t, attack) * Math.Pow(Math.Max(0, 1 - t / seconds), 1.6));
        }
        return y;
    }

    /// <summary>A raised-cosine rise from 0 to 1 over <paramref name="attack"/> seconds: no step at the start.</summary>
    private static double Rise(double t, double attack) => t >= attack ? 1 : 0.5 - 0.5 * Math.Cos(Math.PI * t / attack);

    /// <summary>
    /// A soft tap or breath: band-passed noise with a raised-cosine attack and a rounded decay to
    /// silence. The lower the centre and the wider the band, the softer it reads.
    /// </summary>
    private static float[] Soft(int seed, double seconds, double centre, double q, double attack)
    {
        var y = BandPass(Noise(seconds, seed), _ => centre, q);
        for (var i = 0; i < y.Length; i++)
        {
            var t = i / (double)SampleRate;
            var tail = Math.Max(0, 1 - t / seconds);
            y[i] *= (float)(Rise(t, attack) * tail * tail * (3 - 2 * tail));
        }
        return y;
    }

    /// <summary>
    /// The finish every sound gets: a two-pole low-pass at 3.2 kHz (no hiss, no edge) and a 6 ms
    /// cosine fade at the very end, so even a mix of sounds stops on silence.
    /// </summary>
    private static float[] Smooth(float[] x)
    {
        var w = 2 * Math.PI * 3200 / SampleRate;
        var alpha = Math.Sin(w) / (2 * 0.707);
        var cos = Math.Cos(w);
        var a0 = 1 + alpha;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var v = ((1 - cos) / 2 * x[i] + (1 - cos) * x1 + (1 - cos) / 2 * x2 + 2 * cos * y1 - (1 - alpha) * y2) / a0;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = v;
            x[i] = (float)v;
        }
        var fade = Math.Min(x.Length, (int)(SampleRate * 0.006));
        for (var i = 0; i < fade; i++)
            x[x.Length - 1 - i] *= (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / fade));
        return x;
    }

    /// <summary>A quill nib on parchment: a narrow band of noise chopped into tiny scratchy grains (no longer used; kept for the listening page's formulas).</summary>
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
