using System.Diagnostics;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// What the hex canvas eases instead of switching at once (owner, 2026-10-21): a new chunk picture
/// shows over the one that stood in for it, revealed or hidden hexes melt in and out of the fog,
/// and the far and the near look hand over when the zoom crosses between them. Nothing here is
/// drawn into a kept picture; while any of it runs the frame asks for the next one.
/// </summary>
public sealed partial class HexChunkCache
{
    /// <summary>How long a new chunk picture takes to show over the one that stood in for it.</summary>
    public const double SwapFadeMilliseconds = 140;

    /// <summary>How long a hex takes to come out of the fog or to go back under it.</summary>
    public const double FogFadeMilliseconds = 260;

    /// <summary>How long the near look takes to fade into the far (flat colour) one on a zoom out.</summary>
    public const double LookFadeMilliseconds = 220;

    /// <summary>More hexes than this changing fog at once (a whole level, a new board) switch at once.</summary>
    private const int MaxFogFades = 2048;

    private readonly Dictionary<Place, (SKImage Image, int Level)> _shownStandIns = [];
    private readonly Dictionary<Place, (SKImage From, int FromLevel, long Start)> _swaps = [];
    private readonly Dictionary<(LayerId Layer, HexChunkKey Key), HexChunk?> _fogSeen = [];
    private readonly Dictionary<(LayerId Layer, HexChunkKey Key), List<(HexCoord Hex, bool Revealed, long Start)>> _fogFades = [];
    private readonly Dictionary<LayerId, (bool Flat, bool From, long Since)> _looks = [];
    private int _fogFadeCount;
    private (int Shown, int From, long Since)? _level;
    private long _animateUntil;

    /// <summary>The clock every eased change is timed by.</summary>
    public static long Now => Stopwatch.GetTimestamp();

    /// <summary>True while something eased is still moving and the canvas should draw another frame.</summary>
    public bool Animating(long now) => now < _animateUntil;

    internal static double Progress(long start, long now, double milliseconds) =>
        Math.Clamp((now - start) * 1000.0 / Stopwatch.Frequency / milliseconds, 0, 1);

    /// <summary>Smooth start and end (smoothstep).</summary>
    internal static double Ease(double t) => t * t * (3 - 2 * t);

    private void KeepAnimating(long now, double milliseconds)
    {
        var until = now + (long)(milliseconds * Stopwatch.Frequency / 1000) + 1;
        if (until > _animateUntil) _animateUntil = until;
    }

    /// <summary>Something always moving (the living world) was drawn: ask for the next frame too.</summary>
    internal void KeepAlive(long now) => KeepAnimating(now, 40);

    /// <summary>A stand-in was drawn at a place this frame: when the right picture arrives it fades in over it.</summary>
    internal void NoteStandIn(Place place, SKImage image, int level)
    {
        if (_shownStandIns.Count > 4096 && !_shownStandIns.ContainsKey(place)) _shownStandIns.Clear();
        _shownStandIns[place] = (image, level);
    }

    /// <summary>
    /// The picture that stood in at a place and how far the new <paramref name="image"/> has faded
    /// in over it (0..1), or null when the new picture shows alone.
    /// </summary>
    internal (SKImage From, int FromLevel, double T)? Swap(Place place, SKImage image, long now)
    {
        if (_shownStandIns.Remove(place, out var shown) && !ReferenceEquals(shown.Image, image))
        {
            _swaps[place] = (shown.Image, shown.Level, now);
            KeepAnimating(now, SwapFadeMilliseconds);
        }

        if (!_swaps.TryGetValue(place, out var swap)) return null;
        var t = Progress(swap.Start, now, SwapFadeMilliseconds);
        // A stand-in the budget has let go of since is not drawn again.
        if (t >= 1 || swap.From.Handle == IntPtr.Zero)
        {
            _swaps.Remove(place);
            return null;
        }

        return (swap.From, swap.FromLevel, t);
    }

    /// <summary>
    /// The hexes of a chunk melting into or out of the fog and how far they are (0..1). A chunk
    /// seen before in another version is compared hex by hex with it; one seen for the first time
    /// just shows as it is.
    /// </summary>
    internal List<(HexCoord Hex, bool Revealed, double T)>? FogFades(LayerId layer, HexBoard board, HexChunkKey key, HexChunk? chunk, long now)
    {
        var id = (layer, key);
        if (_fogSeen.TryGetValue(id, out var seen) && !ReferenceEquals(seen, chunk))
        {
            if (!_fogFades.TryGetValue(id, out var list)) _fogFades[id] = list = [];
            for (var row = 0; row < HexBoard.ChunkSize; row++)
            {
                for (var column = 0; column < HexBoard.ChunkSize; column++)
                {
                    var offset = new HexOffset(key.X * HexBoard.ChunkSize + column, key.Y * HexBoard.ChunkSize + row);
                    if (!board.Contains(offset)) continue;
                    var was = seen?[column, row] is { Revealed: true };
                    var shows = chunk?[column, row] is { Revealed: true };
                    if (was == shows) continue;
                    var hex = board.Layout.FromOffset(offset);
                    var at = list.FindIndex(fade => fade.Hex == hex);
                    if (at >= 0)
                    {
                        list.RemoveAt(at);
                        _fogFadeCount--;
                    }

                    list.Add((hex, shows, now));
                    _fogFadeCount++;
                }
            }

            if (_fogFadeCount > MaxFogFades)
            {
                _fogFades.Clear();
                _fogFadeCount = 0;
            }
            else if (list.Count > 0) KeepAnimating(now, FogFadeMilliseconds);
            else _fogFades.Remove(id);
        }

        if (_fogSeen.Count > 16384 && !_fogSeen.ContainsKey(id)) _fogSeen.Clear();
        _fogSeen[id] = chunk;
        if (!_fogFades.TryGetValue(id, out var fades)) return null;
        var result = new List<(HexCoord Hex, bool Revealed, double T)>(fades.Count);
        for (var i = fades.Count - 1; i >= 0; i--)
        {
            var t = Progress(fades[i].Start, now, FogFadeMilliseconds);
            if (t >= 1)
            {
                fades.RemoveAt(i);
                _fogFadeCount--;
                continue;
            }

            result.Add((fades[i].Hex, fades[i].Revealed, t));
        }

        if (fades.Count == 0) _fogFades.Remove(id);
        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// The level of a hex world shown before a switch, and how far the new one has faded in (0..1);
    /// null when no switch is under way. Asked several times a frame, it changes only when the
    /// shown level does.
    /// </summary>
    public (int From, double T)? LevelChange(int shown, long now)
    {
        if (_level is not { } state)
        {
            _level = (shown, shown, 0);
            return null;
        }

        if (state.Shown != shown)
        {
            state = (shown, state.Shown, now);
            _level = state;
        }

        if (state.Since == 0) return null;
        var t = Progress(state.Since, now, LookFadeMilliseconds);
        if (t >= 1)
        {
            _level = (shown, shown, 0);
            return null;
        }

        KeepAnimating(now, 16);
        return (state.From, t);
    }

    /// <summary>
    /// The look the layer showed before the zoom crossed between far and near, and how far the new
    /// one has faded in (0..1); null when nothing is handing over.
    /// </summary>
    internal (bool FromFlat, double T)? LookChange(LayerId layer, bool flat, long now)
    {
        if (!_looks.TryGetValue(layer, out var state))
        {
            _looks[layer] = (flat, flat, 0);
            return null;
        }

        if (state.Flat != flat)
        {
            // Only a zoom out fades (the near look into the far one). Zooming in, the far look would
            // have to be drawn again at the new scale under the fade: 42-46 → 24-35 FPS measured.
            state = flat ? (flat, state.Flat, now) : (flat, flat, 0);
            _looks[layer] = state;
        }

        if (state.Since == 0) return null;
        var t = Progress(state.Since, now, LookFadeMilliseconds);
        if (t >= 1)
        {
            _looks[layer] = (flat, flat, 0);
            return null;
        }

        KeepAnimating(now, 16);
        return (state.From, t);
    }
}
