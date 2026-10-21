using System.Diagnostics;
using Map.Core.Documents;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

/// <summary>
/// Hexes the hex tool just changed ease into their new state (owner, 2026-10-21): a placed tile
/// lands, an erased one lifts off, a region, fog or pipette touch answers with a soft ring. A fill
/// or a paste runs as a wave from the hex that was pressed. The canvas steps this with the hover.
/// </summary>
public sealed partial class EditorViewModel
{
    /// <summary>A single press: the tile lands slowly enough to be seen.</summary>
    internal const double HexLandPressMs = 260;

    /// <summary>A brush stroke: quicker, so a drag never trails far behind the pointer.</summary>
    internal const double HexLandStrokeMs = 170;

    /// <summary>Each hex of a fill or a paste: its turn comes by its distance from the pressed hex.</summary>
    internal const double HexLandWaveMs = 220, HexWaveStepMs = 32, HexWaveMaxDelayMs = 320;

    /// <summary>More hexes than this at once (a large fill, the generator) just appear.</summary>
    internal const int MaxHexSettles = 420;

    /// <summary>A brush footprint larger than this (radius 4 and up) just appears: a drag stays fast.</summary>
    internal const int MaxStrokeSettles = 37;

    private sealed class HexSettleState(LayerId layer, HexCoord hex, HexCell? from, HexCell? to, HexSettleKind kind, long start, double milliseconds)
    {
        public LayerId Layer { get; } = layer;
        public HexCoord Hex { get; } = hex;
        public HexCell? From { get; } = from;
        public HexCell? To { get; } = to;
        public HexSettleKind Kind { get; } = kind;
        public long Start { get; } = start;
        public double Milliseconds { get; } = milliseconds;
        public bool Landed { get; set; }
    }

    private readonly List<HexSettleState> _hexSettles = [];
    private HexSettles? _hexSettlesShown;

    /// <summary>The hexes easing in this frame, for the canvas.</summary>
    public HexSettles? HexSettlesShown => _hexSettlesShown;

    /// <summary>A tile touched down (0..1 picks a pitch per terrain); the app plays a soft tap.</summary>
    public event Action<double>? HexTileLanded;

    /// <summary>
    /// Queues the hexes an edit is about to change (call before it is executed, while the board
    /// still holds the old cells). <paramref name="origin"/> makes a wave from that hex.
    /// </summary>
    private void QueueHexSettles(HexLayer layer, IReadOnlyDictionary<HexCoord, HexCell?> after, HexCoord? origin, double milliseconds,
        int limit = MaxHexSettles)
    {
        if (after.Count == 0 || after.Count > limit) return;
        var now = Stopwatch.GetTimestamp();
        foreach (var (hex, to) in after)
        {
            var from = layer.Board[hex];
            if (Equals(from, to)) continue;
            var kind = to is null ? HexSettleKind.Lift
                : from is null || from.Terrain != to.Terrain || from.Variant != to.Variant || from.Rotation != to.Rotation
                    || from.Feature != to.Feature || from.FeatureVariant != to.FeatureVariant ? HexSettleKind.Land
                : HexSettleKind.Touch;
            var delay = origin is { } o ? Math.Min(HexWaveMaxDelayMs, HexCoord.Distance(o, hex) * HexWaveStepMs) : 0;
            _hexSettles.RemoveAll(settle => settle.Layer == layer.Id && settle.Hex == hex);
            _hexSettles.Add(new HexSettleState(layer.Id, hex, from, to, kind, now + (long)(delay * Stopwatch.Frequency / 1000), milliseconds));
        }

        if (_hexSettles.Count > MaxHexSettles) _hexSettles.RemoveRange(0, _hexSettles.Count - MaxHexSettles);
    }

    /// <summary>A soft ring on one hex (the pipette took its tile).</summary>
    private void QueueHexTouch(HexLayer layer, HexCoord hex)
    {
        var cell = layer.Board[hex];
        _hexSettles.RemoveAll(settle => settle.Layer == layer.Id && settle.Hex == hex);
        _hexSettles.Add(new HexSettleState(layer.Id, hex, cell, cell, HexSettleKind.Touch, Stopwatch.GetTimestamp(), 320));
    }

    /// <summary>Moves the easing hexes to <paramref name="timestamp"/>; true while any still moves.</summary>
    private bool StepHexSettles(long timestamp)
    {
        if (_hexSettles.Count == 0)
        {
            _hexSettlesShown = null;
            return false;
        }

        var layerId = ActiveHexLayer?.Id;
        var items = new List<HexSettle>(_hexSettles.Count);
        var landed = false;
        double pitch = 0;
        for (var i = _hexSettles.Count - 1; i >= 0; i--)
        {
            var settle = _hexSettles[i];
            var t = (timestamp - settle.Start) * 1000.0 / Stopwatch.Frequency / settle.Milliseconds;
            if (t >= 1 || settle.Layer != layerId)
            {
                _hexSettles.RemoveAt(i);
                continue;
            }

            if (!settle.Landed && t >= 0.6 && settle.Kind == HexSettleKind.Land)
            {
                settle.Landed = true;
                landed = true;
                pitch = (settle.To!.Terrain.GetHashCode() & 0xFFFF) / 65535.0;
            }

            items.Add(new HexSettle(settle.Hex, settle.From, settle.To, settle.Kind, t));
        }

        items.Reverse();
        _hexSettlesShown = layerId is { } id && items.Count > 0 ? new HexSettles(id, items) : null;
        if (landed) HexTileLanded?.Invoke(pitch);
        return _hexSettles.Count > 0;
    }

    /// <summary>The tile a click would put down, for the faint ghost under the pointer.</summary>
    private HexGhostTile? HexGhost()
    {
        if (_hexMode != HexToolMode.Place || _hexMix.Count > 0) return null;
        var tile = HexTile;
        var variant = _hexRandomVariant ? 0 : Math.Clamp(_hexFixedVariant, 0, Math.Max(0, tile.VariantCount - 1));
        return new HexGhostTile(tile.Id, variant, tile.Rotatable ? _hexRotation : 0, tile.Overhang);
    }
}
