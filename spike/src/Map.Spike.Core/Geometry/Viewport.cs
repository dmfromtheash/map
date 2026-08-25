namespace Map.Spike.Core.Geometry;

/// <summary>
/// World -> screen mapping. Session state only: never part of the saved document (docs/04).
/// Screen = (World - Offset) * Scale, so Offset is the world point at screen origin.
/// </summary>
public sealed class Viewport
{
    public const double MinScale = 0.02;
    public const double MaxScale = 64.0;

    public double Scale { get; private set; } = 1.0;
    public Vec2 Offset { get; private set; } = Vec2.Zero;

    public double ViewWidth { get; private set; }
    public double ViewHeight { get; private set; }

    public void Resize(double width, double height)
    {
        ViewWidth = Math.Max(0, width);
        ViewHeight = Math.Max(0, height);
    }

    public Vec2 WorldToScreen(Vec2 world) =>
        new((world.X - Offset.X) * Scale, (world.Y - Offset.Y) * Scale);

    public Vec2 ScreenToWorld(Vec2 screen) =>
        new((screen.X / Scale) + Offset.X, (screen.Y / Scale) + Offset.Y);

    /// <summary>World rect currently visible. Used for culling.</summary>
    public Rect2 VisibleWorldBounds()
    {
        var topLeft = ScreenToWorld(Vec2.Zero);
        var bottomRight = ScreenToWorld(new Vec2(ViewWidth, ViewHeight));
        return Rect2.FromLtrb(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
    }

    public void PanByScreenDelta(double dxScreen, double dyScreen) =>
        Offset = new Vec2(Offset.X - (dxScreen / Scale), Offset.Y - (dyScreen / Scale));

    /// <summary>Zoom keeping the world point under <paramref name="anchorScreen"/> fixed.</summary>
    public void ZoomAt(Vec2 anchorScreen, double factor)
    {
        var anchorWorld = ScreenToWorld(anchorScreen);
        var newScale = Math.Clamp(Scale * factor, MinScale, MaxScale);
        if (Math.Abs(newScale - Scale) < double.Epsilon) return;
        Scale = newScale;
        Offset = new Vec2(
            anchorWorld.X - (anchorScreen.X / Scale),
            anchorWorld.Y - (anchorScreen.Y / Scale));
    }

    public void Set(Vec2 offset, double scale)
    {
        Offset = offset;
        Scale = Math.Clamp(scale, MinScale, MaxScale);
    }
}
