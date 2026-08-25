namespace Map.Core.Editing;

/// <summary>
/// World -> screen mapping of the editor canvas. Session state only - never part of the saved
/// document (docs/04). Screen = (World - Offset) * Scale, so Offset is the world point at the
/// screen origin.
/// </summary>
public sealed class Viewport
{
    public const double MinScale = 0.05;
    public const double MaxScale = 32.0;

    public double Scale { get; private set; } = 1.0;
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }

    public double ViewWidth { get; private set; }
    public double ViewHeight { get; private set; }

    public void Resize(double width, double height)
    {
        ViewWidth = Math.Max(0, width);
        ViewHeight = Math.Max(0, height);
    }

    public (double X, double Y) WorldToScreen(double worldX, double worldY) =>
        ((worldX - OffsetX) * Scale, (worldY - OffsetY) * Scale);

    public (double X, double Y) ScreenToWorld(double screenX, double screenY) =>
        ((screenX / Scale) + OffsetX, (screenY / Scale) + OffsetY);

    public void PanByScreenDelta(double dxScreen, double dyScreen)
    {
        OffsetX -= dxScreen / Scale;
        OffsetY -= dyScreen / Scale;
    }

    /// <summary>Zoom keeping the world point under the given screen point fixed.</summary>
    public void ZoomAt(double anchorScreenX, double anchorScreenY, double factor)
    {
        var (anchorWorldX, anchorWorldY) = ScreenToWorld(anchorScreenX, anchorScreenY);
        var newScale = Math.Clamp(Scale * factor, MinScale, MaxScale);
        if (Math.Abs(newScale - Scale) < double.Epsilon)
        {
            return;
        }

        Scale = newScale;
        OffsetX = anchorWorldX - (anchorScreenX / Scale);
        OffsetY = anchorWorldY - (anchorScreenY / Scale);
    }

    public void Set(double offsetX, double offsetY, double scale)
    {
        OffsetX = offsetX;
        OffsetY = offsetY;
        Scale = Math.Clamp(scale, MinScale, MaxScale);
    }

    /// <summary>Fit the given world rectangle into the view with a small margin.</summary>
    public void FitTo(double worldWidth, double worldHeight)
    {
        if (ViewWidth <= 0 || ViewHeight <= 0 || worldWidth <= 0 || worldHeight <= 0)
        {
            return;
        }

        var scale = Math.Clamp(
            Math.Min(ViewWidth / worldWidth, ViewHeight / worldHeight) * 0.96,
            MinScale,
            MaxScale);

        Scale = scale;
        OffsetX = (worldWidth - (ViewWidth / scale)) / 2;
        OffsetY = (worldHeight - (ViewHeight / scale)) / 2;
    }
}
