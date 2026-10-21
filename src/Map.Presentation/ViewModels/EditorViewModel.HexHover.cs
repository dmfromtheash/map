using System.Diagnostics;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

/// <summary>
/// The hex hover outline eased frame by frame ("Шёлк", owner 2026-10-21). Nothing switches when
/// the pointer crosses a hex border: the pointer is followed by a critically damped spring, every
/// hex round it gets a strength from its distance to that point, so at a border both neighbours
/// show and hand over to each other, and each strength eases toward its target.
/// </summary>
public sealed partial class EditorViewModel
{
    /// <summary>How long the eased point takes to catch the pointer.</summary>
    internal const double HexHoverFollowMs = 70;

    /// <summary>How long a hex takes to light up and to fade.</summary>
    internal const double HexHoverRiseMs = 90, HexHoverFadeMs = 300;

    /// <summary>How wide the hand-over at a hex border is (share of the inner radius).</summary>
    internal const double HexHoverBlend = 0.7;

    private readonly Dictionary<HexCoord, (double Strength, IReadOnlyList<HexCoord> Hexes)> _hexHoverCells = [];
    private (double X, double Y, double Vx, double Vy)? _hexHoverPoint;
    private LayerId? _hexHoverLayer;
    private bool _hexHoverErase;
    private long _hexHoverStamp;
    private HexCursorPreview? _hexHoverShown;
    private (HexToolMode Mode, int Radius) _hexHoverShape;

    /// <summary>
    /// Moves the eased hover outline to <paramref name="timestamp"/> (a <see cref="Stopwatch"/> tick);
    /// true while it is still moving and the canvas should draw another frame.
    /// </summary>
    public bool StepHexHover(long timestamp)
    {
        var settling = StepHexSettles(timestamp);
        if (_hexCursor is null && _hexHoverCells.Count == 0)
        {
            _hexHoverShown = null;
            _hexHoverStamp = 0;
            return settling;
        }

        var dt = _hexHoverStamp == 0 ? 1 / 60.0 : Math.Clamp((timestamp - _hexHoverStamp) / (double)Stopwatch.Frequency, 0, 0.05);
        _hexHoverStamp = timestamp;
        var layer = _hexCursor is null ? null : ActiveHexLayer;
        if (layer is not null && _hexHoverLayer != layer.Id)
        {
            _hexHoverCells.Clear();
            _hexHoverPoint = null;
        }

        var busy = false;
        var targets = new Dictionary<HexCoord, double>();
        if (layer is not null && _hexCursor is not null && _hexHover is { } hover)
        {
            _hexHoverLayer = layer.Id;
            _hexHoverErase = _hexCursor.Erase;
            // A new mode or brush size reshapes the footprints that are still lit.
            if (_hexHoverShape != (_hexMode, _hexRadius))
            {
                _hexHoverShape = (_hexMode, _hexRadius);
                foreach (var hex in _hexHoverCells.Keys.ToList())
                    _hexHoverCells[hex] = (_hexHoverCells[hex].Strength, HexCursorFootprint(layer, hex));
            }
            var layout = layer.Board.Layout;
            // A jump of several hexes (a zoom, a new layer) is taken at once instead of slid across.
            var point = _hexHoverPoint is { } p && Math.Abs(p.X - hover.X) + Math.Abs(p.Y - hover.Y) < 4 * layout.Size
                ? Follow(p, hover.X, hover.Y, SimpleHexHover ? 1 : HexHoverFollowMs, dt)
                : (hover.X, hover.Y, 0, 0);
            _hexHoverPoint = point;
            if (Math.Abs(point.X - hover.X) + Math.Abs(point.Y - hover.Y) > layout.Size * 0.002
                || Math.Abs(point.Vx) + Math.Abs(point.Vy) > layout.Size * 0.02) busy = true;

            var near = layout.HexAt(point.X, point.Y);
            var inner2 = Math.Pow(layout.Size * HexLayout.Sqrt3 / 2, 2);
            var candidates = new List<(HexCoord Hex, double D2)>(7);
            foreach (var hex in near.Neighbors().Prepend(near))
            {
                if (!layer.Board.Contains(hex)) continue;
                var (cx, cy) = layout.Center(hex);
                candidates.Add((hex, ((cx - point.X) * (cx - point.X) + (cy - point.Y) * (cy - point.Y)) / inner2));
            }

            if (candidates.Count > 0)
            {
                var nearest = candidates.Min(c => c.D2);
                // A large brush (more than 37 hexes) outlines only the footprint under the pointer:
                // several overlapping footprints of hundreds of hexes each cost a drag its frames.
                var sharp = SimpleHexHover ? 1e9 : 1.6 / (HexHoverBlend * HexHoverBlend);
                foreach (var (hex, d2) in candidates)
                {
                    var target = Math.Exp(-sharp * (d2 - nearest));
                    if (target > 0.003) targets[hex] = target;
                }
            }

            foreach (var hex in targets.Keys)
            {
                if (!_hexHoverCells.ContainsKey(hex)) _hexHoverCells[hex] = (0, HexCursorFootprint(layer, hex));
            }
        }
        else _hexHoverPoint = null;

        foreach (var hex in _hexHoverCells.Keys.ToList())
        {
            var (strength, hexes) = _hexHoverCells[hex];
            var target = targets.GetValueOrDefault(hex);
            var ms = SimpleHexHover ? 0 : target > strength ? HexHoverRiseMs : HexHoverFadeMs;
            strength = ms <= 0 ? target : strength + (target - strength) * (1 - Math.Exp(-dt * 1000 / (ms / 3)));
            if (Math.Abs(target - strength) <= 0.002) strength = target;
            else busy = true;
            if (strength <= 0.003 && target == 0) _hexHoverCells.Remove(hex);
            else _hexHoverCells[hex] = (strength, hexes);
        }

        _hexHoverShown = _hexHoverLayer is { } id && _hexHoverCells.Count > 0
            ? new HexCursorPreview(id, _hexCursor?.Hexes ?? [], _hexHoverErase,
                [.. _hexHoverCells.Values.OrderBy(c => c.Strength).Select(c => new HexCursorGlow(c.Hexes, c.Strength))],
                _hexGestureLayer is null ? HexGhost() : null)
            : null;
        if (!busy) _hexHoverStamp = 0;
        return busy || settling;
    }

    /// <summary>
    /// One outline, straight under the pointer, instead of the eased hand-over: a large brush, or
    /// a brush wider than one hex while it paints (two or three overlapping footprints of 19-37
    /// hexes cost a drag about 3 ms a frame: r3 61 → 47 FPS, eraser r2 59 → 51).
    /// </summary>
    private bool SimpleHexHover => _hexRadius > 3 || (_hexGestureLayer is not null && _hexRadius > 0);

    /// <summary>The hexes the hex tool covers when its footprint is centred on <paramref name="center"/>.</summary>
    private List<HexCoord> HexCursorFootprint(HexLayer layer, HexCoord center) =>
        _hexMode is HexToolMode.Place or HexToolMode.Erase or HexToolMode.Region or HexToolMode.Fog
            ? HexEditing.Footprint(layer.Board, center, _hexRadius).ToList()
            : [center];

    /// <summary>One step of a critically damped spring (no overshoot) with the given smoothing time.</summary>
    private static (double X, double Y, double Vx, double Vy) Follow(
        (double X, double Y, double Vx, double Vy) state, double targetX, double targetY, double smoothMs, double dt)
    {
        var omega = 2 / (smoothMs / 1000);
        var x = omega * dt;
        var decay = 1 / (1 + x + 0.48 * x * x + 0.235 * x * x * x);
        (double, double) Axis(double position, double velocity, double target)
        {
            var change = position - target;
            var temp = (velocity + omega * change) * dt;
            return (target + (change + temp) * decay, (velocity - omega * temp) * decay);
        }

        var (px, vx) = Axis(state.X, state.Vx, targetX);
        var (py, vy) = Axis(state.Y, state.Vy, targetY);
        return (px, py, vx, vy);
    }
}
