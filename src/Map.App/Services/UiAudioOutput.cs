using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Map.App.Services;

/// <summary>
/// One continuous output stream for the interface sounds. PlaySound opened and closed a stream for
/// every 45 ms clip; on Bluetooth headphones (the owner's AirPods Pro) each of those is a start and
/// a stop of the A2DP link, which the headphones answer with a fade-in and a fade-out - after a
/// while of moving the pointer over buttons the user's other audio came out muffled or cut, and
/// the short sounds themselves were lost. Here the stream opens with the first sound, plays
/// silence between sounds, mixes overlapping sounds, and closes only after half a minute without
/// any, so the link starts once per working session, not once per hover.
/// </summary>
internal static class UiAudioOutput
{
    public const int SampleRate = 48000;
    private const int Channels = 2;
    private const int FramesPerBuffer = 720;      // 15 ms
    private const int BufferCount = 4;            // ~60 ms queued: low enough for a hover
    private static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(30);

    private sealed class Voice(float[] samples, float gain)
    {
        public readonly float[] Samples = samples;
        public readonly float Gain = gain;
        public int Position;
    }

    private static readonly object Gate = new();
    private static readonly List<Voice> Voices = [];
    private static Thread? _thread;
    private static long _lastSound;
    private static volatile bool _failed;
    // A driver that refuses to close may still signal its callback. Keep that event alive.
    private static AutoResetEvent? _unclosedDeviceEvent;

    /// <summary>Queues a mono clip at <see cref="SampleRate"/>; false when no device could be opened.</summary>
    public static bool Play(float[] mono, float gain = 1)
    {
        if (!OperatingSystem.IsWindows() || _failed) return false;
        lock (Gate)
        {
            if (_failed) return false;
            // A burst of the same clip (a slider ticking) keeps at most a few voices.
            if (Voices.Count >= 8) Voices.RemoveAt(0);
            Voices.Add(new Voice(mono, gain));
            _lastSound = Environment.TickCount64;
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "Map UI audio", Priority = ThreadPriority.AboveNormal };
                _thread.Start();
            }
        }
        return true;
    }

    private static void Run()
    {
        IntPtr device = IntPtr.Zero;
        var headers = new IntPtr[BufferCount];
        var buffers = new IntPtr[BufferCount];
        var prepared = new bool[BufferCount];
        var ready = new AutoResetEvent(false);
        var headerSize = Marshal.SizeOf<WaveHeader>();
        try
        {
            var format = new WaveFormatEx
            {
                wFormatTag = 1, nChannels = Channels, nSamplesPerSec = SampleRate,
                wBitsPerSample = 16, nBlockAlign = Channels * 2, nAvgBytesPerSec = SampleRate * Channels * 2, cbSize = 0,
            };
            if (waveOutOpen(out device, WaveMapper, ref format, ready.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent) != 0)
            {
                _failed = true;
                return;
            }
            var bytes = FramesPerBuffer * Channels * 2;
            for (var i = 0; i < BufferCount; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(bytes);
                headers[i] = Marshal.AllocHGlobal(headerSize);
                Marshal.StructureToPtr(new WaveHeader { lpData = buffers[i], dwBufferLength = (uint)bytes }, headers[i], false);
                if (waveOutPrepareHeader(device, headers[i], (uint)headerSize) != 0)
                {
                    _failed = true;
                    return;
                }
                prepared[i] = true;
                Fill(buffers[i]);
                if (waveOutWrite(device, headers[i], (uint)headerSize) != 0)
                {
                    _failed = true;
                    return;
                }
            }

            var flagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.dwFlags));
            while (true)
            {
                ready.WaitOne(100);
                lock (Gate)
                {
                    if (Voices.Count == 0 && Environment.TickCount64 - _lastSound > IdleClose.TotalMilliseconds)
                    {
                        break;
                    }
                }
                foreach (var header in headers)
                {
                    if ((Marshal.ReadInt32(header, flagsOffset) & WhdrDone) == 0) continue;
                    Fill(Marshal.ReadIntPtr(header));
                    Marshal.WriteInt32(header, flagsOffset, Marshal.ReadInt32(header, flagsOffset) & ~WhdrDone);
                    if (waveOutWrite(device, header, (uint)headerSize) != 0)
                    {
                        _failed = true;
                        return;
                    }
                }
            }
        }
        catch (Exception)
        {
            _failed = true;
        }
        finally
        {
            var closed = device == IntPtr.Zero;
            try
            {
                if (!closed)
                {
                    if (waveOutReset(device) != 0) _failed = true;
                    for (var i = 0; i < BufferCount; i++)
                    {
                        if (!prepared[i]) continue;
                        var result = waveOutUnprepareHeader(device, headers[i], (uint)headerSize);
                        if (result != 0) _failed = true;
                        // If reset failed, allow the few queued buffers to finish naturally.
                        for (var retry = 0; result == WaveStillPlaying && retry <= BufferCount; retry++)
                        {
                            ready.WaitOne(100);
                            result = waveOutUnprepareHeader(device, headers[i], (uint)headerSize);
                        }
                        if (result == 0) prepared[i] = false;
                    }
                    closed = waveOutClose(device) == 0;
                    if (!closed) _failed = true;
                }
            }
            catch (Exception) { _failed = true; }
            try
            {
                for (var i = 0; i < BufferCount; i++)
                {
                    // Never free memory that a driver which failed reset/close may still use.
                    if (prepared[i] && !closed) continue;
                    if (buffers[i] != IntPtr.Zero) Marshal.FreeHGlobal(buffers[i]);
                    if (headers[i] != IntPtr.Zero) Marshal.FreeHGlobal(headers[i]);
                }
                if (closed) ready.Dispose();
                else _unclosedDeviceEvent = ready;
                // A sound queued in the moment the stream closed starts it again.
                lock (Gate)
                {
                    _thread = null;
                    if (_failed) Voices.Clear();
                    else if (Voices.Count > 0)
                    {
                        _thread = new Thread(Run) { IsBackground = true, Name = "Map UI audio", Priority = ThreadPriority.AboveNormal };
                        _thread.Start();
                    }
                }
            }
            catch (Exception) { _failed = true; }
        }
    }

    private static readonly float[] Mix = new float[FramesPerBuffer];
    private static readonly short[] Pcm = new short[FramesPerBuffer * Channels];

    /// <summary>The next 15 ms: every playing voice summed, silence where there is none.</summary>
    private static void Fill(IntPtr data)
    {
        Array.Clear(Mix);
        lock (Gate)
        {
            for (var v = Voices.Count - 1; v >= 0; v--)
            {
                var voice = Voices[v];
                var count = Math.Min(FramesPerBuffer, voice.Samples.Length - voice.Position);
                for (var i = 0; i < count; i++) Mix[i] += voice.Samples[voice.Position + i] * voice.Gain;
                voice.Position += count;
                if (voice.Position >= voice.Samples.Length) Voices.RemoveAt(v);
            }
        }
        for (var i = 0; i < FramesPerBuffer; i++)
        {
            var s = (short)Math.Round(Math.Clamp(Mix[i], -1f, 1f) * short.MaxValue);
            Pcm[i * 2] = s;
            Pcm[i * 2 + 1] = s;
        }
        Marshal.Copy(Pcm, 0, data, Pcm.Length);
    }

    private const uint WaveMapper = unchecked((uint)-1);
    private const uint CallbackEvent = 0x00050000;
    private const int WhdrDone = 0x00000001;
    private const int WaveStillPlaying = 33;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public int dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr hwo, uint device, ref WaveFormatEx format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hwo, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hwo);
}
