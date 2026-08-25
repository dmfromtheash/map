using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Media;

namespace Map.App.Controls;

/// <summary>
/// Map's own icon set. Every glyph is a hand-authored path on a 24x24 grid; nothing is imported
/// from a third-party library and no emoji is ever used as a working control. Icons inherit the
/// foreground of the button that hosts them, so active/disabled states need no second definition.
/// </summary>
public static class MapIcons
{
    // ------------------------------------------------------------------ tools
    public const string Select = "M6 3 L6 19.5 L10.4 15.4 L13 20.8 L15.6 19.5 L13 14.3 L18.6 14 Z";
    public const string Line = "M4 17.5 C 8 17.5, 8 8, 12 8 S 16 15, 20 15";
    public const string Shape = "M4.5 4.5 H14 V14 H4.5 Z M14 19.5 A5.5 5.5 0 1 0 14 8.5 A5.5 5.5 0 1 0 14 19.5";
    public const string Text = "M5 6 H19 M12 6 V19";
    public const string Brush = "M17 3.5 L20.5 7 L12 15.5 L7.5 17 L9 12.5 Z M4 20.5 C 6.5 20.5, 7.5 19, 7.5 16.5";
    public const string Mask = "M4 4 H20 V20 H4 Z M12 8.2 A3.8 3.8 0 1 0 12 15.8 A3.8 3.8 0 1 0 12 8.2";
    public const string Land = "M3 14.5 C 6 11.5, 8.5 16, 11.5 13 S 17.5 9.5, 21 13 L21 20.5 L3 20.5 Z";

    // ----------------------------------------------------------------- panels
    public const string Layers = "M12 3.5 L21 8.5 L12 13.5 L3 8.5 Z M3.5 13 L12 17.8 L20.5 13";
    public const string Objects = "M5 4.5 H10.5 V10 H5 Z M13.5 4.5 H19 V10 H13.5 Z " +
                                  "M5 14 H10.5 V19.5 H5 Z M13.5 14 H19 V19.5 H13.5 Z";
    public const string Assets = "M3.5 5 H20.5 V19 H3.5 Z M3.5 15.5 L8.5 10.5 L12.5 14.5 L15.5 11.5 L20.5 16.5";
    public const string Notes = "M6 3.5 H14.5 L18.5 7.5 V20.5 H6 Z M14.5 3.5 V7.5 H18.5 M9 12 H15.5 M9 16 H15.5";

    // ------------------------------------------------------------ layer kinds
    public const string LayerObjects = Objects;
    public const string LayerBrush = Brush;
    public const string LayerLand = Land;
    public const string LayerGrid = "M4 4 H20 V20 H4 Z M4 9.33 H20 M4 14.67 H20 M9.33 4 V20 M14.67 4 V20";
    public const string LayerFilter = "M4 4.5 H20 L14 11.5 V19.5 L10 17.5 V11.5 Z";
    public const string LayerBase = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 C 13.6 20.5 14 19.4 14 18.6 " +
                                    "C 14 17.2 13 16.8 14 15.8 C 15 14.8 18.5 15.8 19.6 13.2 " +
                                    "A8.5 8.5 0 0 0 12 3.5 Z";
    public const string LayerMask = Mask;

    // ------------------------------------------------------------ object kinds
    public const string ObjectStamp = "M7 20.5 V9 H9 V6 H11 V9 H13 V6 H15 V9 H17 V20.5 Z";
    public const string ObjectRectangle = "M4.5 6 H19.5 V18 H4.5 Z";
    public const string ObjectEllipse = "M12 6 A7.5 6 0 1 0 12 18 A7.5 6 0 1 0 12 6";
    public const string ObjectComposition = "M4 4.5 H13.5 V14 H4 Z M10.5 10 H20 V19.5 H10.5 Z";
    public const string ObjectPlaceholder = "M4.5 5 H19.5 V19 H4.5 Z M4.5 5 L19.5 19 M19.5 5 L4.5 19";

    // ---------------------------------------------------------------- commands
    public const string Undo = "M9 7 L4 11.5 L9 16 M4 11.5 H13.5 A5 5 0 0 1 13.5 21.5 H10";
    public const string Redo = "M15 7 L20 11.5 L15 16 M20 11.5 H10.5 A5 5 0 0 0 10.5 21.5 H14";
    public const string Save = "M5 4 H16 L20 8 V20 H5 Z M9 4 V9 H15 V4 M8.5 20 V14.5 H16.5 V20";
    public const string FitCanvas = "M4 9 V4 H9 M15 4 H20 V9 M20 15 V20 H15 M9 20 H4 V15 M8 8 H16 V16 H8 Z";
    public const string Back = "M14.5 4.5 L7 12 L14.5 19.5";
    public const string Window = "M3.5 5 H20.5 V19 H3.5 Z M3.5 8.5 H20.5";
    public const string Plus = "M12 5 V19 M5 12 H19";
    public const string Open = "M3.5 18.5 V6 H9.5 L11.5 8.5 H20.5 V18.5 Z";

    // ------------------------------------------------------------------ states
    public const string Eye = "M2.5 12 C 5.5 7.5, 9 5.5, 12 5.5 S 18.5 7.5, 21.5 12 " +
                              "C 18.5 16.5, 15 18.5, 12 18.5 S 5.5 16.5, 2.5 12 Z " +
                              "M12 9 A3 3 0 1 0 12 15 A3 3 0 1 0 12 9";
    public const string EyeOff = "M2.5 12 C 5.5 7.5, 9 5.5, 12 5.5 S 18.5 7.5, 21.5 12 " +
                                 "C 18.5 16.5, 15 18.5, 12 18.5 S 5.5 16.5, 2.5 12 Z M4 4 L20 20";
    public const string Lock = "M7.5 10.5 V8 A4.5 4.5 0 0 1 16.5 8 V10.5 M5 10.5 H19 V20 H5 Z";
    public const string Unlock = "M7.5 10.5 V8 A4.5 4.5 0 0 1 15.5 5.2 M5 10.5 H19 V20 H5 Z";
    public const string Warning = "M12 3.5 L22 20.5 H2 Z M12 10 V15 M12 17.6 V18.2";
    public const string Missing = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 A8.5 8.5 0 1 0 12 3.5 M8 8 L16 16 M16 8 L8 16";
    public const string Check = "M5 12.5 L10 17.5 L19 6.5";
    public const string Search = "M10.5 3.5 A7 7 0 1 0 10.5 17.5 A7 7 0 1 0 10.5 3.5 M15.6 15.6 L20.5 20.5";
    public const string Gear = "M12 8.8 A3.2 3.2 0 1 0 12 15.2 A3.2 3.2 0 1 0 12 8.8 " +
                               "M12 2.6 V5.4 M12 18.6 V21.4 M2.6 12 H5.4 M18.6 12 H21.4 " +
                               "M5.4 5.4 L7.4 7.4 M16.6 16.6 L18.6 18.6 " +
                               "M18.6 5.4 L16.6 7.4 M7.4 16.6 L5.4 18.6";
    public const string Trash = "M4 6.5 H20 M9 6.5 V3.5 H15 V6.5 M6 6.5 L7 20.5 H17 L18 6.5 " +
                                "M10 10.5 V16.5 M14 10.5 V16.5";
    public const string Eraser = "M13.5 3.8 L20.7 11 L12.8 18.9 H6 L2.7 15.6 Z M6.6 10.7 L14.8 18.9";
    public const string Duplicate = "M8 8 H20 V20 H8 Z M4.5 16 V4 H16";
    public const string ArrowUp = "M12 19.5 V4.5 M6 10.5 L12 4.5 L18 10.5";
    public const string ArrowDown = "M12 4.5 V19.5 M6 13.5 L12 19.5 L18 13.5";
    public const string Close = "M6.5 6.5 L17.5 17.5 M17.5 6.5 L6.5 17.5";
    public const string ChevronDown = "M6 9 L12 15 L18 9";
    public const string ChevronRight = "M9 5.5 L15.5 12 L9 18.5";

    // ------------------------------------------------------------- land shapes
    public const string ShapeCircle = "M12 4.5 A7.5 7.5 0 1 0 12 19.5 A7.5 7.5 0 1 0 12 4.5";
    public const string ShapeRectangle = "M4.5 6 H19.5 V18 H4.5 Z";
    public const string ShapeEllipse = ObjectEllipse;
    public const string ShapePolygon = "M12 3.5 L20.5 10 L17.3 20.5 H6.7 L3.5 10 Z";
    public const string ShapeGridBlock = "M5 5 H19 V19 H5 Z M12 5 V19 M5 12 H19";
    public const string ShapeFill = "M6.5 11 L12.5 5 L18.5 11 L12.5 17 Z " +
                                    "M19.5 14.5 C 19.5 17, 21.5 17, 21.5 14.5 C 21.5 13.3, 20.5 12.2, 20.5 12.2 " +
                                    "S 19.5 13.3, 19.5 14.5 Z";
    public const string Move = "M12 3.5 V20.5 M3.5 12 H20.5 M9 6.5 L12 3.5 L15 6.5 " +
                               "M9 17.5 L12 20.5 L15 17.5 M6.5 9 L3.5 12 L6.5 15 M17.5 9 L20.5 12 L17.5 15";

    /// <summary>An outlined glyph that follows the foreground of the button hosting it.</summary>
    public static Path Stroked(string data, double size = 18, IBrush? brush = null)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
            IsHitTestVisible = false,
        };

        Paint(path, Path.StrokeProperty, brush);
        return path;
    }

    /// <summary>A solid glyph, used where an outline would read as noise at 18 px.</summary>
    public static Path Filled(string data, double size = 18, IBrush? brush = null)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data),
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
            IsHitTestVisible = false,
        };

        Paint(path, Path.FillProperty, brush);
        return path;
    }

    private static void Paint(Path path, AvaloniaProperty<IBrush?> property, IBrush? brush)
    {
        if (brush is not null)
        {
            path.SetValue(property, brush);
            return;
        }

        // No explicit brush: inherit from the nearest templated ancestor so hover, active and
        // disabled states of the host control need no duplicated icon definitions.
        path.Bind(property, new Binding
        {
            Path = nameof(TemplatedControl.Foreground),
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor)
            {
                AncestorType = typeof(TemplatedControl),
            },
        });
    }
}
