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
    // A road forking: two edges of a way that opens into a Y, with a short rut in the middle.
    // One hex with a smaller one inside: a tile on its board.
    public const string Hex = "M12 2.5 L20.2 7.25 V16.75 L12 21.5 L3.8 16.75 V7.25 Z M12 7.6 L15.8 9.8 V14.2 L12 16.4 L8.2 14.2 V9.8 Z";
    public const string Road = "M5 20.5 C 7 14, 9 9, 12 3.5 M19 20.5 C 17 14, 15 9, 12 3.5 " +
                               "M12 12 L 12 14.5 M12 17 L 12 19.5";

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
    public const string Duplicate = "M8 8 H20 V20 H8 Z M4.5 16 V4 H16";
    public const string ArrowUp = "M12 19.5 V4.5 M6 10.5 L12 4.5 L18 10.5";
    public const string ArrowDown = "M12 4.5 V19.5 M6 13.5 L12 19.5 L18 13.5";
    public const string ArrowToTop = "M12 20 V8 M6.5 13.5 L12 8 L17.5 13.5 M5 4.5 H19";
    public const string ArrowToBottom = "M12 4 V16 M6.5 10.5 L12 16 L17.5 10.5 M5 19.5 H19";
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

    // ------------------------------------------------ line set (UI redesign 2026-09-26)
    // A calm outline family on one 24-unit grid, drawn by Grid24 without Stretch so every icon
    // keeps its designed proportions. The older glyphs above stay untouched: the quick property
    // strip over the canvas draws with them and must not change.
    private static string Ring(double cx, double cy, double r) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"M{cx - r} {cy} A{r} {r} 0 1 0 {cx + r} {cy} A{r} {r} 0 1 0 {cx - r} {cy} ");

    public static readonly string LineSelect = "M6 3.5 L18.5 10.7 L12.9 12.1 L10.3 17.5 Z M12.9 12.1 L16.9 17.5";
    public static readonly string LineAssets = "M3 20.5 H21 " + Ring(7.5, 9, 3.6) +
        "M7.5 12.6 V20.5 M12.5 20.5 V14.3 L16.5 10.9 L20.5 14.3 V20.5 M15.2 20.5 V17.5 H17.8 V20.5";
    public static readonly string LineLand = "M3.5 14.5 C3.9 10.9 6.8 9.2 9.3 9.6 C10.2 6.3 14.9 5 17.5 7.6 " +
        "C19.9 9.9 19.6 14.2 16.7 15.7 C14.5 16.9 12.3 16.2 10.3 17.3 C7.5 18.9 3.2 18 3.5 14.5 Z " +
        "M8.5 13.2 L10.5 10.6 L12.1 12.4 L13.6 10.4 L16 13.2";
    public static readonly string LineRiver = "M8 2.5 C6.4 5.8 10.9 7.4 9.8 10.9 C8.9 13.9 4.9 14.5 5.2 18.1 C5.4 19.8 6.5 20.9 7.5 21.5 " +
        "M13 2.5 C11.4 5.8 15.9 7.4 14.8 10.9 C13.9 13.9 9.9 14.5 10.2 18.1 C10.4 19.8 11.5 20.9 12.5 21.5 " +
        "M14.8 10.9 C16.4 11.3 18.4 10.6 20.5 8.7";
    public static readonly string LineRoad = "M8.5 21.5 L10.8 2.5 M15.5 21.5 L13.2 2.5 M12 5 V7.2 M12 10.3 V12.9 M12 16.2 V19.2";
    public static readonly string LineStroke = "M4 18.5 C7.5 9 11.5 20 20 6.5 " + Ring(4, 18.5, 1.6) + Ring(20, 6.5, 1.6);
    public static readonly string LineShape = "M5 3.5 H12.5 A1.5 1.5 0 0 1 14 5 V12.5 A1.5 1.5 0 0 1 12.5 14 H5 " +
        "A1.5 1.5 0 0 1 3.5 12.5 V5 A1.5 1.5 0 0 1 5 3.5 Z " + Ring(15, 15, 5.5);
    public static readonly string LineText = "M5 6.5 V4 H19 V6.5 M12 4 V20 M9 20 H15";
    public static readonly string LineNotes = "M6 3.5 H14.5 L18 7 V20.5 H6 Z M14.5 3.5 V7 H18 M9 11 H15 M9 14 H15 M9 17 H13";
    public static readonly string LineBrush = "M20 4 L10.5 13.5 M13.2 10.8 L10.5 8.1 " +
        "M10.4 13.6 C7.6 13 5.4 14.4 5.1 17 C4.9 18.8 4.3 19.8 3.5 20.5 C7.3 21.1 10.8 20.1 11.3 16.9";
    public static readonly string LineMask = Ring(12, 12, 8.5) + "M12 3.5 V20.5 M12 5.5 L17.6 11.1 M12 10 L18.7 16.7 M12 14.6 L15.8 18.4";
    public static readonly string LineLayers = "M12 3.5 L20.5 8 L12 12.5 L3.5 8 Z M3.5 12 L12 16.5 L20.5 12 M3.5 16 L12 20.5 L20.5 16";
    public static readonly string LineWater = "M12 3.5 C15.6 7.8 18 10.9 18 13.8 A6 6 0 0 1 6 13.8 C6 10.9 8.4 7.8 12 3.5 Z M9.2 14.6 A3 3 0 0 0 11.6 17.2";
    public static readonly string LineOther = "M4 20 L8.5 11 L13 20 Z " + Ring(17, 8, 3.5) + "M4 4.5 H11";
    /// <summary>The Свет section: a low sun over a ridge line.</summary>
    public static readonly string LineSun = Ring(12, 10, 3.6) + "M12 2.5 V4.2 M5.2 5.2 L6.4 6.4 M18.8 5.2 L17.6 6.4 M3 10 H4.8 M19.2 10 H21 M3.5 20 L8 15.5 L11 18 L15 13.5 L20.5 20";
    public static readonly string LineReset = "M4.5 5 V9.5 H9 M5 9.2 A7.5 7.5 0 1 1 6.1 16.6";
    public static readonly string LineSparkle = "M12 4 L13.8 8.9 L19 10.5 L13.8 12.2 L12 17 L10.2 12.2 L5 10.5 L10.2 8.9 Z";
    public static readonly string LineTarget = Ring(12, 12, 8) + Ring(12, 12, 3.5) + "M12 2 V5 M12 19 V22 M2 12 H5 M19 12 H22";
    public static readonly string LineWand = "M4 20 L15 9 M15 4 V6 M19 8 H21 M18 5 L19.5 3.5 M20 11 L19 12";
    public static readonly string LineCatalog = "M5 3.5 H9 A1.5 1.5 0 0 1 10.5 5 V9 A1.5 1.5 0 0 1 9 10.5 H5 A1.5 1.5 0 0 1 3.5 9 V5 A1.5 1.5 0 0 1 5 3.5 Z " +
        "M15 3.5 H19 A1.5 1.5 0 0 1 20.5 5 V9 A1.5 1.5 0 0 1 19 10.5 H15 A1.5 1.5 0 0 1 13.5 9 V5 A1.5 1.5 0 0 1 15 3.5 Z " +
        "M5 13.5 H9 A1.5 1.5 0 0 1 10.5 15 V19 A1.5 1.5 0 0 1 9 20.5 H5 A1.5 1.5 0 0 1 3.5 19 V15 A1.5 1.5 0 0 1 5 13.5 Z " +
        "M15 13.5 H19 A1.5 1.5 0 0 1 20.5 15 V19 A1.5 1.5 0 0 1 19 20.5 H15 A1.5 1.5 0 0 1 13.5 19 V15 A1.5 1.5 0 0 1 15 13.5 Z";
    public static readonly string LinePanelLeft = "M5.5 4 H18.5 A2 2 0 0 1 20.5 6 V18 A2 2 0 0 1 18.5 20 H5.5 A2 2 0 0 1 3.5 18 V6 A2 2 0 0 1 5.5 4 Z M9 4 V20 M15.5 9.5 L13 12 L15.5 14.5";
    public static readonly string LinePanelRight = "M5.5 4 H18.5 A2 2 0 0 1 20.5 6 V18 A2 2 0 0 1 18.5 20 H5.5 A2 2 0 0 1 3.5 18 V6 A2 2 0 0 1 5.5 4 Z M15 4 V20 M8.5 9.5 L11 12 L8.5 14.5";
    public static readonly string LineTrash = "M4.5 6.5 H19.5 M9.5 6.5 V4 H14.5 V6.5 M6.5 6.5 L7.5 20.5 H16.5 L17.5 6.5 M10 10.5 V16.5 M14 10.5 V16.5";
    public static readonly string LineInfo = Ring(12, 12, 8.5) + "M12 11 V16.5 M12 7.8 V8";
    public static readonly string ModeOne =Ring(12, 12, 3) + Ring(12, 12, 7.5);
    public static readonly string ModeSeries = Ring(5, 15, 2) + Ring(12, 12, 2) + Ring(19, 9, 2);
    public static readonly string ModeScatter = Ring(12, 12, 8.5) + Ring(9, 9, 1.4) + Ring(15, 10, 1.4) + Ring(10, 15.5, 1.4) + Ring(15.5, 15, 1.4);
    public static readonly string ModePath = "M3.5 18 C7.5 9 14.5 17 20.5 6 " + Ring(6, 14.5, 1.6) + Ring(12, 12.3, 1.6) + Ring(18, 8.8, 1.6);
    public static readonly string ModeArea = "M4 7 L11 3.5 L20 7.5 L18 18.5 L7 20 Z " + Ring(9, 10, 1.3) + Ring(14, 9, 1.3) + Ring(11, 14.5, 1.3) + Ring(15.5, 14, 1.3);

    // Tool panel tabs of the standard constructor (2026-10-19).
    public static readonly string LinePalette = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 C13.4 20.5 13.8 19.4 13.2 18.4 C12.5 17.2 13.3 16 14.6 16 H16.5 A4 4 0 0 0 20.5 12 C20.5 7.3 16.7 3.5 12 3.5 Z " +
        Ring(7.8, 11, 1.2) + Ring(10, 7.3, 1.2) + Ring(14.5, 7.3, 1.2);
    public static readonly string LineGear = Ring(12, 12, 3) + "M12 3 V5.5 M12 18.5 V21 M3 12 H5.5 M18.5 12 H21 M5.6 5.6 L7.4 7.4 M16.6 16.6 L18.4 18.4 M5.6 18.4 L7.4 16.6 M16.6 7.4 L18.4 5.6";
    public static readonly string LineStamp = Ring(8, 8, 4) + "M12.5 12.5 H20.5 V20.5 H12.5 Z";
    public static readonly string LineList = "M8.5 6 H20 M8.5 12 H20 M8.5 18 H20 M4 6 H4.5 M4 12 H4.5 M4 18 H4.5";

    // Hex constructor panel (2026-10-19): its tabs, click modes and sections, same line style.
    public static readonly string HexPlace = "M10 3.5 L16.5 7.2 V14.8 L10 18.5 L3.5 14.8 V7.2 Z M18.5 13.5 V21.5 M14.5 17.5 H22.5";
    public static readonly string HexFill = "M4.5 11 L11 4.5 L18.5 12 L12 18.5 Z M4.5 11 H18 M8.5 7 L4 2.5 " +
        "M19.8 15.4 C19.4 16.4 19.4 17.6 20.4 17.8 C21.4 17.6 21.4 16.4 21 15.4 L20.4 14 Z";
    public static readonly string HexErase = "M14.5 4 L20.5 10 L11.5 19 H6.5 L3.5 16 Z M9 9.5 L15 15.5 M11.5 19 H20.5";
    public static readonly string HexPick = "M13.5 5.5 L18.5 10.5 M16 8 L18.6 5.4 A1.9 1.9 0 0 1 21.3 8.1 L18.7 10.7 M15 9 L6.5 17.5 L4.5 20.5 L3.5 19.5 L6.5 17.5";
    public static readonly string HexRegion = "M5.5 21 V3.5 M5.5 4.5 C9 2.8 11.5 6.2 15 4.5 C16.8 3.6 18.5 4 18.5 4 V13 " +
        "C18.5 13 16.8 12.6 15 13.5 C11.5 15.2 9 11.8 5.5 13.5";
    public static readonly string HexFog = "M7 16.5 H17.5 A3.5 3.5 0 0 0 17 9.6 A5 5 0 0 0 7.6 8.4 A4 4 0 0 0 7 16.5 Z M4 20.5 H13.5 M16.5 20.5 H20";
    public static readonly string HexKey = Ring(8, 12, 4) + "M12 12 H21 M18.5 12 V15 M15.5 12 V14";
    public static readonly string HexRuler = "M3 16.5 L16.5 3 L21 7.5 L7.5 21 Z M7 12.5 L9 14.5 M10 9.5 L12 11.5 M13 6.5 L15 8.5";
    public static readonly string HexSelectBox = "M4 4 H7 M10 4 H14 M17 4 H20 V7 M20 10 V14 M20 17 V20 H17 M14 20 H10 M7 20 H4 V17 M4 14 V10 M4 7 V4";
    public static readonly string HexGlobe = Ring(12, 12, 8.5) + "M3.5 12 H20.5 M12 3.5 C8.5 7 8.5 17 12 20.5 M12 3.5 C15.5 7 15.5 17 12 20.5";
    public static readonly string HexDice = "M6 3.5 H18 A2.5 2.5 0 0 1 20.5 6 V18 A2.5 2.5 0 0 1 18 20.5 H6 A2.5 2.5 0 0 1 3.5 18 V6 A2.5 2.5 0 0 1 6 3.5 Z " +
        Ring(8.5, 8.5, 0.7) + Ring(12, 12, 0.7) + Ring(15.5, 15.5, 0.7);
    public static readonly string HexTurnLeft = "M4.5 5 V9.5 H9 M5 9.2 A7.5 7.5 0 1 1 6.1 16.6";
    public static readonly string HexTurnRight = "M19.5 5 V9.5 H15 M19 9.2 A7.5 7.5 0 1 0 17.9 16.6";
    public static readonly string HexMirror = "M12 3 V21 M9 6.5 L4 17.5 H9 Z M15 6.5 L20 17.5 H15 Z";
    public static readonly string HexTrail = "M4 20 C5.5 17.5 7 16 9 15 M11.5 13.5 C13 12.5 14 11.5 15 10 M16.5 7.5 L18 5 M19.5 3.5 L20 3";
    public static readonly string HexBorder = "M3.5 19 L6.5 15 M8.5 13.5 L11.5 15 M13 13 L15 9 M16.5 7 L20.5 4";
    public static readonly string HexExport = "M12 14.5 V3.5 M8 7.5 L12 3.5 L16 7.5 M5 12.5 V20 H19 V12.5";
    public static readonly string HexPrint = "M7 9 V3.5 H17 V9 M7 17 H4.5 V9 H19.5 V17 H17 M7 14 H17 V20.5 H7 Z";
    public static readonly string HexCopy = "M8.5 8.5 H19.5 V19.5 H8.5 Z M15.5 8.5 V4.5 H4.5 V15.5 H8.5";
    public static readonly string HexCut = Ring(6.5, 17.5, 2.8) + Ring(17.5, 17.5, 2.8) + "M8.6 15.6 L18 3.5 M15.4 15.6 L6 3.5";
    public static readonly string HexPaste = "M8.5 4.5 H5.5 V20.5 H18.5 V4.5 H15.5 M8.5 3 H15.5 V6.5 H8.5 Z M9 11.5 H15 M9 15 H15";
    public static readonly string HexPlayer = "M2.5 12 C5 7.5 8.3 5.5 12 5.5 C15.7 5.5 19 7.5 21.5 12 C19 16.5 15.7 18.5 12 18.5 C8.3 18.5 5 16.5 2.5 12 Z " + Ring(12, 12, 3);

    // ------------------------------------------- solid set (Inkarnate parity 2026-10-16)
    // REA showed Inkarnate's rail as 24 px solid glyphs that read at a glance; Map's 1.6 px outlines
    // at 21 px looked faint on the dark rail. These are Map's own drawings on the same 24-unit
    // grid: a filled body (F0 = even-odd, so inner shapes cut windows) plus an optional heavy
    // stroke for the parts that are lines by nature (rivers, a drawn line).
    /// <summary>A solid glyph; <paramref name="Optical"/> is how far its painted mass, rather than its box, is centred (0..1).</summary>
    public sealed record SolidGlyph(string Fill, string? Stroke = null, double StrokeWidth = 2.6, double Optical = 0.5);

    // The pointer's head is its weight: centred by mass, not by its box, or it reads as pushed aside.
    public static readonly SolidGlyph SolidSelect = new("M5 2.5 L5 19.6 L9.7 15.3 L12.7 21.7 L15.8 20.2 L12.8 13.9 L19.2 13.6 Z", Optical: 1);
    // Tavern set (2026-10-17): assets are a keep with battlements, shapes a heraldic shield, text a
    // quill, notes a scroll and the mask a masquerade mask - the genre's own objects, same grid.
    public static readonly SolidGlyph SolidAssets = new(
        "F0 M3.6 21.6 V6.4 H6.4 V8.8 H8.6 V6.4 H11 V8.8 H13 V6.4 H15.4 V8.8 H17.6 V6.4 H20.4 V21.6 H14.6 V17.2 " +
        "A2.6 2.6 0 0 0 9.4 17.2 V21.6 Z M10.9 11.2 H13.1 V14 H10.9 Z M11.4 1.6 V6 H12.6 V3.9 L15.4 3.1 L12.6 2.3 V1.6 Z");
    public static readonly SolidGlyph SolidLand = new(
        "F0 M2.8 14.6 C2.8 10.5 6.1 8.4 9 8.9 C10.3 5.2 15.5 4 18.3 6.9 C21.1 9.6 20.8 14.6 17.4 16.4 " +
        "C14.9 17.7 12.5 17 10.2 18.3 C7 20 2.8 18.8 2.8 14.6 Z M7.9 14.4 L10.5 10.8 L12.3 13 L14 10.8 L16.8 14.4 Z");
    public static readonly SolidGlyph SolidRiver = new("M0 0",
        "M7.4 2.4 C5.6 6 11.2 7.6 10.2 11.6 C9.3 15.2 4.7 15.6 5.1 19.2 C5.3 20.5 6.1 21.2 7.2 21.6 " +
        "M17.6 2.8 C16 6.6 14 9 10.4 11.5", 3.3);
    // Three hexes of a board, the hex constructor's own tool.
    public static readonly SolidGlyph SolidHex = new(
        "M7.6 2.6 L11.6 4.9 V9.5 L7.6 11.8 L3.6 9.5 V4.9 Z M16.4 2.6 L20.4 4.9 V9.5 L16.4 11.8 L12.4 9.5 V4.9 Z "
        + "M12 10.2 L16 12.5 V17.1 L12 19.4 L8 17.1 V12.5 Z");
    public static readonly SolidGlyph SolidRoad = new(
        "F0 M3.6 21.6 L9.6 2.4 H14.4 L20.4 21.6 Z M11.2 4.6 H12.8 V7.4 H11.2 Z M11 9.8 H13 V13 H11 Z M10.8 15.4 H13.2 V19.4 H10.8 Z");
    public static readonly SolidGlyph SolidLine = new(
        "M4.4 15.6 A2.6 2.6 0 1 0 4.4 20.8 A2.6 2.6 0 1 0 4.4 15.6 Z M19.6 3.6 A2.6 2.6 0 1 0 19.6 8.8 A2.6 2.6 0 1 0 19.6 3.6 Z",
        "M4.4 18.2 C8.2 8.6 12.2 19.8 19.6 6.2", 2.6);
    public static readonly SolidGlyph SolidShape = new(
        "F0 M12 2.2 L20.4 5.2 V11.4 C20.4 16.6 16.8 20.2 12 22 C7.2 20.2 3.6 16.6 3.6 11.4 V5.2 Z " +
        "M12 4.8 V19.4 C8.6 17.8 6.1 15.2 6.1 11.4 V7 Z");
    public static readonly SolidGlyph SolidText = new(
        "M21.2 2.4 C14.2 3.2 9.4 7.6 7.6 14 L6.3 18.6 L7.9 19.1 L9.3 15.4 C13.8 15 17.8 12 18.9 8.1 L15.8 8.6 " +
        "L19.5 5.9 C20.3 4.8 20.8 3.7 21.2 2.4 Z", "M3 21.6 H13", 1.8);
    /// <summary>«Масштаб и украшения»: a ruler laid on the diagonal, its notches cut out.</summary>
    public static readonly SolidGlyph SolidMeasure = new(
        "F0 M2.4 16.4 L16.4 2.4 L21.6 7.6 L7.6 21.6 Z M6.6 13.4 L8.6 15.4 L9.6 14.4 L7.6 12.4 Z " +
        "M9.6 10.4 L12.1 12.9 L13.1 11.9 L10.6 9.4 Z M12.6 7.4 L14.6 9.4 L15.6 8.4 L13.6 6.4 Z");
    public static readonly SolidGlyph SolidNotes = new(
        "F0 M7.2 2.6 H18.2 A2.6 2.6 0 0 1 18.2 7.8 H17 V18.8 A2.8 2.8 0 0 1 14.2 21.6 H5.4 A2.6 2.6 0 0 1 5.4 16.4 H6.6 V3.2 " +
        "C6.6 2.9 6.9 2.6 7.2 2.6 Z M9.2 7.6 H14.6 V9.2 H9.2 Z M9.2 11 H14.6 V12.6 H9.2 Z M9.2 14.4 H13 V16 H9.2 Z");
    public static readonly SolidGlyph SolidBrush = new(
        "M21 2.1 C21.9 3 21.4 4.4 20.4 5.4 L13.5 12.7 L11.3 10.5 L18.6 3.6 C19.6 2.6 20.2 1.3 21 2.1 Z " +
        "M10.4 11.5 L12.5 13.6 C12.4 17 10.2 19.8 6.4 20.6 C5.1 20.9 3.7 21 2.4 20.8 C3.5 19.8 4 18.5 4.3 17 " +
        "C4.8 13.9 7.1 11.7 10.4 11.5 Z");
    public static readonly SolidGlyph SolidMask = new(
        "F0 M2.2 7.6 C5.8 6 9.4 7.1 12 9.1 C14.6 7.1 18.2 6 21.8 7.6 C21.9 12.8 19.6 16.4 16.2 16.4 " +
        "C14.4 16.4 13 15.2 12 13.6 C11 15.2 9.6 16.4 7.8 16.4 C4.4 16.4 2.1 12.8 2.2 7.6 Z " +
        "M5.6 10.6 C6.8 9.6 8.6 9.6 9.8 10.8 C8.6 12.2 6.8 12.2 5.6 10.6 Z " +
        "M18.4 10.6 C17.2 9.6 15.4 9.6 14.2 10.8 C15.4 12.2 17.2 12.2 18.4 10.6 Z");

    // Library rail (2026-10-17, Inkarnate's dashboard rail): home, maps, templates, pictures, help,
    // settings, plus the favourite star and the folder of the library cards - the same 24-unit grid.
    public static readonly SolidGlyph SolidHome = new(
        "F0 M12 2.6 L22 11 H19 V21.4 H14.4 V15 H9.6 V21.4 H5 V11 H2 Z");
    public static readonly SolidGlyph SolidMaps = new(
        "F0 M2.6 5.6 L8.6 3 L15.4 5.6 L21.4 3 V18.4 L15.4 21 L8.6 18.4 L2.6 21 Z " +
        "M9.8 5.2 V16.6 L14.2 18.3 V6.9 Z");
    public static readonly SolidGlyph SolidTemplate = new(
        "F0 M4.4 2.4 H14.6 L19.6 7.4 V21.6 H4.4 Z M7.2 10.4 H16.8 V12.2 H7.2 Z M7.2 14 H16.8 V15.8 H7.2 Z M7.2 17.6 H13.4 V19.4 H7.2 Z");
    public static readonly SolidGlyph SolidPicture = new(
        "F0 M2.4 4.4 H21.6 V19.6 H2.4 Z M4.6 17.4 H19.4 L14.6 10.2 L11.2 15 L8.8 12.4 Z " +
        "M8.4 6.8 A1.8 1.8 0 1 0 8.4 10.4 A1.8 1.8 0 1 0 8.4 6.8 Z");
    public static readonly SolidGlyph SolidHelp = new(
        "F0 M12 2 A10 10 0 1 0 12 22 A10 10 0 1 0 12 2 Z " +
        "M10.8 16 H13.2 V18.4 H10.8 Z M8.4 9.6 C8.4 7.3 10 5.8 12.2 5.8 C14.4 5.8 15.8 7.2 15.8 9 " +
        "C15.8 11.2 13.2 11.6 13.2 14 H10.8 C10.8 10.8 13.4 10.6 13.4 9.1 C13.4 8.4 12.9 8 12.1 8 C11.2 8 10.8 8.6 10.8 9.6 Z");
    public static readonly SolidGlyph SolidGear = new(
        "F0 M10.2 2 H13.8 L14.4 4.9 L16.6 5.8 L19.1 4.2 L21.6 6.7 L20 9.2 L20.9 11.4 L23.8 12 V15.6 L20.9 16.2 L20 18.4 " +
        "L21.6 20.9 L19.1 23.4 L16.6 21.8 L14.4 22.7 L13.8 25.6 H10.2 L9.6 22.7 L7.4 21.8 L4.9 23.4 L2.4 20.9 L4 18.4 " +
        "L3.1 16.2 L0.2 15.6 V12 L3.1 11.4 L4 9.2 L2.4 6.7 L4.9 4.2 L7.4 5.8 L9.6 4.9 Z " +
        "M12 10.2 A3.6 3.6 0 1 0 12 17.4 A3.6 3.6 0 1 0 12 10.2 Z");
    public static readonly SolidGlyph SolidPlus = new("M10.4 3 H13.6 V10.4 H21 V13.6 H13.6 V21 H10.4 V13.6 H3 V10.4 H10.4 Z");
    public static readonly SolidGlyph SolidStar = new(
        "M12 2.2 L14.9 8.6 L21.8 9.3 L16.6 14 L18.1 20.9 L12 17.4 L5.9 20.9 L7.4 14 L2.2 9.3 L9.1 8.6 Z");
    public static readonly SolidGlyph SolidFolder = new(
        "M2.4 5.2 C2.4 4.5 3 4 3.6 4 H9.2 L11.4 6.4 H20.4 C21 6.4 21.6 6.9 21.6 7.6 V18.8 C21.6 19.5 21 20 20.4 20 H3.6 " +
        "C3 20 2.4 19.5 2.4 18.8 Z");
    public const string Star = "M12 3.2 L14.6 8.9 L20.8 9.6 L16.1 13.8 L17.5 20 L12 16.8 L6.5 20 L7.9 13.8 L3.2 9.6 L9.4 8.9 Z";
    public const string Folder = "M3 6 C3 5.4 3.5 5 4 5 H9 L11 7.2 H20 C20.6 7.2 21 7.6 21 8.2 V18 C21 18.6 20.6 19 20 19 H4 C3.4 19 3 18.6 3 18 Z";
    public static readonly SolidGlyph SolidMore = new(
        "M5 10 A2 2 0 1 0 5 14 A2 2 0 1 0 5 10 Z M12 10 A2 2 0 1 0 12 14 A2 2 0 1 0 12 10 Z M19 10 A2 2 0 1 0 19 14 A2 2 0 1 0 19 10 Z");

    /// <summary>The solid rail glyph of a tool.</summary>
    public static SolidGlyph SolidTool(Map.Presentation.ViewModels.EditorTool tool) => tool switch
    {
        Map.Presentation.ViewModels.EditorTool.Select => SolidSelect,
        Map.Presentation.ViewModels.EditorTool.Land => SolidLand,
        Map.Presentation.ViewModels.EditorTool.Line => SolidLine,
        Map.Presentation.ViewModels.EditorTool.Shape => SolidShape,
        Map.Presentation.ViewModels.EditorTool.Text => SolidText,
        Map.Presentation.ViewModels.EditorTool.Notes => SolidNotes,
        Map.Presentation.ViewModels.EditorTool.Brush => SolidBrush,
        Map.Presentation.ViewModels.EditorTool.Mask => SolidMask,
        Map.Presentation.ViewModels.EditorTool.Stamp => SolidAssets,
        Map.Presentation.ViewModels.EditorTool.River => SolidRiver,
        Map.Presentation.ViewModels.EditorTool.Road => SolidRoad,
        Map.Presentation.ViewModels.EditorTool.Hex => SolidHex,
        Map.Presentation.ViewModels.EditorTool.Measure => SolidMeasure,
        _ => SolidSelect,
    };

    /// <summary>Draws a solid glyph on its 24-unit grid, following the host's foreground.</summary>
    public static Control Solid24(SolidGlyph glyph, double size = 24, IBrush? brush = null)
    {
        // Scaled to the drawing's own bounds, not to the 24-unit grid: Inkarnate's glyphs fill
        // their cell, and every Map glyph then reads at the same optical size.
        var fill = Geometry.Parse(glyph.Fill);
        var strokeGeometry = glyph.Stroke is { } s ? Geometry.Parse(s) : null;
        var bounds = fill.Bounds.Width > 0.1 ? fill.Bounds : default;
        if (strokeGeometry is not null)
        {
            var inflated = strokeGeometry.Bounds.Inflate(glyph.StrokeWidth / 2);
            bounds = bounds.Width > 0.1 ? bounds.Union(inflated) : inflated;
        }
        // Optical centre (owner 2026-10-21: "centre the glyphs inside the icons"): a glyph heavy
        // on one side (a quill, a brush, a road in perspective) looks off-centre when only its
        // bounding box is centred. Half way between the box centre and the centre of the painted
        // area sits where the eye expects it.
        var (massX, massY) = MassCentre(fill, strokeGeometry, glyph.StrokeWidth, bounds);
        var centreX = bounds.Center.X + (massX - bounds.Center.X) * glyph.Optical;
        var centreY = bounds.Center.Y + (massY - bounds.Center.Y) * glyph.Optical;
        var halfWidth = System.Math.Max(centreX - bounds.Left, bounds.Right - centreX);
        var halfHeight = System.Math.Max(centreY - bounds.Top, bounds.Bottom - centreY);
        var side = 2 * System.Math.Max(halfWidth, halfHeight);
        var canvas = new Canvas { Width = side, Height = side };
        var offset = new Point(side / 2 - centreX, side / 2 - centreY);
        var body = new Path { Data = fill, IsHitTestVisible = false };
        Canvas.SetLeft(body, offset.X); Canvas.SetTop(body, offset.Y);
        Paint(body, Path.FillProperty, brush);
        canvas.Children.Add(body);
        if (strokeGeometry is not null)
        {
            var line = new Path
            {
                Data = strokeGeometry,
                StrokeThickness = glyph.StrokeWidth,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
            };
            Paint(line, Path.StrokeProperty, brush);
            Canvas.SetLeft(line, offset.X); Canvas.SetTop(line, offset.Y);
            canvas.Children.Add(line);
        }
        return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, IsHitTestVisible = false, Child = canvas };
    }

    private static (double X, double Y) MassCentre(Geometry fill, Geometry? stroke, double strokeWidth, Rect bounds)
    {
        const int Steps = 40;
        var pen = stroke is null ? null : new Pen(Brushes.Black, strokeWidth);
        double sumX = 0, sumY = 0; var count = 0;
        for (var j = 0; j < Steps; j++)
        for (var i = 0; i < Steps; i++)
        {
            var point = new Point(bounds.X + (i + 0.5) * bounds.Width / Steps, bounds.Y + (j + 0.5) * bounds.Height / Steps);
            if (!fill.FillContains(point) && !(stroke is not null && stroke.StrokeContains(pen!, point))) continue;
            sumX += point.X; sumY += point.Y; count++;
        }

        return count == 0 ? (bounds.Center.X, bounds.Center.Y) : (sumX / count, sumY / count);
    }

    /// <summary>The line-set rail glyph of a tool (UI redesign).</summary>
    public static string LineTool(Map.Presentation.ViewModels.EditorTool tool) => tool switch
    {
        Map.Presentation.ViewModels.EditorTool.Select => LineSelect,
        Map.Presentation.ViewModels.EditorTool.Land => LineLand,
        Map.Presentation.ViewModels.EditorTool.Line => LineStroke,
        Map.Presentation.ViewModels.EditorTool.Shape => LineShape,
        Map.Presentation.ViewModels.EditorTool.Text => LineText,
        Map.Presentation.ViewModels.EditorTool.Notes => LineNotes,
        Map.Presentation.ViewModels.EditorTool.Brush => LineBrush,
        Map.Presentation.ViewModels.EditorTool.Mask => LineMask,
        Map.Presentation.ViewModels.EditorTool.Stamp => LineAssets,
        Map.Presentation.ViewModels.EditorTool.River => LineRiver,
        Map.Presentation.ViewModels.EditorTool.Road => LineRoad,
        Map.Presentation.ViewModels.EditorTool.Hex => Hex,
        Map.Presentation.ViewModels.EditorTool.Measure => HexRuler,
        _ => LineSelect,
    };

    /// <summary>
    /// A line-set glyph drawn on its own 24-unit grid and scaled as a whole, so thin details keep
    /// their place instead of being stretched to the path's bounds.
    /// </summary>
    public static Control Grid24(string data, double size = 18, IBrush? brush = null, double thickness = 1.7)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = thickness,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
        Paint(path, Path.StrokeProperty, brush);
        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
            Child = new Canvas { Width = 24, Height = 24, Children = { path } },
        };
    }

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
        Map.Presentation.ViewModels.EditorTool.Road => Road,
        Map.Presentation.ViewModels.EditorTool.Hex => Hex,
        Map.Presentation.ViewModels.EditorTool.Measure => HexRuler,
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
