using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>Options of one scene pass. Selection overlay is editor chrome and is opt-in only.</summary>
public sealed class RenderOptions
{
    /// <summary>Object to draw the selection gizmo around, or null. Never set on export paths.</summary>
    public ObjectId? SelectedObjectId { get; init; }

    /// <summary>
    /// Current view scale, used to keep the gizmo's ring, handles and rotation stem a constant
    /// on-screen size regardless of zoom. Irrelevant when no selection is drawn.
    /// </summary>
    public double ViewScale { get; init; } = 1.0;
}

/// <summary>
/// The single scene evaluator: the editor canvas and the thumbnail exporter both go through it,
/// so what the user sees and what gets stored cannot drift apart. It never mutates the document.
/// </summary>
public static class SceneRenderer
{
    private static readonly SKColor SelectionColor = new(255, 214, 10, 255);

    /// <summary>Draws the document in world coordinates. The caller sets the world transform.</summary>
    public static void Render(SKCanvas canvas, MapDocument document, RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        // Canvas page.
        using (var page = new SKPaint { Color = ParseColor(document.Canvas.Background), IsAntialias = false })
        {
            canvas.DrawRect(0, 0, document.Canvas.Width, document.Canvas.Height, page);
        }

        foreach (var layer in document.Layers)
        {
            if (!layer.Visible || layer.Opacity <= 0)
            {
                continue;
            }

            var layerAlpha = (byte)Math.Clamp(Math.Round(layer.Opacity * 255), 0, 255);

            foreach (var sceneObject in layer.Objects)
            {
                if (!sceneObject.Visible)
                {
                    continue;
                }

                DrawObject(canvas, sceneObject, layerAlpha);
            }
        }

        if (options.SelectedObjectId is { } selectedId)
        {
            var selected = document.Layers
                .SelectMany(l => l.Objects)
                .FirstOrDefault(o => o.Id == selectedId);

            if (selected is not null)
            {
                DrawSelection(canvas, selected, Math.Max(options.ViewScale, 0.01));
            }
        }
    }

    private static void DrawObject(SKCanvas canvas, SceneObject sceneObject, byte layerAlpha)
    {
        canvas.Save();
        ApplyRotation(canvas, sceneObject);

        var rect = new SKRect(
            (float)sceneObject.X,
            (float)sceneObject.Y,
            (float)(sceneObject.X + sceneObject.Width),
            (float)(sceneObject.Y + sceneObject.Height));

        using (var fill = new SKPaint
        {
            Color = ParseColor(sceneObject.FillColor).WithScaledAlpha(layerAlpha),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        })
        {
            DrawShape(canvas, sceneObject.Kind, rect, fill);
        }

        if (sceneObject.StrokeWidth > 0)
        {
            using var stroke = new SKPaint
            {
                Color = ParseColor(sceneObject.StrokeColor).WithScaledAlpha(layerAlpha),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)sceneObject.StrokeWidth,
            };

            DrawShape(canvas, sceneObject.Kind, rect, stroke);
        }

        canvas.Restore();
    }

    /// <summary>
    /// The selection gizmo: ring, eight square resize handles and the round rotation handle on a
    /// stem above the top edge. All sizes are divided by the view scale so the gizmo keeps a
    /// constant on-screen size; positions must match <c>SelectionHandles.Compute</c>, which the
    /// input side uses for hit-testing.
    /// </summary>
    private static void DrawSelection(SKCanvas canvas, SceneObject selected, double viewScale)
    {
        var px = (float)(1.0 / viewScale);

        canvas.Save();
        ApplyRotation(canvas, selected);

        var left = (float)selected.X;
        var top = (float)selected.Y;
        var right = (float)(selected.X + selected.Width);
        var bottom = (float)(selected.Y + selected.Height);
        var centreX = (left + right) / 2;

        using (var ring = new SKPaint
        {
            Color = SelectionColor,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2 * px,
        })
        {
            canvas.DrawRect(left, top, right - left, bottom - top, ring);

            // Stem to the rotation handle (28 screen px above the top edge, matching the VM).
            canvas.DrawLine(centreX, top, centreX, top - (28 * px), ring);
        }

        using var handleFill = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var handleEdge = new SKPaint
        {
            Color = SelectionColor,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f * px,
        };

        var half = 4 * px;
        foreach (var (x, y) in new (float X, float Y)[]
                 {
                     (left, top), (centreX, top), (right, top), (right, (top + bottom) / 2),
                     (right, bottom), (centreX, bottom), (left, bottom), (left, (top + bottom) / 2),
                 })
        {
            var rect = new SKRect(x - half, y - half, x + half, y + half);
            canvas.DrawRect(rect, handleFill);
            canvas.DrawRect(rect, handleEdge);
        }

        // Round rotation handle.
        canvas.DrawCircle(centreX, top - (28 * px), 5 * px, handleFill);
        canvas.DrawCircle(centreX, top - (28 * px), 5 * px, handleEdge);

        canvas.Restore();
    }

    private static void DrawShape(SKCanvas canvas, SceneObjectKind kind, SKRect rect, SKPaint paint)
    {
        if (kind == SceneObjectKind.Ellipse)
        {
            canvas.DrawOval(rect, paint);
        }
        else
        {
            canvas.DrawRect(rect, paint);
        }
    }

    private static void ApplyRotation(SKCanvas canvas, SceneObject sceneObject)
    {
        if (sceneObject.RotationDegrees != 0)
        {
            canvas.RotateDegrees(
                (float)sceneObject.RotationDegrees,
                (float)(sceneObject.X + (sceneObject.Width / 2)),
                (float)(sceneObject.Y + (sceneObject.Height / 2)));
        }
    }

    private static SKColor WithScaledAlpha(this SKColor color, byte layerAlpha) =>
        layerAlpha == 255 ? color : color.WithAlpha((byte)(color.Alpha * layerAlpha / 255));

    /// <summary>Parses "#AARRGGBB" (or "#RRGGBB" as opaque). The domain stores colours as strings.</summary>
    internal static SKColor ParseColor(string color)
    {
        var span = color.AsSpan().TrimStart('#');
        var value = uint.Parse(span, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);

        if (span.Length == 6)
        {
            value |= 0xFF000000;
        }

        return new SKColor(
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF),
            (byte)((value >> 24) & 0xFF));
    }
}
