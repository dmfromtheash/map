using System.Collections.ObjectModel;

namespace Map.Core.Documents;

/// <summary>How a gradient spreads across the object it fills.</summary>
public enum GradientKind
{
    /// <summary>A straight ramp along <see cref="GradientFill.AngleDegrees"/>.</summary>
    Linear = 0,

    /// <summary>A ramp outwards from <see cref="GradientFill.CenterX"/>/<see cref="GradientFill.CenterY"/>.</summary>
    Radial = 1,
}

/// <summary>
/// One colour stop of a gradient. <paramref name="Offset"/> is 0..1 along the ramp and the colour
/// uses the same canonical "#AARRGGBB" contract as every other colour in the document, so the
/// picker, persistence and the renderer all read one syntax.
/// </summary>
public sealed record GradientStopStyle
{
    public GradientStopStyle(double offset, string color)
    {
        if (!double.IsFinite(offset) || offset is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "A gradient stop sits at 0..1 along the ramp.");
        }

        Color = AppearanceColor.Normalize(color)
            ?? throw new ArgumentException("A gradient stop colour is #RRGGBB or #AARRGGBB.", nameof(color));
        Offset = offset;
    }

    public double Offset { get; init; }
    public string Color { get; init; }
}

/// <summary>
/// A gradient fill expressed in the object's own normalized bounds, so it survives move, resize
/// and rotation without being recomputed.
/// <para>
/// The bound of eight stops is deliberate: it is more than a map needs, and it keeps a malformed
/// or hostile project file from asking the renderer for unbounded shader work.
/// </para>
/// </summary>
public sealed record GradientFill
{
    public const int MinStops = 2;
    public const int MaxStops = 8;

    public GradientFill(
        GradientKind kind,
        IReadOnlyList<GradientStopStyle> stops,
        double angleDegrees = 90,
        double centerX = 0.5,
        double centerY = 0.5,
        double radius = 0.5)
    {
        ArgumentNullException.ThrowIfNull(stops);
        if (stops.Count is < MinStops or > MaxStops)
        {
            throw new ArgumentException(
                $"A gradient has {MinStops}..{MaxStops} colour stops.", nameof(stops));
        }

        if (!double.IsFinite(angleDegrees) || !double.IsFinite(centerX) || !double.IsFinite(centerY)
            || !double.IsFinite(radius) || radius <= 0 || radius > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Gradient placement values must be finite and bounded.");
        }

        // Stops are stored in ramp order. The editor lets the user drag one stop past another, and
        // sorting here means neither the renderer nor the file has to cope with a reversed pair.
        Kind = kind;
        Stops = new ReadOnlyCollection<GradientStopStyle>([.. stops.OrderBy(stop => stop.Offset)]);
        AngleDegrees = ((angleDegrees % 360) + 360) % 360;
        CenterX = Math.Clamp(centerX, 0, 1);
        CenterY = Math.Clamp(centerY, 0, 1);
        Radius = radius;
    }

    public GradientKind Kind { get; init; }

    public IReadOnlyList<GradientStopStyle> Stops { get; init; }

    /// <summary>Direction of a linear ramp: 0° runs left to right, 90° runs top to bottom.</summary>
    public double AngleDegrees { get; init; }

    /// <summary>Centre of a radial ramp in the object's normalized bounds.</summary>
    public double CenterX { get; init; }
    public double CenterY { get; init; }

    /// <summary>Radius of a radial ramp as a fraction of the object's larger side.</summary>
    public double Radius { get; init; }

    /// <summary>The neutral starting gradient offered when the user first turns one on.</summary>
    public static GradientFill Default(string fromColor, GradientKind kind = GradientKind.Linear)
    {
        var from = AppearanceColor.Normalize(fromColor) ?? AppearanceColor.OpaqueBlack;
        return new GradientFill(kind, [new GradientStopStyle(0, from), new GradientStopStyle(1, Darken(from))]);
    }

    /// <summary>
    /// A second stop that is visibly different from the first without being a guess about taste:
    /// the same colour at 45 % brightness, which reads as a shadow on any hue.
    /// </summary>
    private static string Darken(string canonical)
    {
        var value = uint.Parse(canonical.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);
        var a = (byte)((value >> 24) & 0xFF);
        var r = (byte)Math.Round(((value >> 16) & 0xFF) * 0.45);
        var g = (byte)Math.Round(((value >> 8) & 0xFF) * 0.45);
        var b = (byte)Math.Round((value & 0xFF) * 0.45);
        return $"#{a:X2}{r:X2}{g:X2}{b:X2}";
    }
}
