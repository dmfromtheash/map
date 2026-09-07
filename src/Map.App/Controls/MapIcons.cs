using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Media;

namespace Map.App.Controls;

/// <summary>
/// Semantic icon tokens for Map. Large primary commands resolve to the generated Blue Atlas
/// PNG artwork; compact secondary actions use the existing native
/// path glyphs. No emoji or font symbols are used as working controls.
/// </summary>
public static class MapIcons
{
    // ------------------------------------------------------------------ tools
    public const string Select = "M6 3 L6 19.5 L10.4 15.4 L13 20.8 L15.6 19.5 L13 14.3 L18.6 14 Z";
    public const string Line = "M4 17.5 C 8 17.5, 8 8, 12 8 S 16 15, 20 15";
    public const string Shape = "M7 3.5 L11 10 H3 Z M4 13 H10 V19 H4 Z M16.5 13 A3.5 3.5 0 1 0 16.5 20 A3.5 3.5 0 1 0 16.5 13";
    public const string Text = "M5 6 H19 M12 6 V19";
    public const string Brush = "M17 3.5 L20.5 7 L12 15.5 L7.5 17 L9 12.5 Z M4 20.5 C 6.5 20.5, 7.5 19, 7.5 16.5";
    // A trailing no-op space keeps the tool token distinct from the layer-visibility token. The
    // tool uses a shield illustration while visibility keeps the native eye glyph.
    public const string Mask = Eye + " ";
    public const string Land = "M16.5 3.5 L20.5 7.5 L13 15 L8.5 16.5 L10 12 Z " +
                               "M2.5 18 C5.5 16.2 8.5 16.8 11.5 18.2 C14.5 19.6 17.5 19.6 21.5 17.5 " +
                               "M2.5 21 C6 19.5 9 20.2 12 21";
    // A meandering main channel with one tributary joining downstream.
    public const string River = "M4 4 C 9 6, 8 12, 12 13.5 C 16 15, 15 19, 20 20.5 " +
                                "M17 5.5 C 14 8.5, 13.5 11, 12.5 13.8";

    // ----------------------------------------------------------------- panels
    public const string Layers = "M12 3.5 L21 8.5 L12 13.5 L3 8.5 Z M3.5 13 L12 17.8 L20.5 13";
    public const string Objects = "M9.5 4 A5.5 5.5 0 1 0 9.5 15 A5.5 5.5 0 1 0 9.5 4 " +
                                  "M13.5 13.5 L20 20 M5.8 9.5 C7.7 7.2 11.3 7.2 13.2 9.5 C11.3 11.8 7.7 11.8 5.8 9.5 Z " +
                                  "M9.5 8.2 A1.3 1.3 0 1 0 9.5 10.8 A1.3 1.3 0 1 0 9.5 8.2";
    public const string Assets = "M3 5.5 C6.2 4.3 9.2 4.8 12 6.7 C14.8 4.8 17.8 4.3 21 5.5 V19 " +
                                 "C17.8 17.8 14.8 18.2 12 20 C9.2 18.2 6.2 17.8 3 19 Z M12 6.7 V20 " +
                                 "M5.2 8.2 C7.3 7.6 9 7.9 10.5 8.8 M5.2 11.5 C7.2 10.8 8.9 11.1 10.5 12 " +
                                 "M13.5 8.8 L15.2 7.4 L16.4 9.2 L18.8 7.8 M13.5 12.2 L15.5 10.5 L17.4 12.5 L19.1 11.2";
    public const string LibraryMaps = "M5 5 H19 V17 H5 Z M3 8 H5 V20 H17 V17 M8 8 C10.5 6.7 13.5 9.3 16 8 M8 12 C10.5 10.7 13.5 13.3 16 12";
    public const string Notes = "M6 3.5 H18 V20.5 H6 Z M9 9 H15 M9 13 H15 M9 17 H13";

    // ------------------------------------------------------------ layer kinds
    public const string LayerObjects = Objects;
    public const string LayerBrush = Brush;
    public const string LayerLand = Land;
    public const string LayerGrid = "M4 4 H20 V20 H4 Z M4 9.33 H20 M4 14.67 H20 M9.33 4 V20 M14.67 4 V20";
    public const string LayerFilter = "M4 6 H20 M4 12 H20 M4 18 H20 M8 4 V8 M16 10 V14 M10 16 V20";
    public const string LayerBase = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 C 13.6 20.5 14 19.4 14 18.6 " +
                                    "C 14 17.2 13 16.8 14 15.8 C 15 14.8 18.5 15.8 19.6 13.2 " +
                                    "A8.5 8.5 0 0 0 12 3.5 Z";
    public const string LayerMask = "M3.5 5 H14.5 V19 H3.5 Z M9.5 8.5 H20.5 V20.5 H9.5 Z " +
                                    "M15 11.5 A3 3 0 1 0 15 17.5 A3 3 0 1 0 15 11.5";

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
    public const string Open = "M3.5 18.5 V7 H9 L11 9 H20.5 L18 18.5 Z M8 6 H17.5 V9";
    public const string File = "M5 3.5 H15 L19 7.5 V20.5 H5 Z M15 3.5 V7.5 H19 M8 11 H16 M8 15 H16";
    public const string Export = "M5 11 V20 H19 V11 M12 16 V3.5 M7.5 8 L12 3.5 L16.5 8";
    public const string Import = "M5 13 V20 H19 V13 M12 3.5 V16 M7.5 11.5 L12 16 L16.5 11.5";
    public const string NewMap = "M3.5 6 H15.5 V20 H3.5 Z M6 8.5 C8.5 7 10.5 10 13 8.5 " +
                                 "M18.5 3.5 V11.5 M14.5 7.5 H22.5";
    public const string Sort = "M4 6 H14 M4 11 H14 M4 16 H14 M19 5 V19 M16 16 L19 19 L22 16";
    public const string Versions = "M12 6 A7 7 0 1 1 5 13 M5 13 H2.5 M5 13 V10.5 M12 9 V13 L15 15";
    public const string ResizeRegion = "M5 7 H16 V17 H5 Z M3 4.5 H18 M3 4.5 L5 3 M3 4.5 L5 6 " +
                                       "M19 5 V19 M19 5 L17.5 7 M19 5 L20.5 7";
    public const string Help = "M3.5 5.5 C6.5 4.5 9.5 5 12 7 C14.5 5 17.5 4.5 20.5 5.5 V19 C17.5 18 14.5 18.5 12 20 C9.5 18.5 6.5 18 3.5 19 Z M12 7 V20";

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

    // -------------------------------------------------- approved parameter glyphs
    public const string BrushSize = "M15.5 3.5 L20.5 8.5 L13 16 L8.5 17.5 L10 13 Z M3 8 H8 M5.5 5.5 V10.5 M16.5 19 H21.5";
    public const string Opacity = "M12 3 C9 7.2 6.5 10.3 6.5 14 A5.5 5.5 0 0 0 17.5 14 C17.5 10.3 15 7.2 12 3 Z M12 3 V19.5";
    public const string SoftEdge = "M12 4 A8 8 0 1 0 12 20 A8 8 0 1 0 12 4 M12 7 A5 5 0 1 0 12 17 A5 5 0 1 0 12 7";
    public const string RoughEdge = "M12 3.5 L14 5 L16.5 4.5 L17.5 7 L20 8 L19 10.5 L20.5 12.5 L18.8 14.5 " +
                                    "L19 17 L16.5 17.5 L15 20 L12.5 18.7 L10 20 L8.5 17.8 L6 17.5 L6.2 15 L3.5 13.5 L5 11.2 L4 8.8 L6.5 7.5 L7.5 5 L10 5.5 Z";
    public const string Smoothing = "M3 16 C6 9 9 20 13 12 C15 8 17 7 21 7 M15.5 5.5 L21 7 L17.5 11.5";
    public const string ScaleObject = "M5 9 V5 H9 M15 5 H19 V9 M19 15 V19 H15 M9 19 H5 V15 M4 4 L8 8 M20 4 L16 8 M20 20 L16 16 M4 20 L8 16";
    public const string Rotate = "M19.5 8.5 V3.5 M19.5 3.5 H14.5 M19 4 A8 8 0 1 1 7 4.5";
    public const string StampSpacing = "M4 9 H8 V15 H4 Z M10 9 H14 V15 H10 Z M16 9 H20 V15 H16 Z M6 18 H18 M6 18 L8 16.5 M6 18 L8 19.5 M18 18 L16 16.5 M18 18 L16 19.5";
    public const string Color = "M8.5 6 A5 5 0 1 0 8.5 16 A5 5 0 1 0 8.5 6 M15.5 6 A5 5 0 1 0 15.5 16 A5 5 0 1 0 15.5 6 M12 11 A5 5 0 1 0 12 21 A5 5 0 1 0 12 11";
    public const string Texture = "M4 4 H20 V20 H4 Z M12 4 V20 M4 12 H20 M5.5 10 L10.5 5 M13.5 5 C15.5 7 17.5 7 19 5.5 M5.5 18.5 L10.5 13.5 M14 14 L18.5 18.5 M14 18.5 L18.5 14";
    public const string LayerOpacity = "M3.5 6 H20.5 V18 H3.5 Z M12 6 V18 M14 8 H16 M18 8 H20 M14 12 H16 M18 12 H20 M14 16 H16 M18 16 H20";
    public const string Blend = "M9.5 5 A6 6 0 1 0 9.5 17 A6 6 0 1 0 9.5 5 M14.5 7 A6 6 0 1 0 14.5 19 A6 6 0 1 0 14.5 7";
    public const string LayerOrder = "M4 5.5 L12 2.5 L20 5.5 L12 8.5 Z M4 11 L12 8 L20 11 L12 14 Z M4 16.5 L12 13.5 L20 16.5 L12 19.5 Z";

    /// <summary>Returns the approved compact glyph for a known inspector parameter.</summary>
    public static string? ForProperty(string label)
    {
        var normalized = label.Trim().ToLowerInvariant();
        if (normalized.Contains("размер кисти")) return BrushSize;
        if (normalized.Contains("мягк")) return SoftEdge;
        if (normalized.Contains("неровн")) return RoughEdge;
        if (normalized.Contains("сглаж")) return Smoothing;
        if (normalized.Contains("интервал ассета") || normalized.Contains("шаг отпечат")) return StampSpacing;
        if (normalized.Contains("непрозрачность слоя")) return LayerOpacity;
        if (normalized.Contains("непрозрач") || normalized.Contains("плотност")) return Opacity;
        if (normalized.Contains("масштаб")) return ScaleObject;
        if (normalized.Contains("поворот") || normalized == "угол") return Rotate;
        if (normalized.Contains("текстур")) return Texture;
        if (normalized.Contains("цвет") || normalized.Contains("оттенок") || normalized.Contains("насыщен")) return Color;
        if (normalized.Contains("видим")) return Eye;
        if (normalized.Contains("блокиров")) return Lock;
        if (normalized.Contains("смешив") || normalized.Contains("наложен")) return Blend;
        if (normalized.Contains("маск")) return LayerMask;
        if (normalized.Contains("порядок")) return LayerOrder;
        return null;
    }

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

    /// <summary>The rail glyph of a tool, so the rail can be built from the shared layout list.</summary>
    public static string Tool(Map.Presentation.ViewModels.EditorTool tool) => tool switch
    {
        Map.Presentation.ViewModels.EditorTool.Select => Select,
        Map.Presentation.ViewModels.EditorTool.Land => Land,
        Map.Presentation.ViewModels.EditorTool.Line => Line,
        Map.Presentation.ViewModels.EditorTool.Shape => Shape,
        Map.Presentation.ViewModels.EditorTool.Text => Text,
        Map.Presentation.ViewModels.EditorTool.Notes => Notes,
        Map.Presentation.ViewModels.EditorTool.Brush => Brush,
        Map.Presentation.ViewModels.EditorTool.Mask => Mask,
        Map.Presentation.ViewModels.EditorTool.Stamp => Assets,
        Map.Presentation.ViewModels.EditorTool.River => River,
        _ => Select,
    };

    /// <summary>
    /// Large tool icons use dimensional artwork; small controls and explicitly coloured glyphs
    /// use a crisp path that follows the foreground of their current native state.
    /// </summary>
    public static Control Stroked(string data, double size = 18, IBrush? brush = null)
    {
        if (brush is null && ApprovedArtwork.TryCreateIcon(data, size, out var approved))
        {
            return approved;
        }

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
