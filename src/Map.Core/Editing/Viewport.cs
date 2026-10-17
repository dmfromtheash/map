namespace Map.Core.Editing;

/// <summary>
/// World -> screen mapping of the editor canvas. Session state only - never part of the saved
/// document (docs/04). Screen = (World - Offset) * Scale, so Offset is the world point at the
/// screen origin.
/// </summary>
public sealed class Viewport
{
    public const double MinScale = 0.005;

    /// <summary>
    /// 1600 %. One world unit is one document pixel and a stamp draws from at most four source
    /// pixels per unit (DEC-035), so past 400 % the view only magnifies; sixteen times leaves a
    /// generous margin for placing and aligning small objects without promising detail that is
    /// not there. Old documents keep their geometry: the limit is session state, never saved.
    /// </summary>
    public const double MaxScale = 16.0;
    private double _effectiveMinScale = MinScale;

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
        var newScale = Math.Clamp(Scale * factor, _effectiveMinScale, MaxScale);
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
        Scale = Math.Clamp(scale, _effectiveMinScale, MaxScale);
    }

    /// <summary>Fit the given world rectangle into the view with a small margin.</summary>
    public void FitTo(double worldWidth, double worldHeight)
    {
        if (ViewWidth <= 0 || ViewHeight <= 0 || worldWidth <= 0 || worldHeight <= 0)
        {
            return;
        }

        var fitScale = Math.Min(ViewWidth / worldWidth, ViewHeight / worldHeight) * 0.96;
        _effectiveMinScale = Math.Min(MinScale, fitScale);
        var scale = Math.Clamp(fitScale, _effectiveMinScale, MaxScale);

        Scale = scale;
        OffsetX = (worldWidth - (ViewWidth / scale)) / 2;
        OffsetY = (worldHeight - (ViewHeight / scale)) / 2;
    }
}
