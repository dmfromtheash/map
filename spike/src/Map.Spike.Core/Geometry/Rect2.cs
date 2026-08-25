namespace Map.Spike.Core.Geometry;

/// <summary>Axis-aligned world-space rectangle. Width/Height are never negative.</summary>
public readonly record struct Rect2(double X, double Y, double Width, double Height)
{
    public static readonly Rect2 Empty = new(0, 0, 0, 0);

    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static Rect2 FromLtrb(double left, double top, double right, double bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public static Rect2 FromCenter(Vec2 center, double width, double height) =>
        new(center.X - (width / 2), center.Y - (height / 2), width, height);

    public bool Contains(Vec2 p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    public bool Intersects(Rect2 other) =>
        other.X < Right && X < other.Right && other.Y < Bottom && Y < other.Bottom;

    public Rect2 Inflate(double amount) =>
        new(X - amount, Y - amount, Width + (2 * amount), Height + (2 * amount));

    public Rect2 Union(Rect2 other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return FromLtrb(
            Math.Min(X, other.X),
            Math.Min(Y, other.Y),
            Math.Max(Right, other.Right),
            Math.Max(Bottom, other.Bottom));
    }
}
